# AGENTS.md

Guidance for OpenCode when working in this repository.

## Project shape

Two language boundaries, named for what they are:

| dir | language | what |
|---|---|---|
| `hdl/` | F# (.NET 10) | DSL, elaborator, simulator, stdlib, debugger, designs |
| `runtime/` | Rust | board runtime, host drivers, generated register maps |
| `hardware/` | TCL/config | Vivado scripts, xmutil packaging, emitted `.v` (build output) |

## Build / test / run

**.NET 10 SDK is at `~/.dotnet` and NOT on `PATH`.** Every command needs this first:

```sh
export PATH="$HOME/.dotnet:$PATH"
```

### F# side (`hdl/`)

```sh
dotnet build Warp11.sln                             # build everything
dotnet run --project Warp11.Designs                 # living checks
dotnet run --project Warp11.Gep                     # GEP suite (22+ checks)
dotnet run --project Warp11.Tutorial.App            # step-through debugger (teaching set)
dotnet run --project Warp11.Designs.App             # debugger on every oracle design
dotnet fsi census.fsx                               # duplication ledger (~1.4 s)
```

### Rust side (`runtime/`)

```sh
cargo test --workspace
cargo build --release --target aarch64-unknown-linux-musl -p mandel-daemon
```

No cross-gcc needed — `rust-lld` + `musl-stub/libdl.a` handle it.

### Emit for silicon

```sh
dotnet run --project Warp11.Mandelbrot -- hardware <repo-root>
```
Writes `hardware/build/*.v` + committed Rust seam in `runtime/core/src/`.

## The differential oracle — CRITICAL

**Ask before every run, and say why.** Never start it unprompted. It costs ~11 min
(no firtool) or ~22 min (`FIRTOOL_LEG=1`) and verifies the toolchain, not the design.

```sh
cd hdl && ./run_differential.sh                    # Sim vs Verilator, every design
FIRTOOL_LEG=1 ./run_differential.sh                # + firtool third leg
```

Run only from `hdl/`. Running from the repo root silently exits 0.
It verifies when `Dsl.fs`, `Ir.fs`, `Verilog.fs`, `Flatten.fs`, `Firrtl.fs`,
or `Sim.fs` move. A change to a design file is NEVER a reason to run it.

### End-of-work gate

```sh
cd hdl && ./run_differential.sh     # ASK FIRST — see above
cd runtime && cargo test --workspace
```

## F# conventions an agent needs

- **Compile order in `Warp11.fsproj` matters.** `Stdlib.fs` is near the END — it sees everything above and nothing above can see it. `RegMap.fs` cannot call stdlib; the AXI-Lite channel they share lives in `AxiLite.fs` ahead of both.
- **One driver per signal per level.** A second `==>` is an elaboration error.
- **Use FSM's .** A second `==>` is an elaboration error.
- **One declaration per name.** Instance staging wires (`{instance}_{port}`) live in the parent's namespace.
- **`checkWidths` / `checkNames` / `checkStreams` all gate `emitDesign`.**
- **Call-site invariance.** Whether a stdlib entry is inline logic or a module is invisible at every use site.
- **A memory declares its storage:** `distributedMem` (LUTRAM, `memRead` ok), `blockMem` (BRAM, `memReadNextCycle` only), or bare `mem` (compile-time error).
- **Spell words out** unless the short form is common usage (accepted: `addr`, `reg`, `ptr`, `sel`, `buf`, `pc`, `cpu`, `esc`, `instr`/`pend`, `rd`/`wr` in AXI/mem contexts).

## Rust conventions

- `runtime/core/` is `no_std`, zero dependencies. `runtime/host/` is std.
- Generated register maps (`*_layout.rs`) are COMMITTED — emitted by the F# elaborate-then-emit step, so host and fabric cannot disagree.
- Drivers are written once against `RegisterWindow` and run against both `MmapWindow` (board) and `FsSimWindow` (simulator bridge).

## Documentation map (read the right one)

| to do this                | read this                                          |
|---------------------------|----------------------------------------------------|
| start, run F# side        | `hdl/README.md`                                    |
| build and run the website | `site/README.md`                                   |
| tutorial app              | `hdl/Warp11.Tutorial/README.md`                                   |
| living work list          | `notes/BACKLOG.md`                                 |
| stdlib feature decisions  | `docs/HDL_COMPARISON.md`                           |
| stream/wormhole API       | `notes/STREAM_API.md`                              |
| FIRRTL alignment          | `notes/FIRRTL_ALIGNMENT.md`                        |
| deploy loop               | `docs/dev-workflow.md`, `hardware/board/README.md` |
