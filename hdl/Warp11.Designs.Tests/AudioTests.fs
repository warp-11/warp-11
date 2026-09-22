module Warp11.Designs.Tests.AudioTests

open Expecto
open Warp11
open Warp11.Designs

let private handWiredShared =
    defModule
        "I2sLinkPassthru"
        (fun ports -> ports.inPort "sd_in" 1, ports.outPort "bclk" 1, ports.outPort "ws" 1, ports.outPort "sd_out" 1)
        (fun (sdIn, bitClock, wordSelect, sdOut) ->
            let clocks = instanceNamed "i2s_clocks" (i2sMasterHz kv260.fabricHz 48_828 32 "I2sMaster")
            clocks.sclk ==> bitClock
            clocks.lrclk ==> wordSelect
            let received = i2sRx "I2sRx" "i2s_rx" clocks.sclkRxTick clocks.lrclk sdIn
            i2sTx "I2sTx" "i2s_tx" clocks.sclkTxTick clocks.lrclk received ==> sdOut)

let private handWiredCodec =
    defModule
        "I2sLinkCodec"
        (fun ports ->
            ports.inPort "sdout" 1,
            ports.outPort "mclk" 1,
            ports.outPort "sclk" 1,
            ports.outPort "lrclk" 1,
            ports.outPort "sdin" 1,
            ports.outPort "mclk2" 1,
            ports.outPort "sclk2" 1,
            ports.outPort "lrclk2" 1)
        (fun (sdout, mclk, sclk, lrclk, sdin, mclk2, sclk2, lrclk2) ->
            let clocks = instanceNamed "i2s_clocks" (i2sMasterHz kv260.fabricHz 48_828 32 "I2sMaster")
            clocks.mclk ==> mclk
            clocks.mclk ==> mclk2
            clocks.sclk ==> sclk
            clocks.sclk ==> sclk2
            clocks.lrclk ==> lrclk
            clocks.lrclk ==> lrclk2
            let received = i2sRx "I2sRx" "i2s_rx" clocks.sclkRxTick clocks.lrclk sdout
            i2sTx "I2sTx" "i2s_tx" clocks.sclkTxTick clocks.lrclk received ==> sdin)

