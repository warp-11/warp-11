# The tutorial

Thirty-four small designs, each with a page explaining what it teaches, in a
debugger you can step. Written for a developer who can program and has not
written RTL.

```sh
cd hdl/Warp11.Tutorial.App
dotnet run -c Release              # open it
dotnet run -c Release -- "RAM"     # straight to one design
dotnet run -c Release -- check     # the living checks
```

## The path

Read them in this order — it is a curriculum, not a reference. Four tiers: the
language, the small shapes it is written in, the stream layer, and the
substrates real accelerators are built from.

### The language

- **Counter** — ports, a register, and statements that drive them. A register
  *holds* when nothing drives it; a wire in that position is an error.
- **Comparator** — combinational logic, where nothing has a before or an after,
  and the `mux` every conditional folds into.
- **Priority mux** — statement order *is* priority, because the statements are
  folded rather than executed.
- **Dot product** — instantiation is function application, and every call is a
  separate piece of silicon. Area buys parallelism.
- **Your own modules** — defining one, and why a use site cannot tell a module
  from inline logic.
- **Bit shapes** — joining, filling, reversing, counting, and what one-hot is
  for. Also: an F# loop generating ports at elaboration time.
- **Signed operations** — the bits are identical, so signedness lives on the
  *operation* rather than the type.
- **Fixed-point** — a Q format in the type, where a multiply changes the format
  and renormalizing is a slice the target names.
- **RAM** — synchronous versus combinational reads as a silicon question, and
  the gotcha that passes both simulators and fails on the board.
- **ROM** — contents fixed at elaboration, so the bitstream arrives loaded.
- **Assertions** — a claim a design makes about itself, checked every cycle in
  simulation and compiled out of the silicon.
- **Sequencer** — one state per cycle, and waiting as the absence of a
  transition.

### The combinators

- **Delay chain** — a tag has to travel as far as the data it describes.
- **Edge detect** — turning a level into an event, in whatever domain `enable`
  defines.
- **LFSR** — testing a *property* rather than a golden vector, because wrong
  taps still produce a plausible stream.
- **Arbiter (one-hot)** — selection with no comparator in it, at log depth.
- **Adder tree** — depth 3 against a chain's 7, and the flagship gotcha:
  correct in every simulator, wrong on silicon.
- **Wrap counters** — the wrap is a *signal*, which is what builds a raster
  scan out of two counters.

### Streams

- **Stream pipe** — the two-wire contract, and that `map` is free.
- **Stream stages** — latency and throughput are different things, which is why
  pipelines get *deeper* to go faster.
- **Buffering** — a FIFO absorbs a burst, and its storage is not part of its
  contract: the depth decides LUTs or a block, and nothing a caller can name.
- **Fork and join** — broadcast is lockstep; merge arbitrates and does not
  order.
- **Farm** — dispatch is lowest-ready, not round-robin, so a farm only uses its
  width under pressure.
- **Carrying context** — a slow stage keeps the caller's data, so no caller
  keeps a shadow queue and no stage grows a passthrough it never reads.
- **Stall probes** — the two ways to waste a cycle, blocked and starved.
- **Pipeline as data** — multiplicity and telemetry as properties of a
  description rather than calls the neighbours can see.
- **Flow (valid only)** — a producer that cannot be told to wait, and the loss
  handed back rather than swallowed.

### Substrates

- **Barrel lane** — hyperthreading with the schedule fixed at build time, so it
  needs no hazard logic at all.
- **PRNG** — a real generator in fabric, and why it has no multiply in it.
- **FIR filter** — the impulse response *is* the coefficient list; N taps is N
  multipliers and the time stays one cycle.
- **Neighborhood** — the stencil gather runs at elaboration time, which is why
  4,096 cells update in one cycle.
- **Shared unit** — sharing an expensive arm, and the measurement that says
  when not to.
- **Register map** — the host seam, and the layout generated from the same
  definition the slave was.
- **DDR master** — the fabric reaching out to memory, and the arm gate that
  keeps it from doing so too early.

## How this project is built

Three things that are deliberate, and the reasons are worth knowing if you are
building something similar.

**The tutorial owns its designs.** There is a second catalog next door
(`Warp11.Designs`) that exists to cover every node of the IR for the
differential oracle. It will happily contort a design to reach one more case.
A teaching design has to carry exactly one idea and no passengers. Sharing them
meant each compromising the other, so these designs are duplicated on
purpose.

**The debugger is a component, not an application.** `Warp11.SimView` is a
library. It knows how to watch signals, window a memory and draw a trace — the
things true of *any* design — and nothing about what a particular design means.
This project supplies the catalog *and* the two extra panels (`about` and
`source`) as ordinary values:

```fsharp
let panels = Pages.both Registry.catalog
let source initial = View.FromCatalog(Registry.catalog, initial)
```

That is the seam any project uses. A Game of Life grid, an AXI register-map
view, a custom display for whatever you built — all arrive the same way, as a
`Panel` with a label, a render function, and a `placement`.

**Placement is why the tutorial reads the way it does.** `about` and `source`
ask to be `Alongside`, so they get a column of their own and stay on screen
while you work — a page can say *poke `enable` and press Step* and you can do it
without leaving the words. A panel that is a thing to look *at* rather than read
from asks for `WithInstruments` instead and becomes another tab beside `watch`,
`memory` and `waveform`. A debugger whose host offers no `Alongside` panel keeps
the two-column layout it always had; an empty third of a window is worse than no
column at all.

**The checks test what the pages claim.** Not that the designs elaborate — that
the counter *holds*, that `sel1` outranks `sel0`, that the async read leads the
sync one by exactly one cycle, that the one-hot round trip is the identity. A
page teaching something the silicon does not do would be worse than no page.
There are also two that guard the set rather than the designs: every entry has
a page, and every "see also" link resolves.

All thirty-four run through the Verilator differential alongside everything else.

**The browser build is for teaching, not for work.** `Warp11.Tutorial.Browser`
publishes this same project to WebAssembly, which is what
[warp11.org/try/](https://warp11.org/try/) serves, and it runs the simulator
around **140× slower than the desktop build** — the runtime interprets IL in the
browser, with a jiterpreter compiling the hot traces. That is ample for stepping
a counter and watching a register move, which is the whole job of these designs.
It is not the thing to reach for when you want to simulate a frame of Mandelbrot:
run `Warp11.Tutorial.App` for that, or drive `Sim` directly. Nothing about the
designs differs between the two — only how fast the cycles go by.

## Adding an entry

1. A design in `Designs.fs`, written to be read.
2. A line in `Registry.fs` — the label, and the binding via `nameof` so the
   compiler checks it.
3. A page at `doc/{binding}.md`. The binding is the key to both the prose and
   the source pane, so there is one name rather than two that can drift.
4. A claim in `Warp11.Tutorial.App/Main.fs` asserting whatever the page
   promises.

Then `dotnet run -- check`, and `../run_differential.sh` for the oracle.
