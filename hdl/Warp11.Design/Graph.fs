/// The design as data: what a GUI holds, and the seam a canvas is swapped
/// behind. Boxes with ports, and edges between ports.
///
/// A port is one thing. A **stream** port carries one stream, its fields
/// together; a **control** port holds one value across beats. An edge joins
/// two ports of the same kind and says what the connection registers, so a
/// drawn chain is the typed one: each box its unit applied once, each edge
/// `connectAs` its connection.
///
/// No reflection: a unit's ports come from its layouts, read here as data.
module Warp11.Graph

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Factories

/// One box: a factory by name, the creation arguments typed into it, and
/// the **settings** — the value each control inlet nobody wired holds, as
/// typed. An unwired control inlet is not an error: it is a literal of its
/// setting, fixed when the design is built, as an argument written in code
/// is. A value the host can change is a number box, wired to the inlet.
type Box =
    { name: string
      unit: string
      /// How many of the unit: above one, a farm over the box's stream —
      /// a beat to whichever copy is free.
      copies: int
      /// Whether a farm hands beats on in the order they came.
      ordered: bool
      arguments: Arguments
      settings: Map<string, string> }

/// A box of a unit as a fresh one is: one copy, in order.
let newBox (name: string) (unit: string) (arguments: Arguments) : Box =
    { name = name
      unit = unit
      copies = 1
      ordered = true
      arguments = arguments
      settings = Map.empty }

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

/// The stream a unit's box takes, and the one it gives: its operands and
/// its results. A box that is a design has that design's boundary streams
/// instead, under their own names.
let streamInlet = "in"
let streamOutlet = "out"

/// The port a unit's box offers its unit on, to the sockets connected to
/// it. A box with edges into this port is a service, not a stage.
let servicePort = "service"

/// The fields a stream carries, in order.
type Fields = (string * NumberFormat) list

/// One stream on the design's own boundary: its name, which is the prefix
/// its ports carry (`in_left`, `in_valid`), and its fields.
type BoundaryStream = { name: string; fields: Fields }

/// A port on a box — or on the design's own boundary, where the box is
/// `"input"` (its input streams and its controls, as sources) or `"output"`.
type PortRef = { box: string; port: string }

/// What several edges leaving one stream port do with its beats.
[<RequireQualifiedAccess>]
type FanOut =
    /// Every edge gets every beat, each when it can take it
    /// (`streamForkEager`). What a second edge means until told otherwise.
    | Broadcast
    /// Each beat goes to one edge that is free (`streamBalance`).
    | Balance

/// One edge: a stream from one stream port to another through a connection,
/// or a control value from its source to an inlet.
type Edge =
    { from: PortRef
      ``to``: PortRef
      connection: Connection }

/// The design: its boundary, the boxes, the edges, the sample rate it is
/// made for, and where a canvas last put each box.
///
/// The rate is the design's because a filter's coefficients fix a frequency
/// in cycles per *sample*: a box designed for 1 kHz holds that only at the
/// rate it was designed for, so the rate is recorded beside the frequencies
/// that depend on it. A mapping checks its own rate against this one and
/// refuses a mismatch, as it refuses a format.
///
/// Positions are part of the design so a saved design opens as it was left;
/// the elaborator never reads them.
type Graph =
    { name: string
      sampleRate: float
      inputs: BoundaryStream list
      controls: (string * NumberFormat) list
      outputs: BoundaryStream list
      boxes: Box list
      controlBoxes: ControlBox list
      /// The edges, in order. Where several meet at one stream port the
      /// order is the lane order: a broadcast's and a balance's branches,
      /// a merge's inputs, and so what `Priority` serves first.
      edges: Edge list
      /// The pattern at a stream port several edges leave, where it is not
      /// `Broadcast`.
      fanOuts: Map<PortRef, FanOut>
      /// The arbitration at a stream port several edges arrive at, where it
      /// is not round-robin in the same cycle.
      merges: Map<PortRef, Arbitration>
      positions: Map<string, float * float>
      /// The designs this design uses as units, by name — Pure Data's
      /// abstractions, embedded so a saved file is complete. A box whose
      /// `unit` names one is that design as a box: its boundary streams the
      /// stream ports, every control port of its own a control inlet.
      designs: Map<string, Graph>
      /// The units written in the GUI that this design uses, by name: the F#
      /// that defines each, so the file is complete and a head with a
      /// compiler opens it again.
      units: Map<string, string>
      /// The design's default mapping: the file beside this one that says
      /// which board it goes to and how (`gain.kv260.json`). None maps to
      /// the simulator only.
      mapping: string option }

