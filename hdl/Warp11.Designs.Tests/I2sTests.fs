module Warp11.Designs.Tests.I2sTests

open Expecto
open Warp11
open Warp11.Designs

let private halfStage =
    defModule
        "HalfStage"
        (fun ports -> streamInputPorts ports "in" sampleLayout, streamOutputPorts ports "out" sampleLayout)
        (fun (input, output) -> streamSource input |> reduceVolume |> streamSink output)

let private framingTests =
    testList
        "I2S framing"
        [ testCase "receiver decodes an ideal I2S frame" <| fun _ ->
              let sim = Sim i2sRxStage.def
              sim.Poke("out_ready", 1UL)
              let step wordSelect data tick =
                  sim.Poke("lrclk", wordSelect)
                  sim.Poke("sdout", data)
                  sim.Poke("sclkTick", tick)
                  sim.Tick()
              let slot wordSelect value padding =
                  step wordSelect 0UL 1UL
                  for index in 0 .. sampleWidth - 1 do step wordSelect ((value >>> (sampleWidth - 1 - index)) &&& 1UL) 1UL
                  for _ in 1..padding do step wordSelect 0UL 1UL
              let left, right = 0x123456UL, 0xABCDEFUL
              step 1UL 0UL 1UL
              slot 0UL left 2
              slot 1UL right 0
              Expect.equal (sim.Peek "out_left") left "Receiver should decode the left word"
              Expect.equal (sim.Peek "out_right") right "Receiver should decode the right word"

          testCase "transmitter emits transition bit then MSB-first words" <| fun _ ->
              let sim = Sim i2sTxStage.def
              let left, right = 0x123456UL, 0xABCDEFUL
              sim.Poke("in_left", left)
              sim.Poke("in_right", right)
              sim.Poke("in_valid", 1UL)
              sim.Poke("sclkTick", 0UL)
              sim.Poke("lrclk", 0UL)
              sim.Tick()
              sim.Poke("in_valid", 0UL)
              sim.Poke("sclkTick", 1UL)
              sim.Poke("lrclk", 1UL)
              sim.Tick()

              let slot wordSelect =
                  sim.Poke("lrclk", wordSelect)
                  sim.Tick()
                  let transition = sim.Peek "sdin"
                  sim.Tick()
                  let word =
                      [ for _ in 1..sampleWidth do let bit = sim.Peek "sdin" in sim.Tick(); yield bit ]
                      |> List.fold (fun value bit -> (value <<< 1) ||| bit) 0UL
                  transition, word

              Expect.equal (slot 0UL) (0UL, left) "Left slot should follow ideal I2S framing"
              Expect.equal (slot 1UL) (0UL, right) "Right slot should follow ideal I2S framing"

          testCase "framer loopback returns the exact stereo frame" <| fun _ ->
              let sim = Sim i2sLoopback.def
              let expected = 0xA5A5A0UL, 0x5A5A50UL
              sim.Poke("in_left", fst expected)
              sim.Poke("in_right", snd expected)
              sim.Poke("in_valid", 1UL)
              sim.Poke("out_ready", 1UL)
              let received = ResizeArray<uint64 * uint64>()
              for _ in 1..12000 do
                  sim.Tick()
                  if sim.Peek "out_valid" = 1UL then received.Add(sim.Peek "out_left", sim.Peek "out_right")
              Expect.isGreaterThan received.Count 1 "Loopback should produce settled frames"
              Expect.equal received[received.Count - 1] expected "Settled loopback should be exact"

          testCase "MSB follows word-select edge by one bit clock" <| fun _ ->
              let sim = Sim i2sLinkPassthru.def
              let codec = I2sCodec(sim, sharedBusSimPins)
              let sample = 1UL <<< (sampleWidth - 1)
              codec.Queue(List.replicate 8 (sample, sample))
              let mutable previousBit = sim.Peek "bclk"
              let mutable previousWord = sim.Peek "ws"
              let mutable sinceEdge = -1
              let mutable first = 0UL
              let edges = ResizeArray<uint64 * uint64>()
              let mutable cycles = 0
              while codec.Count < 12 && cycles < 100000 do
                  codec.Tick()
                  let bit, word = sim.Peek "bclk", sim.Peek "ws"
                  if word <> previousWord then sinceEdge <- 0
                  elif bit = 1UL && previousBit = 0UL && sinceEdge >= 0 then
                      sinceEdge <- sinceEdge + 1
                      if sinceEdge = 1 then first <- sim.Peek "sd_out"
                      elif sinceEdge = 2 then
                          if codec.Count >= 4 then edges.Add(first, sim.Peek "sd_out")
                          sinceEdge <- -1
                  previousBit <- bit
                  previousWord <- word
                  cycles <- cycles + 1
              Expect.isLessThan cycles 100000 $"Codec produced only {codec.Count} frames before timeout"
              Expect.isGreaterThan edges.Count 4 "The run should observe several settled word-select edges"
              for transition, msb in edges do
                  Expect.equal transition 0UL "The transition bit should carry no data"
                  Expect.equal msb 1UL "The following bit should carry the sample MSB" ]

