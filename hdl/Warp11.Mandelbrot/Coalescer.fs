/// Row coalescer — Kotlin's `MandelRowCoalescer`, ported: the hardware
/// `buffer(16)` that turns per-pixel egress into aligned 16-pixel 128-bit
/// beats, the 16× lever off the HP-port single-beat write ceiling.
///
/// Barrel lanes finish pixels out of address order, but a DDR write must be
/// aligned and full. Pixels stage into a padded row buffer at their column;
/// when a full row of `widthPadded` pixels has arrived it drains as
/// `widthPadded`/16 aligned beats. DOUBLE-BUFFERED (ping-pong, both rows in
/// ONE mem with the buffer select as the high address bit — two muxed-read
/// mems would spill out of BRAM): while one buffer drains, the next row fills
/// the other, so `in_ready` stays high across the row boundary.
/// `row_gathered` pulses when a fill completes — the coord-gen advances on
/// THAT, not on drain-complete. Each buffer latches its own `row_base` on its
/// first pixel, so the drain of row R and the fill of row R+1 use independent
/// addresses.
///
/// The buffer read is SYNCHRONOUS (BRAM — the silicon-safe pattern), so beat
/// assembly takes 17 cycles: 16 reads plus one read-latency cycle, columns
/// issued descending so the last byte lands in bits [7:0] (DDR byte order),
/// the shift gated past the first cycle whose read is still in flight.
module Warp11.Mandelbrot.Coalescer

open Warp11

/// The consumer's states: waiting for a full buffer, assembling a beat out of
/// 16 synchronous reads, holding that beat until the sink takes it.
type private Drain =
    | Idle
    | Assemble
    | Emit