let port (box: string) (port: string) : PortRef = { box = box; port = port }

/// A stream edge that registers nothing, or a control edge.
let wire (from: PortRef) (``to``: PortRef) : Edge =
    { from = from
      ``to`` = ``to``
      connection = Connection.Wire }

/// A stream edge through a connection.
let edgeAs (connection: Connection) (from: PortRef) (``to``: PortRef) : Edge =
    { from = from
      ``to`` = ``to``
      connection = connection }

/// The rate a design is made for until a mapping says otherwise: the studio
/// rate a WAV from the desk is at. A board design takes its board's.
let defaultSampleRate = 48_000.0

/// A design with nothing in it yet but the two streams a fresh one starts
/// with, `in` and `out`, carrying `fields`.
let emptyGraph (name: string) (sampleRate: float) (fields: Fields) : Graph =
    { name = name
      sampleRate = sampleRate
      inputs = [ { name = "in"; fields = fields } ]
      controls = []
      outputs = [ { name = "out"; fields = fields } ]
      boxes = []
      controlBoxes = []
      edges = []
      fanOuts = Map.empty
      merges = Map.empty
      positions = Map.empty
      designs = Map.empty
      units = Map.empty
      mapping = None }

/// Which side of a box a port is on. A box may call an input and an output
/// by the same name, so wherever a port is named on its own rather than as
/// an edge's end, it carries its side.
type Side =
    | In
    | Out

/// The first patch: a stereo stream through `gain`, volume and mute from the
/// design's controls.
let gainGraph: Graph =
    let stereo = [ "left", signedInt sampleWidth; "right", signedInt sampleWidth ]

    { emptyGraph "GainPatch" defaultSampleRate stereo with
        controls = [ "volume", unsignedInt 16; "mute", unsignedInt 1 ]
        boxes = [ newBox "gain" "gain" Map.empty ]
        edges =
            [ wire (port "input" "in") (port "gain" streamInlet)
              wire (port "input" "volume") (port "gain" "volume")
              wire (port "input" "mute") (port "gain" "mute")
              wire (port "gain" streamOutlet) (port "output" "out") ] }

let boxOf (g: Graph) (name: string) = g.boxes |> List.tryFind (fun b -> b.name = name)

/// The edges leaving a port and the edges arriving at one, in lane order.
let edgesFrom (g: Graph) (p: PortRef) : Edge list = g.edges |> List.filter (fun e -> e.from = p)

let edgesInto (g: Graph) (p: PortRef) : Edge list = g.edges |> List.filter (fun e -> e.``to`` = p)

// ---------------------------------------------------------------------------
// The boxes that are what code does between stages, rather than units: a
// join, a regrouping of fields, a buffer. They have no layout of their own —
// each takes the fields of whatever is connected to it.

/// Several streams into one beat (`streamJoin`): a beat leaves when every
/// side has one. `inputs` sides, `in0` … , each carrying one field.
let joinBox = "join"

/// Pick, rename and regroup a stream's fields. `pick` lists the fields
/// out, each `name` or `name = field`; every bit out went in unchanged.
let fieldsBox = "fields"

/// A buffer of `depth` beats (`streamFifo`).
let fifoBox = "fifo"

let structuralBoxes = [ joinBox; fieldsBox; fifoBox ]

let isStructural (b: Box) = List.contains b.unit structuralBoxes

