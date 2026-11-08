//! The streaming generation loop's host half — the direction set 2026-07-24:
//! the host owns the population, the fabric never random-accesses DDR, and a
//! generation is three passes the host drives in turn:
//!
//! 1. **Emit.** Auto mode with the op-list base set: the fabric selects —
//!    binary tournament over its fitness table, the elite at index 0 — and
//!    streams one generation's pairing entries to the op-list ring instead
//!    of breeding them. Generation 0's pass scores the staged population
//!    first; later passes skip the score (`skip_score`, the table is there
//!    from the breed pass) and continue the selection stream
//!    (`rng_continue`).
//! 2. **Marshal.** Each entry followed by both parents' records, from the
//!    host's copy of the population ([`marshal_serial`]), into the queue.
//! 3. **Breed.** Queue mode: the fabric breeds, evaluates and writes every
//!    offspring back; the host reads the ring for their fitness and the
//!    other half of the population region for their genomes, which are the
//!    next generation.
//!
//! The fabric resets its region flag at every start, so the host keeps the
//! current population in the first half of the region and the offspring
//! land in the second, every generation. Written once over [`RegisterWindow`]
//! and a [`Ddr`], so the same loop runs against the F# Sim through the
//! `simserve` bridge and against the KV260's `u-dma-buf`.

use std::time::{Duration, Instant};
use warp11_runtime::gep::{marshal_serial, parse_results, BreedRates, GepClusterDevice, GepError, GepRegions, GepResult};
use warp11_runtime::gep_layout as layout;
use warp11_runtime::RegisterWindow;

/// Bulk memory the fabric's masters share with the host, by byte offset
/// within the arena.
pub trait Ddr {
    type Error: std::fmt::Debug;
    fn read_words(&mut self, offset: usize, words: usize) -> Result<Vec<u32>, Self::Error>;
    fn write_words(&mut self, offset: usize, words: &[u32]) -> Result<(), Self::Error>;
    /// Let the fabric run while the host waits — the Sim advances only when
    /// asked; silicon ignores it.
    fn idle(&mut self) -> Result<(), Self::Error> {
        Ok(())
    }
}

/// Where the regions sit in the arena, as byte offsets, and the arena's
/// physical base the fabric addresses them from.
#[derive(Clone, Copy, Debug)]
pub struct Arena {
    pub phys_base: u32,
    pub queue: usize,
    pub pop: usize,
    pub ring: usize,
    pub oplist: usize,
}

impl Arena {
    /// Laid out for `pop` individuals: the queue of inline-parent work
    /// items, two halves of population, the result ring, the op-list ring,
    /// each page-aligned.
    pub fn for_population(phys_base: u32, pop: usize) -> Self {
        let align = |v: usize| (v + 0xFFF) & !0xFFF;
        let queue = 0;
        let pop_off = align(queue + pop * layout::WORK_ITEM_BYTES);
        let ring = align(pop_off + 2 * pop * layout::RECORD_BYTES);
        let oplist = align(ring + pop * layout::RESULT_BYTES);
        Arena { phys_base, queue, pop: pop_off, ring, oplist }
    }

    pub fn bytes_needed(&self, pop: usize) -> usize {
        self.oplist + pop * layout::RECORD_BYTES
    }

    fn regions(&self, pop: usize) -> GepRegions {
        GepRegions {
            queue_base: self.phys_base + self.queue as u32,
            pop_base: self.phys_base + self.pop as u32,
            ring_base: self.phys_base + self.ring as u32,
            queue_entries: pop as u32,
            ring_records: pop as u32,
        }
    }
}

/// The run's policy: the breeding rates, the selection seed, the cases.
pub struct Policy<'a> {
    pub rates: BreedRates,
    pub seed: [u32; 4],
    /// Each case as the fabric's row: the variables, then the target.
    pub cases: Vec<&'a [u32]>,
}

/// One generation as the fabric produced it.
#[derive(Clone, Debug, Default)]
pub struct Generation {
    /// The op-list the emit pass wrote: `pop` entries of `RECORD_WORDS`.
    pub oplist: Vec<u32>,
    /// Offspring fitness by entry id.
    pub fitness: Vec<u64>,
    /// Offspring genomes by entry id, `RECORD_WORDS` each: the next population.
    pub genomes: Vec<u32>,
    pub emit: Duration,
    pub marshal: Duration,
    pub breed: Duration,
    pub harvest: Duration,
    /// Fabric cycles the breed pass took, from its own counter.
    pub breed_cycles: u32,
}

