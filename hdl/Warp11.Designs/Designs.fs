[<AutoOpen>]
module Warp11.Designs.Catalog

open Warp11
open Warp11.NumberOperators

let counterMutable =
    moduleDef "counter" (fun m ->
        let enable = m.Input("enable", 1)
        let count = m.Output("count", 8)
        let r = m.Reg("r", 8, 0UL)
        mux enable (r + lit 1UL 8) r ==> r
        r ==> count)

let adder8Def = fnModule2 "Adder8" ("a", 8) ("b", 8) "sum" (+)
let adder16Def = fnModule2 "Adder16" ("a", 16) ("b", 16) "sum" (+)
let mul8Def = fnModule2 "Mul8" ("a", 8) ("b", 8) "product" ( * )

let satInc8Def =
    fnModule1 "SatInc8" ("x", 8) "y" (fun x -> mux (eq x (lit 255UL 8)) x (x + lit 1UL 8))

/// The unsigned compare set at ports: three one-bit verdicts and the larger
/// operand. `less`/`equal`/`greater` rather than `lt`/`eq`/`gt` because those
/// are the operators' own names — a port may not shadow one.
let comparator8 =
    design "Comparator8" (fun () ->
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

let add3 =
    moduleDef "Add3" (fun m ->
        let x = m.Input("x", 8)
        let y = m.Input("y", 8)
        let z = m.Input("z", 8)
        let sum = m.Output("sum", 8)
        let a1 = m.Instance("a1", adder8Def)
        let a2 = m.Instance("a2", adder8Def)
        a2 (a1 x y) z ==> sum)

let dot2 =
    moduleDef "Dot2" (fun m ->
        let a = m.Input("a", 8)
        let b = m.Input("b", 8)
        let c = m.Input("c", 8)
        let d = m.Input("d", 8)
        let out = m.Output("out", 16)

        let mul1 = m.Instance("mul1", mul8Def)
        let mul2 = m.Instance("mul2", mul8Def)
        let acc = m.Instance("acc", adder16Def)
        let bump = m.Instance("bump", satInc8Def)

        acc (mul1 a b) (mul2 (bump c) d) ==> out)

let dot2Auto =
    moduleDef "Dot2Auto" (fun m ->
        let inst tm = m.Instance tm
        let a = m.Input("a", 8)
        let b = m.Input("b", 8)
        let c = m.Input("c", 8)
        let d = m.Input("d", 8)
        let out = m.Output("out", 16)

        inst adder16Def (inst mul8Def a b) (inst mul8Def (inst satInc8Def c) d)
        ==> out)

let dot2Ambient =
    design "Dot2Ambient" (fun () ->
        let multiply = mulOf 8
        let accumulate = adderOf 16
        let bump = satIncOf 8

        let a = input "a" 8
        let b = input "b" 8
        let c = input "c" 8
        let d = input "d" 8
        let out = output "out" 16

        accumulate (multiply a b) (multiply (bump c) d) ==> out)

/// Character-for-character the same body as `dot2Ambient`. Only the three stdlib
/// bindings differ — `*Logic` instead of `*Of` — and the design goes from four
/// modules to one flat one.
let dot2Inline =
    design "Dot2Inline" (fun () ->
        let multiply = mulLogic 8
        let accumulate = adderLogic 16
        let bump = satIncLogic 8

        let a = input "a" 8
        let b = input "b" 8
        let c = input "c" 8
        let d = input "d" 8
        let out = output "out" 16

        accumulate (multiply a b) (multiply (bump c) d) ==> out)

/// The dot product with three pipeline registers mixed in. `stage` is a stateful
/// module, `accumulate` and `bump` are inline, `multiply` is a combinational module —
/// nothing at the call site distinguishes the three kinds. Each application of
/// `stage` is a fresh register, which is exactly what a pipeline stage wants.
/// Latency 2 from inputs to `out`, verified by behavior_tb.v.
let pipelinedDot =
    design "PipelinedDot" (fun () ->
        let multiply = mulOf 8
        let accumulate = adderLogic 16
        let bump = satIncLogic 8
        let stage = delayOf 16

        let a = input "a" 8
        let b = input "b" 8
        let c = input "c" 8
        let d = input "d" 8
        let out = output "out" 16

        stage (accumulate (stage (multiply a b)) (stage (multiply (bump c) d)))
        ==> out)

/// A stateful module with feedback, used as a plain function of its enable.
let gatedCounter =
    design "GatedCounter" (fun () ->
        let count = counterOf 8
        let enable = inputBit "enable"
        let value = output "value" 8
        count enable ==> value)

/// A register that holds through reset, beside one that does not.
///
/// `regNoReset` is the data-path register: on an FPGA a reset net reaching every
/// flop costs fanout and routing, and it stops Vivado inferring an SRL for a
/// delay chain, so Xilinx's own advice is to reset control state and leave the
/// data path alone. It is also FIRRTL's plain `reg`, which is what most Chisel
/// output contains.
///
/// Both registers here take the same value from the same input on the same
/// edge. The difference shows up only when reset is asserted: `held` keeps what
/// it had, `cleared` goes back to 3. The emitted Verilog says it plainly —
/// `cleared` has a line in the reset branch and `held` does not.
let holdThroughReset =
    design "HoldThroughReset" (fun () ->
        let value = input "value" 8
        let held = regNoReset "held" 8
        let cleared = regInit "cleared" 8 3UL

        value ==> held
        value ==> cleared

        held ==> output "held_out" 8
        cleared ==> output "cleared_out" 8)

/// Shifting by a signal rather than by a number — a barrel shifter, where the
/// constant form is a rewiring.
///
/// The call is the same shape either way: `shl 3 a` appends three zero bits and
/// costs nothing, `shl n a` builds a shifter. Which one you get follows from
/// what you wrote, so there is no second name to remember and no way to write
/// one meaning the other.
///
/// The widths are FIRRTL's. A dynamic left shift keeps every bit it could ever
/// produce — `2^amountWidth - 1` more than it started with, so 8 bits shifted
/// by a 3-bit amount is 15 — because the elaborator cannot know the amount and
/// will not guess. A dynamic right shift keeps its operand's width. Both are
/// wider than most callers want, which is the point: narrowing is a decision,
/// taken with `saturate` or a slice, not a default.
let dynamicShifts =
    design "DynamicShifts" (fun () ->
        let value = input "value" 8
        let amount = input "amount" 3
        let signedValue = input "signed_value" (SInt 8)

        shl amount value ==> output "shifted_left" 15
        shr amount value ==> output "shifted_right" 8
        // Arithmetic, because the operand says it is signed — the sign fills in
        // from the top rather than zeros.
        shr amount signedValue ==> output "shifted_arith" 8
        // And the constant form, at the same call shape, for contrast.
        shl 3 value ==> output "shifted_fixed" 11)

/// A whole value folded to one bit.
///
/// `anyBitSet` is the one designs write by hand — `x != 0` — and it is a single
/// OR gate rather than a comparator. `allBitsSet` is the counter (a wrap is
/// about to happen), and `parity` is the XOR of every bit, which is what a
/// parity check is made of.
///
/// All three are FIRRTL's `orr`/`andr`/`xorr` and Verilog's `|x`/`&x`/`^x`,
/// and all three return one bit whatever they were given.
let bitReductions =
    design "BitReductions" (fun () ->
        let value = input "value" 8

        anyBitSet value ==> outputBit "any"
        allBitsSet value ==> outputBit "all"
        parity value ==> outputBit "odd")

/// Division, at the only shape this surface offers: by a constant.
///
/// `divideBy` takes an `int`, not an `Expr`, so dividing by a signal is not
/// something you can write here — the F# type refuses it before elaboration
/// runs. That line is where the cost is, not where the operation is: `/ 8` is a
/// shift, `/ 10` is a multiply by a reciprocal, and both are free from
/// synthesis, while a divisor that varies is thirty levels of logic that looks
/// identical at the call site.
///
/// The signed quotient is nine bits wide from an eight-bit dividend, which is
/// FIRRTL's rule and not padding: −128 / −1 is +128, and that does not fit in
/// eight.
let constantDivision =
    design "ConstantDivision" (fun () ->
        let value = input "value" 8
        let signedValue = input "signed_value" (SInt 8)

        divideBy 10 value ==> output "tenths" 8
        remainderBy 10 value ==> output "units" 8
        // A power of two, which synthesis turns back into a part-select.
        divideBy 8 value ==> output "eighths" 8
        divideBy 3 signedValue ==> output "thirds" (SInt 9))

/// The stdlib divider, wired as what it is: a stream stage.
///
/// The caller hands it `(dividend, divisor)` beats and reads `(quotient,
/// remainder)` beats back. **No latency crosses the boundary** — the unit
/// reuses one subtractor for eight iterations and therefore cannot take a new
/// pair every cycle, and `ready` is the only thing that can say so. Nothing
/// here counts cycles, which is the whole point of the shape.
let streamDivider =
    design "StreamDivider" (fun () ->
        let requests =
            { payload = input "dividend" 8, input "divisor" 8
              valid = inputBit "in_valid"
              ready = outputBit "in_ready"
              layout = layout2 ("dividend", 8) ("divisor", 8) }

        let results = divider "dv" 8 requests
        let quotient, remainder = results.payload

        quotient ==> output "quotient" 8
        remainder ==> output "remainder" 8
        results.valid ==> outputBit "out_valid"
        inputBit "out_ready" ==> results.ready)

/// A stream FIFO between a producer and a consumer.
///
/// The whole of what it buys is decoupling: a burst is absorbed, and a consumer
/// that pauses stops the producer only once the buffer is full. First-word
/// fall-through, so `payload` and `valid` arrive together as the contract
/// requires.
let bufferedStream =
    design "BufferedStream" (fun () ->
        let src =
            { payload = input "in_data" 8
              valid = inputBit "in_valid"
              ready = outputBit "in_ready"
              layout = layout1 ("data", 8) }

        let out = streamFifo "fifo" 8 src

        out.payload ==> output "out_data" 8
        out.valid ==> outputBit "out_valid"
        inputBit "out_ready" ==> out.ready)

/// A memory read with the caller's own values carried through it.
///
/// The tag and the request's valid go in with the address and come back
/// attached to the word — which is the whole claim, and the reason the port
/// exists rather than the caller writing a register and remembering to. Nothing
/// here names a latency; `through` delays by whatever the port's depth is.
let carriedRead =
    design "CarriedRead" (fun () ->
        let waddr = input "waddr" 4
        let wdata = input "wdata" 8
        let wen = inputBit "wen"
        let raddr = input "raddr" 4
        let tag = input "tag" 8
        let ask = inputBit "ask"

        let store = blockMem "store" 4 8
        If wen (fun () -> memWrite store waddr wdata (lit 1UL 1))

        let read = memReadPort store raddr
        read.data ==> output "data" 8
        read.through "tag" tag ==> output "tag_out" 8
        read.through "ask" ask ==> outputBit "answered")

/// The same FIFO, deep enough that its words live in a block rather than in
/// LUTs — and that is the only thing that is different about it.
///
/// The source is character for character `bufferedStream` with one number
/// changed. Above the crossover the head becomes a synchronous read behind a
/// two-slot skid, which is a different circuit answering to the same `Stream`:
/// same capacity, same beat per cycle, same order. The pair exists so that
/// claim is a measurement rather than a design note — the check runs one model
/// against both.
let deepBufferedStream =
    design "DeepBufferedStream" (fun () ->
        let src =
            { payload = input "in_data" 8
              valid = inputBit "in_valid"
              ready = outputBit "in_ready"
              layout = layout1 ("data", 8) }

        let out = streamFifo "fifo" 128 src

        out.payload ==> output "out_data" 8
        out.valid ==> outputBit "out_valid"
        inputBit "out_ready" ==> out.ready)

let divideOperands = layout2 ("dividend", 8) ("divisor", 8)
let divideResults = layout2 ("quotient", 8) ("remainder", 8)
let divideContext = layout1 ("tag", 8)

/// A slow stage with the caller's data carried through it.
///
/// The divider takes operands and returns a quotient; it has never heard of a
/// tag. `withContext` puts whatever else the beat was carrying into a FIFO and
/// hands it back paired with the result — so a pipeline can send a value away
/// for eight cycles and still know, when it comes back, which pixel or request
/// it belonged to.
///
/// Without this, every component that costs cycles would grow its own
/// passthrough, and every caller would keep a shadow queue and hope the orders
/// lined up.
let taggedDivide =
    design "TaggedDivide" (fun () ->
        let src =
            { payload = (input "dividend" 8, input "divisor" 8), input "tag" 8
              valid = inputBit "in_valid"
              ready = outputBit "in_ready"
              layout = layoutJoin divideOperands divideContext }

        let out =
            withContext "dv" 4 divideOperands divideResults divideContext (divider "dv" 8) src

        let (quotient, remainder), tag = out.payload

        quotient ==> output "quotient" 8
        remainder ==> output "remainder" 8
        tag ==> output "tag_out" 8
        out.valid ==> outputBit "out_valid"
        inputBit "out_ready" ==> out.ready)

/// Four dividers in a farm, and every quotient still knows which request it is.
///
/// **The lanes are deliberately given unequal latencies** — lane `i` has `i`
/// extra buffer stages — because that is the case the arrangement exists for.
/// With identical workers a farm returns beats in very nearly issue order and a
/// plain queue would appear to work; unequal ones interleave heavily, and only
/// context that travels *with* its beat survives that.
///
/// No tags are needed even so. A farm owns both the dispatch and the merge, so
/// it knows which lane produced each beat and each lane carries its own context
/// in its own FIFO. Tags are for routing results back to independent clients,
/// which is `warpFu`.
let farmedDivide =
    design "FarmedDivide" (fun () ->
        let src =
            { payload = (input "dividend" 8, input "divisor" 8), input "tag" 8
              valid = inputBit "in_valid"
              ready = outputBit "in_ready"
              layout = layoutJoin divideOperands divideContext }

        let out =
            Stream.farmWith "div" 4 2 divideOperands divideResults divideContext
                (fun i -> divider $"dv%d{i}" 8 >> Stream.stages i id)
                src

        let (quotient, remainder), tag = out.payload

        quotient ==> output "quotient" 8
        remainder ==> output "remainder" 8
        tag ==> output "tag_out" 8
        out.valid ==> outputBit "out_valid"
        inputBit "out_ready" ==> out.ready)

let byteLayout = layout1 ("data", 8)
let coordLayout = layout2 ("x", 8) ("lum", 8)

/// Two handshake stages and a payload map, chained by nesting. The ready chain runs
/// backwards — sink to source — through ordinary forward function application,
/// because each Stream value carries the net its consumer must drive. Verified by
/// stream_tb.v: backpressure fills both stages, blocks the source, drains in order.
let streamPipe =
    design "StreamPipe" (fun () ->
        let stage = streamStageFor byteLayout
        let bump = satIncLogic 8
        streamOutput "out" (stage (streamMap bump (stage (streamInput "in" byteLayout)))))

/// A two-field payload through the same pipe shape — warp11's pixel-beat rule in
/// miniature: the beat carries its coordinate, so nothing infers position from a
/// cycle count. The map brightens `lum` and leaves `x` alone; because the payload
/// is a typed tuple, touching the wrong field is a compile error, not a name
/// lookup. coord_tb.v proves the fields stay associated under backpressure — the
/// thing the single-field test could not check.
let coordPipe =
    design "CoordPipe" (fun () ->
        let stage = streamStageFor coordLayout
        let brighten = satIncLogic 8

        streamOutput "out" (stage (streamMap (fun (x, lum) -> x, brighten lum) (stage (streamInput "in" coordLayout)))))

/// On/otherwise with nesting: clear beats enable, and the reg holds when neither
/// fires — the hold arm appears nowhere in the source, only in the folded Mux.
let onCounter =
    design "OnCounter" (fun () ->
        let enable = inputBit "enable"
        let clear = inputBit "clear"
        let count = output "count" 8
        let r = reg "r" 8

        If clear (fun () -> lit 0UL 8 ==> r)

        Else (fun () -> If enable (fun () -> r + lit 1UL 8 ==> r))

        r ==> count)

/// A defaulted wire under two sibling If blocks: last connect wins, so sel1
/// outranks sel0 — the classic priority mux, written as statements.
let onPriority =
    design "OnPriority" (fun () ->
        let sel0 = inputBit "sel0"
        let sel1 = inputBit "sel1"
        let a = input "a" 8
        let b = input "b" 8
        let c = input "c" 8
        let out = output "out" 8

        a ==> out
        If sel0 (fun () -> b ==> out)
        If sel1 (fun () -> c ==> out))

// ---------------------------------------------------------------------------
// The utility primitives, one toy apiece. These exist to be *read* — a stdlib
// entry whose use has to be reverse-engineered from a 2,000-line accelerator
// has not really shipped — and each is what the living checks drive, so the
// example cannot drift from the thing it demonstrates.

/// `lfsr`: pseudo-random stimulus from a shift and an xor. `step` gates it, so
/// the state holds while a consumer is busy, and `tap` is the one-bit dither a
/// caller usually actually wants.
let lfsrSource =
    design "LfsrSource" (fun () ->
        let step = inputBit "step"
        let state = output "state" 9
        let tap = outputBit "tap"

        let bits = lfsr "noise" 9 1UL step
        bits ==> state
        slice 0 0 bits ==> tap)

/// `oneHotLowest`: four requesters, and the lowest-numbered one that is asking
/// gets the grant — a fixed-priority arbiter, whole.
let oneHotScan =
    design "OneHotScan" (fun () ->
        let requests = [ for i in 0..3 -> inputBit $"request{i}" ]
        let grants = oneHotLowest requests

        for i in 0..3 do
            let g = outputBit $"grant{i}"
            grants[i] ==> g

        let any = outputBit "any"
        List.reduce (|||) requests ==> any)

/// `mux1H`: the value belonging to whichever grant is high. Pairs with the
/// arbiter above — that is the shape these two are almost always used in, one
/// picking the winner and the other fetching what the winner brought.
let mux1HSelect =
    design "Mux1HSelect" (fun () ->
        let requests = [ for i in 0..3 -> inputBit $"request{i}" ]
        let values = [ for i in 0..3 -> input $"value{i}" 8 ]
        let grants = oneHotLowest requests
        let winner = output "winner" 8
        mux1H grants values ==> winner)

/// `edgeDetect`: `enable` gates the *sample*, not the comparison, so an edge
/// that happens on a slow input is still there to be seen on the next enabled
/// cycle. Tie `enable` high for the plain form.
let edgeDetector =
    design "EdgeDetector" (fun () ->
        let enable = inputBit "enable"
        let signal = inputBit "signal"
        let edge = edgeDetect "sig" enable signal

        let rising = outputBit "rising"
        edge.rising ==> rising
        let falling = outputBit "falling"
        edge.falling ==> falling
        let changed = outputBit "changed"
        edge.changed ==> changed
        let previous = outputBit "previous"
        edge.previous ==> previous)

/// The bit-shape utilities in one place: `catAll` / `fill` / `reverse` /
/// `popCount` / `uintToOneHot` / `oneHotToUInt`. Each is a one-liner at the call
/// site, which is the whole argument for having them — every one of these was
/// otherwise a fold someone had to read twice.
///
/// `uintToOneHot` and `oneHotToUInt` are shown as the round trip they usually
/// are: an index out to a one-hot grant and back again.
let bitShapes =
    design "BitShapes" (fun () ->
        let a = input "a" 4
        let b = input "b" 4
        let flag = inputBit "flag"
        let index = input "index" 2

        let joined = output "joined" 8
        catAll [ a; b ] ==> joined

        // A one-bit signal filled to a mask, the usual reason to reach for it.
        let mask = output "mask" 4
        fill 4 flag ==> mask

        let flipped = output "flipped" 4
        reverse a ==> flipped

        let ones = output "ones" 3
        popCount a ==> ones

        let hot = uintToOneHot 4 index

        for i in 0..3 do
            let o = outputBit $"hot{i}"
            hot[i] ==> o

        // Back again: the round trip is the identity for any index in range.
        let recovered = output "recovered" 2
        oneHotToUInt hot ==> recovered)

/// `counter` and `counterTo`: the canonical use is a clock divider, and it shows
/// what the pair is for — `wrap` is the period-elapsed tick you build with, and
/// the count is incidental. The `phase` register flips on it and nothing reads
/// `count` at all except this design's ports.
///
/// The second counter takes its bound from a port instead of a constant, which
/// is the shape a runtime window wants (a program's instruction count, a row's
/// final column) and the one Chisel's compile-time-`n` `Counter` cannot express.
/// Note the conventions differ on purpose: `counter 6` counts six values, 0..5;
/// `counterTo last` counts up to and including `last`.
let dividers =
    design "Dividers" (fun () ->
        let enable = inputBit "enable"
        let last = input "last" 4

        let period = counter "period" 6 enable
        let phase = regBit "phase"
        If period.wrap (fun () -> bnot phase ==> phase)

        let divided = outputBit "divided"
        phase ==> divided
        let count = output "count" 3
        period.count ==> count
        let wrap = outputBit "wrap"
        period.wrap ==> wrap

        let window = counterTo "window" last enable
        let windowCount = output "window_count" 4
        window.count ==> windowCount
        let windowWrap = outputBit "window_wrap"
        window.wrap ==> windowWrap)

/// `Flow`: an unstoppable producer meeting a consumer that can stall, which is
/// the situation the type exists to make honest.
///
/// A free-running sample counter emits a beat every cycle `sample` is high —
/// nothing can tell it to wait, so it is a flow and not a stream. Giving it a
/// `ready` costs something, and `flowToStream` hands back exactly what: the
/// `overflowed` term, high on each cycle a beat was dropped. Here it is counted
/// into a register, which is the least a design should do with it.
///
/// The `flowStage` on the way in is the other half of the shape: registering a
/// flow is one register per field, because there is no stall to survive.
let flowSampler =
    design "FlowSampler" (fun () ->
        let sample = inputBit "sample"
        let takeReady = inputBit "out_ready"

        let counter = reg "counter" 8
        If sample (fun () -> counter + lit 1UL 8 ==> counter)

        let sampled =
            { payload = counter
              valid = sample
              layout = layout1 ("value", 8) }
            |> flowStage "staged"

        let stream, overflowed = flowToStream sampled
        takeReady ==> stream.ready

        let value = output "out_value" 8
        stream.payload ==> value
        let valid = outputBit "out_valid"
        stream.valid ==> valid

        // What the flow cost: beats the consumer was not there for.
        let droppedCount = reg "dropped_count" 8
        If overflowed (fun () -> droppedCount + lit 1UL 8 ==> droppedCount)
        let dropped = output "dropped" 8
        droppedCount ==> dropped)

/// The stages of `sequencer` below. States are values, so a transition names
/// something the compiler knows — and `%A` on the case is what the debugger
/// shows where the register holds 3.
type private Stage =
    | Idle
    | Fetch
    | Decode
    | Execute
    | Writeback
    | Done

/// A state machine as one declaration: `machine` owns the register, its width,
/// its encoding and the decode, and the states go in as values. What it emits is
/// what the hand-encoded form emits — `eq stage (lit k 3)` and `lit k 3 ==>
/// stage` — so the difference is entirely in what elaboration knows: that `Writeback`
/// is code 4, which the debugger prints, and that every state has a way in,
/// which finalize checks.
///
/// `stall` holds EXECUTE, so the interesting thing about a run is which state it
/// is sitting in rather than how many cycles have passed.
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

/// Structure generated by ordinary F#: a fold building a 4-deep pipeline of Delay8
/// instances. What every real warp11 design does (reduceTree, conv2d, the 104
/// lanes), exercised at spike scale.
let loopPipeline =
    design "LoopPipeline" (fun () ->
        let stage = delayOf 8
        let x = input "x" 8
        let out = output "out" 8
        List.fold (fun acc _ -> stage acc) x [ 1 .. 4 ] ==> out)

/// Recursion as a generator: eight inputs summed through a balanced adder
/// tree — the stdlib `reduceTree`'s oracle (the recursive-over-Exprs shape
/// this design carried locally until the library lifted it, 2026-08-05).
let treeSum =
    design "TreeSum8" (fun () ->
        let inputs = [ for i in 0..7 -> input $"x{i}" 8 ]
        let out = output "out" 11
        let widen x = cat (lit 0UL 3) x
        reduceTree (+) (List.map widen inputs) ==> out)

/// A 8x8 RAM with a write under On, a sync read and an async read. The oracle's
/// random 3-bit addresses collide constantly across 50 cycles, so read-first —
/// the semantics sim and silicon must agree on — is differentially exercised
/// rather than asserted.
let ramTest =
    design "RamTest" (fun () ->
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

/// A 256-word memory that fills itself: one word per cycle while `run` is high,
/// each holding 3× its own address plus one, so a wrong word is obvious by
/// inspection. The catalog's other memories are eight words deep, which is
/// small enough to read at a glance and therefore no test of anything that has
/// to *page* through a memory.
let fillingMemory =
    design "FillingMemory" (fun () ->
        let run = inputBit "run"
        let addr = output "addr" 8
        let word = output "word" 16

        let store = distributedMem "store" 8 16
        let ptr = reg "ptr" 8
        let wide = wire "wide" 16
        let value = wire "value" 16

        cat (lit 0UL 8) ptr ==> wide
        wide + wide + wide + lit 1UL 16 ==> value

        If run (fun () ->
            memWrite store ptr value (lit 1UL 1)
            ptr + lit 1UL 8 ==> ptr)

        ptr ==> addr
        (memReadPort store ptr).data ==> word)

/// Claims stated in the design and checked every cycle. Both are things a
/// saturating counter actually promises, and both can be broken on purpose —
/// `wrap` and driving `hold` with `enable` are the fault injections the negative
/// half of the check needs, because a claim that cannot fail proves nothing.
///
/// The first draft of this design asserted `not (255 < r)` on an 8-bit
/// register, which is constant-true — and Verilator's CMPCONST said so. That is
/// vacuity, the failure mode that makes a green assertion run meaningless, and
/// it took about a minute to write by accident.
let assertedCounter =
    design "AssertedCounter" (fun () ->
        let enable = inputBit "enable"
        let hold = inputBit "hold"
        let wrap = inputBit "wrap"
        let count = output "count" 8
        let previous = output "previous" 8

        let r = reg "r" 8
        let prev = reg "prev" 8
        let top = lit 255UL 8

        // Later If blocks outrank earlier ones, so wrap beats hold beats enable.
        If enable (fun () ->
            mux (eq r top) r (r + lit 1UL 8) ==> r
            // Says nothing about cycles where `enable` is low: the two controls
            // are only claimed to be exclusive while one of them is asserted.
            assertThat (bnot hold) "hold and enable asserted together")

        If hold (fun () -> top ==> r)
        If wrap (fun () -> lit 0UL 8 ==> r)

        r ==> prev

        // The saturation itself: a counter at its ceiling never comes back as
        // zero. True on every cycle, so it is stated unconditionally.
        assertThat (bnot (eq prev top &&& eq r (lit 0UL 8))) "counter wrapped past its ceiling"

        r ==> count
        prev ==> previous)

/// A claim that holds under *any* stimulus, which is what lets this design sit
/// in the differential: a saturating add never returns less than either operand,
/// because saturation only ever clamps upward. `assertedCounter` next door
/// cannot go in the oracle — its inputs exist to break its claims, and random
/// stimulus duly does.
///
/// What this proves is that assertions emit valid Verilog, survive the
/// translate_off region, and stay silent through thousands of random cycles in
/// both worlds at once.
let assertedSaturate =
    design "AssertedSaturate" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let out = output "out" 8

        let wide = wire "wide" 9
        cat (lit 0UL 1) a + cat (lit 0UL 1) b ==> wide

        let clamped = wire "clamped" 8
        saturate 8 wide ==> clamped

        assertThat (bnot (lt clamped a)) "saturating add returned less than its first operand"
        assertThat (bnot (lt clamped b)) "saturating add returned less than its second operand"

        clamped ==> out)

let cmdUnion = union2 (layout2 ("addr", 3) ("value", 8)) (layout1 ("addr", 3))

/// Most of the surface in one design: a union command stream driving a mem.
/// Set writes a value; Bump increments in place (async read + same-cycle write).
/// The two memWrites — one per variant arm — merge to a single priority write
/// site, with valid and tag folded into the enables by the condition stack.
let cmdProcessor =
    design "CmdProcessor" (fun () ->
        let store = distributedMem "store" 3 8
        let cmd = streamInput "cmd" (unionLayout cmdUnion)
        let raddr = input "raddr" 3
        let rdata = output "rdata" 8

        lit 1UL 1 ==> cmd.ready

        If cmd.valid (fun () ->
            matchUnion
                cmdUnion
                cmd.payload
                (fun (addr, value) -> memWrite store addr value (lit 1UL 1))
                (fun addr -> memWrite store addr (memRead store addr + lit 1UL 8) (lit 1UL 1)))

        (memReadPort store raddr).data ==> rdata)

/// Injection and re-extraction, round-tripped: build both variants, mux them,
/// land the data in a wire (the slice rule), and unslice the Set view.
let unionRoundTrip =
    design "UnionRoundTrip" (fun () ->
        let sel = inputBit "sel"
        let addr = input "addr" 3
        let value = input "value" 8
        let outTag = outputBit "out_tag"
        let outAddr = output "out_addr" 3
        let outValue = output "out_value" 8

        let setBeat = inject0 cmdUnion (addr, value)
        let bumpBeat = inject1 cmdUnion addr

        let tagWire = wireBit "t"
        let dataWire = wire "d" 11
        mux sel setBeat.tag bumpBeat.tag ==> tagWire
        mux sel setBeat.data bumpBeat.data ==> dataWire

        let viewAddr, viewValue = variant0 cmdUnion dataWire
        tagWire ==> outTag
        viewAddr ==> outAddr
        viewValue ==> outValue)

/// Fork then join: every beat splits into a bumped copy and a plain copy, which
/// round-robin back into one stream — each input beat yields exactly two output
/// beats. fork_tb.v walks a beat through fill, stall and drain by hand; the
/// oracle randomizes it.
let forkJoin =
    design "ForkJoin" (fun () ->
        let stage = streamStageFor byteLayout
        let source = streamInput "in" byteLayout

        match streamBroadcast 2 source with
        | [ s1; s2 ] ->
            let bumped = stage (streamMap (satIncLogic 8) s1)
            let plain = stage s2
            streamOutput "out" (streamMergeTree [ bumped; plain ])
        | _ -> failwith "replicate 2 gave the wrong arity")

/// Every signed operation at its own port: wraparound subtract, sign-extending
/// multiply, both compare orders, arithmetic shift. Random 8-bit stimulus sets
/// the sign bit half the time, so the two's-complement boundary patterns (0x80,
/// 0xFF) reach the oracle without being enumerated.
let signedOps =
    design "SignedOps" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let diff = output "diff" 8
        let product = output "product" (SInt 16)
        let below = outputBit "below"
        let belowSigned = outputBit "below_signed"
        let shifted = output "shifted" 8

        a - b ==> diff
        mul (asSInt a) (asSInt b) ==> product
        lt a b ==> below
        lt (asSInt a) (asSInt b) ==> belowSigned
        sra 3 a ==> shifted)

