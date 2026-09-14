# Multiband, folded

The page before this one shared one multiplier between two stages. This is the
same idea at the size it was built for: the audio stdlib's **8-band stereo
compressor** — fourteen crossover biquads and sixteen band compressors, 588 DSP
blocks laid out spatially on a KV260 — on **one** multiplier, so that it fits
six of an iCE40 UP5K's eight. The bits are the spatial engine's, checked.

The body of this design is one word:

```fsharp
streamSource inPorts
|> multibandCompressorFolded "MultibandCompressor8Folded" 46_875.0 "mb" settings
```

Write `multibandCompressor` there instead and you have the spatial engine —
same ports, same settings record, same samples, 588 multipliers. Nothing at
the call site can tell the two apart, which is the rule the audio stdlib is
written to: whether a thing is inline logic, a module, or a fold, is decided
where it is *defined*.

## The audio processing

A compressor turns loud down. A *multiband* compressor splits the signal into
frequency bands first and turns each band down by its own amount, so a loud
bass note does not duck the vocal. That is the shape of a mastering
compressor, a broadcast loudness stage, and a hearing aid, where each band's
gain is the prescription for that part of the audiogram. What differs between
those three is only where the numbers come from.

The stdlib's engine does it in three steps, per ear, per sample:

**The crossover.** A tree of seven Linkwitz-Riley splits — at 1410 Hz first,
then 525 and 3810, then 320, 860, 2320 and 6250 — each a second-order
low-pass and high-pass pair whose outputs sum to a first-order all-pass. Each
half of a split is then passed through the all-passes of the *other* half's
splits, so the two halves stay in phase, and the eight leaves are the bands.
Every band's path is seven sections long: three splits and four all-passes.
The bands sum to an all-pass — flat to a hundredth of a decibel, checked at
100 Hz, 1 kHz and 12 kHz — so with every gain at unity the engine changes
nothing you can hear, and what it does change is only ever what the gains did.

It was not always a tree. The first crossover was *subtractive* — seven
low-passes, band *k* the difference of successive ones — which reconstructs
the input bit for bit and isolates terribly: two low-passes with different
cutoffs are not in phase below either, and the difference of two near-unit
vectors 12° apart is 20% of the signal. A 440 Hz tone came out of the
1.4 kHz band at a third of its level, and a hearing-aid prescription with
+30 dB in the high bands put +20 dB on the bass. The tree costs 24 sections
an ear where the split cost 7, and a tone two bands away now leaks at −15 to
−25 dB rather than −5 to −10.

A biquad is the two-pole filter every equaliser is made of:

```
y = b0·x + b1·x1 + b2·x2 − a1·y1 − a2·y2
```

five multiplies of the current and two previous inputs (`x`, `x1`, `x2`) and
the two previous outputs (`y1`, `y2`) by five coefficients, summed. The
coefficients are the filter's shape, Q2.30 fixed point, designed on the host
from the cutoff and the sample rate; the four *history* words are its memory
of the last two samples. Each of the fourteen sections has its own history.

**The compressor**, per band, the same four multiplies `monoBandCompressor`
does in the spatial engine:

1. **boost** — the band times its ear's *makeup* gain for that band, a Q8.8
   word from a sixteen-word table: `boosted = band × makeup ≫ 8`. This is
   where the prescription goes in.
2. **envelope** — a detector follows how loud the band is: it takes the
   magnitude of *last* sample's boosted value (the *peak*), moves the stored
   envelope toward it — `env += α·(peak − env)`, with `α` the attack
   coefficient when the peak is above the envelope and the release
   coefficient when below — and stores the result for next sample.
3. **reduction** — the gain from the envelope: how far it is over the
   threshold, times the ratio, taken away from unity, clipped:
   `gain = 1 − min(excess × ratio, 1)`. It reads the envelope *before* this
   sample's step, which is the spatial engine's order too.
4. **apply** — `gained = boosted × gain ≫ 24`, saturated to the band's width.

**The sum.** The eight gained bands of an ear are added and saturated once to
a sample. The loudest envelope across the sixteen bands is kept as a meter
for the host.

Forty-eight sections and sixteen band compressors: 28 × 5 + 20 × 3 + 16 × 4
= **264 multiplies a stereo sample** (a first-order all-pass has three
non-zero taps). Spatially that is 264 multipliers, several DSP blocks each on
a KV260 because the operands are wider than one block. Folded, it is 264
turns of one multiplier.

## What the word buys

