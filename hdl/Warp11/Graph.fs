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
module Warp11.Graph

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Factories

/// One box: a factory by name, the creation arguments typed into it, how
/// many copies, and the **settings** — the value each control inlet nobody
/// wired holds, as typed. An unwired control inlet is not an error: the
/// elaborator gives it a port of its own, `{box}_{pin}`, and the setting is
/// what that port is poked with until something else drives it.
type Box =
    { name: string
      unit: string
      copies: int
      arguments: Arguments
      settings: Map<string, string> }

/// A control box: Pure Data's number box, toggle and message box. A source
/// of one control value, with no beat through it. A **number** box is a port
/// of the design named after the box — the simulator pokes it live, a board
/// mapping makes it a register; a **constant** is baked in as a literal. A
/// toggle is a number box one bit wide, drawn as a switch.
type ControlKind =
    | NumberBox
    | ConstantBox

type ControlBox =
    { name: string
      kind: ControlKind
      format: NumberFormat
      /// The value as typed: what the port starts at, or what the literal is.
      value: string }

/// The one outlet every control box has.
let controlOutlet = "out"

/// A pin on a box — or on the design's own boundary, where the box is
/// `"input"` (its signal inputs and controls, as sources) or `"output"`.
type PinRef = { box: string; pin: string }

/// One wire. Every signal input pin of every box has exactly one; the
/// design's output pins likewise; every control pin exactly one.
type Edge = { from: PinRef; ``to``: PinRef }

/// The design: its own pins, the boxes, the wires, the sample rate it is
/// made for, and where a canvas last put each box.
///
/// The rate is the design's because a filter's coefficients fix a frequency
/// in cycles per *sample*: a box designed for 1 kHz holds that only at the
/// rate it was designed for, so the rate is recorded beside the frequencies
/// that depend on it, as `FirBands` records it beside its cutoffs. A mapping
/// checks its own rate against this one and refuses a mismatch, as it
/// refuses a pin format.
///
/// Positions are part of the design so a saved design opens as it was left;
/// the elaborator never reads them, so a graph with none — every graph
/// written in F# — elaborates to the same bytes as the same graph laid out
/// by hand.
type Graph =
    { name: string
      streams: int
      sampleRate: float
      inputs: (string * NumberFormat) list
      controls: (string * NumberFormat) list
      outputs: (string * NumberFormat) list
      boxes: Box list
      controlBoxes: ControlBox list
      edges: Edge list
      positions: Map<string, float * float> }

let pin (box: string) (pin: string) : PinRef = { box = box; pin = pin }

/// The rate a design is made for until a mapping says otherwise: the studio
/// rate a WAV from the desk is at. A board design takes its board's.
let defaultSampleRate = 48_000.0

/// A design with nothing in it yet: no boundary pins, no boxes.
let emptyGraph (name: string) (sampleRate: float) : Graph =
    { name = name
      streams = 1
      sampleRate = sampleRate
      inputs = []
      controls = []
      outputs = []
      boxes = []
      controlBoxes = []
      edges = []
      positions = Map.empty }

/// Which side of a box a pin is on. A box may call an input and an output by
/// the same name (`gain` has `left` on both sides), so wherever a pin is
/// named on its own rather than as a wire's end, it carries its side.
type Side =
    | In
    | Out

/// `mac` as a graph. This is the whole design, as data.
let macGraph (streams: int) (multipliers: int) (adders: int) : Graph =
    { name = $"Mac%d{streams}s%d{multipliers}x%d{adders}"
      streams = streams
      sampleRate = defaultSampleRate
      inputs = [ "a", unsignedInt 16; "b", unsignedInt 16; "c", unsignedInt 32 ]
      controls = []
      outputs = [ "out", unsignedInt 33 ]
      boxes =
        [ { name = "product"; unit = "mul16"; copies = multipliers; arguments = Map.empty; settings = Map.empty }
          { name = "sum"; unit = "add32"; copies = adders; arguments = Map.empty; settings = Map.empty } ]
      controlBoxes = []
      edges =
        [ { from = pin "input" "a"; ``to`` = pin "product" "a" }
          { from = pin "input" "b"; ``to`` = pin "product" "b" }
          { from = pin "product" "product"; ``to`` = pin "sum" "x" }
          // Skips `product`: the elaborator holds `c` while the multiply is out.
          { from = pin "input" "c"; ``to`` = pin "sum" "y" }
          // The rename: the box's `sum` is the design's `out`.
          { from = pin "sum" "sum"; ``to`` = pin "output" "out" } ]
      positions = Map.empty }