/// Bitwise xor at its three shapes: plain combinational, the rotate-xor
/// mixing pattern xoshiro leans on (slice + cat + xor), and a xor-accumulating
/// register. Xor is a new IR case, so this design is the toolchain's test
/// input — its stimulus reaches the emitter, both Sim paths and Verilator.
let xorOps =
    design "XorOps" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let mixed = output "mixed" 8
        let rotated = output "rotated" 8
        let acc = output "acc" 8

        (a ^^^ b) ==> mixed
        // ROTR(a, 3) ^ b — {a[2:0], a[7:3]} is the rotate, xor is the mix.
        (cat (slice 2 0 a) (slice 7 3 a)) ^^^ b ==> rotated
        let r = reg "r" 8
        r ^^^ (a &&& b) ==> r
        r ==> acc)

/// The saturate/shift sugar at ports: unsigned and signed saturating narrows,
/// a computed sum saturated through a wire (the ALU shape), the widening left
/// shift and the narrowing right shift. Sugar over mux/lt/slice/cat — no
/// new IR — so the differential proves the desugaring, not the toolchain.
let satOps =
    design "SatOps" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let narrowU = output "narrow_u" 4
        let narrowS = output "narrow_s" (SInt 4)
        let sumU = output "sum_u" 8
        let shifted = output "shifted" 12
        let high = output "high" 5

        saturate 4 a ==> narrowU
        // The same eight bits, read the other way — one `saturate`, two clamps.
        saturate 4 (asSInt a) ==> narrowS
        let sum = wire "sum" 9
        (cat (lit 0UL 1) a) + (cat (lit 0UL 1) b) ==> sum
        saturate 8 sum ==> sumU
        shl 4 a ==> shifted
        shr 3 a ==> high)

