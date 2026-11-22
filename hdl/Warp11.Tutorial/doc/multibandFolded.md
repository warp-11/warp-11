# Multiband, folded

The page before this one shared one multiplier between two stages. This is the
same idea at the size it was built for: the audio stdlib's **8-band stereo
compressor** — forty-eight crossover sections and sixteen band compressors,
hundreds of multipliers laid out spatially — on **one** multiplier, for a part
like the iCE40 UP5K, which has eight.

The body of this design is one word:

```fsharp
streamSource inPorts
|> multibandCompressorFolded "MultibandCompressor8Folded" 46_875.0 "mb" (multibandFoldedSettingsOf settings)
```

`multibandCompressor` is the spatial engine, with the same call shape: a name,
a sample rate, an instance name, a settings record, a stereo stream in, the
stream and a meter out. The two are not interchangeable at this call site,
though, because their laws differ. The spatial engine takes a threshold, a
ratio and sixteen makeup gains on wires; the folded one takes the detector's
two coefficients and a **curve** — a table it reads one word at a time, which
says what the gain is at every level and so replaces the threshold, the ratio
and the makeup gains together. Whether a thing is inline logic, a module or a
fold is still decided where it is *defined*; what a call site sees is the law
it asked for.

## The audio processing

A compressor turns loud down. A *multiband* compressor splits the signal into
frequency bands first and turns each band down by its own amount, so a loud
bass note does not duck the vocal. That is the shape of a mastering
compressor and a broadcast loudness stage. What differs between them is only
where the numbers come from.

The stdlib's engine does it in three steps, per ear, per sample:

**The crossover.** A tree of seven Linkwitz-Riley splits — at 1410 Hz first,
then 525 and 3810, then 320, 860, 2320 and 6250 — each a second-order
low-pass and high-pass pair whose outputs sum to a first-order all-pass. Each
half of a split is then passed through the all-passes of the *other* half's
splits, so the two halves stay in phase, and the eight leaves are the bands.
Every band's path is seven sections long: three splits and four all-passes.
The bands sum to an all-pass — flat to a tenth of a decibel, checked at
100 Hz, 1 kHz and 12 kHz — so with every gain at unity the engine changes
nothing you can hear, and what it does change is only ever what the gains did.

It was not always a tree. The first crossover was *subtractive* — seven
low-passes, band *k* the difference of successive ones — which reconstructs
the input bit for bit and isolates terribly: two low-passes with different
cutoffs are not in phase below either, and the difference of two near-unit
vectors 12° apart is 20% of the signal. A 440 Hz tone came out of the
1.4 kHz band at a third of its level, and a setting with
+30 dB in the high bands put +20 dB on the bass. The tree costs 24 sections
an ear where the split cost 7, and a tone two bands away now leaks at −15 to
−25 dB rather than −5 to −10.

A biquad is the two-pole filter every equaliser is made of:

```
y = b0·x + b1·x1 + b2·x2 − a1·y1 − a2·y2
```

five multiplies of the current and two previous inputs (`x`, `x1`, `x2`) and
the two previous outputs (`y1`, `y2`) by five coefficients, summed. The
coefficients are the filter's shape, Q2.30 fixed point, designed at
elaboration from the cutoff and the sample rate and held in a ROM; the four
*history* words are its memory of the last two samples. Each of the
forty-eight sections — twenty-four an ear — has its own history.

**The compressor**, per band. The law is a **curve**: a gain for every level
the band's envelope can have, tabulated, so the fabric looks the gain up
rather than computing it. Gains travel as the base-two logarithm of
themselves, signed Q5.11, so a gain of zero is unity and one is a doubling.
Four multiplies:

1. **envelope** — a detector follows how loud the band is: it takes the
   magnitude of *last* sample's band (the *peak*), moves the stored envelope
   toward it — `env += α·(peak − env)`, with `α` the attack coefficient when
   the peak is above the envelope and the release coefficient when below —
   and stores the result for next sample.
2. **interpolate** — the envelope *before* this step picks a word of the
   band's curve: the position of its leading one is the octave, the bit under
   it the step within the octave (two entries an octave, 3.01 dB apart), and
   the eight bits under that how far past the entry it falls. A word holds
   the entry's gain and the step to the next entry, so the gain is
   `here + step × fraction ≫ 8` — continuous across entries, with no
   logarithm computed anywhere: the priority encoder *is* the log.
