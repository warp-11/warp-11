/// USE CASES — the judge of `Fu.fs`.
///
/// One module, `mac`: M streams of three numbers in, `a * b + c` out on each.
/// The multiply and the add are units. `mac` takes three integers — how many
/// streams, and how many copies of each unit it may spend — and its body does
/// not change with them. What the stdlib does is a function of M (the streams
/// that name a unit) against N (the unit's copies), and the unit's kind:
///
///                    combinational unit          sequential unit
///     N = M          M copies, in place:         M units, one each,
///                    no nets, no cycles          no arbiter
///     N < M          M clients on N units,       the same
///                    arbitrated, routed back
///     N > M          ELABORATION ERROR —         a farm: N units over
///                    copies nobody can use       M streams
///
/// The first row at M = N = 1 is `a * b + c ==> out` with the handshake wired
/// straight through, and nothing else — byte-identical to writing it by hand.
///
/// Everything a placement costs — arbitration, dispatch, merge, the beat held
/// while its work is out — is the stdlib's. `mac` names its units and says
/// how many; it never mentions a handshake it did not receive from its
/// caller.
///
/// The design as data — what a GUI would hold — is `PlacementData.fs`, a
/// draft that is not compiled yet.
module Warp11.Placement.Placement

open Warp11
open Warp11.Placement.Fu
open Warp11.Placement.Units

// ---------------------------------------------------------------------------
// The units, and the one module.

// USE CASE — declaring a unit: the law, once, over typed pins. The product's
// format is derived from the operands', not typed.
let multiply16 =
    fu
        "mul16"
        (pins2 ("a", uint 16) ("b", uint 16))
        (pins1 ("product", Warp11.Number.productFormat (uint 16) (uint 16)))
        (fun (a, b) -> mul a b)

let add32 = fu "add32" (pins2 ("x", uint 32) ("y", uint 32)) (pins1 ("sum", uint 33)) (fun (x, y) -> add (pad 33 x) (pad 33 y))

// USE CASE — a unit that costs cycles. Sixteen a product, one at a time, so
// copying it is the only way to keep up with a rate.
let shiftAddMultiply16 =
    fuSequential "smul16" (pins2 ("a", uint 16) ("b", uint 16)) (pins1 ("product", uint 32)) (fun instance -> shiftAddMultiplier instance 16)

// helper — the payload shapes the beat takes on its way through.
let macIn = pins3 ("a", uint 16) ("b", uint 16) ("c", uint 32)
let productPins = pins2 ("product", uint 32) ("c", uint 32)
let macOut = pins1 ("out", uint 33)
// A beat field is named for the pin that produced it — the rule a graph
// follows — so the last stage carries `sum`, and the port it lands on is `out`.
let sumPins = pins1 ("sum", uint 33)

// USE CASE — THE module. Three integers and a unit in its signature; none
// of them in its body. The body is two stages over M streams.
let macWith (multiplier: Fu<Expr * Expr, Expr>) (streams: int) (multipliers: int) (adders: int) =
    let multiply = multiplier |> copies multipliers
    let accumulate = add32 |> copies adders

    defModule
        $"Mac%d{streams}s%d{multipliers}x%d{adders}"
        (fun p ->
            [ for i in 1..streams -> streamInputPorts p $"in%d{i}" (lower macIn) ],
            [ for i in 1..streams -> streamOutputPorts p $"out%d{i}" (lower macOut) ])
        (fun (ins, outs) ->
            ins
            |> List.map streamSource
            |> fuStages multiply "product" productPins (fun (a, b, _) -> a, b) (fun ab (_, _, c) -> ab, c)
            |> fuStages accumulate "sum" sumPins (fun (ab, c) -> ab, c) (fun s _ -> s)
            |> List.iter2 streamSink outs)

// USE CASE — the everyday spelling: the DSP multiply.
let mac = macWith multiply16