let private dspTests =
    testList
        "Audio DSP"
        [ testCase "unity audio chain is transparent and mute silences" <| fun _ ->
              let sim = Sim audioChain.def
              sim.Poke("volume", gainUnity)
              sim.Poke("mute", 0UL)
              sim.Poke("threshold", 0UL)
              sim.Poke("ratio", 0UL)
              sim.Poke("attack", 1UL <<< 15)
              sim.Poke("releaseRate", 1UL <<< 15)
              sim.Poke("makeup", gainUnity)
              sim.Poke("limit", (1UL <<< (sampleWidth - 1)) - 1UL)
              sim.Poke("in_valid", 1UL)
              sim.Poke("out_ready", 1UL)
              let left = [ 1000UL; 2000UL; 4095UL; 7UL; 65535UL; 300UL ]
              let right = [ 11UL; 90210UL; 64UL; 123456UL; 5UL; 8191UL ]
              let seenLeft, seenRight = ResizeArray<uint64>(), ResizeArray<uint64>()
              for l, r in List.zip left right do
                  sim.Poke("in_left", l); sim.Poke("in_right", r); sim.Tick()
                  seenLeft.Add(sim.Peek "out_left"); seenRight.Add(sim.Peek "out_right")
              Expect.sequenceEqual seenLeft (Seq.truncate seenLeft.Count (0UL :: left)) "Left channel should pass with pipeline latency"
              Expect.sequenceEqual seenRight (Seq.truncate seenRight.Count (0UL :: right)) "Right channel should pass independently"
              sim.Poke("mute", 1UL)
              let muted = [ for l, r in List.zip left right do sim.Poke("in_left", l); sim.Poke("in_right", r); sim.Tick(); yield sim.Peek "out_left"; yield sim.Peek "out_right" ]
              Expect.all (List.skip 4 muted) ((=) 0UL) "Mute should silence settled output"

          testCase "RBJ cookbook has known endpoint responses" <| fun _ ->
              let fs = 48_000.0
              let response (design: BiquadDesign) atDc =
                  let sign = if atDc then 1.0 else -1.0
                  (design.b0 + sign * design.b1 + design.b2) / (1.0 + sign * design.a1 + design.a2)
              let close tolerance actual expected = Expect.isLessThan (abs (actual - expected)) tolerance "Response should match"
              let low = rbjDesign LowPass 1000.0 0.707 0.0 fs
              let high = rbjDesign HighPass 1000.0 0.707 0.0 fs
              let lowShelf = rbjDesign LowShelf 1000.0 0.707 12.0 fs
              let highShelf = rbjDesign HighShelf 1000.0 0.707 12.0 fs
              let flat = rbjDesign Peaking 1000.0 0.707 0.0 fs
              let gain = 10.0 ** (12.0 / 20.0)
              close 1e-9 (response low true) 1.0; close 1e-9 (response low false) 0.0
              close 1e-9 (response high true) 0.0; close 1e-9 (response high false) 1.0
              close 1e-6 (response lowShelf true) gain; close 1e-6 (response lowShelf false) 1.0
              close 1e-6 (response highShelf true) 1.0; close 1e-6 (response highShelf false) gain
              close 1e-12 flat.b0 1.0; close 1e-12 (flat.b1 - flat.a1) 0.0; close 1e-12 (flat.b2 - flat.a2) 0.0
              close 2e-4 (envelopeAlphaSeconds (envelopeAlphaQ15 0.010 fs) fs) 0.010
              Expect.equal (envelopeAlphaQ15 0.0 fs) 0x7FFFUL "Zero seconds should encode immediate response"

          testCase "multiband reconstruction is flat at DC" <| fun _ ->
              let sim = Sim multibandStage.def
              sim.Poke("threshold", 0UL); sim.Poke("ratio", 0UL); sim.Poke("attack", 1UL <<< 15); sim.Poke("releaseRate", 1UL <<< 15)
              for index in 0 .. multibandBands - 1 do sim.Poke($"lg{index}", gainUnity); sim.Poke($"rg{index}", gainUnity)
              sim.Poke("in_valid", 1UL); sim.Poke("out_ready", 1UL)
              let level = 200_000UL
              sim.Poke("in_left", level); sim.Poke("in_right", level / 2UL)
              for _ in 1..400 do sim.Tick()
              let signed value = if value >= (1UL <<< (sampleWidth - 1)) then int64 value - (1L <<< sampleWidth) else int64 value
              Expect.isLessThanOrEqual (abs (signed (sim.Peek "out_left") - int64 level)) 2048L "Left DC reconstruction should stay within fixed-point bias"
              Expect.isLessThanOrEqual (abs (signed (sim.Peek "out_right") - int64 (level / 2UL))) 2048L "Right DC reconstruction should stay within fixed-point bias"

          testCase "compressor regulates boosted output but leaves unity makeup alone" <| fun _ ->
              let sim = Sim audioChain.def
              let level, makeup = 500_000UL, 8UL
              sim.Poke("volume", gainUnity); sim.Poke("mute", 0UL); sim.Poke("threshold", 1_000_000UL); sim.Poke("ratio", 1UL)
              sim.Poke("attack", 1UL <<< 15); sim.Poke("releaseRate", 1UL <<< 15); sim.Poke("makeup", makeup * gainUnity)
              sim.Poke("limit", (1UL <<< (sampleWidth - 1)) - 1UL); sim.Poke("in_valid", 1UL); sim.Poke("out_ready", 1UL)
              sim.Poke("in_left", level); sim.Poke("in_right", level)
              for _ in 1..200 do sim.Tick()
              let regulated = sim.Peek "out_left"
              sim.Poke("makeup", gainUnity)
              for _ in 1..200 do sim.Tick()
              Expect.isGreaterThan regulated 0UL "Regulated output should remain nonzero"
              Expect.isLessThan regulated (level * makeup) "Regulated output should be below unregulated boost"
              Expect.equal (sim.Peek "out_left") level "Unity makeup below threshold should pass unchanged"

          testCase "FIR presets have known DC response" <| fun _ ->
              let settle preset level =
                  let sim = Sim audioFirStage.def
                  sim.Poke("preset", preset); sim.Poke("in_valid", 1UL); sim.Poke("out_ready", 1UL)
                  sim.Poke("in_left", level); sim.Poke("in_right", level)
                  for _ in 1..40 do sim.Tick()
                  sim.Peek "out_left"
              let signed value = if value >= (1UL <<< (sampleWidth - 1)) then int64 value - (1L <<< sampleWidth) else int64 value
              let level = 100_000UL
              Expect.isLessThanOrEqual (abs (signed (settle (uint64 presetBypass) level) - int64 level)) 4L "Bypass should pass DC"
              Expect.isLessThanOrEqual (abs (signed (settle (uint64 presetLowPass) level) - int64 level)) 4L "Low-pass should pass DC"
              Expect.isLessThanOrEqual (abs (signed (settle (uint64 presetHighPass) level))) 4L "High-pass should reject DC" ]

