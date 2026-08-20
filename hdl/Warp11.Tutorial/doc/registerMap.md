# Register map

Four words the host can reach. This is the seam: a Linux process running on the
board's ARM cores writes a pointer here, reads a status word there, and that is
the entire vocabulary it has for talking to the fabric.

## What to look at

`running` and `elapsed` are ordinary outputs, so you can watch the design
without speaking the bus. Step a few times — nothing moves, because nothing has
turned it on.

Now do a write by hand. Poke `s_axi_awaddr = 0`, `s_axi_awvalid = 1`,
`s_axi_wdata = 1`, `s_axi_wvalid = 1`, `s_axi_bready = 1`, and step until
`s_axi_bvalid` goes high.

- `running` is now 1 and `elapsed` climbs on every step.
- Read it back: poke `s_axi_araddr = 4`, `s_axi_arvalid = 1`, `s_axi_rready = 1`
  and step until `s_axi_rvalid`. `s_axi_rdata` reads **0xA57A**.

You have just done, by hand, what a driver does with `*(volatile uint32_t *)`.

## The three kinds of register

```fsharp
axiLiteSlave
    4                                              // aperture: four words
    [ "control", 0x0UL, 32 ]                       // host writes, design reads
    [ 0x4UL, lit 0xA57AUL 32; 0x8UL, ticks ]       // design provides, host reads
    []                                             // memory windows: none here
```

- **Control** — a register the host writes and the design reads. It comes back
  as an ordinary `Expr`, and this design pulls bit 0 out of it as a run flag.
  Everything a host can ask for arrives this way: a start bit, an iteration
  count, a DDR base address.
- **Constants** — a fixed value at a fixed offset. `0xA57A` here. Every real
  driver reads one of these first, because "am I talking to the bitstream I
  think I am?" is a question that otherwise gets answered by a hang.
- **Status** — a live signal wired to an offset. `ticks` is a free-running
  counter; in a real accelerator it is a done flag, a cycle count, a stall
  counter from [**Stall probes**](streamProbes.md).

Write registers are readable back at their own offsets, which is why reading
0x0 returns the 1 that was written.

## What AXI-Lite actually is

Five independent channels — write address, write data, write response, read
address, read data — each a `valid`/`ready` handshake in the same shape as
everything on the [**Stream pipe**](streamPipe.md) page. There is no clever part. A word goes
one way, an acknowledgement comes back, and the whole thing exists because
processors and memory-mapped peripherals settled on it decades ago.

`axiLiteSlave` implements one transaction at a time, OKAY only. Two things it
deliberately does not do: byte-enables are accepted and ignored (full-word
writes only), and read data is assumed stable while `RVALID` waits. Both are
fine at the scope this is used — a host that pokes a slave — and both are
written down rather than discovered.

There is also `axiLiteSlaveFull`, which adds **pulse registers**: writing a 1
produces a one-cycle strobe rather than a level. That is the shape for "start"
— a level start needs the host to write it back to 0, and a design that misses
the clear runs twice.

## The seam is generated

The half of this that matters is not on this page. A register map defined once
in F# emits *both* the slave you see here **and** the host-side layout — the
offsets, widths and names, as a Rust source file the driver compiles against.

That file is the only thing the two languages share. Nobody maintains a
matching pair of constants; there is no pair. The single most common way to
lose an afternoon on an FPGA is a host and a bitstream disagreeing about an
offset by four, and this is the answer to it.

## Board facts that bite

- **Every Warp 11 slave on the KV260 lives at `0xB0000000`.** It is the only
  sub-4GB window the board's high-performance master port exposes.
- **A wrong base address hangs the whole board.** A read to an address nothing
  decodes waits for a response that will never come, and the interconnect waits
  with it, forever. Not a crash, not an error — a hang that only a power cycle
  clears. Check the base before blaming the design.
- Only one bitstream is loaded at a time, so switching between accelerators
  means stopping whatever is talking to the old one first.

## Try this

- Write 0 back to `control` and watch `elapsed` freeze where it was. Status
  registers keep their value; they are just registers.
- Read offset `0xC`. Nothing is mapped there, and it reads as zero rather than
  faulting — an unmapped offset inside the aperture is silence, which is
  another reason for the identity constant.
- Change the aperture from `4` to `3` in the source and reload. The slave now
  covers two words, and the offset at 0x8 is outside it — an elaboration error
  naming the offset.
- Try a write with `s_axi_wvalid = 1` but `s_axi_awvalid = 0`. Nothing happens
  until both arrive; the two channels are independent and the write needs both.

## See also

- [**Stream pipe**](streamPipe.md) — the `valid`/`ready` handshake this bus is five copies of.
- [**DDR master**](ddrMaster.md) — the other direction, where the fabric reaches out rather
  than waiting to be poked.
- [**Stall probes**](streamProbes.md) — the counters that most often end up wired to a status
  offset.
- [**Sequencer**](sequencer.md) — the state machine a control register usually starts.
