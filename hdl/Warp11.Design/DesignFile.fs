/// A design as a file: the `Graph` as JSON. It holds the boundary's
/// streams with their fields, the boxes by unit name, the edges with what
/// each registers, and the positions — and no types, because the graph
/// holds none: the **palette is the type authority**, resolved by name on
/// every load, as a Pure Data file resolves `biquad~` against the
/// external's code. A unit whose ports have changed since the file was
/// saved opens with the edge that no longer fits still in it, judged
/// against the unit as it is now (`Graph.edgeFit`), for an editor to show
/// and the elaborator to refuse by name.
///
/// ```json
/// { "format": 2, "name": "GainPatch", "sampleRate": 48000,
///   "inputs":   [ { "name": "in", "fields": [ { "name": "left", "width": 24, "fraction": 0, "signed": true }, … ] } ],
///   "controls": [ { "name": "volume", "width": 16, "fraction": 0, "signed": false }, … ],
///   "outputs":  [ { "name": "out", "fields": [ … ] } ],
///   "boxes":    [ { "name": "low", "unit": "eq",
///                   "arguments": { "shape": "lowshelf", "fc": "200", "q": "0.707", "gain": "6" } } ],
///   "edges":    [ { "from": "input.in", "to": "low.in" },
///                 { "from": "low.out", "to": "mid.in", "connection": "Forward" }, … ],
///   "positions": { "input": [60, 120], "low": [320, 120], "output": [580, 120] } }
/// ```
///
/// An edge that registers nothing leaves `connection` out. `format` is 2;
/// a file without it is a design drawn field to field, and is refused
/// saying so.
module Warp11.DesignFile

open System.Text.Json
open System.Text.Json.Nodes
open Warp11.Fu
open Warp11.Factories
open Warp11.Graph

let private formatNode (name: string, f: NumberFormat) : JsonNode =
    let o = JsonObject()
    o["name"] <- JsonValue.Create name
    o["width"] <- JsonValue.Create f.totalWidth
    o["fraction"] <- JsonValue.Create f.fracBits
    o["signed"] <- JsonValue.Create f.signed
    o

/// The format this module writes and reads.
let formatVersion = 2

let private streamNode (stream: BoundaryStream) : JsonNode =
    let o = JsonObject()
    o["name"] <- JsonValue.Create stream.name
    o["fields"] <- JsonArray(stream.fields |> List.map formatNode |> Array.ofList)
    o

let private connections = [ Connection.Wire; Connection.Forward; Connection.Backward; Connection.Both ]

/// The graph as a JSON node.
let rec private node (g: Graph) : JsonObject =
    let root = JsonObject()
    root["format"] <- JsonValue.Create formatVersion
    root["name"] <- JsonValue.Create g.name
    root["sampleRate"] <- JsonValue.Create g.sampleRate
    root["inputs"] <- JsonArray(g.inputs |> List.map streamNode |> Array.ofList)
    root["controls"] <- JsonArray(g.controls |> List.map formatNode |> Array.ofList)
    root["outputs"] <- JsonArray(g.outputs |> List.map streamNode |> Array.ofList)

    root["boxes"] <-
        JsonArray(
            [| for b in g.boxes ->
                   let o = JsonObject()
                   o["name"] <- JsonValue.Create b.name
                   o["unit"] <- JsonValue.Create b.unit
                   if b.copies <> 1 then
                       o["copies"] <- JsonValue.Create b.copies

                   if not b.ordered then
                       o["ordered"] <- JsonValue.Create false

                   let arguments = JsonObject()

                   for KeyValue(k, v) in b.arguments do
                       arguments[k] <- JsonValue.Create v

                   o["arguments"] <- arguments
                   let settings = JsonObject()

                   for KeyValue(k, v) in b.settings do
                       settings[k] <- JsonValue.Create v

                   o["settings"] <- settings
                   o :> JsonNode |]
        )

    root["controlBoxes"] <-
        JsonArray(
            [| for c in g.controlBoxes ->
                   let o = formatNode (c.name, c.format) :?> JsonObject
                   o["kind"] <- JsonValue.Create(match c.kind with NumberBox -> "number" | ConstantBox -> "constant")
                   o["value"] <- JsonValue.Create c.value
                   o :> JsonNode |]
        )

    root["edges"] <-
        JsonArray(
            [| for e in g.edges ->
                   let o = JsonObject()
                   o["from"] <- JsonValue.Create(showPort e.from)
                   o["to"] <- JsonValue.Create(showPort e.``to``)

                   if e.connection <> Connection.Wire then
                       o["connection"] <- JsonValue.Create(string e.connection)

                   o :> JsonNode |]
        )

    if not g.fanOuts.IsEmpty then
        let fanOuts = JsonObject()

        for KeyValue(p, pattern) in g.fanOuts do
            fanOuts[showPort p] <- JsonValue.Create(string pattern)

        root["fanOuts"] <- fanOuts

    if not g.merges.IsEmpty then
        let merges = JsonObject()

        for KeyValue(p, arbitration) in g.merges do
            let o = JsonObject()
            o["policy"] <- JsonValue.Create(string arbitration.policy)
            o["timing"] <- JsonValue.Create(string arbitration.timing)
            merges[showPort p] <- o

        root["merges"] <- merges

    let positions = JsonObject()

    for KeyValue(box, (x, y)) in g.positions do
        positions[box] <- JsonArray(JsonValue.Create x, JsonValue.Create y)

    root["positions"] <- positions
    let designs = JsonObject()

    for KeyValue(name, sub) in g.designs do
        designs[name] <- node sub

    root["designs"] <- designs
    let units = JsonObject()

    for KeyValue(name, source) in g.units do
        units[name] <- JsonValue.Create source

    root["units"] <- units

    match g.mapping with
    | Some m -> root["mapping"] <- JsonValue.Create m
    | None -> ()
    root

