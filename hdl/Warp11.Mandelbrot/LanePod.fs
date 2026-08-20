/// One lane-pod — Kotlin's `mandelLanePod`, ported: a barrel lane + its fused
/// raster coord-gen + a row coalescer. Pulls one **row-run** at a time from
/// the `run` stream (`dx | cxOrigin | cy | rowBase` — the frame-constant view
/// params ride the dispatch payload, no broadcast), feeds the lane
/// `widthPadded` pixels (`px_addr` = column, `cx = cxOrigin + col·dx`), and
/// the finished escape counts flow into the coalescer, which stages the row
/// out-of-order and drains it as aligned 128-bit beats on `res`.
///
/// Double-buffered egress: the coord-gen takes the next row as soon as the
/// coalescer has GATHERED the current one (`row_gathered`), not when it has
/// drained it — the ping-pong buffers drain row R while the lane computes
/// row R+1, so the barrel never idles through the drain. The lane still holds
/// only one row at a time, so results need no per-row tag.
module Warp11.Mandelbrot.LanePod

open Warp11
open Warp11.Mandelbrot.Step
open Warp11.Mandelbrot.Lane
open Warp11.Mandelbrot.Coalescer

/// Width padded to a whole number of 16-px beats, so every drained beat is a
/// full aligned DDR write.
let paddedWidth width = ((width + 15) / 16) * 16

/// Byte address width into the padded framebuffer — shared with anything
/// declaring the beat stream or packing a run.
let lanePodAddrWidth width height = bitsToHold (height * paddedWidth width)

/// The run payload — `dx | cxOrigin | cy | rowBase`, low field first. It crosses
/// as one flat bus (a single `run_data` port through the dispatch tree), so it
/// travels by transporter: the frame processor dematerializes, the coord-gen
/// materializes, and both ends read the field order off THIS definition rather
/// than agreeing by hand across two files.
let lanePodRunTransporter width height =
    transporter (layout4 ("rowBase", lanePodAddrWidth width height) ("cy", 32) ("cxOrigin", 32) ("dx", 32))

let lanePodRunWidth width height = (lanePodRunTransporter width height).width

/// The raster coord-gen as its own module: one row-run in, `widthPadded`
/// pixel coords out (`cx = cxOrigin + col·dx`, the px addr carrying the
/// column), `row_base` handed forward for the row's writes, `row_gathered`
/// accepted back — the double-buffer feedback: the next run is taken when
/// the current row is GATHERED, not drained.
let mandelCoordGen (width: int) (height: int) =
    let widthPadded = paddedWidth width
    let colWidth = bitsToHold widthPadded
    let addrWidth = lanePodAddrWidth width height
    let runWidth = lanePodRunWidth width height

    defineModule
        $"MandelCoordGen_%d{width}x%d{height}"
        (fun p ->
            (p.inPort "run_data" runWidth,
             p.inPort "run_valid" 1,
             p.outPort "run_ready" 1,
             p.outPort "px_cx" 32,
             p.outPort "px_cy" 32,
             p.outPort "px_col" colWidth,
             p.outPort "px_valid" 1,
             p.inPort "px_ready" 1,
             p.outPort "row_base" addrWidth,
             p.inPort "row_gathered" 1))
        (fun m (rData, rValid, rReady, pxCx, pxCy, pxCol, pxValid, pxReady, rowBasePort, rowGatheredPort) (run: Stream<Expr>) (rowGathered: Expr) ->
            run.payload ==> rData
            run.valid ==> rValid
            rReady ==> run.ready
            rowGathered ==> rowGatheredPort
            m.RegisterStreamReady pxReady

            ({ payload = (pxCx, pxCy, pxCol)
               valid = pxValid
               ready = pxReady
               layout = layout3 ("cx", 32) ("cy", 32) ("addr", colWidth) },
             rowBasePort))
        (fun (rData, rValid, rReady, pxCx, pxCy, pxCol, pxValid, pxReady, rowBasePort, rowGatheredPort) _ ->
            let busy = regBit "busy" // feeding a row's pixels
            let gathering = regBit "gather" // all fed; waiting for the row to arrive
            let col = reg "col" colWidth
            let cxCur = reg "cx" 32
            let dxReg = reg "dx" 32 // latched from the run payload
            let cyCur = reg "cy" 32
            let rowBase = reg "rowbase" addrWidth

            // The run payload arrives materialized — field order comes from the
            // transporter, not from offsets restated here.
            let matRowBase, matCy, matCx, matDx =
                (lanePodRunTransporter width height).materialize rData

            let runAddr0 = wire "run_addr0" addrWidth
            matRowBase ==> runAddr0
            let runCy = wire "run_cy" 32
            matCy ==> runCy
            let runCx = wire "run_cx" 32
            matCx ==> runCx
            let runDx = wire "run_dx" 32
            matDx ==> runDx

            let idle = bnot busy &&& bnot gathering
            idle ==> rReady // accept a row once the last is gathered
            let runXfer = wireBit "run_xfer"
            (rValid &&& idle) ==> runXfer

            cxCur ==> pxCx
            cyCur ==> pxCy
            col ==> pxCol
            busy ==> pxValid
            rowBase ==> rowBasePort

            let colM1 = lit (uint64 (widthPadded - 1)) colWidth
            let pxXfer = wireBit "px_xfer"
            (busy &&& pxReady) ==> pxXfer
            let lastFed = wireBit "last_fed"
            (pxXfer &&& eq col colM1) ==> lastFed

            // ---- the FSM (each reg a single assignment) ----
            If runXfer (fun () -> lit 1UL 1 ==> busy)
            Else (fun () -> If lastFed (fun () -> lit 0UL 1 ==> busy))
            If lastFed (fun () -> lit 1UL 1 ==> gathering)
            Else (fun () -> If rowGatheredPort (fun () -> lit 0UL 1 ==> gathering))
            If runXfer (fun () -> lit 0UL colWidth ==> col)
            Else (fun () -> If (pxXfer &&& bnot (eq col colM1)) (fun () -> col + lit 1UL colWidth ==> col))
            If runXfer (fun () -> runCx ==> cxCur)
            Else (fun () -> If (pxXfer &&& bnot (eq col colM1)) (fun () -> cxCur + dxReg ==> cxCur))
            If runXfer (fun () -> runDx ==> dxReg)
            If runXfer (fun () -> runCy ==> cyCur)
            If runXfer (fun () -> runAddr0 ==> rowBase))