/// The arguments a structural box takes, as a palette unit's factory says
/// its own.
let structuralParameters (unit: string) : Parameter list =
    match unit with
    | "join" -> [ { name = "inputs"; kind = IntParameter; ``default`` = "2"; about = "how many streams are joined" } ]
    | "fields" ->
        [ { name = "pick"
            kind = FloatsParameter
            ``default`` = ""
            about = "the fields out, comma-separated: name, or name = field to rename" } ]
    | "fifo" -> [ { name = "depth"; kind = IntParameter; ``default`` = "16"; about = "beats the buffer holds" } ]
    | _ -> []

/// A structural box's argument as typed, or its default.
let structuralArgument (b: Box) (name: string) : string =
    match b.arguments |> Map.tryFind name, structuralParameters b.unit |> List.tryFind (fun p -> p.name = name) with
    | Some v, _ -> v
    | None, Some p -> p.``default``
    | None, None -> ""

/// A join's sides, by port name.
let joinInputs (b: Box) : string list =
    match System.Int32.TryParse(structuralArgument b "inputs") with
    | true, n when n >= 1 -> [ for i in 0 .. n - 1 -> $"in%d{i}" ]
    | _ ->
        let typed = structuralArgument b "inputs"
        failwith $"{b.name}: a join's inputs is a count of at least one, not '{typed}'"

/// A fields box's picks: the name out and the field in it is.
let fieldPicks (b: Box) : (string * string) list =
    (structuralArgument b "pick").Split([| ',' |], System.StringSplitOptions.RemoveEmptyEntries)
    |> List.ofArray
    |> List.map (fun part ->
        match part.Split '=' |> Array.map (fun t -> t.Trim()) with
        | [| name |] when name <> "" -> name, name
        | [| name; field |] when name <> "" && field <> "" -> name, field
        | _ -> failwith $"{b.name}: '{part.Trim()}' is not a field: name, or name = field")

/// The design a box is, when its unit names one of this design's own.
let designOf (g: Graph) (b: Box) : Graph option = g.designs |> Map.tryFind b.unit

/// The unit a palette box is, made by its factory for the design's rate,
/// each socket port served by what `serve` answers for its name or in place
/// where it answers nothing: arguments the box does not state are the
/// factory's defaults. A box whose arguments the factory refuses cannot be
/// elaborated, and says which one. A box that is a design is
/// `Elaborate.unitOf`'s to make.
let paletteUnitServed (g: Graph) (b: Box) (serve: string -> (int -> Server) option) : ErasedFu =
    match (units ()).TryFind b.unit with
    | None -> failwith $"{b.name}: no unit called '{b.unit}' in the palette or among the design's own designs"
    | Some factory ->
        let made =
            match factory.served with
            | Some served -> served.make g.sampleRate (complete factory b.arguments) serve
            | None -> factory.make g.sampleRate (complete factory b.arguments)

        match made with
        | Ok unit -> unit
        | Error why -> failwith $"{b.name}: {why}"

/// The palette unit with every socket served in place.
let paletteUnitOf (g: Graph) (b: Box) : ErasedFu = paletteUnitServed g b (fun _ -> None)

/// A box's socket ports: what its unit needs and does not provide. A box
/// that is a design has none: inside it, an unconnected socket is served
/// in place.
let socketsOf (g: Graph) (box: string) : Sockets list =
    match boxOf g box with
    | Some b when (designOf g b).IsNone ->
        match (units ()).TryFind b.unit |> Option.bind (fun f -> f.served) with
        | Some served -> served.sockets
        | None -> []
    | _ -> []

/// What a box's unit asks and answers, when the box can be a service: a
/// combinational palette unit with no controls, which is what can be put
/// in place or shared.
let servesAs (g: Graph) (box: string) : (Fields * Fields) option =
    match boxOf g box with
    | Some b when (designOf g b).IsNone && (units ()).ContainsKey b.unit ->
        let unit = paletteUnitOf g b

        match unit.law with
        | Combinational _ when unit.controls.IsEmpty -> Some(unit.operands.fields, unit.results.fields)
        | _ -> None
    | _ -> None

