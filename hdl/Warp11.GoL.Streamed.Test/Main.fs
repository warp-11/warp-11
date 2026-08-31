/// Tests for the designs in `Warp11.GoL.Streamed`.
///
/// Expecto tests are ordinary values, and this project is an ordinary console
/// app: `main` hands the list to the runner, so `dotnet run` runs the suite.
///
/// **What is deliberately not here**: that the design emits, that a module
/// instantiates once and is used many times, that the emitter deduplicates a
/// definition. Those are the DSL's behaviour, not this design's, and they are
/// covered by `Warp11.Designs` and the differential oracle over its sixty
/// entries. A test here that re-checked them would pass for reasons that have
/// nothing to do with Life.
module Warp11.GoL.Streamed.Test.Main

open Expecto
open Warp11
open Warp11.GoL.Streamed

/// `Modules.cell` is a value, not a function, so it elaborates once when this
/// module loads. That matters: elaboration writes into the DSL's ambient
/// builder and is not thread-safe, and Expecto runs tests in parallel — a
/// design re-elaborated inside each test races itself and trips the
/// one-declaration-per-name rule. A `Sim` owns its own state, so many Sims over
/// one `ModuleDef` are independent.
let private simOf () = Sim Modules.cell.def

/// B3/S23 stated independently of the design, in ordinary F#. A test that
/// re-derived the rule from the design would agree with it by construction and
/// prove nothing.
let private expected (self: int) (neighborhood: int) =
    let live = System.Numerics.BitOperations.PopCount(uint neighborhood)
    if live = 3 || (self = 1 && live = 2) then 1UL else 0UL

/// Like `cell`, the sweeps elaborate once at module load — see the note above.
let private sweep8 = (Modules.generationSweep 8 1 Modules.Bands).def
let private sweep8x2 = (Modules.generationSweep 8 2 Modules.Bands).def
let private sweep8x4 = (Modules.generationSweep 8 4 Modules.Bands).def
let private sweep64 = (Modules.generationSweep 64 1 Modules.Bands).def
let private sweep64x4 = (Modules.generationSweep 64 4 Modules.Bands).def
let private sweep8reg = (Modules.generationSweep 8 1 Modules.Registers).def
let private sweep16reg = (Modules.generationSweep 16 1 Modules.Registers).def
let private sweep64reg = (Modules.generationSweep 64 1 Modules.Registers).def

/// Toroidal Life over packed rows, stated independently of the design.
let private stepModel (size: int) (world: uint64[]) =
    [| for y in 0 .. size - 1 ->
           let mutable row = 0UL

           for x in 0 .. size - 1 do
               let mutable live = 0

               for dy in -1 .. 1 do
                   for dx in -1 .. 1 do
                       if not (dy = 0 && dx = 0) then
                           let yy = (y + dy + size) % size
                           let xx = (x + dx + size) % size

                           if (world[yy] >>> xx) &&& 1UL = 1UL then
                               live <- live + 1

               let self = (world[y] >>> x) &&& 1UL

               if live = 3 || (self = 1UL && live = 2) then
                   row <- row ||| (1UL <<< x)

           row |]

let private fillWorld (sim: Sim) (size: int) (world: uint64[]) =
    for y in 0 .. size - 1 do
        sim.Poke("fill_addr", uint64 y)
        sim.Poke("fill_data", world[y])
        sim.Poke("fill_enable", 1UL)
        sim.Tick()

    sim.Poke("fill_enable", 0UL)

let private probeWorld (sim: Sim) (size: int) =
    [| for y in 0 .. size - 1 ->
           sim.Poke("probe_addr", uint64 y)
           sim.Tick()
           sim.Peek "probe_data" |]

let private randomWorld (size: int) (seed: int) =
    let rand = System.Random seed
    let mask = if size = 64 then System.UInt64.MaxValue else (1UL <<< size) - 1UL
    [| for _ in 1..size -> (uint64 (rand.Next()) <<< 32 ||| uint64 (rand.Next())) &&& mask |]

