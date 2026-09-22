module Warp11.Designs.Tests.UtilityTests

open Expecto
open Warp11
open Warp11.Designs

let private lfsrTests =
    testList
        "LFSR"
        [ testCase "advertised small LFSRs have maximal periods" <| fun _ ->
              for width in lfsrTaps.Keys |> Seq.filter (fun width -> width <= 16) do
                  let seed = 1UL
                  let mutable state = seed
                  let mutable steps = 0UL
                  let limit = 1UL <<< width

                  while steps < limit && (steps = 0UL || state <> seed) do
                      state <- lfsrNext width state
                      steps <- steps + 1UL

                  Expect.notEqual state 0UL $"The {width}-bit LFSR should never enter the zero trap"
                  Expect.equal steps (limit - 1UL) $"The {width}-bit LFSR should visit every nonzero state"

          testCase "hardware LFSR holds and follows software sequence" <| fun _ ->
              let sim = Sim lfsrSource.def
              let mutable expected = 1UL
              sim.Poke("step", 0UL)
              sim.Tick()
              Expect.equal (sim.Peek "state") 1UL "A disabled LFSR should hold its seed"
              sim.Poke("step", 1UL)

              for cycle in 1..600 do
                  sim.Tick()
                  expected <- lfsrNext 9 expected
                  Expect.equal (sim.Peek "state") expected $"Hardware and software should agree at cycle {cycle}" ]

let private selectionTests =
    testList
        "One-hot selection"
        [ testCase "oneHotLowest grants only the lowest requester" <| fun _ ->
              let sim = Sim oneHotScan.def

              for pattern in 0..15 do
                  for index in 0..3 do
                      sim.Poke($"request{index}", uint64 ((pattern >>> index) &&& 1))

                  sim.Tick()
                  let lowest = [ 0..3 ] |> List.tryFind (fun index -> (pattern >>> index) &&& 1 = 1)

                  for index in 0..3 do
                      Expect.equal
                          (sim.Peek $"grant{index}")
                          (if Some index = lowest then 1UL else 0UL)
                          $"Pattern {pattern} should grant only its lowest set bit"

          testCase "mux1H selects the granted value and defaults to zero" <| fun _ ->
              let sim = Sim mux1HSelect.def
              for index in 0..3 do sim.Poke($"value{index}", uint64 (0x10 * (index + 1)))

              for winner in 0..3 do
                  for index in 0..3 do sim.Poke($"request{index}", if index >= winner then 1UL else 0UL)
                  sim.Tick()
                  Expect.equal (sim.Peek "winner") (uint64 (0x10 * (winner + 1))) $"Requester {winner} should win"

              for index in 0..3 do sim.Poke($"request{index}", 0UL)
              sim.Tick()
              Expect.equal (sim.Peek "winner") 0UL "No selected input should produce zero" ]

let private edgeAndFlowTests =
    testList
        "Edges and flows"
        [ testCase "edge detector reports enabled and pending changes" <| fun _ ->
              let sim = Sim edgeDetector.def

              let cycle enable signal expected =
                  sim.Poke("enable", enable)
                  sim.Poke("signal", signal)
                  Expect.equal (sim.Peek "rising", sim.Peek "falling", sim.Peek "changed") expected $"Unexpected edge for enable={enable}, signal={signal}"
                  sim.Tick()

              cycle 1UL 0UL (0UL, 0UL, 0UL)
              cycle 1UL 1UL (1UL, 0UL, 1UL)
              cycle 1UL 1UL (0UL, 0UL, 0UL)
              cycle 1UL 0UL (0UL, 1UL, 1UL)
              cycle 0UL 1UL (1UL, 0UL, 1UL)
              cycle 0UL 1UL (1UL, 0UL, 1UL)
              cycle 1UL 1UL (1UL, 0UL, 1UL)
              cycle 1UL 1UL (0UL, 0UL, 0UL)

          testCase "flow sampler counts exactly the dropped beats" <| fun _ ->
              let sim = Sim flowSampler.def
              let cycle sample ready =
                  sim.Poke("sample", sample)
                  sim.Poke("out_ready", ready)
                  sim.Tick()

              cycle 1UL 0UL
              let initial = sim.Peek "dropped"
              for _ in 1..10 do cycle 1UL 0UL
              let refused = sim.Peek "dropped"
              Expect.equal refused (initial + 10UL) "Each refused valid beat should be counted"

              for _ in 1..10 do cycle 1UL 1UL
              let taken = sim.Peek "dropped"
              Expect.equal taken refused "Accepted beats should not increment the drop count"
              Expect.equal (sim.Peek "out_valid") 1UL "Accepted flow should remain valid"

              cycle 0UL 0UL
              cycle 0UL 0UL
              Expect.equal (sim.Peek "dropped") (taken + 1UL) "The registered final beat should drain once"
              cycle 0UL 0UL
              Expect.equal (sim.Peek "dropped") (taken + 1UL) "Idle cycles should not be counted"
              Expect.equal (sim.Peek "out_valid") 0UL "The flow should become idle after draining" ]