// ---------------------------------------------------------------------------
// UC1 — N = M, combinational: the expression, M times.
//
// `mac 1 1 1` emits the same Verilog as the body written with no stage
// vocabulary at all. `mac 3 3 3` emits three of them. Not "equivalent" —
// byte-identical. Two stages, two units, M streams, and no net left behind.

// helper — the comparator: what a person writes by hand, for M streams.
let macByHand (streams: int) =
    defModule
        $"Mac%d{streams}s%d{streams}x%d{streams}"
        (fun p ->
            [ for i in 1..streams -> streamInputPorts p $"in%d{i}" (lower macIn) ],
            [ for i in 1..streams -> streamOutputPorts p $"out%d{i}" (lower macOut) ])
        (fun (ins, outs) ->
            for inPorts, outPorts in List.zip ins outs do
                let a, b, c = inPorts.payload
                add (pad 33 (mul a b)) (pad 33 c) ==> List.head outPorts.targets
                inPorts.valid ==> outPorts.valid
                outPorts.ready ==> inPorts.ready)

// CHECK
let inPlaceIsTheExpression () : bool =
    emitDesign (mac 1 1 1).def = emitDesign (macByHand 1).def
    && emitDesign (mac 3 3 3).def = emitDesign (macByHand 3).def

// ---------------------------------------------------------------------------
// UC2 — N < M: sharing, and the streams cannot tell.
//
// `mac 5 1 1` runs five streams through one multiplier and one adder. Each
// stream gets the same bits it would have alone, in order, stalled or not.
// The Verilog has one `*` where `mac 5 5 5` has five, and the arbitration
// that `mac 5 5 5` had no need of.

// helper — run the same beats through every stream of a design AT ONCE, so
// a shared unit is asked by all of them in the same cycles. Stalls are
// staggered per stream, so no two streams pause together. Ends when every
// stream has yielded `beats.Length` results, or after `idleLimit` cycles
// with nothing arriving anywhere.
let private macThrough (design: TypedModule<_>) (streams: int) (stallEvery: int) (beats: uint64 list list) =
    let sim = Sim design.def
    let inputs = [| for i in 1..streams -> streamPins $"in%d{i}" (lower macIn) |]
    let outputs = [| for i in 1..streams -> streamPins $"out%d{i}" (lower macOut) |]
    let pending = Array.create streams beats
    let got = Array.init streams (fun _ -> ResizeArray<uint64 list>())
    let wanted = beats.Length
    let idleLimit = 10_000
    let mutable cycle = 0
    let mutable idle = 0

    while idle < idleLimit && got |> Array.exists (fun g -> g.Count < wanted) do
        let stalling i = stallEvery > 0 && (cycle + i) % stallEvery = 0
        let offering i = not (List.isEmpty pending[i]) && not (stalling i)

        // Every stream's decision on the wires first, then every handshake
        // read — an arbiter's grant depends on who else is asking.
        for i in 0 .. streams - 1 do
            sim.Poke(inputs[i].valid, (if offering i then 1UL else 0UL))
            sim.Poke(outputs[i].ready, (if stalling i then 0UL else 1UL))

            if offering i then
                List.iter2 (fun name value -> sim.Poke(name, value)) inputs[i].fields (List.head pending[i])

        let accepted = [| for i in 0 .. streams - 1 -> offering i && sim.Peek inputs[i].ready = 1UL |]

        let arrivals =
            [| for i in 0 .. streams - 1 ->
                   if not (stalling i) && sim.Peek outputs[i].valid = 1UL then
                       Some(outputs[i].fields |> List.map sim.Peek)
                   else
                       None |]

        sim.Tick()
        cycle <- cycle + 1

        for i in 0 .. streams - 1 do
            if accepted[i] then
                pending[i] <- List.tail pending[i]

        if arrivals |> Array.forall Option.isNone then
            idle <- idle + 1
        else
            idle <- 0

            for i in 0 .. streams - 1 do
                arrivals[i] |> Option.iter got[i].Add

    [ for g in got -> List.ofSeq g ], cycle

