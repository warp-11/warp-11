# KV260 board setup for passwordless deploy

One-time setup so `scp`, `ssh` and `xmutil` work without prompts. The
host-side loop that uses it — emit, bitstream, cross-build, load, run — is
[`docs/dev-workflow.md`](../../docs/dev-workflow.md); this file is the board
side and the per-app daemons.

> **Gradle is gone** (2026-08-13, with the Kotlin stack). Where an older
> revision of this file said `./gradlew :driver:…`, the replacement is a
> `cargo build --target aarch64-unknown-linux-musl` plus an `scp`, spelled out
> below.

## 1. SSH key auth

```sh
ssh-copy-id ubuntu@192.168.1.172
```

Type the board password once. Verify:

```sh
ssh ubuntu@192.168.1.172 'echo ok'   # should print "ok" with no prompt
```

## 2. Passwordless sudo for xmutil + drivers

```sh
scp hardware/board/warp11-sudoers ubuntu@192.168.1.172:/tmp/warp11-sudoers
ssh ubuntu@192.168.1.172 \
  'sudo install -m 440 -o root -g root /tmp/warp11-sudoers /etc/sudoers.d/warp11 && sudo visudo -c'
```

`visudo -c` must report `parsed OK` before you log out — a broken sudoers
file locks you out of sudo entirely.

Verify:

```sh
ssh ubuntu@192.168.1.172 'sudo -n /usr/bin/xmutil listapps | head -3'
```

## 3. Use it

Board binaries are static musl, so deploying one is a build and a copy — there
is nothing to install on the board:

```sh
cd runtime
cargo build --release --target aarch64-unknown-linux-musl -p mandel-daemon
scp target/aarch64-unknown-linux-musl/release/mandel-daemon ubuntu@192.168.1.172:warp11-driver/
ssh ubuntu@192.168.1.172 'cd warp11-driver && ./mandel_frame_first_light'
```

A bitstream is the same shape, via the packager, which prints this recipe with
the paths filled in when it finishes:

```sh
cd hardware/xmutil && ./package_gol-fs.sh
scp <out-dir>/* ubuntu@192.168.1.172:/tmp/gol-fs-app/
ssh ubuntu@192.168.1.172 'sudo mkdir -p /lib/firmware/xilinx/gol-fs &&
                          sudo cp /tmp/gol-fs-app/* /lib/firmware/xilinx/gol-fs/ &&
                          sudo xmutil unloadapp && sudo xmutil loadapp gol-fs'
```

## 4. gol-daemon (GoL control plane)

`runtime/gol-daemon` — a Zenoh peer serving `warp11/gol/frame` and the
`warp11/gol/ctl/*` key space to `hdl/Warp11.GolView`. **Replaced the JVM
`gol-server` (Ktor WS on 8080) on 2026-08-13**, with the rest of the Kotlin
stack.

```sh
# One-time UIO udev rule (applies to all UIO nodes — do this once per board):
echo 'SUBSYSTEM=="uio", OWNER="ubuntu", GROUP="ubuntu", MODE="0660"' | \
    ssh ubuntu@192.168.1.172 'sudo tee /etc/udev/rules.d/99-uio.rules'
ssh ubuntu@192.168.1.172 'sudo udevadm control --reload-rules && sudo udevadm trigger'

cd runtime
cargo build --release --target aarch64-unknown-linux-musl -p gol-daemon
scp target/aarch64-unknown-linux-musl/release/gol-daemon ubuntu@192.168.1.172:warp11-driver/
scp ../hardware/board/gol-daemon.service ubuntu@192.168.1.172:.config/systemd/user/
ssh ubuntu@192.168.1.172 'systemctl --user daemon-reload && systemctl --user restart gol-daemon'
```

Host UI:

```sh
cd hdl && LD_LIBRARY_PATH=<zenohc>/lib \
  dotnet run -c Release --project Warp11.GolView.Desktop -- tcp/192.168.1.172:7447
```

Managing it from the host:

```sh
ssh ubuntu@192.168.1.172 'systemctl --user status gol-daemon.service'
ssh ubuntu@192.168.1.172 'journalctl --user -u gol-daemon -f'
ssh ubuntu@192.168.1.172 'systemctl --user stop|start|restart gol-daemon.service'
```

The listen endpoint is the unit's one argument (`tcp/0.0.0.0:7447`), matching
the binary's own default. **7447, not 7448** — mandel-daemon has that one, and
keeping them apart means the units never fight over a port even though only one
of their bitstreams can be loaded at a time.

