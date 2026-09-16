/// Hearing what the output box hears.
///
/// The device pushes each frame it hears into a short bounded queue on the
/// session's thread; a writer thread drains the queue into a player's stdin.
/// When the player's buffer is full the pipe blocks, the queue fills, and the
/// device's push waits — so the simulator runs exactly as fast as the
/// speaker drains it. The audio clock paces the design through
/// back-pressure, the way a codec paces the fabric. Nothing here counts
/// cycles or sleeps.
///
/// Desktop only: it spawns a process. The browser head compiles it and never
/// calls it.
module Warp11.Placement.Canvas.AudioSink

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open Warp11

/// Players tried in order, each reading s16le stereo PCM on stdin.
/// `WARP11_AUDIO_PLAYER` overrides with a whole command line.
let private players (rate: int) =
    [ "paplay", $"--raw --rate=%d{rate} --channels=2 --format=s16le --latency-msec=60 /dev/stdin"
      "aplay", $"-q -t raw -f S16_LE -r %d{rate} -c 2 -" ]

let private onPath (exe: string) =
    (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split ':'
    |> Array.exists (fun d -> d <> "" && File.Exists(Path.Combine(d, exe)))

let private choose (rate: int) : (string * string) option =
    match Environment.GetEnvironmentVariable "WARP11_AUDIO_PLAYER" with
    | null
    | "" -> players rate |> List.tryFind (fun (exe, _) -> onPath exe)
    | line ->
        let parts = line.Split(' ', 2)
        Some(parts[0], (if parts.Length > 1 then parts[1] else ""))

/// Is there anything to play through?
let available () = (choose 48_000).IsSome

/// One speaker's worth of queue and process. `Push` is what the device
/// calls; `Start`/`Stop` are the Play button.
type AudioSink(rate: int) =
    // ~43 ms at 48 kHz: the knob-to-ear latency the queue adds. The player's
    // own buffer adds its `--latency-msec` on top.
    let queue = new BlockingCollection<struct (int16 * int16)>(2048)
    let mutable listening = false
    let mutable player: Process option = None
    let mutable sent = 0L

    /// Frames handed to the player so far.
    member _.Sent = sent
    member _.Listening = listening

    /// Called on the session's thread for every frame heard. Waits while the
    /// queue is full — that wait is the pacing — but never past a `Stop`.
    member _.Push(left: int16, right: int16) =
        let mutable added = false

        while listening && not added do
            added <- queue.TryAdd(struct (left, right), 50)

    member _.Start() : Result<string, string> =
        match choose rate with
        | None -> Error "no player found: install paplay or aplay, or set WARP11_AUDIO_PLAYER"
        | Some(exe, args) ->
            if not listening then
                let p = Process.Start(ProcessStartInfo(exe, args, RedirectStandardInput = true, UseShellExecute = false))
                player <- Some p
                listening <- true

                let writer =
                    Thread(fun () ->
                        try
                            use w = new BinaryWriter(p.StandardInput.BaseStream)
                            let mutable item = Unchecked.defaultof<struct (int16 * int16)>
                            let mutable pending = 0

                            while listening do
                                if queue.TryTake(&item, 100) then
                                    let struct (l, r) = item
                                    w.Write l
                                    w.Write r
                                    sent <- sent + 1L
                                    pending <- pending + 1

                                    if pending >= 256 then
                                        w.Flush()
                                        pending <- 0
                                else
                                    w.Flush()
                        with _ ->
                            ())

                writer.IsBackground <- true
                writer.Start()

            Ok exe

    /// Closing stdin lets the player drain what it holds and exit on its
    /// own; only one that will not is killed.
    member _.Stop() =
        listening <- false

        player
        |> Option.iter (fun p ->
            try
                p.StandardInput.Close()

                if not (p.WaitForExit 500) then
                    p.Kill()
            with _ ->
                ()

            p.Dispose())

        player <- None
        let mutable dropped = Unchecked.defaultof<struct (int16 * int16)>

        while queue.TryTake(&dropped) do
            ()

// The library's `WavStreamDevice` conversions, restated: 16-bit samples up to
// the design's 24 bits, and back with rounding.
let private toBits (s: int16) = uint64 (int64 s <<< 8) &&& ((1UL <<< sampleWidth) - 1UL)

let private fromBits (bits: uint64) : int16 =
    let signed = if bits >= (1UL <<< (sampleWidth - 1)) then int64 bits - (1L <<< sampleWidth) else int64 bits
    int16 (max -32768L (min 32767L ((signed + 128L) >>> 8)))

/// `WavStreamDevice` with ears: the same drive and sample, and every frame
/// heard also pushed to the sink. Lives here rather than as a tap on the
/// library's device until the trial settles.
type AudibleWavDevice(sim: Sim, input: StreamPins, output: StreamPins, source: WavData, sink: AudioSink) =
    let mutable offered = 0
    let mutable accepted = false
    let mutable arrival: (uint64 * uint64) option = None
    let heard = ResizeArray<int16>(source.samples.Length)

    member _.FramesOffered = offered
    member _.Remaining = source.FrameCount - offered
    member _.Output: WavData = { source with samples = heard.ToArray() }

    interface ISimDevice with
        member _.Drive() =
            let more = offered < source.FrameCount
            sim.Poke(input.valid, (if more then 1UL else 0UL))

            if more then
                sim.Poke(input.fields[0], toBits source.samples[offered * 2])
                sim.Poke(input.fields[1], toBits source.samples[offered * 2 + 1])

            sim.Poke(output.ready, 1UL)
            accepted <- more && sim.Peek input.ready = 1UL

            arrival <-
                if sim.Peek output.valid = 1UL then
                    Some(sim.Peek output.fields[0], sim.Peek output.fields[1])
                else
                    None

        member _.Sample() =
            if accepted then
                offered <- offered + 1

            match arrival with
            | Some(left, right) ->
                let l, r = fromBits left, fromBits right
                heard.Add l
                heard.Add r
                sink.Push(l, r)
            | None -> ()

            accepted <- false
            arrival <- None

/// The handle a session is opened with, and read through afterwards.
type AudibleWavSource(input: StreamPins, output: StreamPins, source: WavData, sink: AudioSink) =
    let mutable device: AudibleWavDevice option = None

    member _.Attach: Sim -> ISimDevice =
        fun sim ->
            let d = AudibleWavDevice(sim, input, output, source, sink)
            device <- Some d
            d :> ISimDevice

    member _.Output = device |> Option.map (fun d -> d.Output)
    member _.FramesOffered = device |> Option.map (fun d -> d.FramesOffered) |> Option.defaultValue 0
    member _.Remaining = device |> Option.map (fun d -> d.Remaining) |> Option.defaultValue source.FrameCount