/// The graph as JSON text.
let write (g: Graph) : string =
    (node g).ToJsonString(JsonSerializerOptions(WriteIndented = true))

// ---------------------------------------------------------------------------
// Reading. Every field is asked for by name and refused by name when it is
// not there or not what it should be, so a hand-edited file fails at the
// line rather than as a null somewhere later.

let private field (o: JsonNode) (name: string) : Result<JsonNode, string> =
    match o with
    | :? JsonObject as o ->
        match o[name] with
        | null -> Error $"'{name}' is missing"
        | v -> Ok v
    | _ -> Error $"expected an object with '{name}'"

let private asString (what: string) (n: JsonNode) : Result<string, string> =
    match n with
    | :? JsonValue as v ->
        match v.TryGetValue<string>() with
        | true, s -> Ok s
        | _ -> Error $"{what}: expected text"
    | _ -> Error $"{what}: expected text"

let private asInt (what: string) (n: JsonNode) : Result<int, string> =
    match n with
    | :? JsonValue as v ->
        match v.TryGetValue<int>() with
        | true, i -> Ok i
        | _ -> Error $"{what}: expected a whole number"
    | _ -> Error $"{what}: expected a whole number"

let private asFloat (what: string) (n: JsonNode) : Result<float, string> =
    match n with
    | :? JsonValue as v ->
        match v.TryGetValue<float>() with
        | true, x -> Ok x
        | _ -> Error $"{what}: expected a number"
    | _ -> Error $"{what}: expected a number"

let private asBool (what: string) (n: JsonNode) : Result<bool, string> =
    match n with
    | :? JsonValue as v ->
        match v.TryGetValue<bool>() with
        | true, b -> Ok b
        | _ -> Error $"{what}: expected true or false"
    | _ -> Error $"{what}: expected true or false"

let private asArray (what: string) (n: JsonNode) : Result<JsonNode list, string> =
    match n with
    | :? JsonArray as a -> Ok(List.ofSeq a)
    | _ -> Error $"{what}: expected a list"

