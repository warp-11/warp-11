/// The multi-lane WarpCPU-shaped frame renderer — `mandelFramePod`,
/// ported and DECOMPOSED (2026-08-04, the STREAM_API refactor): a command
/// beat enters the frame processor, which mints one row-run per row; the
/// runs farm across `numLanes` lane pods (clustered dispatch, clustered
/// merge, one elastic egress register breaking the combinational ready path
/// from the AXI master); the gatherer owns completion — the frame is done
/// when the last beat has EXITED the egress buffer, counted where the beats
/// land. The dispatcher is coarse (one hand-out per row) and self-balancing;
/// view bounds are runtime inputs latched from the command; the
/// frame-constant `cxOrigin`/`dx` ride the run payload through the
/// registered tree instead of a die-spanning broadcast (the fragile
/// +0.020 ns path at 104 lanes).
module Warp11.Mandelbrot.FramePod

open Warp11
open Warp11.Mandelbrot.Coalescer
open Warp11.Mandelbrot.LanePod

/// The drained-beat layout a frame pod of this size emits — shared with the
/// wrapper reading the stream.
let frameBeatLayout width height =
    layout2 ("addr", lanePodAddrWidth width height) ("beat", beatBits)

/// One command beat per frame: the view, latched whole at start.
let private frameCmdLayout = layout4 ("cx", 32) ("cy", 32) ("dx", 32) ("dy", 32)

/// The frame processor — the row-run generator as its own module: one command
/// beat in, `height` row-runs out (dx | cxOrigin | cy | rowBase), then quiet
/// until the next command. Acceptance is unconditional (`cmd_ready` is
/// constant 1): a command mid-frame restarts the walk, exactly as the fused
/// FSM's `On istart` did.
let mandelFrameProcessorDef (width: int) (height: int) =
    let widthPadded = paddedWidth width
    let addrWidth = lanePodAddrWidth width height
    let runWidth = lanePodRunWidth width height
    let rowCountWidth = bitsToHold (height + 1)

    defModule
        $"MandelFrameProcessor_%d{width}x%d{height}"
        (fun p ->
            (streamInputPorts p "cmd" frameCmdLayout,
             streamOutputPorts p "run" (layout1 ("data", runWidth))))
        (fun (cmdPorts, runPorts) ->
            let cmd = streamSource cmdPorts
            let ccx, ccy, cdx, cdy = cmd.payload
            let startedReg = regBit "started"
            let rowReg = reg "row" rowCountWidth // 0..height
            let addr0Cur = reg "addr0" addrWidth // row byte base (py * widthPadded)
            let cyCur = reg "cy_cur" 32
            let dyReg = reg "dy_q" 32
            let cxReg = reg "cx_q" 32 // frame-constant view params, latched from the command
            let dxReg = reg "dx_q" 32

            let moreRows = wireBit "more_rows"
            bnot (eq rowReg (lit (uint64 height) rowCountWidth)) ==> moreRows

            lit 1UL 1 ==> cmd.ready

            streamDrive
                runPorts
                (startedReg &&& moreRows)
                ((lanePodRunTransporter width height).dematerialize (addr0Cur, cyCur, cxReg, dxReg))

            let runXfer = wireBit "run_xfer"
            (startedReg &&& moreRows &&& runPorts.ready) ==> runXfer

            ifElse [
                (cmd.valid, fun () ->
                    lit 1UL 1 ==> startedReg
                    lit 0UL rowCountWidth ==> rowReg
                    lit 0UL addrWidth ==> addr0Cur
                    ccy ==> cyCur
                    cdy ==> dyReg
                    ccx ==> cxReg
                    cdx ==> dxReg)
                (otherwise, fun () ->
                If runXfer (fun () ->
                    rowReg + lit 1UL rowCountWidth ==> rowReg
                    addr0Cur + lit (uint64 widthPadded) addrWidth ==> addr0Cur
                    cyCur + dyReg ==> cyCur)) ])

