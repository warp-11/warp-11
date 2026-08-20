/// The host side of the scaled full-architecture render: drive `mandelFrameDdr`
/// through the Sim's fake DDR, assert bit-exactness against the whole-pixel
/// twin on every pixel, write the PPM, and read the wormhole telemetry — the
/// P3 acceptance run, and the same loop the Rust driver will run on silicon.
module Warp11.Mandelbrot.FrameHost

open System.Numerics
open Warp11
open Warp11.Mandelbrot.Lane
open Warp11.Mandelbrot.FramePod
open Warp11.Mandelbrot.FrameAxi

let frameWidth = 64
let frameHeight = 48
let frameMaxIter = 48


/// `dotnet run -- frame <out.ppm>`: start the pod, service the AXI master into
/// the fake DDR until `frameDone`, flush the master's outstanding writes,
/// verify, render, report.
let renderFrame (outPath: string) =
    let sim = Sim(mandelFrameDdr)
    let fbBase = 0x100 // off zero, so the rebase onto fbBaseAddr is proven
    let ddr = SimAxiWriteSlave(sim, fbBase + frameWidth * frameHeight)
    let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL
    let stepQ = toQ (3.0 / 64.0)
    let cx0 = toQ (-2.25)
    let cy0 = toQ (-1.125)

    sim.Poke("cxOrigin", cx0)
    sim.Poke("cyOrigin", cy0)
    sim.Poke("dx", stepQ)
    sim.Poke("dy", stepQ)
    sim.Poke("fbBaseAddr", uint64 fbBase)
    sim.Poke("start", 1UL)
    ddr.Cycle()
    sim.Poke("start", 0UL)

    let mutable cycles = 1

    while sim.Peek "frameDone" <> 1UL && cycles < 200000 do
        ddr.Cycle()
        cycles <- cycles + 1

    if sim.Peek "frameDone" <> 1UL then
        failwith "MandelFrameDdr did not reach frameDone within 200000 cycles"

    // frameDone means the last beat EXITED the pod; the master's ring may
    // still hold writes — flush them into the model.
    for _ in 1..100 do
        ddr.Cycle()

    let expected r c =
        let cx = (cx0 + uint64 c * stepQ) &&& 0xFFFFFFFFUL
        let cy = (cy0 + uint64 r * stepQ) &&& 0xFFFFFFFFUL
        byte (laneTwin 28 frameMaxIter cx cy)

    let mismatches =
        [ for r in 0 .. frameHeight - 1 do
              for c in 0 .. frameWidth - 1 do
                  if ddr.Memory[fbBase + r * frameWidth + c] <> expected r c then yield r, c ]

    let value r c = ddr.Memory[fbBase + r * frameWidth + c]
    let shade (v: byte) = if int v >= frameMaxIter - 1 then 0 else int v

    let rows =
        [ for r in 0 .. frameHeight - 1 ->
              String.concat " " [ for c in 0 .. frameWidth - 1 -> string (shade (value r c)) ] ]

    System.IO.File.WriteAllText(
        outPath,
        $"P2\n%d{frameWidth} %d{frameHeight}\n%d{frameMaxIter}\n" + String.concat "\n" rows + "\n"
    )

    let ramp = " .:-=+*#%@"

    for charRow in 0 .. frameHeight / 2 - 1 do
        let line =
            System.String(
                [| for c in 0 .. frameWidth - 1 ->
                       let v = int (value (charRow * 2) c)

                       if v >= frameMaxIter - 1 then '@'
                       else ramp[min 8 (v * 9 / frameMaxIter)] |]
            )

        printfn "%s" line

    printfn $"rendered %d{frameWidth}x%d{frameHeight} @ max %d{frameMaxIter}, 4 lanes x 8 threads, in %d{cycles} cycles -> {outPath}"
    printfn $"twin mismatches:              %d{List.length mismatches} of %d{frameWidth * frameHeight} pixels"

    for name, blocked, starved in streamReport sim.Peek mandelFrameDdr do
        printfn $"stream '{name}': blocked %d{blocked} starved %d{starved} of %d{cycles} cycles"

    if not (List.isEmpty mismatches) then
        failwith "the fabric and the software twin disagree"

