# Warp 11

**An F# HDL that runs on real FPGAs.** Describe hardware in F#, step it in a
cycle-accurate debugger, emit Verilog, and put it on silicon — then drive it
from a Rust runtime over AXI. The same source elaborates the simulator and the
bitstream, so what you debug is what you deploy.

*A desktop application rendering Game of Life out of the fabric of a KV260:
64×64, 2 billion generations in. The blocks and blinkers are what a random soup
settles into after that many — at this rate it gets there in the first few
microseconds. The 503M/s on the readout is a k=3 combinational unroll that has
since been removed; the design now runs one generation per 166.67 MHz cycle,
**167 million generations per second**.*

![Game of Life running on a KV260, 2 billion generations into a random soup](https://warp11.org/images/gol-500m.png)

- **Simulate the whole application, host side included.** The same driver code
  runs against the simulator and against the board, so the program and the
  hardware can be tested together before either reaches silicon. The simulator
  itself is checked against Verilator on the emitted Verilog, cycle by cycle,
  across every design in the library.
- **A step-through debugger, attachable to a design already running.** Watch
  lists, memory windows, waveforms, and breakpoints written as expressions over
  your own signals, extensible with your own panels because what you want to
  look at is specific to what you built. The session is a first-class object
  rather than something a window owns, so **your application and a debugger can
  watch one running design at the same time** — set a breakpoint in the debugger
  and your app's display stops with it. The Game of Life example does this in
  about twenty lines and
  [shows you how](https://warp11.org/examples/game-of-life). VCD export when you
  want GTKWave's zoom, search and cursors instead.
- **Board and host integration as a first-class concern.** One register-map
  definition emits the AXI slave *and* the host driver's constants, so the
  program and the fabric cannot disagree about the register map.
- **Scaling is a number you change.** Going from four parallel workers to a
  hundred is editing one number: the stages wire themselves up, back-pressure is
  handled for you, and the counters that say which stage is starving read the
  same in simulation and on the board.
- **Whole classes of bug unrepresentable.** One driver per signal, one
  declaration per name, width checks gating emission, streams consumed exactly
  once, a memory declaring whether it is block or distributed so a
  combinational read of the wrong one cannot elaborate — each from a real bug
  that was legal Verilog, and therefore invisible to every tool downstream.

> **Status: 0.1.0.** Everything described here runs today. Four packages on
> NuGet — `Warp11`, `Warp11.SimView`, `Warp11.SimView.Desktop`, `Warp11.Host`
> — and four boards. See [what is not built](#what-is-not-built).

## Try it without installing anything

The debugger runs in a browser. Pick a design, poke an input, press **Run**, and
watch the registers move — no toolchain, no board, no account. It is a place to
learn the mechanisms rather than a workbench; real simulation happens in the
desktop build, which is a great deal faster.

**[▶ Open the tutorial in your browser](https://warp11.org/try/)**

## What it looks like

A counter, in the Warp 11 DSL:

```fsharp
let counter =
    design "Counter" (fun () ->
        let enable = inputBit "enable"
        let clear = inputBit "clear"
        let count = output "count" 8
        let r = reg "r" 8

        If clear (fun () -> 0UL ==> r)
        Else (fun () -> If enable (fun () -> r + 1UL ==> r))

        r ==> count)
```

`emitDesign counter` produces:

```verilog
module Counter (input clk, input rst, input enable, input clear, output [7:0] count);
    reg [7:0] r;
    assign count = r;
    always @(posedge clk) begin
        if (rst) begin
            r <= 8'd0;
        end else begin
            r <= (clear ? 8'd0 : (enable ? (r + 8'd1) : r));
        end
    end
endmodule
```

That is the whole story in miniature: ordinary F# runs at **elaboration** time
and leaves a circuit behind. Loops, folds, recursion and higher-order functions
are all available, and none of them exist at run time — a `for` loop that
creates four ports leaves four ports, not a loop.

## What it has done

Three accelerators run end to end on a Xilinx KV260 — F# elaborator → emitted
Verilog → Vivado bitstream → Rust driver over AXI:

- **Mandelbrot** — 1400×800 at 256 iterations in **4.30 ms of fabric time**
  (261 Mpx/s; 5.21 ms end to end), across 104 barrel-threaded lanes filling
  **100% of the board's 1,248 DSPs** at 166.67 MHz.
- **GEP** — a genetic-programming generation loop entirely in fabric,
  bit-exact against a software twin, **0.85 µs per offspring**.
- **Game of Life** — 64×64, whole grid updated in a single cycle, streamed to a
  host UI over a triple-buffered snapshot path.

None of those are demos written to look good in a README. Each has a software
twin it is checked against bit-for-bit, and the numbers are measured on the
board.

**And the same designs build for boards with no processing system at all** —
an iCEBreaker, a Go Board, an Icepi Zero — from one mapping file each, the
host reaching them over a UART instead of AXI, a WAV or an image through the
design coming back byte-identical to the simulator. The receipts, from each
board's own toolchain report:

| board | part | hello | what fits |
|---|---|---|---|
| KV260 | ZU5EV, 117 k LUTs, 1 248 DSPs | AXI-Lite | everything here |
| Icepi Zero | ECP5-25F, 24 k cells | 143 cells, 212 MHz | Mandelbrot at 2 lanes, Life at 32×32, every audio and table example |
| iCEBreaker | iCE40 UP5K, 5 280 cells | 192 cells, 60 MHz | gain, adder, passthru, tone, Life at 8×8 |
| Go Board | iCE40 HX1K, 1 280 cells | 199 cells, 167 MHz | passthru, tone, adder |

Every ✗ in the full matrix is a refusal with its number, not a guess —
[Boards](https://warp11.org/guides/boards) has the whole table.

## Why it exists

Most HDL toolkits ask you to be a hardware engineer who tolerates a
general-purpose language. Warp 11 is aimed at a **software developer who wants
an accelerator** and is willing to learn what silicon actually demands — which
is a real amount, but far less than the tooling usually implies.

The honest comparison against Chisel, SpinalHDL, HardCaml, Amaranth, Clash,
Bluespec and Veryl — including where Warp 11 is behind — is in
[HDL_COMPARISON.md](https://warp11.org/guides/comparison).

## Getting started

You need [.NET 10](https://dotnet.microsoft.com/download). Nothing else, until
you want a bitstream.

```sh
git clone https://github.com/warp-11/warp-11.git
cd warp-11/hdl/Warp11.Tutorial.App
dotnet run -c Release
```

That opens the tutorial: **34 designs**, from a counter to a register map and an
AXI master, each with a page explaining what it teaches and the source that
defines it, in a debugger you can step. Every page's claims are checked against
the design that makes them, so a page cannot drift from what the hardware does.

To check the toolchain against itself — the differential oracle, which needs
[Verilator](https://verilator.org). Every design is simulated, its emitted
Verilog is executed against a generated testbench asserting that exact trace,
and any divergence fails:

```sh
cd hdl && ./run_differential.sh

FIRTOOL_LEG=1 ./run_differential.sh   # adds the firtool leg; roughly doubles it
```

`firtool` is never a dependency for using Warp 11 — it is how the claim that
the IR did not invent its own semantics gets measured.

## Where to go next

**Getting started, in order:**

- **[How it fits together](https://warp11.org/guides/architecture)** — the map. What elaboration
  is, what the simulator and the Verilog emitter each do with it, and where the
  Rust runtime sits. Start here if you have not used an HDL before.
- **[Start your own project](https://warp11.org/guides/start-a-project)** — an empty folder to a
  design of yours running in the step-through debugger. No FPGA required.
- **[Drive it from Rust](https://warp11.org/guides/drive-it-from-rust)** — give it a register map
  and a host program, still running against the simulator.

**Then, in any order:**

- **[The tutorial](https://warp11.org/tutorial/counter)** — start at Counter and
  read through Sequencer, which is the first tier and enough to build something.
  Streams, the stdlib and the board-facing pages follow. It assumes you can
  program and does not assume you have written RTL.
- **[Streams](https://warp11.org/guides/streams)** — the ready/valid layer, and `wormhole`, the
  one call that connects anything stream-shaped.
- **The examples** — [Mandelbrot](https://warp11.org/examples/mandelbrot) and
  [Game of Life](https://warp11.org/examples/game-of-life), each with its own write-up.
- **[Runtime and host drivers](https://warp11.org/guides/runtime)** — the crates, cross-compiling
  for the board without a cross-gcc, and the first-light binaries.
- **[Boards](https://warp11.org/guides/boards)** — the four boards, the mapping file
  that puts a design on one, and [how to add your own](https://warp11.org/guides/add-a-board).
- **[Hardware workflow](https://warp11.org/guides/dev-workflow)** — getting a bitstream onto a
  KV260 and driving it.

## What is not built

Being clear about this is cheaper than letting you find out:

- **The API is pre-1.0.** 0.1.0 is the first release whose public surface was
  audited rather than accreted; things will still move between minor versions,
  and the package notes say what did.
- **Four boards, two toolchains.** A Xilinx KV260 through Vivado, and three
  Lattice boards through the open flow. Intel's Quartus is the next toolchain
  and is not in this release. What a design can plug into is short: AXI4-Lite
  from a register map, AXI4 master read and write, a UART register link, I2S
  and PDM in and out — where the established libraries ship half a dozen bus
  families and a shelf of controllers.
- **GEP runs on the KV260 alone.** Its cluster reaches DDR through two AXI
  masters, and carrying it over a UART is a redesign that did not make this
  release.
- **No formal verification.** Clock domains and checked crossings exist;
  nothing proves a design beyond simulation and the differential.

## License

[Apache License 2.0](https://github.com/warp-11/warp-11/blob/main/LICENSE).
