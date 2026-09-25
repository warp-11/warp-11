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

let private gainTableTests =
    // A four-region prescription in dB, the shape every fitting rule takes:
    // expansion below a knee, linear gain through conversation, compression
    // above a second knee, then a hard ceiling.
    let prescription dbFs =
        let dbSpl = dbFs + 115.0
        let linear = 25.0
        let gain =
            if dbSpl < 45.0 then linear - (45.0 - dbSpl) * (1.0 / 0.57 - 1.0)
            elif dbSpl < 50.0 then linear
            else linear - (dbSpl - 50.0) * (1.0 - 1.0 / 1.5)
        min gain (92.0 - dbSpl)

    let dbFsOf env = 20.0 * log10 (float env / 2.0 ** float sampleWidth)
    let envelopes = [ 1..4096 ] @ [ 4096 .. 977 .. (1 <<< sampleWidth) - 1 ]

    // What the fabric's interpolant can say about the law: the chord across
    // the entry the envelope falls in, walked in the **envelope** rather than
    // in dB, because the fraction bits the lookup interpolates on are the
    // mantissa's. Separating this from the law itself is what keeps the checks
    // free of a written-down list of the law's knees, which is the thing that
    // would rot when a prescription changes.
    let chord env =
        let level = float env / 2.0 ** float sampleWidth
        let entry =
            [ 0 .. gainTableEntries - 1 ] |> List.findBack (fun e -> gainTableLevel e <= level)
        let below, above = gainTableLevel entry, gainTableLevel (entry + 1)
        let along = (level - below) / (above - below)
        let gainAt l = prescription (20.0 * log10 l)
        gainAt below + along * (gainAt above - gainAt below)

    testList
        "Gain table"
        [ testCase "the table is the law's chord on its own grid" <| fun _ ->
              let words = gainTableWords prescription
              Expect.equal words.Length gainTableSize "Table should occupy its declared words"
              // Above octave nine the envelope carries all eight fraction
              // bits the lookup interpolates on, so the only slack left is
              // rounding the entry and its step to Q8.8.
              let errors =
                  [ for env in envelopes do
                        if env >= (1 <<< 9) then yield abs (gainDbOfLog (gainTableLookup words env) - chord env), env ]
              let worst, at = List.max errors
              Expect.isLessThan worst 0.02 $"Lookup should be the chord to rounding (worst at env {at})"

          testCase "the grid is fine enough for a prescription" <| fun _ ->
              let words = gainTableWords prescription
              // A region is affine in dB, so a chord is only wrong where a
              // knee falls inside an entry: the slope change over a quarter
              // step, and the steepest knee here is 0.754 over 3.01 dB.
              let worst, at =
                  [ for env in envelopes -> abs (gainDbOfLog (gainTableLookup words env) - prescription (dbFsOf env)), env ]
                  |> List.max
              Expect.isLessThan worst 0.75 $"Tabulation should hold the law to a quarter step (worst at env {at})"

          testCase "a flat law reads flat, and silence reads the floor" <| fun _ ->
              let words = gainTableWords (fun _ -> 12.0)
              for env in [ 0; 1; 2; 3; 255; 4096; (1 <<< sampleWidth) - 1 ] do
                  Expect.isLessThan (abs (gainDbOfLog (gainTableLookup words env) - 12.0)) 0.01 $"A constant law should read constant at env {env}"
              let ramp = gainTableWords prescription
              Expect.equal (gainTableLookup ramp 0) (gainTableLookup ramp 1) "Silence should read entry zero's gain"

          testCase "the fabric reads the table the host wrote" <| fun _ ->
              let words = gainTableWords prescription
              let sim = Sim gainTableStage.def
              sim.Poke("wr_enable", 1UL)
              for address in 0 .. words.Length - 1 do
                  sim.Poke("wr_addr", uint64 address)
                  sim.Poke("wr_data", words[address])
                  sim.Tick()
              sim.Poke("wr_enable", 0UL)
              // Every octave and a scatter within each, so the priority encoder
              // and the mantissa select are both exercised at every position.
              let probes =
                  [ 0; 1; 2; 3; 7 ]
                  @ [ for octave in 3 .. sampleWidth - 1 do
                          for offset in [ 0; 1; 37; 255; 4095 ] do
                              let env = (1 <<< octave) + offset
                              if env < (1 <<< sampleWidth) then yield env ]
              for env in probes do
                  sim.Poke("env", uint64 env)
                  let got = float (int16 (uint16 (sim.Peek "gain"))) / float (1 <<< gainLogFracBits)
                  Expect.equal got (gainTableLookup words env) $"Fabric and host should agree at env {env}"

          testCase "the curve never steps further than the law does" <| fun _ ->
              let words = gainTableWords prescription
              // The stored step is what removes the zipper an entry boundary
              // would otherwise put in a tone. It is inaudible in a sweep, so
              // only walking neighbours finds it. Below octave nine adjacent
              // envelopes are more than a decibel apart on their own, so there
              // is no zipper down there to find — and nothing audible either.
              for env in [ 512..4096 ] @ [ 4096 .. 977 .. (1 <<< sampleWidth) - 1 ] do
                  let jump = abs (gainDbOfLog (gainTableLookup words env - gainTableLookup words (env - 1)))
                  let allowed = abs (prescription (dbFsOf env) - prescription (dbFsOf (env - 1))) + 0.05
                  Expect.isLessThan jump allowed $"Envelope {env} should not step further than the law" ]

