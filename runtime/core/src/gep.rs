//! The WarpCPU genetic-programming cluster's driver — the host half of the
//! streaming redesign, written once against [`RegisterWindow`] so the same code
//! runs against the F# Sim through a bridge and against `/dev/mem` on the
//! KV260.
//!
//! Almost nothing crosses AXI-Lite: the population, the pairing entries, the
//! op-list and the result ring all live in PS DDR, and the fitness table never
//! leaves the fabric at all. This driver owns the *control* flow — program the
//! bases, broadcast the fitness cases, publish entries, poll — while the bulk
//! path is whatever the caller's DDR buffer is.
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

use crate::gep_layout as layout;
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

/// Where the four DDR regions live, as byte addresses the fabric's masters use.
/// The two masks are ring sizes minus one, so both rings must be a power of two
/// in records.
#[derive(Clone, Copy, Debug)]
pub struct GepRegions {
    pub queue_base: u32,
    pub pop_base: u32,
    pub ring_base: u32,
    pub queue_entries: u32,
    pub ring_records: u32,
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
pub fn pack_entry(p: &Pairing, rates: &BreedRates, out: &mut [u32]) {
    let flags = if p.skip_writeback {
        layout::FLAG_SKIP_WRITEBACK
    } else {
        0
    };

    out[layout::ENTRY_PARENT_A] = p.parent_a;
    out[layout::ENTRY_PARENT_B] = p.parent_b;
    out[layout::ENTRY_DEST] = p.dest;
    out[layout::ENTRY_RATE0] = rates.words[0];
    out[layout::ENTRY_RATE0 + 1] = rates.words[1];
    out[layout::ENTRY_RATE0 + 2] = rates.words[2];
    out[layout::ENTRY_RATE0 + 3] = rates.words[3];
    out[layout::ENTRY_FLAGS_WORD] = (rates.words[4] & 0xFFFF) | (flags << 16);
    out[layout::ENTRY_SIGMA] = rates.sigma_fx;
    out[layout::ENTRY_RANGE] = rates.range_fx;
    out[layout::ENTRY_ID] = p.entry_id;
    out[11] = 0;
    out[layout::ENTRY_SEED0..layout::ENTRY_SEED0 + 4].copy_from_slice(&p.seed);
}

/// One fitness-ring record as the fabric writes it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct GepResult {
    pub fitness: u64,
    pub entry_id: u32,
    pub seq: u32,
}