/// Mandelbrot's inner step at toy scale — Q4.4 in 8 bits. The coordinates are
/// declared signed, so the squares and the cross term are a plain `mul` on
/// declared signals; the Q renormalization is a plain
/// slice of the product wire (bits [11:4] of a Q8.8 value are its Q4.4
/// truncation — arithmetic shift and narrowing in one part-select, which is why
/// the pod path needs no Sra of its own); escape is an *unsigned* compare,
/// because squares are non-negative. |z| past the representable range wraps, as
/// it would in fabric without the bailout the real pod carries.
let escapeStep =
    design "EscapeStep" (fun () ->
        let zx = input "zx" (SInt 8)
        let zy = input "zy" (SInt 8)
        let cx = input "cx" (SInt 8)
        let cy = input "cy" (SInt 8)
        let escape = outputBit "escape"
        let nextZx = output "next_zx" 8
        let nextZy = output "next_zy" 8

        let zx2 = wire "zx2" (SInt 16)
        let zy2 = wire "zy2" (SInt 16)
        // xy, not cross: `cross` is an SV reserved word, and the spike has no
        // keyword check at elaboration (Kotlin grew one for exactly this).
        let xy = wire "xy" (SInt 16)
        mul zx zx ==> zx2
        mul zy zy ==> zy2
        mul zx zy ==> xy

        // Unsigned on purpose, and now said rather than only commented: squares
        // are non-negative, so the cheaper compare is correct until Q8.8 wraps.
        // `escapeStepFixed` reads it signed, and the pair diverging exactly
        // there is what that design exists to show.
        let magnitude = wire "magnitude" 16
        asUInt (zx2 + zy2) ==> magnitude
        lt (lit 1024UL 16) magnitude ==> escape // 4.0 in Q8.8

        let zReal = wire "z_real" (SInt 16)
        zx2 - zy2 ==> zReal
        // `asSInt` because a slice lands in UInt by FIRRTL's rule and these
        // renormalizations are signed — the coordinate they add to says so.
        asSInt (slice 11 4 zReal) + cx ==> nextZx
        asSInt (slice 10 3 xy) + cy ==> nextZy) // bits [10:3]: the doubling absorbed into the renorm

