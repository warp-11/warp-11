# Stall probes

The same chain as [**Stream stages**](streamStages.md) with telemetry on both ends. Two counters
per link, and between them they answer the only question that matters when a
pipeline is too slow: **which end is waiting?**

## The two ways to waste a cycle

On any ready/valid link, a cycle where no beat moves is one of exactly two
situations:

- **blocked** — `valid && !ready`. The producer had a beat; the consumer would
  not take it. *Downstream is the problem.*
- **starved** — `ready && !valid`. The consumer was free; the producer had
  nothing. *Upstream is the problem.*

That is the whole diagnostic. A stage whose intake is **blocked** is not the
bottleneck — it is stuck behind one. A stage whose intake is **starved** is
faster than whatever feeds it. Find the link that is blocked on its input and
starved on its output, and you have found the wall.

## What to look at

Two experiments, and the counters are ordinary registers so you can just watch
them.

- Poke `in_valid = 1`, `out_ready = 0` and press **Run** briefly.
  `intake_blocked` and `egress_blocked` climb; the starved counters stay at 0.
- Press **Reset**, then poke `in_valid = 0`, `out_ready = 1` and **Run**.
  Now `intake_starved` and `egress_starved` climb and blocked stays at 0.

Measured over 20 cycles with the sink stalled: 18 blocked, 0 starved — the two
missing cycles are the pipeline filling before the stall could bite.

## Reading the source

```fsharp
Stream.input "in" beatLayout
|> Stream.probe "intake"
|> Stream.stages 2 bump
|> Stream.probe "egress"
|> Stream.out "out"
```

`probe` is **chainable and invisible**. It drives nothing and consumes nothing —
the stream passes through untouched, same ready, same valid, same payload. All
it does is add two saturating 32-bit counters named `{name}_blocked` and
`{name}_starved`.

Because they are just registers, they cost a little area and nothing else. They
are also readable three ways: peeked in the debugger, wired into a status
register for a host driver to read over AXI, or collected in bulk:

```fsharp
streamReport sim.Peek design
```

which walks every probe in the design — including ones inside submodules,
prefixed by instance path — and returns `(name, blocked, starved)` for each.
**Learning where a design stalls costs a tick and a peek, not a Vivado run.**

## Why this beats guessing

On a real accelerator you cannot see inside. The design either hits its cycle
target or it does not, and the gap is somewhere among a hundred links. The
alternative to probes is rebuilding the bitstream with different guesses, at
twenty minutes a guess.

The full-scale Mandelbrot pod's ~8% shortfall against its theoretical limit was
attributed to row-boundary effects by exactly this method: partitioning the idle
cycles between "waiting for work" and "waiting to hand work on" until the
remainder had only one explanation left.

## Try this

- Stall the sink, run, then release and watch **which** counter moves next.
- Put a probe between the two stages as well and compare all three.
- Add a breakpoint `intake_blocked == 0x10` and let it run to the cycle the
  stall got serious.
- Reset and confirm the counters clear — they are registers with a reset like
  any other.

## See also

- [**Stream stages**](streamStages.md) — the chain being measured, and what its storage absorbs.
- [**Farm**](streamFarm.md) — where the answer to "which end is waiting" decides how many lanes
  you need.
- [**Pipeline as data**](streamPipeline.md) — where probing becomes a property of a stage rather
  than a call in the chain.
