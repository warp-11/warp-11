# Boards

A design in Warp 11 never names a board. It declares what it needs — rows in
and out, a value the host sets, a bit it reports — and a **board** is a record
of facts a **mapping** binds those needs to: the part and what it holds, the
clock, how the host reaches the design, how a bitstream is loaded, which pins
carry which role. The same design file builds for every board below by
changing one file, and the ones that cannot say why by number.

This page is the four boards that have run, what each is, and what you do to
put something on one. [How to add a new board](add-a-board.md) is the page
for a board that is not here; [How to add a new toolchain](add-a-toolchain.md)
for a part whose tools are not.

## The four

| board | part | cells | multipliers | block RAM | clock | the host reaches it | loading |
|---|---|---|---|---|---|---|---|
| **KV260** (Xilinx Kria) | ZU5EV, `xck26-sfvc784-2LV-c` | 117 120 LUTs | 1 248 DSPs | 5.2 Mbit (+ 18 Mbit UltraRAM) | the processing system's, per app (100 or 166.67 MHz) | AXI-Lite at `0xB0000000`, from Linux on the same chip | `xmutil loadapp`, an app directory under `/lib/firmware/xilinx` |
| **iCEBreaker** | iCE40 UP5K, `up5k-sg48` | 5 280 | 8 MAC16 | 120 kbit | 12 MHz crystal, a PLL to 24 MHz | a UART at 115 200 on the FTDI's second channel | the SPI flash (`PROG=1`); SRAM loading does not configure this board |
| **Icepi Zero** | ECP5-25F, `25k-CABGA256` | 24 288 | 28 MULT18 | 1 Mbit | 50 MHz oscillator, a PLL to 25 MHz | a UART at 1 000 000 | `openFPGALoader -b icepi-zero`, SRAM or flash |
| **Go Board** (Nandland) | iCE40 HX1K, `hx1k-vq100` | 1 280 | none | 64 kbit | 25 MHz oscillator straight in, no PLL | a UART at 115 200 | the SPI flash |

The fifth is the **DE10-Lite** (Terasic): a MAX 10 10M50 — 49 760 logic
elements, 144 multipliers, 182 M9K blocks of 9 kbit — with a 50 MHz
oscillator straight in and no USB-UART of its own, so the host link is a
3.3 V USB-serial cable on two GPIO header pins (`host_rx` on GPIO_0, `host_tx`
on GPIO_1, ground on pin 12), loaded over the USB-Blaster by `quartus_pgm`.
Its preset `de10lite`, the `Quartus` flow and its gate are in the
repository; Quartus itself was not on the machine that wrote them, so the
first compile on the bench is what checks the pins against the maker's
`DE10_LITE_golden_top.qsf` and the gate against a real `.fit.summary`.

Every number in the table is the part's, stated in `hdl/Warp11/Board.fs` as
`PartFacts`, and the post-build gate confirms it from the toolchain's own
report: a board file with a wrong count does not survive its first build.

## What a design asks for, and what a board offers

A design declares **needs** (`Warp11.BoardTop`):

- `streamIn` / `streamOut` — rows in and out, typed by their layout;
- `valueIn` / `valueOut` — a value the host sets, a value the design reports;
- `tableIn` — a table the host fills;
- `surfaceOut` — a surface the design keeps live, a Life world or a
  framebuffer, taken whole when the host asks.

A board offers **carriers** for them, and the mapping's **path** says which:

| path | rows in | rows out | where it runs |
|---|---|---|---|
| `memory` | from the host's DDR | to the host's DDR | a board with a processing system — the KV260 |
| `count` | a count the fabric mints | to the host's DDR | the KV260: a frame the design draws |
| `link` | the host's words, played over the UART | frames on the same UART | every board with one wire |
| `countlink` | a count | frames on the UART | the same boards: a frame over the wire |
| `pins` | the I2S converter on the board's header | the converter | a board with a Pmod I2S2 plugged in — not part of the release's examples |
| `registers` | — | — | a design with no stream: `hello` |

Values always travel in the register map, over AXI-Lite or the UART, and a
reported bit may also be bound to an LED (`"bind": { "led1": ["OnPin:led1"] }`).
A surface goes where the rows out would: three slots in DDR on the KV260, the
host capturing the freshest complete one; snapshots pulled over the UART
elsewhere.

**A harness** is something plugged into one of the board's sockets — a Pmod
I2S2 in the iCEBreaker's PMOD1A — and a mapping's `plugged` list puts it there,
after which the harness's pins are the board's. Nothing in the release matrix
plugs one in: every example reaches every board over what it already has, its
USB and its LEDs.

**A size** — Life's world, Mandelbrot's lanes — is derived from the part's
facts unless the mapping names it: `"sizes": { "world": 16 }`.

## The tools, once per machine