let rec private stepped (size: int) (world: uint64[]) (generations: int) =
    if generations = 0 then world else stepped size (stepModel size world) (generations - 1)

/// Free-running drive: hold `run` until the generation counter reaches the
/// target, release, and let any sweep still in flight finish. The banded tier
/// may complete one more generation after release — the counter says how
/// many, and the model is stepped that many times, so the check is exact
/// either way.
let private sweepAgrees (name: string) (d: ModuleDef) (size: int) (targetGenerations: int) =
    test name {
        let sim = Sim d
        let world = randomWorld size 17
        fillWorld sim size world

        sim.Poke("run", 1UL)
        let mutable cycles = 0

        while sim.Peek "generation" < uint64 targetGenerations && cycles < 200 * (size + 8) do
            sim.Tick()
            cycles <- cycles + 1

        sim.Poke("run", 0UL)

        for _ in 1 .. 2 * size + 32 do
            sim.Tick()

        let completed = int (sim.Peek "generation")
        Expect.isGreaterThanOrEqual completed targetGenerations "the counter never reached the target"
        Expect.isLessThanOrEqual completed (targetGenerations + 1) "more than one generation overshot"

        Expect.equal
            (List.ofArray (probeWorld sim size))
            (List.ofArray (stepped size world completed))
            $"diverged from the model after %d{completed} generations"
    }

/// The register tier's defining measurement, alongside its correctness: with
/// `run` held for exactly n cycles, the counter reads exactly n — one
/// generation per cycle, which is the claim of the whole tier.
let private registerTierExact (name: string) (d: ModuleDef) (size: int) (generations: int) =
    test name {
        let sim = Sim d
        let world = randomWorld size 23
        fillWorld sim size world

        sim.Poke("run", 1UL)

        for _ in 1..generations do
            sim.Tick()

        sim.Poke("run", 0UL)
        sim.Tick()

        Expect.equal (sim.Peek "generation") (uint64 generations) "not one generation per cycle"

        Expect.equal
            (List.ofArray (probeWorld sim size))
            (List.ofArray (stepped size world generations))
            $"diverged from the model after %d{generations} generations"
    }

let tests =
    testList
        "cell"
        // The rule has 2^8 neighbourhoods and two self states, so "all of them"
        // is 512 cases and takes milliseconds. There is no reason to sample a
        // space this small, and a golden vector over it would be strictly
        // weaker: B3/S22, B3/S23 and B3/S234 all agree on most of it.
        [ test "B3/S23 holds for every neighbourhood" {
              let sim = simOf ()

              for self in 0 .. 1 do
                  for neighborhood in 0 .. 255 do
                      sim.Poke("state", uint64 self)
                      sim.Poke("neighbors", uint64 neighborhood)

                      Expect.equal
                          (sim.Peek "state_out")
                          (expected self neighborhood)
                          $"self=%d{self} neighbours=0b%08B{neighborhood}"
          }

          // The streamed sweep against the same rule stated over whole worlds:
          // the read schedule, the halo addressing, the window, 64 Cells and
          // the write counter all have to be right at once for even one
          // generation to match, and several generations catch state that
          // fails to re-arm between them.
          sweepAgrees "streamed sweep matches Life at 8x8" sweep8 8 4
          sweepAgrees "streamed sweep, 2 engines, 8x8" sweep8x2 8 4
          sweepAgrees "streamed sweep, 4 engines, 8x8" sweep8x4 8 3
          sweepAgrees "streamed sweep matches Life at 64x64" sweep64 64 3
          sweepAgrees "streamed sweep, 4 engines, 64x64" sweep64x4 64 2

          // The register tier: the same design under the flip-flop mapping —
          // and the tier's claim is a measurement, not just agreement.
          registerTierExact "register tier: 1 gen/cycle at 8x8" sweep8reg 8 12
          registerTierExact "register tier: 1 gen/cycle at 16x16" sweep16reg 16 8
          registerTierExact "register tier: 1 gen/cycle at 64x64" sweep64reg 64 6 ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
