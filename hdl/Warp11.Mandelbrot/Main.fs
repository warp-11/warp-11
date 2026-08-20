/// Dispatch: the pod render, the seam emit, the sim server, and the
/// differential-oracle writer for the pod designs.
module Warp11.Mandelbrot.Main

open System.Numerics
open Warp11
open Warp11.Mandelbrot.Pod
open Warp11.Mandelbrot.Step
open Warp11.Mandelbrot.Lane
open Warp11.Mandelbrot.Coalescer
open Warp11.Mandelbrot.LanePod
open Warp11.Mandelbrot.FramePod
open Warp11.Mandelbrot.FrameAxi

let private designs () =
    [ mandelPod
      mandelPodAxi
      mandelStepHarness
      mandelLaneHarness
      mandelCoalescerHarness
      mandelCoalescerLoop
      mandelLanePodHarness
      mandelFramePodHarness
      mandelFramePodHarness1
      mandelFrameDdr
      mandelFrameAxiScaled ]

let private mainDemo () =
    for d in designs () do
        printfn "%s" (emitDesign d)
        printfn ""

    for m in designs () do
        match checkWidths m with
        | [] -> printfn $"{m.name}: widths ok"
        | problems -> problems |> List.iter (printfn "%s")

    // The step against its software twin: random vectors (sign bits set half
    // the time) plus the two's-complement boundary patterns, each held 4
    // cycles through the cone — bit-exact, no tolerance.
    let stepSim = Sim(mandelStepHarness)
    let rand = System.Random(11)
    let randomVector () = uint64 (rand.NextInt64()) &&& 0xFFFFFFFFUL

    let vectors =
        [ 0x80000000UL, 0x80000000UL, 0x80000000UL, 0x80000000UL
          0xFFFFFFFFUL, 0x7FFFFFFFUL, 0x00000000UL, 0xFFFFFFFFUL ]
        @ [ for _ in 1..200 -> randomVector (), randomVector (), randomVector (), randomVector () ]

    let twinOk =
        vectors
        |> List.forall (fun (zx, zy, cx, cy) ->
            stepSim.Poke("zx", zx)
            stepSim.Poke("zy", zy)
            stepSim.Poke("cx", cx)
            stepSim.Poke("cy", cy)

            for _ in 1 .. mandelStepLatency do
                stepSim.Tick()

            let ezx, ezy, eesc = stepTwin 28 zx zy cx cy

            stepSim.Peek "zx_next" = ezx
            && stepSim.Peek "zy_next" = ezy
            && stepSim.Peek "escaped" = eesc)

    printfn $"mandelStep vs twin (%d{List.length vectors} vectors): %b{twinOk}"

    // The barrel lane against the whole-pixel twin: 16 pixels spread across
    // the view (escapers and max-outs both), fed through the px stream with an
    // always-ready consumer; every (addr, iter) result must match `laneTwin`
    // exactly.
    let laneSim = Sim(mandelLaneHarness)
    let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL // Q4.28

    let pixels =
        [ for k in 0..15 -> toQ (-2.0 + 0.25 * float k), toQ (0.5 - 0.06 * float k) ]

    laneSim.Poke("res_ready", 1UL)
    let results = System.Collections.Generic.Dictionary<uint64, uint64>()
    let mutable feed = 0

    for _ in 1..400 do
        if feed < 16 then
            let cx, cy = pixels[feed]
            laneSim.Poke("px_valid", 1UL)
            laneSim.Poke("px_cx", cx)
            laneSim.Poke("px_cy", cy)
            laneSim.Poke("px_addr", uint64 feed)
        else
            laneSim.Poke("px_valid", 0UL)

        let accepted = feed < 16 && laneSim.Peek "px_ready" = 1UL

        if laneSim.Peek "res_valid" = 1UL then
            results[laneSim.Peek "res_addr"] <- laneSim.Peek "res_iter"

        laneSim.Tick()
        if accepted then feed <- feed + 1

    let laneOk =
        laneSim.Peek "all_idle" = 1UL
        && results.Count = 16
        && (pixels
            |> List.mapi (fun k (cx, cy) ->
                results.ContainsKey(uint64 k) && results[uint64 k] = laneTwin 28 8 cx cy)
            |> List.forall id)

    printfn $"barrel lane vs twin (16 px):  %b{laneOk}"

    // The coalescer against expected byte placement: two rows of 32 shuffled
    // columns each (out-of-order fill), always-ready drain — every beat's 16
    // byte lanes must hold the value written at column base+lane, and the two
    // rows' bases must not bleed (ping-pong overlap: row 1 fills while row 0
    // drains).
    let coalSim = Sim(mandelCoalescerHarness)
    coalSim.Poke("beat_ready", 1UL)
    let beats = ResizeArray<uint64 * System.Numerics.BigInteger>()

    let collect () =
        if coalSim.Peek "beat_valid" = 1UL then
            beats.Add(coalSim.Peek "beat_addr", coalSim.PeekWide "beat_beat")

    let shuffled =
        [ 5; 17; 0; 31; 22; 3; 28; 9; 14; 1; 30; 11; 26; 7; 19; 2; 24; 13; 6; 29; 10; 21; 4; 27; 16; 8; 23; 12; 25; 15; 18; 20 ]

    let feedRow (base_: uint64) (offset: int) =
        coalSim.Poke("row_base", base_)

        for c in shuffled do
            coalSim.Poke("px_valid", 1UL)
            coalSim.Poke("px_col", uint64 c)
            coalSim.Poke("px_value", uint64 ((c + offset) &&& 0xFF))
            let mutable guard = 0

            while coalSim.Peek "px_ready" = 0UL && guard < 1000 do
                collect ()
                coalSim.Tick()
                guard <- guard + 1

            collect ()
            coalSim.Tick()

        coalSim.Poke("px_valid", 0UL)

    feedRow 0x40UL 0
    feedRow 0x60UL 64

    for _ in 1..80 do
        collect ()
        coalSim.Tick()

    let coalOk =
        beats.Count = 4
        && (beats
            |> Seq.forall (fun (addr, beat) ->
                let rowOffset, base_ = if addr >= 0x60UL then 64, 0x60UL else 0, 0x40UL

                [ 0..15 ]
                |> List.forall (fun j ->
                    byte ((beat >>> (j * 8)) &&& System.Numerics.BigInteger(255)) = byte ((int (addr - base_) + j + rowOffset) &&& 0xFF))))

    printfn $"row coalescer vs expected:    %b{coalOk}"

    // A whole mini-frame through one lane pod: four row-runs packed as the
    // dispatch tree will pack them, beats gathered into a framebuffer array,
    // every pixel bit-exact against the whole-pixel twin — the P2 acceptance
    // check before the frame pod exists.
    let podSim = Sim(mandelLanePodHarness)
    let dxQ = toQ 0.25
    let cx0Q = toQ (-2.0)
    let cyQ r = toQ (0.5 - 0.25 * float r)
    let podAddrWidth = lanePodAddrWidth 16 4

    let packRun (dx: uint64) (cx: uint64) (cy: uint64) (rowBase: uint64) =
        (BigInteger dx <<< podAddrWidth + 64)
        ||| (BigInteger cx <<< podAddrWidth + 32)
        ||| (BigInteger cy <<< podAddrWidth)
        ||| BigInteger rowBase

    podSim.Poke("res_ready", 1UL)
    let fb = Array.zeroCreate<byte> 64
    let mutable row = 0

    for _ in 1..2000 do
        if row < 4 then
            podSim.PokeWide("run_data", packRun dxQ cx0Q (cyQ row) (uint64 (row * 16)))
            podSim.Poke("run_valid", 1UL)
        else
            podSim.Poke("run_valid", 0UL)

        let accepted = row < 4 && podSim.Peek "run_ready" = 1UL

        if podSim.Peek "res_valid" = 1UL then
            let addr = int (podSim.Peek "res_addr")
            let beat = podSim.PeekWide "res_beat"

            for j in 0..15 do
                fb[addr + j] <- byte ((beat >>> (j * 8)) &&& BigInteger(255))

        podSim.Tick()
        if accepted then row <- row + 1

    let podOk =
        [ for r in 0..3 do
              for c in 0..15 ->
                  let cx = (cx0Q + uint64 c * dxQ) &&& 0xFFFFFFFFUL
                  fb[r * 16 + c] = byte (laneTwin 28 8 cx (cyQ r)) ]
        |> List.forall id

    printfn $"lane pod mini-frame vs twin:  %b{podOk}"

    // The frame pod, two lanes: start latches the view, rows dispatch to
    // whichever lane is free, beats merge back — the whole 16×4 frame
    // bit-exact against the twin, ending on the frameDone pulse.
    let frameSim = Sim(mandelFramePodHarness)
    frameSim.Poke("cxOrigin", cx0Q)
    frameSim.Poke("cyOrigin", cyQ 0)
    frameSim.Poke("dx", dxQ)
    frameSim.Poke("dy", toQ (-0.25))
    frameSim.Poke("beat_ready", 1UL)
    frameSim.Poke("start", 1UL)
    frameSim.Tick()
    frameSim.Poke("start", 0UL)

    let frameFb = Array.zeroCreate<byte> 64
    let mutable doneSeen = false
    let mutable frameCycles = 0

    while not doneSeen && frameCycles < 5000 do
        if frameSim.Peek "beat_valid" = 1UL then
            let addr = int (frameSim.Peek "beat_addr")
            let beat = frameSim.PeekWide "beat_beat"

            for j in 0..15 do
                frameFb[addr + j] <- byte ((beat >>> (j * 8)) &&& BigInteger(255))

        if frameSim.Peek "frameDone" = 1UL then doneSeen <- true
        frameSim.Tick()
        frameCycles <- frameCycles + 1

    let frameOk =
        doneSeen
        && ([ for r in 0..3 do
                  for c in 0..15 ->
                      let cx = (cx0Q + uint64 c * dxQ) &&& 0xFFFFFFFFUL
                      frameFb[r * 16 + c] = byte (laneTwin 28 8 cx (cyQ r)) ]
            |> List.forall id)

    printfn $"frame pod (2 lanes) vs twin:  %b{frameOk} (%d{frameCycles} cycles)"

    // The same frame at numLanes = 1: dispatch and merge shortcut to direct
    // connections, so the degenerate scale renders through no arbiter at all.
    let frame1Sim = Sim(mandelFramePodHarness1)
    frame1Sim.Poke("cxOrigin", cx0Q)
    frame1Sim.Poke("cyOrigin", cyQ 0)
    frame1Sim.Poke("dx", dxQ)
    frame1Sim.Poke("dy", toQ (-0.25))
    frame1Sim.Poke("beat_ready", 1UL)
    frame1Sim.Poke("start", 1UL)
    frame1Sim.Tick()
    frame1Sim.Poke("start", 0UL)

    let frame1Fb = Array.zeroCreate<byte> 64
    let mutable done1 = false
    let mutable frame1Cycles = 0

    while not done1 && frame1Cycles < 5000 do
        if frame1Sim.Peek "beat_valid" = 1UL then
            let addr = int (frame1Sim.Peek "beat_addr")
            let beat = frame1Sim.PeekWide "beat_beat"

            for j in 0..15 do
                frame1Fb[addr + j] <- byte ((beat >>> (j * 8)) &&& BigInteger(255))

        if frame1Sim.Peek "frameDone" = 1UL then done1 <- true
        frame1Sim.Tick()
        frame1Cycles <- frame1Cycles + 1

    let frame1Ok = done1 && frame1Fb = frameFb
    printfn $"frame pod (1 lane) matches:   %b{frame1Ok} (%d{frame1Cycles} cycles)"

    0