/// `escapeStep` rewritten on the Fixed layer — same wires, same order, and the
/// arithmetic emits byte-identical Verilog (mainDemo asserts it): the types
/// compile away entirely. The one deliberate divergence is the escape compare:
/// `Number.lessThan` is signed where the hand-written design chose the unsigned trick, so
/// the two disagree exactly where Q8.8 wraps (|z|² ≥ 128.0) — each
/// self-consistent under the oracle. The doubling in nextZy is `reinterpret`
/// (same bits, one fewer fraction bit = ×2, zero hardware) absorbed by the next
/// renormTo's part-select, landing on the identical slice [10:3].
let escapeStepFixed =
    design "EscapeStepFixed" (fun () ->
        let zx = Number.input "zx" Number.q4_4
        let zy = Number.input "zy" Number.q4_4
        let cx = Number.input "cx" Number.q4_4
        let cy = Number.input "cy" Number.q4_4
        let escape = outputBit "escape"
        let nextZx = output "next_zx" 8
        let nextZy = output "next_zy" 8

        let zx2 = Number.wire "zx2" (zx * zx)
        let zy2 = Number.wire "zy2" (zy * zy)
        let xy = Number.wire "xy" (zx * zy)

        let magnitude = Number.wire "magnitude" (zx2 + zy2)
        Number.lessThan (Number.constant Number.q8_8 4.0) magnitude ==> escape

        let zReal = Number.wire "z_real" (zx2 - zy2)
        (Number.renormTo Number.q4_4 zReal + cx) ==> nextZx
        (Number.renormTo Number.q4_4 (Number.reinterpret Number.q9_7 xy) + cy) ==> nextZy)

/// The same step at Mandelbrot's real precision, Q4.28 — the format the pod
/// will run. Products are Q8.56 in exactly 64 bits, so this design sits on the
/// narrow Sim's ceiling on purpose: the boundary the mini pod needs is
/// differentially exercised here, not discovered there.
let escapeStep28 =
    design "EscapeStep28" (fun () ->
        let zx = Number.input "zx" Number.q4_28
        let zy = Number.input "zy" Number.q4_28
        let cx = Number.input "cx" Number.q4_28
        let cy = Number.input "cy" Number.q4_28
        let escape = outputBit "escape"
        let nextZx = output "next_zx" 32
        let nextZy = output "next_zy" 32

        let zx2 = Number.wire "zx2" (zx * zx)
        let zy2 = Number.wire "zy2" (zy * zy)
        let xy = Number.wire "xy" (zx * zy)

        let magnitude = Number.wire "magnitude" (zx2 + zy2)
        Number.lessThan (Number.constant Number.q8_56 4.0) magnitude ==> escape

        let zReal = Number.wire "z_real" (zx2 - zy2)
        (Number.renormTo Number.q4_28 zReal + cx) ==> nextZx
        (Number.renormTo Number.q4_28 (Number.reinterpret Number.q9_55 xy) + cy) ==> nextZy)

/// The AXI-Lite slave under the oracle: a scratch RW register, an ID constant,
/// a live counter, and a 4-word mem window fed by a free-running writer, in a
/// 6-bit aperture — carried by `axiClock`, so the AXI-named active-low clock
/// pair is exercised here too (the testbench asserts reset low, the emitter
/// derives the internal active-high wire). Random pokes of all five channels
/// exercise accept, decode, readback and the window against Verilator —
/// equivalence, not protocol; a real master's wait states are the FsSimWindow
/// bridge's job (the seam, notes/FINDINGS.md).
let axiScratch =
    designClocked axiClock "AxiScratch" (fun () ->
        let tick = reg "tick" 8
        tick + lit 1UL 8 ==> tick

        let probe = distributedMem "probe" 2 8
        memWrite probe (slice 1 0 tick) tick (lit 1UL 1)

        axiLiteSlave
            6
            [ "scratch", 0x00UL, 32 ]
            [ 0x04UL, lit 0x11FA57UL 32; 0x08UL, tick ]
            [ 0x20UL, probe ]
        |> ignore)

/// Two windows and a register bank answering one AR channel.
///
/// The point is that the host cannot tell how many sources there are: one
/// aperture, one handshake, and each read lands in whichever source owns that
/// address. `even` and `odd` hold values a check can tell apart from each other
/// *and* from the registers, because a window that answered its neighbour's
/// range would otherwise look exactly like a working one.
let twoWindowSlave =
    design "TwoWindowSlave" (fun () ->
        let tick = reg "tick" 8
        (tick + lit 1UL 8) ==> tick

        // Each window holds a value derived from its own index, and the two
        // derivations differ — so a read answered by the wrong window is wrong
        // in a way the check sees, not merely off by a bit.
        let even = distributedMem "even" 2 8
        let odd = distributedMem "odd" 2 8
        let idx = wire "idx" 2
        slice 1 0 tick ==> idx
        memWrite even idx (cat (lit 0UL 4) (cat idx (lit 0UL 2))) (lit 1UL 1)
        memWrite odd idx (cat (lit 0xAUL 4) (cat idx (lit 1UL 2))) (lit 1UL 1)

        axiLiteSlave
            6
            [ "scratch", 0x00UL, 32 ]
            [ 0x04UL, lit 0x11FA57UL 32 ]
            [ 0x10UL, even; 0x20UL, odd ]
        |> ignore)

