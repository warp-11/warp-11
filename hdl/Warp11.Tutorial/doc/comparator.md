# Comparator

Two 8-bit inputs, three one-bit verdicts, and the larger of the two. No
register, no reset, nothing that remembers anything — which makes this the
design to meet **combinational** logic on.

## The idea to get first

Software runs in steps. This does not. Every line below describes a piece of
circuitry that exists all at once and is *always* computing: change `a`, and
`less`, `equal`, `greater` and `larger` all change, with no instruction having
been executed and no clock having ticked. The `==>` lines are not statements
that run in order — they are four permanent connections, and writing them in a
different order would build exactly the same chip.

Time only enters when you add a register (that is the [**Counter**](counter.md) page). Until
then, nothing here has a "before" or an "after".

## What to look at

Watch all six signals, poke `a` and `b`, and notice you never press **Step**.
The outputs are already right. Stepping changes nothing, because there is
nothing to remember.

Try `a = 0x10`, `b = 0x20`, then swap them, then make them equal.

## Reading the source

```fsharp
lt a b ==> less
eq a b ==> equal
lt b a ==> greater
```

`lt` is less-than, `eq` is equal. There is no `gt` — **`greater` is `lt` with
the operands swapped**, because `a > b` and `b < a` are the same question, and
building one comparator you can use twice is cheaper than building two.
"Cheaper" here means literal gates on literal silicon, which is the currency
this whole language spends.

Each comparison produces a `Bool` — a signal exactly one bit wide. That is a
real constraint rather than a convention: `If` requires a one-bit condition, so
"is this true" and "is this 8 bits of something" are different types and Warp 11
will not let you confuse them.

### Why the ports are called `less` and `greater`

```fsharp
let less = outputBit "less"
```

Not `lt` and `gt` — because `lt` and `gt` are the operators' own names, and a
port called `lt` would shadow the function you need to write the next line.
Warp 11 uses short operator names (`lt`, `eq`, `gt`, `le`, `ge`) and the price is
that those five words are spoken for.

## The mux

```fsharp
mux (lt a b) b a ==> larger
```

`mux` — short for multiplexer, and one of the two or three words worth learning
outright — is hardware's ternary operator: *pick `b` if the condition is true,
otherwise `a`*.

The difference from a software ternary matters. In software, `cond ? b : a`
evaluates one branch. In hardware, **both `a` and `b` are physically present at
the mux's inputs the whole time**, and the condition steers which one reaches
the output. Nothing is skipped, because there is nothing to skip — the wires are
already there. A mux costs area proportional to its width, and that cost is the
same whichever way the condition goes.

This is why `if` in hardware is not a branch. Every `If` you write elsewhere in
Warp 11 folds down into muxes exactly like this one.

## Widths are checked

`larger` is 8 bits, and so are `a` and `b`. Had you declared it 4 bits wide, the
design would fail to elaborate rather than quietly dropping the top half — the
single most common way to lose a bug for a week in an HDL. Every signal in
Warp 11 carries its width, and every operation checks.

## Try this

- Change `larger` to `output "larger" 4` and re-run. Read the error; that error
  is a feature you will meet often.
- Add a fifth output, `notEqual`, driven by `bnot (eq a b)`.
- Watch `less` and `greater` and find the input where both are 0.

## See also

- [**Counter**](counter.md) — what changes once a register is involved and time exists.
- [**Priority mux**](priorityMux.md) — `If`/`Else`, and how a stack of conditions folds into
  the muxes you just met.
- [**Signed operations**](signedOps.md) — the same compare, over numbers that can be negative,
  and why that is a different operator rather than a different type.