/// One frame-processor instance under `instName`, as a stage: the command
/// stream in, the row-run stream out.
let mandelFrameProcessor (width: int) (height: int) instName (cmd: Stream<Expr * Expr * Expr * Expr>) =
    let cmdPorts, runPorts = (mandelFrameProcessorDef width height).NewNamed instName

    streamToInputPorts cmdPorts cmd
    streamOfOutputPorts runPorts

/// What a gatherer instance hands back: the beats it passed through, and the
/// two completion signals. Named rather than a positional triple — `busy` and
/// `frameDone` are both one-bit and a swap would make a frame report finished
/// the moment it started.
type GatheredFrame =
    { beats: Stream<Expr * Expr>
      busy: Expr
      frameDone: Expr }

/// The frame gatherer — completion lives where the results land: the beat
/// stream passes through untouched while the counter tracks beats EXITING
/// the egress register. `busy` runs from the command to the last exit;
/// `frame_done` is the one-cycle level the wrapper makes sticky.
let mandelFrameGathererDef (width: int) (height: int) =
    let widthPadded = paddedWidth width
    let totalBeats = height * (widthPadded / pixelsPerBeat)
    let beatCountWidth = bitsToHold (totalBeats + 1)

    defModule
        $"MandelFrameGatherer_%d{width}x%d{height}"
        (fun p ->
            (p.inPort "start" 1,
             streamInputPorts p "in" (frameBeatLayout width height),
             streamOutputPorts p "out" (frameBeatLayout width height),
             p.outPort "busy" 1,
             p.outPort "frame_done" 1))
        (fun (pstart, inPorts, outPorts, pbusy, pdone) ->
            let inAddr, inBeat = inPorts.payload
            let inValid, inReady, outReady = inPorts.valid, inPorts.ready, outPorts.ready
            let busyReg = regBit "busy_reg"
            let writtenCount = reg "written_count" beatCountWidth // beats out this frame
            let frameDoneReg = regBit "frame_done_reg"

            streamDrive outPorts inValid (inAddr, inBeat)
            outReady ==> inReady

            let xfer = wireBit "egress_xfer"
            (inValid &&& outReady) ==> xfer
            let lastWrite = wireBit "last_write"
            (eq writtenCount (lit (uint64 (totalBeats - 1)) beatCountWidth) &&& xfer) ==> lastWrite

            busyReg ==> pbusy
            frameDoneReg ==> pdone

            ifElse [
                (pstart, fun () ->
                    lit 1UL 1 ==> busyReg
                    lit 0UL beatCountWidth ==> writtenCount
                    lit 0UL 1 ==> frameDoneReg)
                (otherwise, fun () ->
                lastWrite ==> frameDoneReg
                If xfer (fun () -> writtenCount + lit 1UL beatCountWidth ==> writtenCount)
                If lastWrite (fun () -> lit 0UL 1 ==> busyReg)) ])

/// One gatherer instance under `instName`: the start pulse and the beat
/// stream in, the (pass-through stream, busy, frame_done) triple out.
let mandelFrameGatherer (width: int) (height: int) instName (start: Expr) (s: Stream<Expr * Expr>) =
    let pstart, inPorts, outPorts, pbusy, pdone = (mandelFrameGathererDef width height).NewNamed instName

    start ==> pstart
    streamToInputPorts inPorts s

    { beats = streamOfOutputPorts outPorts
      busy = pbusy
      frameDone = pdone }

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
        (Stream.specOf "frame" (mandelFrameProcessor width height))
        (Stream.specOf "pod" (mandelLanePod width height maxIter fracBits nThreads)
         |> Stream.lanes numLanes)
        (Stream.specFromFunction (Stream.stage id))

/// The frame pod at ports (16×4, maxIter 8, TWO lanes — dispatch and merge
/// live): the living check renders the whole frame bit-exact against the
/// twin; the oracle throws random starts, views and backpressure at it.
let mandelFramePodHarness =
    defModule
        "MandelFramePodHarness"
        (fun p ->
            (p.inPort "start" 1,
             p.inPort "cxOrigin" 32,
             p.inPort "cyOrigin" 32,
             p.inPort "dx" 32,
             p.inPort "dy" 32,
             p.outPort "busy" 1,
             p.outPort "frameDone" 1,
             streamOutputPorts p "beat" (frameBeatLayout 16 4)))
        (fun (start, cxOrigin, cyOrigin, dx, dy, busyOut, doneOut, beatPorts) ->
            let beats =
                frameCmdStream start cxOrigin cyOrigin dx dy
                |> mandelFramePipeline 16 4 8 28 8 2

            let gathered = mandelFrameGatherer 16 4 "gather" start beats

            gathered.busy ==> busyOut
            gathered.frameDone ==> doneOut
            streamSink beatPorts gathered.beats)

