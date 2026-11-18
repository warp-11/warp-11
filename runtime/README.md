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
| **`core`** (`warp11-runtime`) | `RegisterWindow`, the generated register maps, and the device drivers written against them | **`no_std`, zero dependencies** |
| **`host`** | the backends — `MmapWindow` (uio / `/dev/mem`), `Warp11Dma`, udmabuf, `FsSimWindow`, the software twins, and the first-light binaries | std |
| **`gol-daemon`**, **`mandel-daemon`** | Zenoh peers serving the board views | std |
| **`gol-engine`** | Game of Life's software twin, on the same wire as the daemon | std |

**A driver is written once against `RegisterWindow` and runs in both worlds.**
`MmapWindow` is the board; `FsSimWindow` drives the *F# simulator* over a
bridge, so the same code that will mmap `/dev/mem` is exercised against the
elaborated design before any silicon exists. That is the two-backends property,
across the language seam — and it is why `mandel_frame_bridge` and
`mandel_fs_bridge` are the tests that matter most here.

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
the Mandelbrot, and under `host/src/bin/` `gol_first_light` and
`gep_cluster_first_light`, plus `gol_disarm` and `gol_debug`.

Run one after any bitstream change. Sim green and timing green still do not
imply a correct picture.

## Elsewhere

- [`docs/dev-workflow.md`](../docs/dev-workflow.md) — the deploy loop.
- [`hardware/board/README.md`](../hardware/board/README.md) — board setup and
  the daemon units.

