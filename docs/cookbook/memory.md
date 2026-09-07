# How do I store and move data?

**You want:** somewhere to put data, and a way for something else to get at it.
There are more answers than you would expect, because "somewhere" and
"something else" both have several meanings.

This page is the map. The overview says what the pieces are; the table after it
is the index — find the row that matches what you are trying to do, and follow
it to the section that does it.

## Overview

### Host and fabric, and what passes between them

Two computers, and they share no memory.

The **host** is an ordinary program — Rust on the board's ARM cores, or on your
workstation driving the simulator. It has a heap, a filesystem and gigabytes of
DRAM.

The **fabric** is your elaborated design. It has no heap and no allocator: every
byte it can touch is something you declared while building it, and it is on the
chip, physically, as flip-flops or memory blocks. What it does have is the whole
chip at once — thousands of memories readable in the same cycle.

They meet on exactly two kinds of link, and a real design uses both:

- **The host reaches into the fabric** — one 32-bit word per transaction,
  through the AXI-Lite slave (SPI on a part with no AXI). This is control,
  status, configuration, and small blocks moved rarely.
- **The fabric reaches out to DRAM** — bursts of many beats, through an AXI
  master. This is bulk: frames, populations, streams.

**Mandelbrot uses both at once**, and it is the clearest example of the split:
`cxOrigin`, `dx`, `start` and `busy` go through the AXI-Lite slave — nine words
of control — while the 1.1 MB frame goes out over an AXI master to DDR. The host
tells the fabric *where* to write via one register (`fbBaseAddr`) and then stays
out of the way. Pushing 1.1 MB through the aperture would be 281,600 separate
transactions.

**`RegMap` is not a third link.** It is a *description*: one definition that
elaborates the AXI-Lite slave **and** generates the Rust constants the host
compiles against, which is why the two cannot disagree about the map. See
[Create a register map](register-map.md).

### The KV260 — a hard ARM host on the same chip

```
     PS — the hard ARM side                    PL — your elaborated design
 ┌───────────────────────────────┐        ┌────────────────────────────────────┐
 │  4× Cortex-A53, Linux,        │        │  registers   flip-flops            │
 │  your Rust driver             │        │  LUTRAM      distributedMem        │
 │                               │        │  BRAM        blockMem     5.2 Mb   │
 │  M_AXI_HPM1_FPD ──────────────┼───────►│  URAM        ultraMem    18.4 Mb   │
 │            32-bit, one word   │ s_axi_*│                                    │
 │            per transaction    │        │  AXI-Lite slave @ 0xB0000000       │
 │                               │        │                                    │
 │  S_AXI_HP0_FPD ◄──────────────┼────────┤  AXI master, 128-bit bursts        │
 │        │      128-bit bursts  │ m_axi_*│                                    │
 │        ▼                      │        └────────────────────────────────────┘
 │  DDR4, 4 GB                   │
 │        ▲                      │
 │        └── udmabuf: the same physical buffer the host mmaps
 └───────────────────────────────┘
```

Xilinx's names for the two halves turn up throughout these docs: the **PS**
(Processing System) is the hard ARM side, the **PL** (Programmable Logic) is the
fabric. One chip, two halves, no shared memory — they meet only at those named
AXI ports.

### The iCEBreaker — a separate host, one serial link

```
    host: a PC over the FT2232H, or
    an MCU over SPI                            iCE40 UP5K fabric
 ┌───────────────────────────────┐        ┌────────────────────────────────────┐
 │  your program                 │  SPI   │  registers   flip-flops            │
 │  (register reads and writes)  │───────►│  EBR         blockMem   120 kbit   │
 │                               │  UART  │                                    │
 └───────────────────────────────┘        │  no LUTRAM and no URAM — the part  │
                                          │  has neither primitive             │
                                          │                                    │
                                          │  128 KB SPRAM, on-chip, with no    │
                                          │  storage class to reach it yet     │
                                          └────────────────────────────────────┘

    on the board, not reachable from a design at all: 16 MB QSPI flash (which
    also holds the bitstream), and 8 MB QSPI PSRAM on a v1.1a. There is no DRAM
    and no bulk link — everything a design touches is on the chip.
```

