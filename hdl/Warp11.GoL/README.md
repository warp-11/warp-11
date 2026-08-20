# Game of Life

A 64×64 Conway grid that updates **the entire board in one clock cycle**,
streamed to a desktop UI over a triple-buffered snapshot path.

*Status: on silicon. The smallest of the three accelerators and the best one to
read first.*

*`Warp11.GolView` driving the fabric flat out: 503 million generations per
second, 2 billion generations into a random soup. What is left on screen is
the ash — blocks, blinkers and beehives — which at this rate it reaches within
the first few microseconds.*

![The Game of Life live view running off a KV260 at 503 million generations per second](../../docs/images/gol-500m.png)

## Why it is worth reading

Game of Life is the clearest demonstration of what an FPGA is actually for.

A CPU updates 4,096 cells one at a time. The FPGA builds 4,096 copies of the
cell-update logic — each one nine wires in, one wire out — and they all settle
at once. One cycle per generation, regardless of grid size, until you run out of
board.

That is the trade at its starkest: **area buys time**, and if the work is
parallel and the board is big enough, the time goes to one cycle.

## The shape of it

```
GameOfLife            the grid, and one update rule instanced per cell
  └── neighborhood    the 3×3 stencil, with an edge policy
GoLAxi                the slave: control registers, and the snapshot path
  └── streamConflate3 triple-buffered frames out to PS DDR
```

**The stencil is a stdlib entry, not bespoke code.** `neighborhood` takes a
stencil shape and an edge policy (wrap, clamp, or a constant) and hands back the
neighbours of a cell. The same function draws Sobel's 3×3 window in the image
pipeline. That is the general pattern: an example that needs something is where
the library entry comes from.

**Conflation is what makes the UI honest.** The fabric runs generations far
faster than any display can show them, so the snapshot path keeps three buffers
and always hands the host the newest complete frame, dropping the ones nobody
could have seen. The host never waits for the fabric and the fabric never waits
for the host.

## Running it

In simulation, with the desktop UI:

```sh
cd hdl
dotnet run -c Release --project Warp11.GolView.Desktop
```

The UI is also the place the debugger's customization seam gets used: the Game
of Life view opens a debugger on the *same* session it is rendering, so you can
watch the RTL while the grid animates.

Emit and deploy:

```sh
dotnet run -c Release --project Warp11.GoL.App -- hardware <repo-root>
# then build_golfs_axi.tcl, the gol-fs app, and the gol-daemon on the board
```

## A hardware lesson this example paid for

**Never unload a bitstream while a fabric AXI master has writes in flight.**
Tearing down the PL mid-transaction leaves the PS-side write path with orphaned
beats and a *permanent* address/data pairing skew — every later write lands at
an offset. It survives reloading the app and clears only on reboot.

The symptom is very specific: frames arrive rotated by a few beats while the
populations and registers are perfect. If you see that, do not debug the RTL —
reboot the board.

The defenses are in the design: the write path is armed by a host-written
register so nothing flows before a driver arrives, and `gol-disarm` stops it
cleanly before unload. Both are there because of this bug.

## Files

- `Core.fs` — the grid and the update rule
- `Twin.fs` — the software twin the checks diff against
- `Wrapper.fs` — the slave, the snapshot path, the generated register map
- `../Warp11.GoL.App/` — checks, `diff`, `hardware`
- `../Warp11.GolView/` — the UI and the `IGolBus` seam;
  `../Warp11.GolView.Desktop/` runs it, `../Warp11.GoL.Browser/` is the
  site's live demo of this design in the simulator
