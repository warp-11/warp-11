/// A UART: 8 data bits, no parity, one stop bit, at a rate derived from the
/// fabric clock. The receiver presents bytes as a `Flow` — a byte arrives when
/// it arrives and cannot be refused — and the transmitter takes them as a
/// `Stream`, ready between bytes.
///
/// One of two links a design on a board with an FTDI can be reached over
/// (`SerialRegMap.fs` is what sits on top of it); the other is SPI, which the
/// register protocol above is written not to care about.
[<AutoOpen>]
module Warp11.Uart

/// The two pins, named for the design's side: `rx` is what the design hears.
type UartPins = { rx: Input; tx: Output }

/// Declare the pins. Call from a module's io factory.
let uartPins (p: Ports) (prefix: string) : UartPins =
    { rx = p.inPort $"{prefix}_rx" 1
      tx = p.outPort $"{prefix}_tx" 1 }

/// Fabric cycles per bit for a baud rate, derived rather than passed — and
/// refused if the nearest whole number of cycles is more than 2% off, which
/// is where a receiver sampling at bit centres starts to miss the tenth bit.
let uartCyclesPerBit (fabricHz: int) (baud: int) : int =
    let cycles = int (round (float fabricHz / float baud))
    let actual = float fabricHz / float cycles
    let error = abs (actual - float baud) / float baud

    if cycles < 3 then
        failwith $"%d{baud} baud from %d{fabricHz} Hz is %d{cycles} cycles a bit — too few to sample at the centre"

    if error > 0.02 then
        failwith $"%d{baud} baud from %d{fabricHz} Hz is %d{cycles} cycles a bit, %.1f{error * 100.0}%% off — outside what 8N1 tolerates"

    cycles

/// A byte's worth of stream: the layout every UART link speaks.
let byteLayout: Layout<Expr> = layout1 ("byte", 8)

/// The receiver's states — the one place in a UART that branches: waiting for
/// a start bit, confirming it, taking the bits, checking the stop bit.
type UartRxState =
    | RxIdle
    | RxStart
    | RxData
    | RxStop

/// Receive 8N1 on `rx`: one byte on the flow per frame whose stop bit was
/// high, sampled at the centre of each bit. A frame whose start bit was gone
/// by its centre is noise and is dropped; one with a bad stop bit is dropped
/// too, which is the only framing check 8N1 has.
let uartReceive (name: string) (fabricHz: int) (baud: int) (rx: Expr) : Flow<Expr> =
    let cyclesPerBit = uartCyclesPerBit fabricHz baud
    let phaseWidth = ceilLog2 cyclesPerBit

    // Two flops in, and the line idle-high through reset so a reset is not a
    // start bit.
    let sync1 = regInit $"{name}_rx_sync1" 1 1UL
    rx ==> sync1
    let line = regInit $"{name}_rx_line" 1 1UL
    sync1 ==> line

    let st = machine $"{name}_rx_state" [ RxIdle; RxStart; RxData; RxStop ]

    // Where in the bit we are, from the edge that started the frame.
    let phase = reg $"{name}_rx_phase" phaseWidth
    let atBitEnd = eq phase (lit (uint64 (cyclesPerBit - 1)) phaseWidth)
    let atCentre = eq phase (lit (uint64 (cyclesPerBit / 2)) phaseWidth)

    ifElse
        [ (st.Is RxIdle, fun () -> lit 0UL phaseWidth ==> phase)
          (atBitEnd, fun () -> lit 0UL phaseWidth ==> phase)
          (otherwise, fun () -> phase + lit 1UL phaseWidth ==> phase) ]

    let bit = counter $"{name}_rx_bit" 8 (st.Is RxData &&& atCentre)
    let shift = reg $"{name}_rx_shift" 8
    let byte = reg $"{name}_rx_byte" 8
    let valid = regBit $"{name}_rx_valid"
    lit 0UL 1 ==> valid

    st.Switch
        [ (RxIdle, fun () -> If (bnot line) (fun () -> st.Goto RxStart))
          (RxStart,
           fun () ->
               If atCentre (fun () ->
                   ifElse
                       [ (bnot line, fun () -> st.Goto RxData)
                         (otherwise, fun () -> st.Goto RxIdle) ]))
          (RxData,
           fun () ->
               If atCentre (fun () ->
                   // Least significant bit first.
                   cat line (slice 7 1 shift) ==> shift
                   If bit.wrap (fun () -> st.Goto RxStop)))
          (RxStop,
           fun () ->
               If atCentre (fun () ->
                   If line (fun () ->
                       shift ==> byte
                       lit 1UL 1 ==> valid)

                   st.Goto RxIdle)) ]

    ({ payload = byte
       valid = valid
       layout = byteLayout }
     : Flow<Expr>)

/// Transmit 8N1: takes a byte when idle, shifts the start bit, the eight
/// data bits least significant first and the stop bit out at the baud rate,
/// and is ready again after the stop bit. Returns the line to drive `tx`.
let uartTransmit (name: string) (fabricHz: int) (baud: int) (bytes: Stream<Expr>) : Expr =
    let cyclesPerBit = uartCyclesPerBit fabricHz baud
    let frameBits = 10

    let busy = regBit $"{name}_tx_busy"
    bnot busy ==> bytes.ready
    let accept = bytes.valid &&& bytes.ready

    let phase = counter $"{name}_tx_phase" cyclesPerBit busy
    let bit = counter $"{name}_tx_bit" frameBits phase.wrap

    // The frame, least significant end first: start, data, stop.
    let shift = regInit $"{name}_tx_shift" frameBits 1UL

    ifElse
        [ (accept,
           fun () ->
               cat (lit 1UL 1) (cat bytes.payload (lit 0UL 1)) ==> shift
               lit 1UL 1 ==> busy)
          (phase.wrap,
           fun () ->
               cat (lit 1UL 1) (slice (frameBits - 1) 1 shift) ==> shift
               If bit.wrap (fun () -> lit 0UL 1 ==> busy)) ]

    let tx = wireBit $"{name}_tx_line"
    mux busy (slice 0 0 shift) (lit 1UL 1) ==> tx
    tx
