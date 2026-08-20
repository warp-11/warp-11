/// The bring-up rig: the software twin on a worker thread, behind the same
/// interface as the board. It mirrors the daemon's two load-bearing
/// behaviours rather than just computing pixels — one render at a time, and
/// superseded requests coalesce — so the view's interaction model is
/// exercised locally and does not first meet them over the network.
///
/// It reports `cycles = 0` and `FabricHz = 0.0`: a twin has no fabric, and
/// the view says so instead of quoting a number it made up.
module Warp11.MandelView.SimulatedBus

open Warp11.MandelView.Bus

type SimulatedBus(width: int, height: int, maxIter: int) =
    let frameReceived = Event<MandelFrame>()
    let gate = obj ()
    let mutable pending: MandelView option = None
    let mutable disposed = false

    let worker =
        System.Threading.Thread(
            (fun () ->
                while not disposed do
                    // Take the newest request and drop the rest: the daemon's
                    // coalescing rule, and the reason a drag does not queue a
                    // frame per intermediate view.
                    let request =
                        lock gate (fun () ->
                            let taken = pending
                            pending <- None
                            taken)

                    match request with
                    | None -> System.Threading.Thread.Sleep 5
                    | Some view ->
                        let pixels = Twin.frame view width height maxIter

                        frameReceived.Trigger
                            { view = view
                              cycles = 0u
                              width = width
                              height = height
                              maxIter = maxIter
                              pixels = pixels }),
            IsBackground = true
        )

    do worker.Start()

    interface IMandelBus with
        [<CLIEvent>]
        member _.FrameReceived = frameReceived.Publish

        member _.Render view = lock gate (fun () -> pending <- Some view)

        member _.FabricHz = 0.0
        member _.Describe = $"software twin, {System.Environment.ProcessorCount} threads"

        member _.Dispose() = disposed <- true
