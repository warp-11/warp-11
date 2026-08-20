# Bit shapes

Six small utilities in one design: join, fill, reverse, count, and the one-hot
round trip. None of them is deep. Together they are most of the bit-twiddling
you will ever do, and the one-hot pair is a hardware idea worth meeting early.

## What to look at

Poke `a` and `b` (4 bits each), `flag`, and `index` (2 bits). Switch the radix
to **bin** — this is the one design where binary is the only readable view.

- `joined` is `a` and `b` side by side, 8 bits.
- `mask` is `flag` smeared across 4 bits: all ones or all zeros.
- `flipped` is `a` backwards.
- `ones` is how many bits of `a` are set.
- `hot0..hot3` — exactly one is high, chosen by `index`.
- `recovered` is `index` again, recovered from those four bits.

## Reading the source

```fsharp
catAll [ a; b ] ==> joined
```

Concatenation: 4 bits and 4 bits become 8, with the first argument on top. This
is how you build a wide word from fields — and, run backwards, slicing is how
you take one apart. Together they are the substrate for every layout,
instruction encoding and packet format in the toolkit.

```fsharp
fill 4 flag ==> mask
```

One bit repeated to a width. It exists because of what you do next with it: `x
&&& fill 4 flag` is "keep `x` if the flag is set, else zero", which is how you
conditionally clear a field without a mux. In software you would write
`flag ? x : 0`; in hardware both are fine, and this one is sometimes cheaper.

```fsharp
reverse a ==> flipped
popCount a ==> ones
```

`reverse` turns bit order around — needed more often than you would think, since
protocols disagree about which end comes first. `popCount` counts set bits, and
its output is 3 bits wide because counting up to 4 needs 3 bits. Not 4, not 8 —
the width follows the range, and Warp 11 makes you get that right.

## One-hot

```fsharp
let hot = uintToOneHot 4 index
```

**One-hot** means: as many wires as there are things, and exactly one of them
high. Here `index` is a 2-bit number 0–3, and `hot` is four separate one-bit
signals with exactly one set.

Software would never do this — a number is obviously more compact. Hardware does
it constantly, and the reason is that a one-hot signal is *already decoded*.
Asking "is this the selected item" is one wire, available now, with no
comparator. When four subsystems each need to know whether they were picked,
one-hot hands each of them a wire instead of making each build its own `index ==
k` check.

The trade is width against logic: 2 bits become 4 wires, and in exchange every
consumer gets its answer for free. At 4 items it barely matters. At 104 lanes,
choosing the wrong one shows up in the timing report.

```fsharp
oneHotToUInt hot ==> recovered
```

And back again — encoding the one-hot signal to a number. The round trip is the
identity for any index in range, which is a claim the living checks actually
walk rather than spot-check.

## The loop

```fsharp
for i in 0..3 do
    let o = outputBit $"hot{i}"
    hot[i] ==> o
```

An ordinary F# `for` loop, creating four output ports. This does **not** loop at
run time — there is no run time. The loop runs once during elaboration and
leaves four ports behind, exactly as if you had typed them out.

That is the general shape of generation in Warp 11: F# is the metaprogram, and
anything you can compute in F# — a list, a fold, a recursive function — can
build structure. [**Adder tree**](adderTree.md) is the same idea taken further.

## Try this

- Poke `a = 0b1000`, read `flipped` in binary.
- Set every bit of `a` and watch `ones` reach 4 — the widest value 3 bits holds
  is 7, so there is room.
- Step `index` through 0, 1, 2, 3 and watch the high bit walk across
  `hot0..hot3`.
- Poke `flag` and watch all four bits of `mask` move together.

## See also

- [**Priority mux**](priorityMux.md) — the priority chain that one-hot exists to avoid.
- [**Signed operations**](signedOps.md) — the other way to read a bit vector, and why the
  reading lives on the operation.
- [**Sequencer**](sequencer.md) — `stage.Is` is a one-hot signal by another name.
