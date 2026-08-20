/// The multi-lane WarpCPU-shaped frame renderer — Kotlin's `mandelFramePod`,
/// ported and DECOMPOSED (2026-08-04, the STREAM_API refactor): a command
/// beat enters the frame processor, which mints one row-run per row; the
/// runs farm across `numLanes` lane pods (clustered dispatch, clustered
/// merge, one elastic egress register breaking the combinational ready path
/// from the AXI master); the gatherer owns completion — the frame is done
/// when the last beat has EXITED the egress buffer, counted where the beats
/// land. The dispatcher is coarse (one hand-out per row) and self-balancing;
/// view bounds are runtime inputs latched from the command; the
/// frame-constant `cxOrigin`/`dx` ride the run payload through the
/// registered tree instead of a die-spanning broadcast (Kotlin's fragile
/// +0.020 ns path at 104 lanes).
module Warp11.Mandelbrot.FramePod

open Warp11
open Warp11.Mandelbrot.LanePod

/// The drained-beat layout a frame pod of this size emits — shared with the
/// wrapper reading the stream.
let frameBeatLayout width height =
    layout2 ("addr", lanePodAddrWidth width height) ("beat", 128)

/// One command beat per frame: the view, latched whole at start.
let private frameCmdLayout = layout4 ("cx", 32) ("cy", 32) ("dx", 32) ("dy", 32)

/// The frame processor — the row-run generator as its own module: one command
/// beat in, `height` row-runs out (dx | cxOrigin | cy | rowBase), then quiet
/// until the next command. Acceptance is unconditional (`cmd_ready` is
/// constant 1): a command mid-frame restarts the walk, exactly as the fused
/// FSM's `On istart` did.
let mandelFrameProcessor (width: int) (height: int) =
    let widthPadded = paddedWidth width
    let addrWidth = lanePodAddrWidth width height
    let runWidth = lanePodRunWidth width height
    let rowCountWidth = bitsToHold (height + 1)

    defineModule
        $"MandelFrameProcessor_%d{width}x%d{height}"
        (fun p ->
            (p.inPort "cmd_cx" 32,
             p.inPort "cmd_cy" 32,
             p.inPort "cmd_dx" 32,
             p.inPort "cmd_dy" 32,
             p.inPort "cmd_valid" 1,
             p.outPort "cmd_ready" 1,
             p.outPort "run_data" runWidth,
             p.outPort "run_valid" 1,
             p.inPort "run_ready" 1))
        (fun m (ccx, ccy, cdx, cdy, cvalid, cready, runData, runValid, runReady) (cmd: Stream<Expr * Expr * Expr * Expr>) ->
            let cx, cy, dx, dy = cmd.payload
            cx ==> ccx
            cy ==> ccy
            dx ==> cdx
            dy ==> cdy
            cmd.valid ==> cvalid
            cready ==> cmd.ready
            m.RegisterStreamReady runReady

            { payload = runData
              valid = runValid
              ready = runReady
              layout = layout1 ("data", runWidth) })
        (fun (ccx, ccy, cdx, cdy, cvalid, cready, runData, runValid, runReady) _ ->
            let startedReg = regBit "started"
            let rowReg = reg "row" rowCountWidth // 0..height
            let addr0Cur = reg "addr0" addrWidth // row byte base (py * widthPadded)
            let cyCur = reg "cy_cur" 32
            let dyReg = reg "dy_q" 32
            let cxReg = reg "cx_q" 32 // frame-constant view params, latched from the command
            let dxReg = reg "dx_q" 32

            let moreRows = wireBit "more_rows"
            bnot (eq rowReg (lit (uint64 height) rowCountWidth)) ==> moreRows

            lit 1UL 1 ==> cready
            (startedReg &&& moreRows) ==> runValid
            (lanePodRunTransporter width height).dematerialize (addr0Cur, cyCur, cxReg, dxReg) ==> runData

            let runXfer = wireBit "run_xfer"
            (startedReg &&& moreRows &&& runReady) ==> runXfer

            If cvalid (fun () ->
                lit 1UL 1 ==> startedReg
                lit 0UL rowCountWidth ==> rowReg
                lit 0UL addrWidth ==> addr0Cur
                ccy ==> cyCur
                cdy ==> dyReg
                ccx ==> cxReg
                cdx ==> dxReg)

            Else (fun () ->
                If runXfer (fun () ->
                    rowReg + lit 1UL rowCountWidth ==> rowReg
                    addr0Cur + lit (uint64 widthPadded) addrWidth ==> addr0Cur
                    cyCur + dyReg ==> cyCur)))

