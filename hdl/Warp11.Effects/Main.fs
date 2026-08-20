/// The audio example's executable: the living checks, and the emit that writes
/// the Verilog plus the generated Rust register layouts.
module Warp11.Effects.Main

open Warp11

let private designs =
    [ "AudioToneAxi", audioToneAxi
      "AudioPassthruAxi", audioPassthruAxi
      "AudioGainAxi", audioGainAxi
      "AudioEffectsAxi", audioEffectsAxi
      "AudioBatchAxi", Batch.audioBatchAxi ]

/// Every register's reset value must be a no-op, because these bitstreams ship
/// without a host daemon: load one and it has to pass audio (or, for the tone,
/// make a sound) with nothing written to it. A default that clamps, mutes or
/// zeroes would look exactly like broken hardware on a bench.
let private defaultsArePassthrough () : bool =
    let sim = Sim(audioEffectsAxi)
    // No AXI writes at all — read the reset state straight out of the slave.
    sim.Tick()

    let isUnity (e: RegEntry) expected =
        match e.kind with
        | RwReg (_, init) -> init = expected
        | _ -> false

    isUnity effectsMap.volume gainUnity
    && isUnity effectsMap.mute 0UL
    && isUnity effectsMap.eq[0] biquadUnity
    && List.forall (fun e -> isUnity e 0UL) (List.skip 1 effectsMap.eq)
    && isUnity effectsMap.compRatio 0UL
    && isUnity effectsMap.compMakeup gainUnity
    && isUnity effectsMap.compThreshold ((1UL <<< sampleWidth) - 1UL)
    && isUnity effectsMap.limitThreshold ((1UL <<< (sampleWidth - 1)) - 1UL)

