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
///
/// **A map too wide for seven bits spends one more byte**, right after the
/// command and carrying the index's high bits — so a design with a table in it
/// is reachable, and every design that fits in seven bits emits exactly what it
/// emitted before, down to the state encoding:
///
///     host → A5 | 80+wlo | whi | d0 d1 d2 d3 | xor   write word w, wide map
///     host → A5 | wlo | whi | xor                    read word w, wide map
///
/// **And one frame the fabric sends unasked**, so a design can stream data
/// out of the same wire its registers are on — a recorder, a trace, a log:
///
///     host ← A5 | 02 | d0 d1 d2 d3 | xor           a streamed word
///
/// The status byte is the frame's type, so a host tells them apart by the
/// byte it already had to read. Replies win every race: a stream frame only
/// starts when nothing is replying and no read is in flight, so putting a
/// recorder on the link cannot make `wdrc show` slower to answer — only
/// to answer *later*, by however long one frame takes.
[<AutoOpen>]
module Warp11.SerialRegMap

/// The byte every frame begins with, chosen for its edges: a receiver that
/// missed a byte finds the next frame by it.
let serialSync = 0xA5UL

/// The status byte of a frame the fabric sent unasked, carrying one streamed
/// word. 0 and 1 are a reply's accepted and refused, so this cannot be
/// mistaken for either.
let serialStreamStatus = 0x02UL

/// How far the receiver is through a request.
type SerialRequestState =
    | AwaitSync
    | AwaitCommand
    /// Only on a map too wide for the command byte's own seven bits.
    | AwaitAddress
    | AwaitData
    | AwaitChecksum

/// Address bits a command byte carries on its own.
let serialCommandAddrBits = 7

/// Bytes of a request that carry its word index: the command byte alone, or the
/// command byte and one more. A caller sending frames needs this, and it is a
/// function of the map rather than a choice.
let serialAddressBytes (wordWidth: int) =
    if wordWidth <= serialCommandAddrBits then 1 else 2

/// The widest map a request can address.
let serialMaxAddrBits = serialCommandAddrBits + 8

