# Signed operations

Subtract, multiply, both compares and a shift, over 8-bit values that may be
negative. The page is really about one design decision that catches every
software developer: **the bits do not know whether they are negative. Something
has to say — and here that something is the signal.**

## The idea to get first

A signal is a bag of bits. `0xFF` in eight bits is `255` if you read it as
unsigned and `-1` if you read it as two's complement — and **the bits are
identical**. Nothing about the wire says which you meant.

So a signal says how to read it, once, where it is declared:

```fsharp
let count = input "count" 8            // unsigned — UInt 8
let sample = input "sample" (SInt 16)  // signed
```

and the operations follow. `mul` on signed operands *is* a signed multiply;
`lt` on them is a signed compare. Where you want the **other** reading of the
same bits, you say so with `asSInt` or `asUInt` — which move no bits and cost
no hardware, because reinterpreting is not an operation, it is a decision.

The reason it works this way is that the hardware does. An adder does not know
or care whether you think its operands are signed — the same gates produce the
same bits either way. **Only multiplication, ordering compares, right-shift,
division, saturation and widening actually differ**, and those six are exactly
where the reading has to be known.

## The gates agree, the labels still have to

That last paragraph has a corollary that surprises people, so it is worth
meeting here rather than in an error message. **Mixing a signed operand with an
unsigned one is refused even where the gates do not care** — `add`, `sub` and
`mux` reject it exactly as `mul` and `lt` do:

```
one operand is signed and the other is not (SInt 8 vs UInt 8) — asSInt or asUInt one of them
```

The bits an adder produces really are the same either way. What is *not* the
same is what the result gets called: `add` and `sub` take the reading from their
left operand, `mux` from its true branch. So a mismatched pair makes the answer's
label depend on which side you happened to write first, and that label is what
the *next* operation reads — the compare, the shift, the saturate that does care.
The mismatch costs nothing where it happens and everything one step later.

A literal is exempt, because a literal has no opinion: it borrows its
neighbour's reading the way it already borrows its width. `0 - x` on a signed
`x` is signed, and needs no ceremony.

## What to look at

Watch every output, then poke `a = 0xFF` and `b = 0x01`:

- `below` (unsigned) says `0` — because 255 is not less than 1.
- `below_signed` says `1` — because −1 *is* less than 1.

Same two inputs. Same bits on the wires. Two different answers, because you
asked two different questions.

Then try `a = 0x80`, the most negative 8-bit value (−128), and watch `shifted`.

## Reading the source

```fsharp
a - b ==> diff
```

Subtraction needs no `S`. Two's complement is designed so that one subtractor
is correct for both readings — `0x01 - 0x02` is `0xFF`, which is `255` unsigned
and `−1` signed, and both are right. Addition is the same. This is not Warp 11
being clever; it is why two's complement won.

```fsharp
mul (asSInt a) (asSInt b) ==> product
```

Multiplication *does* need to know. Reading both operands as signed makes the
multiply sign-extend them, so `0xFF × 0xFF` is `1` (−1 × −1) rather than `65025`.
The result is 16 bits wide, because a product of two 8-bit numbers is 16 bits and
Warp 11 never lets a multiply overflow.

`product` is declared `SInt 16` rather than plain `16`, and that is not
decoration. `==>` checks widths and says nothing about the reading — a
connection is solder, and no gates differ between the two readings — so the
signedness of the multiply does **not** travel down the wire. The port has to
say it. Get that wrong and nothing fails: the Verilog is byte-identical, the
differential passes, and the only symptom is the debugger showing `65436` where
you meant `−100`, because a decimal rendering has nothing to consult but the
declaration.

`asSInt` appears here because this design deliberately reads *the same eight
bits* both ways — that is the whole lesson. A design whose values are genuinely
signed says so once, at the declaration, and then just multiplies:

```fsharp
let a = input "a" (SInt 8)
let b = input "b" (SInt 8)
mul a b ==> product
```

```fsharp
lt a b ==> below
lt (asSInt a) (asSInt b) ==> belowSigned
```

The pair above — one `lt`, two readings. It is not a different *type* of
comparison on different data; it is a different circuit reading the same wires,
chosen by what the operands say they are.

```fsharp
sra 3 a ==> shifted
```