/// `dotnet run -- frameaxi <out.ppm>`: the silicon rehearsal. The scaled
/// wrapper driven exactly as the Rust driver will drive the board — every
/// control action a real five-channel AXI-Lite transaction, the framebuffer
/// landing in the fake DDR through the AXI master — then verified bit-exact
/// and reported. Every handshake step is asserted, so a protocol bug is a
/// failure here, not a hang on silicon.
let runFrameAxiWith (jitter: int option) (outPath: string) =
    let sim = Sim(mandelFrameAxiScaled)
    let fbBase = 0x100
    let ddr = SimAxiWriteSlave(sim, fbBase + frameWidth * frameHeight, ?jitter = jitter)
    let cycle () = ddr.Cycle()
    let axi = SimAxi.clientWith sim cycle
    let read32, write32 = axi.read32, axi.write32

    let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL
    let stepQ = toQ (3.0 / 64.0)
    let cx0 = toQ (-2.25)
    let cy0 = toQ (-1.125)

    let idValue = read32 frameStartOffset

    if idValue <> frameIdMagic then
        failwith $"ID read 0x%08x{idValue}, expected 0x%08x{frameIdMagic}"

    write32 frameCxOffset cx0
    write32 frameCyOffset cy0
    write32 frameDxOffset stepQ
    write32 frameDyOffset stepQ
    write32 frameFbBaseOffset (uint64 fbBase)
    write32 frameStartOffset 1UL

    // Poll done through real reads, with free-run gaps between polls so the
    // pod is not transaction-paced.
    let mutable polls = 0

    while read32 frameDoneOffset <> 1UL && polls < 2000 do
        for _ in 1..50 do
            cycle ()

        polls <- polls + 1

    if read32 frameDoneOffset <> 1UL then
        failwith "frameDone never rose"

    for _ in 1..100 do
        cycle () // flush the master's outstanding writes

    let lastFrameCycles = read32 frameCyclesOffset
    let busyNow = read32 frameBusyOffset

    let expected r c =
        let cx = (cx0 + uint64 c * stepQ) &&& 0xFFFFFFFFUL
        let cy = (cy0 + uint64 r * stepQ) &&& 0xFFFFFFFFUL
        byte (laneTwin 28 frameMaxIter cx cy)

    let mismatches =
        [ for r in 0 .. frameHeight - 1 do
              for c in 0 .. frameWidth - 1 do
                  if ddr.Memory[fbBase + r * frameWidth + c] <> expected r c then yield r, c ]

    let shade (v: byte) = if int v >= frameMaxIter - 1 then 0 else int v

    let rows =
        [ for r in 0 .. frameHeight - 1 ->
              String.concat
                  " "
                  [ for c in 0 .. frameWidth - 1 -> string (shade ddr.Memory[fbBase + r * frameWidth + c]) ] ]

    System.IO.File.WriteAllText(
        outPath,
        $"P2\n%d{frameWidth} %d{frameHeight}\n%d{frameMaxIter}\n" + String.concat "\n" rows + "\n"
    )

    printfn $"MandelFrameAxiScaled: ID ok, busy=%d{busyNow}, lastFrameCycles=%d{lastFrameCycles} -> {outPath}"
    printfn $"twin mismatches:              %d{List.length mismatches} of %d{frameWidth * frameHeight} pixels"

    for name, blocked, starved in streamReport sim.Peek mandelFrameAxiScaled do
        printfn $"stream '{name}': blocked %d{blocked} starved %d{starved}"

    if not (List.isEmpty mismatches) then
        failwith "the fabric and the software twin disagree"