// helper — 32 distinct beats, so a beat out of order is a wrong answer.
let private macBeats = [ for i in 0..31 -> [ uint64 (i * 1000 + 3); uint64 (i * 7 + 1); uint64 (i * 100_000) ] ]
let private macExpected = [ for i in 0..31 -> [ uint64 ((i * 1000 + 3) * (i * 7 + 1) + i * 100_000) ] ]

// helper — count a unit's copies in an emitted design.
let private verilog (d: TypedModule<_>) = emitDesign d.def
let private has (needle: string) (text: string) = text.Contains needle
let private count (needle: string) (text: string) = text.Split('\n') |> Array.filter (fun l -> l.Contains needle) |> Array.length
// A declared register whose name ends in `suffix` — counting declarations
// rather than mentions, since an assign that reads the register mentions it
// too (Q4: an Inventory count would be the honest form).
let private declaredRegs (suffix: string) (text: string) =
    text.Split('\n') |> Array.filter (fun l -> l.TrimStart().StartsWith "reg " && l.TrimEnd().EndsWith(suffix + ";")) |> Array.length
let private everyStream streams design stall = fst (macThrough design streams stall macBeats) = List.replicate streams macExpected

// CHECK
let sharingIsOneUnit () : bool =
    everyStream 5 (mac 5 1 1) 0
    && everyStream 5 (mac 5 1 1) 3
    && count " * " (verilog (mac 5 1 1)) = 1
    && count " * " (verilog (mac 5 5 5)) = 5
    && (verilog (mac 5 1 1) |> has "grant")
    && not (verilog (mac 5 5 5) |> has "grant")

// ---------------------------------------------------------------------------
// UC3 — N > M, combinational: an elaboration error.
//
// Five multipliers for one stream is five copies of a zero-cycle operation
// behind a dispatch — the throughput of one, the area of five. Nobody means
// it, so it does not elaborate, and the error names the unit and both
// numbers.

// CHECK
let surplusCombinationalCopiesRefuse () : bool =
    let refuses (build: unit -> TypedModule<_>) (names: string list) =
        try
            (build ()).def |> ignore
            false
        with e ->
            names |> List.forall (fun n -> e.Message.Contains n)

    refuses (fun () -> mac 1 5 1) [ "mul16"; "5 copies"; "1 stream" ]
    && refuses (fun () -> mac 1 1 5) [ "add32"; "5 copies"; "1 stream" ]
    && refuses (fun () -> mac 2 5 2) [ "mul16"; "5 copies"; "2 stream" ]

// ---------------------------------------------------------------------------
// UC4 — N > M, sequential: the farm, and why copies exist.
//
// The same body over the shift-add multiplier. One stream, one copy: sixteen
// cycles a beat. One stream, five copies: five in flight, the beats back in
// order, and the whole run in about a fifth of the cycles. The body did not
// change; the unit's kind and count did.

// helper — cycles for a run to complete, counted by the driver (Q5: `Sim`
// keeps no tick count, and it turns out not to need one).
let private cyclesFor (design: TypedModule<_>) (streams: int) (beats: uint64 list list) : int =
    snd (macThrough design streams 0 beats)