The body of `multibandCompressor8FoldedDef`, in `hdl/Warp11/Audio.fs`, is
this:

```fsharp
streamOfStereo io.s
|> sections shape
|> readNode shape stores
|> skidBuffer "node_skid" (sectionLayout shape)
|> issueTaps shape stores biquad
|> collectTaps shape biquad
|> writeHistory shape stores
|> writeNode shape stores
|> bands
|> skidBuffer "band_skid" bandLayout
|> readState stores
|> boost pod io
|> writeDetected stores
|> detect
|> envelope pod io
|> skidBuffer "stepped_skid" steppedLayout
|> writeEnvelope stores
|> reduction pod io
|> apply pod
|> sumBands
```

Read it as the spatial design's own decomposition, because it is. The first
nine lines are the crossover: a stereo beat becomes forty-eight **section**
beats, the tree's twenty-four sections for each ear in an order where a
section comes after the sections it reads; each fetches the node it filters,
has its taps multiplied on the shared unit — the history words and the
coefficients fetched as each tap issues — and summed as they land, writes its
history and its output node back, and — if its output is a leaf of the tree
— goes on as a **band**. The next ten are one band's compressor — the same
four multiplies `monoBandCompressor` does, in the same order, with the
envelope and last boosted value read from a store before and written after,
and the makeup gain fetched from its table. The last line sums eight bands an
ear and offers the stereo beat. The three `skidBuffer`s are not processing at
all; they are where the handshake's ready chain is cut, below.

Every stage holds one beat. `readState` is `readStage` told which store and
which addresses; `envelope`, `reduction` and `apply` are `podStage` told what
to multiply and what to make of the product — the two halves of the spatial
engine's own arithmetic, split around the shared unit. There are three
generic stages, a buffer, and seventeen lines that use them. Nothing is
scheduled and no latency is written anywhere: a beat moves when the next
stage can take it, and a stage that holds one beat always has somewhere for
its answer to land, which is all `warpFu` asks. The one wait in the crossover
is exact rather than counted: a section whose input another section writes
waits until that node is marked as this sample's.

## The stages, one by one

What flows between the stages is a **beat** — a small record, the fields a
stage's registers hold — and each stage's type says what it needs and what it
adds. Reading down the chain is reading the beat grow and shrink.

**`sections`** — *stereo sample → `Section`, forty-eight per sample.*
Accepts a stereo beat and holds both samples and a parity bit that flips per
sample; then two counters walk the tree's twenty-four sections, left ear then
right for each, reading a program ROM that says which node the section reads,
which it writes, whether that node is a band, and whether the section is
first order. The next sample is accepted only after all forty-eight have
gone, which is what guarantees a section is never in the chain twice.

**`readNode`** — *`Section` → `Section`, with `x` now the value to filter.*
Node 0 is the ear's sample, carried in the beat. Any other node was written
by an earlier section of this sample — and the stage **waits** until it has
been: each node keeps the parity of the sample that last wrote it, and the
read is issued only once that matches the beat's. That wait is the only
ordering in the crossover, it is exact, and it is why no stage needs to know
how far apart a producer and its consumer are.

**`issueTaps`** — *`Section` → `Section`.* Issues the section's multiplies
to the shared unit, one per grant: `x·b0, x1·b1, x2·b2, y1·a1, y2·a2` for a
second-order section, `x·b0, x1·b1, y1·a1` for a first-order all-pass whose
other two taps are zero. Coefficients come from the ROM as needed, and the
history words from their store the same way — two ports each, one at the tap
the counter names and one at the next, so the right word is there whether or
not the last cycle was a grant. Nothing rides in the beat that a store can
supply. Each product goes out with a two-bit tag: *subtracts* and *last*.
The beat is handed on the moment the last tap is granted, so the next section
issues while this one's products are still landing.

The history is two slots each of `x` and `y`, and the slot a sample writes is
the sample's *parity* — the bit `sections` flips per sample and puts on every
beat. This sample's `x` goes where the sample before last's was, so `x1` is
at the other parity and `x2` at this one: a sample writes two words, and
nothing is ever shifted.

**`collectTaps`** — *`Section` → `SectionDone { …; y }`.* Sums
products as they land, in `biquadDef`'s own 59-bit width — a product tagged
*last* on the previous landing loads the accumulator, one tagged *subtracts*
subtracts — so the total is exactly what the spatial adder tree produces.
The beat after the last product, the accumulator is shifted down by the
coefficients' 30 fraction bits and saturated to a sample. Products may land
before this stage holds their section (the issuer handed it on two cycles
before the last product), so a finished result waits in `pending` for its
beat.