let private gainExpTests =
    let signedSample (raw: uint64) =
        let bits = int64 raw
        if bits >= (1L <<< (sampleWidth - 1)) then int (bits - (1L <<< sampleWidth)) else int bits

    let logs =
        // Every octave the gain reaches, and a scatter of fractions inside each,
        // so the mantissa select and the shift are both exercised end to end.
        [ for exponent in -16 .. 15 do
              for fraction in [ 0; 1; 337; 1024; 2047 ] -> (exponent <<< gainLogFracBits) ||| fraction ]

    testList
        "Gain apply"
        [ testCase "the mantissa table is 2^f" <| fun _ ->
              let worst =
                  [ for gainLog in logs do
                        let fraction = float (gainLog &&& ((1 <<< gainLogFracBits) - 1)) / float (1 <<< gainLogFracBits)
                        let want = 2.0 ** fraction * float (1 <<< (gainExpMantissaBits - 1))
                        yield abs (float (gainExpMantissaOfLog gainLog) - want) / want ]
                  |> List.max
              // As decibels, because that is the unit the error has to be small in.
              Expect.isLessThan (gainDbOfLog (log (1.0 + worst) / log 2.0)) 0.01 "The mantissa should reproduce 2^f"

          testCase "unity gain is exactly transparent" <| fun _ ->
              for sample in [ 0; 1; -1; 4095; -4096; (1 <<< (sampleWidth - 1)) - 1; -(1 <<< (sampleWidth - 1)) ] do
                  Expect.equal (gainApplyToSample 0 sample) sample $"A zero log gain should pass sample {sample}"

          testCase "the applied gain is the gain it was asked for" <| fun _ ->
              // Well inside full scale, so nothing saturates and the ratio is
              // the whole story. The bound is stated as what the arithmetic can
              // cost rather than as a decibel figure: the final shift truncates,
              // which is one count whatever the level, and the mantissa's chord
              // is 0.0003 of the value. Written as a decibel tolerance it would
              // have to be loose enough for the quietest case it is checked at,
              // which would stop saying anything about the loudest.
              let sample = 1 <<< (sampleWidth - 6)
              for gainLog in logs do
                  // The gain the apply will actually deliver: the *exponent* is
                  // clamped to the octaves it reaches — that clamp is what
                  // bounds the shifter — and the fraction rides through it.
                  let exponent =
                      gainLog >>> gainLogFracBits
                      |> max -gainApplyOctaves
                      |> min (gainApplyOctaves - 1)
                  let fraction = gainLog &&& ((1 <<< gainLogFracBits) - 1)
                  let delivered = float exponent + float fraction / float (1 <<< gainLogFracBits)
                  let want = float sample * 2.0 ** delivered
                  if want < float ((1 <<< (sampleWidth - 1)) - 1) then
                      let got = float (gainApplyToSample gainLog sample)
                      Expect.isLessThan (abs (got - want)) (1.0 + want * 0.0003) $"The apply should hit the gain asked at {gainLog}"

          testCase "the gain clamps at the octaves the apply delivers" <| fun _ ->
              let sample = 1 <<< (sampleWidth - 6)
              let atOctaves n = gainApplyToSample (n <<< gainLogFracBits) sample
              // Below the floor every gain is the floor, so a curve asking for
              // silence gets 1/256 rather than something the shifter cannot say.
              Expect.equal (atOctaves -gainApplyOctaves) (atOctaves -(gainApplyOctaves + 4)) "Below the floor should clamp"
              Expect.equal (atOctaves -gainApplyOctaves) (sample >>> gainApplyOctaves) "The floor should be 2^-octaves"
              // And above the ceiling, likewise — up to where full scale bites.
              Expect.equal
                  (gainApplyToSample ((gainApplyOctaves - 1) <<< gainLogFracBits) 1)
                  (gainApplyToSample ((gainApplyOctaves + 4) <<< gainLogFracBits) 1)
                  "Above the ceiling should clamp"

          testCase "full scale saturates rather than wrapping" <| fun _ ->
              let loudest = (1 <<< (sampleWidth - 1)) - 1
              for gainLog in [ 1 <<< gainLogFracBits; 8 <<< gainLogFracBits; 15 <<< gainLogFracBits ] do
                  Expect.equal (gainApplyToSample gainLog loudest) loudest $"Gain {gainLog} on full scale should clamp"
                  Expect.equal (gainApplyToSample gainLog (-loudest - 1)) (-loudest - 1) $"Gain {gainLog} on negative full scale should clamp"

          testCase "the fabric applies the gain the host predicts" <| fun _ ->
              let sim = Sim gainApplyStage.def
              let samples = [ 0; 1; -1; 1023; -4097; 1 <<< (sampleWidth - 6); -(1 <<< (sampleWidth - 3)); (1 <<< (sampleWidth - 1)) - 1 ]
              for gainLog in logs do
                  sim.Poke("gain", uint64 gainLog &&& ((1UL <<< gainLogWidth) - 1UL))
                  for sample in samples do
                      sim.Poke("sample", uint64 sample &&& ((1UL <<< sampleWidth) - 1UL))
                      Expect.equal
                          (signedSample (sim.Peek "scaled"))
                          (gainApplyToSample gainLog sample)
                          $"Fabric and host should agree at gain {gainLog}, sample {sample}" ]

