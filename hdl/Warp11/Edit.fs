/// Everything an editor can do to a design, as functions `Graph -> Graph`.
/// The canvas calls one and renders what comes back; a change that cannot
/// be made says why, naming the box or the pin, before anything is drawn.
/// Undo is a list of the graphs before each change.
module Warp11.Edit

open Warp11.Fu
open Warp11.Factories
open Warp11.Graph

type Position = float * float

let private boundary = [ "input"; "output" ]

/// A name a box or a pin may have: an identifier, and not a boundary box's.
let private checkName (what: string) (name: string) : Result<unit, string> =
    if name = "" then
        Error $"a {what} needs a name"
    elif not (System.Char.IsLetter name[0] || name[0] = '_') || not (name |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')) then
        Error $"'{name}': a {what}'s name is letters, digits and underscores, starting with a letter"
    elif List.contains name boundary then
        Error $"'{name}' is the design's own {name} box"
    else
        Ok()

let private hasBox (g: Graph) (name: string) =
    g.boxes |> List.exists (fun b -> b.name = name) || (controlBoxOf g name).IsSome

/// The name a new box of `unit` gets: the unit's own name when it is free,
/// else the unit's name and the first free number from 2.
let freshBoxName (g: Graph) (unit: string) : string =
    if not (hasBox g unit) then
        unit
    else
        Seq.initInfinite (fun i -> $"{unit}%d{i + 2}") |> Seq.find (fun n -> not (hasBox g n))

/// A box of a palette unit with the factory's default arguments, one copy,
/// placed at `at`. The box's name comes back with the graph so the caller
/// can select it.
let addBox (unit: string) (at: Position) (g: Graph) : Result<Graph * string, string> =
    let arguments =
        match palette.TryFind unit, g.designs.TryFind unit with
        | Some factory, _ -> Ok(defaults factory)
        | None, Some _ -> Ok Map.empty
        | None, None -> Error $"no unit called '{unit}' in the palette or among the design's own designs"

    arguments
    |> Result.map (fun arguments ->
        let name = freshBoxName g unit

        { g with
            boxes = g.boxes @ [ { name = name; unit = unit; copies = 1; arguments = arguments; settings = Map.empty } ]
            positions = g.positions |> Map.add name at },
        name)

/// The factory's verdict on a box as it would be: made for the rate with
/// these arguments, or refused naming the box and the parameter. A box that
/// is a design has no arguments to refuse.
let private checkBox (g: Graph) (rate: float) (b: Box) : Result<unit, string> =
    match palette.TryFind b.unit, g.designs.TryFind b.unit with
    | Some factory, _ -> factory.make rate (complete factory b.arguments) |> Result.map ignore |> Result.mapError (fun why -> $"{b.name}: {why}")
    | None, Some _ -> Ok()
    | None, None -> Error $"{b.name}: no unit called '{b.unit}'"

/// One creation argument of a box, as typed. The factory checks it before
/// the box holds it; a change is a new design.
let setArgument (name: string) (parameter: string) (value: string) (g: Graph) : Result<Graph, string> =
    match g.boxes |> List.tryFind (fun b -> b.name = name), g.boxes |> List.tryFind (fun b -> b.name = name) |> Option.bind (fun b -> palette.TryFind b.unit) with
    | None, _ -> Error $"no box called '{name}'"
    | Some _, None -> Error $"{name}: a design has no creation arguments — open it to change it"
    | Some b, Some factory ->
        if not (factory.parameters |> List.exists (fun p -> p.name = parameter)) then
            let names =
                match factory.parameters |> List.map (fun p -> p.name) with
                | [] -> "none"
                | ps -> String.concat ", " ps

            Error $"{name}: no parameter called '{parameter}' — {b.unit} takes {names}"
        else
            let changed = { b with arguments = b.arguments |> Map.add parameter value }

            checkBox g g.sampleRate changed
            |> Result.map (fun () -> { g with boxes = g.boxes |> List.map (fun x -> if x.name = name then changed else x) })

/// The rate the design is made for, and for every design inside it — a
/// design used as a box runs at its parent's rate. Every box is re-made for
/// it first, so a corner above the new rate's half refuses here, naming the
/// box.
let rec setSampleRate (rate: float) (g: Graph) : Result<Graph, string> =
    if rate <= 0.0 then
        Error $"a sample rate is above zero, not %g{rate}"
    else
        let boxes =
            g.boxes |> List.fold (fun acc b -> acc |> Result.bind (fun () -> checkBox g rate b)) (Ok())

        let designs =
            (Ok Map.empty, g.designs |> Map.toList)
            ||> List.fold (fun acc (name, sub) ->
                acc
                |> Result.bind (fun (m: Map<string, Graph>) ->
                    setSampleRate rate sub |> Result.mapError (fun why -> $"{name}: {why}") |> Result.map (fun sub -> Map.add name sub m)))

        match boxes, designs with
        | Ok(), Ok designs -> Ok { g with sampleRate = rate; designs = designs }
        | Error e, _
        | _, Error e -> Error e

/// A design brought in as a unit this design may place: made for this
/// design's rate, named so a box can name it. Refused when the palette or
/// this design already has the name.
let importDesign (sub: Graph) (g: Graph) : Result<Graph, string> =
    if palette.ContainsKey sub.name then
        Error $"'{sub.name}' is a unit in the palette"
    elif g.designs.ContainsKey sub.name then
        Error $"there is already a design called '{sub.name}' in this one"
    elif sub.name = g.name then
        Error $"a design cannot use itself"
    elif sub.streams <> 1 then
        Error $"'{sub.name}' has %d{sub.streams} streams, and a design used as a box has one"
    else
        setSampleRate g.sampleRate sub
        |> Result.mapError (fun why -> $"'{sub.name}' at %g{g.sampleRate} Hz: {why}")
        |> Result.map (fun sub -> { g with designs = g.designs |> Map.add sub.name sub })

/// A design this one uses, taken out again — refused while a box still is it.
let removeDesign (name: string) (g: Graph) : Result<Graph, string> =
    if not (g.designs.ContainsKey name) then
        Error $"no design called '{name}' in this one"
    else
        match g.boxes |> List.filter (fun b -> b.unit = name) with
        | [] -> Ok { g with designs = g.designs |> Map.remove name }
        | boxes ->
            let placed = boxes |> List.map (fun b -> b.name) |> String.concat ", "
            Error $"'{name}' is still placed: {placed}"

/// A change applied to the design a path of boxes leads to — `[]` is this
/// design, `[ "reverb" ]` the design the box `reverb` is — so an editor
/// drilled into a box edits the design behind it, and the parent is a new
/// design for it.
let rec atPath (path: string list) (change: Graph -> Result<Graph, string>) (g: Graph) : Result<Graph, string> =
    match path with
    | [] -> change g
    | box :: rest ->
        match g.boxes |> List.tryFind (fun b -> b.name = box) |> Option.bind (fun b -> g.designs.TryFind b.unit |> Option.map (fun sub -> b, sub)) with
        | None -> Error $"'{box}' is not a box that is a design"
        | Some(b, sub) ->
            atPath rest change sub
            |> Result.map (fun sub -> { g with designs = g.designs |> Map.add b.unit sub })

/// The design a path of boxes leads to.
let rec graphAt (path: string list) (g: Graph) : Graph option =
    match path with
    | [] -> Some g
    | box :: rest ->
        g.boxes
        |> List.tryFind (fun b -> b.name = box)
        |> Option.bind (fun b -> g.designs.TryFind b.unit)
        |> Option.bind (graphAt rest)

/// A box — a unit's or a control's — and every wire on it.
let removeBox (name: string) (g: Graph) : Result<Graph, string> =
    if not (hasBox g name) then
        Error $"no box called '{name}'"
    else
        Ok
            { g with
                boxes = g.boxes |> List.filter (fun b -> b.name <> name)
                controlBoxes = g.controlBoxes |> List.filter (fun c -> c.name <> name)
                edges = g.edges |> List.filter (fun e -> e.from.box <> name && e.``to``.box <> name)
                positions = g.positions |> Map.remove name }

/// The same box under a new name; its wires follow it.
let renameBox (name: string) (newName: string) (g: Graph) : Result<Graph, string> =
    if not (hasBox g name) then
        Error $"no box called '{name}'"
    elif newName = name then
        Ok g
    elif hasBox g newName then
        Error $"there is already a box called '{newName}'"
    else
        checkName "box" newName
        |> Result.map (fun () ->
            let follow (p: PinRef) = if p.box = name then { p with box = newName } else p

            { g with
                boxes = g.boxes |> List.map (fun b -> if b.name = name then { b with name = newName } else b)
                controlBoxes = g.controlBoxes |> List.map (fun c -> if c.name = name then { c with name = newName } else c)
                edges = g.edges |> List.map (fun e -> { from = follow e.from; ``to`` = follow e.``to`` })
                positions =
                    match g.positions |> Map.tryFind name with
                    | Some at -> g.positions |> Map.remove name |> Map.add newName at
                    | None -> g.positions })

/// Where a box sits on the canvas. The boundary boxes have positions too.
let moveBox (name: string) (at: Position) (g: Graph) : Graph =
    { g with positions = g.positions |> Map.add name at }

/// How many copies of its unit a box may spend.
let setCopies (name: string) (n: int) (g: Graph) : Result<Graph, string> =
    if not (hasBox g name) then
        Error $"no box called '{name}'"
    elif n < 1 then
        Error $"{name}: copies is at least 1, not %d{n}"
    else
        Ok { g with boxes = g.boxes |> List.map (fun b -> if b.name = name then { b with copies = n } else b) }

/// A second box of the same unit with the same copies, arguments and
/// settings, unwired — every input takes exactly one wire, so the copy
/// starts with none. A control box copies with its value.
let duplicateBox (name: string) (at: Position) (g: Graph) : Result<Graph * string, string> =
    match g.boxes |> List.tryFind (fun b -> b.name = name), controlBoxOf g name with
    | Some b, _ ->
        let copyName = freshBoxName g b.unit

        Ok(
            { g with
                boxes = g.boxes @ [ { b with name = copyName } ]
                positions = g.positions |> Map.add copyName at },
            copyName
        )
    | None, Some c ->
        let stem = (match c.kind with NumberBox -> "number" | ConstantBox -> "constant")
        let copyName = freshBoxName g stem

        Ok(
            { g with
                controlBoxes = g.controlBoxes @ [ { c with name = copyName } ]
                positions = g.positions |> Map.add copyName at },
            copyName
        )
    | None, None -> Error $"no box called '{name}'"

/// A value as a control's bits: a whole number that fits the format.
let private controlBits (what: string) (f: NumberFormat) (text: string) : Result<uint64, string> =
    match System.UInt64.TryParse text with
    | true, v when f.totalWidth >= 64 || v < (1UL <<< f.totalWidth) -> Ok v
    | true, v -> Error $"{what}: %d{v} does not fit %d{f.totalWidth} bits"
    | _ -> Error $"{what}: '{text}' is not a whole number"

/// The value a box's unwired control inlet holds. Checked against the
/// inlet's format; a wired inlet takes its value from the wire, not here.
let setSetting (name: string) (inlet: string) (value: string) (g: Graph) : Result<Graph, string> =
    match g.boxes |> List.tryFind (fun b -> b.name = name) with
    | None -> Error $"no box called '{name}'"
    | Some b ->
        match controlsOf g name |> List.tryFind (fun (n, _) -> n = inlet) with
        | None -> Error $"{name}: no control inlet called '{inlet}'"
        | Some(_, f) ->
            controlBits (showPin (pin name inlet)) f value
            |> Result.map (fun _ ->
                { g with boxes = g.boxes |> List.map (fun x -> if x.name = name then { b with settings = b.settings |> Map.add inlet value } else x) })

/// The setting's value as bits, or the format's zero when nothing was typed.
let settingBits (b: Box) (inlet: string, f: NumberFormat) : uint64 =
    match b.settings |> Map.tryFind inlet |> Option.map (controlBits inlet f) with
    | Some(Ok v) -> v
    | _ -> 0UL

/// A control box: a number box, a toggle (a one-bit number box) or a
/// constant, of a format, starting at a value.
let addControlBox (kind: ControlKind) (format: NumberFormat) (value: string) (at: Position) (g: Graph) : Result<Graph * string, string> =
    let stem =
        match kind, format.totalWidth with
        | NumberBox, 1 -> "toggle"
        | NumberBox, _ -> "number"
        | ConstantBox, _ -> "constant"

    let name = freshBoxName g stem

    controlBits name format value
    |> Result.map (fun _ ->
        { g with
            controlBoxes = g.controlBoxes @ [ { name = name; kind = kind; format = format; value = value } ]
            positions = g.positions |> Map.add name at },
        name)

/// A control box's value. For a number box this is where the port starts;
/// for a constant it is the design.
let setControlValue (name: string) (value: string) (g: Graph) : Result<Graph, string> =
    match controlBoxOf g name with
    | None -> Error $"no control box called '{name}'"
    | Some c ->
        controlBits name c.format value
        |> Result.map (fun _ -> { g with controlBoxes = g.controlBoxes |> List.map (fun x -> if x.name = name then { c with value = value } else x) })

let controlValueBits (c: ControlBox) : uint64 =
    match controlBits c.name c.format c.value with
    | Ok v -> v
    | Error _ -> 0UL

/// What the design's own control ports start at: each number box's value
/// and each unwired inlet's setting — and for an inlet of a box that is a
/// design, nothing typed here means the value that design starts its own
/// port at. The design's own controls are the mapping's to give.
let rec startingValues (g: Graph) : (string * uint64) list =
    [ for c in g.controlBoxes do
          if c.kind = NumberBox then
              yield c.name, controlValueBits c
      for b, n, f in implicitControls g ->
          let value =
              match b.settings |> Map.tryFind n, designOf g b with
              | Some _, _ -> settingBits b (n, f)
              | None, Some sub -> startingValues sub |> List.tryFind (fun (port, _) -> port = n) |> Option.map snd |> Option.defaultValue 0UL
              | None, None -> 0UL

          implicitPortName b.name n, value ]

/// A wire between two pins, either end first: the edge always runs from
/// the output to the input. Refused, naming the pin, when the ends are the
/// same side, the same box, different kinds or formats, or the input
/// already has its one wire — everything the elaborator would refuse,
/// said before the wire exists.
let addWire (a: PinRef, sideA: Side) (b: PinRef, sideB: Side) (g: Graph) : Result<Graph, string> =
    if sideA = sideB then
        Error(if sideA = In then "both pins are inputs" else "both pins are outputs")
    elif a.box = b.box then
        Error $"{a.box}: a box cannot feed itself"
    else
        let src, dst = if sideA = In then b, a else a, b

        match lookupPin g Out src, lookupPin g In dst with
        | Error why, _
        | _, Error why -> Error why
        | Ok(ka, fa), Ok(kb, fb) ->
            let kindsAgree =
                match ka, kb with
                | SignalOut, SignalIn
                | ControlOut, ControlIn -> true
                | _ -> false

            let kindName k =
                match k with
                | SignalIn
                | SignalOut -> "signal"
                | ControlIn
                | ControlOut -> "control"

            if not kindsAgree then
                Error $"{showPin src} is a {kindName ka}, {showPin dst} takes a {kindName kb}"
            elif fa <> fb then
                Error $"{showPin src} is {describeFormat fa}, {showPin dst} is {describeFormat fb}"
            elif g.edges |> List.exists (fun e -> e.``to`` = dst) then
                Error $"{showPin dst} already has a wire"
            else
                Ok { g with edges = g.edges @ [ { from = src; ``to`` = dst } ] }

let removeWire (e: Edge) (g: Graph) : Graph =
    { g with edges = g.edges |> List.filter (fun x -> x <> e) }

/// The design's boundary: a signal pin in, a control, a signal pin out. An
/// input signal and a control share the input box, so they cannot share a
/// name; an output may be called what an input is, as `left` is.
let private addTo (what: string) (taken: string list) (name: string, format: NumberFormat) (pins: (string * NumberFormat) list) =
    checkName what name
    |> Result.bind (fun () ->
        if List.contains name taken then
            Error $"there is already a pin called '{name}' on that box"
        else
            Ok(pins @ [ name, format ]))

let addInputPin (name: string, format: NumberFormat) (g: Graph) : Result<Graph, string> =
    addTo "pin" (List.map fst (g.inputs @ g.controls)) (name, format) g.inputs
    |> Result.map (fun inputs -> { g with inputs = inputs })

let addControl (name: string, format: NumberFormat) (g: Graph) : Result<Graph, string> =
    addTo "control" (List.map fst (g.inputs @ g.controls)) (name, format) g.controls
    |> Result.map (fun controls -> { g with controls = controls })

let addOutputPin (name: string, format: NumberFormat) (g: Graph) : Result<Graph, string> =
    addTo "pin" (List.map fst g.outputs) (name, format) g.outputs
    |> Result.map (fun outputs -> { g with outputs = outputs })

/// A boundary pin, and the wires from or to it.
let removeBoundaryPin (p: PinRef) (g: Graph) : Result<Graph, string> =
    let without pins = pins |> List.filter (fun (n, _) -> n <> p.pin)
    let unwired = g.edges |> List.filter (fun e -> e.from <> p && e.``to`` <> p)

    match p.box with
    | "input" when g.inputs |> List.exists (fun (n, _) -> n = p.pin) -> Ok { g with inputs = without g.inputs; edges = unwired }
    | "input" when g.controls |> List.exists (fun (n, _) -> n = p.pin) -> Ok { g with controls = without g.controls; edges = unwired }
    | "output" when g.outputs |> List.exists (fun (n, _) -> n = p.pin) -> Ok { g with outputs = without g.outputs; edges = unwired }
    | "input"
    | "output" -> Error $"{showPin p}: no such pin"
    | _ -> Error $"{p.box}: a box's pins are its unit's; only the design's own pins are added and removed"

let rename (name: string) (g: Graph) : Result<Graph, string> =
    checkName "design" name |> Result.map (fun () -> { g with name = name })

// ---------------------------------------------------------------------------
// Undo.

/// The design and its history: every graph before the present one, and
/// after an undo, the ones ahead.
type History =
    { past: Graph list
      present: Graph
      future: Graph list }

let history (g: Graph) : History = { past = []; present = g; future = [] }

/// A change applied: the new present, the old one remembered, and nothing
/// ahead any more. A change that is refused leaves the history as it was
/// and hands the reason back.
let apply (change: Graph -> Result<Graph, string>) (h: History) : History * string option =
    match change h.present with
    | Ok g when g = h.present -> h, None
    | Ok g -> { past = h.present :: h.past; present = g; future = [] }, None
    | Error why -> h, Some why

/// A change that cannot be refused.
let applyAlways (change: Graph -> Graph) (h: History) : History = fst (apply (change >> Ok) h)

let undo (h: History) : History =
    match h.past with
    | [] -> h
    | g :: rest -> { past = rest; present = g; future = h.present :: h.future }

let redo (h: History) : History =
    match h.future with
    | [] -> h
    | g :: rest -> { past = h.present :: h.past; present = g; future = rest }
