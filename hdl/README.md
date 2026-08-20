# hdl — the F# side

An F# spike of the Warp 11 DSL, built 2026-08-01 to put measured evidence under
a measured scorecard, which until then was entirely research and judgment.

**This is the DSL direction (Jason, 2026-08-03)** — it graduated from spike the
night its pod ran bit-exact on the KV260. The runtime half stays
Rust (`runtime/core`, `host` — the 2026-07-30 verdict, which governs the
runtime only); Kotlin `:core` and the Rust `hdl` crates stand as evidence.
This file says how to run this side.

**New here? Start with the [tutorial](Warp11.Tutorial/doc/counter.md)** — 34
small designs, each with a page, in a debugger you can step. `Warp11.GoL/` is
the worked example that goes all the way to a bitstream and a Rust driver (the
library; `Warp11.GoL.App/` is the runnable head).

## Running it

The .NET 10 SDK lives at `~/.dotnet` and is not on `PATH`:

```sh
export PATH="$HOME/.dotnet:$PATH"
dotnet run --project Warp11.Designs/Warp11.Designs.fsproj                      # demo dump + living checks
dotnet run --project Warp11.Mandelbrot/Warp11.Mandelbrot.fsproj -- mandel /tmp/mandel.ppm   # render the pod
dotnet run --project Warp11.Gep/Warp11.Gep.fsproj                              # the GEP suite (22 checks)
dotnet run --project Warp11.Tutorial.App                                       # the step-through debugger
dotnet run --project Warp11.Gep/Warp11.Gep.fsproj -- debug operator-engine     # ...on a design the registry cannot see
dotnet run --project Warp11.GolView.Desktop -- --hdl             # the GoL view on the real RTL
dotnet fsi census.fsx                                                          # what has been written more than once
```

The debugger opens on the first design in the registry, or on one named:
`-- "Filling memory (256 words)"`. Pick signals to watch from the filter list,
type breakpoints over them (`count == 0x40 && !valid`, `store[3] == 0x55`,
`signed(zx) < 0`), and Run until one fires. Record a trace and the waveform tab
draws one column per cycle; **Save VCD** hands it to GTKWave or Surfer.
A design can also carry its own claims — `assertThat cond "message"` — which
the debugger checks every cycle and stops on, and which the differential checks
in both worlds at once. `--hdl` on the GoL view runs the
elaborated 64×64 design in simulation and its **Debugger** button opens a
window on the *same* session. The registry spans
`Warp11.Designs` only, so a project opens a debugger on its own designs instead:
`-- debug operator-engine` in `Warp11.Gep`, `-- debug coalescer` in
`Warp11.Mandelbrot`. A state machine built with `machine` shows its state names
there — `MutationGate (0x0b)`, not `0x0b`.

The GEP project carries the most machinery, so its modes are worth listing:

```sh
cd hdl
P="--project Warp11.Gep/Warp11.Gep.fsproj"
dotnet run $P                            # 22 living checks, oracle-gated
dotnet run $P -- diff <dir>              # the 12 differential testbenches
dotnet run $P -- problems                # symbolic regression + classification
dotnet run $P -- srbench                 # the Feynman starter set, fixed budget
dotnet run $P -- srbench-seeds I.12.2 40 # ...as a seed distribution (see below)
dotnet run $P -- sweep "4x8x2@32/256"    # breeders x lanes x fillers @ latency/cases
dotnet run $P -- hardware <repo-root>    # the silicon seam: RTL + gep_layout.rs
dotnet run $P -- boardvector <dir> [n]   # a first-light vector with its answers
dotnet run $P -- emit-cluster <dir>      # one .v per arrangement, for linting
dotnet run $P -- debug <label>           # a debugger on one design; an unknown label lists them
```

**Two of those exist because a single number misled us.** `srbench-seeds` runs
a problem over many seeds because both remaining Feynman solves are
restart-sensitive, and one run cannot tell "solved" from "got lucky";
`sweep` takes a DDR read latency because with a zero-latency model every
feeder/lane mix looks identical and the real bottleneck is invisible.

The demo prints the emitted Verilog for every design plus the width-checker
results. To lint the output:

```sh
dotnet run --project Warp11.Designs/Warp11.Designs.fsproj > /tmp/out.txt
sed -n '/^module Mul8/,$p' /tmp/out.txt | sed -n '/^module/,/^endmodule$/p' > /tmp/Dot2Ambient.v
verilator --lint-only -Wall -Wno-DECLFILENAME --top-module Dot2Ambient /tmp/Dot2Ambient.v
```

## Layout

Three projects, split 2026-08-02 when the spike graduated to real. The split
moved nothing: every emitted module, all 25 testbenches *as the suite stood
that day*, the rendered PPM and the seam artifacts were byte-identical across
it.

- **`Warp11/`** — the library. One file per layer, compiled in dependency
  order (the real one is `Warp11.fsproj`'s, and it is worth reading before
  assuming a dependency): `Ir.fs`, `Keywords.fs`, `Layout.fs`, `Dsl.fs`,
  `Combinators.fs`, `Streams.fs`, `AxiLite.fs`, `RegMap.fs`, `Number.fs`,
  `NumberOperators.fs`, `Verilog.fs`, `Flatten.fs`, `Firrtl.fs`,
  `FirrtlImport.fs`, `Inventory.fs`, `Sim.fs`, `SimAxi.fs`, `Breakpoint.fs`,
  `Debug.fs`, `Catalog.fs`, `Vcd.fs`, `Diff.fs`, `Stdlib.fs`, `Audio.fs`,
  `Wav.fs`. Most modules are `[<AutoOpen>]` — so `open Warp11` is the whole DSL
  surface — the exceptions being `Breakpoint`, `Catalog`, `Debug`, `Firrtl`,
  `FirrtlImport`, `Inventory`, `Number` and `Vcd`, which are reached by
  qualified name because their members would otherwise shadow the IR's.