/// `dotnet run -- frameserve`: the frame half of the FsSimWindow bridge — the
/// Rust `MandelFrameDevice` drives the scaled wrapper in the Sim before any
/// silicon exists. `R`/`W` are AXI-Lite transactions (the fake DDR serviced on
/// every tick), `C <hexn>` runs free cycles, `D <hexoff> <hexlen>` dumps DDR
/// bytes as hex — the framebuffer readback the register aperture cannot carry,
/// standing in for the board's mmap of PS DDR.
let frameserve () =
    let sim = Sim(mandelFrameAxiScaled)
    let ddr = SimAxiWriteSlave(sim, 65536)
    let cycle () = ddr.Cycle()
    let axi = SimAxi.clientWith sim cycle
    let read32, write32 = axi.read32, axi.write32

    let out = System.Console.Out
    out.WriteLine "FRAMESERVE"
    out.Flush()

    let mutable line = System.Console.In.ReadLine()

    while line <> null do
        (match line.Split(' ') with
         | [| "R"; off |] -> out.WriteLine(sprintf "%08x" (read32 (System.Convert.ToUInt64(off, 16))))
         | [| "W"; off; value |] ->
             write32 (System.Convert.ToUInt64(off, 16)) (System.Convert.ToUInt64(value, 16))
             out.WriteLine "OK"
         | [| "C"; n |] ->
             for _ in 1 .. int (System.Convert.ToUInt64(n, 16)) do
                 cycle ()

             out.WriteLine "OK"
         | [| "D"; off; len |] ->
             let start = int (System.Convert.ToUInt64(off, 16))
             let count = int (System.Convert.ToUInt64(len, 16))

             out.WriteLine(
                 ddr.Memory[start .. start + count - 1]
                 |> Array.map (sprintf "%02x")
                 |> String.concat ""
             )
         | _ -> out.WriteLine "ERR")

        out.Flush()
        line <- System.Console.In.ReadLine()

/// `dotnet run -- cyclesweep`: the scaling-axes sweep, mirroring the Kotlin
/// CycleGapProbeTest — used to localize the full-scale cycle finding (F#
/// 728,898 vs Kotlin 548,809 at 104 lanes, yet cycle-parity at 64×48/4).
let cycleSweep () =
    let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL

    let run (w: int) (h: int) (maxIter: int) (lanes: int) =
        let harness =
            design $"Sweep_%d{w}x%d{h}_m%d{maxIter}_l%d{lanes}" (fun () ->
                let start = inputBit "start"
                let cxOrigin = input "cxOrigin" 32
                let cyOrigin = input "cyOrigin" 32
                let dx = input "dx" 32
                let dy = input "dy" 32

                let beats =
                    frameCmdStream start cxOrigin cyOrigin dx dy
                    |> mandelFramePipeline w h maxIter 28 8 lanes

                let out, busy, frameDone =
                    instanceNamed "gather" (mandelFrameGatherer w h) start beats

                let busyOut = outputBit "busy"
                busy ==> busyOut
                let doneOut = outputBit "frameDone"
                frameDone ==> doneOut
                streamOutput "beat" out)

        let sim = Sim(harness)
        sim.Poke("cxOrigin", toQ (-2.25))
        sim.Poke("cyOrigin", toQ (-1.125))
        sim.Poke("dx", toQ (3.0 / float w))
        sim.Poke("dy", toQ (2.25 / float h))
        sim.Poke("beat_ready", 1UL)
        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)
        let mutable cyc = 0
        let idle = Array.zeroCreate<int> lanes
        let gather = Array.zeroCreate<int> lanes

        while sim.Peek "busy" = 1UL && cyc < 2000000 do
            for i in 0 .. lanes - 1 do
                let b = sim.Peek $"pod%d{i}_cg_busy"
                let g = sim.Peek $"pod%d{i}_cg_gather"
                if b = 0UL && g = 0UL then idle[i] <- idle[i] + 1
                if g = 1UL then gather[i] <- gather[i] + 1

            sim.Tick()
            cyc <- cyc + 1

        printfn $"    lane idle: %A{idle}  gather: %A{gather}"
        cyc

    for lanes in [ 1; 2; 4; 8 ] do
        printfn $"[Sweep] lanes=%d{lanes} 64x48/48: %d{run 64 48 48 lanes}"

    for maxIter in [ 48; 128; 256 ] do
        printfn $"[Sweep] maxIter=%d{maxIter} 64x48/4 lanes: %d{run 64 48 maxIter 4}"

    for h in [ 48; 96 ] do
        printfn $"[Sweep] height=%d{h} 64xH/48/4 lanes: %d{run 64 h 48 4}"

    for w in [ 64; 256; 1408 ] do
        printfn $"[Sweep] width=%d{w} Wx8/48/4 lanes: %d{run w 8 48 4}"

    for lanes in [ 13; 16; 26 ] do
        printfn $"[Sweep] lanes=%d{lanes} 64x48/48: %d{run 64 48 48 lanes}"

