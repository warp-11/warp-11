/// What satisfies a graph's boundary in a given world.
///
/// A graph declares what it needs — so many stream pins in and out, of such
/// formats, and these controls. It says nothing about where the beats come
/// from. A *mapping* says that, per world: in the simulator a WAV file plays
/// into the input box and what leaves the output box is a WAV file; on a
/// board the same boundary would be I2S pins. The graph is the same data in
/// both, which is the point of keeping the two apart.
///
/// Only the simulator's mapping exists yet.
module Warp11.Placement.Devices

open Warp11
open Warp11.Placement.Fu
open Warp11.Placement.Graph
open Warp11.Placement.Edit
open Warp11.Placement.Elaborate

/// The simulator's answer to a stereo boundary: a recording in, control
/// values held for the run, and where to put what came out.
type SimMapping =
    { source: WavData
      controls: (string * uint64) list
      /// Written when the run ends, if given; the result comes back either way.
      outputPath: string option }

/// What a stereo WAV device needs the boundary to be: one stream, two signed
/// sample pins each way.
let private stereo = [ "left", sint sampleWidth; "right", sint sampleWidth ]

let private describeFormat (f: NumberFormat) =
    let sign = if f.signed then "signed" else "unsigned"
    $"%d{f.totalWidth}w/%d{f.fracBits}f/{sign}"

let private describePins (pins: (string * NumberFormat) list) =
    pins |> List.map (fun (n, f) -> $"{n}: {describeFormat f}") |> String.concat ", "

/// Does the mapping fit what the graph declares? Said before anything runs,
/// naming the side that does not.
let private check (g: Graph) (m: SimMapping) =
    if g.streams <> 1 then
        failwith $"{g.name}: a WAV device plays one stream, and the graph declares %d{g.streams}"

    // A WAV's header holds a whole number of hertz; the design's rate need not
    // be one, so the two agree when they round to the same.
    if round g.sampleRate <> float m.source.sampleRate then
        failwith $"{g.name}: the design is made for %g{g.sampleRate} Hz and the recording is %d{m.source.sampleRate} Hz"

    for side, pins in [ "input", g.inputs; "output", g.outputs ] do
        if pins <> stereo then
            failwith $"{g.name}: a WAV device needs the {side} box to be [{describePins stereo}], and it is [{describePins pins}]"

    let ports = controlPorts g

    for name, _ in m.controls do
        if not (ports |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: no control called '{name}' — it has [{describePins ports}]"

    // The design's own controls come from outside, so the mapping must say;
    // a number box and an unwired inlet hold their own values.
    for name, _ in g.controls do
        if not (m.controls |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: the mapping gives no value for control '{name}'"

/// What the design's own control ports start at: each number box's value
/// and each unwired inlet's setting. The design's own controls are the
/// mapping's to give.
let startingValues (g: Graph) : (string * uint64) list =
    [ for c in g.controlBoxes do
          if c.kind = NumberBox then
              yield c.name, controlValueBits c
      for b, n, f in implicitControls g -> implicitPortName b.name n, settingBits b (n, f) ]

/// Play the mapping's recording through the graph's design in the simulator
/// and return what the output box heard. `idleLimit` bounds the wait for a
/// beat that never comes, so a patch that deadlocks fails rather than hangs.
let runInSim (idleLimit: int) (g: Graph) (m: SimMapping) : WavData =
    check g m
    let design = elaborate g
    let sim = Sim design.def

    for name, value in startingValues g @ m.controls do
        sim.Poke(name, value)

    let heard =
        runWavThroughStream sim (streamPins "in1" (lower (pinsOfList g.inputs))) (streamPins "out1" (lower (pinsOfList g.outputs))) idleLimit m.source

    m.outputPath |> Option.iter (fun path -> writeWavFile path heard)
    heard
