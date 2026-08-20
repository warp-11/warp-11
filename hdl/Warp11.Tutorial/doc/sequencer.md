# Sequencer (state machine)

Six named states, walking `Idle → Fetch → Decode → Execute → Writeback` four
times and stopping at `Done`. This is how hardware does control flow, and it is
the design where the debugger starts telling you things a waveform cannot.

## The idea to get first

There is no program counter here and no instruction being executed. A state
machine is a **register holding which state you are in**, plus combinational
logic deciding what the next state is. Every clock tick, the next-state logic
runs and the register updates. That is all.

Which means: one state per cycle, always. Not "however long the body takes" —
there is no body, and nothing takes longer than a cycle. If a step needs more
time, it gets more states, or it stays in the same state for more cycles. That
is what `stall` does below.

Every loop, every wait, every "do this then that" in hardware is ultimately this
shape.

## What to look at

Watch `stage`, `count`, `busy` and `finished`, then poke `start = 1` and press
**Step** repeatedly.

Look at what `stage` shows:

```
Fetch (0x1)
```

Not `0x1`. **`Fetch`.** Elaboration knows the state names, so the debugger
prints the meaning next to the number. That is a small thing that changes how
this feels — a waveform viewer would show you `1` and leave you counting.

Now poke `stall = 1` while `stage` is `Execute` and keep stepping. It sits
there. Release it and it moves on. That is a hardware "wait" — not a blocked
thread, just a next-state decision that keeps choosing the same state.

## Reading the source

```fsharp
let stage = machine "stage" [ Idle; Fetch; Decode; Execute; Writeback; Done ]
```

One declaration. `machine` owns the register, picks its width, assigns the
encodings, and builds the decode logic. The states go in as **values from your
own F# type** — not integers you have to keep straight yourself.

```fsharp
stage.If Fetch (fun () -> stage.Goto Decode)
stage.If Decode (fun () -> stage.Goto Execute)
```

`stage.If S` means *while in state S*, and `stage.Goto T` means *next cycle, be
in T*. Written this way the transition table is the source, in the order you
would draw it.

```fsharp
stage.If Execute (fun () -> If (bnot stall) (fun () -> stage.Goto Writeback))
```

A conditional transition: leave `Execute` only when `stall` is low. Note there
is no `else` — with no `Goto` taken, the state register has nothing driving it,
so it **holds**, exactly as in [**Counter**](counter.md). Waiting is the absence of a
transition.

```fsharp
stage.If Writeback (fun () ->
    count + lit 1UL 8 ==> count
    If (eq count (lit 3UL 8)) (fun () -> stage.Goto Done)
    Else (fun () -> stage.Goto Fetch))
```

A state that both does work and branches. `count` increments here and nowhere
else, so it counts completed passes.

```fsharp
bnot (stage.Is Idle ||| stage.Is Done) ==> busy
stage.Is Done ==> finished
```

`stage.Is S` is a one-bit signal, true while in that state. Outputs derived from
the current state like this are combinational — they change the instant the
state register does, with no extra cycle.

## What `machine` buys over doing it by hand

The emitted Verilog is identical to writing `lit 4 3 ==> stage` and
`eq stage (lit 4 3)` yourself. Everything `machine` adds is *known at
elaboration*:

- The debugger can print `Writeback` instead of `4`.
- Finalize checks that **every state has a way in**. A state you declared and
  never `Goto`'d is dead logic and almost always a typo — and it is exactly the
  kind of mistake that survives a test suite, because the tests also never reach
  it.
- The width is computed, so adding a seventh state cannot overflow an encoding
  you sized by hand.

None of that costs a gate.

## Try this

- Add a breakpoint `stage == 5` and press **Run**. It stops on the cycle it
  reaches `Done`.
- Hold `stall` high through an entire run and watch `count` never move.
- Open the **waveform** tab, record `stage` and `count`, and run — the staircase
  is the four passes.
- In the **source** tab, count the states, then check the width of `stage` in
  the signal list. Six states, three bits.

## See also

- [**Counter**](counter.md) — the holding rule this design leans on for `stall`.
- [**Priority mux**](priorityMux.md) — the `If`/`Else` inside `Writeback`.
- [**RAM**](ram.md) — the memory a sequencer like this one usually exists to drive.
- [**Assertions**](assertions.md) — claims a design makes about itself, checked every cycle at
  no cost on the chip.
