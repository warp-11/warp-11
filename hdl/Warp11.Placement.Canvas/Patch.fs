/// Opening a patch live: the graph elaborated with probes, a recording on its
/// boundary, a debug session around it, and every probe on the watch list.
module Warp11.Placement.Canvas.Patch

open Warp11
open Warp11.Debug
open Warp11.Placement.Fu
open Warp11.Placement.Graph
open Warp11.Placement.Elaborate
open Warp11.Placement.Canvas.FuncCanvas

/// A graph running in the simulator with a WAV playing into it. The session
/// runs on its own thread except in a browser, where the canvas pumps it.
let rec openWith (g: Graph) (recording: WavData) (controls: (string * uint64) list) (savePath: string option) : Live =
    let design = elaborateWith true g

    let source =
        WavStreamSource(streamPins "in1" (lower (pinsOfList g.inputs)), streamPins "out1" (lower (pinsOfList g.outputs)), recording)

    let session =
        new DebugSession(design.def, ownThread = not (System.OperatingSystem.IsBrowser()), devices = [ source.Attach ]) :> IDebugSession

    for name, value in controls do
        session.Poke(name, System.Numerics.BigInteger value)

    for name in probeNames g do
        session.Watch name

    for name, _ in g.inputs do
        session.Watch $"in1_{name}"

    for name, _ in g.outputs do
        session.Watch $"out1_{name}"

    session.Watch "in1_valid"
    session.Watch "out1_valid"

    for name, _ in g.controls do
        session.Watch name

    let inventory = session.Inventory
    let probes = set (probeNames g)

    { session = session
      source = Some source
      controls = g.controls
      probeOf = probeName g
      validOf = validName g
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
      reopen = fun () -> openWith g recording controls savePath }

/// The first patch: `wav in → gain → wav out`, unity gain, unmuted.
let openGain (wavPath: string) : Live =
    let heardPath = System.IO.Path.ChangeExtension(wavPath, ".heard.wav")
    openWith gainGraph (readWavFile wavPath) [ "volume", gainUnity; "mute", 0UL ] (Some heardPath)
