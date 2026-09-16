/// A design as a file: the `Graph` as JSON. It holds the boundary's pins
/// with their formats, the boxes by unit name, the wires and the positions
/// — and no types, because the graph holds none: the **palette is the type
/// authority**, resolved by name on every load, as a Pure Data file resolves
/// `biquad~` against the external's code. So a unit whose pins have changed
/// refuses an old design at the exact wire, by name, rather than opening it
/// wrong.
///
/// ```json
/// { "name": "GainPatch", "streams": 1, "sampleRate": 48000,
///   "inputs":   [ { "name": "left", "width": 24, "fraction": 0, "signed": true }, … ],
///   "controls": [ { "name": "volume", "width": 16, "fraction": 0, "signed": false }, … ],
///   "outputs":  [ … ],
///   "boxes":    [ { "name": "eq", "unit": "eq", "copies": 1,
///                   "arguments": { "shape": "lowshelf", "fc": "200", "q": "0.707", "gain": "6" } } ],
///   "wires":    [ { "from": "input.left", "to": "gain.left" }, … ],
///   "positions": { "input": [60, 120], "gain": [320, 120], "output": [580, 120] } }
/// ```
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

let private pinText (p: PinRef) = $"{p.box}.{p.pin}"

/// The graph as JSON text.
let write (g: Graph) : string =
    let root = JsonObject()
    root["name"] <- JsonValue.Create g.name
    root["streams"] <- JsonValue.Create g.streams
    root["sampleRate"] <- JsonValue.Create g.sampleRate
    root["inputs"] <- JsonArray(g.inputs |> List.map formatNode |> Array.ofList)
    root["controls"] <- JsonArray(g.controls |> List.map formatNode |> Array.ofList)
    root["outputs"] <- JsonArray(g.outputs |> List.map formatNode |> Array.ofList)

    root["boxes"] <-
        JsonArray(
            [| for b in g.boxes ->
                   let o = JsonObject()
                   o["name"] <- JsonValue.Create b.name
                   o["unit"] <- JsonValue.Create b.unit
                   o["copies"] <- JsonValue.Create b.copies
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

    root["wires"] <-
        JsonArray(
            [| for e in g.edges ->
                   let o = JsonObject()
                   o["from"] <- JsonValue.Create(pinText e.from)
                   o["to"] <- JsonValue.Create(pinText e.``to``)
                   o :> JsonNode |]
        )

    let positions = JsonObject()

    for KeyValue(box, (x, y)) in g.positions do
        positions[box] <- JsonArray(JsonValue.Create x, JsonValue.Create y)

    root["positions"] <- positions
    root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

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

let private readPin (where: string) (text: string) : Result<PinRef, string> =
    match text.Split '.' with
    | [| box; pin |] when box <> "" && pin <> "" -> Ok { box = box; pin = pin }
    | _ -> Error $"{where}: '{text}' is not box.pin"

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

let private readBox (rate: float) (n: JsonNode) : Result<Box, string> =
    let get name f = field n name |> Result.bind (f $"a box's '{name}'")

    match get "name" asString, get "unit" asString, get "copies" asInt with
    | Ok name, Ok unit, Ok copies ->
        match palette.TryFind unit with
        | None -> Error $"box '{name}': no unit called '{unit}' in the palette"
        | Some factory ->
            // A file written before a unit had arguments has none: the defaults.
            let arguments =
                match field n "arguments" with
                | Ok node -> readArguments name factory node
                | Error _ -> Ok(defaults factory)

            let settings =
                match field n "settings" with
                | Ok(:? JsonObject as o) ->
                    o |> List.ofSeq |> each (fun (KeyValue(k, v)) -> asString $"box '{name}': setting '{k}'" v |> Result.map (fun t -> k, t)) |> Result.map Map.ofList
                | Ok _ -> Error $"box '{name}': 'settings' should be an object"
                | Error _ -> Ok Map.empty

            match arguments, settings with
            | Ok arguments, Ok settings ->
                // The factory's verdict, so a file never opens a box it cannot make.
                factory.make rate arguments
                |> Result.mapError (fun why -> $"box '{name}': {why}")
                |> Result.map (fun _ -> { name = name; unit = unit; copies = copies; arguments = arguments; settings = settings })
            | Error e, _
            | _, Error e -> Error e
    | Error e, _, _
    | _, Error e, _
    | _, _, Error e -> Error e

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

let private readWire (n: JsonNode) : Result<Edge, string> =
    let get name = field n name |> Result.bind (asString $"a wire's '{name}'") |> Result.bind (readPin $"a wire's '{name}'")

    match get "from", get "to" with
    | Ok from, Ok ``to`` -> Ok { from = from; ``to`` = ``to`` }
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

/// Every wire's ends looked up against the palette's pins — the check that
/// makes the palette the type authority: a pin that no longer exists, or
/// whose format has changed, refuses the design here, naming the wire.
let private checkWires (g: Graph) : Result<Graph, string> =
    g.edges
    |> each (fun e ->
        match lookupPin g Out e.from, lookupPin g In e.``to`` with
        | Error why, _
        | _, Error why -> Error $"wire {pinText e.from} → {pinText e.``to``}: {why}"
        | Ok(_, fa), Ok(_, fb) when fa <> fb ->
            Error $"wire {pinText e.from} → {pinText e.``to``}: {pinText e.from} is {describeFormat fa}, {pinText e.``to``} is {describeFormat fb}"
        | Ok _, Ok _ -> Ok e)
    |> Result.map (fun _ -> g)

/// The JSON text as a graph, or the first thing wrong with it.
let parse (text: string) : Result<Graph, string> =
    let root =
        try
            Ok(JsonNode.Parse text)
        with e ->
            Error $"not JSON: {e.Message}"

    root
    |> Result.bind (fun root ->
        let get name f = field root name |> Result.bind f
        let formats name = get name (asArray $"'{name}'") |> Result.bind (each (readFormat name))

        // The name first, so a file that is not a design is refused by the
        // field a person looks for first.
        get "name" (asString "'name'")
        |> Result.bind (fun _ -> get "sampleRate" (asFloat "'sampleRate'"))
        |> Result.bind (fun rate ->
            match
                get "name" (asString "'name'"),
                get "streams" (asInt "'streams'"),
                formats "inputs",
                formats "controls",
                formats "outputs",
                get "boxes" (asArray "'boxes'") |> Result.bind (each (readBox rate)),
                (match field root "controlBoxes" with
                 | Ok node -> asArray "'controlBoxes'" node |> Result.bind (each readControlBox)
                 | Error _ -> Ok []),
                get "wires" (asArray "'wires'") |> Result.bind (each readWire),
                get "positions" readPositions
            with
            | Ok name, Ok streams, Ok inputs, Ok controls, Ok outputs, Ok boxes, Ok controlBoxes, Ok wires, Ok positions ->
                checkWires
                    { name = name
                      streams = streams
                      sampleRate = rate
                      inputs = inputs
                      controls = controls
                      outputs = outputs
                      boxes = boxes
                      controlBoxes = controlBoxes
                      edges = wires
                      positions = positions }
            | Error e, _, _, _, _, _, _, _, _
            | _, Error e, _, _, _, _, _, _, _
            | _, _, Error e, _, _, _, _, _, _
            | _, _, _, Error e, _, _, _, _, _
            | _, _, _, _, Error e, _, _, _, _
            | _, _, _, _, _, Error e, _, _, _
            | _, _, _, _, _, _, Error e, _, _
            | _, _, _, _, _, _, _, Error e, _
            | _, _, _, _, _, _, _, _, Error e -> Error e))

let save (path: string) (g: Graph) : unit = System.IO.File.WriteAllText(path, write g)

let load (path: string) : Result<Graph, string> =
    if System.IO.File.Exists path then
        parse (System.IO.File.ReadAllText path)
    else
        Error $"{path}: no such file"
