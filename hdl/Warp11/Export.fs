/// A design as F# source: the typed form, the one a person would have
/// written — a `defModule` with one `fuStagesWith` per box, tuple beats
/// named after the pins, units by their F# names with their creation
/// arguments as values. One way: the in-place row dissolves into an
/// expression, so no graph can be read back from it, which is why the design
/// file is JSON and this is an export.
///
/// The printer is honest only if what it prints elaborates to the bytes the
/// graph does, and that is its one check (UD11): the source is compiled and
/// its Verilog compared. It walks the beat exactly as `Elaborate` does.
module Warp11.Export

open Warp11.Fu
open Warp11.Factories
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate

/// F# keywords a pin or port may be called, escaped in the source.
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

/// The names a design's io tuple binds: `in1 … inM, out1 … outM`, then a
/// port per control. What its own module destructures, and what a parent
/// destructures to reach the streams and controls of an instance.
let private ioNames (g: Graph) =
    [ for i in 1 .. g.streams -> $"in%d{i}" ], [ for i in 1 .. g.streams -> $"out%d{i}" ], [ for n, _ in controlPorts g -> ident n ]

/// One design as a `let`: the typed form, its sub-designs already printed
/// above it under their own names.
let rec private printDesign (g: Graph) : Result<string, string> =
    try
        checkEdges g
        let order = wireOrder g
        let ports = controlPorts g

        let collect (rs: Result<'a, string> list) : Result<'a list, string> =
            (Ok [], rs) ||> List.fold (fun acc r -> match acc, r with | Ok xs, Ok x -> Ok(x :: xs) | Error e, _ | _, Error e -> Error e) |> Result.map List.rev

        // One `let` per box: the unit, its arguments as values, and its copies —
        // or, for a box that is a design, that design's module as a unit.
        let units =
            order
            |> List.map (fun b ->
                let printed =
                    match designOf g b with
                    | Some sub ->
                        let ins, outs, controls = ioNames sub
                        let ctlFormats = [ for n, f in controlPorts sub -> $"\"{n}\", {showFormat f}" ] |> String.concat "; "
                        let ctlList = if ctlFormats = "" then "[]" else $"[ {ctlFormats} ]"
                        let ctlNames = if controls.IsEmpty then "[]" else "[ " + String.concat "; " controls + " ]"

                        let inList = String.concat "; " ins
                        let outList = String.concat "; " outs
                        let pattern = tuple (ins @ outs @ controls)
                        let value = valueName sub.name

                        match showPins $"{sub.name}'s inputs" sub.inputs, showPins $"{sub.name}'s outputs" sub.outputs with
                        | Ok inPins, Ok outPins ->
                            Ok $"designUnit \"{sub.name}\" ({inPins}) ({outPins}) {ctlList} {value} (fun {pattern} -> [ {inList} ], [ {outList} ], {ctlNames})"
                        | Error e, _
                        | _, Error e -> Error e
                    | None ->
                        let factory = palette[b.unit]
                        factory.print g.sampleRate (complete factory b.arguments)

                printed
                |> Result.map (fun printed ->
                    let unit = if b.copies = 1 then printed else $"copies %d{b.copies} ({printed})"
                    $"    let {ident b.name}Unit = {unit}"))
            |> collect

        let inPins = showPins "the input box" g.inputs
        let outPins = showPins "the output box" g.outputs

        match units, inPins, outPins with
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e
        | Ok units, Ok inPins, Ok outPins ->
            let ins, outs, _ = ioNames g

            let ioFactory =
                [ for i in ins -> $"streamInputPorts p \"{i}\" ({inPins})" ]
                @ [ for o in outs -> $"streamOutputPorts p \"{o}\" ({outPins})" ]
                @ [ for n, f in ports -> $"p.inPort \"{n}\" %d{f.totalWidth}" ]

            let ioPattern = tuple (ins @ outs @ [ for n, _ in ports -> ident n ])

            // The beat, as the elaborator walks it: names and the pin each came from.
            let sourceOf (sink: PinRef) = (g.edges |> List.find (fun e -> e.``to`` = sink)).from

            let neededAfter (b: Box) (source: PinRef) =
                let later = order |> List.skipWhile (fun x -> x.name <> b.name) |> List.tail |> List.map (fun x -> x.name) |> set
                g.edges |> List.exists (fun e -> e.from = source && (e.``to``.box = "output" || later.Contains e.``to``.box))

            let firstBeat = [ for n, f in g.inputs -> n, f, pin "input" n ]

            let stages, slots =
                (((Ok []: Result<string list, string>), firstBeat), order)
                ||> List.fold (fun (lines, slots) b ->
                    let unit = unitOf g b
                    let names = slots |> List.map (fun (n, _, _) -> n)

                    let indexOf (source: PinRef) =
                        slots |> List.findIndex (fun (_, _, s) -> s = source)

                    let operandIdx = [ for n, _ in unit.operands.fields -> indexOf (sourceOf (pin b.name n)) ]

                    let controls =
                        [ for n, f in unit.controls ->
                              match g.edges |> List.tryFind (fun e -> e.``to`` = pin b.name n) with
                              | None -> ident (implicitPortName b.name n)
                              | Some e when e.from.box = "input" -> ident e.from.pin
                              | Some e ->
                                  match controlBoxOf g e.from.box with
                                  | Some c when c.kind = NumberBox -> ident c.name
                                  | Some c -> $"lit %d{controlValueBits c}UL %d{f.totalWidth}"
                                  | None -> failwith $"{showPin e.from}: a control wired from a unit's box is not built" ]

                    let carried = slots |> List.indexed |> List.filter (fun (_, (_, _, s)) -> neededAfter b s)
                    let results = [ for n, f in unit.results.fields -> n, f, pin b.name n ]
                    let nextSlots = results @ List.map snd carried
                    let carriedIdx = carried |> List.map fst |> set

                    let operandsLambda =
                        let pattern = names |> List.mapi (fun i n -> if List.contains i operandIdx then ident n else "_")
                        $"(fun {tuple pattern} -> {tupleValue [ for i in operandIdx -> ident names[i] ]})"

                    let finishLambda =
                        let resultNames = [ for n, _, _ in results -> ident n ]
                        let beatPattern = names |> List.mapi (fun i n -> if carriedIdx.Contains i then ident n else "_")
                        let beat = if carriedIdx.IsEmpty then "_" else tuple beatPattern
                        $"(fun {tuple resultNames} {beat} -> {tupleValue (resultNames @ [ for i in carried |> List.map fst -> ident names[i] ])})"

                    let controlsList = if controls.IsEmpty then "[]" else "[ " + String.concat "; " controls + " ]"

                    let line =
                        showPins $"the beat after {b.name}" [ for n, f, _ in nextSlots -> n, f ]
                        |> Result.map (fun outPins ->
                            $"            |> fuStagesWith {controlsList} {ident b.name}Unit \"{b.name}\" ({outPins}) {operandsLambda} {finishLambda}")

                    (match lines, line with
                     | Ok ls, Ok l -> Ok(ls @ [ l ])
                     | Error e, _
                     | _, Error e -> Error e),
                    nextSlots)

            stages
            |> Result.map (fun stages ->
                // The design's outputs picked out of the last beat, unless it is already in order.
                let names = slots |> List.map (fun (n, _, _) -> n)

                let outIdx =
                    [ for n, _ in g.outputs ->
                          let src = sourceOf (pin "output" n)
                          slots |> List.findIndex (fun (_, _, s) -> s = src) ]

                let projection =
                    if outIdx = [ 0 .. slots.Length - 1 ] then
                        []
                    else
                        let pattern = names |> List.mapi (fun i n -> if List.contains i outIdx then ident n else "_")
                        [ $"            |> List.map (streamMapTo ({outPins}) (fun {tuple pattern} -> {tupleValue [ for i in outIdx -> ident names[i] ]}))" ]

                let sources = ins |> List.map (fun i -> $"streamSource {i}") |> String.concat "; "
                let sinks = String.concat "; " outs

                String.concat
                    "\n"
                    ([ $"let {valueName g.name} =" ]
                     @ units
                     @ [ (if units.IsEmpty then "    defModule" else "\n    defModule")
                         $"        \"{g.name}\""
                         "        (fun p ->"
                         "            " + String.concat ",\n            " ioFactory + ")"
                         $"        (fun {ioPattern} ->"
                         $"            [ {sources} ]" ]
                     @ stages
                     @ projection
                     @ [ $"            |> List.iter2 streamSink [ {sinks} ])" ]))
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
              match palette.TryFind b.unit with
              | Some { definition = Some source } -> yield source
              | _ -> () ]

    (own @ [ for KeyValue(_, sub) in g.designs do yield! definitionsOf sub ]) |> List.distinct

/// The source, or why it cannot be written: one module, the design's rate,
/// the units written for it, the designs it uses as values above it, and
/// the design itself.
let export (g: Graph) : Result<string, string> =
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
               ""
               $"let sampleRate = {showFloat g.sampleRate}"
               "" ]
             @ [ String.concat "\n\n" blocks ])
        + "\n")
