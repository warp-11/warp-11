# Drive it from Rust

[**Start your own project**](start-a-project.md) got a design running in the simulator. This page adds
the other half: a host program that talks to it over AXI, written in Rust —
and running against the simulator, so **still no FPGA**.

That last part is the point. The driver you write here is the same code that
will `mmap` `/dev/mem` on a board. It never learns which world it is in.

## Give the design a register map

Pick up the `Blinker` from the previous page. To be reachable from a host it
needs an AXI-Lite slave, and the way you get one is to declare the register map
as a value:

```fsharp
open Warp11

/// Declared once. This value generates the AXI-Lite slave in the fabric *and*
/// the Rust constants the host compiles against.
module BlinkerMap =
    let id = roConst "id" 0x0UL 0xB11EDUL
    let enable = rwReg "enable" 0x4UL 1 0UL
    let count = roField "count" 0x8UL 0 24

    let map =
        { apertureAddrWidth = 4
          entries = [ id; enable; count ] }

let blinkerAxi =
    designClocked axiClock "BlinkerAxi" (fun () ->
        let regs = axiLiteSlaveOf BlinkerMap.map
        let counter = reg "counter" 24

        If (regs.value BlinkerMap.enable) (fun () -> counter + 1UL ==> counter)
        regs.drive BlinkerMap.count counter

        let led = outputBit "led"
        slice 23 23 counter ==> led)
```

Three kinds of entry, which is most of what a map ever needs: `roConst` is a
fixed value the host reads to know which bitstream it is talking to, `rwReg` is
written by the host and read by the design, and `roField` is provided by the
design and read by the host. `designClocked axiClock` gives the module the
AXI-style clock and active-low reset the bus expects.

> If you name a map entry the same as a signal — `count` and `reg "count"` —
> elaboration stops with *"declared twice"*. That is the one-declaration rule,
> and it is why the register above is `counter`.

## Two commands on the F# side

Add them as cases in `main`:

```fsharp
[<EntryPoint>]
let main argv =
    match argv with
    | [| "simserve" |] -> SimAxi.serve (SimAxi.client (Sim blinkerAxi))
    | [| "layout"; path |] ->
        System.IO.File.WriteAllLines(path, regMapRsLines BlinkerMap.map)
        printfn $"wrote {path}"
    | _ -> printfn "%s" (emitVerilog blinkerAxi)
    0
```

**`simserve`** is the F# half of the bridge. `SimAxi.client` drives the design's
`s_axi_*` pins through real five-channel handshakes in the simulator, asserting
every step — a protocol mistake fails here, in something you can step, rather
than hanging on silicon. `SimAxi.serve` wraps that in the line protocol the Rust
side speaks.

**`layout`** writes the Rust constants out of the same map:

```sh
dotnet run -- layout ../blinker-driver/src/blinker_layout.rs
```

```rust
pub const APERTURE_BYTES: usize = 16;
pub const ID_OFFSET: usize = 0x000;
pub const ID_VALUE: u32 = 0x000b11ed;
pub const ENABLE_OFFSET: usize = 0x004;
pub const COUNT_OFFSET: usize = 0x008;
pub const COUNT_SHIFT: u32 = 0;
pub const COUNT_MASK: u32 = 0xffffff;
```

Nobody wrote those offsets twice. That is the seam the whole project is built
around: move a field in the F# map, re-emit, and the host either agrees or fails
to compile.

## The driver

A normal Rust crate. Both dependencies are path references, since Warp 11 is not
published to crates.io any more than it is to NuGet:

```toml
[dependencies]
warp11-runtime = { path = "../warp-11/runtime/core" }
warp11-host = { path = "../warp-11/runtime/host" }
```

```rust
mod blinker_layout;

use blinker_layout as layout;
use std::path::PathBuf;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_runtime::RegisterWindow;

/// Written against `RegisterWindow`, so it never learns which world it is in —
/// the simulator here, an mmap of /dev/mem on the board.
fn run<W: RegisterWindow>(w: &mut W) -> Result<(), W::Error> {
    let id = w.read32(layout::ID_OFFSET)?;
    assert_eq!(id, layout::ID_VALUE, "not the bitstream we expect");
    println!("id:      0x{id:05X}  — the right design");

    w.write32(layout::ENABLE_OFFSET, 1)?;
    let a = (w.read32(layout::COUNT_OFFSET)? >> layout::COUNT_SHIFT) & layout::COUNT_MASK;
    let b = (w.read32(layout::COUNT_OFFSET)? >> layout::COUNT_SHIFT) & layout::COUNT_MASK;
    println!("running: {a} then {b}");

    w.write32(layout::ENABLE_OFFSET, 0)?;
    let c = (w.read32(layout::COUNT_OFFSET)? >> layout::COUNT_SHIFT) & layout::COUNT_MASK;
    let d = (w.read32(layout::COUNT_OFFSET)? >> layout::COUNT_SHIFT) & layout::COUNT_MASK;
    println!("stopped: {c} then {d}");
    Ok(())
}

fn main() {
    let fsproj = PathBuf::from(std::env::args().nth(1).expect("path to the .fsproj"));
    let mut window = FsSimWindow::spawn(&fsproj).expect("simserve spawns and reports ready");
    run(&mut window).expect("the design answers");
}
```

`RegisterWindow` is the whole interface between a driver and the world: a 32-bit
read and a 32-bit write at an offset. `FsSimWindow` is one implementation — it
spawns your `simserve` and speaks to it. `MmapWindow` is the other, and it is
what you swap in for the board.

```sh
cargo run -- ../Blinker/Blinker.fsproj
```

```
id:      0xB11ED  — the right design
running: 2 then 4
stopped: 6 then 6
```

**`dotnet` has to be on `PATH`** for the spawn to work. If you get
`NotFound`, that is what happened.

## What that output tells you

The counter reads **2, then 4** — not 1 and 2. Each AXI-Lite read is a
two-cycle transaction, and *the design only advances while the bus is talking to
it*, because in this world the simulator's clock is driven by the conversation.
On silicon the fabric free-runs and you would see whatever it had reached. That
difference is worth internalising early: the bridge is cycle-accurate about the
design, not about wall-clock time.

Then `stopped: 6 then 6` — two reads, same value. The `enable` write landed and
the counter is holding, which is the host and the fabric agreeing about a
register that exists in both languages and was declared in neither of them
twice.

## On to a board

Nothing above changes. `run` is generic over `RegisterWindow`; you construct an
`MmapWindow` instead of an `FsSimWindow` and pass it the same function. What
*does* change is everything around it — synthesis, timing closure, loading a
bitstream, and the class of bug that only appears on real silicon. That is the
[**Hardware workflow**](dev-workflow.md) guide.

The order is deliberate. By the time you get to a board, the driver has already
been exercised against the design; what is left to debug is the hardware, not
the program.

## Where to go next

- [**Register map**](../hdl/Warp11.Tutorial/doc/registerMap.md) in the tutorial — the same mechanism at a smaller scale, with
  every entry kind laid out.
- [**How it fits together**](architecture.md) — where the bridge sits in the whole system.
- [**Hardware workflow**](dev-workflow.md) — synthesis and the board.
- `runtime/README.md` in the repository — the crates, cross-compiling for
  aarch64 without a cross-gcc, and the first-light binaries each accelerator
  ships.
