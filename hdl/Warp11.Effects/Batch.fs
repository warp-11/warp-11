/// The batch audio accelerator: a block of frames sitting in PS DDR, read by
/// the fabric, pushed through the multiband compressor, and written back.
///
/// This is the example that answers "run a file through the board". Every other
/// audio app here is a live I2S chain, which needs a codec on the Pmod header
/// and gives you nothing to diff; this one takes bytes in and produces bytes
/// out, so its result can be compared against the software twin exactly, and
/// against `Warp11.Designs -- wav` — the same DSP running in the simulator.
module Warp11.Effects.Batch

open Warp11
open Warp11.Audio

// ---------------------------------------------------------------------------
// The DDR contract.
//
// One frame is 8 bytes: [left i32][right i32], little-endian, the 24-bit two's
// complement sample sign-extended into each 32-bit lane. Two frames fill a
// 128-bit beat, which is what keeps every write 16-byte aligned — the KV260's
// HP slave port silently drops writes that are not, and that failure looks like
// corrupt audio rather than an error.
//
// A 32-bit lane for a 24-bit sample wastes a third of the bandwidth and is
// still the right call: it makes the host side a plain `i32` array with no
// unpacking, and this design is nowhere near memory-bound.

let laneWidth = 32
let beatWidth = 128
let framesPerBeat = beatWidth / (2 * laneWidth)

/// Beats per burst. 16 × 16 bytes = 256 B, so a 256 B-aligned base can never
/// cross AXI's 4 KB boundary — the rule `axiMasterReaderBurst` leaves to its
/// caller, and the cheapest way to satisfy it is to make it unreachable.
let beatsPerBurst = 16
let framesPerBurst = framesPerBeat * beatsPerBurst

let private apertureAddrWidth = 8

// ---------------------------------------------------------------------------
// The register map. One value per entry, so the slave and the generated Rust
// name each register exactly once and cannot drift apart.

type BatchMap =
    { id: RegEntry
      start: RegEntry
      busy: RegEntry
      doneIrq: RegEntry
      srcAddr: RegEntry
      dstAddr: RegEntry
      frameCount: RegEntry
      threshold: RegEntry
      ratio: RegEntry
      attack: RegEntry
      releaseRate: RegEntry
      gains: RegEntry list }

let batchRegs, batchMap =
    buildRegMapPinned apertureAddrWidth (fun r ->
        // The overlay word: the identity answers reads, the start pulse takes
        // the write side.
        let id, start = r.Word(fun w -> w.Const("id", 0xAB12C001UL), w.Pulse "start")

        // Which revision of this map the fabric was built from — see GoL's
        // and GEP's, same reason: the identity says which design, this says
        // whether the offsets still mean what the host was compiled against.
        r.LayoutHash "layoutHash"

        { id = id
          start = start
          busy = r.Word(fun w -> w.Field("busy", 1))
          doneIrq = r.Word(fun w -> w.W1c "doneIrq")
          srcAddr = r.RwReg("srcAddr", 32, 0UL)
          dstAddr = r.RwReg("dstAddr", 32, 0UL)
          frameCount = r.RwReg("frameCount", 32, 0UL)

          // Every DSP default is a no-op, so a freshly loaded bitstream copies
          // its input to its output untouched — measurably bit-exact, which is
          // what lets "did the DDR path work" stay a separate question from
          // "did the compressor work" now that there is no bypass mux to
          // answer it.
          threshold = r.RwReg("threshold", sampleWidth, (1UL <<< sampleWidth) - 1UL)
          ratio = r.RwReg("ratio", 8, 0UL)
          attack = r.RwReg("attack", 16, 0UL)
          releaseRate = r.RwReg("releaseRate", 16, 0UL)

          // Two 16-bit band gains per 32-bit word: left band i in the low half,
          // right band i in the high half, so a host writes one word per band.
          gains =
            List.init multibandBands (fun i ->
                r.RwReg($"gain{i}", 32, gainUnity ||| (gainUnity <<< 16))) })

// ---------------------------------------------------------------------------
// Beat ↔ frame, the two width changes this design is made of.

