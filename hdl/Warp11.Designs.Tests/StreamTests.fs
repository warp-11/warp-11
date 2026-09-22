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

let tests =
    testList
        "Streams"
        [ testCase "module boundaries deliver every beat once and in order" <| fun _ ->
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
