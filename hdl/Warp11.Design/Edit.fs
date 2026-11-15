/// Everything an editor can do to a design, as functions `Graph -> Graph`.
/// The canvas calls one and renders what comes back; a change that cannot
/// be made says why, naming the box or the port, before anything is drawn.
/// Undo is a list of the graphs before each change.
module Warp11.Edit

open Warp11.Fu
open Warp11.Factories
open Warp11.Graph

type Position = float * float

let private boundary = [ "input"; "output" ]

/// A name a box, a stream or a control may have: an identifier, and not a boundary box's.
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

/// A box of a palette unit with the factory's default arguments, placed at
/// `at`. The box's name comes back with the graph so the caller
/// can select it.
let addBox (unit: string) (at: Position) (g: Graph) : Result<Graph * string, string> =
    let arguments =
        match (units ()).TryFind unit, g.designs.TryFind unit with
        | _ when List.contains unit structuralBoxes -> Ok Map.empty
        | Some factory, _ -> Ok(defaults factory)
        | None, Some _ -> Ok Map.empty
        | None, None -> Error $"no unit called '{unit}' in the palette or among the design's own designs"

    arguments
    |> Result.map (fun arguments ->
        let name = freshBoxName g unit

        { g with
            boxes = g.boxes @ [ newBox name unit arguments ]
            positions = g.positions |> Map.add name at },
        name)

/// The factory's verdict on a box as it would be: made for the rate with
/// these arguments, or refused naming the box and the parameter. A box that
/// is a design has no arguments to refuse.
let private checkBox (g: Graph) (rate: float) (b: Box) : Result<unit, string> =
    match (units ()).TryFind b.unit, g.designs.TryFind b.unit with
    | _ when isStructural b -> Ok()
    | Some factory, _ -> factory.make rate (complete factory b.arguments) |> Result.map ignore |> Result.mapError (fun why -> $"{b.name}: {why}")
    | None, Some _ -> Ok()
    | None, None -> Error $"{b.name}: no unit called '{b.unit}'"

/// One creation argument of a box, as typed. The factory checks it before
/// the box holds it; a change is a new design.
let setArgument (name: string) (parameter: string) (value: string) (g: Graph) : Result<Graph, string> =
    match g.boxes |> List.tryFind (fun b -> b.name = name), g.boxes |> List.tryFind (fun b -> b.name = name) |> Option.bind (fun b -> (units ()).TryFind b.unit) with
    | None, _ -> Error $"no box called '{name}'"
    | Some b, _ when isStructural b ->
        // A structural box's argument: checked by parsing the box it makes.
        if not (structuralParameters b.unit |> List.exists (fun p -> p.name = parameter)) then
            Error $"{name}: a {b.unit} box has no '{parameter}'"
        else
            let changed = { b with arguments = b.arguments |> Map.add parameter value }

            try
                (if changed.unit = joinBox then joinInputs changed |> ignore
                 elif changed.unit = fieldsBox then fieldPicks changed |> ignore
                 else
                     match System.Int32.TryParse value with
                     | true, d when d >= 1 -> ()
                     | _ -> failwith $"{name}: a buffer's depth is a count of at least one, not '{value}'")

                Ok { g with boxes = g.boxes |> List.map (fun x -> if x.name = name then changed else x) }
            with e ->
                Error e.Message
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
    if (units ()).ContainsKey sub.name then
        Error $"'{sub.name}' is a unit in the palette"
    elif g.designs.ContainsKey sub.name then
        Error $"there is already a design called '{sub.name}' in this one"
    elif sub.name = g.name then
        Error $"a design cannot use itself"
    else
        setSampleRate g.sampleRate sub
        |> Result.mapError (fun why -> $"'{sub.name}' at %g{g.sampleRate} Hz: {why}")
        |> Result.map (fun sub -> { g with designs = g.designs |> Map.add sub.name sub })

