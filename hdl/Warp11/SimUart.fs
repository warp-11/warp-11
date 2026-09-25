/// A software UART on a design's pins, and a register-map client on top of
/// it: the host end of `SerialRegMap.fs`, in the Sim, so the same frames the
/// Rust tool sends over an FTDI are sent here and checked before a bitstream
/// exists.
///
/// The framing lives in one place — `SerialFrame` — and both this client and
/// the living checks read it, so a protocol change is one edit and the checks
/// say whether the fabric followed it.
[<AutoOpen>]
module Warp11.SimUart

/// The request and reply bytes of the serial register protocol, as host code
/// forms and reads them.
module SerialFrame =
    let sync = byte serialSync

    let private xorOf (bytes: byte list) = bytes |> List.fold (^^^) 0uy

    /// The address bytes of a request, for a map needing `addressBytes` of them:
    /// the command byte, with the index's high bits after it on a wide map.
    ///
    /// `addressBytes` is the map's, never the index's — a wide map's receiver
    /// waits for the second byte whatever word is being asked for.
    let private addressOf (addressBytes: int) (top: int) (word: int) =
        match addressBytes with
        | 1 -> [ byte (top ||| (word &&& 0x7F)) ]
        | 2 -> [ byte (top ||| (word &&& 0x7F)); byte ((word >>> 7) &&& 0xFF) ]
        | n -> failwith $"serial frame: %d{n} address bytes is not a shape this protocol has"

    /// The bytes that write `value` to word `word`, on a map of `addressBytes`.
    let writeWide (addressBytes: int) (word: int) (value: uint64) : byte list =
        let body =
            addressOf addressBytes 0x80 word
            @ [ byte (value &&& 0xFFUL)
                byte ((value >>> 8) &&& 0xFFUL)
                byte ((value >>> 16) &&& 0xFFUL)
                byte ((value >>> 24) &&& 0xFFUL) ]

        sync :: body @ [ xorOf body ]

    /// The bytes that read word `word`, on a map of `addressBytes`.
    let readWide (addressBytes: int) (word: int) : byte list =
        let body = addressOf addressBytes 0 word
        sync :: body @ [ xorOf body ]

    /// The bytes that write `value` to word `word`, on a map narrow enough for
    /// the command byte to address it on its own.
    let write (word: int) (value: uint64) : byte list = writeWide 1 word value

    /// The bytes that read word `word`, on a map narrow enough for the command
    /// byte to address it on its own.
    let read (word: int) : byte list = readWide 1 word

    /// How long the reply to a request is, sync and checksum included.
    let replyLength (isRead: bool) = if isRead then 7 else 3

    /// A reply's status and, for a read, its value — or the reason it is not
    /// a reply.
    let parse (isRead: bool) (reply: byte list) : Result<uint64 option, string> =
        match reply with
        | s :: rest when s = sync ->
            let body = List.take (List.length rest - 1) rest
            let check = List.last rest

            if xorOf body <> check then
                Error "reply checksum"
            else
                match body with
                | 0uy :: data when isRead && List.length data = 4 ->
                    Ok(Some(data |> List.mapi (fun i b -> uint64 b <<< (8 * i)) |> List.sum))
                | [ 0uy ] when not isRead -> Ok None
                | 1uy :: _ -> Error "refused: request checksum"
                | _ -> Error $"malformed reply %A{reply}"
        | _ -> Error $"no sync byte in %A{reply}"

/// A UART's two pins as the Sim names them.
type UartSimPins = { toDesign: string; fromDesign: string }

/// Pins declared by `uartPins p prefix`.
let uartSimPins (prefix: string) : UartSimPins =
    { toDesign = $"{prefix}_rx"
      fromDesign = $"{prefix}_tx" }

