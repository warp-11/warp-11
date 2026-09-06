/// The tutorial's designs.
///
/// These are written to be *read*. Where the catalog next door wants coverage
/// of every IR node and will happily contort a design to reach one, everything
/// here is chosen so that a page can explain it in one sitting: the smallest
/// design that carries one idea, no second idea riding along, and no port that
/// exists only to make an oracle happy.
///
/// That is why they are duplicated rather than shared. Two artifacts with
/// different jobs were compromising each other, and eight tiny designs is a
/// cheap price for letting each be good at its own.
///
/// Every one is differentially tested against Verilator by
/// `Warp11.Tutorial.Checks` — a page that teaches something the silicon does not
/// do would be worse than no page.
[<AutoOpen>]
module Warp11.Tutorial.Designs

open Warp11
open Warp11.NumberOperators

/// A register that counts while `enable` is high and clears when `clear` is.
/// The first design: ports, a register, and statements that drive them.
let counter =
    defModule
        "Counter"
        (fun p -> (p.inPort "enable" 1, p.inPort "clear" 1, p.outPort "count" 64))
        (fun (enable, clear, count) ->
            let r = reg "r" 64

            ifElse [
                (clear, fun () -> 0UL ==> r)
                (otherwise, fun () -> If enable (fun () -> r + 1UL ==> r)) ]

            r ==> count)

/// Unsigned compare at ports: three one-bit verdicts and the larger operand.
/// `less`/`equal`/`greater` rather than `lt`/`eq`/`gt`, because those are the
/// operators' own names and a port may not shadow one.
let comparator =
    defModule
        "Comparator"
        (fun p ->
            (p.inPort "a" 8,
             p.inPort "b" 8,
             p.outPort "less" 1,
             p.outPort "equal" 1,
             p.outPort "greater" 1,
             p.outPort "larger" 8))
        (fun (a, b, less, equal, greater, larger) ->
            lt a b ==> less
            eq a b ==> equal
            lt b a ==> greater
            mux (lt a b) b a ==> larger)

/// A defaulted wire under two sibling `If` blocks. The later statement ends up
/// outermost in the folded mux tree, so `sel1` outranks `sel0` — priority is
/// the order you wrote them in.
let priorityMux =
    defModule
        "PriorityMux"
        (fun p ->
            (p.inPort "sel0" 1,
             p.inPort "sel1" 1,
             p.inPort "a" 8,
             p.inPort "b" 8,
             p.inPort "c" 8,
             p.outPort "out" 8))
        (fun (sel0, sel1, a, b, c, out) ->
            a ==> out
            If sel0 (fun () -> b ==> out)
            If sel1 (fun () -> c ==> out))

/// `(a * b) + (satInc c * d)`, built from three stdlib entries. Each call
/// plants its own hardware, so this design contains two multipliers.
let dotProduct =
    defModule
        "DotProduct"
        (fun p ->
            (p.inPort "a" 8,
             p.inPort "b" 8,
             p.inPort "c" 8,
             p.inPort "d" 8,
             p.outPort "out" 16))
        (fun (a, b, c, d, out) ->
            let multiply = mulOf 8
            let accumulate = adderOf 16
            let bump = satIncOf 8

            accumulate (multiply a b) (multiply (bump c) d) ==> out)

/// A module of your own, defined once and instantiated twice.
///
/// `SatAcc8` is the full `defModule` route — an IO bundle, and one body that
/// is ordinary design code over those wires. `Min8`
/// is the light route: a pure function
/// wrapped by `fnModule2`, made callable by `liftBinary`. The design holds
/// three `SatAcc8` instances — two named, one via the `satAccOf` wrapper — so
/// the one-definition-many-instances shape is visible
/// in the emitted Verilog, and its outputs are named `total_left`, not
/// `left_total` — an instance's staging wires are `{instance}_{port}` in the
/// parent's namespace, so `left_total` is already taken by the instance
/// called `left`.
type SatAccIo =
    { add: Input
      en: Input
      total: Output }

let satAcc =
    defModule
        "SatAcc8"
        (fun p ->
            { add = p.inPort "add" 8
              en = p.inPort "en" 1
              total = p.outPort "total" 8 })
        (fun io ->
            let r = reg "r" 8
            let sum = wire "sum" 9
            pad 9 r + pad 9 io.add ==> sum

            let next = wire "next" 8
            mux (slice 8 8 sum) (lit 0xFFUL 8) (slice 7 0 sum) ==> next

            If io.en (fun () -> next ==> r)
            r ==> io.total)