let private counterTests =
    testCase "fixed and runtime counters wrap at their documented bounds" <| fun _ ->
        let sim = Sim dividers.def
        let period = 6

        let cycle enable last =
            sim.Poke("enable", enable)
            sim.Poke("last", last)
            let observed = sim.Peek "count", sim.Peek "wrap", sim.Peek "window_count", sim.Peek "window_wrap"
            sim.Tick()
            observed

        let observed = [ for _ in 1 .. period * 3 -> cycle 1UL 3UL ]
        for index, (count, wrap, windowCount, windowWrap) in List.indexed observed do
            Expect.equal count (uint64 (index % period)) $"Fixed counter value at cycle {index}"
            Expect.equal wrap (if index % period = period - 1 then 1UL else 0UL) $"Fixed counter wrap at cycle {index}"
            Expect.equal windowCount (uint64 (index % 4)) $"Runtime counter value at cycle {index}"
            Expect.equal windowWrap (if index % 4 = 3 then 1UL else 0UL) $"Runtime counter wrap at cycle {index}"

        Expect.equal (observed |> List.sumBy (fun (_, wrap, _, _) -> int wrap)) 3 "Three periods should produce three wraps"
        let held = cycle 0UL 3UL
        Expect.equal (cycle 0UL 3UL) held "Disabled counters should hold"
        let _, wrap, _, _ = held
        Expect.equal wrap 0UL "Disabled counters should not assert wrap"

        let flipping = Sim dividers.def
        flipping.Poke("enable", 1UL)
        flipping.Poke("last", 3UL)
        let before = flipping.Peek "divided"
        for _ in 1..period do flipping.Tick()
        let after = flipping.Peek "divided"
        for _ in 1..period do flipping.Tick()
        Expect.notEqual after before "The divider should flip once per period"
        Expect.equal (flipping.Peek "divided") before "Two periods should return to the original phase"

let private bitShapeTests =
    testCase "bit-shape operations match their definitions exhaustively" <| fun _ ->
        let sim = Sim bitShapes.def
        let bits width value = [ for index in 0 .. width - 1 -> (value >>> index) % 2UL ]

        for a in 0UL..15UL do
            for b in 0UL..15UL do
                sim.Poke("a", a)
                sim.Poke("b", b)
                sim.Poke("flag", a % 2UL)
                sim.Poke("index", a % 4UL)
                sim.Tick()
                Expect.equal (sim.Peek "joined") ((a <<< 4) + b) $"catAll should concatenate {a} and {b}"
                Expect.equal (sim.Peek "mask") (if a % 2UL = 1UL then 15UL else 0UL) "replicate should fill every bit"
                Expect.equal (sim.Peek "flipped") (bits 4 a |> List.mapi (fun index bit -> bit <<< (3 - index)) |> List.sum) "reverseBits should reverse position"
                Expect.equal (sim.Peek "ones") (bits 4 a |> List.sum) "popCount should count set bits"

                for index in 0..3 do
                    Expect.equal (sim.Peek $"hot{index}") (if uint64 index = a % 4UL then 1UL else 0UL) "uintToOneHot should select one position"

                Expect.equal (sim.Peek "recovered") (a % 4UL) "one-hot round trip should recover the index"

let private divide (sim: Sim) a b =
    sim.Poke("dividend", a)
    sim.Poke("divisor", b)
    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 1UL)
    while sim.Peek "in_ready" <> 1UL do sim.Tick()
    sim.Tick()
    sim.Poke("in_valid", 0UL)
    let mutable refused = true
    while sim.Peek "out_valid" <> 1UL do
        if sim.Peek "in_ready" = 1UL then refused <- false
        sim.Tick()
    let result = sim.Peek "quotient", sim.Peek "remainder", refused
    sim.Tick()
    result

