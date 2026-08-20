# FIR filter

Two four-tap filters over one sample stream. A FIR is a weighted average of the
last N samples — that is the whole definition, and the weights are the design.

## What to look at

Poke `sample = 1`, then step once and set `sample = 0`. Step four more times
and watch `smoothed`:

```
1   2   2   1   0
```

Those are the coefficients, in order. Feed a filter a single 1 and it hands
back its own weights one per cycle — the **impulse response**, and the one
measurement that pins down taps, ordering and delay-line depth at once. It is
also how the tutorial's check judges this design.

`averaged` is the same hardware with `[1;1;1;1]` instead, so its impulse
response is `1 1 1 1`: an unweighted moving average over four samples.

## What the hardware is

```fsharp
fir 8 8 [ 1UL; 2UL; 2UL; 1UL ] sample ==> smoothed
```

Behind that: three registers holding the last three samples, four multipliers,
and an adder tree. Every cycle, all four multiplies happen at once and the tree
sums them. There is no loop — a four-tap filter *is* four multipliers, and a
64-tap filter is 64 of them.

That is the trade to internalize. In software, doubling the taps doubles the
time. Here it doubles the **area** and the time stays one cycle. On this board
you have 1,248 multipliers, and a filter is priced in how many of them it takes.

## The accumulator is sized, not saturated

The output is 18 bits for 8-bit data and 8-bit coefficients:

```
dataWidth + coeffWidth + ceil(log2 taps)  =  8 + 8 + 2  =  18
```

Every product is 16 bits, and summing four of them needs two more. So the
accumulator **cannot** overflow, and no caller has to reason about clipping.
This is the same reasoning as `a * b` producing a full-width product in the DSL
— width is derived from what can actually happen, rather than being asserted by
a caller who has to be right.

Handing back a narrower result and saturating would be a decision this function
has no business making; [**Fixed-point**](fixedPoint.md) is where a design says how it wants a
wide value brought back down.

## Why the sum is a tree

The obvious sum is `p0 + p1 + p2 + p3`, three adders in a row. `fir` uses
`reduceTree` instead: `(p0+p1) + (p2+p3)`, two levels.

Identical arithmetic — integer addition is associative — at half the depth, and
at 64 taps a tenth of it. [**Adder tree**](adderTree.md) is the page for this, including the
part where a chain that passes every simulator still fails on silicon because
nothing in a cycle-accurate model knows how long a wire takes.

## Coefficients at elaboration time

`coeffs` is an F# list, read while the design is being built. The constants end
up **baked into the bitstream**, which is why they cost multipliers-by-a-constant
rather than general multipliers — a multiply by 2 is a shift, and synthesis
knows it.

A filter whose coefficients change at run time is a different design: the
coefficients come from registers the host writes, every tap becomes a full
multiplier, and the area goes up accordingly. That is `biquad` in the audio
stdlib, which takes its five coefficients as signals for exactly this reason.

## Try this

- Set the coefficients to `[1UL; 0UL; 0UL; 0UL]` and reload. The filter becomes
  a wire — the impulse response is `1 0 0 0`, and `smoothed` tracks `raw`.
- Then try `[0UL; 0UL; 0UL; 1UL]`: a three-cycle delay line and nothing else. A
  pure delay is a filter too.
- Feed `sample = 255` continuously and watch `averaged` settle at 1020 — four
  taps of gain 1 is a gain of 4, which is why a real averaging filter divides
  (a right shift) at the end.
- Add a fifth coefficient and watch the output width go to 19 without you
  touching it.

## See also

- [**Adder tree**](adderTree.md) — the sum inside this, and the silicon gotcha it exists for.
- [**Delay chain**](delayAlign.md) — the sample line, as its own thing.
- [**Fixed-point**](fixedPoint.md) — how a design brings a wide accumulator back to a usable
  width on purpose.
- [**Dot product**](dotProduct.md) — the same "each call is real hardware" lesson at two
  multipliers instead of four.