/// The call shape, as an ordinary function beside the module — one auto-named
/// instance per call, wired and done.
let satAccOf (add: Expr) (en: Expr) =
    let c = satAcc.New
    add ==> c.add
    en ==> c.en
    c.total

let minOf8 =
    fnModule2 "Min8" ("a", 8) ("b", 8) "m" (fun a b -> mux (lt a b) a b)
    |> liftBinary

let ownModules =
    defModule
        "OwnModules"
        (fun p ->
            (p.inPort "add_left" 8,
             p.inPort "add_right" 8,
             p.inPort "en" 1,
             p.outPort "total_left" 8,
             p.outPort "total_right" 8,
             p.outPort "total_both" 8,
             p.outPort "lowest" 8))
        (fun (addLeft, addRight, en, totalLeftOut, totalRightOut, totalBothOut, lowest) ->
            // The named instances: the bundle over `left_*`/`right_*` staging
            // wires, wired where it is used.
            let totalLeft =
                let c = satAcc.NewNamed "left"
                addLeft ==> c.add
                en ==> c.en
                c.total

            let totalRight =
                let c = satAcc.NewNamed "right"
                addRight ==> c.add
                en ==> c.en
                c.total

            // The third accumulator does not care what its instance is called —
            // `satAccOf` is the function feel, one auto-named copy per call.
            let totalBoth = satAccOf (addLeft + addRight) en

            totalLeft ==> totalLeftOut
            totalRight ==> totalRightOut
            totalBoth ==> totalBothOut
            minOf8 totalLeft totalRight ==> lowest)

/// The bit utilities in one place: join, fill, reverse, count, and the one-hot
/// round trip out to four grants and back to an index.
let bitShapes =
    defModule
        "BitShapes"
        (fun p ->
            (p.inPort "a" 4,
             p.inPort "b" 4,
             p.inPort "flag" 1,
             p.inPort "index" 2,
             p.outPort "joined" 8,
             p.outPort "mask" 4,
             p.outPort "flipped" 4,
             p.outPort "ones" 3,
             [ for i in 0..3 -> p.outPort $"hot{i}" 1 ],
             p.outPort "recovered" 2))
        (fun (a, b, flag, index, joined, mask, flipped, ones, hotOuts, recovered) ->
            catAll [ a; b ] ==> joined

            fill 4 flag ==> mask

            reverse a ==> flipped

            popCount a ==> ones

            let hot = uintToOneHot 4 index

            for i in 0..3 do
                hot[i] ==> hotOuts[i]

            oneHotToUInt hot ==> recovered)

/// The same bits read two ways. `diff` needs no signed form because two's
/// complement makes one subtractor correct for both readings; compare,
/// multiply and right-shift are three of the six that genuinely differ.
let signedOps =
    defModule
        "SignedOps"
        (fun p ->
            (p.inPort "a" 8,
             p.inPort "b" 8,
             p.outPort "diff" 8,
             // Signed, because a signed multiply is what drives it. The bits are the
             // same either way; the declaration is what lets the debugger show −100
             // rather than 65436, and it is the lesson of this design at its own port.
             p.outPortAs "product" (SInt 16),
             p.outPort "below" 1,
             p.outPort "below_signed" 1,
             p.outPort "shifted" 8))
        (fun (a, b, diff, product, below, belowSigned, shifted) ->
            a - b ==> diff
            // The same eight bits, read two ways. `a` and `b` are declared unsigned,
            // so `lt` compares them that way; `asSInt` says to read them as two's
            // complement, and the same `lt` and `mul` then do the signed thing.
            mul (asSInt a) (asSInt b) ==> product
            lt a b ==> below
            lt (asSInt a) (asSInt b) ==> belowSigned
            sra 3 a ==> shifted)

/// Eight words of eight bits, one write port and two read ports — one
/// synchronous, one combinational. Reading both at the same address is the
/// whole point: they differ by exactly one cycle.
let ram =
    defModule
        "Ram"
        (fun p ->
            (p.inPort "waddr" 3,
             p.inPort "wdata" 8,
             p.inPort "wen" 1,
             p.inPort "raddr" 3,
             p.outPort "next_cycle_out" 8,
             p.outPort "this_cycle_out" 8))
        (fun (waddr, wdata, wen, raddr, nextCycleOut, thisCycleOut) ->
            let store = distributedMem "store" 3 8
            If wen (fun () -> memWrite store waddr wdata (lit 1UL 1))
            (memReadPort store raddr).data ==> nextCycleOut
            memRead store raddr ==> thisCycleOut)

type private Stage =
    | Idle
    | Fetch
    | Decode
    | Execute
    | Writeback
    | Done

