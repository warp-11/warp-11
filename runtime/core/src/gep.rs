//! The WarpCPU genetic-programming cluster's driver — the host half of the
//! streaming redesign, written once against [`RegisterWindow`] and the
//! cluster's seam ([`GepSeam`]), so the same code runs against the F# Sim,
//! a board with the host's memory behind it and a board on a serial link.
//!
//! Almost nothing crosses the registers: the population, the pairing
//! entries, the op-list and the result ring are regions, carried however
//! the board carries them, and the fitness table never leaves the fabric at
//! all. This driver owns the *control* flow — broadcast the fitness cases,
//! publish entries, poll — and the shape of the records.
//!
//! The generation loop has two shapes, and both are here:
//!
//! - **Queue mode** — the host owns selection, writes pairing entries into the
//!   queue ring, and bumps `entries_published`. With inline parents each work
//!   item is the entry plus both parent records ([`marshal_serial`]), so the
//!   fabric reads parents as the next sequential beats of the entry's own burst
//!   instead of random-accessing the population.
//! - **Auto / op-list mode** — the fabric selects for itself. A pass either runs
//!   the whole loop in fabric, or emits one generation's pairing entries to a
//!   DDR ring for the host to gather against and hand back through queue mode.
//!   `skip_score` and `rng_continue` carry the fitness table and the selection
//!   RNG stream across those passes.

use crate::gep_seam::GepSeam;
use crate::RegisterWindow;

/// What can go wrong talking to the cluster, independent of the backend.
#[derive(Debug, PartialEq, Eq)]
pub enum GepError<E> {
    /// A poll budget ran out. Against a Sim bridge each read advances the
    /// fabric, so a budget is also a cycle budget; on hardware it is patience.
    Timeout,
    /// The window itself failed.
    Window(E),
}

impl<E> From<E> for GepError<E> {
    fn from(inner: E) -> Self {
        GepError::Window(inner)
    }
}

/// The record geometry, read off the regions the seam states — the build's,
/// not this driver's. A gene record and a pairing entry are both 16 words; a
/// work item is an entry and two gene records.
impl GepSeam {
    pub fn record_words(&self) -> usize {
        self.population_record_bytes / 4
    }

    pub fn entry_words(&self) -> usize {
        self.queue_parent_a_record_offset / 4
    }

    pub fn work_item_words(&self) -> usize {
        self.queue_record_bytes / 4
    }

    pub fn result_words(&self) -> usize {
        self.ring_record_bytes / 4
    }
}

/// One breeding policy, already quantized the way the fabric reads it: nine
/// gates as their top 16 bits packed two per word, plus the two spans the creep
/// and constant draws scale by. Build it with [`BreedRates::from_thresholds`] so
/// the packing lives in one place.
#[derive(Clone, Copy, Debug, Default)]
pub struct BreedRates {
    /// Words 3..7 of a pairing entry; word 4's high half is the flags at pack
    /// time, so only its low half is carried here.
    pub words: [u32; 5],
    pub sigma_fx: u32,
    pub range_fx: u32,
}

impl BreedRates {
    /// `gates` are full 32-bit Bernoulli thresholds in entry-record order:
    /// mutation, constReplace, creep, inversion, isTrans, risTrans, onePoint,
    /// twoPoint, geneRecomb. Only the top 16 bits of each survive, which is
    /// what the fabric compares against — so a software oracle must draw
    /// against the same quantized values.
    pub fn from_thresholds(gates: [u32; 9], sigma_fx: u32, range_fx: u32) -> Self {
        let hi = |v: u32| v >> 16;
        BreedRates {
            words: [
                hi(gates[0]) | (hi(gates[1]) << 16),
                hi(gates[2]) | (hi(gates[3]) << 16),
                hi(gates[4]) | (hi(gates[5]) << 16),
                hi(gates[6]) | (hi(gates[7]) << 16),
                hi(gates[8]),
            ],
            sigma_fx,
            range_fx,
        }
    }
}