**`writeHistory`** — *`SectionDone` → `SectionDone`.* A `writeStage`: two
writes, `x` and `y` into this parity's slots, one a cycle, then the beat
handed on unchanged.

**`writeNode`** — *`SectionDone` → `SectionDone`.* Free: as the beat moves
on, `y` is written to `(ear, output)` in the node store and the node's parity
bit set to the sample's — which is what a later `readNode` of it is waiting
for.

**`bands`** — *`SectionDone` → `Band { ear; band; value }`.* Free, and a
filter: a section whose output is a leaf offers `y` as that band; any other
section's beat ends here. Sections become bands.

**`readState`** — *`Band` → `BandState { …; env; detected }`.* A `readStage`:
two words from the `band_state` store, `(ear, band, envelope)` — this band's
envelope as of last sample — and `(ear, band, boosted)` — its boosted value
from last sample, which is what the detector listens to.

**`boost`** — *`BandState` → `Boosted { ear; band; env; detected; boosted }`.*
Two steps in one stage, so the beat is held once. First the fetch: a request
for word `ear·8 + band` goes out on the bank's *makeup* port and is held
until the table takes it — a host reading the table back borrows its one
read port for a cycle, and the bank simply asks again — and the gain lands a
port-depth later. Then the multiply, as a `podStage` would: the band value
times the gain, `boosted = product ≫ 8`, the Q8.8 fraction bits dropped. The
band's raw `value` is done with; `boosted` replaces it in the beat.

The table is the caller's. On the board it is a register map's `RwArray`: a
block memory that boots at the prescription and that the host rewrites a
word at a time over the serial link. Here it boots at unity and is loaded
through three ports. It is a memory rather than sixteen registers because on
a UP5K the register file was the difference between fitting and not — a
window costs a block, a register file costs the fabric — and because
everything the fitting side will add to a band later is another word.

**`writeDetected`** — *`Boosted` → `Boosted`.* A `writeThrough`: as the beat
moves on, `boosted` is written to `(ear, band, boosted)` — next sample's
`detected`. It costs no beat and no register: the write fires on the
transfer.

**`detect`** — *`Boosted` → `Detected { ear; band; env; boosted; peak }`.*
Also free: `peak` is `|detected|` saturated to a sample. It is its own stage
only for timing — negate, pick and saturate are three carry chains, and the
envelope step has two more; a stage boundary between them is what lets the
part make 24 MHz. `detected` leaves the beat here.

**`envelope`** — *`Detected` → `Stepped { ear; band; env; boosted; envNext }`.*
A `podStage`, and the one law written once: `envelopeOperands` picks `α`
(attack if `peak > env`, else release) and forms `peak − env`; the pod
multiplies; `envelopeFromStep` shifts the product down 15 bits, adds it to
`env`, and clips to the envelope's range. `env` stays in the beat *unstepped*,
because the next stage wants it that way.

**`writeEnvelope`** — *`Stepped` → `Stepped`.* `envNext` to
`(ear, band, envelope)`, on the transfer, for next sample. Free.

**`reduction`** — *`Stepped` → `Gain { ear; band; boosted; gain; envNext }`.*
A `podStage` around `gainComputer`'s halves: `gainExcess` is
`max(0, env − threshold)`; the pod multiplies it by the ratio;
`gainFromReduction` clips the product at unity and subtracts it from unity,
giving a Q0.24 gain. `env` has done its two jobs and leaves the beat.

**`apply`** — *`Gain` → `Gained { ear; band; gained; envNext }`.* A
`podStage`: `boosted × gain`, shifted down 24 bits, saturated to the band's
width. Only the answer and the meter's envelope remain.

**`sumBands`** — *`Gained` → stereo sample.* Adds each ear's eight `gained`
values into that ear's accumulator as they arrive — the crossover runs the
ears interleaved, so bands come left, right, left, right — saturates once to
a sample at band 7, and when the right ear's band 7 has landed offers the
stereo beat and holds it until taken. `envNext` is folded into a running
maximum along the way, latched with the beat as the meter.

**The `skidBuffer`s.** A one-beat stage that accepts the next beat in the
same cycle its offer is taken — which is what lets the chain move a beat a
stage per cycle — has a `ready` that depends on the next stage's `ready`,
combinationally, and so on down the chain: fourteen stages and 46 ns on an
iCE40, measured. A two-beat register buffer whose `ready` is a function of
its own state cuts the chain. Three of them keep every run under six stages.