/// The frame gatherer — completion lives where the results land: the beat
/// stream passes through untouched while the counter tracks beats EXITING
/// the egress register. `busy` runs from the command to the last exit;
/// `frame_done` is the one-cycle level the wrapper makes sticky.
let mandelFrameGatherer (width: int) (height: int) =
    let widthPadded = paddedWidth width
    let addrWidth = lanePodAddrWidth width height
    let totalBeats = height * (widthPadded / 16)
    let beatCountWidth = bitsToHold (totalBeats + 1)

    defineModule
        $"MandelFrameGatherer_%d{width}x%d{height}"
        (fun p ->
            (p.inPort "start" 1,
             p.inPort "in_addr" addrWidth,
             p.inPort "in_beat" 128,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_addr" addrWidth,
             p.outPort "out_beat" 128,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1,
             p.outPort "busy" 1,
             p.outPort "frame_done" 1))
        (fun m (pstart, inAddr, inBeat, inValid, inReady, outAddr, outBeat, outValid, outReady, pbusy, pdone) (start: Expr) (s: Stream<Expr * Expr>) ->
            let addr, beat = s.payload
            start ==> pstart
            addr ==> inAddr
            beat ==> inBeat
            s.valid ==> inValid
            inReady ==> s.ready
            m.RegisterStreamReady outReady

            ({ payload = (outAddr, outBeat)
               valid = outValid
               ready = outReady
               layout = layout2 ("addr", addrWidth) ("beat", 128) },
             pbusy,
             pdone))
        (fun (pstart, inAddr, inBeat, inValid, inReady, outAddr, outBeat, outValid, outReady, pbusy, pdone) _ ->
            let busyReg = regBit "busy_reg"
            let writtenCount = reg "written_count" beatCountWidth // beats out this frame
            let frameDoneReg = regBit "frame_done_reg"

            inAddr ==> outAddr
            inBeat ==> outBeat
            inValid ==> outValid
            outReady ==> inReady

            let xfer = wireBit "egress_xfer"
            (inValid &&& outReady) ==> xfer
            let lastWrite = wireBit "last_write"
            (eq writtenCount (lit (uint64 (totalBeats - 1)) beatCountWidth) &&& xfer) ==> lastWrite

            busyReg ==> pbusy
            frameDoneReg ==> pdone

            If pstart (fun () ->
                lit 1UL 1 ==> busyReg
                lit 0UL beatCountWidth ==> writtenCount
                lit 0UL 1 ==> frameDoneReg)

            Else (fun () ->
                lastWrite ==> frameDoneReg
                If xfer (fun () -> writtenCount + lit 1UL beatCountWidth ==> writtenCount)
                If lastWrite (fun () -> lit 0UL 1 ==> busyReg)))

/// A command stream from start/view ports — the boundary-side source every
/// harness and the AXI wrapper share: one beat per start pulse, the view as
/// payload. Ready is exported but unconsulted (command acceptance is
/// unconditional).
let frameCmdStream (start: Expr) (cx: Expr) (cy: Expr) (dx: Expr) (dy: Expr) : Stream<Expr * Expr * Expr * Expr> =
    { payload = (cx, cy, dx, dy)
      valid = start
      ready = wireBit "cmd_ready_w"
      layout = frameCmdLayout }

