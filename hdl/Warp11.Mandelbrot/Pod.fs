/// The mini Mandelbrot pod — the Mandelbrot path's acceptance artifact. Four
/// barrel lanes of four threads each iterate Q4.28 pixels; results fan in
/// through the round-robin merge tree to a framebuffer mem the host reads back
/// through the Sim's PeekMem backdoor. Everything here is composition of what
/// Program.fs already verified piecewise: mems as barrel state, On as control,
/// the Fixed layer as the step, streamMergeTree as the fan-in. AXI, Vivado and
/// the board are out of spike scope.
module Warp11.Mandelbrot.Pod

open Warp11
open Warp11.NumberOperators

// The numeric layer is used qualified rather than opened: it has its own `wire`
// and `input`, which would shadow the DSL's for the rest of the file. Only the
// formats are bound locally, because they are what the arithmetic reads as.
let private q4_28 = Number.q4_28
let private q8_56 = Number.q8_56
let private q9_55 = Number.q9_55

/// Pixel coordinates are integers — the `fracBits = 0` case of the same numeric
/// layer the Q4.28 coordinates use, so `px * step` needs no integer-times-fixed
/// special case.
let private pixelIndex = Number.signedInt 7

let internal mandelWidth = 64
let internal mandelHeight = 48
let internal mandelMaxIter = 48UL
// x spans [-2.25, 0.75), y spans [-1.125, 1.125): width 3.0 over 64 pixels
// makes the step 3/64 — exact in Q4.28 — and the pixels square.
let internal mandelStep = 3.0 / 64.0
let internal mandelXMin = -2.25
let internal mandelYMin = -1.125
/// 4096 words — the frame's 3072 pixels plus dead space to the power of two.
let internal fbAddrWidth = 12

let resultLayout = layout2 ("pixel", 12) ("iter", 8)