**What is built and what is not.** The emitter has an iCE40 target and it is
strict rather than accommodating: `distributedMem` is *refused* on iCE40 (the
part has no LUT-RAM primitive, and dropping the attribute to satisfy yosys would
land a combinational read in sync-read EBR — a cycle wrong on silicon while the
Sim and Verilator pass), and `ultraMem` is refused for having no counterpart. So
on this part a memory is `blockMem`, read through `memReadPort`.

The register seam is **planned, not built**: `spiSlaveOf : RegMap -> SlaveRegs`
is designed as a third consumer of the same map beside `regMapSlave` and the
Rust generator, and the generated layout is deliberately unchanged across buses
so the host driver survives the move. The QSPI PSRAM, the flash and the UP5K's
128 KB SPRAM have no DSL storage class yet. See
[What if the design is not on AXI?](register-map.md#what-if-the-design-is-not-on-axi)

### The five places data can sit

| kind | where it physically is | how you say it | KV260 | iCE40 UP5K | read |
|---|---|---|---|---|---|
| **registers** | flip-flops in the fabric | `reg`, or a `RegMap` entry | as many as you spend | as many as you spend | this cycle |
| **LUTRAM** | SLICEM lookup tables used as small RAMs | `distributedMem` / `distributedRom` | as many LUTs as you spend | **not available** | combinational **or** synchronous |
| **BRAM** | dedicated 36 kbit blocks | `blockMem` / `blockRom` | 144 blocks = **5.2 Mb** | EBR, 120 kbit | **synchronous only** |
| **URAM** | dedicated 288 kbit blocks | `ultraMem` | 64 blocks = **18.4 Mb** | **not available** | **synchronous only**, no initial contents |
| **DDR** | DRAM on the host's side of the chip | an AXI master, plus a base address | 4 GB | — | bursts, tens of cycles |

**You always say which, and bare `mem` and `rom` are compile-time errors.** That
is deliberate: a memory whose storage the synthesiser picks changes shape when
the array's shape changes, and the resulting bug is invisible through
simulation, lint *and* synthesis. It cost this project a full debugging session
— GEP's case table moved from five narrow arrays to one wide one, Vivado moved
it from LUTs to block RAM, and every lane silently scored against zeros.

## Memory access patterns

Find the row that matches what you are trying to do. **Initiator** is who starts
the transfer, which is the thing that decides most of the answer. **Speed** is
*fast* when the transfer is in the design's cycle budget or its streaming
bandwidth, and *slow* when it happens once at startup, or per frame, a
transaction at a time.

| use case | initiator | memory type | speed |
|---|---|---|---|
| [Set a parameter, read a status word](#set-a-parameter-read-a-status-word) | host | registers | slow |
| [Move a few kilobytes in at startup](#move-a-few-kilobytes-in-at-startup) | host | LUTRAM (aperture array) | slow |
| [Read a small result block back](#read-a-small-result-block-back) | host | LUTRAM (aperture array) | slow |
| [Keep a few words and use them this cycle](#keep-a-few-words-and-use-them-this-cycle) | fabric | LUTRAM | fast |
| [Hold a buffer, a table or a line store](#hold-a-buffer-a-table-or-a-line-store) | fabric | BRAM | fast |
| [Hold megabits on the chip](#hold-megabits-on-the-chip) | fabric | URAM | fast |
| [Look up a table decided at elaboration](#look-up-a-table-decided-at-elaboration) | fabric | LUTRAM or BRAM, as ROM | fast |
| [Write bulk results out to DDR](#write-bulk-results-out-to-ddr) | fabric | DDR | fast |
| [Stream bulk input in from DDR](#stream-bulk-input-in-from-ddr) | fabric | DDR | fast |
| [Let the host see the latest frame](#let-the-host-see-the-latest-frame) | fabric, host samples | DDR | fast |
| [Get at that DDR buffer from the host](#get-at-that-ddr-buffer-from-the-host) | host | DDR | fast in bulk, slow per word |
| [Write a client that does not know the storage](#write-a-client-that-does-not-know-the-storage) | fabric | any of them | either |

## Set a parameter, read a status word

The default seam, and the one every design has. A `RegMap` definition names the
words; the host writes a threshold or a base address, reads a `busy` bit or a
cycle count.

```fsharp
let threshold = r.RwReg("threshold", 16, 0UL)
let busy      = r.RoField("busy", 1)
```

**Tutorial:** [Register map](../../hdl/Warp11.Tutorial/doc/registerMap.md) —
four words the host can reach, in the debugger.
**Cookbook:** [Create a register map](register-map.md) — every constructor,
entry by entry, and the rules that bite.

### What a register actually is

Worth knowing, because it makes everything else on this page obvious. From the
host it looks like memory:

```c
uint32_t x = *(volatile uint32_t *)(0xB0000000 + 0x04);
```

On the fabric there is no memory. A **read is a mux**:

```verilog
assign s_axi_rdata = (ar_word == 4) ? patLow
                   : (ar_word == 1) ? threshold : ... : 32'd0;
```

and a **write is an enable on a flip-flop**:

```verilog
threshold <= ((write_fire & (aw_word == 1)) ? s_axi_wdata[15:0] : threshold);
```

So the address space is a naming scheme for wires, not an allocation. Which is
why a `roField` costs no storage at all (it is a wire the design already drives),
why an unmapped address reads zero rather than faulting, and why gaps in a map
are free.

### The word "aperture"

The **aperture** is the address range the AXI-Lite slave decodes — the block of
addresses the interconnect routes to this design. `apertureAddrWidth = 8` means
256 bytes. It is a *decode range*, not an allocation: a 256-byte aperture holding
nine registers wastes nothing, it just means the address comparator is 8 bits
wide. The device tree and the block design name the same range, which is why it
is worth pinning once a design has either.

## Move a few kilobytes in at startup

A register map can expose a **block of words** rather than a single register:

```fsharp
let loadRow = r.RwArray("loadRow", 128)     // the host writes, the design reads
```

These occupy the aperture — one 32-bit address per entry — and they exist for one
situation: **a few kilobytes the host moves rarely, where building the DDR path
is not worth it.** Game of Life's initial grid is the case: the host writes the
pattern once, then the fabric runs for billions of generations. The alternative —
an AXI master to DDR — needs a contiguous physical buffer, a udmabuf, cache
management and a base-address handshake, all to move 512 bytes once.

The trade is address space and speed: one AXI-Lite transaction per word. A 4 KB
buffer is 1,024 transactions, which is fine once at startup and hopeless per
frame.

Reaching one from the design:

```fsharp
let port = regs.readArray loadRow addr      // an ordinary MemReadPort, plus…
port.read.data
port.hostTurn                                // …the cycles the host borrowed it
```

`hostTurn` exists because a host-written array has **one** read port shared
between the host's readback and the design. The host wins, for exactly the
in-flight read and no longer — the alternative hangs the bus the moment a design
reads its array every cycle, and one does. A design consuming the array
statefully gates on `hostTurn`; one deriving combinational values from it may
ignore a one-cycle glitch only the reading host could observe.

**They were called `rwWindow`/`roWindow` and are now `rwArray`/`roArray`** —
"window" already meant `ReadWindow`/`WriteWindow`, which is a different idea, and
having one word for both was the single most confusing thing on this page.

### The current limit, stated plainly

**Aperture arrays are always 32-bit LUTRAM.** `rwArray`/`roArray` fabricate a
`distributedMem name aw 32` and you cannot hand them a memory you already have.
That is fine at 16 or 128 words and wrong past that: the Mandelbrot pod's
framebuffer is 4096 × 8 bits in **block RAM**, and as an aperture array it would
become 4096 × 32 bits of LUTRAM — four times the bits, in the most expensive
place to put them. The pod therefore uses the older list-driven slave
(`axiLiteSlaveOn`), whose memory regions take a `Mem` of any width and any
storage class.

So: **BRAM behind an aperture array is a real need and is not expressible
through `RegMap` yet.** URAM behind one almost certainly never is — 288 kbit at
one word per transaction is 9,216 reads, and anything that size wants a master.

## Read a small result block back

The same mechanism the other way round: the design writes, the host reads.

```fsharp
let trace = r.RoArray("trace", 16)          // the design writes, the host reads
let mem   = regs.driveArray traceEntry      // the raw mem, the design's to write
```

Right for a trace buffer, a histogram, a handful of per-lane counters — anything
the host reads occasionally and which is small enough that a transaction per
word does not matter. Past a few kilobytes, or once per frame,
[go out to DDR](#write-bulk-results-out-to-ddr) instead.

## Keep a few words and use them this cycle

```fsharp
let m = distributedMem "acc" 3 8            // 8 words of 8 bits, in LUTs
let word = memRead m addr                   // this cycle
```

`distributedMem` is the only storage `memRead` is legal on, and that is the whole
reason to reach for it: no latency, no handshake, no ceremony. It costs real
LUTs, so it stops being the answer somewhere in the low hundreds of words — and
it does not exist at all on iCE40.

**Tutorial:** [RAM](../../hdl/Warp11.Tutorial/doc/ram.md) — one write port and
two read ports, one that takes a cycle and one that does not.

## Hold a buffer, a table or a line store

`blockMem` is the default answer for anything from kilobits to a few megabits.
The read is synchronous, so it arrives through a port that owns the delay:

```fsharp
let m = blockMem "tile" 12 32
let port = memReadPort m addr
port.data                              // the word, `port.depth` cycles later
port.through "tag" myTag               // carry anything else across the same gap
```

`through` is the point: a hand-written `reg` states the delay, so it is wrong the
first time the storage changes. `through` delays by *the port's* depth, whatever
that is.

### The rule that catches the expensive mistake

**`memRead` is combinational and legal only on `distributedMem`.** On a block or
ultra memory the read arrives a cycle later, and neither the Sim nor Verilator
will tell you — both honour the RTL you wrote, and the RTL says combinational.
Silicon disagrees. So block and ultra memories force `memReadPort`, which owns
the latency where you can see it.

The companion trap is on the write side: **multiple `memWrite` calls on one mem
fold to one priority-muxed write site**, because two write sites on one array
kill BRAM inference in Vivado. That is a property of the DSL here rather than a
discipline you have to keep.

**Tutorial:** [RAM](../../hdl/Warp11.Tutorial/doc/ram.md) — the same page, and it
carries this gotcha as its point.

## Hold megabits on the chip

```fsharp
let m = ultraMem "population" 14 72
```

On a KV260, 18.4 of the chip's 24.2 Mb of on-chip memory live in URAM, so a
design that wants most of the chip's memory has to reach for it. Synchronous read
only, through `memReadPort`, exactly like `blockMem`.

**It takes no initial contents** — UltraRAM cannot be initialised, so after
configuration it is zero, and there is no `ultraRom`. Anything that has to start
with data in it either arrives over the bus at startup or lives in a `blockRom`.

## Look up a table decided at elaboration

The oldest trick in hardware: if you cannot compute it fast, look it up. The
contents are computed by your F# at elaboration and arrive already in the chip.

```fsharp
let squares = blockRom "squares" 16 [| for i in 0 .. 255 -> uint64 (i * i) |]
```

`distributedRom` for a handful of entries you want combinationally,
`blockRom` for anything larger.

**One hardware trap:** a sync-read ROM feeding a DSP multiply silently demotes to
LUTROM, because the output register is absorbed as the DSP's input register.
Re-register after any ROM read that feeds a multiplier.

**Tutorial:** [ROM](../../hdl/Warp11.Tutorial/doc/romTable.md) — two lookup
tables, contents decided while the design is built.

## Write bulk results out to DDR

The fabric reaching out. An AXI master turns a stream of beats into DRAM writes:

```fsharp
let bus = axiWriteBusOf ports
axiMasterWriterOn bus maxOutstanding beats
```

This is the path for frames, populations, anything measured in kilobytes or
megabytes. The host's only involvement is one register carrying the base
address, and a `start` bit.

**The gotchas here are hardware ones and they cost real time:**

- **Keep an AXI master's R and W data widths equal**, and note that the HP slave
  port **silently drops sub-word writes that are not 128-bit aligned** — so
  always a 128-bit master with 16-byte-aligned writes.
- **Never unload a bitstream while a master has transactions in flight.** It
  leaves a *permanent* PS-side HP0 AW/W pairing skew that survives every app
  reload and clears only on reboot. Arm the write path on a host-written
  register, disarm before unload; if frames arrive rotated while registers read
  perfectly, reboot rather than debugging the RTL.
- **A wrong AXI base hangs the whole board.** Power-cycle only.

**Tutorial:** [DDR master](../../hdl/Warp11.Tutorial/doc/ddrMaster.md) — the
design going to memory on its own, including the arm gate.

## Stream bulk input in from DDR

The same bus the other way: a stream of addresses in, a stream of words out.

```fsharp
let words = axiMasterReaderOn bus maxOutstanding requests
```

Right when the working set does not fit on the chip — GEP's population, a texture,
a coefficient set the host regenerates per run. `axiMasterReaderBurstOn` is the
same thing when the addresses are contiguous, which is most of the time and is
where the bandwidth is.

If the client should not know that the storage is DDR at all, wrap it in a
[read window](#write-a-client-that-does-not-know-the-storage).

## Let the host see the latest frame

A design producing continuously and a host reading occasionally do not want a
queue between them — a queue delivers the oldest frame, and blocks when the host
is slow. **Conflate keeps the newest**, into a triple buffer in DDR:

```fsharp
snapshotSource "grid" rows
|> streamConflate3 "frames" hostCapture hostRelease writerIdle
```

The fabric writes whichever of three buffers the host is not reading and
publishes the index in a register; the host reads that register, then that
buffer. The fabric never stalls and the host never sees a torn frame. Game of
Life's board view runs on exactly this at 167M generations per second, sampling
a few times a second.

**Guide:** [Streams — conflate, keep-latest into DDR](../streams.md#conflate-keep-latest-into-ddr)

## Get at that DDR buffer from the host

The buffer needs to be physically contiguous and both sides need to agree where
it is, which on Linux means **udmabuf**: the host allocates it, mmaps it, and
writes its physical address into the design's base-address register.

Two things to know before the first read:

- **udmabuf mmap is CACHED unless the fd is opened `O_SYNC`**, and the F# stdlib's
  AXI masters drive AxCACHE=0, so they do not snoop. Either open `O_SYNC`, or use
  the cached mapping with an explicit `sync_for_cpu`.
- **Always one bulk read of a contiguous region, never a loop of small reads.**
  GEP's harvest measured 9.2 ms per generation as a loop against 326 µs of fabric
  work. `warp11-dma` exists for exactly this and is 12–16× a write-combining
  mapping.

**Guide:** [Drive it from Rust](../drive-it-from-rust.md) — the host half, from
`RegisterWindow` to the first binary.
**Guide:** [Hardware workflow](../dev-workflow.md) — kernel modules, deploy, and
the troubleshooting list for when a frame looks wrong.

## Write a client that does not know the storage

The three ways to touch storage from a design look like duplication and are not.
They are three levels, and the right one depends on **how much the caller is
allowed to know**.

### `memRead` — you know everything

```fsharp
let word = memRead m addr        // this cycle, distributedMem only
```

Combinational. Right when the storage is small, local, and settled.

### `memReadPort` — you know the storage, not the timing

```fsharp
let port = memReadPort m addr
port.data, port.depth, port.through
```

A **value that is late**, with the delay owned by the port. No handshake, one
register per carried signal. Right inside a datapath whose storage is fixed and
which does not want to pay for backpressure — GEP's inner loops, Mandelbrot's
coalescer.

### `ReadWindow` / `WriteWindow` — you know nothing, deliberately

```fsharp
let window = blockReadWindow "tile" m       // or readWindowOn bus …
indices |> window.read                       // a Stream in, a Stream out
```

A **`Stream`**: indices in, words out, with ready/valid. It costs a handshake and
buys not having to know — the same client runs against LUTs, a block, or a region
of DDR on the far side of an AXI master and cannot tell which it got. Right when
something *above* the design decides what the storage is.

`WriteWindow` additionally carries `idle`, which is the only honest "everything I
accepted has landed" — anything upstream of the storage asserts too early, and
how far "landed" is varies from one cycle to a full AXI write response.

**Choosing between them:** how much does the caller need to not know?

| the caller | reach for |
|---|---|
| owns the memory and wants it now | `memRead` |
| owns the memory and can wait a known number of cycles | `memReadPort` |
| should work whatever the memory turns out to be | `ReadWindow` / `WriteWindow` |

## See also

- [Create a register map](register-map.md) — the map itself, entry by entry
- [Drive it from Rust](../drive-it-from-rust.md) — the host half
- [Streams](../streams.md) — the ready/valid layer windows are built on
- [How it fits together](../architecture.md) — the whole system, one level up
