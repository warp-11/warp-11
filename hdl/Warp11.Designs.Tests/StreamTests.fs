module Warp11.Designs.Tests.StreamTests

open System.Collections.Generic
open Expecto
open Warp11
open Warp11.Designs

let private verifyFifo name design depth =
    let sim = Sim design
    let random = System.Random 4242
    let model = Queue<uint64>()
    let mutable next = 1UL

    for cycle in 1..20000 do
        let offer = random.Next 2 = 0
        let take = random.Next 2 = 0

        sim.Poke("in_data", next)
        sim.Poke("in_valid", if offer then 1UL else 0UL)
        sim.Poke("out_ready", if take then 1UL else 0UL)

        let accepted = offer && sim.Peek "in_ready" = 1UL

        if take && sim.Peek "out_valid" = 1UL then
            Expect.isGreaterThan model.Count 0 $"{name} produced a phantom beat at cycle {cycle}"
            let expected = model.Dequeue()
            Expect.equal (sim.Peek "out_data") expected $"{name} reordered or corrupted a beat at cycle {cycle}"

        if accepted then
            model.Enqueue next
            next <- (next % 250UL) + 1UL

        Expect.isLessThanOrEqual model.Count depth $"{name} exceeded its declared depth at cycle {cycle}"
        sim.Tick()

let private capacityOf design =
    let sim = Sim design
    sim.Poke("in_data", 7UL)
    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 0UL)
    let mutable accepted = 0

    for _ in 1..600 do
        if sim.Peek "in_ready" = 1UL then accepted <- accepted + 1
        sim.Tick()

    accepted

let private sustainedRate design =
    let sim = Sim design
    sim.Poke("in_data", 9UL)
    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 1UL)

    for _ in 1..64 do
        sim.Tick()

    let mutable moved = 0

    for _ in 1..1000 do
        if sim.Peek "out_valid" = 1UL then moved <- moved + 1
        sim.Tick()

    moved

let private runStreamStalled
    label
    (design: ModuleDef)
    (inputFields: string list)
    (outputFields: string list)
    (beats: uint64 list list)
    seed
    =
    let sim = Sim design
    let random = seed |> Option.map System.Random
    let output = ResizeArray<uint64 list>()
    let mutable fed = 0
    let mutable cycles = 0

    while output.Count < beats.Length && cycles < 400_000 do
        let offer =
            fed < beats.Length
            && (match random with
                | Some rng -> rng.Next(0, 4) > 0
                | None -> true)

        let take =
            match random with
            | Some rng -> rng.Next(0, 4) > 0
            | None -> true

        sim.Poke("in_valid", if offer then 1UL else 0UL)
        sim.Poke("out_ready", if take then 1UL else 0UL)

        if offer then
            List.iter2 (fun field value -> sim.Poke(field, value)) inputFields beats[fed]

        if take && sim.Peek "out_valid" = 1UL then
            output.Add(outputFields |> List.map sim.Peek)

        let accepted = offer && sim.Peek "in_ready" = 1UL
        sim.Tick()
        if accepted then fed <- fed + 1
        cycles <- cycles + 1

    Expect.equal fed beats.Length $"{label} timed out after accepting {fed}/{beats.Length} inputs"
    Expect.equal output.Count beats.Length $"{label} timed out after producing {output.Count}/{beats.Length} outputs"
    List.ofSeq output

let private verifyStallIndependence ordered label design inputFields outputFields beats =
    let canonicalize results = if ordered then results else List.sort results
    let baseline = runStreamStalled $"{label} baseline" design inputFields outputFields beats None

    for seed in 1..6 do
        let stalled = runStreamStalled $"{label}, seed {seed}" design inputFields outputFields beats (Some seed)
        Expect.equal (canonicalize stalled) (canonicalize baseline) $"Stalls changed {label} output for seed {seed}"

/// What one run of `arbitratedMerge` saw: beats served per input, and whether
/// a beat that was offered and not taken was still the one offered next cycle.
type private MergeRun = { served: int list; offersHeld: bool }

