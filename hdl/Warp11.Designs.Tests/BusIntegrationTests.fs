module Warp11.Designs.Tests.BusIntegrationTests

open System.Text.RegularExpressions
open Expecto
open Warp11
open Warp11.Designs

let private words = [ for index in 0..63 -> uint64 (index * 7 + 3) ]
let private expectedSums = words |> List.scan (+) 0UL |> List.tail |> List.map (fun value -> value &&& 0xFFFFFFFFUL)
let private clientExpected = [ for index in 0..15 -> uint64 ((index + 1) * (index + 2) / 2 * 3) ]

let private runOnChip design =
    let sim = Sim design
    sim.Poke("run", 0UL)
    words |> List.iteri (fun index word ->
        sim.Poke("fill_addr", uint64 index)
        sim.Poke("fill_data", word)
        sim.Poke("fill_enable", 1UL)
        sim.Tick())
    sim.Poke("fill_enable", 0UL)
    sim.Poke("run", 1UL)
    let mutable cycles = 0
    while sim.Peek "req_more" = 1UL || cycles < 8 do
        sim.Tick()
        cycles <- cycles + 1
    for _ in 1..64 do sim.Tick()
    [ for index in 0..63 do
          sim.Poke("probe_addr", uint64 index)
          sim.Tick()
          yield sim.Peek "probe_data" ]

let private topLines (design: ModuleDef) =
    let text = emitDesign design
    text.Substring(text.IndexOf $"module {design.name} ").Split '\n'

let private storageTests =
    testList
        "Storage mapping"
        [ testCase "one kernel computes identical sums across four storages" <| fun _ ->
              Expect.equal (runOnChip sumOverLut.def) expectedSums "Distributed memory sums should agree"
              Expect.equal (runOnChip sumOverBlock.def) expectedSums "Block memory sums should agree"
              Expect.equal (runOnChip sumOverUltra.def) expectedSums "UltraRAM sums should agree"

              let sim = Sim sumOverDdr.def
              let ddr = SimAxiDdr(sim, 8192, dataBytes = 4, arEvery = 2, rDelay = 8, awEvery = 2, bDelay = 4)
              words |> List.iteri (fun index word -> ddr.WriteWord(index * 4, uint32 word))
              sim.Poke("run", 1UL)
              let mutable cycles = 0
              while (sim.Peek "req_more" = 1UL || cycles < 8) && cycles < 50000 do
                  ddr.Cycle()
                  cycles <- cycles + 1
              Expect.isLessThan cycles 50000 $"DDR request issue timed out after {cycles} cycles"
              for _ in 1..4096 do ddr.Cycle()
              let actual = [ for index in 0..63 -> uint64 (ddr.ReadWord(0x1000 + index * 4)) ]
              Expect.equal actual expectedSums "Paced DDR sums should agree"

          testCase "running-sum kernel structure is storage invariant" <| fun _ ->
              let owned = [ "req_index"; "req_more"; "acc"; "sum_total"; "sum_ready" ]
              let declarations design =
                  topLines design
                  |> Array.filter (fun line ->
                      let text = line.Trim()
                      (text.StartsWith "reg " || text.StartsWith "wire ")
                      && owned |> List.exists (fun name -> text.EndsWith $"{name};"))
                  |> Array.map (fun line -> line.Trim())
                  |> Array.sort
              let updates design = topLines design |> Array.filter (fun line -> line.Trim().StartsWith "req_index <=") |> Array.map (fun line -> line.Trim())
              let designs = [ sumOverLut.def; sumOverBlock.def; sumOverUltra.def; sumOverDdr.def ]
              let expectedDeclarations = declarations sumOverLut.def
              let expectedUpdates = updates sumOverLut.def
              Expect.equal expectedDeclarations.Length 5 "The comparison should cover every kernel declaration"
              Expect.equal expectedUpdates.Length 2 "The comparison should cover reset and update assignments"
              for design in designs do
                  Expect.equal (declarations design) expectedDeclarations $"{design.name} should retain kernel declarations"
                  Expect.equal (updates design) expectedUpdates $"{design.name} should retain address generation" ]