/// The first patch: a stereo stream through `gain`, volume and mute from the
/// design's controls.
let gainGraph: Graph =
    let stereo = [ "left", signedInt sampleWidth; "right", signedInt sampleWidth ]

    { name = "GainPatch"
      streams = 1
      sampleRate = defaultSampleRate
      inputs = stereo
      controls = [ "volume", unsignedInt 16; "mute", unsignedInt 1 ]
      outputs = stereo
      boxes = [ { name = "gain"; unit = "gain"; copies = 1; arguments = Map.empty; settings = Map.empty } ]
      controlBoxes = []
      edges =
        [ { from = pin "input" "left"; ``to`` = pin "gain" "left" }
          { from = pin "input" "right"; ``to`` = pin "gain" "right" }
          { from = pin "input" "volume"; ``to`` = pin "gain" "volume" }
          { from = pin "input" "mute"; ``to`` = pin "gain" "mute" }
          { from = pin "gain" "left"; ``to`` = pin "output" "left" }
          { from = pin "gain" "right"; ``to`` = pin "output" "right" } ]
      positions = Map.empty }

let private boxOf (g: Graph) (name: string) = g.boxes |> List.tryFind (fun b -> b.name = name)

/// The unit a box is, made by its factory for the design's rate: arguments
/// the box does not state are the factory's defaults. A box whose arguments
/// the factory refuses cannot be elaborated, and says which one.
let unitOf (g: Graph) (b: Box) : ErasedFu =
    let factory = palette[b.unit]

    match factory.make g.sampleRate (complete factory b.arguments) with
    | Ok unit -> unit
    | Error why -> failwith $"{b.name}: {why}"

/// A box's signal pins, inputs and outputs, from the palette — or the design's
/// own boundary boxes, whose pins are the design's.
let pinsOf (g: Graph) (box: string) : (string * NumberFormat) list * (string * NumberFormat) list =
    match box with
    | "input" -> [], g.inputs
    | "output" -> g.outputs, []
    | name ->
        match boxOf g name with
        | Some b ->
            let unit = unitOf g b
            unit.operands.fields, unit.results.fields
        | None -> [], []

/// A box's control pins: sinks on a box, sources on the `input` box.
let controlsOf (g: Graph) (box: string) : (string * NumberFormat) list =
    match box with
    | "input" -> g.controls
    | "output" -> []
    | name ->
        match boxOf g name with
        | Some b -> (unitOf g b).controls
        | None -> []

/// What a pin is: a field of the beat in or out of a box, or a control held
/// beside it, sourced from the design's own controls.
type PinKind =
    | SignalIn
    | SignalOut
    | ControlIn
    | ControlOut

let showPin (p: PinRef) = $"{p.box}.{p.pin}"

let controlBoxOf (g: Graph) (name: string) = g.controlBoxes |> List.tryFind (fun c -> c.name = name)

let boxExists (g: Graph) (name: string) =
    name = "input"
    || name = "output"
    || g.boxes |> List.exists (fun b -> b.name = name)
    || (controlBoxOf g name).IsSome

/// A box's control inlets nobody wired, and the port each gets.
let implicitControls (g: Graph) : (Box * string * NumberFormat) list =
    [ for b in g.boxes do
          for n, f in controlsOf g b.name do
              if not (g.edges |> List.exists (fun e -> e.``to`` = pin b.name n)) then
                  yield b, n, f ]

let implicitPortName (box: string) (pin: string) = $"{box}_{pin}"

/// What a pin is, or why it is not. A wire's `from` end is looked up among a
/// box's outputs and its `to` end among its inputs — a box may call an input
/// and an output by the same name, as `gain` calls both `left`.
let lookupPin (g: Graph) (side: Side) (p: PinRef) : Result<PinKind * NumberFormat, string> =
    if not (boxExists g p.box) then
        Error $"{showPin p}: no such box"
    else
        let signalIns, signalOuts = pinsOf g p.box
        let controls = controlsOf g p.box
        let find pins = pins |> List.tryFind (fun (n, _) -> n = p.pin) |> Option.map snd

        match side, controlBoxOf g p.box with
        | Out, Some c when p.pin = controlOutlet -> Ok(ControlOut, c.format)
        | Out, Some _ -> Error $"{showPin p}: a control box's one outlet is '{controlOutlet}'"
        | In, Some _ -> Error $"{showPin p}: a control box has no inlets"
        | Out, None ->
            match find signalOuts, (if p.box = "input" then find controls else None) with
            | Some f, _ -> Ok(SignalOut, f)
            | _, Some f -> Ok(ControlOut, f)
            | _ -> Error $"{showPin p}: no such output pin"
        | In, None ->
            match find signalIns, (if p.box = "input" then None else find controls) with
            | Some f, _ -> Ok(SignalIn, f)
            | _, Some f -> Ok(ControlIn, f)
            | _ -> Error $"{showPin p}: no such input pin"
