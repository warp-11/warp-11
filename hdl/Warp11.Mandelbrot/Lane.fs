/// One barrel (thread-interleaved) Mandelbrot lane — Kotlin's
/// `MandelBarrelLane`, ported. A single pipelined `mandelStep` cone kept full
/// by round-robining `nThreads` independent pixel-threads through it, so the
/// deep multiply pipeline never bubbles on the `z ← z² + c` recurrence —
/// latency traded for clock, the WarpCPU barrel move.
///
/// Each cycle thread `turn` (rotating) issues; the result lands
/// `mandelStepLatency` cycles later, with the issue context (thread id,
/// iteration, valid) riding a matching delay chain so writeback realigns.
/// With `nThreads > latency` a thread's next issue always follows its own
/// writeback, so slot state is race-free without a handshake.
///
/// Per-thread state (`cx cy addr zx zy iter`) lives in async-read mems —
/// LUTRAM-shaped by construction (a combinational read cannot infer BRAM), the
/// register-file move that freed Kotlin's LUT/FF budget. `active`/`pend` stay
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
module Warp11.Mandelbrot.Lane

open Warp11
open Warp11.Mandelbrot.Step

/// Bits of a lane's `iter` result for a given maxIter — shared with anything
/// declaring a stream that carries it.
let laneIterWidth maxIter =
    bitsToHold (max (maxIter - 1) 1 + 1) |> max 1

let mandelBarrelLane (maxIter: int) (fracBits: int) (nThreads: int) (addrWidth: int) =
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

    defineModule
        $"MandelBarrelLane_max%d{maxIter}_n%d{nThreads}_a%d{addrWidth}"
        (fun p ->
            (p.inPort "px_cx" 32,
             p.inPort "px_cy" 32,
             p.inPort "px_addr" addrWidth,
             p.inPort "px_valid" 1,
             p.outPort "px_ready" 1,
             p.outPort "res_addr" addrWidth,
             p.outPort "res_iter" iterWidth,
             p.outPort "res_valid" 1,
             p.inPort "res_ready" 1,
             p.outPort "all_idle" 1))
        (fun m (pxCx, pxCy, pxAddr, pxValid, pxReady, resAddr, resIter, resValid, resReady, allIdle) (px: Stream<Expr * Expr * Expr>) ->
            let cx, cy, addr = px.payload
            cx ==> pxCx
            cy ==> pxCy
            addr ==> pxAddr
            px.valid ==> pxValid
            pxReady ==> px.ready
            m.RegisterStreamReady resReady

            { payload = (resAddr, resIter)
              valid = resValid
              ready = resReady
              layout = layout2 ("addr", addrWidth) ("iter", iterWidth) },
            allIdle)
        (fun (pxCx, pxCy, pxAddr, pxValid, pxReady, resAddr, resIter, resValid, resReady, allIdle) _ ->
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
            (needPull &&& pxValid) ==> pull // consuming a pixel this cycle
            needPull ==> pxReady

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

            let writebackZxN, writebackZyN, writebackEsc = instanceNamed "step" step issueZx issueZy issueCx issueCy

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
                let emitAccept = emitSel[t] &&& resReady

                If writebackDoneT (fun () -> lit 0UL 1 ==> active[t])
                Else (fun () -> If issueLoad (fun () -> lit 1UL 1 ==> active[t]))
                If writebackDoneT (fun () -> lit 1UL 1 ==> pend[t])
                Else (fun () -> If emitAccept (fun () -> lit 0UL 1 ==> pend[t]))

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
            pendAny ==> resValid
            memRead addrMem emitIndex ==> resAddr
            memRead iterMem emitIndex ==> resIter

            let anyBusy = wireBit "anyBusy"
            List.reduce (|||) (List.map2 (|||) active pend) ==> anyBusy
            bnot anyBusy ==> allIdle)

/// The whole-pixel software twin: iterate with `stepTwin` from z=0 until the
/// step escapes or the issue iteration reaches maxIter-1, reporting the issue
/// iteration — exactly the lane's writeback rule.
let laneTwin (fracBits: int) (maxIter: int) (cx: uint64) (cy: uint64) =
    let rec go zx zy n =
        let zxn, zyn, esc = stepTwin fracBits zx zy cx cy

        if esc = 1UL || n = maxIter - 1 then uint64 n else go zxn zyn (n + 1)

    go 0UL 0UL 0

/// The lane at ports for the oracle: maxIter 8 so escapes AND max-outs both
/// happen inside the testbench's 50 cycles, 8 threads, random px beats and
/// random res_ready — pull, refill-at-issue, the delay chains, DONE-PENDING
/// holds and the emit arbitration all under the differential.
let mandelLaneHarness =
    design "MandelLaneHarness" (fun () ->
        let px = streamInput "px" (layout3 ("cx", 32) ("cy", 32) ("addr", 8))
        let res, allIdle = instanceNamed "lane" (mandelBarrelLane 8 28 8 8) px
        streamOutput "res" res
        let idle = outputBit "all_idle"
        allIdle ==> idle)