let private stage (sim: Sim) cycle clients =
    sim.Poke("run", 0UL)
    for client in clients do
        for index in 0..15 do
            sim.Poke($"{client}_fill_addr", uint64 index)
            sim.Poke($"{client}_fill_data", uint64 ((index + 1) * 3))
            sim.Poke($"{client}_fill_enable", 1UL)
            cycle ()
        sim.Poke($"{client}_fill_enable", 0UL)
    sim.Poke("run", 1UL)
    for _ in 1..2000 do cycle ()

let private overBus design clients prefixes =
    let sim = Sim design
    let slaves = [ for prefix in prefixes -> SimAxiWriteSlave(sim, 4096, prefix = prefix, dataBytes = 4) ]
    let cycle () =
        for slave in slaves do slave.Capture()
        sim.Tick()
        for slave in slaves do slave.Pace()
    stage sim cycle clients
    let memory = slaves.Head.Memory
    [ for index in 0..15 ->
          uint64 memory[0x100 + index * 4]
          ||| (uint64 memory[0x101 + index * 4] <<< 8)
          ||| (uint64 memory[0x102 + index * 4] <<< 16)
          ||| (uint64 memory[0x103 + index * 4] <<< 24) ]

let private busLevelTests =
    testList
        "Bus and window levels"
        [ testCase "client structure is invariant across bus mappings" <| fun _ ->
              let owned = [ "a_index"; "a_asking"; "a_more"; "a_out_index"; "a_writing"; "a_acc"; "a_total"; "a_all_accepted" ]
              let declarations design =
                  topLines design
                  |> Array.map (fun line -> line.Trim())
                  |> Array.filter (fun text -> (text.StartsWith "reg " || text.StartsWith "wire ") && owned |> List.exists (fun name -> text.EndsWith $"{name};"))
                  |> Array.sort
              let designs = [ oneOwnerOnePort.def; twoOwnersOnePort.def; twoOwnersTwoPorts.def; sumWhollyOnChip.def ]
              let expected = declarations designs.Head
              Expect.equal expected.Length 8 "The comparison should cover every client declaration"
              for design in designs do Expect.equal (declarations design) expected $"{design.name} should retain client structure"

          testCase "ports and arbitration reflect region topology" <| fun _ ->
              let arbitration design = emitDesign design |> fun text -> text.Split('\n') |> Array.filter (fun line -> line.Contains "mergeTakeA") |> Array.length
              let ports design = Regex.Matches((emitDesign design).Split('\n')[0], "m_axi[a-z0-9_]*_(aw|ar|w|r|b)[a-z]+").Count
              Expect.equal (ports sumWhollyOnChip.def) 0 "On-chip mapping should emit no AXI ports"
              Expect.equal (arbitration sumWhollyOnChip.def) 0 "On-chip mapping should emit no arbiter"
              Expect.equal (ports oneOwnerOnePort.def) 16 "One port should expose one AXI interface"
              Expect.equal (arbitration oneOwnerOnePort.def) 0 "One region should need no arbiter"
              Expect.equal (ports twoOwnersOnePort.def) 16 "Two regions should share one AXI interface"
              Expect.isGreaterThan (arbitration twoOwnersOnePort.def) 0 "Two shared regions should emit arbitration"
              Expect.equal (ports twoOwnersTwoPorts.def) 32 "Two ports should expose two AXI interfaces"
              Expect.equal (arbitration twoOwnersTwoPorts.def) 0 "Separate ports should need no arbiter"
              Expect.stringContains (emitDesign twoOwnersTwoPorts.def) "m_axi_hp1_awvalid" "The second bus should retain its prefix"

          testCase "bus ownership and unused windows are rejected" <| fun _ ->
              Expect.throwsC (fun () -> emitDesign (onBusWithTwoOwners ()).def |> ignore) (fun ex -> Expect.stringContains ex.Message "already has an owner" "The bus should identify duplicate ownership")
              Expect.throwsC (fun () -> emitDesign (onWindowNeverWritten ()).def |> ignore) (fun ex ->
                  Expect.stringContains ex.Message "b_window_valid" "The unused window should be named"
                  Expect.stringContains ex.Message "driven 0 times" "The unused window should identify its floating valid net")

          testCase "bus and on-chip mappings compute identical sums" <| fun _ ->
              Expect.equal (overBus oneOwnerOnePort.def [ "a" ] [ "m_axi" ]) clientExpected "One window on one port should agree"
              Expect.equal (overBus twoOwnersOnePort.def [ "a"; "b" ] [ "m_axi" ]) clientExpected "Two windows sharing one port should agree"
              Expect.equal (overBus twoOwnersTwoPorts.def [ "a"; "b" ] [ "m_axi_hp0"; "m_axi_hp1" ]) clientExpected "Two windows on separate ports should agree"
              let sim = Sim sumWhollyOnChip.def
              stage sim sim.Tick [ "a" ]
              let actual = [ for index in 0..15 do sim.Poke("probe_addr", uint64 index); sim.Tick(); yield sim.Peek "probe_data" ]
              Expect.equal actual clientExpected "On-chip mapping should agree" ]

