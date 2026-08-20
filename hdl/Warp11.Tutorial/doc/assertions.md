# Assertions

A counter that walks 0→4 and wraps, and a claim it makes about itself: the
register never holds anything above 4. Three bits can hold eight values; this
design only ever reaches five, and it says so out loud.

## Why bother

A hardware bug is expensive in a way software bugs are not. On silicon you
cannot print, cannot attach a debugger, cannot add a log line and re-run — the
design is a picture that came out wrong three seconds ago. Whatever you did not
check in simulation, you find out about by *guessing*.

An assertion is a claim you write next to the thing it is about, and it is
checked **on every cycle of every simulation from now on**. Not on the cycle you
happened to look at. Not on the inputs your test used. Every cycle, every run,
including the runs someone else does next year with different stimulus.

## What to look at

Watch `phase` and `wrapped`. Poke `step = 1` and hold **Run**.

`phase` walks 0, 1, 2, 3, 4, 0, 1… and `wrapped` pulses at 4. It runs forever
and nothing ever fires, which is what a healthy assertion looks like: **silence**.

To see one fire you have to break the design, which is the "try this" below.

## Reading the source

```fsharp
If step (fun () ->
    If (eq r (lit 4UL 3)) (fun () -> lit 0UL 3 ==> r)
    Else (fun () -> r + lit 1UL 3 ==> r))

assertThat (bnot (lt (lit 4UL 3) r)) "phase left its range"
```

`assertThat` takes a condition and a message. The condition here reads *"not (4
< r)"* — that is, r ≤ 4. There is no `gt`, for the reason the [**Comparator**](comparator.md)
page gives: `4 < r` is how you spell `r > 4`.

It is called `assertThat` rather than `assert` because `assert` is an F#
keyword.

**Written inside an `If`, the claim inherits that branch's condition** — it
becomes "whenever this branch is taken, this must hold". At the top level like
this one, it means every cycle.

## What it costs on the chip: nothing

This is the part that makes assertions worth using freely. The emitted Verilog
puts them in their own block wrapped in a `translate_off` region:

```verilog
// synthesis translate_off
always @(posedge clk) begin
    if (!rst) begin
        if (!(!(3'd4 < r))) $fatal(1, "assertion failed: phase left its range");
    end
end
// synthesis translate_on
```

Synthesis skips that region entirely. **The bitstream contains no trace of it** —
no gates, no LUTs, no timing impact. Simulation does not skip it. So the claim
is checked everywhere it can be checked and costs nothing where it cannot.

They are also held off during reset, because a design has not promised anything
yet while it is being reset.

## Two simulators check it, and so does the debugger

- The F# simulator checks assertions when built with `Sim(design, checkAsserts = true)`.
  The debugger always does — it is a debugger, so claims the design makes about
  itself are exactly what it is for. **A violation stops the run like a
  breakpoint**, because that is what it is: a breakpoint the design carries with
  it.
- Verilator honours the `$fatal`, so the differential run fails too.

That second one has a consequence worth knowing: **a design whose assertion can
be tripped by random stimulus cannot ship in the tutorial**, because the
differential harness drives random inputs and would kill the testbench. Which is
a good discipline. An assertion should be an *invariant* — something true of
every reachable state — not a guess about what the inputs will be.

## Try this

The interesting experiments all involve breaking it on purpose.

- In the source, change the wrap test from `eq r (lit 4UL 3)` to
  `eq r (lit 5UL 3)`. Now `r` reaches 5, the claim is false, and the run stops
  the cycle it happens with the message you wrote.
- Move the `assertThat` inside the `If step` block. It now only checks on
  cycles where `step` is high — same claim, narrower scope.
- Add a second claim that is obviously false, like
  `assertThat (eq r (lit 0UL 3)) "r is always zero"`, and watch it stop
  immediately.
- Add a breakpoint on `phase == 4` and compare the two mechanisms. A breakpoint
  is a question you are asking now; an assertion is one the design keeps
  asking.

## See also

- [**Sequencer**](sequencer.md) — a bigger state machine, where `machine` checks a related
  property at elaboration: that every state has a way in.
- [**ROM**](romTable.md) — the padding addresses nobody should ask for, which is exactly what
  an assertion is for.
- [**Counter**](counter.md) — the same wrap-and-hold shape without the claim.
