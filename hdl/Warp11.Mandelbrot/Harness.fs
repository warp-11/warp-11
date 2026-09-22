/// The lane at ports for the oracle and the living checks: the cone, the
/// barrel, the coalescer alone and in a loop, and the pod, each a design
/// the differential throws random stimulus at.
module Warp11.Mandelbrot.Harness

open Warp11
open Warp11.Mandelbrot.Lane

/// The cone at ports, for the oracle: the testbench pokes a NEW random vector
/// every cycle and checks every cycle, so II=1 is differentially exercised,
/// not asserted.
let mandelStepHarness =
    defModule
        "MandelStepHarness"
        (fun p ->
            (p.inPort "zx" 32,
             p.inPort "zy" 32,
             p.inPort "cx" 32,
             p.inPort "cy" 32,
             p.outPort "zx_next" 32,
             p.outPort "zy_next" 32,
             p.outPort "escaped" 1))
        (fun (zx, zy, cx, cy, zxNextOut, zyNextOut, escapedOut) ->
            let stepped = mandelStep 28 "step" { re = zx; im = zy } { re = cx; im = cy }
            stepped.z.re ==> zxNextOut
            stepped.z.im ==> zyNextOut
            stepped.escaped ==> escapedOut)

/// The lane at ports for the oracle: maxIter 8 so escapes AND max-outs both
/// happen inside the testbench's 50 cycles, 8 threads, random px beats and
/// random res_ready — pull, refill-at-issue, the delay chains, DONE-PENDING
/// holds and the emit arbitration all under the differential.
let mandelLaneHarness =
    defModule
        "MandelLaneHarness"
        (fun p ->
            (streamInputPorts p "px" (layout3 ("cx", 32) ("cy", 32) ("addr", 8)),
             streamOutputPorts p "res" (layout2 ("addr", 8) ("iter", laneIterWidth 8)),
             p.outPort "all_idle" 1))
        (fun (pxPorts, resPorts, idle) ->
            let px = streamSource pxPorts
            let res, allIdle = mandelBarrelLane 8 28 8 8 "lane" px
            streamSink resPorts res
            allIdle ==> idle)

/// The coalescer at ports (widthPadded 32 → two beats/row): the living check
/// feeds shuffled columns and asserts byte placement; the oracle's random
/// fill-side stimulus rides the same design.
let mandelCoalescerHarness =
    defModule
        "MandelCoalescerHarness"
        (fun p ->
            (p.inPort "row_base" 8,
             streamInputPorts p "px" (layout2 ("col", 5) ("value", 8)),
             streamOutputPorts p "beat" (layout2 ("addr", 8) ("beat", 128)),
             p.outPort "row_gathered" 1,
             p.outPort "row_done" 1))
        (fun (rowBase, pxPorts, beatPorts, g, d) ->
            let coalesced = mandelRowCoalescer 32 8 "coal" rowBase (streamSource pxPorts)

            streamSink beatPorts coalesced.beats
            coalesced.gathered ==> g
            coalesced.rowDone ==> d)

/// Self-feeding coalescer (widthPadded 16): an internal raster feeder offers a
/// pixel every cycle, so complete fill → 17-cycle assembly → emit → ping-pong
/// cycles all happen inside the oracle's 50 cycles, with the testbench's
/// random `beat_ready` throttling the drain — the sync-read assembly timing
/// differentially verified, not just asserted.
let mandelCoalescerLoop =
    defModule
        "MandelCoalescerLoop"
        (fun p -> streamOutputPorts p "beat" (layout2 ("addr", 8) ("beat", 128)))
        (fun beatPorts ->
            let feedCol = reg "feed_col" 4
            let feedRow = reg "feed_row" 3
            let feedReady = wireBit "feed_ready"
            let feedValue = wire "feed_value" 8
            cat (lit 0UL 1) (cat feedRow feedCol) ==> feedValue
            let rowBase = wire "row_base_w" 8
            cat (lit 0UL 1) (cat feedRow (lit 0UL 4)) ==> rowBase

            let feed =
                { payload = (feedCol, feedValue)
                  valid = lit 1UL 1
                  ready = feedReady
                  layout = layout2 ("col", 4) ("value", 8) }

            let coalesced = mandelRowCoalescer 16 8 "coal" rowBase feed

            If feedReady (fun () -> feedCol + lit 1UL 4 ==> feedCol)
            If coalesced.gathered (fun () -> feedRow + lit 1UL 3 ==> feedRow)
            streamSink beatPorts coalesced.beats)

/// The pod at ports for the oracle and the mini-frame living check: 16×4 at
/// maxIter 8, so a whole frame renders in a couple thousand Sim cycles. The
/// 102-bit run port also puts the wide-stimulus testbench path to work.
let mandelLanePodHarness =
    defModule
        "MandelLanePodHarness"
        (fun p ->
            (streamInputPorts p "run" (layout1 ("data", lanePodRunWidth 16 4)),
             streamOutputPorts p "res" (layout2 ("addr", lanePodAddrWidth 16 4) ("beat", 128))))
        (fun (runPorts, resPorts) ->
            let res = mandelLanePod 16 4 8 28 8 "pod" (streamSource runPorts)
            streamSink resPorts res)
