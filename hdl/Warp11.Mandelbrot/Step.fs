/// The full-scale pod's iteration cone — Kotlin's `MandelStep`, ported stage
/// for stage. One pipelined Mandelbrot step: the fused `z² + c` cone sliced
/// into `mandelStepLatency` registered stages so the three signed 32×32
/// multiplies stand between registers and the tools retime them onto the
/// DSP48 cascade (MREG/PREG). Initiation interval is 1 — a new vector may
/// enter every cycle regardless of depth, which is what makes the latency
/// free in a barrel: it costs threads (context), not throughput.
module Warp11.Mandelbrot.Step

open Warp11

/// Cycles from an input vector entering to its (zxNext, zyNext, escaped)
/// triple appearing. The barrel that wraps this cone must interleave more
/// threads than this, and any control travelling with the data rides a
/// matching delay chain.
let mandelStepLatency = 4

/// Combinational reference (bit-exact, latency aside):
///   zxx = (zx*zx) >> f ;  zyy = (zy*zy) >> f ;  zxy = (zx*zy) >> f
///   escaped = (zxx + zyy) >s (4 << f)        — signed, on the input z
///   zxNext  = trunc32(zxx - zyy + cx)
///   zyNext  = trunc32(zxy + zxy + cy)        — 2·zx·zy via add
/// Products are recovered at the (64-fracBits)-bit working width (Q8.28 for
/// Q4.28 inputs) in one part-select — arithmetic shift and narrowing in a
/// single slice, the renormTo trick. Truncation back to 32 is a no-op
/// pre-escape (|zx|,|zy| < 2 then); post-escape the value is discarded.
///
/// Stage 1  register the inputs                 (DSP48 A/B regs)
/// Stage 2  the three products, registered      (DSP48 M reg)
/// Stage 3  second product register             (retimes into the cascade — PREG)
/// Stage 4  recover Q, next-z + escape, register the outputs
let mandelStep (fracBits: int) =
    if fracBits < 1 || fracBits > 30 then
        failwith $"fracBits must be 1..30, got %d{fracBits}"

    let shWidth = 64 - fracBits // the shifted-product working width
    let escapeThresh = 4UL <<< fracBits

    defineModule
        $"MandelStep_q%d{32 - fracBits}_%d{fracBits}"
        (fun p ->
            (p.inPortAs "zx" (SInt 32),
             p.inPortAs "zy" (SInt 32),
             p.inPortAs "cx" (SInt 32),
             p.inPortAs "cy" (SInt 32),
             p.outPortAs "zx_next" (SInt 32),
             p.outPortAs "zy_next" (SInt 32),
             p.outPort "escaped" 1))
        (fun m (izx, izy, icx, icy, ozxn, ozyn, oesc) zx zy cx cy ->
            zx ==> izx
            zy ==> izy
            cx ==> icx
            cy ==> icy
            (ozxn, ozyn, oesc))
        (fun (zx, zy, cx, cy, zxNext, zyNext, escaped) _ ->
            // Stage 1 — register the inputs.
            let s1zx = reg "s1_zx" (SInt 32)
            zx ==> s1zx
            let s1zy = reg "s1_zy" (SInt 32)
            zy ==> s1zy
            let s1cx = reg "s1_cx" (SInt 32)
            cx ==> s1cx
            let s1cy = reg "s1_cy" (SInt 32)
            cy ==> s1cy

            // Stage 2 — the three products stand alone; c rides along.
            let s2zxx = reg "s2_zxx" (SInt 64)
            mul s1zx s1zx ==> s2zxx
            let s2zyy = reg "s2_zyy" (SInt 64)
            mul s1zy s1zy ==> s2zyy
            let s2zxy = reg "s2_zxy" (SInt 64)
            mul s1zx s1zy ==> s2zxy
            let s2cx = reg "s2_cx" (SInt 32)
            s1cx ==> s2cx
            let s2cy = reg "s2_cy" (SInt 32)
            s1cy ==> s2cy

            // Stage 3 — second product register, so a 32×32 spanning two
            // cascaded DSP48s absorbs it; c rides along.
            let s3zxx = reg "s3_zxx" (SInt 64)
            s2zxx ==> s3zxx
            let s3zyy = reg "s3_zyy" (SInt 64)
            s2zyy ==> s3zyy
            let s3zxy = reg "s3_zxy" (SInt 64)
            s2zxy ==> s3zxy
            let s3cx = reg "s3_cx" (SInt 32)
            s2cx ==> s3cx
            let s3cy = reg "s3_cy" (SInt 32)
            s2cy ==> s3cy

            // Stage 4 — recover the Q-format products (one part-select each),
            // form next-z + the escape test, register the outputs.
            // The part-select lands the Q-format product's sign bit at the top,
            // which is what makes these signed values rather than raw bits.
            let zxxQ = wire "zxxQ" (SInt shWidth)
            slice 63 fracBits s3zxx ==> zxxQ
            let zyyQ = wire "zyyQ" (SInt shWidth)
            slice 63 fracBits s3zyy ==> zyyQ
            let zxyQ = wire "zxyQ" (SInt shWidth)
            slice 63 fracBits s3zxy ==> zxyQ

            let zMagSq = wire "zMagSq" (SInt shWidth)
            zxxQ + zyyQ ==> zMagSq
            let escapedNow = wireBit "escapedNow"
            lt (lit escapeThresh shWidth) zMagSq ==> escapedNow

            let zxNextW = wire "zxNextW" (SInt shWidth)
            zxxQ - zyyQ + signExtend shWidth s3cx ==> zxNextW
            let zyNextW = wire "zyNextW" (SInt shWidth)
            zxyQ + zxyQ + signExtend shWidth s3cy ==> zyNextW

            let s4zx = reg "s4_zx" (SInt 32)
            slice 31 0 zxNextW ==> s4zx
            let s4zy = reg "s4_zy" (SInt 32)
            slice 31 0 zyNextW ==> s4zy
            let s4esc = regBit "s4_esc"
            escapedNow ==> s4esc

            s4zx ==> zxNext
            s4zy ==> zyNext
            s4esc ==> escaped)

