/// The audio example's executable: the living checks, and the emit that writes
/// the Verilog plus the generated Rust register layouts.
module Warp11.Effects.Main

open Warp11

let private designs =
    [ "AudioToneAxi", audioToneAxi.def
      "AudioPassthruAxi", audioPassthruAxi.def
      "AudioGainAxi", audioGainAxi.def
      "AudioEffectsAxi", audioEffectsAxi.def
      "AudioBatchAxi", Batch.audioBatchAxi.def ]

/// Every register's reset value must be a no-op, because these bitstreams ship
/// without a host daemon: load one and it has to pass audio (or, for the tone,
/// make a sound) with nothing written to it. A default that clamps, mutes or
/// zeroes would look exactly like broken hardware on a bench.
let private defaultsArePassthrough () : bool =
    let sim = Sim audioEffectsAxi.def
    // No AXI writes at all — read the reset state straight out of the slave.
    sim.Tick()

    let isUnity (e: RegEntry) expected =
        match e.kind with
        | RwReg (_, init) -> init = expected
        | _ -> false

    isUnity effectsRegs.volume gainUnity
    && isUnity effectsRegs.mute 0UL
    && isUnity effectsRegs.eq[0] biquadUnity
    && List.forall (fun e -> isUnity e 0UL) (List.skip 1 effectsRegs.eq)
    && isUnity effectsRegs.compRatio 0UL
    && isUnity effectsRegs.compMakeup gainUnity
    && isUnity effectsRegs.compThreshold ((1UL <<< sampleWidth) - 1UL)
    && isUnity effectsRegs.limitThreshold ((1UL <<< (sampleWidth - 1)) - 1UL)

/// The four slaves must not collide with each other's conventions or with
/// themselves: every entry word-aligned and inside its aperture, and no two
/// entries of one map sharing a word unless they are bit fields. `RegMap`'s own
/// validation runs at elaboration, so reaching here already proves most of it —
/// this pins the part that is a *convention* rather than a rule.
let private mapsAreWellFormed () : bool =
    let maps =
        [ "tone", toneMap
          "passthru", passthruMap
          "gain", gainMap
          "effects", effectsMap ]

    maps
    |> List.forall (fun (_, m) ->
        let aperture = 1UL <<< m.apertureAddrWidth

        m.entries
        |> List.forall (fun e -> e.offset % 4UL = 0UL && e.offset < aperture))

// ---------------------------------------------------------------------------
// The batch path, through real AXI transactions against a behavioural DDR.

let private sampleMask = (1UL <<< sampleWidth) - 1UL

/// Stage `n` random stereo frames into the DDR model at `at`, and hand back the
/// 24-bit samples that went in.
let private stageFrames (ddr: SimAxiDdr) (at: int) (n: int) (seed: int) =
    let rand = System.Random seed

    [| for i in 0 .. n - 1 ->
        // A quarter of full scale, so the compressor has room to work and the
        // sign bit is exercised on both lanes.
        let l = uint64 (rand.Next(-4194304, 4194304)) &&& sampleMask
        let r = uint64 (rand.Next(-4194304, 4194304)) &&& sampleMask

        // Sign-extended into the 32-bit lane, which is what the fabric writes
        // back and what the host reads as a plain i32.
        let lane (v: uint64) =
            if v >= (1UL <<< (sampleWidth - 1)) then uint32 v ||| 0xFF000000u else uint32 v

        ddr.WriteWord(at + i * 8, lane l)
        ddr.WriteWord(at + i * 8 + 4, lane r)
        l, r |]

/// Drive `audioBatchAxi` over AXI-Lite: stage the frames, point it at them,
/// pulse start, wait for busy to clear, read the results back out of the same
/// behavioural DDR.
let private runBatchPaced (flat: bool) (frames: int) (seed: int) (jitter: int option) =
    let sim = Sim Batch.audioBatchAxi.def
    let src = 0x1000
    let dst = 0x9000
    let ddr = SimAxiDdr(sim, 0x20000, ?jitter = jitter)
    let input = stageFrames ddr src frames seed

    let axi = SimAxi.clientWith sim ddr.Cycle
    let m = Batch.batchRegs
    let idOk = axi.read32 m.id.offset = 0xAB12C001UL

    axi.write32 m.srcAddr.offset (uint64 src)
    axi.write32 m.dstAddr.offset (uint64 dst)
    axi.write32 m.frameCount.offset (uint64 frames)
    // "Flat" is now a configuration rather than a route around the DSP:
    // threshold at full scale with ratio 0 is no gain reduction whatever the
    // signal, which this chain reassembles bit-exactly.
    axi.write32 m.threshold.offset (if flat then (1UL <<< sampleWidth) - 1UL else 200_000UL)
    axi.write32 m.ratio.offset (if flat then 0UL else 4UL)
    axi.write32 m.attack.offset (1UL <<< 14)
    axi.write32 m.releaseRate.offset (1UL <<< 12)
    axi.write32 m.start.offset 1UL

    // Bounded: a design that never lowers busy is a bug, not a reason to hang.
    let mutable spins = 0

    while axi.read32 m.busy.offset <> 0UL && spins < 400_000 do
        ddr.Cycle()
        spins <- spins + 1

    let out =
        [| for i in 0 .. frames - 1 ->
            uint64 (ddr.ReadWord(dst + i * 8)) &&& sampleMask,
            uint64 (ddr.ReadWord(dst + i * 8 + 4)) &&& sampleMask |]

    idOk, input, out, spins

