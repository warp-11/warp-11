/// The port model's checks for chains: a drawn design against the pipeline
/// a person writes, at the bytes; what an edge registers; what fits.
module Warp11.Placement.ChainChecks

open Warp11
open Warp11.Fu
open Warp11.Graph
open Warp11.Elaborate
open Warp11.Factories
open Warp11.Placement.Placement

/// The rate the recordings beside `Warp11.Effects` are at.
let rate = 48_828.0

let private verilog (m: TypedModule<'io>) = Warp11.Verilog.emitDesign m.def

let private same (drawn: Graph) (typed: TypedModule<'io>) : bool =
    let a, b = verilog (elaborate drawn), verilog typed

    if a <> b then
        let la, lb = a.Split '\n', b.Split '\n'
        let at = Seq.zip la lb |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min la.Length lb.Length)
        printfn $"      {drawn.name}: first difference at line %d{at + 1}"
        let line (lines: string array) = if at < lines.Length then lines[at] else "(end)"
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

let box (name: string) (unit: string) (arguments: (string * string) list) : Box = newBox name unit (Map.ofList arguments)

// ---------------------------------------------------------------------------
// CD1 — a stream through one box, its controls the design's own.

// THE DRAWING is `Graph.gainGraph`.

// THE PIPELINE
let gainPipeline =
    defModule
        "GainPatch"
        (fun p -> streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins, p.inPort "volume" 16, p.inPort "mute" 1)
        (fun (inPorts, outPorts, volume, mute) -> streamSource inPorts |> audioGain "AudioGain" "gain" volume mute |> streamSink outPorts)

// CHECK
let drawnGainIsThePipeline () : bool = same gainGraph gainPipeline

// ---------------------------------------------------------------------------
// CD2 — three bands in a row, a register stage on each edge between them.

// THE DRAWING
let threeBandEq (between: Connection) : Graph =
    let stereo = stereoPins.fields

    { emptyGraph "ThreeBandEq" rate stereo with
        boxes =
            [ box "low" "eq" [ "shape", "lowshelf"; "fc", "200"; "gain", "6" ]
              box "mid" "eq" [ "shape", "peaking"; "fc", "1000"; "gain", "-4" ]
              box "high" "eq" [ "shape", "highshelf"; "fc", "5000"; "gain", "3" ] ]
        edges =
            [ wire (port "input" "in") (port "low" streamInlet)
              edgeAs between (port "low" streamOutlet) (port "mid" streamInlet)
              edgeAs between (port "mid" streamOutlet) (port "high" streamInlet)
              wire (port "high" streamOutlet) (port "output" "out") ] }

// THE PIPELINE
let threeBandEqPipeline (between: Connection) =
    let band (shape: EqType) (fc: float) (gainDb: float) (instance: string) =
        stage [] (eqSection shape fc 0.707 gainDb rate) instance

    defModule
        "ThreeBandEq"
        (fun p -> streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins)
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> band LowShelf 200.0 6.0 "low"
            |> connectAs between
            |> band Peaking 1000.0 -4.0 "mid"
            |> connectAs between
            |> band HighShelf 5000.0 3.0 "high"
            |> streamSink outPorts)

// CHECK — and a connection changes the hardware, so the four are four designs.
let edgeIsItsConnection () : bool =
    let connections = [ Connection.Wire; Connection.Forward; Connection.Backward; Connection.Both ]
    let each = connections |> List.forall (fun c -> same (threeBandEq c) (threeBandEqPipeline c))
    let distinct = connections |> List.map (fun c -> verilog (elaborate (threeBandEq c))) |> List.distinct |> List.length
    each && distinct = connections.Length

// ---------------------------------------------------------------------------
// CD3 — a design as a box: the three-band EQ twice, `Backward` between.

// THE DRAWING
let twice: Graph =
    let inner = threeBandEq Connection.Forward

    { emptyGraph "Twice" rate stereoPins.fields with
        designs = Map.ofList [ inner.name, inner ]
        boxes = [ box "first" inner.name []; box "second" inner.name [] ]
        edges =
            [ wire (port "input" "in") (port "first" "in")
              edgeAs Connection.Backward (port "first" "out") (port "second" "in")
              wire (port "second" "out") (port "output" "out") ] }

// THE PIPELINE
let twicePipeline =
    let inner = threeBandEqPipeline Connection.Forward

    let through (instance: string) (s: Stream<Expr * Expr>) =
        let inPorts, outPorts = inner.NewNamed instance
        streamThroughInstance inPorts outPorts s

    defModule
        "Twice"
        (fun p -> streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins)
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> through "first"
            |> connectAs Connection.Backward
            |> through "second"
            |> streamSink outPorts)

// CHECK
let designIsABox () : bool = same twice twicePipeline

// ---------------------------------------------------------------------------
// CD4 — controls: a number box is a port of the design under its own name,
// a constant is a literal, and an inlet nobody wired is a literal of its
// setting, as an argument written in code is.