let private each (f: 'a -> Result<'b, string>) (xs: 'a list) : Result<'b list, string> =
    (Ok [], xs)
    ||> List.fold (fun acc x ->
        match acc, f x with
        | Ok ys, Ok y -> Ok(y :: ys)
        | Error e, _
        | _, Error e -> Error e)
    |> Result.map List.rev

let private readFormat (where: string) (n: JsonNode) : Result<string * NumberFormat, string> =
    let get name f = field n name |> Result.bind (f $"{where}: '{name}'")

    match get "name" asString, get "width" asInt, get "fraction" asInt, get "signed" asBool with
    | Ok name, Ok width, Ok fraction, Ok signed -> Ok(name, { totalWidth = width; fracBits = fraction; signed = signed })
    | Error e, _, _, _
    | _, Error e, _, _
    | _, _, Error e, _
    | _, _, _, Error e -> Error e

let private readStream (where: string) (n: JsonNode) : Result<BoundaryStream, string> =
    match field n "name" |> Result.bind (asString $"{where}: a stream's 'name'") with
    | Error e -> Error e
    | Ok name ->
        field n "fields"
        |> Result.bind (asArray $"{where} '{name}': 'fields'")
        |> Result.bind (each (readFormat $"{where} '{name}'"))
        |> Result.map (fun fields -> { name = name; fields = fields })

let private readPort (where: string) (text: string) : Result<PortRef, string> =
    match text.Split '.' with
    | [| box; port |] when box <> "" && port <> "" -> Ok { box = box; port = port }
    | _ -> Error $"{where}: '{text}' is not box.port"

let private readArguments (box: string) (factory: Factory) (n: JsonNode) : Result<Arguments, string> =
    match n with
    | :? JsonObject as o ->
        o
        |> List.ofSeq
        |> each (fun (KeyValue(k, v)) ->
            if not (factory.parameters |> List.exists (fun p -> p.name = k)) then
                Error $"box '{box}': {factory.name} has no parameter called '{k}'"
            else
                asString $"box '{box}': argument '{k}'" v |> Result.map (fun text -> k, text))
        |> Result.map (Map.ofList >> complete factory)
    | _ -> Error $"box '{box}': 'arguments' should be an object"

let private readBox (rate: float) (designs: Map<string, Graph>) (n: JsonNode) : Result<Box, string> =
    let get name f = field n name |> Result.bind (f $"a box's '{name}'")

    match get "name" asString, get "unit" asString with
    | Ok name, Ok unit ->
        match (units ()).TryFind unit, designs.TryFind unit with
        | None, None when List.contains unit structuralBoxes ->
            // A structural box: its arguments as typed, nothing to make.
            let arguments =
                match field n "arguments" with
                | Ok(:? JsonObject as o) ->
                    o |> List.ofSeq |> each (fun (KeyValue(k, v)) -> asString $"box '{name}': argument '{k}'" v |> Result.map (fun t -> k, t)) |> Result.map Map.ofList
                | _ -> Ok Map.empty

            arguments |> Result.map (newBox name unit)
        | None, None -> Error $"box '{name}': no unit called '{unit}' in the palette or among the design's designs"
        | factory, _ ->
            let settings =
                match field n "settings" with
                | Ok(:? JsonObject as o) ->
                    o |> List.ofSeq |> each (fun (KeyValue(k, v)) -> asString $"box '{name}': setting '{k}'" v |> Result.map (fun t -> k, t)) |> Result.map Map.ofList
                | Ok _ -> Error $"box '{name}': 'settings' should be an object"
                | Error _ -> Ok Map.empty

            // Absent means the default: one copy, in order.
            let copies = match field n "copies" |> Result.bind (asInt "copies") with Ok c -> c | Error _ -> 1
            let ordered = match field n "ordered" |> Result.bind (asBool "ordered") with Ok o -> o | Error _ -> true

            match factory, settings with
            | _, Error e -> Error e
            | None, Ok settings -> Ok { newBox name unit Map.empty with copies = copies; ordered = ordered; settings = settings }
            | Some factory, Ok settings ->
                // A file written before a unit had arguments has none: the defaults.
                let arguments =
                    match field n "arguments" with
                    | Ok node -> readArguments name factory node
                    | Error _ -> Ok(defaults factory)

                arguments
                |> Result.bind (fun arguments ->
                    // The factory's verdict, so a file never opens a box it cannot make.
                    factory.make rate arguments
                    |> Result.mapError (fun why -> $"box '{name}': {why}")
                    |> Result.map (fun _ -> { newBox name unit arguments with copies = copies; ordered = ordered; settings = settings }))
    | Error e, _
    | _, Error e -> Error e

let private readControlBox (n: JsonNode) : Result<ControlBox, string> =
    readFormat "a control box" n
    |> Result.bind (fun (name, format) ->
        let get k f = field n k |> Result.bind (f $"control box '{name}': '{k}'")

        match get "kind" asString, get "value" asString with
        | Ok "number", Ok value -> Ok { name = name; kind = NumberBox; format = format; value = value }
        | Ok "constant", Ok value -> Ok { name = name; kind = ConstantBox; format = format; value = value }
        | Ok other, _ -> Error $"control box '{name}': kind is number or constant, not '{other}'"
        | Error e, _
        | _, Error e -> Error e)

let private readEdge (n: JsonNode) : Result<Edge, string> =
    let get name = field n name |> Result.bind (asString $"an edge's '{name}'") |> Result.bind (readPort $"an edge's '{name}'")

    let connection =
        match field n "connection" with
        | Error _ -> Ok Connection.Wire
        | Ok node ->
            asString "an edge's 'connection'" node
            |> Result.bind (fun text ->
                match connections |> List.tryFind (fun c -> string c = text) with
                | Some c -> Ok c
                | None ->
                    let listed = connections |> List.map string |> String.concat ", "
                    Error $"an edge's 'connection': '{text}' is not one of {listed}")

    match get "from", get "to", connection with
    | Ok from, Ok ``to``, Ok connection -> Ok { from = from; ``to`` = ``to``; connection = connection }
    | Error e, _, _
    | _, Error e, _
    | _, _, Error e -> Error e

let private named (what: string) (choices: 'a list) (text: string) : Result<'a, string> =
    match choices |> List.tryFind (fun c -> string c = text) with
    | Some c -> Ok c
    | None ->
        let listed = choices |> List.map string |> String.concat ", "
        Error $"{what}: '{text}' is not one of {listed}"

/// The meetings a file states, by port: absent is the default everywhere.
let private readByPort (what: string) (read: string -> JsonNode -> Result<'a, string>) (root: JsonNode) : Result<Map<PortRef, 'a>, string> =
    match field root what with
    | Error _ -> Ok Map.empty
    | Ok(:? JsonObject as o) ->
        o
        |> List.ofSeq
        |> each (fun (KeyValue(k, v)) ->
            match readPort $"'{what}'" k, read $"'{what}' at {k}" v with
            | Ok p, Ok value -> Ok(p, value)
            | Error e, _
            | _, Error e -> Error e)
        |> Result.map Map.ofList
    | Ok _ -> Error $"'{what}': expected an object"

let private readFanOut (where: string) (n: JsonNode) : Result<FanOut, string> =
    asString where n |> Result.bind (named where [ FanOut.Broadcast; FanOut.Balance ])

let private readArbitration (where: string) (n: JsonNode) : Result<Arbitration, string> =
    let get name choices = field n name |> Result.bind (asString $"{where}: '{name}'") |> Result.bind (named $"{where}: '{name}'" choices)

    match
        get "policy" [ ArbitrationPolicy.RoundRobin; ArbitrationPolicy.Priority ],
        get "timing" [ ArbitrationTiming.SameCycle; ArbitrationTiming.Registered ]
    with
    | Ok policy, Ok timing -> Ok { policy = policy; timing = timing }
    | Error e, _
    | _, Error e -> Error e

let private readPositions (n: JsonNode) : Result<Map<string, float * float>, string> =
    match n with
    | :? JsonObject as o ->
        o
        |> List.ofSeq
        |> each (fun (KeyValue(box, at)) ->
            asArray $"position of '{box}'" at
            |> Result.bind (function
                | [ x; y ] ->
                    match asFloat $"position of '{box}'" x, asFloat $"position of '{box}'" y with
                    | Ok x, Ok y -> Ok(box, (x, y))
                    | Error e, _
                    | _, Error e -> Error e
                | _ -> Error $"position of '{box}': expected [x, y]"))
        |> Result.map Map.ofList
    | _ -> Error "'positions': expected an object"

/// The format a file says it is in. A design drawn field to field has no
/// `format` and a `wires` list, and cannot be read as streams: a field
/// that skipped a box has no stream to be.
let private checkFormat (root: JsonNode) : Result<unit, string> =
    match field root "format" |> Result.bind (asInt "'format'") with
    | Ok v when v = formatVersion -> Ok()
    | Ok v -> Error $"this file is in format %d{v}, and this reads format %d{formatVersion}"
    | Error _ ->
        Error
            $"this design was drawn field to field, before boxes had stream ports (it has no 'format'; this reads format %d{formatVersion}). Redraw it: one edge per stream"

/// A JSON node as a graph, or the first thing wrong with it.
let rec private readGraph (root: JsonNode) : Result<Graph, string> =
        let get name f = field root name |> Result.bind f
        let formats name = get name (asArray $"'{name}'") |> Result.bind (each (readFormat name))
        let streams name = get name (asArray $"'{name}'") |> Result.bind (each (readStream name))

        // The name first, so a file that is not a design is refused by the
        // field a person looks for first.
        get "name" (asString "'name'")
        |> Result.bind (fun _ -> checkFormat root)
        |> Result.bind (fun () -> get "sampleRate" (asFloat "'sampleRate'"))
        |> Result.bind (fun rate ->
            // The units written in the GUI before the boxes, since a box may
            // be one: compiled here when this head can, refused when it cannot.
            let units =
                match field root "units" with
                | Ok(:? JsonObject as o) ->
                    o
                    |> List.ofSeq
                    |> each (fun (KeyValue(k, v)) ->
                        asString $"unit '{k}'" v
                        |> Result.bind (fun source ->
                            if (units ()).ContainsKey k then
                                Ok(k, source)
                            else
                                match compileUnit with
                                | None -> Error $"unit '{k}' was written in the GUI, and this head has no compiler to open it with"
                                | Some compile -> compile k source |> Result.bind addSessionUnit |> Result.map (fun () -> k, source)))
                    |> Result.map Map.ofList
                | Ok _ -> Error "'units': expected an object"
                | Error _ -> Ok Map.empty

            // The designs before the boxes, since a box may be one of them.
            let designs =
                match field root "designs" with
                | Ok(:? JsonObject as o) ->
                    o |> List.ofSeq |> each (fun (KeyValue(k, v)) -> readGraph v |> Result.mapError (fun why -> $"design '{k}': {why}") |> Result.map (fun sub -> k, sub)) |> Result.map Map.ofList
                | Ok _ -> Error "'designs': expected an object"
                | Error _ -> Ok Map.empty

            match
                get "name" (asString "'name'"),
                streams "inputs",
                formats "controls",
                streams "outputs",
                units |> Result.bind (fun units -> designs |> Result.bind (fun designs -> get "boxes" (asArray "'boxes'") |> Result.bind (each (readBox rate designs)) |> Result.map (fun boxes -> boxes, designs, units))),
                (match field root "controlBoxes" with
                 | Ok node -> asArray "'controlBoxes'" node |> Result.bind (each readControlBox)
                 | Error _ -> Ok []),
                get "edges" (asArray "'edges'") |> Result.bind (each readEdge),
                (match readByPort "fanOuts" readFanOut root, readByPort "merges" readArbitration root, get "positions" readPositions with
                 | Ok fanOuts, Ok merges, Ok positions -> Ok(fanOuts, merges, positions)
                 | Error e, _, _
                 | _, Error e, _
                 | _, _, Error e -> Error e)
            with
            | Ok name, Ok inputs, Ok controls, Ok outputs, Ok(boxes, designs, units), Ok controlBoxes, Ok edges, Ok(fanOuts, merges, positions) ->
                Ok
                    { name = name
                      sampleRate = rate
                      inputs = inputs
                      controls = controls
                      outputs = outputs
                      boxes = boxes
                      controlBoxes = controlBoxes
                      edges = edges
                      fanOuts = fanOuts
                      merges = merges
                      positions = positions
                      designs = designs
                      units = units
                      mapping =
                        match field root "mapping" with
                        | Ok node ->
                            match asString "'mapping'" node with
                            | Ok m -> Some m
                            | Error _ -> None
                        | Error _ -> None }
            | Error e, _, _, _, _, _, _, _
            | _, Error e, _, _, _, _, _, _
            | _, _, Error e, _, _, _, _, _
            | _, _, _, Error e, _, _, _, _
            | _, _, _, _, Error e, _, _, _
            | _, _, _, _, _, Error e, _, _
            | _, _, _, _, _, _, Error e, _
            | _, _, _, _, _, _, _, Error e -> Error e)

/// The JSON text as a graph, or the first thing wrong with it.
let parse (text: string) : Result<Graph, string> =
    try
        readGraph (JsonNode.Parse text)
    with e ->
        Error $"not JSON: {e.Message}"

let save (path: string) (g: Graph) : unit = System.IO.File.WriteAllText(path, write g)

let load (path: string) : Result<Graph, string> =
    if System.IO.File.Exists path then
        parse (System.IO.File.ReadAllText path)
    else
        Error $"{path}: no such file"