/// A byte-enabled memory: the write reaches only the lanes its strobe selects.
///
/// Four 8-bit lanes in a 32-bit word — AXI's `wstrb`, and the shape a
/// synthesiser turns into one block RAM with four write-enables rather than
/// read-modify-write logic. Each lane carries a different byte of `wdata` so a
/// strobe that reached the wrong lane is visible, and the word is read back
/// whole: what the check is really asserting is that the lanes the strobe left
/// alone still hold what a *previous* write put there.
let maskedWrite =
    design "MaskedWrite" (fun () ->
        let waddr = input "waddr" 3
        let wdata = input "wdata" 32
        let wstrb = input "wstrb" 4
        let wen = inputBit "wen"
        let raddr = input "raddr" 3

        let store = blockMem "store" 3 32
        memWriteMasked store waddr wdata wen wstrb

        (memReadPort store raddr).data ==> output "rdata" 32)

/// The masked write at a width no uint64 can hold: a 128-bit word in four
/// 32-bit lanes.
///
/// Same contract as `maskedWrite`, and deliberately the same shape — what this
/// design exists to exercise is the simulator's *wide* memory store (BigInteger
/// words, BigInteger keep masks), which the 64-bit toy structurally cannot
/// reach. It is the shape GEP's merged tables take, proven here first.
let maskedWriteWide =
    design "MaskedWriteWide" (fun () ->
        let waddr = input "waddr" 3
        let wdata = input "wdata" 128
        let wstrb = input "wstrb" 4
        let wen = inputBit "wen"
        let raddr = input "raddr" 3

        let store = blockMem "store" 3 128
        memWriteMasked store waddr wdata wen wstrb

        (memReadPort store raddr).data ==> output "rdata" 128)

/// A read channel that waits three cycles, so the busy flag has something to do.
///
/// No read source in the tree costs more than one cycle yet, so the deep path
/// would otherwise be code nothing runs. A channel asked to wait longer than its
/// sources need is *correct* — the word is ready early and sampled late — and it
/// exercises exactly the machinery a slow source will need: the walk from accept
/// to answer, and the flag that holds AR off across the gap it opens.
///
/// Without that flag the gap is wide open: RVALID is still low, so the ordinary
/// `bnot rvalidR` guard says "idle", a second AR is accepted on top of the
/// first, and since there is one held address and one RDATA the host is answered
/// twice with whichever word won.
let deepChannelSlave =
    design "DeepChannelSlave" (fun () ->
        let ch = axiLiteChannel 6 3

        let scratch = reg "scratch" 32
        If (ch.writeFire &&& eq ch.awWord (lit 0UL 4)) (fun () -> ch.wdata ==> scratch)

        let readWord = (ch.beginRead ()).word

        mux (eq readWord (lit 0UL 4)) scratch (mux (eq readWord (lit 1UL 4)) (lit 0xC0FFEEUL 32) (lit 0UL 32))
        ==> ch.rdata)

/// The pipelined read channel: four transactions in flight, a response every
/// cycle behind the first.
///
/// Two sources at the channel's contract — the answer arrives exactly
/// `answersAfter` after the accept. The memory read port does that naturally;
/// the register word gets one capture register, because a combinational mux
/// evaluated when the *next* address is already presented would answer about
/// the wrong transaction. That capture is the whole discipline pipelining
/// imposes, stated once here.
let pipelinedReadSlave =
    design "PipelinedReadSlave" (fun () ->
        let ch = axiLiteChannelPipelined 6 1 4

        let scratch = reg "scratch" 32
        If (ch.writeFire &&& eq ch.awWord (lit 0UL 4)) (fun () -> ch.wdata ==> scratch)

        let lookup = distributedMem "lookup" 3 32
        memWrite lookup (slice 2 0 ch.awWord) ch.wdata (ch.writeFire &&& eq (slice 3 3 ch.awWord) (lit 1UL 1))

        // The register word, captured at present time so it answers about the
        // address that was on the wire, not the one that replaced it.
        let regWord = reg "reg_word" 32
        mux (eq ch.word (lit 0UL 4)) scratch (lit 0x11C0DEUL 32) ==> regWord

        let tableWord = (memReadPort lookup (slice 2 0 ch.word)).data
        let inTable = delayChain "in_table" 1 1 (slice 3 3 ch.word)

        mux inTable tableWord regWord ==> ch.answer)

/// Two windows over the same words. A function, not a value: the point is that
/// elaboration refuses, and a swallowed window is silent otherwise — the fold
/// picks one and the host reads plausible words from the wrong memory.
let onOverlappingWindows () =
    design "OnOverlappingWindows" (fun () ->
        let a = distributedMem "winA" 3 8
        let b = distributedMem "winB" 2 8
        axiLiteSlave 6 [] [] [ 0x00UL, a; 0x10UL, b ] |> ignore)

/// A register underneath a window. The window answers reads at that address, so
/// the register would be write-only and silently so.
let onRegisterInsideWindow () =
    design "OnRegisterInsideWindow" (fun () ->
        let w = distributedMem "win" 2 8
        axiLiteSlave 6 [ "buried", 0x14UL, 8 ] [] [ 0x10UL, w ] |> ignore)

/// A wire conditionally assigned with no default — the error warp11's rule
/// promises. A function, not a value: the failure happens at elaboration.
let onBadWire () =
    design "OnBadWire" (fun () ->
        let enable = inputBit "enable"
        let out = output "out" 8
        If enable (fun () -> lit 1UL 8 ==> out))

/// Dematerialize four fields onto one bus and materialize them straight back.
/// The point of a transporter is that the two directions cannot disagree, so
/// the round trip is its oracle: any offset or field-order slip shows up as a
/// field arriving as some other field's bits. Deliberately ragged widths — a
/// 5-bit first field means every later offset is unaligned, which is exactly
/// where hand-written slices go wrong.
let transporterRoundTrip =
    design "TransporterRoundTrip" (fun () ->
        let t = transporter (layout4 ("a", 5) ("b", 32) ("c", 7) ("d", 32))
        let a = input "a" 5
        let b = input "b" 32
        let c = input "c" 7
        let d = input "d" 32

        let bus = wire "bus" t.width
        t.dematerialize (a, b, c, d) ==> bus

        let a2, b2, c2, d2 = t.materialize bus
        let outA = output "outA" 5
        a2 ==> outA
        let outB = output "outB" 32
        b2 ==> outB
        let outC = output "outC" 7
        c2 ==> outC
        let outD = output "outD" 32
        d2 ==> outD)

/// Two unconditional drivers on one wire. Exists to prove `Assign` fires; before
/// it, the scope was last-connect-wins all the way to the call site, so the first
/// assign vanished with no error at elaboration, lint or synthesis.
let doubleAssign () =
    design "DoubleAssign" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let out = output "out" 8
        a ==> out
        b ==> out)

/// A stream created and never consumed — its ready has no driver. Exists to prove
/// `checkStreams` fires; before it, this emitted an undriven output port and only
/// Verilator noticed.
let danglingStream =
    design "Dangling" (fun () -> streamInput "in" byteLayout |> ignore)

/// An 8-bit output driven by a 16-bit expression. Exists to prove `checkWidths`
/// now *gates* emission: it always reported this, but `emitDesign` did not
/// consult it, so the demo printed the violation as one line among forty and
/// the emitted Verilog truncated silently — `axiPulse`'s pulse counter is the
/// case that measured it, where only Verilator objected.
let widthViolation =
    design "WidthViolation" (fun () ->
        let a = input "a" 8
        let out = output "out" 8
        a + lit 1UL 16 ==> out)

/// Two different modules claiming the name `Mul8`. Exists to prove `checkNames`
/// fires — before it, the emitter silently kept the first and dropped the other.
let nameCollision =
    design "Collision" (fun () ->
        let realMultiply = mulOf 8
        let impostor = liftBinary (fnModule2 "Mul8" ("a", 8) ("b", 8) "product" (+))

        let a = input "a" 8
        let b = input "b" 8
        let out = output "out" 16

        realMultiply (impostor a b) b ==> out)


/// The full-scale pod's egress shape at oracle scale: a 128-bit beat assembled
/// by shifting bytes in (the coalescer's move), sliced back out narrow, muxed
/// and compared wide — the Sim's BigInteger path differentially exercised on
/// every op the egress needs (Ref, Concat, Slice, Mux, Eq past 64 bits), plus
/// the wide-testbench stimulus itself (64-bit input ports draw from the
/// chunked generator and travel as hex).
let wideBeat =
    design "WideBeat" (fun () ->
        let byteIn = input "byte_in" 8
        let shiftEn = inputBit "shift_en"
        let sel = inputBit "sel"
        let cmpHi = input "cmp_hi" 64
        let cmpLo = input "cmp_lo" 64
        let outHi = output "out_hi" 8
        let outMid = output "out_mid" 64
        let beatEq = outputBit "beat_eq"

        let beat = reg "beat" 128
        If shiftEn (fun () -> cat (slice 119 0 beat) byteIn ==> beat)

        let cmp = wire "cmp" 128
        cat cmpHi cmpLo ==> cmp
        let picked = wire "picked" 128
        mux sel beat cmp ==> picked
        slice 127 120 picked ==> outHi
        slice 95 32 picked ==> outMid
        eq beat cmp ==> beatEq)

/// Sign extension under the oracle: narrow→narrow, the summed pair (the step
/// cone's actual use — sign-extended c added to Q-recovered products), and
/// narrow→wide (a 72-bit port, so the op is exercised on the BigInteger path
/// too). Random 8-bit stimulus sets the sign bit half the time.
let widenOps =
    design "WidenOps" (fun () ->
        let a = input "a" 8
        let b = input "b" 8
        let wide12 = output "wide12" 12
        let sum13 = output "sum13" 13
        let wide72 = output "wide72" 72

        signExtend 12 a ==> wide12
        signExtend 13 a + signExtend 13 b ==> sum13
        signExtend 72 a ==> wide72)