/// The numbers behind UC4, for the eye: cycles for 32 beats at one copy and
/// at five, and what a copy costs the Verilog in registers.
let throughputReport () =
    let slow = macWith shiftAddMultiply16
    let regs (d: TypedModule<_>) = count "reg " (verilog d)
    let one = cyclesFor (slow 1 1 1) 1 macBeats
    let five = cyclesFor (slow 1 5 1) 1 macBeats
    let got design stall = fst (macThrough design 1 stall macBeats) |> List.head
    let expect = macExpected
    let firstBad (xs: uint64 list list) = xs |> List.indexed |> List.tryFind (fun (i, x) -> i >= expect.Length || x <> expect[i])

    $"32 beats: one copy %d{one} cycles (%d{regs (slow 1 1 1)} regs), five copies %d{five} cycles (%d{regs (slow 1 5 1)} regs), in-place mac 1 1 1 %d{cyclesFor (mac 1 1 1) 1 macBeats} cycles\n"
    + $"  one copy, no stalls: %A{firstBad (got (slow 1 1 1) 0)} (got %d{(got (slow 1 1 1) 0).Length})\n"
    + $"  five copies, no stalls: %A{firstBad (got (slow 1 5 1) 0)} (got %d{(got (slow 1 5 1) 0).Length})\n"
    + $"  five copies, stall 3: %A{firstBad (got (slow 1 5 1) 3)} (got %d{(got (slow 1 5 1) 3).Length})\n"
    + (let accOne, accFive = declaredRegs "_acc" (verilog (slow 1 1 1)), declaredRegs "_acc" (verilog (slow 1 5 1))
       $"  acc regs: one %d{accOne}, five %d{accFive}")

// CHECK
let copiesBuyThroughput () : bool =
    let slow = macWith shiftAddMultiply16
    let one = cyclesFor (slow 1 1 1) 1 macBeats
    let five = cyclesFor (slow 1 5 1) 1 macBeats

    everyStream 1 (slow 1 1 1) 0
    && everyStream 1 (slow 1 5 1) 0
    && everyStream 1 (slow 1 5 1) 3
    && declaredRegs "_acc" (verilog (slow 1 1 1)) = 1
    && declaredRegs "_acc" (verilog (slow 1 5 1)) = 5
    && five * 4 < one

// ---------------------------------------------------------------------------
// UC5 — The counts are independent, and mixed rows are not special.
//
// `mac 5 5 1`: every stream its own multiplier in place, all five sharing one
// adder. `mac 5 1 5`: the reverse. Each stage's placement is its own unit's
// row in the matrix.

// CHECK
let countsAreIndependent () : bool =
    everyStream 5 (mac 5 5 1) 0
    && everyStream 5 (mac 5 1 5) 0
    && count " * " (verilog (mac 5 5 1)) = 5
    && count " * " (verilog (mac 5 1 5)) = 1

// ---------------------------------------------------------------------------
// UC6 — A parameter that is not in the beat.
//
// The same two stages, but `c` is a port the host writes rather than a field
// of the beat. A unit's operand can come from anywhere the stage can see; the
// row of the matrix does not care where. Each beat sees the `c` that was
// there when it was accepted, in place or shared.

// USE CASE — one operand captured from a port.
let macWithOffset (streams: int) (multipliers: int) (adders: int) =
    let multiply = multiply16 |> copies multipliers
    let accumulate = add32 |> copies adders
    let pairIn = pins2 ("a", uint 16) ("b", uint 16)
    let productOnly = pins1 ("product", uint 32)

    defModule
        $"MacOffset%d{streams}s%d{multipliers}x%d{adders}"
        (fun p ->
            [ for i in 1..streams -> streamInputPorts p $"in%d{i}" (lower pairIn) ],
            [ for i in 1..streams -> streamOutputPorts p $"out%d{i}" (lower macOut) ],
            p.inPort "c" 32)
        (fun (ins, outs, c) ->
            ins
            |> List.map streamSource
            |> fuStages multiply "product" productOnly (fun (a, b) -> a, b) (fun ab _ -> ab)
            |> fuStages accumulate "sum" sumPins (fun ab -> ab, c) (fun s _ -> s)
            |> List.iter2 streamSink outs)

// CHECK
let parameterTracks () : bool =
    let run streams multipliers adders =
        let sim = Sim (macWithOffset streams multipliers adders).def
        let pairIn = pins2 ("a", uint 16) ("b", uint 16)
        let one beat = streamThrough sim (streamPins "in1" (lower pairIn)) (streamPins "out1" (lower macOut)) [ beat ] |> Seq.head
        sim.Poke("c", 10UL)
        let first = one [ 6UL; 7UL ]
        sim.Poke("c", 20UL)
        let second = one [ 6UL; 7UL ]
        first = [ 52UL ] && second = [ 62UL ]

    run 1 1 1 && run 5 1 1