/// Bypassed, the accelerator is a memcpy with a 128-bit beat in the middle.
/// Byte-exact output is what says the burst read, the beat/frame unpack, the
/// repack and the write address arithmetic all agree — with the DSP taken out
/// of the question entirely, so a failure here is plumbing and nothing else.
let private runBatch flat frames seed = runBatchPaced flat frames seed None



let private batchCopiesWhenFlat () =
    let idOk, input, out, spins = runBatch true 64 7
    idOk && spins < 400_000 && input = out

/// Unbypassed, every frame must equal what the bare stage produced from the
/// same sequence. The DSP is deterministic and both see identical frames in
/// identical order, so anything but equality means the DDR path reordered,
/// dropped or duplicated a frame.
let private batchMatchesTheStage () =
    let _, input, out, spins = runBatch false 64 11

    let sim = Sim Batch.multibandStageRef.def
    sim.Poke("threshold", 200_000UL)
    sim.Poke("ratio", 4UL)
    sim.Poke("attack", 1UL <<< 14)
    sim.Poke("releaseRate", 1UL <<< 12)

    for i in 0 .. multibandBands - 1 do
        sim.Poke($"lg{i}", gainUnity)
        sim.Poke($"rg{i}", gainUnity)

    sim.Poke("out_ready", 1UL)
    sim.Poke("in_valid", 1UL)

    let expected = ResizeArray()

    let collect () =
        if sim.Peek "out_valid" = 1UL then
            expected.Add(sim.Peek "out_left", sim.Peek "out_right")

    for l, r in input do
        sim.Poke("in_left", l)
        sim.Poke("in_right", r)
        sim.Tick()
        collect ()

    // Drain whatever is still inside the stage's pipeline.
    sim.Poke("in_valid", 0UL)
    let mutable guard = 0

    while expected.Count < input.Length && guard < 10_000 do
        sim.Tick()
        collect ()
        guard <- guard + 1

    spins < 400_000 && Array.ofSeq expected = out

/// **The property the whole stream layer rests on**: a stage advances on
/// `valid && ready`, not on the clock, so how fast the beats arrive cannot
/// change what comes out. The always-ready DDR model runs this design at one
/// frame per cycle; real DDR does not, and the board measured 3.3 cycles per
/// frame. If any state in the chain is clocked rather than gated, those two
/// produce different audio — which is a bug you cannot see until silicon,
/// unless the model is paced.
let private pacingDoesNotChangeTheAudio () =
    let baseline = let _, _, out, _ = runBatchPaced false 64 11 None in out

    // Every seed must agree with the always-ready run. A design whose output
    // depends on when the memory answered is a design that will behave one way
    // in this suite and another on a board.
    [ 1..8 ]
    |> List.forall (fun seed ->
        let _, _, out, _ = runBatchPaced false 64 11 (Some seed)
        out = baseline)

/// Splits the blame for `pacingDoesNotChangeTheAudio`. Bypassed, the same
/// stalls run through the same beat/frame plumbing with the DSP taken out — so
/// if this is byte-exact and the compressed one is not, the plumbing is fine
/// and the state that moves under backpressure is inside the audio chain.
let private pacedFlatStillCopies () =
    [ 1..8 ]
    |> List.forall (fun seed ->
        let _, input, out, _ = runBatchPaced true 64 7 (Some seed)
        input = out)

// ---------------------------------------------------------------------------
// Stall-independence, at the stage rather than through the DDR.
//
// The batch accelerator says *that* something in the audio chain moves under
// backpressure; this says *which*. No AXI, no memory model — just a stage, its
// two wires, and a stall pattern.

