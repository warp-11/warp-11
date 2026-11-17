/// A design as F# source: the typed form, the one a person would have
/// written. A box is its unit applied once (`stage`, or `farm` with
/// copies), an edge is `connectAs` its connection, a meeting is the library
/// call for its pattern, a shared service is `sharingWith` around the
/// stages that use it — the calls `Elaborate` makes, in the order it makes
/// them. One way: no graph can be read back from it, which is why the
/// design file is JSON and this is an export.
///
/// The printer is honest only if what it prints elaborates to the bytes the
/// graph does, and that is its one check (UD11): the source is compiled and
/// its Verilog compared.
module Warp11.Export

open Warp11
open Warp11.Fu
open Warp11.Factories
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate

/// F# keywords a field, a stream or a port may be called, escaped in the source.
let private keywords =
    set
        [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate"; "do"; "done"; "downcast"; "downto"; "elif"; "else"; "end"
          "exception"; "extern"; "false"; "finally"; "fixed"; "for"; "fun"; "function"; "global"; "if"; "in"; "inherit"; "inline"; "interface"; "internal"
          "lazy"; "let"; "match"; "member"; "module"; "mutable"; "namespace"; "new"; "not"; "null"; "of"; "open"; "or"; "override"; "private"; "public"
          "rec"; "return"; "select"; "sig"; "static"; "struct"; "then"; "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while"; "with"; "yield"
          "const"; "params"; "process"; "pure"; "tailcall"; "trait"; "virtual"; "atomic"; "break"; "checked"; "component"; "constraint"; "constructor"; "continue"
          "eager"; "event"; "external"; "functor"; "include"; "method"; "mixin"; "object"; "parallel"; "protected"; "sealed"; "volatile" ]

let private ident (name: string) = if keywords.Contains name then $"``{name}``" else name

let private showFormat (f: NumberFormat) =
    if f.fracBits = 0 then
        (if f.signed then $"signedInt %d{f.totalWidth}" else $"unsignedInt %d{f.totalWidth}")
    else
        let signed = if f.signed then "true" else "false"
        $"({{ totalWidth = %d{f.totalWidth}; fracBits = %d{f.fracBits}; signed = {signed} }}: NumberFormat)"

/// `pinsN (name, format) …` for a beat of N fields — the typed form goes to six.
let private showPins (what: string) (pins: (string * NumberFormat) list) : Result<string, string> =
    match pins.Length with
    | 0 -> Error $"export: {what} has no fields"
    | n when n > 6 -> Error $"export: {what} is %d{n} fields wide, and the typed form's pins go to six"
    | n ->
        let fields = pins |> List.map (fun (name, f) -> $"(\"{name}\", {showFormat f})") |> String.concat " "
        Ok $"pins%d{n} {fields}"

let private tuple (names: string list) =
    match names with
    | [ x ] -> x
    | xs -> "(" + String.concat ", " xs + ")"

let private tupleValue (names: string list) =
    match names with
    | [ x ] -> x
    | xs -> String.concat ", " xs

/// The design's name as a value: first letter down.
let private valueName (name: string) =
    ident (string (System.Char.ToLowerInvariant name[0]) + name.Substring 1)

/// The names a design's io tuple binds: a stream each for its inputs and
/// outputs, under the streams' own names (an output called what an input is
/// gets `_out`), then a port per control. What its own module destructures,
/// and what a parent destructures to reach an instance's.
let private ioNames (g: Graph) =
    let ins = [ for s in g.inputs -> s.name ]
    let controls = [ for n, _ in controlPorts g -> n ]
    let outs = [ for s in g.outputs -> if List.contains s.name (ins @ controls) then $"{s.name}_out" else s.name ]
    List.map ident ins, List.map ident outs, List.map ident controls

let private showArbitration (a: Arbitration) =
    if a = Arbitration.roundRobin then
        "Arbitration.roundRobin"
    else
        $"{{ policy = ArbitrationPolicy.{a.policy}; timing = ArbitrationTiming.{a.timing} }}"