let private landedWhen watch =
    let sim = Sim sumReportsDone.def
    let ddr = SimAxiWriteSlave(sim, 4096, dataBytes = 4, awEvery = 8, bDelay = 6)
    sim.Poke("run", 0UL)
    for index in 0..15 do
        sim.Poke("a_fill_addr", uint64 index)
        sim.Poke("a_fill_data", uint64 ((index + 1) * 3))
        sim.Poke("a_fill_enable", 1UL)
        ddr.Cycle()
    sim.Poke("a_fill_enable", 0UL)
    sim.Poke("run", 1UL)
    let mutable cycles = 0
    while sim.Peek watch <> 1UL && cycles < 5000 do ddr.Cycle(); cycles <- cycles + 1
    Expect.isLessThan cycles 5000 $"{watch} did not rise after {cycles} cycles"
    let memory = ddr.Memory
    [ for index in 0..15 ->
          uint64 memory[0x100 + index * 4]
          ||| (uint64 memory[0x101 + index * 4] <<< 8)
          ||| (uint64 memory[0x102 + index * 4] <<< 16)
          ||| (uint64 memory[0x103 + index * 4] <<< 24) ]

let private completionTests =
    testList
        "Completion semantics"
        [ testCase "done means every paced bus write has landed" <| fun _ ->
              Expect.equal (landedWhen "a_done") clientExpected "Every word should be readable when done rises"
              let handed = landedWhen "a_handed_over"
              Expect.isLessThan (List.map2 (=) handed clientExpected |> List.filter id |> List.length) 16 "Hand-over should precede physical completion on a paced port"

          testCase "on-chip completion means every sum is readable" <| fun _ ->
              let sim = Sim sumWhollyOnChip.def
              sim.Poke("run", 0UL)
              for index in 0..15 do
                  sim.Poke("a_fill_addr", uint64 index)
                  sim.Poke("a_fill_data", uint64 ((index + 1) * 3))
                  sim.Poke("a_fill_enable", 1UL)
                  sim.Tick()
              sim.Poke("a_fill_enable", 0UL)
              sim.Poke("run", 1UL)
              let mutable cycles = 0
              while sim.Peek "a_done" <> 1UL && cycles < 5000 do sim.Tick(); cycles <- cycles + 1
              Expect.isLessThan cycles 5000 $"On-chip done did not rise after {cycles} cycles"
              let actual = [ for index in 0..15 do sim.Poke("probe_addr", uint64 index); sim.Tick(); yield sim.Peek "probe_data" ]
              Expect.equal actual clientExpected "Every on-chip result should be readable at completion" ]

