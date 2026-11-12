/// The audio stages as bare stream designs: the twins the chains in
/// `Chains.fs` are checked against, and one stage each so a defect the whole
/// chain shows can be localised. Fixtures, not board designs — the board
/// designs are `effects` and `multiband`, built for a board by a mapping.
module Warp11.Effects.Batch

open Warp11
open Warp11.Audio

/// The same compressor as a bare stream stage, one frame per cycle. This is the
/// twin the batch path is checked against: identical DSP, none of the DDR
/// plumbing, so a mismatch localises to the plumbing rather than to the audio.
///
/// The rate is the one the crossovers are designed for: the converter's on a
/// pins path, `BoardTop.nominalRate` on a host path, where the design is
/// handed the file's nominal rate rather than a converter's.
let multibandStageAt (name: string) (rate: float) =
    defModule
        name
        (fun p ->
            (multibandSettingsPorts p,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (settings, inPorts, outPorts) ->
            let stage, _envelope =
                streamSource inPorts
                |> multibandCompressor "MultibandCompressor8" rate "mb" settings

            streamSink outPorts stage)

/// The stage at the KV260 converter's rate.
let multibandStageRef = multibandStageAt "MultibandStageRef" stockSampleRate

/// The same compressor on one multiplier: the folded engine's twin, with the
/// spatial twin's stream and law and its makeup gains in the loadable table,
/// so the two can be run on one stimulus and compared frame for frame.
let multibandStageFoldedRef =
    defModule
        "MultibandStageFoldedRef"
        (fun p ->
            (multibandFoldedSettingsPorts p,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (settings, inPorts, outPorts) ->
            let stage, _envelope =
                streamSource inPorts
                |> multibandCompressorFolded "MultibandCompressor8Folded" stockSampleRate "mb" (multibandFoldedSettingsOf settings)

            streamSink outPorts stage)

// ---------------------------------------------------------------------------
// One stage each, as a bare stream design, so the stall-independence property
// can be asked of them one at a time. These exist to localise a defect the
// whole chain shows; they are fixtures, not board designs.

let gainStage =
    defModule
        "GainStage"
        (fun p ->
            (p.inPort "volume" 16,
             p.inPort "mute" 1,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (volume, mute, inPorts, outPorts) ->
            streamSource inPorts
            |> audioGain "AudioGain" "g" volume mute
            |> streamSink outPorts)

let eqStage =
    defModule
        "EqStage"
        (fun p ->
            ([ for n in [ "b0"; "b1"; "b2"; "a1"; "a2" ] -> p.inPort n biquadCoeffWidth ],
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (coeffs, inPorts, outPorts) ->
            streamSource inPorts
            |> audioEqBand "AudioEqBand" "eq" coeffs
            |> streamSink outPorts)

let compressorStage =
    defModule
        "CompressorStage"
        (fun p ->
            (compressorSettingsPorts p,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (settings, inPorts, outPorts) ->
            streamSource inPorts
            |> audioCompressor "AudioCompressor" "c" settings
            |> streamSink outPorts)

/// How deep the echo line is: the next power of two above half a second at the
/// rate this board frames. Derived rather than written down, so a change of
/// divisors moves the buffer with the rate instead of leaving it a fixed number
/// of samples that used to be half a second.
///
/// At 48,828 Hz that is 32,768 frames of 48 bits — around 44 block RAMs, which
/// is a third of this part's supply and the number a board build would have to
/// argue with. Nothing here is on the board yet, so it argues with nobody.
let echoCapacity =
    let wanted = int (stockSampleRate / 2.0)
    let rec upTo n = if n >= wanted then n else upTo (n * 2)
    upTo 1

/// The echo alone, stream-driven — the delay line the rest of this file does
/// not have.
let echoStage =
    defModule
        "EchoStage"
        (fun p ->
            (p.inPort "delay" (log2Exact echoCapacity),
             p.inPort "feedback" 16,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (delay, feedback, inPorts, outPorts) ->
            streamSource inPorts
            |> audioEcho "AudioEcho" echoCapacity "echo" delay feedback
            |> streamSink outPorts)

let limiterStage =
    defModule
        "LimiterStage"
        (fun p ->
            (p.inPort "threshold" sampleWidth,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (threshold, inPorts, outPorts) ->
            streamSource inPorts
            |> audioLimiter "AudioLimiter" "l" threshold
            |> streamSink outPorts)

// ---------------------------------------------------------------------------
// The drift, as something you can step.
//
// Reading about a one-step divergence is not the same as watching it happen, so
// this harness drives the multiband stage itself: a ramp of samples, the
// consumer always ready, and `in_valid` held low on exactly one cycle. That one
// bubble is the whole experiment.
//
// Two counters mirror the two gates the stage uses:
//
//   pipe_steps  counts `enable`  — a ready cycle, what the delay lines follow
//   dsp_steps   counts `advance` — an accepted beat, what the arithmetic follows
//
// `drift` is the gap. It settles to a constant while beats flow (the input
// register costs one), and **steps by one at the bubble and never comes back**.
// The design asserts that it does not, so the debugger stops on the cycle it
// happens rather than leaving you to spot it.

let driftHarness =
    defModule
        "MultibandDrift"
        (fun p ->
            (p.inPort "run" 1,
             p.inPort "stallAt" 8,
             p.outPortAs "out_left" (SInt sampleWidth),
             p.outPortAs "out_right" (SInt sampleWidth),
             p.outPort "out_valid" 1,
             p.outPort "envelope" sampleWidth,
             p.outPort "drift" 8,
             p.outPort "drift_baseline_out" 8))
        (fun (run, stallAt, outLeftPort, outRightPort, outValidPort, envelopePort, driftPort, baselinePort) ->

        let cycle = reg "cycle" 8
        If run (fun () -> cycle + lit 1UL 8 ==> cycle)

        // A ramp, so every sample is a different number and a repeated one is
        // obvious in the output.
        let sample = reg "sample" (SInt sampleWidth)

        // The producer offers on every cycle but one.
        let offering = wireBit "offering"
        (run &&& bnot (eq cycle stallAt)) ==> offering

        let srcReady = wireBit "src_ready"
        registerStreamReady srcReady

        let src: Stream<Expr * Expr> =
            { payload = (sample, sample)
              valid = offering
              ready = srcReady
              layout = sampleLayout }

        let accepted = wireBit "accepted"
        (offering &&& srcReady) ==> accepted
        If accepted (fun () -> sample + lit 1UL sampleWidth ==> sample)

        let out, envelope =
            src
            |> multibandCompressor
                "MultibandCompressor8"
                stockSampleRate
                "mb"
                { threshold = lit 200_000UL sampleWidth
                  ratio = lit 4UL 8
                  attack = lit (1UL <<< 14) 16
                  releaseRate = lit (1UL <<< 12) 16
                  leftGains = List.replicate multibandBands (lit gainUnity 16)
                  rightGains = List.replicate multibandBands (lit gainUnity 16) }

        // Always ready: the consumer is never the reason anything stalls, so
        // the only bubble in the run is the one above.
        lit 1UL 1 ==> out.ready

        let outLeft, outRight = out.payload
        outLeft ==> outLeftPort
        outRight ==> outRightPort
        out.valid ==> outValidPort
        envelope ==> envelopePort

        // `advance` one level down, restated here so the counters can see it:
        // a beat accepted last cycle, with the consumer ready now.
        let acceptedLast = regBit "accepted_last"
        If run (fun () -> accepted ==> acceptedLast)

        let dspSteps = reg "dsp_steps" 8
        let pipeSteps = reg "pipe_steps" 8
        If acceptedLast (fun () -> dspSteps + lit 1UL 8 ==> dspSteps)
        If run (fun () -> pipeSteps + lit 1UL 8 ==> pipeSteps)

        // The gap between the two gates, at a port so it is easy to watch.
        let drift = wire "drift_w" 8
        sub pipeSteps dspSteps ==> drift
        drift ==> driftPort

        // Latch the settled gap once the pipeline is warm, then hold the design
        // to it. A stage whose two halves agree keeps this forever; this one
        // does not, and the assertion is what stops the debugger on the cycle.
        let baseline = reg "drift_baseline" 8
        let armed = regBit "armed"

        If (run &&& eq cycle (lit 8UL 8)) (fun () ->
            drift ==> baseline
            lit 1UL 1 ==> armed)

        assertThat (bnot armed ||| eq drift baseline) "the valid pipe drifted from the DSP path"

        baseline ==> baselinePort)

/// The FIR, as a bare stream stage. The third of the three hand-rolled valid
/// delay lines in the audio library, and the only one the sweep did not reach.
let firStage =
    defModule
        "FirStage"
        (fun p ->
            (p.inPort "preset" 2,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (preset, inPorts, outPorts) ->
            streamSource inPorts
            |> audioFir
                "AudioFir"
                16
                // The rate the design actually runs at, not a round 48 000.
                // A filter's coefficients fix a frequency in cycles per
                // *sample*, so designing at one rate and clocking at another
                // moves every corner by the ratio between them — the same
                // defect `multibandCompressor` was fixed for, and the same
                // 1.73 % it was wrong by.
                { sampleRate = stockSampleRate
                  lowPass = 4_000.0
                  highPass = 400.0 }
                "fir"
                preset
            |> streamSink outPorts)