- **`Warp11.Designs/`** — the toy and evidence designs (the oracle's test
  inputs), and the demo dump with its living checks
  plus the `diff` writer for its 75 designs.
- **`Warp11.Mandelbrot/`** — the mini pod (`Pod.fs`, § The pod) and the
  **full-scale 104-lane accelerator** (`Step.fs` → `FrameAxi.fs` — see
  [its own README](Warp11.Mandelbrot/README.md)), with the register-map seams
  (`Seam.fs`) and the host sides (`Host.fs`, `FrameHost.fs`).
- **`Warp11.GoL/`** — Game of Life, the tutorial's reference implementation,
  through to a bitstream and the Zenoh-fed live view.
- **`Warp11.SimView/`** — the step-through debugger (Avalonia + FuncUI):
  filterable watch list, breakpoints over the IR, memory windows, a per-cycle
  waveform (`Waveform.fs`) and VCD export.
  `DebugWindow` is public so another app can open one on a session it owns, and
  `Warp11.SimView.Desktop.debug` runs one as its own application over a design a
  project hands it — which is how GEP's and Mandelbrot's designs are reachable at all.
- **`Warp11.Gep/`** — genetic programming, and the largest thing here. The
  software engine (the oracle: `Chromosome`/`Karva`/`MicroProgram`/
  `HwBreeding`/`Engine`), the hardware ladder (`Hdl.fs` — ALU, divide,
  karva compiler, operator engine, unit engine, breeder block, record router),
  the WarpCPU cluster (`Cluster.fs` — queue datapath, generation loop, op-list
  emitter) behind its register map (`ClusterAxi.fs`), and the benchmarks the
  search exists for (`Problems.fs`, `Srbench.fs`). **On the KV260 since
  2026-08-11**, 512/512 offspring bit-exact against the software chain.

The table below walks the library's layers in dependency order (files sit in
`Warp11/` unless another project is named); the last three rows before the
designs are the interesting ones.

