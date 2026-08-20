# Barrel lane

Four independent work items taking turns through one pipeline, one per cycle.
This is the shape every large design in this repository is built from — the
Mandelbrot renderer is 104 of these, the GEP engine is 64 — and it is the
cheapest trick in hardware.

## What to look at

Poke `x = 1` and step.

- `turn_now` cycles 0, 1, 2, 3, 0, 1, … forever. That is the schedule.
- `thread0` sits still for three cycles, moves on the fourth, sits still again.
  So do the other three, each on its own cycle.
- After 42 steps the four read **10, 20, 30, 40**. Each thread took ten turns;
  thread *t* adds *t+1* per turn, so the four totals are the same count seen
  four ways.
- `latency` and `threads` are 2 and 4. Neither is state — they are facts about
  the lane, wired out as constants.

## The problem this solves

The design computes `total = total + x * weight`. Written the obvious way that
is a multiply and an add in one cycle, and **that cone decides your clock**: the
chip can only run as fast as its slowest path.

The standard fix is to cut the path with registers — multiply this cycle, add
the next. The clock doubles. And the throughput collapses, because now
`total` for the next sample is not available until two cycles later, and the
next sample needs it.

That is the deal a pipeline offers and the reason it so often disappoints: it
improves *latency-per-stage* and does nothing for a workload that depends on
its own previous answer.

## The trick

Give the pipeline four workloads that do not depend on each other.

```fsharp
let lane = barrel 2 4          // 2 cycles deep, 4 items interleaved
```

Cycle 0 issues thread 0, cycle 1 issues thread 1, cycle 2 issues thread 2 —
and by the time thread 0's turn comes round again on cycle 4, its result landed
back on cycle 2. **The pipeline is full every cycle and nothing ever waits.**

There is no hazard detection here, no forwarding network, no stall logic. Those
exist in a CPU because a CPU has to run one instruction stream fast. A barrel
gives that up in exchange for never needing any of it: a thread's next issue is
*structurally* after its own writeback, so the dependency cannot arise.

If you have met this in software, it is the same idea as hyperthreading —
interleave independent work to keep an idle pipeline busy — but done with the
schedule fixed at build time, so it costs no logic at all.

## The one rule

```fsharp
if threads <= latency then failwith "…"
```

`barrel` refuses to build a lane whose threads cannot cover its own pipeline.
At `threads = latency` a thread's next issue lands in the same cycle as its own
writeback, and it reads a stale total. That bug is a *race*, one cycle wide,
producing arithmetic that is almost right — the single worst kind to find by
staring at output. So it is not a bug you can have; it is an elaboration error.

## Carrying context

```fsharp
let held = lane.CarryTo 1 "issued" 16 current
memWrite acc (lane.Carry "slot" 2 turn) sum (lit 1UL 1)
```

Anything read at issue that is needed at writeback has to *travel* the pipeline,
because by the time the result comes out, `turn` has moved on twice. `Carry`
delays a value by the full latency; `CarryTo` by fewer stages, for values that
have to meet a result one stage early.

Getting this off by one is the classic barrel bug — the answer is right and
lands in the wrong thread's slot. [**Delay chain**](delayAlign.md) is the same mechanism on its
own, and worth reading first if this looks arbitrary.

## What it costs

State, multiplied by threads. Each thread needs its own copy of everything the
computation holds — here one 16-bit total, in Mandelbrot six fields per thread.
That is a register file, and on the KV260 those live in LUTRAM (distributed
memory), which is why this design's `acc` is a small memory with an async read
rather than four registers and a mux.

The arithmetic is *not* multiplied by threads. One multiplier, four threads:
the whole point is that the expensive part is shared and only the cheap part
duplicates.

## Try this

- Change `barrel 2 4` to `barrel 2 2` in the source and reload. Read the error
  — it names the reason, not just the rule.
- Change `lane.Carry "slot" 2 turn` to `lane.Carry "slot" 1 turn`. Everything
  still runs; the totals land in the wrong threads. This is what an off-by-one
  in the carry looks like, and why the delay is derived from the lane rather
  than typed in.
- Poke `x = 3` mid-flight and watch the four totals diverge at their own turns
  rather than together.

## See also

- [**Delay chain**](delayAlign.md) — the carry mechanism on its own.
- [**Stream stages**](streamStages.md) — the other way to keep a pipeline full, when the work
  arrives as a stream rather than as a fixed set of threads.
- [**RAM**](ram.md) — the async read this lane uses as its register file, and why the
  read port matters.
- [**Shared unit**](sharedUnit.md) — what to do when one arm of the pipeline is too expensive to
  give every lane its own.
