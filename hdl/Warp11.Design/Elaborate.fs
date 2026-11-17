/// A `Graph` back into a design — the data form's half of the bargain, so a
/// design drawn in the GUI elaborates through exactly the calls a person
/// writes by hand and emits the same bytes.
///
/// A box is its unit applied once (`Fu.stage`), an edge is `connectAs` its
/// connection, and a design used as a box is its module, instantiated. A
/// drawn chain is therefore
/// `streamSource in |> stage … |> connectAs … |> stage … |> streamSink out`,
/// in that order, and nothing else is built.
///
/// Everything a GUI would draw as a red edge is refused here first, naming
/// the box and the port: an edge that does not fit, a stream port nobody
/// connected, a place where several edges meet, a cycle.
module Warp11.Elaborate

open Warp11
open Warp11.Fu
open Warp11.Graph
open Warp11.Edit
open Warp11.Factories

/// Every edge, judged at both ends, and every port's count of edges.
let checkEdges (g: Graph) =
    // A fields box that picks what does not reach it says so itself, ahead
    // of the edge after it that would no longer fit.
    for b in g.boxes do
        if b.unit = fieldsBox then
            match streamsOf g b.name with
            | [ input ], _ when not input.fields.IsEmpty ->
                for name, field in fieldPicks b do
                    if not (input.fields |> List.exists (fun (n, _) -> n = field)) then
                        let has = input.fields |> List.map fst |> String.concat ", "
                        failwith $"{b.name}: '{name}' picks '{field}', and what reaches it carries {has}"
            | _ -> ()
        elif b.unit = joinBox then
            let _, joined = streamsOf g b.name

            match joined.Head.fields |> List.map fst |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
            | Some(name, _) -> failwith $"{b.name}: two of its sides carry a field called '{name}'; rename one with a fields box"
            | None -> ()

    for e in g.edges do
        match edgeFit g e with
        | DoesNotFit why -> failwith $"{showEdge e}: {why}"
        | Fits
        | FitsNamesDiffer _ -> ()

    match g.edges |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
    | Some(e, _) -> failwith $"{showEdge e}: drawn twice"
    | None -> ()

    // A box is a stage or a service, by what is connected to it.
    for b in g.boxes do
        if isService g b then
            let ins, outs = streamsOf g b.name

            for s in ins @ outs do
                if not ((edgesInto g (port b.name s.name) @ edgesFrom g (port b.name s.name)).IsEmpty) then
                    failwith $"{b.name}: sockets are connected to it and so is a stream, and a box is a service or a stage, not both"

            if b.copies > clientsOf g b then
                failwith $"{b.name}: %d{b.copies} copies for %d{clientsOf g b} socket(s) — a copy would have nothing to do"

        for s in socketsOf g b.name do
            match edgesFrom g (port b.name s.name) with
            | []
            | [ _ ] -> ()
            | many -> failwith $"{showPort (port b.name s.name)}: %d{many.Length} edges leave it, and a socket connects to one service"

    let boxes = "input" :: "output" :: [ for b in g.boxes do if not (isService g b) then yield b.name ]

    // A stream port is one end of at least one edge: with none it is a
    // stream nobody gives or takes. Several is a meeting, with its pattern.
    for box in boxes do
        let ins, outs = streamsOf g box

        for s in ins do
            if (edgesInto g (port box s.name)).IsEmpty then
                failwith $"{showPort (port box s.name)}: nothing is connected to it"

        for s in outs do
            if (edgesFrom g (port box s.name)).IsEmpty then
                failwith $"{showPort (port box s.name)}: its stream goes nowhere"

    // A control inlet takes at most one edge; one nobody wired is its setting.
    for b in g.boxes do
        for n, _ in controlsOf g b.name do
            match edgesInto g (port b.name n) with
            | []
            | [ _ ] -> ()
            | many -> failwith $"{showPort (port b.name n)}: %d{many.Length} edges land on it, and a control inlet takes one"

/// Boxes in an order every stream can be followed: a box after everything
/// that feeds it, ties broken by the graph's own order.
let edgeOrder (g: Graph) : Box list =
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

    go [] (g.boxes |> List.filter (isService g >> not))

/// The design's ports: a stream group per input and per output, under the
/// stream's own name, then a plain input per control of the design's own
/// and one per number box.
type GraphPorts =
    { ins: StreamInputPorts<Expr list> list
      outs: StreamOutputPorts<Expr list> list
      controls: (string * Expr) list }

