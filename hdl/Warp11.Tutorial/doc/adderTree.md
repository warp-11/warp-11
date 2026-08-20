# Adder tree

Eight values summed two ways — a combinational balanced tree, and the same tree
with every level registered. They give the same answer. Which one you want is
decided by the clock, and getting it wrong is the single most expensive mistake
in this whole tutorial.

## What to look at

Poke `x0..x7` to 1 through 8 and `enable = 1`.

- `flat` reads **36** immediately. No stepping.
- `pipelined` reads 0, then 0, then after **three** steps, 36.
- `depth` reads 3, which the design did not have to be told — the tree reports
  how deep it turned out.

Same sum. One arrives now, one arrives in three cycles.

## Why a tree and not a chain

The obvious way to sum eight numbers is `a + b + c + d + …` — seven adders in a
row. That works, and its **depth is seven adders**: the last one cannot start
until the sixth has finished, because it needs the answer.

```fsharp
reduceTree (+) widened ==> flat
```

`reduceTree` pairs them up instead: four adds, then two, then one. Same seven
adders, same area, same arithmetic — but **depth 3 instead of 7**, because the
four first-level adds happen simultaneously.

Depth is what a clock period has to accommodate. For eight inputs the tree is
twice as fast for free; for 64 it is ten times.

Integer addition is associative, which is what makes the regrouping legal. That
is worth saying explicitly because floating-point addition is *not*, and the
same transformation there changes the answer.

## The part that costs real money

Here is the gotcha, and it is the flagship one in this repo:

**A combinational tree that is correct in every simulator can be wrong on
silicon.** The simulator and Verilator are cycle-accurate and have **no timing
model**. A cone of logic several times too slow for the clock period looks
perfectly correct in both — and on the real chip, the register at the end
latches a value that has not finished settling. You get a design that passes
every test and produces garbage on the board.

Reading the post-route timing report is free and catches the whole class. The
fix, when it is needed, is this page's second half.

## `adderTreePipelined` — registers as the point

```fsharp
let deep, levels = adderTreePipelined "acc" 11 enable widened
```

Every level of the tree gets a register, `enable` freezes the whole thing
together for backpressure, and it returns the sum **and its latency**, because
the depth is a fact about the tree rather than something the caller should be
asserting. (Same principle as [**Stream pipe**](streamPipe.md)'s rejection of `pipe(latency)`.)

**This is not the combinational tree with registers bolted on.** The library's
note on it is emphatic, and the reason is specific to FPGAs: a combinational
tree feeding DSP blocks gets *re-flattened* by synthesis into a deep DSP
cascade, because the DSP's dedicated accumulate path is the tool's preferred
shape and that path is linear. Registers are the one thing the tool cannot move
across.

So anything that maps to DSPs — FIR filters, convolution, dot products — wants
the pipelined form, and the log-depth combinational tree is not a substitute.
The `fir` entry in the stdlib uses `reduceTree` for precisely this reason, and
its own comment points at the same gotcha.

## Where this is used

`countWhere` — how many of these satisfy a predicate — is an adder tree with a
zero-extended verdict per input. The Game of Life population count is a
4096-leaf one. Above 256 inputs it lands on named partial wires per chunk,
because a 4096-leaf tree on a single line overflows Verilator's token limit,
which is the kind of thing you learn by hitting it.

## Try this

- Set all eight inputs to 255 and check `flat` — 11 bits is enough for 8 × 255,
  which is why the inputs are widened before summing.
- Poke `enable = 0` and step. The pipelined tree freezes mid-flight; the flat
  one does not care, having nothing to freeze.
- Change the inputs while the pipeline is in flight and watch a mixed answer
  emerge — the stages hold different generations.
- In the source, replace `reduceTree (+)` with `List.reduce (+)` and diff the
  emitted Verilog. Same arithmetic, very different shape.

## See also

- [**Arbiter (one-hot)**](arbiter.md) — `mux1H` is a `reduceTree` over ORs.
- [**Bit shapes**](bitShapes.md) — `popCount`, which is this at one bit per input.
- [**Stream stages**](streamStages.md) — latency and throughput, the trade this page is an
  instance of.