| section | file | what |
|---|---|---|
| `Expr` + shim | `Ir.fs` | the expression IR as a DU, plus curried constructors — DU cases are tupled, so `Add` cannot be passed where `Expr -> Expr -> Expr` is wanted. `+` and `*` are static members on the DU, and F#'s operator-as-value *is* curried, so `( * )` passes point-free. |
| **the signed ops** | `Ir.fs` | **Signedness is on the value, not the operation.** A ground type is `UInt w` or `SInt w` (FIRRTL's), leaves carry it and everything else computes it, so there is one `Mul` and one `Lt` and each reads its operands. A declaration says which — `input "a" (SInt 8)`, with a bare width still meaning `UInt` — and `asSInt`/`asUInt` are the zero-hardware reinterpretations for values that are genuinely read both ways. Operands that disagree are an elaboration error naming both types; a **literal borrows its neighbour's reading**, the same rule by which it already borrows its width. Arithmetic keeps its operands' reading; slice, `cat` and the bitwise ops land in `UInt`, because bits are bits until someone says otherwise. `Sub` needs no variant at all (two's complement makes the gates identical). The shift and widen primitives are FIRRTL's: `Shr` narrows *and* keeps the reading (so a Q-format truncation stays signed, which is what lets `saturate` and `pad` dispatch on the value rather than on a suffix), and `Pad` widens by replicating the sign bit or padding zeros, whichever the value says. `sra` — the width-preserving arithmetic shift — is `pad(shr(x, n), w)`, a composition rather than a node. A signed multiply and `sra` follow the slice rule — operands must be declared signals, because emission replicates a named sign bit (`{{8{a[7]}}, a}`), which keeps every width self-determined with no `$signed`, whose meaning would depend on the inlining context. A signed compare needs no name (it emits the sign-bit-flip trick `(a ^ 8'd128) < (b ^ 8'd128)`) but requires equal widths — sign-extending the narrower operand is the caller's decision. The `mulS`/`ltS` pairs this replaced were retired 2026-08-16, moving no Verilog in any design. Today's additions sit in the same shape: **`Shr`/`Pad`** (a narrowing shift that keeps its reading, and a widen that follows the value), **`DynamicShl`/`DynamicShr`** (shift by a signal — `shl 3 a` is a rewiring and `shl n a` a barrel shifter, dispatched on the amount's type), **`Reduce`** (`anyBitSet`/`allBitsSet`/`parity`, one node with a kind), and **`Div`/`Rem`** — where the IR carries the general form for reading foreign designs but the authoring surface offers only `divideBy (k: int)`, so dividing by a signal does not compile. |
| **the FIRRTL export** | `Firrtl.fs` | The IR as low-FIRRTL text, so the oracle can run a leg through `firtool` — a stranger to our Sim, which is what makes "we did not invent an IR" a measurement rather than an intention. Thin, because Phase 1 put `UInt`/`SInt` in the IR first: nearly every node maps one-for-one. The four that do not are FIRRTL's widening `add`/`sub` (ours wrap, which is `tail(…, 1)` exactly), its *typed* connects and operands (driving a `UInt` net from a signed sum is ordinary here and illegal there, so the reinterpretation becomes an explicit `asUInt`/`asSInt` costing no gates), memory ports being fields of the memory rather than names, and FIRRTL 4.0 requiring the main module to be `public`. A preloaded ROM's contents cannot be said in `.fir` at all, so the export raises `Unrepresentable` naming the design rather than emitting a memory that reads as zeros — 135 of the 141 designs the oracle covers export, the six that do not being preloaded ROMs; whether `firtool` accepts them is what the `FIRTOOL_LEG=1` leg measures. **Never a user-facing dependency.** |
| **`Number` + `NumberFormat`** | `Number.fs` | one numeric layer over the width-only IR. A format is three numbers — a width, a count of fraction bits, and whether the top bit is a sign — so **signed and unsigned integers and signed and unsigned fixed point are one type**, integers being the `fracBits = 0` case. Since the IR's ground types carry signedness, `signed` here *types the bits* — `Number.input`/`Number.wire` declare `SInt`/`UInt` — and `*` and `lessThan` are then plain `mul` and `lt`. What remains is the part the IR has no opinion about, where the binary point sits. `saturateTo` and `resize` are now plain `saturate` and `pad`; `shiftRight` keeps a branch, but over *width* rather than signedness — the signed form preserves it, the unsigned one narrows, and that is a real choice about what the caller wants back. `+`/`-` need no branch (two's complement makes the gates identical) and lift the IR's own. `renormTo` (drop fraction bits and narrow in one part-select) and `reinterpret` (same bits, k fewer fraction bits = ×2^k, zero hardware) are the two named escapes, each naming its target format. Formats are checked at elaboration, as widths always have been. **Replaced the measure-typed `Fixed<'q>` layer** (2026-08-15): a units-of-measure type checked by the compiler became a format record checked at elaboration, and the retirement moved no Verilog in any of the six design projects. |
| `width` / `emit` | `Ir.fs` / `Verilog.fs` | one `match` each; `width` implements Warp 11's rule that `a * b` is `width a + width b` |
| `Decl` / `Stmt` / `ModuleDef` | `Ir.fs` | the module IR, including instances |
| **the keyword check** | `Keywords.fs` | Verilog/SystemVerilog reserved words refused at elaboration, at every declared name, instance name and module name — closing the one gap where a *design* alone could emit invalid Verilog. Two live cases (`cross`, `matches`) before it landed. |
| `Builder` | `Dsl.fs` | the mutable builder everything else runs on. `Instance` returns the module's *function*, not its port record; the one-argument overload derives an instance name from the child module. |
| `defineModule` | `Dsl.fs` | the general typed-port form — a `Ports -> 'io` factory run twice, which is warp11-Kotlin's re-runnable-factory trick ported directly |
| `fnModule1/2/3` | `Dsl.fs` | sugar over `defineModule` for combinational modules: one lambda is both the semantics and the call-site type, and the output width is inferred from it |
| **the ambient layer** | `Dsl.fs` | `elaborating` (a stack of builders), `design`, and free `input`/`output`/`wire`/`reg`/`==>`. This is what makes a module body read as ordinary F# — the same conclusion Rust reached with `thread_local!` and Kotlin with the receiver lambda. |
| **`liftUnary` / `liftBinary`** | `Dsl.fs` | turn a module into a function that creates a fresh instance on every call. All naming happens here. |
| **`*Logic` vs `*Of`** | `Stdlib.fs` | the same semantics inline (`mulLogic 8`) or wrapped in a module (`mulOf 8`). **Both are `Expr -> Expr -> Expr`, so a use site cannot tell them apart** — whether something becomes a Verilog module is a stdlib-definition choice, not a call-site one. `memoize` means `mulOf 8` at two use sites is one `Mul8`. |
| **`stateModule1`** | `Dsl.fs` | a module whose body is ordinary ambient code — `reg`, `==>` — rather than a pure function of its inputs. `defineModule` pushes the module's builder, so a definition body and a design body are the same kind of code. `delayOf` and `counterOf` are the stateful stdlib entries; a register is four lines. |
| **`Stream<'p>` + `Layout<'p>`** | `Layout.fs` / `Streams.fs` | the ready/valid layer, generic over its payload. A `Stream` carries `payload`/`valid` forward **and the `ready` net its consumer must drive** — the backward wire travels forward inside the value, so `stage (streamMap f (stage src))` is ordinary nesting even though the handshake flows both ways. A `Layout` is the hand-written witness (field names/widths + pack/unpack) that turns a typed payload into ports — the no-reflection answer; `layout2 ("x", 8) ("lum", 8)` is one line. `streamMap` may change the payload *type*, so projections are ordinary maps, and touching the wrong field is a compile error rather than a name lookup. |
| **`streamStageFor`** | `Streams.fs` | one register stage per layout. Not memoized — a `Layout` holds functions, which have no useful equality — so the module name derives from the fields and structurally identical re-elaborations are collapsed by the one-name-one-module check: the Rust spike's arrangement, adopted exactly where memoization stops being possible. |
| **`streamFifo`** | `Streams.fs` | a FIFO between producer and consumer — a burst absorbed, a pause propagated only once full. First-word fall-through, so `payload` and `valid` arrive together as the contract requires. **Its storage is not part of its contract**: up to `streamFifoDistributedMax` (64) the words live in LUTs and the head is a combinational read; at or above it they live in a block and the head is a synchronous read behind a two-slot skid. Both hold exactly `depth` beats and both present the same `Stream`, so a depth going from 8 to 8192 changes where the bits sit and nothing a caller can name — which is the boundary the DSL draws generally, that code may assume a combinational read of something *always* LUT-shaped and never of something whose storage depends on how big it got. |
| **`withContext` / `Stream.farmWith`** | `Streams.fs` | a slow stage keeps the caller's data. The stage never learns about it — the context rides a FIFO and comes back paired with the result, so no component grows a passthrough for a payload it never reads and no caller keeps a shadow queue. `farmWith` is `farm` of `withContext`, and needs **no tags**: a farm owns the dispatch *and* the merge, so it knows which lane produced each beat. Depth is a throughput knob, not a correctness one. |
| **`streamZip` / `layoutJoin`** | `Streams.fs` / `Layout.fs` | pair two streams beat for beat, and put two payloads side by side. A *join*, which `streamMergeTree` is not — a merge arbitrates between alternatives and would hand back two beats from the same side. |
| **`divider`** | `Stdlib.fs` | an integer divider as a stream stage, radix-2 restoring. One subtractor reused for `width` iterations, so it cannot take a new pair every cycle and `ready` is the only thing that can say so — **no latency crosses the boundary**. Division by zero saturates to all-ones, because every trial subtraction succeeds. |
| **`warpFu`** | `Stdlib.fs` | share one fixed-latency unit across N clients without touching the unit: round-robin issue, a tag delay-line, a writeback demux, and at one client none of it. The core **reports** its own depth (`Expr list -> Expr list * int`) rather than being told one — a wrapper cannot check a number it was handed, and a wrong one misroutes every result. |
| **`checkStreams`** | `Verilog.fs` | a stream has exactly one consumer: every registered ready net must be driven exactly once. Zero = created-but-never-consumed (was an undriven port only Verilator noticed); two = two consumers fighting. `emitDesign` refuses on violation. |
| **`If` / `Else`** | `Dsl.fs` | conditional assignment folding to Mux trees at block end — Warp 11's construct, as two sequential statements. Each branch elaborates into its own scope and merges into its parent as a Mux, so nesting AND-folds structurally and last-connect-wins gives priority semantics. A reg with no unconditional default holds (the hold arm exists only in the fold); a wire there is an elaboration error. `Else` must immediately follow its `If` — any intervening statement seals the On as else-less. |
| **`mem` / `distributedMem` / `blockMem` / `memWrite` / `memWriteMasked` / `memRead` / `memReadPort`** | `Dsl.fs` | **A memory declares its storage, and the read is checked against it** (2026-08-17): `distributedMem` is LUTRAM and the only kind the combinational `memRead` is legal on, `blockMem` is BRAM and takes `memReadPort` only, `mem` leaves the choice to the tool and so refuses the combinational read too. The emitted `(* ram_style = … *)` takes the decision away from the synthesiser, which is what turns this repo's oldest silicon trap — a combinational read of an array the tool put in a block, silently gaining a cycle while passing sim *and* Verilator — into an elaboration error. Measured before the change: 49 memories in the tree are read asynchronously, largest 8192 bits, all of them register files, small tables and FIFO storage. | memories of 2^addrWidth words (an address can never leave the array). `memWrite` ANDs the enclosing On conditions into its enable — the condition stack the Rust spike's `when` lacked. Multiple writes to one mem merge into a **single priority-muxed write site** at finalize, because two write sites kill BRAM inference even when mutually exclusive. `memRead` is combinational. `memReadPort` costs a cycle (emits the BRAM pattern `rd <= mem[addr]`, **read-first** vs a same-cycle write — differentially verified) and hands back a port rather than a value: `read.data` plus `read.through "name" signal`, which delays a carried signal by the port's own depth. A latency a caller has to remember is one it will eventually forget, so the port remembers it — the same rule that makes `reduceTreePipelined` return its depth rather than take one. `memWriteMasked` takes one bit per lane and emits the byte-enable template — one block RAM with per-lane write ports rather than read-modify-write logic; masks do **not** merge across write sites, the priority pick happens first and the winner's mask second, and writes disagreeing on lane count are an elaboration error. A masked memory exports to FIRRTL as a vector data type and cannot be read back, so it sits out the round-trip the way a ROM's contents do. The array doesn't reset; read-port regs do. |
| **`streamBroadcast` / `streamMerge2` / `streamMergeTree`** | `Streams.fs` | the fan-out/fan-in pair, chosen by what Mandelbrot needs. Broadcast is lockstep: every consumer sees every beat, the source's ready is the AND of theirs. Merge2 is round-robin (the side not served last wins), and the tree composes it N→1 — the lane-results shape, order-free because beats carry their coordinates. The **wormhole family** is the connect vocabulary over the primitives: `wormhole` (1→1, incl. `streamExport` port sinks), `wormholeOut` (1→N — `mode` is a required `FanOut`, the types cannot decide; the sink FACTORY runs once per lane, so the wormhole owns the multiplicity and creates instances inside the connect) and `wormholeIn` (N→1 + elastic `stages`). Topology is the call's decision by N: direct at 1, flat at 2, clustered at ≥3. Underneath: `streamDispatch`/`streamDispatchClustered`/`streamMergeClustered`, plus `streamProbe`/`streamReport` telemetry (saturating `blocked`/`starved` counters per link, read by Sim peek). CONFLATE landed as `snapshotSource` + `streamConflate3` — the keep-latest triple buffer GoL streams through into PS DDR. `fork_tb.v` asserts the semantics the oracle cannot judge: exactly two copies per beat, lossless under stall. |
| **`Union2` + `matchUnion`** | `Layout.fs` / `Streams.fs` | sum-type payloads with **no new IR** — the tag is a field, the data is a field, variant fields are slices, and variants are ordinary `Layout`s, so a variant's view is typed end to end. `inject0/1` build a beat; `matchUnion` runs each typed handler under `If(tag == k)` with the variant's fields unpacked, so a handler reads like a DU match arm. `unionLayout` makes a union stream ride the generic stream machinery unchanged. Slicing follows Kotlin's rule, now enforced at elaboration *and* emission: slice a declared signal, never a computed value. |
| `checkWidths` / `checkNames` | `Verilog.fs` | declared-vs-driven widths (every module in the hierarchy, not just the top), and one-name-one-module. `emitDesign` refuses on either — `checkWidths` used to only *report*, so a truncating assign reached Verilog and only Verilator objected. |
| **one declaration per name** | `Dsl.fs` | a name may be declared once per module, and an instance's staging wires (`{instance}_{port}`) go through the same door — they live in the **parent's** namespace, so `Stream.out "b_low"` beside an instance named `b` collided and emitted a port redeclared as a wire, self-assigned. The error names both sides, because where a staging wire came from is the half the source doesn't show. Fails at the declaration, as the one-driver rule fails at the second `==>`. |
| `emitAt` / `emitVerilog` / `emitDesign` | `Verilog.fs` | children-first multi-module emission. `emitAt` zero-extends every operand to its result width so a sub-expression's width never depends on its context — see below. Registers reset to their declared init under `rst`; `clk`/`rst` thread transitively (`needsClk`), so a reg-free parent holding a stateful child still gets and forwards the pair. |
| designs | `Warp11.Designs/Designs.fs` + `Warp11.Mandelbrot/Pod.fs` | `counterMutable`, `add3`, then `dot2` / `dot2Auto` / `dot2Ambient` / `dot2Inline`, the stateful pair `pipelinedDot` / `gatedCounter`, `streamPipe` (single-field stream), `coordPipe` (two-field payload — Warp 11's pixel-beat rule in miniature), the `If` pair `onCounter` (nested, reg-hold) / `onPriority` (defaulted wire, last-connect priority), the generator pair `loopPipeline` (a fold building 4 instances) / `treeSum` (recursive inline adder tree), `ramTest` (write under On, sync + async reads, read-first), the union pair `cmdProcessor` (a union command stream driving a mem — most of the surface in one design) / `unionRoundTrip` (inject, mux, unslice), `forkJoin` (replicate → two branches → round-robin merge), and the signed pair `signedOps` (every signed op at its own port; random stimulus sets the sign bit half the time, so the 0x80/0xFF boundary patterns reach the oracle without being enumerated) / `escapeStep` (Mandelbrot's inner step in Q4.4 — coordinates declared `SInt 8`, Q renormalization as a plain slice of the product wire, escape as an *unsigned* compare because squares are non-negative, now said with an `asUInt` rather than left to a comment), and the Fixed pair `escapeStepFixed` (the same step on the typed layer — same wires, same order, byte-identical arithmetic; the escape compare is the one divergence, `fLt` being signed where the hand-written design chose the unsigned trick) / `escapeStep28` (the step at Mandelbrot's real Q4.28, whose Q8.56 products sit exactly on the narrow Sim's 64-bit ceiling — the boundary the mini pod needs, differentially exercised before the pod exists), and **`mandelLane` / `mandelPod`** — the acceptance artifact, § The pod below. `nameCollision`, `danglingStream`, `onBadWire`, `doubleAssign`, `declCollision` and `widthViolation` exist to make the checks fire. |

