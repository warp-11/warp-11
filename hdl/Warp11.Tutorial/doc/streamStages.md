# Stream stages

The same pipe as [**Stream pipe**](streamPipe.md), with three registered stages in it. Each one
buys a cycle of latency — and a place for a beat to wait, which is the part that
matters.

## What to look at

Poke `in_value = 5`, `in_valid = 1`, `out_ready = 1`, then press **Step** and
count:

- step 1 — `out_valid` still 0
- step 2 — still 0
- step 3 — `out_valid` goes 1, and `out_value` is **8**

Three stages, three cycles, and 5 + 1 + 1 + 1 = 8, because each stage applies
the transform on its way through.

Now the interesting half. Set `out_ready = 0` and keep stepping with
`in_valid = 1`. Watch `in_ready`:

- it stays high for the first few steps — the stages are filling up
- then it goes low, and the whole chain is stopped

Compare that with [**Stream pipe**](streamPipe.md), where `in_ready` dropped *instantly*. The
stages absorbed three beats before the stall reached the source. That is what
storage in a chain is for.

## Latency and throughput are different things

This is the idea to carry away, and software intuition gets it wrong.

The chain has **three cycles of latency** — any given beat takes three cycles to
cross it. But it has a **throughput of one beat per cycle**, because all three
stages work simultaneously on three different beats. Adding stages makes each
beat slower and the chain no slower overall.

That is why hardware pipelines get *deeper* to go faster. Each stage does less
work, so the clock can be faster, so more beats per second — at the cost of any
single beat taking more cycles. The Mandelbrot accelerator's lanes are deep for
exactly this reason, and its 166 MHz clock is what the depth bought.

## Reading the source

```fsharp
Stream.input "in" beatLayout |> Stream.stages 3 bump |> Stream.out "out"
```

`stages n f` is `stage f` applied `n` times, and `stage` is the word that costs
a cycle. The vocabulary is deliberate:

- `map` — free, combinational, no storage
- `stage` — one register, one cycle, one beat of buffering

`stages 0 f` is a direct connection, which is worth knowing because it means a
depth of zero is not a special case anyone has to write.

## What a stage actually is

Not just a register. A plain register would **drop a beat** the cycle a stall
arrives: it has already latched the next value while the previous one is still
waiting to leave.

A ready/valid stage is a *skid buffer* — it holds a beat under backpressure and
accepts a new one when it is empty or when the downstream takes this cycle. That
is why it can absorb a stall rather than lose to one, and it is why a stage
costs a little more than a register.

The `Flow` type exists for the case where you genuinely do not need this — see
[**Flow (valid only)**](flowSampler.md).

## Try this

- Set `out_ready = 0`, step until `in_ready` drops, and count how many beats the
  chain swallowed.
- Release `out_ready` and watch them come out back to back, one per cycle.
- Open the **waveform** tab, record `in_valid`, `in_ready`, `out_valid` and run
  with the sink stalling — the elasticity is much easier to see as a picture.
- In the source, change `stages 3` to `stages 1` and re-run the latency count.

## See also

- [**Stream pipe**](streamPipe.md) — the same chain with no storage, where a stall is instant.
- [**Farm**](streamFarm.md) — several chains of *different* depth, and what that does to order.
- [**Stall probes**](streamProbes.md) — measuring where the stalls actually are.