/// The software twin of one step — every truncation, wrap and compare the
/// same, in host integers, latency aside. GEP's pattern: the fabric is right
/// when it matches this with no tolerance.
let stepTwin (fracBits: int) (zx: uint64) (zy: uint64) (cx: uint64) (cy: uint64) =
    let mask32 = 0xFFFFFFFFUL
    let shWidth = 64 - fracBits
    let maskSh = (1UL <<< shWidth) - 1UL
    let prod a b = uint64 (int64 (signExtend64 32 a) * int64 (signExtend64 32 b))
    let q v = uint64 (int64 v >>> fracBits) &&& maskSh
    let zxxQ = q (prod zx zx)
    let zyyQ = q (prod zy zy)
    let zxyQ = q (prod zx zy)
    let zMagSq = (zxxQ + zyyQ) &&& maskSh
    let escaped = int64 (signExtend64 shWidth (4UL <<< fracBits)) < int64 (signExtend64 shWidth zMagSq)
    let cxSx = signExtend64 32 cx &&& maskSh
    let cySx = signExtend64 32 cy &&& maskSh
    let zxNext = (zxxQ - zyyQ + cxSx) &&& mask32
    let zyNext = (zxyQ + zxyQ + cySx) &&& mask32
    (zxNext, zyNext, (if escaped then 1UL else 0UL))

/// The cone at ports, for the oracle: the testbench pokes a NEW random vector
/// every cycle and checks every cycle, so II=1 is differentially exercised,
/// not asserted.
let mandelStepHarness =
    design "MandelStepHarness" (fun () ->
        let zx = input "zx" 32
        let zy = input "zy" 32
        let cx = input "cx" 32
        let cy = input "cy" 32
        let zxNextOut = output "zx_next" 32
        let zyNextOut = output "zy_next" 32
        let escapedOut = outputBit "escaped"

        let zxn, zyn, esc = instanceNamed "step" (mandelStep 28) zx zy cx cy
        zxn ==> zxNextOut
        zyn ==> zyNextOut
        esc ==> escapedOut)
