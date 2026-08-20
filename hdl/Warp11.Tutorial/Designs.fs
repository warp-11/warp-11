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
    design "Counter" (fun () ->
        let enable = inputBit "enable"
        let clear = inputBit "clear"
        let count = output "count" 64
        let r = reg "r" 64

        If clear (fun () -> 0UL ==> r)
        Else (fun () -> If enable (fun () -> r + 1UL ==> r))

        r ==> count)

/// Unsigned compare at ports: three one-bit verdicts and the larger operand.
/// `less`/`equal`/`greater` rather than `lt`/`eq`/`gt`, because those are the
/// operators' own names and a port may not shadow one.
let comparator =
    design "Comparator" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let less = outputBit "less"
        let equal = outputBit "equal"
        let greater = outputBit "greater"
        let larger = output "larger" 8

        lt a b ==> less
        eq a b ==> equal
        lt b a ==> greater
        mux (lt a b) b a ==> larger)

/// A defaulted wire under two sibling `If` blocks. The later statement ends up
/// outermost in the folded mux tree, so `sel1` outranks `sel0` — priority is
/// the order you wrote them in.
let priorityMux =
    design "PriorityMux" (fun () ->
        let sel0 = inputBit "sel0"
        let sel1 = inputBit "sel1"
        let a = input "a" 8
        let b = input "b" 8
        let c = input "c" 8
        let out = output "out" 8

        a ==> out
        If sel0 (fun () -> b ==> out)
        If sel1 (fun () -> c ==> out))

/// `(a * b) + (satInc c * d)`, built from three stdlib entries. Each call
/// plants its own hardware, so this design contains two multipliers.
let dotProduct =
    design "DotProduct" (fun () ->
        let multiply = mulOf 8
        let accumulate = adderOf 16
        let bump = satIncOf 8

        let a = input "a" 8
        let b = input "b" 8
        let c = input "c" 8
        let d = input "d" 8
        let out = output "out" 16

        accumulate (multiply a b) (multiply (bump c) d) ==> out)

/// A module of your own, defined once and instantiated twice.
///
/// `SatAcc8` is the full `defineModule` route — typed ports, state, a body
/// that is ordinary design code. `Min8` is the light route: a pure function
/// wrapped by `fnModule2`, made callable by `liftBinary`. The design holds two
/// `SatAcc8` instances so the one-definition-many-instances shape is visible
/// in the emitted Verilog, and its outputs are named `total_left`, not
/// `left_total` — an instance's staging wires are `{instance}_{port}` in the
/// parent's namespace, so `left_total` is already taken by the instance
/// called `left`.
let satAcc =
    defineModule
        "SatAcc8"
        (fun p ->
            {| add = p.inPort "add" 8
               en = p.inPort "en" 1
               total = p.outPort "total" 8 |})
        (fun m io ->
            fun (add: Expr) (en: Expr) ->
                add ==> io.add
                en ==> io.en
                io.total)
        (fun io _ ->
            let r = reg "r" 8
            let sum = wire "sum" 9
            pad 9 r + pad 9 io.add ==> sum

            let next = wire "next" 8
            mux (slice 8 8 sum) (lit 0xFFUL 8) (slice 7 0 sum) ==> next

            If io.en (fun () -> next ==> r)
            r ==> io.total)

let minOf8 =
    fnModule2 "Min8" ("a", 8) ("b", 8) "m" (fun a b -> mux (lt a b) a b)
    |> liftBinary

let ownModules =
    design "OwnModules" (fun () ->
        let addLeft = input "add_left" 8
        let addRight = input "add_right" 8
        let en = inputBit "en"

        let totalLeft = instanceNamed "left" satAcc addLeft en
        let totalRight = instanceNamed "right" satAcc addRight en

        totalLeft ==> output "total_left" 8
        totalRight ==> output "total_right" 8
        minOf8 totalLeft totalRight ==> output "lowest" 8)

