# HDL Landscape — Warp 11 in Context

## Why Warp 11 exists

**Warp 11 exists to make it as easy as possible to create a wide variety of FPGA designs at any scale** — wide variety first, ease second. In practice that means one description of a design that drives a cycle-accurate simulator *and* real silicon, host↔FPGA mixing in both worlds, a step-through debugger over the running design, and a generated seam so the host program and the fabric cannot disagree about the register map.

Warp 11 starts from a software developer who wants to deploy part of a workload to an FPGA and keep the rest on the host, and treats *the whole loop* — elaborate, simulate, debug, synthesize, drive from a program, read the telemetry back — as the thing to make cheap. That starting point, rather than any particular feature, is what the rest of this document is comparing.

That is not a claim of being "better" than the entrenched HDLs. Chisel, SpinalHDL and the others have years of production hardening, deeper formal stories, and broader standard libraries. It is a claim about a different axis: the distance from an idea to a thing running on a board with a program talking to it.

The rest of this doc walks Warp 11 against each major HDL feature by feature, then covers the [WarpCPU shape](#the-warpcpu-shape--an-architectural-pattern-not-a-language-feature) — an *architecture* the project keeps arriving at rather than a feature, and the place it differs most from the field.

## The shape: F# elaborates, Rust runs, the seam is generated

Unlike every other entry here, Warp 11 is **not one host language**. That is deliberate, and it was arrived at by building this toolkit end to end three times and measuring each:

- **F# is the DSL half.** Designs, the standard library, elaboration, the simulator, Verilog emission. Chosen at the *use* site: instances are ordinary functions, so `accumulate (multiply a b) (multiply (bump c) d)` is a design, and whether each of those is a module instance or inline logic is a definition-site decision invisible to the caller.
- **Rust is the runtime half.** The board-side driver, the daemons, the memory-mapped register access, the DMA. Chosen for board reach: the smallest targets in scope run no managed runtime at all.
- **The seam between them is generated, not agreed.** An AXI-Lite register map declared once in F# emits both the slave's RTL and a Rust layout module of offsets, bit positions and masks that the driver imports. Neither side can drift from the other, because only one of them is authored.

The cost is real and was measured before being accepted: two toolchains, and one artifact crossing between them. The benefit is that neither half is compromised to keep the other happy — which is what happens when a single language has to be good at both algebraic data types and `#![no_std]`.

## TL;DR matrix

| HDL | Host language | Backend | Stdlib breadth | Sim story | Formal | Docs | Production users |
|---|---|---|---|---|---|---|---|
| **Chisel** | Scala 2.13 | FIRRTL → SV | Very broad (Decoupled, Queue, Arbiter, Vec, Mem, RegMapper, AXI generators…) | Treadle, ChiselSim, ChiselTest, Verilator integration | Via FIRRTL passes / external | Excellent, large bootcamp | SiFive RISC-V cores, RocketChip, BOOM, lots |
| **SpinalHDL** | Scala 2.13 | Direct → V/SV/VHDL | Very broad — explicit Stream/Flow/Fragment + AXI4, AHB, APB, Wishbone, TileLink | Built-in cocotb-like (SpinalSim), Verilator backend | SymbiYosys integration | Very good, Sphinx site | VexRiscv (popular open RISC-V), commercial designs |
| **HardCaml** | OCaml | Verilog/VHDL/SV | Broad (Hardcaml_circuits, Xilinx prims, FIFOs, RAMs, AXI in `hardcaml_axi`) | First-class OCaml simulator + `hardcaml_waveterm` | SAT-based via `hardcaml_verify` | Good (Jane Street style mdx) | Jane Street's HFT infrastructure |
| **Amaranth** | Python 3 | Verilog-2001 | Growing — `amaranth.lib` (data, fifo, cdc, wiring, io, memory) | Built-in Python event-driven simulator | Via Yosys + SBY | Good, sphinx site, examples | LiteX ecosystem, Glasgow Interface Explorer |
| **Clash** | Haskell (GHC) | Verilog/VHDL/SV | Functional stdlib (Vec, signals); narrower than Chisel-class | Interactive REPL, simulation in GHC | Via standard tools | Decent (Hackage + tutorial) | QBayLogic, niche academic |
| **Bluespec (BSV)** | BSV (own lang, SV-or-Haskell flavored) | Verilog | Guarded-atomic-actions stdlib (rules, methods, FIFOs, BRAMs, NoC fabric) | bluesim included | Recent partnership w/ Axiomise for RISC-V cores | Decent, BSC has 20+ yrs of materials | Flute, Piccolo, Shakti RISC-V; Achronix integrations |
| **Veryl** | own lang (Rust-syntax-inspired) | SystemVerilog | Thin (it's a transpiler, leans on SV ecosystem) | Defer to SV simulators (Verilator, VCS) | Defer to SV tools | Good, modern site, LSP integration | Early adopters, growing |
| **Warp 11** | F# (DSL) + Rust (runtime) | Verilog (multi-module, hierarchical) | Audio (biquad EQ + RBJ cookbook, FIR, broadband compressor, limiter, gain, tone, I2S); AXI-Lite slave from a declarative register map with generated Rust layouts (registers, pulse/W1C bits, and windows in both directions — host-written with arbitrated readback, and design-written for the host to read), AXI4 master read+write (burst, multi-outstanding); typed streams with `wormhole` connect + operator chains, replication, unions, merge/dispatch trees, stall telemetry; snapshot buffer (CONFLATE/N=3); `reduceTree`/`countWhere`/`neighborhood`/`warpFu`/xoshiro; state machines over a union of states, decoded by name in the debugger; one numeric layer over the width-only IR — width, fraction bits and signedness in one format, so signed/unsigned integers and signed/unsigned fixed point are one type and the five sign-dependent primitives are chosen by the format rather than by the call site. no image-processing or crypto modules | Compiled cycle-based interpreter; Verilator differential across narrow + wide; **step-through debugger with signal breakpoints**; Rust driver runs against the F# sim over a bridge | None | This file + README + per-area design docs | One developer; three accelerators (Mandelbrot, GoL, GEP) running on a KV260, F#-elaborated and driven by the Rust runtime |

## Per-HDL deep dive

### Chisel (chipsalliance/chisel)

The 800-lb gorilla. Scala embedded DSL, lowers to FIRRTL (intermediate IR), then to Verilog/SystemVerilog. Latest at v7.x. Used to build RocketChip, SiFive's commercial cores, BOOM. Standard library is broad: `Decoupled` (ready/valid handshake), `Queue` (FIFO with Decoupled on both sides), `Arbiter`/`RRArbiter`, `Vec`, `Mem`/`SyncReadMem`, BlackBox for foreign Verilog, formal annotation hooks.

Ergonomics: powerful but Scala/sbt friction is the standard complaint. Error messages have improved a lot but still inherit Scala compiler diagnostics. The bootcamp materials (Jupyter-based) are excellent for onboarding. The FIRRTL intermediate layer enables transformations (CIRCT MLIR backend, lint passes, equivalence checks) that other HDLs can't easily do.

### SpinalHDL (SpinalHDL/SpinalHDL)

Often described as "Chisel with the rough edges smoothed." Direct generation to Verilog/VHDL (no FIRRTL middle), Scala embedded. The standard library is genuinely impressive:

- `Stream[T]`, `Flow[T]`, `Fragment[T]` as first-class communication primitives.
- Full AXI4 / AXI4-Lite / AXI4-Stream bus systems with one-line connections (`s1 >> s2` style).
- Wishbone, AHB-Lite, APB3, TileLink bus families.
- BMB (Banana Memory Bus, internal SpinalHDL bus).
- Built-in test infrastructure (SpinalSim) with cocotb-style fork/join.
- Auto-pipelining helpers, register transformation phases.

Production: VexRiscv (the popular open RISC-V soft-core) is SpinalHDL. Active development, responsive community.

### HardCaml (janestreet/hardcaml)

Jane Street's OCaml HDL, used in their HFT hardware infrastructure. Strongly typed, functional, with OCaml functors for parametric components. Includes a full simulator, waveform output (`hardcaml_waveterm`), SAT-based formal verification (`hardcaml_verify`), Xilinx primitive wrappers, and FPGA-friendly RAM/FIFO models.

Ergonomics: OCaml is a smaller talent pool, but the language gives you a very strong type system that catches many circuit-level errors at compile time. Jane Street's docs are good (executable markdown via mdx).

### Amaranth (amaranth-lang/amaranth)

Python embedded DSL, formerly nMigen. Targets Verilog-2001 (so any synthesis tool that takes Verilog-2001 works). The `amaranth.lib` stdlib is growing: `lib.fifo`, `lib.cdc` (clock-domain crossing), `lib.wiring` (typed interfaces), `lib.data` (struct/union/array types), `lib.io`, `lib.memory`. Built-in event-driven simulator in pure Python (no system deps).

Ergonomics: Python's accessibility is the main draw. First-class clock domains and FSMs make state machines easier than in most HDLs. NLnet has funded development; the project is active and well-documented.

Notable users: LiteX (an entire SoC framework built on it), Glasgow Interface Explorer hardware.

### Clash (clash-lang/clash-compiler)

Haskell-to-HDL compiler. Treats hardware as functions from `Signal a -> Signal b`. Strongly typed in a way only Haskell can manage — clock domains are type-level, mixing them is a type error. REPL-based development.

Ergonomics: Haskell's learning curve is real. But for a Haskeller, the design feels natural. Smaller community than Chisel/SpinalHDL.

### Bluespec (B-Lang-org/bsc)

Open-sourced in 2020 (was commercial before). Distinct from the others: uses *guarded atomic actions* — you describe hardware as concurrent rules with implicit scheduling. The compiler figures out the cycle-by-cycle behavior. Higher abstraction, steeper learning curve, but produces correct-by-construction designs that are very hard to write race conditions into.

Production: Flute, Piccolo, Shakti RISC-V cores. Recent commercial partnerships (Achronix Speedster7t embedding, Axiomise formal verification, July 2025).

### Veryl (veryl-lang/veryl)

Newer (~2023). NOT an embedded DSL — it's a standalone language with its own syntax (Rust-flavored). Transpiles to human-readable SystemVerilog. Comes with a formatter, language server (works in VSCode/Vim/Emacs), package manager, real-time semantic checker. Has a `translate` command to convert existing SV → Veryl.

## Feature-by-feature

### Typed I/O / bundles

| HDL | Mechanism |
|---|---|
| Chisel | `Bundle` classes with `Decoupled`/`Valid` wrappers; direction via `Input`/`Output`/`Flipped` |
| SpinalHDL | `Bundle` + `master`/`slave` interface direction; auto-connect via `>>` |
| HardCaml | OCaml records via PPX deriver |
| Amaranth | `lib.wiring.Signature` and `Component` |
| Clash | Type-level via Haskell records |
| Warp 11 | `defineModule` with a re-runnable IO factory, so a module's ports are a typed value (tuple or record) at both definition and use. **Instances are functions**: instantiating returns the module's own function, so a call site cannot tell a module from inline logic. Streams carry typed `Layout` payloads and `Union2` sum types; `Number` gives a format — width, fraction bits, signedness — so Q4.28 × Q4.28 is Q8.56 by construction and a mismatch fails at elaboration |

Two things here have no direct analogue in the field surveyed. **Call-site
invariance**: `dot2Ambient` and `dot2Inline` are character-for-character the
same design body — only the three stdlib bindings differ, and one elaborates to
four modules while the other elaborates to one flat module. Whether something is
a module is a decision at its *definition*, switchable without touching a
caller. And **one numeric layer** where a format is a width, a count of fraction bits
and a signedness: Q4.28 multiplied by Q4.28 is Q8.56 by construction, integers
are the `fracBits = 0` case of the same type, and the five operations whose bits
depend on signedness are chosen by the format rather than by the call site, with
`renormTo`/`reinterpret` as the only named escapes — the Q-format bookkeeping that every fixed-point
design does in comments is in the type system instead. Clash's type-level clock
domains are the nearest thing in spirit.

**Aggregates live above the IR, on purpose.** A bundle is a `Layout` and a
vector is an F# list, and both are gone before the IR sees anything — the IR is
ground types and nothing else. That is not a shortcut; it is where FIRRTL puts
them too, which is what its `LowerTypes` pass exists to do, and why *low* FIRRTL
has no aggregates at all. Two consequences worth stating:

- **A vector is a list, so it composes.** Audio's FIR builds its 16-tap delay
  line with `List.scan`, and the taps then `zip` against the coefficients and
  fold into an adder tree. A `Vec` type would be *worse* than this, not better —
  it would need its own map, its own zip and its own fold, all of which the host
  language already has.
- **Reading a design that does use them is a preprocessing step, not a missing
  feature.** `firtool --ir-fir | circt-translate --export-firrtl` scalarizes
  bundles and vectors into exactly the subset `Warp11/FirrtlImport.fs` reads.

Still missing versus SpinalHDL, and genuinely: nested-bundle helpers and
interface-level `master`/`slave` flipping. Those are ergonomics on the `Layout`
layer rather than anything the IR would need to grow.

### Registers and reset

| HDL | how you get a register that holds through reset |
|---|---|
| **Chisel** | `Reg(t)` — the default; `RegInit(v)` is the one that resets |
| **SpinalHDL** | `Reg(t)` by default; `.init(v)` adds the reset |
| **Amaranth** | `Signal(reset_less=True)` |
| **Clash** | `register` always takes an initial value; unreset needs `unsafe` primitives |
| **Warp 11** | `regNoReset "held" 8`, beside `reg "counter" 8` (resets to zero — the measured 98.6% case, so zero is the default) and `regInit "cleared" 8 3UL` |

Resetting every register is the safe default and the wrong one for a data
path. **A reset net reaching every flop costs fanout and
routing, and it stops Vivado inferring an SRL for a delay chain** — Xilinx's own
advice is to reset control state and leave the data path alone. Note where the
default sits: Chisel and Spinal make *unreset* the default and resetting the
thing you ask for; Warp 11 does the opposite, because a state machine that
survives reset is a bug and the safe default is worth more than the keystrokes.

A module whose registers are all `regNoReset` and which writes no memory emits
no reset branch at all, so the flops genuinely have none:

```verilog
always @(posedge clk) begin
    acc <= (sel ? bundleIn_a : bundleIn_b);
end
```

It is also FIRRTL's plain `reg`, which is what most Chisel output contains — so
this is the same feature that lets `Warp11/FirrtlImport.fs` read a lowered
Chisel design rather than refusing it.

### Mixing signed and unsigned

| HDL | `signedVal + unsignedVal` | connecting a signed value to an unsigned wire |
|---|---|---|
| **Chisel** | **Scala compile error** — `UInt` and `SInt` are distinct types with distinct operators (`def +(that: UInt)`) | **elaboration error** — `:=` takes `Data`, so scalac cannot help; `MonoConnect` requires structural type equivalence and "UInt and SInt are not" |
| **SpinalHDL** | rejected | rejected |
| **Amaranth** | allowed — the value model converts | allowed |
| **Warp 11** | **elaboration error** — in `add`/`sub`/`mux` as well as `mul`/`lt`; a literal borrows its neighbour's reading | **allowed**, and deliberately |

Chisel gets the compile-time win on operands because signedness lives in the
*Scala* type. Ours lives in a `GroundType` the `Expr` carries as a value, so the
same mistake is caught one stage later — at elaboration, which is the build.

The connect column is where we differ on purpose. A connection is solder: no
gates differ between the two readings, and the FIRRTL export inserts the
`asUInt`/`asSInt` that FIRRTL's typed connects require, at zero cost. Requiring
it in the source would mostly restate the target's own declaration — measured
across four projects, roughly half the unsigned→signed connections are
a `slice` or `cat` result landing on a wire declared `SInt`, which is exactly how
a design says "read these bits as signed" now that bit manipulation lands in
`UInt` by FIRRTL's rule.

What we do instead is make the *operations* strict and let the declarations carry
meaning, which is why `shr`, `pad` and `saturate` all return their operand's
reading rather than bit patterns.

### Shifting by a signal

| HDL | constant shift | shift by a signal |
|---|---|---|
| **Chisel** | `x << 3` | `x << n` — same operator, width `w + 2^m - 1` |
| **SpinalHDL** | `x << 3` | `x |<< n` (fixed width) or `x << n` (widening) |
| **Amaranth** | `x << 3` | `x << n` |
| **Warp 11** | `shl 3 a` | `shl n a` — same call, and `n` being a signal is what chooses |

The constant forms are a rewiring — `cat` and a part-select — and cost nothing;
the variable ones are a barrel shifter. `shl`/`shr` take either, dispatched on
the amount's *type*, so there is no second name and no way to write one meaning
the other.

The widths are FIRRTL's, including the part that surprises people: a dynamic
left shift keeps every bit it could ever produce, so 8 bits by a 3-bit amount is
**15 bits**. The elaborator cannot know the amount and will not guess; narrowing
is a decision you take with `saturate` or a slice. A shift amount wider than 12
bits is refused rather than attempted, since the rule would ask for a
4-gigabit value.

A dynamic right shift is arithmetic exactly when its operand is signed, and that
is **one of only two places the emitter writes `$signed`** (signed division is
the other) — a variable-distance sign fill has no width-explicit form, where the
constant case replicates a named bit.

### Folding a value to one bit

| HDL | any bit set | all bits set | parity |
|---|---|---|---|
| **Chisel** | `x.orR` | `x.andR` | `x.xorR` |
| **SpinalHDL** | `x.orR` | `x.andR` | `x.xorR` |
| **Amaranth** | `x.any()` | `x.all()` | `x.xor()` |
| **Warp 11** | `anyBitSet x` | `allBitsSet x` | `parity x` |

Named for what they answer rather than for the gate — FIRRTL and Verilog call
these `orr`/`andr`/`xorr` and `|x`/`&x`/`^x`, which say how rather than what.
`anyBitSet` is the one designs otherwise write as `x != 0`, and it is a single
OR gate where that is a comparator.

### Division

| HDL | what `/` gives you |
|---|---|
| **Chisel** | `a / b` for any operands — a combinational divider |
| **SpinalHDL** | `a / b` for any operands |
| **Amaranth** | `a // b` for any operands |
| **Warp 11** | `divideBy 10 a` — **constant divisors only**, enforced by the parameter being an `int` |

Every one of those emits a combinational divide, which is correct and, at width
on an FPGA, a timing failure that is invisible until Vivado's WNS report. Warp 11
draws the line where the cost is rather than at the operation: a constant divisor
is not a divider at all — `/ 8` is a part-select, `/ 10` is a multiply by a
reciprocal — and both are free from synthesis. A divisor that varies is thirty
levels of logic, and it looks identical at the call site, so this surface does
not let you write it. The F# type refuses it before elaboration runs.

The IR still carries the general form, because a foreign FIRRTL design is
entitled to divide by a signal and the reader has to take what it says. Same
split as the registers above: the IR mirrors FIRRTL, and the authoring surface
has opinions.

Widths follow FIRRTL, including the one that looks like padding and is not: a
signed quotient is one bit wider than its dividend, because −128 / −1 is +128.

For a divisor that genuinely varies, the stdlib has one — **as a stream stage,
not a latency**:

```fsharp
let results = divider "dv" 32 requests
// requests : Stream<Expr * Expr>   (dividend, divisor)
// results  : Stream<Expr * Expr>   (quotient, remainder)
```

Radix-2 restoring, one subtractor reused for `width` iterations, so it cannot
take a new pair every cycle — and `ready` is the only thing that can say so.
**No latency crosses the boundary**, which is the same principle by which
`pipe(latency)` was rejected for stream stages, applied to a unit that actually
needs it. Division by zero saturates to all-ones rather than trapping, because
every trial subtraction succeeds.

That is the default. `warpFu` wraps the same idea when many clients share one
divider and results need tags to find their way home, and a C-slow barrel can
use a bare fixed-latency core because `threads > latency` makes the handshake
unnecessary by arithmetic. Three regimes, and only the last one involves a
latency number.

### Storage style

| HDL | |
|---|---|
| Chisel | `SyncReadMem` / `Mem`; style via `annotate` or a `ram_style` chisel-annotation |
| SpinalHDL | `Mem(...).addAttribute("ram_style", "distributed")` |
| Amaranth | `Memory` + platform-specific attributes |
| Warp 11 | `distributedMem` / `blockMem` / `mem`, and **the read is checked against it** |

The difference is where the mistake is caught. Everywhere else the style is an
attribute you may or may not have attached, and a combinational read of an array
the tool put in a block silently gains a cycle — which passes simulation, passes
Verilator, and corrupts on the board. Warp 11 makes the style part of the
declaration and a combinational `memRead` an error on anything but
`distributedMem`, so the
question is answered where the memory is created rather than discovered in a
timing report or a garbled frame.

### Memories

| HDL | Patterns |
|---|---|
| Chisel | `Mem` (combinational read), `SyncReadMem` (BRAM); explicit read/write ports |
| SpinalHDL | `Mem`; reads as `mem.readSync(...)` / `mem.readAsync(...)` |
| HardCaml | RAM primitives, plus Xilinx-specific wrappers |
| Amaranth | `lib.memory.Memory` with `.read_port()` / `.write_port()` |
| Warp 11 | `mem name addrWidth width` with `memRead` (combinational, `distributedMem` only) / `memReadPort` (1 cycle, any storage, carries the caller's values across itself) / `memWrite` / `memWriteMasked` (one bit per lane — AXI's `wstrb`, the byte-enabled block-RAM shape), plus initialized ROMs. Depth is `2^addrWidth`, so an address can never leave the array. BRAM-shaped semantics; read-first on same-cycle write; initialized memories emit a Verilog `initial` block → BRAM INIT, and `Reset()` reloads them so the sim models reconfiguration |

Three of Vivado's behaviours are respected *by construction* here rather than
by documentation.

**Writes merge.** Writes issued under several `If` branches become a single
write site with muxed address and data, because two write sites kill BRAM
inference even when they are provably mutually exclusive.

**The storage is declared and the read is checked against it.** A combinational
read of an array the synthesiser put in a block gains a cycle that no cycle
simulator models — the trap that rendered a garbage frame here once. It is an
elaboration error now, not a documented hazard.

**A write can reach part of a word.** `memWriteMasked` takes one bit per lane
and emits the byte-enable template, so a 32-bit memory with a 4-bit strobe is
one block RAM with per-lane write ports rather than read-modify-write logic
around a whole-word write. The alternative — banking the word into one array
per lane — is the same silicon at wide lanes and much worse at narrow ones,
where a 32-bit array with four byte-enables is a single primitive and four
8-bit arrays are four of them at a width that wastes most of each.

Where Warp 11 is ahead of the field surveyed is narrower and worth stating
exactly: **the read port owns its own latency.** `memReadPort` hands back
`read.data` *and* `read.through "name" signal`, which delays a carried signal by
the port's depth rather than by a number written into the design. Everywhere
else the caller adds a `RegNext` per carried signal with the latency spelled
into each one — right until the read changes, and then silently not. See
[below](#carrying-a-callers-data-through-a-memory-read), including the case
where a port is the wrong tool.

### FSMs

| HDL | Native support? |
|---|---|
| Chisel | Library helpers, no native syntax — manual `switch/is` |
| SpinalHDL | First-class `StateMachine`/`State` |
| Amaranth | First-class `m.FSM()` / `m.State()` |
| Clash | Mealy/Moore machines as functions |
| Bluespec | Implicit via guarded actions |
| Warp 11 | First-class `machine "st" [ Idle; Fetch; … ]` — states are values of an ordinary union |

```fsharp
let stage = machine "stage" [ Idle; Fetch; Decode; Execute; Writeback; Done ]

bnot (stage.Is Idle ||| stage.Is Done) ==> busy
stage.If Execute (fun () -> If (bnot stall) (fun () -> stage.Goto Writeback))
```

Register, width, encoding, decode and the checks all come off that one
declaration. Three things distinguish it from the field's versions rather than
merely matching them:

- **States are typed values, not strings.** SpinalHDL's are objects too;
  Amaranth's are strings (`m.next = "READY"`). Warp 11 cannot take the string
  route — a name used to *refer* to something that already exists is the one
  thing its naming rule makes unrepresentable — and the union turns out to be
  the better end of that constraint anyway: a typo is a compile error, and a
  state the machine was not given is refused at the `Goto`.
- **The meaning of a code reaches the debugger.** Elaboration is the only layer
  that knows code 35 is `RootInsertionScan`; it records that on the module, so
  the watch table and the waveform lane read `Execute (0x3)` rather than `0x3`,
  and a code no state was given reads `?`. One GEP cluster design
  self-describes 13 machines with their instance paths — `cl_br0_oe_st`, 39
  states — where before there were 13 registers holding numbers.
- **A state nothing transitions to is an elaboration error.** Dead logic that a
  block of `let sFoo = 7UL` cannot report. It found a real one on first contact
  (below).

The encoding is the declaration order, and `machineCoded` spells it out where
something outside the design fixed it — a retired state that left a hole the
remaining ones must not close. That matters because it makes conversion free:
all ten hand-encoded machines in the codebase were converted — the Mandelbrot
coalescer, GEP's karva compiler, breeder block, unit engine and **39-state
operator engine**, and the cluster's filler, occupancy, emit and selection
machines — with the emitted Verilog byte-identical at every step, including the
6.4 MB file the bitstream is built from.

Thinner than SpinalHDL's and Amaranth's in what it does *not* have: no
`onEntry`/`onExit` hooks, no nested or parallel sub-machines, no one-hot
encoding option. Those are real conveniences, and none of the ten machines here
wanted one. The pain it removed turned out to be two-sided: the repetition an
earlier version of this section named, and — the half that was not anticipated —
not being able to tell what state a design was in while watching it run.

### Buffering a stream

| HDL | |
|---|---|
| Chisel | `Queue(io.in, 8)` — depth, optional flow/pipe |
| SpinalHDL | `stream.queue(8)`, `stream.s2mPipe`, `m2sPipe` |
| Amaranth | `lib.fifo.SyncFIFOBuffered(width, depth)` |
| Warp 11 | `streamFifo "name" 8 s` |

First-word fall-through in all of them, because the contract needs `payload` and
`valid` together.

**Multiple sources answer one AR channel.** A Warp 11 slave's read side is a
register bank plus as many memory windows as the aperture has room for, each
owning a range, and the host cannot tell how many there are — one aperture, one
handshake. The channel states how long it waits (`answersAfter`) and refuses a
source that needs longer, which is the part that used to be a coincidence: RDATA
was a 0-cycle mux over registers *and* a 1-cycle window read, correct only
because RVALID happened to rise exactly one cycle after the AR accept.

The reason a mixture works at all is worth stating, because it is what makes the
arrangement cheap: `readWord` is **held** while RVALID waits, so a combinational
mux over it reads the same word on every cycle of the wait, and so does a memory
whose address has stopped moving. No source needs alignment registers — only a
guarantee that it is ready by the time the channel samples. Overlapping windows,
and registers buried under one, are elaboration errors; both are silent
otherwise, since the read mux would simply pick one and the host would get
plausible words from the wrong memory.

A channel that waits longer than one cycle opens a gap between accepting an
address and answering it, and in that gap RVALID is still low — so the ordinary
"am I idle" guard would wave a second address straight in on top of the first,
and with one held address and one RDATA the host would be answered twice with
whichever won. A busy flag holds ARREADY down across the gap. It costs nothing
at depth one, where there is no gap and the RVALID register is already the
flag, so a slave whose sources all answer in a cycle emits what it always did.

**Warp 11's FIFO picks its own storage from the depth**, and that is the part worth
explaining. Up to 64 the words live in LUTs and the head is a combinational read,
which is what makes fall-through free. Past it they live in a block and the head
becomes a synchronous read behind a two-slot skid — one slot would have to wait
for the consumer to take a beat before it could issue the read that replaces it,
which is correct and half the throughput.

Neither is named at the call site. Both hold exactly `depth` beats and both
sustain a beat per cycle, so `streamFifo "name" 8` and `streamFifo "name" 8192`
differ in where the bits sit and in nothing a caller can write down. A `Stream`
already hides latency, so the storage was never part of the contract — it only
looked like it was while the deep one did not exist.

The general rule this draws, and the one the other entries here leave to the
author: **code may assume a combinational read of something that is always LUTs**
— a register file, a small table, and `distributedMem` says so and emits
`ram_style = "distributed"` to hold the synthesiser to it — **and must not assume
one of anything whose storage depends on how big it got.** The second class is
what carries its latency in a handshake instead. Getting that backwards is
the combinational-read-of-a-block trap: a hidden cycle on silicon that
this repo's Sim and
Verilator both pass.

### Carrying a caller's data through a memory read

| HDL | |
|---|---|
| Chisel | `SyncReadMem.read(addr)`; delay the rest yourself with `RegNext` |
| SpinalHDL | `mem.readSync(addr)`; `Delay(x, 1)` for anything that must arrive with it |
| Amaranth | `lib.memory` read port; the caller adds its own pipeline registers |
| Warp 11 | `let read = memReadPort m addr` — then `read.through "tag" tag` |

Every entry here hands back a word one cycle late and leaves the caller to
remember what it asked for, which in practice means a register per carried
signal with the number **1** written into the design at each one. That is the
same objection this codebase raises against a stage that declares its latency:
the number is right until the read changes, and then nothing anywhere says
otherwise.

`through` delays by the port's depth rather than by a number, so the carried
signals follow the port. It is `withContext`'s bargain at a scale that does not
want a FIFO — no handshake, no backpressure, one register per carried signal,
and the caller names them so a waveform still reads the way it did.

**Where it deliberately does not apply**, because this is the more interesting
half: when the read register *is* a declared pipeline stage, it should stay one.
GEP's reciprocal divider registers five sibling signals by hand into a stage it
names `a3`, and the ROM's own read register is the sixth. There the latency is
not implicit — it is the structure, and a port would hide it. The rule that
falls out is about which of the two a site is doing: **carrying** wants the
port, **staging** does not.

### Carrying a caller's data through a slow stage

| HDL | |
|---|---|
| Chisel | put the context in the `Bundle` and thread it through the stage yourself; `Queue` + manual pairing, or an AXI-style `id` field |
| SpinalHDL | `Stream[T]` where `T` holds the context; `translateWith` before and after |
| Warp 11 | `withContext name depth operands results context stage s` |

The problem is the same everywhere: a divider takes operands and returns a
quotient, and knows nothing about the pixel or request the beat belonged to. The
usual answers are to widen the component so it carries a payload it never reads,
or to keep a shadow queue beside it and hope the orders line up.

**`withContext` puts it in a FIFO and hands it back paired with the result**, and
the stage is untouched:

```fsharp
let out = withContext "dv" 4 operands results context (divider "dv" 8) requests
// requests : Stream<(dividend, divisor) * ctx>
// out      : Stream<(quotient, remainder) * ctx>
```

`depth` is a **throughput** knob, not a correctness one — the context FIFO is
pushed and popped in lockstep with the stage's own accept and emit, so it cannot
mismatch; too shallow only holds the source off sooner.

It relies on the stage producing in the order it accepted, which is true of one
unit and false of a farm. So the farm carries context per lane:

```fsharp
// four dividers; every quotient still knows which request it is
Stream.farmWith "div" 4 2 operands results context (fun i -> divider $"dv{i}" 8) requests
```

**Still no tags**, which is the surprising part — a farm returns beats in
completion order, the case that usually forces one. But a farm owns both the
dispatch *and* the merge, so it knows which lane produced each beat; each lane
keeps its own context in its own FIFO and the merge interleaves beats that are
already paired. `farmWith` is literally `farm` of `withContext`, which is the
argument for having built the latter as a combinator.

A tag is needed only to route results *back* to independent clients, which is
`warpFu`. Three mechanisms, and a caller writes none of them:

| stage shape | how context rejoins | tag? |
|---|---|---|
| one in-order unit | `withContext` — a FIFO alongside | no |
| replicated (`farmWith`) | per-lane FIFO; the merge knows the lane | no |
| shared by independent clients | routed back by tag (`warpFu`) | yes |

### Streaming / ready-valid

| HDL | Primitive? |
|---|---|
| Chisel | `Decoupled[T]`, `Irrevocable[T]`, `Valid[T]` in stdlib |
| SpinalHDL | `Stream[T]`, `Flow[T]`, `Fragment[T]`, `>>` connect |
| HardCaml | Library FIFOs; `hardcaml_axi` for streaming |
| Amaranth | `lib.stream` (Amaranth-stream) |
| Warp 11 | `Stream<'p>` with typed payloads from a shared `Layout` and `Union2` sum types. A stream is a *value you apply a sink to*, so chains are ordinary function application and the ready chain runs backwards through forward composition: `streamOutput "out" (stage (streamMap bump (stage (streamInput "in" layout))))`. Operators: `streamStageFor`/`streamMap`/`streamMapTo`/`streamFifo`/`streamBroadcast`/`streamBalance`/`streamMerge2`/`streamMergeTree`/`streamConflate3`/`streamProbe`; `wormhole` + `wormholeOut`/`wormholeIn` for fan shapes (flat or clustered ~√N registered tree) |

**This is a strength rather than a gap** — see [`docs/streams.md`](streams.md). Two things have no direct analogue in the field surveyed:

- **The connect call carries the timing lesson.** The clustered fan builds the ~√N registered dispatch/merge tree that MandelPod needed to get from ~106 MHz to 166 MHz, so the next design gets it without a bitstream build and a timing report. Measured: at 104 lanes the generated tree's critical path has **zero logic levels** — pure route delay.
- **Exactly-once consumption is checked at elaboration.** A stream has one consumer; `checkStreams` counts drives of each ready net and refuses to emit a design where a stream was dropped or double-consumed. Dangling a stream is expressible, but only by saying so.

`Flow[T]` is valid-only: `streamToFlow`/`flowToStream`, `flowStage`, `flowMap`. The conversion back to a stream returns an `overflowed` term rather than swallowing it, because that is the one place a design silently loses data and the loss should be something the caller has to spend. Three sites that had been writing `1 ==> ready` by hand say so in the type now (the Mandelbrot pod's framebuffer write, the GEP cluster's burst response, `warpFu`'s single-client path), byte-identically. Still missing versus SpinalHDL: conditional routing, `Fragment`-style packetization, and Flow's CDC helpers — the last of which is the real reason Spinal has the type, and needs multi-clock support here first.

### Bus libraries (AXI/etc.)

| HDL | AXI etc.? |
|---|---|
| Chisel | rocketchip-diplomacy; community AXI libraries |
| SpinalHDL | AXI4, AXI4-Lite, AXI4-Stream, AHB-Lite, APB3, Wishbone, TileLink — all built-in |
| HardCaml | `hardcaml_axi` |
| Amaranth | `amaranth-soc` for SoC interconnect (CSR bus etc.); AXI via separate libs |
| Warp 11 | A **declarative register map** — a list of `pulseBit` / `rwReg` / `roField` / `w1cBit` / `roConst` / `rwWindow` entries — from which `axiLiteSlaveOf` synthesizes the W/R FSMs, register storage, multi-source IRQ OR and read mux, *and* `regMapRsLines` emits the Rust layout the driver imports. `axiMasterReader` (multi-beat INCR bursts, `maxOutstanding`), `axiMasterReaderBurst`, `axiMasterWriter` (single- and multi-outstanding ring-buffer modes) |

Warp 11 doesn't ship higher-level fabrics (TileLink, Wishbone, etc.) or an AXI4 *slave* beyond AXI-Lite, but the AXI-Lite + AXI4-master pair covers KV260 deployments and is what every demo runs on — including GEP's sustained burst feed and Mandelbrot's 16-px-per-beat framebuffer egress.

The **generated seam** is the piece with the least prior art among the embedded HDLs: the register map is written once, and both the RTL and the host's constants are outputs of it, so offsets and bit positions provably cannot drift. LiteX is the closest analogue in the wider field — it generates CSR maps and host-side access from the SoC description — and is more general, being a whole SoC framework. The difference is which direction the artifact flows: here a single design-level declaration produces both sides, and the generated file is committed, so a review sees the seam move.

### Formal verification

| HDL | Support |
|---|---|
| Chisel | `assert`/`assume`/`cover`; FIRRTL passes propagate to SVA |
| SpinalHDL | SymbiYosys / yosys-smtbmc integration |
| HardCaml | `hardcaml_verify` (SAT) |
| Amaranth | Yosys + SBY |
| Clash | Type system catches a lot at compile time |
| Bluespec | Strong typing + recent commercial formal partnerships |
| Warp 11 | `assert` only — no model checker |

`assertThat cond "message"` is an IR primitive: checked every cycle by the simulator (opt-in, since a claim costs what its expression costs), and emitted into a `translate_off` region that synthesis skips and Verilator compiles. So a claim written in a design is checked in *both* worlds on the differential's random stimulus, which is most of the value of assertions without any solver involved.

What is still missing is the solver, and with it `assume` — which constrains the environment so a model checker does not report impossible counterexamples, and which has no meaning in simulation, so it was deliberately not added. Without one, the Verilator differential remains a *behavioral* check (the F# sim, the emitted Verilog and Verilator all agree) rather than a *property* check (this circuit never deadlocks, under all inputs). That gap is the field's clearest remaining advantage over Warp 11.

One thing the feature demonstrated on arrival: the first assertion written into the catalog was `not (255 < r)` on an 8-bit register — constant-true, therefore vacuous, therefore worthless — and Verilator's `CMPCONST` warning caught it. Vacuity is the failure mode that makes a green assertion or formal run meaningless, and it is easy to write by accident.

### Simulation

| HDL | Built-in simulator | External |
|---|---|---|
| Chisel | ChiselSim (Treadle replacement) | Verilator integration via ChiselTest |
| SpinalHDL | SpinalSim — cocotb-like, fork/join, threading | Verilator-backed |
| HardCaml | First-class OCaml simulator | High-speed C++ backend |
| Amaranth | Built-in Python event-driven sim | — |
| Warp 11 | Cycle-based interpreter that **compiles the design at construction** — names become slot indices, every expression becomes a thunk with its widths and masks folded in, a tick allocates nothing. Narrow (≤64-bit) values on `uint64`, a parallel `BigInteger` path for wider. Differential against Verilator, narrow and wide | Verilator, as the oracle rather than as the simulator |

This is a Warp 11 strength, in two specific ways.

**The differential is a discipline, not a feature.** Every design in the catalog is driven through the F# simulator and through Verilator's execution of the emitted Verilog under the same seeded stimulus, and any divergence fails. It is the bug oracle the project actually leans on: it has caught emitter bugs on first contact repeatedly, and it is what makes a simulator rewrite safe — the compiled simulator above replaced a tree-walking one with an 11× speedup and the evidence that it was correct was `ALL DIFF PASS`, GEP's 22 checks, and a Mandelbrot render byte-identical at the same 12,933 cycles.

**Fast enough to be a design tool.** GEP's shape probes sweep breeders × lanes × fillers *in simulation*, with a per-shape hardware-vs-software divergence oracle, and pick the bitstream shape before Vivado runs at all. Measured after the compile pass: 2,468 cycles/s on the 104-lane Mandelbrot pod, the largest design here.

History is recorded per *cycle* into a bounded ring — inside the tick loop, not at snapshot rate, which is the difference between a waveform and a sparse sample — and comes out either as a waveform lane in the debugger or as **VCD** for GTKWave and Surfer. The ring trades depth against width inside a fixed budget: the watch list gets 8,192 cycles, every signal of a 4,245-signal design still gets nearly 2,000.

What is still narrower than the field: the trace holds what you chose to record before the run, and the in-app view pages at one pixel per cycle rather than offering zoom and search — which is why the VCD path exists at all.

### Interactive debugging

| HDL | What you get |
|---|---|
| Chisel | Treadle's REPL gave incremental execution and combined with a Scala debugger for step-through; ChiselSim replaced it. Breakpoints are on *testbench* code |
| SpinalHDL | SpinalSim is Scala, so an IDE debugger breaks in the testbench and can read DUT signals from there |
| Amaranth | Python sim, so `pdb`; signal history goes to a waveform file |
| cocotb (any HDL) | Python testbench, so `pdb` — the field's most common answer |
| HGDB (research, Chisel/others) | The most ambitious: source-level breakpoints mapped back to *generator* variables, conditional breakpoints, signal deposit, forward/backward debugging, driven against commercial simulators |
| Warp 11 | A **step-through debugger over the elaborated design** (Avalonia + FuncUI): filterable watch list over every signal at every hierarchy depth, editable inputs, memory windows that page, a per-cycle waveform lane with a cursor, VCD export, state-machine registers shown as their state names, and **breakpoints written as expressions over the design's own signals** — `count == 0x40 && !valid`, `store[3] == 0x55`, `signed(zx) < 0` |

The capability is not novel — HGDB does more of it, and "put a breakpoint in your Python testbench" is a decade old. Three things are unusual in combination:

- **Breakpoints are the design's own expression language.** `generation == 1000` parses into the same `Expr` IR that the design's assignments are made of, and compiles through the same evaluator, so testing one costs a single thunk call per cycle and there is no second semantics to keep in sync. `>` is `Lt` with its operands swapped; nothing was added to the IR to support debugging.
- **It attaches to a running application.** Only one thing may call `Tick`, so the run loop lives in a session object and every window posts commands into it. The Game of Life view runs the *real elaborated 64×64 RTL* in simulation, and its Debugger button opens a debugger on the same session — set `generation == 1000`, press Flat out, and both windows stop together on the same cycle. Debugging the design an application is driving, without a separate testbench, is the part the field mostly does not offer. The same seam is how a project points the debugger at its own designs — `dotnet run --project Warp11.Gep -- debug operator-engine` opens on the 39-state engine — rather than a shared catalog reaching into every example to list them.
- **A state machine reads as its states.** `machine` records what each code means at elaboration, so the watch table and the waveform lane show `MutationGate (0x0b)` where the register holds 11, at any hierarchy depth (`cl_br0_oe_st`). The alternative everywhere else is a note beside the monitor.
- **It ships with the toolkit.** No simulator integration, no debug symbol table, no second tool to install.

Against that: no backward stepping and no source-level mapping to the F# that generated a signal — HGDB does both. History being opt-in is not on that list: the run is deterministic, so re-running with more recorded is exact and cheap, and tracing everything by default would cost simulator throughput and disk to answer a question a second run answers for free.

### Elaboration-time checks

Not a feature other HDLs lack so much as a policy Warp 11 keeps arriving at from the same direction: every one of these exists because a real design was silently wrong, and the fix was to make that wrongness unrepresentable rather than documented.

| check | the bug that bought it |
|---|---|
| **One driver per signal per level** — a second `==>` at one scope is an error, not last-connect-wins | a discarded first assign is silent through elaboration, lint *and* synthesis, because the emitter writes one `assign` per target, so Vivado's multi-driver check never sees it |
| **One declaration per name**, instance staging wires included | an instance named `b` beside a stream output named `b_low` declared the same net twice — an instance's staging wires live in the *parent's* namespace — and emitted a port redeclared as a wire and self-assigned. The stream checker *passed*, because the self-assign counted as the ready net's one driver |
| **Width checks gate emission** | a truncating assign reached Verilog and only Verilator objected — the check existed but only reported |
| **Reserved-word check** | six live designs were emitting Verilog that would not parse |
| **Streams consumed exactly once** | a dropped stream is a deadlock with no error anywhere |
| **Every state of a machine has a way in** | the unit engine's `WAITREMOTE` is reachable only where there is a divide pod to offload to, so in the per-lane build it was dead logic — invisible while the states were a block of `let sFoo = 7UL`, because an unused number reads exactly like a used one |

The generalization is the interesting part: each of these is a case where the *emitted Verilog was legal* and therefore no downstream tool could complain. That is the class of bug an embedded DSL is uniquely positioned to catch, because it is the only layer that still knows what the author meant.

### Tooling

| HDL | LSP | Formatter | Package manager |
|---|---|---|---|
| Veryl | Yes | Yes | Yes |
| Chisel | Via Metals (Scala) | scalafmt (Scala) | sbt |
| SpinalHDL | Via Metals | scalafmt | sbt |
| Amaranth | Via Python tooling | black/ruff | pip |
| Warp 11 | Via F# LSP — Ionide, Rider (general) | Fantomas (general) | NuGet / `dotnet` (plus Cargo for the runtime half) |

Veryl's commitment to dev tooling is unusual. Warp 11 inherits the .NET ecosystem's tooling on the DSL side and Cargo's on the runtime side; neither is HDL-aware, which is the standard trade for an embedded DSL. The cost of the two-language shape shows up here honestly: two toolchains to install before anything runs.

### Generated output quality

| HDL | Output |
|---|---|
| Chisel | FIRRTL → Verilog (machine-generated, machine-readable) |
| SpinalHDL | Verilog/VHDL, generally clean and readable |
| Veryl | SystemVerilog, designed for human review |
| HardCaml | Verilog, fairly clean |
| Warp 11 | Verilog, multi-module, hierarchical, width-typed literals throughout |

Warp 11's Verilog quality is comparable to SpinalHDL's: multi-module hierarchical output, transitive clk/rst threading, no `genvar` salad or machine-generated naming, and names that survive from the design to the emitted file, which is what makes the debugger's watch list and a Vivado timing report talk about the same signals. Reviewable by a human.

One honest gap: there is no automated `verilator --lint-only -Wall` sweep across the catalog. The differential compiles every design under Verilator, which catches anything that will not build, but the stricter warning pass is run by hand.

### FIRRTL interoperability

Everything above compares languages. This compares what they can *hand to
something else*, which is a different question and one the field answers
lopsidedly.

| HDL | emits `.fir` | reads `.fir` |
|---|---|---|
| Chisel | yes — it is the compiler's own IR | no |
| SpinalHDL, Amaranth, HardCaml, Veryl, Clash, Bluespec | no | no |
| Warp 11 | yes — `Warp11/Firrtl.fs` | **yes — `Warp11/FirrtlImport.fs`** |

The emit column is unremarkable: Chisel's whole backend is FIRRTL, and the
others each chose to own their path to Verilog. The read column is where this
sits alone, and it is worth being precise about why, because "reads FIRRTL" is
true of `firtool` too and means something else there. A compiler reads `.fir` to
*lower* it. Warp 11 reads it back into the same IR the DSL elaborates into — so
an imported design is indistinguishable from one written here, and goes through
the same simulator, the same debugger and the same emitter.

What that is actually for, in order of how much it has paid:

1. **A second opinion on the emitter.** The differential's third leg runs
   `firtool` on the exported `.fir` and asserts the Sim's trace against
   *someone else's* Verilog. Both other legs come from our emitter, so a bug in
   it is invisible to them by construction — this one found a memory write
   firing during reset.
2. **Foreign input as a judge of the reader.** `hdl/firrtl-foreign/` is
   hand-written `.fir` that `firtool` also compiles, so a disagreement is ours
   and provably not a matter of taste. It found `sub` on two `UInt` operands
   being read as signed — which a round trip could never have found, because
   export and import were wrong together.
3. **A stated semantics.** The IR's operations mean what the FIRRTL spec says
   they mean, width rules included, and that is checkable rather than asserted.

The honest limits, all of them:

- **Low FIRRTL only.** Bundles and vectors are not read, which is the same
  choice as the [Typed I/O](#typed-io--bundles) section's: `firtool --ir-fir |
  circt-translate --export-firrtl` scalarizes them into exactly the subset that
  is read, and the one construct that does not survive that is dynamic indexing
  into a vector (`multibit_mux`).
- **Preloaded ROM contents cannot be exported.** `.fir` has no initialiser, so
  four designs are refused by name rather than silently emitting an empty
  memory.
- **A lane-masked memory exports but does not come back.** A FIRRTL write
  port's mask mirrors its data type, so a scalar carries one mask bit and only a
  vector carries one per lane; a masked memory therefore exports as
  `data-type => UInt<8>[4]`, connected lane by lane, with reads reassembling the
  word. `firtool` compiles that and the third leg passes on it. The *reader*
  refuses a vector memory by name, because putting a masked write back together
  from per-lane connects is inference rather than parsing — the connects need not
  be adjacent or complete — and a write reconstructed wrong is circuit behaviour
  changed in silence. Masked designs are excluded from the round-trip the way
  preloaded ROMs are.
- **`ram_style` does not survive a round trip.** FIRRTL describes a circuit, not
  how to build one. Behaviour is unaffected, so the export carries the memory
  and drops the attribute; the round-trip check compares with it stripped.
- **We do not depend on `firtool`.** Our emitter stays the default, and the
  third leg is behind `FIRTOOL_LEG=1` because CIRCT is a 200 MB pinned download.
  FIRRTL is an interchange format and a discipline here, deliberately not a
  build dependency.

`hdl/README.md` has the
subset table enumerated against the spec.

### Scaling, replication and stall telemetry

Two capabilities the field mostly leaves to the user, treated here as first-class because every accelerator in the project needed both. The [WarpCPU section](#the-warpcpu-shape--an-architectural-pattern-not-a-language-feature) covers the architecture; this is where it sits against the field.

| HDL | Replication / fan | In-design telemetry |
|---|---|---|
| Chisel | `Vec` + `Arbiter`; the fan topology is yours to build | Hand-written counters |
| SpinalHDL | Stream fan-out/fan-in with arbitration; explicit pipelining API | Hand-written counters |
| Amaranth / HardCaml / Clash | Manual | Hand-written counters |
| LiteX / LiteScope | — | **LiteScope**: an embedded logic analyzer with configurable triggers, capture into FPGA memory, upload to a host, VCD/sigrok/CSV export |
| Warp 11 | Lane count is **one integer**: the fan builder instantiates N copies, dispatches into them and merges their results, choosing flat or a ~√N registered tree by size. `MANDEL_NUM_LANES = 104` is the whole knob for filling 100% of the chip's 1,248 DSPs | `streamProbe` puts two saturating counters on any link — `blocked` (`valid && !ready`) and `starved` (`ready && !valid`) — and `streamReport` walks a design for them. The same registers are peeked in the simulator and read over AXI-Lite on the board |

The distinction from LiteScope is worth being precise about, because LiteScope is the better tool for its own job. It is a *logic analyzer*: you trigger, capture a window of samples into BRAM, and upload it to look at waveforms. Warp 11's counters are *aggregate occupancy* — no window, no trigger, no waveform, just "this link blocked 17,749 times". That is much less information, and it is available continuously, in simulation, at zero upload cost, which turns out to be the shape that answers "where is the bottleneck" in one `tick` and one `peek`.

What that buys is a development loop: telemetry, not intuition, has re-ranked GEP's roadmap three times (the table in [§3 below](#3-telemetry-as-a-first-class-output-readable-in-simulation)), and the shape sweep that picks a bitstream's breeder × lane × filler counts runs entirely in simulation before Vivado starts.

### Board and host integration

The part of the loop the other entries mostly consider out of scope — and, for a project whose stated goal is the distance from an idea to a thing running on a board, the part that matters most.

| HDL | From elaborated design to a program talking to silicon |
|---|---|
| Chisel / SpinalHDL / HardCaml / Clash / Veryl | Emit RTL; the vendor flow, the register map and the driver are yours. Register offsets are agreed by hand between HDL and host code |
| Amaranth | Same, unless you adopt **LiteX**, which is the field's strongest answer: a full SoC framework that generates the interconnect, a CSR map, and host-side access (plus LiteScope for capture) |
| Warp 11 | One declarative register map → the AXI-Lite slave *and* the Rust layout the driver imports. A Rust runtime (`RegisterWindow` over `/dev/mem` mmap, `/dev/uioN` with blocking IRQ, udmabuf contiguous buffers, a GDMA bulk-read path worth 12–16× on large reads). Board daemons publish over Zenoh to a desktop GUI. Emit is a `dotnet run -- hardware` mode that writes the RTL *and* the layout together; the board binary is a cross-compiled `cargo build` (aarch64 musl); `xmutil` switches which bitstream is loaded |

The piece with the least prior art is what the runtime's `RegisterWindow` trait makes possible: **the same driver code runs against silicon and against the simulator**, because a register window can be backed by `/dev/mem` *or* by the cycle simulator driving the slave's `s_axi_*` pins through real five-channel handshakes. It goes one step further than that across the language seam — `FsSimWindow` spawns the F# project in a `simserve` mode and speaks a line protocol to it, so a Rust driver exercises an F#-elaborated design with **no board and no bitstream**, through the same handshakes it will use on hardware.

That is what makes a host-side bug cheap: the AXI rehearsal that replays every host action as a real transaction, reading frames back out of a fake DDR, is the same sequence the board driver runs, and it runs in a test.

Two honest limits. This is one board (KV260 / ZU5EV) and one deployment style — a Linux-class PS driving a PL slave — so "board integration" here means that shape done well, not a portable board-support story. And the vendor flow is still Vivado, driven by committed Tcl; nothing about place-and-route is abstracted.

## The WarpCPU shape — an architectural pattern, not a language feature

Everything above compares *primitives*. The most distinctive thing Warp 11 has
produced isn't a primitive, it's a repeatable **architecture** for accelerators,
arrived at by measurement on two unrelated workloads (Mandelbrot rendering and
genetic-programming search) and now the house shape for both. Calling it out here
because it's the part a prospective user would actually adopt, and because it's
the axis on which Warp 11 differs most from the field — the other HDLs give you
better primitives and leave the architecture to you.

**None of the three ingredients is novel on its own.** Barrel (C-slow) processors
are CDC-6600-era; Sun's Niagara shared one FPU across 8 barrel cores and the GPU
SM shares ~4 special-function units across ~32 lanes; performance counters are
older than that. The claim is narrower and, I think, defensible: *no surveyed HDL
makes this shape cheap to author, cheap to instrument, and cheap to re-size* — so
in practice you get it once, by hand, if you already knew to want it.

### 1. Barrel threading as the default answer to a slow cone

The pattern: pipeline the datapath deeply, then interleave N independent work
items round-robin so each item's next operation enters exactly as its previous one
exits. Latency is hidden, the pipe stays full, and the clock is set by one stage
rather than the whole cone.

The governing rule, worth stating because it decides every functional-unit design
in both projects: **latency costs threads, not throughput** — a fully pipelined
unit accepts work every cycle however deep it is, so pad short operators to match
the deepest and add threads. **Initiation interval > 1 costs throughput and
threads cannot fix it** — a structurally-occupied unit blocks the shared execute
slot, and a data-hazard cure doesn't touch a structural hazard.

Measured, on the same 4 DSPs:

| GEP evaluation datapath | clock (OOC, xck26) |
|---|---|
| combinational engine (~45 logic levels) | 80 MHz |
| ALU-pipelined (`GEP_ALU_PIPELINE_LATENCY = 4`) | 275 MHz |
| + registered SOURCE stage | **317 MHz** |

Issue→writeback latency is 7, hidden by 8 threads. The predicted ~4× clock, at
identical DSP cost. Mandelbrot's `mandelBarrelLane` is the same shape around a
4-stage `mandelStep` cone, with an elaboration check that `nThreads >
MANDEL_STEP_LATENCY`.

The pattern's *negative* result is equally load-bearing and is why it's stated as
a principle rather than a preference: GEP's breeder is the one pod that stayed a
single-item 39-state FSM, and it is **11.2k LUT — 47% of the whole design — at
~63% busy, pinning the PL clock at ~130 MHz while the threaded lanes clear
252 MHz**. Monolithic single-item logic costs idle area *and* dead Fmax. (Honest
caveat: threading needs cross-item parallelism. The lanes have it — independent
fitness cases. The breeder's operators are sequential within one offspring and
mutate a buffer in place, so it can only be threaded *across* offspring, which is
genuinely hard and still open.)

| HDL | Support for this shape |
|---|---|
| SpinalHDL | Closest: an explicit pipelining API (`Stageable` / stage areas) plus automatic register-retiming phases. Aimed at classic in-order pipelines rather than thread interleaving. |
| Bluespec | A different answer to the same problem — guarded atomic actions let the compiler schedule concurrent rules, so keeping a datapath busy is the compiler's job. Arguably the most principled approach in the field. |
| Chisel | Manual. Rocket/BOOM contain hand-built pipelines; nothing in the stdlib generalizes them. |
| HardCaml / Amaranth / Clash / Veryl | Manual. |
| Warp 11 | Manual too — **but** the surrounding machinery (streams, replication, telemetry, a simulator fast enough to sweep shapes) is what makes it practical, and the pattern is documented with its measurements. |

**Being honest about the gap:** there is no `barrel(threads, body)` primitive.
Mandelbrot's lane and GEP's engine share a *pattern*, not code. Two instances is
enough to describe a shape and not enough to abstract one — the project's own rule
is to lift to `stdlib` on the second real user, and the two here differ enough
(thread-state layout, refill discipline, egress) that the useful common core isn't
obvious yet. A third barrel design is what would settle it.

### 2. Scaling as a number you edit

The WarpCPU shape is a **rate-matched hierarchy of work queues with barrel
processors at the leaves**, where every stage type scales independently — including
to zero — as an elaboration decision:

```
fillers ──queue──▶ breeders ──queue──▶ eval lanes ──interleave──▶ threads ──issue──▶ FUs
```

Each ratio is sized by `service_time × frequency`. Three properties fall out:

- **Replication is one integer.** `replicate(n, template, input, output)`
  instantiates n copies, dispatches into them, and merges their results, so lane
  count is a number you tune. `MANDEL_NUM_LANES = 104` is the whole knob for
  "fill 100% of the chip's 1,248 DSPs" — each lane is a fixed 12-DSP quantum.
- **The fan gets the right topology for free.** `Shape.Auto` builds the ~√N
  registered dispatch/merge tree, which is what took MandelPod from ~106 MHz to
  166 MHz, so scaling up doesn't quietly reintroduce the fan-out wall.
- **Per-operator placement is a knob, not a redesign.** An operator can be
  **absent** (not in the function set → elides from the bitstream entirely,
  e.g. `withDiv = false`), **resident** (one instance per lane), or **pooled**
  (`warpFu` wraps any fixed-latency II=1 unit into an N-requester shared pod
  *without modifying the unit* — round-robin arbiter → the unchanged core → a
  matched tag delay-line → response demux). Moving an operator is a rebuild.

And the measured rule for choosing, which is the sort of thing you only learn by
building it both ways: **pooling a cheap FU is a mistake.** GEP's divider is
697 LUT — 5.6% of the design for all 8 lanes — and pooling it *cost 44–118%
throughput* while saving nothing on the binding resource, because dividers are
DSP/BRAM-heavy and both were abundant. Pooling pays only for units that are big
**and** rare, which is what the transcendentals will be.

Chisel's rocketchip-diplomacy is the field's most developed answer to
parameterized assembly — negotiated parameters across a hierarchy — and is more
general than anything here. The difference is scope: diplomacy negotiates *bus
and address* parameters for SoC construction; this is about sizing *compute pods*
against each other from measured service times.

### 3. Telemetry as a first-class output, readable in simulation

The part with the least prior art in the surveyed HDLs. Counters are authored in
HDL, so **the same registers serve both worlds**: the simulator peeks them while
tuning, and the board reads them over AXI-Lite through the generated layout.

- `wormhole(..., instrument = true)` puts two saturating counters on any link —
  `blocked` (`valid && !ready`, consumer too slow) and `starved`
  (`ready && !valid`, producer too slow). That pair localizes a bottleneck to a
  specific link and names the fix.
- GEP's cluster exposes ~20 aggregate counters plus a **per-breeder and per-lane
  busy-cycle block**, generated by a loop over `roField(...)`, so utilization is
  visible per unit rather than in aggregate.

What this buys is a development loop the field doesn't offer. Finding where a
64-lane pod stalls conventionally costs a synthesis run and a timing report;
here it costs a `tick` and a `peek`. Concretely, telemetry — not intuition — has
re-ranked GEP's roadmap twice:

| reading | what it forced |
|---|---|
| fill 95.4% busy, lanes 75%, one breeder at 2.3% | the wall was the serial dispatcher, not lane count — dispatch de-serialization jumped the queue ahead of FU scaling |
| after that fix: fill 59.0%, lanes 96.9% | the wall had *moved* to eval capacity, so lane scaling paid again |
| flat 71 cyc/offspring across 6×12 and 8×16, fill saturated ~77%, breeders 29–39% idle | a single dispatcher cannot feed 6–8 breeders; warping it restored linear throughput-per-area (8×16 went 71 → 40) |

The last row came from `runStreamProbe`, which sweeps breeders × lanes × fillers
**in simulation**, with a per-shape hardware-vs-software divergence oracle, and
picks the bitstream shape before Vivado runs at all. That is the payoff of a
cycle-based simulator fast enough to be a design tool rather than only a check —
and of instrumentation that exists in the design rather than in the testbench.

Two caveats on the claim. Every HDL can hand-write a counter; nothing here is
unreachable elsewhere. And this is measured on one chip (KV260 / ZU5EV) by one
developer, so the *thresholds* (`FAN_FLAT_MAX`, the pooling rule, filler counts)
are calibrated to that target, not established generally.

## Where Warp 11 sits

**Strong at:**
- **The whole loop, on real silicon.** Three non-trivial accelerators on a $300 KV260, each authored entirely in the DSL with Vivado only as the place-and-route backend:
  - [**Mandelbrot**](../hdl/Warp11.Mandelbrot/README.md) — 1400×800 / 256 iterations in **4.30 ms (261 Mpx/s)** — 715,938 cycles — at 104 barrel lanes filling **100% of the chip's 1,248 DSPs** at 166.67 MHz. Beats a vectorized numpy on an M4 Max end-to-end by 128×, and DuckDB by 262×.
  - **GEP symbolic regression** — the *whole* generation loop (selection, breeding, Karva compilation, fitness) in fabric, bit-exact against a software twin. Solved Feynman I.12.2 exactly on silicon; the cluster runs 512/512 offspring bit-exact at 0.85 µs/offspring.
  - [**Game of Life**](../hdl/Warp11.GoL/README.md) — 64×64 at 166.67 MHz with a k=3 unroll: **500M generations/s**, CONFLATE/N=3 triple-buffered into PS DDR for tear-free display.
- **Board and host integration as a first-class concern.** One declarative register map produces the slave *and* the driver's constants; the same driver code runs against `/dev/mem` and against the simulator, across the language seam. [Section above.](#board-and-host-integration) The caveat is the second toolchain, and it is smaller than it sounds: designing, simulating, debugging and emitting Verilog need .NET alone — Rust enters only when you deploy to a board. Every entry here needs a host-language toolchain plus the vendor flow, so the marginal cost is one more, paid at the point you have hardware in hand rather than at the front door. Against the HDLs surveyed that is the price of a capability they do not offer at all; against **LiteX**, which generates a CSR map and host-side access from Python end to end, it is a real comparative cost.
- **The step-through debugger.** Watch lists, memory windows, breakpoints written as expressions over the design's own signals, and state machines that read as their state names — attachable to the session an application is already driving. [Section above.](#interactive-debugging)
- **The stream connect layer.** One connect call for every fan shape, with the measured timing fix (~√N registered tree) chosen by size, and exactly-once consumption checked at elaboration. See [`docs/streams.md`](streams.md).
- **The WarpCPU accelerator shape** — barrel-threaded compute pods, rate-matched and independently scalable, with in-HDL telemetry that reads the same in simulation and on silicon. Not a language feature and not novel in its parts, but the surrounding machinery makes it cheap to author and re-size, which is what the other HDLs leave to you. [Section above.](#the-warpcpu-shape--an-architectural-pattern-not-a-language-feature)
- **The differential oracle as a working discipline.** F# simulator ↔ emitted Verilog ↔ Verilator agree cycle-by-cycle across every design, every run. It has caught emitter bugs on first contact repeatedly, and it is what makes large internal changes (an 11× simulator rewrite) safe to make at all.
- **Correctness that is unrepresentable-by-construction rather than documented.** One driver per signal, one declaration per name, width checks gating emission, reserved words rejected, streams consumed exactly once — each from a real bug that was legal Verilog. [Section above.](#elaboration-time-checks)
- **One numeric layer over a width-only IR.** Width, fraction bits and signedness live in a single format, so signed and unsigned integers and signed and unsigned fixed point are one type, and the five sign-dependent primitives are chosen by the format rather than by the call site. A Q4.28 × Q4.28 product is Q8.56 by construction, and a format mismatch is an elaboration error rather than a silent rescale. This was a units-of-measure type until 2026-08-15, when the measure was retired in favour of a format record checked at elaboration; the retirement emitted byte-identical Verilog across all six design projects.
- **A domain library at all.** The audio chain — biquad EQ with the RBJ cookbook, FIR, compressor, limiter, gain, tone generation, I2S in and out — is the kind of thing the field leaves to application code. SpinalHDL's `spinal.lib.graphic` is video *timing*, SpinalCrypto is a separate companion repo, and Jane Street's JPEG decoder is a standalone project; nobody ships DSP blocks in-library. This is an axis rather than a scoreboard — nothing here says the others should — but if you want working, oracle-tested signal processing rather than the parts to build it from, this is unusual.

**Weak at:**
- **No model checker.** `assert` exists and is checked in simulation and under Verilator, but nothing proves a property for *all* inputs, and `assume` — which a solver needs and simulation cannot use — is deliberately absent until one arrives. The field's clearest remaining advantage.
- **No multi-clock support.** Single implicit clock everywhere. Real CDC-heavy designs would need this.
- **Limited hardware support**, on both edges at once. One board: everything here is measured on a KV260 with a Linux-class PS driving a PL slave, and nothing claims portability beyond it. And a short list of things to plug into: AXI4-Lite (from the register map), AXI4 master read and write, and I2S in and out — where SpinalHDL ships AXI4/AXI4-Lite/AXI4-Stream, AHB-Lite, APB3, Wishbone, TileLink and BMB plus UART/I2C/SPI/JTAG, and LiteX brings DRAM, Ethernet, PCIe and SATA controllers. This is about the hardware edges, not the library as a whole — the audio chain and the stream connect layer are deep.
- **No `BitPat`-style decode tables.** Chisel's `decode`/`BitPat` turns a truth table of don't-care patterns into minimized logic; here a decoder is written as the slices and muxes it is. The gap is narrower than it sounds, and for a specific reason: `BitPat` pays for encodings you did not choose — RISC-V, x86 — where the fields are scattered and the patterns overlap with don't-cares. Every decoder in this repo decodes an encoding it designed itself, so the property needed is a field and the decode is a slice (GEP's karva compiler: "arity comes from the opcode's class bits, terminal/constant from bit 5 — no ROMs, pure slicing"). The gap becomes real the day something implements an ISA it does not own.
- **No conditional stream routing, no `Fragment`-style packetization.** `Flow[T]` exists, but without multi-clock support it is missing the job it does in SpinalHDL: a one-direction handshake is far easier to cross clock domains with than a two-direction one, and that is what `FlowCCByToggle` is for.

**At parity with the field:**
- Typed IO / bundles, and ahead on call-site invariance and Q-format typing.
- Memory primitives (BRAM-shaped, clean, Vivado infers correctly).
- Multi-module hierarchy + Verilog emission quality.
- Ready/valid streaming and arbitration.

The WarpCPU shape is documented in depth where it is built — the barrel
datapath and the pod/wormhole cluster in
[`Warp11.Gep`](../hdl/Warp11.Gep/README.md), the barrel-threaded lane in
[`Warp11.Mandelbrot`](../hdl/Warp11.Mandelbrot/README.md). The stack's own docs
are [`hdl/README.md`](../hdl/README.md) (the DSL and everything above it) and
[`runtime/README.md`](../runtime/README.md) (the runtime).

Sources:
- [Barrel processor (CDC 6600 lineage)](https://en.wikipedia.org/wiki/Barrel_processor) and [C-slow retiming](https://en.wikipedia.org/wiki/C-slowing) — the prior art the barrel lanes are an instance of
- [UltraSPARC T1 (Niagara)](https://en.wikipedia.org/wiki/UltraSPARC_T1) — 8 barrel cores sharing one FPU, the shared-FU precedent
- [Chisel - chipsalliance/chisel](https://github.com/chipsalliance/chisel)
- [Chisel docs](https://www.chisel-lang.org/docs)
- [SpinalHDL docs](https://spinalhdl.github.io/SpinalDoc-RTD/master/index.html)
- [SpinalHDL Stream library](https://spinalhdl.github.io/SpinalDoc-RTD/master/SpinalHDL/Libraries/stream.html)
- [HardCaml on GitHub](https://github.com/janestreet/hardcaml)
- [Jane Street: Growing the Hardcaml toolset](https://blog.janestreet.com/growing-the-hardcaml-toolset-index/)
- [Amaranth language & toolchain](https://amaranth-lang.org/docs/amaranth/latest/intro.html)
- [Amaranth on GitHub](https://github.com/amaranth-lang/amaranth)
- [Clash compiler](https://clash-lang.org/)
- [Bluespec Compiler (BSC)](https://github.com/B-Lang-org/bsc)
- [Bluespec - Wikipedia](https://en.wikipedia.org/wiki/Bluespec)
- [Veryl language](https://veryl-lang.org/)
- [Veryl paper (arxiv)](https://arxiv.org/pdf/2411.12983)
- [LiteScope — embedded FPGA logic analyzer](https://github.com/enjoy-digital/litescope) and [using it to debug a LiteX SoC](https://github.com/enjoy-digital/litex/wiki/Use-LiteScope-To-Debug-A-SoC) — the closest prior art for in-design telemetry and generated host-side access
- [HGDB: Bringing Source-Level Debugging Frameworks to Hardware Generators (arxiv)](https://arxiv.org/pdf/2203.05742) and [libhgdb](https://pypi.org/project/libhgdb/) — the most ambitious hardware-debugger work, and more capable than Warp 11's on every axis except being packaged with the toolkit
- [Treadle (Chisel's interactive simulator REPL)](https://github.com/chipsalliance/treadle) — prior art for step-through simulation from a host-language debugger
- [Amaranth simulator docs](https://amaranth-lang.org/docs/amaranth/latest/simulator.html)
