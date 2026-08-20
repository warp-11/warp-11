# Start your own project

The tutorial pages each teach one mechanism inside a design that already exists.
This page goes the other direction: an empty folder, and something of yours
running in the debugger about ten minutes later. **No FPGA required** — none of
this needs hardware, which is rather the point.

If you have not read [**How it fits together**](architecture.md), the one idea to carry in is that
your F# does not run on the FPGA. It runs once, on your machine, and builds a
circuit.

## What you need

The .NET 10 SDK, and a clone of the repository.

```sh
git clone https://github.com/warp-11/warp-11.git
cd warp-11/hdl
dotnet build Warp11.sln
```

**There is no NuGet package yet.** You use Warp 11 by referencing the library
project from a clone, so your design lives beside it rather than pulling it in
as a dependency. That is a real limitation and worth saying plainly: updating
means `git pull`, and there is no version to pin. Packaging is a decision the
project has not made.

## A project of your own

Put a folder next to the others in `hdl/` — call it `Blinker` — with two files
in it.

`Blinker/Blinker.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
    <ItemGroup>
        <Compile Include="Program.fs"/>
    </ItemGroup>
    <ItemGroup>
        <ProjectReference Include="../Warp11/Warp11.fsproj"/>
        <ProjectReference Include="../Warp11.SimView.Desktop/Warp11.SimView.Desktop.fsproj"/>
    </ItemGroup>
</Project>
```

The first reference is the DSL, the elaborator, the simulator and the emitter.
The second is the debugger, and is optional — leave it out if you only want to
simulate and emit.

`Blinker/Program.fs`:

```fsharp
module Blinker

open Warp11

/// A counter whose top bit is an LED: it turns over every 2^23 clocks, which
/// at 100 MHz is a blink you can see.
let blinker =
    design "Blinker" (fun () ->
        let enable = inputBit "enable"
        let led = outputBit "led"

        let count = reg "count" 24
        If enable (fun () -> count + 1UL ==> count)

        slice 23 23 count ==> led)

[<EntryPoint>]
let main _ =
    printfn "%s" (emitVerilog blinker)

    let sim = Sim blinker
    sim.Poke("enable", 1UL)

    for _ in 1 .. (1 <<< 23) do
        sim.Tick()

    printfn "led after 2^23 ticks: %d" (sim.Peek "led")
    0
```

A single `open Warp11` is the whole surface: the DSL, `Sim`, and `emitVerilog`.

```sh
cd Blinker && dotnet run
```

## What just happened

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

```
led after 2^23 ticks: 1
```

Three things in that Verilog are worth noticing, because each is a rule rather
than a coincidence.

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

Meanwhile the simulator ran the same design for 8.4 million cycles in about a
second and reported the LED high — which it should be, since bit 23 of a counter
that has just reached 2²³ is precisely the bit that turned over.

## Step it

Add one line to `main`, before the `0`:

```fsharp
Warp11.SimView.Desktop.debug "Blinker" blinker
```

Run again, and the step-through debugger opens on *your* design: your three
signals down the left, a watch list, a memory pane, a waveform, and a breakpoint
box that takes expressions over your own signal names. Try `count == 0x7fffff`
and press **Run**.

That one line is the entire integration. `Warp11.Gep` and `Warp11.Mandelbrot`
open their debuggers exactly the same way.

## Where to go from here

- **Change the width.** Make `count` 8 bits and slice bit 7, then step it. A
  blink slow enough to watch turn over by hand is the fastest way to feel what a
  register is.
- **Add a port.** `let speed = input "speed" 2`, and choose which bit drives the
  LED with `selectIndexed` — now the design is configurable while it runs.
- **Then parameterise it at elaboration instead**, by making `blinker` a
  *function* of the counter width. That is the move the whole toolkit is built
  around, and the difference between the two is the thing worth understanding: a
  104-lane compute pod is a `List.map`, not 104 copies of anything.
- **Give it a register map** so a host program can drive it. That is the
  [**Register map**](../hdl/Warp11.Tutorial/doc/registerMap.md) tutorial, and it is where the Rust side first appears.
- **Put it on hardware.** The [**Hardware workflow**](dev-workflow.md) guide covers synthesis, the
  board, and the gotchas that only show up on silicon.

The thirty-odd **Tutorial** pages are the reference for individual mechanisms,
each one steppable in your browser without installing anything. This page was the
doorway; those are the rooms.