/// Six named states walking a four-pass loop. `stall` holds `Execute` by taking
/// no transition at all, which is what waiting is in hardware.
let fsm =
    defModule
        "FSM"
        (fun p ->
            (p.inPort "start" 1,
             p.inPort "stall" 1,
             p.outPort "busy" 1,
             p.outPort "finished" 1,
             p.outPort "retired" 8))
        (fun (start, stall, busy, finished, retired) ->
            let stage = machine "stage" [ Idle; Fetch; Decode; Execute; Writeback; Done ]
            let count = reg "count" 8

            bnot (stage.Is Idle ||| stage.Is Done) ==> busy
            stage.Is Done ==> finished
            count ==> retired

            let begin' () =
                If start (fun () ->
                    lit 0UL 8 ==> count
                    stage.Goto Fetch)

            stage.Switch
                [ Idle, begin'
                  Done, begin'
                  Fetch, fun () -> stage.Goto Decode
                  Decode, fun () -> stage.Goto Execute
                  Execute, fun () -> If (bnot stall) (fun () -> stage.Goto Writeback)
                  Writeback,
                  fun () ->
                      count + lit 1UL 8 ==> count

                      ifElse [
                          (eq count (lit 3UL 8), fun () -> stage.Goto Done)
                          (otherwise, fun () -> stage.Goto Fetch) ] ])

/// A Q format is one line: a total width, a count of fraction bits, and a
/// measure binding the two so the type system can carry it. Q5.3 is the same
/// eight bits as `q4_4` with the point moved.
let private q5_3 = Number.signedFixed 8 3

/// Fixed-point arithmetic where the Q format is part of the type. A multiply
/// changes format — widths add and fraction bits add — and the renormalization
/// back is a slice the target format names.
let fixedPoint =
    defModule
        "FixedPoint"
        (fun p ->
            (Number.inPort p "a" Number.q4_4,
             Number.inPort p "b" Number.q4_4,
             // The format says signed, so the port does too — which is how the
             // debugger knows to read −48 rather than 208.
             p.outPortAs "product" (Number.groundType Number.q4_4),
             p.outPortAs "doubled" (Number.groundType q5_3),
             p.outPort "below" 1))
        (fun (a, b, product, doubled, below) ->
            // Q4.4 * Q4.4 is Q8.8: sixteen bits, eight of them fractional.
            let wide = Number.wire "wide" (a * b)

            (Number.renormTo Number.q4_4 wide) ==> product

            // The same eight bits read as Q5.3 mean twice as much. No gates.
            (Number.reinterpret q5_3 a) ==> doubled

            Number.lessThan a b ==> below)

/// Two read-only tables. Contents are fixed at elaboration and become a Verilog
/// `initial` block, which Vivado turns into a memory the bitstream arrives
/// pre-loaded with.
let romTable =
    defModule
        "RomTable"
        (fun p -> (p.inPort "index" 3, p.outPort "square" 8, p.outPort "prime" 8))
        (fun (index, square, prime) ->
            let squares = distributedRom "squares" 8 [| 0UL; 1UL; 4UL; 9UL; 16UL; 25UL; 36UL; 49UL |]
            memRead squares index ==> square

            // Five values in a table that has to be a power of two deep: the
            // remaining three addresses read zero.
            let primes = distributedRom "primes" 8 [| 2UL; 3UL; 5UL; 7UL; 11UL |]
            memRead primes index ==> prime)

/// A claim the design makes about itself, checked every cycle. The counter
/// walks 0 to 4 and wraps, so the top three of its eight reachable values are
/// unreachable — and it says so.
let assertions =
    defModule
        "Assertions"
        (fun p -> (p.inPort "step" 1, p.outPort "phase" 3, p.outPort "wrapped" 1))
        (fun (step, phase, wrapped) ->
            let r = reg "r" 3

            If step (fun () ->
                ifElse [
                    (eq r (lit 4UL 3), fun () -> lit 0UL 3 ==> r)
                    (otherwise, fun () -> r + lit 1UL 3 ==> r) ])

            assertThat (bnot (lt (lit 4UL 3) r)) "phase left its range"

            r ==> phase
            eq r (lit 4UL 3) ==> wrapped)

// ---------------------------------------------------------------------------
// Streams: the ready/valid layer. One payload shape for all of them, so the
// pages differ only in the topology they build.

let private beatLayout = layout1 ("value", 8)

/// A beat that knows which one it is. Anything that can reorder needs this.
let private tagged = layout2 ("id", 8) ("value", 8)

let private bump (v: Expr) = v + lit 1UL 8