The four `dot2*` designs are deliberate duplicates: they emit the same computation
through four different surfaces — named instances, derived names, the ambient HOF
layer, and fully inlined — which is what makes the surfaces comparable. `dot2Ambient`
and `dot2Inline` differ *only* in their three stdlib bindings; every other character
of the body is identical.

## Why the emitter is width-explicit

Verilog widths are *context-determined*: the width of an assignment target propagates
down into the expression. Module ports hide this, because every port is a declared
width that pins its sub-expression. Inline the same computation and it stops being
pinned — an 8×8 multiply assigned to an 8-bit target silently truncates, where the
same multiply behind a 16-bit output port does not.

So `emitAt` widens every operand explicitly (`{8'd0, a} * {8'd0, b}`), which is what
warp11-Kotlin's emitter already does. Verilator `-Wall` found this on the first
inlined design and would not have found it from the hierarchical ones.

## Behavioral checks

Lint proves structure; the testbenches prove behavior. `behavior_tb.v`: the pipeline's
latency is exactly 2 and the counter counts, holds, and resets. `stream_tb.v`: two
beats fill the stalled stream pipe, the source blocks, and both beats drain in order
with the payload map applied. `coord_tb.v`: two-field beats stay *associated* —
each `x` arrives with its own brightened `lum` — through fill, stall and drain.

