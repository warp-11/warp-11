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
GepClusterAxi            the generated board top: controls, reports, regions
  └── gepClusterPool       the generation loop
        ├── GepBreederBlock × 8      selection and recombination
        ├── GepUnitEngine × 4        one chromosome at a time, 16 threads deep
        │     └── GepAluPipelined    the operator set, the divider per lane
        │                            or pooled behind `warpFu`
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
parks that thread and picks up another. Which one is the shape's choice
(`FuSharing`: `PerLane` or `Pooled`); the silicon shape keeps a divider per
lane, because the breeders, not the dividers, bind the LUTs, and pooling
cost 44–118% throughput.

The cluster is LUT-bound, not DSP-bound — the opposite of Mandelbrot. It is
a `Design` on regions (`ClusterAxi.fs`, `notes/DEVICES.md` §13): its needs
are the host's controls, what it reports, and four regions — the queue, the
population, the ring and the op-list — and the board says where they live,
so the top, the register map and the seam all come from one definition.
The 8×4×2 shape that ships (the re-swept mix) uses 43.7% of the KV260's
LUTs; the 4×8×2 before it, 34.5%.

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

- `Fixed.fs`, `Rng.fs`, `Opcodes.fs`, `Karva.fs`, `Chromosome.fs`,
  `MicroProgram.fs`, `HwBreeding.fs`, `Fitness.fs`, `Engine.fs` — the
  software twin
- `Hdl.fs` — the fabric: the ALU, the unit engine, the breeder block, the
  record router
- `Cluster.fs` — the cluster pool, its shapes and its regions
- `ClusterAxi.fs` — the cluster as a design, on the KV260 and the Icepi
- `Problems.fs`, `Srbench.fs` — the benchmark set
- `Golden.fs`, `golden/` — the golden vectors from the original engine

## Numbers

- 4 breeders × 8 engines × 2, 512 of 512 offspring bit-exact
- **0.85 µs per offspring**
- 34.5% LUT utilization
- division solved exactly in fabric, not approximated

| board | shape | measured |
|---|---|---|
| KV260 | 8 × 4 × 2 | first light 64/64 and the streaming loop 3 generations bit-exact, 43.7% LUT, WNS +0.623 |
| Icepi Zero | 1 × 1 | regions over the link: the streaming loop's score round and 3 generations bit-exact against the mirror, 16/16 each, 7.1 ms an offspring at 1 Mbaud (23 434 / 24 288 cells, 18 / 28 multipliers, 38.8 MHz) |
| Go Board | — | refused: one breeder exceeds the part |

## Known gaps

- **ADFs raise the ceiling but not the floor.** Automatically defined functions
  are implemented and made harder problems reachable, but the Feynman I.13.4
  starter problem is still unsolved at 0/40 seeds with 3, 4 or 6 ADFs. Both
  engines call it a search-power gap rather than a budget one. The next lever
  is the homeotic alphabet.
- Scaling the old 4×8×2 up does not fit: 6×12×2 models at 131% of the board.
