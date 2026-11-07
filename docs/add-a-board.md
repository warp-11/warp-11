# How to add a new board

A board is a file. A design is built for it by a mapping that names the
board and nothing else, so adding a board is writing that file, and the test
of having written it correctly is that a design you already have builds for
it with no change to the design and no change to Warp 11.

This page is the Nandland Go Board being added, as it was done. It is a
useful example because its part — an iCE40HX1K, the other end of the iCE40
line from the iCEBreaker's UltraPlus — was one the library had never seen,
so every fact about it had to be stated. A board on a part the library
already knows is the same page with the facts left out.

## 1. The board file

Every number in it comes from a file the board's maker publishes — a `.pcf`,
an `.lpf`, an `.xdc` — or from the schematic. Do not type a pin from memory.
This is the Go Board's, abridged only in the socket's positions:

```json
{
  "name": "goboard",
  "part": {
    "family": "Ice40",
    "device": "hx1k",
    "package": "vq100",
    "facts": {
      "processingSystem": false,
      "lutRam": false,
      "multipliers": 0,
      "cells": 1280,
      "blockRamBits": 65536
    }
  },
  "tool": "OpenFlow",
  "clock": { "source": "Oscillator", "hz": 25000000 },
  "fabricHz": 25000000,
  "host": { "driver": "Uart", "baud": 115200 },
  "loading": { "kind": "Sram" },
  "connectors": [
    { "role": "ClockIn",  "pins": { "clk25": { "pin": "15" } } },
    { "role": "HostUart", "pins": { "host_rx": { "pin": "73" }, "host_tx": { "pin": "74" } } },
    { "role": "Leds",     "pins": { "led1": { "pin": "56" }, "led2": { "pin": "57" },
                                    "led3": { "pin": "59" }, "led4": { "pin": "60" } } }
  ],
  "sockets": [
    { "name": "PMOD", "kind": "pmod",
      "positions": { "1": { "pin": "65" }, "2": { "pin": "64" }, "3": { "pin": "63" }, "4": { "pin": "62" },
                     "7": { "pin": "78" }, "8": { "pin": "79" }, "9": { "pin": "80" }, "10": { "pin": "81" } } }
  ]
}
```

Field by field:

- **`part`** is what the toolchain is told, and what the part has.
  `family` picks the toolchain's dialect — `UltraScalePlus` for Vivado,
  `Ice40` or `Ecp5` for yosys and the matching nextpnr — and the emitter and
  the build script follow from it. `device` and `package` are what that tool
  is given: nextpnr's `--hx1k --package vq100`; for Vivado `device` is the
  `-part` string and `package` is empty. `boardPart` is the toolchain's own
  name for the board where it has one (Vivado's board part, openFPGALoader's
  `-b`), and is left out for a bare part programmed by `iceprog`.
- **`facts`** are the part's, from its datasheet: whether a processing
  system sits beside the fabric, whether the fabric has LUT RAM, how many
  hard multipliers it has, its logic cells in whatever the tool's report
  counts, and its block RAM in bits. A design that sizes itself reads them
  first; the board check refuses what they rule out; the post-build gate
  confirms them from the toolchain's report. **On a part the library knows**
  (`xck26-sfvc784-2LV-c`, `up5k`, `hx1k`, `25k`) **the whole `facts` object
  may be left out** and the library's own are used. On any other part it is
  required, and the reader says which five fields when it is missing.
- **`clock` and `fabricHz`.** An `Oscillator` drives the clock pin at the
  rate the fabric runs at, and `fabricHz` must equal it: there is no PLL
  between them. A `Crystal` is a rate the wrapper takes through a PLL to
  `fabricHz`, which is then yours to choose. The Go Board's HX1K has no PLL
  bonded on this package, so it is an `Oscillator`, and the wrapper the open
  flow writes wires the pin straight to `clk` with no PLL primitive.
- **`host`** is how the host reaches the register map: `Uart` and a baud
  through a USB-serial bridge, or `AxiLite` and a base address on a part
  with a processing system. The Go Board's FT2232H has two channels, A for
  the flash and B for the UART; the design never sees which.
- **`loading`.** `Sram` and `Flash` are the open flow's, through `iceprog`
  or openFPGALoader; `OsApp` is a Zynq's FPGA manager.
- **`connectors`** are the pins behind each device role the board carries
  on its own, keyed by role so the port names are the role's, not the
  board's. The names matter: `host_rx` is into the fabric and `host_tx` out,
  whichever way the maker's file names them. An LED pin may say
  `"activeLow": true` when it is lit by driving low — a fact about the
  wiring, and the top inverts for it, so a design never knows. The Go
  Board's are lit high.
- **`sockets`** are where a harness plugs in, as the package pin behind each
  position. A Pmod is positions 1–4 and 7–10; a harness written for another
  board's Pmod plugs into this one unchanged, because the two meet at the
  position number.

What the board has that no role reads yet — the Go Board's buttons,
seven-segment digits and VGA — is not in the file. A connector needs a role
to be keyed by, and a role exists when a design declares a need for it.

