/// Row coalescer — `MandelRowCoalescer`, ported: the hardware
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
