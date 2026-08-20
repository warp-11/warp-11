/// A running design, driven from somewhere else.
///
/// Only one thing may call `Tick`, so this owns the run loop and everything
/// that has to be consistent with it — the cycle count, the breakpoints, the
/// watched signals. A UI never touches the `Sim`: it posts commands, which are
/// drained between ticks, and renders the snapshots that come back. That is
/// what lets a debugger window sit beside another window driving the same
/// design without the two racing for the simulator.
///
/// `IDebugSession` is the seam. `DebugSession` is the in-process implementation
/// and the only one today; a remote one would implement the same interface
/// without the views knowing.
module Warp11.Debug

open System.Numerics
open System.Threading
open Warp11.Inventory

type WatchValue =
    { name: string
      width: int
      value: BigInteger }

type BreakpointView =
    { text: string
      enabled: bool
      hits: int }

/// A window onto one memory. A mem is 2^addrWidth words, which is not a table —
/// so a viewer asks for a range and gets exactly that range, refreshed with
/// every snapshot.
type MemoryView =
    { name: string
      start: int
      /// BigInteger, not uint64: a word is whatever width the memory declares,
      /// and the 128-bit lane-masked tables were the first to not fit.
      words: System.Numerics.BigInteger[] }

/// One signal's history, oldest sample first. Narrow signals use `values` and
/// wide ones `wideValues`, because a trace of a 64-bit counter should not be an
/// array of boxed integers.
type TraceSignal =
    { name: string
      width: int
      values: uint64[]
      wideValues: BigInteger[] }

/// What was recorded, as cycles rather than as snapshots. The distinction is
/// the whole point: a snapshot arrives 30 times a second and a run advances
/// thousands of cycles between two of them, so sampling at snapshot rate would
/// produce a sparse picture and call it a waveform.
type Trace =
    { /// Cycle number of sample 0. A ring that has wrapped starts later than 0.
      firstCycle: int
      signals: TraceSignal list }

    member t.Length =
        match t.signals with
        | [] -> 0
        | s :: _ -> if s.width > 64 then s.wideValues.Length else s.values.Length

/// Everything a view renders, as one immutable value taken between ticks.
type Snapshot =
    { cycle: int
      running: bool
      /// Cycles per second over the recent window; zero while paused.
      rate: float
      /// The watched signals, in the order they were added.
      values: WatchValue list
      /// Signals a *program* asked for, as opposed to the ones a person is
      /// watching. Kept apart so an app reading the design cannot have its
      /// data removed by someone tidying up the watch list.
      sampled: Map<string, BigInteger>
      breakpoints: BreakpointView list
      /// The memory range being viewed, if any.
      memory: MemoryView option
      recording: bool
      /// Cycles held in the ring right now, and how many it can hold.
      recorded: int
      capacity: int
      /// The breakpoint that stopped the run, if one did.
      hit: string option }

type IDebugSession =
    inherit System.IDisposable
    abstract Design: ModuleDef
    abstract Inventory: ModuleInventory
    /// Fires on the session's own thread — a view marshals.
    [<CLIEvent>]
    abstract Published: IEvent<Snapshot>
    abstract Latest: Snapshot

    /// One step of the run loop, for a host that owns no thread to run it on —
    /// a session started with `ownThread = false` does nothing until someone
    /// calls this. True when it went idle. A session that runs its own thread
    /// answers true and does nothing, so a host may call it unconditionally.
    abstract Pump: unit -> bool

    abstract Watch: name: string -> unit
    abstract Unwatch: name: string -> unit
    abstract Poke: name: string * value: BigInteger -> unit

    /// Sample these signals into every snapshot from now on — the channel an
    /// app built on a design reads through, separate from the watch list.
    abstract Sample: names: string list -> unit

    /// Show `count` words of a memory from `start`, in every snapshot from now
    /// on. Out-of-range asks are clamped to the memory rather than refused —
    /// paging past the end is a scroll, not a mistake.
    abstract ViewMemory: name: string * start: int * count: int -> unit
    abstract ClearMemoryView: unit -> unit

    /// Start filling the trace ring, one sample per *cycle*. `everySignal`
    /// records the whole design rather than the watch list; the ring gets
    /// shorter to stay inside the memory budget, and `Latest.capacity` says how
    /// much shorter. Returns what went wrong, if anything.
    abstract StartRecording: everySignal: bool -> Result<unit, string>
    abstract StopRecording: unit -> unit
    /// The ring, unrolled oldest-first. Empty when nothing has been recorded.
    abstract Trace: unit -> Trace
    /// `count` samples from `start` (0 = oldest held), clamped to what exists.
    /// One sample per cycle, never decimated — a viewer that thinned samples to
    /// fit its width would hide every transition between the ones it kept.
    abstract TraceSlice: start: int * count: int -> Trace

    /// Compiled on the caller's thread, so a bad expression comes back as an
    /// error where it was typed rather than as a message from a worker.
    abstract AddBreakpoint: text: string -> Result<unit, string>
    abstract RemoveBreakpoint: text: string -> unit
    abstract EnableBreakpoint: text: string * enabled: bool -> unit

    abstract Reset: unit -> unit
    abstract Step: cycles: int -> unit
    abstract Run: unit -> unit
    abstract Pause: unit -> unit