#[derive(Debug)]
pub enum EvolveError<W, D> {
    Device(GepError<W>),
    Ddr(D),
    /// The ring came back with an entry id outside the population.
    StrayEntry(u32),
}

impl<W, D> From<GepError<W>> for EvolveError<W, D> {
    fn from(e: GepError<W>) -> Self {
        EvolveError::Device(e)
    }
}

/// Poll `done` until it answers true, idling the DDR side between polls so a
/// Sim advances; `polls` is the budget.
fn await_with<W: RegisterWindow, D: Ddr>(
    ddr: &mut D,
    polls: u32,
    mut done: impl FnMut() -> Result<bool, W::Error>,
) -> Result<(), EvolveError<W::Error, D::Error>> {
    for _ in 0..polls {
        if done().map_err(|e| EvolveError::Device(GepError::Window(e)))? {
            return Ok(());
        }
        ddr.idle().map_err(EvolveError::Ddr)?;
    }
    Err(EvolveError::Device(GepError::Timeout))
}

/// The whole loop: `generations` of emit, marshal, breed, from `population`
/// (`pop` records), returning every generation's op-list, fitness and
/// genomes. Generation 0's score-round fitness of the staged population
/// comes back first, as `initial_fitness`.
pub fn evolve<W: RegisterWindow, D: Ddr>(
    device: &mut GepClusterDevice<W>,
    ddr: &mut D,
    arena: Arena,
    policy: &Policy,
    population: &[u32],
    generations: usize,
    polls: u32,
) -> Result<(Vec<u64>, Vec<Generation>), EvolveError<W::Error, D::Error>> {
    let pop = population.len() / layout::RECORD_WORDS;
    let regions = arena.regions(pop);
    let mut current: Vec<u32> = population.to_vec();
    let mut initial_fitness = Vec::new();
    let mut out = Vec::with_capacity(generations);

    ddr.write_words(arena.pop, &current).map_err(EvolveError::Ddr)?;

    for g in 0..generations {
        let mut gen = Generation::default();

        // ---- emit ----
        let t = Instant::now();
        device.set_auto_mode(true).map_err(GepError::Window)?;
        device.configure_auto(pop as u32, 1, &policy.rates, policy.seed).map_err(GepError::Window)?;
        device.set_oplist_base(arena.phys_base + arena.oplist as u32).map_err(GepError::Window)?;
        device.set_pass_flags(g > 0, g > 0).map_err(GepError::Window)?;
        // The cases go in before the start: the emit pass scores the staged
        // population the moment it starts, and a case loaded after that is
        // a case the score never saw.
        if g == 0 {
            device.load_cases(&policy.cases).map_err(GepError::Window)?;
        }
        device.start(regions, policy.cases.len() as u32).map_err(GepError::Window)?;
        await_with::<W, D>(ddr, polls, || device.oplist_done())?;
        await_with::<W, D>(ddr, polls, || device.all_idle())?;
        gen.oplist = ddr.read_words(arena.oplist, pop * layout::RECORD_WORDS).map_err(EvolveError::Ddr)?;
        if g == 0 {
            let ring = ddr.read_words(arena.ring, pop * layout::RESULT_WORDS).map_err(EvolveError::Ddr)?;
            let mut results = vec![GepResult { fitness: 0, entry_id: 0, seq: 0 }; pop];
            parse_results(&ring, &mut results);
            initial_fitness = vec![0u64; pop];
            for r in &results {
                if (r.entry_id as usize) < pop {
                    initial_fitness[r.entry_id as usize] = r.fitness;
                } else {
                    return Err(EvolveError::StrayEntry(r.entry_id));
                }
            }
        }
        gen.emit = t.elapsed();

        // ---- marshal ----
        let t = Instant::now();
        let mut items = vec![0u32; pop * layout::WORK_ITEM_WORDS];
        marshal_serial(&gen.oplist, pop, &current, &mut items);
        ddr.write_words(arena.queue, &items).map_err(EvolveError::Ddr)?;
        gen.marshal = t.elapsed();

        // ---- breed ----
        let t = Instant::now();
        device.set_auto_mode(false).map_err(GepError::Window)?;
        device.start(regions, policy.cases.len() as u32).map_err(GepError::Window)?;
        device.publish(pop as u32).map_err(GepError::Window)?;
        await_with::<W, D>(ddr, polls, || Ok(device.results_done()? >= pop as u32))?;
        await_with::<W, D>(ddr, polls, || device.all_idle())?;
        gen.breed_cycles = device.telemetry().map_err(GepError::Window)?.cycle_count;
        gen.breed = t.elapsed();

        // ---- harvest ----
        let t = Instant::now();
        let ring = ddr.read_words(arena.ring, pop * layout::RESULT_WORDS).map_err(EvolveError::Ddr)?;
        let mut results = vec![GepResult { fitness: 0, entry_id: 0, seq: 0 }; pop];
        parse_results(&ring, &mut results);
        gen.fitness = vec![0u64; pop];
        for r in &results {
            if (r.entry_id as usize) < pop {
                gen.fitness[r.entry_id as usize] = r.fitness;
            } else {
                return Err(EvolveError::StrayEntry(r.entry_id));
            }
        }
        gen.genomes = ddr
            .read_words(arena.pop + pop * layout::RECORD_BYTES, pop * layout::RECORD_WORDS)
            .map_err(EvolveError::Ddr)?;
        gen.harvest = t.elapsed();

        // The offspring are the next generation: in the host's copy, and in
        // the first half of the region, which keeps the region truthful for
        // anything reading it even though the next emit pass, skipping the
        // score, reads nothing from it.
        current = gen.genomes.clone();
        ddr.write_words(arena.pop, &current).map_err(EvolveError::Ddr)?;
        // Disarm between passes, so nothing is in flight at teardown.
        device.publish(0).map_err(GepError::Window)?;
        out.push(gen);
    }

    Ok((initial_fitness, out))
}

