/// A `Graph` back into a design — the data form's half of the bargain, so a
/// patch drawn in the GUI elaborates through exactly the calls `macWith`
/// makes by hand and emits the same bytes.
///
/// The model is the beat. The design's inputs are the first beat; each box,
/// in wire order, is one `fuStagesWith` call whose operands are picked out
/// of the beat by index and whose finish puts its results first and then
/// whatever a later box still needs — a field is *carried* exactly while
/// some wire from it has not landed yet. Names follow the pins that made
/// them, so a box called `sum` with a result pin `sum` carries `sum`.
///
/// Everything a GUI would draw as a red wire is refused here first, naming
/// the box and the pin: a pin that is not there, a wire between kinds or
/// formats, an input nobody wired, a result nobody reads, a cycle.
module Warp11.Placement.Elaborate

open Warp11
open Warp11.Placement.Fu
open Warp11.Placement.Graph
open Warp11.Placement.Edit

/// One field of the beat, and the pin it came from.
type private Slot =
    { name: string
      format: NumberFormat
      source: PinRef }

let private show = showPin

/// Every wire, checked at both ends.
let private checkEdges (g: Graph) =
    for e in g.edges do
        match lookupPin g Out e.from, lookupPin g In e.``to`` with
        | Error why, _
        | _, Error why -> failwith why
        | Ok(SignalOut, fa), Ok(SignalIn, fb)
        | Ok(ControlOut, fa), Ok(ControlIn, fb) ->
            if fa <> fb then
                failwith $"{show e.from} is {describeFormat fa}, {show e.``to``} is {describeFormat fb}"
        | Ok(ka, _), Ok(kb, _) -> failwith $"{show e.from} → {show e.``to``}: a wire runs from an output to an input of the same kind, not {ka} to {kb}"

    // Every signal sink exactly once; a control inlet at most once — one
    // nobody wired holds its setting; every source at least once.
    let signalSinks =
        [ for b in g.boxes do
              let ins, _ = pinsOf g b.name

              for n, _ in ins do
                  yield pin b.name n
          for n, _ in g.outputs -> pin "output" n ]

    let controlSinks =
        [ for b in g.boxes do
              for n, _ in controlsOf g b.name do
                  yield pin b.name n ]

    for p in signalSinks @ controlSinks do
        match g.edges |> List.filter (fun e -> e.``to`` = p) with
        | [] when List.contains p signalSinks -> failwith $"{show p}: nothing is wired to it"
        | []
        | [ _ ] -> ()
        | many -> failwith $"{show p}: %d{many.Length} wires land on it, and an input takes one"

    for b in g.boxes do
        let _, outs = pinsOf g b.name

        for n, _ in outs do
            if not (g.edges |> List.exists (fun e -> e.from = pin b.name n)) then
                failwith $"{show (pin b.name n)}: its result is not used"

/// Boxes in an order every wire can be followed: a box after everything that
/// feeds it, ties broken by the graph's own order.
let private wireOrder (g: Graph) : Box list =
    let feeds (b: Box) =
        g.edges
        |> List.filter (fun e -> e.``to``.box = b.name && e.from.box <> "input" && (controlBoxOf g e.from.box).IsNone)
        |> List.map (fun e -> e.from.box)
        |> List.distinct

    let rec go (placed: Box list) (pending: Box list) =
        match pending with
        | [] -> List.rev placed
        | _ ->
            let placedNames = placed |> List.map (fun b -> b.name) |> set

            match pending |> List.tryFind (fun b -> feeds b |> List.forall placedNames.Contains) with
            | Some next -> go (next :: placed) (pending |> List.filter (fun b -> b.name <> next.name))
            | None ->
                let names = pending |> List.map (fun b -> b.name) |> String.concat ", "
                failwith $"the graph has a cycle through {names} — feedback needs a register box in it"

    go [] g.boxes

