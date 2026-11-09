# Hello on every board

The first design to run on any board is `hello`: the board's LEDs showing a
register the host writes over the board's link. It needs nothing but the
board and its USB, and it proves the three things everything else depends on
— the clock, the link and the pins — together, before anything is asked of
them.

This page puts it on one board, then moves it to the other four. The point
of the second half is what does **not** change: the design, the verb, the
host tool. A board is a file, and switching boards is switching which file
you pass.

Fifteen minutes a board once the toolchain is installed. [Boards](boards.md)
has the tools, once per machine; this page assumes they are there.

## Before you start

Build the host tools once. They live in the Rust runtime and read the seam
the build writes, so they need telling nothing but which file:

```sh
cd runtime
cargo build --release
export PATH="$PWD/target/release:$PATH"      # warp11_regs, warp11_batch, warp11_snapshot
```

And the .NET side, from `hdl/`, which is where every command below runs:

```sh
export PATH="$HOME/.dotnet:$PATH"
cd hdl
dotnet build Warp11.sln
```

## The design

`hdl/Warp11.Designs/Hello.fs`, the whole of it that matters:

```fsharp
let hello: BoardTop.Design =
    BoardTop.sized "Hello" (fun fit ->
        let leds = ledsOf fit.board
        let width = max 1 leds.Length

        BoardTop.design
            "Hello"
            (fun n ->
                { pattern = n.valueIn ("pattern", unsignedInt width)
                  lights = [ for led in leds -> n.valueOut (led, unsignedInt 1) ] })
            (fun io ->
                let pattern = wire "pattern_bits" width
                io.pattern ==> pattern
                io.lights |> List.iteri (fun k light -> light (slice k k pattern))))
```

Three things to notice, because they are what the rest of the page leans on:

- **It never names a board.** It asks the board it is being built for what
  LEDs it has (`ledsOf fit.board`) and declares one reported bit per LED,
  named for the LED. A board with ten LEDs gets a ten-bit `pattern`; one with
  two gets two bits; one with none gets a one-bit register the host reads
  back.
- **It declares needs, not pins.** `valueIn` is a value the host sets,
  `valueOut` a value the design reports. Which register, which bus, which pin
  — none of that is here.
- **It is sized by the part**, through `BoardTop.sized`. Life's world and
  Mandelbrot's lanes are sized the same way; here the size is the LED count.

## The mapping

A mapping binds the design's needs to what a board offers. Hello's is
code, beside the design in `Hello.fs`: a function of the board, and the one
line that says which board.

```fsharp
let mappingFor (board: Board) : Mapping.Mapping =
    { board = board
      path = { viaRegisters with bindings = viaRegisters.bindings @ [ for led in ledsOf board -> { need = ByName led; carrier = OnPin led } ] }
      plugged = []
      sizes = [] }

let board = iceBreakerAt 24_000_000

let mapping = mappingFor board
```

The host's registers carry the values, and each reported bit is bound to
the LED of its name. `-- mapping` prints what the board gets from it; the
iCEBreaker's, shortened to its shape:

```json
{
  "board": {
    "name": "icebreaker",
    "part": { "family": "Ice40", "device": "up5k", "package": "sg48", "facts": { "cells": 5280, "…": "…" } },
    "tool": "OpenFlow",
    "clock": { "source": "Crystal", "hz": 12000000 },
    "fabricHz": 24000000,
    "host": { "driver": "Uart", "baud": 115200 },
    "loading": { "kind": "Sram" },
    "connectors": [
      { "role": "ClockIn",  "pins": { "clk12": { "pin": "35" } } },
      { "role": "HostUart", "pins": { "host_rx": { "pin": "6" }, "host_tx": { "pin": "9" } } },
      { "role": "Leds",     "pins": { "ledr_n": { "pin": "11", "activeLow": true },
                                      "ledg_n": { "pin": "37", "activeLow": true } } }
    ]
  },
  "path": "registers",
  "bind": { "ledr_n": ["OnPin:ledr_n"], "ledg_n": ["OnPin:ledg_n"] }
}
```

The `board` block is the board as facts: the part and what it holds, the
clock, how the host reaches the design, how a bitstream gets loaded, which
pins carry which role. `"path": "registers"` says the design has no streams,
only values in the register map. `bind` puts each reported bit on the pin of
its name. The `activeLow` on the iCEBreaker's LEDs is the board's business:
the generator inverts the bit there, so a `1` in `pattern` lights an LED on
every board.

Print it, change the line to `let board = de10Lite`, print it again, and
put the two side by side:

```sh
dotnet run --project Warp11.Designs -- mapping > /tmp/a.json
# Hello.fs: let board = de10Lite
dotnet run --project Warp11.Designs -- mapping > /tmp/b.json
diff /tmp/a.json /tmp/b.json
```