/// Drive a stereo stream design and collect the beats that actually transfer.
/// With `seed = None` the producer always offers and the consumer always takes,
/// which is the one-beat-per-cycle case every check has used until now. With a
/// seed, both sides stall randomly — and a stage that advances on `valid &&
/// ready` must produce exactly the same sequence either way.
let private runStageStalled (d: ModuleDef) (setup: Sim -> unit) (samples: (uint64 * uint64)[]) (seed: int option) =
    let sim = Sim(d)
    setup sim
    let rng = seed |> Option.map System.Random
    let out = ResizeArray<uint64 * uint64>()
    let mutable fed = 0
    let mutable guard = 0

    while out.Count < samples.Length && guard < 200_000 do
        let offer = fed < samples.Length && (match rng with Some r -> r.Next(0, 4) > 0 | None -> true)
        let take = match rng with Some r -> r.Next(0, 4) > 0 | None -> true

        sim.Poke("in_valid", (if offer then 1UL else 0UL))
        sim.Poke("out_ready", (if take then 1UL else 0UL))

        if offer then
            let l, r = samples[fed]
            sim.Poke("in_left", l)
            sim.Poke("in_right", r)

        // Peek the settled combinational state, decide the transfers, then let
        // the edge happen — which is the order the handshake is defined in.
        if take && sim.Peek "out_valid" = 1UL then
            out.Add(sim.Peek "out_left", sim.Peek "out_right")

        let accepted = offer && sim.Peek "in_ready" = 1UL
        sim.Tick()

        if accepted then
            fed <- fed + 1

        guard <- guard + 1

    out.ToArray()

let private stereoSamples (n: int) (seed: int) =
    let rand = System.Random seed
    let mask = (1UL <<< sampleWidth) - 1UL

    [| for _ in 1..n ->
        uint64 (rand.Next(-4194304, 4194304)) &&& mask, uint64 (rand.Next(-4194304, 4194304)) &&& mask |]

/// The property: stalling a stage changes when its beats move, never what they
/// are. Returns true if every stall pattern agrees with the unstalled run.
let private stageIsStallIndependent (d: ModuleDef) (setup: Sim -> unit) =
    let samples = stereoSamples 48 3
    let baseline = runStageStalled d setup samples None

    baseline.Length = samples.Length
    && ([ 1..6 ] |> List.forall (fun seed -> runStageStalled d setup samples (Some seed) = baseline))

/// Can the DSP be made transparent by *configuration* rather than by routing
/// around it? Threshold at full scale with ratio 0 is "no gain reduction
/// whatever the signal", gains are unity, so the chain should be a no-op. If
/// the output equals the input the bypass mux is redundant and the parallel
/// path it needs could go; if it does not, the subtractive crossover does not
/// reassemble bit-exactly in fixed point and a raw copy is the only way to get
/// one.
let private unitySettingsPassAudioThrough () =
    let samples = stereoSamples 64 5
    let got = runStageStalled Batch.multibandStageRef.def (fun s ->
        s.Poke("threshold", (1UL <<< sampleWidth) - 1UL)
        s.Poke("ratio", 0UL)
        s.Poke("attack", 0UL)
        s.Poke("releaseRate", 0UL)
        for i in 0 .. multibandBands - 1 do
            s.Poke($"lg{i}", gainUnity)
            s.Poke($"rg{i}", gainUnity)) samples None

    let differing = Seq.zip got samples |> Seq.filter (fun (a, b) -> a <> b) |> Seq.length
    printfn $"      unity-config passthrough: %d{differing} of %d{samples.Length} frames differ from the input"
    differing = 0

let private multibandSetup (sim: Sim) =
    sim.Poke("threshold", 200_000UL)
    sim.Poke("ratio", 4UL)
    sim.Poke("attack", 1UL <<< 14)
    sim.Poke("releaseRate", 1UL <<< 12)

    for i in 0 .. multibandBands - 1 do
        sim.Poke($"lg{i}", gainUnity)
        sim.Poke($"rg{i}", gainUnity)

let private multibandIsStallIndependent () =
    stageIsStallIndependent Batch.multibandStageRef.def multibandSetup

/// The same question of each stage on its own, so a failure names one entry in
/// the audio stdlib rather than "somewhere in the chain".
let private stageStallReport () =
    let stages =
        [ "gain", Batch.gainStage.def, (fun (sim: Sim) ->
            sim.Poke("volume", gainUnity)
            sim.Poke("mute", 0UL))
          "eq (biquad)", Batch.eqStage.def, (fun (sim: Sim) ->
            sim.Poke("b0", biquadUnity)
            for n in [ "b1"; "b2"; "a1"; "a2" ] do sim.Poke(n, 0UL))
          "compressor", Batch.compressorStage.def, (fun (sim: Sim) ->
            sim.Poke("threshold", 200_000UL)
            sim.Poke("ratio", 4UL)
            sim.Poke("attack", 1UL <<< 14)
            sim.Poke("releaseRate", 1UL <<< 12)
            sim.Poke("makeup", gainUnity))
          "limiter", Batch.limiterStage.def, (fun (sim: Sim) ->
            sim.Poke("threshold", (1UL <<< (sampleWidth - 1)) - 1UL))
          "fir", Batch.firStage.def, (fun (sim: Sim) -> sim.Poke("preset", 0UL))
          "multiband", Batch.multibandStageRef.def, multibandSetup ]

    let mutable allOk = true

    for name, d, setup in stages do
        let ok = stageIsStallIndependent d setup
        printfn $"      stall-independent: %-14s{name} %b{ok}"
        allOk <- allOk && ok

    allOk