/// The smallest handshake there is: a source, a combinational transform, a
/// sink. `map` costs nothing — ready and valid pass straight through — so this
/// whole design is wires.
let streamPipe =
    defModule
        "StreamPipe"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) -> streamSource inPorts |> Stream.map bump |> streamSink outPorts)

/// Three registered stages. Each buys a cycle of latency and a place for a
/// beat to wait, which is what makes the chain elastic under backpressure.
let streamStages =
    defModule
        "StreamStages"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) -> streamSource inPorts |> Stream.stages 3 bump |> streamSink outPorts)

/// The worker's port bundle: one stream in, one stream out. Each `StreamPorts`
/// group is a data word, its valid, and the ready that answers it — which side
/// of the handshake each wire is on is decided by the declaring helper, so the
/// record reads the same at definition and over an instance's staging wires.
type SlowWorkerIo =
    { input: StreamPorts
      output: StreamPorts }

/// The calculation, with no streams in sight — the recursion
///
///     let rec delay n out =
///         if n = 0 then out
///         else delay (n - 1) out
///
/// applied as `delay cycles (bump beat)`, taken apart per `Iteration`: the
/// arguments `(n, out)` are the state, each recursive call is one cycle's
/// `step`, `n = 0` is the base case, and `out` is what it returns.
let private grind cycles : Iteration<Expr, Expr * Expr, Expr> =
    { state = layout2 ("remaining", 8) ("value", 8)
      init = fun beat -> lit (uint64 cycles) 8, bump beat
      step = fun (n, out) -> n - lit 1UL 8, out
      finished = fun (n, _) -> eq n (lit 0UL 8)
      result = fun (_, out) -> out }

/// A worker that GRINDS: accepts a beat, works `cycles` cycles, then offers
/// the bumped result. Both sides of its boundary speak ready/valid — the flow
/// control belongs to the module, not to whoever instantiates it — and the
/// body crosses that boundary through `streamOfPorts`/`streamToPorts`, so
/// inside it the stream API is the same one a design body speaks.
let slowWorker cycles =
    defModule
        $"SlowWorker%d{cycles}"
        (fun p ->
            { input = streamInPorts p "in" 8
              output = streamOutPorts p "out" 8 })
        (fun io ->
            streamOfPorts beatLayout io.input
            |> streamIterate beatLayout (grind cycles)
            |> streamToPorts io.output)

/// The call shape, an ordinary function beside the module: one named instance
/// per call, its input side fed from the caller's stream, its output side
/// handed back as one. The same two helpers the body used, pointed at the
/// instance's staging wires instead of the real ports.
let slowWorkerOf cycles instName (s: Stream<Expr>) : Stream<Expr> =
    let c = (slowWorker cycles).NewNamed instName
    streamToPorts c.input s
    streamOfPorts beatLayout c.output

/// A module with flow-control IO, dropped into a chain as if it were any
/// library stage — because from the chain's side it is one.
let ownStage =
    defModule
        "OwnStage"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> slowWorkerOf 3 "worker"
            |> streamSink outPorts)

/// One beat in, two out: broadcast copies every beat to both branches, which
/// do different work and merge back. A broadcast beat fires only when both
/// branches can take it — the slower branch sets the pace.
let streamFork =
    defModule
        "StreamFork"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) ->
            let stage = streamStageFor beatLayout
            let source = streamSource inPorts

            match streamBroadcast 2 source with
            | [ a; b ] ->
                let incremented = stage (Stream.map bump a)
                let doubled = stage (Stream.map (fun v -> v + v) b)
                streamSink outPorts (Stream.merge [ incremented; doubled ])
            | _ -> failwith "broadcast 2 gave the wrong arity")

/// Three workers of deliberately unequal depth — one, two and three stages.
/// Beats leave in completion order, not issue order, so each one carries an
/// `id` that rides through untouched: without it there is no way to tell which
/// answer belongs to which question.
let streamFarm =
    defModule
        "StreamFarm"
        (fun p -> (streamInputPorts p "in" tagged, streamOutputPorts p "out" tagged))
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> Stream.farm 3 (fun i lane -> lane |> Stream.stages (i + 1) (fun (id, v) -> id, bump v))
            |> streamSink outPorts)

/// A buffer between a producer and a consumer: the same beats, later, with room
/// for eight of them in between. Nothing transforms the payload — the whole of
/// what it buys is that the two ends stop having to move in lockstep.
let streamBuffer =
    defModule
        "StreamBuffer"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> streamFifo "fifo" 8
            |> streamSink outPorts)