/// Drive `arbitratedMerge`: each input in `asking` raises a beat with
/// probability `askRate` whenever it has none waiting and holds it until
/// taken; the output is ready with probability `readyRate`. Every beat
/// carries its input and that input's running count, so a beat lost,
/// repeated, reordered within its input or granted to the wrong input fails
/// here, whatever the arbitration.
let private runMerge (arbitration: Arbitration) (asking: int list) (askRate: float) (readyRate: float) (cycles: int) : MergeRun =
    let design = (arbitratedMerge arbitration).def
    let name = design.name
    let n = arbitratedMergeInputs
    let sim = Sim design
    let random = System.Random 2026_11_14
    let sent = Array.zeroCreate n
    let served = Array.zeroCreate n
    let waiting = Array.create n false
    let mutable offered = None
    let mutable offersHeld = true

    for cycle in 1..cycles do
        for i in asking do
            if not waiting[i] && random.NextDouble() < askRate then
                waiting[i] <- true
                sim.Poke($"in%d{i}_data", (uint64 i <<< 12) ||| uint64 (sent[i] % 4096))

        for i in 0 .. n - 1 do
            sim.Poke($"in%d{i}_valid", if waiting[i] then 1UL else 0UL)

        sim.Poke("out_ready", if random.NextDouble() < readyRate then 1UL else 0UL)

        let valid = sim.Peek "out_valid" = 1UL
        let ready = sim.Peek "out_ready" = 1UL
        let data = sim.Peek "out_data"
        let taken = [ for i in 0 .. n - 1 -> waiting[i] && sim.Peek $"in%d{i}_ready" = 1UL ]

        match offered with
        | Some earlier when not valid || data <> earlier -> offersHeld <- false
        | _ -> ()

        offered <- if valid && not ready then Some data else None

        if valid && ready then
            let lane = int (data >>> 12)
            Expect.equal (int (data &&& 0xFFFUL)) (sent[lane] % 4096) $"{name}: input {lane}'s beats out of order at cycle {cycle}"
            Expect.equal taken [ for i in 0 .. n - 1 -> i = lane ] $"{name}: exactly the served input should be taken at cycle {cycle}"
            served[lane] <- served[lane] + 1
        else
            Expect.isFalse (List.exists id taken) $"{name}: an input was taken with no beat leaving at cycle {cycle}"

        sim.Tick()

        for i in 0 .. n - 1 do
            if taken[i] then
                waiting[i] <- false
                sent[i] <- sent[i] + 1

    { served = List.ofArray served
      offersHeld = offersHeld }