## 2. Build for it

Name the file where a preset's name would go:

```sh
dotnet run --project Warp11.Placement -- build my.json goboard.json pins out/
cd out && ./build.sh
```

Or make it the board of the design's mapping — `my.goboard.json` beside
`my.json`, with the board inline — and `-- build my.json out/` finds it. A
harness plugged into one of the sockets goes in the mapping's `plugged`
list, and the `warp11_regs` and `warp11_batch` drivers reach the design
over whatever `host` says, with nothing to tell them.

The directory `build.sh` writes is the same shape for every board on the
same tool, and the last thing it does is the gate: it reads the toolchain's
own report and writes `build/report.json` — cells, block RAMs and
multipliers used against the part's total, and the achieved clock against
`fabricHz`. It refuses three things, each naming the number: a resource
over the part's total, a clock short of the target, and a fact in your
board file that disagrees with what the tool says the part has. So a board
file with a wrong number does not survive its first build. The Go Board's
first build, a design streaming through a Pmod I2S harness with one bit on
an LED, came back as:

```
== gate ==
  cells           439 /   1280
  blockRams         0 /     16
  multipliers       0 /      0
  clock        119.19 MHz against 25.00 - 4.77x
```

## 3. What is not a file

Three things need Warp 11's source, and the first two are about a
toolchain rather than a board:

- **A new emitter dialect** — a tool that reads a different memory
  attribute, as Quartus reads `ramstyle` where Vivado and yosys read
  `ram_style`. That is a case in `Verilog.fs`.
- **A new flow** — the files a tool builds from and the loader it programs
  with. That is `Build.fs`, and a `Family` case to name it by. *How to add a
  new toolchain* is the page for both.
- **A new device role.** Roles are what designs declare needs against, so a
  role no design knows has no reader. One arrives with its first design.

A board on a known tool, with roles that exist, is never source.

## 4. How this repository keeps its own boards

The boards built here are F# values in `hdl/Warp11/Board.fs` — the same
record the file is read into — because the tests and the examples want them
by name, and `presets` is the list a mapping file or the canvas looks a name
up in. A part is a value too, so a second board on the same part starts from
it:

```fsharp
let goBoard =
    let bare (pin: int) = { pin = string pin; standard = None; activeLow = false }

    { name = "goboard"
      part = hx1k
      tool = OpenFlow
      clock = Oscillator 25_000_000
      fabricHz = 25_000_000
      audioClockHz = None
      hostMemory = None
      host = UartAt 115_200
      loading = Sram
      connectors =
        [ { role = ClockIn; pins = [ "clk25", bare 15 ] }
          { role = HostUart; pins = [ "host_rx", bare 73; "host_tx", bare 74 ] }
          { role = Leds; pins = [ "led1", bare 56; "led2", bare 57; "led3", bare 59; "led4", bare 60 ] } ]
      sockets =
        [ { name = "PMOD"; kind = "pmod"
            positions = [ 1, bare 65; 2, bare 64; 3, bare 63; 4, bare 62; 7, bare 78; 8, bare 79; 9, bare 80; 10, bare 81 ] } ] }
```

Its test, in `hdl/Warp11.Designs.Tests/BoardTopTests.fs`, is the same four
claims for every board, and the shape to copy if you keep yours in F#:

1. `checkBoard` accepts it, and refuses what the board cannot be — the Go
   Board at any `fabricHz` but 25 MHz.
2. It round-trips through a board file, with its facts written, and reads
   back with them left out.
3. A design that exists builds for it with a mapping and nothing else: the
   suite's `lit` through a Pmod I2S harness, its reported bit on `led1`, the
   Sim showing the LED driven the way the wiring says.
4. The build directory says what the board said: the part, the package, the
   loader, `-dsp` exactly when the part has multipliers, no PLL when the
   oscillator is the fabric clock, every pin from the constraints file.

## 5. The first build on the bench

The first design to run on any board is `hello`: the LEDs showing a
register the host writes over the board's link. It needs nothing but the
board and its USB, and it proves the clock, the link and the LEDs together
before anything else is asked of them. Its mapping is the board plus one
line per LED, binding the bit of that name to the pin of that name:

```json
{ "board": { … }, "path": "registers",
  "bind": { "led1": ["OnPin:led1"], "led2": ["OnPin:led2"], "led3": ["OnPin:led3"], "led4": ["OnPin:led4"] } }
```

```sh
dotnet run --project Warp11.Designs -- hello hello.goboard.json out/
cd out && PROG=1 ./build.sh
warp11_regs --serial /dev/ttyUSB1 --baud 115200 --layout hello_uart_layout.rs --set pattern=5
```

`hello.{kv260,icebreaker,icepi,goboard}.json` beside `Warp11.Designs` are
the four here; the Go Board's, measured, is in the boards table.

A board is added when `hello` is on it and its `report.json` — cells and
block RAMs against the part, the achieved clock against `fabricHz` — is in
its row of the boards table.