let private checks =
    [ "effects defaults are passthrough", defaultsArePassthrough
      "register maps well formed", mapsAreWellFormed
      "batch: flat settings copy exactly", batchCopiesWhenFlat
      "batch: DSP matches the stage", batchMatchesTheStage
      "batch: pacing changes nothing", pacingDoesNotChangeTheAudio
      "batch: flat copy survives jitter", pacedFlatStillCopies
      "audio: every stage is stall-independent", stageStallReport
      "audio: unity settings pass audio through", unitySettingsPassAudioThrough ]

/// The seam: each design's Verilog beside a Rust layout generated from the very
/// map the slave was elaborated from. Nothing in public warp11 drives these
/// boards from a host, but the layout is what makes that possible without
/// anyone transcribing an offset by hand — and a transcribed offset is the
/// failure that costs a board reboot.
let private writeHardware (repoRoot: string) =
    let buildDir = System.IO.Path.Combine(repoRoot, "hardware", "build")
    let runtimeSrc = System.IO.Path.Combine(repoRoot, "runtime", "core", "src")
    System.IO.Directory.CreateDirectory buildDir |> ignore

    for name, design in designs do
        let path = System.IO.Path.Combine(buildDir, $"{name}.v")
        System.IO.File.WriteAllText(path, emitDesign design + "\n")
        printfn $"wrote {path}"

    let layoutFor (title: string) (m: RegMap) =
        [ $"//! Register map for the `{title}` AXI-Lite slave."
          "//! Generated by `dotnet run -- hardware <repo-root>` in hdl/Warp11.Effects."
          "//! Do not edit by hand — changes will be overwritten on next emit."
          "" ]
        @ regMapRsLines m

    let layouts =
        [ "audio_tone_layout.rs", "AudioToneAxi", toneMap
          "audio_passthru_layout.rs", "AudioPassthruAxi", passthruMap
          "audio_gain_layout.rs", "AudioGainAxi", gainMap
          "audio_effects_layout.rs", "AudioEffectsAxi", effectsMap
          "audio_batch_layout.rs", "AudioBatchAxi", Batch.batchMap ]

    for file, title, m in layouts do
        let path = System.IO.Path.Combine(runtimeSrc, file)
        System.IO.File.WriteAllText(path, String.concat "\n" (layoutFor title m) + "\n")
        printfn $"wrote {path}"

/// The sample recording that ships beside these designs, and the code that made
/// it — so `in.wav` in this project is a build product rather than a blob
/// nobody can regenerate.
///
/// **A plucked string, by Karplus-Strong.** A buffer of noise is circulated
/// round a delay line one period long, averaged two taps at a time on the way
/// past. The average is a lowpass, so the highs die before the fundamental
/// does, which is what makes it sound plucked rather than like a filtered
/// click. Fifteen lines for something recognisably a guitar.
///
/// **Why not tones.** A steady sine is the worst possible signal to judge an
/// effect by: it has no transient for a compressor to catch, no harmonics for a
/// clipper to add to, and nothing for a filter to move. A plucked string has
/// all three — an attack, a decaying spectrum and a tail — so what the fabric
/// does to it is audible rather than merely measurable.
///
/// The random buffer comes from a hand-rolled xorshift rather than
/// `System.Random`, so the file is byte-reproducible by anyone on any runtime:
/// `sample` regenerating something different from what is committed would make
/// the committed file unverifiable.
let private sampleWav () : WavData =
    // The rate the link actually frames at, so `listen` has nothing to warn
    // about and the file cannot outlive the divisors that set it.
    let rate = int stockSampleRate
    let seconds = 2.0
    let frames = int (float rate * seconds)

    let pluck (hz: float) (count: int) (seed: uint32) =
        let period = max 2 (int (float rate / hz))

        // xorshift32, three lines and identical everywhere.
        let mutable state = seed
        let next () =
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            float state / float System.UInt32.MaxValue * 2.0 - 1.0

        let ring = Array.init period (fun _ -> next ())
        let out = Array.zeroCreate<float> count
        let mutable at = 0

        for i in 0 .. count - 1 do
            let current = ring[at]
            out[i] <- current
            // The 0.9995 is the string's damping; the average either side of it
            // is the string itself.
            ring[at] <- 0.9995 * 0.5 * (current + ring[(at + 1) % period])
            at <- (at + 1) % period

        out

    // An E minor arpeggio, then the chord — six notes to walk up and something
    // to ring out under the last half second, which is where a compressor's
    // release and (later) an echo's tail are audible.
    let notes =
        [ 0.00, 82.41, 0.70 // E2
          0.22, 123.47, 0.60 // B2
          0.44, 164.81, 0.60 // E3
          0.66, 196.00, 0.60 // G3
          0.88, 246.94, 0.60 // B3
          1.10, 329.63, 0.50 // E4
          1.40, 82.41, 0.70 // the chord
          1.40, 164.81, 0.50
          1.40, 196.00, 0.50
          1.40, 246.94, 0.45 ]

    let left = Array.zeroCreate<float> frames
    let right = Array.zeroCreate<float> frames

    notes
    |> List.iteri (fun index (at, hz, level) ->
        let start = int (at * float rate)
        let voice = pluck hz (frames - start) (uint32 index * 2_654_435_761u + 1u)
        // Alternating emphasis, so the pair is wide enough that a swapped
        // channel is audible.
        let toLeft, toRight = if index % 2 = 0 then 1.0, 0.75 else 0.75, 1.0

        for i in 0 .. voice.Length - 1 do
            left[start + i] <- left[start + i] + level * toLeft * voice[i]
            right[start + i] <- right[start + i] + level * toRight * voice[i])

    // Normalised together rather than per channel, so the stereo image survives.
    let loudest =
        Array.fold (fun peak v -> max peak (abs v)) 0.0 left
        |> fun peak -> Array.fold (fun peak v -> max peak (abs v)) peak right

    let scale = 0.85 / loudest
    let samples = Array.zeroCreate<int16> (frames * 2)

    let quantise (v: float) =
        int16 (max -32768.0 (min 32767.0 (v * scale * 32767.0)))

    for i in 0 .. frames - 1 do
        samples[i * 2] <- quantise left[i]
        samples[i * 2 + 1] <- quantise right[i]

    { sampleRate = rate
      channels = 2
      bitsPerSample = 16
      samples = samples }