/// A slow stage with the caller's data carried through it, and then the same
/// thing replicated.
///
/// The divider takes operands and returns a quotient; it has never heard of an
/// `id`. `withContext` puts the id in a FIFO and hands it back paired with the
/// answer — so unlike **Farm**, where the payload was widened by hand to carry
/// one, nothing about the worker changes.
let streamContext =
    defModule
        "StreamContext"
        (fun p ->
            (p.inPort "in_dividend" 8,
             p.inPort "in_divisor" 8,
             p.inPort "in_id" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_quotient" 8,
             p.outPort "out_remainder" 8,
             p.outPort "out_id" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (inDividend, inDivisor, inId, inValid, inReady, quotientOut, remainderOut, idOut, outValid, outReady) ->
            let operands = layout2 ("dividend", 8) ("divisor", 8)
            let results = layout2 ("quotient", 8) ("remainder", 8)
            let identity = layout1 ("id", 8)

            let src =
                { payload = (inDividend, inDivisor), inId
                  valid = inValid
                  ready = inReady
                  layout = layoutJoin operands identity }

            // Three lanes of deliberately unequal depth, as in **Farm** — so beats
            // really do overtake one another and the id has something to prove.
            let out =
                Stream.farmWith "dv" 3 2 operands results identity
                    (fun i -> divider $"dv%d{i}" 8 >> Stream.stages (i * 8) id)
                    src

            let (quotient, remainder), identifier = out.payload

            quotient ==> quotientOut
            remainder ==> remainderOut
            identifier ==> idOut
            out.valid ==> outValid
            outReady ==> out.ready)

/// The same chain with telemetry on both ends. The counters are ordinary
/// registers, so finding out where a design stalls costs a peek rather than a
/// Vivado run.
let streamProbes =
    defModule
        "StreamProbes"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> Stream.probe "intake"
            |> Stream.stages 2 bump
            |> Stream.probe "egress"
            |> streamSink outPorts)

/// A pipeline written as data: three stage descriptors in a list, one of them
/// three lanes wide and two of them probed. The multiplicity and the telemetry
/// are properties of the description, not calls the neighbours can see.
let streamPipeline =
    defModule
        "StreamPipeline"
        (fun p -> (streamInputPorts p "in" beatLayout, streamOutputPorts p "out" beatLayout))
        (fun (inPorts, outPorts) ->
            let bumpStage = Stream.specFromFunction (Stream.stage bump)
            let doubleStage = Stream.specFromFunction (Stream.stage (fun v -> v + v))

            streamSource inPorts
            |> Stream.pipeline
                [ bumpStage |> Stream.probed "intake"
                  doubleStage |> Stream.lanes 3 |> Stream.probed "farm"
                  bumpStage ]
            |> streamSink outPorts)

/// A producer that cannot be told to wait. A counter emits a beat every cycle
/// `sample` is high; giving that a `ready` is where beats get lost, and
/// `flowToStream` hands back exactly which cycles they were lost on.
let flowSampler =
    defModule
        "FlowSampler"
        (fun p ->
            (p.inPort "sample" 1,
             p.inPort "out_ready" 1,
             p.outPort "out_value" 8,
             p.outPort "out_valid" 1,
             p.outPort "dropped_count" 8))
        (fun (sample, outReady, value, valid, droppedOut) ->
            let ticks = reg "ticks" 8
            If sample (fun () -> ticks + lit 1UL 8 ==> ticks)

            let sampled =
                { payload = ticks
                  valid = sample
                  layout = beatLayout }
                |> flowStage "staged"

            let stream, overflowed = flowToStream sampled
            outReady ==> stream.ready

            stream.payload ==> value
            stream.valid ==> valid

            // The one place this design loses data, counted rather than ignored.
            let dropped = reg "dropped" 8
            If overflowed (fun () -> dropped + lit 1UL 8 ==> dropped)
            dropped ==> droppedOut)

// ---------------------------------------------------------------------------
// The combinators: small shapes that were each written by hand four or five
// times before the library lifted them.

/// Three cycles of arithmetic on the data, and a tag that has to travel the
/// same distance to still be describing the same beat. `raw_tag` is what it
/// looks like when it does not.
let delayAlign =
    defModule
        "DelayAlign"
        (fun p ->
            (p.inPort "data" 8,
             p.inPort "tag" 1,
             p.outPort "out" 8,
             p.outPort "aligned_tag" 1,
             p.outPort "raw_tag" 1))
        (fun (data, tag, out, aligned, raw) ->
            delayChain "data" 8 3 (data + lit 1UL 8) ==> out

            delayChain "tag" 1 3 tag ==> aligned

            tag ==> raw)

