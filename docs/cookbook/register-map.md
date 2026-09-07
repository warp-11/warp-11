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

type BlinkerRegs =
    { id: RegEntry
      enable: RegEntry
      count: RegEntry }

let blinkerRegs, blinkerMap =
    buildRegMap (fun r ->
        { id = r.RoConst("id", 0xB11EDUL)     // 0x000
          enable = r.RwReg("enable", 1, 0UL)  // 0x004
          count = r.RoField("count", 24) })   // 0x008
```

**No offsets.** `buildRegMap` allocates a word per register in declaration
order and derives the aperture from what it ends up holding. The entries are
**values you hold on to**: every later access is keyed by the entry itself,
never by its name spelled a second time.

The builder is handed to your function and read *after* it returns, which is
why the map comes back beside the record rather than inside it — a `map` field
would be correct only while it happened to be written last.

### The other constructors

| | |
|---|---|
| `r.RwReg(name, width, init)` | host writes, design reads. **Owns its word**, whatever its width |
| `r.RoField(name, width)` | design drives, host reads. Owns a word; pack several with `Word` |
| `r.RoConst(name, value)` | a fixed identifying pattern |
| `r.RwArray(name, words)` / `r.RoArray(name, words)` | a block of words. **Rounds the cursor up** to the window's own size — the alignment rule that made windows fiddly to place by hand |
| `r.Word(fun w -> …)` | one word shared by several small entries — below |
| `r.LayoutHash(name)` | a word answering a fingerprint of the map — which *revision* the fabric was built from |
| `buildRegMapPinned n (fun r -> …)` | the same, with the aperture stated rather than derived |

### Packing several entries into one word

```fsharp
let running, allIdle =
    r.Word(fun w -> w.Field("running", 1), w.Field("allIdle", 1))