let tests =
    testList
        "Streams"
        [ testCase "a merge delivers every beat once, each input's in order, under every arbitration" <| fun _ ->
              for arbitration in arbitrations do
                  let run = runMerge arbitration [ 0 .. arbitratedMergeInputs - 1 ] 0.3 0.6 6000
                  Expect.isGreaterThan (List.sum run.served) 1000 $"{arbitrationName arbitration}: the merge should keep moving"

          testCase "round-robin gives every asker the same share, at a count that is not a power of two" <| fun _ ->
              for timing in [ ArbitrationTiming.SameCycle; ArbitrationTiming.Registered ] do
                  let arbitration = { policy = ArbitrationPolicy.RoundRobin; timing = timing }
                  let name = arbitrationName arbitration

                  // Every input asking flat out: a fifth each, and a beat a cycle.
                  let all = runMerge arbitration [ 0 .. arbitratedMergeInputs - 1 ] 1.0 1.0 3000
                  Expect.isGreaterThanOrEqual (List.sum all.served) 2999 $"{name}: saturated, a beat a cycle"
                  Expect.isLessThanOrEqual (List.max all.served - List.min all.served) 1 $"{name}: saturated shares {all.served}"

                  // Three of the five: a third each, nothing for the idle two.
                  let some = runMerge arbitration [ 0; 2; 3 ] 1.0 1.0 3000
                  let asked = [ some.served[0]; some.served[2]; some.served[3] ]
                  Expect.isLessThanOrEqual (List.max asked - List.min asked) 1 $"{name}: three askers' shares {some.served}"
                  Expect.equal (some.served[1], some.served[4]) (0, 0) $"{name}: an idle input is never served"

                  // Under stalls and gaps, nobody is left behind.
                  let loaded = runMerge arbitration [ 0 .. arbitratedMergeInputs - 1 ] 0.9 0.5 6000
                  Expect.isLessThanOrEqual (List.max loaded.served - List.min loaded.served) 2 $"{name}: loaded shares {loaded.served}"

          testCase "priority serves the lowest-numbered asker" <| fun _ ->
              for timing in [ ArbitrationTiming.SameCycle; ArbitrationTiming.Registered ] do
                  let arbitration = { policy = ArbitrationPolicy.Priority; timing = timing }
                  let name = arbitrationName arbitration

                  let all = runMerge arbitration [ 0 .. arbitratedMergeInputs - 1 ] 1.0 1.0 3000
                  Expect.equal all.served.Tail [ 0; 0; 0; 0 ] $"{name}: with input 0 asking flat out, nobody else is served"
                  Expect.isGreaterThanOrEqual all.served.Head 2999 $"{name}: and it gets a beat a cycle"

                  let rest = runMerge arbitration [ 1 .. arbitratedMergeInputs - 1 ] 1.0 1.0 3000
                  Expect.equal (rest.served[0], rest.served[2], rest.served[3], rest.served[4]) (0, 0, 0, 0) $"{name}: with input 0 idle, input 1 is the lowest"

                  // With gaps in the high inputs, the low ones are still served, in order of rank.
                  let gaps = runMerge arbitration [ 0 .. arbitratedMergeInputs - 1 ] 0.3 0.6 6000
                  Expect.equal gaps.served (List.sortDescending gaps.served) $"{name}: served by rank {gaps.served}"
                  Expect.isGreaterThan (List.last gaps.served) 0 $"{name}: the last input is served when the others have gaps"

          testCase "a registered merge offers from registers and holds what it offers" <| fun _ ->
              for arbitration in arbitrations do
                  let name = arbitrationName arbitration
                  let registered = arbitration.timing = ArbitrationTiming.Registered

                  let run = runMerge arbitration [ 0 .. arbitratedMergeInputs - 1 ] 0.3 0.6 6000

                  if registered then
                      Expect.isTrue run.offersHeld $"{name}: a beat offered and not taken stays offered"

                  // Within one cycle: does the output's valid follow an input's?
                  let sim = Sim (arbitratedMerge arbitration).def
                  sim.Poke("in2_data", 7UL)
                  sim.Poke("in2_valid", 1UL)
                  Expect.equal (sim.Peek "out_valid" = 1UL) (not registered) $"{name}: whether valid crosses the merge within a cycle"
                  sim.Tick()
                  Expect.equal (sim.Peek "out_valid") 1UL $"{name}: the beat is offered by the cycle after"
                  Expect.equal (sim.Peek "out_data") 7UL $"{name}: and it is the asker's"

          testCase "every connection delivers every beat once and in order under stalls" <| fun _ ->
              let beats = [ for i in 1..300 -> [ uint64 (i * 37 % 65536) ] ]
              for design in [ connectionWire; connectionForward; connectionBackward; connectionBoth ] do
                  for seed in 1..6 do
                      let out = runStreamStalled $"{design.def.name}, seed {seed}" design.def [ "in_data" ] [ "out_data" ] beats (Some seed)
                      Expect.equal out beats $"{design.def.name} should pass every beat once, in order (seed {seed})"

          testCase "a connection registers what its name says" <| fun _ ->
              // (design, ready registered, cycles a beat takes)
              for design, readyRegistered, latency in
                  [ connectionWire, false, 0; connectionForward, false, 1; connectionBackward, true, 0; connectionBoth, true, 1 ] do
                  let name = design.def.name
                  let sim = Sim design.def
                  sim.Poke("in_valid", 1UL)
                  sim.Poke("in_data", 7UL)
                  // Within one cycle: does the input's ready follow the output's?
                  let readyWhen outReady =
                      sim.Poke("out_ready", outReady)
                      sim.Peek "in_ready"
                  let changes = [ for _ in 1..20 do
                                      let a = readyWhen 0UL
                                      let b = readyWhen 1UL
                                      sim.Tick()
                                      yield a <> b ]
                  Expect.equal (List.exists id changes) (not readyRegistered) $"{name}: whether ready crosses it within a cycle"
                  // Latency, from empty: how many ticks before the first beat shows.
                  let fresh = Sim design.def
                  fresh.Poke("out_ready", 1UL)
                  fresh.Poke("in_valid", 1UL)
                  fresh.Poke("in_data", 9UL)
                  let shown = Seq.initInfinite id |> Seq.find (fun i -> (if i > 0 then fresh.Tick()); fresh.Peek "out_valid" = 1UL || i > 5)
                  Expect.equal shown latency $"{name}: cycles before the first beat shows"
                  // Full rate: unstalled, a beat a cycle.
                  let rate = Sim design.def
                  rate.Poke("out_ready", 1UL)
                  rate.Poke("in_valid", 1UL)
                  let mutable taken = 0
                  for i in 1..100 do
                      rate.Poke("in_data", uint64 i)
                      if rate.Peek "out_valid" = 1UL then taken <- taken + 1
                      rate.Tick()
                  Expect.isGreaterThanOrEqual taken (100 - latency) $"{name}: a beat a cycle when nothing stalls"

          testCase "eachChannel pairs every beat with its own partner when the channels drift" <| fun _ ->
              let random = System.Random 2026_11_14
              let beats = [ for i in 1..300 -> [ uint64 i; uint64 (random.Next(0, 1 <<< 24)) ] ]
              let fields = [ "left"; "right" ]
              for seed in 1..6 do
                  let out = runStreamStalled $"uneven channels, seed {seed}" eachChannelUneven.def [ for f in fields -> $"in_{f}" ] [ for f in fields -> $"out_{f}" ] beats (Some seed)
                  Expect.equal out beats $"Every beat should come back whole and in order (seed {seed})"

          testCase "module boundaries deliver every beat once and in order" <| fun _ ->
              let sim = Sim boundaryWalk.def
              let random = System.Random 20260908
              let seen = ResizeArray<uint64>()

              for _ in 1..2000 do
                  let take = random.Next 3 <> 0
                  sim.Poke("out_ready", if take then 1UL else 0UL)

                  if take && sim.Peek "out_valid" = 1UL then
                      seen.Add(sim.Peek "out_data")

                  sim.Tick()

              let unsaturated = seen |> Seq.takeWhile (fun value -> value < 0xFFUL) |> List.ofSeq
              Expect.isGreaterThan unsaturated.Length 60 "The run should contain enough transfers under backpressure"
              Expect.sequenceEqual unsaturated [ for value in 1..unsaturated.Length -> uint64 value ] "Backpressure must not drop or repeat beats"

          testCase "distributed FIFO agrees with a queue model" <| fun _ ->
              verifyFifo "distributed FIFO" bufferedStream.def 8

          testCase "block FIFO agrees with a queue model" <| fun _ ->
              verifyFifo "block FIFO" deepBufferedStream.def 128

          testCase "FIFO capacity is independent of storage implementation" <| fun _ ->
              Expect.equal (capacityOf bufferedStream.def) 8 "The distributed FIFO should hold exactly its declared depth"
              Expect.equal (capacityOf deepBufferedStream.def) 128 "Internal block-memory staging must not change visible capacity"

          testCase "FIFO throughput is independent of storage implementation" <| fun _ ->
              Expect.equal (sustainedRate bufferedStream.def) 1000 "The distributed FIFO should sustain one beat per cycle"
              Expect.equal (sustainedRate deepBufferedStream.def) 1000 "The block FIFO should sustain one beat per cycle"

          testCase "withContext returns each result with its request tag" <| fun _ ->
              let sim = Sim taggedDivide.def
              let random = System.Random 99
              let pending = Queue<uint64 * uint64 * uint64>()
              let mutable tag = 1UL
              let mutable completed = 0

              for cycle in 1..6000 do
                  let dividend = uint64 (random.Next 256)
                  let divisor = uint64 (random.Next(1, 256))

                  sim.Poke("dividend", dividend)
                  sim.Poke("divisor", divisor)
                  sim.Poke("tag", tag)
                  sim.Poke("in_valid", 1UL)
                  sim.Poke("out_ready", if random.Next 3 = 0 then 0UL else 1UL)

                  if sim.Peek "in_ready" = 1UL then
                      pending.Enqueue(dividend / divisor, dividend % divisor, tag)
                      tag <- (tag % 200UL) + 1UL

                  if sim.Peek "out_valid" = 1UL && sim.Peek "out_ready" = 1UL then
                      Expect.isGreaterThan pending.Count 0 $"Cycle {cycle} produced a result without a request"
                      let expectedQuotient, expectedRemainder, expectedTag = pending.Dequeue()
                      Expect.equal (sim.Peek "quotient") expectedQuotient $"Wrong quotient at cycle {cycle}"
                      Expect.equal (sim.Peek "remainder") expectedRemainder $"Wrong remainder at cycle {cycle}"
                      Expect.equal (sim.Peek "tag_out") expectedTag $"Result lost its request tag at cycle {cycle}"
                      completed <- completed + 1

                  sim.Tick()

              Expect.isGreaterThan completed 100 "The randomized run should complete enough divisions"

          testCase "farm returns each result with its request context" <| fun _ ->
              let sim = Sim farmedDivide.def
              let random = System.Random 7
              let expected = Dictionary<uint64, uint64 * uint64>()
              let mutable tag = 1UL
              let mutable completed = 0
              let mutable reordered = 0
              let mutable lastTag = 0UL

              for cycle in 1..8000 do
                  let dividend = uint64 (random.Next 256)
                  let divisor = uint64 (random.Next(1, 256))

                  sim.Poke("dividend", dividend)
                  sim.Poke("divisor", divisor)
                  sim.Poke("tag", tag)
                  sim.Poke("in_valid", 1UL)
                  sim.Poke("out_ready", if random.Next 4 = 0 then 0UL else 1UL)

                  if sim.Peek "in_ready" = 1UL && not (expected.ContainsKey tag) then
                      expected[tag] <- dividend / divisor, dividend % divisor
                      tag <- (tag % 200UL) + 1UL

                  if sim.Peek "out_valid" = 1UL && sim.Peek "out_ready" = 1UL then
                      let resultTag = sim.Peek "tag_out"
                      Expect.isTrue (expected.ContainsKey resultTag) $"Cycle {cycle} returned unknown or duplicate tag {resultTag}"
                      let expectedQuotient, expectedRemainder = expected[resultTag]
                      Expect.equal (sim.Peek "quotient") expectedQuotient $"Wrong quotient for tag {resultTag} at cycle {cycle}"
                      Expect.equal (sim.Peek "remainder") expectedRemainder $"Wrong remainder for tag {resultTag} at cycle {cycle}"
                      expected.Remove resultTag |> ignore

                      if lastTag <> 0UL && resultTag <> (lastTag % 200UL) + 1UL then
                          reordered <- reordered + 1

                      lastTag <- resultTag
                      completed <- completed + 1

                  sim.Tick()

              Expect.isGreaterThan completed 100 "The randomized run should complete enough farmed divisions"
              Expect.isGreaterThan reordered 100 "Unequal lane depths should exercise out-of-order completion"

          testCase "divider family is independent of stalls" <| fun _ ->
              let random = System.Random 11
              let pairs = [ for _ in 1..24 -> [ uint64 (random.Next(1, 256)); uint64 (random.Next(1, 256)) ] ]
              let tagged = pairs |> List.mapi (fun index operands -> operands @ [ uint64 (index % 16) ])

              verifyStallIndependence
                  true
                  "divider"
                  streamDivider.def
                  [ "dividend"; "divisor" ]
                  [ "quotient"; "remainder" ]
                  pairs

              verifyStallIndependence
                  true
                  "divider with context"
                  taggedDivide.def
                  [ "dividend"; "divisor"; "tag" ]
                  [ "quotient"; "remainder"; "tag_out" ]
                  tagged

              verifyStallIndependence
                  false
                  "divider farm"
                  farmedDivide.def
                  [ "dividend"; "divisor"; "tag" ]
                  [ "quotient"; "remainder"; "tag_out" ]
                  tagged ]
