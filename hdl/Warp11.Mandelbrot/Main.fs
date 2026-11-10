/// Dispatch: the living checks, the frame's build directory, the Sim renders
/// and probes, and the differential-oracle writer for the designs here.
module Warp11.Mandelbrot.Main

open System.Numerics
open Warp11
open Warp11.Mandelbrot.Lane
open Warp11.Mandelbrot.Harness
open Warp11.Mandelbrot.Pod
open Warp11.Mandelbrot.FramePod

let private designs () =
    [ mandelPod.def
      mandelStepHarness.def
      mandelLaneHarness.def
      mandelCoalescerHarness.def
      mandelCoalescerLoop.def
      mandelLanePodHarness.def
      mandelFramePodHarness.def
      mandelFramePodHarness1.def
      mandelFrameDdr.def ]

/// What the oracle runs: the designs above at the default length, and the
/// frame that is built, small — a flat farm and the clustered one above
/// `fanFlatMax` — plus the KV260 top the generator puts around it.
///
/// A frame beat takes a few hundred cycles to come back out, so the frames
/// run 800: under the random stimulus that is ~56 beats out of the flat farm
/// and ~600 out of the clustered one, where 50 cycles asserted an empty
/// pipeline. The top stays at the default, because no length helps it:
/// random AXI-Lite stimulus never completes the handshakes that set a count
/// and start a batch, so what it checks is the slave, the registers and an
/// idle master — the frame inside is the two above.
let private diffDesigns () =
    [ for d in designs () -> d, diffCycles ]
    @ [ (mandelScatterDef "MandelFrame3" 64 32 48 28 8 3).def, 800
        (mandelScatterDef "MandelFrame20" 64 32 48 28 8 20).def, 800
        (BoardTop.boardTop kv260 viaScatter (Chunked.mandelScatter "MandelFrame" 64 32 48 28 8 2)).top, diffCycles ]

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
    let stepSim = Sim mandelStepHarness.def
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
    let laneSim = Sim mandelLaneHarness.def
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
    let coalSim = Sim mandelCoalescerHarness.def
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
    let podSim = Sim mandelLanePodHarness.def
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
    let frameSim = Sim mandelFramePodHarness.def
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
    let frame1Sim = Sim mandelFramePodHarness1.def
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

    // The frame as chunk beats, on the KV260's counted top against the
    // behavioural DDR: one lane, three, and twenty — above `fanFlatMax`,
    // where the farm clusters — every pixel at its place. A chunk is at
    // least two beats, so 32-pixel chunks over a 64×8 frame.
    let chunkView = (cx0Q, cyQ 0, toQ 0.125, toQ -0.25)
    let chunkTwin = Chunked.renderTwin 64 8 32 48 28 chunkView

    for lanes in [ 1; 3; 20 ] do
        let pixels, cycles, top = Chunked.renderThroughBoardTop 64 8 32 48 28 8 lanes chunkView
        printfn $"frame top vs twin ({top.name}, %d{lanes} lanes): %b{pixels = chunkTwin} (%d{cycles} cycles)"

    // The same over the Icepi's host link: each beat back with its index,
    // placed by it.
    let linkPixels, linkCycles, linkBusy, linkTop = Chunked.renderThroughLink 64 8 32 48 28 8 2 chunkView
    let linkOk = linkPixels = chunkTwin && linkBusy = 0UL
    printfn $"frame link top vs twin, placed by index ({linkTop.name}, 2 lanes): %b{linkOk} (%d{linkCycles} cycles)"

    0

/// What `debug` will open, by label — the pod designs on this side of the
/// debugger's registry dependency.
let private debuggable =
    [ "coalescer", fun () -> mandelCoalescerHarness.def
      "coalescer-loop", fun () -> mandelCoalescerLoop.def
      "lane", fun () -> mandelLaneHarness.def
      "lane-pod", fun () -> mandelLanePodHarness.def
      "frame-pod", fun () -> mandelFramePodHarness.def
      "pod", fun () -> mandelPod.def ]