```

Bits are assigned in order and the word advances once at the end. `w.Pulse`,
`w.W1c` and `w.Const` share the scope.

Packing is deliberate rather than incidental: a host reading `running` and
`allIdle` from **one** word gets a coherent snapshot, where two reads could
straddle a change. That is why it is a scope you enter, not something the
allocator does when things happen to fit.

**The ID-overlay falls out of it.** A `Const` owns the word's *read* side and a
`Pulse` owns only the *write* side, so they compose:

```fsharp
let id, start = r.Word(fun w -> w.Const("id", 0xB11EDUL), w.Pulse "start")
```

Reads answer the identity; writes pulse start. That is why Mandelbrot's layout
has `ID_OFFSET` and `START_OFFSET` at the same address.

### Widths: what packs and what does not

- **Read-only fields pack**, up to 32 bits per word, via `Word`.
- **Read-write registers do not.** An 8-bit `rwReg` still consumes a whole
  32-bit word, and the map refuses to put two in one: *"rw register 'x' must own
  word 0x1c alone"*. The reason is not address space — the slave ignores
  byte-enables and trusts whole-word writes, so changing one field of a packed
  *writable* word would mean read-modify-write on the host, and that races
  another writer or the hardware. Owning the word makes every host write atomic
  and independent.
- **32 bits is the maximum** for any single entry, and the bus is 32 bits wide.
  A 64-bit value is two entries:

  ```fsharp
  let bestFitLo = r.RoField("bestFitLo", 32)
  let bestFitHi = r.RoField("bestFitHi", 32)
  ```

  **Reading that pair is not atomic** — the value can change between the two
  reads and tear. Live with it where the value moves slowly, or latch `_HI` into
  a shadow register when `_LO` is read.

### Pinning an address — and why you almost certainly should not

`r.At 0xNNN` moves the cursor. **No map in this repository calls it**, and that
is deliberate: everything here reads its offsets from the generated Rust
layout, which comes off this same map, so moving a register moves the host with
it. `w.FieldAt` / `w.PulseAt` / `w.W1cAt` are the same escape at bit
granularity, and equally unused.

Reach for them only when you can *name the outside thing* whose addresses you
are matching — a shipped host binary, a board script poking offsets by hand, a
device tree. If you cannot name one, allocation in declaration order is the
point.

### Two constants worth having: identity and layout hash

They answer different questions, and each is silent about the other's failure.

```fsharp
let id, start = r.Word(fun w -> w.Const("id", 0xB11EDUL), w.Pulse "start")
r.LayoutHash "layoutHash"
```

```rust
pub const ID_VALUE: u32 = 0x000b11ed;        // which design
pub const LAYOUT_HASH_VALUE: u32 = 0x3939;   // which revision of its map
```

**The identity** catches the wrong bitstream or the wrong base address — the
cases where you are not talking to this design at all.

**The layout hash** catches the case the identity cannot: the *right* design,
built from an *older revision of the map*. Offsets are allocated, so adding one
register moves every address after it. That bitstream answers the identity
happily and then serves every register from the wrong place — and each read
**succeeds**, because an unmapped read inside the aperture returns zero rather
than faulting. Without the hash, that is a driver quietly reading rubbish.

Check both when you open the device:

```rust
if window.read32(layout::ID_OFFSET)? != layout::ID_VALUE { … }
if window.read32(layout::LAYOUT_HASH_OFFSET)? != layout::LAYOUT_HASH_VALUE { … }
```

Three things about the hash worth knowing:

- **It tracks the layout, not the source.** The fingerprint is taken over an
  address-sorted rendering, so declaring the same registers in a different order
  with the same offsets hashes the same. Moving, resizing, renaming, adding or
  removing one does not.
- **It is 16 bits and deterministic.** FNV-1a, rolled by hand rather than
  reached for, because .NET's `String.GetHashCode` is randomized per process —
  the same map would hash differently on two runs, and this value is committed.
- **`LayoutHash` returns nothing**, on purpose. Its value is not known until the
  map is closed, so there is no entry for a caller to hold; nothing in a design
  body has any use for it anyway.

It costs one word — a `roConst` owns a whole read side, so it cannot share with
the identity.

### The aperture: derived, but pinnable

`buildRegMap` derives it — the smallest power of two holding what you declared,
floored at 16 bytes. For a new design that is the right answer and you never
think about it.

`buildRegMapPinned 8` states it instead, and the reason it exists is that **the
aperture is a port width**:

```verilog
input [7:0] s_axi_awaddr, ... input [7:0] s_axi_araddr
```

That makes it a contract with two things written outside the F# — the Vivado
block design's address segment, and the device tree's `reg = <… 0x1000>`. Under
a *derived* aperture, adding one status field past a power of two silently
widens the boundary and those two quietly stop matching. So pin it once a design
has a block design or a device tree, and a register that no longer fits becomes
an elaboration error naming the register rather than a boundary that moved.

It is also a decode *window*, not a packing problem: a round 256 bytes with room
to grow beats the tightest fit that changes shape every time you add a counter.

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
        (fun p -> (axiLiteSlavePorts p blinkerMap.apertureAddrWidth, p.outPort "led" 1))
        (fun (slavePorts, led) ->
            let regs = regMapSlave slavePorts blinkerMap

            let counter = reg "counter" 24
            If (regs.value blinkerRegs.enable) (fun () -> counter + 1UL ==> counter)

            regs.drive blinkerRegs.count counter
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
{ pulse; value; drive; setBit; readArray; driveArray; irq }
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

The part that does not survive for free: `RwArray` arbitration assumes a bus
with a read-address handshake to borrow the port during. A map with no windows
sidesteps it entirely.

## Which kind to reach for

| builder call | direction | what it is |
|---|---|---|
| `r.RwReg(name, width, init)` | host writes, design reads | a setting. Reads back what was written. **Owns its whole word.** |
| `r.RoField(name, width)` / `w.Field` | design drives, host reads | a status field. Pack several with `Word`. |
| `r.RoConst(name, value)` / `w.Const` | host reads | a fixed identifying pattern. Read it first in the driver — see below. |
| `w.Pulse(name)` | host writes | a **one-cycle strobe**, not a level. This is what `start` wants. |
| `w.W1c(name)` | design sets, host clears | an interrupt-status bit. Every one joins the map's `irq` line. |
| `r.RwArray(name, words)` | host writes, design reads | a block of words backed by a mem — a coefficient table, a program. |
| `r.RoArray(name, words)` | design writes, host reads | a block the design fills — a result buffer, a trace. |

The bare `rwReg` / `roField` / `pulseBit` / … constructors still exist and take
an explicit offset. The builder calls them; reach for them directly only if you
are assembling entries some other way.

**Use a pulse bit for anything the host *does* rather than *sets*.** A level
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
| `regs.window entry addr` | the arbitrated read port onto a `rwArray` — call it **once** |
| `regs.driveArray entry` | the `Mem` behind a `roArray`, for the design to write |
| `regs.irq` | the OR of every `w1cBit`, for the board's interrupt line |

## Generating the Rust seam

`regMapRsLines` turns the same map into constants. Emit them into the driver
crate and commit the result, so a layout change shows up in the driver's diff:

```fsharp
let layout = regMapRsLines blinkerMap
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
