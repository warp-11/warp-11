module Warp11.Designs.Tests.DebugTests

open System
open System.Numerics
open System.Threading
open Expecto
open Warp11
open Warp11.Designs

let private nestedDelayOf width =
    liftUnary (
        stateModule1 $"DoubleDelay%d{width}" ("d", width) ("q", width) (fun input ->
            let stage = delayOf width
            stage (stage input))
    )

let private nestedGroups =
    defModule "NestedGroups" (fun ports -> ports.inPort "x" 8, ports.outPort "out" 8) (fun (input, output) ->
        nestedDelayOf 8 input ==> output)

let private waitUntil description predicate =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while not (predicate ()) && DateTime.UtcNow <= deadline do
        Thread.Sleep 5

    Expect.isTrue (predicate ()) description

let private compileBreakpoint sim text =
    match Breakpoint.compile sim text with
    | Ok breakpoint -> breakpoint
    | Error error -> failtestf "Breakpoint '%s' did not compile: %A" text error

let private single description items =
    match items with
    | [ item ] -> item
    | _ -> failtestf "%s: expected one item, got %d" description (List.length items)

let private assertCounterHit text expected =
    let sim = Sim counterMutable
    sim.Poke("enable", 1UL)
    let actual = Breakpoint.runUntil sim (compileBreakpoint sim text).isHit 40
    Expect.equal actual expected $"Breakpoint '{text}' should fire at the expected cycle"

let private comparisonHits a b text =
    let sim = Sim comparator8.def
    sim.Poke("a", a)
    sim.Poke("b", b)
    (compileBreakpoint sim text).isHit ()

let private memoryHits address text =
    let sim = Sim ramTest.def
    sim.Poke("waddr", 3UL)
    sim.Poke("wdata", 0x55UL)
    sim.Poke("wen", 1UL)
    sim.Tick()
    sim.Poke("wen", 0UL)
    Expect.equal ((compileBreakpoint sim text).isHit ()) address $"Memory breakpoint '{text}' returned the wrong result"

let private wideHits text =
    let sim = Sim wideBeat.def
    sim.Poke("shift_en", 1UL)

    for value in 1UL..3UL do
        sim.Poke("byte_in", value)
        sim.Tick()

    (compileBreakpoint sim text).isHit ()

let private inventoryTests =
    testList
        "Inventory"
        [ testCase "every inventory name can be peeked" <| fun _ ->
              let designs =
                  [ add3
                    counterMutable
                    comparator8.def
                    nestedGroups.def
                    ramTest.def
                    assertedSaturate.def
                    wideBeat.def
                    i2sLinkPassthru.def ]

              for design in designs do
                  let sim = Sim design
                  let inventory = Inventory.ofDesign design

                  for signal in inventory.signals do
                      if signal.width > 64 then
                          sim.PeekWide signal.name |> ignore
                      else
                          sim.Peek signal.name |> ignore

                  for memory in inventory.mems do
                      let lastAddress = (1 <<< memory.addrWidth) - 1

                      if memory.wordWidth > 64 then
                          sim.PeekMemWide(memory.name, lastAddress) |> ignore
                      else
                          sim.PeekMem(memory.name, lastAddress) |> ignore

          testCase "inventory groups use the longest instance prefix" <| fun _ ->
              let flat = Inventory.ofDesign add3
              let nested = Inventory.ofDesign nestedGroups.def
              let ram = Inventory.ofDesign ramTest.def

              Expect.equal flat.groups [ ""; "a1_"; "a2_" ] "Add3 should group signals by its two child instances"

              Expect.isTrue
                  (nested.groups
                   |> List.exists (fun outer ->
                       outer <> ""
                       && nested.groups
                          |> List.exists (fun inner -> inner.Length > outer.Length && inner.StartsWith outer)))
                  "Nested instances should retain both prefix levels"

              for signal in nested.signals do
                  Expect.contains nested.groups signal.group $"Signal {signal.name} should refer to an existing group"

              Expect.equal
                  (ram.mems |> List.map (fun memory -> memory.name, memory.addrWidth, memory.wordWidth))
                  [ "store", 3, 8 ]
                  "RAM inventory metadata should preserve address and word widths" ]

