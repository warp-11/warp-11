# runtime — the board runtime and host drivers

The half of Warp 11 that **ships**. Elaboration runs on your machine and its
output is Verilog, so the F# side never leaves your desk; this code runs on the
board — or on a workstation driving one — which is why it is Rust. Board reach
decided that outright, and it is the one place the choice was not close.

```sh
cd runtime && cargo test --workspace
```

## The crates

| crate | what | `std`? |
|---|---|---|
| **`core`** (`warp11-runtime`) | `RegisterWindow`, and the drivers written against it — the batch contract (`BatchDevice`), regions, the counted frame, the GEP cluster — with `gep_seam.rs`, the cluster's names as a type | **`no_std`, zero dependencies** |
| **`host`** (`warp11-host`) | `Connection` and the backends under it — `MmapWindow` (uio / `/dev/mem`), `SerialWindow`, `FsSimWindow`, `Warp11Dma`, udmabuf, the seam reader — the software twins, and the tools under `src/bin/` (`warp11_batch`, `warp11_regs`, `warp11_snapshot`, `warp11_record`, `warp11_region_copy`, `gep_cluster_evolve`, `gep_cluster_first_light`) | std |
| **`gol-daemon`**, **`mandel-daemon`** | Zenoh peers serving the board views | std |
| **`gol-engine`** | Game of Life's software twin, on the same wire as the daemon | std |

**A driver is written once against `RegisterWindow` and runs in both worlds.**
`MmapWindow` is the board; `FsSimWindow` drives the *F# simulator* over a
bridge, so the same code that will map a uio node is exercised against the
elaborated design before any silicon exists. That is the two-backends property,
across the language seam — and it is why `batch_bridge` and `region_bridge`
(`host/tests/`) are the tests that matter most here.

**The register maps are generated, not written, and a host reads the one
its build wrote.** Every build directory carries a seam (`*_layout.rs`),
emitted by the same F# definition that elaborates the slave: the registers,
the regions, and how the build is reached — `HOST_BAUD` over a serial line,
`HOST_APP` through a uio node. `Connection::board(<build-dir>, port)`
(`host/src/connection.rs`) reads it and hands back a register window and
the design's regions by name (`host/src/regions.rs`), so a host is the same
code on a board with the host's memory behind it, on a board that keeps its
regions over the link, and in the Sim (`Connection::sim`). What stays
compiled in is the *names*: `core/src/gep_seam.rs` is the names every build
of the cluster states, as a type
(`dotnet run --project Warp11.Gep -- hardware <repo-root>`, committed), so a
driver's spelling is the compiler's to check and the numbers are the
build's. Host and fabric cannot disagree about an offset.

## Cross-compiling for the board

No cross-gcc needed; `.cargo/config.toml` carries the two settings that make it
a plain command.

```sh
cargo build --release --target aarch64-unknown-linux-musl -p mandel-daemon
```

`rust-lld` as the linker, because the host `cc` cannot link aarch64 objects,
and `musl-stub/libdl.a` — an empty archive — because zenoh reaches `libloading`
which links `-ldl`, a library musl folds into libc and ships no archive for.
Don't delete the 8-byte file; the config comment explains it.

## First light

Each accelerator has a first-light run that drives it on the board and
compares against a software twin, bit for bit: `mandel-daemon … check` for
the Mandelbrot, `gol-daemon`'s skew canary at start (and `gol-daemon …
disarm` before an unload) for Game of Life, and `gep_cluster_first_light`
under `host/src/bin/` for the GEP cluster. The rows-through designs are
checked with `warp11_batch`, byte for byte against the same run with
`--sim`.

Run one after any bitstream change. Sim green and timing green still do not
imply a correct picture.

## Elsewhere

- [`docs/dev-workflow.md`](../docs/dev-workflow.md) — the deploy loop.
- [`hardware/board/README.md`](../hardware/board/README.md) — board setup and
  the daemon units.