/// A unit written in the GUI, its source kept with the design so the file
/// is complete. The factory itself is the session's, added by the head that
/// compiled it; this records what it was compiled from.
let defineUnit (name: string) (source: string) (g: Graph) : Result<Graph, string> =
    if not ((units ()).ContainsKey name) then
        Error $"'{name}' is not a unit this session knows — compile it first"
    else
        Ok { g with units = g.units |> Map.add name source }

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

/// A box — a unit's or a control's — and every edge on it.
let removeBox (name: string) (g: Graph) : Result<Graph, string> =
    if not (hasBox g name) then
        Error $"no box called '{name}'"
    else
        Ok
            { g with
                boxes = g.boxes |> List.filter (fun b -> b.name <> name)
                controlBoxes = g.controlBoxes |> List.filter (fun c -> c.name <> name)
                edges = g.edges |> List.filter (fun e -> e.from.box <> name && e.``to``.box <> name)
                fanOuts = g.fanOuts |> Map.filter (fun p _ -> p.box <> name)
                merges = g.merges |> Map.filter (fun p _ -> p.box <> name)
                positions = g.positions |> Map.remove name }

/// The same box under a new name; its edges follow it.
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
            let follow (p: PortRef) = if p.box = name then { p with box = newName } else p

            { g with
                boxes = g.boxes |> List.map (fun b -> if b.name = name then { b with name = newName } else b)
                controlBoxes = g.controlBoxes |> List.map (fun c -> if c.name = name then { c with name = newName } else c)
                edges = g.edges |> List.map (fun e -> { e with from = follow e.from; ``to`` = follow e.``to`` })
                fanOuts = g.fanOuts |> Map.toList |> List.map (fun (p, v) -> follow p, v) |> Map.ofList
                merges = g.merges |> Map.toList |> List.map (fun (p, v) -> follow p, v) |> Map.ofList
                positions =
                    match g.positions |> Map.tryFind name with
                    | Some at -> g.positions |> Map.remove name |> Map.add newName at
                    | None -> g.positions })

/// Where a box sits on the canvas. The boundary boxes have positions too.
let moveBox (name: string) (at: Position) (g: Graph) : Graph =
    { g with positions = g.positions |> Map.add name at }

/// A second box of the same unit with the same arguments and settings,
/// unconnected. A control box copies with its value.
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

/// The value a box's unwired control inlet holds: the literal the design
/// is built with. Checked against the inlet's format; a wired inlet takes
/// its value from the edge, not here.
let setSetting (name: string) (inlet: string) (value: string) (g: Graph) : Result<Graph, string> =
    match g.boxes |> List.tryFind (fun b -> b.name = name) with
    | None -> Error $"no box called '{name}'"
    | Some b ->
        match controlsOf g name |> List.tryFind (fun (n, _) -> n = inlet) with
        | None -> Error $"{name}: no control inlet called '{inlet}'"
        | Some(_, f) ->
            controlBits (showPort (port name inlet)) f value
            |> Result.map (fun _ ->
                { g with boxes = g.boxes |> List.map (fun x -> if x.name = name then { b with settings = b.settings |> Map.add inlet value } else x) })

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

/// What the design's own control ports start at: each number box's value.
/// The design's own controls are the mapping's to give.
let startingValues (g: Graph) : (string * uint64) list =
    [ for c in g.controlBoxes do
          if c.kind = NumberBox then
              yield c.name, controlValueBits c ]

