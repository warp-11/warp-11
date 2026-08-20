# How it fits together

A map of the whole system, for someone who has not seen it before. Every other
guide assumes you already know how the pieces relate; this is the page that says
so. It is deliberately shallow — each section ends with a pointer to the thing
that goes deep.

## Start with the GPU you already know

If you have ever moved part of a workload onto a GPU, you know the shape: some
of the program runs on the host, the expensive inner part runs on a chip built
for it, and the host feeds it work and reads results back. An FPGA is used the
same way, and Warp 11 exists to make that offload reachable from ordinary
software work.

**The difference is what the chip is.** A GPU is a fixed piece of silicon with
an instruction set, and your kernel is a program it executes. An FPGA has no
instruction set. It is a field of logic gates, memories and multipliers with
programmable wiring between them, and what you send it is not a program but a
*circuit* — a description of which gates connect to which. Load that and the
chip becomes your circuit, until you load a different one.

So the code you write here is not code that runs on the FPGA. It is code that
**builds** the thing the FPGA becomes.

## Your F# does not run on the FPGA

This is the one idea worth getting straight before anything else, because
everything downstream follows from it.

```fsharp
let counter =
    design "Counter" (fun () ->
        let enable = inputBit "enable"
        let count = output "count" 64
        let r = reg "r" 64

        If enable (fun () -> r + 1UL ==> r)
        r ==> count)
```

That `If` is not a branch taken at run time. It runs **once, on your machine**,
while the design is being built, and what it does is decide what wires exist.
The `+` does not add anything — it plants an adder. Running this F# produces a
data structure describing a circuit; the circuit is what has behaviour later.

That step is called **elaboration**, and the distinction it draws — F# runs at
elaboration, the circuit runs on the clock — is the mental shift the whole
toolkit rests on. It is also what makes F# a good fit: your full language is
available to *generate* hardware, so a 104-lane compute pod is a `List.map`, not
104 copies of anything.

The [**Counter**](../hdl/Warp11.Tutorial/doc/counter.md) tutorial walks exactly this design, one line at a time.

## The pieces

```
              your design, in F#
                       │
                       │  elaboration — runs once, on your machine
                       ▼
                   the IR ── a graph of gates, registers and memories
                    │  │
       ┌────────────┘  └────────────┐
       ▼                            ▼
   Verilog                    the simulator
       │                     (compiled, cycle-accurate)
       │                            │
       ▼                            ├──► the step-through debugger
   Vivado                           │
       │                            └──► your Rust driver, over a bridge
       ▼                                          ▲
   bitstream ──► the FPGA ◄── AXI ──► Rust runtime ┘
                                          ▲
                                          │
                    one register-map definition generates BOTH
                    the AXI slave in the fabric and the Rust
                    struct the host reads it through
```

**The elaborator and the IR** (`hdl/`, F#). The DSL builds an intermediate
representation: width-typed bit vectors, registers, memories, module instances.
Everything above it — streams, fixed-point numbers, state machines, the standard
library — compiles down to that same small vocabulary, which is why a new
abstraction costs nothing at the bottom.

**The Verilog emitter.** Turns the IR into Verilog, which is the language
FPGA vendor tools take as input. Verilog is where this ends up because it is
what Vivado reads, not because anyone enjoys writing it — you can go a long way
here without reading any.

**The simulator.** A second consumer of the same IR, and the reason the loop is
quick. It compiles the design at construction — names become slots, expressions
become closures with their widths folded in — so it is fast enough to run real
workloads, not just smoke tests. Because it reads the *same IR* as the emitter,
what you simulate is what you synthesize.

**The debugger** (`Warp11.SimView`). A desktop application over a running
simulation: watch any signal at any depth, poke inputs, page through memories,
set breakpoints written as expressions over the design's own signals, export
VCD. It is also the thing you can try in a browser without installing anything.

**The runtime** (`runtime/`, Rust). What runs on the board's CPU — or on your
workstation — to drive the design over AXI: register access, DMA, the device
drivers for each accelerator. Rust rather than F# because this half *ships* onto
boards that may have no Linux-class host, where the F# half never leaves your
machine.

**The seam between them.** One register-map definition generates two things: the
AXI slave that appears in the fabric, and the Rust structs the host reads it
through (`runtime/core/src/*_layout.rs`, generated and committed). Because both
come from one source, the program and the hardware cannot disagree about which
register lives at which offset — the class of bug that otherwise costs an
afternoon every time a field moves.

**And the same driver runs against both.** The Rust runtime can talk to the F#
simulator over a bridge instead of to a real board, so host code and hardware
can be developed and tested together before any silicon exists.

## What you actually type

```sh
dotnet run --project Warp11.Designs          # elaborate + run the living checks
dotnet run --project Warp11.Tutorial.App     # the step-through debugger
./run_differential.sh                        # the oracle (see below)

# emit for silicon: writes the Verilog *and* the generated Rust seam
dotnet run --project Warp11.Mandelbrot -- hardware <repo-root>
```

From there it is vendor territory: Vivado turns the Verilog into a bitstream,
the board loads it, and the Rust runtime drives it. That path — synthesis,
timing, deploying to a KV260, the gotchas that only appear on real hardware —
is the [**Hardware workflow**](dev-workflow.md) guide, and it is the part that is genuinely
fiddly. Nothing above it requires an FPGA; you can do all of it with a
simulator and a debugger.

## How we know it is right

The simulator and the Verilog come from one IR, which is convenient and also a
risk: a bug in the shared understanding would be invisible to both. So the
project's main test is a **differential oracle**. Every design in the library is
run twice — once in the F# simulator, once by Verilator executing the emitted
Verilog against a generated self-checking testbench asserting the simulator's
exact trace, cycle by cycle. Any divergence fails the build. It has caught real
emitter bugs on first contact, repeatedly.

There is a third leg, off by default: the same design exported as low-level
**FIRRTL** — a standard hardware IR — and compiled by the `firtool` compiler from
the LLVM project, then checked the same way. The point is independence. Our
simulator and firtool are strangers to each other, so where they agree, the
agreement means something that two of our own tools agreeing would not.

**You never need FIRRTL to use Warp 11**, and it is not installed by default.
It is a second opinion the project keeps for its own benefit, and it earns its
keep: it has caught bugs that the Verilog leg could not see, because Verilog was
happy with output our simulator also considered correct.

## Where to go next

- [**Counter**](../hdl/Warp11.Tutorial/doc/counter.md) and the rest of the tutorial — the mechanisms, one small design per
  page, each steppable in the browser.
- [**Streams**](streams.md) — the connection layer most real designs are built out of.
- [**Hardware workflow**](dev-workflow.md) — synthesis, the board, and the hardware-only gotchas.
- [**Comparison to other HDLs**](HDL_COMPARISON.md) — how this relates to Chisel, SpinalHDL,
  Amaranth, Clash and the rest, feature by feature.
- The example write-ups — Mandelbrot, Game of Life, GEP — for what a whole
  project looks like, fabric and host together.