/// Dispatch round-trip: each beat goes to exactly ONE branch — bumped in one,
/// untouched in the other — then round-robins back, so which lane took a beat
/// is visible in the payload. Contrast forkJoin, where every beat takes both
/// branches. The oracle's random ready/valid exercises the priority
/// arbitration and the OR'd source ready.
let dispatchRoundTrip =
    design "DispatchRoundTrip" (fun () ->
        let stage = streamStageFor byteLayout
        let source = streamInput "in" byteLayout

        match streamBalance 2 source with
        | [ s1; s2 ] ->
            let bumped = stage (streamMap (satIncLogic 8) s1)
            let plain = stage s2
            streamOutput "out" (streamMergeTree [ bumped; plain ])
        | _ -> failwith "dispatch 2 gave the wrong arity")

/// The clustered pair at 4 lanes: 2 clusters of 2 with a register stage at
/// each cluster node on both sides — the frame pod's Shape.Auto composition at
/// oracle scale. Each lane adds its own index, so routing is visible in the
/// payload.
let clusteredRoundTrip =
    design "ClusteredRoundTrip" (fun () ->
        let stage = streamStageFor byteLayout
        let source = streamInput "in" byteLayout

        let lanes =
            source
            |> wormholeOut Balance 4 (fun i lane -> stage (streamMap (fun d -> d + lit (uint64 i) 8) lane))

        streamOutput "out" (wormholeIn 0 id lanes))

/// Module A of the two-consumer case: one input stream routed onto TWO separate
/// output streams by each beat's top bit — a router, not a fork; every beat
/// lands on exactly one side. Two streams out of one module is nothing special:
/// the apply returns a pair, one Stream per boundary port trio.
let private byteSplitter =
    defineModule
        "ByteSplitter"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "low_data" 8,
             p.outPort "low_valid" 1,
             p.inPort "low_ready" 1,
             p.outPort "high_data" 8,
             p.outPort "high_valid" 1,
             p.inPort "high_ready" 1))
        (fun m (inData, inValid, inReady, lowData, lowValid, lowReady, highData, highValid, highReady) (s: Stream<Expr>) ->
            s.payload ==> inData
            s.valid ==> inValid
            inReady ==> s.ready
            m.RegisterStreamReady lowReady
            m.RegisterStreamReady highReady

            ({ payload = lowData
               valid = lowValid
               ready = lowReady
               layout = byteLayout },
             { payload = highData
               valid = highValid
               ready = highReady
               layout = byteLayout }))
        (fun (inData, inValid, inReady, lowData, lowValid, lowReady, highData, highValid, highReady) _ ->
            let isHigh = wireBit "is_high"
            slice 7 7 inData ==> isHigh
            inData ==> lowData
            inData ==> highData
            (inValid &&& bnot isHigh) ==> lowValid
            (inValid &&& isHigh) ==> highValid
            mux isHigh highReady lowReady ==> inReady)


/// Test case 1 of the connect-layer discussion: module A produces two separate
/// streams, each consumed by its own chain — `Stream.stage f` registers the
/// transformed beat (the word `stage` is what buys the flop). The layout rides
/// the stream from its creation site, so the chains name nothing but the
/// boundary ports. The two results leave the design separately, so the
/// oracle's random ready/valid backpressures the chains independently.
let twoStreamSplit =
    design "TwoStreamSplit" (fun () ->
        let low, high = instanceNamed "split" byteSplitter (Stream.input "in" byteLayout)
        low |> Stream.stage satInc |> Stream.out "b_out"
        high |> Stream.stage (fun d -> lit 0xFFUL 8 - d) |> Stream.out "c_out")

/// An instance's staging wires land in the PARENT's namespace: instance `b`
/// wired to a child port `low_data` stages `b_low_data`, which is also what
/// `Stream.out "b_low"` names its data port. Exists to prove the declaration
/// check fires — before it, the name was declared twice (once as a port, once
/// as a wire), the emitted Verilog redeclared the port as a wire and
/// self-assigned it, and nothing complained at elaboration, lint or synthesis.
/// Found writing `twoStreamSplit`, whose instance is named `split` for this
/// reason.
let declCollision () =
    design "DeclCollision" (fun () ->
        let low, high = instanceNamed "b" byteSplitter (Stream.input "in" byteLayout)
        low |> Stream.out "b_low"
        high |> Stream.out "c_out")

/// Test case 2: the WarpCPU shape — one stream splits by opcode (here the top
/// bit), the two paths run through pipelines of DIFFERENT depths, and an
/// arbitrated join funnels them back into one result stream. The join is
/// arbitration, not reordering: with unequal latencies results leave in
/// completion order, so the payload carries its identity (the pixel-beat
/// rule) — here the value range says which path a beat took.
let twoStreamSplitReplicateJoin =
    design "TwoStreamSplitReplicateJoin" (fun () ->
        let lowDepth = 5
        let highDepth = 10
        let low, high = instanceNamed "split" byteSplitter (Stream.input "in" byteLayout)
        let lowPath = low |> Stream.stages lowDepth satInc
        let highPath = high |> Stream.stages highDepth (fun d -> lit 0xFFUL 8 - d)
        [ lowPath; highPath ] |> Stream.merge |> Stream.out "final_out")

/// The frame-processor shape at toy scale: ONE command beat in, `rows` run
/// beats out (base, base+1, …), then ready for the next command — a stateful
/// beat expander, which is a 1→1 stream stage no matter how many beats it
/// mints. FramePod's row-run generator, extracted.
let private rowExpander rows =
    defineModule
        $"RowExpander%d{rows}"
        (fun p ->
            (p.inPort "cmd_data" 8,
             p.inPort "cmd_valid" 1,
             p.outPort "cmd_ready" 1,
             p.outPort "run_data" 8,
             p.outPort "run_valid" 1,
             p.inPort "run_ready" 1))
        (fun m (cmdData, cmdValid, cmdReady, runData, runValid, runReady) (s: Stream<Expr>) ->
            s.payload ==> cmdData
            s.valid ==> cmdValid
            cmdReady ==> s.ready
            m.RegisterStreamReady runReady

            { payload = runData
              valid = runValid
              ready = runReady
              layout = byteLayout })
        (fun (cmdData, cmdValid, cmdReady, runData, runValid, runReady) _ ->
            let busy = regBit "busy"
            let baseReg = reg "base_v" 8
            let row = reg "row" 8

            bnot busy ==> cmdReady
            busy ==> runValid
            baseReg + row ==> runData

            If (cmdValid &&& bnot busy) (fun () ->
                lit 1UL 1 ==> busy
                cmdData ==> baseReg
                lit 0UL 8 ==> row)

            Else (fun () ->
                If (busy &&& runReady) (fun () ->
                    row + lit 1UL 8 ==> row
                    If (eq row (lit (uint64 (rows - 1)) 8)) (fun () -> lit 0UL 1 ==> busy))))

/// The row-gatherer shape at toy scale: consume every result beat, fold it
/// into state (a sum — order-insensitive, because the farm reorders), count,
/// and raise `frame_done` when a frame's worth has landed. Completion lives
/// where the results land — FramePod's written-count FSM, extracted.
let private beatGatherer rows =
    defineModule
        $"BeatGatherer%d{rows}"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "gathered" 8,
             p.outPort "beat_count" 16,
             p.outPort "frame_done" 1))
        (fun m (inData, inValid, inReady, gathered, beatCount, frameDone) (s: Stream<Expr>) ->
            s.payload ==> inData
            s.valid ==> inValid
            inReady ==> s.ready
            (gathered, beatCount, frameDone))
        (fun (inData, inValid, inReady, gathered, beatCount, frameDone) _ ->
            let sum = reg "sum" 8
            let count = reg "count" 16

            lit 1UL 1 ==> inReady

            If inValid (fun () ->
                sum + inData ==> sum
                count + lit 1UL 16 ==> count)

            sum ==> gathered
            count ==> beatCount
            eq count (lit (uint64 rows) 16) ==> frameDone)

/// A worker that GRINDS: accepts a beat, is busy `cycles` cycles, then offers
/// the bumped result — throughput 1/(cycles+2), the rowProcessor reality. A
/// pipelined stage never needs replication (1 beat/cycle already); this is
/// the shape whose farm width is worth sweeping.
let private slowWorker cycles =
    defineModule
        $"SlowWorker%d{cycles}"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_data" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun m (inData, inValid, inReady, outData, outValid, outReady) (s: Stream<Expr>) ->
            s.payload ==> inData
            s.valid ==> inValid
            inReady ==> s.ready
            m.RegisterStreamReady outReady

            { payload = outData
              valid = outValid
              ready = outReady
              layout = byteLayout })
        (fun (inData, inValid, inReady, outData, outValid, outReady) _ ->
            let busy = regBit "busy"
            let held = reg "held" 8
            let remaining = reg "remaining" 8

            let emit = wireBit "emit"
            (busy &&& eq remaining (lit 0UL 8)) ==> emit

            bnot busy ==> inReady
            emit ==> outValid
            held ==> outData

            If (busy &&& bnot emit) (fun () -> remaining - lit 1UL 8 ==> remaining)
            If (emit &&& outReady) (fun () -> lit 0UL 1 ==> busy)

            If (inValid &&& bnot busy) (fun () ->
                lit 1UL 1 ==> busy
                satInc inData ==> held
                lit (uint64 cycles) 8 ==> remaining))

/// Test case 3: the decomposed frame pipeline — the FramePod refactor shape,
/// proven at toy scale. Command source, beat expander, a farm of three
/// workers with UNEQUAL depths (1/2/3 stages), and a gatherer that owns
/// completion. Every link is `|>` — the 1→1 wormhole IS reverse application —
/// and the two multiplicity changes live inside `farm`, invisible here.
let framePipeline =
    design "FramePipeline" (fun () ->
        let rows = 4

        let gathered, beatCount, frameDone =
            Stream.input "cmd" byteLayout
            |> instanceNamed "expand" (rowExpander rows)
            |> Stream.farm 3 (fun i lane -> lane |> Stream.stages (i + 1) satInc)
            |> instanceNamed "gather" (beatGatherer rows)

        let gatheredOut = output "gathered" 8
        gathered ==> gatheredOut
        let beatCountOut = output "beat_count" 16
        beatCount ==> beatCountOut
        let frameDoneOut = outputBit "frame_done"
        frameDone ==> frameDoneOut)

