/// The boxes that are what code does between stages: a join, a regrouping
/// of fields, a buffer. Each against the call a person writes, at the bytes.
module Warp11.Placement.BoxChecks

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

let private through (design: ModuleDef) (stallEvery: int) (beats: uint64 list list) : uint64 list list =
    let sim = Sim design

    beats
    |> streamThroughWith sim (streamPins "in" stereoPins) (streamPins "out" stereoPins) stallEvery 2000
    |> Seq.truncate beats.Length
    |> List.ofSeq

let private beats = [ for i in 1UL .. 40UL -> [ i * 100UL; i * 100UL + 7UL ] ]

// ---------------------------------------------------------------------------
// BD1 — multiply and accumulate, `a * b + c`: three streams on the boundary,
// joined into the beats the units take. Nothing is carried past a box; what
// the adder needs beside the product is joined to it.

// THE DRAWING
let macDrawn: Graph =
    let u16, u32 = unsignedInt 16, unsignedInt 32

    { emptyGraph "Mac" rate [] with
        inputs = [ { name = "a"; fields = [ "a", u16 ] }; { name = "b"; fields = [ "b", u16 ] }; { name = "c"; fields = [ "c", u32 ] } ]
        outputs = [ { name = "out"; fields = [ "sum", unsignedInt 33 ] } ]
        boxes =
            [ box "factors" joinBox []
              box "product" "mul16" []
              box "terms" joinBox []
              box "sum" "add32" [] ]
        edges =
            [ wire (port "input" "a") (port "factors" "in0")
              wire (port "input" "b") (port "factors" "in1")
              wire (port "factors" streamOutlet) (port "product" streamInlet)
              wire (port "product" streamOutlet) (port "terms" "in0")
              wire (port "input" "c") (port "terms" "in1")
              wire (port "terms" streamOutlet) (port "sum" streamInlet)
              wire (port "sum" streamOutlet) (port "output" "out") ] }

// THE PIPELINE
let macPipeline =
    let u16, u32 = unsignedInt 16, unsignedInt 32

    defModule
        "Mac"
        (fun p ->
            streamInputPorts p "a" (pins1 ("a", u16)),
            streamInputPorts p "b" (pins1 ("b", u16)),
            streamInputPorts p "c" (pins1 ("c", u32)),
            streamOutputPorts p "out" (pins1 ("sum", unsignedInt 33)))
        (fun (a, b, c, out) ->
            let a, b, c = streamSource a, streamSource b, streamSource c
            let product = streamJoin (pins2 ("a", u16) ("b", u16)) [ a; b ] |> stage [] multiply16 "product"

            streamJoin (pins2 ("product", u32) ("c", u32)) [ product; c ]
            |> stage [] add32 "sum"
            |> streamSink out)

// CHECK — and it computes.
let joinsMakeTheBeat () : bool =
    let sim = Sim (elaborate macDrawn).def
    sim.Poke("out_ready", 1UL)

    let one (a: uint64) (b: uint64) (c: uint64) =
        sim.Poke("a_a", a)
        sim.Poke("b_b", b)
        sim.Poke("c_c", c)

        for stream in [ "a"; "b"; "c" ] do
            sim.Poke($"{stream}_valid", 1UL)

        let valid, sum = sim.Peek "out_valid", sim.Peek "out_sum"
        sim.Tick()
        valid = 1UL && sum = a * b + c

    // A beat leaves only when every side has one.
    let waits =
        let fresh = Sim (elaborate macDrawn).def
        fresh.Poke("out_ready", 1UL)
        fresh.Poke("a_valid", 1UL)
        fresh.Poke("b_valid", 1UL)
        fresh.Peek "out_valid" = 0UL && fresh.Peek "a_ready" = 0UL

    same macDrawn macPipeline
    // ...and the example, built by edits, is the same design.
    && { Examples.mac with positions = Map.empty; sampleRate = rate } = { macDrawn with boxes = Examples.mac.boxes }
    && same { Examples.mac with sampleRate = rate } macPipeline
    && one 3UL 4UL 5UL
    && one 65535UL 65535UL 4294967295UL
    && waits
    // The join takes its fields from what is connected: its sides and its
    // stream out are the factors'.
    && (streamsOf macDrawn "factors" |> snd |> List.map (fun s -> s.fields)) = [ [ "a", unsignedInt 16; "b", unsignedInt 16 ] ]
    && macDrawn.edges |> List.forall (fun e -> match edgeFit macDrawn e with DoesNotFit _ -> false | _ -> true)
    && edgeFit macDrawn (wire (port "terms" streamOutlet) (port "sum" streamInlet)) = FitsNamesDiffer [ "product", "x"; "c", "y" ]

// ---------------------------------------------------------------------------
// BD2 — fields picked, renamed and regrouped: the channels crossed, and a
// stream split into its channels and joined again.

// THE DRAWINGS
let crossed: Graph =
    { emptyGraph "Crossed" rate stereoPins.fields with
        boxes = [ box "cross" fieldsBox [ "pick", "left = right, right = left" ] ]
        edges =
            [ wire (port "input" "in") (port "cross" streamInlet)
              wire (port "cross" streamOutlet) (port "output" "out") ] }

