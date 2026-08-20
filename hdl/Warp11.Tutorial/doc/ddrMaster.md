# DDR master

The fabric reaching out. [**Register map**](registerMap.md) is the host poking the design; this is
the design going to memory on its own — reading gigabytes of DRAM the chip has
no room to hold, and writing results back where a host process can find them.

## What to look at

Set `m_axi_awready = 1`, `m_axi_wready = 1`, `m_axi_bvalid = 1` — a memory that
is always willing — and step twenty times.

**Nothing happens.** `m_axi_awvalid` never rises, `armed` is 0, and
`words_written` stays at 0.

Now poke `base_addr = 0x40000000` and step again. `armed` goes high,
`m_axi_awaddr` walks `0x40000000`, `0x40000004`, `0x40000008`, and
`words_written` climbs.

That refusal is the most important thing on this page.

## The arm gate

```fsharp
bnot (eq baseAddr (lit 0UL 32)) ==> armed
```

One comparison, and it is a hardware-safety interlock rather than a nicety.

A write master with no gate starts writing the instant the bitstream loads —
to whatever address its base register holds, which after reset is **0**. It
will do that before any driver has started, before anyone has told it where the
buffer is, into memory that belongs to something else.

Worse is what happens at the other end. Unload a bitstream while writes are in
flight and the memory path is left with orphaned beats: address and data pair
up permanently offset by however many were in the air. Every later write lands
somewhere it should not. This survives reloading the app, survives loading a
different app, and clears **only on reboot**. It was diagnosed the hard way —
frames arriving rotated by eight words while every register and every
computation was perfect, because the fault was not in the fabric at all.

So the protocol is three things, all of them:

1. **Arm on a host-written register**, so nothing flows before a driver exists.
2. **Disarm before unloading** — the driver writes stop and clears the base on
   exit.
3. If frames arrive rotated and the numbers are all correct, **do not debug the
   design. Reboot the board.**

## The read half

```fsharp
Stream.input "req" (layout1 ("addr", 32))
|> axiMasterReader 32 32 4
|> Stream.out "resp"
```

Addresses in, data out, and the `4` is how many reads may be in flight at once.

That number is throughput. DRAM latency is tens of cycles; issue one read and
wait for it and you get one word per round trip. Issue four and the latencies
overlap. The master keeps a small ring of slots — the bus guarantees responses
come back in order at a fixed transaction id, so a pointer is enough
bookkeeping and no per-slot matching is needed.

For bulk movement there is `axiMasterReaderBurst`, where a request is
`(address, length)` and one transaction fetches many consecutive words. The
difference is not small: a loop of single-word reads once cost 9.2 ms per
generation against 326 µs of actual computation. **Move a contiguous region
with one bulk request, never a loop of small ones.**

## Alignment, which is silent when wrong

Two rules with no error message behind them:

- **The high-performance memory port silently drops sub-word writes that are
  not properly aligned.** Not an error, not a fault — the write does not
  happen. Use a 128-bit master with 16-byte-aligned addresses for bulk traffic.
- **Keep a master's read and write data widths equal.** Give one interface
  32-bit reads and 128-bit writes and the tools infer a 128-bit port, wire the
  32-bit read data to byte lane 0, and never mention it. Invisible in
  simulation, because in simulation both widths are exactly what you asked for.

This design uses 32 bits both ways, which is the right size for a page that is
about the shape rather than about throughput.

## Streams both ways

Both halves are `Stream`s, and everything on the [**Stream pipe**](streamPipe.md) page applies:
the read master stalls its requests when its slots are full, the write master
stalls its beats when the bus is busy, and the design upstream does not have to
know which. The write beats here are built by hand rather than arriving from a
port, which is what an internal producer looks like:

```fsharp
{ payload = addr, payload, lit 0xFUL 4
  valid = armed
  ready = ready
  layout = axiWriteBeatLayout 32 32 }
|> axiMasterWriter 32 32 4
```

`valid` is the arm gate. `ready` is the wire the master drives back, and the
counters only advance when both agree — which is the handshake, written out
once rather than hidden.

## Try this

- Set `base_addr` back to 0 while it is running. It stops immediately, mid
  stream. That is what a driver's disarm does on exit.
- Hold `m_axi_awready` low. `words_written` stops climbing: the master cannot
  accept beats it has nowhere to put, and the backpressure reaches all the way
  to the counter.
- Set `base_addr = 0x40000002` — a base that is not word-aligned. Everything
  runs and every address is misaligned. Nothing in this simulation objects, and
  neither would Verilator; the hardware would simply lose the writes.
- Drive the read side: poke `req_addr`, `req_valid = 1` and `m_axi_arready = 1`,
  then step. `m_axi_arvalid` rises a cycle later — the request is taken into a
  slot first and issued from there. Supply `m_axi_rdata` with `m_axi_rvalid = 1`
  and it comes out on `resp_data`.

## See also

- [**Register map**](registerMap.md) — where `base_addr` comes from in a real design, and the
  other half of the host seam.
- [**Stream pipe**](streamPipe.md) — the handshake both halves of this are made of.
- [**Farm**](streamFarm.md) — what usually sits between a read master and a write master.
- [**Flow (valid only)**](flowSampler.md) — the shape for a producer that cannot be told to wait,
  which a memory bus emphatically is not.
