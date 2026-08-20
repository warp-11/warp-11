# Wrap counters

Two counters and a cascade. The interesting part is not counting — it is that
**the wrap is a signal**, which is what lets one counter drive the next.

## What to look at

Poke `enable = 1` and `last = 2`, then step and watch `column`, `column_wrap`
and `row`.

```
tick  1  column=1 wrap=0 row=0
tick  2  column=2 wrap=0 row=0
tick  3  column=3 wrap=0 row=0
tick  4  column=4 wrap=1 row=0     <- wrap goes high ON the last count
tick  5  column=0 wrap=0 row=1     <- row advanced
```

`column` counts 0–4 and starts over. `column_wrap` is high on the cycle
`column` is at its last value — **not** after it has wrapped — which is exactly
what the next counter up needs as its enable.

Meanwhile `bounded_count` counts 0, 1, 2 and wraps, because you told it `last = 2`
at run time. Change `last` while it runs and watch it obey immediately.

## Reading the source

```fsharp
let columns = Warp11.Stdlib.counter "columns" 5 enable
let rows = Warp11.Stdlib.counter "rows" 3 columns.wrap
```

`counter name n enable` counts 0 to n−1 and returns two things:

```fsharp
{| count = ...; wrap = ... |}
```

The width is computed from `n` — five states needs three bits, and nobody had to
work that out. Getting that wrong by hand is a classic: a counter one bit too
narrow wraps early, silently, and only at the boundary.

And `rows` is enabled by `columns.wrap`. That is the whole cascade: a raster
scan is a column counter whose wrap advances a row counter, and a frame counter
above that. Every accelerator in this repo that walks a 2-D space is built this
way.

Note the qualification, `Warp11.Stdlib.counter`. This tutorial project has its
own design called `counter` (the very first page), and it shadows the library
entry — a small, honest consequence of teaching designs and library entries
sharing a namespace.

## Why the wrap has to be a signal

Because the alternative is recomputing it. Without `wrap`, the row counter's
enable is `eq column (lit 4 3)` written out again at the call site — a second
place that knows the period, which someone will change one of.

Returning it makes the fact travel with the thing that knows it. Every counter
in the codebase used to compute its own `atLast` inline; six of them collapsed
into this call when it landed.

## `counter` and `counterTo` differ on purpose

```fsharp
let columns = Warp11.Stdlib.counter "columns" 5 enable      // period: 5 counts
let bounded = counterTo "bounded" last enable                // bound: 0..last
```

`counter` takes a **period** known at elaboration; `counterTo` takes the **final
value** as a live signal.

Those are opposite conventions and the difference is deliberate. A design with a
runtime limit almost always already holds the last index — a program's
instruction count, a row's final column — so making it pass `last + 1` would buy
an adder and an off-by-one for nothing. The two names differ so that the two
meanings cannot be confused at a call site.

`counterTo`'s width comes from its bound's width, which is the other reason the
signatures cannot be merged.

## Try this

- Set `enable = 0` mid-count. Everything holds — including `wrap`, if it was
  high.
- Watch `row` advance exactly once per five ticks, then confirm it wraps at 3.
- Change `last` from 2 to 0 and watch `bounded_count` sit at 0 with
  `bounded_wrap` permanently high.
- Set `last` to something smaller than the current count and see it run to the
  top and come back — the comparison is equality, not "greater than".

## See also

- [**Counter**](counter.md) — the same register written out by hand, and the holding rule.
- [**Edge detect**](edges.md) — a wrap is an edge somebody already computed.
- [**Sequencer**](sequencer.md) — the other way to structure "do this, then that".