- **The open flow** (iCEBreaker, Go Board, Icepi): yosys, nextpnr and the
  packers, pinned. `hdl/tools/install-oss-cad-suite.sh` puts the suite under
  `~/tools`, which every generated `build.sh` looks for first; a distribution
  yosys can be years older and fails in the DSP inference pass on any design
  with a wide multiply.
- **Vivado** (KV260): 2025.2, installed where Xilinx puts it; a generated
  `build.sh` sources `settings64.sh` itself. `docs/dev-workflow.md` is the
  board side — `ssh` without passwords, `xmutil`, the kernel modules.
- **Quartus Prime Lite** (DE10-Lite): installed under `~/tools/intelFPGA_lite`,
  which a generated `build.sh` looks for (or `QUARTUS_BIN`), with the
  USB-Blaster II udev rule. The flow is `quartus_sh --flow compile`, the load
  `quartus_pgm -m jtag`, SRAM only.

`iceprog` wants a udev rule on the iCEBreaker and the Go Board:
`hardware/icebreaker/README.md` has it and the two traps that cost a morning
(`iceprog -S` exits 0 and does not configure the iCEBreaker; the board arrives
with no headers fitted).

## The first design on a board

`hello`: the LEDs showing a register the host writes over the board's link.
It needs nothing but the board and its USB, and it proves the clock, the link
and the LEDs together. Its mapping files are beside `hdl/Warp11.Designs`:

```sh
cd hdl
dotnet run --project Warp11.Designs -- hello Warp11.Designs/hello.goboard.json out/
cd out && PROG=1 ./build.sh
warp11_regs --serial /dev/ttyUSB1 --baud 115200 --layout hello_uart_layout.rs --set pattern=5
```

`build.sh` ends with the gate, which reads the toolchain's own report and
writes `build/report.json`: cells, block RAMs and multipliers used of the
part's, and the clock achieved against the target. Measured:

| board | hello | cells | clock achieved against target |
|---|---|---|---|
| iCEBreaker | `HelloUart` | 192 / 5 280 | 60.2 MHz / 24 |
| Icepi | `HelloUart` | 143 / 24 288 | 212 MHz / 25 |
| Go Board | `HelloUart` | 199 / 1 280 | 166.6 MHz / 25 |
| KV260 | `HelloAxi` | directory written; Vivado run is the bench's | |
| DE10-Lite | `HelloUart` | directory written; Quartus run is the bench's | |

Then the identity — `passthru` — over the same link, which proves the batch
path with nothing in it, then whatever you came for.

## Building anything for a board

Every design project answers the same verb with a mapping file:

```sh
dotnet run --project Warp11.Placement  -- build examples/gain.json examples/gain.icepi.json out/   # a drawn design
dotnet run --project Warp11.Effects    -- build tone Warp11.Effects/tone.goboard.json out/        # a typed one
dotnet run --project Warp11.GoL.App    -- build Warp11.GoL/life.icepi.json out/
dotnet run --project Warp11.Mandelbrot -- bitstream Warp11.Mandelbrot/mandelbrot.icepi.json out/
cd out && ./build.sh
```

The directory is the same shape for every board on the same tool — the top's
Verilog, the constraints, the flow, the Rust seam the host tools read — and
`build.sh` is the whole of the toolchain run. On the KV260 it also writes the
block design, the device-tree overlay and the app's packaging, and the app is
named for the top (`life64x64_batch`).

Then the host: `warp11_regs` sets and reads registers by name over either
link; `warp11_batch` runs a WAV, a PGM or a count through the design over
DDR, over the UART, or against the Sim through a bridge, and the check that
matters is that **the same file in gives the same bytes out on every board as
in the Sim**.

## What fits where

The gate's numbers for this release's examples, through the real toolchains:

| design | iCEBreaker (5 280) | Icepi (24 288) | Go Board (1 280) |
|---|---|---|---|
| passthru, tone | 656 (tone) | fits | 837 passthru, 629 tone |
| gain | 953, 3 of 30 block RAMs | fits | ✗ 2 096 cells |
| adder | fits | fits | 916 |
| three-band EQ | ✗ 5 489 | fits | ✗ |
| twice | ✗ 8 825 | fits | ✗ |
| blur (a 64-pixel row) | ✗ 11 364, 32 of 30 block RAMs | fits | ✗ |
| Life | 8×8 derived | 32×32: 18 697, 49 MHz; 64×64 refused at 75 109 | ✗ 8×8 is ~1 980 |
| Mandelbrot | ✗ a lane takes 12 multipliers | 2 lanes: 3 812 cells, 24 multipliers, 62 MHz | ✗ no multipliers |

A ✗ is the gate's or the sizing rule's refusal, with its number; nothing on
this page was estimated. The KV260 holds all of them, and GEP runs there
alone in this release, over DDR.