/// The software UART: bytes queued with `Send` go out on the design's `rx`
/// one bit per `cyclesPerBit`; bytes the design sends on `tx` are decoded
/// into `Received`.
type SimUart(sim: Sim, pins: UartSimPins, cyclesPerBit: int) =
    let outgoing = System.Collections.Generic.Queue<byte>()
    let received = System.Collections.Generic.Queue<byte>()

    // Transmit side: the frame being shifted out, one bit per bit time.
    let mutable txFrame = 0
    let mutable txBit = -1 // -1: idle
    let mutable txPhase = 0
    let mutable line = 1UL

    // Receive side: sampled at bit centres from the start edge.
    let mutable rxBit = -1
    let mutable rxPhase = 0
    let mutable rxShift = 0

    member _.Send(bytes: byte seq) = for b in bytes do outgoing.Enqueue b
    member _.Received: byte list = List.ofSeq received
    member _.TakeReceived() = let r = List.ofSeq received in received.Clear(); r
    member _.Idle = txBit < 0 && outgoing.Count = 0

    interface ISimDevice with
        member _.Drive() =
            if txBit < 0 && outgoing.Count > 0 then
                // start (0), data LSB first, stop (1)
                txFrame <- (int (outgoing.Dequeue()) <<< 1) ||| (1 <<< 9)
                txBit <- 0
                txPhase <- 0

            if txBit >= 0 then
                line <- uint64 ((txFrame >>> txBit) &&& 1)
                txPhase <- txPhase + 1

                if txPhase = cyclesPerBit then
                    txPhase <- 0
                    txBit <- txBit + 1
                    if txBit = 10 then txBit <- -1
            else
                line <- 1UL

            sim.Poke(pins.toDesign, line)

        member _.Sample() =
            let tx = sim.Peek pins.fromDesign

            if rxBit < 0 then
                if tx = 0UL then
                    rxBit <- 0
                    rxPhase <- 0
            else
                rxPhase <- rxPhase + 1

                if rxPhase = cyclesPerBit / 2 then
                    // bit 0 is the start bit, 1..8 data, 9 stop
                    if rxBit >= 1 && rxBit <= 8 then
                        rxShift <- rxShift ||| (int tx <<< (rxBit - 1))
                    elif rxBit = 9 then
                        if tx = 1UL then received.Enqueue(byte rxShift)
                        rxShift <- 0
                elif rxPhase = cyclesPerBit then
                    rxPhase <- 0
                    rxBit <- rxBit + 1
                    if rxBit = 10 then rxBit <- -1

/// The register-map client over the software UART, with the same two verbs
/// as `SimAxi`'s: each sends a request, runs the design until the reply is
/// in, and hands back the value. `advance` is how one cycle passes, as in
/// `SimAxi.clientWith`.
let serialClientOn
    (addressBytes: int)
    (sim: Sim)
    (pins: UartSimPins)
    (cyclesPerBit: int)
    (advance: unit -> unit)
    : AxiLiteClient * SimUart =
    let uart = SimUart(sim, pins, cyclesPerBit)
    let device = uart :> ISimDevice

    // Frames the fabric sends unasked — a design streaming on the same wire
    // — are dropped here, because this client's job is the register map. A
    // real host that wants them reads the same status byte and keeps them;
    // what neither may do is mistake one for a reply.
    let rec dropStreamed (bytes: byte list) =
        match bytes with
        | sync :: status :: rest when sync = byte serialSync && status = byte serialStreamStatus ->
            // sync, status, four data bytes, checksum.
            if List.length rest >= 5 then dropStreamed (List.skip 5 rest) else []
        | _ -> bytes

    let exchange (isRead: bool) (request: byte list) =
        uart.TakeReceived() |> ignore
        uart.Send request
        let wanted = SerialFrame.replyLength isRead
        // Bits on the wire for the request and the reply, plus slack. A
        // streamed frame can land between them, so the budget allows for
        // some going past while the reply is waited for.
        let budget = (List.length request + wanted) * 10 * cyclesPerBit + 512 * cyclesPerBit
        let mutable cycles = 0
        let mutable pending = []

        while List.length (dropStreamed (pending @ uart.Received)) < wanted && cycles < budget do
            device.Drive()
            advance ()
            device.Sample()
            cycles <- cycles + 1

        match SerialFrame.parse isRead (dropStreamed (pending @ uart.TakeReceived())) with
        | Ok v -> v
        | Error why -> failwith $"serial register map: {why} after %d{cycles} cycles"

    { read32 = fun offset -> exchange true (SerialFrame.readWide addressBytes (int (offset >>> 2))) |> Option.get
      write32 =
        fun offset value ->
            exchange false (SerialFrame.writeWide addressBytes (int (offset >>> 2)) value)
            |> ignore },
    uart

/// The common case: cycles pass by ticking the Sim.
let serialClientWith (sim: Sim) (pins: UartSimPins) (cyclesPerBit: int) (advance: unit -> unit) =
    serialClientOn 1 sim pins cyclesPerBit advance

let serialClient (sim: Sim) (pins: UartSimPins) (cyclesPerBit: int) = serialClientWith sim pins cyclesPerBit sim.Tick

/// The client for a particular map, which is what a design with a table in it
/// needs: the frame shape follows from the map's width rather than being a thing
/// the caller remembers.
let serialClientForMap (m: RegMap) (sim: Sim) (pins: UartSimPins) (cyclesPerBit: int) =
    serialClientOn (serialAddressBytes (m.apertureAddrWidth - 2)) sim pins cyclesPerBit sim.Tick
