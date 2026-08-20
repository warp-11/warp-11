# Counter

A register that counts up while `enable` is high, and snaps back to zero when
`clear` is high. It is the smallest design that still has all three of the
things every Warp 11 design is made of: **ports**, a **register**, and
**statements that drive them**.

## What you are actually writing

Chips are described in a **hardware description language**, and the industry
standard is **Verilog**. A Verilog file declares a **module** — a box with named
inputs and outputs — and inside it, statements that say what drives what. The
tools that turn a design into a chip take Verilog, so anything you build has to
end up as Verilog eventually.

**Warp 11 is F# that writes that Verilog for you.** You get a module the same
way you get a function: `design "Counter"` declares one, `input` and `output`
declare its ports, and the statements in between wire it up. Every page in this
tutorial can show you the Verilog it produced — it is the *output*, not
something you maintain.

Two reasons to go through F# rather than write the Verilog directly:

- **It is ordinary code, so the language is yours.** A design that needs
  sixty-four copies of something is a `for` loop. A parameter is a function
  argument. A shape you use twice is a function. Verilog has its own weak
  versions of these, learned separately and used carefully; here they are the
  language you already know, and they run *before* any hardware exists.
- **It is checked before it is hardware.** Widths, names and connections are
  verified as your program runs, and the same source drives a cycle-accurate
  simulator you can step — the debugger this design opens in. A mismatch is an
  error in F# rather than a surprise on the board.

## What the F# is doing

**The code on this page never runs on the FPGA.** It runs once, on your machine,
and what it leaves behind is a circuit. Getting that straight first makes
everything else here read correctly.

`reg` does not create a variable — it creates a *register*, a row of flip-flops
that will exist on the chip. `==>` does not assign — it *connects*, permanently,
the way solder does. `If` does not branch when the design runs — it builds a
multiplexer, a piece of hardware that chooses between two values. Running your
program to produce all this is called **elaboration**, and what comes out is a
description of what is wired to what.

Three consequences, which are where intuition from software goes wrong:

- **Everything happens at once.** A processor runs one instruction and then the
  next. A circuit has no "next" — every statement on this page is live on every
  clock cycle, all of them, simultaneously. Order in the source expresses
  *priority*, not time.
- **A loop is a copier.** `for i in 0..3 do` runs four times during elaboration,
  on your machine, and leaves four copies of hardware behind. There is no loop
  on the chip to run.
- **The size of the design is fixed when elaboration ends.** Nothing is
  allocated afterwards, because there is nothing left to allocate it from.

Time in a circuit comes from one place: the **clock**. On each tick every
register samples whatever its inputs have settled to, and holds it until the
next tick. **Step** in this debugger is one tick.

## What to look at

`enable`, `clear`, `count` and `r` are already in the **watch** tab — a design's
ports arrive watched, and this page asks for `r` too. `enable` starts at 1,
because this page set it that way for you.

- Press **Step** a few times. `r` climbs.
- Set `enable` to 0 by typing into the box beside it, and keep stepping. `r`
  **holds** — it does not fall back to zero. (Typing a value into an input this
  way is called *poking* it — later pages will just say poke.)
- Set `clear` to 1. `r` is zero on the very next cycle, whatever `enable` says.

That last one is the priority, and it is the priority because of where the two
statements sit, not because anything declared it.

## Reading the source

```fsharp
let r = reg "r" 64
```

`reg` takes a name and a width, and the register **resets to zero** — which is
what nearly every register wants, so zero is the default rather than an
argument. The exceptions say so by name: `regInit "alloc" 6 1UL` resets to a
stated value, and `regNoReset` holds through reset entirely (a later page).
The name is not decoration:
it is what the register is called in the emitted Verilog and what you type into
the filter box to watch it. Names belong at creation and at emission, and Warp 11
gives you no way to attach one later.

### Why a register, and not just the output?