let private rateTests =
    testCase "sample-rate wrapper selects exact divisors and rejects bad clocks" <| fun _ ->
        let stock = emitDesign (i2sMasterDefault "I2sMaster").def
        Expect.equal (emitDesign (i2sMasterHz 100_000_000 48_828 32 "I2sMaster").def) stock "Named sample rate should emit stock hardware"
        Expect.equal (emitDesign (i2sMasterHz kv260.fabricHz 48_828 32 "I2sMaster").def) stock "Board frequency should emit stock hardware"
        Expect.throws (fun () -> i2sMasterHz 100_000_000 48_000 32 "I2sMaster" |> ignore) "Inexact 100 MHz/48 kHz should be refused"
        Expect.throws (fun () -> i2sMasterHz iceBreaker.fabricHz 48_000 32 "I2sMaster" |> ignore) "Inexact iCEBreaker rate should be refused"
        Expect.equal iceBreaker.fabricHz 12_000_000 "The iCEBreaker fixture should retain its crystal frequency"
        i2sMasterHz 12_000_000 46_875 32 "I2sMaster" |> ignore
        Expect.floatClose Accuracy.high (sampleRateOf 100_000_000 16 32) 48_828.125 "Derived sample rate should match the documented value"

let private streamTests =
    testList
        "Audio stream helpers"
        [ testCase "reduceVolume halves signed samples arithmetically" <| fun _ ->
              let sim = Sim halfStage.def
              sim.Poke("in_valid", 1UL)
              sim.Poke("out_ready", 1UL)
              let mask = (1UL <<< sampleWidth) - 1UL
              let signed value = if value >= (1UL <<< (sampleWidth - 1)) then int64 value - (1L <<< sampleWidth) else int64 value
              let half value = if value >= 0L || value % 2L = 0L then value / 2L else value / 2L - 1L
              for sample in [ 1000L; -1000L; -1L; 1L; 0L; 8388607L; -8388608L ] do
                  let raw = uint64 sample &&& mask
                  sim.Poke("in_left", raw)
                  sim.Poke("in_right", raw)
                  sim.Tick()
                  Expect.equal (signed (sim.Peek "out_left")) (half sample) $"Left sample {sample} should halve arithmetically"
                  Expect.equal (signed (sim.Peek "out_right")) (half sample) $"Right sample {sample} should halve arithmetically"

          testCase "stream driver preserves output under stalls" <| fun _ ->
              let beats = [ for index in 1UL..8UL -> [ index * 1000UL; index * 7UL ] ]
              let expected = beats |> List.map (List.map (fun value -> value >>> 1))
              let run stall = streamThroughWith (Sim halfStage.def) (streamPins "in" sampleLayout) (streamPins "out" sampleLayout) stall 100000 beats |> Seq.take 8 |> List.ofSeq
              Expect.equal (run 0) expected "Unstalled stream should halve every beat"
              for stall in [ 2; 3; 5 ] do Expect.equal (run stall) expected $"Stall interval {stall} should not alter the sequence"

          testCase "selectFirst uses first-match ordering" <| fun _ ->
              let sim = Sim selectLadder.def
              sim.Poke("in_valid", 1UL)
              sim.Poke("out_ready", 1UL)
              sim.Poke("in_left", 700UL)
              sim.Poke("in_right", 800UL)
              for a in 0UL..1UL do
                  for b in 0UL..1UL do
                      for c in 0UL..1UL do
                          sim.Poke("a", a)
                          sim.Poke("b", b)
                          sim.Poke("c", c)
                          sim.Tick()
                          let scalar = if a = 1UL then 10UL elif b = 1UL then 20UL elif c = 1UL then 30UL else 0UL
                          let left, right = if a = 1UL then 1UL, 2UL elif b = 1UL then 3UL, 4UL else 700UL, 800UL
                          Expect.equal (sim.Peek "scalar") scalar "Scalar first-match result should agree"
                          Expect.equal (sim.Peek "out_left", sim.Peek "out_right") (left, right) "Payload first-match result should agree"

          testCase "i2sThrough is a lazy exact pipeline" <| fun _ ->
              let samples = [ 0xA5A5A0UL, 0x5A5A50UL; 0x123456UL, 0x654321UL; 0x111111UL, 0x222222UL ]
              let through take = samples |> i2sThrough (Sim i2sLinkPassthru.def) sharedBusSimPins |> Seq.skipWhile i2sSilence |> Seq.take take |> List.ofSeq
              Expect.equal (through 3) samples "Pulling three samples should round-trip all three"
              Expect.equal (through 1) [ List.head samples ] "Pulling one sample should not force the rest" ]

let tests = testList "I2S and audio streams" [ framingTests; rateTests; streamTests ]
