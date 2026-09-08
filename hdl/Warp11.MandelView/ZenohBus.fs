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

/// The PL clock, which is what turns the daemon's cycle count into
/// milliseconds — **taken from the board the design was built for**, not
/// written down here.
///
/// It used to be written down, with a comment saying the daemon names it too
/// and that if the two ever disagreed the reported fabric time would be wrong
/// while every pixel stayed right. They did disagree: the Game of Life daemon
/// next door had 166_666_667 where the overlay programs 166_666_672. A number
/// that only shows up in a *time* is exactly the kind that drifts unnoticed,
/// so it now comes from `Warp11.Mandelbrot.Board.frameBoard` — the app the
/// daemon serves — and the generated seam
/// carries the same value to the Rust side.

let PlClockHz = float Warp11.Mandelbrot.Board.frameBoard.fabricHz

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
