# Priority mux

Three candidate values and two select bits. `sel1` wins if it is set, else
`sel0`, else a default. Fifteen lines that explain how every conditional in
Warp 11 actually works.

## What to look at

Watch `out`, `sel0` and `sel1`. Poke `a`, `b`, `c` to distinct values —
`0x11`, `0x22`, `0x33` — and then walk the four combinations of the selects:

- both low: `out` is `a`
- `sel0` only: `out` is `b`
- `sel1` only: `out` is `c`
- **both high: `out` is `c`** — `sel1` beat `sel0`

No stepping. This is combinational, like [**Comparator**](comparator.md).

## Reading the source

```fsharp
a ==> out
If sel0 (fun () -> b ==> out)
If sel1 (fun () -> c ==> out)
```

Read it as three statements: *default to `a`; if `sel0`, use `b` instead; if
`sel1`, use `c` instead.* The **later statement wins**, so priority is simply
the order you wrote them in. To make `sel0` outrank `sel1`, swap the two lines.

That is the whole trick, and it is worth pausing on, because it is doing
something a software reader will misread.

## These are not assignments

`out` is a wire. It has exactly one value at a time and no memory. So the three
lines cannot be "assign, then reassign, then reassign again" — there is no
*then*. What actually happens is that elaboration collapses them into nested
muxes, and what gets emitted is one permanent connection:

```verilog
assign out = sel1 ? c : (sel0 ? b : a);
```

The statements are a *description* being folded, not a sequence being executed.
Once you see that, the priority rule stops being a rule to memorise: the last
statement ends up outermost in the nest, and outermost wins.

This shape scales. A dozen `If` blocks over one wire is a dozen-deep mux chain
and reads top-to-bottom as a priority list — which is why arbiters, interrupt
controllers and instruction decoders all look like this.

## The rule this design lives inside

Warp 11 enforces **one driver per signal per level**. Writing `a ==> out` twice at
the same scope is an elaboration error, not a silent overwrite — because a
discarded first assignment is invisible through elaboration, through lint, *and*
through synthesis, and would cost you a day.

So why is this design legal? Because the second and third `==>` are inside `If`
bodies, which are **child scopes**. Overriding a default from inside a
conditional is the intended idiom; writing two unconditional drivers at the top
level is the mistake. The default plus the overrides is the pattern:

```fsharp
something ==> w          // the default, at this level
If c (fun () -> other ==> w)   // the override, one level down
```

## Wires and registers differ here

`out` is a wire, and it has an unconditional default, so every path through the
mux tree gives it a value. Remove the `a ==> out` line and elaboration fails:
with both selects low there would be no value to produce, and a wire cannot
invent one.

A **register** in that position is fine, because it has somewhere to fall back
to — its own current value. That is the rule from [**Counter**](counter.md), seen from the
other side: a reg with no default holds; a wire with no default is an error.

## `Else`

There is a companion to `If`:

```fsharp
If clear (fun () -> ...)
Else (fun () -> ...)
```

`Else` attaches to the `If` immediately before it, giving you if/else
rather than a stack of independent overrides. Use `If`/`Else` when the
cases are exclusive and a bare stack of `If`s when you mean a priority list.
[**Counter**](counter.md) uses the first form; this design uses the second.

## Try this

- Swap the two `If` lines and re-check the both-high case.
- Delete `a ==> out` and read the elaboration error.
- Add `If (sel0 &&& sel1) (fun () -> lit 0xFFUL 8 ==> out)` at the end and see it
  take precedence over both.
- Open the **source** tab, then imagine the Verilog: one `assign`, two nested
  ternaries.

## See also

- [**Counter**](counter.md) — `If`/`Else` over a register, and the holding rule.
- [**Comparator**](comparator.md) — the `mux` these fold into, written by hand.
- [**Bit shapes**](bitShapes.md) — one-hot, which is what you reach for when the selects are
  already known to be mutually exclusive and a priority chain is waste.