/// Peak over RMS, in dB. The number a clipper moves and a filter does not: a
/// plucked string is around 18 dB, a square wave is 0, so it says how far
/// towards square a stage has taken the signal.
let private crestDb (w: WavData) =
    let squares = w.samples |> Array.sumBy (fun v -> let x = float v in x * x)
    let rms = sqrt (squares / float w.samples.Length)
    let peak = w.samples |> Array.fold (fun m v -> max m (abs (float v))) 0.0
    if rms <= 0.0 then nan else 20.0 * log10 (peak / rms)

let private eqCoeffNames = [ "b0"; "b1"; "b2"; "a1"; "a2" ]

/// The chain's audible half, written out as files to play against each other.
///
/// `wav` measures the compressor, which is the effect you can put a number on
/// and the one you cannot hear: at this threshold and slope it moves a plucked
/// string by under 2 dB. This verb is the other half — clipping and filtering,
/// which are unmistakable — and **every stage in it already ships**. There is
/// no new hardware here, only settings.
///
/// **Overdrive is the gain stage, not a new one.** `audioGain` saturates above
/// unity, so a volume of 8x is a signal driven into the rail and clipped there.
/// That is what an overdrive pedal is, and it comes out loudness-matched for
/// free because the output sits at full scale either way — which is what makes
/// the A/B honest rather than just louder.
///
/// **The wah is the seam.** The host designs a fresh peaking biquad per block
/// and writes its five coefficients while the fabric filters; the fabric never
/// computes a cosine. That is the same division of labour the register map
/// exists for, running at audio rate — and it is why the sweep costs no
/// hardware at all.
let private fx (inPath: string) (prefix: string) =
    let input = readWavFile inPath
    printfn $"in:  {input.FrameCount} frames, {input.sampleRate} Hz, {input.channels} ch"

    let report (label: string) (path: string) (w: WavData) =
        writeWavFile path w
        let left, right = peaks w
        printfn $"  %-10s{label} crest %5.1f{crestDb w} dB   peaks {left}/{right}   {path}"

    // Unity is a documented pass-through, so this doubles as a check that the
    // stage is transparent when it is told to be.
    let through (volume: uint64) =
        let sim = Sim Batch.gainStage.def
        sim.Poke("volume", volume)
        sim.Poke("mute", 0UL)
        runWavThroughSim sim defaultWavPorts 0 input

    // One sweep up and back across the file, geometric so it moves by octaves
    // rather than by hertz — a linear sweep spends most of its time up top
    // where there is nothing to hear.
    let wah () =
        let sim = Sim Batch.eqStage.def
        let blockFrames = 512
        let blocks = (input.FrameCount + blockFrames - 1) / blockFrames
        let heard = ResizeArray<int16>(input.samples.Length)

        for block in 0 .. blocks - 1 do
            let start = block * blockFrames
            let count = min blockFrames (input.FrameCount - start)
            let phase = float block / float blocks
            let travel = 0.5 - 0.5 * cos (2.0 * System.Math.PI * phase)
            let centreHz = 300.0 * ((3000.0 / 300.0) ** travel)

            rbjDesign Peaking centreHz 6.0 10.0 (float input.sampleRate)
            |> toQ230
            |> List.iter2 (fun name value -> sim.Poke(name, value)) eqCoeffNames

            // The same `sim` across every block, so the biquad's state carries
            // over: a filter reset each block would click at every boundary.
            let slice = { input with samples = Array.sub input.samples (start * 2) (count * 2) }
            heard.AddRange (runWavThroughSim sim defaultWavPorts 0 slice).samples

        { input with samples = heard.ToArray() }

    let source = "(source)"
    let clean = through gainUnity

    report "clean" $"{prefix}-clean.wav" clean

    // Unity is documented as a pass-through; on real audio it is worth saying
    // whether it actually was, because "nearly" and "exactly" are different
    // claims and only one of them is this stage's.
    if clean.samples = input.samples then
        printfn "             ...identical to the source, sample for sample"
    report "overdrive" $"{prefix}-drive.wav" (through (8UL * gainUnity))
    report "fuzz" $"{prefix}-fuzz.wav" (through (48UL * gainUnity))
    // Silence on the end, so the repeats have somewhere to ring out. Without it
    // the tail is cut at the last frame of the source and an echo sounds like a
    // stutter that stops.
    let withTail (milliseconds: float) (w: WavData) =
        let extra = int (milliseconds / 1000.0 * float w.sampleRate)
        { w with samples = Array.append w.samples (Array.zeroCreate (extra * 2)) }

    // Delay in samples, from a time and the file's own rate. A number of
    // samples typed here would mean something different the moment either moved.
    let echo (milliseconds: float) (feedback: uint64) =
        let sim = Sim Batch.echoStage.def
        let source = withTail (4.0 * milliseconds) input
        sim.Poke("delay", uint64 (milliseconds / 1000.0 * float input.sampleRate))
        sim.Poke("feedback", feedback)
        runWavThroughSim sim defaultWavPorts 0 source

    report "wah" $"{prefix}-wah.wav" (wah ())

    // Both are the same stage at different settings: this topology feeds its
    // own output back, so every repeat is `feedback` times the last and there
    // is no single-repeat setting. A short delay with a low feedback is what
    // slapback actually is.
    report "echo" $"{prefix}-echo.wav" (echo 320.0 (115UL * gainUnity / 256UL))
    report "slapback" $"{prefix}-slap.wav" (echo 90.0 (77UL * gainUnity / 256UL))
    printfn $"  %-10s{source} crest %5.1f{crestDb input} dB"
    0

