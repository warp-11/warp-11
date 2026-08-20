# Buffering

A FIFO between the producer and the consumer. Nothing transforms the payload —
the whole of what this buys is that the two ends stop having to move in
lockstep.

```fsharp
Stream.input "in" beatLayout
|> streamFifo "fifo" 8
|> Stream.out "out"
```

## What to look at

The interesting behaviour only shows with the sink stalled, because that is when
a buffer is doing anything at all.

- Poke `out_ready = 0`, `in_valid = 1`, and step twelve times, changing
  `in_value` each step (1, 2, 3 …).
- Watch `in_ready`. It stays high while there is room and then drops.

Measured: **eight beats are accepted, and the ninth is refused.** That is the
depth asked for, and the producer is stopped rather than the beat being lost.

Now poke `out_ready = 1` and step:

```
1  2  3  4  5  6  7  8
```

Same beats, same order, later. A FIFO is the one piece of stream vocabulary that
changes *when* and nothing else.

## Where it goes without one

Compare [**Stream stages**](streamStages.md). A `stage` is one beat of slack — enough to break a
combinational path, not enough to absorb a burst. If a producer emits eight
beats back to back and the consumer pauses, a chain of stages stalls the
producer on the second beat; this holds all eight and lets it carry on.

That is the difference between "the pipeline runs at the speed of its slowest
stage" and "the pipeline runs at the speed of its slowest stage *on average*".

## How deep it can go, and what changes when it does

`streamFifo "fifo" 8192` is as legal as `8`, and the interesting part is that
nothing else about your design changes when you write it.

Up to 64 the words live in LUTs and the head is read **combinationally** — that
is what makes first-word fall-through free, so `payload` and `valid` arrive
together as the `Stream` contract requires. Past 64 they live in a block, where
a combinational read is not physically possible (see [**RAM**](ram.md)), so the
head becomes a read that costs a cycle, hidden behind a two-slot output buffer.

Two slots rather than one, and that is the whole trick: with one, the read that
refills the slot could not be issued until the consumer took the beat sitting in
it, so beats would arrive every *other* cycle and the deep FIFO would run at half
the shallow one's rate. Correct, and half the speed, which is a bad thing for a
component to be quietly.

So both forms hold exactly the depth you asked for, both sustain a beat per
cycle, and both present the same `Stream`. The only difference a design can
observe is how long an *empty* FIFO takes to show its first beat — and a
`Stream` exists precisely so that nobody has to know.

**The rule this draws is worth carrying to your own designs.** Code may assume a
combinational read of something that is *always* built from LUTs — a small
register file, a lookup table. It must not assume one of anything whose storage
depends on how big it got, because that decision belongs to the synthesiser and
it will make it differently at a different size. The second kind carries its
latency in a handshake instead, which is what this FIFO does for you.

## Try this

- Hold `out_ready = 0` and keep poking beats after `in_ready` drops. Nothing
  moves anywhere, and nothing is lost — step `out_ready = 1` and they all come
  out.
- Poke `in_valid = 1` and `out_ready = 1` together from empty. The first beat
  takes a cycle to appear; after that it is one beat per cycle straight through.
- Watch `fifo_write` and `fifo_read` in the signal list. They are one bit wider
  than the address, which is how full and empty are told apart: same index and
  same wrap bit is empty, same index and different is full.

## See also

- [**Stream stages**](streamStages.md) — one beat of slack, and why that is a different tool.
- [**Carrying context**](streamContext.md) — what a FIFO is for when it is holding the *other* half
  of a beat rather than the beat itself.
- [**Farm**](streamFarm.md) — the other answer to a slow stage: more of them.