/// An edge between two ports, either end first: it always runs from the
/// output to the input, and starts as a wire. Refused, naming the port,
/// when the ends are the same side or the same box, when they do not fit,
/// when the edge is already drawn, or when a control inlet already has its
/// one — everything the elaborator would refuse, said before the edge
/// exists. A second edge at a stream port is a meeting: the port's pattern
/// decides what it does, `Broadcast` and round-robin until set.
let addEdge (a: PortRef, sideA: Side) (b: PortRef, sideB: Side) (g: Graph) : Result<Graph, string> =
    if sideA = sideB then
        Error(if sideA = In then "both ports are inputs" else "both ports are outputs")
    elif a.box = b.box then
        Error $"{a.box}: a box cannot feed itself"
    else
        let src, dst = if sideA = In then b, a else a, b
        let e = wire src dst
        let source = lookupPort g Out src
        let isControl = match source with Ok(ControlOut _) -> true | _ -> false
        let isSocket = match source with Ok(SocketOut _) -> true | _ -> false

        match edgeFit g e with
        | DoesNotFit why -> Error $"{showEdge e}: {why}"
        | Fits
        | FitsNamesDiffer _ ->
            if g.edges |> List.exists (fun x -> x.from = src && x.``to`` = dst) then
                Error $"{showEdge e} is already drawn"
            elif isControl && g.edges |> List.exists (fun x -> x.``to`` = dst) then
                Error $"{showPort dst} already has an edge, and a control inlet takes one"
            elif isSocket && g.edges |> List.exists (fun x -> x.from = src) then
                Error $"{showPort src} already has an edge, and a socket connects to one service"
            else
                Ok { g with edges = g.edges @ [ e ] }

/// What a stream edge registers: the parent's choice, made on the edge.
let setConnection (e: Edge) (connection: Connection) (g: Graph) : Result<Graph, string> =
    if not (List.contains e g.edges) then
        Error $"{showEdge e}: no such edge"
    else
        let changed = { e with connection = connection }

        match edgeFit g changed with
        | DoesNotFit why -> Error $"{showEdge e}: {why}"
        | Fits
        | FitsNamesDiffer _ -> Ok { g with edges = g.edges |> List.map (fun x -> if x = e then changed else x) }

/// The pattern at a stream port several edges leave.
let setFanOut (p: PortRef) (pattern: FanOut) (g: Graph) : Result<Graph, string> =
    match lookupPort g Out p with
    | Ok(StreamOut _) ->
        Ok
            { g with
                fanOuts = if pattern = FanOut.Broadcast then g.fanOuts |> Map.remove p else g.fanOuts |> Map.add p pattern }
    | Ok _ -> Error $"{showPort p}: a pattern is chosen where streams leave, and this is a control"
    | Error why -> Error why

/// The arbitration at a stream port several edges arrive at, or at a
/// service several sockets share.
let setMerge (p: PortRef) (arbitration: Arbitration) (g: Graph) : Result<Graph, string> =
    match lookupPort g In p with
    | Ok(StreamIn _)
    | Ok(ServiceIn _) ->
        Ok
            { g with
                merges =
                    if arbitration = Arbitration.roundRobin then
                        g.merges |> Map.remove p
                    else
                        g.merges |> Map.add p arbitration }
    | Ok _ -> Error $"{showPort p}: an arbitration is chosen where streams arrive, and this is a control"
    | Error why -> Error why

/// An edge moved ahead of the others: first among the edges it meets at
/// either end, so the first lane, and the one `Priority` serves first.
let raiseEdge (e: Edge) (g: Graph) : Result<Graph, string> =
    if not (List.contains e g.edges) then
        Error $"{showEdge e}: no such edge"
    else
        Ok { g with edges = e :: (g.edges |> List.filter ((<>) e)) }

/// How many copies of its unit a box is, and whether beats leave them in
/// the order they came.
let setCopies (name: string) (n: int) (g: Graph) : Result<Graph, string> =
    if not (g.boxes |> List.exists (fun b -> b.name = name)) then
        Error $"no box called '{name}'"
    elif n < 1 then
        Error $"{name}: copies is at least 1, not %d{n}"
    else
        Ok { g with boxes = g.boxes |> List.map (fun b -> if b.name = name then { b with copies = n } else b) }

let setOrdered (name: string) (ordered: bool) (g: Graph) : Result<Graph, string> =
    if not (g.boxes |> List.exists (fun b -> b.name = name)) then
        Error $"no box called '{name}'"
    else
        Ok { g with boxes = g.boxes |> List.map (fun b -> if b.name = name then { b with ordered = ordered } else b) }