let mandelLanePod (width: int) (height: int) (maxIter: int) (fracBits: int) (nThreads: int) =
    if width < 1 || height < 1 then
        failwith $"width/height must be >= 1, got %d{width}x%d{height}"

    let widthPadded = paddedWidth width
    let colWidth = bitsToHold widthPadded
    let addrWidth = lanePodAddrWidth width height
    let iterWidth = laneIterWidth maxIter
    let runWidth = lanePodRunWidth width height
    let coordGen = mandelCoordGen width height
    let lane = mandelBarrelLane maxIter fracBits nThreads colWidth // px addr carries the COLUMN
    let coalescer = mandelRowCoalescer widthPadded addrWidth

    defineModule
        $"MandelLanePod_%d{width}x%d{height}_max%d{maxIter}_n%d{nThreads}"
        (fun p ->
            (p.inPort "run_data" runWidth,
             p.inPort "run_valid" 1,
             p.outPort "run_ready" 1,
             p.outPort "res_addr" addrWidth,
             p.outPort "res_beat" 128,
             p.outPort "res_valid" 1,
             p.inPort "res_ready" 1))
        (fun m (runData, runValid, runReady, resAddr, resBeat, resValid, resReady) (run: Stream<Expr>) ->
            run.payload ==> runData
            run.valid ==> runValid
            runReady ==> run.ready
            m.RegisterStreamReady resReady

            { payload = (resAddr, resBeat)
              valid = resValid
              ready = resReady
              layout = layout2 ("addr", addrWidth) ("beat", 128) })
        (fun (runData, runValid, runReady, resAddr, resBeat, resValid, resReady) _ ->
            // The pod is just the lane pipeline: coord-gen → barrel lane →
            // widen-to-byte → row coalescer → the boundary. The stream chain
            // carries the pixels; two control edges carry what it cannot —
            // rowBase forward to the coalescer, row_gathered back to the
            // coord-gen (the double-buffer feedback). No stage knows its
            // neighbour; this composition is the only place the wiring lives.
            let run =
                { payload = runData
                  valid = runValid
                  ready = runReady
                  layout = layout1 ("data", runWidth) }

            let rowGathered = wireBit "row_gathered_w"
            let px, rowBase = instanceNamed "cg" coordGen run rowGathered
            let laneRes, _allIdle = instanceNamed "lane" lane px

            // Lane result → coalescer fill: the escape count widens to the
            // framebuffer's byte.
            let coalIn =
                laneRes
                |> streamMapTo
                    (layout2 ("col", colWidth) ("value", 8))
                    (fun (col, iter) -> col, (if iterWidth = 8 then iter else cat (lit 0UL (8 - iterWidth)) iter))

            let coalOut, coalGathered, _rowDone = instanceNamed "coal" coalescer rowBase coalIn
            coalGathered ==> rowGathered

            coalOut |> wormhole (streamExport (resAddr, resBeat) resValid resReady))

/// The pod at ports for the oracle and the mini-frame living check: 16×4 at
/// maxIter 8, so a whole frame renders in a couple thousand Sim cycles. The
/// 102-bit run port also puts the wide-stimulus testbench path to work.
let mandelLanePodHarness =
    design "MandelLanePodHarness" (fun () ->
        let run = streamInput "run" (layout1 ("data", lanePodRunWidth 16 4))
        let res = instanceNamed "pod" (mandelLanePod 16 4 8 28 8) run
        streamOutput "res" res)
