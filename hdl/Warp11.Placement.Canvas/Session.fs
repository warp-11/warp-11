/// Opening a patch live: the graph elaborated with probes, a recording on its
/// boundary, a debug session around it, and every probe on the watch list.
module Warp11.Placement.Canvas.Session

open Warp11
open Warp11.Debug
open Warp11.Fu
open Warp11.Graph
open Warp11.Elaborate
open Warp11.Placement.Canvas.FuncCanvas

/// A graph running in the simulator with a WAV playing into it. The session
/// runs on its own thread except in a browser, where the canvas pumps it.
let rec openWith (g: Graph) (recording: WavData) (controls: (string * uint64) list) (savePath: string option) (sink: AudioSink.AudioSink option) : Live =
    let design = elaborateWith true g

    // The recording into every stream; with a speaker, the first stream's
    // device is the one that also plays, the rest are the library's own
    // recording device. The canvas follows the first.
    let attach, view =
        let inPins, outPins = Warp11.Devices.streamPinsOf g 1

        match sink with
        | Some sink ->
            let src = AudioSink.AudibleWavSource(inPins, outPins, recording, sink)

            src.Attach,
            { framesOffered = fun () -> src.FramesOffered
              remaining = fun () -> src.Remaining
              heard = fun () -> src.Output }
        | None ->
            let src = WavStreamSource(inPins, outPins, recording)

            src.Attach,
            { framesOffered = fun () -> src.FramesOffered
              remaining = fun () -> src.Remaining
              heard = fun () -> src.Output }

    let others =
        [ for i in 2 .. g.streams ->
              let inPins, outPins = Warp11.Devices.streamPinsOf g i
              WavStreamSource(inPins, outPins, recording).Attach ]

    let session =
        new DebugSession(design.def, ownThread = not (System.OperatingSystem.IsBrowser()), devices = attach :: others) :> IDebugSession

    // Number boxes and unwired inlets start at their own values; then the
    // knobs — only the controls this graph has, since an edited design may
    // have lost one.
    let ports = controlPorts g

    for name, value in Warp11.Edit.startingValues g @ controls do
        if ports |> List.exists (fun (n, _) -> n = name) then
            session.Poke(name, System.Numerics.BigInteger value)

    for name in probeNames g do
        session.Watch name

    for i in 1 .. g.streams do
        for name, _ in g.inputs do
            session.Watch $"in%d{i}_{name}"

        for name, _ in g.outputs do
            session.Watch $"out%d{i}_{name}"

        session.Watch $"in%d{i}_valid"
        session.Watch $"out%d{i}_valid"

    for name, _ in controlPorts g do
        session.Watch name


    let inventory = session.Inventory
    let probes = set (probeNames g)

    { session = session
      recording = Some view
      audio = sink
      controls = g.controls
      signalsOf =
        fun box ->
            // The stage's own nets are `{box}{i}_…`, its instance's
            // `{box}{i}_{unit}_…`; the probes are listed with the pins instead.
            inventory.signals
            |> List.map (fun s -> s.name)
            |> List.filter (fun n -> n.StartsWith box && not (probes.Contains n) && not (n.StartsWith $"{box}_"))
            |> List.sort
      savePath = savePath
      framesPerSecond = recording.sampleRate
      reopen = fun g knobs -> openWith g recording knobs savePath sink }

/// The simulator's mapping for a WAV file: a recording in, what was heard
/// written beside it, and the speakers when there are any. What the canvas
/// calls to run the design it holds.
let wavOpenerOf (wavPath: string) (recording: WavData) (audible: bool) : Graph -> (string * uint64) list -> Live =
    let heardPath = System.IO.Path.ChangeExtension(wavPath, ".heard.wav")
    let sink = if audible then Some(AudioSink.AudioSink recording.sampleRate) else None
    fun g knobs -> openWith g recording knobs (Some heardPath) sink

let wavOpener (wavPath: string) (audible: bool) : Graph -> (string * uint64) list -> Live =
    wavOpenerOf wavPath (readWavFile wavPath) audible

/// The first design: `wav in → gain → wav out`, unity gain, unmuted.
let openGain (wavPath: string) (audible: bool) : Live =
    wavOpener wavPath audible gainGraph [ "volume", gainUnity; "mute", 0UL ]