/// Parse `count` ring records out of a word buffer.
pub fn parse_results(words: &[u32], out: &mut [GepResult]) {
    for (r, slot) in out.iter_mut().enumerate() {
        let base = r * layout::RESULT_WORDS;
        *slot = GepResult {
            fitness: (words[base + 1] as u64) << 32 | words[base] as u64,
            entry_id: words[base + 2],
            seq: words[base + 3],
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
pub fn marshal_serial(op_list: &[u32], item_count: usize, population: &[u32], out: &mut [u32]) {
    for i in 0..item_count {
        let src = i * layout::RECORD_WORDS;
        let dst = i * layout::WORK_ITEM_WORDS;
        out[dst..dst + layout::RECORD_WORDS].copy_from_slice(&op_list[src..src + layout::RECORD_WORDS]);
        // The tail of the padded stride is never read by the fabric; zero it so
        // a stale buffer cannot look like data if that ever changes.
        out[dst + layout::WORK_ITEM_PAYLOAD_WORDS..dst + layout::WORK_ITEM_WORDS].fill(0);

        for (half, slot) in [op_list[src] as usize, op_list[src + 1] as usize]
            .iter()
            .enumerate()
        {
            let from = slot * layout::RECORD_WORDS;
            let to = dst + layout::RECORD_WORDS * (1 + half);
            out[to..to + layout::RECORD_WORDS].copy_from_slice(&population[from..from + layout::RECORD_WORDS]);
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
}

impl<W: RegisterWindow> GepClusterDevice<W> {
    pub fn new(window: W) -> Self {
        GepClusterDevice { window }
    }

    pub fn window(&mut self) -> &mut W {
        &mut self.window
    }

    fn field(&mut self, offset: usize, shift: u32, mask: u32) -> Result<u32, W::Error> {
        Ok((self.window.read32(offset)? & mask) >> shift)
    }

    /// Program the DDR geometry and the case count, then pulse start — which
    /// latches all of it, frees every breeder, and zeroes every counter.
    ///
    /// The masks are derived here rather than taken, so a caller cannot program
    /// a ring size that is not a power of two.
    pub fn start(&mut self, regions: GepRegions, n_cases: u32) -> Result<(), W::Error> {
        self.window.write32(layout::QUEUE_BASE_OFFSET, regions.queue_base)?;
        self.window.write32(layout::POP_BASE_OFFSET, regions.pop_base)?;
        self.window.write32(layout::RING_BASE_OFFSET, regions.ring_base)?;
        self.window
            .write32(layout::QUEUE_MASK_OFFSET, regions.queue_entries - 1)?;
        self.window
            .write32(layout::RING_MASK_OFFSET, regions.ring_records - 1)?;
        self.window.write32(layout::N_CASES_OFFSET, n_cases)?;
        self.window
            .write32(layout::START_QUEUE_OFFSET, 1 << layout::START_QUEUE_BIT)
    }

    /// Broadcast the epoch's fitness cases into every lane's resident table.
    /// `rows` is one row per case: `var_count` inputs then the target, so the
    /// caller's stride and the fabric's field index cannot drift apart.
    pub fn load_cases(&mut self, rows: &[&[u32]]) -> Result<(), W::Error> {
        for (index, row) in rows.iter().enumerate() {
            self.window.write32(layout::CASE_ADDR_OFFSET, index as u32)?;

            for (field, value) in row.iter().enumerate() {
                self.window.write32(layout::CASE_FIELD_OFFSET, field as u32)?;
                self.window.write32(layout::CASE_DATA_OFFSET, *value)?;
                self.window
                    .write32(layout::LD_CASE_OFFSET, 1 << layout::LD_CASE_BIT)?;
            }
        }

        Ok(())
    }

    /// Publish the queue tail. The fabric chases it, so this may be bumped
    /// repeatedly while work is in flight — that is the trickle the run-forever
    /// mode is built on.
    pub fn publish(&mut self, entries: u32) -> Result<(), W::Error> {
        self.window.write32(layout::ENTRIES_PUBLISHED_OFFSET, entries)
    }

    pub fn results_done(&mut self) -> Result<u32, W::Error> {
        self.field(
            layout::RESULTS_DONE_OFFSET,
            layout::RESULTS_DONE_SHIFT,
            layout::RESULTS_DONE_MASK,
        )
    }

    pub fn all_idle(&mut self) -> Result<bool, W::Error> {
        Ok(self.field(
            layout::ALL_IDLE_OFFSET,
            layout::ALL_IDLE_SHIFT,
            layout::ALL_IDLE_MASK,
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
            cycle_count: self.window.read32(layout::CYCLE_COUNT_OFFSET)?,
            results_done: self.window.read32(layout::RESULTS_DONE_OFFSET)?,
            entries_taken: self.window.read32(layout::ENTRIES_TAKEN_OFFSET)?,
            feed_stall_cycles: self.window.read32(layout::FEED_STALL_CYCLES_OFFSET)?,
            breeder_stall_cycles: self.window.read32(layout::BREEDER_STALL_CYCLES_OFFSET)?,
            fill_busy_cycles: self.window.read32(layout::FILL_BUSY_CYCLES_OFFSET)?,
            pack_busy_cycles: self.window.read32(layout::PACK_BUSY_CYCLES_OFFSET)?,
            emit_busy_cycles: self.window.read32(layout::EMIT_BUSY_CYCLES_OFFSET)?,
            busy_breeder_cycles: self.window.read32(layout::BUSY_BREEDER_CYCLES_OFFSET)?,
            busy_lane_cycles: self.window.read32(layout::BUSY_LANE_CYCLES_OFFSET)?,
            streams_active: self.field(
                layout::STREAMS_ACTIVE_OFFSET,
                layout::STREAMS_ACTIVE_SHIFT,
                layout::STREAMS_ACTIVE_MASK,
            )?,
        })
    }

    /// Busy cycles for one breeder or one lane, out of the fixed counter
    /// blocks — the per-instance view a balance report is built from.
    pub fn breeder_busy(&mut self, breeder: usize) -> Result<u32, W::Error> {
        self.window
            .read32(layout::BREEDER0_BUSY_CYCLES_OFFSET + 4 * breeder)
    }

    pub fn lane_busy(&mut self, lane: usize) -> Result<u32, W::Error> {
        self.window.read32(layout::LANE0_BUSY_CYCLES_OFFSET + 4 * lane)
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
        self.window.write32(layout::AUTO_POP_OFFSET, pop)?;
        self.window.write32(layout::AUTO_GENS_OFFSET, generations)?;
        self.window.write32(layout::AUTO_R0_OFFSET, rates.words[0])?;
        self.window.write32(layout::AUTO_R1_OFFSET, rates.words[1])?;
        self.window.write32(layout::AUTO_R2_OFFSET, rates.words[2])?;
        self.window.write32(layout::AUTO_R3_OFFSET, rates.words[3])?;
        self.window.write32(layout::AUTO_R4_OFFSET, rates.words[4])?;
        self.window.write32(layout::AUTO_SIGMA_OFFSET, rates.sigma_fx)?;
        self.window.write32(layout::AUTO_RANGE_OFFSET, rates.range_fx)?;
        self.window.write32(layout::AUTO_S0_OFFSET, seed[0])?;
        self.window.write32(layout::AUTO_S1_OFFSET, seed[1])?;
        self.window.write32(layout::AUTO_S2_OFFSET, seed[2])?;
        self.window.write32(layout::AUTO_S3_OFFSET, seed[3])
    }

    /// Arm or disarm the loop. Must be set before `start`, which is what
    /// latches it — and the selection RNG is seeded by that same pulse.
    pub fn set_auto_mode(&mut self, on: bool) -> Result<(), W::Error> {
        self.window.write32(layout::AUTO_MODE_OFFSET, on as u32)
    }

    /// The op-list pass's two carry-over flags: skip the score round because
    /// the previous breed pass already filled the fitness table, and continue
    /// the selection stream rather than reseeding it. Both are for generation
    /// 1 and later; generation 0 clears them.
    pub fn set_pass_flags(&mut self, skip_score: bool, rng_continue: bool) -> Result<(), W::Error> {
        self.window
            .write32(layout::SKIP_SCORE_OFFSET, skip_score as u32)?;
        self.window
            .write32(layout::RNG_CONTINUE_OFFSET, rng_continue as u32)
    }

    pub fn set_oplist_base(&mut self, base: u32) -> Result<(), W::Error> {
        self.window.write32(layout::OPLIST_BASE_OFFSET, base)
    }

    /// Emitted AND committed: every entry accepted and every beat B-acked, so
    /// the op-list ring is safe to read the instant this is true.
    pub fn oplist_done(&mut self) -> Result<bool, W::Error> {
        Ok(self.field(
            layout::OPLIST_DONE_OFFSET,
            layout::OPLIST_DONE_SHIFT,
            layout::OPLIST_DONE_MASK,
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
            layout::AUTO_DONE_OFFSET,
            layout::AUTO_DONE_SHIFT,
            layout::AUTO_DONE_MASK,
        )? == 1)
    }

    pub fn auto_round(&mut self) -> Result<u32, W::Error> {
        self.window.read32(layout::AUTO_ROUND_OFFSET)
    }

    /// Which population region is current. The loop ping-pongs, so a host
    /// reading the final population needs this to know which half to read.
    pub fn auto_base(&mut self) -> Result<u32, W::Error> {
        self.field(
            layout::AUTO_BASE_OFFSET,
            layout::AUTO_BASE_SHIFT,
            layout::AUTO_BASE_MASK,
        )
    }

    /// The last completed round's argmin: the elite's index within its region,
    /// and its fitness.
    pub fn best(&mut self) -> Result<(u32, u64), W::Error> {
        let idx = self.field(
            layout::BEST_IDX_OFFSET,
            layout::BEST_IDX_SHIFT,
            layout::BEST_IDX_MASK,
        )?;
        let lo = self.window.read32(layout::BEST_FIT_LO_OFFSET)? as u64;
        let hi = self.window.read32(layout::BEST_FIT_HI_OFFSET)? as u64;
        Ok((idx, hi << 32 | lo))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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

        let mut words = [0u32; layout::RECORD_WORDS];
        pack_entry(&pairing, &rates, &mut words);

        assert_eq!(words[layout::ENTRY_PARENT_A], 5);
        assert_eq!(words[layout::ENTRY_PARENT_B], 9);
        assert_eq!(words[layout::ENTRY_DEST], 40);
        assert_eq!(words[layout::ENTRY_RATE0], 0x2222_1111);
        assert_eq!(words[layout::ENTRY_RATE0 + 1], 0x4444_3333);
        assert_eq!(words[layout::ENTRY_RATE0 + 2], 0x6666_5555);
        assert_eq!(words[layout::ENTRY_RATE0 + 3], 0x8888_7777);
        // Word 7 is the flags over the geneRecomb half.
        assert_eq!(
            words[layout::ENTRY_FLAGS_WORD],
            (layout::FLAG_SKIP_WRITEBACK << 16) | 0x9999
        );
        assert_eq!(words[layout::ENTRY_SIGMA], 0x0001_999A);
        assert_eq!(words[layout::ENTRY_RANGE], 0x000A_0000);
        assert_eq!(words[layout::ENTRY_ID], 0xD00D);
        assert_eq!(words[11], 0);
        assert_eq!(&words[layout::ENTRY_SEED0..], &[1, 2, 3, 4]);
    }

    /// The gather puts each entry's two parent records immediately behind it,
    /// which is the whole claim: one sequential burst per work item.
    #[test]
    fn marshal_lays_parents_behind_their_entry() {
        const ITEMS: usize = 3;
        let mut population = [0u32; 8 * layout::RECORD_WORDS];

        for slot in 0..8 {
            for w in 0..layout::RECORD_WORDS {
                population[slot * layout::RECORD_WORDS + w] = (slot as u32) << 16 | w as u32;
            }
        }

        let mut op_list = [0u32; ITEMS * layout::RECORD_WORDS];
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

            let base = i * layout::RECORD_WORDS;
            pack_entry(&pairing, &rates, &mut op_list[base..base + layout::RECORD_WORDS]);
        }

        let mut out = [0u32; ITEMS * layout::WORK_ITEM_WORDS];
        marshal_serial(&op_list, ITEMS, &population, &mut out);

        for i in 0..ITEMS {
            let dst = i * layout::WORK_ITEM_WORDS;
            let src = i * layout::RECORD_WORDS;
            assert_eq!(
                &out[dst..dst + layout::RECORD_WORDS],
                &op_list[src..src + layout::RECORD_WORDS],
                "item {i} entry"
            );

            for half in 0..2 {
                let slot = (i * 2 + half) * layout::RECORD_WORDS;
                let to = dst + layout::RECORD_WORDS * (1 + half);
                assert_eq!(
                    &out[to..to + layout::RECORD_WORDS],
                    &population[slot..slot + layout::RECORD_WORDS],
                    "item {i} parent {half}"
                );
            }
        }
    }

    /// The payload is the entry plus two records; the STRIDE is padded so the
    /// fabric can fetch a whole item as one burst without crossing a 4 KB
    /// boundary. Both numbers matter and they are not the same one.
    #[test]
    fn work_item_stride_matches_the_fabric() {
        assert_eq!(layout::WORK_ITEM_PAYLOAD_WORDS, 3 * layout::RECORD_WORDS);
        assert_eq!(layout::RECORD_BYTES, 64);
        assert_eq!(layout::WORK_ITEM_BYTES, layout::WORK_ITEM_WORDS * 4);
        // The stride must divide 4 KB, or a burst straddles the boundary.
        assert_eq!(4096 % layout::WORK_ITEM_BYTES, 0);
        assert!(layout::WORK_ITEM_BYTES >= layout::WORK_ITEM_PAYLOAD_WORDS * 4);
        assert_eq!(layout::WORK_ITEM_BEATS * 16, layout::WORK_ITEM_PAYLOAD_WORDS * 4);
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
        parse_results(&words, &mut out);

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
