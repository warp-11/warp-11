# RAM

Eight words of eight bits, one write port and two read ports — one that takes a
cycle and one that does not. Small, and it carries the single most expensive
gotcha in this whole toolkit.

## What to look at

Watch `this_cycle_out` and `next_cycle_out`, and use the **memory** tab to see
the array itself.

- Poke `waddr = 3`, `wdata = 0xAA`, `wen = 1`, press **Step**. The memory tab
  shows `0xAA` at address 3.
- Set `wen = 0`, poke `raddr = 3`. `this_cycle_out` reads `0xAA` **immediately**.
  `next_cycle_out` still shows whatever it held.
- Press **Step**. Now `next_cycle_out` reads `0xAA` too.

That one-cycle difference is the whole page.

## Reading the source

```fsharp
let store = distributedMem "store" 3 8
```

A memory with a 3-bit address — so 2³ = 8 words — each 8 bits wide. Unlike an
array in software, this is a *component*: it has physical read and write ports,
and how many of each you ask for changes what it costs and what it can be built
out of.

`distributedMem` is the first half of the page's lesson. A memory **declares
what it is built from**, and there are three ways to say it:

| | built from | reads |
|---|---|---|
| `distributedMem` | LUTs — "distributed RAM" | this cycle **or** next |
| `blockMem` | block RAM | next cycle only |
| `mem` | the synthesiser chooses | next cycle only |

This design says `distributedMem` because it wants a combinational read, and
that is the only declaration a combinational read is legal on. The next two
sections are why.

```fsharp
If wen (fun () -> memWrite store waddr wdata (lit 1UL 1))
```

A write, gated on `wen`. The trailing `lit 1UL 1` is the write enable, and the
`If` folds into it — writing under a condition and passing a condition as the
enable are the same thing, so you may use whichever reads better.

```fsharp
(memReadPort store raddr).data ==> nextCycleOut
memRead store raddr ==> thisCycleOut
```

Two reads of the same memory at the same address, differing only in timing.

## The two reads

`memRead` gives you the word **this cycle**: address in, data out, no clock,
exactly like indexing an array. It is combinational, and it behaves like every
other value an expression is built from.

`memReadPort` gives it to you **later** — one clock, for a memory. It hands
back a *port* rather than a value: `read.data` is the word, and
`read.through "name" signal` delays anything else that has to arrive with it.
That second half is the point. Whatever the caller was doing when it asked —
which row this was, whether the request was even real — has to still be true
when the answer lands, and writing that delay by hand means writing the number
`1` into the design at every such signal. `through` takes it from the port.

The names are worth a note, because they used to be `memReadAsync` and
`memRead`. That was the hardware vocabulary — distributed RAM genuinely has an
*asynchronous read port* — but in F# `Async` names a computation that finishes
*later*, so the name said the opposite of what the read did. It is the one read
that finishes now.

Reading this cycle is obviously nicer, so why would anyone wait? Because of what
the two become on a real FPGA. An FPGA has dedicated memory blocks — block RAM, BRAM —
which are fast, dense, and **synchronous by construction**. There is no such
thing as a combinational BRAM read. A combinational read has to be built out of
general-purpose logic instead, which is fine for a few dozen words and
catastrophic for a few thousand.

So the shape of the choice is: **read this cycle for small memories, next cycle
for anything BRAM-sized**, with the consumer pipelined to absorb it.

## The gotcha this used to be

This was for a long time the most expensive trap in the toolkit, and it is worth
knowing even though you can no longer walk into it:

**A combinational read of a BRAM-sized memory has a hidden one-cycle latency on
silicon — while the simulator and Verilator both pass.**

Both simulators honour the RTL you wrote, and the RTL says combinational. The
synthesis tool, faced with a memory too big for logic, puts it in a BRAM
anyway — and a BRAM read takes a cycle. Your design is off by one, on hardware
only, with every test green. No cycle-accurate model can catch that, because it
is not a cycle-modelling question.

**The fix was to stop leaving the decision to the synthesiser.** That is what
`distributedMem`/`blockMem`/`mem` are for, and why a combinational read is
refused on the latter two:

```
memRead on 'store', whose storage the synthesiser chooses — and if it chooses
block RAM this read gains a cycle on silicon that no check here would catch.
Declare it distributedMem to mean it, or use memReadPort and pipeline the
consumer
```

The declaration also reaches the emitted Verilog as a `ram_style` attribute, so
the synthesiser is told rather than asked, and the cost shows up as LUTs in the
utilization report instead of as a wrong answer on the board. Try it: change
`distributedMem` to `mem` in the source and the design stops elaborating.

The general lesson survives the specific fix — **some classes of bug are visible
only to a synthesis tool**, which is why this repo keeps a list of hardware
gotchas alongside its test suites. The ones worth turning into elaboration
errors get turned into elaboration errors; this page documents one that made the
journey.

## Read-first

Write and read the same address on the same cycle and you get the **old** value
— read-first. Both the simulator and the emitted Verilog agree, deliberately, so
this is one place where "it worked in sim" does mean something.

Try it: `waddr = 5, wdata = 0x11, wen = 1, raddr = 5`, then step and watch
`this_cycle_out`.

## Two more things silicon cares about

- **A memory array does not reset.** Press **Reset** and the contents survive,
  because that is what BRAM does — the read-port output registers reset, the
  array does not. Do not assume zeros at startup unless you initialised it.
- **Two write sites on one memory destroy BRAM inference**, even when they are
  provably mutually exclusive — synthesis reports it and silently builds
  something much worse. Like the read-latency trap above, this one has been
  taken out of your hands: several `memWrite` calls on one memory **fold into a
  single priority-muxed write site**, latest call winning, so the merge happens
  whether or not you knew you needed it.

## Try this

- Write four different values to four addresses, then page through the memory
  tab.
- Set `wen = 1` and hold it while changing `wdata` every step — a memory filling
  up, one word per cycle.
- Watch `this_cycle_out` and `next_cycle_out` together while changing `raddr`
  every step; `next_cycle_out` is `this_cycle_out` delayed by exactly one.

## See also

- [**ROM**](romTable.md) — the same declaration with contents, which arrives in the bitstream
  already filled.
- [**Counter**](counter.md) — the register that `memReadPort` has on its output, met on its
  own.
- [**Sequencer**](sequencer.md) — what drives a memory once the accesses have to happen in an
  order.