let private collect (rs: Result<'a, string> list) : Result<'a list, string> =
    (Ok [], rs)
    ||> List.fold (fun acc r ->
        match acc, r with
        | Ok xs, Ok x -> Ok(x :: xs)
        | Error e, _
        | _, Error e -> Error e)
    |> Result.map List.rev

/// One design as a `let`: the typed form, its sub-designs already printed
/// above it under their own names.
let rec private printDesign (g: Graph) : Result<string, string> =
    try
        checkEdges g
        let order = edgeOrder g
        let services = g.boxes |> List.filter (isService g)

        // Which boxes read their operands at their formats, and which
        // operands: found by elaborating, so the print cannot disagree.
        let reads = System.Collections.Generic.Dictionary<string, bool list>()
        Warp11.Verilog.emitDesign (elaborateTracing (fun box unread -> reads[box] <- unread) g).def |> ignore

        let ins, outs, controls = ioNames g
        let controlName = Map.ofList (List.zip (List.map fst (controlPorts g)) controls)

        // A stream's name where it leaves a port, and each edge's branch.
        let leavingName (p: PortRef) = ident $"{p.box}_{p.port}"
        let branchesName (p: PortRef) = ident $"{p.box}_{p.port}_branches"
        let reachName (stream: string) = ident $"output_{stream}"
        let outputBox = "output"

        let branch (e: Edge) =
            match edgesFrom g e.from with
            | [ _ ] -> leavingName e.from
            | edges -> $"{branchesName e.from}[%d{List.findIndex ((=) e) edges}]"

        // The lines that name a stream leaving a port: the stream, and its
        // fan-out where several edges leave.
        let leaving (p: PortRef) (expression: string list) =
            let named =
                match expression with
                | [ one ] -> [ $"let {leavingName p} = {one}" ]
                | first :: rest -> [ $"let {leavingName p} ="; "    " + first ] @ [ for line in rest -> "    " + line ]
                | [] -> []

            match edgesFrom g p with
            | [ _ ] -> named
            | edges ->
                let call =
                    match fanOutAt g p with
                    | FanOut.Broadcast -> "streamForkEager"
                    | FanOut.Balance -> "streamBalance"

                named @ [ $"let {branchesName p} = {leavingName p} |> {call} %d{edges.Length}" ]

        // The stream that reaches a port, as an expression.
        let arriving (sink: PortRef) =
            let through (e: Edge) =
                if e.connection = Connection.Wire then branch e else $"{branch e} |> connectAs Connection.{e.connection}"

            match edgesInto g sink |> List.map through with
            | [ one ] -> one
            | several ->
                let listed = several |> List.map (fun s -> if s.Contains "|>" then $"({s})" else s) |> String.concat "; "
                $"streamMergeWith {showArbitration (mergeAt g sink)} [ {listed} ]"

        // A structural box: the call code makes between stages.
        let structuralOf (b: Box) : Result<string list, string> =
            let outFields = (snd (streamsOf g b.name)).Head.fields
            let wrapped (expression: string) = if expression.Contains "|>" then $"({expression})" else expression

            match showPins $"{b.name}'s fields" outFields with
            | Error e -> Error e
            | Ok outPins ->
                if b.unit = fifoBox then
                    let depth = structuralArgument b "depth"
                    Ok(leaving (port b.name streamOutlet) [ arriving (port b.name streamInlet); $"|> streamFifo \"{b.name}\" {depth}" ])
                elif b.unit = fieldsBox then
                    let arrive = (fst (streamsOf g b.name)).Head.fields |> List.map fst
                    let picked = fieldPicks b |> List.map snd
                    let pattern = arrive |> List.map (fun n -> if List.contains n picked then ident n else "_")

                    Ok(
                        leaving
                            (port b.name streamOutlet)
                            [ arriving (port b.name streamInlet)
                              $"|> streamMapTo ({outPins}) (fun {tuple pattern} -> {tupleValue (List.map ident picked)})" ]
                    )
                else
                    let sideFields = (fst (streamsOf g b.name)) |> List.map (fun side -> side.fields)

                    if sideFields |> List.forall (fun fields -> fields.Length = 1) then
                        let sides = joinInputs b |> List.map (fun side -> wrapped (arriving (port b.name side))) |> String.concat "; "
                        Ok(leaving (port b.name streamOutlet) [ $"streamJoin ({outPins}) [ {sides} ]" ])
                    else
                        // A side that carries several fields: each side as
                        // its fields in a list, joined as groups.
                        let side (name: string) (fields: Fields) =
                            let names = fields |> List.map (fst >> ident)
                            let formats = fields |> List.map (fun (n, f) -> $"\"{n}\", {showFormat f}") |> String.concat "; "
                            let listed = String.concat "; " names
                            $"({arriving (port b.name name)} |> streamMapTo (layoutOfList [ {formats} ]) (fun {tuple names} -> [ {listed} ]))"

                        let sides = List.map2 side (joinInputs b) sideFields

                        Ok(
                            leaving
                                (port b.name streamOutlet)
                                ([ $"streamJoinGroups ({outPins})" ]
                                 @ [ for i, text in List.indexed sides ->
                                         (if i = 0 then "    [ " else "      ") + text + (if i = sides.Length - 1 then " ]" else "") ])
                        )

        let stageOf (b: Box) : Result<string list, string> =
            if isStructural b then structuralOf b else

            let unit =
                match designOf g b with
                | Some sub ->
                    let subIns, subOuts, subControls = ioNames sub

                    match sub.inputs, sub.outputs with
                    | [ input ], [ output ] ->
                        match showPins $"{sub.name}'s input" input.fields, showPins $"{sub.name}'s output" output.fields with
                        | Ok inPins, Ok outPins ->
                            let formats = [ for n, f in controlPorts sub -> $"\"{n}\", {showFormat f}" ] |> String.concat "; "
                            let formatList = if formats = "" then "[]" else $"[ {formats} ]"
                            let ports = if subControls.IsEmpty then "[]" else "[ " + String.concat "; " subControls + " ]"
                            let pattern = tuple (subIns @ subOuts @ subControls)
                            Ok $"designUnit \"{sub.name}\" ({inPins}) ({outPins}) {formatList} {valueName sub.name} (fun {pattern} -> {subIns.Head}, {subOuts.Head}, {ports})"
                        | Error e, _
                        | _, Error e -> Error e
                    | _ -> Error $"export: {sub.name} as a box has one stream in and one out"
                | None ->
                    let factory = (units ())[b.unit]
                    let arguments = complete factory b.arguments

                    match factory.served with
                    | Some served ->
                        served.print g.sampleRate arguments (fun socket ->
                            edgesFrom g (port b.name socket) |> List.tryHead |> Option.map (fun e -> ident e.``to``.box))
                    | None -> factory.print g.sampleRate arguments

            let controlValues =
                [ for n, f in controlsOf g b.name ->
                      match g.edges |> List.tryFind (fun e -> e.``to`` = port b.name n) with
                      | None -> $"lit %d{settingBits g b (n, f)}UL %d{f.totalWidth}"
                      | Some e when e.from.box = "input" -> controlName[e.from.port]
                      | Some e ->
                          match controlBoxOf g e.from.box with
                          | Some c when c.kind = NumberBox -> controlName[c.name]
                          | Some c -> $"lit %d{controlValueBits c}UL %d{f.totalWidth}"
                          | None -> failwith $"{showEdge e}: a control wired from a unit's box is not built" ]

            let controlList = if controlValues.IsEmpty then "[]" else "[ " + String.concat "; " controlValues + " ]"

            unit
            |> Result.bind (fun unit ->
                match streamsOf g b.name with
                | [ input ], [ output ] ->
                    let call =
                        if b.copies = 1 then
                            $"stage {controlList} ({unit}) \"{b.name}\""
                        else
                            let order = if b.ordered then "Farm.InOrder" else "Farm.AsDone"
                            $"farm {order} {controlList} ({unit}) \"{b.name}\" %d{b.copies}"

                    // The operands read at their formats, where the
                    // elaboration read them.
                    let read =
                        match reads.TryGetValue b.name with
                        | true, unread ->
                            let fields = (unitOf g b).operands.fields
                            let names = fields |> List.map (fst >> ident)

                            let values =
                                List.zip3 names fields unread
                                |> List.map (fun (n, (_, f), u) -> if not u then n elif f.signed then $"asSInt {n}" else $"asUInt {n}")

                            match showPins $"{b.name}'s operands" fields with
                            | Ok pins -> Ok [ $"|> streamMapTo ({pins}) (fun {tuple names} -> {tupleValue values})" ]
                            | Error e -> Error e
                        | _ -> Ok []

                    read
                    |> Result.map (fun read ->
                        leaving (port b.name output.name) ([ arriving (port b.name input.name) ] @ read @ [ $"|> {call}" ]))
                | _ -> Error $"export: {b.name} has more than one stream in or out")

        let inPins = g.inputs |> List.map (fun s -> showPins $"the stream '{s.name}'" s.fields) |> collect
        let outPins = g.outputs |> List.map (fun s -> showPins $"the stream '{s.name}'" s.fields) |> collect

        match inPins, outPins, order |> List.map stageOf |> collect with
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e
        | Ok inPins, Ok outPins, Ok stages ->
            let ioFactory =
                [ for s, pins in List.zip g.inputs inPins -> $"streamInputPorts p \"{s.name}\" ({pins})" ]
                @ [ for s, pins in List.zip g.outputs outPins -> $"streamOutputPorts p \"{s.name}\" ({pins})" ]
                @ [ for n, f in controlPorts g -> $"p.inPort \"{n}\" %d{f.totalWidth}" ]

            let sources =
                [ for s, name in List.zip g.inputs ins do
                      yield! leaving (port "input" s.name) [ $"streamSource {name}" ] ]

            let reaching = [ for s in g.outputs -> (let reaches = arriving (port outputBox s.name) in $"let {reachName s.name} = {reaches}") ]
            let reached = [ for s in g.outputs -> reachName s.name ]
            let inner = sources @ List.concat stages @ reaching

            // The services around the stages, outermost first: in place a
            // `let`, shared a `sharingWith` whose body is everything inside.
            let rec serving (remaining: Box list) (indent: string) : string list =
                match remaining with
                | [] -> [ for line in inner -> indent + line ] @ [ indent + tupleValue reached ]
                | service :: rest ->
                    let factory = (units ())[service.unit]

                    match factory.print g.sampleRate (complete factory service.arguments) with
                    | Error e -> failwith e
                    | Ok unit ->
                        if clientsOf g service = 1 then
                            [ $"{indent}let {ident service.name} = inPlace ({unit})" ] @ serving rest indent
                        else
                            let arbitration = showArbitration (mergeAt g (port service.name servicePort))

                            let opening =
                                if service.copies = 1 then
                                    $"{indent}sharingWith {arbitration} ({unit}) \"{service.name}\" (fun {ident service.name} ->"
                                else
                                    $"{indent}sharingAmong {arbitration} %d{service.copies} ({unit}) \"{service.name}\" (fun {ident service.name} ->"

                            [ opening ]
                            @ serving rest (indent + "    ")
                            |> fun lines -> (lines |> List.take (lines.Length - 1)) @ [ List.last lines + ")" ]

            let bodyIndent = "            "

            let body =
                if services.IsEmpty then
                    [ for line in inner -> bodyIndent + line ]
                    @ [ for s, out in List.zip g.outputs outs -> $"{bodyIndent}streamSink {out} {reachName s.name}" ]
                else
                    [ $"{bodyIndent}let {tuple reached} =" ]
                    @ serving services (bodyIndent + "    ")
                    @ [ "" ]
                    @ [ for s, out in List.zip g.outputs outs -> $"{bodyIndent}streamSink {out} {reachName s.name}" ]

            // The last sink closes the body.
            let body = (body |> List.take (body.Length - 1)) @ [ List.last body + ")" ]

            Ok(
                String.concat
                    "\n"
                    ([ $"let {valueName g.name} ="
                       "    defModule"
                       $"        \"{g.name}\""
                       "        (fun p ->"
                       "            " + String.concat ",\n            " ioFactory + ")"
                       $"        (fun {tuple (ins @ outs @ controls)} ->" ]
                     @ body)
            )
    with e ->
        Error e.Message

/// Every design a design uses, deepest first and each once, then the design
/// itself — the order the source needs them in.
let rec private designsInOrder (g: Graph) : Graph list =
    let inner = [ for KeyValue(_, sub) in g.designs do yield! designsInOrder sub ]
    (inner |> List.distinctBy (fun d -> d.name)) @ [ g ]

/// The units written in the GUI that a design and the designs inside it
/// use: their definitions, each once, printed above everything.
let rec private definitionsOf (g: Graph) : string list =
    let own =
        [ for b in g.boxes do
              match (units ()).TryFind b.unit with
              | Some { definition = Some source } -> yield source
              | _ -> () ]

    (own @ [ for KeyValue(_, sub) in g.designs do yield! definitionsOf sub ]) |> List.distinct

/// The source, or why it cannot be written: one module, the design's rate,
/// the units written for it, the designs it uses as values above it, and
/// the design itself.
let exportWith (mapping: Warp11.Mapping.Mapping option) (g: Graph) : Result<string, string> =
    let target =
        match mapping with
        | None -> []
        | Some m ->
            let path =
                match m.path with
                // The exported F# names the board, which is what a person
                // would have written; a path that is not one prints as its
                // bindings.
                | p ->
                    match Warp11.Mapping.pathText p with
                    | "pins" -> "viaPins"
                    | "memory" -> "viaHostMemory"
                    | "count" -> "viaCount"
                    | "scatter" -> "viaScatter"
                    | "link" -> "viaLink"
                    | "countlink" -> "viaCountLink"
                    | "registers" -> "viaRegisters"
                    | _ ->
                        let binding (b: Binding) = $"{{ need = {b.need}; carrier = {b.carrier} }}"
                        $"""{{ bindings = [ {String.concat "; " (List.map binding p.bindings)} ] }}"""

            let provenance =
                match Warp11.Boards.nameOf m.board with
                | Some name -> $"/// The target: the `{name}` board, every axis spelled out."
                | None -> "/// The target, every axis spelled out."

            let ins, outs, controls = ioNames g
            let ports = controlPorts g

            let input, output =
                match g.inputs, g.outputs with
                | [ input ], [ output ] -> input, output
                | _ -> failwith $"export: {g.name} on a board carries one stream in and one out"

            let q (text: string) = "\"" + text + "\""
            let fmtList (pins: (string * NumberFormat) list) = pins |> List.map (fun (n, f) -> q n + ", " + showFormat f) |> String.concat "; "
            let listOf (items: string) = if items = "" then "[]" else "[ " + items + " ]"
            let startingOf = startingValues g |> Map.ofList
            let needValues =
                [ for n, f in ports ->
                      "Warp11.BoardTop.valueFrom " + q n + " (" + showFormat f + ") " + string (startingOf |> Map.tryFind n |> Option.defaultValue 0UL) + "UL" ]
            let fieldsIn = [ for i in 0 .. input.fields.Length - 1 -> "fields[" + string i + "]" ]
            let outNames = [ for n, _ in output.fields -> ident n ]
            let portList = [ for (n, _), v in List.zip ports controls -> q n + ", " + v ] |> String.concat "; "
            let inPins = match showPins "the input stream" input.fields with Ok p -> p | Error e -> failwith e
            let ioPattern = tuple (ins @ outs @ controls)
            let outList = "[ " + String.concat "; " outNames + " ]"

            [ ""
              provenance
              "let board ="
              Warp11.Mapping.showBoard m.board
              ""
              "let path = " + path
              ""
              "/// The design as a board top takes it: its boundary as needs, and the"
              "/// instance under a name."
              "let design: Warp11.BoardTop.Design ="
              "    let needs ="
              "        [ Warp11.BoardTop.streamIn \"in\" " + listOf (fmtList input.fields)
              "          Warp11.BoardTop.streamOut \"out\" " + listOf (fmtList output.fields) + (if needValues = [] then " ]" else "")
            ]
            @ [ for i, v in List.indexed needValues -> "          " + v + (if i = needValues.Length - 1 then " ]" else "") ]
            @ [ ""
                "    { name = " + q g.name
                "      sampleRate = sampleRate"
                "      streams = 1"
                "      needs = needs"
                "      answers = " + string (answersOf g)
                "      atClocking = None"
                "      body ="
                "        Warp11.BoardTop.rigged needs (fun instance ->"
                "            let " + ioPattern + " = " + valueName g.name + ".NewNamed instance"
                ""
                "            { through ="
                "                fun s ->"
                "                    s"
                "                    |> streamMapTo (" + inPins + ") (fun fields -> " + tupleValue fieldsIn + ")"
                "                    |> streamThroughInstance " + ins.Head + " " + outs.Head
                "                    |> streamMapTo (layoutOfList " + listOf (fmtList output.fields) + ") (fun " + tuple outNames + " -> " + outList + ")"
                "              ports = " + listOf portList
                "              readbacks = [] }) }"
                ""
                "/// The build directory for this design on the board: `build \"out\"` writes"
                "/// what the board's toolchain builds from, and says how to run it."
                "let build (dir: string) = Warp11.Build.write dir (Warp11.BoardTop.boardTop board path design)" ]

    designsInOrder g
    |> List.distinctBy (fun d -> d.name)
    |> List.map printDesign
    |> List.fold (fun acc r -> match acc, r with | Ok xs, Ok x -> Ok(xs @ [ x ]) | Error e, _ | _, Error e -> Error e) (Ok [])
    |> Result.map (fun blocks ->
        let blocks = definitionsOf g @ blocks

        String.concat
            "\n"
            ([ $"/// {g.name}, exported from the design canvas: the typed form of the drawn"
               "/// design, elaborating to the same bytes."
               $"module Exported.{g.name}"
               ""
               "open Warp11"
               "open Warp11.Fu"
               "open Warp11.Units"
               "open Warp11.Pedal"
               "open Warp11.Factories"
               "open Warp11.Elaborate"
               "open Warp11.Audio"
               // What the units this design names came from, so the source
               // compiles where they live rather than only where it was
               // written.
               yield!
                   (g.boxes
                    |> List.collect (fun b -> (units ()) |> Map.tryFind b.unit |> Option.map (fun f -> f.opens) |> Option.defaultValue [])
                    |> List.distinct
                    |> List.sort
                    |> List.map (fun m -> $"open {m}"))
               ""
               $"let sampleRate = {showFloat g.sampleRate}"
               "" ]
             @ [ String.concat "\n\n" blocks ]
             @ target)
        + "\n")

/// `exportWith` no mapping: the design alone.
let export (g: Graph) : Result<string, string> = exportWith None g
