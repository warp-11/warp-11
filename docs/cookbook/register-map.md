# How do I create a register map?

**You want:** a set of addresses a host program can read and write, so a Linux
process on the board can start your design, configure it, and read results
back.

**You write one definition.** It elaborates the AXI-Lite slave in the fabric
*and* generates the Rust constants the host compiles against, so the two cannot
disagree about an offset. That is the whole point of the type — a mismatched
offset is the single most reliable way to lose an afternoon on an FPGA, and
there is no second place for it to be written down.

## The shortest thing that works

```fsharp
open Warp11

module BlinkerMap =
    let id = roConst "id" 0x0UL 0xB11EDUL
    let enable = rwReg "enable" 0x4UL 1 0UL
    let count = roField "count" 0x8UL 0 24

    let map =
        { apertureAddrWidth = 4
          entries = [ id; enable; count ] }
```

The entries are **values you hold on to**: every later access is keyed by the
entry itself, never by its name spelled a second time.

### Why you declare the aperture rather than have it computed

`apertureAddrWidth = 4` means a 16-byte window — four words, which is what these
three entries need. It looks derivable, and it deliberately is not, because
**the aperture is a port width**:

```verilog
input [3:0] s_axi_awaddr, ... input [3:0] s_axi_araddr
```

That makes it a contract with two things written outside F# — the Vivado block
design's address segment, and the device tree's `reg = <… 0x1000>`. If it were
computed from the entry list, adding one status field would silently widen
`s_axi_awaddr`, the block-design wrapper would quietly stop matching, and
nothing would say so. Because you declare it, a register that does not fit is an
elaboration error naming the register: **the declaration is the check.**

It is also a decode *window*, not a packing problem. A round 256 bytes with room
to grow is usually what you want, rather than the tightest fit that changes
shape every time you add a counter.

### Why the `id` register

Nothing requires it and the map works without one. Every driver in this
repository has one anyway, because each way of getting this wrong is either
silent or fatal:

- **An unmapped offset inside the aperture reads as zero, not a fault.** So a
  read of 0 cannot distinguish "wrong bitstream" from "a register that happens
  to be 0".
- **One bitstream is loaded at a time, and the tooling will load the wrong one.**
  The longest bring-up saga in this project's history ended up being exactly
  that, hidden because two device-tree files named the UIO node identically.
- **A wrong base address hangs the whole board** — power cycle only. An identity
  check turns a hang into an error message.

```rust
let found = window.read32(layout::ID_OFFSET)?;
if found != layout::ID_MAGIC {
    return Err(MandelFrameError::WrongId { found });
}
```

A `roConst` owns only the **read** side of its word and a `pulseBit` only the
**write** side, so the two can share an offset — the ID-overlay pattern, which
is why Mandelbrot's generated layout reads:

```rust
pub const ID_OFFSET: usize = 0x00;    // reads as ID; writes pulse start
pub const START_OFFSET: usize = 0x00;
```

## Wiring it into a design

Two calls, and **they go in different places**. `axiLiteSlavePorts` declares the
`s_axi_*` boundary, so it belongs in the io factory; `regMapSlave` builds the
register file over those ports, so it belongs in the body:

```fsharp
let blinkerAxi =
    defModuleClocked
        axiClock
        "BlinkerAxi"
        (fun p -> (axiLiteSlavePorts p BlinkerMap.map.apertureAddrWidth, p.outPort "led" 1))
        (fun (slavePorts, led) ->
            let regs = regMapSlave slavePorts BlinkerMap.map

            let counter = reg "counter" 24
            If (regs.value BlinkerMap.enable) (fun () -> counter + 1UL ==> counter)

            regs.drive BlinkerMap.count counter
            slice 23 23 counter ==> led)
```

`defModuleClocked axiClock` gives the module `s_axi_aclk` / `s_axi_aresetn`
instead of the default `clk` / `rst`, which is what an AXI peripheral is
clocked by.

Calling `axiLiteSlavePorts` from the body is an error — a module's boundary
seals when its io factory returns, and the message names the port.

**`axiLiteSlavePorts` is only a boundary.** It declares the seventeen `s_axi_*`
wires and hands them back as a record; there is no logic in it. `regMapSlave` is
the body half that turns those wires into a register file, an address decode, a
read mux and the interrupt OR.

## What if the design is not on AXI?

**Your map and your design body do not change.** `regMapSlave` returns a
`SlaveRegs`, and that type is bus-neutral by construction:

```fsharp
{ pulse; value; drive; setBit; window; driveWindow; irq }
```

Not one of those mentions AXI. The body only ever calls `regs.value`,
`regs.drive` and friends, so it never learns which bus is underneath. A
different bus is a different *producer* of `SlaveRegs` — a peer of
`regMapSlave`, not a change to the map. Two lines move:

```fsharp
(fun p -> (axiLiteSlavePorts p map.apertureAddrWidth, …))   →   spiSlavePorts p …
let regs = regMapSlave slavePorts map                        →   spiSlave slavePorts map
```