/// Test case 4: the design-space sweep. The SAME pipeline, elaborated at a
/// worker count passed as data — so finding the right width is a plain loop
/// over configs reading the probes (see the sweep in Main). `runs` blocked
/// means every worker was busy (add workers); `results` starved means the
/// farm cannot feed the sink (add workers); `runs` starved at the same width
/// means the expander is the wall instead.
let sweepPipeline nWorkers =
    design $"SweepPipeline_w%d{nWorkers}" (fun () ->
        let rows = 8

        let gathered, beatCount, frameDone =
            Stream.input "cmd" byteLayout
            |> Stream.pipeline
                [ Stream.spec "expand" (rowExpander rows)
                  Stream.spec "worker" (slowWorker 3) |> Stream.lanes nWorkers |> Stream.probed "runs" ]
            |> Stream.probe "results"
            |> instanceNamed "gather" (beatGatherer rows)

        let gatheredOut = output "gathered" 8
        gathered ==> gatheredOut
        let beatCountOut = output "beat_count" 16
        beatCount ==> beatCountOut
        let frameDoneOut = outputBit "frame_done"
        frameDone ==> frameDoneOut)

/// Byte → pair: the payload TYPE changes across this stage (Expr becomes
/// Expr * Expr), which is what the arity-typed pipelines exist for.
let private widenStage =
    defineModule
        "WidenPair"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_a" 8,
             p.outPort "out_b" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun m (inData, inValid, inReady, outA, outB, outValid, outReady) (s: Stream<Expr>) ->
            s.payload ==> inData
            s.valid ==> inValid
            inReady ==> s.ready
            m.RegisterStreamReady outReady

            { payload = (outA, outB)
              valid = outValid
              ready = outReady
              layout = layout2 ("a", 8) ("b", 8) })
        (fun (inData, inValid, inReady, outA, outB, outValid, outReady) _ ->
            inData ==> outA
            satInc inData ==> outB
            inValid ==> outValid
            outReady ==> inReady)

