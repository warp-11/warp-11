/// The third bus: the actual Game of Life RTL, in simulation.
///
/// `SimulatedBus` runs the software twin and `ZenohBus` talks to the board, so
/// between them the elaborated design — the thing both of the others are
/// *about* — was the one world the view could not show. This is that world: a
/// `DebugSession` over `golLiveHarness`, driven through the same `IGolBus` the
/// other two implement.
///
/// It is also the side-by-side case. The session is a first-class object, so
/// `Debugger` hands it to a debugger window that steps and breakpoints the very
/// design this view is rendering — one simulator, two windows, no race, because
/// nothing here touches the `Sim` directly.
module Warp11.GolView.HdlSimBus

open System.Numerics
open Warp11.Debug
open Warp11.GoL.Core
open Warp11.GolView.Bus

/// Frames come off the design's own ports, and the generation counter is the
/// design's own register — not a tally the host kept, which would go on
/// counting if the design stopped.
let private rowNames height = [ for y in 0 .. height - 1 -> $"row_%d{y}" ]

type HdlSimBus(gridWidth: int, gridHeight: int) =
    let frameReceived = Event<GolFrame>()

    // In a browser there is one thread, so the session leaves its run loop
    // unstarted and the pacing timer below pumps it — the same split SimView
    // makes when it opens a session there.
    let browser = System.OperatingSystem.IsBrowser()

    let session =
        new DebugSession(golLiveHarness gridWidth gridHeight, ownThread = not browser)

    let live = session :> IDebugSession

    let gate = obj ()
    let mutable gensPerSec = 0u
    let mutable running = false
    let mutable disposed = false

    do live.Sample("population" :: "generation" :: rowNames gridHeight)

    let frameOf (snapshot: Snapshot) =
        let word name =
            snapshot.sampled |> Map.tryFind name |> Option.defaultValue BigInteger.Zero

        { generation = uint32 (word "generation")
          population = uint32 (word "population")
          rows = [| for y in 0 .. gridHeight - 1 -> uint64 (word $"row_%d{y}") |] }

    /// Load is a cycle of its own: `load_enable` outranks `tick_enable` in the
    /// grid, and the design zeroes its generation on the same edge.
    let load (rows: uint64[]) =
        lock gate (fun () ->
            live.Pause()

            for y in 0 .. gridHeight - 1 do
                live.Poke($"load_row_%d{y}", BigInteger(if y < rows.Length then rows[y] else 0UL))

            live.Poke("load_enable", BigInteger.One)
            live.Poke("tick_enable", BigInteger.Zero)
            live.Step 1
            live.Poke("load_enable", BigInteger.Zero)
            live.Poke("tick_enable", BigInteger.One)

            if running then
                if gensPerSec = 0u then live.Run())

    // The pacing, host-agnostic: flat out hands the session its own `Run` and
    // gets out of the way; a rate accrues fractional generations and posts the
    // whole ones as steps. Called from whichever loop this host can have.
    let clock = System.Diagnostics.Stopwatch.StartNew()
    let mutable nextPublish = 0.0
    let mutable credit = 0.0
    let mutable lastAccrual = 0.0

    let pace () =
        let now = clock.Elapsed.TotalSeconds

        lock gate (fun () ->
            // A breakpoint stops the design whichever window is
            // driving it. Flat out that happens by itself — the
            // session's own loop halts — but a paced run is this
            // caller posting steps, and it would otherwise walk
            // straight past the thing that just stopped.
            if running && live.Latest.hit.IsSome then
                running <- false

            credit <-
                if running && gensPerSec > 0u then
                    min (credit + float gensPerSec * (now - lastAccrual)) 4096.0
                else
                    0.0

            lastAccrual <- now

            if running && gensPerSec > 0u && credit >= 1.0 then
                live.Step(int credit)
                credit <- credit - floor credit)

        if now >= nextPublish then
            frameReceived.Trigger(frameOf live.Latest)
            nextPublish <- now + 0.033

    // One thread owns the pacing, exactly as in `SimulatedBus` — except in a
    // browser, where a `DispatcherTimer` on the one thread there is does the
    // same job and pumps the session's run loop while it is at it.
    let worker =
        if browser then
            None
        else
            Some(
                System.Threading.Thread(
                    (fun () ->
                        while not disposed do
                            pace ()
                            System.Threading.Thread.Sleep 1),
                    IsBackground = true
                )
            )

    do
        live.Poke("tick_enable", BigInteger.One)

        match worker with
        | Some thread -> thread.Start()
        | None ->
            Avalonia.Threading.DispatcherTimer.Run(
                (fun () ->
                    if not disposed then
                        live.Pump() |> ignore
                        pace ()

                    not disposed),
                System.TimeSpan.FromMilliseconds 16.0
            )
            |> ignore

    /// The session behind this bus, for a debugger window opened beside the
    /// view. Handing out the session rather than the `Sim` is the whole point:
    /// commands queue, so two windows driving one design cannot race.
    member _.Session = live

    interface IGolBus with
        [<CLIEvent>]
        member _.FrameReceived = frameReceived.Publish

        member _.Load rows = load rows

        member _.Run rate =
            lock gate (fun () ->
                gensPerSec <- rate
                running <- true
                // Flat out is the session's own run loop; a rate is paced from
                // the worker above, so the loop must not also be free-running.
                if rate = 0u then live.Run() else live.Pause())

        member _.Stop() =
            lock gate (fun () ->
                running <- false
                live.Pause())

        member _.Reset() =
            lock gate (fun () -> running <- false)
            load (Array.zeroCreate gridHeight)

        member _.Dispose() =
            disposed <- true
            worker |> Option.iter (fun thread -> thread.Join 500 |> ignore)
            live.Dispose()