/// `dotnet run -- lanescale`: the full-lane-count probe — 26/52/104 lanes on
/// a 64×208 frame (2 rows per lane even at 104), charting the both-clustered
/// residual at the real cluster counts, with wall-clock per run so the same
/// numbers place the F# Sim against the Kotlin sim (the perf qualifier).
let laneScale (configs: (int * int * int * int) list) =
    let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL

    let run (w: int, h: int, maxIter: int, lanes: int) =

        let sw = System.Diagnostics.Stopwatch.StartNew()

        let harness =
            design $"LaneScale_%d{w}x%d{h}_%d{maxIter}_%d{lanes}" (fun () ->
                let start = inputBit "start"
                let cxOrigin = input "cxOrigin" 32
                let cyOrigin = input "cyOrigin" 32
                let dx = input "dx" 32
                let dy = input "dy" 32

                let beats =
                    frameCmdStream start cxOrigin cyOrigin dx dy
                    |> mandelFramePipeline w h maxIter 28 8 lanes

                let out, busy, frameDone =
                    instanceNamed "gather" (mandelFrameGatherer w h) start beats

                let busyOut = outputBit "busy"
                busy ==> busyOut
                let doneOut = outputBit "frameDone"
                frameDone ==> doneOut
                streamOutput "beat" out)

        let sim = Sim(harness)
        let elabMs = sw.ElapsedMilliseconds
        sim.Poke("cxOrigin", toQ (-2.25))
        sim.Poke("cyOrigin", toQ (-1.125))
        sim.Poke("dx", toQ (3.0 / float w))
        sim.Poke("dy", toQ (2.25 / float h))
        sim.Poke("beat_ready", 1UL)
        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)

        let idle = Array.zeroCreate<int> lanes
        let gather = Array.zeroCreate<int> lanes
        let bubble = Array.zeroCreate<int> lanes // has a row, but no slot issued this cycle
        let mutable cyc = 0

        while sim.Peek "busy" = 1UL && cyc < 2000000 do
            for i in 0 .. lanes - 1 do
                let b = sim.Peek $"pod%d{i}_cg_busy"
                let g = sim.Peek $"pod%d{i}_cg_gather"

                if b = 0UL && g = 0UL then idle[i] <- idle[i] + 1
                if g = 1UL then gather[i] <- gather[i] + 1

                if (b = 1UL || g = 1UL) && sim.Peek $"pod%d{i}_lane_issueValid" = 0UL then
                    bubble[i] <- bubble[i] + 1

            sim.Tick()
            cyc <- cyc + 1

        sw.Stop()
        let simMs = sw.ElapsedMilliseconds - elabMs
        let idleTotal = Array.sum idle
        let idleMax = Array.max idle
        let idleArgMax = Array.findIndex ((=) idleMax) idle

        printfn
            $"[LaneScale] %d{w}x%d{h}/m%d{maxIter}/l%d{lanes}: %d{cyc} cycles | idle total=%d{idleTotal} max=%d{idleMax}@lane%d{idleArgMax} | gather total=%d{Array.sum gather} | bubble total=%d{Array.sum bubble} | elab %d{elabMs} ms, sim %d{simMs} ms (%.0f{float cyc / float simMs * 1000.0} cyc/s)"

    for c in configs do
        run c


/// The same render with the DDR answering after a random 0-3 cycle delay and
/// stalling AW and W independently. The picture must be identical: a frame that
/// depends on when memory answered is a frame that will be right in this suite
/// and wrong on a board, which is exactly how the first render came out sheared.
let runFrameAxi (outPath: string) = runFrameAxiWith None outPath

let frameIsIndifferentToMemoryTiming () =
    let tmp = System.IO.Path.GetTempFileName()
    let baseline = runFrameAxiWith None tmp
    [ 1..4 ] |> List.forall (fun seed -> runFrameAxiWith (Some seed) tmp = baseline)