The `board` block differs, the `bind` lists the LEDs each board has, and
nothing else does. The board is a value: the five built here are in
`hdl/Warp11/Board.fs` — `kv260`, `iceBreakerAt 24_000_000`, `icepi`,
`goBoard`, `de10Lite` — and one that is not there is the same record,
written in your own project ([How to add a new board](add-a-board.md)).
Nothing here is a stored copy of a board: where the design is code, so is
its mapping, and a mapping file exists only for a design that is itself a
file, the canvas's. Put the line back to the iCEBreaker before going on.

## The first board: the iCEBreaker

The udev rule first, once (`hardware/icebreaker/README.md` has it and the
two traps). Then:

```sh
dotnet run --project Warp11.Designs -- hello out/hello-icebreaker
```

It prints the files it wrote and the line to run. The directory is the whole
build input:

| file | what |
|---|---|
| `HelloUart.v` | the design with its register slave, emitted |
| `hello_uart_top.v` | the board wrapper: the PLL from 12 to 24 MHz, reset, the pins |
| `hello_uart_top.pcf` | every pin, from the board block |
| `hello_uart_layout.rs` | the seam: the register map as Rust constants, which the host tool reads |
| `build.sh` | the toolchain run, the gate, the load |

Build and load. `PROG=1` writes the SPI flash, which is the only loading that
configures this board; the plain SRAM load exits 0 and does nothing here:

```sh
cd out/hello-icebreaker && PROG=1 ./build.sh
```

The run ends with the gate, which reads nextpnr's own report and writes
`build/report.json`:

```
== gate ==
  cells           192 /   5280
  blockRams         0 /     30
  multipliers       0 /      8
  clock         60.16 MHz against 24.00 - 2.51x
```

A gate that refuses — too many cells, a clock short of the target, a part
fact the tool disagrees with — stops before the load, with the number.

Now the host. The iCEBreaker's UART is the FTDI's second channel, which
Linux usually enumerates as `/dev/ttyUSB1`; the baud is the board file's:

```sh
warp11_regs --serial /dev/ttyUSB1 --baud 115200 --layout hello_uart_layout.rs --set pattern=3 --get pattern
```

