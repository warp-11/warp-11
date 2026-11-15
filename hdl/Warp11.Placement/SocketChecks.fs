/// Sharing, drawn: a unit's box as a service, and the sockets of the boxes
/// that need it as edges into it. Each against the typed form at the bytes,
/// and against the same design with nothing shared, at the bits.
module Warp11.Placement.SocketChecks

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Factories
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
        let line (lines: string array) = (if at < lines.Length then lines[at] else "(end)") |> fun l -> l.Substring(0, min l.Length 160)
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

let private stereoIo (p: Ports) = streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins

let private gainMultiplierName = gainMultiplier.name
let private eqMultiplierName = eqMultiplier.name

let private priorityRegistered =
    { policy = ArbitrationPolicy.Priority
      timing = ArbitrationTiming.Registered }

/// Beats through a design's `in` and `out`, the output stalled every third
/// cycle when asked.
let private through (design: ModuleDef) (controls: (string * uint64) list) (stallEvery: int) (beats: uint64 list list) : uint64 list list =
    let sim = Sim design

    for name, value in controls do
        sim.Poke(name, value)

    beats
    |> streamThroughWith sim (streamPins "in" stereoPins) (streamPins "out" stereoPins) stallEvery 4000
    |> Seq.truncate beats.Length
    |> List.ofSeq

let private beats =
    let random = System.Random 5
    [ for _ in 1..60 -> [ uint64 (random.Next(1 <<< 20)); uint64 (random.Next(1 <<< 20)) ] ]

// ---------------------------------------------------------------------------
// SD1 — the gain's two channels on one multiplier: the multiplier is a box,
// the gain's socket port an edge into it.

// THE DRAWING
let sharedGain (arbitration: Arbitration) : Graph =
    let suffix = if arbitration = Arbitration.roundRobin then "" else $"{arbitration.policy}{arbitration.timing}"

    { gainGraph with
        name = $"SharedGain{suffix}"
        boxes = gainGraph.boxes @ [ box "multiplier" gainMultiplierName [] ]
        merges = (if arbitration = Arbitration.roundRobin then Map.empty else Map.ofList [ port "multiplier" servicePort, arbitration ])
        edges = gainGraph.edges @ [ wire (port "gain" "multiply") (port "multiplier" servicePort) ] }

// THE TYPED FORM
let sharedGainTyped (arbitration: Arbitration) =
    let suffix = if arbitration = Arbitration.roundRobin then "" else $"{arbitration.policy}{arbitration.timing}"

    defModule
        $"SharedGain{suffix}"
        (fun p -> streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins, p.inPort "volume" 16, p.inPort "mute" 1)
        (fun (inPorts, outPorts, volume, mute) ->
            sharingWith arbitration gainMultiplier "multiplier" (fun multiply ->
                streamSource inPorts |> audioGainWith (fun _ -> multiply) "AudioGain" "gain" volume mute)
            |> streamSink outPorts)

// CHECK — the same bits as the gain with a multiplier a channel.
let socketsShareAUnit () : bool =
    let controls = [ "volume", 3UL * gainUnity / 2UL; "mute", 0UL ]
    let inPlace = through (elaborate gainGraph).def controls 3 beats
    let shared = through (elaborate (sharedGain Arbitration.roundRobin)).def controls 3 beats
    let ranked = through (elaborate (sharedGain priorityRegistered)).def controls 3 beats
    let multiplies (g: Graph) = (verilog (elaborate g)).Split(" * ").Length - 1

    same (sharedGain Arbitration.roundRobin) (sharedGainTyped Arbitration.roundRobin)
    && same (sharedGain priorityRegistered) (sharedGainTyped priorityRegistered)
    && inPlace.Length = beats.Length
    && shared = inPlace
    && ranked = inPlace
    && multiplies gainGraph = 2
    && multiplies (sharedGain Arbitration.roundRobin) = 1

// ---------------------------------------------------------------------------
// SD2 — three bands. A multiplier a band: three service boxes, each band's
// ten products on its own. And one multiplier for all thirty.

// THE DRAWING
let sharedEq (multipliers: int) : Graph =
    let chain = threeBandEq Connection.Backward
    let bands = [ "low"; "mid"; "high" ]
    let serviceFor (i: int) = if multipliers = 1 then "multiplier" else $"multiplier_{bands[i]}"

    { chain with
        name = $"SharedEq%d{multipliers}"
        boxes = chain.boxes @ [ for name in List.distinct [ for i in 0..2 -> serviceFor i ] -> box name eqMultiplierName [] ]
        edges = chain.edges @ [ for i, band in List.indexed bands -> wire (port band "multiply") (port (serviceFor i) servicePort) ] }

// THE TYPED FORM
let sharedEqTyped (multipliers: int) =
    let band (multiply: Stream<Expr * Expr> -> Stream<Expr>) (shape: EqType) (fc: float) (gainDb: float) (instance: string) =
        stage [] (eqSectionWith (fun _ _ -> multiply) shape fc 0.707 gainDb rate) instance

    defModule $"SharedEq%d{multipliers}" stereoIo (fun (inPorts, outPorts) ->
        let chain (low: Stream<Expr * Expr> -> Stream<Expr>) mid high =
            streamSource inPorts
            |> band low LowShelf 200.0 6.0 "low"
            |> connectAs Connection.Backward
            |> band mid Peaking 1000.0 -4.0 "mid"
            |> connectAs Connection.Backward
            |> band high HighShelf 5000.0 3.0 "high"

        let out =
            if multipliers = 1 then
                sharing eqMultiplier "multiplier" (fun multiply -> chain multiply multiply multiply)
            else
                sharing eqMultiplier "multiplier_low" (fun low ->
                    sharing eqMultiplier "multiplier_mid" (fun mid ->
                        sharing eqMultiplier "multiplier_high" (fun high -> chain low mid high)))

        streamSink outPorts out)

