# Warp11.GolView — the GoL live view

The Avalonia FuncUI view for the `gol-fs` accelerator: renders the fabric's
conflate frames live and drives load/run/stop/reset. This project is the
library — the view and the buses; the runnable heads sit next door.
The comms sit behind `IGolBus` (`Bus.fs`) with three implementations — which
one a window gets is the composition root's choice, invisible to the view:

- **SimulatedBus** — the software twin on a timer; no board, no network.
  The GUI's bring-up rig and offline demo.
- **HdlSimBus** — the elaborated design itself in the F# Sim, behind the same
  seam, with a debugger window available on the very session the grid renders.
  In a browser it swaps its worker thread for a `DispatcherTimer` that pumps
  the session — WebAssembly has one thread.
- **ZenohBus** — a Zenoh client into the board's `gol-daemon` peer
  (`runtime/gol-daemon`, key space documented there). Desktop-only, so it
  lives in `../Warp11.GolView.Desktop/` with the desktop head.

The heads: `../Warp11.GolView.Desktop/` (all buses, picked by argument) and
`../Warp11.GoL.Browser/` (HdlSimBus only — the site's live demo at
`/live/gol/`, seeded and running on arrival).

## Running

```sh
export PATH="$HOME/.dotnet:$PATH"
cd Warp11.GolView.Desktop
dotnet run -c Release -- --sim                        # local twin
dotnet run -c Release -- --hdl                        # the RTL in the Sim
LD_LIBRARY_PATH=<zenohc>/lib \
dotnet run -c Release -- tcp/192.168.1.172:7447       # the board daemon
```

The board path needs the zenoh-c native library (the Zenoh-CS binding
P/Invokes `libzenohc.so`), version **1.6.2** to match Zenoh-CS 0.4.1:

```sh
curl -sL -o zenohc.zip https://github.com/eclipse-zenoh/zenoh-c/releases/download/1.6.2/zenoh-c-1.6.2-x86_64-unknown-linux-gnu-standalone.zip
unzip zenohc.zip -d zenohc && export LD_LIBRARY_PATH=$PWD/zenohc/lib
```

(Not vendored into the repo — it's a 30 MB platform binary. The wire between
the 1.6.2 client and the daemon's zenoh 1.9 is protocol-compatible,
verified live.)

The daemon side:

```sh
scp runtime/target/aarch64-unknown-linux-musl/release/gol-daemon ubuntu@<board>:warp11-driver/
scp hardware/board/gol-daemon.service ubuntu@<board>:.config/systemd/user/
ssh ubuntu@<board> 'systemctl --user daemon-reload && systemctl --user restart gol-daemon'
```