3. **expand** — the gain's fraction becomes a multiplier, `2^f`, from a
   sixteen-point table interpolated the same way, a Q1.15 mantissa.
4. **apply** — `band × mantissa`, shifted by the gain's integer part,
   saturated. The shift is the one variable shifter in the audio path, and a
   folded engine has one of it however many bands it serves.

**The sum.** The eight gained bands of an ear are added and saturated once to
a sample. The loudest envelope across the sixteen bands is kept as a meter
for the host.

Forty-eight sections and sixteen band compressors: 28 × 5 + 20 × 3 + 16 × 4
= **264 multiplies a stereo sample** (a first-order all-pass has three
non-zero taps). Spatially that is 264 multipliers, several DSP blocks each
because the operands are wider than one block. Folded, it is 264 turns of one
multiplier.

## What the word buys

The body of `multibandCompressor8FoldedPrefilteredDef`, in
`hdl/Warp11/Audio.fs`, is this:

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
|> writeDetected stores
|> detect
|> envelope pod io
|> writeEnvelope stores
|> fetchCurve io
|> interpolate pod
|> expand pod
|> applyLog pod
|> sumBands
```

Read it as the engine's own decomposition. The first nine lines are the
crossover: a stereo beat becomes forty-eight **section** beats, the tree's
twenty-four sections for each ear in an order where a section comes after the
sections it reads; each fetches the node it filters, has its taps multiplied
on the shared unit — the history words and the coefficients fetched as each
tap issues — and summed as they land, writes its history and its output node
back, and — if its output is a leaf of the tree — goes on as a **band**. The
next ten are one band's compressor — the four multiplies above, in that
order, with the envelope and last sample's band read from memory before and
written after, and the curve word fetched from its table. The last line sums
eight bands an ear and offers the stereo beat. The two `skidBuffer`s are not
processing at all; they are where the handshake's ready chain is cut, below.

Every stage holds one beat, or costs none. `readState` is `readStageFrom`
told which stores and which addresses; `writeHistory` is a `writeStage`;
`writeDetected` and `writeEnvelope` are `writeThrough`s; `envelope`,
`interpolate`, `expand` and `applyLog` are `podStage`s told what to multiply
and what to make of the product — each law written once, in two halves split
around the shared unit. Nothing is scheduled and no latency is written
anywhere: a beat moves when the next stage can take it, and a stage that
holds one beat always has somewhere for its answer to land, which is all
`warpFu` asks. The one wait in the crossover is exact rather than counted: a
section whose input another section writes waits until that node is marked as
this sample's.

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

**`collectTaps`** — *`Section` → `SectionDone { …; y }`.* Sums products as
they land, in `biquadDef`'s own accumulator width — a product tagged *last*
on the previous landing loads the accumulator, one tagged *subtracts*
subtracts — so the total is the tree's total. The beat after the last
product, the accumulator is shifted down by the coefficients' 30 fraction
bits and saturated to a sample. Products may land before this stage holds
their section (the issuer handed it on at the last grant), so a finished
result waits in `pending` for its beat.

**`writeHistory`** — *`SectionDone` → `SectionDone`.* A `writeStage`: two
writes, `x` and `y` into this parity's slots, then the beat handed on
unchanged.

**`writeNode`** — *`SectionDone` → `SectionDone`.* Free: as the beat moves
on, `y` is written to `(ear, output)` in the node store and the node's parity
bit set to the sample's — which is what a later `readNode` of it is waiting
for.

**`bands`** — *`SectionDone` → `Band { ear; band; value }`.* Free, and a
filter: a section whose output is a leaf offers `y` as that band; any other
section's beat ends here. Sections become bands.

**`readState`** — *`Band` → `BandState { …; env; detected }`.* Reads two
words at `(ear, band)`: from `band_envelope`, this band's envelope as of last
sample, and from `band_detected`, last sample's band value, which is what the
detector listens to. They are two memories rather than two fields of one
because two different stages write them, and two writes to one memory fold
to one port — a cycle where both fired would drop one. Separate memories
make that collision impossible to express.

**`writeDetected`** — *`BandState` → `BandState`.* A `writeThrough`: as the
beat moves on, this sample's band `value` is written to `band_detected` —
next sample's `detected`. It costs no beat and no register: the write fires
on the transfer.

**`detect`** — *`BandState` → `Detected { ear; band; env; value; peak }`.*
Also free: `peak` is `|detected|` saturated to a sample. Negate, pick and
saturate are three carry chains, and the envelope step has two more; this
puts the first three a stage boundary ahead of the other two. `detected`
leaves the beat here.

**`envelope`** — *`Detected` → `Stepped { ear; band; env; value; envNext }`.*
A `podStage`: `envelopeOperands` picks `α` (attack if `peak > env`, else
release) and forms `peak − env`; the pod multiplies; `envelopeFromStep`
shifts the product down 15 bits, adds it to `env`, and clips to the
envelope's range. `env` stays in the beat *unstepped*, because the curve is
read with it.

**`writeEnvelope`** — *`Stepped` → `Stepped`.* `envNext` to `band_envelope`,
on the transfer, for next sample. Free.

**`fetchCurve`** — *`Stepped` → `Curved { ear; band; value; word; fraction; envNext }`.*
`gainTableIndex` splits `env` into the table address — octave and step — and
the fraction past it. A request for word `ear ++ band ++ address` goes out on
the bank's `curve_request` port and is held until the table takes it — a host
reading the table back borrows its one read port for a cycle, and the bank
simply asks again — and the word lands on `curve_answer` a port-depth later.
The fraction is sliced from the same held envelope, so it rides out with the
word. `env` is done with and leaves the beat.

The table is the caller's. On a board it is a register map's `RwArray` that
the host writes a word at a time; here `multibandFoldedSettingsOf` builds it
as a block memory loaded through three ports — `curve_index`, `curve_word`,
`curve_write`. **It boots zeroed, and a zeroed table is unity**: a word of
zero is a gain of zero log2. It is a memory rather than registers because a
table costs a block where a register file costs the fabric, and on an iCE40
that is the difference that matters.

**`interpolate`** — *`Curved` → `Gain { ear; band; value; gainLog; envNext }`.*
A `podStage`: the word's step times the fraction, shifted down 8 bits and
added to the word's own gain.

**`expand`** — *`Gain` → `Mantissa { …; gainLog; mantissa; … }`.* A
`podStage`: the gain's fraction picks one of sixteen points of `2^f`, and the
pod interpolates within it, giving the mantissa. `gainLog` stays, because its
integer part is the next stage's shift.

**`applyLog`** — *`Mantissa` → `Gained { ear; band; gained; envNext }`.* A
`podStage`: `value × mantissa`, then shifted by the gain's integer part and
saturated to the width the sum needs. Only the answer and the meter's
envelope remain.

**`sumBands`** — *`Gained` → stereo sample.* Adds each ear's eight `gained`
values into that ear's accumulator as they arrive — the crossover runs the
ears interleaved, so bands come left, right, left, right — saturates once to
a sample the cycle after that ear's band 7 lands, and once the right ear's
has, offers the stereo beat and holds it until taken. `envNext` is folded into a
running maximum along the way, latched with the beat as the meter.

**The `skidBuffer`s.** A one-beat stage that accepts the next beat in the
same cycle its offer is taken — which is what lets the chain move a beat a
stage per cycle — has a `ready` that depends on the next stage's `ready`,
combinationally, and so on down the chain. A two-beat register buffer whose
`ready` is a function of its own state cuts the chain; `sumBands`, whose
`ready` is its own register, cuts it too.

The band stores are read a stage or two ahead of their writes, and the same
band comes round again sixteen beats later — which is why nothing here needs
to know how long anything takes. The `history` store is the same story a
section at a time: read as a section's taps issue, written a few stages
later, twenty-four sections before the section comes round.

## What to look at

The tutorial preloads a stereo sample, the detector's two coefficients, and
holds `in_valid` and `out_ready` high; the curve is left at boot, which is
unity. **Step N** with 20 and watch the counters:

- `mb_section` and `mb_section_ear` count the forty-eight section beats out
  of the sequencer; the sample was accepted on step 1.
- `mb_biquad_pod_grant` first pulses on step 8 — the first section fetched
  its node first — then five times in a row: one section's five taps, back
  to back, each with its history word and coefficient fetched as it issues.
  Sections then follow at seven steps each, the first-order all-passes
  taking three grants in five.
- `mb_written_0` fills in as sections finish: one bit per node of the left
  ear, set to the sample's parity as each is written. A section that reads a
  node waits in `readNode` for its bit.
- The other four grants begin on step 212, when the tree's first leaf —
  band 0 — finishes and starts down the compressor chain: `envelope` there,
  then `interp` on 233, `expand` on 254 and `apply` on 272, in the gaps
  between the crossover's taps.
- `mb_ear_sum_0` and `mb_ear_sum_1` grow as bands land. On step 535
  `out_valid` goes high with the first stereo beat. It is not the input: the
  tree is an all-pass, and a sample that starts from nothing rings through
  seven sections.

Press **Run**. The next sample is accepted on step 294, before the first has
finished — the crossover is on the second sample while the compressors finish
the first — and from then on a sample every **368 cycles**. At 46 875 Hz on a
24 MHz fabric a sample lasts 512, so the engine is busy 72% of the time.

## The property, and where it is checked

A fold with a wrong schedule still produces plausible audio, so the check
that matters is not a golden vector; it is **the law**. `audio: folded engine
follows its curve`, in `Warp11.Effects`, runs the engine under random stalls
with three flat curves loaded through the three ports: at zero the output
must be the crossover's own all-pass of the input; at one octave every frame
must be exactly twice the unity frame (the mantissa is 32768 and the shift
fourteen, so nothing is truncated); at minus one octave, half of it to within
eight counts, one a band. `audio: folded pass fits an iCE40 frame` measures the cycles
from one accepted sample to the next against the iCEBreaker's 512. The check
on *this* page is smaller: one sample goes through and comes back inside one
frame, and all five clients — `biquad`, `envelope`, `interp`, `expand`,
`apply` — were granted the multiplier along the way.

## What it costs, and what it bought

| | spatial | folded |
|---|---|---|
| multipliers | several per multiply — 1,288 asked of the UP5K's 8 | **one**, shared |
| cycles a stereo sample | 1 | 368 |
| the law | threshold, ratio, sixteen makeup gains | a curve per band, in a table |

Three shapes were tried for the folded side and the first two are worth
knowing about, because the failure modes are general. A **state machine** —
accept, filter, compress, offer — met timing at 376 cycles with every wait a
latency summed by hand, and the multiplier idle for two thirds of the pass. A
**program ROM** driving lockstep pipeline stages ran at 167 cycles and could
not be read: every stage dispatched on the op code it was handed. The chain
above is slower than the second and readable, and readable won.

## Try this

- Poke `out_ready = 0` after the first output and keep running. The chain
  fills from the back — `sumBands` holds, then `applyLog`, then `expand`,
  then `interpolate`, the skid buffers filling in between — until `in_ready`
  drops. Nothing is lost; release it and the beats drain.
- Load a curve word: `curve_index` is the ear, the band and the entry
  (`ear ++ band ++ entry`, ten bits), `curve_word` the entry's gain in the
  low sixteen bits and the step to the next in the high sixteen, both Q5.11
  log2. `curve_word = 0x800` is one octave up — `+6.02 dB` — flat across
  that entry. Set `curve_write = 1`, step once, and put it back. It moves
  only the levels that entry covers; `gainTableWords` in `Audio.fs`
  tabulates a whole curve in decibels, and the `Warp11.Effects` check loads
  one a word a cycle through these same three ports.
- In `Audio.fs`, swap `interpolate` and `expand` in the chain. It does not
  compile: each stage's type says what beat it takes, and `expand` needs a
  `Gain`, which only `interpolate` makes.

## See also

- [**Folding**](folded.md) — the two-stage miniature, with the stage and the
  shared unit shown in full.
- [**Shared unit**](sharedUnit.md) — `warpFu`, and the measurement that says
  when sharing is the wrong answer.
- [**FIR filter**](firFilter.md) — the spatial way to filter: N taps, N
  multipliers, one cycle.
- [**Your own stage**](ownStage.md) — the one-beat worker every stage here is.
