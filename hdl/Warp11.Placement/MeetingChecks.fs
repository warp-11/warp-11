/// Where edges meet: a stream port several edges leave or arrive at, and a
/// box with copies. Each against the pipeline a person writes, at the bytes,
/// and then on what the pattern promises.
module Warp11.Placement.MeetingChecks

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate
open Warp11.Placement.Placement
open Warp11.Placement.ChainChecks

let private verilog (m: TypedModule<'io>) = Warp11.Verilog.emitDesign m.def

let private same (drawn: Graph) (typed: TypedModule<'io>) : bool =
    let a, b = verilog (elaborate drawn), verilog typed

    if a <> b then
        let la, lb = a.Split '\n', b.Split '\n'
        let at = Seq.zip la lb |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min la.Length lb.Length)
        let line (lines: string array) = if at < lines.Length then lines[at] else "(end)"
        printfn $"      {drawn.name}: first difference at line %d{at + 1}"
        printfn $"        drawn: {line la}"
        printfn $"        typed: {line lb}"

    a = b

let private refuses (expecting: string) (g: Graph) : bool =
    try
        verilog (elaborate g) |> ignore
        printfn $"      {g.name}: built, expected a refusal about '{expecting}'"
        false
    with e ->
        if not (e.Message.Contains expecting) then
            printfn $"      {g.name}: refused with '{e.Message}', expected '{expecting}'"

        e.Message.Contains expecting

/// A gain box whose volume is a setting, so two of them differ in what
/// they do to a beat.
let private gainAt (name: string) (volume: uint64) : Box =
    { box name "gain" [] with settings = Map.ofList [ "volume", string volume ] }

let private gainStage (name: string) (volume: uint64) =
    audioGain "AudioGain" name (lit volume 16) (lit 0UL 1)

let private stereoIo (p: Ports) = streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins

/// Beats through a design's `in` and `out`, the output stalled every third
/// cycle when asked.
let private through (design: ModuleDef) (stallEvery: int) (beats: uint64 list list) (expected: int) : uint64 list list =
    let sim = Sim design

    beats
    |> streamThroughWith sim (streamPins "in" stereoPins) (streamPins "out" stereoPins) stallEvery (max 400 (expected * 40))
    |> Seq.truncate expected
    |> List.ofSeq

let private beats = [ for i in 1UL .. 40UL -> [ i * 100UL; i * 100UL + 7UL ] ]

// ---------------------------------------------------------------------------
// MD1 — a fan-out and a merge: one stream to two gains, the two merged.
// Each edge out of the fan-out registers `ready` and each edge into the
// merge is a register stage. On wires, `Balance` is refused as a
// combinational loop: it offers a beat to a branch that is ready, and a
// gain is ready when it is offered one. What an edge registers is how the
// drawer breaks it.

// THE DRAWING
let diamond (pattern: FanOut) : Graph =
    { emptyGraph $"Diamond{pattern}" rate stereoPins.fields with
        boxes = [ gainAt "once" gainUnity; gainAt "twice" (2UL * gainUnity) ]
        fanOuts = (if pattern = FanOut.Broadcast then Map.empty else Map.ofList [ port "input" "in", pattern ])
        edges =
            [ edgeAs Connection.Backward (port "input" "in") (port "once" streamInlet)
              edgeAs Connection.Backward (port "input" "in") (port "twice" streamInlet)
              edgeAs Connection.Forward (port "once" streamOutlet) (port "output" "out")
              edgeAs Connection.Forward (port "twice" streamOutlet) (port "output" "out") ] }

// THE PIPELINE
let diamondPipeline (pattern: FanOut) =
    defModule $"Diamond{pattern}" stereoIo (fun (inPorts, outPorts) ->
        let branches =
            match pattern with
            | FanOut.Broadcast -> streamSource inPorts |> streamForkEager 2
            | FanOut.Balance -> streamSource inPorts |> streamBalance 2

        let once = branches[0] |> connectAs Connection.Backward |> gainStage "once" gainUnity
        let twice = branches[1] |> connectAs Connection.Backward |> gainStage "twice" (2UL * gainUnity)

        streamMergeWith Arbitration.roundRobin [ connectAs Connection.Forward once; connectAs Connection.Forward twice ]
        |> streamSink outPorts)

// CHECK — broadcast: every beat out of both; balance: every beat out of one.
let fanOutThenMerge () : bool =
    let doubled (beat: uint64 list) = beat |> List.map ((*) 2UL)
    let broadcast = through (elaborate (diamond FanOut.Broadcast)).def 3 beats (2 * beats.Length)
    let balanced = through (elaborate (diamond FanOut.Balance)).def 3 beats beats.Length

    same (diamond FanOut.Broadcast) (diamondPipeline FanOut.Broadcast)
    && same (diamond FanOut.Balance) (diamondPipeline FanOut.Balance)
    && List.sort broadcast = List.sort (beats @ List.map doubled beats)
    && balanced.Length = beats.Length
    // ...and the same diamond on wires is the loop, named.
    && (let onWires = { diamond FanOut.Balance with edges = (diamond FanOut.Balance).edges |> List.map (fun e -> { e with connection = Connection.Wire }) }

        try
            Sim (elaborate onWires).def |> ignore
            false
        with e ->
            e.Message.Contains "combinational loop")
    && (balanced |> List.forall (fun got -> beats |> List.exists (fun sent -> got = sent || got = doubled sent)))

// ---------------------------------------------------------------------------
// MD2 — a merge's arbitration is the port's, and the edges' order is the
// lanes': five boundary streams into one, under each arbitration.

// THE DRAWING
let merged (arbitration: Arbitration) : Graph =
    let name = $"Merged{arbitration.policy}{arbitration.timing}"

    { emptyGraph name rate stereoPins.fields with
        inputs = [ for i in 0..4 -> { name = $"in%d{i}"; fields = stereoPins.fields } ]
        boxes = [ gainAt "gain" gainUnity ]
        merges = (if arbitration = Arbitration.roundRobin then Map.empty else Map.ofList [ port "gain" streamInlet, arbitration ])
        edges =
            [ for i in 0..4 -> wire (port "input" $"in%d{i}") (port "gain" streamInlet) ]
            @ [ wire (port "gain" streamOutlet) (port "output" "out") ] }

// THE PIPELINE
let mergedPipeline (arbitration: Arbitration) =
    defModule
        $"Merged{arbitration.policy}{arbitration.timing}"
        (fun p -> [ for i in 0..4 -> streamInputPorts p $"in%d{i}" stereoPins ], streamOutputPorts p "out" stereoPins)
        (fun (inPorts, outPorts) ->
            inPorts
            |> List.map streamSource
            |> streamMergeWith arbitration
            |> gainStage "gain" gainUnity
            |> streamSink outPorts)

let private arbitrations =
    [ for policy in [ ArbitrationPolicy.RoundRobin; ArbitrationPolicy.Priority ] do
          for timing in [ ArbitrationTiming.SameCycle; ArbitrationTiming.Registered ] -> { policy = policy; timing = timing } ]

/// Every input asking flat out: who is served, by the `left` each offers.
let private served (g: Graph) (cycles: int) : int list =
    let sim = Sim (elaborate g).def
    let counts = Array.zeroCreate 5

    for i in 0..4 do
        sim.Poke($"in%d{i}_left", uint64 (i + 1))
        sim.Poke($"in%d{i}_valid", 1UL)

    sim.Poke("out_ready", 1UL)

    for _ in 1..cycles do
        if sim.Peek "out_valid" = 1UL then
            let lane = int (sim.Peek "out_left") - 1
            counts[lane] <- counts[lane] + 1

        sim.Tick()

    List.ofArray counts

// CHECK
let mergeIsThePortsArbitration () : bool =
    let priority = { policy = ArbitrationPolicy.Priority; timing = ArbitrationTiming.SameCycle }
    let fair = served (merged Arbitration.roundRobin) 1000
    let ranked = served (merged priority) 1000
    // Raise the last input's edge: it is the first lane now, and priority's.
    let raised = raiseEdge (wire (port "input" "in4") (port "gain" streamInlet)) (merged priority) |> Result.defaultWith failwith
    let reranked = served raised 1000

    arbitrations |> List.forall (fun a -> same (merged a) (mergedPipeline a))
    && List.max fair - List.min fair <= 1
    && ranked.Tail = [ 0; 0; 0; 0 ]
    && reranked[4] > 990
    && List.sum reranked = reranked[4]

// ---------------------------------------------------------------------------
// MD3 — copies on a box are a farm: a multiplier that takes sixteen cycles a
// product, four of it, in order and not.

let private products = pins2 ("a", unsignedInt 16) ("b", unsignedInt 16)
let private product = pins1 ("product", unsignedInt 32)

// THE DRAWING
let farmed (copies: int) (ordered: bool) : Graph =
    let suffix = if ordered then "" else "AsDone"

    { emptyGraph $"Farmed%d{copies}{suffix}" rate products.fields with
        outputs = [ { name = "out"; fields = product.fields } ]
        boxes = [ { box "product" "smul16" [] with copies = copies; ordered = ordered } ]
        edges =
            [ wire (port "input" "in") (port "product" streamInlet)
              wire (port "product" streamOutlet) (port "output" "out") ] }

// THE PIPELINES — the farm written out, not through `Fu.farm`.
let farmedPipeline (copies: int) (ordered: bool) =
    let suffix = if ordered then "" else "AsDone"
    let worker (i: int) =
        connectAs Connection.Backward >> stage [] shiftAddMultiply16 $"product%d{i}" >> connectAs Connection.Forward

    defModule
        $"Farmed%d{copies}{suffix}"
        (fun p -> streamInputPorts p "in" products, streamOutputPorts p "out" product)
        (fun (inPorts, outPorts) ->
            let s = streamSource inPorts

            let out =
                if copies = 1 then stage [] shiftAddMultiply16 "product" s
                elif ordered then streamFarmOrdered "product" copies (2 * copies) 1 worker s
                else streamBalanceClustered copies s |> List.mapi worker |> streamMergeClustered

            streamSink outPorts out)

/// Products through the farm: what came out, and how many cycles it took.
let private multiply (g: Graph) (stallEvery: int) (pairs: (uint64 * uint64) list) : uint64 list * int =
    let sim = Sim (elaborate g).def
    let out = ResizeArray<uint64>()
    let mutable sent = 0
    let mutable cycles = 0

    while out.Count < pairs.Length && cycles < 100_000 do
        let offering = sent < pairs.Length

        if offering then
            sim.Poke("in_a", fst pairs[sent])
            sim.Poke("in_b", snd pairs[sent])

        sim.Poke("in_valid", if offering then 1UL else 0UL)
        sim.Poke("out_ready", if stallEvery > 0 && cycles % stallEvery = 0 then 0UL else 1UL)
        let accepted = offering && sim.Peek "in_ready" = 1UL

        if sim.Peek "out_valid" = 1UL && sim.Peek "out_ready" = 1UL then
            out.Add(sim.Peek "out_product")

        sim.Tick()
        cycles <- cycles + 1

        if accepted then
            sent <- sent + 1

    List.ofSeq out, cycles

// CHECK
let copiesAreAFarm () : bool =
    let random = System.Random 11
    let pairs = [ for _ in 1..64 -> uint64 (random.Next 65536), uint64 (random.Next 65536) ]
    let expected = pairs |> List.map (fun (a, b) -> a * b)
    let one, alone = multiply (farmed 1 true) 0 pairs
    let inOrder, four = multiply (farmed 4 true) 0 pairs
    let stalled, _ = multiply (farmed 4 true) 3 pairs
    let asDone, _ = multiply (farmed 4 false) 3 pairs
    printfn $"      64 products: %d{alone} cycles on one copy, %d{four} on four"

    [ 1, true; 4, true; 4, false ] |> List.forall (fun (copies, ordered) -> same (farmed copies ordered) (farmedPipeline copies ordered))
    && one = expected
    && inOrder = expected
    && stalled = expected
    && List.sort asDone = List.sort expected
    // Four copies, most of four times the beats.
    && four * 3 < alone
    // One copy is the stage: the drawing with `copies = 1` is the chain.
    && verilog (elaborate (farmed 1 true)) = (verilog (elaborate (farmed 1 false))).Replace("Farmed1AsDone", "Farmed1")
    // A combinational unit has nothing to farm.
    && refuses "is combinational" { farmed 2 true with boxes = [ { box "product" "mul16" [] with copies = 2 } ] }

// ---------------------------------------------------------------------------
// MD4 — a design as a box takes copies the same way: a chain as M lanes.

// THE DRAWING
let lanes (copies: int) : Graph =
    let inner = threeBandEq Connection.Forward

    { emptyGraph $"Lanes%d{copies}" rate stereoPins.fields with
        designs = Map.ofList [ inner.name, inner ]
        boxes = [ { box "eq" inner.name [] with copies = copies } ]
        edges =
            [ wire (port "input" "in") (port "eq" "in")
              wire (port "eq" "out") (port "output" "out") ] }

// THE PIPELINE
let lanesPipeline (copies: int) =
    let lane (i: int) (s: Stream<Expr * Expr>) =
        let held = connectAs Connection.Backward s
        let inPorts, outPorts = (threeBandEqPipeline Connection.Forward).NewNamed $"eq%d{i}"
        held |> streamThroughInstance inPorts outPorts |> connectAs Connection.Forward

    defModule $"Lanes%d{copies}" stereoIo (fun (inPorts, outPorts) ->
        streamSource inPorts |> streamFarmOrdered "eq" copies (2 * copies) 1 lane |> streamSink outPorts)

// CHECK — and the lanes hand back what one lane does, in order.
let designTakesCopies () : bool =
    let alone = through (elaborate (lanes 1)).def 0 beats beats.Length
    let two = through (elaborate (lanes 2)).def 3 beats beats.Length

    same (lanes 2) (lanesPipeline 2) && alone.Length = beats.Length && two = alone

// ---------------------------------------------------------------------------
// MD5 — meetings are edits, and the file says only what is not the default.

let meetingsAreEdits () : bool =
    let step (change: Graph -> Result<Graph, string>) (g: Graph) = change g |> Result.defaultWith failwith
    let priority = { policy = ArbitrationPolicy.Priority; timing = ArbitrationTiming.Registered }

    // The diamond by edits: a second edge at a port is allowed, and means
    // broadcast and round-robin until set.
    let drawn =
        { emptyGraph "DiamondBroadcast" rate stereoPins.fields with boxes = [ gainAt "once" gainUnity; gainAt "twice" (2UL * gainUnity) ] }
        |> step (addEdge (port "input" "in", Out) (port "once" streamInlet, In))
        |> step (addEdge (port "input" "in", Out) (port "twice" streamInlet, In))
        |> step (addEdge (port "once" streamOutlet, Out) (port "output" "out", In))
        |> step (addEdge (port "twice" streamOutlet, Out) (port "output" "out", In))
        |> step (setConnection (wire (port "input" "in") (port "once" streamInlet)) Connection.Backward)
        |> step (setConnection (wire (port "input" "in") (port "twice" streamInlet)) Connection.Backward)
        |> step (setConnection (wire (port "once" streamOutlet) (port "output" "out")) Connection.Forward)
        |> step (setConnection (wire (port "twice" streamOutlet) (port "output" "out")) Connection.Forward)

    let chosen =
        drawn
        |> step (setFanOut (port "input" "in") FanOut.Balance)
        |> step (setMerge (port "output" "out") priority)
        |> step (setCopies "once" 3)
        |> step (setOrdered "once" false)

    let back =
        chosen
        |> step (setFanOut (port "input" "in") FanOut.Broadcast)
        |> step (setMerge (port "output" "out") Arbitration.roundRobin)
        |> step (setCopies "once" 1)
        |> step (setOrdered "once" true)

    let roundTrips (g: Graph) = DesignFile.parse (DesignFile.write g) = Ok g
    let plain, stated = DesignFile.write drawn, DesignFile.write chosen
    let says (text: string) (what: string) = text.Contains what

    let refuses (change: Graph -> Result<Graph, string>) (expecting: string) =
        match change drawn with
        | Ok _ -> false
        | Error why -> why.Contains expecting

    drawn = diamond FanOut.Broadcast
    && back = drawn
    && roundTrips drawn
    && roundTrips chosen
    && not (says plain "fanOuts" || says plain "merges" || says plain "copies" || says plain "ordered")
    && says stated "\"input.in\": \"Balance\""
    && says stated "\"policy\": \"Priority\""
    && says stated "\"copies\": 3"
    && says stated "\"ordered\": false"
    // A renamed box takes its meetings with it; a removed one leaves none.
    && (chosen |> step (renameBox "once" "first") |> fun g -> g.boxes.Head.name = "first" && g.merges.ContainsKey(port "output" "out"))
    && (merged priority |> step (renameBox "gain" "amp") |> fun g -> g.merges.ContainsKey(port "amp" streamInlet))
    && (merged priority |> step (removeBox "gain") |> fun g -> g.merges.IsEmpty)
    && refuses (addEdge (port "input" "in", Out) (port "once" streamInlet, In)) "already drawn"
    && refuses (setCopies "once" 0) "at least 1"
    && refuses (setFanOut (port "once" "volume") FanOut.Balance) "once.volume"
    && refuses (setMerge (port "nowhere" "in") priority) "nowhere.in"
