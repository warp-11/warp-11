/// The bring-up rig: a software engine stepping on a worker thread, behind
/// the same interface as the board. `Run 0` means flat out — the same
/// convention the fabric's interval register uses — and the rig then
/// conflates exactly like the hardware's triple buffer: it steps as fast as
/// the engine allows and publishes the latest completed state at ~30 Hz.
/// Which engine it steps is the constructor's business — the composition
/// root swaps `Engine.stepIdiomatic` for its faster siblings and nothing
/// else in the app moves.
module Warp11.GolView.SimulatedBus

open Warp11.GolView.Bus

type SimulatedBus(step: uint64[] -> uint64[]) =
    let frameReceived = Event<GolFrame>()
    let gate = obj ()
    let mutable rows = Array.zeroCreate<uint64> 64
    let mutable generation = 0u
    let mutable running = false
    let mutable gensPerSec = 0u
    let mutable disposed = false

    let publish () =
        let frame =
            lock gate (fun () ->
                { generation = generation
                  population = Engine.population rows
                  rows = Array.copy rows })

        frameReceived.Trigger frame

    // One thread owns the stepping; controls only flip state under the gate.
    // Flat out, the lock is taken per 64-generation batch so a control never
    // waits more than one batch.
    let worker =
        System.Threading.Thread(
            (fun () ->
                let clock = System.Diagnostics.Stopwatch.StartNew()
                let mutable nextPublish = 0.0
                let mutable credit = 0.0
                let mutable lastAccrual = 0.0

                while not disposed do
                    let now = clock.Elapsed.TotalSeconds

                    let idle =
                        lock gate (fun () ->
                            // Paced mode accrues fractional generations so any
                            // rate is honored; the cap absorbs suspend stalls.
                            credit <-
                                if running && gensPerSec > 0u then
                                    min (credit + float gensPerSec * (now - lastAccrual)) 4096.0
                                else
                                    0.0

                            lastAccrual <- now

                            if not running then
                                true
                            elif gensPerSec = 0u then
                                for _ in 1..64 do
                                    rows <- step rows
                                    generation <- generation + 1u

                                false
                            elif credit >= 1.0 then
                                for _ in 1 .. int credit do
                                    rows <- step rows
                                    generation <- generation + 1u

                                credit <- credit - floor credit
                                false
                            else
                                true)

                    if now >= nextPublish then
                        publish ()
                        nextPublish <- now + 0.033

                    if idle then
                        System.Threading.Thread.Sleep 1),
            IsBackground = true
        )

    do worker.Start()

    interface IGolBus with
        [<CLIEvent>]
        member _.FrameReceived = frameReceived.Publish

        member _.Load newRows =
            lock gate (fun () ->
                rows <- Array.copy newRows
                generation <- 0u)

        member _.Run rate =
            lock gate (fun () ->
                gensPerSec <- rate
                running <- true)

        member _.Stop() = lock gate (fun () -> running <- false)

        // Stop-then-clear, the same sequence the board daemon runs on
        // `ctl/reset`.
        member _.Reset() =
            lock gate (fun () ->
                running <- false
                rows <- Array.zeroCreate 64
                generation <- 0u)

        member _.Dispose() = disposed <- true