let removeEdge (e: Edge) (g: Graph) : Graph =
    { g with edges = g.edges |> List.filter (fun x -> x <> e) }

/// The design's boundary: a stream in, a control, a stream out. An input
/// stream and a control share the input box, so they cannot share a name;
/// an output may be called what an input is.
let private checkFree (what: string) (taken: string list) (name: string) : Result<unit, string> =
    checkName what name
    |> Result.bind (fun () ->
        if List.contains name taken then
            Error $"there is already a port called '{name}' on that box"
        else
            Ok())

let private checkFields (fields: Fields) : Result<unit, string> =
    let names = List.map fst fields

    if fields.IsEmpty then
        Error "a stream carries at least one field"
    elif List.distinct names <> names then
        Error "a stream's fields have different names"
    else
        (Ok(), names) ||> List.fold (fun acc n -> acc |> Result.bind (fun () -> checkName "field" n))

let private inputNames (g: Graph) = [ for s in g.inputs -> s.name ] @ List.map fst g.controls

let addInputStream (name: string) (fields: Fields) (g: Graph) : Result<Graph, string> =
    checkFree "stream" (inputNames g) name
    |> Result.bind (fun () -> checkFields fields)
    |> Result.map (fun () -> { g with inputs = g.inputs @ [ { name = name; fields = fields } ] })

let addOutputStream (name: string) (fields: Fields) (g: Graph) : Result<Graph, string> =
    checkFree "stream" [ for s in g.outputs -> s.name ] name
    |> Result.bind (fun () -> checkFields fields)
    |> Result.map (fun () -> { g with outputs = g.outputs @ [ { name = name; fields = fields } ] })

let addControl (name: string, format: NumberFormat) (g: Graph) : Result<Graph, string> =
    checkFree "control" (inputNames g) name
    |> Result.map (fun () -> { g with controls = g.controls @ [ name, format ] })

/// The fields a boundary stream carries. Its edge stays where it is and is
/// judged again against the new fields.
let setFields (p: PortRef) (fields: Fields) (g: Graph) : Result<Graph, string> =
    let change (streams: BoundaryStream list) =
        streams |> List.map (fun s -> if s.name = p.port then { s with fields = fields } else s)

    checkFields fields
    |> Result.bind (fun () ->
        match p.box with
        | "input" when g.inputs |> List.exists (fun s -> s.name = p.port) -> Ok { g with inputs = change g.inputs }
        | "output" when g.outputs |> List.exists (fun s -> s.name = p.port) -> Ok { g with outputs = change g.outputs }
        | _ -> Error $"{showPort p}: not a stream on the design's boundary")

/// A boundary stream or control, and the edges from or to it.
let removeBoundaryPort (p: PortRef) (g: Graph) : Result<Graph, string> =
    let without (streams: BoundaryStream list) = streams |> List.filter (fun s -> s.name <> p.port)
    let unconnected = g.edges |> List.filter (fun e -> e.from <> p && e.``to`` <> p)

    match p.box with
    | "input" when g.inputs |> List.exists (fun s -> s.name = p.port) -> Ok { g with inputs = without g.inputs; edges = unconnected }
    | "input" when g.controls |> List.exists (fun (n, _) -> n = p.port) ->
        Ok { g with controls = g.controls |> List.filter (fun (n, _) -> n <> p.port); edges = unconnected }
    | "output" when g.outputs |> List.exists (fun s -> s.name = p.port) -> Ok { g with outputs = without g.outputs; edges = unconnected }
    | "input"
    | "output" -> Error $"{showPort p}: no such port"
    | _ -> Error $"{p.box}: a box's ports are its unit's; only the design's own are added and removed"

let rename (name: string) (g: Graph) : Result<Graph, string> =
    checkName "design" name |> Result.map (fun () -> { g with name = name })

/// The design's default mapping: the file beside it, or none.
let setMapping (file: string option) (g: Graph) : Result<Graph, string> = Ok { g with mapping = file }

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