/// A recording playing into the codec pins of a design open in the debugger.
///
/// The pin-level counterpart of the `wav` verb below, and the reason
/// `ISimDevice` exists. `wav` drives the DSP stage's *stream* ports, so `i2sRx`
/// and `i2sTx` are not in its path and there is nothing to look at until the
/// run has finished. This drives the pins, so the clock generator and both
/// framers are in the path, and the device rides the debugger's own clock —
/// stepping one cycle steps the recording with it, and a breakpoint stops the
/// audio where it stops the design.
///
/// **It is not a way to listen to a file.** A frame is `fabricHz / sampleRate`
/// cycles — 2,048 here — so a second of audio is a hundred million of them and
/// the debugger runs this design at about 153k a second. Open a short clip,
/// stop on the frame
/// that interests you, and reach for `runWavThroughI2s` when what you want is
/// the whole file processed.
let private listen (inPath: string) (outPath: string option) =
    let input = readWavFile inPath
    // Derived rather than written down: the link frames at the rate its
    // divisors make, and a number typed here would outlive the divisors.
    let cyclesPerFrame = board.fabricHz / int stockSampleRate

    printfn $"in:  {input.FrameCount} frames, {input.sampleRate} Hz, {input.channels} ch"
    printfn $"     {cyclesPerFrame} cycles a frame, {input.FrameCount * cyclesPerFrame} to play it out"

    // The link frames at the board's rate whatever the file says, and the
    // recording that comes back carries the file's header. A file at another
    // rate still plays; it just comes back at a pitch this says out loud.
    if abs (float input.sampleRate - stockSampleRate) > 1.0 then
        printfn $"     note: the link frames at %.3f{stockSampleRate} Hz, not the file's {input.sampleRate}"

    // The settings are AXI-Lite registers, and the debugger's watch panel drives
    // *inputs*. So this window shows the chain running at its no-op resets and
    // there is nothing in it to turn — worth saying rather than leaving someone
    // hunting the watch list for a field beside `volume`.
    printfn "     filter the signals for 'left' and watch audio_rx_out_left through limiter_out_left"
    printfn "     to see one sample move down the chain, at the registers' no-op reset settings"

    let source = WavI2sSource(separateCodecSimPins, input)

    let code =
        Warp11.SimView.Desktop.debugWith
            "a WAV on the codec pins"
            audioEffectsAxi.def
            [ source.Attach ]

    // Read once the window has closed rather than from inside it: `Output` is
    // what has been heard *so far*, and what a run produced is only settled
    // when the run is over.
    match outPath, source.Output with
    | Some path, Some heard ->
        writeWavFile path heard
        let inLeft, inRight = peaks input
        let outLeft, outRight = peaks heard
        printfn $"out: {heard.FrameCount} frames, peaks {inLeft}/{inRight} -> {outLeft}/{outRight}"

        // Both ends of the run need saying, because the peaks above compare the
        // whole input against whatever was heard and neither case is visible in
        // the numbers. Close the window early and that is a whole file against
        // part of one; leave it running past the end and the model holds its
        // last frame, so the tail is neither the recording nor silence.
        let unplayed = input.FrameCount - source.FramesPlayed
        let past = heard.FrameCount - source.FramesPlayed

        if unplayed > 0 then
            printfn $"     the window closed with {unplayed} frames still to play, so that compares the whole file against part of it"
        elif past > 0 then
            printfn $"     the last {past} are past the recording, where the model repeats its final frame"

        printfn $"wrote {path}"
    | Some path, None -> printfn $"nothing was heard, so {path} was not written"
    | None, _ -> ()

    code