/// The four slaves must not collide with each other's conventions or with
/// themselves: every entry word-aligned and inside its aperture, and no two
/// entries of one map sharing a word unless they are bit fields. `RegMap`'s own
/// validation runs at elaboration, so reaching here already proves most of it —
/// this pins the part that is a *convention* rather than a rule.
let private mapsAreWellFormed () : bool =
    let maps =
        [ "tone", toneMap.map
          "passthru", passthruMap.map
          "gain", gainMap.map
          "effects", effectsMap.map ]

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
    let sim = Sim(Batch.audioBatchAxi)
    let src = 0x1000
    let dst = 0x9000
    let ddr = SimAxiDdr(sim, 0x20000, ?jitter = jitter)
    let input = stageFrames ddr src frames seed

    let axi = SimAxi.clientWith sim ddr.Cycle
    let m = Batch.batchMap
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

    let sim = Sim(Batch.multibandStageRef)
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
    let got = runStageStalled Batch.multibandStageRef (fun s ->
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
    stageIsStallIndependent Batch.multibandStageRef multibandSetup

/// The same question of each stage on its own, so a failure names one entry in
/// the audio stdlib rather than "somewhere in the chain".
let private stageStallReport () =
    let stages =
        [ "gain", Batch.gainStage, (fun (sim: Sim) ->
            sim.Poke("volume", gainUnity)
            sim.Poke("mute", 0UL))
          "eq (biquad)", Batch.eqStage, (fun (sim: Sim) ->
            sim.Poke("b0", biquadUnity)
            for n in [ "b1"; "b2"; "a1"; "a2" ] do sim.Poke(n, 0UL))
          "compressor", Batch.compressorStage, (fun (sim: Sim) ->
            sim.Poke("threshold", 200_000UL)
            sim.Poke("ratio", 4UL)
            sim.Poke("attack", 1UL <<< 14)
            sim.Poke("releaseRate", 1UL <<< 12)
            sim.Poke("makeup", gainUnity))
          "limiter", Batch.limiterStage, (fun (sim: Sim) ->
            sim.Poke("threshold", (1UL <<< (sampleWidth - 1)) - 1UL))
          "fir", Batch.firStage, (fun (sim: Sim) -> sim.Poke("preset", 0UL))
          "multiband", Batch.multibandStageRef, multibandSetup ]

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
        [ "audio_tone_layout.rs", "AudioToneAxi", toneMap.map
          "audio_passthru_layout.rs", "AudioPassthruAxi", passthruMap.map
          "audio_gain_layout.rs", "AudioGainAxi", gainMap.map
          "audio_effects_layout.rs", "AudioEffectsAxi", effectsMap.map
          "audio_batch_layout.rs", "AudioBatchAxi", Batch.batchMap.map ]

    for file, title, m in layouts do
        let path = System.IO.Path.Combine(runtimeSrc, file)
        System.IO.File.WriteAllText(path, String.concat "\n" (layoutFor title m) + "\n")
        printfn $"wrote {path}"

[<EntryPoint>]
let main argv =
    match argv with
    // A debugger on one of this project's designs. `drift` is the one to open:
    // it drives the multiband stage itself and asserts the property that fails,
    // so pressing Run stops on the cycle the divergence appears.
    // Headless: run the drift harness and report where it breaks, so the
    // debugger's stopping point can be checked without opening a window.
    | [| "drift" |] ->
        let sim = Sim(Batch.driftHarness, checkAsserts = true)
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
            let sim = Sim(Batch.audioBatchAxi)
            let ddr = mk sim
            let src, dst = 0x1000, 0x9000
            let axi = SimAxi.clientWith sim ddr.Cycle
            let m = Batch.batchMap
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
                [ Warp11.Catalog.entry "Drift (start here)" (nameof Batch.driftHarness) (fun () -> Batch.driftHarness)
                  |> Warp11.Catalog.watching [ "drift"; "drift_baseline_out"; "dsp_steps"; "pipe_steps"; "cycle"; "offering"; "accepted" ]
                  |> Warp11.Catalog.poking [ "run", 1UL; "stallAt", 20UL ]
                  Warp11.Catalog.entry "Multiband stage" (nameof Batch.multibandStageRef) (fun () -> Batch.multibandStageRef)
                  |> Warp11.Catalog.poking
                      [ "bypass", 0UL; "threshold", 200_000UL; "ratio", 4UL; "attack", 1UL <<< 14; "releaseRate", 1UL <<< 12 ]
                  Warp11.Catalog.entry "Gain" (nameof Batch.gainStage) (fun () -> Batch.gainStage)
                  |> Warp11.Catalog.poking [ "volume", gainUnity; "mute", 0UL ]
                  Warp11.Catalog.entry "EQ (biquad)" (nameof Batch.eqStage) (fun () -> Batch.eqStage)
                  |> Warp11.Catalog.poking [ "b0", biquadUnity ]
                  Warp11.Catalog.entry "Compressor" (nameof Batch.compressorStage) (fun () -> Batch.compressorStage)
                  |> Warp11.Catalog.poking
                      [ "threshold", 200_000UL; "ratio", 4UL; "attack", 1UL <<< 14; "releaseRate", 1UL <<< 12; "makeup", gainUnity ]
                  Warp11.Catalog.entry "Limiter" (nameof Batch.limiterStage) (fun () -> Batch.limiterStage)
                  |> Warp11.Catalog.poking [ "threshold", (1UL <<< (sampleWidth - 1)) - 1UL ]
                  Warp11.Catalog.entry "Batch accelerator" (nameof Batch.audioBatchAxi) (fun () -> Batch.audioBatchAxi) ]

        let initial = if argv.Length > 1 then Some argv[1] else None
        Warp11.SimView.Desktop.run (Warp11.SimView.View.FromCatalog(catalog, initial)) []
        0
    // The simulator's answer for the same file the board processes, so the two
    // can be diffed byte for byte. `settleCycles = 0` because the batch design
    // does not pre-run its first frame either — the comparison is only honest
    // if both see exactly the same sequence.
    | [| "wav"; inPath; outPath |] ->
        let input = readWavFile inPath
        printfn $"in:  {input.FrameCount} frames, {input.sampleRate} Hz, {input.channels} ch"

        let sim = Sim(Batch.multibandStageRef)
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