let mandelRowCoalescer (widthPadded: int) (addrWidth: int) =
    if widthPadded % 16 <> 0 || widthPadded < 16 then
        failwith $"widthPadded must be a positive multiple of 16, got %d{widthPadded}"

    let nBeats = widthPadded / 16
    let colWidth = bitsToHold widthPadded
    let beatIndexWidth = bitsToHold nBeats
    let fillCountWidth = bitsToHold (widthPadded + 1)
    let bufAddrWidth = bitsToHold (2 * widthPadded)

    defineModule
        $"MandelRowCoalescer_%d{widthPadded}_a%d{addrWidth}"
        (fun p ->
            (p.inPort "in_col" colWidth,
             p.inPort "in_value" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_addr" addrWidth,
             p.outPort "out_beat" 128,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1,
             p.inPort "row_base" addrWidth,
             p.outPort "row_gathered" 1,
             p.outPort "row_done" 1))
        (fun m (inCol, inValue, inValid, inReady, outAddr, outBeat, outValid, outReady, rowBasePort, rowGathered, rowDone) (rowBase: Expr) (inp: Stream<Expr * Expr>) ->
            let col, value = inp.payload
            col ==> inCol
            value ==> inValue
            inp.valid ==> inValid
            inReady ==> inp.ready
            rowBase ==> rowBasePort
            m.RegisterStreamReady outReady

            { payload = (outAddr, outBeat)
              valid = outValid
              ready = outReady
              layout = layout2 ("addr", addrWidth) ("beat", 128) },
            rowGathered,
            rowDone)
        (fun (inCol, inValue, inValid, inReady, outAddr, outBeat, outValid, outReady, rowBasePort, rowGathered, rowDone) _ ->
            // Ping-pong row buffers packed into one mem (buffer = high address
            // bit), single write site + single sync read — the BRAM shape.
            let buf = blockMem "rowbuf" bufAddrWidth 8
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
            let asmCount = reg "asm_cnt" 5 // 0..16: 16 reads + 1 BRAM latency cycle
            let beatReg = reg "beat_reg" 128

            // ---- producer: accept a pixel into the current fill buffer ----
            let fillFull = wireBit "fill_full"
            mux fillSel full1 full0 ==> fillFull
            bnot fillFull ==> inReady // stall only while the fill buffer is undrained
            let accept = wireBit "accept"
            (inValid &&& bnot fillFull) ==> accept
            let fillAddr = wire "fill_addr" bufAddrWidth
            bufHalf fillSel + padCol inCol ==> fillAddr
            memWrite buf fillAddr inValue accept // one write site (BRAM-safe)

            let fillLast = wireBit "fill_last" // this pixel completes the fill buffer
            (accept &&& eq fillCount (lit (uint64 (widthPadded - 1)) fillCountWidth)) ==> fillLast
            fillLast ==> rowGathered
            let firstPix = accept &&& eq fillCount (lit 0UL fillCountWidth) // latch the row's base
            If (firstPix &&& bnot fillSel) (fun () -> rowBasePort ==> fillBase0)
            If (firstPix &&& fillSel) (fun () -> rowBasePort ==> fillBase1)

            If accept (fun () ->
                If fillLast (fun () -> lit 0UL fillCountWidth ==> fillCount)
                Else (fun () -> fillCount + lit 1UL fillCountWidth ==> fillCount))

            If fillLast (fun () -> bnot fillSel ==> fillSel) // hand off to the other buffer

            // ---- consumer: drain a full buffer as aligned beats ----
            let cIdle = drain.Is Idle
            let cAsm = drain.Is Assemble
            let cEmit = drain.Is Emit
            let drainFull = wireBit "drain_full"
            mux drainSel full1 full0 ==> drainFull
            let startDrain = cIdle &&& drainFull
            let asmDone = cAsm &&& eq asmCount (lit 16UL 5)
            let emitAccept = cEmit &&& outReady
            let lastBeat = eq beatIndex (lit (uint64 (nBeats - 1)) beatIndexWidth)
            let drainDone = wireBit "drain_done"
            (emitAccept &&& lastBeat) ==> drainDone
            drainDone ==> rowDone

            // full flags: set by the producer, cleared by the consumer — per
            // buffer mutually exclusive, so each is single-writer.
            If (fillLast &&& bnot fillSel) (fun () -> lit 1UL 1 ==> full0)
            Else (fun () -> If (drainDone &&& bnot drainSel) (fun () -> lit 0UL 1 ==> full0))
            If (fillLast &&& fillSel) (fun () -> lit 1UL 1 ==> full1)
            Else (fun () -> If (drainDone &&& drainSel) (fun () -> lit 0UL 1 ==> full1))
            If drainDone (fun () -> bnot drainSel ==> drainSel)

            // byte offset of the current beat within the row (beatIndex*16),
            // sized to colWidth — robust to nBeats=1, where beatIndexWidth+4 > colWidth.
            let beatCat = wire "beat_cat" (beatIndexWidth + 4)
            cat beatIndex (lit 0UL 4) ==> beatCat
            let beatBase = wire "beat_base" colWidth

            (if beatIndexWidth + 4 >= colWidth then
                     slice (colWidth - 1) 0 beatCat
                 else
                     cat (lit 0UL (colWidth - beatIndexWidth - 4)) beatCat)
            ==> beatBase

            // ASM issues column beatIndex*16 + (15 - asmCount) each cycle,
            // descending, so the last byte lands in bits [7:0] — DDR order.
            let asmLow = wire "asm_low" 4
            slice 3 0 asmCount ==> asmLow
            let revIndex = wire "rev_idx" 4
            lit 15UL 4 - asmLow ==> revIndex
            let asmCol = wire "asm_col" colWidth
            beatBase + (if colWidth > 4 then cat (lit 0UL (colWidth - 4)) revIndex else revIndex) ==> asmCol
            let drainAddr = wire "drain_addr" bufAddrWidth
            bufHalf drainSel + padCol asmCol ==> drainAddr
            let bufRd = (memReadPort buf drainAddr).data // synchronous → BRAM + hardware-accurate
            let drainBase = wire "drain_base" addrWidth
            mux drainSel fillBase1 fillBase0 ==> drainBase

            // ---- outputs ----
            cEmit ==> outValid

            drainBase
                + (if addrWidth > colWidth then cat (lit 0UL (addrWidth - colWidth)) beatBase else beatBase)
            ==> outAddr

            beatReg ==> outBeat

            // ---- consumer FSM ----
            If cIdle (fun () -> If startDrain (fun () -> drain.Goto Assemble))

            Else (fun () ->
                If cAsm (fun () -> If asmDone (fun () -> drain.Goto Emit))

                Else (fun () ->
                    If emitAccept (fun () ->
                        If lastBeat (fun () -> drain.Goto Idle)
                        Else (fun () -> drain.Goto Assemble))))

            // beatIndex: reset entering a drain, advance between beats
            If cIdle (fun () -> If startDrain (fun () -> lit 0UL beatIndexWidth ==> beatIndex))
            Else (fun () -> If (emitAccept &&& bnot lastBeat) (fun () -> beatIndex + lit 1UL beatIndexWidth ==> beatIndex))

            // asmCount: count 0..16 during ASM, 0 otherwise
            If cAsm (fun () ->
                If asmDone (fun () -> lit 0UL 5 ==> asmCount)
                Else (fun () -> asmCount + lit 1UL 5 ==> asmCount))

            Else (fun () -> lit 0UL 5 ==> asmCount)

            // beatReg: shift in the byte that arrived this cycle (the read
            // issued last cycle); asmCount=0's read is still in flight.
            If (cAsm &&& bnot (eq asmCount (lit 0UL 5))) (fun () -> cat (slice 119 0 beatReg) bufRd ==> beatReg))

/// The coalescer at ports (widthPadded 32 → two beats/row): the living check
/// feeds shuffled columns and asserts byte placement; the oracle's random
/// fill-side stimulus rides the same design.
let mandelCoalescerHarness =
    design "MandelCoalescerHarness" (fun () ->
        let rowBase = input "row_base" 8
        let inp = streamInput "px" (layout2 ("col", 5) ("value", 8))

        let out, rowGathered, rowDone =
            instanceNamed "coal" (mandelRowCoalescer 32 8) rowBase inp

        streamOutput "beat" out
        let g = outputBit "row_gathered"
        rowGathered ==> g
        let d = outputBit "row_done"
        rowDone ==> d)

/// Self-feeding coalescer (widthPadded 16): an internal raster feeder offers a
/// pixel every cycle, so complete fill → 17-cycle assembly → emit → ping-pong
/// cycles all happen inside the oracle's 50 cycles, with the testbench's
/// random `beat_ready` throttling the drain — the sync-read assembly timing
/// differentially verified, not just asserted.
let mandelCoalescerLoop =
    design "MandelCoalescerLoop" (fun () ->
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

        let out, rowGathered, _ =
            instanceNamed "coal" (mandelRowCoalescer 16 8) rowBase feed

        If feedReady (fun () -> feedCol + lit 1UL 4 ==> feedCol)
        If rowGathered (fun () -> feedRow + lit 1UL 3 ==> feedRow)
        streamOutput "beat" out)
