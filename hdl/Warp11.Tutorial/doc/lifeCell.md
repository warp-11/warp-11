# Neighborhood

One Game of Life cell, plus the three things an off-grid neighbor can be. The
shape here — read the cells around this one, do something with them — is the
same one behind image blur, edge detection and every cellular automaton, and it
is called a *stencil*.

## What to look at

The nine inputs `g00`–`g22` are a 3×3 grid. Set them all to 1.

- `live` reads **8** — the center's eight neighbors, itself excluded.
- `next` reads **0**: eight neighbors is overcrowding, and the cell dies.
- `orthogonal` reads 4 — the same center under a four-neighbor stencil.
- `corner_zero`, `corner_wrap` and `corner_clamp` read **3, 8 and 8**. Same
  cell, same grid, three answers.

Now clear everything and set only `g22`. The corner at (0,0) is nowhere near
it — but `corner_wrap` reads 1, because under wrap the far corner *is* its
diagonal neighbor.

## The gather happens at build time

```fsharp
neighborhood Stencil.Moore Edge.Zero grid 1 1
```

This returns **a list of eight expressions**. It is ordinary F# list code that
runs while the design is being elaborated: it indexes the grid, and for
out-of-range indices it either fabricates a zero literal, wraps the index, or
clamps it.

Nothing loops in hardware. By the time there is a circuit, the eight neighbors
have been resolved to eight specific wires, and what remains is whatever you
did with them. That is the reason the Game of Life accelerator can update
4,096 cells in **one cycle**: there is no iteration to unroll, because the
iteration was over a list at build time.

This is also why `neighborhood` counts nothing. It hands back the eight terms
and stops. Life counts them, a blur averages them, a Sobel filter weights them
by sign. One gather, several users, and the library is not in the business of
guessing which.

## The rule, in one line

```fsharp
(eq live (lit 3UL 4) ||| (grid[1][1] &&& eq live (lit 2UL 4))) ==> next
```

Born on exactly three, survives on two if already alive. Both branches are
computed and both are always present — this is combinational logic, so there is
no "else" being skipped. [**Comparator**](comparator.md) is the page for that if it still feels
strange.

## The three borders, and the surprise

- **`Edge.Zero`** — off-grid reads as a dead cell. The usual choice, and the
  one Life uses when the grid has real edges.
- **`Edge.Wrap`** — indices wrap around, so the grid is a torus. Nothing has an
  edge, which is what makes patterns able to travel forever.
- **`Edge.Clamp`** — indices clamp to the nearest in-grid cell. Common in image
  processing, where you want a border pixel to behave like its neighbors rather
  than like darkness.

Clamp has a trap worth seeing once. Clear the grid and set **only `g00`**, the
corner itself. Then `corner_clamp` reads **3**.

The corner's three off-grid neighbors — up-left, up, and left — all clamp back
onto (0,0), which is the cell itself. Under clamp, a corner cell is its own
neighbor three times over. That is correct behaviour for a blur, where you want
the border to be dominated by itself, and completely wrong for Life, where a
lone cell would resurrect itself. **The edge policy is part of the algorithm,
not a detail of the array indexing.**

## Counting

```fsharp
countWhere 4 id (neighborhood …)
```

`countWhere` zero-extends each one-bit verdict and sums them through an adder
tree — depth 3 for eight neighbors rather than the seven of a chain. On the
full Game of Life grid the population count is a 4,096-leaf version of the same
call. See [**Adder tree**](adderTree.md).

## Try this

- Set the top row (`g00`, `g01`, `g02`) and clear everything else. `live` reads
  3 and `next` reads 1 — a birth. This is the blinker, one third of it.
- Set `g11` and two of its neighbors: `next` stays 1, the survival rule.
- Set only `g11`: `live` reads 0 and the cell dies of loneliness.
- In the source, change the center count's edge policy to `Edge.Wrap` and see
  that nothing changes — the center of a 3×3 grid has no off-grid neighbors, so
  every policy agrees there. The policies only ever differ at a border.

## See also

- [**Adder tree**](adderTree.md) — `countWhere`'s summation, and why depth matters.
- [**Comparator**](comparator.md) — combinational logic, where both branches always exist.
- [**Bit shapes**](bitShapes.md) — the other place a list of expressions becomes a bus.
- [**Barrel lane**](barrelLane.md) — the alternative to doing every cell at once, when the grid
  is too big to fit.
