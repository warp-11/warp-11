/// The data form against the typed form: they meet at the bytes, or one of
/// them is lying.
module Warp11.Placement.GraphChecks

open Warp11
open Warp11.Placement.Fu
open Warp11.Placement.Units
open Warp11.Placement.Placement
open Warp11.Placement.Factories
open Warp11.Placement.Graph
open Warp11.Placement.Edit
open Warp11.Placement.Elaborate
open Warp11.Placement.Devices

// ---------------------------------------------------------------------------
// UD1 — The data form and the typed form meet at the bytes.
//
// For every row of the matrix `mac` exercises, the design built from
// `macGraph` emits the same Verilog as `mac`. This is the gate that says the
// GUI's data model is sufficient: nothing `mac` does is unreachable from a
// list of boxes and wires.

// CHECK
let dataIsTheTypedDesign () : bool =
    [ 1, 1, 1; 3, 3, 3; 5, 1, 1; 5, 5, 1; 5, 1, 5 ]
    |> List.forall (fun (s, m, a) -> emitDesign (elaborate (macGraph s m a)).def = emitDesign (mac s m a).def)

// ---------------------------------------------------------------------------
// UD2 — Wires are checked at both ends, by name and by format.
//
// A wire to a pin that does not exist, a wire between pins of different
// formats, an input pin with no wire, a design output nobody drives, a result
// nobody reads, and a cycle — each refuses, naming the pins. This is what a
// GUI draws as a red wire, and it is the elaborator saying it, not the GUI.

// CHECK
let badWiresRefuse () : bool =
    let refuses (edit: Graph -> Graph) (names: string list) =
        try
            (elaborate (edit (macGraph 1 1 1))).def |> ignore
            false
        with e ->
            names |> List.forall (fun n -> e.Message.Contains n)

    // a pin that is not there
    refuses (fun g -> { g with edges = { from = pin "input" "d"; ``to`` = pin "product" "a" } :: List.tail g.edges }) [ "input"; "d" ]
    // formats disagree: 16 bits into a 32-bit operand
    && refuses
        (fun g ->
            { g with
                edges =
                    { from = pin "input" "a"; ``to`` = pin "sum" "y" }
                    :: (g.edges |> List.filter (fun e -> e.``to`` <> pin "sum" "y")) })
        [ "sum"; "y"; "16"; "32" ]
    // an operand nobody wired
    && refuses (fun g -> { g with edges = g.edges |> List.filter (fun e -> e.``to`` <> pin "sum" "y") }) [ "sum"; "y" ]
    // the design's output nobody drives
    && refuses (fun g -> { g with edges = g.edges |> List.filter (fun e -> e.``to``.box <> "output") }) [ "output"; "out" ]
    // a result nobody reads: the product goes nowhere, sum takes c twice
    && refuses
        (fun g ->
            { g with
                edges =
                    g.edges
                    |> List.map (fun e -> if e.from = pin "product" "product" then { e with from = pin "input" "c" } else e) })
        [ "product"; "product" ]
    // a cycle: gain feeds itself (formats agree, so it is the cycle that refuses)
    && (try
            (elaborate
                { gainGraph with
                    edges =
                        gainGraph.edges
                        |> List.map (fun e -> if e.``to`` = pin "gain" "left" then { e with from = pin "gain" "left" } else e) })
                .def
            |> ignore

            false
        with e ->
            e.Message.Contains "cycle" && e.Message.Contains "gain")

// ---------------------------------------------------------------------------
// UD3 — The palette cannot drift from the units.
//
// `erase` is the only way an entry is made, and it reads the typed unit's
// pins. So the format a GUI shows for a box is the format the typed API
// elaborates — by construction, and checked here once.

// CHECK
let paletteIsTheUnits () : bool =
    let unit (name: string) =
        match palette[name].make defaultSampleRate (defaults palette[name]) with
        | Ok u -> u
        | Error why -> failwith why

    (unit "mul16").operands.pins = multiply16.operands.pins
    && (unit "mul16").results.pins = multiply16.results.pins
    && (unit "add32").results.pins = add32.results.pins
    && (unit "gain").controls = gainModule.controls
    && (match (unit "smul16").law, (unit "gain").law with
        | Sequential _, Sequential _ -> true
        | _ -> false)

// ---------------------------------------------------------------------------
// UD4 — A module box. The gain graph and the typed gain patch meet at the
// bytes, and the graph's design scales audio.