// THE DRAWING
let controlsGraph (mute: ControlKind) : Graph =
    { gainGraph with
        name = "Controls"
        controls = []
        boxes = [ { box "gain" "gain" [] with settings = Map.ofList [ "volume", "512" ] } ]
        controlBoxes = [ { name = "toggle"; kind = mute; format = unsignedInt 1; value = "0" } ]
        edges =
            [ wire (port "input" "in") (port "gain" streamInlet)
              wire (port "toggle" controlOutlet) (port "gain" "mute")
              wire (port "gain" streamOutlet) (port "output" "out") ] }

// THE PIPELINES
let controlsPipeline =
    defModule
        "Controls"
        (fun p -> streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins, p.inPort "toggle" 1)
        (fun (inPorts, outPorts, toggle) -> streamSource inPorts |> audioGain "AudioGain" "gain" (lit 512UL 16) toggle |> streamSink outPorts)

let constantPipeline =
    defModule
        "Controls"
        (fun p -> streamInputPorts p "in" stereoPins, streamOutputPorts p "out" stereoPins)
        (fun (inPorts, outPorts) -> streamSource inPorts |> audioGain "AudioGain" "gain" (lit 512UL 16) (lit 0UL 1) |> streamSink outPorts)

// CHECK — and a design in a box hands its number box up as an inlet, which
// unwired holds what that number box starts at.
let controlsHoldValues () : bool =
    let inner = { controlsGraph NumberBox with controlBoxes = [ { name = "toggle"; kind = NumberBox; format = unsignedInt 1; value = "1" } ] }

    let outer (edges: Edge list) (controls: (string * NumberFormat) list) : Graph =
        { emptyGraph "Outer" rate stereoPins.fields with
            controls = controls
            designs = Map.ofList [ inner.name, inner ]
            boxes = [ box "inner" inner.name [] ]
            edges = [ wire (port "input" "in") (port "inner" "in"); wire (port "inner" "out") (port "output" "out") ] @ edges }

    let muted (g: Graph) : bool =
        let sim = Sim (elaborate g).def
        if g.controls <> [] then sim.Poke("mute", 0UL)
        let beat = streamThrough sim (streamPins "in" stereoPins) (streamPins "out" stereoPins) [ [ 1000UL; 2000UL ] ] |> Seq.head
        beat = [ 0UL; 0UL ]

    same (controlsGraph NumberBox) controlsPipeline
    && same (controlsGraph ConstantBox) constantPipeline
    // Unwired, the inner toggle's own start: muted. Wired to the outer
    // design's control and poked to zero: not.
    && muted (outer [] [])
    && not (muted (outer [ wire (port "input" "mute") (port "inner" "toggle") ] [ "mute", unsignedInt 1 ]))

// ---------------------------------------------------------------------------
// CD5 — beats through the drawn gain come out scaled by its controls.

let beatsThroughTheDrawnGain () : bool =
    let sim = Sim (elaborate gainGraph).def
    let one beat = streamThrough sim (streamPins "in" stereoPins) (streamPins "out" stereoPins) [ beat ] |> Seq.head
    sim.Poke("volume", 2UL * gainUnity)
    sim.Poke("mute", 0UL)
    let doubled = one [ 1000UL; 2000UL ]
    sim.Poke("volume", gainUnity)
    let unity = one [ 1000UL; 2000UL ]
    sim.Poke("mute", 1UL)
    let muted = one [ 1000UL; 2000UL ]
    doubled = [ 2000UL; 4000UL ] && unity = [ 1000UL; 2000UL ] && muted = [ 0UL; 0UL ]

// ---------------------------------------------------------------------------
// CD6 — what makes two stream ports fit: the formats in order. Names never
// refuse; they are reported.

let private pairGraph (name: string) (boundary: Fields) (unit: string) : Graph =
    { emptyGraph name rate boundary with
        outputs = []
        boxes = [ box "it" unit [] ]
        edges = [ wire (port "input" "in") (port "it" streamInlet) ] }

