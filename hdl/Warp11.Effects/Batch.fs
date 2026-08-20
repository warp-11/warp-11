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
      gains: RegEntry list
      map: RegMap }

let batchMap: BatchMap =
    let id = roConst "id" 0x000UL 0xAB12C001UL
    let start = pulseBit "start" 0x000UL 0
    let busy = roField "busy" 0x004UL 0 1
    let doneIrq = w1cBit "doneIrq" 0x008UL 0
    let srcAddr = rwReg "srcAddr" 0x00CUL 32 0UL
    let dstAddr = rwReg "dstAddr" 0x010UL 32 0UL
    let frameCount = rwReg "frameCount" 0x014UL 32 0UL

    // Every DSP default is a no-op, so a freshly loaded bitstream copies its
    // input to its output untouched — measurably bit-exact, which is what lets
    // "did the DDR path work" stay a separate question from "did the
    // compressor work" now that there is no bypass mux to answer it.
    let threshold = rwReg "threshold" 0x01CUL sampleWidth ((1UL <<< sampleWidth) - 1UL)
    let ratio = rwReg "ratio" 0x020UL 8 0UL
    let attack = rwReg "attack" 0x024UL 16 0UL
    let releaseRate = rwReg "releaseRate" 0x028UL 16 0UL

    // Two 16-bit band gains per 32-bit word: left band i in the low half,
    // right band i in the high half, so a host writes one word per band.
    let gains =
        List.init multibandBands (fun i -> rwReg $"gain{i}" (0x040UL + uint64 (i * 4)) 32 (gainUnity ||| (gainUnity <<< 16)))

    { id = id
      start = start
      busy = busy
      doneIrq = doneIrq
      srcAddr = srcAddr
      dstAddr = dstAddr
      frameCount = frameCount
      threshold = threshold
      ratio = ratio
      attack = attack
      releaseRate = releaseRate
      gains = gains
      map =
        { apertureAddrWidth = apertureAddrWidth
          entries =
            [ id; start; busy; doneIrq; srcAddr; dstAddr; frameCount
              threshold; ratio; attack; releaseRate ]
            @ gains } }

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
    designClocked axiClock "AudioBatchAxi" (fun () ->
        let regs = axiLiteSlaveOf batchMap.map

        let bursts = wire "bursts" 32
        // frameCount / framesPerBurst, and framesPerBurst is 32 — a slice.
        pad 32 (slice 31 5 (regs.value batchMap.frameCount)) ==> bursts

        let running = regBit "running"
        let arIssued = reg "ar_issued" 32
        let beatsWritten = reg "beats_written" 32

        // --- the read side: one descriptor per burst -------------------------
        let reqReady = wireBit "req_ready"
        let arMore = wireBit "ar_more"
        (running &&& lt arIssued bursts) ==> arMore

        let reqAddr = wire "req_addr" 32
        // burst stride is beatsPerBurst * 16 bytes = 256, so shift by 8.
        (regs.value batchMap.srcAddr + asUInt (shl 8 (slice 23 0 arIssued))) ==> reqAddr

        let requests: Stream<Expr * Expr> =
            { payload = (reqAddr, lit (uint64 (beatsPerBurst - 1)) 8)
              valid = arMore
              ready = reqReady
              layout = layout2 ("addr", 32) ("len", 8) }

        If (arMore &&& reqReady) (fun () -> arIssued + lit 1UL 32 ==> arIssued)

        let beats = axiMasterReaderBurst 32 beatWidth 4 beatsPerBurst requests

        // --- the DSP ---------------------------------------------------------
        let leftGains = batchMap.gains |> List.map (fun g -> slice 15 0 (regs.value g))
        let rightGains = batchMap.gains |> List.map (fun g -> slice 31 16 (regs.value g))

        let processed, _envelope =
            instanceNamed
                "mb"
                (multibandCompressor "MultibandCompressor8")
                (regs.value batchMap.threshold)
                (regs.value batchMap.ratio)
                (regs.value batchMap.attack)
                (regs.value batchMap.releaseRate)
                leftGains
                rightGains
            |> fun apply -> apply (beatsToFrames beats)

        // --- the write side --------------------------------------------------
        let outBeats = framesToBeats processed

        let wrAddr = wire "wr_addr" 32
        (regs.value batchMap.dstAddr + asUInt (shl 4 (slice 27 0 beatsWritten))) ==> wrAddr

        let wrReady = wireBit "wr_ready"

        let writeBeats: Stream<Expr * Expr * Expr> =
            { payload = (wrAddr, outBeats.payload, lit ((1UL <<< (beatWidth / 8)) - 1UL) (beatWidth / 8))
              valid = outBeats.valid
              ready = wrReady
              layout = axiWriteBeatLayout 32 beatWidth }

        wrReady ==> outBeats.ready

        let writerIdle = axiMasterWriterWithIdle 32 beatWidth 4 writeBeats

        If (outBeats.valid &&& wrReady) (fun () -> beatsWritten + lit 1UL 32 ==> beatsWritten)

        // --- control ---------------------------------------------------------
        let beatsTotal = wire "beats_total" 32
        asUInt (shl 4 (slice 27 0 bursts)) ==> beatsTotal

        let finished = wireBit "finished"
        (running &&& eq beatsWritten beatsTotal &&& writerIdle) ==> finished

        If (regs.pulse batchMap.start) (fun () ->
            lit 1UL 1 ==> running
            lit 0UL 32 ==> arIssued
            lit 0UL 32 ==> beatsWritten)
        Else (fun () -> If finished (fun () -> lit 0UL 1 ==> running))

        regs.drive batchMap.busy running
        regs.setBit batchMap.doneIrq finished

        // A burst is 256 bytes and the stride is 256 bytes, so no burst can
        // cross AXI's 4 KB boundary *provided* the bases are 256-byte aligned.
        // That is the caller's half of the contract, and an unaligned base
        // would corrupt audio rather than fail, so the design says so out loud:
        // checked every cycle in simulation and by the differential in both
        // worlds, and compiled out of the silicon.
        let aligned (e: RegEntry) =
            bnot running ||| eq (slice 7 0 (regs.value e)) (lit 0UL 8)

        assertThat (aligned batchMap.srcAddr) "srcAddr must be 256-byte aligned"
        assertThat (aligned batchMap.dstAddr) "dstAddr must be 256-byte aligned"

        // Likewise the frame count: the fabric processes whole bursts, so a
        // remainder would be silently dropped.
        assertThat
            (bnot running ||| eq (slice 4 0 (regs.value batchMap.frameCount)) (lit 0UL 5))
            "frameCount must be a multiple of framesPerBurst (32)")