```sh
dotnet run --project Warp11.Designs/Warp11.Designs.fsproj -- diff /tmp/fsdiff   # writes modules.v
verilator --binary -Wno-DECLFILENAME --top-module tb behavior_tb.v /tmp/modules.v
./obj_dir/Vtb            # prints BEHAVIOR OK
verilator --binary -Wno-DECLFILENAME --top-module stream_tb stream_tb.v /tmp/modules.v
./obj_dir/Vstream_tb     # prints STREAM OK
```

Those three are scenario checks with hand-computed values, kept as documentation of
specific behaviors. The systematic verification is the differential oracle:

## The differential oracle

```sh
./run_differential.sh          # ALL DIFF PASS
```

`Sim` (`Warp11/Sim.fs`) is a two-phase simulator over the flattened design —
topo-sorted combinational assigns, snapshot-then-commit registers, peek/poke by
flattened name. The design is **compiled once at construction**: every signal
becomes an index into a flat array and every expression a thunk with its widths
and masks already folded in, which is what makes it fast enough to sit under an
interactive debugger. Signals of 64 bits or fewer run on `uint64`; an assignment
touching anything wider runs on a parallel `BigInteger` path — the full-scale
pod's 128-bit egress beats — so a wide design pays only where it is wide.
`dotnet run -- diff <dir>` in each
design project runs 50 cycles of seeded random stimulus through the Sim for each of
its designs (75 in `Warp11.Designs`, 11 in `Warp11.Mandelbrot`; 141 across the
six projects the runner walks) and
writes a self-checking Verilog testbench asserting that exact trace; the script
drives Verilator over them. Any divergence between the two implementations fails.