let edgeFits () : bool =
    let fitOf (g: Graph) = edgeFit g g.edges.Head
    let audio = signedInt sampleWidth

    // The same names and formats.
    let plain = fitOf (pairGraph "Plain" [ "left", audio; "right", audio ] "gain")
    // The same formats under other names: fits, and says which.
    let renamed = fitOf (pairGraph "Renamed" [ "dry", audio; "right", audio ] "gain")
    // A field short, a width off, the other signedness, other fraction bits.
    let short = fitOf (pairGraph "Short" [ "left", audio ] "gain")
    let narrow = fitOf (pairGraph "Narrow" [ "left", audio; "right", signedInt 16 ] "gain")
    let unsigned = fitOf (pairGraph "Unsigned" [ "left", audio; "right", unsignedInt sampleWidth ] "gain")
    let fraction = fitOf (pairGraph "Fraction" [ "left", audio; "right", { audio with fracBits = 8 } ] "gain")
    let isNot fit = match fit with DoesNotFit _ -> true | _ -> false

    // A control edge: its format, and no connection but a wire.
    let control (format: NumberFormat) (connection: Connection) =
        let g = { gainGraph with controls = [ "volume", format; "mute", unsignedInt 1 ] }
        edgeFit g (edgeAs connection (port "input" "volume") (port "gain" "volume"))

    plain = Fits
    && renamed = FitsNamesDiffer [ "dry", "left" ]
    && List.forall isNot [ short; narrow; unsigned; fraction ]
    && control (unsignedInt 16) Connection.Wire = Fits
    && isNot (control (unsignedInt 8) Connection.Wire)
    && isNot (control (unsignedInt 16) Connection.Forward)
    // By another name still builds, and builds what the same names build.
    && (let named (first: string) =
            { emptyGraph "Named" rate [ first, audio; "right", audio ] with
                outputs = [ { name = "out"; fields = [ "left", audio; "right", audio ] } ]
                controls = gainGraph.controls
                boxes = gainGraph.boxes
                edges = gainGraph.edges }

        let ports (text: string) = text.Replace("in_dry", "in_left")
        ports (verilog (elaborate (named "dry"))) = verilog (elaborate (named "left")))

// ---------------------------------------------------------------------------
// CD7 — a side that changes after the edge was drawn. The blur's row is as
// wide as its `columns`; change the argument and the edges are still there,
// judged again, and the design refuses naming them.

let changedSideLeavesItsEdge () : bool =
    // The boundary carries the row the blur took when the edges were drawn.
    let drawnFor = (Warp11.Units.blurUnit 64 64).operands.fields

    let blurred (columns: int) : Graph =
        { emptyGraph "Blurred" defaultSampleRate drawnFor with
            boxes = [ box "blur" "blur" [ "columns", string columns; "rows", "64" ] ]
            edges =
                [ wire (port "input" "in") (port "blur" streamInlet)
                  wire (port "blur" streamOutlet) (port "output" "out") ] }

    let before, after = blurred 64, blurred 32

    before.edges |> List.forall (fun e -> edgeFit before e = Fits)
    && after.edges = before.edges
    && after.edges |> List.forall (fun e -> match edgeFit after e with DoesNotFit _ -> true | _ -> false)
    && refuses "input.in → blur.in" after

// ---------------------------------------------------------------------------
// CD8 — everything an editor would draw red is refused, naming the port.

let badEdgesRefuse () : bool =
    let without (e: Edge) (g: Graph) = { g with edges = g.edges |> List.filter ((<>) e) }
    let chain = threeBandEq Connection.Wire
    let lowToMid = wire (port "low" streamOutlet) (port "mid" streamInlet)

    // A stream port nobody connected, on either side.
    refuses "mid.in: nothing is connected to it" (chain |> without lowToMid |> fun g -> { g with edges = g.edges @ [ wire (port "low" streamOutlet) (port "output" "out") ] } |> without (wire (port "high" streamOutlet) (port "output" "out")))
    && refuses "output.out: nothing is connected to it" (gainGraph |> without (wire (port "gain" streamOutlet) (port "output" "out")))
    && refuses "input.in: its stream goes nowhere" (gainGraph |> without (wire (port "input" "in") (port "gain" streamInlet)))
    // A stream of other fields does not merge with this one.
    && refuses "side → gain.in"
        { gainGraph with
            inputs = gainGraph.inputs @ [ { name = "side"; fields = [ "mono", signedInt sampleWidth ] } ]
            edges = gainGraph.edges @ [ wire (port "input" "side") (port "gain" streamInlet) ] }
    // A port or a box that is not there.
    && refuses "gain.sideways: no such input port" { gainGraph with edges = gainGraph.edges @ [ wire (port "input" "volume") (port "gain" "sideways") ] }
    && refuses "nowhere.out: no such box" { gainGraph with edges = gainGraph.edges @ [ wire (port "nowhere" "out") (port "gain" "mute") ] }
    // Kinds do not mix.
    && refuses "a control into a stream" { gainGraph with edges = [ wire (port "input" "volume") (port "gain" streamInlet) ] }
    // A control inlet takes one edge.
    && refuses "gain.mute: 2 edges land on it"
        { gainGraph with
            controls = gainGraph.controls @ [ "other", unsignedInt 1 ]
            edges = gainGraph.edges @ [ wire (port "input" "other") (port "gain" "mute") ] }
    // The same edge twice.
    && refuses "input.mute → gain.mute: drawn twice" { gainGraph with edges = gainGraph.edges @ [ wire (port "input" "mute") (port "gain" "mute") ] }
    // A cycle.
    && refuses "cycle" { chain with edges = [ wire (port "input" "in") (port "low" streamInlet); wire (port "low" streamOutlet) (port "output" "out"); wire (port "mid" streamOutlet) (port "high" streamInlet); wire (port "high" streamOutlet) (port "mid" streamInlet) ] }
    // A unit nobody has.
    && refuses "no unit called 'theremin'" { gainGraph with boxes = [ box "gain" "theremin" [] ] }
