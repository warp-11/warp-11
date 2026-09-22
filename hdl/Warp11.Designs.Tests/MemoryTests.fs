module Warp11.Designs.Tests.MemoryTests

open System.Collections.Generic
open System.Numerics
open Expecto
open Warp11
open Warp11.Designs

let tests =
    testList
        "Memory"
        [ testCase "distributed ROM returns every declared value" <| fun _ ->
              let expected = [| 0UL; 1UL; 4UL; 9UL; 16UL; 25UL; 36UL; 49UL |]
              let sim = Sim romLookup.def

              for address, value in Array.indexed expected do
                  sim.Poke("index", uint64 address)
                  sim.Tick()
                  Expect.equal (sim.Peek "square") value $"Wrong square at address {address}"

          testCase "block ROM returns every declared value" <| fun _ ->
              let expected =
                  [| 2UL; 3UL; 5UL; 7UL; 11UL; 13UL; 17UL; 19UL
                     23UL; 29UL; 31UL; 37UL; 41UL; 43UL; 47UL; 53UL |]

              let sim = Sim blockRomLookup.def

              for address, value in Array.indexed expected do
                  sim.Poke("index", uint64 address)
                  sim.Tick()
                  Expect.equal (sim.Peek "prime") value $"Wrong prime at address {address}"

          testCase "block ROM contents and storage intent reach Verilog" <| fun _ ->
              let verilog = emitDesign blockRomLookup.def
              Expect.stringContains verilog "primes[15] = 16'd53" "The final ROM value should be initialized"
              Expect.stringContains verilog "(* ram_style = \"block\" *)" "The ROM should request block storage"

          testCase "byte strobes preserve untouched lanes" <| fun _ ->
              let sim = Sim maskedWrite.def
              let random = System.Random 606
              let model = Array.zeroCreate<uint64> 8
              let mutable pending = None
              let mutable checks = 0

              for cycle in 1..6000 do
                  let writeAddress = random.Next 8
                  let writeData = uint64 (random.Next()) &&& 0xFFFFFFFFUL
                  let strobe = random.Next 16
                  let writeEnabled = random.Next 4 <> 0
                  let readAddress = random.Next 8

                  sim.Poke("waddr", uint64 writeAddress)
                  sim.Poke("wdata", writeData)
                  sim.Poke("wstrb", uint64 strobe)
                  sim.Poke("wen", if writeEnabled then 1UL else 0UL)
                  sim.Poke("raddr", uint64 readAddress)

                  match pending with
                  | Some (address, expected) ->
                      checks <- checks + 1
                      Expect.equal (sim.Peek "rdata") expected $"Read-first result differs at cycle {cycle}, address {address}"
                  | None -> ()

                  pending <- Some(readAddress, model[readAddress])

                  if writeEnabled then
                      let mutable word = model[writeAddress]

                      for lane in 0..3 do
                          if (strobe >>> lane) &&& 1 = 1 then
                              let keep = 0xFFUL <<< (lane * 8)
                              word <- (writeData &&& keep) ||| (word &&& ~~~keep)

                      model[writeAddress] <- word

                  sim.Tick()

              Expect.isGreaterThan checks 5000 "The randomized run should exercise enough completed reads"

          testCase "wide strobes preserve untouched lanes" <| fun _ ->
              let sim = Sim maskedWriteWide.def
              let random = System.Random 128128
              let model = Array.create 8 BigInteger.Zero
              let laneMask = (BigInteger.One <<< 32) - BigInteger.One
              let mutable pending = None
              let mutable checks = 0

              for cycle in 1..4000 do
                  let writeAddress = random.Next 8

                  let writeData =
                      [ 0..3 ]
                      |> List.fold
                          (fun word lane -> word ||| (BigInteger(uint64 (random.Next()) &&& 0xFFFFFFFFUL) <<< (lane * 32)))
                          BigInteger.Zero

                  let strobe = random.Next 16
                  let writeEnabled = random.Next 4 <> 0
                  let readAddress = random.Next 8

                  sim.Poke("waddr", uint64 writeAddress)
                  sim.PokeWide("wdata", writeData)
                  sim.Poke("wstrb", uint64 strobe)
                  sim.Poke("wen", if writeEnabled then 1UL else 0UL)
                  sim.Poke("raddr", uint64 readAddress)

                  match pending with
                  | Some (address, expected) ->
                      checks <- checks + 1
                      Expect.equal (sim.PeekWide "rdata") expected $"Wide read-first result differs at cycle {cycle}, address {address}"
                  | None -> ()

                  pending <- Some(readAddress, model[readAddress])

                  if writeEnabled then
                      let mutable word = model[writeAddress]

                      for lane in 0..3 do
                          if (strobe >>> lane) &&& 1 = 1 then
                              let keep = laneMask <<< (lane * 32)
                              word <- (writeData &&& keep) ||| (word &&& (BigInteger.MinusOne ^^^ keep))

                      model[writeAddress] <- word

                  sim.Tick()

              Expect.isGreaterThan checks 3500 "The randomized run should exercise enough completed wide reads"

          testCase "last enabled write site wins" <| fun _ ->
              let verilog = emitDesign priorityWrite.def
              Expect.equal (verilog.Split("store[").Length - 1) 2 "Multiple source writes should fold to one emitted write site"

              let sim = Sim priorityWrite.def
              let random = System.Random 3131
              let model = Array.zeroCreate<uint64> 8
              let mutable allThree = 0

              for cycle in 1..4000 do
                  let address = random.Next 8
                  let readAddress = random.Next 8
                  let low = random.Next 2 = 1
                  let middle = random.Next 2 = 1
                  let high = random.Next 2 = 1

                  sim.Poke("addr", uint64 address)
                  sim.Poke("raddr", uint64 readAddress)
                  sim.Poke("low_enable", if low then 1UL else 0UL)
                  sim.Poke("mid_enable", if middle then 1UL else 0UL)
                  sim.Poke("high_enable", if high then 1UL else 0UL)

                  Expect.equal (sim.Peek "word") model[readAddress] $"Combinational read differs at cycle {cycle}, address {readAddress}"

                  if high then model[(address + 2) % 8] <- 0x33UL
                  elif middle then model[(address + 1) % 8] <- 0x22UL
                  elif low then model[address] <- 0x11UL

                  if low && middle && high then allThree <- allThree + 1
                  sim.Tick()

              Expect.isGreaterThan allThree 300 "The randomized run should exercise overlapping writes"

          testCase "masks do not merge across write sites" <| fun _ ->
              let sim = Sim maskedWritePriority.def
              let random = System.Random 7272
              let model = Array.zeroCreate<uint64> 8
              let mutable pending = None
              let mutable checks = 0
              let mutable bothFired = 0

              let apply address data lanes =
                  let keep = List.fold (fun mask lane -> mask ||| (0xFFUL <<< (lane * 8))) 0UL lanes
                  model[address] <- (data &&& keep) ||| (model[address] &&& ~~~keep)

              for cycle in 1..6000 do
                  let address = random.Next 8
                  let readAddress = random.Next 8
                  let lowData = uint64 (random.Next()) &&& 0xFFFFFFFFUL
                  let highData = uint64 (random.Next()) &&& 0xFFFFFFFFUL
                  let low = random.Next 2 = 1
                  let high = random.Next 2 = 1

                  sim.Poke("addr", uint64 address)
                  sim.Poke("raddr", uint64 readAddress)
                  sim.Poke("low_data", lowData)
                  sim.Poke("high_data", highData)
                  sim.Poke("low_enable", if low then 1UL else 0UL)
                  sim.Poke("high_enable", if high then 1UL else 0UL)

                  match pending with
                  | Some (pendingAddress, expected) ->
                      checks <- checks + 1
                      Expect.equal (sim.Peek "rdata") expected $"Priority masked read differs at cycle {cycle}, address {pendingAddress}"
                  | None -> ()

                  pending <- Some(readAddress, model[readAddress])

                  if high then apply address highData [ 2; 3 ]
                  elif low then apply address lowData [ 0; 1 ]

                  if low && high then bothFired <- bothFired + 1
                  sim.Tick()

              Expect.isGreaterThan checks 5000 "The randomized run should exercise enough completed reads"
              Expect.isGreaterThan bothFired 1000 "The randomized run should exercise both writes together"

          testCase "independent memory reads keep their own addresses" <| fun _ ->
              let sim = Sim dualRead.def
              let random = System.Random 4242
              let model = Array.zeroCreate<uint64> 8
              let mutable pending = None
              let mutable distinctAddresses = 0

              for cycle in 1..4000 do
                  let writeAddress = random.Next 8
                  let writeData = uint64 (random.Next 256)
                  let writeEnabled = random.Next 4 <> 0
                  let addressA = random.Next 8
                  let addressB = random.Next 8

                  sim.Poke("waddr", uint64 writeAddress)
                  sim.Poke("wdata", writeData)
                  sim.Poke("wen", if writeEnabled then 1UL else 0UL)
                  sim.Poke("addr_a", uint64 addressA)
                  sim.Poke("addr_b", uint64 addressB)

                  Expect.equal (sim.Peek "now_a") model[addressA] $"Combinational port A differs at cycle {cycle}"
                  Expect.equal (sim.Peek "now_b") model[addressB] $"Combinational port B differs at cycle {cycle}"

                  match pending with
                  | Some (expectedA, expectedB) ->
                      Expect.equal (sim.Peek "next_a") expectedA $"Synchronous port A differs at cycle {cycle}"
                      Expect.equal (sim.Peek "next_b") expectedB $"Synchronous port B differs at cycle {cycle}"
                  | None -> ()

                  pending <- Some(model[addressA], model[addressB])
                  if addressA <> addressB then distinctAddresses <- distinctAddresses + 1
                  if writeEnabled then model[writeAddress] <- writeData
                  sim.Tick()

              Expect.isGreaterThan distinctAddresses 3000 "The randomized run should usually drive the ports apart"

          testCase "synchronous reads carry request context" <| fun _ ->
              let sim = Sim carriedRead.def
              let random = System.Random 20250818
              let model = Array.zeroCreate<uint64> 16
              let inFlight = Queue<uint64 * uint64>()
              let mutable answered = 0

              for cycle in 1..8000 do
                  let writeAddress = random.Next 16
                  let writeData = uint64 (random.Next 256)
                  let writeEnabled = random.Next 3 = 0
                  let readAddress = random.Next 16
                  let tag = uint64 (random.Next 256)
                  let ask = random.Next 2 = 0

                  sim.Poke("waddr", uint64 writeAddress)
                  sim.Poke("wdata", writeData)
                  sim.Poke("wen", if writeEnabled then 1UL else 0UL)
                  sim.Poke("raddr", uint64 readAddress)
                  sim.Poke("tag", tag)
                  sim.Poke("ask", if ask then 1UL else 0UL)

                  if sim.Peek "answered" = 1UL then
                      answered <- answered + 1
                      Expect.isGreaterThan inFlight.Count 0 $"Cycle {cycle} produced an answer without a request"
                      let expectedWord, expectedTag = inFlight.Dequeue()
                      Expect.equal (sim.Peek "data") expectedWord $"Read data lost request alignment at cycle {cycle}"
                      Expect.equal (sim.Peek "tag_out") expectedTag $"Read tag lost request alignment at cycle {cycle}"

                  if ask then inFlight.Enqueue(model[readAddress], tag)
                  if writeEnabled then model[writeAddress] <- writeData
                  sim.Tick()

              Expect.isGreaterThan answered 3000 "The randomized run should complete enough read requests"

          testCase "AXI windows answer only their own address ranges" <| fun _ ->
              let sim = Sim twoWindowSlave.def

              for _ in 1..8 do
                  sim.Tick()

              let axi = SimAxi.client sim
              axi.write32 0x00UL 0xDEADBEEFUL
              Expect.equal (axi.read32 0x00UL) 0xDEADBEEFUL "The scratch register should retain its value"
              Expect.equal (axi.read32 0x04UL) 0x11FA57UL "The constant register should retain its value"

              for index in 0..3 do
                  let address = 0x10UL + uint64 (index * 4)
                  Expect.equal (axi.read32 address) (uint64 (index * 4)) $"Even window returned the wrong word at 0x{address:X}"

              for index in 0..3 do
                  let address = 0x20UL + uint64 (index * 4)
                  let expected = uint64 (0xA0 + index * 4 + 1)
                  Expect.equal (axi.read32 address) expected $"Odd window returned the wrong word at 0x{address:X}" ]