/// The fields a stream carries where it leaves a port. A structural box's
/// are what reaches it, followed back through the edges; `seen` stops a
/// cycle from being followed for ever.
let rec private leavingFields (g: Graph) (seen: Set<PortRef>) (p: PortRef) : Fields =
    match p.box with
    | "input" -> g.inputs |> List.tryFind (fun s -> s.name = p.port) |> Option.map (fun s -> s.fields) |> Option.defaultValue []
    | box ->
        match boxOf g box with
        | Some b when isStructural b -> structuralOut g seen b
        | Some b ->
            match designOf g b with
            | Some sub -> sub.outputs |> List.tryFind (fun s -> s.name = p.port) |> Option.map (fun s -> s.fields) |> Option.defaultValue []
            | None -> (paletteUnitOf g b).results.fields
        | None -> []

/// The fields that reach a port: its first edge's.
and private arrivingFields (g: Graph) (seen: Set<PortRef>) (sink: PortRef) : Fields =
    match edgesInto g sink |> List.tryHead with
    | Some e when not (seen.Contains e.from) -> leavingFields g (seen.Add e.from) e.from
    | _ -> []

and private structuralOut (g: Graph) (seen: Set<PortRef>) (b: Box) : Fields =
    match b.unit with
    | "fifo" -> arrivingFields g seen (port b.name "in")
    | "join" -> joinInputs b |> List.collect (fun side -> arrivingFields g seen (port b.name side))
    | _ ->
        let arriving = arrivingFields g seen (port b.name "in")

        [ for name, field in fieldPicks b do
              match arriving |> List.tryFind (fun (n, _) -> n = field) with
              | Some(_, f) -> yield name, f
              | None -> () ]

/// A box's stream ports, in and out — from the palette, where a unit takes
/// one stream and gives one, from the design the box is, or for a
/// structural box from what is connected to it — or the design's own
/// boundary boxes, whose ports are the design's streams.
let streamsOf (g: Graph) (box: string) : BoundaryStream list * BoundaryStream list =
    match box with
    | "input" -> [], g.inputs
    | "output" -> g.outputs, []
    | name ->
        match boxOf g name with
        | Some b when isStructural b ->
            let ins = if b.unit = joinBox then joinInputs b else [ streamInlet ]

            [ for side in ins -> { name = side; fields = arrivingFields g Set.empty (port b.name side) } ],
            [ { name = streamOutlet; fields = structuralOut g Set.empty b } ]
        | Some b ->
            match designOf g b with
            | Some sub -> sub.inputs, sub.outputs
            | None ->
                let unit = paletteUnitOf g b
                [ { name = streamInlet; fields = unit.operands.fields } ], [ { name = streamOutlet; fields = unit.results.fields } ]
        | None -> [], []

/// A box's control ports: sinks on a box, sources on the `input` box. A box
/// that is a design has every control port of that design as an inlet.
let rec controlsOf (g: Graph) (box: string) : (string * NumberFormat) list =
    match box with
    | "input" -> g.controls
    | "output" -> []
    | name ->
        match boxOf g name with
        | Some b when isStructural b -> []
        | Some b ->
            match designOf g b with
            | Some sub -> controlPorts sub
            | None -> (paletteUnitOf g b).controls
        | None -> []

/// Every control port the design has, in port order: its own, then its
/// number boxes. A mapping pokes these; a parent design that uses this one
/// as a box sees them as its control inlets.
and controlPorts (g: Graph) : (string * NumberFormat) list =
    g.controls
    @ [ for c in g.controlBoxes do
            if c.kind = NumberBox then
                yield c.name, c.format ]

/// What a port is: a stream in or out of a box, a control held beside it,
/// a socket (what the box's unit asks for and what it gets back) or the
/// service a socket connects to.
type PortKind =
    | StreamIn of Fields
    | StreamOut of Fields
    | ControlIn of NumberFormat
    | ControlOut of NumberFormat
    | SocketOut of Fields * Fields
    | ServiceIn of Fields * Fields

let showPort (p: PortRef) = $"{p.box}.{p.port}"

let showEdge (e: Edge) = $"{showPort e.from} → {showPort e.``to``}"

