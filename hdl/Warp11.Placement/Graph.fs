/// The design as data: what a GUI holds, and the seam a canvas is swapped
/// behind. A graph, not a chain — boxes with pins, and wires between pins.
/// A field that used to be "carried through" a box is a wire that skips it; a
/// rename is a wire onto the design's own output pin.
///
/// Two kinds of pin, as Pure Data has two kinds of inlet: **signal** pins
/// carry a field of the beat, **control** pins hold a value across beats. A
/// wire joins two pins of the same kind.
///
/// No reflection: a unit's pins come from its `Pins` witness, hand-written
/// once per shape and read here as data. The `'p` type parameter is the
/// compile-time face of the same information.
module Warp11.Placement.Graph

open Warp11
open Warp11.Placement.Fu
open Warp11.Placement.Units
open Warp11.Placement.Placement

/// One box: a unit by name, and how many copies.
type Box = { name: string; unit: string; copies: int }

/// A pin on a box — or on the design's own boundary, where the box is
/// `"input"` (its signal inputs and controls, as sources) or `"output"`.
type PinRef = { box: string; pin: string }

/// One wire. Every signal input pin of every box has exactly one; the
/// design's output pins likewise; every control pin exactly one.
type Edge = { from: PinRef; ``to``: PinRef }

/// The design: its own pins, the boxes, the wires.
type Graph =
    { name: string
      streams: int
      inputs: (string * NumberFormat) list
      controls: (string * NumberFormat) list
      outputs: (string * NumberFormat) list
      boxes: Box list
      edges: Edge list }

let pin (box: string) (pin: string) : PinRef = { box = box; pin = pin }

/// Which side of a box a pin is on. A box may call an input and an output by
/// the same name (`gain` has `left` on both sides), so wherever a pin is
/// named on its own rather than as a wire's end, it carries its side.
type Side =
    | In
    | Out

/// Every unit the GUI may offer, erased once. `erase` is the only way in.
let palette: Map<string, ErasedFu> =
    [ erase multiply16; erase add32; erase shiftAddMultiply16; erase gainModule ]
    |> List.map (fun u -> u.name, u)
    |> Map.ofList

/// `mac` as a graph. This is the whole design, as data.
let macGraph (streams: int) (multipliers: int) (adders: int) : Graph =
    { name = $"Mac%d{streams}s%d{multipliers}x%d{adders}"
      streams = streams
      inputs = [ "a", uint 16; "b", uint 16; "c", uint 32 ]
      controls = []
      outputs = [ "out", uint 33 ]
      boxes =
        [ { name = "product"; unit = "mul16"; copies = multipliers }
          { name = "sum"; unit = "add32"; copies = adders } ]
      edges =
        [ { from = pin "input" "a"; ``to`` = pin "product" "a" }
          { from = pin "input" "b"; ``to`` = pin "product" "b" }
          { from = pin "product" "product"; ``to`` = pin "sum" "x" }
          // Skips `product`: the elaborator holds `c` while the multiply is out.
          { from = pin "input" "c"; ``to`` = pin "sum" "y" }
          // The rename: the box's `sum` is the design's `out`.
          { from = pin "sum" "sum"; ``to`` = pin "output" "out" } ] }

/// The first patch: a stereo stream through `gain`, volume and mute from the
/// design's controls.
let gainGraph: Graph =
    let stereo = [ "left", sint sampleWidth; "right", sint sampleWidth ]

    { name = "GainPatch"
      streams = 1
      inputs = stereo
      controls = [ "volume", uint 16; "mute", uint 1 ]
      outputs = stereo
      boxes = [ { name = "gain"; unit = "gain"; copies = 1 } ]
      edges =
        [ { from = pin "input" "left"; ``to`` = pin "gain" "left" }
          { from = pin "input" "right"; ``to`` = pin "gain" "right" }
          { from = pin "input" "volume"; ``to`` = pin "gain" "volume" }
          { from = pin "input" "mute"; ``to`` = pin "gain" "mute" }
          { from = pin "gain" "left"; ``to`` = pin "output" "left" }
          { from = pin "gain" "right"; ``to`` = pin "output" "right" } ] }

let private boxOf (g: Graph) (name: string) = g.boxes |> List.tryFind (fun b -> b.name = name)

/// A box's signal pins, inputs and outputs, from the palette — or the design's
/// own boundary boxes, whose pins are the design's.
let pinsOf (g: Graph) (box: string) : (string * NumberFormat) list * (string * NumberFormat) list =
    match box with
    | "input" -> [], g.inputs
    | "output" -> g.outputs, []
    | name ->
        match boxOf g name with
        | Some b -> palette[b.unit].operands.pins, palette[b.unit].results.pins
        | None -> [], []

/// A box's control pins: sinks on a box, sources on the `input` box.
let controlsOf (g: Graph) (box: string) : (string * NumberFormat) list =
    match box with
    | "input" -> g.controls
    | "output" -> []
    | name ->
        match boxOf g name with
        | Some b -> palette[b.unit].controls
        | None -> []
