/// A design's register map over a real serial port: the host end of
/// `SerialRegMap.fs` on a tty, the twin of the Rust `SerialWindow` in
/// `runtime/host`. The frames are `SerialFrame`'s, shared with the Sim's
/// `SimUart`, so a client checked against the design in the simulator sends
/// the same bytes to the board.
///
/// The verbs are `AxiLiteClient`'s two, which is what every driver on the F#
/// side is written against: a `RegMap` reached this way and one reached
/// through `SimAxi` or `SimUart` are indistinguishable to the code above.
///
///     use link = SerialLink.openForMap "/dev/ttyUSB0" 1_000_000 top.map
///     link.Client.read32 offset
module Warp11.Host.Serial

open System
open System.IO.Ports
open Warp11

/// How many streamed frames one request will step over before giving up. A
/// design streaming flat out must not be able to make a read hang forever; at
/// that point the link is oversubscribed and the caller should know.
let maxStreamedPerExchange = 4096

/// How long a reply may take to start arriving. A register read on the fabric
/// answers within a few bit times; what this covers is a USB serial adapter's
/// own latency, which is milliseconds, not the fabric.
let defaultReplyTimeout = TimeSpan.FromMilliseconds 500.0

/// A serial port speaking the register protocol for a map needing
/// `addressBytes` address bytes (one, or two for a map wider than the command
/// byte addresses — `serialAddressBytes` says which from the map's width).
///
/// Exchanges are serialised on the port: a telemetry poller and a settings
/// writer on two threads share one link safely.
type SerialLink(port: string, baud: int, addressBytes: int, replyTimeout: TimeSpan) =
    let serial = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
    let gate = obj ()

    do
        serial.ReadTimeout <- int replyTimeout.TotalMilliseconds
        serial.WriteTimeout <- int replyTimeout.TotalMilliseconds
        serial.Open()
        serial.DiscardInBuffer()

    // Replies are read a frame at a time, not a byte at a time: on Linux a
    // `ReadByte` costs a millisecond or more each, which made a seven-byte
    // reply a ten-millisecond exchange and starved a player sharing the link.
    let readBytes (n: int) : byte list =
        let buffer = Array.zeroCreate<byte> n
        let mutable got = 0

        while got < n do
            let took =
                try
                    serial.Read(buffer, got, n - got)
                with :? TimeoutException ->
                    failwith $"serial register map on {port}: no reply within {replyTimeout.TotalMilliseconds} ms"

            if took <= 0 then
                failwith $"serial register map on {port}: no reply within {replyTimeout.TotalMilliseconds} ms"

            got <- got + took

        List.ofArray buffer

    let readByte () = List.head (readBytes 1)

    /// The next frame on the wire that is a reply: bytes before a sync are
    /// noise from a reset or a half-frame and are skipped; streamed frames
    /// are read whole and stepped over.
    let rec readReply (isRead: bool) (streamedSeen: int) : byte list =
        if streamedSeen > maxStreamedPerExchange then
            failwith $"serial register map on {port}: {streamedSeen} streamed frames and no reply — the link is oversubscribed"

        let first = readByte ()

        if first <> SerialFrame.sync then
            readReply isRead streamedSeen
        else
            let status = readByte ()

            if status = byte serialStreamStatus then
                readBytes (SerialFrame.streamedLength - 2) |> ignore
                readReply isRead (streamedSeen + 1)
            else
                first :: status :: readBytes (SerialFrame.replyLength isRead - 2)

    let exchange (isRead: bool) (request: byte list) =
        lock gate (fun () ->
            let bytes = Array.ofList request
            serial.Write(bytes, 0, bytes.Length)

            match SerialFrame.parse isRead (readReply isRead 0) with
            | Ok v -> v
            | Error why -> failwith $"serial register map on {port}: {why}")

    /// The register map, by byte offset — the seam's `*_OFFSET` constants and
    /// a `RegEntry`'s `offset` address it here exactly as they do over AXI-Lite.
    member _.Client: AxiLiteClient =
        { read32 = fun offset -> exchange true (SerialFrame.readWide addressBytes (int (offset >>> 2))) |> Option.get
          write32 =
            fun offset value ->
                exchange false (SerialFrame.writeWide addressBytes (int (offset >>> 2)) value)
                |> ignore }

    /// Play `words` into a link that takes played frames — one write of
    /// every frame, under the same gate as the exchanges so no frame lands
    /// inside a request. Nothing replies: the design's `…LinkFill` register
    /// is what a caller paces by.
    member _.Play(words: uint32 seq) =
        let bytes = words |> Seq.collect (fun w -> SerialFrame.play (uint64 w)) |> Array.ofSeq
        lock gate (fun () -> serial.Write(bytes, 0, bytes.Length))

    member _.Port = port
    member _.Baud = baud

    interface IDisposable with
        member _.Dispose() = serial.Dispose()

/// A link to a particular map, which is what a design with a table in it
/// needs: the frame shape follows from the map's width rather than being a
/// thing the caller remembers — as `serialClientForMap` does in the Sim.
let openForMap (port: string) (baud: int) (m: RegMap) : SerialLink =
    new SerialLink(port, baud, serialAddressBytes (m.apertureAddrWidth - 2), defaultReplyTimeout)