/// Turning a level into an event. `enable` gates only the sample, so the whole
/// thing can detect edges in a slower domain than the clock.
let edges =
    defModule
        "Edges"
        (fun p ->
            (p.inPort "signal" 1,
             p.inPort "enable" 1,
             p.outPort "rising" 1,
             p.outPort "falling" 1,
             p.outPort "changed" 1,
             p.outPort "previous" 1,
             p.outPort "pulses" 8))
        (fun (signal, enable, rising, falling, changed, previous, pulses) ->
            let e = edgeDetect "sig" enable signal

            e.rising ==> rising
            e.falling ==> falling
            e.changed ==> changed
            e.previous ==> previous

            // Counting edges is the usual reason to find them.
            let seen = reg "seen" 8
            If e.rising (fun () -> seen + lit 1UL 8 ==> seen)
            seen ==> pulses)

/// A maximal-length Galois LFSR: a shift and a masked xor, visiting all 255
/// non-zero states before repeating.
let noise =
    defModule
        "Noise"
        (fun p -> (p.inPort "step" 1, p.outPort "value" 8, p.outPort "low_bit" 1))
        (fun (step, value, lowBit) ->
            let state = lfsr "state" 8 0xACUL step

            state ==> value

            // The reason it is not a random-number generator: consecutive states
            // share seven of their eight bits.
            slice 0 0 state ==> lowBit)

/// Four requesters, one server. `oneHotLowest` turns the request bits into a
/// grant exactly one of which is high, and `mux1H` uses that grant to select
/// the winner's payload without a comparator anywhere.
let arbiter =
    defModule
        "Arbiter"
        (fun p ->
            ([ for i in 0..3 -> p.inPort $"req{i}" 1 ],
             [ for i in 0..3 -> p.inPort $"value{i}" 8 ],
             [ for i in 0..3 -> p.outPort $"grant{i}" 1 ],
             p.outPort "any" 1,
             p.outPort "served" 8))
        (fun (requests, values, grantOuts, any, served) ->
            let grants = oneHotLowest requests

            for i in 0..3 do
                grants[i] ==> grantOuts[i]

            reduceTree (|||) requests ==> any

            mux1H grants values ==> served)

/// Eight values summed two ways: a combinational balanced tree, and the same
/// tree with every level registered. They agree — after the pipelined one has
/// been given its cycles.
let adderTree =
    defModule
        "AdderTree"
        (fun p ->
            (p.inPort "enable" 1,
             [ for i in 0..7 -> p.inPort $"x{i}" 8 ],
             p.outPort "flat" 11,
             p.outPort "pipelined" 11,
             p.outPort "depth" 4))
        (fun (enable, inputs, flat, pipelined, depth) ->
            let widen x = cat (lit 0UL 3) x
            let widened = List.map widen inputs

            reduceTree (+) widened ==> flat

            let deep, levels = adderTreePipelined "acc" 11 enable widened

            deep ==> pipelined

            // The latency is reported, not assumed — it is however deep the tree
            // turned out to be.
            lit (uint64 levels) 4 ==> depth)

/// Two wrap counters and a cascade. `columns` wraps every 5 counts and its
/// wrap is what advances `rows` — which is how a raster scan is built, and why
/// the wrap is a signal rather than something the caller recomputes.
let wrapCounter =
    defModule
        "WrapCounter"
        (fun p ->
            (p.inPort "enable" 1,
             p.inPort "last" 4,
             p.outPort "column" 3,
             p.outPort "column_wrap" 1,
             p.outPort "row" 2,
             p.outPort "bounded_count" 4,
             p.outPort "bounded_wrap" 1))
        (fun (enable, last, columnOut, columnWrap, rowOut, boundedOut, boundedWrap) ->
            // Qualified because this project's own first design is called
            // `counter`, and it shadows the stdlib entry of the same name.
            let columns = Warp11.Stdlib.counter "columns" 5 enable
            columns.count ==> columnOut
            columns.wrap ==> columnWrap

            let rows = Warp11.Stdlib.counter "rows" 3 columns.wrap
            rows.count ==> rowOut

            // The same shape with a bound the design does not know until it runs.
            let bounded = counterTo "bounded" last enable
            bounded.count ==> boundedOut
            bounded.wrap ==> boundedWrap)

// ---------------------------------------------------------------------------
// The substrates: the shapes the accelerators in this repository are built out
// of. Everything above is about the language; these are about the machine, and
// each one carries a constraint that only silicon imposes.

