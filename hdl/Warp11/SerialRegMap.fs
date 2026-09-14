/// A register map over a serial link: the same `RegMap` a design puts behind
/// AXI-Lite on a KV260, behind a UART on a board that has no bus — an
/// iCEBreaker's FTDI channel, a microcontroller's UART — with the map, the
/// slave and the generated seam unchanged.
///
/// **The protocol.** A request is a sync byte, a command byte — bit 7 says
/// write, bits 6:0 the word index — four data bytes if it is a write, least
/// significant first, and a checksum: the xor of every byte after the sync.
/// The reply is a sync byte, a status byte (0 accepted; 1 the checksum did
/// not match, and nothing was done), the four data bytes if it was a read,
/// and the xor of the bytes after the sync. Nothing here is UART-specific:
/// the framing takes a byte flow in and gives a byte stream out, and SPI
/// would carry the same bytes.
///
///     host → A5 | 80+w | d0 d1 d2 d3 | xor        write word w
///     host ← A5 | 00 | xor
///     host → A5 | w | xor                          read word w
///     host ← A5 | 00 | d0 d1 d2 d3 | xor
[<AutoOpen>]
module Warp11.SerialRegMap

/// The byte every frame begins with, chosen for its edges: a receiver that
/// missed a byte finds the next frame by it.
let serialSync = 0xA5UL

/// How far the receiver is through a request.
type SerialRequestState =
    | AwaitSync
    | AwaitCommand
    | AwaitData
    | AwaitChecksum

