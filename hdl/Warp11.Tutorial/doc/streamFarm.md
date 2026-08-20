# Farm

Three workers of deliberately unequal depth, with beats dispatched to whichever
is free and merged back afterwards. This is how you buy throughput when one
worker is not fast enough — and it is the page where the handshake stops being
tidy.

## What to look at

The interesting behaviour needs the sink stalled first, so the workers fill up.

- Poke `in_valid = 1`, `out_ready = 0`, and step about eight times, changing
  `in_id` each step (1, 2, 3 …). Watch `in_ready` — it stays high while there is
  room anywhere, then drops.
- Now poke `out_ready = 1` and step, watching `out_id`.

Measured, issuing ids 1 to 6:

```
issued    1  2  3  4  5  6
returned  1  4  2  5  3  6
```

**Same beats. Different order.**

And `out_value` tells you why: worker *i* applies the transform *i+1* times, so
a beat that came back with +1 went through worker 0, +2 through worker 1, +3
through worker 2. Issue 4 came back before issue 2 because it took a different
worker.

## Dispatch is *lowest ready*, not round-robin

This surprised me writing the page, and it is worth being precise about.

`streamBalance` picks the **lowest-indexed worker that is ready**. It is not a
rotation. Under light load that means **worker 0 does all the work** and the
others never see a beat — which you can watch: with `out_ready` held high and
one beat per cycle, worker 0 keeps up alone and nothing else ever fires.

That is deliberate, and the reason is in the library's own notes. A rotating
dispatch commits a beat to a worker *before* knowing which will be free first,
which destroys late-binding load balance; the full-scale Mandelbrot pod measured
a 33% cycle penalty from exactly that. Keeping every beat at the source until
some worker can actually take it is what makes the farm adaptive.

The practical consequence: **a farm only uses its width when it is under
pressure.** If you are benchmarking one and the extra lanes look dead, the
design is not broken — you are not pushing hard enough.

## Beats must carry identity

```fsharp
let tagged = layout2 ("id", 8) ("value", 8)

Stream.input "in" tagged
|> Stream.farm 3 (fun i lane -> lane |> Stream.stages (i + 1) (fun (id, v) -> id, bump v))
|> Stream.out "out"
```

The `id` field rides through untouched and does nothing — except make the result
interpretable. Without it, the outputs are just values in an order nobody can
map back to the inputs, and any consumer that assumed "the third beat out is the
third beat in" is now silently wrong.

This is the **pixel-beat rule**: anything that can be reordered carries its own
coordinates. The Mandelbrot accelerator's beats carry their x and y for exactly
this reason — the row coalescer does not count arrivals, it reads positions.

## The worker must register something

`Stream.farm` rejects a fully combinational worker. A worker with no storage
couples the dispatch grant directly to the merge arbitration, which is a
combinational loop, and the elaborator catches it.

Read that as a design rule rather than a restriction: **a farm is an
asynchronous boundary.** Work goes in, results come back later, and "later" is
what the register makes true.

## Try this

- Hold `out_ready` high and step steadily. Watch `out_value` — every beat comes
  back +1, meaning worker 0 handled all of them alone.
- Stall the sink, fill the farm, release, and watch the order scramble.
- Change the workers to equal depth (`Stream.stages 2` for all) and see the
  reordering mostly disappear — mostly, because arbitration still decides ties.
- Watch `dispatch_ready_1`, `dispatch_ready_2`, `dispatch_ready_3`: the three
  worker readies the dispatch chooses between.

## See also

- [**Fork and join**](streamFork.md) — the other fan-out, where every worker gets every beat.
- [**Stream stages**](streamStages.md) — the storage each worker is required to have.
- [**Pipeline as data**](streamPipeline.md) — where the lane count stops being written at the call
  site and becomes a property of the stage.