/// The bit utilities in one place: join, fill, reverse, count, and the one-hot
/// round trip out to four grants and back to an index.
let bitShapes =
    design "BitShapes" (fun () ->
        let a = input "a" 4
        let b = input "b" 4
        let flag = inputBit "flag"
        let index = input "index" 2

        let joined = output "joined" 8
        catAll [ a; b ] ==> joined

        let mask = output "mask" 4
        fill 4 flag ==> mask

        let flipped = output "flipped" 4
        reverse a ==> flipped

        let ones = output "ones" 3
        popCount a ==> ones

        let hot = uintToOneHot 4 index

        for i in 0..3 do
            let grant = outputBit $"hot{i}"
            hot[i] ==> grant

        let recovered = output "recovered" 2
        oneHotToUInt hot ==> recovered)

/// The same bits read two ways. `diff` needs no signed form because two's
/// complement makes one subtractor correct for both readings; compare,
/// multiply and right-shift are three of the six that genuinely differ.
let signedOps =
    design "SignedOps" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let diff = output "diff" 8
        // Signed, because a signed multiply is what drives it. The bits are the
        // same either way; the declaration is what lets the debugger show −100
        // rather than 65436, and it is the lesson of this design at its own port.
        let product = output "product" (SInt 16)
        let below = outputBit "below"
        let belowSigned = outputBit "below_signed"
        let shifted = output "shifted" 8

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
    design "Ram" (fun () ->
        let waddr = input "waddr" 3
        let wdata = input "wdata" 8
        let wen = inputBit "wen"
        let raddr = input "raddr" 3
        let nextCycleOut = output "next_cycle_out" 8
        let thisCycleOut = output "this_cycle_out" 8

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
let sequencer =
    design "Sequencer" (fun () ->
        let start = inputBit "start"
        let stall = inputBit "stall"
        let busy = outputBit "busy"
        let finished = outputBit "finished"
        let retired = output "retired" 8

        let stage = machine "stage" [ Idle; Fetch; Decode; Execute; Writeback; Done ]
        let count = reg "count" 8

        bnot (stage.Is Idle ||| stage.Is Done) ==> busy
        stage.Is Done ==> finished
        count ==> retired

        let begin' () =
            If start (fun () ->
                lit 0UL 8 ==> count
                stage.Goto Fetch)

        stage.If Idle begin'
        stage.If Done begin'
        stage.If Fetch (fun () -> stage.Goto Decode)
        stage.If Decode (fun () -> stage.Goto Execute)
        stage.If Execute (fun () -> If (bnot stall) (fun () -> stage.Goto Writeback))

        stage.If Writeback (fun () ->
            count + lit 1UL 8 ==> count

            If (eq count (lit 3UL 8)) (fun () -> stage.Goto Done)
            Else (fun () -> stage.Goto Fetch)))

/// A Q format is one line: a total width, a count of fraction bits, and a
/// measure binding the two so the type system can carry it. Q5.3 is the same
/// eight bits as `q4_4` with the point moved.
let private q5_3 = Number.signedFixed 8 3

/// Fixed-point arithmetic where the Q format is part of the type. A multiply
/// changes format — widths add and fraction bits add — and the renormalization
/// back is a slice the target format names.
let fixedPoint =
    design "FixedPoint" (fun () ->
        let a = Number.input "a" Number.q4_4
        let b = Number.input "b" Number.q4_4

        // Q4.4 * Q4.4 is Q8.8: sixteen bits, eight of them fractional.
        let wide = Number.wire "wide" (a * b)

        // The format says signed, so the port does too — which is how the
        // debugger knows to read −48 rather than 208.
        let product = output "product" (Number.groundType Number.q4_4)
        (Number.renormTo Number.q4_4 wide) ==> product

        // The same eight bits read as Q5.3 mean twice as much. No gates.
        let doubled = output "doubled" (Number.groundType q5_3)
        (Number.reinterpret q5_3 a) ==> doubled

        let below = outputBit "below"
        Number.lessThan a b ==> below)

/// Two read-only tables. Contents are fixed at elaboration and become a Verilog
/// `initial` block, which Vivado turns into a memory the bitstream arrives
/// pre-loaded with.
let romTable =
    design "RomTable" (fun () ->
        let index = input "index" 3

        let squares = distributedRom "squares" 8 [| 0UL; 1UL; 4UL; 9UL; 16UL; 25UL; 36UL; 49UL |]
        let square = output "square" 8
        memRead squares index ==> square

        // Five values in a table that has to be a power of two deep: the
        // remaining three addresses read zero.
        let primes = distributedRom "primes" 8 [| 2UL; 3UL; 5UL; 7UL; 11UL |]
        let prime = output "prime" 8
        memRead primes index ==> prime)

/// A claim the design makes about itself, checked every cycle. The counter
/// walks 0 to 4 and wraps, so the top three of its eight reachable values are
/// unreachable — and it says so.
let assertions =
    design "Assertions" (fun () ->
        let step = inputBit "step"
        let phase = output "phase" 3
        let wrapped = outputBit "wrapped"
        let r = reg "r" 3

        If step (fun () ->
            If (eq r (lit 4UL 3)) (fun () -> lit 0UL 3 ==> r)
            Else (fun () -> r + lit 1UL 3 ==> r))

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
    design "StreamPipe" (fun () ->
        Stream.input "in" beatLayout |> Stream.map bump |> Stream.out "out")

/// Three registered stages. Each buys a cycle of latency and a place for a
/// beat to wait, which is what makes the chain elastic under backpressure.
let streamStages =
    design "StreamStages" (fun () ->
        Stream.input "in" beatLayout |> Stream.stages 3 bump |> Stream.out "out")

/// One beat in, two out: broadcast copies every beat to both branches, which
/// do different work and merge back. A broadcast beat fires only when both
/// branches can take it — the slower branch sets the pace.
let streamFork =
    design "StreamFork" (fun () ->
        let stage = streamStageFor beatLayout
        let source = Stream.input "in" beatLayout

        match streamBroadcast 2 source with
        | [ a; b ] ->
            let incremented = stage (Stream.map bump a)
            let doubled = stage (Stream.map (fun v -> v + v) b)
            Stream.out "out" (Stream.merge [ incremented; doubled ])
        | _ -> failwith "broadcast 2 gave the wrong arity")

/// Three workers of deliberately unequal depth — one, two and three stages.
/// Beats leave in completion order, not issue order, so each one carries an
/// `id` that rides through untouched: without it there is no way to tell which
/// answer belongs to which question.
let streamFarm =
    design "StreamFarm" (fun () ->
        Stream.input "in" tagged
        |> Stream.farm 3 (fun i lane -> lane |> Stream.stages (i + 1) (fun (id, v) -> id, bump v))
        |> Stream.out "out")

/// A buffer between a producer and a consumer: the same beats, later, with room
/// for eight of them in between. Nothing transforms the payload — the whole of
/// what it buys is that the two ends stop having to move in lockstep.
let streamBuffer =
    design "StreamBuffer" (fun () ->
        Stream.input "in" beatLayout
        |> streamFifo "fifo" 8
        |> Stream.out "out")

/// A slow stage with the caller's data carried through it, and then the same
/// thing replicated.
///
/// The divider takes operands and returns a quotient; it has never heard of an
/// `id`. `withContext` puts the id in a FIFO and hands it back paired with the
/// answer — so unlike **Farm**, where the payload was widened by hand to carry
/// one, nothing about the worker changes.
let streamContext =
    design "StreamContext" (fun () ->
        let operands = layout2 ("dividend", 8) ("divisor", 8)
        let results = layout2 ("quotient", 8) ("remainder", 8)
        let identity = layout1 ("id", 8)

        let src =
            { payload = (input "in_dividend" 8, input "in_divisor" 8), input "in_id" 8
              valid = inputBit "in_valid"
              ready = outputBit "in_ready"
              layout = layoutJoin operands identity }

        // Three lanes of deliberately unequal depth, as in **Farm** — so beats
        // really do overtake one another and the id has something to prove.
        let out =
            Stream.farmWith "dv" 3 2 operands results identity
                (fun i -> divider $"dv%d{i}" 8 >> Stream.stages (i * 8) id)
                src

        let (quotient, remainder), identifier = out.payload

        quotient ==> output "out_quotient" 8
        remainder ==> output "out_remainder" 8
        identifier ==> output "out_id" 8
        out.valid ==> outputBit "out_valid"
        inputBit "out_ready" ==> out.ready)

/// The same chain with telemetry on both ends. The counters are ordinary
/// registers, so finding out where a design stalls costs a peek rather than a
/// Vivado run.
let streamProbes =
    design "StreamProbes" (fun () ->
        Stream.input "in" beatLayout
        |> Stream.probe "intake"
        |> Stream.stages 2 bump
        |> Stream.probe "egress"
        |> Stream.out "out")

/// A pipeline written as data: three stage descriptors in a list, one of them
/// three lanes wide and two of them probed. The multiplicity and the telemetry
/// are properties of the description, not calls the neighbours can see.
let streamPipeline =
    design "StreamPipeline" (fun () ->
        let bumpStage = Stream.specFromFunction (Stream.stage bump)
        let doubleStage = Stream.specFromFunction (Stream.stage (fun v -> v + v))

        Stream.input "in" beatLayout
        |> Stream.pipeline
            [ bumpStage |> Stream.probed "intake"
              doubleStage |> Stream.lanes 3 |> Stream.probed "farm"
              bumpStage ]
        |> Stream.out "out")

/// A producer that cannot be told to wait. A counter emits a beat every cycle
/// `sample` is high; giving that a `ready` is where beats get lost, and
/// `flowToStream` hands back exactly which cycles they were lost on.
let flowSampler =
    design "FlowSampler" (fun () ->
        let sample = inputBit "sample"
        let outReady = inputBit "out_ready"

        let ticks = reg "ticks" 8
        If sample (fun () -> ticks + lit 1UL 8 ==> ticks)

        let sampled =
            { payload = ticks
              valid = sample
              layout = beatLayout }
            |> flowStage "staged"

        let stream, overflowed = flowToStream sampled
        outReady ==> stream.ready

        let value = output "out_value" 8
        stream.payload ==> value
        let valid = outputBit "out_valid"
        stream.valid ==> valid

        // The one place this design loses data, counted rather than ignored.
        let dropped = reg "dropped" 8
        If overflowed (fun () -> dropped + lit 1UL 8 ==> dropped)
        let droppedOut = output "dropped_count" 8
        dropped ==> droppedOut)

// ---------------------------------------------------------------------------
// The combinators: small shapes that were each written by hand four or five
// times before the library lifted them.

/// Three cycles of arithmetic on the data, and a tag that has to travel the
/// same distance to still be describing the same beat. `raw_tag` is what it
/// looks like when it does not.
let delayAlign =
    design "DelayAlign" (fun () ->
        let data = input "data" 8
        let tag = inputBit "tag"

        let out = output "out" 8
        delayChain "data" 8 3 (data + lit 1UL 8) ==> out

        let aligned = outputBit "aligned_tag"
        delayChain "tag" 1 3 tag ==> aligned

        let raw = outputBit "raw_tag"
        tag ==> raw)

/// Turning a level into an event. `enable` gates only the sample, so the whole
/// thing can detect edges in a slower domain than the clock.
let edges =
    design "Edges" (fun () ->
        let signal = inputBit "signal"
        let enable = inputBit "enable"

        let e = edgeDetect "sig" enable signal

        let rising = outputBit "rising"
        e.rising ==> rising
        let falling = outputBit "falling"
        e.falling ==> falling
        let changed = outputBit "changed"
        e.changed ==> changed
        let previous = outputBit "previous"
        e.previous ==> previous

        // Counting edges is the usual reason to find them.
        let seen = reg "seen" 8
        If e.rising (fun () -> seen + lit 1UL 8 ==> seen)
        let pulses = output "pulses" 8
        seen ==> pulses)

/// A maximal-length Galois LFSR: a shift and a masked xor, visiting all 255
/// non-zero states before repeating.
let noise =
    design "Noise" (fun () ->
        let step = inputBit "step"
        let state = lfsr "state" 8 0xACUL step

        let value = output "value" 8
        state ==> value

        // The reason it is not a random-number generator: consecutive states
        // share seven of their eight bits.
        let lowBit = outputBit "low_bit"
        slice 0 0 state ==> lowBit)

/// Four requesters, one server. `oneHotLowest` turns the request bits into a
/// grant exactly one of which is high, and `mux1H` uses that grant to select
/// the winner's payload without a comparator anywhere.
let arbiter =
    design "Arbiter" (fun () ->
        let requests = [ for i in 0..3 -> inputBit $"req{i}" ]
        let values = [ for i in 0..3 -> input $"value{i}" 8 ]

        let grants = oneHotLowest requests

        for i in 0..3 do
            let g = outputBit $"grant{i}"
            grants[i] ==> g

        let any = outputBit "any"
        reduceTree (|||) requests ==> any

        let served = output "served" 8
        mux1H grants values ==> served)

/// Eight values summed two ways: a combinational balanced tree, and the same
/// tree with every level registered. They agree — after the pipelined one has
/// been given its cycles.
let adderTree =
    design "AdderTree" (fun () ->
        let enable = inputBit "enable"
        let inputs = [ for i in 0..7 -> input $"x{i}" 8 ]
        let widen x = cat (lit 0UL 3) x
        let widened = List.map widen inputs

        let flat = output "flat" 11
        reduceTree (+) widened ==> flat

        let deep, levels = adderTreePipelined "acc" 11 enable widened

        let pipelined = output "pipelined" 11
        deep ==> pipelined

        // The latency is reported, not assumed — it is however deep the tree
        // turned out to be.
        let depth = output "depth" 4
        lit (uint64 levels) 4 ==> depth)

/// Two wrap counters and a cascade. `columns` wraps every 5 counts and its
/// wrap is what advances `rows` — which is how a raster scan is built, and why
/// the wrap is a signal rather than something the caller recomputes.
let wrapCounter =
    design "WrapCounter" (fun () ->
        let enable = inputBit "enable"
        let last = input "last" 4

        // Qualified because this project's own first design is called
        // `counter`, and it shadows the stdlib entry of the same name.
        let columns = Warp11.Stdlib.counter "columns" 5 enable
        let columnOut = output "column" 3
        columns.count ==> columnOut
        let columnWrap = outputBit "column_wrap"
        columns.wrap ==> columnWrap

        let rows = Warp11.Stdlib.counter "rows" 3 columns.wrap
        let rowOut = output "row" 2
        rows.count ==> rowOut

        // The same shape with a bound the design does not know until it runs.
        let bounded = counterTo "bounded" last enable
        let boundedOut = output "bounded_count" 4
        bounded.count ==> boundedOut
        let boundedWrap = outputBit "bounded_wrap"
        bounded.wrap ==> boundedWrap)

// ---------------------------------------------------------------------------
// The substrates: the shapes the accelerators in this repository are built out
// of. Everything above is about the language; these are about the machine, and
// each one carries a constraint that only silicon imposes.

/// Four work items interleaved through one two-cycle pipeline. A thread reads
/// its running total at issue and writes it back two cycles later, which is
/// only correct because its next turn is four cycles away.
let barrelLane =
    design "BarrelLane" (fun () ->
        let x = input "x" 8

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
            let total = output $"thread{t}" 16
            memRead acc (lit (uint64 t) 2) ==> total

        let slot = output "turn_now" 2
        turn ==> slot

        // Both are elaboration-time facts about the lane, not runtime state.
        let latency = output "latency" 4
        lit (uint64 lane.Latency) 4 ==> latency
        let threads = output "threads" 4
        lit (uint64 lane.Threads) 4 ==> threads)

/// xoshiro128++ in fabric: 128 bits of state, one 32-bit word per `step`, and
/// not a multiplier in it. `load` replaces the whole state in one cycle, which
/// is how a host seeds it.
let prng =
    design "Prng" (fun () ->
        let step = inputBit "step"
        let load = inputBit "load"
        let seed = [ for i in 0..3 -> input $"seed{i}" 32 ]

        let word = instanceNamed "rng" (xoshiro128pp "Xoshiro128pp") load seed step

        let value = output "value" 32
        word ==> value

        // The usual reason to want one: a bounded draw. Every bit of a
        // xoshiro word is equally good, so a mask is a fair die.
        let roll = output "roll" 3
        slice 2 0 word ==> roll

        let draws = reg "draws" 16
        If step (fun () -> draws + lit 1UL 16 ==> draws)
        let count = output "drawn" 16
        draws ==> count)

/// Two four-tap filters over one sample stream: a [1,2,2,1] low-pass and a
/// boxcar average. Same hardware shape, different constants — which is the
/// whole of what a FIR is.
let firFilter =
    design "FirFilter" (fun () ->
        let sample = input "sample" 8

        let smoothed = output "smoothed" 18
        fir 8 8 [ 1UL; 2UL; 2UL; 1UL ] sample ==> smoothed

        let averaged = output "averaged" 18
        fir 8 8 [ 1UL; 1UL; 1UL; 1UL ] sample ==> averaged

        // The unfiltered sample, to see what the delay line cost.
        let raw = output "raw" 8
        sample ==> raw)

/// One Game of Life cell, and the three things an off-grid neighbor can be.
/// `neighborhood` gathers the eight expressions; what to do with them — count,
/// compare, apply a rule — is the design's business, not the library's.
let lifeCell =
    design "LifeCell" (fun () ->
        let grid = [ for y in 0..2 -> [ for x in 0..2 -> inputBit $"g{y}{x}" ] ]

        let count name stencil edge y x =
            let out = output name 4
            countWhere 4 id (neighborhood stencil edge grid y x) ==> out
            out

        let live = count "live" Stencil.Moore Edge.Zero 1 1

        // Life's rule, in the one line it actually is.
        let next = outputBit "next"
        (eq live (lit 3UL 4) ||| (grid[1][1] &&& eq live (lit 2UL 4))) ==> next

        // The same corner cell under all three border policies. They disagree,
        // and the page is mostly about how.
        count "corner_zero" Stencil.Moore Edge.Zero 0 0 |> ignore
        count "corner_wrap" Stencil.Moore Edge.Wrap 0 0 |> ignore
        count "corner_clamp" Stencil.Moore Edge.Clamp 0 0 |> ignore

        count "orthogonal" Stencil.VonNeumann Edge.Zero 1 1 |> ignore)

/// Two clients sharing one two-cycle multiplier. Neither client knows the
/// other exists: each offers a tagged beat and gets a tagged answer back, and
/// everything between — arbitration, the tag delay line, the writeback demux —
/// is `warpFu`.
let sharedUnit =
    design "SharedUnit" (fun () ->
        let issue = fuLayout 4 [ "a", 8; "b", 8 ]
        let clients = [ for i in 0..1 -> Stream.input $"c{i}" issue ]

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
        |> List.iteri (fun i s -> Stream.out $"w{i}" s))

/// A register map: four words the host can reach over AXI-Lite. `control` is
/// written by the host and read by the design, `identity` is a constant the
/// driver checks it is talking to the right bitstream, and `ticks` is live
/// state the host polls.
let registerMap =
    design "RegisterMap" (fun () ->
        let ticks = reg "ticks" 32

        let regs =
            axiLiteSlave
                4 // a 16-byte aperture: four words at 0x0, 0x4, 0x8, 0xC
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
            let running = outputBit "running"
            go ==> running
            let elapsed = output "elapsed" 32
            ticks ==> elapsed
        | _ -> failwith "expected exactly one write register")

/// A master on the memory bus: fabric reaching out to DDR rather than waiting
/// to be poked. The read half takes addresses and hands back data; the write
/// half streams words out — but only once the host has said where.
let ddrMaster =
    design "DdrMaster" (fun () ->
        Stream.input "req" (layout1 ("addr", 32))
        |> axiMasterReader 32 32 4
        |> Stream.out "resp"

        // The arm gate. A master that free-runs will write to whatever its
        // reset value points at, and tearing the design down mid-write leaves
        // the memory path skewed until the board is rebooted.
        let baseAddr = input "base_addr" 32
        let armed = outputBit "armed"
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
        |> axiMasterWriter 32 32 4

        let written = output "words_written" 8
        index ==> written)
