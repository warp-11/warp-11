# Dev workflow: host → KV260 deploy

How to get an F#-elaborated design and its Rust driver onto a Kria KV260 and
run them, without typing passwords. Bitstream builds are separate and slower —
see [`hardware/board/README.md`](../hardware/board/README.md) for the board
side and the per-app daemons.

## Prerequisites

- KV260 booted, on the LAN, reachable by IP.
- Stock Ubuntu Kria image with the `ubuntu` user and `xmutil`.
- On the host: `ssh`, `rsync`, the .NET 10 SDK at `~/.dotnet`, and a Rust
  toolchain with the `aarch64-unknown-linux-musl` target
  (`rustup target add aarch64-unknown-linux-musl`).

**No cross-gcc is needed.** `runtime/.cargo/config.toml` carries the two
settings that make the musl target a plain cargo command: `rust-lld` as the
linker, and an empty `libdl.a` for the `-ldl` that zenoh asks for and musl
folds into libc.

## One-time setup

### 1. SSH key auth

```sh
ssh-copy-id ubuntu@<board-ip>
```

Type the board password once. If the key has a passphrase (recommended), add
`AddKeysToAgent yes` to `~/.ssh/config` so the first `ssh` per session prompts
once and caches; alternatively `ssh-add ~/.ssh/id_ed25519` each session.

Verify perms on the board are tight enough for sshd's `StrictModes`:

```sh
ssh ubuntu@<board-ip> 'chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys && ls -ld ~ ~/.ssh ~/.ssh/authorized_keys'
```

`~` must not be group-writable.

### 2. Passwordless sudo for xmutil

```sh
scp hardware/board/warp11-sudoers ubuntu@<board-ip>:/tmp/warp11-sudoers
ssh -t ubuntu@<board-ip> 'sudo install -m 440 -o root -g root /tmp/warp11-sudoers /etc/sudoers.d/warp11 && sudo visudo -c'
```

`visudo -c` must report `parsed OK` before you log out — a broken sudoers file
locks you out of sudo entirely. Verify:

```sh
ssh ubuntu@<board-ip> 'sudo -n /usr/bin/xmutil listapps | head -3'
```

### 3. Kernel modules

`u-dma-buf` (the DDR framebuffer and work queues) and optionally `warp11-dma`.
Both are DKMS-managed so they survive kernel updates. See
`hardware/board/README.md` §3.

## The loop

**1. Emit.** Each design project writes the directory Vivado builds from —
the Verilog, block design, constraints, overlay, packaging and the Rust seam —
from its mapping, the Verilog and the seam from one definition:

```sh
cd hdl
dotnet run -c Release --project Warp11.Mandelbrot -- bitstream out/
# → out/MandelbrotBatch.v, out/mandelbrot_batch_layout.rs (the seam), out/build.sh, …
```

The seam is read at run time — the daemon takes it as `--layout` — so the
registers, the clock and the frame's shape travel with the bitstream they were
built for, and a stale one is refused by its layout hash.

**2. Bitstream**, when the RTL changed (~20–30 min, gated on timing). The
directory's own script runs the flow, packages the app and ends with the gate
that reads Vivado's reports back:

```sh
out/build.sh
```

(`Warp11.GoL.App -- build out/`, `Warp11.Gep -- build out/`
and `Warp11.Placement -- build my.json my.kv260.json out/` are the same shape.
The hand-written `hardware/vivado/*.tcl` scripts the first accelerators were
built with are retired as each regenerated bitstream is run bit-exact.)

A good free window for the differential oracle — four minutes of Verilator
against twenty of Vivado costs nothing but contention, and a failure arrives in
time to kill the build.

**3. Board binaries** — static musl, nothing to install on the board:

```sh
cd runtime
cargo build --release --target aarch64-unknown-linux-musl -p mandel-daemon
scp target/aarch64-unknown-linux-musl/release/mandel-daemon \
    ../hdl/out/mandelbrot_batch_layout.rs ubuntu@<board-ip>:warp11-driver/
```

**4. Load and run** — the app first (copied over as `build.sh` prints), then
the bring-up check, then the daemon:

```sh
ssh ubuntu@<board-ip> 'sudo xmutil unloadapp && sudo xmutil loadapp mandelbrot-batch'
ssh ubuntu@<board-ip> 'cd warp11-driver && ./mandel-daemon --board mandelbrot_batch --layout mandelbrot_batch_layout.rs check'
ssh ubuntu@<board-ip> 'systemctl --user restart mandel-daemon'
```

**5. The host view**, over Zenoh:

```sh
cd hdl && LD_LIBRARY_PATH=<zenohc>/lib \
  dotnet run -c Release --project Warp11.MandelView -- tcp/<board-ip>:7448
```

## First light before trusting anything

Every accelerator has a first-light run that drives it and compares against a
software twin, bit for bit — for the Mandelbrot, the daemon's `check`:

```sh
ssh ubuntu@<board-ip> 'cd warp11-driver && ./mandel-daemon --board mandelbrot_batch --layout mandelbrot_batch_layout.rs check'
```

Run it after any bitstream change. Sim green and timing green **still do not
imply a correct picture** — the first Mandelbrot render on silicon was sheared
by a host readback-stride bug that both had passed.

## Troubleshooting

### `Permission denied (publickey,password)` — server "accepts key" but denies session

Private key has a passphrase and your shell has no ssh-agent with it loaded.
Non-interactive callers forbid prompting, so the client gives up silently —
sshd logs `Connection closed [preauth]` with no "Failed publickey" line.

Fix: `ssh-add ~/.ssh/id_ed25519` once per login session, or `AddKeysToAgent yes`
in `~/.ssh/config`.

### `Permission denied` despite the key being in `authorized_keys`

sshd `StrictModes` rejects keys when `~`, `~/.ssh` or `~/.ssh/authorized_keys`
are too permissive; `~` group-writable is the usual culprit.
`chmod go-w ~ && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys`.

### `sudo: a password is required`

`/etc/sudoers.d/warp11` wasn't installed, or its mode/owner is wrong (must be
`440` root:root). Re-run step 2.

### `no uio node named '<app>'`

The bitstream isn't loaded, or a different app is. `xmutil listapps` shows the
active slot. A daemon that hits this exits non-zero rather than looping.

### Board hard-locks after running a driver

The AXI base doesn't match what the bitstream decoded. Reads to undecoded
addresses hang the smartconnect indefinitely — whole-board freeze, recoverable
only by power-cycle. **Every Warp 11 slave app is at `0xB0000000`**, the only
sub-4GB aperture HPM1_FPD exposes on the KV260. The generated block design
takes it from the board's `AxiLiteAt`, so a mismatch means a bitstream built
from a different board file than the driver's seam.

### Frames arrive rotated, but every register reads correctly

Don't debug the RTL. Tearing down a bitstream while a fabric AXI master had
writes in flight leaves a **permanent** PS-side HP0 AW/W pairing skew that
survives every app reload. **Reboot the board.** Then always stop the daemon
before `xmutil unloadapp`.
