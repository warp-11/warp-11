# Warp11.MandelView — the Mandelbrot live view

The Avalonia FuncUI host app for the Mandelbrot frame (`mandelbrot-batch`): asks the
fabric for a frame and shows it, with the fabric's own cycle count beside the
round trip. The comms sit behind `IMandelBus` (`Bus.fs`) with two
implementations — which one a window gets is the composition root's choice,
invisible to the view:

- **SimulatedBus** — the software twin, rows in parallel; no board, no
  network. It renders with `laneTwin`, the same whole-pixel twin every
  Mandelbrot oracle judges the fabric against, so **the two buses produce
  identical pixels for a view** and a difference on screen is a real
  difference. Verified: a screenshot of the silicon render and one
  of the twin render differ in 0 of 797,680 pixels.
- **ZenohBus** — a Zenoh client into the board's `mandel-daemon` peer
  (`runtime/mandel-daemon`, key space documented there).

The view is fixed — no pan or zoom. Both were built and measured first, and
removed because `maxIterations` is 256 in the elaborated design: a few notches toward
the boundary and every pixel hits the cap and the frame goes black. The limit
is the iteration count, not the Q4.28 format. The backlog has the
measurements and what would earn them back.

## Running

```sh
export PATH="$HOME/.dotnet:$PATH"
dotnet run -c Release -- --software                   # local twin (the default)
LD_LIBRARY_PATH=<zenohc>/lib \
dotnet run -c Release -- tcp/192.168.1.172:7448       # the board daemon
```

The board path needs the zenoh-c native library, exactly as GolView does —
see `../Warp11.GolView/README.md` for the download. Note the **port is 7448**,
so mandel-daemon and gol-daemon can listen at once (only one of them can have
its bitstream loaded, but the ports do not have to be swapped too).

## The daemon side

The bitstream first — only one app is loaded at a time:

```sh
ssh ubuntu@<board> 'sudo xmutil unloadapp && sudo xmutil loadapp mandelbrot-batch'
```

Then the daemon, with the seam the bitstream's build directory wrote — it
reads the registers and the frame's shape from there. The cross build needs
no cross-gcc; `runtime/.cargo/config.toml` carries the two settings that make
it a plain command:

```sh
cd runtime
cargo build --release --target aarch64-unknown-linux-musl -p mandel-daemon
scp target/aarch64-unknown-linux-musl/release/mandel-daemon \
    <build dir>/mandelbrot_batch_layout.rs ubuntu@<board>:warp11-driver/
ssh ubuntu@<board> 'cd warp11-driver && nohup ./mandel-daemon --board mandelbrot_batch \
    --layout mandelbrot_batch_layout.rs > mandel-daemon.log 2>&1 &'
```

It renders once at startup and refuses to serve if the render never finishes,
so the log says whether the fabric is clocked and reaching DDR before any
client connects:

```
render: 723389 cycles = 4.340 ms fabric (4.341 ms wall)
startup render ok: the fabric is clocked and reaching DDR
mandel-daemon up: 1400x800 frame, arena 0x37200000, listening on tcp/0.0.0.0:7448
```

## Unloading

Lighter than GoL's protocol, because this accelerator is demand-driven: the
write master moves only between `start` and done, and the daemon blocks
between renders, so an idle daemon has nothing in flight. There is no `disarm`
step and none is needed — but the rule that matters still holds, so stop the
daemon before unloading rather than unloading underneath a render:

```sh
ssh ubuntu@<board> 'pkill mandel-daemon'
ssh ubuntu@<board> 'sudo xmutil unloadapp'
```

If frames ever come back visibly rotated while the registers read perfectly,
that is the HP0 AW/W pairing skew: reboot the board, do not debug
the RTL.

## Measured on the KV260

| | |
|---|---|
| fabric | **about 723,500 cycles = 4.34 ms**, 258 Mpx/s at 166.67 MHz |
| round trip | **53–75 ms** over eight renders (2026-11-09; it moves with the network) |
| frame on the wire | 1,120,000 bytes, cropped from the fabric's padded 1408 stride |
| correctness | pixel-identical to the software twin |

The round trip is dominated by moving 1.1 MB over TCP, not by the render — the
fabric is ~10× faster than the wire. That is also why coalescing never fired
in testing: six button presses 50 ms apart produced six renders, because at
4.3 ms a frame the accelerator outruns a human. The coalescing path is
exercised in `SimulatedBus` (where a render takes ~1 s) and by the daemon's
unit tests, not by the board.
