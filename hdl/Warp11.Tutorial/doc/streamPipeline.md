# Pipeline as data

The same kind of chain as everywhere else in this tier, except the pipeline is
a **list of values** rather than a sequence of calls. Three stage descriptions,
one of them three lanes wide, two of them probed — and none of that visible to
the stages on either side.

## What to look at

Functionally this is unremarkable: beats go in, beats come out transformed. The
signal list is where the interest is — expand the groups and count the stage
instances. Three of them for the middle stage, because that stage said it wanted
three lanes.

Then read the source, which is the point.

## Reading the source

```fsharp
let bumpStage = Stream.specFromFunction (Stream.stage bump)
let doubleStage = Stream.specFromFunction (Stream.stage (fun v -> v + v))

Stream.input "in" beatLayout
|> Stream.pipeline
    [ bumpStage |> Stream.probed "intake"
      doubleStage |> Stream.lanes 3 |> Stream.probed "farm"
      bumpStage ]
|> Stream.out "out"
```

A `StageSpec` is a record: how to create the stage, how many lanes it wants,
whether to probe its intake. `lanes` and `probed` are ordinary functions
returning a modified record — so the *description* is data you can build,
filter, generate or read from a config, and `Stream.pipeline` folds the list
into a chain.

## Why this shape

Because the alternative puts topology at every call site.

Written as calls, making the middle stage three lanes wide means changing the
middle stage's *invocation* — wrapping it in a farm, which the stages either
side can see in the code even though nothing about them changed. Add a probe and
the chain grows another link. Every operational decision leaves a mark on the
description of what the pipeline *does*.

Here, `lanes 3` is a property of one record. Its neighbours are untouched, and
so is the pipeline expression's shape. Whether a stage is one lane or a hundred
is invisible to everything except the stage itself.

That is the same rule as [**Dot product**](dotProduct.md)'s inline-versus-instantiated: an
implementation choice belongs where the thing is *defined*, and must not be
visible where it is *used*. Here it applies to multiplicity and telemetry
instead of to module boundaries.

## Modules and functions are both stages

```fsharp
Stream.spec "pod" someModule          // a module instance
Stream.specFromFunction (Stream.stage bump)   // a plain function
```

Both produce a `StageSpec`, and `lanes` and `probed` apply to either. Without
the second one, whether a stage happened to be a module would leak into every
pipeline that composed it — a function-shaped stage simply could not join.

The difference that remains is naming: a module instance owns a name and gets
one, and a function owns nothing, so anything it builds internally is named by
the library.

## Where this goes

This is the surface a real accelerator's top level wants to be. A frame
pipeline is *a list*: read, transform, transform, coalesce, write — with lane
counts chosen per stage against an area budget, and probes on the ones you are
still arguing about. Changing "eight lanes" to "sixteen" is editing a number in
a list, not restructuring a chain.

The full-scale Mandelbrot pod is built this way, and the type-changing forms
(`pipeline2`, `pipeline3`) exist for when a stage's output payload differs from
its input — one function per arity, the same pattern as the layouts.

## Try this

- Change `lanes 3` to `lanes 1` and compare the instance count in the signal
  list. Nothing else in the source changes.
- Add `|> Stream.probed "tail"` to the last stage and watch two more counters
  appear.
- Reorder the list. It is a list.
- Build the list with a `for` comprehension instead of writing it out —
  elaboration-time F#, as in [**Bit shapes**](bitShapes.md).

## See also

- [**Farm**](streamFarm.md) — what `lanes n` turns into, and why it reorders.
- [**Stall probes**](streamProbes.md) — what `probed` turns into, and how to read it.
- [**Dot product**](dotProduct.md) — the same principle applied to whether something is a module.