/// Four work items interleaved through one two-cycle pipeline. A thread reads
/// its running total at issue and writes it back two cycles later, which is
/// only correct because its next turn is four cycles away.
let barrelLane =
    defModule
        "BarrelLane"
        (fun p ->
            (p.inPort "x" 8,
             [ for t in 0..3 -> p.outPort $"thread{t}" 16 ],
             p.outPort "turn_now" 2,
             p.outPort "latency" 4,
             p.outPort "threads" 4))
        (fun (x, threadOuts, slot, latencyOut, threadsOut) ->
            // Two cycles from issue to writeback, four threads to cover them.
            let lane = barrel 2 4

            let turn = reg "turn" 2
            turn + lit 1UL 2 ==> turn

            let acc = distributedMem "acc" 2 16

            // Issue: this thread's running total, and a weight that says which
            // thread it is — thread t adds t+1 times the sample, so the four are
            // told apart at a glance.
            let current = wire "current" 16
            memRead acc turn ==> current
            let weight = wire "weight" 8
            cat (lit 0UL 6) turn + lit 1UL 8 ==> weight

            // The cone: multiply, register, add, register. Two cycles deep, and
            // the total read at issue has to be held for one of them to meet the
            // product it belongs with.
            let product = delayChain "product" 16 1 (mul x weight)
            let held = lane.CarryTo 1 "issued" 16 current
            let sum = delayChain "sum" 16 1 (held + product)

            // Writeback, to whichever thread issued two cycles ago.
            memWrite acc (lane.Carry "slot" 2 turn) sum (lit 1UL 1)

            for t in 0..3 do
                memRead acc (lit (uint64 t) 2) ==> threadOuts[t]

            turn ==> slot

            // Both are elaboration-time facts about the lane, not runtime state.
            lit (uint64 lane.Latency) 4 ==> latencyOut
            lit (uint64 lane.Threads) 4 ==> threadsOut)

/// xoshiro128++ in fabric: 128 bits of state, one 32-bit word per `step`, and
/// not a multiplier in it. `load` replaces the whole state in one cycle, which
/// is how a host seeds it.
let prng =
    defModule
        "Prng"
        (fun p ->
            (p.inPort "step" 1,
             p.inPort "load" 1,
             [ for i in 0..3 -> p.inPort $"seed{i}" 32 ],
             p.outPort "value" 32,
             p.outPort "roll" 3,
             p.outPort "drawn" 16))
        (fun (step, load, seed, value, roll, count) ->
            let word = xoshiro128pp "Xoshiro128pp" "rng" load seed step

            word ==> value

            // The usual reason to want one: a bounded draw. Every bit of a
            // xoshiro word is equally good, so a mask is a fair die.
            slice 2 0 word ==> roll

            let draws = reg "draws" 16
            If step (fun () -> draws + lit 1UL 16 ==> draws)
            draws ==> count)

/// Two four-tap filters over one sample stream: a [1,2,2,1] low-pass and a
/// boxcar average. Same hardware shape, different constants — which is the
/// whole of what a FIR is.
let firFilter =
    defModule
        "FirFilter"
        (fun p ->
            (p.inPort "sample" 8,
             p.outPort "smoothed" 18,
             p.outPort "averaged" 18,
             p.outPort "raw" 8))
        (fun (sample, smoothed, averaged, raw) ->
            fir 8 8 [ 1UL; 2UL; 2UL; 1UL ] sample ==> smoothed

            fir 8 8 [ 1UL; 1UL; 1UL; 1UL ] sample ==> averaged

            // The unfiltered sample, to see what the delay line cost.
            sample ==> raw)

/// One Game of Life cell, and the three things an off-grid neighbor can be.
/// `neighborhood` gathers the eight expressions; what to do with them — count,
/// compare, apply a rule — is the design's business, not the library's.
let lifeCell =
    defModule
        "LifeCell"
        (fun p ->
            (inPortArray p "g" 3 3 1,
             p.outPort "live" 4,
             p.outPort "next" 1,
             p.outPort "corner_zero" 4,
             p.outPort "corner_wrap" 4,
             p.outPort "corner_clamp" 4,
             p.outPort "orthogonal" 4))
        (fun (grid, liveOut, next, cornerZero, cornerWrap, cornerClamp, orthogonal) ->
            let count out stencil edge y x =
                countWhere 4 id (neighborhood stencil edge grid y x) ==> out
                out

            let live = count liveOut Stencil.Moore Edge.Zero 1 1

            // Life's rule, in the one line it actually is.
            (eq live (lit 3UL 4) ||| (grid[1][1] &&& eq live (lit 2UL 4))) ==> next

            // The same corner cell under all three border policies. They disagree,
            // and the page is mostly about how.
            count cornerZero Stencil.Moore Edge.Zero 0 0 |> ignore
            count cornerWrap Stencil.Moore Edge.Wrap 0 0 |> ignore
            count cornerClamp Stencil.Moore Edge.Clamp 0 0 |> ignore

            count orthogonal Stencil.VonNeumann Edge.Zero 1 1 |> ignore)