/// One barrel lane: four threads round-robin through one shared Q4.28 step, so
/// each cycle advances one thread by one iteration — the real pod's shape, with
/// the multiplier latency the barrel exists to hide collapsed to one cycle at
/// spike scale. Threads live in 4-entry LUTRAM-shaped mems (combinational
/// reads; small enough that the readAsync silicon trap does not apply). A
/// finished thread offers (pixel, iter) on the output stream and holds its
/// state until the beat is taken — losing arbitration in the merge tree just
/// means retrying next barrel round. Pixels stride by lane count: this lane
/// owns indices `4k + lane_base`, so `cat count base` is the next index and no
/// adder exists. The lane index arrives as a constant-driven input port, which
/// is what keeps all four instances one module.
let mandelLane =
    defineModule
        "MandelLane"
        (fun p ->
            (p.inPort "lane_base" 2,
             p.outPort "out_pixel" 12,
             p.outPort "out_iter" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun m (laneBase, outPixel, outIter, outValid, outReady) baseValue ->
            baseValue ==> laneBase
            m.RegisterStreamReady outReady

            { payload = (outPixel, outIter)
              valid = outValid
              ready = outReady
              layout = resultLayout })
        (fun (laneBase, outPixel, outIter, outValid, outReady) _ ->
            let zxMem = distributedMem "zx" 2 32
            let zyMem = distributedMem "zy" 2 32
            let cxMem = distributedMem "cx" 2 32
            let cyMem = distributedMem "cy" 2 32
            let iterMem = distributedMem "iter" 2 8
            let pixelMem = distributedMem "pixel" 2 12
            let activeMem = distributedMem "active" 2 1

            let thread = reg "thread" 2
            thread + lit 1UL 2 ==> thread

            // this thread's state, landed in wires (a signed multiply and
            // slice both need names)
            let zxT = wire "zx_t" 32
            memRead zxMem thread ==> zxT
            let zyT = wire "zy_t" 32
            memRead zyMem thread ==> zyT
            let cxT = wire "cx_t" 32
            memRead cxMem thread ==> cxT
            let cyT = wire "cy_t" 32
            memRead cyMem thread ==> cyT
            let iterT = wire "iter_t" 8
            memRead iterMem thread ==> iterT
            let pixelT = wire "pixel_t" 12
            memRead pixelMem thread ==> pixelT
            let activeT = wireBit "active_t"
            memRead activeMem thread ==> activeT

            // escapeStep28's arithmetic, verbatim
            let zx = Number.ofBits q4_28 zxT
            let zy = Number.ofBits q4_28 zyT
            let zx2 = Number.wire "zx2" (zx * zx)
            let zy2 = Number.wire "zy2" (zy * zy)
            let xy = Number.wire "xy" (zx * zy)
            let magnitude = Number.wire "magnitude" (zx2 + zy2)
            let escape = wireBit "escape"
            Number.lessThan (Number.constant q8_56 4.0) magnitude ==> escape
            let zReal = Number.wire "z_real" (zx2 - zy2)
            let zxNext = Number.renormTo q4_28 zReal + Number.ofBits q4_28 cxT
            let zyNext = Number.renormTo q4_28 (Number.reinterpret q9_55 xy) + Number.ofBits q4_28 cyT

            let finish = wireBit "finish"
            (escape ||| eq iterT (lit mandelMaxIter 8)) ==> finish

            pixelT ==> outPixel
            iterT ==> outIter
            (activeT &&& finish) ==> outValid
            let taken = wireBit "taken"
            (outValid &&& outReady) ==> taken

            // next-pixel machinery: count strides this lane's share of the frame
            let count = reg "strideCount" 10
            let morePixels = wireBit "more_pixels"
            lt count (lit (uint64 (mandelWidth * mandelHeight / 4)) 10) ==> morePixels
            let newPixel = wire "new_pixel" 12
            cat count laneBase ==> newPixel
            let px = wire "px" 7
            cat (lit 0UL 1) (slice 5 0 newPixel) ==> px
            let py = wire "py" 7
            cat (lit 0UL 1) (slice 11 6 newPixel) ==> py
            let step = wire "step" 32
            (Number.constant q4_28 mandelStep) ==> step

            let pxScaled = Number.wire "px_scaled" (Number.ofBits pixelIndex px * Number.ofBits q4_28 step)
            let pyScaled = Number.wire "py_scaled" (Number.ofBits pixelIndex py * Number.ofBits q4_28 step)
            let cxNew = Number.renormTo q4_28 pxScaled + Number.constant q4_28 mandelXMin
            let cyNew = Number.renormTo q4_28 pyScaled + Number.constant q4_28 mandelYMin

            // one action per thread visit: step, offer-and-hold, reload, or retire
            let stepping = wireBit "stepping"
            (activeT &&& bnot finish) ==> stepping
            let reload = wireBit "reload"
            ((taken ||| bnot activeT) &&& morePixels) ==> reload
            let retire = wireBit "retire"
            (taken &&& bnot morePixels) ==> retire

            memWrite zxMem thread zxNext.bits stepping
            memWrite zxMem thread (lit 0UL 32) reload
            memWrite zyMem thread zyNext.bits stepping
            memWrite zyMem thread (lit 0UL 32) reload
            memWrite iterMem thread (iterT + lit 1UL 8) stepping
            memWrite iterMem thread (lit 0UL 8) reload
            memWrite cxMem thread cxNew.bits reload
            memWrite cyMem thread cyNew.bits reload
            memWrite pixelMem thread newPixel reload
            memWrite activeMem thread (lit 1UL 1) reload
            memWrite activeMem thread (lit 0UL 1) retire

            If reload (fun () -> count + lit 1UL 10 ==> count))

/// The pod: four lanes, the merge tree, and a framebuffer as the sole (always
/// ready) consumer. `done` rises when every pixel's result has landed; the
/// merged beat is also exported at ports so the differential oracle sees the
/// startup and arbitration behavior, not just a idle `done`.
/// What a boundary needs from the pod's machinery: the framebuffer for a read
/// window, the count and done predicate for status, the merged beat for
/// observability. Exprs, not ports — the caller decides what becomes a port
/// (mandelPod) and what feeds a bus (mandelPodAxi).
type private PodParts =
    { fb: Mem
      resultCount: Expr
      donePredicate: Expr
      pixel: Expr
      iter: Expr
      valid: Expr }

/// The pod's machinery, elaborated in the current design — lanes, merge tree,
/// framebuffer, result counter. Factored out so the standalone design and the
/// AXI wrapper are the same elaboration behind different boundaries.
let private mandelPodParts () : PodParts =
    let lanes =
        [ for i in 0 .. 3 -> instanceNamed $"lane%d{i}" mandelLane (lit (uint64 i) 2) ]

    // The framebuffer write cannot refuse a result, and never needed to: the
    // lanes are rate-matched to it by construction.
    let merged = streamToFlow (streamMergeTree lanes)
    let pixel, iterCount = merged.payload

    let fb = blockMem "fb" fbAddrWidth 8
    let resultCount = reg "resultCount" 13

    If merged.valid (fun () ->
        memWrite fb pixel iterCount (lit 1UL 1)
        resultCount + lit 1UL 13 ==> resultCount)

    { fb = fb
      resultCount = resultCount
      donePredicate = eq resultCount (lit (uint64 (mandelWidth * mandelHeight)) 13)
      pixel = pixel
      iter = iterCount
      valid = merged.valid }

let mandelPod =
    design "MandelPod" (fun () ->
        let parts = mandelPodParts ()

        let finished = outputBit "done"
        parts.donePredicate ==> finished

        let resultPixel = output "result_pixel" 12
        let resultIter = output "result_iter" 8
        let resultValid = outputBit "result_valid"
        parts.pixel ==> resultPixel
        parts.iter ==> resultIter
        parts.valid ==> resultValid)

// ---------------------------------------------------------------------------
// The AXI wrapper and the register-map seam. The map is defined once, here,
// and consumed twice: the slave elaboration below and the generated Rust
// layout (a committed .rs is the F#↔Rust boundary — notes/FINDINGS.md).

/// Reads as "F# pod v1" if you squint; the driver's first sanity read.
let internal idMagic = 0xF5B0D001UL
let internal idOffset = 0x00UL
let internal doneOffset = 0x04UL
let internal resultCountOffset = 0x08UL
let internal scratchOffset = 0x0CUL
let internal frameCyclesOffset = 0x10UL
let internal fbOffset = 0x4000UL
let internal apertureAddrWidth = 15

/// The run-once pod behind an AXI-Lite boundary: ID, done, result count, a
/// scratch register to smoke the write path, a frame-cycle counter that
/// freezes at done (so fabric time is a measured number at first light), and
/// the framebuffer as a 4096-word read window ending the aperture. The pod
/// starts at reset release and runs once — the seam, notes/FINDINGS.md; start/soft
/// reset is the named fast-follow.
let mandelPodAxi =
    designClocked axiClock "MandelPodAxi" (fun () ->
        let parts = mandelPodParts ()

        let frameCycles = reg "frame_cycles" 32
        If (bnot parts.donePredicate) (fun () -> frameCycles + lit 1UL 32 ==> frameCycles)

        axiLiteSlave
            apertureAddrWidth
            [ "scratch", scratchOffset, 32 ]
            [ idOffset, lit idMagic 32
              doneOffset, parts.donePredicate
              resultCountOffset, parts.resultCount
              frameCyclesOffset, frameCycles ]
            [ fbOffset, parts.fb ]
        |> ignore)