Both LEDs light, and the read back prints `pattern = 3`. `--set pattern=1`
lights the red one alone, `--set pattern=0` puts them out. That is the clock
(the PLL locked and the UART's divider is right, or no byte would arrive),
the link (the register slave decoded the write and answered the read) and
the pins (the LEDs are where the board file says) in one line.

## Switching boards

Everything above is the same on the other four. `Hello.board` names a
different value; `build.sh` knows its own toolchain and loader; the seam is
named the same way; the host tool is the same tool. What differs is on the
table:

| board | `Hello.board` | LEDs → `pattern` bits | build and load | the link |
|---|---|---|---|---|
| iCEBreaker | `iceBreakerAt 24_000_000` | 2 | `PROG=1 ./build.sh` — the flash; SRAM does not configure it | `--serial /dev/ttyUSB1 --baud 115200` |
| Go Board | `goBoard` | 4 | `PROG=1 ./build.sh` — the flash | `--serial /dev/ttyUSB1 --baud 115200` |
| Icepi Zero | `icepi` | 5 | `PROG=sram ./build.sh` (volatile) or `PROG=1` (flash), through `openFPGALoader -b icepi-zero` | `--serial <the device the board enumerates> --baud 1000000` |
| DE10-Lite | `de10Lite` | 10 | `PROG=1 ./build.sh` — `quartus_pgm` over the USB-Blaster, volatile | `--serial /dev/ttyUSB0 --baud 115200`, a 3.3 V USB-serial cable on the GPIO header |
| KV260 | `kv260` | none: a 1-bit register, read back | `./build.sh` writes the app; then the three lines it prints | `--uio hello_axi`, on the board |

Each one, in the order the toolchains are cheapest to learn:

**Go Board.** Same flow as the iCEBreaker, the HX1K instead of the UP5K, the
25 MHz oscillator straight in with no PLL. Four LEDs, so `pattern` is four
bits. In `Hello.fs`, `let board = goBoard`; then:

```sh
dotnet run --project Warp11.Designs -- hello out/hello-goboard
cd out/hello-goboard && PROG=1 ./build.sh
warp11_regs --serial /dev/ttyUSB1 --baud 115200 --layout hello_uart_layout.rs --set pattern=5 --get pattern
```

LEDs 1 and 3 light. The gate's numbers: 199 of 1 280 cells, 166.6 MHz
against 25.

**Icepi Zero.** The ECP5 flow — yosys, nextpnr-ecp5, `ecppack` — and
`openFPGALoader`, which knows the board by name from the board file's
`boardPart`. A volatile load is enough to see it work. Five LEDs; the link
runs at a million baud. `let board = icepi`; then:

```sh
dotnet run --project Warp11.Designs -- hello out/hello-icepi
cd out/hello-icepi && PROG=sram ./build.sh
warp11_regs --serial /dev/ttyACM0 --baud 1000000 --layout hello_uart_layout.rs --set pattern=21 --get pattern
```

`ls /dev/tty*` before and after plugging the board in tells you the device.
Three of the five light, alternating. The gate: 143 of 24 288 cells, 212 MHz
against 25.

**DE10-Lite.** Quartus, and the first board here with no USB-UART of its
own: the host link is a 3.3 V USB-serial cable on three GPIO header pins
(`host_rx` on GPIO_0, `host_tx` on GPIO_1, ground on pin 12 —
`hardware/de10lite/README.md` has the picture, the USB-Blaster udev rule,
and the pins to check against the maker's `.qsf` before the first load).
Ten LEDs. `let board = de10Lite`; then:

```sh
dotnet run --project Warp11.Designs -- hello out/hello-de10lite
cd out/hello-de10lite && PROG=1 ./build.sh
warp11_regs --serial /dev/ttyUSB0 --baud 115200 --layout hello_uart_layout.rs --set pattern=682 --get pattern
```

Every other LED lights. The gate reads Quartus's fitter summary and timing
report: 216 of 49 760 logic elements, 218 MHz against 50. The load is over
the USB-Blaster into SRAM, so a power cycle clears it.

**KV260.** The board with a processing system, so the host is on the chip:
the link is AXI-Lite at `0xB0000000`, Linux reaches it through a uio device,
and the LEDs are the processing system's, so `hello` here is the register
alone, one bit wide. `build.sh` runs Vivado, packages the app — bitstream,
device-tree overlay, `shell.json` — and prints the three lines that put it
on the board. `let board = kv260`; then:

```sh
dotnet run --project Warp11.Designs -- hello out/hello-kv260
cd out/hello-kv260 && ./build.sh
# then the scp, the install and `xmutil loadapp hello-axi` it prints
```

On the board, the same tool over the other transport:

```sh
warp11_regs --uio hello_axi --layout hello_axi_layout.rs --set pattern=1 --get pattern
```

`pattern = 1` back is the whole proof: the bitstream loaded, the overlay
pinned the clock the design was built for, the slave decoded the address.
`docs/dev-workflow.md` is the board side — `ssh`, `xmutil`, the kernel
modules — and `runtime/README.md` the cross-compile of `warp11_regs` for
the Arm, one plain `cargo build` with no cross-gcc.

## What you changed

Nothing in the design, nothing in its mapping, and nothing in the host
program. Across five boards, three toolchains and two transports, the
differences were:

- the one line in `Hello.fs` that names the board;
- the `PROG` the loader wants, which the table gives and `build.sh` already
  knows how to do;
- the device the host tool opens, which is the cable's business, not the
  design's.

The seam made the rest invisible. `hello_uart_layout.rs` and
`hello_axi_layout.rs` are written by the same register-map definition that
elaborated the slave, so `warp11_regs` set `pattern` by name on every board
without knowing an address — and could not have been handed a stale one.

## When it does not light

- **The gate passed, the load ran, nothing happened.** On the iCEBreaker,
  `PROG=sram` exits 0 and does not configure the board; use `PROG=1`. On the
  KV260, `sudo xmutil listapps` says which app is loaded; a reboot loads the
  default one, so `unloadapp` then `loadapp` again.
- **The write times out.** The wrong channel or the wrong baud. The
  iCEBreaker and the Go Board put the UART on the FTDI's *second* channel.
  The baud is the board file's `host.baud`, and the wrapper's divider is
  computed from `fabricHz`, so the file and the cable must agree, not the
  file and a guess.
- **The read comes back, the LEDs are wrong.** The board's pins, or their
  polarity. Compare the board's `Leds` connector in `Board.fs` against the
  maker's pin table; an LED that lights when it should be out wants
  `activeLow = true`.
- **It lights on one board and not another.** That is what this page is
  for. The design did not change, so the difference is in the board's
  record or on the bench.

## Then

`passthru` — the identity over the same link, rows in and the same rows
out — proves the batch path with nothing in it, and
[Put a design on a board](cookbook/mapping-and-harness.md) is the page for
it. After that, whatever you came for: [Boards](boards.md) has what fits
where, with the gate's numbers, and [How to add a new board](add-a-board.md)
is this page's board block written for a board that is not here.
