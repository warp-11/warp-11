# Folding

A design has more multiplies than the part has multipliers. That is the whole
situation, and it is common: the 8-band compressor in the audio stdlib is 588
DSP blocks laid out spatially, and the iCE40 it was wanted on has 8. **Folding**
is doing the same arithmetic on fewer units than it has operations, and this
page is that engine in miniature — two multiplies, one multiplier.

The value is scaled twice, `y = (x · a) · b`, both ways at once:

- `spatial` — two multipliers in a row, as a free `streamMap`. This is every
  stream lesson so far.
- `out` — two **stages**, one per multiply, sharing **one** multiplier. Each
  stage holds a beat, asks for the unit, and offers the product when it lands.

The bits are identical. What differs is one multiplier against two, and a few
cycles.

## What to look at

The tutorial preloads `a = 3`, `b = 5`, `in_value = 7`, and both `ready`s.
Before you step, `spatial_valid` is already high with `spatial_value = 105`:
the map costs nothing, so its answer is offered the moment the beat is. Now
Step, and count:

- step 1 — `first_pod_grant` pulses: the first stage took the beat and was
  given the multiplier at once.
- step 5 — `second_pod_grant` pulses: the second stage has the first's product
  (21) and asks for the unit itself. On the same step `in_ready` and
  `spatial_valid` go high again — the first stage is free, and the next beat
  goes in.
- step 8 — `out_valid` goes high with `out_value = 105`.

Keep stepping with `in_valid` held high. The grants **alternate** — first on
step 6, second on 10, first on 11 — one beat every five cycles, because the
two stages are both busy and there is one multiplier between them. Neither
stage knows the other exists. That is the arbiter from
[**Shared unit**](sharedUnit.md), doing what it did there, for clients that
happen to be consecutive stages of one pipeline.

## The stage

```fsharp
let private times pod name gain outWidth s =
    let st, out = streamFsm s (layout1 ($"{name}_value", outWidth))
    let held = reg $"{name}_held" (width s.payload)
    If (st.Is Accepting &&& s.valid) (fun () -> s.payload ==> held)

    let client = pod.Client name
    pad (width client.a) held ==> client.a
    pad (width client.b) gain ==> client.b
    // ...ask once, hold the product when it lands, Goto Offering
```

It is [**Your own stage**](ownStage.md) with the grind replaced by a request.
`streamFsm` gives it the three states every one-beat worker has — accepting,
working, offering — and the module's own flow control: while it holds a beat
its `ready` is low, and its offer stays up until taken. What it does while
working is ask the shared unit and wait for `landed`.

**One beat at a time is not a limitation here, it is the contract.** `warpFu`
never reads a client's writeback `ready`; a presented result must be taken.
A stage that holds one beat always has somewhere for its one answer to go,
so the contract is met by shape rather than by a buffer.

## The unit

```fsharp
let pod = SharedMultiplier("pod", 12, 4)

toFolded
|> times pod "first" a 12
|> times pod "second" b 16
|> streamSink outPorts

pod.Finish()
```

Read the chain first: it is the spatial version's two multiplies, as two
lines. Then the two lines around it. `SharedMultiplier` hands each stage a
**client** — six wires: the operands and a request the stage drives, a grant,
a product and a `landed` the unit drives. `Finish`, called last, puts every
client behind `warpFu`, with the registered multiply as the core. The clients
have to come first, because a stage needs its wires before the arbiter can
exist; the arbiter is wired up to them afterwards, which is what nets allow.

Nothing in the chain says how many cycles the multiplier takes. The core
*reports* its depth to `warpFu` — the [**Shared unit**](sharedUnit.md)
lesson — and the stages never learn it: they wait for `landed`. Change the
core to three stages and nothing else moves.

## Why this is the shape

The first folded engine written for the real compressor was a state machine:
accept, run the filters, run the compressors, offer, each state waiting a
counted number of cycles for the last. It worked and it met timing, and it was
wrong in two ways that took a reader to see. Every wait was a latency summed
by hand — `applyAt = reductionAt + pod.Depth + 1` — which is the thing the
no-latency rule exists to forbid, and it rots the first time anything moves.
And a state machine whose states are a straight line is a *pipeline written as
a schedule*: the multiplier idled through every fetch, and one beat was in
flight where several could be.

The shape here is the rule that replaced it: **an FSM whose states are a
straight line is a sequence of streams.** Each operation is a stage; the
expensive thing is shared behind them; a beat moves when the next stage can
take it. The full engine — `multibandCompressorFolded` in `Audio.fs` — is
sixteen such lines, and reads as the crossover, then each band's compressor
steps, then the sum: the spatial design's own decomposition, with one
multiplier behind it. 588 DSP blocks became 6.

## What it costs

Two things, and they are worth knowing before reaching for this.

**Registers.** Every stage holds its beat. In the real engine that is the
difference between fitting and not: the first draft held each beat twice —
once on the way in, once to offer — and did not place on the part. Offer from
what you hold.

**Cycles.** A beat here takes about five cycles a stage; the spatial map takes
none. The fold trades multipliers for time, and the trade is only good when
there is time to spend — an audio sample at 48 kHz on a 24 MHz fabric has 512
cycles, and the whole eight-band engine uses 204 of them.

## Try this

- Poke `out_ready = 0` and step. The second stage finishes, offers, and holds;
  the first stage finishes its next beat and holds too; `in_ready` drops. Two
  beats parked, nothing lost, nothing multiplied for nothing. Release it.
- Poke `a = 15`, `b = 15`, `in_value = 255`. `spatial_value` and `out_value`
  still agree — at 57 375, and the widths were chosen so they can.
- In the source, change `stages` in `Finish` from 2 to 3. `out_value`
  arrives two steps later — one per stage — and nothing else changes: the
  depth was never written anywhere but the core.
- Add a third `times` line to the chain. It gets its own client, the arbiter
  grows to three, and the page's claim — same bits as the spatial map — still
  holds, which is what the living check asserts.

## See also

- [**Shared unit**](sharedUnit.md) — the arbiter and the tag line, and when sharing is
  the wrong answer.
- [**Your own stage**](ownStage.md) — the one-beat worker this stage is a variation of.
- [**Stream stages**](streamStages.md) — why a pipeline of one-beat stages still moves
  one beat a stage per cycle.
- [**Barrel lane**](barrelLane.md) — the other way to keep one unit busy: interleave
  threads with the schedule fixed at build time.
