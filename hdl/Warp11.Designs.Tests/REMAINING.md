# Remaining Test Migration

Current state: `Warp11.Designs.Tests` has 69 passing Expecto tests. Run from `hdl/`:

```sh
dotnet run --project Warp11.Designs.Tests
```

Keep artifact generation (`diff`, `firrtl-foreign`, `firrtl-sim`, WAV conversion), performance reports, the differential oracle, and hardware checks outside Expecto.

## Recommended Order

### 1. Debugger And Assertions

- `inventoryNamesPeek` (`Main.fs:1223`)
- `inventoryGroups` (`Main.fs:1294`)
- `breakpointExpressions` (`Main.fs:1318`)
- `debugSessionDrives` (`Main.fs:1422`)
- `debugSessionShowsMemory` (`Main.fs:1465`)
- `tracesEveryCycle` (`Main.fs:1520`)
- `assertionsHold` (`Main.fs:1576`)
- `debugSessionDrivesDevices` (`Main.fs:791`)

### 2. Utility Primitives

- Split `utilityPrimitives` (`Main.fs:1637`) into focused tests:
  - maximal-period LFSR plus hardware/software sequence
  - one-hot priority and one-hot mux
  - edge detection
  - flow sampling/drop accounting
  - fixed and runtime counters/dividers
  - bit-shape operations
- `dividerDivides` (`Main.fs:3276`): arithmetic, busy refusal, held result, divide-by-zero
- Saturate/shift value semantics (`Main.fs:4813`)
- Transporter round trip (`Main.fs:4981`)

The 24-bit maximal-period LFSR walk is about 16.7 million software iterations; consider marking it as a slow test or testing smaller widths by default.

### 3. Elaboration Guards

Complete the paired positive/negative cases from `elaborationGate` (`Main.fs:1991`):

- combinational loop rejected / registered feedback accepted
- undriven output rejected / conditional override accepted
- ROM write rejected / RAM write accepted
- combinational UltraRAM read rejected
- own input and child output drive rejected
- undriven child input rejected / fully wired child accepted
- stream ready drive accepted
- body-depth port declaration rejected

Also migrate the local rejection checks at `Main.fs:4933-5095` not already covered by `DslTests.fs`: reserved words, computed signed multiply operands, mixed signedness, shift/extension ranges, mismatched compare widths, invalid renormalization, wrong Number boundary width, and out-of-range constants.

### 4. Bus And Memory Integration

- `oneKernelThreeStorages` (`Main.fs:3725`)
- `busAndWindowLevels` (`Main.fs:3883`)
- `doneMeansLanded` (`Main.fs:4039`)
- `pipelinedChannelPipelines` (`Main.fs:4123`)
- `busyFlagHoldsArOff` (`Main.fs:4206`)

These are larger integration tests. Preserve AXI timing, read-first ordering, completion semantics, and structural assertions separately.

### 5. AXI And Snapshot Integration

- AXI read rehearsal: ring, single, and burst paths over four pacing patterns (`Main.fs:4838`)
- AXI write master versus DDR model (`Main.fs:5167`)
- Neighborhood edge policies (`Main.fs:5192`)
- Snapshot/conflate to DDR (`Main.fs:5493`)

Replace all cycle-limit fall-through with explicit timeout failures containing progress counts.

### 6. Register Map Serial And Preload

- UART register map including bad checksum refusal (`Main.fs:5365`)
- Preloaded writable window and reset reload (`Main.fs:5410`)

These are slower bit-level UART tests. Keep them separate from the fast AXI register-map suite.

### 7. Audio, WAV, And I2S

Move the top-level checks currently defined before `inventoryNamesPeek`, including:

- audio unity, FIR response, compressor, multiband reconstruction, and RBJ cookbook checks
- WAV multiband, extensible-header, stream-port, and I2S-pin checks
- I2S receive, transmit, loopback, divisor selection, edge timing, and hand-wiring equivalence
- `SimI2s` round trip and stream laziness
- delay buffer, echo, volume, `selectFirst`, and `i2sThrough`

Separate pure DSP tests from file-I/O and bit-level integration tests. Temporary files must be deleted in `finally` blocks.

## Cleanup After Migration

- Remove each migrated private check and its `%b` print from `Warp11.Designs/Main.fs`; do not leave dead helpers under FS1182.
- Reduce `mainDemo` to demonstration/reporting only. It must not be treated as a correctness gate.
- Preserve `diffDesignsAtDefault`, `diffDesigns`, and command handlers used by `run_differential.sh`.
- Keep production fixtures in `MemoryDesigns.fs`, `Designs.fs`, and `BusDesigns.fs`; move only test-local fixtures into the test project.
- After each batch run:

```sh
cd hdl
dotnet run --project Warp11.Designs.Tests
dotnet build Warp11.sln
```

The solution currently builds with pre-existing duplicate/downgraded `FSharp.Core` warnings from `Warp11.Placement.Canvas`.
