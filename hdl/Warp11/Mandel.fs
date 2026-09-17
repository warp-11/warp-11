/// The Mandelbrot lane, in the library so the canvas can place it: the
/// pipelined `z² + c` cone, the barrel lane that keeps it full, the row
/// coalescer, the raster coord-gen and the pod that chains them — one
/// row-run in, aligned 128-bit beats out — plus the pod at one beat wide
/// as a unit, `mandel16`. Each section keeps the header it was written
/// under in `Warp11.Mandelbrot`, where the frame tops and harnesses stay.
module Warp11.Mandel

open Warp11
open Warp11.Fu

// ---------------------------------------------------------------------------
// Step

// The full-scale pod's iteration cone — `MandelStep`, ported stage
// for stage. One pipelined Mandelbrot step: the fused `z² + c` cone sliced
// into `mandelStepLatency` registered stages so the three signed 32×32
// multiplies stand between registers and the tools retime them onto the
// DSP48 cascade (MREG/PREG). Initiation interval is 1 — a new vector may
// enter every cycle regardless of depth, which is what makes the latency
// free in a barrel: it costs threads (context), not throughput.

/// Cycles from an input vector entering to its (zxNext, zyNext, escaped)
/// triple appearing. The barrel that wraps this cone must interleave more
/// threads than this, and any control travelling with the data rides a
/// matching delay chain.
let mandelStepLatency = 4

/// One point on the complex plane, as the cone's operands travel.
///
/// `z` and `c` are each a pair of 32-bit signed values, so passed positionally
/// they are four adjacent arguments of one type and a call site that swapped a
/// pair would elaborate cleanly and render the wrong set. Named halves make
/// that unwriteable, and `z`/`c` then read at the call site as the two things
/// the arithmetic is about rather than as four numbers.
type Point = { re: Expr; im: Expr }