[<EntryPoint>]
let main argv =
    // This project's units, then the verbs every drawn design answers to —
    // which is all a project has to do to get `emit`, `export`, `frame`,
    // `board`, `build` and `batchserve` over designs that name them.
    Units.register ()

    match Warp11.Design.Verbs.run argv with
    | Some code -> code
    | None ->

    match argv with
    | [| "debug"; label |] ->
        match debuggable |> List.tryFind (fst >> (=) label) with
        | Some (_, build) -> Warp11.SimView.Desktop.debug $"Mandelbrot — {label}" (build ())
        | None ->
            printfn "unknown design '%s'" label
            printfn "try: %s" (String.concat ", " (List.map fst debuggable))
            1
    | [| "diff"; outDir |] ->
        writeDiffWith (diffDesigns ()) outDir
        0
    | [| "mandel"; outPath |] ->
        Host.runMandel outPath
        0
    | [| "frame"; outPath |] ->
        FrameHost.renderFrame outPath
        0
    | [| "lanescale" |] ->
        FrameHost.laneScale [ (64, 208, 48, 26); (64, 208, 48, 52); (64, 208, 48, 104) ]
        0
    | [| "lanescale"; w; h; mi; l |] ->
        FrameHost.laneScale [ (int w, int h, int mi, int l) ]
        0
    // The frame through the counted board top and the behavioural DDR.
    | [| "boardscale"; w; h; px; mi; l |] ->
        let w, h, pixels, maxIter, lanes = int w, int h, int px, int mi, int l
        let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL
        let view = (toQ -2.25, toQ -1.125, toQ (3.0 / float w), toQ (2.25 / float h))
        let frame, cycles, top = Chunked.renderThroughBoardTop w h pixels maxIter 28 8 lanes view
        let ok = frame = Chunked.renderTwin w h pixels maxIter 28 view
        printfn $"[BoardScale] {top.name} %d{w}x%d{h}/px%d{pixels}/m%d{maxIter}/l%d{lanes}: %d{cycles} cycles | bit-exact %b{ok}"
        0
    | [| "cyclesweep" |] ->
        FrameHost.cycleSweep ()
        0
    // The frame at several chunk widths: `chunksweep w h maxIter lanes`.
    // Each is checked against the twin, and the padded row is printed beside
    // the cycles because padding is pixels computed and thrown away — a wider
    // chunk that pads further can cost more than the tail it saves.
    | [| "chunksweep"; w; h; mi; l |] ->
        let w, h, maxIter, lanes = int w, int h, int mi, int l
        let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL
        let view = (toQ -2.25, toQ -1.125, toQ (3.0 / float w), toQ (2.25 / float h))
        printfn $"[ChunkSweep] %d{w}x%d{h}/m%d{maxIter}/l%d{lanes}"

        for pixels in [ 128; 352; 704 ] do
            let padded = Lane.chunkedWidth pixels w
            let twin = Chunked.renderTwin w h pixels maxIter 28 view
            let frame, cycles, _ = Chunked.renderThroughBoardTop w h pixels maxIter 28 8 lanes view
            let perPixel = float cycles / float (padded * h)

            printfn
                $"  chunk %4d{pixels}: %7d{cycles} cycles | padded row %d{padded} (%d{padded * h} px) | %.3f{perPixel} cyc/px | bit-exact %b{frame = twin}"

        0
    // The frame as the directory its board's toolchain builds from, from the
    // typed design: `bitstream <dir> [lanes]`, the board `Chunked.board` and
    // the lanes `Chunked.lanes` — a line each to change. Named for what
    // comes out of it — the directory's own `build.sh` runs the tool and
    // packages the app. Fewer lanes builds faster and renders the same
    // picture, so the count on the line is a dial for a sweep. `mapping`
    // prints the mapping the frame gets.
    | [| "mapping" |] ->
        printfn "%s" (Warp11.Mapping.write Chunked.mapping)
        0
    | [| "bitstream"; dir |]
    | [| "bitstream"; dir; _ |] ->
        let lanes = if argv.Length = 3 then Some(int argv[2]) else None
        let out = Chunked.buildDirectory dir lanes

        for file in out.files do
            printfn $"wrote {file}"

        printfn $"build with: {out.run}"
        0
    | _ -> mainDemo ()