/// One pairing: which two population slots breed, where the offspring goes,
/// under what policy, from what seed.
#[derive(Clone, Copy, Debug)]
pub struct Pairing {
    pub parent_a: u32,
    pub parent_b: u32,
    pub dest: u32,
    pub entry_id: u32,
    /// Evaluate only — score the offspring and skip the genome writeback.
    pub skip_writeback: bool,
    /// The pre-expanded xoshiro128++ state (SplitMix64 of a 64-bit seed,
    /// expanded host-side because the fabric will not do 64x64 multiplies).
    pub seed: [u32; 4],
}

/// Pack a pairing into its 16-word DDR entry record. This layout is shared
/// three ways — the fabric's filler reads it, the fabric's op-list emitter
/// writes it, and this function produces it — so it is spelled once, here.
pub fn pack_entry(seam: &GepSeam, p: &Pairing, rates: &BreedRates, out: &mut [u32]) {
    let flags = if p.skip_writeback {
        seam.flag_skip_writeback
    } else {
        0
    };

    out[seam.queue_parent_a_offset / 4] = p.parent_a;
    out[seam.queue_parent_b_offset / 4] = p.parent_b;
    out[seam.queue_dest_offset / 4] = p.dest;
    out[seam.queue_rates0_offset / 4] = rates.words[0];
    out[seam.queue_rates1_offset / 4] = rates.words[1];
    out[seam.queue_rates2_offset / 4] = rates.words[2];
    out[seam.queue_rates3_offset / 4] = rates.words[3];
    out[seam.queue_recomb_flags_offset / 4] = (rates.words[4] & 0xFFFF) | (flags << 16);
    out[seam.queue_sigma_offset / 4] = rates.sigma_fx;
    out[seam.queue_range_offset / 4] = rates.range_fx;
    out[seam.queue_entry_id_offset / 4] = p.entry_id;
    out[seam.queue_reserved_offset / 4] = 0;
    let seed = seam.queue_seed_offset / 4;
    out[seed..seed + seam.queue_seed_words].copy_from_slice(&p.seed);
}

/// One fitness-ring record as the fabric writes it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GepResult {
    pub fitness: u64,
    pub entry_id: u32,
    pub seq: u32,
}

/// Parse `count` ring records out of a word buffer.
pub fn parse_results(seam: &GepSeam, words: &[u32], out: &mut [GepResult]) {
    for (r, slot) in out.iter_mut().enumerate() {
        let base = r * seam.result_words();
        let fitness = base + seam.ring_fitness_offset / 4;
        *slot = GepResult {
            fitness: (words[fitness + 1] as u64) << 32 | words[fitness] as u64,
            entry_id: words[base + seam.ring_result_entry_id_offset / 4],
            seq: words[base + seam.ring_seq_offset / 4],
        };
    }
}

/// The gather that makes the streaming redesign work: each op-list entry
/// followed by both of its parent records, so the fabric never random-accesses
/// DDR — parents arrive as the next beats of a sequential burst, where read
/// latency is invisible.
///
/// Pure memory movement over disjoint output ranges, which is why the Kotlin
/// side could parallelize it without a lock. This is the serial form; it is the
/// definition the parallel one has to agree with.
pub fn marshal_serial(seam: &GepSeam, op_list: &[u32], item_count: usize, population: &[u32], out: &mut [u32]) {
    let (entry, item, record) = (seam.entry_words(), seam.work_item_words(), seam.record_words());
    for i in 0..item_count {
        let src = i * entry;
        let dst = i * item;
        out[dst..dst + entry].copy_from_slice(&op_list[src..src + entry]);

        let parents = [
            (op_list[src + seam.queue_parent_a_offset / 4] as usize, seam.queue_parent_a_record_offset / 4),
            (op_list[src + seam.queue_parent_b_offset / 4] as usize, seam.queue_parent_b_record_offset / 4),
        ];
        for (slot, at) in parents {
            let from = slot * record;
            let to = dst + at;
            out[to..to + record].copy_from_slice(&population[from..from + record]);
        }
    }
}

/// The cluster's telemetry, read in one sweep. Every field is a free-running
/// counter reset by `start`, so a driver takes two readings and subtracts, or
/// starts and reads once.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GepTelemetry {
    pub cycle_count: u32,
    pub results_done: u32,
    pub entries_taken: u32,
    pub feed_stall_cycles: u32,
    pub breeder_stall_cycles: u32,
    pub fill_busy_cycles: u32,
    pub pack_busy_cycles: u32,
    pub emit_busy_cycles: u32,
    pub busy_breeder_cycles: u32,
    pub busy_lane_cycles: u32,
    pub streams_active: u32,
}

