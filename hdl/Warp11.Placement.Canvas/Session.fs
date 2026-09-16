/// Opening a patch live: the graph elaborated with probes, a recording on its
/// boundary, a debug session around it, and every probe on the watch list.
module Warp11.Placement.Canvas.Session

open Warp11
open Warp11.Debug
open Warp11.Fu
open Warp11.Graph
open Warp11.Elaborate
open Warp11.Placement.Canvas.FuncCanvas

/// What a mapping puts on a design's boundary in a session: a device per
/// stream, a view of its progress, and where what came out is saved.
type Source =
    { attach: (Sim -> ISimDevice) list
      framesOffered: unit -> int
      remaining: unit -> int
      /// Save what the first stream heard to `path`; the message to show.
      save: string -> string
      /// Beats a second at real time, for Play.
      framesPerSecond: int
      /// The speaker, when the source is a recording and there is one.
      audio: AudioSink.AudioSink option }

/// A recording into every stream; with a speaker, the first stream's device
/// is the one that also plays.
let wavSource (g: Graph) (recording: WavData) (audible: bool) : Source =
    let sink = if audible then Some(AudioSink.AudioSink recording.sampleRate) else None
    let inPins, outPins = Warp11.Devices.streamPinsOf g 1

    let first, offered, remaining, heard =
        match sink with
        | Some sink ->
            let src = AudioSink.AudibleWavSource(inPins, outPins, recording, sink)
            src.Attach, (fun () -> src.FramesOffered), (fun () -> src.Remaining), (fun () -> src.Output)
        | None ->
            let src = WavStreamSource(inPins, outPins, recording)
            src.Attach, (fun () -> src.FramesOffered), (fun () -> src.Remaining), (fun () -> src.Output)

    let others =
        [ for i in 2 .. g.streams ->
              let inPins, outPins = Warp11.Devices.streamPinsOf g i
              WavStreamSource(inPins, outPins, recording).Attach ]

    { attach = first :: others
      framesOffered = offered
      remaining = remaining
      save =
        fun path ->
            match heard () with
            | Some w ->
                writeWavFile path w
                $"wrote {path}: %d{w.FrameCount} frames"
            | None -> "nothing heard yet"
      framesPerSecond = recording.sampleRate
      audio = sink }

/// Beats from a list into every stream — an image's rows or a table's —
/// saved by `write` from what the first stream heard.
let private beatSource (g: Graph) (beats: System.Numerics.BigInteger list list) (wanted: int) (write: string -> System.Numerics.BigInteger list list -> string) : Source =
    let devices = System.Collections.Generic.List<Warp11.Devices.BeatStreamDevice>()

    let attach i =
        fun (sim: Sim) ->
            let input, output = Warp11.Devices.streamPinsOf g i
            let d = Warp11.Devices.BeatStreamDevice(sim, input, output, beats, wanted)
            devices.Add d
            d :> ISimDevice

    { attach = [ for i in 1 .. g.streams -> attach i ]
      framesOffered = fun () -> if devices.Count = 0 then 0 else devices[0].FramesOffered
      remaining = fun () -> if devices.Count = 0 then beats.Length else devices[0].Remaining
      save = fun path -> if devices.Count = 0 then "nothing heard yet" else write path devices[0].Heard
      // A row a millisecond: a frame in the time it takes to look at it.
      framesPerSecond = 1000
      audio = None }

/// A grey image into the design's rows; what comes out is saved as a PGM.
let imageSource (g: Graph) (image: Grey) : Source =
    let m: Warp11.Devices.ImageMapping = { image = image; outputPath = None }

    beatSource g (Warp11.Devices.imageBeats m) image.height (fun path heard ->
        let out = ofRows image.width (heard |> List.truncate image.height |> List.map List.head)
        writePgm path out
        $"wrote {path}: %d{out.height} rows")

/// A table into the design, a row a beat, columns by pin name; what comes
/// out is saved as a CSV of the output pins.
let csvSource (g: Graph) (table: Table) : Source =
    beatSource g (Warp11.Devices.csvBeats g table) table.rows.Length (fun path heard ->
        let out =
            { columns = g.outputs |> List.map fst
              rows = heard |> List.map (fun beat -> List.map2 (fun (_, f) bits -> formatCell f bits) g.outputs beat) }

        writeCsv path out
        $"wrote {path}: %d{out.rows.Length} rows")

/// A graph running in the simulator with a source on its boundary. The
/// session runs on its own thread except in a browser, where the canvas
/// pumps it.
let rec openWith (g: Graph) (source: Graph -> Source) (controls: (string * uint64) list) (savePath: string option) : Live =
    let design = elaborateWith true g
    let src = source g

    let session =
        new DebugSession(design.def, ownThread = not (System.OperatingSystem.IsBrowser()), devices = src.attach) :> IDebugSession

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
      recording =
        Some
            { framesOffered = src.framesOffered
              remaining = src.remaining
              save = src.save }
      audio = src.audio
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
      framesPerSecond = src.framesPerSecond
      reopen = fun g knobs -> openWith g source knobs savePath }

/// The source a file is, by its extension: a recording, an image or a
/// table. What `-- edit design.json <file>` opens with.
let sourceOf (path: string) (audible: bool) : (Graph -> Source) * string =
    let heardPath (ext: string) = System.IO.Path.ChangeExtension(path, $".heard{ext}")

    match System.IO.Path.GetExtension(path).ToLowerInvariant() with
    | ".wav" ->
        let recording = readWavFile path
        (fun g -> wavSource g recording audible), heardPath ".wav"
    | ".pgm" ->
        let image = readPgm path
        (fun g -> imageSource g image), heardPath ".pgm"
    | ".csv" ->
        let table = readCsv path
        (fun g -> csvSource g table), heardPath ".csv"
    | other -> failwith $"{path}: a source is a .wav, a .pgm or a .csv, not '{other}'"

/// The rate a new design opened with a file is made for: a recording's,
/// or the default for anything without one.
let rateOf (path: string) : float =
    match System.IO.Path.GetExtension(path).ToLowerInvariant() with
    | ".wav" -> float (readWavFile path).sampleRate
    | _ -> defaultSampleRate

/// The first design: `wav in → gain → wav out`, unity gain, unmuted.
let openGain (wavPath: string) (audible: bool) : Live =
    let source, heard = sourceOf wavPath audible
    openWith gainGraph source [ "volume", gainUnity; "mute", 0UL ] (Some heard)