[<EntryPoint>]
let main argv =
    match argv with
    // A debugger on one of this project's designs. `drift` is the one to open:
    // it drives the multiband stage itself and asserts the property that fails,
    // so pressing Run stops on the cycle the divergence appears.
    // Headless: run the drift harness and report where it breaks, so the
    // debugger's stopping point can be checked without opening a window.
    | [| "drift" |] ->
        let sim = Sim(Batch.driftHarness.def, checkAsserts = true)
        sim.Poke("stallAt", 20UL)
        sim.Poke("run", 1UL)
        let mutable c = 0

        while c < 40 && sim.ViolationCount = 0 do
            sim.Tick()
            c <- c + 1
            let cyc = sim.Peek "cycle"
            let drift = sim.Peek "drift"
            let baseline = sim.Peek "drift_baseline_out"
            let dsp = sim.Peek "dsp_steps"
            let pipe = sim.Peek "pipe_steps"
            printfn $"  cycle %2d{int cyc}  drift=%d{drift} baseline=%d{baseline}  dsp={dsp} pipe={pipe}"

        match sim.LastViolation with
        | Some (msg, cyc) -> printfn $"STOPPED at cycle {cyc}: {msg}"
        | None -> printfn "no violation in 40 cycles"

        0
    // A debugger on one of this project's designs, with the catalog behind it
    // so a design arrives with its signals already picked and its inputs
    // already driven. `drift` is the one to open: it drives the multiband stage
    // itself and asserts the property that fails, so pressing Run stops on the
    // cycle the divergence appears rather than leaving you to spot it.
    // Is the accelerator waiting on memory or on its own arithmetic? The board
    // measures 3.22 cycles a frame; the datapath is one frame per cycle, so
    // something costs the other 2.2. Running the same design against DDR models
    // of varying quality says which — a fabric-bound design does not care how
    // fast memory answers.
    | [| "batch-perf" |] ->
        let frames = 512

        let cyclesFor (label: string) (mk: Sim -> SimAxiDdr) =
            let sim = Sim Batch.audioBatchAxi.def
            let ddr = mk sim
            let src, dst = 0x1000, 0x9000
            let axi = SimAxi.clientWith sim ddr.Cycle
            let m = Batch.batchRegs
            axi.write32 m.srcAddr.offset (uint64 src)
            axi.write32 m.dstAddr.offset (uint64 dst)
            axi.write32 m.frameCount.offset (uint64 frames)
            axi.write32 m.threshold.offset 200_000UL
            axi.write32 m.ratio.offset 4UL
            axi.write32 m.attack.offset (1UL <<< 14)
            axi.write32 m.releaseRate.offset (1UL <<< 12)
            axi.write32 m.start.offset 1UL

            let mutable c = 0

            while sim.Peek "running" = 1UL && c < 400_000 do
                ddr.Cycle()
                c <- c + 1

            printfn $"  %-34s{label} %6d{c} cycles   %5.2f{float c / float frames} cyc/frame"
            float c / float frames

        printfn $"  {frames} frames through AudioBatchAxi\n"
        let ideal = cyclesFor "ideal DDR (0 latency, always ready)" (fun sim -> SimAxiDdr(sim, 0x20000))
        let r8 = cyclesFor "read latency 8" (fun sim -> SimAxiDdr(sim, 0x20000, rDelay = 8))
        let r32 = cyclesFor "read latency 32" (fun sim -> SimAxiDdr(sim, 0x20000, rDelay = 32))
        let wSlow = cyclesFor "write accepts every 4th cycle" (fun sim -> SimAxiDdr(sim, 0x20000, awEvery = 4, wEvery = 4))
        let bSlow = cyclesFor "write response delayed 16" (fun sim -> SimAxiDdr(sim, 0x20000, bDelay = 16))

        printfn ""
        printfn $"  the fabric's own floor is %.2f{ideal} cyc/frame; the board measures 3.22"
        printfn $"  read latency 8 -> 32 moves it by %.2f{r32 - r8}"
        printfn $"  a 4x slower write channel moves it by %.2f{wSlow - ideal}"
        printfn $"  a 16-cycle write response moves it by %.2f{bSlow - ideal}"
        0
    | [| "debug" |]
    | [| "debug"; _ |] ->
        let catalog =
            Warp11.Catalog.embedded
                (System.Reflection.Assembly.GetExecutingAssembly())
                "Batch.fs"
                [ Warp11.Catalog.entry "Drift (start here)" (nameof Batch.driftHarness) (fun () -> Batch.driftHarness.def)
                  |> Warp11.Catalog.watching [ "drift"; "drift_baseline_out"; "dsp_steps"; "pipe_steps"; "cycle"; "offering"; "accepted" ]
                  |> Warp11.Catalog.poking [ "run", 1UL; "stallAt", 20UL ]
                  Warp11.Catalog.entry "Multiband stage" (nameof Batch.multibandStageRef) (fun () -> Batch.multibandStageRef.def)
                  |> Warp11.Catalog.poking
                      [ "bypass", 0UL; "threshold", 200_000UL; "ratio", 4UL; "attack", 1UL <<< 14; "releaseRate", 1UL <<< 12 ]
                  Warp11.Catalog.entry "Gain" (nameof Batch.gainStage) (fun () -> Batch.gainStage.def)
                  |> Warp11.Catalog.poking [ "volume", gainUnity; "mute", 0UL ]
                  Warp11.Catalog.entry "EQ (biquad)" (nameof Batch.eqStage) (fun () -> Batch.eqStage.def)
                  |> Warp11.Catalog.poking [ "b0", biquadUnity ]
                  Warp11.Catalog.entry "Compressor" (nameof Batch.compressorStage) (fun () -> Batch.compressorStage.def)
                  |> Warp11.Catalog.poking
                      [ "threshold", 200_000UL; "ratio", 4UL; "attack", 1UL <<< 14; "releaseRate", 1UL <<< 12; "makeup", gainUnity ]
                  Warp11.Catalog.entry "Limiter" (nameof Batch.limiterStage) (fun () -> Batch.limiterStage.def)
                  |> Warp11.Catalog.poking [ "threshold", (1UL <<< (sampleWidth - 1)) - 1UL ]
                  Warp11.Catalog.entry "Batch accelerator" (nameof Batch.audioBatchAxi) (fun () -> Batch.audioBatchAxi.def) ]

        let initial = if argv.Length > 1 then Some argv[1] else None
        // The window's own exit code is the process's, as in the other two
        // debugger hosts. Discarding it and returning 0 was what the
        // implicit-ignore warning here was pointing at.
        Warp11.SimView.Desktop.run (Warp11.SimView.View.FromCatalog(catalog, initial)) []
    // The simulator's answer for the same file the board processes, so the two
    // can be diffed byte for byte. `settleCycles = 0` because the batch design
    // does not pre-run its first frame either — the comparison is only honest
    // if both see exactly the same sequence.
    // The debugger with the recording attached. `out.wav` is optional: leave it
    // off to step and look, give it to keep what came back.
    | [| "listen"; inPath |] -> listen inPath None
    | [| "listen"; inPath; outPath |] -> listen inPath (Some outPath)
    // The audible demo: one file in, four out, nothing but settings between
    // them.
    | [| "fx"; inPath; prefix |] -> fx inPath prefix
    // Rewrite the shipped `in.wav`. Committed so the commands above run as
    // written, regenerable so it is not a file of unknown provenance.
    | [| "sample"; outPath |] ->
        let sample = sampleWav ()
        writeWavFile outPath sample
        let left, right = peaks sample
        printfn $"wrote {outPath}: {sample.FrameCount} frames, {sample.sampleRate} Hz, 2 ch, peaks {left}/{right}"
        0
    | [| "wav"; inPath; outPath |] ->
        let input = readWavFile inPath
        printfn $"in:  {input.FrameCount} frames, {input.sampleRate} Hz, {input.channels} ch"

        let sim = Sim Batch.multibandStageRef.def
        sim.Poke("threshold", 200_000UL)
        sim.Poke("ratio", 4UL)
        sim.Poke("attack", 1UL <<< 14)
        sim.Poke("releaseRate", 1UL <<< 12)

        for i in 0 .. multibandBands - 1 do
            sim.Poke($"lg{i}", gainUnity)
            sim.Poke($"rg{i}", gainUnity)

        let output = runWavThroughSim sim defaultWavPorts 0 input
        writeWavFile outPath output
        let inL, inR = peaks input
        let outL, outR = peaks output
        printfn $"out: {output.FrameCount} frames, peaks {inL}/{inR} -> {outL}/{outR}"
        printfn $"wrote {outPath}"
        0
    | [| "hardware"; repoRoot |] ->
        writeHardware repoRoot
        0
    | [| "diff"; outDir |] ->
        writeDiff (List.map snd designs) outDir
        0
    | _ ->
        for name, design in designs do
            printfn $"{name}: {(emitDesign design).Split('\n').Length} lines of Verilog"

        let mutable ok = true

        for name, check in checks do
            let result = check ()
            printfn $"{name}: {result}"
            ok <- ok && result

        if ok then 0 else 1
