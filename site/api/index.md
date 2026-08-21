# API reference

Generated from the doc comments in `Warp11`, the library every design is built
from. Everything below is one namespace, `Warp11`, and most of it is
`[<AutoOpen>]` — `open Warp11` and the vocabulary is in scope.

If you have not written a design yet, [start a project](/guides/start-a-project.html)
first; this page is the map, not the introduction. The flat listing of every
type and function is at [`Warp11`](/reference/warp11.html).

## Where to start

| module | what is in it |
|---|---|
| [`Dsl`](/reference/warp11-dsl.html) | the ambient builder: `design`, `input`/`output`/`wire`/`reg`, `==>`, `If`/`Else`, `defineModule` and the instance-as-function surface. **The one to read first.** |
| [`Ir`](/reference/warp11-ir.html) | what a design *is* — `Expr`, `Decl`, `Stmt`, `ModuleDef` — and the operations over it. Width-only bit vectors with a signed/unsigned reading, and nothing above that |
| [`Stdlib`](/reference/warp11-stdlib.html) | reusable hardware: counters, FSMs, barrel threading, AXI masters, the divider, `warpFu` |
| [`Combinators`](/reference/warp11-combinators.html) | the small shapes — `delayChain`, `memReadPort`, `selectIndexed`, the width math |

## Building things out of streams

| module | what is in it |
|---|---|
| [`Layout`](/reference/warp11-layout.html) | `Layout<'p>`, `Stream<'p>`, `Flow<'p>`, `Union2` — how a typed payload becomes ports and back |
| [`Streams`](/reference/warp11-streams.html) | ready/valid: stages, FIFOs, fork and join, farms, the `wormhole` connect family, pipelines as data, stall probes |

## Numbers

| module | what is in it |
|---|---|
| [`Number`](/reference/warp11-number.html) | width, fraction bits and signedness as one format record. Not auto-opened — `open Warp11.Number` where you want it |
| [`NumberOperators`](/reference/warp11-numberoperators.html) | `==>` extended so a `Number` can drive a net |

## Getting off the chip

| module | what is in it |
|---|---|
| [`Verilog`](/reference/warp11-verilog.html) | emission, and the three checks that gate it |
| [`RegMap`](/reference/warp11-regmap.html), [`AxiLite`](/reference/warp11-axilite.html) | one register-map definition, elaborated as a slave *and* emitted as the Rust seam the host compiles against |
| [`Firrtl`](/reference/warp11-firrtl.html), [`FirrtlImport`](/reference/warp11-firrtlimport.html) | low-FIRRTL out and back in |

## Simulating and debugging

| module | what is in it |
|---|---|
| [`Sim`](/reference/warp11-sim.html) | the compiled cycle-accurate simulator, and the AXI slaves a harness drives it through |
| [`Debug`](/reference/warp11-debug.html) | `IDebugSession` — step, run, watch, breakpoint, record |
| [`Inventory`](/reference/warp11-inventory.html), [`Breakpoint`](/reference/warp11-breakpoint.html), [`Catalog`](/reference/warp11-catalog.html), [`Vcd`](/reference/warp11-vcd.html) | the debugger's substrate |
| [`Diff`](/reference/warp11-diff.html) | the differential oracle's testbench generator |

## Audio

| module | what is in it |
|---|---|
| [`Audio`](/reference/warp11-audio.html) | I2S, biquads, FIR, gain, compressor, limiter, the 8-band multiband compressor |
| [`Wav`](/reference/warp11-wav.html) | reading and writing WAV, and streaming one through a simulated design |
