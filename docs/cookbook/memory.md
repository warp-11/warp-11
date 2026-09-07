# How do I store and move data?

**You want:** somewhere to put data, and a way for something else to get at it.
There are more answers than you would expect, because "somewhere" and
"something else" both have several meanings.

This page is the map. It is arranged as the two questions you actually have —
*what kind of storage*, and *who is reaching for it* — because those are
independent and the confusion comes from treating them as one.

## Part 1: what kind of storage

Three physical kinds, and **you always say which**. Bare `mem` and `rom` are
compile-time errors, deliberately: a memory whose storage the synthesiser picks
changes shape when the array's shape changes, and the resulting bug is invisible
through simulation, lint *and* synthesis. It cost this project a full debugging
session — GEP's case table moved from five narrow arrays to one wide one, Vivado
moved it from LUTs to block RAM, and every lane silently scored against zeros.

| you write | becomes | size on a KV260 | read |
|---|---|---|---|
| `distributedMem` / `distributedRom` | **LUTRAM** — SLICEM lookup tables used as small RAMs | as many LUTs as you spend | combinational **or** synchronous |
| `blockMem` / `blockRom` | **BRAM** — 36 kbit blocks | 144 blocks = **5.2 Mb** | **synchronous only** |
| `ultraMem` | **URAM** — 288 kbit blocks | 64 blocks = **18.4 Mb** | **synchronous only**, no initial contents |

### Choosing

- **A handful of words, and you want the value this cycle** → `distributedMem`.
  It is the only one `memRead` is legal on. Costs real LUTs, so it stops being
  the answer somewhere in the low hundreds of words.
- **Kilobits to a few megabits** → `blockMem`. The default answer for anything
  that looks like a buffer, a table or a line store.
- **Megabits** → `ultraMem`. On this part it is where 18.4 of the chip's 24.2 Mb
  live, so a design that needs most of the on-chip memory has to reach for it.
  Note it takes no INIT — its contents after configuration are zero.
- **A ROM with contents** → `distributedRom` / `blockRom`. There is no
  `ultraRom`, because UltraRAM cannot be initialised.

### The rule that catches the expensive mistake

**`memRead` is combinational and legal only on `distributedMem`.** On a block or
ultra memory the read arrives a cycle later, and neither the Sim nor Verilator
will tell you — both honour the RTL you wrote, and the RTL says combinational.
Silicon disagrees. So block and ultra memories force `memReadPort`, which owns
the latency where you can see it:

```fsharp
let port = memReadPort m addr
port.data                              // the word, `port.depth` cycles later
port.through "tag" myTag               // carry anything else across the same gap
```

`through` is the point: a hand-written `reg` states the delay, so it is wrong the
first time the storage changes. `through` delays by *the port's* depth, whatever
that is.

## Part 2: who is reaching for it

Three transports, and they are not alternatives — a real design uses two or
three at once.

| | who starts the transfer | shape | use it for |
|---|---|---|---|
| **AXI-Lite slave** | the **host** reaches *into* the fabric | one 32-bit word per transaction | control, status, configuration |
| **AXI master** | the **fabric** reaches *out* to DDR | bursts, many beats | bulk data — frames, populations, streams |
| **local memory** | the design, itself | whatever you declared | working state |

**`RegMap` is not a fourth transport.** It is a *description*: one definition
that elaborates the AXI-Lite slave **and** generates the Rust constants the host
compiles against. See [Create a register map](register-map.md).

**Mandelbrot uses both buses at once**, and it is the clearest example of the
split: `cxOrigin`, `dx`, `start` and `busy` go through the AXI-Lite slave — nine
words of control — while the 1.1 MB frame goes out over an AXI master to DDR.
The host tells the fabric *where* to write via one register (`fbBaseAddr`) and
then stays out of the way. Pushing 1.1 MB through the aperture would be 281,600
separate transactions.

### The word "aperture"

The **aperture** is the address range the AXI-Lite slave decodes — the block of
addresses the interconnect routes to this design. `apertureAddrWidth = 8` means
256 bytes. It is a *decode range*, not an allocation: a 256-byte aperture holding
nine registers wastes nothing, it just means the address comparator is 8 bits
wide. The device tree and the block design name the same range, which is why it
is worth pinning once a design has either.

### What a register actually is

Worth knowing, because it makes everything else obvious. From the host it looks
like memory:

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

## Part 3: the three ways to touch storage from a design

This is the part that looks like duplication and is not. They are three levels,
and the right one depends on **how much the caller is allowed to know**.

### `memRead` — you know everything

```fsharp
let word = memRead m addr        // this cycle, distributedMem only
```

Combinational. No latency, no handshake, no ceremony. Right when the storage is
small, local, and settled.

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

## Part 4: host-visible arrays

A register map can expose a **block of words** rather than a single register:

```fsharp
let loadRow = r.RwArray("loadRow", 128)     // the host writes, the design reads
let trace   = r.RoArray("trace", 16)        // the design writes, the host reads
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

**They were called `rwWindow`/`roWindow` and are now `rwArray`/`roArray`** —
"window" already meant `ReadWindow`/`WriteWindow`, which is a different idea, and
having one word for both was the single most confusing thing on this page.

Reaching one from the design:

```fsharp
let port = regs.readArray loadRow addr      // an ordinary MemReadPort, plus…
port.read.data
port.hostTurn                                // …the cycles the host borrowed it

let trace = regs.driveArray traceEntry       // the raw mem, the design's to write
```

`hostTurn` exists because a host-written array has **one** read port shared
between the host's readback and the design. The host wins, for exactly the
in-flight read and no longer — the alternative hangs the bus the moment a design
reads its array every cycle, and one does. A design consuming the array
statefully gates on `hostTurn`; one deriving combinational values from it may
ignore a one-cycle glitch only the reading host could observe.

## The current limit, stated plainly

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

## See also

- [Create a register map](register-map.md) — the map itself, entry by entry
- [Drive it from Rust](../drive-it-from-rust.md) — the host half
- [Streams](../streams.md) — the ready/valid layer windows are built on
