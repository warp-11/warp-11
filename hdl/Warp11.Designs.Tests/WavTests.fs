module Warp11.Designs.Tests.WavTests

open System
open Expecto
open Warp11
open Warp11.Designs

let private multibandTest =
    testCase "WAV signal exercises multiband peak behavior" <| fun _ ->
        let input = toneWav 48_000 2_000 440.0 0.8
        let run threshold =
            let sim = Sim multibandStage.def
            sim.Poke("threshold", threshold)
            sim.Poke("ratio", 4UL)
            sim.Poke("attack", 1UL <<< 14)
            sim.Poke("releaseRate", 1UL <<< 12)
            for index in 0 .. multibandBands - 1 do
                sim.Poke($"lg{index}", gainUnity)
                sim.Poke($"rg{index}", gainUnity)
            runWavThroughSim sim defaultWavPorts 64 input

        let settled wav =
            let skip = 800 * wav.channels
            { wav with samples = Array.sub wav.samples skip (wav.samples.Length - skip) }

        let inputLeft, inputRight = peaks (settled input)
        let openLeft, openRight = peaks (settled (run ((1UL <<< (sampleWidth - 1)) - 1UL)))
        let clampedLeft, clampedRight = peaks (settled (run 200_000UL))
        Expect.isLessThan (abs (openLeft - inputLeft) * 100) (inputLeft * 5) "Wide-open left peak should remain within 5%"
        Expect.isLessThan (abs (openRight - inputRight) * 100) (inputRight * 5) "Wide-open right peak should remain within 5%"
        Expect.isGreaterThan clampedLeft 0 "Clamped left output should remain audible"
        Expect.isGreaterThan clampedRight 0 "Clamped right output should remain audible"
        Expect.isLessThan (clampedLeft * 10) (openLeft * 9) "Compression should reduce the left peak by more than 10%"
        Expect.isLessThan (clampedRight * 10) (openRight * 9) "Compression should reduce the right peak by more than 10%"

let private extensibleHeaderTest =
    testCase "WAVE_FORMAT_EXTENSIBLE accepts only lossless PCM packaging" <| fun _ ->
        let source = toneWav 48_000 64 440.0 0.5
        let plain = writeWav source
        let pcmGuidSuffix =
            [| 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0x10uy; 0x00uy
               0x80uy; 0x00uy; 0x00uy; 0xAAuy; 0x00uy; 0x38uy; 0x9Buy; 0x71uy |]

        let repackage (subFormat: uint16) (validBits: uint16) (guidSuffix: byte[]) =
            let head = Array.sub plain 0 20
            let format = Array.sub plain 20 16
            let rest = Array.sub plain 36 (plain.Length - 36)
            Array.blit (BitConverter.GetBytes 40) 0 head 16 4
            Array.blit (BitConverter.GetBytes 0xFFFEus) 0 format 0 2
            let extension =
                Array.concat
                    [ BitConverter.GetBytes 22us
                      BitConverter.GetBytes validBits
                      BitConverter.GetBytes 3u
                      BitConverter.GetBytes subFormat
                      guidSuffix ]
            let output = Array.concat [ head; format; extension; rest ]
            Array.blit (BitConverter.GetBytes(output.Length - 8)) 0 output 4 4
            output

        Expect.equal (readWav plain) source "Ordinary PCM should round-trip"
        Expect.equal (readWav (repackage 1us 0us pcmGuidSuffix)) source "Zero valid bits should mean the full container"
        Expect.equal (readWav (repackage 1us 16us pcmGuidSuffix)) source "Explicit 16 valid bits should decode identically"
        Expect.throws (fun () -> readWav (repackage 3us 0us pcmGuidSuffix) |> ignore) "IEEE float subformat should be rejected"
        Expect.throws (fun () -> readWav (repackage 1us 0us (Array.create 14 0uy)) |> ignore) "A false PCM GUID should be rejected"
        Expect.throws (fun () -> readWav (repackage 1us 12us pcmGuidSuffix) |> ignore) "Padded samples should be rejected"

let private i2sPinsTest =
    testCase "WAV samples round-trip through I2S pins exactly" <| fun _ ->
        let input = toneWav 48_000 64 440.0 0.5
        let preRoll = 2
        let through = runWavThroughI2s (Sim i2sLinkPassthru.def) sharedBusSimPins 4 input
        let halved = runWavThroughI2s (Sim i2sLinkHalfVolume.def) sharedBusSimPins 4 input
        Expect.isGreaterThanOrEqual through.FrameCount (input.FrameCount + preRoll) "Pass-through recording should include pre-roll and all input frames"
        Expect.isGreaterThanOrEqual halved.FrameCount (input.FrameCount + preRoll) "Processed recording should include pre-roll and all input frames"
        for frame in 0 .. input.FrameCount - 1 do
            for channel in 0..1 do
                let inputSample = input.samples[frame * 2 + channel]
                Expect.equal through.samples[(frame + preRoll) * 2 + channel] inputSample $"Frame {frame}, channel {channel} should round-trip"
                Expect.equal halved.samples[(frame + preRoll) * 2 + channel] (int16 ((int inputSample + 1) >>> 1)) $"Frame {frame}, channel {channel} should halve"
        Expect.equal through.sampleRate input.sampleRate "I2S recording should preserve sample rate"
        Expect.equal through.channels 2 "I2S recording should remain stereo"

let private streamPortsTest =
    testCase "WAV batch and stepped stream harnesses agree" <| fun _ ->
        let input = toneWav 48_000 512 440.0 0.6
        let batch = runWavThroughSim (Sim audioChain.def) defaultWavPorts 0 input
        let stepped = runWavThroughStream (Sim audioChain.def) (streamPins "in" sampleLayout) (streamPins "out" sampleLayout) 1_000 input
        let shared = min batch.FrameCount stepped.FrameCount
        Expect.isGreaterThan shared (input.FrameCount / 2) "The harnesses should share more than half the recording"
        for index in 0 .. shared * 2 - 1 do
            Expect.equal stepped.samples[index] batch.samples[index] $"Shared sample {index} should agree"
        Expect.equal stepped.sampleRate input.sampleRate "Stepped harness should preserve sample rate"
        Expect.equal stepped.channels 2 "Stepped harness should remain stereo"

let tests =
    testList "WAV integration" [ multibandTest; extensibleHeaderTest; i2sPinsTest; streamPortsTest ]