// CHECK
let gainGraphIsTheGainPatch () : bool =
    let fromData = elaborate gainGraph
    let sim = Sim fromData.def
    let one beat = streamThrough sim (streamPins "in1" (lower stereoPins)) (streamPins "out1" (lower stereoPins)) [ beat ] |> Seq.head
    sim.Poke("volume", 2UL * gainUnity)
    sim.Poke("mute", 0UL)

    emitDesign fromData.def = emitDesign gainPatch.def
    && one [ 1000UL; 2000UL ] = [ 2000UL; 4000UL ]

// ---------------------------------------------------------------------------
// UD5 — The boundary bound to a device. A tone plays into the gain graph's
// input box through the simulator's mapping; what the output box heard is
// the tone, exactly twice as loud, every frame. And a mapping that does not
// fit the boundary is refused before anything runs, naming the side.

// CHECK
let wavPlaysThroughTheGraph () : bool =
    // A quarter of full scale, so twice as loud still fits a 16-bit sample.
    let tone = toneWav 48_000 200 440.0 0.25

    let heard =
        runInSim 10_000 gainGraph { source = tone; controls = [ "volume", 2UL * gainUnity; "mute", 0UL ]; outputPath = None }

    let doubled = heard.samples = (tone.samples |> Array.map (fun s -> s * 2s))

    let refuses (g: Graph) (m: SimMapping) (names: string list) =
        try
            runInSim 100 g m |> ignore
            false
        with e ->
            names |> List.forall (fun n -> e.Message.Contains n)

    doubled
    && heard.FrameCount = tone.FrameCount
    // mac's boundary is not stereo audio
    && refuses (macGraph 1 1 1) { source = tone; controls = []; outputPath = None } [ "Mac1s1x1"; "input"; "left" ]
    // a control the mapping forgot
    && refuses gainGraph { source = tone; controls = [ "volume", gainUnity ]; outputPath = None } [ "mute" ]

// ---------------------------------------------------------------------------
// UD6 — The editor's changes are functions on the graph, and they compose
// into a design. The gain design built from an empty graph by the same
// edits a canvas makes — boundary pins, a box, wires — elaborates to the
// bytes of the typed gain patch. Every refusal names the pin; removing a box
// takes its wires; undo is exact.

// CHECK
let editsBuildTheGainDesign () : bool =
    let stereo = [ "left", sint sampleWidth; "right", sint sampleWidth ]

    let step (change: Graph -> Result<Graph, string>) (h: History) =
        match apply change h with
        | h, None -> h
        | _, Some why -> failwith why

    let wire (a: string) (b: string) =
        let ends (s: string) =
            match s.Split '.' with
            | [| box; p |] -> pin box p
            | _ -> failwith s

        addWire (ends a, Out) (ends b, In)

    let built =
        history (emptyGraph "GainPatch" defaultSampleRate)
        |> step (addInputPin stereo[0])
        |> step (addInputPin stereo[1])
        |> step (addControl ("volume", uint 16))
        |> step (addControl ("mute", uint 1))
        |> step (addOutputPin stereo[0])
        |> step (addOutputPin stereo[1])
        |> step (addBox "gain" (320.0, 120.0) >> Result.map fst)
        |> step (wire "input.left" "gain.left")
        |> step (wire "input.right" "gain.right")
        |> step (wire "input.volume" "gain.volume")
        |> step (wire "input.mute" "gain.mute")
        |> step (wire "gain.left" "output.left")
        |> step (wire "gain.right" "output.right")

    let g = built.present

    let refuses (change: Graph -> Result<Graph, string>) (names: string list) =
        match change g with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    // The first box of a unit takes the unit's name; the next is numbered.
    let withSecond, second = addBox "gain" (0.0, 0.0) g |> Result.defaultWith failwith
    // Renaming carries the wires.
    let renamed = renameBox "gain" "amp" g |> Result.defaultWith failwith
    // Removing the box takes its six wires with it.
    let removed = removeBox "gain" g |> Result.defaultWith failwith

    emitDesign (elaborate g).def = emitDesign gainPatch.def
    && g.boxes = gainGraph.boxes
    && g.edges = gainGraph.edges
    && built.past.Length = 13
    && (built |> undo |> undo).present.edges.Length = 4
    && (built |> undo |> undo |> redo |> redo).present = g
    && second = "gain2"
    && withSecond.boxes.Length = 2
    && renamed.edges |> List.forall (fun e -> e.from.box <> "gain" && e.``to``.box <> "gain")
    && emitDesign (elaborate renamed).def <> emitDesign gainPatch.def
    && removed.edges = []
    && removed.boxes = []
    // both ends inputs
    && refuses (addWire (pin "gain" "left", In) (pin "output" "left", In)) [ "inputs" ]
    // a box feeding itself
    && refuses (addWire (pin "gain" "left", Out) (pin "gain" "right", In)) [ "gain"; "itself" ]
    // a signal into a control inlet
    && refuses (addWire (pin "input" "left", Out) (pin "gain" "volume", In)) [ "input.left"; "signal"; "gain.volume"; "control" ]
    // the input already has its wire
    && refuses (addWire (pin "input" "right", Out) (pin "gain" "left", In)) [ "gain.left"; "already" ]
    // a pin that is not there
    && refuses (addWire (pin "input" "centre", Out) (pin "gain" "left", In)) [ "input.centre" ]
    // a control named like an input pin shares the input box with it
    && refuses (addControl ("left", uint 8)) [ "left" ]
    // a box cannot be named for the boundary, and a unit must be in the palette
    && refuses (renameBox "gain" "output") [ "output" ]
    && refuses (addBox "biquad" (0.0, 0.0) >> Result.map fst) [ "biquad"; "palette" ]