let private breakpointTests =
    testList
        "Breakpoints"
        [ testCase "counter expressions fire on exact cycles" <| fun _ ->
              [ "count == 10", (10, true)
                "count == 0xa", (10, true)
                "count == 0b1010", (10, true)
                "count == 3 && enable == 1", (3, true)
                "count == 3 && enable == 0", (40, false)
                "count > 200", (40, false)
                "count != 0 && !(count < 7)", (7, true)
                "count[0] == 1 && count > 4", (5, true)
                "count[3:2] == 0b11", (12, true)
                "count >> 1 == 5", (10, true) ]
              |> List.iter (fun (text, expected) -> assertCounterHit text expected)

          testCase "signed comparisons and shifts retain their reading" <| fun _ ->
              Expect.isTrue (comparisonHits 200UL 10UL "signed(a) < 0") "200 should be negative as an 8-bit signed value"
              Expect.isTrue (comparisonHits 200UL 10UL "signed(a) < signed(b)") "Signed comparison should see -56 < 10"
              Expect.isFalse (comparisonHits 200UL 10UL "a < b") "Unsigned comparison should see 200 >= 10"
              Expect.isTrue (comparisonHits 200UL 10UL "signed(a) >> 2 == 0xf2") "Signed shift should extend the sign"
              Expect.isTrue (comparisonHits 200UL 10UL "a >> 2 == 50") "Unsigned shift should fill with zeroes"
              Expect.isTrue (comparisonHits 200UL 10UL "larger == 200 && greater == 1") "Derived outputs should be readable"
              Expect.isTrue (comparisonHits 7UL 7UL "equal == 1 && !less && !greater") "Boolean negation should compose"

          testCase "memory and wide expressions read current simulator state" <| fun _ ->
              memoryHits true "store[3] == 0x55"
              memoryHits false "store[2] == 0x55"
              memoryHits true "store[3] + 1 == 0x56"
              Expect.isTrue (wideHits "beat[23:0] == 0x010203") "Wide slices should read shifted bytes"
              Expect.isTrue (wideHits "beat == 0x010203") "Wide values should compare directly"
              Expect.isFalse (wideHits "beat == 0x010204") "A different wide value should not match"
              Expect.isTrue (wideHits "beat[7:0] == 3 && beat[15:8] == 2") "Multiple slices should compose"

          testCase "invalid breakpoint expressions are rejected" <| fun _ ->
              for text in [ "count == "; "nosuchsignal == 1"; "count 5"; "count[99] == 1"; "count << r"; "" ] do
                  Expect.isError (Breakpoint.compile (Sim counterMutable) text) $"Breakpoint '{text}' should be rejected" ]

let private sessionTests =
    testList
        "Debug session"
        [ testCase "commands drive watches, breakpoints, and reset" <| fun _ ->
              use raw = new Debug.DebugSession(counterMutable)
              let session = raw :> Debug.IDebugSession
              session.Poke("enable", BigInteger.One)
              session.Watch "count"
              session.Step 10
              waitUntil "The session should finish ten steps" (fun () -> session.Latest.cycle = 10 && not session.Latest.running)

              Expect.exists session.Latest.values (fun value -> value.name = "count" && value.value = BigInteger 10) "The watch should follow the counter"
              Expect.isOk (session.AddBreakpoint "count == 20") "The breakpoint should compile"
              session.Run()
              waitUntil "The session should stop at count 20" (fun () -> session.Latest.hit = Some "count == 20")
              Expect.equal session.Latest.cycle 20 "The breakpoint should stop on its exact cycle"
              Expect.equal session.Latest.breakpoints [ { text = "count == 20"; enabled = true; hits = 1 } ] "The hit count should update"

              session.EnableBreakpoint("count == 20", false)
              session.Step 15
              waitUntil "A disabled breakpoint should not stop the run" (fun () -> session.Latest.cycle = 35)
              session.Reset()
              waitUntil "Reset should restore cycle zero and clear the hit" (fun () -> session.Latest.cycle = 0 && session.Latest.hit.IsNone)
              Expect.isError (session.AddBreakpoint "nosuchsignal == 1") "Unknown signals should be rejected"

          testCase "memory views update, clamp, and clear" <| fun _ ->
              use raw = new Debug.DebugSession(ramTest.def)
              let session = raw :> Debug.IDebugSession

              let wordsAre (expected: uint64 list) =
                  match session.Latest.memory with
                  | Some view -> view.start = 0 && List.ofArray view.words = List.map (fun (value: uint64) -> BigInteger value) expected
                  | None -> false

              session.ViewMemory("store", 0, 8)
              waitUntil "The memory window should appear" (fun () -> session.Latest.memory.IsSome)
              Expect.isTrue (wordsAre [ 0UL; 0UL; 0UL; 0UL; 0UL; 0UL; 0UL; 0UL ]) "RAM should start empty"

              session.Poke("waddr", BigInteger 3)
              session.Poke("wdata", BigInteger 0x55)
              session.Poke("wen", BigInteger.One)
              session.Step 1
              session.Poke("wen", BigInteger.Zero)
              waitUntil "The memory view should show the landed write" (fun () -> wordsAre [ 0UL; 0UL; 0UL; 0x55UL; 0UL; 0UL; 0UL; 0UL ])
              session.Step 3
              waitUntil "The session should advance without repeating the write" (fun () -> session.Latest.cycle = 4)
              Expect.isTrue (wordsAre [ 0UL; 0UL; 0UL; 0x55UL; 0UL; 0UL; 0UL; 0UL ]) "Exactly one write should land"

              session.ViewMemory("store", 6, 8)
              waitUntil "A window beyond the end should clamp to the tail" (fun () ->
                  match session.Latest.memory with
                  | Some view -> view.start = 6 && view.words.Length = 2
                  | None -> false)
              session.ClearMemoryView()
              waitUntil "Clearing the memory view should remove it" (fun () -> session.Latest.memory.IsNone)

          testCase "trace records every cycle and renders VCD" <| fun _ ->
              use raw = new Debug.DebugSession(counterMutable)
              let session = raw :> Debug.IDebugSession
              session.Poke("enable", BigInteger.One)
              session.Watch "count"
              waitUntil "The counter watch should appear" (fun () -> session.Latest.values |> List.exists (fun value -> value.name = "count"))
              Expect.isOk (session.StartRecording false) "Recording should start"
              session.Step 40
              waitUntil "The trace should include all 41 samples" (fun () -> session.Latest.cycle = 40 && session.Latest.recorded >= 41)

              let trace = session.Trace()
              let counted = single "Only the watched counter should be recorded" trace.signals
              Expect.equal counted.name "count" "The trace should name the watched signal"
              Expect.sequenceEqual (Seq.truncate 41 counted.values) [ 0UL..40UL ] "The trace should contain every cycle"

              let vcd = Vcd.render "counter" trace
              Expect.stringContains vcd "$enddefinitions $end" "VCD should close its definitions"
              Expect.stringContains vcd "$var wire 8 " "VCD should retain the counter width"
              Expect.stringContains vcd "count $end" "VCD should retain the counter name"
              Expect.isGreaterThanOrEqual (vcd.Split('\n') |> Array.filter (fun line -> line.StartsWith "#") |> Array.length) 41 "VCD should timestamp every changed cycle"

              session.StopRecording()
              waitUntil "Recording should stop" (fun () -> not session.Latest.recording)
              let finalTrace = session.Trace()
              let finalCounter = single "The stopped trace should retain the watched counter" finalTrace.signals
              Expect.equal finalCounter.values[finalCounter.values.Length - 1] (uint64 session.Latest.cycle) "The final sample should match the current cycle"
              Expect.equal (finalTrace.firstCycle + finalCounter.values.Length - 1) session.Latest.cycle "The trace range should end at the current cycle"

          testCase "attached I2S device advances with a pumped session" <| fun _ ->
              let input = toneWav 48_000 8 440.0 0.5
              let source = WavI2sSource(sharedBusSimPins, input)
              use raw = new Debug.DebugSession(i2sLinkPassthru.def, ownThread = false, devices = [ source.Attach ])
              let session = raw :> Debug.IDebugSession
              let preRoll = 2
              session.Step((input.FrameCount + preRoll + 2) * 2048)

              let mutable pumps = 0
              while not (session.Pump()) && pumps < 10_000_000 do
                  pumps <- pumps + 1

              Expect.isLessThan pumps 10_000_000 "The pumped session should complete before the guard"
              let output = Expect.wantSome source.Output "The attached source should produce a recording"
              Expect.isGreaterThanOrEqual output.FrameCount (input.FrameCount + preRoll) "The output should include pre-roll and every input frame"

              for frame in 0 .. input.FrameCount - 1 do
                  Expect.equal output.samples[(frame + preRoll) * 2] input.samples[frame * 2] $"Left sample {frame} should survive"
                  Expect.equal output.samples[(frame + preRoll) * 2 + 1] input.samples[frame * 2 + 1] $"Right sample {frame} should survive" ]