/// The design's ports: M stream groups in, M out, a plain input per
/// control of the design's own, then one per number box, then one per
/// control inlet nobody wired — every control port, by name.
type GraphPorts =
    { ins: StreamInputPorts<Expr list> list
      outs: StreamOutputPorts<Expr list> list
      controls: (string * Expr) list }

/// Every control port the design has, in port order: its own, its number
/// boxes, and the implicit ones. A mapping pokes these.
let controlPorts (g: Graph) : (string * NumberFormat) list =
    g.controls
    @ [ for c in g.controlBoxes do
            if c.kind = NumberBox then
                yield c.name, c.format ]
    @ [ for b, n, f in implicitControls g -> implicitPortName b.name n, f ]

/// The net a pin's value rides on, when the design was elaborated with
/// probes — what a debugger watches to paint the pin. The boundary boxes'
/// pins are the design's own ports; a box's pins are its probe wires; a
/// control pin is the port it was wired from.
let probeName (g: Graph) (p: PinRef) (side: Side) : string option =
    let isSignal =
        let ins, outs = pinsOf g p.box
        (match side with In -> ins | Out -> outs) |> List.exists (fun (n, _) -> n = p.pin)

    match p.box, side with
    | "input", Out when isSignal -> Some $"in1_{p.pin}"
    | "input", Out -> Some p.pin
    | "output", In -> Some $"out1_{p.pin}"
    | box, In when isSignal -> Some $"{box}_in_{p.pin}"
    | box, In ->
        // A control: the port it was wired from, a number box's port, or
        // the implicit port of an inlet nobody wired. A constant has no net.
        match g.edges |> List.tryFind (fun e -> e.``to`` = pin box p.pin) with
        | Some e when e.from.box = "input" -> Some e.from.pin
        | Some e ->
            match controlBoxOf g e.from.box with
            | Some c when c.kind = NumberBox -> Some c.name
            | _ -> None
        | None -> Some(implicitPortName box p.pin)
    | box, Out when isSignal -> Some $"{box}_out_{p.pin}"
    | _ -> None

/// The net that says a box's pins on one side carry a beat this cycle.
let validName (g: Graph) (box: string) (side: Side) : string =
    match box, side with
    | "input", _ -> "in1_valid"
    | "output", _ -> "out1_valid"
    | box, In -> $"{box}_in_valid"
    | box, Out -> $"{box}_out_valid"

/// Every probe a design elaborated with probes declares, for a debugger to
/// watch them all at once.
let probeNames (g: Graph) : string list =
    [ for b in g.boxes do
          let ins, outs = pinsOf g b.name
          yield validName g b.name In
          yield validName g b.name Out

          for n, _ in ins do
              yield $"{b.name}_in_{n}"

          for n, _ in outs do
              yield $"{b.name}_out_{n}" ]