// ---------------------------------------------------------------------------
// UC7 — A module as a box: `audioGain` between two stream ports, its two
// controls from the design's own ports. The typed form; the graph form is
// `Graph.gainGraph`, and the two must meet at the bytes.

let stereoPins = pins2 ("left", sint sampleWidth) ("right", sint sampleWidth)

// USE CASE — a stream through a module, controls beside it.
let gainPatch =
    defModule
        "GainPatch"
        (fun p ->
            streamInputPorts p "in1" (lower stereoPins),
            streamOutputPorts p "out1" (lower stereoPins),
            p.inPort "volume" 16,
            p.inPort "mute" 1)
        (fun (inPorts, outPorts, volume, mute) ->
            [ streamSource inPorts ]
            |> fuStagesWith [ volume; mute ] gainModule "gain" stereoPins (fun (l, r) -> l, r) (fun (l, r) _ -> l, r)
            |> List.iter2 streamSink [ outPorts ])

// CHECK
let gainScales () : bool =
    let sim = Sim gainPatch.def
    let one beat = streamThrough sim (streamPins "in1" (lower stereoPins)) (streamPins "out1" (lower stereoPins)) [ beat ] |> Seq.head
    sim.Poke("volume", 2UL * gainUnity)
    sim.Poke("mute", 0UL)
    let doubled = one [ 1000UL; 2000UL ]
    sim.Poke("volume", gainUnity)
    let unity = one [ 1000UL; 2000UL ]
    sim.Poke("mute", 1UL)
    let muted = one [ 1000UL; 2000UL ]
    doubled = [ 2000UL; 4000UL ] && unity = [ 1000UL; 2000UL ] && muted = [ 0UL; 0UL ]

// ---------------------------------------------------------------------------
// What this surface deliberately does NOT do, and why.
//
// 1. An expression-shaped caller — `mul a b ==> c`, with `c` read this
//    cycle — has no row in the matrix. The stage form is the placeable one;
//    UC1 is proof it costs nothing when there is nothing to hold for.
//
// 2. M is the streams handed to ONE `fuStages` call. The folded compressor's
//    case — five stages at five different points of one chain, sharing a
//    multiplier — is the same row (N < M) reached differently: the M sites
//    are scattered through a pipeline, and the count is known only when the
//    module seals. That is the next use case, not this one. It cannot reach
//    the in-place row byte-identically (a site elaborated before M is known
//    has already declared its client wires), and does not need to: scattered
//    sites are sharing by definition.
//
// 3. No latency is declared anywhere. A sequential unit's depth is between
//    it and its tag chain.
//
// Open questions, still:
//
// Q2. N < M with N > 1 — `mac 5 2 1`, five streams on two multipliers.
//     `warpFu` has one core; this row needs K cores behind one arbiter, or
//     two pods and a split. The surface should not preclude it.
//
// Q3. DONE for this row: `orderedFarm` in `Fu.fs` — round-robin dispatch
//     and merge in lockstep, a slow lane stalls the merge rather than being
//     overtaken. A new topology beside `farmWith`, not a change to it:
//     `farmWith` trades order for throughput on purpose.
//
// Q4. UC2/UC3/UC5 count substrings in the Verilog. Brittle. Is there an
//     `Inventory`-side count (copies of a unit, registers) that should exist
//     instead — the same number the area gate would want?
//
// Q5. DONE: the driver counts its own cycles; `Sim` needs nothing.
//
// Q6. DONE here: `macThrough` offers on all M inputs in the same cycles,
//     stalls staggered. If it stays useful it belongs beside
//     `streamThroughWith` in `SimStream.fs`.
