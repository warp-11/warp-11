# GEP

Gene Expression Programming — a genetic-programming engine — with the whole
generation loop in fabric. Population in DDR, evaluation and breeding on the
FPGA, **0.85 µs per offspring**, bit-exact against a software twin.

*Status: on silicon. The largest example here by a wide margin.*

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

Emit the hardware and its register map:

```sh
dotnet run -c Release --project Warp11.Gep -- hardware <repo-root>
```

Then `build_gepclusterfs_axi.tcl`, the `gep-cluster-fs` app, and
`gep_cluster_first_light` on the board.

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