pub struct GepClusterDevice<W> {
    window: W,
    seam: GepSeam,
}

impl<W: RegisterWindow> GepClusterDevice<W> {
    /// The cluster behind a window, with the seam of the build that window
    /// reaches. Whether it is the right build is the connection's to check,
    /// where the seam states an identity to check it by.
    pub fn new(window: W, seam: GepSeam) -> Self {
        GepClusterDevice { window, seam }
    }

    pub fn seam(&self) -> &GepSeam {
        &self.seam
    }

    pub fn window(&mut self) -> &mut W {
        &mut self.window
    }

    fn field(&mut self, offset: usize, shift: u32, mask: u32) -> Result<u32, W::Error> {
        Ok((self.window.read32(offset)? & mask) >> shift)
    }

    /// Program the case count, then pulse start — which frees every breeder
    /// and zeroes every counter.
    pub fn start(&mut self, n_cases: u32) -> Result<(), W::Error> {
        self.window.write32(self.seam.n_cases_offset, n_cases)?;
        self.window
            .write32(self.seam.start_queue_offset, 1 << self.seam.start_queue_bit)
    }

    /// Broadcast the epoch's fitness cases into every lane's resident table.
    /// `rows` is one row per case: `var_count` inputs then the target, so the
    /// caller's stride and the fabric's field index cannot drift apart.
    pub fn load_cases(&mut self, rows: &[&[u32]]) -> Result<(), W::Error> {
        for (index, row) in rows.iter().enumerate() {
            self.window.write32(self.seam.case_addr_offset, index as u32)?;

            for (field, value) in row.iter().enumerate() {
                self.window.write32(self.seam.case_field_offset, field as u32)?;
                self.window.write32(self.seam.case_data_offset, *value)?;
                self.window
                    .write32(self.seam.ld_case_offset, 1 << self.seam.ld_case_bit)?;
            }
        }

        Ok(())
    }

    /// Publish the queue tail. The fabric chases it, so this may be bumped
    /// repeatedly while work is in flight — that is the trickle the run-forever
    /// mode is built on.
    pub fn publish(&mut self, entries: u32) -> Result<(), W::Error> {
        self.window.write32(self.seam.entries_published_offset, entries)
    }

    pub fn results_done(&mut self) -> Result<u32, W::Error> {
        self.field(
            self.seam.results_done_offset,
            self.seam.results_done_shift,
            self.seam.results_done_mask,
        )
    }

    pub fn all_idle(&mut self) -> Result<bool, W::Error> {
        Ok(self.field(
            self.seam.all_idle_offset,
            self.seam.all_idle_shift,
            self.seam.all_idle_mask,
        )? == 1)
    }

    /// Wait until `results_done` reaches `target`. A ring B-ack is what that
    /// counts, and writes go out genome-before-ring per offspring — so when it
    /// lands, those offspring's genomes are in DDR too.
    pub fn await_results(&mut self, target: u32, polls: u32) -> Result<(), GepError<W::Error>> {
        for _ in 0..polls {
            if self.results_done()? >= target {
                return Ok(());
            }
        }

        Err(GepError::Timeout)
    }

    /// Wait for quiescence — no unconsumed entries, every breeder free, every
    /// lane idle, the write master drained. What a host waits on before
    /// reloading cases or re-starting.
    pub fn await_idle(&mut self, polls: u32) -> Result<(), GepError<W::Error>> {
        for _ in 0..polls {
            if self.all_idle()? {
                return Ok(());
            }
        }

        Err(GepError::Timeout)
    }

