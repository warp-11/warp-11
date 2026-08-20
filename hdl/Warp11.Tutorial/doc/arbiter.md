# Arbiter (one-hot)

Four requesters, one server. Two library entries do the work: one turns the
requests into a grant, the other uses that grant to select a payload — and
neither of them contains a comparator.

## What to look at

Watch `grant0..grant3`, `served` and `any`.

- Poke all four `req` high and give the values something distinguishable
  (`value0 = 0x10`, `value1 = 0x20`, and so on). Exactly **one** grant is high —
  `grant0` — and `served` is `0x10`.
- Drop `req0`. Now `grant1` is high and `served` is `0x20`.
- Drop `req0` and `req1`. `grant2`, `served = 0x30`.
- Drop all four. Every grant is low, `any` is low, and `served` is **0**.

## Reading the source

```fsharp
let grants = oneHotLowest requests
let served = mux1H grants values
let any = reduceTree (|||) requests
```

Three lines, three ideas.

### `oneHotLowest` — the priority scan

Takes a list of request bits and returns a list the same length with **at most
one bit set**: the lowest-indexed requester that asked. `[0; 1; 1; 0]` gives
`[0; 1; 0; 0]`.

It is a fold that carries "nobody lower has asked" along the list, so its depth
grows linearly. That is right for a handful of requesters and wrong for a
hundred; `priorityPick` is the balanced-tree version for when the fold becomes
the timing path, and it carries payload fields along at the same time.

### `mux1H` — select without comparing

Given a one-hot select and a list of values, produce the selected one:

```fsharp
reduceTree (|||) (List.map2 (fun sel v -> mux sel v (lit 0UL w)) selects values)
```

Zero out every loser, then OR them all together through a balanced tree. Depth
is **log₂ n**, against the n−1 of a chain of muxes each testing an index.

That is the payoff for keeping a grant in one-hot form rather than encoding it
to a number: the select is already decoded, so nothing has to compare. It is the
same trade [**Bit shapes**](bitShapes.md) introduces — more wires, less logic — and here you can
see what buys.

With no select high, `mux1H` gives zero. That is not a fallback, it is the
honest answer for a caller that gates on `any` anyway.

### `any`

`reduceTree (|||)` over the requests — is anybody asking. A one-hot grant cannot
tell you this by itself, because all-zero is a legal grant, so `any` is the
signal a consumer actually gates on.

## Fixed priority starves

This arbiter is **not fair**. Requester 0 wins every time it asks, so under
continuous load requester 3 may never be served at all.

That is correct for plenty of situations — a refill path that must outrank a
speculative one, an error handler over normal traffic — and disastrous for
shared resources. When fairness is required the library has `roundRobinPick`,
which takes a base index and finds the lowest set request at or after it;
feeding it `lastGranted + 1` gives rotation.

The rule of thumb: **fixed priority when the requesters mean different things,
rotation when they mean the same thing.** The GEP cluster's divider socket
rotates; a Mandelbrot lane's refill does not.

## Try this

- Poke `req1` and `req3` only. Watch `grant1` win and `served` follow.
- Hold `req0` high and toggle the rest — nothing else is ever served.
- Poke no requests and confirm `served` is 0 rather than stale.
- Count the grants in the signal list and satisfy yourself that two can never be
  high at once.

## See also

- [**Bit shapes**](bitShapes.md) — the one-hot round trip, and what the representation is for.
- [**Farm**](streamFarm.md) — the same lowest-ready-wins dispatch, applied to whole workers.
- [**Adder tree**](adderTree.md) — the `reduceTree` both `mux1H` and `any` are built from.