type private Command =
    | PokeSignal of string * BigInteger
    | AddWatch of string
    | DropWatch of string
    | SampleThese of string list
    | BeginRecording of (string * Handle)[] * int
    | EndRecording
    | ShowMemory of string * int * int
    | HideMemory
    | ArmBreakpoint of string * (unit -> bool)
    | DisarmBreakpoint of string
    | EnableBreakpoint of string * bool
    | DoReset
    | DoStep of int
    | DoRun
    | DoPause

type private Armed =
    { text: string
      isHit: unit -> bool
      mutable enabled: bool
      mutable hits: int }

/// One column of the trace ring. Only one of the two arrays is ever filled;
/// the other is empty, which costs a reference.
type private Column =
    { name: string
      width: int
      handle: Handle
      narrow: uint64[]
      wide: BigInteger[] }

/// How long a run may tick before it stops to drain commands and publish. Long
/// enough that the loop is not all overhead, short enough that Pause feels
/// immediate.
let private sliceMilliseconds = 8.0

/// The trace ring's bounds. Depth is traded against width inside a fixed memory
/// budget, so recording every signal of a large design gives a shorter window
/// rather than an error — 4,245 signals still leaves nearly 2,000 cycles.
let private traceBudgetBytes = 64 * 1024 * 1024
let private traceMaxCycles = 8192
let private traceMinCycles = 256

/// Publishing faster than this is work nobody sees.
let private publishMilliseconds = 30.0