let private channelTests =
    testList
        "Read channels"
        [ testCase "pipelined channel preserves order and throughput" <| fun _ ->
              let sim = Sim pipelinedReadSlave.def
              let axi = SimAxi.client sim
              axi.write32 0x00UL 0xAAAA5555UL
              for index in 0..7 do axi.write32 (0x20UL + uint64 (index * 4)) (0xB000UL + uint64 index)
              let value word = if word >= 8 then 0xB000UL + uint64 (word - 8) elif word = 0 then 0xAAAA5555UL else 0x11C0DEUL
              let addresses = [| 0; 8; 1; 9; 2; 10; 15; 0 |]
              let got = ResizeArray<uint64>()
              let mutable issued = 0
              let mutable cycles = 0
              sim.Poke("s_axi_rready", 1UL)
              sim.Poke("s_axi_arvalid", 1UL)
              sim.Poke("s_axi_araddr", uint64 (addresses[0] * 4))
              while got.Count < 8 && cycles < 40 do
                  let accepted = issued < 8 && sim.Peek "s_axi_arready" = 1UL
                  if sim.Peek "s_axi_rvalid" = 1UL then got.Add(sim.Peek "s_axi_rdata")
                  sim.Tick()
                  if accepted then
                      issued <- issued + 1
                      if issued < 8 then sim.Poke("s_axi_araddr", uint64 (addresses[issued] * 4)) else sim.Poke("s_axi_arvalid", 0UL)
                  cycles <- cycles + 1
              Expect.equal got.Count 8 $"Only {got.Count}/8 reads completed after {cycles} cycles"
              Expect.sequenceEqual got (addresses |> Seq.map value) "Back-to-back responses should remain ordered"
              Expect.isLessThanOrEqual cycles 13 "Eight pipelined reads should complete within thirteen cycles"

          testCase "pipelined channel credits cap and drain in order" <| fun _ ->
              let sim = Sim pipelinedReadSlave.def
              let axi = SimAxi.client sim
              for index in 0..7 do axi.write32 (0x20UL + uint64 (index * 4)) (0xB000UL + uint64 index)
              sim.Poke("s_axi_rready", 0UL)
              sim.Poke("s_axi_arvalid", 1UL)
              let mutable accepts = 0
              for index in 0..7 do
                  sim.Poke("s_axi_araddr", uint64 ((8 + min index 7) * 4))
                  if sim.Peek "s_axi_arready" = 1UL then accepts <- accepts + 1
                  sim.Tick()
              Expect.equal accepts 4 "Credits should allow exactly four outstanding reads"
              Expect.equal (sim.Peek "s_axi_arready") 0UL "ARREADY should stay low at the credit ceiling"
              sim.Poke("s_axi_arvalid", 0UL)
              sim.Poke("s_axi_rready", 1UL)
              let drained = ResizeArray<uint64>()
              for _ in 1..10 do
                  if sim.Peek "s_axi_rvalid" = 1UL then drained.Add(sim.Peek "s_axi_rdata")
                  sim.Tick()
              Expect.sequenceEqual drained [ 0xB000UL; 0xB001UL; 0xB002UL; 0xB003UL ] "Queued responses should drain in order"

          testCase "busy read channel holds ARREADY low until its answer" <| fun _ ->
              let sim = Sim deepChannelSlave.def
              sim.Poke("s_axi_awaddr", 0UL)
              sim.Poke("s_axi_awvalid", 1UL)
              sim.Poke("s_axi_wdata", 0x5A5A1234UL)
              sim.Poke("s_axi_wvalid", 1UL)
              sim.Poke("s_axi_bready", 1UL)
              sim.Tick()
              sim.Poke("s_axi_awvalid", 0UL)
              sim.Poke("s_axi_wvalid", 0UL)
              sim.Tick()
              sim.Poke("s_axi_araddr", 0UL)
              sim.Poke("s_axi_arvalid", 1UL)
              sim.Poke("s_axi_rready", 1UL)
              Expect.equal (sim.Peek "s_axi_arready") 1UL "The first read should be accepted immediately"
              sim.Tick()
              for cycle in 1..2 do
                  Expect.equal (sim.Peek "s_axi_arready") 0UL $"ARREADY should be low during gap cycle {cycle}"
                  Expect.equal (sim.Peek "s_axi_rvalid") 0UL $"RVALID should not rise during gap cycle {cycle}"
                  sim.Tick()
              Expect.equal (sim.Peek "s_axi_rvalid") 1UL "RVALID should rise exactly three cycles after acceptance"
              Expect.equal (sim.Peek "s_axi_rdata") 0x5A5A1234UL "The delayed response should contain the written word" ]

let tests =
    testList "Bus and memory integration" [ storageTests; busLevelTests; completionTests; channelTests ]
