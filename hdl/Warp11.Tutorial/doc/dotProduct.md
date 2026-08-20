# Dot product

`(a × b) + (satInc(c) × d)`, built from three pieces pulled out of the standard
library. It is the smallest design that is *made of other designs*, which is the
subject here.

## What to look at

Poke `a`, `b`, `c`, `d` and watch `out`. Then look at the group dropdown on the
left: the signal list is no longer flat. There are instances now — `mul1`,
`mul2`, `acc`, `bump` — each with its own internal signals, and you can watch
what is happening *inside* a submodule from out here.

## Instantiation is function application

This is the line the whole design is about:

```fsharp
let multiply = mulOf 8
```

`mulOf 8` hands you back **a function**. You call it like any other function:

```fsharp
accumulate (multiply a b) (multiply (bump c) d) ==> out
```

and each call plants a multiplier into the design and wires your operands to
it. There is no `instantiate` step, no port map, no name to invent and then
refer to later. If you can write nested function calls, you can write structural
hardware.

That is a bigger deal than it looks. In most HDLs, instantiating a submodule is
a distinct grammatical form — declare an instance, name it, then connect each
port by name — and expressions built from submodules become a page of wiring
with the shape of the computation hidden inside it. Here the expression *is* the
wiring.

## The part that surprises software people

**Every call is a separate piece of silicon.** `multiply a b` and
`multiply (bump c) d` are two calls, so this design contains **two multipliers**
— two physical arrays of gates, both running at the same time, permanently.
Calling the function twice did not reuse anything. It cost twice as much chip.

The instinct to reach for a loop, or to call the same function repeatedly to
save space, is exactly backwards here. In software a function call is nearly
free and running the same code twice costs time. In hardware, instantiating
twice costs **area** and buys you **parallelism** — the two multiplies happen
simultaneously. Sharing one multiplier between two operations is possible, but
it is a design you build on purpose, with a register and a sequencer to take
turns (see [**Sequencer**](sequencer.md)), and you build it when you have run out of room, not
by default.

This trade — area against time — is most of what hardware design *is*, and it
is why the Mandelbrot accelerator in this repo has 104 copies of the same lane.

## Inline or instantiated — the call site cannot tell

The standard library defines both of these:

```fsharp
let mulLogic (_: int) = mul                            // inline gates
let mulOf = memoize (fun w -> liftBinary (fnModule2 ...))   // a real submodule
```

They have **the same type** and are called the same way. Swap `mulOf 8` for
`mulLogic 8` in the line above and the design still elaborates, still
simulates, still emits — but the Verilog now has the multiply spelled out
inline instead of as a `Mul8` instance.

That is a deliberate rule in Warp 11: whether a library entry is inline logic or
a submodule is decided where it is *defined*, and is invisible everywhere it is
*used*. It means a library author can change their mind later without touching a
single caller.

`bump` is `satIncOf 8` — a saturating increment, which stops at 255 instead of
wrapping to 0. Same shape: a function you apply.

## Widths, and where the extra bits come from

```fsharp
let multiply = mulOf 8
let accumulate = adderOf 16
```

The multiplier takes two 8-bit inputs and produces **16 bits**, because that is
how wide the product of two 8-bit numbers actually is. Multiplication in Warp 11
never overflows — it widens. So the adder that consumes two products has to be
16 bits, and `out` is 16 bits.

Addition does *not* widen (see [**Counter**](counter.md)), and the asymmetry is deliberate: a
counter that grew a bit per cycle would be useless, while a product that
silently lost its top half would be a bug.

## Try this

- Change `mulOf 8` to `mulLogic 8` and look at the group dropdown — `mul1` and
  `mul2` are gone; the multiply is now part of the top module.
- Add a third product and widen `out`. Watch the instance count grow.
- Watch a signal *inside* `mul1` while poking `a`.

## See also

- [**Counter**](counter.md) — where `+` does not widen, and why the asymmetry is deliberate.
- [**Bit shapes**](bitShapes.md) — an F# loop generating four ports, which is the same
  elaboration-time trick at a smaller scale.
- [**Comparator**](comparator.md) — what a single instance's worth of logic looks like written
  out by hand.