The oracle was validated the way the Rust spike validated its own: the Sim's mux
was deliberately inverted, and the harness caught it — at `CoordPipe`, not `Add3`,
because Add3 contains no mux and **an oracle sees only what reaches a port**. The
design set is part of the oracle, not just its input.

## Reading FIRRTL

warp11 reads and writes **low FIRRTL** — `Warp11/Firrtl.fs` out,
`Warp11/FirrtlImport.fs` in. Writing it is how the differential gets a third
leg; reading it is how the IR stays honest, and it means the simulator can run
a design nobody here wrote.

```sh
# a .fir in, 40 cycles of seeded stimulus, a waveform out
dotnet run --project Warp11.Designs -- firrtl-sim firrtl-foreign/registers.fir 40 out.vcd
```

The stimulus is seeded, so a run is repeatable and two people looking at the
same file see the same waveform.

### Low FIRRTL only, and what to do about it

**Bundles, vectors, `when` and `invalid` are refused by name**, not
half-understood — they are the high dialect, and lowering them is a compiler
pass rather than a parser feature. A design coming out of Chisel is high
FIRRTL, so it needs lowering first.

`firtool` will do it. Given a module with a bundle port and a `when`/`else`:

```
input bundleIn : { a : UInt<8>, b : UInt<8> }
when sel :
  connect acc, bundleIn.a
else :
  connect acc, bundleIn.b
```

`firtool design.fir --ir-fir` returns exactly the subset we read — the bundle
scalarized into `bundleIn_a`/`bundleIn_b`, the `when` collapsed into a `mux`,
every type ground:

```mlir
firrtl.module @High(in %clock: !firrtl.clock, ..., in %bundleIn_a: !firrtl.uint<8>,
                    in %bundleIn_b: !firrtl.uint<8>, out %out: !firrtl.uint<8>) {
  %acc = firrtl.reg %clock : !firrtl.clock, !firrtl.uint<8>
  %0 = firrtl.mux(%sel, %bundleIn_a, %bundleIn_b) : ...
```

`--ir-fir` emits MLIR rather than `.fir` text, so it takes a second step —
`circt-translate --export-firrtl`, from the same CIRCT release.
`hdl/tools/install-firtool.sh` installs both:

```sh
firtool design.fir --ir-fir -o design.mlir
circt-translate --export-firrtl design.mlir -o design.low.fir
dotnet run --project Warp11.Designs -- firrtl-sim design.low.fir 100 out.vcd
```

**Measured, not assumed** (2026-08-16): that pipeline produces real low `.fir` —
the bundle scalarized, the `when` gone. Two things about its output are worth
knowing before you try it:

- It attaches `@[file line:col]` source locators to every line. The reader
  strips them; they say nothing about the circuit.
- **`when` lowers to a `mux`, and a plain `reg` stays a plain `reg`** — which
  the reader now takes, as `regNoReset`.

So the honest position: lowering is a solved step, and the *subset* is where the
work would be. That is why warp11 stays low-`.fir`-only for now rather than
claiming to read arbitrary Chisel output.

### What of low FIRRTL is not supported

Enumerated against the spec rather than against whatever happened to come up.
Everything in a "no" column is **refused by name at import** — nothing is
silently ignored, and an unrecognised statement is an error rather than a
dropped line, because dropping one changes what the circuit does.

The reader also satisfies warp11's **named-operand rule** on the file's behalf.
`slice`, `shr` and a signed multiply take a declared signal here, because Verilog
has no part-select of an expression; FIRRTL has no such rule, so
`bits(dshl(a, n), 7, 0)` is ordinary there. Those operands are hoisted into
`_hoist_N` wires, which is what firtool's own `_GEN` wires are for.

**A register with no reset used to be the one that bit first** — FIRRTL's `reg`
holds through reset where warp11's always reset. That is now `regNoReset`, an IR
feature rather than an import workaround, and worth having on its own: a reset
net reaching every flop costs fanout and routing and blocks SRL inference. See
*Registers and reset* in `docs/HDL_COMPARISON.md`.