/// What one step produces: where `z` goes next, and whether the `z` that went
/// in had already escaped.
type StepResult = { z: Point; escaped: Expr }

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
let mandelStepDef (fracBits: int) =
    if fracBits < 1 || fracBits > 30 then
        failwith $"fracBits must be 1..30, got %d{fracBits}"

    let shWidth = 64 - fracBits // the shifted-product working width
    let escapeThresh = 4UL <<< fracBits

    defModule
        $"MandelStep_q%d{32 - fracBits}_%d{fracBits}"
        (fun p ->
            (p.inPortAs "zx" (SInt 32),
             p.inPortAs "zy" (SInt 32),
             p.inPortAs "cx" (SInt 32),
             p.inPortAs "cy" (SInt 32),
             p.outPortAs "zx_next" (SInt 32),
             p.outPortAs "zy_next" (SInt 32),
             p.outPort "escaped" 1))
        (fun (zx, zy, cx, cy, zxNext, zyNext, escaped) ->
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

/// One cone instance under `instName`, called as a function: drive the z and
/// c points, read the next z and the escape flag.
let mandelStep (fracBits: int) instName (z: Point) (c: Point) : StepResult =
    let izx, izy, icx, icy, ozxn, ozyn, oesc = (mandelStepDef fracBits).NewNamed instName
    z.re ==> izx
    z.im ==> izy
    c.re ==> icx
    c.im ==> icy
    { z = { re = ozxn; im = ozyn }; escaped = oesc }

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

// ---------------------------------------------------------------------------
// Lane

// One barrel (thread-interleaved) Mandelbrot lane — `MandelBarrelLane`,
// ported. A single pipelined `mandelStep` cone kept full
// by round-robining `nThreads` independent pixel-threads through it, so the
// deep multiply pipeline never bubbles on the `z ← z² + c` recurrence —
// latency traded for clock, the WarpCPU barrel move.
//
// Each cycle thread `turn` (rotating) issues; the result lands
// `mandelStepLatency` cycles later, with the issue context (thread id,
// iteration, valid) riding a matching delay chain so writeback realigns.
// With `nThreads > latency` a thread's next issue always follows its own
// writeback, so slot state is race-free without a handshake.
//
// Per-thread state (`cx cy addr zx zy iter`) lives in async-read mems —
// LUTRAM-shaped by construction (a combinational read cannot infer BRAM), the
        /// register-file move that freed the LUT/FF budget. `active`/`pend` stay
/// per-slot 1-bit regs: read as a full array by the emit priority mux, not
/// register-file-shaped. Refill happens AT ISSUE: an INACTIVE slot at its turn
/// pulls the next pixel and issues it as iteration 0 the same cycle, so the
/// slots prime themselves organically.
///
/// Egress is a wormhole, not a FIFO: a finished pixel goes DONE-PENDING and
/// holds `(addr, iter)` on the output stream until accepted (lowest pending
/// index wins). A pending slot is neither issued nor refilled, so a stalled
/// consumer backs the slots up and compute throttles losslessly — the N slots
/// are the buffer. `all_idle` rises when every slot is INACTIVE.

/// Bits of a lane's `iter` result for a given maxIter — shared with anything
/// declaring a stream that carries it.
let laneIterWidth maxIter =
    bitsToHold (max (maxIter - 1) 1 + 1) |> max 1

let mandelBarrelLaneDef (maxIter: int) (fracBits: int) (nThreads: int) (addrWidth: int) =
    if maxIter < 1 || maxIter > 256 then
        failwith $"maxIter must fit an 8-bit iter range, got %d{maxIter}"

    if not (isPowerOfTwo nThreads) then
        failwith $"nThreads must be a power of two, got %d{nThreads}"

    // The cone and the interleave, together: `barrel` is where `nThreads >
    // latency` is checked, and where the delay the writeback context rides is
    // stated once instead of at each chain.
    let cone = barrel mandelStepLatency nThreads

    let threadWidth = bitsToHold nThreads
    let iterWidth = laneIterWidth maxIter
    let step = mandelStep fracBits

    defModule
        $"MandelBarrelLane_max%d{maxIter}_n%d{nThreads}_a%d{addrWidth}"
        (fun p ->
            (streamInputPorts p "px" (layout3 ("cx", 32) ("cy", 32) ("addr", addrWidth)),
             streamOutputPorts p "res" (layout2 ("addr", addrWidth) ("iter", iterWidth)),
             p.outPort "all_idle" 1))
        (fun (pxPorts, resPorts, allIdle) ->
            let px = streamSource pxPorts
            let pxCx, pxCy, pxAddr = px.payload
            // ---- per-thread slot state: async-read register files ----
            // Single write site each: cx/cy/addr written at issue, zx/zy only
            // on continue, iter on every valid writeback (continue: +1, done:
            // the escaped iteration) — so an immediate escaper reports right
            // with no reset write; the step-input mux injects 0 for a fresh
            // pixel and the first writeback populates the slot before the
            // next issue reads it (nThreads > latency).
            let cxMem = distributedMem "cxRegFile" threadWidth 32
            let cyMem = distributedMem "cyRegFile" threadWidth 32
            let addrMem = distributedMem "addrRegFile" threadWidth addrWidth
            let zxMem = distributedMem "zxRegFile" threadWidth 32
            let zyMem = distributedMem "zyRegFile" threadWidth 32
            let iterMem = distributedMem "iterRegFile" threadWidth iterWidth
            let active = [ for t in 0 .. nThreads - 1 -> regBit $"active%d{t}" ] // COMPUTING
            let pend = [ for t in 0 .. nThreads - 1 -> regBit $"pend%d{t}" ] // DONE-PENDING

            // ---- schedule ----
            let turn = reg "turn" threadWidth
            turn + lit 1UL threadWidth ==> turn // wraps mod nThreads

            let curZx = wire "curZx" 32
            memRead zxMem turn ==> curZx
            let curZy = wire "curZy" 32
            memRead zyMem turn ==> curZy
            let curIter = wire "curIter" iterWidth
            memRead iterMem turn ==> curIter
            let curCx = wire "curCx" 32
            memRead cxMem turn ==> curCx
            let curCy = wire "curCy" 32
            memRead cyMem turn ==> curCy
            let curActive = wireBit "curActive"
            selectIndexed turn active ==> curActive
            let curPend = wireBit "curPend"
            selectIndexed turn pend ==> curPend

            let needPull = bnot curActive &&& bnot curPend // slot is INACTIVE
            let pull = wireBit "pull"
            (needPull &&& px.valid) ==> pull // consuming a pixel this cycle
            needPull ==> px.ready

            // Issue operands: an active slot continues from its own z; an
            // empty slot that just pulled starts fresh at z=0. Pending/idle →
            // bubble.
            let issueZx = wire "issueZx" 32
            mux curActive curZx (lit 0UL 32) ==> issueZx
            let issueZy = wire "issueZy" 32
            mux curActive curZy (lit 0UL 32) ==> issueZy
            let issueCx = wire "issueCx" 32
            mux curActive curCx pxCx ==> issueCx
            let issueCy = wire "issueCy" 32
            mux curActive curCy pxCy ==> issueCy
            let issueIter = wire "issueIter" iterWidth
            mux curActive curIter (lit 0UL iterWidth) ==> issueIter
            let issueValid = wireBit "issueValid"
            (curActive ||| pull) ==> issueValid

            let stepped = step "step" { re = issueZx; im = issueZy } { re = issueCx; im = issueCy }
            let writebackZxN, writebackZyN, writebackEsc = stepped.z.re, stepped.z.im, stepped.escaped

            // ---- writeback, mandelStepLatency cycles after issue ----
            let writebackTurn = cone.Carry "writebackTurn" threadWidth turn
            let writebackValid = cone.Carry "writebackValidU" 1 issueValid
            let writebackIter = cone.Carry "writebackIter" iterWidth issueIter

            let maxIterM1 = lit (uint64 (maxIter - 1)) iterWidth
            let atMax = eq writebackIter maxIterM1
            let writebackDone = writebackValid &&& (writebackEsc ||| atMax)
            let writebackContinue = writebackValid &&& bnot writebackEsc &&& bnot atMax

            // ---- emit arbitration: lowest-index DONE-PENDING slot wins ----
            let emitSel = oneHotLowest pend

            // ---- per-slot next-state ----
            // Issue and writeback are mutually exclusive per slot each cycle
            // (turn vs turn-delayed-by-L); a pending slot has no writeback in
            // flight and is not issued — one writer per reg per cycle.
            for t in 0 .. nThreads - 1 do
                let issueLoad = eq turn (lit (uint64 t) threadWidth) &&& pull
                let writebackDoneT = eq writebackTurn (lit (uint64 t) threadWidth) &&& writebackDone
                let emitAccept = emitSel[t] &&& resPorts.ready

                ifElse [
                    (writebackDoneT, fun () -> lit 0UL 1 ==> active[t])
                    (otherwise, fun () -> If issueLoad (fun () -> lit 1UL 1 ==> active[t])) ]
                ifElse [
                    (writebackDoneT, fun () -> lit 1UL 1 ==> pend[t])
                    (otherwise, fun () -> If emitAccept (fun () -> lit 0UL 1 ==> pend[t])) ]

            // ---- register-file writes (each mem one write site) ----
            memWrite cxMem turn pxCx pull
            memWrite cyMem turn pxCy pull
            memWrite addrMem turn pxAddr pull
            memWrite zxMem writebackTurn writebackZxN writebackContinue
            memWrite zyMem writebackTurn writebackZyN writebackContinue
            memWrite iterMem writebackTurn (mux writebackDone writebackIter (writebackIter + lit 1UL iterWidth)) writebackValid

            // ---- outputs: the winning pending slot ----
            let emitIndex = wire "emitIndex" threadWidth

            List.foldBack
                    (fun t acc -> mux pend[t] (lit (uint64 t) threadWidth) acc)
                    [ 0 .. nThreads - 2 ]
                    (lit (uint64 (nThreads - 1)) threadWidth)
            ==> emitIndex

            let pendAny = wireBit "pendAny"
            List.reduce (|||) pend ==> pendAny
            streamDrive resPorts pendAny (memRead addrMem emitIndex, memRead iterMem emitIndex)

            let anyBusy = wireBit "anyBusy"
            List.reduce (|||) (List.map2 (|||) active pend) ==> anyBusy
            bnot anyBusy ==> allIdle)

/// One lane instance under `instName`, called as a stage: the pixel stream
/// in, the (result stream, all_idle) pair out.
let mandelBarrelLane (maxIter: int) (fracBits: int) (nThreads: int) (addrWidth: int) instName (px: Stream<Expr * Expr * Expr>) =
    let pxPorts, resPorts, allIdle =
        (mandelBarrelLaneDef maxIter fracBits nThreads addrWidth).NewNamed instName

    streamToInputPorts pxPorts px
    streamOfOutputPorts resPorts, allIdle

/// The whole-pixel software twin: iterate with `stepTwin` from z=0 until the
/// step escapes or the issue iteration reaches maxIter-1, reporting the issue
/// iteration — exactly the lane's writeback rule.
let laneTwin (fracBits: int) (maxIter: int) (cx: uint64) (cy: uint64) =
    let rec go zx zy n =
        let zxn, zyn, esc = stepTwin fracBits zx zy cx cy

        if esc = 1UL || n = maxIter - 1 then uint64 n else go zxn zyn (n + 1)

    go 0UL 0UL 0

// ---------------------------------------------------------------------------
// Coalescer

// Row coalescer — `MandelRowCoalescer`, ported: the hardware
// `buffer(16)` that turns per-pixel egress into aligned 16-pixel 128-bit
// beats, the 16× lever off the HP-port single-beat write ceiling.
//
// Barrel lanes finish pixels out of address order, but a DDR write must be
// aligned and full. Pixels stage into a padded row buffer at their column;
// when a full row of `widthPadded` pixels has arrived it drains as
// `widthPadded`/16 aligned beats. DOUBLE-BUFFERED (ping-pong, both rows in
// ONE mem with the buffer select as the high address bit — two muxed-read
// mems would spill out of BRAM): while one buffer drains, the next row fills
// the other, so `in_ready` stays high across the row boundary.
// `row_gathered` pulses when a fill completes — the coord-gen advances on
// THAT, not on drain-complete. Each buffer latches its own `row_base` on its
// first pixel, so the drain of row R and the fill of row R+1 use independent
// addresses.
//
// The buffer read is SYNCHRONOUS (BRAM — the silicon-safe pattern), so beat
// assembly takes 17 cycles: 16 reads plus one read-latency cycle, columns
// issued descending so the last byte lands in bits [7:0] (DDR byte order),
// the shift gated past the first cycle whose read is still in flight.

/// The consumer's states: waiting for a full buffer, assembling a beat out of
/// one synchronous read per pixel, holding that beat until the sink takes it.
type private Drain =
    | Idle
    | Assemble
    | Emit

/// The row side of the boundary — everything that is not one of the two
/// streams. `rowBase` arrives with the row being filled; the two pulses report
/// a fill completed and a drain completed.
///
/// A bundle rather than three loose ports because `gathered` and `rowDone` are
/// both one-bit outputs about the same row: told apart only by position, a
/// swap would elaborate, emit, and advance the coord-gen on the wrong edge.
type CoalescerRowPorts =
    { rowBase: Input
      gathered: Output
      rowDone: Output }

let rowPorts (p: Ports) (addrWidth: int) : CoalescerRowPorts =
    { rowBase = p.inPort "row_base" addrWidth
      gathered = p.outPort "row_gathered" 1
      rowDone = p.outPort "row_done" 1 }

/// What one coalescer instance hands its caller: the beat stream, and the two
/// row edges. Named for the same reason the ports are — the pod wires
/// `gathered` back to the coord-gen and ignores `rowDone`, and a positional
/// pair would let those two swap silently.
type CoalescerOut =
    { beats: Stream<Expr * Expr>
      gathered: Expr
      rowDone: Expr }

/// A DDR beat and the pixel that packs into it. Every count in this file falls
/// out of these two: how many pixels a beat holds, how many bits of a column
/// index that spends, how wide the assembly counter must be, and where the
/// shift register's window sits. Written down instead, each would be a
/// separate chance to change the framebuffer's pixel format and leave one of
/// them behind — and `slice 119 0` is not a number anyone would notice was
/// stale.
let beatBits = 128

let pixelBits = 8

let pixelsPerBeat = beatBits / pixelBits

let private beatColBits = bitsToHold pixelsPerBeat

let mandelRowCoalescerDef (widthPadded: int) (addrWidth: int) =
    if widthPadded % pixelsPerBeat <> 0 || widthPadded < pixelsPerBeat then
        failwith $"widthPadded must be a positive multiple of %d{pixelsPerBeat}, got %d{widthPadded}"

    let nBeats = widthPadded / pixelsPerBeat
    let colWidth = bitsToHold widthPadded
    let beatIndexWidth = bitsToHold nBeats
    let fillCountWidth = bitsToHold (widthPadded + 1)
    let bufAddrWidth = bitsToHold (2 * widthPadded)
    // 0..pixelsPerBeat inclusive: one read per pixel plus the latency cycle.
    let asmCountWidth = bitsToHold (pixelsPerBeat + 1)

    defModule
        $"MandelRowCoalescer_%d{widthPadded}_a%d{addrWidth}"
        (fun p ->
            (streamInputPorts p "in" (layout2 ("col", colWidth) ("value", pixelBits)),
             streamOutputPorts p "out" (layout2 ("addr", addrWidth) ("beat", beatBits)),
             rowPorts p addrWidth))
        (fun (inPorts, outPorts, row) ->
            let pixels = streamSource inPorts
            let inCol, inValue = pixels.payload
            // Ping-pong row buffers packed into one mem (buffer = high address
            // bit), single write site + single sync read — the BRAM shape.
            let buf = blockMem "rowbuf" bufAddrWidth pixelBits
            let bufHalf sel = mux sel (lit (uint64 widthPadded) bufAddrWidth) (lit 0UL bufAddrWidth)
            let padCol (e: Expr) = cat (lit 0UL (bufAddrWidth - colWidth)) e

            // ---- producer (fill) state ----
            let fillSel = regBit "fill_sel"
            let fillCount = reg "fill_cnt" fillCountWidth
            let fillBase0 = reg "fill_base0" addrWidth
            let fillBase1 = reg "fill_base1" addrWidth
            let full0 = regBit "full0" // buffer holds a complete row
            let full1 = regBit "full1"

            // ---- consumer (drain) state ----
            let drainSel = regBit "drain_sel"
            let drain = machine "cstate" [ Idle; Assemble; Emit ]
            let beatIndex = reg "beat_idx" beatIndexWidth
            let asmCount = reg "asm_cnt" asmCountWidth
            let beatReg = reg "beat_reg" beatBits

            // ---- producer: accept a pixel into the current fill buffer ----
            let fillFull = wireBit "fill_full"
            mux fillSel full1 full0 ==> fillFull
            bnot fillFull ==> pixels.ready // stall only while the fill buffer is undrained
            let accept = wireBit "accept"
            (pixels.valid &&& bnot fillFull) ==> accept
            let fillAddr = wire "fill_addr" bufAddrWidth
            bufHalf fillSel + padCol inCol ==> fillAddr
            memWrite buf fillAddr inValue accept // one write site (BRAM-safe)

            let fillLast = wireBit "fill_last" // this pixel completes the fill buffer
            (accept &&& eq fillCount (lit (uint64 (widthPadded - 1)) fillCountWidth)) ==> fillLast
            fillLast ==> row.gathered
            let firstPix = accept &&& eq fillCount (lit 0UL fillCountWidth) // latch the row's base
            If (firstPix &&& bnot fillSel) (fun () -> row.rowBase ==> fillBase0)
            If (firstPix &&& fillSel) (fun () -> row.rowBase ==> fillBase1)

            If accept (fun () ->
            ifElse [(fillLast, fun () -> lit 0UL fillCountWidth ==> fillCount); (otherwise, fun () -> fillCount + lit 1UL fillCountWidth ==> fillCount) ])

            If fillLast (fun () -> bnot fillSel ==> fillSel) // hand off to the other buffer

            // ---- consumer: drain a full buffer as aligned beats ----
            let cIdle = drain.Is Idle
            let cAsm = drain.Is Assemble
            let cEmit = drain.Is Emit
            let drainFull = wireBit "drain_full"
            mux drainSel full1 full0 ==> drainFull
            let startDrain = cIdle &&& drainFull
            let asmDone = cAsm &&& eq asmCount (lit (uint64 pixelsPerBeat) asmCountWidth)
            let emitAccept = cEmit &&& outPorts.ready
            let lastBeat = eq beatIndex (lit (uint64 (nBeats - 1)) beatIndexWidth)
            let drainDone = wireBit "drain_done"
            (emitAccept &&& lastBeat) ==> drainDone
            drainDone ==> row.rowDone

            // full flags: set by the producer, cleared by the consumer — per
            // buffer mutually exclusive, so each is single-writer.
            ifElse [(fillLast &&& bnot fillSel, fun () -> lit 1UL 1 ==> full0); (otherwise, fun () -> If (drainDone &&& bnot drainSel) (fun () -> lit 0UL 1 ==> full0)) ]
            ifElse [(fillLast &&& fillSel, fun () -> lit 1UL 1 ==> full1); (otherwise, fun () -> If (drainDone &&& drainSel) (fun () -> lit 0UL 1 ==> full1)) ]
            If drainDone (fun () -> bnot drainSel ==> drainSel)

            // byte offset of the current beat within the row (beatIndex*16),
            // sized to colWidth — robust to nBeats=1, where beatIndexWidth+4 > colWidth.
            let beatCat = wire "beat_cat" (beatIndexWidth + beatColBits)
            cat beatIndex (lit 0UL beatColBits) ==> beatCat
            let beatBase = wire "beat_base" colWidth

            (if beatIndexWidth + beatColBits >= colWidth then
                     slice (colWidth - 1) 0 beatCat
                 else
                     cat (lit 0UL (colWidth - beatIndexWidth - beatColBits)) beatCat)
            ==> beatBase

            // ASM issues column beatIndex*16 + (15 - asmCount) each cycle,
            // descending, so the last byte lands in bits [7:0] — DDR order.
            let asmLow = wire "asm_low" beatColBits
            slice (beatColBits - 1) 0 asmCount ==> asmLow
            let revIndex = wire "rev_idx" beatColBits
            lit (uint64 (pixelsPerBeat - 1)) beatColBits - asmLow ==> revIndex
            let asmCol = wire "asm_col" colWidth

            beatBase
            + (if colWidth > beatColBits then
                   cat (lit 0UL (colWidth - beatColBits)) revIndex
               else
                   revIndex)
            ==> asmCol
            let drainAddr = wire "drain_addr" bufAddrWidth
            bufHalf drainSel + padCol asmCol ==> drainAddr
            let bufRd = (memReadPort buf drainAddr).data // synchronous → BRAM + hardware-accurate
            let drainBase = wire "drain_base" addrWidth
            mux drainSel fillBase1 fillBase0 ==> drainBase

            // ---- outputs ----
            let beatAddr =
                drainBase
                + (if addrWidth > colWidth then
                       cat (lit 0UL (addrWidth - colWidth)) beatBase
                   else
                       beatBase)

            streamDrive outPorts cEmit (beatAddr, beatReg)

            // ---- consumer FSM ----
            drain.Switch
                [ Idle, (fun () -> If startDrain (fun () -> drain.Goto Assemble))
                  Assemble, (fun () -> If asmDone (fun () -> drain.Goto Emit))
                  Emit,
                  (fun () ->
                      If emitAccept (fun () ->
                          ifElse [ (lastBeat, fun () -> drain.Goto Idle)
                                   (otherwise, fun () -> drain.Goto Assemble) ])) ]

            // beatIndex: reset entering a drain, advance between beats
            ifElse [(cIdle, fun () -> If startDrain (fun () -> lit 0UL beatIndexWidth ==> beatIndex)); (otherwise, fun () -> If (emitAccept &&& bnot lastBeat) (fun () -> beatIndex + lit 1UL beatIndexWidth ==> beatIndex)) ]

            // asmCount: count 0..16 during ASM, 0 otherwise
            ifElse [(cAsm, fun () ->
                ifElse [(asmDone, fun () -> lit 0UL asmCountWidth ==> asmCount); (otherwise, fun () -> asmCount + lit 1UL asmCountWidth ==> asmCount) ]); (otherwise, fun () -> lit 0UL asmCountWidth ==> asmCount) ]

            // beatReg: shift in the byte that arrived this cycle (the read
            // issued last cycle); asmCount=0's read is still in flight.
            If (cAsm &&& bnot (eq asmCount (lit 0UL asmCountWidth))) (fun () ->
                cat (slice (beatBits - pixelBits - 1) 0 beatReg) bufRd ==> beatReg))

/// One coalescer instance under `instName`: the row base and (col, value)
/// stream in, the (addr, beat) stream plus the two row edges out.
let mandelRowCoalescer (widthPadded: int) (addrWidth: int) instName (rowBase: Expr) (inp: Stream<Expr * Expr>) : CoalescerOut =
    let inPorts, outPorts, row = (mandelRowCoalescerDef widthPadded addrWidth).NewNamed instName

    streamToInputPorts inPorts inp
    rowBase ==> row.rowBase

    { beats = streamOfOutputPorts outPorts
      gathered = row.gathered
      rowDone = row.rowDone }

// ---------------------------------------------------------------------------
// LanePod

// One lane-pod — `mandelLanePod`, ported: a barrel lane + its fused
// raster coord-gen + a row coalescer. Pulls one **row-run** at a time from
// the `run` stream (`dx | cxOrigin | cy | rowBase` — the frame-constant view
// params ride the dispatch payload, no broadcast), feeds the lane
// `widthPadded` pixels (`px_addr` = column, `cx = cxOrigin + col·dx`), and
// the finished escape counts flow into the coalescer, which stages the row
// out-of-order and drains it as aligned 128-bit beats on `res`.
//
// Double-buffered egress: the coord-gen takes the next row as soon as the
// coalescer has GATHERED the current one (`row_gathered`), not when it has
// drained it — the ping-pong buffers drain row R while the lane computes
// row R+1, so the barrel never idles through the drain. The lane still holds
// only one row at a time, so results need no per-row tag.

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
let mandelCoordGenDef (width: int) (height: int) =
    let widthPadded = paddedWidth width
    let colWidth = bitsToHold widthPadded
    let addrWidth = lanePodAddrWidth width height
    let runWidth = lanePodRunWidth width height

    defModule
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
        (fun (rData, rValid, rReady, pxCx, pxCy, pxCol, pxValid, pxReady, rowBasePort, rowGatheredPort) ->
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
            ifElse [
                (runXfer, fun () -> lit 1UL 1 ==> busy)
                (otherwise, fun () -> If lastFed (fun () -> lit 0UL 1 ==> busy)) ]
            ifElse [
                (lastFed, fun () -> lit 1UL 1 ==> gathering)
                (otherwise, fun () -> If rowGatheredPort (fun () -> lit 0UL 1 ==> gathering)) ]
            ifElse [
                (runXfer, fun () -> lit 0UL colWidth ==> col)
                (otherwise, fun () -> If (pxXfer &&& bnot (eq col colM1)) (fun () -> col + lit 1UL colWidth ==> col)) ]
            ifElse [
                (runXfer, fun () -> runCx ==> cxCur)
                (otherwise, fun () -> If (pxXfer &&& bnot (eq col colM1)) (fun () -> cxCur + dxReg ==> cxCur)) ]
            If runXfer (fun () -> runDx ==> dxReg)
            If runXfer (fun () -> runCy ==> cyCur)
            If runXfer (fun () -> runAddr0 ==> rowBase))

/// One coord-gen instance under `instName`: the row-run stream and the
/// row_gathered feedback in, the (pixel stream, row_base) pair out.
let mandelCoordGen (width: int) (height: int) instName (run: Stream<Expr>) (rowGathered: Expr) =
    let widthPadded = paddedWidth width
    let colWidth = bitsToHold widthPadded

    let rData, rValid, rReady, pxCx, pxCy, pxCol, pxValid, pxReady, rowBasePort, rowGatheredPort =
        (mandelCoordGenDef width height).NewNamed instName

    run.payload ==> rData
    run.valid ==> rValid
    rReady ==> run.ready
    rowGathered ==> rowGatheredPort
    registerStreamReady pxReady

    let px: Stream<Expr * Expr * Expr> =
        { payload = (pxCx, pxCy, pxCol)
          valid = pxValid
          ready = pxReady
          layout = layout3 ("cx", 32) ("cy", 32) ("addr", colWidth) }

    px, rowBasePort

let mandelLanePodDef (width: int) (height: int) (maxIter: int) (fracBits: int) (nThreads: int) =
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

    defModule
        $"MandelLanePod_%d{width}x%d{height}_max%d{maxIter}_n%d{nThreads}"
        (fun p ->
            (streamInputPorts p "run" (layout1 ("data", runWidth)),
             streamOutputPorts p "res" (layout2 ("addr", addrWidth) ("beat", 128))))
        (fun (runPorts, resPorts) ->
            // The pod is just the lane pipeline: coord-gen → barrel lane →
            // widen-to-byte → row coalescer → the boundary. The stream chain
            // carries the pixels; two control edges carry what it cannot —
            // rowBase forward to the coalescer, row_gathered back to the
            // coord-gen (the double-buffer feedback). No stage knows its
            // neighbour; this composition is the only place the wiring lives.
            let run = streamSource runPorts

            let rowGathered = wireBit "row_gathered_w"
            let px, rowBase = coordGen "cg" run rowGathered
            let laneRes, _allIdle = lane "lane" px

            // Lane result → coalescer fill: the escape count widens to the
            // framebuffer's byte.
            let coalIn =
                laneRes
                |> streamMapTo
                    (layout2 ("col", colWidth) ("value", 8))
                    (fun (col, iter) -> col, (if iterWidth = 8 then iter else cat (lit 0UL (8 - iterWidth)) iter))

            let coalesced = coalescer "coal" rowBase coalIn
            coalesced.gathered ==> rowGathered

            coalesced.beats |> wormhole (streamSink resPorts))

/// One pod instance under `instName`, as a stage: the row-run stream in, the
/// (addr, beat) result stream out.
let mandelLanePod (width: int) (height: int) (maxIter: int) (fracBits: int) (nThreads: int) instName (run: Stream<Expr>) =
    let runPorts, resPorts = (mandelLanePodDef width height maxIter fracBits nThreads).NewNamed instName

    streamToInputPorts runPorts run
    streamOfOutputPorts resPorts

// ---------------------------------------------------------------------------
// The lane as the canvas places it

/// The view's numbers: signed, `fracBits` of fraction in 32 bits — Q4.28 at
/// the silicon config. Said once, so every pin that carries one agrees.
let viewFormat (fracBits: int) = signedFixed 32 fracBits

/// One beat of the frame, sixteen pixels wide: a byte an escape count,
/// pixel `c` at bits `8c+7 … 8c`, the coalescer's beat as the DDR takes it.
let pixelsFormat = unsignedInt beatBits

/// The pod at one beat wide as a unit: a chunk's view in — its first pixel's
/// `cx0`, its row's `cy`, the step `dx` — and its sixteen escape counts out
/// as one beat. `mandelLanePod 16 1` unchanged: the coord-gen sweeps the
/// sixteen columns, the barrel iterates them, the coalescer's double buffer
/// gathers the beat while the next chunk starts. One beat in, one beat out,
/// so `copies` farms it in order.
///
/// `dx` rides in the beat rather than as a control on purpose: a control on a
/// box spent 104 times is a net across the die, the path the frame design
/// took out of its dispatch tree by carrying `dx` in the run.
let mandel16 (maxIter: int) (fracBits: int) (threads: int) : Fu<Expr * Expr * Expr, Expr> =
    let view = viewFormat fracBits
    let run = lanePodRunTransporter pixelsPerBeat 1

    moduleUnit
        "mandel16"
        (pins3 ("cx0", view) ("cy", view) ("dx", view))
        (pins1 ("pixels", pixelsFormat))
        []
        (fun instance _ s ->
            let runs =
                s
                |> streamMapTo (layout1 ("data", run.width)) (fun (cx0, cy, dx) -> run.dematerialize (lit 0UL (lanePodAddrWidth pixelsPerBeat 1), asUInt cy, asUInt cx0, asUInt dx))

            mandelLanePod pixelsPerBeat 1 maxIter fracBits threads instance runs
            |> streamMapTo (layout1 ("pixels", beatBits)) snd)

/// The raster as chunk views: beat `k` of the frame becomes the view of its
/// chunk — `cx0` steps by sixteen `dx` a beat and returns to `cxOrigin` at
/// each row's start, `cy` steps by `dy` a row — both from the origin at beat
/// zero. Adders and a counter; the frame's width says how many chunks a row
/// is, and the host says how many beats a frame is by how many it asks for.
let coords (width: int) (fracBits: int) : Fu<Expr, Expr * Expr * Expr> =
    let chunks = paddedWidth width / pixelsPerBeat
    let chunkWidth = max 1 (ceilLog2 chunks)
    let view = viewFormat fracBits

    { name = "coords"
      operands = pins1 ("beat", unsignedInt 32)
      results = pins3 ("cx0", view) ("cy", view) ("dx", view)
      controls = [ "cxOrigin", view; "cyOrigin", view; "dx", view; "dy", view ]
      copies = 1
      law =
        Sequential(fun instance controls s ->
            let cxOrigin, cyOrigin, dx, dy =
                match controls |> List.map asUInt with
                | [ a; b; c; d ] -> a, b, c, d
                | _ -> failwith "coords: four controls"

            let ready = wireBit $"{instance}_ready"
            registerStreamReady ready
            ready ==> s.ready
            let fire = s.valid &&& ready

            let chunk = reg $"{instance}_chunk" chunkWidth
            let cxNext = reg $"{instance}_cx_next" 32
            let cyNext = reg $"{instance}_cy_next" 32

            // The first beat restarts the raster from the origin; the rest
            // continue from where the last left off.
            let first = eq s.payload (lit 0UL 32)
            let thisChunk = mux first (lit 0UL chunkWidth) chunk
            let cx0 = mux first cxOrigin cxNext
            let cy = mux first cyOrigin cyNext
            let lastOfRow = eq thisChunk (lit (uint64 (chunks - 1)) chunkWidth)
            // Sixteen `dx`: the step across one chunk, at the view's width.
            let dx16 = cat (slice 27 0 dx) (lit 0UL 4)

            If fire (fun () ->
                ifElse
                    [ (lastOfRow,
                       fun () ->
                           lit 0UL chunkWidth ==> chunk
                           cxOrigin ==> cxNext
                           cy + dy ==> cyNext)
                      (otherwise,
                       fun () ->
                           thisChunk + lit 1UL chunkWidth ==> chunk
                           cx0 + dx16 ==> cxNext
                           cy ==> cyNext) ])

            { payload = (cx0, cy, dx)
              valid = s.valid
              ready = ready
              layout = layout3 ("cx0", 32) ("cy", 32) ("dx", 32) }) }