    pub fn telemetry(&mut self) -> Result<GepTelemetry, W::Error> {
        Ok(GepTelemetry {
            cycle_count: self.window.read32(self.seam.cycle_count_offset)?,
            results_done: self.window.read32(self.seam.results_done_offset)?,
            entries_taken: self.window.read32(self.seam.entries_taken_offset)?,
            feed_stall_cycles: self.window.read32(self.seam.feed_stall_cycles_offset)?,
            breeder_stall_cycles: self.window.read32(self.seam.breeder_stall_cycles_offset)?,
            fill_busy_cycles: self.window.read32(self.seam.fill_busy_cycles_offset)?,
            pack_busy_cycles: self.window.read32(self.seam.pack_busy_cycles_offset)?,
            emit_busy_cycles: self.window.read32(self.seam.emit_busy_cycles_offset)?,
            busy_breeder_cycles: self.window.read32(self.seam.busy_breeder_cycles_offset)?,
            busy_lane_cycles: self.window.read32(self.seam.busy_lane_cycles_offset)?,
            streams_active: self.field(
                self.seam.streams_active_offset,
                self.seam.streams_active_shift,
                self.seam.streams_active_mask,
            )?,
        })
    }

    /// Busy cycles for one breeder or one lane, out of the fixed counter
    /// blocks — the per-instance view a balance report is built from.
    pub fn breeder_busy(&mut self, breeder: usize) -> Result<u32, W::Error> {
        self.window
            .read32(self.seam.breeder0_busy_cycles_offset + 4 * breeder)
    }

    pub fn lane_busy(&mut self, lane: usize) -> Result<u32, W::Error> {
        self.window.read32(self.seam.lane0_busy_cycles_offset + 4 * lane)
    }

    // ---- the in-fabric generation loop ----

    /// Program the loop's policy. `pop` is individuals per region (a power of
    /// two, at most `AUTO_POP_CAPACITY`); the population region holds two of
    /// those, which ping-pong.
    pub fn configure_auto(
        &mut self,
        pop: u32,
        generations: u32,
        rates: &BreedRates,
        seed: [u32; 4],
    ) -> Result<(), W::Error> {
        self.window.write32(self.seam.auto_pop_offset, pop)?;
        self.window.write32(self.seam.auto_gens_offset, generations)?;
        self.window.write32(self.seam.auto_r0_offset, rates.words[0])?;
        self.window.write32(self.seam.auto_r1_offset, rates.words[1])?;
        self.window.write32(self.seam.auto_r2_offset, rates.words[2])?;
        self.window.write32(self.seam.auto_r3_offset, rates.words[3])?;
        self.window.write32(self.seam.auto_r4_offset, rates.words[4])?;
        self.window.write32(self.seam.auto_sigma_offset, rates.sigma_fx)?;
        self.window.write32(self.seam.auto_range_offset, rates.range_fx)?;
        self.window.write32(self.seam.auto_s0_offset, seed[0])?;
        self.window.write32(self.seam.auto_s1_offset, seed[1])?;
        self.window.write32(self.seam.auto_s2_offset, seed[2])?;
        self.window.write32(self.seam.auto_s3_offset, seed[3])
    }

    /// Arm or disarm the loop. Must be set before `start`, which is what
    /// latches it — and the selection RNG is seeded by that same pulse.
    pub fn set_auto_mode(&mut self, on: bool) -> Result<(), W::Error> {
        self.window.write32(self.seam.auto_mode_offset, on as u32)
    }

    /// The op-list pass's two carry-over flags: skip the score round because
    /// the previous breed pass already filled the fitness table, and continue
    /// the selection stream rather than reseeding it. Both are for generation
    /// 1 and later; generation 0 clears them.
    pub fn set_pass_flags(&mut self, skip_score: bool, rng_continue: bool) -> Result<(), W::Error> {
        self.window
            .write32(self.seam.skip_score_offset, skip_score as u32)?;
        self.window
            .write32(self.seam.rng_continue_offset, rng_continue as u32)
    }

    /// Emitted AND committed: every entry accepted and every beat B-acked, so
    /// the op-list ring is safe to read the instant this is true.
    pub fn oplist_done(&mut self) -> Result<bool, W::Error> {
        Ok(self.field(
            self.seam.oplist_done_offset,
            self.seam.oplist_done_shift,
            self.seam.oplist_done_mask,
        )? == 1)
    }

    pub fn await_oplist(&mut self, polls: u32) -> Result<(), GepError<W::Error>> {
        for _ in 0..polls {
            if self.oplist_done()? {
                return Ok(());
            }
        }

        Err(GepError::Timeout)
    }