/// Two clients sharing one two-cycle multiplier. Neither client knows the
/// other exists: each offers a tagged beat and gets a tagged answer back, and
/// everything between — arbitration, the tag delay line, the writeback demux —
/// is `warpFu`.
let sharedUnit =
    defModule
        "SharedUnit"
        (fun p ->
            let issue = fuLayout 4 [ "a", 8; "b", 8 ]

            ([ for i in 0..1 -> streamInputPorts p $"c{i}" issue ],
             [ for i in 0..1 -> streamOutputPorts p $"w{i}" (fuLayout 4 [ "product", 16 ]) ]))
        (fun (clientPorts, writebackPorts) ->
            let clients = clientPorts |> List.map streamSource

            // The unit itself: an ordinary two-cycle multiply that has never heard
            // of tags, clients or arbitration. It reports its own depth, so the
            // number appears once — `warpFu` is not told a latency it would have no
            // way to check.
            let stages = 2

            let multiply operands =
                match operands with
                | [ a; b ] -> [ delayChain "mul" 16 stages (mul a b) ], stages
                | _ -> failwith "the multiplier takes two operands"

            warpFu "fu" [ "product", 16 ] multiply clients
            |> List.iteri (fun i s -> streamSink writebackPorts[i] s))

/// A register map: four words the host can reach over AXI-Lite. `control` is
/// written by the host and read by the design, `identity` is a constant the
/// driver checks it is talking to the right bitstream, and `ticks` is live
/// state the host polls.
let registerMap =
    defModule
        "RegisterMap"
        (fun p ->
            // a 16-byte aperture: four words at 0x0, 0x4, 0x8, 0xC
            (axiLiteSlavePorts p 4, p.outPort "running" 1, p.outPort "elapsed" 32))
        (fun (slavePorts, running, elapsed) ->
            let ticks = reg "ticks" 32

            let regs =
                axiLiteSlaveOn
                    slavePorts
                    [ "control", 0x0UL, 32 ]
                    [ 0x4UL, lit 0xA57AUL 32; 0x8UL, ticks ]
                    []

            match regs with
            | [ control ] ->
                let go = wireBit "go"
                slice 0 0 control ==> go
                If go (fun () -> ticks + lit 1UL 32 ==> ticks)

                // The same two values at ports, so the debugger can watch them
                // without speaking AXI.
                go ==> running
                ticks ==> elapsed
            | _ -> failwith "expected exactly one write register")

/// A master on the memory bus: fabric reaching out to DDR rather than waiting
/// to be poked. The read half takes addresses and hands back data; the write
/// half streams words out — but only once the host has said where.
let ddrMaster =
    defModule
        "DdrMaster"
        (fun p ->
            (streamInputPorts p "req" (layout1 ("addr", 32)),
             axiReadBusPorts p "m_axi" 32 32,
             streamOutputPorts p "resp" (layout1 ("data", 32)),
             p.inPort "base_addr" 32,
             p.outPort "armed" 1,
             axiWriteBusPorts p "m_axi" 32 32,
             p.outPort "words_written" 8))
        (fun (reqPorts, readBus, respPorts, baseAddr, armed, writeBus, written) ->
            streamSource reqPorts
            |> axiMasterReaderOn (axiReadBusOf readBus) 4
            |> streamSink respPorts

            // The arm gate. A master that free-runs will write to whatever its
            // reset value points at, and tearing the design down mid-write leaves
            // the memory path skewed until the board is rebooted.
            bnot (eq baseAddr (lit 0UL 32)) ==> armed

            let index = reg "index" 8
            let payload = reg "payload" 32

            let ready = wireBit "beat_ready"
            registerStreamReady ready

            If (armed &&& ready) (fun () ->
                index + lit 1UL 8 ==> index
                payload + lit 1UL 32 ==> payload)

            let addr = wire "beat_addr" 32
            baseAddr + cat (lit 0UL 22) (cat index (lit 0UL 2)) ==> addr

            { payload = addr, payload, lit 0xFUL 4
              valid = armed
              ready = ready
              layout = axiWriteBeatLayout 32 32 }
            |> axiMasterWriterOn (axiWriteBusOf writeBus) 4

            index ==> written)