/// What `debug` will open, by label — the pod designs on this side of the
/// debugger's registry dependency.
let private debuggable =
    [ "coalescer", fun () -> mandelCoalescerHarness
      "coalescer-loop", fun () -> mandelCoalescerLoop
      "lane", fun () -> mandelLaneHarness
      "lane-pod", fun () -> mandelLanePodHarness
      "frame-pod", fun () -> mandelFramePodHarness
      "pod", fun () -> mandelPod ]

[<EntryPoint>]
let main argv =
    match argv with
    | [| "debug"; label |] ->
        match debuggable |> List.tryFind (fst >> (=) label) with
        | Some (_, build) -> Warp11.SimView.Desktop.debug $"Mandelbrot — {label}" (build ())
        | None ->
            printfn "unknown design '%s'" label
            printfn "try: %s" (String.concat ", " (List.map fst debuggable))
            1
    | [| "diff"; outDir |] ->
        writeDiff (designs ()) outDir
        0
    | [| "mandel"; outPath |] ->
        Host.runMandel outPath
        0
    | [| "frame"; outPath |] ->
        FrameHost.renderFrame outPath
        0
    | [| "frameaxi"; outPath |] ->
        FrameHost.runFrameAxi outPath
        0
    // The same render under random memory timing. The picture must not change:
    // a frame that depends on when DDR answered is one that will be right here
    // and wrong on a board — which is how the first render came out sheared.
    | [| "frameaxi-jitter" |] ->
        let ok = FrameHost.frameIsIndifferentToMemoryTiming ()
        printfn $"frame is indifferent to memory timing (4 seeds): %b{ok}"
        if ok then 0 else 1
    | [| "lanescale" |] ->
        FrameHost.laneScale [ (64, 208, 48, 26); (64, 208, 48, 52); (64, 208, 48, 104) ]
        0
    | [| "lanescale"; w; h; mi; l |] ->
        FrameHost.laneScale [ (int w, int h, int mi, int l) ]
        0
    | [| "cyclesweep" |] ->
        FrameHost.cycleSweep ()
        0
    | [| "frameserve" |] ->
        FrameHost.frameserve ()
        0
    | [| "hardware"; repoRoot |] ->
        Seam.writeHardware repoRoot
        0
    | [| "simserve" |] ->
        Host.simserve ()
        0
    | _ -> mainDemo ()
