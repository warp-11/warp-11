# How to add a new toolchain

A board is a file, and a toolchain is source: the one place in Warp 11 where
supporting new hardware means code. This page is what that code is, read off
the three flows that exist — Vivado for the KV260, yosys with nextpnr-ice40
for the iCE40 boards, yosys with nextpnr-ecp5 for the Icepi — so that the
fourth, Quartus for the DE10-Lite, is written against a shape the first three
already fit. If the fourth needs a special case the first three did not, the
layer has a leak, and the leak is the finding.

A toolchain is five things. Each is a function of the board top (the design
on its board: the top module, the register map, the pinned ports) and nothing
else.

## 1. A `Family`, and the dialect the emitter speaks to it

`Family` (`hdl/Warp11/Board.fs`) names what the toolchain is told —
`UltraScalePlus`, `Ice40`, `Ecp5` — and nothing about what the part *has*;
that is `PartFacts`, data in the board file. A new tool adds a case here and
a case to `Verilog.Target` (`hdl/Warp11/Verilog.fs`) if it reads a different
spelling of the memory attribute: Vivado and yosys both read `ram_style`;
Quartus reads `ramstyle`. The emitter is otherwise one emitter. `BoardTop.targetOf`
maps the family to the target.

## 2. The flow: what `build.sh` runs

`Warp11.Build.write dir top` (`hdl/Warp11/Build.fs`) writes a directory and
dispatches on `board.tool`: `Vivado` goes to `writeVivado`, `OpenFlow` to
`writeOpenFlow`, which itself chooses the iCE40 or the ECP5 script by family.
A new tool is a new `BuildTool` case and a `write…` function producing:

- **the top's Verilog**, emitted for the target;
- **a wrapper**, where the board's clock is not the fabric clock: the iCE40
  and ECP5 flows write a Verilog wrapper that instantiates the part's PLL
  (`icePll`, `ecp5Pll` compute the dividers from the crystal and `fabricHz`,
  and refuse what the PLL cannot reach) and holds reset until it locks; a
  board whose oscillator *is* the fabric clock gets no PLL;
- **constraints** — `.xdc`, `.pcf`, `.lpf` — from the pinned ports, one line
  a pin, the board's `activeLow` and I/O standard where the format carries
  them;
- **the script** — `build.tcl` for Vivado, or the yosys/nextpnr/pack
  commands — and `build.sh` to run it, with the tool found where the install
  script put it and the environment sanitised;
- **the loader**: `Loading` says `OsApp` (the KV260's `xmutil`), `Sram` or
  `Flash`, and the script's `PROG=` handling turns that into `iceprog`,
  `openFPGALoader -b <boardPart>` or the app's packaging.

On a part with a processing system the flow also writes **the block design**
(the PS, the interconnect, the design as a slave at the board's `AxiLiteAt`
base, its master into the PS port the board names, its `irq` into the PS),
**the device-tree overlay** (the aperture, the interrupt, the PL clock pinned
per app, a `u-dma-buf` for the host's arena) and **the packaging** `xmutil`
loads. None of that applies to a part without one, and the open flows write
none of it.

## 3. The pin gate

Before any tool runs, `Build.pinGate` matches every port of the top that is
not a bus port against the board's connectors and refuses a port no pin
backs, by name. It is the same function for every flow — a toolchain adds
nothing here unless its constraint format needs a fact the `Pin` record does
not carry, which would be the second kind of leak.

## 4. The post-build gate

`build.sh` ends by reading the tool's own report and writing
`build/report.json` — cells, block RAMs and multipliers used of the part's
total, the clock achieved against `fabricHz` — and refusing three things by
number: a resource over the part, a clock short of the target, and a board
fact that disagrees with what the tool says the part has. Two parsers exist:
`nextpnrGate` reads nextpnr's "Device utilisation" block and its
"Max frequency" lines (and knows nextpnr lists only the primitives a design
used, so an absent row is none used); `vivadoGate` reads the placed
utilisation and routed timing reports. A new tool adds the parser for its
report and names the three primitives it counts — what a cell, a block RAM
and a multiplier are called in that report — and the gate's refusals and
`report.json` are then the same on the new board as on the others.

## 5. The install script

`hdl/tools/install-oss-cad-suite.sh` is the open flow's: pinned, checksummed,
no root, into `~/tools`. A vendor tool has its own installer; what the flow
needs is to find it — `build.sh` sources Vivado's `settings64.sh` from where
Xilinx installs it — and to say clearly when it is not there.

## The fourth flow, as written

Quartus for the DE10-Lite was written against this page before the tool was
on the machine, which is the test the page asked for. What it took, in
order: a `Family.Max10` case and a `Target.Max10` case whose attribute is
`ramstyle` — `"M9K"` for a block, `"logic"` for what would have been LUT
RAM, since MAX 10 has no MLAB; a `BuildTool.Quartus` case and `writeQuartus`,
which writes the top, a wrapper with the oscillator straight in and a
power-on reset (no PLL yet, refused by name if `fabricHz` asks for one), a
`.qpf`, a `.qsf` carrying the device, the sources, every pin with its I/O
standard, an `.sdc` with the clock's period, and `build.sh` running
`quartus_sh --flow compile` then `quartus_pgm -m jtag` for `PROG=sram`;
and a gate parser for `.fit.summary` (logic elements; memory bits, which
the board knows as 9 216-bit M9Ks; multipliers, which Quartus reports as
9-bit elements, two to an 18×18) and `.sta.rpt`'s Fmax summary. The pin
gate needed nothing. No sentence about Quartus had to be about anything
but a flow — the one report quirk, the 9-bit elements, is a number in the
parser. The numbers this page owes are the first compile's.

## The cross-check

The three flows are described above without special cases, and that is the
test a fourth has to pass: if describing Quartus's flow needs a sentence that
is about Quartus rather than about *a flow*, the sentence names something
`Build.fs` should carry as data or as one more case of an existing choice,
not as a branch of its own. The DE10-Lite's bring-up is where this page gets
its worked example and its numbers; until then it is the shape the three
existing flows share, written down so the fourth is held to it.

## See also

- [How to add a new board](add-a-board.md) — the file, when the tool already
  exists.
- [Boards](boards.md) — the four that have run, with the gate's numbers.