// ---------------------------------------------------------------------------
// UD7 — A saved design opens as it was, positions included, and elaborates
// to the same bytes. The palette is the type authority: a unit the palette
// does not have, or a wire onto a pin it no longer has, refuses the file by
// name rather than opening it wrong.

// CHECK
let savedDesignOpensAsItWas () : bool =
    let laidOut =
        { gainGraph with
            positions = Map.ofList [ "input", (60.0, 120.0); "gain", (320.5, 140.25); "output", (580.0, 120.0) ] }

    let text = DesignFile.write laidOut

    let reopened =
        match DesignFile.parse text with
        | Ok g -> g
        | Error why -> failwith why

    let refuses (edit: string -> string) (names: string list) =
        match DesignFile.parse (edit text) with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    reopened = laidOut
    && emitDesign (elaborate reopened).def = emitDesign gainPatch.def
    && (match DesignFile.parse (DesignFile.write (macGraph 3 3 3)) with
        | Ok g -> g = macGraph 3 3 3
        | Error _ -> false)
    // a unit the palette does not have
    && refuses (fun t -> t.Replace("\"unit\": \"gain\"", "\"unit\": \"biquad\"")) [ "gain"; "biquad"; "palette" ]
    // a wire onto a pin the unit does not have
    && refuses (fun t -> t.Replace("\"to\": \"gain.mute\"", "\"to\": \"gain.bypass\"")) [ "gain.bypass" ]
    // a wire whose format the unit no longer agrees with
    && refuses (fun t -> t.Replace("\"name\": \"volume\",\n      \"width\": 16", "\"name\": \"volume\",\n      \"width\": 8")) [ "volume"; "8w"; "16w" ]
    // not a design at all
    && refuses (fun _ -> "{ \"name\": 3 }") [ "name" ]
    && refuses (fun _ -> "nonsense") [ "JSON" ]

// ---------------------------------------------------------------------------
// UD8 — A box is a factory and its creation arguments. Three `eq` boxes with
// shapes, corners and gains typed as text, chained, elaborate to the bytes
// of the typed form — three biquad units whose coefficients were designed
// by hand for the same rate. The arguments reach the hardware: a low-pass
// at 100 Hz silences a 5 kHz tone, and a flat peaking band passes it. And
// what a factory refuses, it refuses naming the parameter: a corner above
// half the rate, a shape that is not one, a capacity that is not a power of
// two, an argument the unit does not have — at the box, at the file, and when
// the design's rate moves under a box.