/// Parse requests from a byte flow into the write and read fires a
/// `RegMap` slave wants, and reply on a byte stream. Returns the channel
/// for `regMapSlaveOn` and the reply stream for the transmitter.
let serialRegMapChannelWith
    (name: string)
    (wordWidth: int)
    (stream: Stream<Expr> option)
    (bytes: Flow<Expr>)
    : AxiLiteChannel * Stream<Expr> =
    if wordWidth > serialMaxAddrBits then
        failwith
            $"serial register map: a request addresses %d{1 <<< serialMaxAddrBits} words; the map wants %d{1 <<< wordWidth}"

    let wide = serialAddressBytes wordWidth > 1
    let byte = bytes.payload
    let arrived = bytes.valid
    // The wide map's extra state goes after `AwaitCommand`, so the narrow
    // encoding — and therefore the emitted Verilog of every design that had one
    // — is untouched.
    let st =
        machine
            $"{name}_request"
            ([ AwaitSync; AwaitCommand ] @ (if wide then [ AwaitAddress ] else []) @ [ AwaitData; AwaitChecksum ])

    let command = reg $"{name}_command" 8
    let addrHigh = if wide then Some(reg $"{name}_addr_high" 8) else None
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
        [ yield
              (AwaitSync,
               fun () ->
                   If (arrived &&& eq byte (lit serialSync 8)) (fun () ->
                       lit 0UL 8 ==> running
                       st.Goto AwaitCommand))
          yield
              (AwaitCommand,
               fun () ->
                   If arrived (fun () ->
                       byte ==> command
                       byte ==> running

                       match addrHigh with
                       | Some _ -> st.Goto AwaitAddress
                       | None ->
                           ifElse
                               [ (slice 7 7 byte, fun () -> st.Goto AwaitData)
                                 (otherwise, fun () -> st.Goto AwaitChecksum) ]))
          yield!
              (match addrHigh with
               | None -> []
               | Some high ->
                   [ (AwaitAddress,
                      fun () ->
                          If arrived (fun () ->
                              byte ==> high
                              (running ^^^ byte) ==> running

                              ifElse
                                  [ (isWrite, fun () -> st.Goto AwaitData)
                                    (otherwise, fun () -> st.Goto AwaitChecksum) ])) ])
          yield
              (AwaitData,
               fun () ->
                   If arrived (fun () ->
                       // Least significant byte first.
                       ifElse
                           [ for i, d in List.indexed dataBytes -> (eq index.count (lit (uint64 i) 2), fun () -> byte ==> d) ]

                       (running ^^^ byte) ==> running
                       If index.wrap (fun () -> st.Goto AwaitChecksum)))
          yield
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

    // The index a request named: the command byte's low bits, with the extra
    // byte above them on a wide map.
    let requestAddr =
        match addrHigh with
        | None -> command
        | Some high ->
            let combined = wire $"{name}_addr" (8 + serialCommandAddrBits)
            cat high (slice (serialCommandAddrBits - 1) 0 command) ==> combined
            combined

    let beginRead () =
        let word = wire $"{name}_read_word" wordWidth
        slice (wordWidth - 1) 0 requestAddr ==> word

        { word = word
          inFlight = readFire ||| answered }

    // The reply: sync, status, the data for a read, checksum — a stream the
    // transmitter takes a byte at a time.
    let replying = regBit $"{name}_replying"
    let replyIsRead = regBit $"{name}_reply_is_read"
    // A streamed word rides the same frame machinery as a read's reply: both
    // carry four data bytes, and only the status byte differs.
    let replyIsStream = regBit $"{name}_reply_is_stream"
    let replyBad = regBit $"{name}_reply_bad"
    let carriesData = wire $"{name}_reply_carries_data" 1
    (replyIsRead ||| replyIsStream) ==> carriesData
    let replyLength = wire $"{name}_reply_length" 3
    mux carriesData (lit 6UL 3) (lit 2UL 3) ==> replyLength
    let replyReady = wireBit $"{name}_reply_ready"
    registerStreamReady replyReady
    let replyIndex = counterTo $"{name}_reply_index" replyLength (replying &&& replyReady)

    // A reply cannot barge into a frame already going out, or it would
    // clobber a streamed word half-sent and neither would arrive. So it is
    // raised as pending and starts when the link next falls idle. Without a
    // stream on the link nothing is ever in flight when a reply is raised,
    // and this is the same cycle it always was.
    let pendingReply = regBit $"{name}_reply_pending"
    let pendingIsRead = regBit $"{name}_reply_pending_is_read"
    let pendingBad = regBit $"{name}_reply_pending_bad"

    If (writeFire ||| refused) (fun () ->
        lit 1UL 1 ==> pendingReply
        lit 0UL 1 ==> pendingIsRead
        refused ==> pendingBad)

    If answerDue (fun () ->
        lit 1UL 1 ==> pendingReply
        lit 1UL 1 ==> pendingIsRead
        lit 0UL 1 ==> pendingBad
        lit 1UL 1 ==> answered)

    If (pendingReply &&& bnot replying) (fun () ->
        lit 1UL 1 ==> replying
        pendingIsRead ==> replyIsRead
        lit 0UL 1 ==> replyIsStream
        pendingBad ==> replyBad
        lit 0UL 1 ==> pendingReply)

    // A streamed word goes out only in the gaps: nothing replying, no reply
    // about to start, and no read waiting for its answer. The host's traffic
    // is never held up by the design's.
    let streamTaken = wireBit $"{name}_stream_taken"

    match stream with
    | None -> lit 0UL 1 ==> streamTaken
    | Some src ->
        // Nothing going out, nothing waiting to go out, and no request in
        // the middle of arriving or waiting for its answer.
        let quiet =
            bnot replying
            &&& bnot pendingReply
            &&& bnot (writeFire ||| refused ||| answerDue)
            &&& bnot (readFire ||| answered)
            &&& st.Is AwaitSync

        (src.valid &&& quiet) ==> streamTaken
        streamTaken ==> src.ready

        If streamTaken (fun () ->
            lit 1UL 1 ==> replying
            lit 0UL 1 ==> replyIsRead
            lit 1UL 1 ==> replyIsStream
            lit 0UL 1 ==> replyBad
            src.payload ==> replyData)

    let replyStatus = wire $"{name}_reply_status" 8
    mux replyIsStream (lit serialStreamStatus 8) (pad 8 replyBad) ==> replyStatus

    let replyBytes =
        [ lit serialSync 8
          replyStatus
          slice 7 0 replyData
          slice 15 8 replyData
          slice 23 16 replyData
          slice 31 24 replyData ]

    // The xor of the bytes after the sync, over whichever of them this reply
    // carries: status alone for a write, status and data for a read.
    let dataXor = wire $"{name}_reply_data_xor" 8
    (slice 7 0 replyData ^^^ slice 15 8 replyData ^^^ slice 23 16 replyData ^^^ slice 31 24 replyData) ==> dataXor
    let replyXor = wire $"{name}_reply_xor" 8
    mux carriesData (replyStatus ^^^ dataXor) replyStatus ==> replyXor

    let replyByte = wire $"{name}_reply_byte" 8
    mux (eq replyIndex.count replyLength) replyXor (selectIndexed replyIndex.count replyBytes) ==> replyByte

    If replyIndex.wrap (fun () ->
        lit 0UL 1 ==> replying
        lit 0UL 1 ==> replyIsStream
        lit 0UL 1 ==> answered)

    let reply: Stream<Expr> =
        { payload = replyByte
          valid = replying
          ready = replyReady
          layout = byteLayout }

    let awWord = wire $"{name}_write_word" wordWidth
    slice (wordWidth - 1) 0 requestAddr ==> awWord

    { wordWidth = wordWidth
      wdata = data
      writeFire = writeFire
      awWord = awWord
      beginRead = beginRead
      answersAfter = answersAfter
      rdata = readData },
    reply

/// The channel with no stream on it — what every design had before one could
/// be asked for, and still the common case.
let serialRegMapChannel (name: string) (wordWidth: int) (bytes: Flow<Expr>) : AxiLiteChannel * Stream<Expr> =
    serialRegMapChannelWith name wordWidth None bytes

/// The slave for a map, behind a UART on `pins` at `baud`. Same `SlaveRegs`
/// as `regMapSlave` gives over AXI-Lite; the design cannot tell.
let serialRegMapSlave (name: string) (fabricHz: int) (baud: int) (pins: UartPins) (m: RegMap) : SlaveRegs =
    let received = uartReceive name fabricHz baud pins.rx
    let channel, reply = serialRegMapChannel name (m.apertureAddrWidth - 2) received
    let regs = regMapSlaveOn channel m
    uartTransmit name fabricHz baud reply ==> pins.tx
    regs

/// The same slave with a **stream of words going out on the same wire**, as
/// frames the host tells apart by their status byte. What a design reaches
/// for when it has bulk data and only one link — a recorder on a band whose
/// register map is already on the FTDI's second channel.
///
/// The stream is back-pressured by the link: its `ready` rises only when a
/// frame can start, so a design that produces faster than the baud carries
/// simply waits, and a design that must not wait puts a `streamFork` with a
/// `Dropping` branch in front of this.
let serialRegMapSlaveWith
    (name: string)
    (fabricHz: int)
    (baud: int)
    (pins: UartPins)
    (m: RegMap)
    (stream: Stream<Expr>)
    : SlaveRegs =
    let received = uartReceive name fabricHz baud pins.rx
    let channel, reply = serialRegMapChannelWith name (m.apertureAddrWidth - 2) (Some stream) received
    let regs = regMapSlaveOn channel m
    uartTransmit name fabricHz baud reply ==> pins.tx
    regs