    pub fn auto_done(&mut self) -> Result<bool, W::Error> {
        Ok(self.field(
            self.seam.auto_done_offset,
            self.seam.auto_done_shift,
            self.seam.auto_done_mask,
        )? == 1)
    }

    pub fn auto_round(&mut self) -> Result<u32, W::Error> {
        self.window.read32(self.seam.auto_round_offset)
    }

    /// Which population region is current. The loop ping-pongs, so a host
    /// reading the final population needs this to know which half to read.
    pub fn auto_base(&mut self) -> Result<u32, W::Error> {
        self.field(
            self.seam.auto_base_offset,
            self.seam.auto_base_shift,
            self.seam.auto_base_mask,
        )
    }

    /// The last completed round's argmin: the elite's index within its region,
    /// and its fitness.
    pub fn best(&mut self) -> Result<(u32, u64), W::Error> {
        let idx = self.field(
            self.seam.best_idx_offset,
            self.seam.best_idx_shift,
            self.seam.best_idx_mask,
        )?;
        let lo = self.window.read32(self.seam.best_fit_lo_offset)? as u64;
        let hi = self.window.read32(self.seam.best_fit_hi_offset)? as u64;
        Ok((idx, hi << 32 | lo))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The record layout as the fabric has always had it, and nothing else:
    /// these checks are about where this driver puts things given a seam,
    /// so every other name reads zero.
    fn seam() -> GepSeam {
        let layout = [
            ("QUEUE_PARENT_A_OFFSET", 0x00),
            ("QUEUE_PARENT_B_OFFSET", 0x04),
            ("QUEUE_DEST_OFFSET", 0x08),
            ("QUEUE_RATES0_OFFSET", 0x0c),
            ("QUEUE_RATES1_OFFSET", 0x10),
            ("QUEUE_RATES2_OFFSET", 0x14),
            ("QUEUE_RATES3_OFFSET", 0x18),
            ("QUEUE_RECOMB_FLAGS_OFFSET", 0x1c),
            ("QUEUE_SIGMA_OFFSET", 0x20),
            ("QUEUE_RANGE_OFFSET", 0x24),
            ("QUEUE_ENTRY_ID_OFFSET", 0x28),
            ("QUEUE_RESERVED_OFFSET", 0x2c),
            ("QUEUE_SEED_OFFSET", 0x30),
            ("QUEUE_SEED_WORDS", 4),
            ("QUEUE_PARENT_A_RECORD_OFFSET", 0x40),
            ("QUEUE_PARENT_B_RECORD_OFFSET", 0x80),
            ("QUEUE_RECORD_BYTES", 192),
            ("POPULATION_RECORD_BYTES", 64),
            ("RING_RECORD_BYTES", 16),
            ("RING_FITNESS_OFFSET", 0x0),
            ("RING_RESULT_ENTRY_ID_OFFSET", 0x8),
            ("RING_SEQ_OFFSET", 0xc),
            ("FLAG_SKIP_WRITEBACK", 1),
        ];
        GepSeam::read(|name| Some(layout.iter().find(|(n, _)| *n == name).map_or(0, |(_, v)| *v))).unwrap()
    }

    const RECORD_WORDS: usize = 16;
    const ENTRY_WORDS: usize = 16;
    const WORK_ITEM_WORDS: usize = 48;

    /// A pairing's entry record round-trips through the shared layout: the
    /// fields land where the fabric reads them, and a quantized gate's top 16
    /// bits are what survives.
    #[test]
    fn entry_packs_where_the_fabric_reads() {
        let rates = BreedRates::from_thresholds(
            [
                0x1111_0000, 0x2222_0000, 0x3333_0000, 0x4444_0000, 0x5555_0000, 0x6666_0000,
                0x7777_0000, 0x8888_0000, 0x9999_0000,
            ],
            0x0001_999A,
            0x000A_0000,
        );

        let pairing = Pairing {
            parent_a: 5,
            parent_b: 9,
            dest: 40,
            entry_id: 0xD00D,
            skip_writeback: true,
            seed: [1, 2, 3, 4],
        };

        let mut words = [0u32; ENTRY_WORDS];
        pack_entry(&seam(), &pairing, &rates, &mut words);

        // The words where they have always been — the fabric replays an entry
        // as beats and slices each by position, so the region's placement of
        // these fields is pinned here by number, not by name.
        assert_eq!(ENTRY_WORDS, 16);
        assert_eq!(words[0], 5);
        assert_eq!(words[1], 9);
        assert_eq!(words[2], 40);
        assert_eq!(words[3], 0x2222_1111);
        assert_eq!(words[4], 0x4444_3333);
        assert_eq!(words[5], 0x6666_5555);
        assert_eq!(words[6], 0x8888_7777);
        // Word 7 is the flags over the geneRecomb half.
        assert_eq!(words[7], (1 << 16) | 0x9999);
        assert_eq!(words[8], 0x0001_999A);
        assert_eq!(words[9], 0x000A_0000);
        assert_eq!(words[10], 0xD00D);
        assert_eq!(words[11], 0);
        assert_eq!(&words[12..], &[1, 2, 3, 4]);
    }

    /// The gather puts each entry's two parent records immediately behind it,
    /// which is the whole claim: one sequential burst per work item.
    #[test]
    fn marshal_lays_parents_behind_their_entry() {
        const ITEMS: usize = 3;
        let mut population = [0u32; 8 * RECORD_WORDS];

        for slot in 0..8 {
            for w in 0..RECORD_WORDS {
                population[slot * RECORD_WORDS + w] = (slot as u32) << 16 | w as u32;
            }
        }

        let mut op_list = [0u32; ITEMS * RECORD_WORDS];
        let rates = BreedRates::default();

        for i in 0..ITEMS {
            let pairing = Pairing {
                parent_a: (i * 2) as u32,
                parent_b: (i * 2 + 1) as u32,
                dest: 100 + i as u32,
                entry_id: i as u32,
                skip_writeback: false,
                seed: [9, 9, 9, 9],
            };

            let base = i * RECORD_WORDS;
            pack_entry(&seam(), &pairing, &rates, &mut op_list[base..base + RECORD_WORDS]);
        }

        let mut out = [0u32; ITEMS * WORK_ITEM_WORDS];
        marshal_serial(&seam(), &op_list, ITEMS, &population, &mut out);

        for i in 0..ITEMS {
            let dst = i * WORK_ITEM_WORDS;
            let src = i * RECORD_WORDS;
            assert_eq!(
                &out[dst..dst + RECORD_WORDS],
                &op_list[src..src + RECORD_WORDS],
                "item {i} entry"
            );

            for half in 0..2 {
                let slot = (i * 2 + half) * RECORD_WORDS;
                let to = dst + RECORD_WORDS * (1 + half);
                assert_eq!(
                    &out[to..to + RECORD_WORDS],
                    &population[slot..slot + RECORD_WORDS],
                    "item {i} parent {half}"
                );
            }
        }
    }

    /// A work item is the entry and two records, and the seam's geometry
    /// says so.
    #[test]
    fn work_item_is_an_entry_and_two_records() {
        let seam = seam();
        assert_eq!(seam.record_words(), RECORD_WORDS);
        assert_eq!(seam.entry_words(), ENTRY_WORDS);
        assert_eq!(seam.work_item_words(), ENTRY_WORDS + 2 * RECORD_WORDS);
        assert_eq!(seam.result_words(), 4);
    }

    /// Ring records parse back to what the fabric writes: fitness low word
    /// first, then the entry id and the sequence number.
    #[test]
    fn results_parse_from_the_ring() {
        let words = [0x1111_2222u32, 0x0000_0003, 0xABCD, 7, 5, 0, 0xBEEF, 8];
        let mut out = [GepResult {
            fitness: 0,
            entry_id: 0,
            seq: 0,
        }; 2];
        parse_results(&seam(), &words, &mut out);

        assert_eq!(
            out[0],
            GepResult {
                fitness: 0x0000_0003_1111_2222,
                entry_id: 0xABCD,
                seq: 7
            }
        );
        assert_eq!(
            out[1],
            GepResult {
                fitness: 5,
                entry_id: 0xBEEF,
                seq: 8
            }
        );
    }
}