let controlBoxOf (g: Graph) (name: string) = g.controlBoxes |> List.tryFind (fun c -> c.name = name)

let boxExists (g: Graph) (name: string) =
    name = "input"
    || name = "output"
    || g.boxes |> List.exists (fun b -> b.name = name)
    || (controlBoxOf g name).IsSome

/// What a port is, or why it is not. An edge's `from` end is looked up
/// among a box's outputs and its `to` end among its inputs.
let lookupPort (g: Graph) (side: Side) (p: PortRef) : Result<PortKind, string> =
    if not (boxExists g p.box) then
        Error $"{showPort p}: no such box"
    else
        let streamIns, streamOuts = streamsOf g p.box
        let controls = controlsOf g p.box
        let stream (among: BoundaryStream list) = among |> List.tryFind (fun s -> s.name = p.port) |> Option.map (fun s -> s.fields)
        let control () = controls |> List.tryFind (fun (n, _) -> n = p.port) |> Option.map snd

        match side, controlBoxOf g p.box with
        | Out, Some c when p.port = controlOutlet -> Ok(ControlOut c.format)
        | Out, Some _ -> Error $"{showPort p}: a control box's one outlet is '{controlOutlet}'"
        | In, Some _ -> Error $"{showPort p}: a control box has no inlets"
        | Out, None ->
            let socket = socketsOf g p.box |> List.tryFind (fun s -> s.name = p.port)

            match stream streamOuts, socket, (if p.box = "input" then control () else None) with
            | Some fields, _, _ -> Ok(StreamOut fields)
            | _, Some s, _ -> Ok(SocketOut(s.needs.operands.fields, s.needs.results.fields))
            | _, _, Some f -> Ok(ControlOut f)
            | _ -> Error $"{showPort p}: no such output port"
        | In, None ->
            let service = if p.port = servicePort then servesAs g p.box else None

            match stream streamIns, service, (if p.box = "input" then None else control ()) with
            | Some fields, _, _ -> Ok(StreamIn fields)
            | _, Some(asks, answers), _ -> Ok(ServiceIn(asks, answers))
            | _, _, Some f -> Ok(ControlIn f)
            | _ -> Error $"{showPort p}: no such input port"

/// Whether an edge's two ends fit, judged whenever it is asked: a side can
/// change after the edge was drawn — an argument that changes a unit's
/// layout, a design edited inside — and the edge stays where it is.
type Fit =
    /// The same formats in order under the same names.
    | Fits
    /// The same formats in order; these pairs of fields go by different
    /// names on the two sides. Builds, and an editor marks the edge.
    | FitsNamesDiffer of (string * string) list
    /// The ends are not there, are not the same kind, or carry different
    /// formats: why. Elaboration refuses the design, naming the edge.
    | DoesNotFit of string

/// Two streams' fields against each other: the count, then each format in
/// order — width, signedness and fraction bits. Names never refuse.
let fieldsFit (from: Fields) (``to``: Fields) : Fit =
    if from.Length <> ``to``.Length then
        DoesNotFit $"%d{from.Length} field(s) into %d{``to``.Length}"
    else
        match List.zip from ``to`` |> List.tryFind (fun ((_, fa), (_, fb)) -> fa <> fb) with
        | Some((na, fa), (nb, fb)) -> DoesNotFit $"'{na}' is {describeFormat fa}, '{nb}' is {describeFormat fb}"
        | None ->
            match [ for (na, _), (nb, _) in List.zip from ``to`` do
                        if na <> nb then
                            yield na, nb ] with
            | [] -> Fits
            | renamed -> FitsNamesDiffer renamed