/// The same compressor as a bare stream stage, one frame per cycle. This is the
/// twin the batch path is checked against: identical DSP, none of the DDR
/// plumbing, so a mismatch localises to the plumbing rather than to the audio.
let multibandStageRef =
    design "MultibandStageRef" (fun () ->
        let threshold = input "threshold" sampleWidth
        let ratio = input "ratio" 8
        let attack = input "attack" 16
        let releaseRate = input "releaseRate" 16
        let leftGains = List.init multibandBands (fun i -> input $"lg{i}" 16)
        let rightGains = List.init multibandBands (fun i -> input $"rg{i}" 16)

        let stage, _envelope =
            instanceNamed "mb" (multibandCompressor "MultibandCompressor8") threshold ratio attack releaseRate leftGains rightGains
            |> fun apply -> apply (streamInput "in" sampleLayout)

        streamOutput "out" stage)

// ---------------------------------------------------------------------------
// One stage each, as a bare stream design, so the stall-independence property
// can be asked of them one at a time. These exist to localise a defect the
// whole chain shows; they are fixtures, not board designs.

let gainStage =
    design "GainStage" (fun () ->
        let volume = input "volume" 16
        let mute = inputBit "mute"

        streamInput "in" sampleLayout
        |> instanceNamed "g" (audioGain "AudioGain") volume mute
        |> streamOutput "out")

let eqStage =
    design "EqStage" (fun () ->
        let coeffs = [ for n in [ "b0"; "b1"; "b2"; "a1"; "a2" ] -> input n biquadCoeffWidth ]

        streamInput "in" sampleLayout
        |> instanceNamed "eq" (audioEqBand "AudioEqBand") coeffs
        |> streamOutput "out")

let compressorStage =
    design "CompressorStage" (fun () ->
        let threshold = input "threshold" sampleWidth
        let ratio = input "ratio" 8
        let attack = input "attack" 16
        let releaseRate = input "releaseRate" 16
        let makeup = input "makeup" 16

        streamInput "in" sampleLayout
        |> instanceNamed "c" (audioCompressor "AudioCompressor") threshold ratio attack releaseRate makeup
        |> streamOutput "out")

let limiterStage =
    design "LimiterStage" (fun () ->
        let threshold = input "threshold" sampleWidth

        streamInput "in" sampleLayout
        |> instanceNamed "l" (audioLimiter "AudioLimiter") threshold
        |> streamOutput "out")

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
    design "MultibandDrift" (fun () ->
        let run = inputBit "run"
        // Pokeable: move the bubble and watch the stop move with it.
        let stallAt = input "stallAt" 8

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
            instanceNamed
                "mb"
                (multibandCompressor "MultibandCompressor8")
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
        outLeft ==> output "out_left" (SInt sampleWidth)
        outRight ==> output "out_right" (SInt sampleWidth)
        out.valid ==> outputBit "out_valid"
        envelope ==> output "envelope" sampleWidth

        // `advance` one level down, restated here so the counters can see it:
        // a beat accepted last cycle, with the consumer ready now.
        let acceptedLast = regBit "accepted_last"
        If run (fun () -> accepted ==> acceptedLast)

        let dspSteps = reg "dsp_steps" 8
        let pipeSteps = reg "pipe_steps" 8
        If acceptedLast (fun () -> dspSteps + lit 1UL 8 ==> dspSteps)
        If run (fun () -> pipeSteps + lit 1UL 8 ==> pipeSteps)

        // The gap between the two gates, as a port so it is easy to watch.
        let drift = output "drift" 8
        sub pipeSteps dspSteps ==> drift

        // Latch the settled gap once the pipeline is warm, then hold the design
        // to it. A stage whose two halves agree keeps this forever; this one
        // does not, and the assertion is what stops the debugger on the cycle.
        let baseline = reg "drift_baseline" 8
        let armed = regBit "armed"

        If (run &&& eq cycle (lit 8UL 8)) (fun () ->
            drift ==> baseline
            lit 1UL 1 ==> armed)

        assertThat (bnot armed ||| eq drift baseline) "the valid pipe drifted from the DSP path"

        baseline ==> output "drift_baseline_out" 8)

/// The FIR, as a bare stream stage. The third of the three hand-rolled valid
/// delay lines in the audio library, and the only one the sweep did not reach.
let firStage =
    design "FirStage" (fun () ->
        let preset = input "preset" 2

        streamInput "in" sampleLayout
        |> instanceNamed "fir" (audioFir "AudioFir" 16 48_000.0 4_000.0 400.0) preset
        |> streamOutput "out")
