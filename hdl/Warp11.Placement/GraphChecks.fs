/// The data form against the typed form: they meet at the bytes, or one of
/// them is lying.
module Warp11.Placement.GraphChecks

open Warp11
open Warp11.Placement.Fu
open Warp11.Placement.Units
open Warp11.Placement.Placement
open Warp11.Placement.Graph
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
    palette["mul16"].operands.pins = multiply16.operands.pins
    && palette["mul16"].results.pins = multiply16.results.pins
    && palette["add32"].results.pins = add32.results.pins
    && palette["gain"].controls = gainModule.controls
    && (match palette["smul16"].law, palette["gain"].law with
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