| | supported | not supported |
|---|---|---|
| **types** | `UInt<n>`, `SInt<n>`, `Clock` | `AsyncReset`, `Analog` |
| **declarations** | `wire`, `reg`, `regreset`, `node`, `inst`, `mem` | `extmodule`, `intmodule` |
| **statements** | `connect`, `assert`, `skip`, `invalidate` (a no-op) | `printf`, `stop`, `assume`, `cover`, `attach` |
| **arithmetic** | `add` `sub` `mul` `neg` `cvt` `div` `rem` | |
| **compare** | `lt` `leq` `gt` `geq` `eq` `neq` | |
| **bits** | `and` `or` `xor` `not` `cat` `bits` `head` `tail` `pad` `shl` `shr` `dshl` `dshr` `andr` `orr` `xorr` `asUInt` `asSInt` `mux` | `asClock`, `asAsyncReset` |
| **memories** | read-latency 0, write-latency 1, any number of readers and one writer | `readwriter` ports, other latencies, masks wider than one bit |
| **storage style** | — | FIRRTL has no `ram_style`; an imported memory comes back `Unspecified` |
| **structure** | one clock and one reset per module | multiple clocks, a clock chosen per register |

A few of those are shapes rather than omissions, and are worth knowing as such:

- **`div`/`rem` read, but the authoring surface only offers a constant
  divisor.** `divideBy` takes an `int`, not an `Expr`, so dividing by a signal
  does not compile — that line is where the cost is, since `/ 8` is a shift and
  `/ 10` is a multiply by a reciprocal, while a divisor that varies is thirty
  levels of logic that looks identical at the call site. The IR carries the
  general form so a foreign design reads whatever it says, the same split the
  registers take.
- **Division by zero** is undefined in FIRRTL and X in Verilog. The Sim answers
  zero, which is what Verilator does — `firrtl-foreign/division.fir` holds the
  two to that rather than a comment claiming they agree.
- **A `readwriter` port** is a *physical port-sharing* device: one block-RAM
  port doing read-or-write in a cycle, for when a design needs more ports than
  the block has. Warp 11's model — any number of readers plus one folded write
  site — maps to simple dual-port, which is what these designs use, so nothing
  here wants to author one. Importing it would not even be free: a readwriter's
  read is undefined in write mode, so splitting it into a reader and a writer
  would be *more* permissive than the source said.
- **`ram_style` does not survive a round trip**, because FIRRTL describes a
  circuit rather than how to build one. Nothing about behaviour changes — it is
  a directive to Vivado — so the export does not refuse it the way it refuses a
  ROM's contents, and the round-trip check compares with the attribute stripped
  and says why.
- **`invalidate`** is a no-op rather than an error: anything the file does drive
  the net with wins, and anything it does not is caught by `checkWidths`.

### What is not low FIRRTL at all

These are a different answer to a different question, and belong out of the
table above: they are **high**-dialect constructs, which lowering removes rather
than warp11 lacking them. If your file has one, it needs
`firtool --ir-fir | circt-translate --export-firrtl` first, and then it will not
have one.

| construct | what lowering turns it into | checked |
|---|---|---|
| bundles `{ a : UInt<8>, … }` | scalarized ports — `x_a`, `x_b` | yes |
| vectors `UInt<8>[4]` | scalarized signals — `xs_0` … `xs_3` | yes |
| `when` / `else` | a `mux` | yes |
| abstract `Reset` | inferred to `UInt<1>` or `AsyncReset` — but **only inside** a module; firtool refuses one on a *public* port outright | yes |
| layers, probes (`define`, `propassign`) | erased, or hoisted into ordinary modules | no |

The reader refuses each of them by name and says to lower — it does not attempt
a lowering pass of its own, which would be reimplementing `firtool` for a path
that already works.

**One hole in that path, and it is `circt-translate`'s rather than ours.**
Indexing a vector by a *signal* survives lowering as a `multibit_mux`, which
`--export-firrtl` cannot write back out — it emits a placeholder and carries on:

```
connect acc, <unsupported-expr-multibit_mux>
```

A static index is fine (`connect acc, xs_2`). The reader detects the placeholder
and says so, rather than reporting an undeclared signal named
`<unsupported-expr-multibit_mux>`.

**Bundles and vectors are worth one more sentence**, because "not supported"
reads like a gap and it is not one. warp11 puts aggregates exactly where FIRRTL
does: above the IR. A bundle is a `Layout`, and a vector is an F# list — which
is why Audio's FIR builds a 16-tap delay line with `List.scan` and then zips and
folds it. See *Typed I/O / bundles* in `docs/HDL_COMPARISON.md`.

### What the reader is checked against

Two properties, and they answer different questions.

- **Round trip**, in `Warp11.Designs`: `emitDesign (import (emitFirrtl d))` is
  byte-identical to `emitDesign d`, over the 41 exportable designs of the 43 in
  that project's debugger registry (a preloaded ROM and a lane-masked memory are
  the two excluded by name). That proves
  the reader and the writer agree.
- **`hdl/firrtl-foreign/`**, under `FIRTOOL_LEG=1`: files written to the spec in
  shapes our emitter never produces, where the judge is firtool rather than us.
  That is the only one of the two that can catch a construct we misread — see
  that directory's README for the rule it found wrong on the first file.

## Rules

**Call-site invariance (Jason, 2026-08-01).** Whether a stdlib entry is inline
logic (`mulLogic 8`) or a module (`mulOf 8`) is decided at its definition and is
invisible at every use site: same type, same call shape, switchable without
touching a caller. Any proposed surface that would let a call site tell the two
apart gets flagged before it is built.

The living check is the `dot2Ambient` / `dot2Inline` pair: character-identical
bodies apart from their three bindings, emitting four modules and a single assign
respectively. A change that forces those bodies to diverge anywhere but the
bindings violates the rule.

