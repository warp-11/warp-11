module Warp11.Designs.Tests.AxiIntegrationTests

open System.Numerics
open Expecto
open Warp11
open Warp11.Designs

let private fill (memory: byte[]) =
    for index in 0 .. memory.Length - 1 do memory[index] <- byte ((index * 37 + 11) % 256)

let private word (memory: byte[]) address =
    uint64 memory[address]
    ||| (uint64 memory[address + 1] <<< 8)
    ||| (uint64 memory[address + 2] <<< 16)
    ||| (uint64 memory[address + 3] <<< 24)

let private runSingleWord design (arEvery, rDelay, respEvery) =
    let sim = Sim design
    let slave = SimAxiReadSlave(sim, 4096, dataBytes = 4, arEvery = arEvery, rDelay = rDelay)
    fill slave.Memory
    let addresses = [| for index in 0..23 -> (index * 164) % 4092 &&& ~~~3 |]
    let results = ResizeArray<uint64>()
    let mutable next = 0
    let mutable cycles = 0
    while results.Count < addresses.Length && cycles < 3000 do
        sim.Poke("resp_ready", if cycles % respEvery = 0 then 1UL else 0UL)
        if next < addresses.Length then
            sim.Poke("req_valid", 1UL)
            sim.Poke("req_addr", uint64 addresses[next])
        else sim.Poke("req_valid", 0UL)
        slave.BeginCycle()
        let accepted = next < addresses.Length && sim.Peek "req_ready" = 1UL
        if sim.Peek "resp_valid" = 1UL && sim.Peek "resp_ready" = 1UL then results.Add(sim.Peek "resp_data")
        slave.FinishCycle()
        if accepted then next <- next + 1
        cycles <- cycles + 1
    Expect.equal results.Count addresses.Length $"{design.name} completed {results.Count}/{addresses.Length} reads after {cycles} cycles at pacing {(arEvery, rDelay, respEvery)}"
    Expect.sequenceEqual results (addresses |> Seq.map (word slave.Memory)) $"{design.name} returned wrong words at pacing {(arEvery, rDelay, respEvery)}"

let private runBurst (arEvery, rDelay, respEvery) =
    let sim = Sim axiReadMasterBurst.def
    let slave = SimAxiReadSlave(sim, 4096, dataBytes = 4, arEvery = arEvery, rDelay = rDelay)
    fill slave.Memory
    let bursts = [| 0, 16; 512, 1; 1024, 8; 2048, 4; 64, 16; 3000, 2 |]
    let expected = [ for address, beats in bursts do for index in 0 .. beats - 1 -> word slave.Memory (address + 4 * index), if index = beats - 1 then 1UL else 0UL ]
    let results = ResizeArray<uint64 * uint64>()
    let mutable next = 0
    let mutable cycles = 0
    while results.Count < expected.Length && cycles < 3000 do
        sim.Poke("resp_ready", if cycles % respEvery = 0 then 1UL else 0UL)
        if next < bursts.Length then
            let address, beats = bursts[next]
            sim.Poke("req_valid", 1UL)
            sim.Poke("req_addr", uint64 address)
            sim.Poke("req_len", uint64 (beats - 1))
        else sim.Poke("req_valid", 0UL)
        slave.BeginCycle()
        let accepted = next < bursts.Length && sim.Peek "req_ready" = 1UL
        if sim.Peek "resp_valid" = 1UL && sim.Peek "resp_ready" = 1UL then results.Add(sim.Peek "resp_data", sim.Peek "resp_last")
        slave.FinishCycle()
        if accepted then next <- next + 1
        cycles <- cycles + 1
    Expect.equal results.Count expected.Length $"Burst master completed {results.Count}/{expected.Length} beats after {cycles} cycles at pacing {(arEvery, rDelay, respEvery)}"
    Expect.sequenceEqual results expected $"Burst data or last markers differed at pacing {(arEvery, rDelay, respEvery)}"

let private readTests =
    testCase "AXI read masters survive four slave pacing patterns" <| fun _ ->
        for pacing in [ 1, 0, 1; 3, 2, 1; 2, 5, 2; 7, 3, 3 ] do
            runSingleWord axiReadMaster.def pacing
            runSingleWord axiReadMasterSingle.def pacing
            runBurst pacing

