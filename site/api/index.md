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
| [`Dsl`](/reference/warp11-dsl.html) | the ambient builder: `defModule` (ports declared in its io factory, the boundary sealed when it returns) and the typed instance bundles, `wire`/`reg`, `==>`, `If`/`ifElse`, `machine`, the memories. **The one to read first.** |
| [`Ir`](/reference/warp11-ir.html) | what a design *is* — `Expr`, `Decl`, `Stmt`, `ModuleDef` — and the operations over it. Width-only bit vectors with a signed/unsigned reading, and nothing above that |
| [`Stdlib`](/reference/warp11-stdlib.html) | reusable hardware: counters, FSMs, barrel threading, AXI masters, the divider, `warpFu` |
| [`Combinators`](/reference/warp11-combinators.html) | the small shapes — `delayChain`, `memReadPort`, `selectIndexed`, the width math |

## Building things out of streams

| module | what is in it |
|---|---|
| [`Layout`](/reference/warp11-layout.html) | `Layout<'p>`, `Stream<'p>`, `Flow<'p>`, `Union2` — how a typed payload becomes ports and back |
| [`Streams`](/reference/warp11-streams.html) | ready/valid: stages, FIFOs, fork and join, farms, the `wormhole` connect family, pipelines as data, stall probes |
| [`Fu`](/reference/warp11-fu.html) | a unit as a law over typed pins; an operation that could be shared as a socket, served in place or by a pool (`inPlace`, `sharingWith`, `sharingAmong`), and the farm. Not auto-opened |

## Numbers

| module | what is in it |
|---|---|
| [`Number`](/reference/warp11-number.html) | width, fraction bits and signedness as one format record. Not auto-opened — `open Warp11.Number` where you want it |
| [`NumberOperators`](/reference/warp11-numberoperators.html) | `==>` extended so a `Number` can drive a net |

## Getting off the chip

| module | what is in it |
|---|---|
| [`Verilog`](/reference/warp11-verilog.html) | emission, and the three checks that gate it |
| [`RegMap`](/reference/warp11-regmap.html), [`AxiLite`](/reference/warp11-axilite.html) | one register-map definition, elaborated as a slave *and* emitted as the Rust seam the host reads |
| [`Firrtl`](/reference/warp11-firrtl.html), [`FirrtlImport`](/reference/warp11-firrtlimport.html) | low-FIRRTL out and back in |
| [`Uart`](/reference/warp11-uart.html), [`SerialRegMap`](/reference/warp11-serialregmap.html) | the same register map behind a UART instead of AXI-Lite, framed and checksummed |
| [`Boards`](/reference/warp11-boards.html), [`Mapping`](/reference/warp11-mapping.html) | what a design is built for — part, tool, clock, host link, connectors — with the five boards as values, and a design's mapping onto one |
| [`BoardTop`](/reference/warp11-boardtop.html), [`Build`](/reference/warp11-build.html) | what satisfies a design's needs on a board — the top, the register map and the seam — and the build directory its toolchain runs |
| [`Windows`](/reference/warp11-windows.html), [`Regions`](/reference/warp11-regions.html), [`Sdram`](/reference/warp11-sdram.html) | memory as a need: an area addressed by index, records the board places, and an SDRAM controller answering the same buses |

## Simulating and debugging

| module | what is in it |
|---|---|
| [`Sim`](/reference/warp11-sim.html) | the compiled cycle-accurate simulator |
| [`Debug`](/reference/warp11-debug.html) | `IDebugSession` — step, run, watch, breakpoint, record |
| [`Inventory`](/reference/warp11-inventory.html), [`Breakpoint`](/reference/warp11-breakpoint.html), [`Catalog`](/reference/warp11-catalog.html), [`Vcd`](/reference/warp11-vcd.html) | the debugger's substrate |
| [`SimDevice`](/reference/warp11-simdevice.html) | `ISimDevice` — something attached to a running design's pins, driven around every cycle by a test loop or by the debugger |
| [`SimI2s`](/reference/warp11-simi2s.html) | a software I2S converter on those pins, written against the standard rather than against our own framers |
| [`Diff`](/reference/warp11-diff.html) | the differential oracle's testbench generator |
| [`SimAxi`](/reference/warp11-simaxi.html), [`SimStream`](/reference/warp11-simstream.html), [`SimUart`](/reference/warp11-simuart.html) | what drives a simulated design's pins: the AXI-Lite bridge a host runs through, ready/valid as a lazy sequence, and the serial register protocol's host end |

## Audio

| module | what is in it |
|---|---|
| [`Audio`](/reference/warp11-audio.html) | I2S, biquads, FIR, gain, compressor, limiter, the 8-band multiband compressor |
| [`Wav`](/reference/warp11-wav.html) | reading and writing WAV, and playing one through a simulated design — `runWavThroughSim` at its stream ports, `runWavThroughI2s` and `WavI2sSource` at its I2S pins |
| [`Pdm`](/reference/warp11-pdm.html), [`SimPdm`](/reference/warp11-simpdm.html) | a PDM bus's pins and the CIC decimator, and a sigma-delta model on the Sim's pins |
| [`Pgm`](/reference/warp11-pgm.html), [`Csv`](/reference/warp11-csv.html) | a greyscale image as row beats and a table as a row a beat — the other two things a design's boundary can carry |