/// A module whose boundary is a stream in, a stream out and control ports,
/// as a unit: the shape a design used as a box has, in typed code. `ports`
/// says where those are on the module's io, so an exported design names
/// its sub-designs' ports as its own text does.
let designUnit
    (name: string)
    (operands: Layout<'a>)
    (results: Layout<'r>)
    (controls: (string * NumberFormat) list)
    (m: TypedModule<'io>)
    (ports: 'io -> StreamInputPorts<'a> * StreamOutputPorts<'r> * Expr list)
    : Fu<'a, 'r> =
    moduleUnit name operands results controls (fun instance controlValues s ->
        let input, output, controlPorts = ports (m.NewNamed instance)
        List.iter2 (fun port value -> value ==> port) controlPorts controlValues
        streamThroughInstance input output s)

/// The net that says a box's stream carries a beat this cycle, when the
/// design was elaborated with probes. The boundary's are its own ports. A
/// box's are `probe_{box}_…`: its instance is named after the box, so
/// `{box}_in_valid` is already the instance's own wire.
let validName (box: string) (side: Side) (stream: string) : string =
    match box, side with
    | "input", _
    | "output", _ -> $"{stream}_valid"
    | box, In -> $"probe_{box}_in_valid"
    | box, Out -> $"probe_{box}_out_valid"

/// The nets a stream port's fields ride on, when the design was elaborated
/// with probes — what a debugger watches to paint the port. The boundary
/// boxes' are the design's own ports; a box's are its probe wires.
let fieldProbes (g: Graph) (p: PortRef) (side: Side) : (string * NumberFormat) list =
    let ins, outs = streamsOf g p.box

    match (match side with In -> ins | Out -> outs) |> List.tryFind (fun s -> s.name = p.port), p.box with
    | Some s, ("input" | "output") -> [ for n, f in s.fields -> $"{s.name}_{n}", f ]
    | Some s, box -> [ for n, f in s.fields -> (match side with In -> $"probe_{box}_in_{n}" | Out -> $"probe_{box}_out_{n}"), f ]
    | None, _ -> []

/// The net a control port's value rides on: the design's port it was wired
/// from or a number box's. A constant and a setting are literals, with no
/// net.
let controlProbe (g: Graph) (p: PortRef) (side: Side) : string option =
    match p.box, side with
    | "input", Out when g.controls |> List.exists (fun (n, _) -> n = p.port) -> Some p.port
    | box, Out when (controlBoxOf g box |> Option.exists (fun c -> c.kind = NumberBox)) -> Some box
    | box, In ->
        match g.edges |> List.tryFind (fun e -> e.``to`` = port box p.port) with
        | Some e when e.from.box = "input" -> Some e.from.port
        | Some e ->
            match controlBoxOf g e.from.box with
            | Some c when c.kind = NumberBox -> Some c.name
            | _ -> None
        | None -> None
    | _ -> None

/// The prefix a box's instance puts on every net inside it, as the
/// simulator flattens them: the instance is named after the box, with the
/// copy's index when it is a farm. What a debugger drilled into a box that
/// is a design adds to that design's own net names.
let instancePrefix (b: Box) : string = if b.copies = 1 then $"{b.name}_" else $"{b.name}0_"

/// Every probe a design elaborated with probes declares, for a debugger to
/// watch them all at once — and, through a box that is a design, that
/// design's probes and boundary under the instance's prefix.
let rec probeNames (g: Graph) : string list =
    [ for b in g.boxes do
          if not (isService g b || isStructural b) then
              let ins, outs = streamsOf g b.name

              for s in ins do
                  yield validName b.name In s.name

                  for n, _ in fieldProbes g (port b.name s.name) In do
                      yield n

              for s in outs do
                  yield validName b.name Out s.name

                  for n, _ in fieldProbes g (port b.name s.name) Out do
                      yield n

              match designOf g b with
              | Some sub ->
                  let prefix = instancePrefix b

                  for n in probeNames sub do
                      yield prefix + n

                  for s in sub.inputs @ sub.outputs do
                      yield $"{prefix}{s.name}_valid"

                      for n, _ in s.fields do
                          yield $"{prefix}{s.name}_{n}"

                  for n, _ in controlPorts sub do
                      yield prefix + n
              | None -> () ]

/// The graph as a module. `onRead` hears each combinational box whose
/// operands were read at its formats, and which of them: what the export
/// needs to print the same reads.
///
/// With `probes`, one wire per field of each box's stream in and stream out
/// — `probe_{box}_in_{field}`, `probe_{box}_out_{field}`, and `_valid` on each side — is
/// declared and driven from the stream, so a debugger can watch a port by
/// name. Nothing reads them, so they change no behaviour; the production
/// elaboration leaves them out and stays byte-identical to the typed form.
let rec elaborateFull (probes: bool) (onRead: string -> bool list -> unit) (g: Graph) : TypedModule<GraphPorts> =
    checkEdges g
    let order = edgeOrder g

    defModule
        g.name
        (fun p ->
            { ins = [ for s in g.inputs -> streamInputPorts p s.name (layoutOfList s.fields) ]
              outs = [ for s in g.outputs -> streamOutputPorts p s.name (layoutOfList s.fields) ]
              controls = [ for n, f in controlPorts g -> n, p.inPort n f.totalWidth ] })
        (fun io ->
            let controlPorts = Map.ofList io.controls

            // A stream leaving a port, on each edge that leaves it: the
            // stream itself on one edge, a branch of the port's pattern on
            // several, in edge order.
            let leaving (branches: Map<Edge, Stream<Expr list>>) (source: PortRef) (s: Stream<Expr list>) =
                match edgesFrom g source with
                | [ e ] -> branches |> Map.add e s
                | edges ->
                    let fanned =
                        match fanOutAt g source with
                        | FanOut.Broadcast -> streamForkEager edges.Length s
                        | FanOut.Balance -> streamBalance edges.Length s

                    (branches, List.zip edges fanned) ||> List.fold (fun m (e, branch) -> Map.add e branch m)

            // The stream that reaches a port: each edge's branch through that
            // edge's connection, and the port's merge where several arrive.
            let arriving (branches: Map<Edge, Stream<Expr list>>) (sink: PortRef) =
                let through (e: Edge) =
                    match branches.TryFind e with
                    | Some s -> connectAs e.connection s
                    | None -> failwith $"{showEdge e}: its source comes from a later box"

                match edgesInto g sink |> List.map through with
                | [ s ] -> s
                | several -> streamMergeWith (mergeAt g sink) several

            // The stages: every box that is not a service, in stream order,
            // its sockets served by `servers` where they are connected and
            // in place where they are not. What reaches each output comes
            // back, to be handed to its ports.
            let stages (servers: Map<string, Server>) =
                let sources =
                    (Map.empty, List.zip g.inputs io.ins)
                    ||> List.fold (fun branches (s, ports) -> leaving branches (port "input" s.name) (streamSource ports))

                let branches =
                    (sources, order)
                    ||> List.fold (fun branches b ->
                        // A control inlet's value: the design's port it was wired
                        // from, a number box's port, a constant's literal, or the
                        // literal of its setting when nobody wired it.
                        let controls =
                            [ for n, f in controlsOf g b.name ->
                                  match g.edges |> List.tryFind (fun e -> e.``to`` = port b.name n) with
                                  | None -> lit (settingBits g b (n, f)) f.totalWidth
                                  | Some e when e.from.box = "input" -> controlPorts[e.from.port]
                                  | Some e ->
                                      match controlBoxOf g e.from.box with
                                      | Some c when c.kind = NumberBox -> controlPorts[c.name]
                                      | Some c -> lit (controlValueBits c) f.totalWidth
                                      | None -> failwith $"{showEdge e}: a control wired from a unit's box is not built" ]

                        // A socket port's server: the service its one edge
                        // reaches, the same stage for each of its sockets.
                        let serve (socket: string) =
                            edgesFrom g (port b.name socket)
                            |> List.tryHead
                            |> Option.map (fun e -> servers[e.``to``.box])

                        // What a structural box carries out, and what
                        // reaches each of its sides.
                        let outFields () = (snd (streamsOf g b.name)).Head.fields

                        match streamsOf g b.name with
                        | _ when b.unit = fifoBox ->
                            let depth =
                                match System.Int32.TryParse(structuralArgument b "depth") with
                                | true, d when d >= 1 -> d
                                | _ -> failwith $"{b.name}: a buffer's depth is a count of at least one"

                            arriving branches (port b.name streamInlet)
                            |> streamFifo b.name depth
                            |> leaving branches (port b.name streamOutlet)
                        | _ when b.unit = fieldsBox ->
                            let s = arriving branches (port b.name streamInlet)
                            let arrive = s.layout.fields |> List.map fst
                            let picks = fieldPicks b

                            if picks.IsEmpty then
                                failwith $"{b.name}: no fields picked"

                            match picks |> List.map fst |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
                            | Some(name, _) -> failwith $"{b.name}: '{name}' is picked twice"
                            | None -> ()

                            let at =
                                [ for name, field in picks ->
                                      match arrive |> List.tryFindIndex ((=) field) with
                                      | Some i -> i
                                      | None ->
                                          let has = String.concat ", " arrive
                                          failwith $"{b.name}: '{name}' picks '{field}', and what reaches it carries {has}" ]

                            s
                            |> streamMapTo (layoutOfList (outFields ())) (fun fields -> [ for i in at -> fields[i] ])
                            |> leaving branches (port b.name streamOutlet)
                        | _ when b.unit = joinBox ->
                            let joined = outFields ()

                            match joined |> List.map fst |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
                            | Some(name, _) -> failwith $"{b.name}: two of its sides carry a field called '{name}'; rename one with a fields box"
                            | None -> ()

                            let sides = [ for side in joinInputs b -> arriving branches (port b.name side) ]

                            // Sides of one field each are `streamJoin`; a
                            // side that carries several makes it the join
                            // of groups.
                            let out =
                                if sides |> List.forall (fun s -> s.layout.fields.Length = 1) then
                                    streamJoin (layoutOfList joined) [ for s in sides -> s |> streamMapTo (pins1 s.layout.fields.Head) List.head ]
                                else
                                    streamJoinGroups (layoutOfList joined) sides

                            leaving branches (port b.name streamOutlet) out
                        | [ input ], [ output ] ->
                            let order = if b.ordered then Farm.InOrder else Farm.AsDone

                            let unit = unitServedWith probes g b serve

                            // A combinational unit reads its operands as its
                            // formats say. The edge was judged on formats, and
                            // a net from the design's own boundary is bits with
                            // no reading, so where one arrives unread it is
                            // read here; a net that already reads right is left.
                            let read (s: Stream<Expr list>) =
                                match unit.law with
                                | Sequential _ -> s
                                | Combinational _ ->
                                    let asFormat (f: NumberFormat) (e: Expr) =
                                        if (formatOf e).signed = f.signed then e
                                        elif f.signed then asSInt e
                                        else asUInt e

                                    let unread = List.map2 (fun (_, f: NumberFormat) e -> (formatOf e).signed <> f.signed) unit.operands.fields s.payload

                                    if not (List.exists id unread) then
                                        s
                                    else
                                        onRead b.name unread
                                        s |> streamMapTo s.layout (List.map2 (fun (_, f) e -> asFormat f e) unit.operands.fields)

                            let before = arriving branches (port b.name input.name) |> read
                            let after = before |> farm order controls unit b.name b.copies

                            if probes then
                                let probe (s: Stream<Expr list>) (side: Side) (stream: string) =
                                    s.valid ==> wireBit (validName b.name side stream)

                                    for (name, f), value in List.zip (fieldProbes g (port b.name stream) side) s.payload do
                                        value ==> Warp11.Dsl.wire name f.totalWidth

                                probe before In input.name
                                probe after Out output.name

                            leaving branches (port b.name output.name) after
                        | ins, outs -> failwith $"{b.name}: a box with %d{ins.Length} stream(s) in and %d{outs.Length} out is not built yet")

                [ for s, ports in List.zip g.outputs io.outs -> ports, arriving branches (port "output" s.name) ]

            // The services, outermost first: one client is the unit in
            // place, several share the box's copies behind the port's
            // arbitration, built when every client has been made.
            let rec serving (services: Box list) (servers: Map<string, Server>) =
                match services with
                | [] -> stages servers
                | service :: rest ->
                    let unit = paletteUnitOf g service

                    if clientsOf g service = 1 then
                        serving rest (servers |> Map.add service.name (inPlace unit))
                    else
                        sharingAmong (mergeAt g (port service.name servicePort)) service.copies unit service.name (fun stage ->
                            serving rest (servers |> Map.add service.name stage))

            for ports, s in serving (g.boxes |> List.filter (isService g)) Map.empty do
                streamSink ports s)

/// A design as a unit: its module, instantiated under the box's name, its
/// control ports driven and the stream put through its boundary. With
/// `probes`, the design inside is elaborated with them too, so a debugger
/// drilled into the box sees its ports.
and graphUnit (probes: bool) (sub: Graph) : ErasedFu =
    match sub.inputs, sub.outputs with
    | [ input ], [ output ] ->
        moduleUnit sub.name (layoutOfList input.fields) (layoutOfList output.fields) (controlPorts sub) (fun instance controlValues s ->
            let io = (elaborateFull probes (fun _ _ -> ()) sub).NewNamed instance
            List.iter2 (fun (_, port) value -> value ==> port) io.controls controlValues
            streamThroughInstance io.ins.Head io.outs.Head s)
    | ins, outs -> failwith $"{sub.name}: a design used as a box has one stream in and one out for now, not %d{ins.Length} and %d{outs.Length}"

/// The unit a box is: a palette unit made by its factory with its sockets
/// served as `serve` says, or the design the box is.
and unitServedWith (probes: bool) (g: Graph) (b: Box) (serve: string -> Server option) : ErasedFu =
    match designOf g b with
    | Some sub -> graphUnit probes sub
    | None -> paletteUnitServed g b serve

/// The unit a box is with every socket served in place.
and unitOf (g: Graph) (b: Box) : ErasedFu = unitServedWith false g b (fun _ -> None)

/// The production form: no probes, nobody listening.
let elaborate (g: Graph) : TypedModule<GraphPorts> = elaborateFull false (fun _ _ -> ()) g

/// The production form, with the reads reported.
let elaborateTracing (onRead: string -> bool list -> unit) (g: Graph) : TypedModule<GraphPorts> = elaborateFull false onRead g

/// With probes, for a debugger.
let elaborateWith (probes: bool) (g: Graph) : TypedModule<GraphPorts> = elaborateFull probes (fun _ _ -> ()) g

/// Beats the design emits for one it takes: the product over its boxes,
/// each answering its own count — a chain, so a box that answers four
/// followed by one that answers two is eight for one.
let rec answersOf (g: Graph) : int =
    g.boxes
    |> List.fold
        (fun acc b ->
            match designOf g b with
            | Some sub -> acc * answersOf sub
            | None when isStructural b || isService g b -> acc
            | None -> acc * (paletteUnitOf g b).answers)
        1

// ---------------------------------------------------------------------------
// The drawn design on a board: `BoardTop`'s typed `Design`, made from a graph.

/// A drawn design, as a board top takes it: its one stream in and its one
/// stream out, and every control port a value the host gives.
let ofGraph (g: Graph) : BoardTop.Design =
    let input, output =
        match g.inputs, g.outputs with
        | [ input ], [ output ] -> input, output
        | ins, outs -> failwith $"{g.name}: a board top carries one stream in and one out, and the design has %d{ins.Length} in and %d{outs.Length} out"

    // The drawn boundary as needs, in the order the seam reads them: the
    // stream in, the stream out, then the controls as declared.
    let needs =
        [ yield BoardTop.streamIn "in" input.fields
          yield BoardTop.streamOut "out" output.fields
          let starting = startingValues g |> Map.ofList

          for name, format in controlPorts g ->
              BoardTop.valueFrom name format (starting |> Map.tryFind name |> Option.defaultValue 0UL) ]

    { name = g.name
      sampleRate = g.sampleRate
      streams = 1
      needs = needs
      answers = answersOf g
      atClocking = None
      body =
        BoardTop.rigged needs (fun instance ->
            let io = (elaborate g).NewNamed instance

            { through = streamThroughInstance io.ins.Head io.outs.Head
              ports = io.controls
              // A drawn design has no way to declare telemetry yet: the
              // canvas has control inlets and no report outlets.
              readbacks = [] }) }

/// `Warp11.BoardTop.boardTop` for a drawn design.
let boardTopOf (board: Board) (path: DataPath) (g: Graph) : BoardTop.BoardTop = BoardTop.boardTop board path (ofGraph g)

/// The drawn design on the board its mapping says.
let boardTopFor (m: Warp11.Mapping.Mapping) (g: Graph) : BoardTop.BoardTop = BoardTop.boardTopFor m (ofGraph g)