**This is not hypothetical — it is the iCE40 path.** An iCEBreaker has no ARM
cores and no AXI interconnect, so a register seam there is SPI (or UART over the
FT2232H for bring-up). `spiSlaveOf : RegMap -> SlaveRegs` is planned as the
**third consumer of the same map**, beside `regMapSlave` and `regMapRsLines`,
and it does not exist yet.

What survives the move: the map, the design body, and **the generated Rust
layout** — 32-bit words and `% 4` alignment are kept deliberately so the layout
file is unchanged across buses, which means the host driver is too.

The part that does not survive for free: `RwWindow` arbitration assumes a bus
with a read-address handshake to borrow the port during. A map with no windows
sidesteps it entirely.

## The entry kinds

| constructor | direction | what it is |
|---|---|---|
| `rwReg name offset width init` | host writes, design reads | a setting. Reads back what was written. **Owns its whole word.** |
| `roField name offset bitOffset width` | design drives, host reads | a status field. Several pack into one word at different bit offsets. |
| `roConst name offset value` | host reads | a fixed identifying pattern. Read it first in the driver — see below. |
| `pulseBit name offset bit` | host writes | a **one-cycle strobe**, not a level. This is what `start` wants. |
| `w1cBit name offset bit` | design sets, host clears | an interrupt-status bit. Every one joins the map's `irq` line. |
| `rwWindow name offset words` | host writes, design reads | a block of words backed by a mem — a coefficient table, a program. |
| `roWindow name offset words` | design writes, host reads | a block the design fills — a result buffer, a trace. |

**Use `pulseBit` for anything the host *does* rather than *sets*.** A level
`start` needs the host to write it back to zero, and a design that misses the
clear runs twice.

**Always include a `roConst` identity register.** Every driver here reads it on
open and refuses to continue if it does not match. The alternative — talking to
the wrong bitstream — presents as a hang rather than an error, and a hang on
this board needs a power cycle.

## Reaching the registers from the body

`regMapSlave` hands back one record, keyed by entry:

| | |
|---|---|
| `regs.value entry` | read a `rwReg` — an ordinary `Expr` |
| `regs.drive entry expr` | drive a `roField`. **Exactly once**; never driving it fails at emission |
| `regs.pulse entry` | the one-cycle strobe from a `pulseBit` |
| `regs.setBit entry expr` | set a `w1cBit` from hardware. Set wins over a same-cycle host clear |
| `regs.window entry addr` | the arbitrated read port onto a `rwWindow` — call it **once** |
| `regs.driveWindow entry` | the `Mem` behind a `roWindow`, for the design to write |
| `regs.irq` | the OR of every `w1cBit`, for the board's interrupt line |

## Generating the Rust seam

`regMapRsLines` turns the same map into constants. Emit them into the driver
crate and commit the result, so a layout change shows up in the driver's diff:

```fsharp
let layout = regMapRsLines BlinkerMap.map
System.IO.File.WriteAllLines(path, layout)
```

For `id`, `enable` and `count` that yields:

```rust
pub const APERTURE_BYTES: usize = 16;
pub const ID_OFFSET: usize = 0x000;
pub const ID_VALUE: u32 = 0x000b11ed;
pub const ENABLE_OFFSET: usize = 0x004;
pub const COUNT_OFFSET: usize = 0x008;
pub const COUNT_SHIFT: u32 = 0;
pub const COUNT_MASK: u32 = 0xffffff;
```

Names become `UPPER_SNAKE`. A `pulseBit` or `w1cBit` also emits `_BIT`, a
`roField` emits `_SHIFT` and `_MASK`, a window emits `_WORDS`.

## Rules that bite, all checked at elaboration

The map validates itself, so these are error messages rather than silicon
surprises. Each names the register:

- **Offsets are word-aligned** and inside the aperture.
- **A `rwReg` owns its word alone.** It reads back what was written, so nothing
  else can occupy that word.
- **Read-side fields may not overlap** — the check is per *bit*, and the error
  names both owners and the bit. Packing several `roField`s into one word is
  expected; overlapping them is not.
- **A `pulseBit` contributes nothing to reads**, so it *can* share a word with
  read-side fields. That is the ID-overlay pattern: an identity constant the
  host reads at an offset whose writes pulse `start`.
- **Windows are a power of two and aligned to their own size**, and nothing may
  land inside one.
- **A declared name appears once.**

## The board facts

- **Every Warp 11 slave on the KV260 is at `0xB0000000`**, IRQ SPI 89.
- **A wrong base address hangs the whole board** — a read nothing decodes waits
  forever and takes the interconnect with it. Power cycle only. Check the base
  before suspecting the design.
- An unmapped offset *inside* the aperture reads as zero rather than faulting,
  which is another reason for the identity register.

## See also

- [Drive it from Rust](../drive-it-from-rust.md) — the other half: `RegisterWindow`,
  `MmapWindow`, and driving the design from a host program.
- [Register map (tutorial)](../../hdl/Warp11.Tutorial/doc/registerMap.md) — the
  five AXI-Lite channels themselves, steppable in the debugger. It teaches the
  **list-driven** `axiLiteSlave`, which is a different and simpler surface: it
  is for understanding the bus, not for building a design with a host seam.
- `hdl/Warp11.Effects/Wrappers.fs` — four real maps side by side.