/// The frame COMPUTE pipeline, composed in the ambient design: command →
/// frame processor → farmed lane pods → the elastic egress register (which
/// breaks the merge tree's combinational ready path from whatever consumes
/// it). Compute only — completion (the gatherer) and the consumer belong to
/// the caller; this function neither knows nor cares what drains it.
let mandelFramePipeline
    (width: int)
    (height: int)
    (maxIter: int)
    (fracBits: int)
    (nThreads: int)
    (numLanes: int)
    (cmd: Stream<Expr * Expr * Expr * Expr>)
    : Stream<Expr * Expr> =
    if numLanes < 1 then
        failwith $"numLanes must be >= 1, got %d{numLanes}"

    cmd
    |> Stream.pipeline3
        (Stream.spec "frame" (mandelFrameProcessor width height))
        (Stream.spec "pod" (mandelLanePod width height maxIter fracBits nThreads)
         |> Stream.lanes numLanes)
        (Stream.specFromFunction (Stream.stage id))

/// The frame pod at ports (16×4, maxIter 8, TWO lanes — dispatch and merge
/// live): the living check renders the whole frame bit-exact against the
/// twin; the oracle throws random starts, views and backpressure at it.
let mandelFramePodHarness =
    design "MandelFramePodHarness" (fun () ->
        let start = inputBit "start"
        let cxOrigin = input "cxOrigin" 32
        let cyOrigin = input "cyOrigin" 32
        let dx = input "dx" 32
        let dy = input "dy" 32

        let beats =
            frameCmdStream start cxOrigin cyOrigin dx dy
            |> mandelFramePipeline 16 4 8 28 8 2

        let out, busy, frameDone =
            instanceNamed "gather" (mandelFrameGatherer 16 4) start beats

        let busyOut = outputBit "busy"
        busy ==> busyOut
        let doneOut = outputBit "frameDone"
        frameDone ==> doneOut
        streamOutput "beat" out)

/// The degenerate scale: numLanes = 1, where dispatch and merge both shortcut
/// to direct connections — the same frame must render through no arbiter at
/// all.
let mandelFramePodHarness1 =
    design "MandelFramePodHarness1" (fun () ->
        let start = inputBit "start"
        let cxOrigin = input "cxOrigin" 32
        let cyOrigin = input "cyOrigin" 32
        let dx = input "dx" 32
        let dy = input "dy" 32

        let beats =
            frameCmdStream start cxOrigin cyOrigin dx dy
            |> mandelFramePipeline 16 4 8 28 8 1

        let out, busy, frameDone =
            instanceNamed "gather" (mandelFrameGatherer 16 4) start beats

        let busyOut = outputBit "busy"
        busy ==> busyOut
        let doneOut = outputBit "frameDone"
        frameDone ==> doneOut
        streamOutput "beat" out)

/// The near-silicon composition: the frame pod behind the AXI master with the
/// egress link probed — everything `MandelFrameAxi` will be, minus the control
/// slave (ports where the registers will go). 64×48 at maxIter 48, FOUR lanes,
/// so the clustered dispatch/merge trees run with their register nodes live
/// (2 clusters of 2).
let mandelFrameDdr =
    design "MandelFrameDdr" (fun () ->
        let start = inputBit "start"
        let cxOrigin = input "cxOrigin" 32
        let cyOrigin = input "cyOrigin" 32
        let dx = input "dx" 32
        let dy = input "dy" 32
        let fbBaseAddr = input "fbBaseAddr" 32

        let piped =
            frameCmdStream start cxOrigin cyOrigin dx dy
            |> mandelFramePipeline 64 48 48 28 8 4

        let beats, busy, frameDone =
            instanceNamed "gather" (mandelFrameGatherer 64 48) start piped

        let busyOut = outputBit "busy"
        busy ==> busyOut
        let doneOut = outputBit "frameDone"
        frameDone ==> doneOut

        let addrWidth = lanePodAddrWidth 64 48

        streamProbe "egress" beats
        |> streamMapTo (axiWriteBeatLayout 32 128) (fun (addr, beat) -> (fbBaseAddr + cat (lit 0UL (32 - addrWidth)) addr, beat, lit 0xFFFFUL 16))
        |> axiMasterWriter 32 128 16)