One stage reads the `band_state` store and two write it, and the reads of
one sample's band are a stage or two ahead of the writes, sixteen beats before
the same band comes round again — which is why nothing here needs to know how
long anything takes. The `history` store is the same story a section at a
time: read as a section's taps issue, written a few stages later, twenty-four
sections before the section comes round.

## What to look at

The tutorial preloads a stereo sample, the demo's compressor settings, and
unity band gains. **Step N** with 20 and watch the counters:

- `mb_section` and `mb_section_ear` count the forty-eight section beats out
  of the sequencer; the sample was accepted on step 1.
- `mb_biquad_pod_grant` first pulses on step 8 — the first section fetched
  its node first — then five times in a row: one section's five taps, back
  to back, each with its history word and coefficient fetched as it issues.
  Sections then follow at five to seven steps each, the first-order
  all-passes taking three grants.
- `mb_written_0` fills in as sections finish: one bit per node of the left
  ear, set to the sample's parity as each is written. A section that reads a
  node waits in `readNode` for its bit.
- The other four grants begin around step 219, when the tree's first leaf —
  band 0 — finishes and starts down the compressor chain; `boost`,
  `envelope`, `reduction`, `apply` each pulse as that band reaches them, in
  the gaps between the crossover's taps.
- `mb_ear_sum_0` and `mb_ear_sum_1` grow as bands land. On step 503
  `out_valid` goes high with the first stereo beat. It is not the input: the
  tree is an all-pass, and a sample that starts from nothing rings through
  seven sections. What it is, exactly, is what the spatial engine makes of the
  same sample, which is the living check.

Press **Run**. The next sample is accepted on step 291, before the first has
finished — the crossover is on the second sample while the compressors finish
the first — and from then on a sample every **341 cycles**. At 46 875 Hz on a
24 MHz fabric a sample lasts 512, so the engine is busy 67% of the time.

## The property, and where it is checked

A fold with a wrong schedule still produces plausible audio. The check that
matters is not a golden vector; it is **the spatial engine on the same
stimulus**: `audio: folded engine matches the spatial one` in
`Warp11.Effects` runs both under random stalls at three settings and compares
frame for frame. It has been zero frames different through every version of
this engine — three shapes and a new crossover in a day — which is what let
it be rewritten without fear. The check on *this* page is smaller: one sample
through, what the spatial engine makes of it comes back, and all five clients
were granted along the way.

## What it costs, and what it bought

| | spatial | folded |
|---|---|---|
| DSP blocks (UP5K has 8) | hundreds | **6** |
| block RAMs | 0 | 16 |
| logic cells, with the I2S link, limiter and a serial register map | does not fit | 4,486 of 5,280 |
| cycles a stereo sample | 1 | 341 |
| the bits | — | identical |

Three shapes were tried for the folded side and the first two are worth
knowing about, because the failure modes are general. A **state machine** —
accept, filter, compress, offer — met timing at 376 cycles with every wait a
latency summed by hand, and the multiplier idle for two thirds of the pass. A
**program ROM** driving lockstep pipeline stages ran at 167 cycles and could
not be read: every stage dispatched on the op code it was handed. The chain
above is slower than the second and readable, and readable won.

## Try this

- Change the word. `multibandCompressor` for `multibandCompressorFolded` in
  the source, and re-run: the ports are the same, the checks still pass, and
  the `verilog` tab is 588 multiplies long.
- Poke `out_ready = 0` after the first output and keep running. The chain
  fills from the back — `sumBands` holds, then `apply`, then `reduction`, the
  skid buffers filling in between — until `in_ready` drops. Nothing is lost;
  release it and the beats drain.
- Load a gain: poke `makeup_index = 3`, `makeup_gain = 512` (two times unity
  on the left ear's fourth band), `makeup_write = 1`, step once, and put
  `makeup_write` back. Watch `out_left` diverge from `out_right` on the next
  sample. The table is live; on the board it is the hearing prescription,
  and the host rewrites it the same way.

## See also

- [**Folding**](folded.md) — the two-stage miniature, with the stage and the
  shared unit shown in full.
- [**Shared unit**](sharedUnit.md) — `warpFu`, and the measurement that says
  when sharing is the wrong answer.
- [**FIR filter**](firFilter.md) — the spatial way to filter: N taps, N
  multipliers, one cycle.
- [**Your own stage**](ownStage.md) — the one-beat worker every stage here is.