let private bandTableTests =
    // The same four-region prescription the table checks use, so a failure here
    // is the composition and not the curve.
    let prescription dbFs =
        let dbSpl = dbFs + 115.0
        let gain =
            if dbSpl < 45.0 then 25.0 - (45.0 - dbSpl) * (1.0 / 0.57 - 1.0)
            elif dbSpl < 50.0 then 25.0
            else 25.0 - (dbSpl - 50.0) * (1.0 - 1.0 / 1.5)
        min gain (92.0 - dbSpl)

    testList
        "Band table law"
        [ testCase "a level in comes out with the gain the prescription asks for" <| fun _ ->
              let sim = Sim bandTableStage.def
              let words = gainTableWords prescription
              sim.Poke("wr_enable", 1UL)
              for address in 0 .. words.Length - 1 do
                  sim.Poke("wr_addr", uint64 address)
                  sim.Poke("wr_data", words[address])
                  sim.Tick()
              sim.Poke("wr_enable", 0UL)
              // Near-unity coefficients, so the envelope tracks in a handful of
              // samples and the check is about the law rather than the detector.
              sim.Poke("attack", 0x7FF0UL)
              sim.Poke("releaseRate", 0x7FF0UL)
              sim.Poke("advance", 1UL)

              let settle level =
                  // A square wave at the level, so the envelope settles on it
                  // exactly and the gained magnitude is the level times the gain.
                  let mutable last = 0, 0
                  for beat in 0 .. 63 do
                      let value = if beat % 2 = 0 then level else -level
                      sim.Poke("band", uint64 value &&& ((1UL <<< bandWidth) - 1UL))
                      let raw = int64 (sim.Peek "gained")
                      let signed =
                          if raw >= (1L <<< (bandWidth - 1)) then int (raw - (1L <<< bandWidth)) else int raw
                      last <- int (sim.Peek "envelope"), signed
                      sim.Tick()
                  last

              // Every decade of level the law has an opinion about, from the
              // expansion floor up to where limiting takes over.
              for shift in 6 .. sampleWidth - 2 do
                  let level = 1 <<< shift
                  let envelope, gainedValue = settle level
                  // The detector stops one count short at any level, because
                  // `alpha*(peak-env) >> 15` truncates to zero once the gap is
                  // one — so the bound is a count, not a fraction. The law is
                  // then checked against the envelope it actually reached, which
                  // keeps this about the lookup and the apply.
                  Expect.isLessThanOrEqual
                      (abs (envelope - level))
                      1
                      $"The envelope should track the level at 2^{shift}"
                  let dbFs = 20.0 * log10 (float envelope / 2.0 ** float sampleWidth)
                  let want = float level * 10.0 ** (prescription dbFs / 20.0)
                  if want < float ((1 <<< (bandWidth - 1)) - 1) then
                      // One count for the apply's truncation, and the tabulation's
                      // own 0.6 dB where a knee falls inside an entry.
                      let allowed = 1.0 + want * (10.0 ** (0.6 / 20.0) - 1.0)
                      Expect.isLessThan
                          (abs (float (abs gainedValue) - want))
                          allowed
                          $"At 2^{shift} the band should come out {want} and came out {abs gainedValue}"

          testCase "a silent band stays silent" <| fun _ ->
              let sim = Sim bandTableStage.def
              let words = gainTableWords prescription
              sim.Poke("wr_enable", 1UL)
              for address in 0 .. words.Length - 1 do
                  sim.Poke("wr_addr", uint64 address)
                  sim.Poke("wr_data", words[address])
                  sim.Tick()
              sim.Poke("wr_enable", 0UL)
              sim.Poke("attack", 0x7FF0UL)
              sim.Poke("releaseRate", 0x7FF0UL)
              sim.Poke("advance", 1UL)
              sim.Poke("band", 0UL)
              for _ in 0 .. 63 do
                  sim.Tick()
              // The floor entry is a large negative gain, and zero times anything
              // is zero — but a wrong shift or a wrong sign would not be.
              Expect.equal (sim.Peek "gained") 0UL "Silence in should be silence out"
              Expect.equal (sim.Peek "envelope") 0UL "The envelope should be at rest" ]

let tests =
    testList "Audio" [ dspTests; gainTableTests; gainExpTests; bandTableTests; integrationTests ]