let private dividerTests =
    testList
        "Stream divider"
        [ testCase "divider computes quotient and remainder while refusing new work" <| fun _ ->
              let sim = Sim streamDivider.def
              for dividend in 0UL..255UL do
                  for divisor in [ 1UL; 2UL; 3UL; 7UL; 16UL; 100UL; 255UL ] do
                      let quotient, remainder, refused = divide sim dividend divisor
                      Expect.equal quotient (dividend / divisor) $"Wrong quotient for {dividend}/{divisor}"
                      Expect.equal remainder (dividend % divisor) $"Wrong remainder for {dividend}/{divisor}"
                      Expect.isTrue refused $"Divider should refuse new work while dividing {dividend}/{divisor}"

          testCase "divider holds a completed result under backpressure" <| fun _ ->
              let sim = Sim streamDivider.def
              sim.Poke("dividend", 100UL)
              sim.Poke("divisor", 7UL)
              sim.Poke("in_valid", 1UL)
              sim.Poke("out_ready", 0UL)
              while sim.Peek "in_ready" <> 1UL do sim.Tick()
              sim.Tick()
              sim.Poke("in_valid", 0UL)
              while sim.Peek "out_valid" <> 1UL do sim.Tick()

              for cycle in 1..20 do
                  sim.Tick()
                  Expect.equal (sim.Peek "out_valid") 1UL $"Result should remain valid at held cycle {cycle}"
                  Expect.equal (sim.Peek "quotient") 14UL $"Result should remain unchanged at held cycle {cycle}"

          testCase "division by zero returns an all-ones quotient" <| fun _ ->
              let quotient, _, _ = divide (Sim streamDivider.def) 42UL 0UL
              Expect.equal quotient 255UL "Eight-bit division by zero should saturate to all ones" ]

let private valueTests =
    testList
        "Value operations"
        [ testCase "saturate and shifts match software semantics" <| fun _ ->
              let sim = Sim satOps.def
              let clampSigned value =
                  let signed = if value >= 128UL then int64 value - 256L else int64 value
                  uint64 (max -8L (min 7L signed)) &&& 0xFUL

              for a, b in Seq.allPairs [ 0UL; 7UL; 8UL; 15UL; 16UL; 127UL; 128UL; 200UL; 255UL ] [ 0UL; 1UL; 100UL; 255UL ] do
                  sim.Poke("a", a)
                  sim.Poke("b", b)
                  sim.Tick()
                  Expect.equal (sim.Peek "narrow_u") (min a 15UL) $"Unsigned saturation for {a}"
                  Expect.equal (sim.Peek "narrow_s") (clampSigned a) $"Signed saturation for {a}"
                  Expect.equal (sim.Peek "sum_u") (min (a + b) 255UL) $"Saturating sum for {a}+{b}"
                  Expect.equal (sim.Peek "shifted") (a * 16UL) $"Left shift for {a}"
                  Expect.equal (sim.Peek "high") (a / 8UL) $"Right slice for {a}"

          testCase "transporter preserves ragged fields" <| fun _ ->
              let sim = Sim transporterRoundTrip.def
              for a, b, c, d in [ 0UL, 0UL, 0UL, 0UL; 31UL, 0xFFFFFFFFUL, 127UL, 0xFFFFFFFFUL; 21UL, 0xDEADBEEFUL, 85UL, 0xCAFEF00DUL ] do
                  sim.Poke("a", a)
                  sim.Poke("b", b)
                  sim.Poke("c", c)
                  sim.Poke("d", d)
                  sim.Tick()
                  Expect.equal (sim.Peek "outA") a "Field A should round-trip"
                  Expect.equal (sim.Peek "outB") b "Field B should round-trip"
                  Expect.equal (sim.Peek "outC") c "Field C should round-trip"
                  Expect.equal (sim.Peek "outD") d "Field D should round-trip" ]

let tests =
    testList
        "Utility primitives"
        [ lfsrTests
          selectionTests
          edgeAndFlowTests
          counterTests
          bitShapeTests
          dividerTests
          valueTests ]