A fair question, and the answer is the difference between the two kinds of
signal. **An output is a wire.** A wire has no memory — it is a name for
whatever is driving it *right now*, and it must be driven on every path or it
has no value at all. So `count + 1UL ==> count` is not a counter; it is a wire
defined in terms of itself, which is a circle rather than a circuit.

**Counting needs something that remembers**, and that is what a register is: a
row of flip-flops that samples on the clock edge and holds until the next one.
`r` remembers, `count` shows it. The last line is the whole connection between
them, and it costs nothing on the chip — the emitted Verilog is `assign count =
r;`, and synthesis collapses the alias.

```fsharp
If clear (fun () -> 0UL ==> r)
Else (fun () -> If enable (fun () -> r + 1UL ==> r))
```

`If` and `Else` are Warp 11's, spelled with a capital because `if` and `else`
are F# keywords and cannot be borrowed. **That capital is the tell**: where you
see one in a design, the thing being built is hardware rather than a branch your
program takes. Read these two as statements in order: *if clear, zero it;
otherwise, if enable, add one.* Nesting is ordinary code — the body of an `If` is
a lambda, so anything you can write in F# you can write inside one.

The `0UL` and `1UL` have no width written on them because they do not need one:
a literal takes the width of the signal it is written beside, and `r` is 64 bits.
Where nothing is beside it to ask, the literal says its own width — `lit 0UL 64`
— and both spellings produce exactly the same hardware.

At elaboration these collapse into **selectors**: a piece of hardware that is
handed both answers at once and picks one, the way a railway point is always
connected to both tracks and passes traffic to whichever it is thrown to. The
conditions are what throw them. Two nested conditions make two selectors in a
row, and the emitted Verilog is one `always @(posedge clk)` block with a nested
`if`. Nothing here runs; it is a description of that arrangement, written in the
order people think about it. (The usual name for one of these is a
*multiplexer*, or **mux**, and the rest of the tutorial will use the short word.)

**The rule worth carrying away:** a **register** with no unconditional default
*holds its value* — the else branch is a self-assignment, which is why `r` stays
put when both inputs are low. A **wire** in that same position is an
elaboration error, because a wire that is not driven on every path has no value
to hold. That difference is the whole reason Warp 11 makes you say which one you
want.

```fsharp
r ==> count
```

`==>` is connect, and it points the way the data goes: the value on the
left, the signal it drives on the right. The width is checked here: `count` is 64 bits and so is `r`,
and if they disagreed this design would fail to elaborate rather than silently
truncating.

## The arithmetic

`r + 1UL` is 64-bit addition and it **wraps** — add one to all-ones and
you are back at zero. Warp 11 does not widen an add the way it widens a
multiply, because a counter that grew a bit every cycle would be useless.

At this width the wrap is theory: 2⁶⁴ counts at 166 MHz is about 3,500 years.
That is the point of choosing it — a counter of cycles, packets or generations
never has to be checked for running out. Narrow it to `8` and the same design
wraps every 256 cycles, which you can watch happen in a few seconds of stepping.
**The width is a decision, not a detail**, and it is one you make per signal.

If you want the count to stop at the top instead of wrapping, that is `satInc`,
and if you want the wrap to *mean* something — a row ending, a frame ending —
that is `counter` and `counterTo` in the stdlib, which hand you the wrap as a
signal you can use.

## Try this

- Change `If clear` and `Else` around and re-run. `enable` now outranks
  `clear`, which is almost certainly not what you want — and is exactly the kind
  of thing the debugger shows you in three steps.
- Add a breakpoint `r == 0x10` and press **Run**. It stops on the cycle the
  register reaches 16.
- Open the **waveform** tab, record `r` and `count`, and run. `count` is `r`
  with no delay, because it is a wire, not another register.

## See also

- [**Priority mux**](priorityMux.md) — the same `If`/`Else` shape driving a wire instead, and
  what "last connect wins" means underneath.
- [**Comparator**](comparator.md) — the same design with the register taken away, so nothing
  has a before or an after.
- [**Sequencer**](sequencer.md) — the holding rule again, doing real work: a state machine
  waits by taking no transition.