/// The graph as a module. Every box is one `fuStagesWith`; the beat between
/// boxes is its results and what a later box still needs.
///
/// With `probes`, one wire per box pin — `{box}_in_{pin}`, `{box}_out_{pin}`,
/// and `_valid` on each side — is declared and driven from the beat, so a
/// debugger can watch a pin by name. Nothing reads them, so they change no
/// behaviour; the production elaboration leaves them out and stays
/// byte-identical to the typed form.
let elaborateWith (probes: bool) (g: Graph) : TypedModule<GraphPorts> =
    checkEdges g
    let order = wireOrder g

    defModule
        g.name
        (fun p ->
            { ins = [ for i in 1 .. g.streams -> streamInputPorts p $"in%d{i}" (lower (pinsOfList g.inputs)) ]
              outs = [ for i in 1 .. g.streams -> streamOutputPorts p $"out%d{i}" (lower (pinsOfList g.outputs)) ]
              controls = [ for n, f in controlPorts g -> n, p.inPort n f.totalWidth ] })
        (fun io ->
            let controlPorts = Map.ofList io.controls

            let sourceOf (sink: PinRef) =
                (g.edges |> List.find (fun e -> e.``to`` = sink)).from

            // A source is still needed after box `b` if some wire from it lands
            // on a later box or on the design's output.
            let neededAfter (b: Box) (source: PinRef) =
                let later = order |> List.skipWhile (fun x -> x.name <> b.name) |> List.tail |> List.map (fun x -> x.name) |> set

                g.edges
                |> List.exists (fun e -> e.from = source && (e.``to``.box = "output" || later.Contains e.``to``.box))

            let firstBeat = [ for n, f in g.inputs -> { name = n; format = f; source = pin "input" n } ]

            let streams, slots =
                (((io.ins |> List.map streamSource), firstBeat), order)
                ||> List.fold (fun (streams, slots) b ->
                    let unit = unitOf g b |> copies b.copies
                    let indexOf (source: PinRef) =
                        match slots |> List.tryFindIndex (fun s -> s.source = source) with
                        | Some i -> i
                        | None -> failwith $"{show source}: not available at {b.name} — it comes from a later box"

                    let operandIdx = [ for n, _ in unit.operands.pins -> indexOf (sourceOf (pin b.name n)) ]

                    // A control inlet's value: the design's port it was wired
                    // from, a number box's port, a constant's literal, or the
                    // implicit port of an inlet nobody wired.
                    let controls =
                        [ for n, f in unit.controls ->
                              match g.edges |> List.tryFind (fun e -> e.``to`` = pin b.name n) with
                              | None -> controlPorts[implicitPortName b.name n]
                              | Some e when e.from.box = "input" -> controlPorts[e.from.pin]
                              | Some e ->
                                  match controlBoxOf g e.from.box with
                                  | Some c when c.kind = NumberBox -> controlPorts[c.name]
                                  | Some c -> lit (controlValueBits c) f.totalWidth
                                  | None -> failwith $"{show e.from} → {show (pin b.name n)}: a control wired from a unit's box is not built" ]

                    let carried = slots |> List.indexed |> List.filter (fun (_, s) -> neededAfter b s.source)
                    let results = [ for n, f in unit.results.pins -> { name = n; format = f; source = pin b.name n } ]

                    for r in results do
                        if carried |> List.exists (fun (_, s) -> s.name = r.name) then
                            failwith $"{b.name}: its result '{r.name}' has the same name as a field still carried past it"

                    let nextSlots = results @ List.map snd carried
                    let outPins = pinsOfList [ for s in nextSlots -> s.name, s.format ]
                    let carriedIdx = List.map fst carried

                    let staged =
                        fuStagesWith
                            controls
                            unit
                            b.name
                            outPins
                            (fun (fields: Expr list) -> [ for i in operandIdx -> fields[i] ])
                            (fun r (fields: Expr list) -> r @ [ for i in carriedIdx -> fields[i] ])
                            streams

                    if probes then
                        // The first stream's view of this box, before and after.
                        let before, after = streams.Head, staged.Head
                        before.valid ==> wireBit (validName g b.name In)
                        after.valid ==> wireBit (validName g b.name Out)

                        for (n, f), i in List.zip unit.operands.pins operandIdx do
                            before.payload[i] ==> wire $"{b.name}_in_{n}" f.totalWidth

                        for i, (n, f) in List.indexed unit.results.pins do
                            after.payload[i] ==> wire $"{b.name}_out_{n}" f.totalWidth

                    staged, nextSlots)

            // The design's outputs, picked out of the last beat — a projection
            // that declares nothing when the beat is already in order.
            let outIdx =
                [ for n, _ in g.outputs ->
                      let src = sourceOf (pin "output" n)

                      match slots |> List.tryFindIndex (fun s -> s.source = src) with
                      | Some i -> i
                      | None -> failwith $"{show src}: not available at the output" ]

            let outLayout = lower (pinsOfList g.outputs)

            let finished =
                if outIdx = [ 0 .. slots.Length - 1 ] then
                    streams
                else
                    streams |> List.map (streamMapTo outLayout (fun fields -> [ for i in outIdx -> fields[i] ]))

            List.iter2 streamSink io.outs finished)

/// The production form: no probes.
let elaborate (g: Graph) : TypedModule<GraphPorts> = elaborateWith false g