let private writeTest =
    testCase "AXI write master lands wide beats in DDR" <| fun _ ->
        let sim = Sim axiWriteMaster.def
        let ddr = SimAxiWriteSlave(sim, 256)
        let mutable sent = 0
        let mutable cycles = 0
        while sent < 4 && cycles < 24 do
            sim.Poke("in_valid", 1UL)
            sim.Poke("in_addr", uint64 (sent * 16))
            sim.PokeWide("in_data", (BigInteger(0xA0 + sent) <<< 120) ||| BigInteger(0x10 + sent))
            sim.Poke("in_strb", 0xFFFFUL)
            ddr.Capture()
            let accepted = sim.Peek "in_ready" = 1UL
            sim.Tick()
            ddr.Pace()
            if accepted then sent <- sent + 1
            cycles <- cycles + 1
        Expect.equal sent 4 $"Write master accepted only {sent}/4 beats after {cycles} cycles"
        for _ in 1..24 do ddr.Capture(); sim.Tick(); ddr.Pace()
        for index in 0..3 do
            Expect.equal ddr.Memory[index * 16] (byte (0x10 + index)) $"Low byte of beat {index} should land"
            Expect.equal ddr.Memory[index * 16 + 15] (byte (0xA0 + index)) $"High byte of beat {index} should land"

let private neighborhoodTest =
    testCase "neighborhood edge policies select the expected cells" <| fun _ ->
        let sim = Sim neighborCount.def
        let pattern = [ [ 1; 0; 1 ]; [ 0; 1; 0 ]; [ 1; 0; 1 ] ]
        for y in 0..2 do for x in 0..2 do sim.Poke($"g{y}{x}", uint64 pattern[y].[x])
        sim.Tick()
        Expect.equal (sim.Peek "moore") 4UL "Moore neighborhood should count four corners"
        Expect.equal (sim.Peek "corner") 1UL "Dead-border corner should see only the center"
        Expect.equal (sim.Peek "vonNeumann") 0UL "Von Neumann center should see four dead edges"
        Expect.equal (sim.Peek "wrapped") 4UL "Toroidal corner should wrap four live cells"
        Expect.equal (sim.Peek "clamped") 4UL "Clamped corner should replicate its center"

let private captureFrame (sim: Sim) (ddr: SimAxiWriteSlave) =
    sim.Poke("snap_capture", 1UL)
    ddr.Cycle()
    sim.Poke("snap_capture", 0UL)
    let mutable cycles = 0
    while sim.Peek "host_ready" = 0UL && cycles < 300 do
        ddr.Cycle()
        cycles <- cycles + 1
    Expect.equal (sim.Peek "host_ready") 1UL $"Snapshot was not ready after {cycles} cycles"
    let slot = int (sim.Peek "host_slot")
    [ for row in 0..3 -> ddr.Memory[slot * 16 + row * 4] ]

let private coherentAt (rows: byte list) =
    let instant = rows[0]
    if rows |> List.mapi (fun row value -> value = instant + byte (row * 16)) |> List.forall id then Some instant else None

let private snapshotTest =
    testCase "snapshot conflate writes coherent rotating frames to DDR" <| fun _ ->
        let sim = Sim snapshotDdr.def
        let ddr = SimAxiWriteSlave(sim, 64, dataBytes = 4)
        for _ in 1..40 do ddr.Cycle()
        let first = captureFrame sim ddr
        let firstInstant = Expect.wantSome (coherentAt first) $"First frame was incoherent: {first}"

        sim.Poke("snap_capture", 1UL)
        ddr.Cycle()
        sim.Poke("snap_capture", 0UL)
        Expect.equal (sim.Peek "host_overrun") 1UL "Capture while holding should count an overrun"
        sim.Poke("snap_release", 1UL)
        ddr.Cycle()
        sim.Poke("snap_release", 0UL)
        for _ in 1..25 do ddr.Cycle()

        let second = captureFrame sim ddr
        let secondInstant = Expect.wantSome (coherentAt second) $"Second frame was incoherent: {second}"
        Expect.notEqual secondInstant firstInstant "Rotating snapshots should capture different instants"
        Expect.equal (sim.Peek "host_overrun") 0UL "A successful later capture should clear overrun"

let tests = testList "AXI and snapshot integration" [ readTests; writeTest; neighborhoodTest; snapshotTest ]