/// One 128-bit beat becomes two frames. The beat is *not* copied into a
/// register: `ready` is asserted only as the second frame leaves, and the read
/// master holds its payload until then, so the handshake does the storing.
let private beatsToFrames (beats: Stream<Expr * Expr>) : Stream<Expr * Expr> =
    let data, _last = beats.payload

    let beat = wire "unpack_beat" beatWidth
    data ==> beat

    let phase = regBit "unpack_phase"
    let outReady = wireBit "unpack_out_ready"
    registerStreamReady outReady

    let fire = beats.valid &&& outReady
    // The beat retires as its second frame is taken.
    (fire &&& phase) ==> beats.ready
    If fire (fun () -> bnot phase ==> phase)

    let sampleAt lo = slice (lo + sampleWidth - 1) lo beat

    { payload =
        (mux phase (sampleAt (2 * laneWidth)) (sampleAt 0),
         mux phase (sampleAt (3 * laneWidth)) (sampleAt laneWidth))
      valid = beats.valid
      ready = outReady
      layout = sampleLayout }

/// Two frames become one 128-bit beat. The first frame is held in a register —
/// unavoidable here, because the producer downstream of the DSP has already
/// been told its frame was taken.
let private framesToBeats (frames: Stream<Expr * Expr>) : Stream<Expr> =
    let left, right = frames.payload

    // The 24-bit sample sign-extended into its 32-bit lane, so the host reads a
    // plain i32 and gets the number the fabric computed.
    let lane (s: Expr) = asUInt (pad laneWidth (asSInt s))
    let frameBits = cat (lane right) (lane left)

    let held = reg "pack_held" (2 * laneWidth)
    let phase = regBit "pack_phase"

    let outValid = wireBit "pack_out_valid"
    let outReady = wireBit "pack_out_ready"
    registerStreamReady outReady

    // Frame 0 is always accepted; frame 1 only when the beat can leave.
    (bnot phase ||| outReady) ==> frames.ready
    (frames.valid &&& phase) ==> outValid

    let fire = frames.valid &&& (bnot phase ||| outReady)

    If fire (fun () ->
        bnot phase ==> phase
        If (bnot phase) (fun () -> frameBits ==> held))

    { payload = cat frameBits held
      valid = outValid
      ready = outReady
      layout = layout1 ("data", beatWidth) }

// ---------------------------------------------------------------------------
// The design.

