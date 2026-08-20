/// The board bus: a Zenoh client session into the gol-daemon peer over the
/// endpoint given on the command line (`tcp/192.168.1.172:7447`). Frames
/// arrive on `warp11/gol/frame`; control goes out as puts on
/// `warp11/gol/ctl/*` — the exact key space the daemon documents. Uses the
/// community Zenoh-CS binding over zenoh-c 1.6.2 (`libzenohc.so` must be on
/// LD_LIBRARY_PATH — see README.md here for where to get it).
module Warp11.GolView.ZenohBus

open Warp11.GolView.Bus
open Zenoh

let private keyed (name: string) =
    match Keyexpr.FromString name with
    | null -> failwith $"bad key expression: {name}"
    | key -> key

type ZenohBus(endpoint: string) =
    let frameReceived = Event<GolFrame>()

    let config =
        let json =
            sprintf """{ mode: "client", connect: { endpoints: ["%s"] } }""" endpoint

        match Config.FromStr json with
        | null -> failwith $"zenoh rejected the config for {endpoint}"
        | c -> c

    let session =
        let mutable s: Session = null
        let result = Session.Open(config, &s)

        match s with
        | null -> failwith $"zenoh session open failed ({result}) — is gol-daemon listening at {endpoint}?"
        | s -> s

    // The delegate is held as a field so the GC cannot collect it out from
    // under the native callback.
    let onSample =
        Subscriber.Cb(fun sample ->
            decodeFrame (sample.GetPayload().ToByteArray())
            |> Option.iter frameReceived.Trigger)

    let subscriber =
        let mutable sub: Subscriber = null
        let result = session.DeclareSubscriber(keyed "warp11/gol/frame", SubscriberOptions(), onSample, &sub)

        match sub with
        | null -> failwith $"frame subscriber failed: {result}"
        | s -> s

    let put (name: string) (payload: byte[]) =
        session.Put(keyed $"warp11/gol/ctl/{name}", ZBytes.FromBytes payload, new PutOptions())
        |> ignore

    interface IGolBus with
        [<CLIEvent>]
        member _.FrameReceived = frameReceived.Publish

        member _.Load rows = put "load" (encodeRows rows)
        member _.Run gensPerSec = put "run" (System.BitConverter.GetBytes gensPerSec)
        member _.Stop() = put "stop" [| 0uy |]
        member _.Reset() = put "reset" [| 0uy |]

        member _.Dispose() =
            subscriber.Undeclare()
            session.Close()