let private integrationTests =
    testList
        "Audio integration"
        [ testCase "SimI2s round-trips shared, codec, and processed links" <| fun _ ->
              let samples = [ 0xA5A5A0UL, 0x5A5A50UL; 0x123456UL, 0x654321UL; 0x111111UL, 0x222222UL ]
              let take values = List.truncate samples.Length (List.distinct values)
              Expect.equal (i2sExchange (Sim i2sLinkPassthru.def) sharedBusSimPins samples |> take) samples "Shared-bus codec should round-trip"
              Expect.equal (i2sExchange (Sim i2sLinkCodec.def) separateCodecSimPins samples |> take) samples "Separate codec should round-trip"
              let mask = (1UL <<< sampleWidth) - 1UL
              let half value = let signed = if value >= (1UL <<< (sampleWidth - 1)) then int64 value - (1L <<< sampleWidth) else int64 value in uint64 (if signed >= 0L || signed % 2L = 0L then signed / 2L else signed / 2L - 1L) &&& mask
              Expect.equal (i2sExchange (Sim i2sLinkHalfVolume.def) sharedBusSimPins samples |> take) (samples |> List.map (fun (left, right) -> half left, half right)) "Processed link should halve samples"
              Expect.throws (fun () -> defModule "NarrowSlot" (fun ports -> i2sPins ports SharedBus) (fun pins -> let link = i2sLink "i2s" pins kv260.fabricHz 48_828 16 in link.input |> link.send) |> ignore) "Slots narrower than samples should be refused"

          testCase "delay buffer counts accepted beats across stalls" <| fun _ ->
              let capacity = delayBufferMinimum
              let holds tap stalled =
                  let sim = Sim delayTap.def
                  sim.Poke("tap", uint64 tap)
                  let offered, heard = ResizeArray<uint64>(), ResizeArray<uint64>()
                  let mutable cycle = 0
                  while heard.Count < 4 * capacity && cycle < 100000 do
                      let stall = stalled cycle
                      let value = uint64 (offered.Count % 60000) + 1UL
                      sim.Poke("value", value); sim.Poke("enable", if stall then 0UL else 1UL)
                      if not stall then heard.Add(sim.Peek "delayed"); offered.Add value
                      sim.Tick(); cycle <- cycle + 1
                  Expect.equal heard.Count (4 * capacity) $"Tap {tap} produced only {heard.Count} accepted beats after {cycle} cycles"
                  [ tap .. heard.Count - 1 ] |> List.forall (fun beat -> heard[beat] = offered[beat - tap])
              for tap in [ 2; 3; 8; capacity - 1 ] do
                  Expect.isTrue (holds tap (fun _ -> false)) $"Tap {tap} should work unstalled"
                  Expect.isTrue (holds tap (fun cycle -> cycle % 3 = 1)) $"Tap {tap} should survive periodic stalls"
                  Expect.isTrue (holds tap (fun cycle -> (cycle / 7) % 2 = 0)) $"Tap {tap} should survive burst stalls"

          testCase "echo repeats at its delay and decays by feedback" <| fun _ ->
              let impulse = 1UL <<< (sampleWidth - 4)
              let taps delay feedback beats =
                  let sim = Sim audioEchoStage.def
                  sim.Poke("delay", uint64 delay); sim.Poke("feedback", feedback); sim.Poke("in_valid", 1UL); sim.Poke("out_ready", 1UL)
                  [ for beat in 0 .. beats - 1 do
                        sim.Poke("in_left", if beat = 0 then impulse else 0UL); sim.Poke("in_right", 0UL)
                        let value = sim.Peek "out_left"
                        if value <> 0UL then yield beat, value
                        sim.Tick() ]
              let delay = 37
              let repeats = taps delay 192UL (5 * delay)
              Expect.isGreaterThanOrEqual repeats.Length 4 "Echo should produce several repeats"
              for (firstBeat, loud), (secondBeat, quiet) in List.pairwise repeats do
                  Expect.equal (secondBeat - firstBeat) delay "Echo spacing should equal delay"
                  Expect.equal quiet (loud * 192UL / uint64 gainUnity) "Echo should decay by feedback"
              Expect.equal (taps delay 0UL (3 * delay)) [ 0, impulse ] "Zero feedback should leave only the dry impulse"

          testCase "i2sLink emits exactly the hand wiring" <| fun _ ->
              Expect.equal (emitDesign i2sLinkPassthru.def) (emitDesign handWiredShared.def) "Shared link abstraction should add no structure"
              Expect.equal (emitDesign i2sLinkCodec.def) (emitDesign handWiredCodec.def) "Codec link abstraction should add no structure" ]

let tests = testList "Audio" [ dspTests; integrationTests ]