// CHECK
let argumentsMakeTheUnit () : bool =
    let rate = 48_000.0

    let step (change: Graph -> Result<Graph, string>) (h: History) =
        match apply change h with
        | h, None -> h
        | _, Some why -> failwith why

    let wire (a: string) (b: string) =
        let ends (s: string) =
            match s.Split '.' with
            | [| box; p |] -> pin box p
            | _ -> failwith s

        addWire (ends a, Out) (ends b, In)

    let band (name: string) (shape: string) (fc: string) (gain: string) (h: History) =
        h
        |> step (addBox "eq" (0.0, 0.0) >> Result.map fst)
        |> step (renameBox "eq" name)
        |> step (setArgument name "shape" shape)
        |> step (setArgument name "fc" fc)
        |> step (setArgument name "gain" gain)

    let threeBand (low: string) (mid: string) (high: string) =
        (history (emptyGraph "ThreeBandEq" rate)
         |> step (addInputPin stereoPins.pins[0])
         |> step (addInputPin stereoPins.pins[1])
         |> step (addOutputPin stereoPins.pins[0])
         |> step (addOutputPin stereoPins.pins[1])
         |> band "low" low "200" "6"
         |> band "mid" mid "1000" "-4"
         |> band "high" high "5000" "3"
         |> step (wire "input.left" "low.left")
         |> step (wire "input.right" "low.right")
         |> step (wire "low.left" "mid.left")
         |> step (wire "low.right" "mid.right")
         |> step (wire "mid.left" "high.left")
         |> step (wire "mid.right" "high.right")
         |> step (wire "high.left" "output.left")
         |> step (wire "high.right" "output.right"))
            .present

    let g = threeBand "lowshelf" "peaking" "highshelf"

    // The typed form: the same three sections, coefficients designed by hand.
    let typed =
        let section (shape: EqType) (fc: float) (gainDb: float) =
            let coefficients = toQ230 (rbjDesign shape fc 0.707 gainDb rate) |> List.map (fun v -> lit v biquadCoeffWidth)
            moduleUnit "eq" stereoPins stereoPins [] (fun instance _ s -> audioEqBand "AudioEqBand" instance coefficients s)

        defModule
            "ThreeBandEq"
            (fun p -> streamInputPorts p "in1" (lower stereoPins), streamOutputPorts p "out1" (lower stereoPins))
            (fun (inPorts, outPorts) ->
                [ streamSource inPorts ]
                |> fuStagesWith [] (section LowShelf 200.0 6.0) "low" stereoPins id (fun r _ -> r)
                |> fuStagesWith [] (section Peaking 1000.0 -4.0) "mid" stereoPins id (fun r _ -> r)
                |> fuStagesWith [] (section HighShelf 5000.0 3.0) "high" stereoPins id (fun r _ -> r)
                |> List.iter2 streamSink [ outPorts ])

    let reopened =
        match DesignFile.parse (DesignFile.write g) with
        | Ok g -> g
        | Error why -> failwith why

    // A 5 kHz tone through a 100 Hz low-pass is silenced; through a flat band it is not.
    let tone = toneWav (int rate) 200 5000.0 0.25
    let peak (w: WavData) = w.samples |> Array.map (fun s -> abs (int s)) |> Array.max

    let heard (g: Graph) =
        runInSim 10_000 g { source = tone; controls = []; outputPath = None }

    let lowPassed =
        let h = history g |> step (setArgument "low" "shape" "lowpass") |> step (setArgument "low" "fc" "100")
        heard h.present

    let refuses (change: Graph -> Result<Graph, string>) (names: string list) =
        match change g with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    let fileRefuses (edit: string -> string) (names: string list) =
        match DesignFile.parse (edit (DesignFile.write g)) with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    emitDesign (elaborate g).def = emitDesign typed.def
    && reopened = g
    && emitDesign (elaborate reopened).def = emitDesign typed.def
    && peak (heard g) > peak tone / 2
    && peak lowPassed < peak tone / 20
    // a corner above half the rate
    && refuses (setArgument "mid" "fc" "30000") [ "mid"; "fc"; "30000" ]
    // not a number
    && refuses (setArgument "mid" "q" "loud") [ "mid"; "q"; "loud" ]
    // not a shape
    && refuses (setArgument "mid" "shape" "notch") [ "mid"; "shape"; "notch"; "peaking" ]
    // not a parameter
    && refuses (setArgument "mid" "fq" "1") [ "mid"; "fq"; "shape, fc, q, gain" ]
    // the rate moved under the high band
    && refuses (setSampleRate 8_000.0) [ "high"; "fc"; "5000" ]
    // an echo whose line is not a power of two
    && refuses (fun g -> addBox "echo" (0.0, 0.0) g |> Result.bind (fun (g, n) -> setArgument n "capacity" "1000" g)) [ "echo"; "capacity"; "1000" ]
    // a file with an argument the unit does not have, and one the factory refuses
    && fileRefuses (fun t -> t.Replace("\"fc\": \"1000\"", "\"corner\": \"1000\"")) [ "mid"; "corner" ]
    && fileRefuses (fun t -> t.Replace("\"fc\": \"1000\"", "\"fc\": \"90000\"")) [ "mid"; "fc"; "90000" ]
    // a mapping at another rate
    && (try
            runInSim 100 g { source = toneWav 44_100 10 440.0 0.25; controls = []; outputPath = None } |> ignore
            false
        with e ->
            e.Message.Contains "48000" && e.Message.Contains "44100")
