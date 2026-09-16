/// The example designs, built by the same edits a canvas makes, laid out,
/// and written as files by `-- examples <dir>` — so an example a person
/// opens is exactly a design the checks prove. Open one with
/// `Warp11.Placement.Canvas -- edit <file> <recording.wav>`.
module Warp11.Placement.Examples

open Warp11
open Warp11.Fu
open Warp11.Graph
open Warp11.Edit
open Warp11.Placement.Placement

/// The rate the recordings beside `Warp11.Effects` are at.
let recordingRate = 48_828.0

let private step (change: Graph -> Result<Graph, string>) (h: History) =
    match apply change h with
    | h, None -> h
    | _, Some why -> failwith why

let private wire (a: PinRef) (b: PinRef) = addWire (a, Out) (b, In)

/// Boxes in a row, left to right, in the order given.
let private inARow (names: string list) (g: Graph) : Graph =
    (g, List.indexed names) ||> List.fold (fun g (i, name) -> moveBox name (60.0 + float i * 260.0, 120.0) g)

/// A stereo boundary at the rate: the start of every audio design here.
let private stereoDesign (name: string) (rate: float) : History =
    history (emptyGraph name rate)
    |> step (addInputPin stereoPins.fields[0])
    |> step (addInputPin stereoPins.fields[1])
    |> step (addOutputPin stereoPins.fields[0])
    |> step (addOutputPin stereoPins.fields[1])

/// M1's design: a stereo stream through `gain`, volume and mute the
/// design's own controls.
let gain (rate: float) : Graph =
    { gainGraph with sampleRate = rate } |> inARow [ "input"; "gain"; "output" ]

/// M2's design: three `eq` sections with creation arguments — a low shelf,
/// a peaking cut, a high shelf — designed for the rate.
let threeBandEq (rate: float) : Graph =
    let band (name: string) (shape: string) (fc: string) (gain: string) (h: History) =
        h
        |> step (addBox "eq" (0.0, 0.0) >> Result.map fst)
        |> step (renameBox "eq" name)
        |> step (setArgument name "shape" shape)
        |> step (setArgument name "fc" fc)
        |> step (setArgument name "gain" gain)

    (stereoDesign "ThreeBandEq" rate
     |> band "low" "lowshelf" "200" "6"
     |> band "mid" "peaking" "1000" "-4"
     |> band "high" "highshelf" "5000" "3"
     |> step (wire (pin "input" "left") (pin "low" "left"))
     |> step (wire (pin "input" "right") (pin "low" "right"))
     |> step (wire (pin "low" "left") (pin "mid" "left"))
     |> step (wire (pin "low" "right") (pin "mid" "right"))
     |> step (wire (pin "mid" "left") (pin "high" "left"))
     |> step (wire (pin "mid" "right") (pin "high" "right"))
     |> step (wire (pin "high" "left") (pin "output" "left"))
     |> step (wire (pin "high" "right") (pin "output" "right")))
        .present
    |> inARow [ "input"; "low"; "mid"; "high"; "output" ]

/// The second half of M2: a gain whose volume is an unwired inlet holding a
/// setting, and a toggle box on its mute.
let controls (rate: float) : Graph =
    (history (gain rate)
     |> step (rename "Controls")
     |> step (removeBoundaryPin (pin "input" "volume"))
     |> step (removeBoundaryPin (pin "input" "mute"))
     |> step (setSetting "gain" "volume" "256")
     |> step (addControlBox NumberBox (unsignedInt 1) "0" (320.0, 320.0) >> Result.map fst)
     |> step (wire (pin "toggle" controlOutlet) (pin "gain" "mute")))
        .present

/// M4's design: the three-band EQ imported and placed twice in series — a
/// design as a box, to double-click into.
let twice (rate: float) : Graph =
    (stereoDesign "Twice" rate
     |> step (importDesign (threeBandEq rate))
     |> step (addBox "ThreeBandEq" (0.0, 0.0) >> Result.map fst)
     |> step (addBox "ThreeBandEq" (0.0, 0.0) >> Result.map fst)
     |> step (wire (pin "input" "left") (pin "ThreeBandEq" "left"))
     |> step (wire (pin "input" "right") (pin "ThreeBandEq" "right"))
     |> step (wire (pin "ThreeBandEq" "left") (pin "ThreeBandEq2" "left"))
     |> step (wire (pin "ThreeBandEq" "right") (pin "ThreeBandEq2" "right"))
     |> step (wire (pin "ThreeBandEq2" "left") (pin "output" "left"))
     |> step (wire (pin "ThreeBandEq2" "right") (pin "output" "right")))
        .present
    |> inARow [ "input"; "ThreeBandEq"; "ThreeBandEq2"; "output" ]

/// Every example, with the file it is written to.
let all (rate: float) : (string * Graph) list =
    [ "gain.json", gain rate
      "three-band-eq.json", threeBandEq rate
      "controls.json", controls rate
      "twice.json", twice rate ]

/// Write every example into `dir`.
let write (dir: string) =
    System.IO.Directory.CreateDirectory dir |> ignore

    for file, g in all recordingRate do
        Warp11.DesignFile.save (System.IO.Path.Combine(dir, file)) g
