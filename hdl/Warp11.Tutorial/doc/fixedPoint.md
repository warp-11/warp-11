# Fixed-point

Multiply two fractional numbers, renormalize the result, and double one for
free. The page is about the layer that makes fixed-point arithmetic survivable:
**the format travels with the value**, so getting it wrong is an error you are
told about rather than a wrong picture you have to notice.

## The problem it solves

FPGAs do not have floating point — not for free, anyway. A float multiplier is
a large, slow lump of logic, and the accelerators in this repo have hundreds of
multipliers each. So real designs use **fixed point**: an integer with an
imaginary binary point at an agreed position.

"Q4.4" means eight bits, four of them fractional. The bits `0001_1000` are the
integer 24, and 24 / 2⁴ = **1.5**. The hardware only ever sees 24. The 1.5 is a
convention between you and yourself.

And that is exactly the problem. Nothing on the wire says where the point is.
Add a Q4.4 to a Q6.2 and the adder is perfectly happy to give you a number that
means nothing at all, silently, forever.

## What to look at

Poke `a = 0x18` (1.5) and `b = 0x20` (2.0).

- `product` reads `0x30` — 48, which is 3.0 in Q4.4. Correct.
- `doubled` reads `0x18` — **the same bits as `a`**, and it means 3.0.
- `below` is 1, because 1.5 < 2.0.

The second one is the interesting one and the section below explains it.

## Reading the source

```fsharp
let a = Number.input "a" Number.q4_4
let b = Number.input "b" Number.q4_4
```

`q4_4` is a **format**: one line saying how to read a bag of bits as a number.

```fsharp
let q4_4 = Number.signedFixed 8 4     // { totalWidth = 8; fracBits = 4; signed = true }
```

Three numbers, and they are the whole description — a width, a count of fraction
bits, and whether the top bit is a sign. **An integer is the same description
with `fracBits = 0`**, which is why there is one layer here rather than a
fixed-point one and an integer one.

```fsharp
let wide = Number.wire "wide" (a * b)
```

Multiplying Q4.4 by Q4.4 gives Q8.8: widths add, fraction bits add. `*` works
that out and carries it, so `wide` is 16 bits with 8 fraction bits and nobody
wrote it down twice.

The payoff is what it makes impossible. Add that Q8.8 result to a Q4.4 and
elaboration stops with

    (+): operand formats disagree — 16w/8f/signed vs 8w/4f/signed

rather than handing you a number that means nothing. The whole class of "the
point ended up somewhere else" bugs becomes a message instead of a picture.

```fsharp
(Number.renormTo Number.q4_4 wide).bits ==> product
```

`renormTo` brings the wide result back to Q4.4 by dropping the extra four
fraction bits — which in hardware is a **slice**, not a divide. It is free.

It is also the point where you, not the arithmetic, say what the result means:
nothing relates Q8.8 to Q4.4 except your intent. That is why it takes the target
format explicitly, and why it is one of exactly two such escapes.

### The one that costs nothing

```fsharp
(Number.reinterpret q5_3 a).bits ==> doubled
```

`q5_3` is the same eight bits with three fraction bits instead of four. The bits
do not move — `reinterpret` emits no hardware at all — but claiming one fewer
fraction bit means each bit is worth twice as much, so the value doubles.

That is the trick worth carrying away: **in fixed point, multiplying or dividing
by a power of two is a relabelling, not an operation.** Where a software
programmer writes `x * 2`, a hardware designer moves the point and pays nothing.
The next `renormTo`'s slice absorbs the change in position.

## Where this is used for real

The Mandelbrot accelerator is entirely fixed-point — every one of its 104 lanes
multiplies Q-format values in the escape-time iteration, and the format choice
is what decides how far you can zoom before the picture falls apart. The audio
filters use it too. It is not a toy layer.

## Try this

- Poke `a = 0x08` (0.5) and `b = 0x08`. `product` reads `0x04` — 0.25. Correct,
  and note it is *not* 0.
- Poke `a = 0x80`. That is the most negative Q4.4 value, −8.0. Watch `below`.
- Look at `wide` in the signal list: 16 bits, the full product, before anything
  was thrown away.
- In the source, try `Number.renormTo Number.q9_7 wide` instead. Read the
  elaboration error — it tells you which bit it would have needed.
- Change `q4_4` to `Number.unsignedFixed 8 4` and poke `a = 0x80`. The same bits
  now mean 8.0 rather than −8.0, `below` flips, and the multiply changes from
  sign-extended to zero-extended in the emitted Verilog.

## See also

- [**Signed operations**](signedOps.md) — what is underneath all of this: the bits are just
  bits, and the meaning lives in a layer above.
- [**Dot product**](dotProduct.md) — the same multiply, untyped, and where the extra width goes.
- [**Comparator**](comparator.md) — `Number.lessThan` is just `lt`: the format types the bits it
  hands the IR, so the compare reads them correctly without being told.