let private assertionTests =
    testList
        "Assertions"
        [ testCase "valid and untaken assertions stay quiet" <| fun _ ->
              let run pokes cycles =
                  let sim = Sim(assertedCounter.def, checkAsserts = true)
                  for name, value in pokes do sim.Poke(name, value)
                  for _ in 1..cycles do sim.Tick()
                  sim.Violations

              Expect.isEmpty (run [ "enable", 1UL ] 400) "Saturating normally should satisfy both assertions"
              Expect.isEmpty (run [ "hold", 1UL ] 20) "An implication should be vacuous when its condition is false"

          testCase "assertions report their exact violation cycles" <| fun _ ->
              let sim = Sim(assertedCounter.def, checkAsserts = true)
              sim.Poke("enable", 1UL)
              sim.Poke("hold", 1UL)
              for _ in 1..3 do sim.Tick()
              Expect.equal (sim.Violations |> List.map snd) [ 1; 2; 3 ] "The conditional assertion should fire every violating cycle"
              Expect.all (sim.Violations |> List.map fst) (fun message -> message.Contains "asserted together") "Each violation should identify the controls"

              let wrapped = Sim(assertedCounter.def, checkAsserts = true)
              wrapped.Poke("hold", 1UL)
              wrapped.Tick()
              wrapped.Poke("hold", 0UL)
              wrapped.Poke("wrap", 1UL)
              wrapped.Tick()
              let message, cycle = single "Wrapping should produce one violation" wrapped.Violations
              Expect.stringContains message "wrapped past" "The saturation assertion should identify wrapping"
              Expect.equal cycle 2 "The saturation assertion should fire on the wrap cycle"

          testCase "assertion checking can be disabled" <| fun _ ->
              let sim = Sim assertedCounter.def
              sim.Poke("hold", 1UL)
              sim.Tick()
              sim.Poke("hold", 0UL)
              sim.Poke("wrap", 1UL)
              sim.Tick()
              Expect.isEmpty sim.Violations "A simulator without assertion checking should record nothing" ]

let tests =
    testList "Debugger and assertions" [ inventoryTests; breakpointTests; sessionTests; assertionTests ]