Prereq: the `gol-fs` bitstream must be loaded (`sudo xmutil loadapp gol-fs`) and
`udmabuf.service` must have run, otherwise the uio probe fails. Like
mandel-daemon it exits non-zero with the reason rather than crash-looping, and
`StartLimitBurst=3` stops systemd retrying after three tries in a minute.

It also round-trips a known soup through the whole write path at startup and
refuses to serve if the frame comes back rotated, so the journal says whether
the PS-side pairing is intact before any client connects:

```
skew canary ok: known soup round-tripped bit-exact through DDR
```

**Unloading is a protocol, not a command**, and systemd cannot express it:
stopping the unit does *not* disarm the fabric's write master.

```sh
ssh ubuntu@192.168.1.172 'systemctl --user stop gol-daemon'
ssh ubuntu@192.168.1.172 'cd warp11-driver && ./gol_disarm'
ssh ubuntu@192.168.1.172 'sudo xmutil unloadapp'
```

Tearing down the PL with beats in flight leaves a permanent PS-side HP0 AW/W
pairing skew that survives every app reload and clears only on reboot — the
Game of Life write-up tells the story. The
unit deliberately has no `ExecStopPost=` disarm: it would fire on the restart
path too, and disarming a master the next start is about to re-arm is a race
worth not having. The canary is the backstop.

## 5. mandel-daemon (Mandelbrot accelerator control plane)

`runtime/mandel-daemon` — a Zenoh peer serving `warp11/mandel/frame`
and `warp11/mandel/ctl/render` to `hdl/Warp11.MandelView`. **Replaced the
JVM `mandel-server` (Ktor WS on 8081) on 2026-08-13**, with the rest of
the Kotlin stack; the unit is `hardware/board/mandel-daemon.service` and
there is no Gradle task, because there is no Gradle.

```sh
# UIO udev rule from §4 is sufficient (applies to all UIO nodes).

cd runtime
cargo build --release --target aarch64-unknown-linux-musl -p mandel-daemon
scp target/aarch64-unknown-linux-musl/release/mandel-daemon ubuntu@192.168.1.172:warp11-driver/
scp ../hardware/board/mandel-daemon.service ubuntu@192.168.1.172:.config/systemd/user/
ssh ubuntu@192.168.1.172 'systemctl --user daemon-reload && systemctl --user restart mandel-daemon'
```

