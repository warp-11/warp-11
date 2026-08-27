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
          } ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