/// Parse requests from a byte flow into the write and read fires a
/// `RegMap` slave wants, and reply on a byte stream. Returns the channel
/// for `regMapSlaveOn` and the reply stream for the transmitter.
let serialRegMapChannel (name: string) (wordWidth: int) (bytes: Flow<Expr>) : AxiLiteChannel * Stream<Expr> =
    if wordWidth > 7 then
        failwith $"serial register map: a command byte addresses 128 words; the map wants %d{1 <<< wordWidth}"

    let byte = bytes.payload
    let arrived = bytes.valid
    let st = machine $"{name}_request" [ AwaitSync; AwaitCommand; AwaitData; AwaitChecksum ]

    let command = reg $"{name}_command" 8
    let dataBytes = [ for i in 0..3 -> reg $"{name}_data%d{i}" 8 ]
    let data = wire $"{name}_data" 32
    cat dataBytes[3] (cat dataBytes[2] (cat dataBytes[1] dataBytes[0])) ==> data
    let index = counter $"{name}_data_index" 4 (arrived &&& st.Is AwaitData)
    let running = reg $"{name}_xor" 8
    let isWrite = wire $"{name}_is_write" 1
    slice 7 7 command ==> isWrite

    // The fires a slave consumes, one cycle each, on an accepted checksum.
    let writeFire = regBit $"{name}_write_fire"
    let readFire = regBit $"{name}_read_fire"
    let refused = regBit $"{name}_refused"
    lit 0UL 1 ==> writeFire
    lit 0UL 1 ==> readFire
    lit 0UL 1 ==> refused

    st.Switch
        [ (AwaitSync,
           fun () ->
               If (arrived &&& eq byte (lit serialSync 8)) (fun () ->
                   lit 0UL 8 ==> running
                   st.Goto AwaitCommand))
          (AwaitCommand,
           fun () ->
               If arrived (fun () ->
                   byte ==> command
                   byte ==> running

                   ifElse
                       [ (slice 7 7 byte, fun () -> st.Goto AwaitData)
                         (otherwise, fun () -> st.Goto AwaitChecksum) ]))
          (AwaitData,
           fun () ->
               If arrived (fun () ->
                   // Least significant byte first.
                   ifElse [ for i, d in List.indexed dataBytes -> (eq index.count (lit (uint64 i) 2), fun () -> byte ==> d) ]

                   (running ^^^ byte) ==> running
                   If index.wrap (fun () -> st.Goto AwaitChecksum)))
          (AwaitChecksum,
           fun () ->
               If arrived (fun () ->
                   ifElse
                       [ (eq byte running,
                          fun () ->
                              isWrite ==> writeFire
                              bnot isWrite ==> readFire)
                         (otherwise, fun () -> lit 1UL 1 ==> refused) ]

                   st.Goto AwaitSync)) ]

    // The read: the slave answers `answersAfter` cycles on, and the word is
    // held for it from the fire until the reply has gone out.
    let answersAfter = 1
    let readData = wire $"{name}_rdata" 32
    let answered = regBit $"{name}_answered"
    let answerDue = delayChain $"{name}_answer_due" 1 answersAfter readFire
    let replyData = reg $"{name}_reply_data" 32
    If answerDue (fun () -> readData ==> replyData)

    let beginRead () =
        let word = wire $"{name}_read_word" wordWidth
        slice (wordWidth - 1) 0 command ==> word

        { word = word
          inFlight = readFire ||| answered }

    // The reply: sync, status, the data for a read, checksum — a stream the
    // transmitter takes a byte at a time.
    let replying = regBit $"{name}_replying"
    let replyIsRead = regBit $"{name}_reply_is_read"
    let replyBad = regBit $"{name}_reply_bad"
    let replyLength = wire $"{name}_reply_length" 3
    mux replyIsRead (lit 6UL 3) (lit 2UL 3) ==> replyLength
    let replyReady = wireBit $"{name}_reply_ready"
    registerStreamReady replyReady
    let replyIndex = counterTo $"{name}_reply_index" replyLength (replying &&& replyReady)

    If (writeFire ||| refused) (fun () ->
        lit 1UL 1 ==> replying
        lit 0UL 1 ==> replyIsRead
        refused ==> replyBad)

    If answerDue (fun () ->
        lit 1UL 1 ==> replying
        lit 1UL 1 ==> replyIsRead
        lit 0UL 1 ==> replyBad
        lit 1UL 1 ==> answered)

    let replyBytes =
        [ lit serialSync 8
          pad 8 replyBad
          slice 7 0 replyData
          slice 15 8 replyData
          slice 23 16 replyData
          slice 31 24 replyData ]

    // The xor of the bytes after the sync, over whichever of them this reply
    // carries: status alone for a write, status and data for a read.
    let dataXor = wire $"{name}_reply_data_xor" 8
    (slice 7 0 replyData ^^^ slice 15 8 replyData ^^^ slice 23 16 replyData ^^^ slice 31 24 replyData) ==> dataXor
    let replyXor = wire $"{name}_reply_xor" 8
    mux replyIsRead (pad 8 replyBad ^^^ dataXor) (pad 8 replyBad) ==> replyXor

    let replyByte = wire $"{name}_reply_byte" 8
    mux (eq replyIndex.count replyLength) replyXor (selectIndexed replyIndex.count replyBytes) ==> replyByte

    If replyIndex.wrap (fun () ->
        lit 0UL 1 ==> replying
        lit 0UL 1 ==> answered)

    let reply: Stream<Expr> =
        { payload = replyByte
          valid = replying
          ready = replyReady
          layout = byteLayout }

    let awWord = wire $"{name}_write_word" wordWidth
    slice (wordWidth - 1) 0 command ==> awWord

    { wordWidth = wordWidth
      wdata = data
      writeFire = writeFire
      awWord = awWord
      beginRead = beginRead
      answersAfter = answersAfter
      rdata = readData },
    reply

/// The slave for a map, behind a UART on `pins` at `baud`. Same `SlaveRegs`
/// as `regMapSlave` gives over AXI-Lite; the design cannot tell.
let serialRegMapSlave (name: string) (fabricHz: int) (baud: int) (pins: UartPins) (m: RegMap) : SlaveRegs =
    let received = uartReceive name fabricHz baud pins.rx
    let channel, reply = serialRegMapChannel name (m.apertureAddrWidth - 2) received
    let regs = regMapSlaveOn channel m
    uartTransmit name fabricHz baud reply ==> pins.tx
    regs
