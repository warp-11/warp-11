module Warp11.Designs.Tests.ForkTests

open System.Collections.Generic
open Expecto
open Warp11

/// A fork with one blocking branch and one dropping branch, both brought out
/// as streams, so a test can stall one and watch the other.
let private forkHarness =
    defModule
        "ForkHarness"
        (fun p ->
            (streamInputPorts p "in" (layout1 ("data", 16)),
             streamOutputPorts p "keep" (layout1 ("data", 16)),
             streamOutputPorts p "obs" (layout1 ("data", 16)),
             p.outPort "obs_dropped" 32))
        (fun (inPorts, keepPorts, obsPorts, droppedPort) ->
            let fork =
                streamFork "fx" [ blockingBranch 4; droppingBranch 4 ] (streamSource inPorts)

            streamSink keepPorts fork.branches[0]
            streamSink obsPorts fork.branches[1]
            fork.dropped[1] ==> droppedPort)

/// Drive `n` beats through the harness, taking from the blocking branch
/// whenever it offers and taking from the observer only when `observe` says.
/// Returns what the blocking branch produced, and the drop count.
let private run (observe: int -> bool) (n: int) =
    let sim = Sim forkHarness.def
    let kept = List<uint64>()
    let mutable next = 1UL
    let mutable cycle = 0

    sim.Poke("keep_ready", 1UL)

    while kept.Count < n && cycle < 20000 do
        sim.Poke("in_valid", 1UL)
        sim.Poke("in_data", next)
        sim.Poke("obs_ready", (if observe cycle then 1UL else 0UL))

        let accepted = sim.Peek "in_ready" = 1UL

        if sim.Peek "keep_valid" = 1UL then
            kept.Add(sim.Peek "keep_data")

        if accepted then
            next <- next + 1UL

        sim.Tick()
        cycle <- cycle + 1

    kept |> List.ofSeq, sim.Peek "obs_dropped"

[<Tests>]
let tests =
    testList
        "fork"
        [
          // THE PROPERTY. An observer is attached to watch a stream, so
          // attaching one must not change the stream. A `Dropping` branch
          // that is never read at all must leave the blocking branch's
          // sequence exactly as it was when the observer kept up perfectly.
          test "a stalled dropping branch does not perturb the blocking branch" {
              let attentive, noDrops = run (fun _ -> true) 200
              let absent, someDrops = run (fun _ -> false) 200
              let occasional, _ = run (fun c -> c % 7 = 0) 200

              Expect.equal absent attentive "an ignored observer changed what the kept branch saw"
              Expect.equal occasional attentive "a slow observer changed what the kept branch saw"
              Expect.equal noDrops 0UL "the attentive observer dropped a beat"
              Expect.isGreaterThan someDrops 0UL "the ignored observer should have dropped beats"
          }

          // The blocking branch is still lossless and in order: a fork is a
          // broadcast, not a sampler, for anything that asked to block.
          test "the blocking branch loses nothing and reorders nothing" {
              let kept, _ = run (fun c -> c % 5 = 0) 300
              let expected = [ for i in 1UL .. uint64 kept.Length -> i ]
              Expect.equal kept expected "the blocking branch dropped or reordered a beat"
          }

          // A dropping branch drops, but what it does deliver is in order —
          // it loses beats, never ordering.
          test "a dropping branch delivers a subsequence, in order" {
              let sim = Sim forkHarness.def
              let seen = List<uint64>()
              let mutable next = 1UL

              sim.Poke("keep_ready", 1UL)

              for cycle in 1..2000 do
                  sim.Poke("in_valid", 1UL)
                  sim.Poke("in_data", next)
                  sim.Poke("obs_ready", (if cycle % 9 = 0 then 1UL else 0UL))

                  if cycle % 9 = 0 && sim.Peek "obs_valid" = 1UL then
                      seen.Add(sim.Peek "obs_data")

                  if sim.Peek "in_ready" = 1UL then
                      next <- next + 1UL

                  sim.Tick()

              Expect.isGreaterThan seen.Count 0 "the observer saw nothing at all"
              Expect.isAscending (List.ofSeq seen) "the dropping branch reordered beats"
          }
        ]
