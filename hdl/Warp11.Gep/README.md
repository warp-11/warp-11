# GEP

> **Work in progress.** 

Gene Expression Programming — a genetic-programming engine — with the whole
generation loop in fabric. Population in DDR, evaluation and breeding on the
FPGA, **0.85 µs per offspring**, bit-exact against a software twin.

*Status: on silicon, still under active development. The largest example here by a wide margin.*

## What it does

GEP evolves programs. A chromosome is a linear string of symbols that decodes
into an expression tree; a population of them is scored against a dataset, the
best are selected, and the next generation is bred by mutation and
recombination. Repeat until something fits.

Every part of that loop is on the FPGA: decode, evaluate, score, select, breed.
The host owns the population in DDR and streams work in; the fabric never
random-accesses memory it does not own.

## The shape of it

```
GepClusterPool           the generation loop
  ├── GepBreederBlock × 4      selection and recombination
  ├── GepUnitEngine × 8        one chromosome at a time, 16 threads deep
  │     └── GepAluPipelined    the operator set
  │           └── warpFu       the shared divider socket
  └── GepRecordRouter          results back out to DDR
```

Two ideas do most of the work:

**Barrel threading, again.** A `GepUnitEngine` interleaves 16 chromosomes
through one pipeline. Expression evaluation is branchy and irregular — exactly
the workload that wastes a fixed pipeline — so the engine advances a different
thread each cycle and the arithmetic stays busy.

**A shared function unit for the expensive operator.** Division costs ~697 LUTs
and is used rarely. Giving every engine its own divider is most of the area
budget spent on an operator that is mostly idle, so instead the engines share a
pod of dividers through a request socket (`warpFu`). The engine issuing a divide
parks that thread and picks up another.

The cluster is LUT-bound, not DSP-bound — the opposite of Mandelbrot — and the
4×8×2 configuration that ships uses 34.5% of the board's LUTs.

## Running it

The engine has a complete software twin, which is the point: everything the
fabric does can be checked against it exactly.

```sh
cd hdl
dotnet run -c Release --project Warp11.Gep                    # 20+ living checks
dotnet run -c Release --project Warp11.Gep -- problems        # symbolic regression
dotnet run -c Release --project Warp11.Gep -- diff <dir>      # differential testbenches
```

A build directory for each board — the KV260's block design, overlay and
packaging, the Icepi's open flow, the seam either way — and the names the
Rust driver is compiled against:

```sh
dotnet run -c Release --project Warp11.Gep -- build out/      # the KV260, 8x4x2; then out/build.sh; the app is gep-cluster-axi
dotnet run -c Release --project Warp11.Gep -- icepi out/      # the Icepi, 1x1, its regions over the link
dotnet run -c Release --project Warp11.Gep -- hardware <repo-root>   # runtime/core/src/gep_seam.rs
```

Then `gep_cluster_first_light <vector-dir> --board <build-dir>` for one
queue-mode pass against `-- boardvector <dir>`, and the whole streaming
loop — emit, marshal, breed, generation after generation — against the
mirror's answer. The host is the same on every board: it is handed the
build directory (or the seam file out of it), which says how the build is
reached and where its regions are.

```sh
dotnet run -c Release --project Warp11.Gep -- streamvector vec/ 6          # the oracle, six generations, the silicon shape
dotnet run -c Release --project Warp11.Gep -- streamvector vec/ 6 icepi    # ...for the Icepi's shape
(cd ../runtime && cargo run --bin gep_cluster_evolve -- ../hdl/vec --sim)  # the same loop against the Sim, through the bridge
./gep_cluster_evolve vec/ --board gep_cluster_axi_layout.rs                # on the KV260
gep_cluster_evolve vec/ --board out/ --port /dev/ttyUSB0                   # on the Icepi, from the PC
```

`gep_cluster_evolve` prints, per generation, how many op-list entries,
genomes and fitnesses matched the mirror and what each pass cost; a pass
is every one of them, every generation.

## Files

- `Karva.fs`, `Chromosome.fs`, `Operators.fs`, `Engine.fs` — the software twin
- `GepAlu.fs`, `GepUnitEngine.fs`, `GepBreederBlock.fs`, `GepClusterPool.fs` —
  the fabric
- `ClusterAxi.fs` — the slave and the generated register map
- `Problems.fs`, `Srbench.fs` — the benchmark set

## Numbers

- 4 breeders × 8 engines × 2, 512 of 512 offspring bit-exact
- **0.85 µs per offspring**
- 34.5% LUT utilization
- division solved exactly in fabric, not approximated

## Known gaps

- **ADFs raise the ceiling but not the floor.** Automatically defined functions
  are implemented and made harder problems reachable, but the Feynman I.13.4
  starter problem is still unsolved at 0/40 seeds with 3, 4 or 6 ADFs. Both
  engines call it a search-power gap rather than a budget one. The next lever
  is the homeotic alphabet.
- Scaling past 4×8×2 does not fit: 6×12×2 models at 131% of the board.
