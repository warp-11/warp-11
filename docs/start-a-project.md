# Start your own project

The tutorial pages each teach one mechanism inside a design that already exists.
This page goes the other direction: an empty folder, and something of yours
running in the debugger about ten minutes later. **No FPGA required** — none of
this needs hardware, which is rather the point.

If you have not read [**How it fits together**](architecture.md), the one idea to carry in is that
your F# does not run on the FPGA. It runs once, on your machine, and builds a
circuit.

## What you need

The .NET 10 SDK. Nothing else — Warp 11 is on NuGet, so there is no repository
to clone and no solution of ours to build.

```sh
dotnet new console -lang "F#" -o Blinker
cd Blinker
dotnet add package Warp11 --prerelease
dotnet add package Warp11.SimView.Desktop --prerelease
```

`Warp11` is the DSL, the elaborator, the simulator and the emitter.
`Warp11.SimView.Desktop` is the step-through debugger, and is optional — leave
it out if you only want to simulate and emit.

`--prerelease` is needed until 0.1.0; the API still moves between alphas, so pin
the version in your `.fsproj` once you have something you care about rather
than floating to whatever is newest.

## A design, and a way to look at it

Replace the generated `Program.fs` with this.

```fsharp
module Blinker

open Warp11
open Warp11.Catalog

/// A counter whose top bit is an LED: it turns over every 2^23 clocks, which
/// at 100 MHz is a blink you can see.
let blinker =
    design "Blinker" (fun () ->
        let enable = inputBit "enable"
        let led = outputBit "led"

        let count = reg "count" 24
        If enable (fun () -> count + 1UL ==> count)

        slice 23 23 count ==> led)

let catalog =
    designs [ entry "Blinker" (nameof blinker) (fun () -> blinker)
              |> watching [ "count" ]
              |> poking [ "enable", 1UL ] ]

[<EntryPoint>]
let main _ = Warp11.SimView.Desktop.debugCatalog catalog
```

```sh
dotnet run -c Release
```

The debugger opens on your design. Three signals down the left, `enable` already
at 1, `count` on the watch list, and the Verilog it becomes in the pane on the
right.

**A catalog is how a project hands its designs to a debugger.** It is worth the
four lines even for one design, because of what the two modifiers do:

- **`poking`** sets inputs when the session opens. Without it `enable` sits at
  0, and the first **Step** you press does nothing — a design that looks broken
  rather than idle. This is the single most common first-run confusion, and it
  is one line to remove.
- **`watching`** names signals to put on screen. You do not need to list
  `enable` and `led`: the debugger watches a design's *ports* by itself, because
  the ports are the interface and it can work them out. What it cannot guess is
  which of your registers you care about — `count` is one of an unbounded number
  a design might have, and only you know the page is about it.

`designs` builds a catalog from entries and nothing else. Catalogs can also
carry a written page and the source text per design, which is what the
[tutorial](../hdl/Warp11.Tutorial/doc/counter.md) does — that is `embedded`, and
it wants those files declared in your `.fsproj`. `designs` is the version for a
project that has not written any prose, which is most of them.

## What just happened

Click the **verilog** tab:

```verilog
module Blinker (input clk, input rst, input enable, output led);
    reg [23:0] count;
    assign led = count[23:23];
    always @(posedge clk) begin
        if (rst) begin
            count <= 24'd0;
        end else begin
            count <= (enable ? (count + 24'd1) : count);
        end
    end
endmodule
```

Three things there are worth noticing, because each is a rule rather than a
coincidence.

**The clock and reset appear on their own.** You never declared them. Using a
`reg` implies them, so they are added to the port list at emission — which is
also why nothing in the F# above mentions a clock edge.

**`If` became a conditional expression, not a branch.** `If enable (…)` ran once,
during elaboration, and what it left behind was a mux: `enable ? count + 1 :
count`. That is the elaboration-versus-execution distinction made concrete. An
ordinary F# `if` would have chosen one of the two while the program ran and
emitted only that one.

**`slice 23 23 count` became a part-select**, not a shift and a mask. Taking bits
apart is wiring in this IR, and wiring costs nothing.

Now press **Step** a few times and watch `count` climb, or type
`count == 0x7fffff` into the breakpoint box and press **Run**.

## Split it, so you can test it

One project was the fastest way to see something move. It is the wrong shape the
moment you want tests, because a test project should not have to drag a desktop
GUI in behind it. Three projects:

```
Blinker/            a library — your designs, and nothing else
Blinker.App/        an executable — opens the debugger
Blinker.Test/       an executable — runs the tests
```

```sh
cd ..
dotnet new classlib -lang "F#" -o Blinker.App          # then set OutputType to Exe
dotnet new console  -lang "F#" -o Blinker.Test
```

Move the `blinker` design into `Blinker/` as a library (delete its
`<OutputType>Exe</OutputType>` and its `[<EntryPoint>]`), leave the catalog and
`main` in `Blinker.App/`, and give each executable a reference to the library:

```sh
cd Blinker.App  && dotnet add reference ../Blinker/Blinker.fsproj
cd ../Blinker.Test && dotnet add reference ../Blinker/Blinker.fsproj
dotnet add package Expecto
```

The library depends on `Warp11` alone. Only `Blinker.App` depends on
`Warp11.SimView.Desktop`, so the test project never sees Avalonia.

## A test

**Expecto tests are values, not attributes** — nothing discovers them, so the
test project is an ordinary console app whose `main` hands the list to the
runner. That is the one thing that catches people out.

`Blinker.Test/Program.fs`:

```fsharp
module Blinker.Test.Program

open Expecto
open Warp11

let private blinking (enable: uint64) =
    let sim = Sim Blinker.blinker
    sim.Reset()
    sim.Poke("enable", enable)
    sim

let private tick (sim: Sim) n =
    for _ in 1 .. n do
        sim.Tick()

let tests =
    testList
        "blinker"
        [ test "enable gates rather than resets" {
              let sim = blinking 1UL
              tick sim 1000
              sim.Poke("enable", 0UL)
              tick sim 1000
              Expect.equal (sim.Peek "count") 1000UL "dropping enable lost the count"
          }

          test "the led divides the clock by 2^24" {
              let sim = blinking 1UL
              let half = 1 <<< 23

              tick sim (half - 1)
              Expect.equal (sim.Peek "led") 0UL "the led rose early"

              sim.Tick()
              Expect.equal (sim.Peek "led") 1UL "the led did not rise at 2^23"

              tick sim (half - 1)
              Expect.equal (sim.Peek "led") 1UL "the led fell before the end of its high half"

              sim.Tick()
              Expect.equal (sim.Peek "count") 0UL "the counter did not wrap at 2^24"
              Expect.equal (sim.Peek "led") 0UL "the led did not fall when the counter wrapped"
          }

          test "the design emits" {
              let verilog = emitDesign Blinker.blinker
              Expect.stringContains verilog "module Blinker" "no module header"
          } ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
```

```sh
dotnet run -c Release                       # the whole suite
dotnet run -c Release -- --filter blinker   # one list
```

Two of those tests are worth a second look.

**The divider test walks a full period — 16.7 million cycles — and takes about
half a second**, because `Sim` compiles the design when you construct it rather
than interpreting it per cycle. Simulating a design properly is not the slow
step you may be bracing for.

**It walks the period rather than checking one point** because a golden
assertion like *"`led` is 1 after 2^23 ticks"* also passes for a design that
latches high and never falls. Pinning all four edges is the difference between
testing the property and testing an example of it, and it is the habit this
whole toolkit is built on: an LFSR with the wrong taps still produces a
plausible-looking stream, and only walking its full period says otherwise.

**`emitDesign` is doing more than producing text.** It runs three elaboration
checks first — `checkWidths`, `checkNames`, `checkStreams` — and refuses on any
of them, because each catches something that is silent through elaboration *and*
through synthesis. Then it emits every module in the design, children first,
deduplicated by name, so a component instantiated forty times is emitted once.
`Blinker` is a single flat module, so there are no children yet. There will be
the moment you instantiate anything, and you will not have to remember to do
anything differently.

## Where to go from here

- **Change the width.** Make `count` 8 bits and slice bit 7, then step it. A
  blink slow enough to watch turn over by hand is the fastest way to feel what a
  register is.
- **Add a port.** `let speed = input "speed" 2`, and choose which bit drives the
  LED with `selectIndexed` — now the design is configurable while it runs.
- **Then parameterise it at elaboration instead**, by making `blinker` a
  *function* of the counter width. That is the move the whole toolkit is built
  around, and the difference between the two is the thing worth understanding: a
  104-lane compute pod is a `List.map`, not 104 copies of anything. Your tests
  will thank you too — a 4-bit blinker walks several full periods in
  microseconds.
- **Give it a register map** so a host program can drive it. That is the
  [**Register map**](../hdl/Warp11.Tutorial/doc/registerMap.md) tutorial, and it is where the Rust side first appears.
- **Put it on hardware.** The [**Hardware workflow**](dev-workflow.md) guide covers synthesis, the
  board, and the gotchas that only show up on silicon.

The thirty-odd **Tutorial** pages are the reference for individual mechanisms,
each one steppable in your browser without installing anything. This page was the
doorway; those are the rooms.

## When you might want the repository after all

The package is the right way to *use* Warp 11. Clone
[the repository](https://github.com/warp-11/warp-11) when you want to do something to it:

- **Run the differential oracle against your own designs.** `Warp11.Diff`
  generates a self-checking Verilog testbench from a Sim trace, and
  `run_differential.sh` compiles it under Verilator. That harness lives in the
  repository, and it is the strongest check available on a design.
- **Read or change the library itself.** Though for reading, note that the
  packages ship source-linked with symbols — your debugger can already step
  straight into `Warp11`'s own code from your project, without a clone.