/// Pair → byte, the narrowing half — REGISTERED, because a farm worker is an
/// async boundary: a fully combinational worker couples the dispatch grant to
/// the merge arbitration into a valid/ready loop, which elaboration rejects
/// (the loop check caught exactly that when this stage was first written
/// combinational).
let private sumStage =
    defineModule
        "PairSum"
        (fun p ->
            (p.inPort "in_a" 8,
             p.inPort "in_b" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_sum" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun m (inA, inB, inValid, inReady, outSum, outValid, outReady) (s: Stream<Expr * Expr>) ->
            let a, b = s.payload
            a ==> inA
            b ==> inB
            s.valid ==> inValid
            inReady ==> s.ready
            m.RegisterStreamReady outReady

            { payload = outSum
              valid = outValid
              ready = outReady
              layout = layout1 ("sum", 8) })
        (fun (inA, inB, inValid, inReady, outSum, outValid, outReady) _ ->
            let sumR = reg "sumR" 8
            let validR = regBit "validR"
            (bnot validR ||| outReady) ==> inReady
            mux inReady (inA + inB) sumR ==> sumR
            mux inReady inValid validR ==> validR
            sumR ==> outSum
            validR ==> outValid)

/// The type-changing pipeline under the oracle: byte → pair → sum through
/// `pipeline2`, with the narrowing stage FARMED (2 lanes) — a spec farms
/// across a payload-type change exactly as it does within one.
let typedPipeline =
    design "TypedPipeline" (fun () ->
        Stream.input "in" byteLayout
        |> Stream.pipeline2
            (Stream.spec "widen" widenStage)
            (Stream.spec "sum" sumStage |> Stream.lanes 2 |> Stream.probed "pairs")
        |> Stream.out "out")

/// A probed link under the oracle: the counters ride output ports, so the
/// blocked/starved semantics are differentially verified against Verilator
/// under random ready/valid, not just asserted in the Sim.
let probedPipe =
    design "ProbedPipe" (fun () ->
        let stage = streamStageFor byteLayout
        let source, counters = streamProbeCounters "ingress" (streamInput "in" byteLayout)
        streamOutput "out" (stage source)

        let blockedCount = output "blocked_count" 32
        let starvedCount = output "starved_count" 32
        counters.blocked ==> blockedCount
        counters.starved ==> starvedCount)

/// The AXI master's pointer ring under the oracle: 128-bit beats, 4 slots.
/// The testbench's random awready/wready/bvalid stand in for the interconnect,
/// so protocol-state equivalence is checked under adversarial slave timing —
/// including illegal timing (spurious bvalid), where both implementations must
/// still agree state-for-state.
let axiWriteMaster =
    design "AxiWriteMaster" (fun () ->
        streamInput "in" (axiWriteBeatLayout 32 128)
        |> axiMasterWriter 32 128 4)

/// The AXI4 read master's ring path at ports: request addresses in, read data
/// out, `m_axi_ar*`/`m_axi_r*` at the boundary. The rehearsal drives it
/// against `SimAxiReadSlave` across the pacing matrix.
let axiReadMaster =
    design "AxiReadMaster" (fun () ->
        streamInput "req" (layout1 ("addr", 32))
        |> axiMasterReader 32 32 8
        |> streamOutput "resp")

/// The single-outstanding degenerate read path — pending flags, no ring.
let axiReadMasterSingle =
    design "AxiReadMasterSingle" (fun () ->
        streamInput "req" (layout1 ("addr", 32))
        |> axiMasterReader 32 32 1
        |> streamOutput "resp")

/// The burst read master: (addr, len) descriptors in, (data, last) beats out,
/// streaming R passthrough — GEP's host-marshaled streaming shape.
let axiReadMasterBurst =
    design "AxiReadMasterBurst" (fun () ->
        streamInput "req" (layout2 ("addr", 32) ("len", 8))
        |> axiMasterReaderBurst 32 32 4 16
        |> streamOutput "resp")

/// The single-outstanding degenerate path — pending flags, no ring.
let axiWriteMasterSingle =
    design "AxiWriteMasterSingle" (fun () ->
        streamInput "in" (axiWriteBeatLayout 16 32)
        |> axiMasterWriter 16 32 1)

/// A w1p pulse register under the oracle: each accepted write of 1 to offset 0
/// bumps a counter read back at offset 4 — write-fire gating, bit-0 decode and
/// read-as-zero all differentially exercised.
let axiPulse =
    designClocked axiClock "AxiPulse" (fun () ->
        let count = reg "pulse_count" 8

        let pulses, _ =
            axiLiteSlaveFull 4 [ "go", 0x0UL ] [] [ 0x4UL, count ] []

        match pulses with
        | [ go ] -> If go (fun () -> count + lit 1UL 8 ==> count)
        | _ -> failwith "expected exactly one pulse register")

/// The lifted neighborhood gather under the oracle: a 3x3 grid of input bits,
/// counted through every stencil/edge combination Life and its relatives use.
/// Purely combinational, so 50 random grids differentially pin the gather
/// order, the dead border, the wrap and the clamp all at once.
let neighborCount =
    design "NeighborCount" (fun () ->
        let grid = [ for y in 0..2 -> [ for x in 0..2 -> inputBit $"g{y}{x}" ] ]
        let count w stencil edge y x = countWhere w id (neighborhood stencil edge grid y x)

        let moore = output "moore" 4
        count 4 Stencil.Moore Edge.Zero 1 1 ==> moore
        let corner = output "corner" 4
        count 4 Stencil.Moore Edge.Zero 0 0 ==> corner
        let vonNeumann = output "vonNeumann" 3
        count 3 Stencil.VonNeumann Edge.Zero 1 1 ==> vonNeumann
        let wrapped = output "wrapped" 4
        count 4 Stencil.Moore Edge.Wrap 0 0 ==> wrapped
        let clamped = output "clamped" 4
        count 4 Stencil.Moore Edge.Clamp 2 2 ==> clamped)

/// The declarative register map exercised whole: every entry kind in one
/// aperture. 0x000 is the overlay word (ID reads, two pulse bits write);
/// `count` and `high` pack one read word; `wrapIrq` is the w1c + irq path;
/// `pattern` is a host-written window the hardware sync-reads back out
/// through `patLow`.
module ScratchMap =
    let id = roConst "id" 0x000UL 0xF5C0FFEEUL
    let bump = pulseBit "bump" 0x000UL 0
    let clear = pulseBit "clear" 0x000UL 1
    let threshold = rwReg "threshold" 0x004UL 16 0UL
    let count = roField "count" 0x008UL 0 8
    let high = roField "high" 0x008UL 8 1
    let wrapIrq = w1cBit "wrapIrq" 0x00CUL 0
    let patLow = roField "patLow" 0x010UL 0 8

    let pattern = rwWindow "pattern" 0x040UL 16
    let trace = roWindow "trace" 0x080UL 16

    let map =
        { apertureAddrWidth = 8
          entries = [ id; bump; clear; threshold; count; high; wrapIrq; patLow; pattern; trace ] }

let regMapScratch =
    designClocked axiClock "RegMapScratch" (fun () ->
        let regs = axiLiteSlaveOf ScratchMap.map

        let count = reg "count_reg" 8
        let bump = regs.pulse ScratchMap.bump

        If (regs.pulse ScratchMap.clear) (fun () -> lit 0UL 8 ==> count)
        Else (fun () -> If bump (fun () -> count + lit 1UL 8 ==> count))

        regs.drive ScratchMap.count count
        regs.drive ScratchMap.high (bnot (lt count (slice 7 0 (regs.value ScratchMap.threshold))))
        regs.setBit ScratchMap.wrapIrq (bump &&& eq count (lit 255UL 8))

        // `hostTurn` is ignored on purpose: patternWord only drives a
        // read-only field, so the one-cycle glitch during a host readback is
        // observable only by the very host doing the read — mid-transaction,
        // on a different offset.
        let patternWord = (regs.window ScratchMap.pattern (slice 3 0 count)).data
        regs.drive ScratchMap.patLow (slice 7 0 patternWord)

        // The mirror window: the design writes, the host reads. Each bump
        // leaves a marked word at the count it happened on, so a host read of
        // trace[i] proves the design's write port and the host's read port are
        // the same array.
        let trace = regs.driveWindow ScratchMap.trace
        memWrite trace (slice 3 0 count) (cat (lit 0xC5UL 24) count) bump

        let irqOut = outputBit "irq"
        regs.irq ==> irqOut)

/// Four animated rows (free-running counters, so every frame differs) through
/// `snapshotSource` and `streamConflate3` at ports: the testbench's random
/// capture/release/writer-idle/backpressure pokes differentially exercise the
/// slot rotation, the capture queue, the drain gate and the overrun counter.
let snapshotConflate =
    design "SnapshotConflate" (fun () ->
        let capture = inputBit "snap_capture"
        let release = inputBit "snap_release"
        let writerIdle = inputBit "writer_idle"

        let rows =
            [ for i in 0..3 ->
                  let r = regInit $"row%d{i}" 8 (uint64 (i * 16))
                  r + lit 1UL 8 ==> r
                  r ]

        let beats, status =
            snapshotSource "snap" rows
            |> streamConflate3 "conflate" capture release writerIdle

        let readyOut = outputBit "host_ready"
        status.ready ==> readyOut
        let slotOut = output "host_slot" 2
        status.readSlot ==> slotOut
        let overrunOut = output "host_overrun" 8
        status.overrun ==> overrunOut
        let irqOut = outputBit "host_irq"
        status.irq ==> irqOut
        Stream.out "beat" beats)

/// The whole snapshot path closed through the write master: conflate beats
/// become AXI beats at `(slot, index)`-derived addresses, and the master's
/// own drained level feeds the conflate's publish gate — the coherency loop a
/// DDR-backed snapshot needs. `m_axi` at the boundary, so the oracle throws
/// random slave timing at it; the demo runs the same design against the fake
/// DDR and asserts frame coherency.
let snapshotDdr =
    design "SnapshotDdr" (fun () ->
        let capture = inputBit "snap_capture"
        let release = inputBit "snap_release"

        let rows =
            [ for i in 0..3 ->
                  let r = regInit $"row%d{i}" 8 (uint64 (i * 16))
                  r + lit 1UL 8 ==> r
                  r ]

        let writerIdle = wireBit "writer_idle_w"

        let beats, status =
            snapshotSource "snap" rows
            |> streamConflate3 "conflate" capture release writerIdle

        let readyOut = outputBit "host_ready"
        status.ready ==> readyOut
        let slotOut = output "host_slot" 2
        status.readSlot ==> slotOut
        let overrunOut = output "host_overrun" 8
        status.overrun ==> overrunOut

        let axiBeats =
            beats
            |> streamMapTo (axiWriteBeatLayout 32 32) (fun (slot, index, data) ->
                let wordAddr = cat (cat slot index) (lit 0UL 2) // (slot*4 + index) * 4 bytes
                cat (lit 0UL 26) wordAddr, cat (lit 0UL 24) data, lit 0xFUL 4)

        axiMasterWriterWithIdle 32 32 4 axiBeats ==> writerIdle)

/// The audio stdlib's two datapath entries as one toolchain test input: a
/// 4-tap FIR over the delay-line chain, and a biquad section driven at its
/// real widths.
///
/// The biquad is the interesting half. Its feedback path means a wrong
/// `advance` gate, a wrong narrowing shift or a wrong saturation does not
/// merely produce one bad sample — it poisons the state and diverges, which is
/// exactly what a cycle-by-cycle differential catches and an eyeball does not.
/// `coeff_*` are ports rather than constants so one elaboration covers the
/// identity case and an arbitrary filter.
let audioOps =
    design "AudioOps" (fun () ->
        let x = input "x" sampleWidth
        let advance = inputBit "advance"
        let b0 = input "coeff_b0" biquadCoeffWidth
        let b1 = input "coeff_b1" biquadCoeffWidth
        let b2 = input "coeff_b2" biquadCoeffWidth
        let a1 = input "coeff_a1" biquadCoeffWidth
        let a2 = input "coeff_a2" biquadCoeffWidth
        let y = output "y" sampleWidth
        let firOut = output "fir_out" 18
        let left = output "left" sampleWidth
        let right = output "right" sampleWidth

        let section = instanceNamed "section" (biquadSection "BiquadSection")
        section x advance { b0 = b0; b1 = b1; b2 = b2; a1 = a1; a2 = a2 } ==> y

        // 4-tap [1,2,2,1] low-pass over the low byte of the sample.
        let sampleByte = wire "sample_byte" 8
        slice 7 0 x ==> sampleByte
        fir 8 8 [ 1UL; 2UL; 2UL; 1UL ] sampleByte ==> firOut

        // The packed stereo encoding round-trips: pack then unpack must be the
        // identity, which is the property the AXI register path depends on.
        let stereo = wire "stereo" sampleBits
        packSample x (bnot x) ==> stereo
        sampleLeft stereo ==> left
        sampleRight stereo ==> right)

/// The three combinational-to-shallow audio stages as one chain: gain →
/// compressor → limiter, the order the effects chain uses them in.
///
/// Chained rather than tested one at a time on purpose. Each stage splices the
/// handshake differently — gain and limiter are zero-latency passthroughs, the
/// compressor is `compressorLatency` deep with a valid pipeline beside it — so
/// wiring them in series is what proves the depths compose rather than each
/// being self-consistent alone. The controls are ports so one elaboration
/// covers unity settings and arbitrary ones.
let audioChain =
    design "AudioChain" (fun () ->
        let volume = input "volume" 16
        let mute = inputBit "mute"
        let threshold = input "threshold" sampleWidth
        let ratio = input "ratio" 8
        let attack = input "attack" 16
        let releaseRate = input "releaseRate" 16
        let makeup = input "makeup" 16
        let limit = input "limit" sampleWidth

        let gain = instanceNamed "gain" (audioGain "AudioGain") volume mute
        let compressor = instanceNamed "compressor" (audioCompressor "AudioCompressor") threshold ratio attack releaseRate makeup
        let limiter = instanceNamed "limiter" (audioLimiter "AudioLimiter") limit

        streamInput "in" sampleLayout
        |> gain
        |> compressor
        |> limiter
        |> streamOutput "out")

/// The tone generator sourcing the tone-control FIR: a stream source feeding a
/// pipelined-MAC stage. Together they cover the two shapes the earlier audio
/// designs did not — a module that *originates* a stream (its phase advances
/// on the handshake, not the clock) and one whose latency comes from an
/// `adderTreePipelined` rather than a hand-placed register.
let audioTone =
    design "AudioTone" (fun () ->
        let enable = inputBit "enable"
        let step = input "step" tonePhaseWidth
        let preset = input "preset" 2

        let tone = instanceNamed "tone" (toneGenerator "ToneGenerator") enable step
        let filter = instanceNamed "filter" (audioToneFilter "AudioToneFilter") preset

        tone |> filter |> streamOutput "out")

/// The tone-control FIR alone, stream-driven, so a test can choose the input
/// rather than take whatever the oscillator produces.
let audioFirStage =
    design "AudioFirStage" (fun () ->
        let preset = input "preset" 2
        let filter = instanceNamed "filter" (audioToneFilter "AudioToneFilter") preset
        streamInput "in" sampleLayout |> filter |> streamOutput "out")

/// The three I2S modules wired as a loopback: the clock generator drives both
/// edge ticks, the transmitter serialises a stream onto `sdin`, and that line
/// feeds straight back into the receiver's `sdout`.
///
/// Tested in isolation from the DSP chain, and as a loop rather than
/// separately, because the frame convention is the thing that can be wrong.
/// Rx and tx each look self-consistent while disagreeing by a bit position or
/// a channel — a transmitter that emits its MSB one tick early and a receiver
/// that latches one tick late both pass their own inspection. Only the round
/// trip pins the convention, and it pins the clock generator's two edge ticks
/// with it, since rx samples on one and tx updates on the other.
let i2sLoopback =
    design "I2sLoopback" (fun () ->
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster") ()

        let sdin =
            instanceNamed "tx" (i2sTx "I2sTx") clocks.sclkTxTick clocks.lrclk (streamInput "in" sampleLayout)

        let line = wireBit "line"
        sdin ==> line

        instanceNamed "rx" (i2sRx "I2sRx") clocks.sclkRxTick clocks.lrclk line
        |> streamOutput "out"

        // The codec-facing pins, so the loopback covers what a wrapper drives.
        let mclk = outputBit "mclk"
        clocks.mclk ==> mclk
        let sclk = outputBit "sclk"
        clocks.sclk ==> sclk
        let lrclk = outputBit "lrclk"
        clocks.lrclk ==> lrclk
        let serial = outputBit "serial"
        line ==> serial)

/// The I2S framers exposed bare, for the isolation checks: each driven by a
/// hand-built ideal frame rather than by the clock generator, which is how the
/// Kotlin side verifies them and the only way to test one without the other.
let i2sRxStage =
    design "I2sRxStage" (fun () ->
        let sclkTick = inputBit "sclkTick"
        let lrclk = inputBit "lrclk"
        let sdout = inputBit "sdout"

        instanceNamed "rx" (i2sRx "I2sRx") sclkTick lrclk sdout
        |> streamOutput "out")

let i2sTxStage =
    design "I2sTxStage" (fun () ->
        let sclkTick = inputBit "sclkTick"
        let lrclk = inputBit "lrclk"
        let sdin = outputBit "sdin"
        instanceNamed "tx" (i2sTx "I2sTx") sclkTick lrclk (streamInput "in" sampleLayout) ==> sdin)

/// The 8-band multiband compressor as a stream stage.
let multibandStage =
    design "MultibandStage" (fun () ->
        let threshold = input "threshold" sampleWidth
        let ratio = input "ratio" 8
        let attack = input "attack" 16
        let releaseRate = input "releaseRate" 16
        let leftGains = List.init multibandBands (fun i -> input $"lg{i}" 16)
        let rightGains = List.init multibandBands (fun i -> input $"rg{i}" 16)

        let stage, envelope =
            instanceNamed "mb" (multibandCompressor "MultibandCompressor8") threshold ratio attack releaseRate leftGains rightGains
            |> fun apply -> apply (streamInput "in" sampleLayout)

        streamOutput "out" stage
        let envOut = output "envelope" sampleWidth
        envelope ==> envOut)