/// The bridge as both halves: registers and DDR through one `FsSimWindow`,
/// each poll idling the fabric so a wait is cycles rather than spin.
pub mod sim {
    use super::Ddr;
    use crate::fs_sim_window::{FsSimError, FsSimWindow};
    use std::cell::RefCell;
    use std::rc::Rc;
    use warp11_runtime::RegisterWindow;

    #[derive(Clone)]
    pub struct Shared(pub Rc<RefCell<FsSimWindow>>);

    impl RegisterWindow for Shared {
        type Error = FsSimError;

        fn read32(&mut self, offset: usize) -> Result<u32, FsSimError> {
            self.0.borrow_mut().read32(offset)
        }

        fn write32(&mut self, offset: usize, value: u32) -> Result<(), FsSimError> {
            self.0.borrow_mut().write32(offset, value)
        }
    }

    impl Ddr for Shared {
        type Error = FsSimError;

        fn read_words(&mut self, offset: usize, words: usize) -> Result<Vec<u32>, FsSimError> {
            let bytes = self.0.borrow_mut().read_ddr(offset, words * 4)?;
            Ok(bytes.chunks(4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect())
        }

        fn write_words(&mut self, offset: usize, words: &[u32]) -> Result<(), FsSimError> {
            let bytes: Vec<u8> = words.iter().flat_map(|w| w.to_le_bytes()).collect();
            self.0.borrow_mut().write_ddr(offset, &bytes)
        }

        fn idle(&mut self) -> Result<(), FsSimError> {
            self.0.borrow_mut().free_cycles(2000)
        }
    }
}

/// A mapped `u-dma-buf` as the DDR: bulk copies through the mapping's bytes.
pub mod board {
    use super::Ddr;
    use crate::mmap::MmapWindow;

    pub struct Mapped(pub MmapWindow);

    impl Ddr for Mapped {
        type Error = std::convert::Infallible;

        fn read_words(&mut self, offset: usize, words: usize) -> Result<Vec<u32>, Self::Error> {
            let bytes = &self.0.bytes()[offset..offset + words * 4];
            Ok(bytes.chunks(4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect())
        }

        fn write_words(&mut self, offset: usize, words: &[u32]) -> Result<(), Self::Error> {
            let out = &mut self.0.bytes_mut()[offset..offset + words.len() * 4];
            for (chunk, w) in out.chunks_mut(4).zip(words) {
                chunk.copy_from_slice(&w.to_le_bytes());
            }
            Ok(())
        }
    }
}
