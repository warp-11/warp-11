/// The board bus: a Zenoh client session into the mandel-daemon peer over the
/// endpoint given on the command line (`tcp/192.168.1.172:7448`). Frames
/// arrive on `warp11/mandel/frame`; render requests go out as puts on
/// `warp11/mandel/ctl/render` — the exact key space the daemon documents.
/// Uses the community Zenoh-CS binding over zenoh-c 1.6.2 (`libzenohc.so`
/// must be on LD_LIBRARY_PATH — see the GolView README for where to get it).
module Warp11.MandelView.ZenohBus

open Warp11.MandelView.Bus
open Zenoh

let private keyed (name: string) =
    match Keyexpr.FromString name with
    | null -> failwith $"bad key expression: {name}"
    | key -> key

/// The PL clock `mandelframe_bd_bd.tcl` pins, which is what turns the
/// daemon's cycle count into milliseconds. It is the daemon's constant too;
/// if the two ever disagree the reported fabric time is wrong while every
/// pixel stays right, so it is named in both places rather than derived.
[<Literal>]
let PlClockHz = 166_666_672.0

type ZenohBus(endpoint: string) =
    let frameReceived = Event<MandelFrame>()

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
        | null -> failwith $"zenoh session open failed ({result}) — is mandel-daemon listening at {endpoint}?"
        | s -> s

    // The delegate is held as a field so the GC cannot collect it out from
    // under the native callback.
    let onSample =
        Subscriber.Cb(fun sample ->
            decodeFrame (sample.GetPayload().ToByteArray())
            |> Option.iter frameReceived.Trigger)

    let subscriber =
        let mutable sub: Subscriber = null

        let result =
            session.DeclareSubscriber(keyed "warp11/mandel/frame", SubscriberOptions(), onSample, &sub)

        match sub with
        | null -> failwith $"frame subscriber failed: {result}"
        | s -> s

    interface IMandelBus with
        [<CLIEvent>]
        member _.FrameReceived = frameReceived.Publish

        member _.Render view =
            session.Put(
                keyed "warp11/mandel/ctl/render",
                ZBytes.FromBytes(encodeView view),
                new PutOptions()
            )
            |> ignore

        member _.FabricHz = PlClockHz
        member _.Describe = $"KV260 @ {PlClockHz / 1e6:F2} MHz — {endpoint}"

        member _.Dispose() =
            subscriber.Undeclare()
            session.Close()