The rule caught a real leak on 2026-08-04: `Stream.spec` took only a
`TypedModule`, so a function-shaped stage could not join a `pipeline` at all and
the frame pod's egress register had to hang off the end as a separate operator.
`Stream.specFromFunction` (Akka's `Flow.fromFunction`) closes it — both forms are
`StageSpec`, and `lanes`/`probed` apply to either.

**One driver per signal per level (2026-08-04).** A second unconditional `==>` to
one signal is an elaboration error naming the signal and the module. The scope
underneath is still last-connect-wins — that is how an `If` branch merges into its
parent — but `Assign` rejects a user-level second assign, so it is unreachable
from a design body. The `default ==> w` + `If c (other ==> w)` idiom is untouched
(the override lands in a child scope, which the reg-hold rule already requires for
wires), and `Warp11.Designs` asserts both halves: `doubleAssign` must fail,
`OverrideIdiom` must still elaborate.

Adopted after measuring **zero** double-assigns across every F# design — nothing
depended on it. The bug it catches is silent through elaboration, lint *and*
synthesis: Warp 11 emits one `assign` per target, so a discarded first assign never
becomes a Verilog multi-driver for Vivado to find.

## The pod: the Mandelbrot path, complete (agreed 2026-08-01, landed 2026-08-02)

The path landed in three steps, each under the oracle before the next began:
**signed arithmetic** (`Sub`, `Mul`, `Lt`, `Shr`/`Pad` — the last IR
extension; `escapeStep` also showed the pod path renormalizes by *slicing* the
product wire, so `sra` earns its keep on standalone shifts, not in the inner
loop), the **typed numeric layer** (row 2's last researched claim, now
measured; `escapeStepFixed` proves it compiles away, `escapeStep28` proves
Q4.28 at the narrow Sim's 64-bit ceiling), and the
**acceptance artifact**: `mandelPod`.

The pod lives in `Warp11.Mandelbrot/`, apart from the library on purpose:
it is the one *user* of the surface rather than part of it, and
It is compared unit-by-unit with the design it miniaturizes in the project's
own scorecard.

`mandelLane` is one barrel lane — four threads in 4-entry LUTRAM-shaped mems
round-robin through one shared Q4.28 step (`escapeStep28`'s arithmetic,
verbatim, on the numeric layer), the real pod's shape with the multiplier latency
the barrel exists to hide collapsed to one cycle at spike scale. A finished
thread offers `(pixel, iter)` on its output stream and holds state until the
beat is taken, so losing merge arbitration just means retrying next barrel
round. Lanes stride the frame (`cat count base` — no adder), the lane index
arrives as a constant-driven input port so all four instances are one module,
and pixel coordinates are plain signed integers — the `fracBits = 0` case of
the same numeric layer the Q4.28 coordinates use, so the multiply needs no
integer-times-fixed special case. `mandelPod` merges the four lanes
through `streamMergeTree` into a framebuffer mem and raises `done` when all
3072 results have landed.

`dotnet run --project Warp11.Mandelbrot/Warp11.Mandelbrot.fsproj -- mandel out.ppm`
ticks the Sim to `done` (64×48 @ 48 iterations:
12,933 cycles, ~2 s), reads the framebuffer through the Sim's `PeekMem`
backdoor, asserts **bit-exactness against a software twin on every pixel**
(GEP's pattern — no tolerance), writes the PPM and prints an ASCII preview.
The differential testbench covers the pod's first 50 cycles — bootstrap, first
escapes, merge arbitration; 39 of the 50 carry a live result beat — while the
twin covers the full frame. AXI, Vivado and the board stay out of spike scope,
which is where the trial's no-silicon qualifier now lives.

## Known gaps

`Union2` is two-variant only (the layoutN arity tax again); tag width
generalizes with a log2. Reserved words are checked at elaboration
(`Keywords.fs`, the port of Kotlin's `VerilogKeywords.kt`) — a wire named
`cross` cost a lint session during the signed work and `matches` another during
the wide-Sim work before the check landed; both now refuse at the declaration.
Designs that slice a product wire (`escapeStep`) lint with
`-Wno-UNUSEDSIGNAL`, the same documented suppression the Kotlin toolchain
carries for exactly this shift-by-part-select pattern. Latency is invisible at
the use site: a stateful `Expr -> Expr` has the same type as a combinational one, so
aligning parallel pipeline branches is the designer's job — inside a stream, the
handshake does that alignment, which is Warp 11's answer too.

Four entries left this list rather than being fixed around, and each is worth
knowing as *closed*: CONFLATE landed (`snapshotSource` + `streamConflate3`), so
did dispatch; initialized memories emit an `initial` block Vivado turns into
BRAM INIT and `rom`/`distributedRom` are the authoring surface; the
combinational read of a block-shaped memory is an **elaboration error** since
2026-08-17 rather than a silicon trap the oracle structurally could not see; and
slicing a computed operand is refused at elaboration *and* emission rather than
reaching Verilog. The Sim is no longer narrow-only either — values of 64 bits or
fewer run on `uint64` and anything wider takes a parallel `BigInteger` path.

`FS0025` (non-exhaustive match) is an error, not a warning — set in the fsproj, so
an IR case added without emitter coverage fails the build rather than throwing at
runtime.

Two known costs of the ambient layer, both shared with Rust's `thread_local!`:
calling `input` outside a `design` fails at runtime rather than at compile time, and
`elaborating` is global mutable state that would need to be thread-local before
anything elaborates in parallel.