No cross-gcc is needed — `runtime/.cargo/config.toml` carries the two
settings (`rust-lld`, and an empty `libdl.a` for zenoh's `-ldl`) that make
the musl target a plain cargo command.

Host UI:

```sh
cd hdl && LD_LIBRARY_PATH=<zenohc>/lib \
  dotnet run -c Release --project Warp11.MandelView -- tcp/192.168.1.172:7448
```

Managing it is the same shape as gol-daemon (§4):

```sh
ssh ubuntu@192.168.1.172 'systemctl --user status mandel-daemon.service'
ssh ubuntu@192.168.1.172 'journalctl --user -u mandel-daemon -f'
ssh ubuntu@192.168.1.172 'systemctl --user stop|start|restart mandel-daemon.service'
```

The listen endpoint is the unit's one argument (`tcp/0.0.0.0:7448`) rather
than an env knob. **7448, not 7447** — gol-daemon has that one, and keeping
them apart means the units never fight over a port even though only one of
their bitstreams can be loaded at a time.

Prereq: the `mandel-frame` app must be loaded first (`sudo xmutil loadapp
mandel-frame`), and `udmabuf.service` must have run. **Unlike the JVM units
this one does not crash-loop forever** when they haven't: it exits non-zero
with the reason, and `StartLimitBurst=3` stops systemd retrying after three
tries in a minute. That is a direct answer to the failure §6 documents — an
enabled server whose bitstream is missing burning a full core after a reboot
and silently poisoning benchmarks.

It also renders once at startup and refuses to serve if `frameDone` never
rises, so the journal says whether the fabric is clocked and reaching DDR
before any client connects:

```
render: 715938 cycles = 4.296 ms fabric (7.164 ms wall)
startup render ok: the fabric is clocked and reaching DDR
mandel-daemon up: fb 0x36100000, listening on tcp/0.0.0.0:7448
```

**The framebuffer is udmabuf0, not a `memmap=` reservation.** The
Kotlin-era instruction to append `memmap=4M$0x70000000` to
`/boot/firmware/cmdline.txt` is obsolete and was already known not to work
(see the udmabuf notes in §3): the frame accelerator writes to udmabuf0's
physical address, which the daemon reads from `/sys/class/u-dma-buf/
udmabuf0/phys_addr` and programs into `fbBaseAddr` on every render. Nothing
needs reserving by hand.

Unloading is lighter than GoL's protocol — this accelerator is
demand-driven, so an idle daemon has nothing in flight and there is no
disarm step:

```sh
ssh ubuntu@192.168.1.172 'systemctl --user stop mandel-daemon'
ssh ubuntu@192.168.1.172 'sudo xmutil unloadapp'
```

## 6. Switching between apps (gol ↔ mandel)

Both apps live at the same AXI base (`0xB000_0000` — the only sub-4GB
aperture on the KV260's HPM1_FPD master) and share IRQ SPI 89. Only
**one** xmutil app can be loaded at a time; when you switch, the previous
app's UIO node disappears and any server holding that handle will
crash-loop trying to reopen it (`Restart=on-failure` keeps trying forever).

Procedure to switch from gol → mandel:

```sh
# 1. Stop the outgoing daemon — and for gol, disarm before the unload (§4).
ssh ubuntu@192.168.1.172 'systemctl --user stop gol-daemon'
ssh ubuntu@192.168.1.172 'cd warp11-driver && ./gol_disarm'

# 2. Swap the bitstream (packaged from this repo; see §3 for the full recipe).
ssh ubuntu@192.168.1.172 'sudo xmutil unloadapp && sudo xmutil loadapp mandel-frame'

# 3. Start the incoming daemon.
ssh ubuntu@192.168.1.172 'systemctl --user start mandel-daemon.service'
```

**Convention (2026-07-22): all app server services stay `disabled`;
enable only the one you are actively working on.** An `enabled` server
whose bitstream isn't loaded crash-loops at ~1 full core after any
reboot (its UIO node doesn't exist), silently poisoning benchmarks —
a `Restart=on-failure` JVM relaunch storm looks like a generic system
slowdown, not an error. Enable/disable per app:

```sh
ssh ubuntu@192.168.1.172 'systemctl --user enable --now mandel-daemon.service'   # working on mandel
ssh ubuntu@192.168.1.172 'systemctl --user disable --now mandel-daemon.service'  # done
```

(The JVM units this convention was written for — `audio-server`, `gol-server`,
`mandel-server`, `wdrc-server` — are all gone as of 2026-08-13. The Rust
daemons need it less: both set `StartLimitBurst=3`, so they give up with the
reason in the journal rather than storming.)

Mirror the steps to switch back. If you forget to stop the outgoing daemon
first, `journalctl --user -u <svc>` will be full of "no UIO node with
name='<old>'" — recover by stopping the wrong one and starting the right one.

**Sudoers**: `hardware/board/warp11-sudoers` grants the firmware-directory
operations by **wildcard** (`/lib/firmware/xilinx/*/*`), so a new app needs no
new entry. It used to name apps one per line, and every name in it was
Kotlin-era — which is how stale bitstreams accumulated: new apps could be
*installed* through the unrestricted `install` rule and never cleaned. One of
them (`audio-batch`) was then loaded in preference to its F# replacement, twice,
before the driver's aperture scan caught it.

**Validate it locally before shipping it**: `visudo -cf hardware/board/warp11-sudoers`.
The board-side `visudo -c` in the install line runs *after* the file is in
place, and a malformed sudoers file locks you out of sudo entirely. The current file has entries
for `gol` and `mandel`; adding a third app means editing the sudoers
file + re-installing it on the board (see header of that file for the
install command).

**No need to stop services when you're done for the day** — they idle
at near-zero CPU and ~150 MB resident memory each on the KV260's 4 GB
RAM. Stop them only when (a) switching apps, or (b) you want a clean
journal log for the next session.

## 7. Auto-load an app at boot (skip the manual xmutil swap)

`xmutil` has no native "set default PL app" knob — the boot firmware loads
the Kria stock default, so after every reboot you'd `xmutil unloadapp` +
`xmutil loadapp <app>` by hand. `hardware/board/warp11-loadapp.service` is a
**system** (root) oneshot that does that swap automatically once `dfx-mgrd`
is up.

```sh
# Pick the app: edit WARP11_APP in the unit (default: mandel).
scp hardware/board/warp11-loadapp.service ubuntu@192.168.1.172:/tmp/
ssh ubuntu@192.168.1.172 \
  'sudo install -m 644 -o root -g root /tmp/warp11-loadapp.service \
     /etc/systemd/system/warp11-loadapp.service \
   && sudo systemctl daemon-reload \
   && sudo systemctl enable --now warp11-loadapp.service'

# Verify the app loaded:
ssh ubuntu@192.168.1.172 'xmutil listapps'
```

To change the default app later, edit `WARP11_APP` (re-scp), then
`sudo systemctl daemon-reload && sudo systemctl restart warp11-loadapp.service`.
To go back to no auto-load: `sudo systemctl disable warp11-loadapp.service`.

This is a system unit (runs as root), so it doesn't need the `ubuntu`
sudoers entries. A daemon's `--user` unit is ordered independently; since
`mandel-daemon` retries the UIO open a bounded number of times, boot ordering
between them does not matter in practice as long as this oneshot wins within
that window.

## 8. Kernel modules: u-dma-buf + warp11-dma (both DKMS-managed)

Two out-of-tree modules serve the DMA paths, and since 2026-07-24 both are
**DKMS-managed** so kernel updates rebuild them automatically (the
2026-07-24 reboot onto 5.15.0-1075 killed the old insmod-by-path
arrangement — a vermagic-stale .ko — which is exactly what DKMS prevents):

- **u-dma-buf** (ikwzm, source at `~/udmabuf-master/`): the contiguous-DDR
  arena the fabric reads/writes (`/dev/udmabuf0` 4 MiB WC, `/dev/udmabuf1`,
  plus per-app dt-overlay buffers like `udmabuf-gep-cluster`). Registered
  as `u-dma-buf/5.5.0`; its dkms.conf (committed at
  `hardware/board/u-dma-buf.dkms.conf`) MUST drive the wrapper's own `all`
  target — a generic kbuild MAKE line builds nothing because of the
  `obj-$(CONFIG_U_DMA_BUF)` guard. `hardware/board/udmabuf.service` loads
  it via **modprobe** (not insmod-by-path) at boot.
- **warp11-dma** (`hardware/board/warp11-dma/`): fast bulk READS of
  fabric-written DDR — a PS GDMA memcpy channel copies into cached,
  kernel-maintained buffers (12–16× over WC reads at 64 KB–1 MB, ~20 µs
  fixed/copy; measured table + install steps in its README). Loaded at
  boot via `/etc/modules-load.d/warp11-dma.conf`.

DKMS caveats learned the hard way: this image has **no boot-time
`dkms.service`** — modules rebuild via the kernel package's postinst hook,
so a kernel that was installed *before* `dkms add` needs a one-time
`sudo dkms install <mod>/<ver>` after booting it. Headers
(`linux-headers-<ver>`) must be present for the hook to build.

### GEP host-marshal / read-path bench (gates 1–2, no bitstream) — HISTORICAL

> **Kotlin-era, and currently unrunnable.** `gep-marshal-bench.sh` drives a
> `gep-hw` binary that went with the Kotlin stack on 2026-08-13, and the
> `:examples:gep:*` tasks went with Gradle. The script is kept for the gate
> definitions and the measured numbers; the Rust equivalent of its read-path
> half is `gep_cluster_first_light`. Rewriting it against the Rust host is on
> the backlog, not done.

`hardware/board/gep-marshal-bench.sh` measures the two host-side gates of
the GEP streaming redesign (`examples/gep/gep_plan.md` Track 2 item 4):
the **warp11-dma read path** (gate 1) via `gep-hw --unit --memprobe`, and
the **host marshal** (gate 2) via `gep-hw --marshal` swept over thread
counts and the publish A/B knobs (batch vs `--stream` tail-chasing;
write-combine vs `--dma` staged burst). It needs **no FPGA app** — the
marshal is pure host memory + DMA over a scratch udmabuf (`udmabuf0` by
default, always present).

It ran directly on the board, against a deployed `gep-hw`:

```sh
bash ~/warp11-gep/gep-marshal-bench.sh --pops "1024 4096" --reps 50
```

Wait for load < ~1 first (§9). Gate 2 passes if total read + gather +
publish stays well under ~1 ms/gen at pop 1024 — the fabric breed pass it
hides behind.

## 9. After any reboot

1. The default app auto-loads — `sudo xmutil unloadapp` before
   `sudo xmutil loadapp <app>` (a bare loadapp fails with `load Error: -1`).
2. Both kernel modules load automatically (udmabuf.service +
   modules-load.d); per-app udmabuf DT buffers appear when the app loads.
3. Post-boot load storm (gnome-shell/snapd) inflates benches for ~2-3
   minutes — wait for load < ~1 before measuring.