let splitAndJoined: Graph =
    { emptyGraph "SplitAndJoined" rate stereoPins.fields with
        boxes =
            [ box "leftOnly" fieldsBox [ "pick", "left" ]
              box "rightOnly" fieldsBox [ "pick", "right" ]
              box "both" joinBox [] ]
        edges =
            [ wire (port "input" "in") (port "leftOnly" streamInlet)
              wire (port "input" "in") (port "rightOnly" streamInlet)
              wire (port "leftOnly" streamOutlet) (port "both" "in0")
              wire (port "rightOnly" streamOutlet) (port "both" "in1")
              wire (port "both" streamOutlet) (port "output" "out") ] }

// THE PIPELINES
let crossedPipeline =
    defModule "Crossed" stereoIo (fun (inPorts, outPorts) ->
        streamSource inPorts |> streamMapTo stereoPins (fun (left, right) -> right, left) |> streamSink outPorts)

let splitAndJoinedPipeline =
    let sample = signedInt sampleWidth

    defModule "SplitAndJoined" stereoIo (fun (inPorts, outPorts) ->
        let branches = streamSource inPorts |> streamForkEager 2
        let left = branches[0] |> streamMapTo (pins1 ("left", sample)) (fun (left, _) -> left)
        let right = branches[1] |> streamMapTo (pins1 ("right", sample)) (fun (_, right) -> right)
        streamJoin stereoPins [ left; right ] |> streamSink outPorts)

// CHECK
let fieldsAreRegrouped () : bool =
    let swapped = through (elaborate crossed).def 3 beats
    let rejoined = through (elaborate splitAndJoined).def 3 beats

    same crossed crossedPipeline
    && same splitAndJoined splitAndJoinedPipeline
    && swapped = (beats |> List.map List.rev)
    && rejoined = beats
    // a field that is not there; a name picked twice; nothing picked
    && refuses "picks 'centre'" { crossed with boxes = [ box "cross" fieldsBox [ "pick", "left = centre, right" ] ] }
    && refuses "'left' is picked twice" { crossed with boxes = [ box "cross" fieldsBox [ "pick", "left, left = right" ] ] }
    // a side of a join with two fields; two sides with one name
    && refuses "a side of a join carries one field"
        { splitAndJoined with boxes = [ box "leftOnly" fieldsBox [ "pick", "left, right" ]; box "rightOnly" fieldsBox [ "pick", "right" ]; box "both" joinBox [] ] }
    && refuses "two of its sides carry a field called 'left'"
        { splitAndJoined with boxes = [ box "leftOnly" fieldsBox [ "pick", "left" ]; box "rightOnly" fieldsBox [ "pick", "left" ]; box "both" joinBox [] ] }

// ---------------------------------------------------------------------------
// BD3 — a buffer is a box: its depth the drawer's, its fields whatever is
// connected.

// THE DRAWING
let buffered (depth: int) : Graph =
    { emptyGraph $"Buffered%d{depth}" rate stereoPins.fields with
        boxes = [ box "buffer" fifoBox [ "depth", string depth ] ]
        edges =
            [ wire (port "input" "in") (port "buffer" streamInlet)
              wire (port "buffer" streamOutlet) (port "output" "out") ] }

// THE PIPELINE
let bufferedPipeline (depth: int) =
    defModule $"Buffered%d{depth}" stereoIo (fun (inPorts, outPorts) -> streamSource inPorts |> streamFifo "buffer" depth |> streamSink outPorts)

// CHECK — in order under stalls, and it holds what it says: with nobody
// taking, `depth` beats go in and no more.
let bufferIsABox () : bool =
    let held (depth: int) =
        let sim = Sim (elaborate (buffered depth)).def
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 0UL)
        let mutable accepted = 0

        for _ in 1..50 do
            if sim.Peek "in_ready" = 1UL then
                accepted <- accepted + 1

            sim.Tick()

        accepted

    [ 2; 4; 16 ] |> List.forall (fun depth -> same (buffered depth) (bufferedPipeline depth))
    && through (elaborate (buffered 4)).def 3 beats = beats
    && held 4 = 4
    && held 16 = 16
    && refuses "depth is a count" { buffered 4 with boxes = [ box "buffer" fifoBox [ "depth", "none" ] ] }

// ---------------------------------------------------------------------------
// BD4 — the boxes are placed, set and saved as any box is.

let boxesAreEdits () : bool =
    let step (change: Graph -> Result<Graph, string>) (g: Graph) = change g |> Result.defaultWith failwith

    let drawn =
        emptyGraph "Crossed" rate stereoPins.fields
        |> step (addBox fieldsBox (0.0, 0.0) >> Result.map fst)
        |> step (renameBox fieldsBox "cross")
        |> step (setArgument "cross" "pick" "left = right, right = left")
        |> step (addEdge (port "input" "in", Out) (port "cross" streamInlet, In))
        |> step (addEdge (port "cross" streamOutlet, Out) (port "output" "out", In))

    let refuses (change: Graph -> Result<Graph, string>) (expecting: string) =
        match change drawn with
        | Ok _ -> false
        | Error why -> why.Contains expecting

    { drawn with positions = Map.empty } = crossed
    && [ macDrawn; crossed; splitAndJoined; buffered 4 ] |> List.forall (fun g -> DesignFile.parse (DesignFile.write g) = Ok g)
    && refuses (setArgument "cross" "pick" "left = ") "is not a field"
    && refuses (setArgument "cross" "depth" "4") "has no 'depth'"
    // What it carries follows what is connected: nothing, until something is.
    && (streamsOf (emptyGraph "Empty" rate stereoPins.fields |> step (addBox fifoBox (0.0, 0.0) >> Result.map fst)) fifoBox |> snd |> List.map (fun s -> s.fields)) = [ [] ]