/// `ownThread = false` leaves the run loop unstarted, and the host drives
/// `Pump` itself. That is not a preference — WebAssembly has one thread and
/// `Thread.Start` throws there, so a browser host has nothing else to offer.
type DebugSession(design: ModuleDef, ?ownThread: bool) =
    let ownThread = defaultArg ownThread true

    // A debugger is for finding bugs, so claims the design makes about itself
    // are checked here even though the Sim's own default is off. A violation
    // stops the run exactly like a breakpoint, because that is what it is —
    // one the design carries with it.
    let sim = Sim(design, checkAsserts = true)
    let inventory = Inventory.ofDesign design

    let commands = System.Collections.Concurrent.ConcurrentQueue<Command>()
    let wake = new AutoResetEvent(false)
    let published = Event<Snapshot>()

    let watched = ResizeArray<string * Handle>()
    let sampled = ResizeArray<string * Handle>()
    let breakpoints = ResizeArray<Armed>()

    let clock = System.Diagnostics.Stopwatch.StartNew()

    let mutable cycle = 0
    let mutable running = false
    let mutable hit = None
    let mutable rate = 0.0
    let mutable stepsLeft = 0
    let mutable memoryView = None

    // The trace ring. `written` counts samples ever taken, so the ring is full
    // once it passes `capacity` and sample k lives at k % capacity.
    let mutable columns: Column[] = [||]
    let mutable capacity = 0
    let mutable written = 0
    let mutable recording = false
    let mutable recordStartCycle = 0
    let mutable seenViolations = 0

    /// Held for the duration of a tick slice, and by anyone copying the ring
    /// out. Per-slice rather than per-cycle, so a reader waits at most one
    /// slice and the run pays one lock every few thousand ticks.
    let traceGate = obj ()
    let mutable disposed = false

    /// Set straight from the caller's thread rather than through the queue,
    /// because a pause has to be able to interrupt the very step that is
    /// holding the queue up.
    [<VolatileField>]
    let mutable interrupted = false

    /// Written by the loop, read by whoever asks — a reference swap, so a
    /// reader sees one whole snapshot or the previous one, never a mixture.
    [<VolatileField>]
    let mutable latest = Unchecked.defaultof<Snapshot>

    let snapshot () =
        { cycle = cycle
          running = running
          rate = rate
          values =
            [ for name, handle in watched ->
                  { name = name
                    width = handle.Width
                    value = sim.PeekWideAt handle } ]
          sampled = Map.ofList [ for name, handle in sampled -> name, sim.PeekWideAt handle ]
          breakpoints =
            [ for b in breakpoints ->
                  { text = b.text
                    enabled = b.enabled
                    hits = b.hits } ]
          recording = recording
          recorded = min written capacity
          capacity = capacity
          memory =
            memoryView
            |> Option.map (fun (name, start, count) ->
                { name = name
                  start = start
                  words = [| for i in start .. start + count - 1 -> sim.PeekMemWide(name, i) |] })
          hit = hit }

    let publish () =
        let s = snapshot ()
        latest <- s
        published.Trigger s

    /// Take one sample of every recorded column. Called once per cycle while
    /// recording, so it is deliberately a straight loop over resolved handles.
    let sample () =
        let slot = written % capacity

        for c in columns do
            if c.width > 64 then
                c.wide[slot] <- sim.PeekWideAt c.handle
            else
                c.narrow[slot] <- sim.PeekAt c.handle

        written <- written + 1

    /// One tick and the breakpoint test, which is what makes a run a run rather
    /// than a loop. Returns the breakpoint that fired.
    let tickOnce () =
        sim.Tick()
        cycle <- cycle + 1

        // Sampled before the breakpoint test, so the cycle that stops a run is
        // the last one *in* the trace rather than the first one missing from it.
        if recording then sample ()

        if sim.ViolationCount > seenViolations then
            seenViolations <- sim.ViolationCount
            running <- false
            stepsLeft <- 0

            hit <-
                match sim.LastViolation with
                | Some (message, _) -> Some $"assertion: {message}"
                | None -> hit

        let fired =
            if hit.IsSome && not running then
                None
            else
                breakpoints |> Seq.tryFind (fun b -> b.enabled && b.isHit ())

        match fired with
        | Some b ->
            b.hits <- b.hits + 1
            running <- false
            stepsLeft <- 0
            hit <- Some b.text
        | None -> ()

        fired

    let apply command =
        match command with
        | PokeSignal (name, value) ->
            match sim.TryWidth name with
            | Some w when w > 64 -> sim.PokeWide(name, value)
            | Some _ -> sim.Poke(name, uint64 value)
            | None -> ()
        | AddWatch name ->
            if not (watched |> Seq.exists (fun (n, _) -> n = name)) then
                match sim.TryWidth name with
                | Some _ -> watched.Add(name, sim.Handle name)
                | None -> ()
        | DropWatch name -> watched.RemoveAll(fun (n, _) -> n = name) |> ignore
        | SampleThese names ->
            sampled.Clear()

            for name in names do
                match sim.TryWidth name with
                | Some _ -> sampled.Add(name, sim.Handle name)
                | None -> ()
        | ShowMemory (name, start, count) ->
            // Clamped here, where the depth is known, so a viewer can ask for
            // the page after the last one and simply get the tail.
            match sim.TryMemShape name with
            | None -> memoryView <- None
            | Some (addrWidth, _) ->
                let depth = 1 <<< addrWidth
                let first = max 0 (min start (depth - 1))
                memoryView <- Some(name, first, max 0 (min count (depth - first)))
        | HideMemory -> memoryView <- None
        | BeginRecording (wanted, depth) ->
            capacity <- depth
            written <- 0
            recordStartCycle <- cycle

            columns <-
                [| for name, handle in wanted ->
                       { name = name
                         width = handle.Width
                         handle = handle
                         narrow = if handle.Width > 64 then [||] else Array.zeroCreate depth
                         wide = if handle.Width > 64 then Array.create depth BigInteger.Zero else [||] } |]

            recording <- true
            // The state as it stands is sample 0, so a trace that begins before
            // the first tick still says what the design started from.
            if columns.Length > 0 then sample ()
        | EndRecording -> recording <- false
        | ArmBreakpoint (text, isHit) ->
            breakpoints.RemoveAll(fun b -> b.text = text) |> ignore

            breakpoints.Add
                { text = text
                  isHit = isHit
                  enabled = true
                  hits = 0 }
        | DisarmBreakpoint text -> breakpoints.RemoveAll(fun b -> b.text = text) |> ignore
        | EnableBreakpoint (text, enabled) ->
            for b in breakpoints do
                if b.text = text then b.enabled <- enabled
        | DoReset ->
            sim.Reset()
            cycle <- 0
            running <- false
            stepsLeft <- 0
            hit <- None
            rate <- 0.0
        | DoStep n ->
            hit <- None
            stepsLeft <- max 0 n
        | DoRun ->
            hit <- None
            running <- true
        | DoPause ->
            running <- false
            stepsLeft <- 0

    /// Tick for one slice, stopping early on a breakpoint. Returns the cycles
    /// advanced, so the caller can price the rate.
    let runSlice budget =
        let started = clock.Elapsed.TotalMilliseconds

        let rec go advanced =
            if advanced >= budget then
                advanced
            elif clock.Elapsed.TotalMilliseconds - started >= sliceMilliseconds then
                advanced
            else
                match tickOnce () with
                | Some _ -> advanced + 1
                | None -> go (advanced + 1)

        go 0

    let mutable lastPublish = 0.0
    let mutable owed = false
    let mutable rateCycles = 0
    let mutable rateSince = 0.0

    /// One bounded step of the run loop: drain the commands a step is not
    /// holding back, tick at most `sliceMilliseconds` worth, publish what is
    /// owed. Returns true when there was nothing left to do, which is the
    /// driver's cue to wait rather than spin.
    ///
    /// A step rather than a loop because who drives it is the host's business.
    /// A desktop process gives it a thread; a browser has none to give and
    /// drives it from the frame timer instead.
    let pump () =
            if interrupted then
                interrupted <- false
                running <- false
                stepsLeft <- 0

            let mutable command = Unchecked.defaultof<Command>

            // A step holds the queue until it finishes, so `poke; step; poke`
            // means what it plainly says. Draining everything first and then
            // stepping would apply the trailing pokes to the cycle the step was
            // supposed to see — which is exactly how a load that must land on
            // one edge silently stops landing at all.
            while stepsLeft = 0 && commands.TryDequeue &command do
                apply command
                owed <- true

            let advanced =
                lock traceGate (fun () ->
                    if running then
                        runSlice System.Int32.MaxValue
                    elif stepsLeft > 0 then
                        let n = runSlice stepsLeft
                        stepsLeft <- max 0 (stepsLeft - n)
                        n
                    else
                        0)

            let now = clock.Elapsed.TotalMilliseconds
            owed <- owed || advanced > 0

            if advanced > 0 then
                rateCycles <- rateCycles + advanced

                if now - rateSince >= 250.0 then
                    let sample = float rateCycles * 1000.0 / (now - rateSince)
                    rate <- if rate = 0.0 then sample else 0.6 * rate + 0.4 * sample
                    rateCycles <- 0
                    rateSince <- now
            else
                rate <- 0.0
                rateCycles <- 0
                rateSince <- now

            // While a run is in flight, publishing is throttled — nobody sees
            // more. The moment there is nothing left to do, publish whatever is
            // owed before sleeping, or the last state of a run never arrives.
            if advanced = 0 && stepsLeft = 0 && commands.IsEmpty then
                if owed then
                    publish ()
                    lastPublish <- now
                    owed <- false

                true
            else
                if owed && now - lastPublish >= publishMilliseconds then
                    publish ()
                    lastPublish <- now
                    owed <- false

                false

    let loop () =
        lastPublish <- clock.Elapsed.TotalMilliseconds
        rateSince <- clock.Elapsed.TotalMilliseconds

        while not disposed do
            if pump () then
                wake.WaitOne 50 |> ignore

    let thread =
        if ownThread then
            Some(Thread(loop, IsBackground = true, Name = $"warp11-debug-{design.name}"))
        else
            None

    do
        latest <- snapshot ()
        thread |> Option.iter (fun t -> t.Start())

    let post command =
        commands.Enqueue command
        wake.Set() |> ignore

    member _.Sim = sim

    interface IDebugSession with
        member _.Design = design
        member _.Inventory = inventory

        [<CLIEvent>]
        member _.Published = published.Publish

        member _.Latest = latest
        member _.Pump() = if ownThread then true else pump ()

        member _.Watch name = post (AddWatch name)
        member _.Unwatch name = post (DropWatch name)
        member _.Poke(name, value) = post (PokeSignal(name, value))
        member _.Sample names = post (SampleThese names)
        member _.ViewMemory(name, start, count) = post (ShowMemory(name, start, count))
        member _.ClearMemoryView() = post HideMemory

        member _.StartRecording everySignal =
            // What the watch table is showing is what "record the watch list"
            // means — the same list the person is looking at.
            let names =
                if everySignal then
                    inventory.signals |> List.map (fun s -> s.name)
                else
                    latest.values |> List.map (fun v -> v.name)

            let wanted =
                [| for name in names do
                       match sim.TryWidth name with
                       | Some _ -> yield name, sim.Handle name
                       | None -> () |]

            if wanted.Length = 0 then
                Error "nothing to record — watch some signals first, or record every signal"
            else
                let affordable = traceBudgetBytes / (wanted.Length * 8)
                let depth = min traceMaxCycles affordable

                if depth < traceMinCycles then
                    Error
                        $"%d{wanted.Length} signals would leave room for only %d{depth} cycles — watch fewer, or raise the budget"
                else
                    post (BeginRecording(wanted, depth))
                    Ok()

        member _.StopRecording() = post EndRecording

        member this.Trace() = (this :> IDebugSession).TraceSlice(0, System.Int32.MaxValue)

        member _.TraceSlice(start, count) =
            lock traceGate (fun () ->
                let held = min written capacity
                // Oldest first: once the ring has wrapped, sample k lives at
                // k % capacity and the oldest retained k is written - held.
                let oldest = written - held
                let first = max 0 (min start (max 0 (held - 1)))
                let take = max 0 (min count (held - first))

                if take = 0 then
                    { firstCycle = 0; signals = [] }
                else
                    { firstCycle = recordStartCycle + oldest + first
                      signals =
                        [ for c in columns ->
                              let narrow = if c.width > 64 then [||] else Array.zeroCreate take
                              let wide = if c.width > 64 then Array.create take BigInteger.Zero else [||]

                              for i in 0 .. take - 1 do
                                  let slot = (oldest + first + i) % capacity

                                  if c.width > 64 then
                                      wide[i] <- c.wide[slot]
                                  else
                                      narrow[i] <- c.narrow[slot]

                              { name = c.name
                                width = c.width
                                values = narrow
                                wideValues = wide } ] })

        member _.AddBreakpoint text =
            // Parsing and compiling only read tables that stop changing at
            // construction, so this is safe to do on the caller's thread — and
            // that is what lets the error land where it was typed.
            match Breakpoint.compile sim text with
            | Error message -> Error message
            | Ok bp ->
                post (ArmBreakpoint(bp.text, bp.isHit))
                Ok()

        member _.RemoveBreakpoint text = post (DisarmBreakpoint text)
        member _.EnableBreakpoint(text, enabled) = post (EnableBreakpoint(text, enabled))

        member _.Reset() = post DoReset
        member _.Step cycles = post (DoStep cycles)
        member _.Run() = post DoRun
        member _.Pause() =
            interrupted <- true
            post DoPause

        member _.Dispose() =
            disposed <- true
            wake.Set() |> ignore
            thread |> Option.iter (fun t -> t.Join 500 |> ignore)
            wake.Dispose()