/// The degenerate scale: numLanes = 1, where dispatch and merge both shortcut
/// to direct connections — the same frame must render through no arbiter at
/// all.
let mandelFramePodHarness1 =
    defModule
        "MandelFramePodHarness1"
        (fun p ->
            (p.inPort "start" 1,
             p.inPort "cxOrigin" 32,
             p.inPort "cyOrigin" 32,
             p.inPort "dx" 32,
             p.inPort "dy" 32,
             p.outPort "busy" 1,
             p.outPort "frameDone" 1,
             streamOutputPorts p "beat" (frameBeatLayout 16 4)))
        (fun (start, cxOrigin, cyOrigin, dx, dy, busyOut, doneOut, beatPorts) ->
            let beats =
                frameCmdStream start cxOrigin cyOrigin dx dy
                |> mandelFramePipeline 16 4 8 28 8 1

            let gathered = mandelFrameGatherer 16 4 "gather" start beats

            gathered.busy ==> busyOut
            gathered.frameDone ==> doneOut
            streamSink beatPorts gathered.beats)

/// The near-silicon composition: the frame pod behind the AXI master with the
/// egress link probed — everything `MandelFrameAxi` will be, minus the control
/// slave (ports where the registers will go). 64×48 at maxIter 48, FOUR lanes,
/// so the clustered dispatch/merge trees run with their register nodes live
/// (2 clusters of 2).
let mandelFrameDdr =
    defModule
        "MandelFrameDdr"
        (fun p ->
            (p.inPort "start" 1,
             p.inPort "cxOrigin" 32,
             p.inPort "cyOrigin" 32,
             p.inPort "dx" 32,
             p.inPort "dy" 32,
             p.inPort "fbBaseAddr" 32,
             axiWriteBusPorts p "m_axi" 32 128,
             p.outPort "busy" 1,
             p.outPort "frameDone" 1))
        (fun (start, cxOrigin, cyOrigin, dx, dy, fbBaseAddr, writeBusPorts, busyOut, doneOut) ->
        let addrWidth = lanePodAddrWidth 64 48

        // The framebuffer as a window, as in `FrameAxi`. The index is the
        // beat's byte offset with its low four bits dropped — a 128-bit beat is
        // sixteen bytes and the window shifts them straight back — so where the
        // buffer starts and how far apart beats are stay the window's business.
        let frame =
            writeWindowOn (axiWriteBusOf writeBusPorts) 16 "fb" fbBaseAddr (1 <<< (addrWidth - 4))

        let piped =
            frameCmdStream start cxOrigin cyOrigin dx dy
            |> mandelFramePipeline 64 48 48 28 8 4

        let gathered = mandelFrameGatherer 64 48 "gather" start piped

        gathered.busy ==> busyOut

        // The same completion as `FrameAxi`'s, because this design is that one
        // minus the control slave — a `frameDone` meaning something weaker here
        // would make the rehearsal rehearse the wrong thing. It is a level, not
        // the gatherer's pulse: sticky from the last beat gathered, cleared on
        // `start`, and held low until the framebuffer says every word landed.
        let allGathered = regBit "all_gathered"

        ifElse [
            (start, fun () -> lit 0UL 1 ==> allGathered)
            (otherwise, fun () -> If gathered.frameDone (fun () -> lit 1UL 1 ==> allGathered)) ]

        (allGathered &&& frame.idle) ==> doneOut

        streamProbe "egress" gathered.beats
        |> streamMapTo
            (layout2 ("index", addrWidth - 4) ("word", 128))
            (fun (addr, beat) -> slice (addrWidth - 1) 4 addr, beat)
        |> frame.write)