/// Frames in DDR at `srcAddr`, through the 8-band compressor, back to DDR at
/// `dstAddr`. `frameCount` must be a multiple of `framesPerBurst` (32) and both
/// addresses 256-byte aligned; the host driver checks and pads, because a
/// design that silently processed a truncated block would be worse than one
/// that refused.
let audioBatchAxi =
    defModuleClocked
        axiClock
        "AudioBatchAxi"
        (fun p ->
            (axiLiteSlavePorts p batchMap.apertureAddrWidth,
             axiReadBusPorts p "m_axi" 32 beatWidth,
             axiWriteBusPorts p "m_axi" 32 beatWidth))
        (fun (slavePorts, readBusPorts, writeBusPorts) ->
        let regs = regMapSlave slavePorts batchMap

        let bursts = wire "bursts" 32
        // frameCount / framesPerBurst, and framesPerBurst is 32 — a slice.
        pad 32 (slice 31 5 (regs.value batchRegs.frameCount)) ==> bursts

        let running = regBit "running"
        let arIssued = reg "ar_issued" 32
        let beatsWritten = reg "beats_written" 32

        // --- the read side: one descriptor per burst -------------------------
        let reqReady = wireBit "req_ready"
        let arMore = wireBit "ar_more"
        (running &&& lt arIssued bursts) ==> arMore

        let reqAddr = wire "req_addr" 32
        // burst stride is beatsPerBurst * 16 bytes = 256, so shift by 8.
        (regs.value batchRegs.srcAddr + asUInt (shl 8 (slice 23 0 arIssued))) ==> reqAddr

        let requests: Stream<Expr * Expr> =
            { payload = (reqAddr, lit (uint64 (beatsPerBurst - 1)) 8)
              valid = arMore
              ready = reqReady
              layout = layout2 ("addr", 32) ("len", 8) }

        If (arMore &&& reqReady) (fun () -> arIssued + lit 1UL 32 ==> arIssued)

        let beats = axiMasterReaderBurstOn (axiReadBusOf readBusPorts) 4 beatsPerBurst requests

        // --- the DSP ---------------------------------------------------------
        let leftGains = batchRegs.gains |> List.map (fun g -> slice 15 0 (regs.value g))
        let rightGains = batchRegs.gains |> List.map (fun g -> slice 31 16 (regs.value g))

        let processed, _envelope =
            multibandCompressor
                "MultibandCompressor8"
                "mb"
                (regs.value batchRegs.threshold)
                (regs.value batchRegs.ratio)
                (regs.value batchRegs.attack)
                (regs.value batchRegs.releaseRate)
                leftGains
                rightGains
            |> fun apply -> apply (beatsToFrames beats)

        // --- the write side --------------------------------------------------
        let outBeats = framesToBeats processed

        let wrAddr = wire "wr_addr" 32
        (regs.value batchRegs.dstAddr + asUInt (shl 4 (slice 27 0 beatsWritten))) ==> wrAddr

        let wrReady = wireBit "wr_ready"

        let writeBeats: Stream<Expr * Expr * Expr> =
            { payload = (wrAddr, outBeats.payload, lit ((1UL <<< (beatWidth / 8)) - 1UL) (beatWidth / 8))
              valid = outBeats.valid
              ready = wrReady
              layout = axiWriteBeatLayout 32 beatWidth }

        wrReady ==> outBeats.ready

        let writerIdle = axiMasterWriterWithIdleOn (axiWriteBusOf writeBusPorts) 4 writeBeats

        If (outBeats.valid &&& wrReady) (fun () -> beatsWritten + lit 1UL 32 ==> beatsWritten)

        // --- control ---------------------------------------------------------
        let beatsTotal = wire "beats_total" 32
        asUInt (shl 4 (slice 27 0 bursts)) ==> beatsTotal

        let finished = wireBit "finished"
        (running &&& eq beatsWritten beatsTotal &&& writerIdle) ==> finished

        ifElse [
            (regs.pulse batchRegs.start, fun () ->
                lit 1UL 1 ==> running
                lit 0UL 32 ==> arIssued
                lit 0UL 32 ==> beatsWritten)
            (otherwise, fun () -> If finished (fun () -> lit 0UL 1 ==> running)) ]

        regs.drive batchRegs.busy running
        regs.setBit batchRegs.doneIrq finished

        // A burst is 256 bytes and the stride is 256 bytes, so no burst can
        // cross AXI's 4 KB boundary *provided* the bases are 256-byte aligned.
        // That is the caller's half of the contract, and an unaligned base
        // would corrupt audio rather than fail, so the design says so out loud:
        // checked every cycle in simulation and by the differential in both
        // worlds, and compiled out of the silicon.
        let aligned (e: RegEntry) =
            bnot running ||| eq (slice 7 0 (regs.value e)) (lit 0UL 8)

        assertThat (aligned batchRegs.srcAddr) "srcAddr must be 256-byte aligned"
        assertThat (aligned batchRegs.dstAddr) "dstAddr must be 256-byte aligned"

        // Likewise the frame count: the fabric processes whole bursts, so a
        // remainder would be silently dropped.
        assertThat
            (bnot running ||| eq (slice 4 0 (regs.value batchRegs.frameCount)) (lit 0UL 5))
            "frameCount must be a multiple of framesPerBurst (32)")

/// The same compressor as a bare stream stage, one frame per cycle. This is the
/// twin the batch path is checked against: identical DSP, none of the DDR
/// plumbing, so a mismatch localises to the plumbing rather than to the audio.
let multibandStageRef =
    defModule
        "MultibandStageRef"
        (fun p ->
            (p.inPort "threshold" sampleWidth,
             p.inPort "ratio" 8,
             p.inPort "attack" 16,
             p.inPort "releaseRate" 16,
             List.init multibandBands (fun i -> p.inPort $"lg{i}" 16),
             List.init multibandBands (fun i -> p.inPort $"rg{i}" 16),
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (threshold, ratio, attack, releaseRate, leftGains, rightGains, inPorts, outPorts) ->
            let stage, _envelope =
                multibandCompressor "MultibandCompressor8" "mb" threshold ratio attack releaseRate leftGains rightGains
                |> fun apply -> apply (streamSource inPorts)

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
            (p.inPort "threshold" sampleWidth,
             p.inPort "ratio" 8,
             p.inPort "attack" 16,
             p.inPort "releaseRate" 16,
             p.inPort "makeup" 16,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (threshold, ratio, attack, releaseRate, makeup, inPorts, outPorts) ->
            streamSource inPorts
            |> audioCompressor "AudioCompressor" "c" threshold ratio attack releaseRate makeup
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
            multibandCompressor
                "MultibandCompressor8"
                "mb"
                (lit 200_000UL sampleWidth)
                (lit 4UL 8)
                (lit (1UL <<< 14) 16)
                (lit (1UL <<< 12) 16)
                (List.replicate multibandBands (lit gainUnity 16))
                (List.replicate multibandBands (lit gainUnity 16))
            |> fun apply -> apply src

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
            |> audioFir "AudioFir" 16 48_000.0 4_000.0 400.0 "fir" preset
            |> streamSink outPorts)