/// An edge as the design stands now.
let edgeFit (g: Graph) (e: Edge) : Fit =
    match lookupPort g Out e.from, lookupPort g In e.``to`` with
    | Error why, _
    | _, Error why -> DoesNotFit why
    | Ok(StreamOut from), Ok(StreamIn ``to``) ->
        // A structural box takes the fields of what is connected to it, so
        // the first edge into one of its ports always fits; a later one
        // fits what the first brought.
        match boxOf g e.``to``.box with
        | Some b when isStructural b ->
            match edgesInto g e.``to`` |> List.tryHead with
            | Some first when first <> e -> fieldsFit from ``to``
            | _ -> Fits
        | _ -> fieldsFit from ``to``
    | Ok(ControlOut fa), Ok(ControlIn fb) ->
        if fa <> fb then
            DoesNotFit $"{describeFormat fa} into {describeFormat fb}"
        elif e.connection <> Connection.Wire then
            DoesNotFit $"a control is a held value, and {e.connection} registers a stream"
        else
            Fits
    | Ok(SocketOut(asks, answers)), Ok(ServiceIn(takes, gives)) ->
        if e.connection <> Connection.Wire then
            DoesNotFit $"a socket's edge registers nothing, and this says {e.connection}; when a shared unit answers is its arbitration"
        else
            // What the socket asks against what the service takes, and what
            // the service gives against what the socket expects back.
            match fieldsFit asks takes, fieldsFit gives answers with
            | DoesNotFit why, _ -> DoesNotFit $"what it asks: {why}"
            | _, DoesNotFit why -> DoesNotFit $"what comes back: {why}"
            | Fits, Fits -> Fits
            | FitsNamesDiffer a, FitsNamesDiffer b -> FitsNamesDiffer(a @ b)
            | FitsNamesDiffer a, Fits
            | Fits, FitsNamesDiffer a -> FitsNamesDiffer a
    | Ok(SocketOut _), Ok _ -> DoesNotFit "a socket connects to a service"
    | Ok _, Ok(ServiceIn _) -> DoesNotFit "a service takes sockets"
    | Ok(StreamOut _), Ok _ -> DoesNotFit "a stream into a control"
    | Ok _, Ok _ -> DoesNotFit "a control into a stream"

/// A value as a control's bits: a whole number that fits the format.
let controlBits (what: string) (f: NumberFormat) (text: string) : Result<uint64, string> =
    match System.UInt64.TryParse text with
    | true, v when f.totalWidth >= 64 || v < (1UL <<< f.totalWidth) -> Ok v
    | true, v -> Error $"{what}: %d{v} does not fit %d{f.totalWidth} bits"
    | _ -> Error $"{what}: '{text}' is not a whole number"

let controlValueBits (c: ControlBox) : uint64 =
    match controlBits c.name c.format c.value with
    | Ok v -> v
    | Error _ -> 0UL

/// What a control inlet nobody wired holds: the setting typed on the box;
/// with nothing typed, on a box that is a design, what that design's own
/// number box starts at; otherwise zero.
let settingBits (g: Graph) (b: Box) (inlet: string, f: NumberFormat) : uint64 =
    match b.settings |> Map.tryFind inlet |> Option.map (controlBits inlet f), designOf g b with
    | Some(Ok v), _ -> v
    | _, Some sub -> controlBoxOf sub inlet |> Option.map controlValueBits |> Option.defaultValue 0UL
    | _ -> 0UL

/// The pattern at a stream port edges leave, and the arbitration at one
/// they arrive at: what was chosen, or what a meeting is until someone does.
let fanOutAt (g: Graph) (p: PortRef) : FanOut = g.fanOuts |> Map.tryFind p |> Option.defaultValue FanOut.Broadcast

let mergeAt (g: Graph) (p: PortRef) : Arbitration = g.merges |> Map.tryFind p |> Option.defaultValue Arbitration.roundRobin


/// Whether a box is a service: something is connected to its service port.
/// The use decides, as it does in code (`inPlace` against `Fu.sharing`).
let isService (g: Graph) (b: Box) : bool = not (edgesInto g (port b.name servicePort)).IsEmpty

/// How many sockets a service serves: every socket of every port connected
/// to it.
let clientsOf (g: Graph) (service: Box) : int =
    edgesInto g (port service.name servicePort)
    |> List.sumBy (fun e -> socketsOf g e.from.box |> List.tryFind (fun s -> s.name = e.from.port) |> Option.map (fun s -> s.count) |> Option.defaultValue 0)