// CHECK
let bandsShareMultipliers () : bool =
    let spatial = through (elaborate (threeBandEq Connection.Backward)).def [] 3 beats
    let perBand = through (elaborate (sharedEq 3)).def [] 3 beats
    let one = through (elaborate (sharedEq 1)).def [] 3 beats
    let multiplies (g: Graph) = (verilog (elaborate g)).Split(" * ").Length - 1
    printfn $"      multiplies: %d{multiplies (threeBandEq Connection.Backward)} in place, %d{multiplies (sharedEq 3)} a band, %d{multiplies (sharedEq 1)} shared by all"

    same (sharedEq 3) (sharedEqTyped 3)
    && same (sharedEq 1) (sharedEqTyped 1)
    && spatial.Length = beats.Length
    && perBand = spatial
    && one = spatial
    && multiplies (sharedEq 3) = 3
    && multiplies (sharedEq 1) = 1

// ---------------------------------------------------------------------------
// SD3 — what fits, and what a box can be.

let socketsFitTheirService () : bool =
    let g = sharedGain Arbitration.roundRobin
    let socket = wire (port "gain" "multiply") (port "multiplier" servicePort)
    let isNot (fit: Fit) = match fit with DoesNotFit _ -> true | _ -> false

    // The eq's multiplier is another width: the gain's socket does not fit it.
    let wrongUnit = { g with boxes = gainGraph.boxes @ [ box "multiplier" eqMultiplierName [] ] }

    // A stage as well as a service.
    let both =
        { emptyGraph "Both" rate gainMultiplier.operands.fields with
            outputs = [ { name = "out"; fields = gainMultiplier.results.fields } ]
            boxes = [ box "multiplier" gainMultiplierName [] ]
            edges = [ wire (port "input" "in") (port "multiplier" streamInlet); wire (port "multiplier" streamOutlet) (port "output" "out") ] }

    edgeFit g socket = Fits
    && isNot (edgeFit wrongUnit socket)
    && refuses "gain.multiply → multiplier.service" wrongUnit
    // a socket's edge registers nothing
    && isNot (edgeFit g { socket with connection = Connection.Forward })
    // a socket goes to a service, a stream does not
    && isNot (edgeFit g (wire (port "gain" "multiply") (port "output" "out")))
    && isNot (edgeFit g (wire (port "input" "in") (port "multiplier" servicePort)))
    // a unit with controls, or a module, offers no service
    && isNot (edgeFit { g with boxes = g.boxes @ [ box "other" "gain" [] ] } (wire (port "gain" "multiply") (port "other" servicePort)))
    // the multiplier alone is a stage, and builds
    && (verilog (elaborate both)).Contains "module Both"
    // ...and with a socket into it as well it is neither
    && refuses "a service or a stage, not both"
        { g with
            inputs = g.inputs @ [ { name = "side"; fields = gainMultiplier.operands.fields } ]
            outputs = g.outputs @ [ { name = "product"; fields = gainMultiplier.results.fields } ]
            edges = g.edges @ [ wire (port "input" "side") (port "multiplier" streamInlet); wire (port "multiplier" streamOutlet) (port "output" "product") ] }
    && refuses "a service is one copy" { g with boxes = gainGraph.boxes @ [ { box "multiplier" gainMultiplierName [] with copies = 2 } ] }

// ---------------------------------------------------------------------------
// SD4 — sharing is drawn by the same edits, and saved in the same file.

let sharingIsAnEdit () : bool =
    let step (change: Graph -> Result<Graph, string>) (g: Graph) = change g |> Result.defaultWith failwith

    let drawn =
        { gainGraph with name = "SharedGain" }
        |> step (addBox gainMultiplierName (0.0, 0.0) >> Result.map fst)
        |> step (renameBox gainMultiplierName "multiplier")
        |> step (addEdge (port "gain" "multiply", Out) (port "multiplier" servicePort, In))

    let ranked = drawn |> step (setMerge (port "multiplier" servicePort) priorityRegistered)

    let refuses (change: Graph -> Result<Graph, string>) (expecting: string) =
        match change drawn with
        | Ok _ -> false
        | Error why -> why.Contains expecting

    { drawn with positions = Map.empty } = sharedGain Arbitration.roundRobin
    && { ranked with positions = Map.empty; name = (sharedGain priorityRegistered).name } = sharedGain priorityRegistered
    && DesignFile.parse (DesignFile.write ranked) = Ok ranked
    && (DesignFile.write ranked).Contains "\"multiplier.service\""
    // a socket connects to one service
    && refuses (addBox gainMultiplierName (0.0, 0.0) >> Result.map fst >> Result.bind (addEdge (port "gain" "multiply", Out) (port gainMultiplierName servicePort, In))) "a socket connects to one service"