`sra` is **arithmetic** shift right: it shifts in copies of the sign bit rather
than zeros, so `0x80 >> 3` is `0xF0` (−128 → −16) and not `0x10`. That keeps
halving-by-a-shift working for negative numbers, which a plain shift does not.

## `shr` narrows, `sra` keeps the width

`sra` is not its own operation. The right shift is `shr`, and `shr` **narrows** —
`shr 3` on eight bits hands you the top five, because the low three are exactly
what a shift throws away. Nor does `shr` decide how to read what is left: the
sign bit is among the bits that survive, so it simply keeps the operand's own
reading.

`sra` is those two facts composed — `pad (shr (asSInt x) 3) 8` — read as signed,
shifted, then widened back to where it started, and widening a signed value
replicates its sign bit. That is where the `0xF0` comes from. It is a name for a
composition rather than a node in the IR, which is also why it forces the
reading: `sra 3 a` fills with the sign bit even though this design declared `a`
unsigned, because writing `sra` is what says to read it that way.

## The amount may be a signal

A shift amount can be a constant **or** a signal, at the same call shape:

```fsharp
shr 3 x      // a part-select — no gates at all
shr n x      // a barrel shifter
```

Which circuit you get follows from what you wrote, so there is no second name to
remember and no way to write one meaning the other. They differ in width as well
as in cost: the constant form narrows to `w − 3`, and the signal form cannot,
because the elaborator does not know the amount — so it keeps the operand's
width and leaves any narrowing to you.

The part that belongs on this page: **a dynamic right shift is arithmetic
exactly when its operand is signed.** `shr n a` on an unsigned `a` fills with
zeros; declare `a` as `SInt 8` and the identical expression fills with the sign
bit. Same rule as everywhere else — the value says how to read it — and it is
one of only two places the emitter has to write `$signed`, because a sign fill
over a distance it cannot know has no width-explicit form. The constant shift
replicates a named bit instead, which is why `sra` needs a declared signal.

## Division, the one that disagrees with the shift

Division is another of the six whose answer depends on the reading, and the one
where the signed form catches people out. The divisor must be a **constant**:
`x / 8` is a shift and `x / 10` a multiply by a reciprocal, both of which
synthesis does for free, where `a / b` on two signals is thirty levels of logic
that looks identical at the call site and first appears as a timing failure.

```fsharp
divideBy 2 (asSInt a) ==> half      // remainderBy is the same rule
```

Dividing by a negative constant needs a signed value and is refused on an
unsigned one, rather than guessed at. But the sharper point is that **halving is
not shifting** once the value can be negative:

```
a = 0xF9 (−7)      sra 1 a                = 0xFC  (−4)
                   divideBy 2 (asSInt a)  = 0xFD  (−3)
```

`sra` floors, rounding toward −∞; division truncates toward zero, which is what
`/` means in every language you already know. On unsigned values they agree; on
negative ones they differ by one whenever the division has a remainder. Reaching
for the shift because it looked cheaper changes the answer.

## When you want the type back

Bare bit vectors are the right substrate but a miserable place to live if you
are doing fixed-point arithmetic. That is what `Number` is for: it layers a
Q-format — how many bits are fractional — on top, checks that you do not add a
Q8.8 to a Q4.12, and compiles down to exactly these operations. Meaning lives in
the layer above; the IR stays width-only bit vectors.

## Try this

- Poke `a = 0x7F` (127) and `b = 0x80` (−128). `below` and `below_signed`
  disagree as strongly as they can.
- Watch `product` with `a = 0xFF, b = 0xFF`. Then imagine dropping the two
  `asSInt`s.
- Poke `a = 0x80` and read `shifted` in **bin** — the sign bit copied down.
- Poke `a = 0xF9` (−7). `shifted` is `0xFF`, which is −1: `sra 3` floored −0.875
  down to −1, where `−7 / 8` truncated toward zero is `0`.

## See also

- [**Comparator**](comparator.md) — the unsigned compares alone, and the `mux` underneath.
- [**Fixed-point**](fixedPoint.md) — the layer above: the same bits with a Q format attached, so
  the meaning is checked rather than remembered.
- [**Bit shapes**](bitShapes.md) — more ways to take bit vectors apart, all of them
  interpretation-free.
- [**Counter**](counter.md) — where `+` wraps, and why that is deliberate.
