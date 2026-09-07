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
    defModule
        "Comparator8"
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

let add3 =
    moduleDef "Add3" (fun m ->
        let x = m.Input("x", 8)
        let y = m.Input("y", 8)
        let z = m.Input("z", 8)
        let sum = m.Output("sum", 8)
        let a1a, a1b, a1sum = m.Instance("a1", adder8Def)
        let a2a, a2b, a2sum = m.Instance("a2", adder8Def)
        connect a1a x
        connect a1b y
        connect a2a a1sum
        connect a2b z
        a2sum ==> sum)

let dot2 =
    moduleDef "Dot2" (fun m ->
        let a = m.Input("a", 8)
        let b = m.Input("b", 8)
        let c = m.Input("c", 8)
        let d = m.Input("d", 8)
        let out = m.Output("out", 16)

        let mul1a, mul1b, mul1p = m.Instance("mul1", mul8Def)
        let mul2a, mul2b, mul2p = m.Instance("mul2", mul8Def)
        let acca, accb, accSum = m.Instance("acc", adder16Def)
        let bumpX, bumpY = m.Instance("bump", satInc8Def)

        connect mul1a a
        connect mul1b b
        connect bumpX c
        connect mul2a bumpY
        connect mul2b d
        connect acca mul1p
        connect accb mul2p
        accSum ==> out)

let dot2Auto =
    moduleDef "Dot2Auto" (fun m ->
        let inst tm = m.Instance tm
        let a = m.Input("a", 8)
        let b = m.Input("b", 8)
        let c = m.Input("c", 8)
        let d = m.Input("d", 8)
        let out = m.Output("out", 16)

        let mul1a, mul1b, mul1p = inst mul8Def
        connect mul1a a
        connect mul1b b
        let bumpX, bumpY = inst satInc8Def
        connect bumpX c
        let mul2a, mul2b, mul2p = inst mul8Def
        connect mul2a bumpY
        connect mul2b d
        let accA, accB, accSum = inst adder16Def
        connect accA mul1p
        connect accB mul2p
        accSum ==> out)

let dot2Ambient =
    defModule
        "Dot2Ambient"
        (fun p -> (p.inPort "a" 8, p.inPort "b" 8, p.inPort "c" 8, p.inPort "d" 8, p.outPort "out" 16))
        (fun (a, b, c, d, out) ->
            let multiply = mulOf 8
            let accumulate = adderOf 16
            let bump = satIncOf 8

            accumulate (multiply a b) (multiply (bump c) d) ==> out)

/// Character-for-character the same body as `dot2Ambient`. Only the three stdlib
/// bindings differ — `*Logic` instead of `*Of` — and the design goes from four
/// modules to one flat one.
let dot2Inline =
    defModule
        "Dot2Inline"
        (fun p -> (p.inPort "a" 8, p.inPort "b" 8, p.inPort "c" 8, p.inPort "d" 8, p.outPort "out" 16))
        (fun (a, b, c, d, out) ->
            let multiply = mulLogic 8
            let accumulate = adderLogic 16
            let bump = satIncLogic 8

            accumulate (multiply a b) (multiply (bump c) d) ==> out)

/// The dot product with three pipeline registers mixed in. `stage` is a stateful
/// module, `accumulate` and `bump` are inline, `multiply` is a combinational module —
/// nothing at the call site distinguishes the three kinds. Each application of
/// `stage` is a fresh register, which is exactly what a pipeline stage wants.
/// Latency 2 from inputs to `out`, verified by behavior_tb.v.
let pipelinedDot =
    defModule
        "PipelinedDot"
        (fun p -> (p.inPort "a" 8, p.inPort "b" 8, p.inPort "c" 8, p.inPort "d" 8, p.outPort "out" 16))
        (fun (a, b, c, d, out) ->
            let multiply = mulOf 8
            let accumulate = adderLogic 16
            let bump = satIncLogic 8
            let stage = delayOf 16

            stage (accumulate (stage (multiply a b)) (stage (multiply (bump c) d)))
            ==> out)

/// A stateful module with feedback, used as a plain function of its enable.
let gatedCounter =
    defModule
        "GatedCounter"
        (fun p -> (p.inPort "enable" 1, p.outPort "value" 8))
        (fun (enable, value) ->
            let count = counterOf 8
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
    defModule
        "HoldThroughReset"
        (fun p -> (p.inPort "value" 8, p.outPort "held_out" 8, p.outPort "cleared_out" 8))
        (fun (value, heldOut, clearedOut) ->
            let held = regNoReset "held" 8
            let cleared = regInit "cleared" 8 3UL

            value ==> held
            value ==> cleared

            held ==> heldOut
            cleared ==> clearedOut)

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
    defModule
        "DynamicShifts"
        (fun p ->
            (p.inPort "value" 8,
             p.inPort "amount" 3,
             p.inPortAs "signed_value" (SInt 8),
             p.outPort "shifted_left" 15,
             p.outPort "shifted_right" 8,
             p.outPort "shifted_arith" 8,
             p.outPort "shifted_fixed" 11))
        (fun (value, amount, signedValue, shiftedLeft, shiftedRight, shiftedArith, shiftedFixed) ->
            shl amount value ==> shiftedLeft
            shr amount value ==> shiftedRight
            // Arithmetic, because the operand says it is signed — the sign fills in
            // from the top rather than zeros.
            shr amount signedValue ==> shiftedArith
            // And the constant form, at the same call shape, for contrast.
            shl 3 value ==> shiftedFixed)

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
    defModule
        "BitReductions"
        (fun p -> (p.inPort "value" 8, p.outPort "any" 1, p.outPort "all" 1, p.outPort "odd" 1))
        (fun (value, any, all, odd) ->
            anyBitSet value ==> any
            allBitsSet value ==> all
            parity value ==> odd)

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
    defModule
        "ConstantDivision"
        (fun p ->
            (p.inPort "value" 8,
             p.inPortAs "signed_value" (SInt 8),
             p.outPort "tenths" 8,
             p.outPort "units" 8,
             p.outPort "eighths" 8,
             p.outPortAs "thirds" (SInt 9)))
        (fun (value, signedValue, tenths, units, eighths, thirds) ->
            divideBy 10 value ==> tenths
            remainderBy 10 value ==> units
            // A power of two, which synthesis turns back into a part-select.
            divideBy 8 value ==> eighths
            divideBy 3 signedValue ==> thirds)

/// The stdlib divider, wired as what it is: a stream stage.
///
/// The caller hands it `(dividend, divisor)` beats and reads `(quotient,
/// remainder)` beats back. **No latency crosses the boundary** — the unit
/// reuses one subtractor for eight iterations and therefore cannot take a new
/// pair every cycle, and `ready` is the only thing that can say so. Nothing
/// here counts cycles, which is the whole point of the shape.
let streamDivider =
    defModule
        "StreamDivider"
        (fun p ->
            (p.inPort "dividend" 8,
             p.inPort "divisor" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "quotient" 8,
             p.outPort "remainder" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (dividend, divisor, inValid, inReady, quotientOut, remainderOut, outValid, outReady) ->
            let requests =
                { payload = dividend, divisor
                  valid = inValid
                  ready = inReady
                  layout = layout2 ("dividend", 8) ("divisor", 8) }

            let results = divider "dv" 8 requests
            let quotient, remainder = results.payload

            quotient ==> quotientOut
            remainder ==> remainderOut
            results.valid ==> outValid
            outReady ==> results.ready)

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
    defModule
        "TaggedDivide"
        (fun p ->
            (p.inPort "dividend" 8,
             p.inPort "divisor" 8,
             p.inPort "tag" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "quotient" 8,
             p.outPort "remainder" 8,
             p.outPort "tag_out" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (dividend, divisor, tagIn, inValid, inReady, quotientOut, remainderOut, tagOut, outValid, outReady) ->
            let src =
                { payload = (dividend, divisor), tagIn
                  valid = inValid
                  ready = inReady
                  layout = layoutJoin divideOperands divideContext }

            let out =
                withContext "dv" 4 divideOperands divideResults divideContext (divider "dv" 8) src

            let (quotient, remainder), tag = out.payload

            quotient ==> quotientOut
            remainder ==> remainderOut
            tag ==> tagOut
            out.valid ==> outValid
            outReady ==> out.ready)

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
    defModule
        "FarmedDivide"
        (fun p ->
            (p.inPort "dividend" 8,
             p.inPort "divisor" 8,
             p.inPort "tag" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "quotient" 8,
             p.outPort "remainder" 8,
             p.outPort "tag_out" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (dividend, divisor, tagIn, inValid, inReady, quotientOut, remainderOut, tagOut, outValid, outReady) ->
            let src =
                { payload = (dividend, divisor), tagIn
                  valid = inValid
                  ready = inReady
                  layout = layoutJoin divideOperands divideContext }

            let out =
                Stream.farmWith "div" 4 2 divideOperands divideResults divideContext
                    (fun i -> divider $"dv%d{i}" 8 >> Stream.stages i id)
                    src

            let (quotient, remainder), tag = out.payload

            quotient ==> quotientOut
            remainder ==> remainderOut
            tag ==> tagOut
            out.valid ==> outValid
            outReady ==> out.ready)

let byteLayout = layout1 ("data", 8)
let coordLayout = layout2 ("x", 8) ("lum", 8)

/// Two handshake stages and a payload map, chained by nesting. The ready chain runs
/// backwards — sink to source — through ordinary forward function application,
/// because each Stream value carries the net its consumer must drive. Verified by
/// stream_tb.v: backpressure fills both stages, blocks the source, drains in order.
let streamPipe =
    defModule
        "StreamPipe"
        (fun p -> (streamInputPorts p "in" byteLayout, streamOutputPorts p "out" byteLayout))
        (fun (inPorts, outPorts) ->
            let stage = streamStageFor byteLayout
            let bump = satIncLogic 8
            streamSink outPorts (stage (streamMap bump (stage (streamSource inPorts)))))

/// A two-field payload through the same pipe shape — warp11's pixel-beat rule in
/// miniature: the beat carries its coordinate, so nothing infers position from a
/// cycle count. The map brightens `lum` and leaves `x` alone; because the payload
/// is a typed tuple, touching the wrong field is a compile error, not a name
/// lookup. coord_tb.v proves the fields stay associated under backpressure — the
/// thing the single-field test could not check.
let coordPipe =
    defModule
        "CoordPipe"
        (fun p -> (streamInputPorts p "in" coordLayout, streamOutputPorts p "out" coordLayout))
        (fun (inPorts, outPorts) ->
            let stage = streamStageFor coordLayout
            let brighten = satIncLogic 8

            streamSink outPorts (stage (streamMap (fun (x, lum) -> x, brighten lum) (stage (streamSource inPorts)))))

/// On/otherwise with nesting: clear beats enable, and the reg holds when neither
/// fires — the hold arm appears nowhere in the source, only in the folded Mux.
let onCounter =
    defModule
        "OnCounter"
        (fun p -> (p.inPort "enable" 1, p.inPort "clear" 1, p.outPort "count" 8))
        (fun (enable, clear, count) ->
            let r = reg "r" 8

            ifElse [
                (clear, fun () -> lit 0UL 8 ==> r)
                (otherwise, fun () -> If enable (fun () -> r + lit 1UL 8 ==> r)) ]

            r ==> count)

/// A defaulted wire under two sibling If blocks: last connect wins, so sel1
/// outranks sel0 — the classic priority mux, written as statements.
let onPriority =
    defModule
        "OnPriority"
        (fun p ->
            (p.inPort "sel0" 1, p.inPort "sel1" 1, p.inPort "a" 8, p.inPort "b" 8, p.inPort "c" 8, p.outPort "out" 8))
        (fun (sel0, sel1, a, b, c, out) ->
            a ==> out
            If sel0 (fun () -> b ==> out)
            If sel1 (fun () -> c ==> out))

/// `ifElse`: the same priority mux as a ladder, written so that **ordering is
/// the only thing the result can be read for**. The three guarded conditions
/// nest rather than partition — `x < 4` implies `x < 8` implies `x < 16` — so
/// for any x below 4 every arm is true at once, and a ladder that resolved
/// last-match-wins would emit a mux tree just as plausible as this one. Only
/// walking the input says which way it resolved. Disjoint conditions could not
/// tell the two apart, which is why they are not used here.
///
/// `held` is driven by the first and third arms and by nothing else, so it also
/// carries the second claim: a target the winning arm does not drive keeps what
/// it had, through the ladder rather than around it. Its unconditional default
/// is what that falls back to — an output with nothing to hold is an error, as
/// everywhere else.
///
/// The last arm is `otherwise`, which is the else. Drop it and the ladder
/// simply drives nothing when no arm matches.
let ifElseLadder =
    defModule
        "IfElseLadder"
        (fun p -> (p.inPort "x" 8, p.outPort "band" 8, p.outPort "held" 8))
        (fun (x, band, held) ->
            lit 0UL 8 ==> band
            lit 0UL 8 ==> held

            ifElse
                [ (lt x (lit 4UL 8),
                   fun () ->
                       lit 1UL 8 ==> band
                       lit 11UL 8 ==> held)
                  (lt x (lit 8UL 8), fun () -> lit 2UL 8 ==> band)
                  (lt x (lit 16UL 8),
                   fun () ->
                       lit 3UL 8 ==> band
                       lit 33UL 8 ==> held)
                  (otherwise, fun () -> lit 4UL 8 ==> band) ])

/// The states of `switchRing`, public so the living check can write the same
/// machine the old way and compare.
type Phase =
    | Load
    | Warm
    | Run
    | Drain
    | Flush
    | Park
    | Hold
    | Stop

/// The eight in declaration order, so the living check can build the same ring
/// at several sizes and watch how each form grows.
let allPhases = [ Load; Warm; Run; Drain; Flush; Park; Hold; Stop ]

/// `Machine.Switch`: every state's behaviour in one ladder.
///
/// Each arm here transitions *conditionally* — `If go`, `If halt`, a count
/// compare — which is the shape that matters. Written as six sibling `st.If`
/// blocks, each one's fall-through is the whole expression so far and the
/// conditional inner `If` names it twice, so the emitted comparator count goes
/// as 2^n. Written as one `Switch` it goes as n. Six states is 63 comparators
/// against 6; the living check asserts the second number and that both forms
/// behave identically.
let switchRing =
    defModule
        "SwitchRing"
        (fun p -> (p.inPort "go" 1, p.inPort "halt" 1, p.outPort "phase" 3, p.outPort "ticks" 8))
        (fun (go, halt, phase, ticks) ->
            let st = machine "stage" [ Load; Warm; Run; Drain; Flush; Park ]
            let count = reg "count" 8

            st.Value ==> phase
            count ==> ticks

            st.Switch
                [ Load, fun () -> If go (fun () -> st.Goto Warm)
                  Warm, fun () -> st.Goto Run
                  Run,
                  fun () ->
                      count + lit 1UL 8 ==> count
                      If halt (fun () -> st.Goto Drain)
                  Drain, fun () -> If (eq count (lit 4UL 8)) (fun () -> st.Goto Flush)
                  Flush, fun () -> st.Goto Park
                  Park,
                  fun () ->
                      If go (fun () ->
                          lit 0UL 8 ==> count
                          st.Goto Load) ])

// ---------------------------------------------------------------------------
// The utility primitives, one toy apiece. These exist to be *read* — a stdlib
// entry whose use has to be reverse-engineered from a 2,000-line accelerator
// has not really shipped — and each is what the living checks drive, so the
// example cannot drift from the thing it demonstrates.

/// `lfsr`: pseudo-random stimulus from a shift and an xor. `step` gates it, so
/// the state holds while a consumer is busy, and `tap` is the one-bit dither a
/// caller usually actually wants.
let lfsrSource =
    defModule
        "LfsrSource"
        (fun p -> (p.inPort "step" 1, p.outPort "state" 9, p.outPort "tap" 1))
        (fun (step, state, tap) ->
            let bits = lfsr "noise" 9 1UL step
            bits ==> state
            slice 0 0 bits ==> tap)

/// `oneHotLowest`: four requesters, and the lowest-numbered one that is asking
/// gets the grant — a fixed-priority arbiter, whole.
let oneHotScan =
    defModule
        "OneHotScan"
        (fun p ->
            ([ for i in 0..3 -> p.inPort $"request{i}" 1 ],
             [ for i in 0..3 -> p.outPort $"grant{i}" 1 ],
             p.outPort "any" 1))
        (fun (requests, grantPorts, any) ->
            let grants = oneHotLowest requests

            for i in 0..3 do
                grants[i] ==> grantPorts[i]

            List.reduce (|||) requests ==> any)

/// `mux1H`: the value belonging to whichever grant is high. Pairs with the
/// arbiter above — that is the shape these two are almost always used in, one
/// picking the winner and the other fetching what the winner brought.
let mux1HSelect =
    defModule
        "Mux1HSelect"
        (fun p ->
            ([ for i in 0..3 -> p.inPort $"request{i}" 1 ],
             [ for i in 0..3 -> p.inPort $"value{i}" 8 ],
             p.outPort "winner" 8))
        (fun (requests, values, winner) ->
            let grants = oneHotLowest requests
            mux1H grants values ==> winner)

/// `edgeDetect`: `enable` gates the *sample*, not the comparison, so an edge
/// that happens on a slow input is still there to be seen on the next enabled
/// cycle. Tie `enable` high for the plain form.
let edgeDetector =
    defModule
        "EdgeDetector"
        (fun p ->
            (p.inPort "enable" 1,
             p.inPort "signal" 1,
             p.outPort "rising" 1,
             p.outPort "falling" 1,
             p.outPort "changed" 1,
             p.outPort "previous" 1))
        (fun (enable, signal, rising, falling, changed, previous) ->
            let edge = edgeDetect "sig" enable signal

            edge.rising ==> rising
            edge.falling ==> falling
            edge.changed ==> changed
            edge.previous ==> previous)

/// The bit-shape utilities in one place: `catAll` / `fill` / `reverse` /
/// `popCount` / `uintToOneHot` / `oneHotToUInt`. Each is a one-liner at the call
/// site, which is the whole argument for having them — every one of these was
/// otherwise a fold someone had to read twice.
///
/// `uintToOneHot` and `oneHotToUInt` are shown as the round trip they usually
/// are: an index out to a one-hot grant and back again.
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
        (fun (a, b, flag, index, joined, mask, flipped, ones, hotPorts, recovered) ->
            catAll [ a; b ] ==> joined

            // A one-bit signal filled to a mask, the usual reason to reach for it.
            fill 4 flag ==> mask

            reverse a ==> flipped

            popCount a ==> ones

            let hot = uintToOneHot 4 index

            for i in 0..3 do
                hot[i] ==> hotPorts[i]

            // Back again: the round trip is the identity for any index in range.
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
    defModule
        "Dividers"
        (fun p ->
            (p.inPort "enable" 1,
             p.inPort "last" 4,
             p.outPort "divided" 1,
             p.outPort "count" 3,
             p.outPort "wrap" 1,
             p.outPort "window_count" 4,
             p.outPort "window_wrap" 1))
        (fun (enable, last, divided, count, wrap, windowCount, windowWrap) ->
            let period = counter "period" 6 enable
            let phase = regBit "phase"
            If period.wrap (fun () -> bnot phase ==> phase)

            phase ==> divided
            period.count ==> count
            period.wrap ==> wrap

            let window = counterTo "window" last enable
            window.count ==> windowCount
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
    defModule
        "FlowSampler"
        (fun p ->
            (p.inPort "sample" 1,
             p.inPort "out_ready" 1,
             p.outPort "out_value" 8,
             p.outPort "out_valid" 1,
             p.outPort "dropped" 8))
        (fun (sample, takeReady, value, valid, dropped) ->
            let counter = reg "counter" 8
            If sample (fun () -> counter + lit 1UL 8 ==> counter)

            let sampled =
                { payload = counter
                  valid = sample
                  layout = layout1 ("value", 8) }
                |> flowStage "staged"

            let stream, overflowed = flowToStream sampled
            takeReady ==> stream.ready

            stream.payload ==> value
            stream.valid ==> valid

            // What the flow cost: beats the consumer was not there for.
            let droppedCount = reg "dropped_count" 8
            If overflowed (fun () -> droppedCount + lit 1UL 8 ==> droppedCount)
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
    defModule
        "Sequencer"
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

/// Structure generated by ordinary F#: a fold building a 4-deep pipeline of Delay8
/// instances. What every real warp11 design does (reduceTree, conv2d, the 104
/// lanes), exercised at spike scale.
let loopPipeline =
    defModule
        "LoopPipeline"
        (fun p -> (p.inPort "x" 8, p.outPort "out" 8))
        (fun (x, out) ->
            let stage = delayOf 8
            List.fold (fun acc _ -> stage acc) x [ 1 .. 4 ] ==> out)

/// Recursion as a generator: eight inputs summed through a balanced adder
/// tree — the stdlib `reduceTree`'s oracle (the recursive-over-Exprs shape
/// this design carried locally until the library lifted it, 2026-08-05).
let treeSum =
    defModule
        "TreeSum8"
        (fun p -> ([ for i in 0..7 -> p.inPort $"x{i}" 8 ], p.outPort "out" 11))
        (fun (inputs, out) ->
            let widen x = cat (lit 0UL 3) x
            reduceTree (+) (List.map widen inputs) ==> out)

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
    defModule
        "AssertedCounter"
        (fun p ->
            (p.inPort "enable" 1,
             p.inPort "hold" 1,
             p.inPort "wrap" 1,
             p.outPort "count" 8,
             p.outPort "previous" 8))
        (fun (enable, hold, wrap, count, previous) ->
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
    defModule
        "AssertedSaturate"
        (fun p -> (p.inPort "a" 8, p.inPort "b" 8, p.outPort "out" 8))
        (fun (a, b, out) ->
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
    defModule
        "CmdProcessor"
        (fun p -> (streamInputPorts p "cmd" (unionLayout cmdUnion), p.inPort "raddr" 3, p.outPort "rdata" 8))
        (fun (cmdPorts, raddr, rdata) ->
            let store = distributedMem "store" 3 8
            let cmd = streamSource cmdPorts

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
    defModule
        "UnionRoundTrip"
        (fun p ->
            (p.inPort "sel" 1,
             p.inPort "addr" 3,
             p.inPort "value" 8,
             p.outPort "out_tag" 1,
             p.outPort "out_addr" 3,
             p.outPort "out_value" 8))
        (fun (sel, addr, value, outTag, outAddr, outValue) ->
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
    defModule
        "ForkJoin"
        (fun p -> (streamInputPorts p "in" byteLayout, streamOutputPorts p "out" byteLayout))
        (fun (inPorts, outPorts) ->
            let stage = streamStageFor byteLayout
            let source = streamSource inPorts

            match streamBroadcast 2 source with
            | [ s1; s2 ] ->
                let bumped = stage (streamMap (satIncLogic 8) s1)
                let plain = stage s2
                streamSink outPorts (streamMergeTree [ bumped; plain ])
            | _ -> failwith "replicate 2 gave the wrong arity")

/// Every signed operation at its own port: wraparound subtract, sign-extending
/// multiply, both compare orders, arithmetic shift. Random 8-bit stimulus sets
/// the sign bit half the time, so the two's-complement boundary patterns (0x80,
/// 0xFF) reach the oracle without being enumerated.
let signedOps =
    defModule
        "SignedOps"
        (fun p ->
            (p.inPort "a" 8,
             p.inPort "b" 8,
             p.outPort "diff" 8,
             p.outPortAs "product" (SInt 16),
             p.outPort "below" 1,
             p.outPort "below_signed" 1,
             p.outPort "shifted" 8))
        (fun (a, b, diff, product, below, belowSigned, shifted) ->
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
    defModule
        "XorOps"
        (fun p ->
            (p.inPort "a" 8, p.inPort "b" 8, p.outPort "mixed" 8, p.outPort "rotated" 8, p.outPort "acc" 8))
        (fun (a, b, mixed, rotated, acc) ->
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
    defModule
        "SatOps"
        (fun p ->
            (p.inPort "a" 8,
             p.inPort "b" 8,
             p.outPort "narrow_u" 4,
             p.outPortAs "narrow_s" (SInt 4),
             p.outPort "sum_u" 8,
             p.outPort "shifted" 12,
             p.outPort "high" 5))
        (fun (a, b, narrowU, narrowS, sumU, shifted, high) ->
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
    defModule
        "EscapeStep"
        (fun p ->
            (p.inPortAs "zx" (SInt 8),
             p.inPortAs "zy" (SInt 8),
             p.inPortAs "cx" (SInt 8),
             p.inPortAs "cy" (SInt 8),
             p.outPort "escape" 1,
             p.outPort "next_zx" 8,
             p.outPort "next_zy" 8))
        (fun (zx, zy, cx, cy, escape, nextZx, nextZy) ->
            let zx2 = wire "zx2" (SInt 16)
            let zy2 = wire "zy2" (SInt 16)
            // xy, not cross: `cross` is an SV reserved word, and the spike has no
            // keyword check at elaboration (the prior implementation had one for exactly this).
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
    defModule
        "EscapeStepFixed"
        (fun p ->
            (Number.inPort p "zx" Number.q4_4,
             Number.inPort p "zy" Number.q4_4,
             Number.inPort p "cx" Number.q4_4,
             Number.inPort p "cy" Number.q4_4,
             p.outPort "escape" 1,
             p.outPort "next_zx" 8,
             p.outPort "next_zy" 8))
        (fun (zx, zy, cx, cy, escape, nextZx, nextZy) ->
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
    defModule
        "EscapeStep28"
        (fun p ->
            (Number.inPort p "zx" Number.q4_28,
             Number.inPort p "zy" Number.q4_28,
             Number.inPort p "cx" Number.q4_28,
             Number.inPort p "cy" Number.q4_28,
             p.outPort "escape" 1,
             p.outPort "next_zx" 32,
             p.outPort "next_zy" 32))
        (fun (zx, zy, cx, cy, escape, nextZx, nextZy) ->
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
    defModuleClocked
        axiClock
        "AxiScratch"
        (fun p -> axiLiteSlavePorts p 6)
        (fun slavePorts ->
            let tick = reg "tick" 8
            tick + lit 1UL 8 ==> tick

            let probe = distributedMem "probe" 2 8
            memWrite probe (slice 1 0 tick) tick (lit 1UL 1)

            axiLiteSlaveOn
                slavePorts
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
    defModule
        "TwoWindowSlave"
        (fun p -> axiLiteSlavePorts p 6)
        (fun slavePorts ->
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

            axiLiteSlaveOn
                slavePorts
                [ "scratch", 0x00UL, 32 ]
                [ 0x04UL, lit 0x11FA57UL 32 ]
                [ 0x10UL, even; 0x20UL, odd ]
            |> ignore)

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
    defModule
        "DeepChannelSlave"
        (fun p -> axiLiteSlavePorts p 6)
        (fun slavePorts ->
            let ch = axiLiteChannelOn slavePorts 3

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
    defModule
        "PipelinedReadSlave"
        (fun p -> axiLiteSlavePorts p 6)
        (fun slavePorts ->
            let ch = axiLiteChannelPipelinedOn slavePorts 1 4

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
    (defModule
        "OnOverlappingWindows"
        (fun p -> axiLiteSlavePorts p 6)
        (fun slavePorts ->
            let a = distributedMem "winA" 3 8
            let b = distributedMem "winB" 2 8
            axiLiteSlaveOn slavePorts [] [] [ 0x00UL, a; 0x10UL, b ] |> ignore))
        .def

/// A register underneath a window. The window answers reads at that address, so
/// the register would be write-only and silently so.
let onRegisterInsideWindow () =
    (defModule
        "OnRegisterInsideWindow"
        (fun p -> axiLiteSlavePorts p 6)
        (fun slavePorts ->
            let w = distributedMem "win" 2 8
            axiLiteSlaveOn slavePorts [ "buried", 0x14UL, 8 ] [] [ 0x10UL, w ] |> ignore))
        .def

/// A wire conditionally assigned with no default — the error warp11's rule
/// promises. A function, not a value: the failure happens at elaboration.
let onBadWire () =
    (defModule
        "OnBadWire"
        (fun p -> (p.inPort "enable" 1, p.outPort "out" 8))
        (fun (enable, out) -> If enable (fun () -> lit 1UL 8 ==> out)))
        .def

/// Dematerialize four fields onto one bus and materialize them straight back.
/// The point of a transporter is that the two directions cannot disagree, so
/// the round trip is its oracle: any offset or field-order slip shows up as a
/// field arriving as some other field's bits. Deliberately ragged widths — a
/// 5-bit first field means every later offset is unaligned, which is exactly
/// where hand-written slices go wrong.
let transporterRoundTrip =
    defModule
        "TransporterRoundTrip"
        (fun p ->
            (p.inPort "a" 5,
             p.inPort "b" 32,
             p.inPort "c" 7,
             p.inPort "d" 32,
             p.outPort "outA" 5,
             p.outPort "outB" 32,
             p.outPort "outC" 7,
             p.outPort "outD" 32))
        (fun (a, b, c, d, outA, outB, outC, outD) ->
            let t = transporter (layout4 ("a", 5) ("b", 32) ("c", 7) ("d", 32))

            let bus = wire "bus" t.width
            t.dematerialize (a, b, c, d) ==> bus

            let a2, b2, c2, d2 = t.materialize bus
            a2 ==> outA
            b2 ==> outB
            c2 ==> outC
            d2 ==> outD)

/// Two unconditional drivers on one wire. Exists to prove `Assign` fires; before
/// it, the scope was last-connect-wins all the way to the call site, so the first
/// assign vanished with no error at elaboration, lint or synthesis.
let doubleAssign () =
    (defModule
        "DoubleAssign"
        (fun p -> (p.inPort "a" 8, p.inPort "b" 8, p.outPort "out" 8))
        (fun (a, b, out) ->
            a ==> out
            b ==> out))
        .def

/// A stream created and never consumed — its ready has no driver. Exists to prove
/// `checkStreams` fires; before it, this emitted an undriven output port and only
/// Verilator noticed.
let danglingStream =
    defModule
        "Dangling"
        (fun p -> streamInputPorts p "in" byteLayout)
        (fun inPorts -> streamSource inPorts |> ignore)

/// An 8-bit output driven by a 16-bit expression. Exists to prove `checkWidths`
/// now *gates* emission: it always reported this, but `emitDesign` did not
/// consult it, so the demo printed the violation as one line among forty and
/// the emitted Verilog truncated silently — `axiPulse`'s pulse counter is the
/// case that measured it, where only Verilator objected.
let widthViolation =
    defModule
        "WidthViolation"
        (fun p -> (p.inPort "a" 8, p.outPort "out" 8))
        (fun (a, out) -> a + lit 1UL 16 ==> out)

/// Two different modules claiming the name `Mul8`. Exists to prove `checkNames`
/// fires — before it, the emitter silently kept the first and dropped the other.
let nameCollision =
    defModule
        "Collision"
        (fun p -> (p.inPort "a" 8, p.inPort "b" 8, p.outPort "out" 16))
        (fun (a, b, out) ->
            let realMultiply = mulOf 8
            let impostor = liftBinary (fnModule2 "Mul8" ("a", 8) ("b", 8) "product" (+))

            realMultiply (impostor a b) b ==> out)

/// The full-scale pod's egress shape at oracle scale: a 128-bit beat assembled
/// by shifting bytes in (the coalescer's move), sliced back out narrow, muxed
/// and compared wide — the Sim's BigInteger path differentially exercised on
/// every op the egress needs (Ref, Concat, Slice, Mux, Eq past 64 bits), plus
/// the wide-testbench stimulus itself (64-bit input ports draw from the
/// chunked generator and travel as hex).
let wideBeat =
    defModule
        "WideBeat"
        (fun p ->
            (p.inPort "byte_in" 8,
             p.inPort "shift_en" 1,
             p.inPort "sel" 1,
             p.inPort "cmp_hi" 64,
             p.inPort "cmp_lo" 64,
             p.outPort "out_hi" 8,
             p.outPort "out_mid" 64,
             p.outPort "beat_eq" 1))
        (fun (byteIn, shiftEn, sel, cmpHi, cmpLo, outHi, outMid, beatEq) ->
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
    defModule
        "WidenOps"
        (fun p ->
            (p.inPort "a" 8, p.inPort "b" 8, p.outPort "wide12" 12, p.outPort "sum13" 13, p.outPort "wide72" 72))
        (fun (a, b, wide12, sum13, wide72) ->
            signExtend 12 a ==> wide12
            signExtend 13 a + signExtend 13 b ==> sum13
            signExtend 72 a ==> wide72)

/// Dispatch round-trip: each beat goes to exactly ONE branch — bumped in one,
/// untouched in the other — then round-robins back, so which lane took a beat
/// is visible in the payload. Contrast forkJoin, where every beat takes both
/// branches. The oracle's random ready/valid exercises the priority
/// arbitration and the OR'd source ready.
let dispatchRoundTrip =
    defModule
        "DispatchRoundTrip"
        (fun p -> (streamInputPorts p "in" byteLayout, streamOutputPorts p "out" byteLayout))
        (fun (inPorts, outPorts) ->
            let stage = streamStageFor byteLayout
            let source = streamSource inPorts

            match streamBalance 2 source with
            | [ s1; s2 ] ->
                let bumped = stage (streamMap (satIncLogic 8) s1)
                let plain = stage s2
                streamSink outPorts (streamMergeTree [ bumped; plain ])
            | _ -> failwith "dispatch 2 gave the wrong arity")

/// The clustered pair at 4 lanes: 2 clusters of 2 with a register stage at
/// each cluster node on both sides — the frame pod's Shape.Auto composition at
/// oracle scale. Each lane adds its own index, so routing is visible in the
/// payload.
let clusteredRoundTrip =
    defModule
        "ClusteredRoundTrip"
        (fun p -> (streamInputPorts p "in" byteLayout, streamOutputPorts p "out" byteLayout))
        (fun (inPorts, outPorts) ->
            let stage = streamStageFor byteLayout
            let source = streamSource inPorts

            let lanes =
                source
                |> wormholeOut Balance 4 (fun i lane -> stage (streamMap (fun d -> d + lit (uint64 i) 8) lane))

            streamSink outPorts (wormholeIn 0 id lanes))

/// Module A of the two-consumer case: one input stream routed onto TWO separate
/// output streams by each beat's top bit — a router, not a fork; every beat
/// lands on exactly one side. Two streams out of one module is nothing special:
/// the wrapper returns a pair, one Stream per boundary port trio.
let private byteSplitterDef =
    defModule
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
        (fun (inData, inValid, inReady, lowData, lowValid, lowReady, highData, highValid, highReady) ->
            let isHigh = wireBit "is_high"
            slice 7 7 inData ==> isHigh
            inData ==> lowData
            inData ==> highData
            (inValid &&& bnot isHigh) ==> lowValid
            (inValid &&& isHigh) ==> highValid
            mux isHigh highReady lowReady ==> inReady)

let private byteSplitter instName (s: Stream<Expr>) =
    let inData, inValid, inReady, lowData, lowValid, lowReady, highData, highValid, highReady =
        byteSplitterDef.NewNamed instName

    s.payload ==> inData
    s.valid ==> inValid
    inReady ==> s.ready
    registerStreamReady lowReady
    registerStreamReady highReady

    ({ payload = lowData
       valid = lowValid
       ready = lowReady
       layout = byteLayout },
     { payload = highData
       valid = highValid
       ready = highReady
       layout = byteLayout })

/// Test case 1 of the connect-layer discussion: module A produces two separate
/// streams, each consumed by its own chain — `Stream.stage f` registers the
/// transformed beat (the word `stage` is what buys the flop). The layout rides
/// the stream from its creation site, so the chains name nothing but the
/// boundary ports. The two results leave the design separately, so the
/// oracle's random ready/valid backpressures the chains independently.
let twoStreamSplit =
    defModule
        "TwoStreamSplit"
        (fun p ->
            (streamInputPorts p "in" byteLayout,
             streamOutputPorts p "b_out" byteLayout,
             streamOutputPorts p "c_out" byteLayout))
        (fun (inPorts, bOutPorts, cOutPorts) ->
            let low, high = byteSplitter "split" (streamSource inPorts)
            low |> Stream.stage satInc |> streamSink bOutPorts
            high |> Stream.stage (fun d -> lit 0xFFUL 8 - d) |> streamSink cOutPorts)

/// An instance's staging wires land in the PARENT's namespace: instance `b`
/// wired to a child port `low_data` stages `b_low_data`, which is also what
/// `Stream.out "b_low"` names its data port. Exists to prove the declaration
/// check fires — before it, the name was declared twice (once as a port, once
/// as a wire), the emitted Verilog redeclared the port as a wire and
/// self-assigned it, and nothing complained at elaboration, lint or synthesis.
/// Found writing `twoStreamSplit`, whose instance is named `split` for this
/// reason.
let declCollision () =
    (defModule
        "DeclCollision"
        (fun p ->
            (streamInputPorts p "in" byteLayout,
             streamOutputPorts p "b_low" byteLayout,
             streamOutputPorts p "c_out" byteLayout))
        (fun (inPorts, bLowPorts, cOutPorts) ->
            let low, high = byteSplitter "b" (streamSource inPorts)
            low |> streamSink bLowPorts
            high |> streamSink cOutPorts))
        .def

/// Test case 2: the WarpCPU shape — one stream splits by opcode (here the top
/// bit), the two paths run through pipelines of DIFFERENT depths, and an
/// arbitrated join funnels them back into one result stream. The join is
/// arbitration, not reordering: with unequal latencies results leave in
/// completion order, so the payload carries its identity (the pixel-beat
/// rule) — here the value range says which path a beat took.
let twoStreamSplitReplicateJoin =
    defModule
        "TwoStreamSplitReplicateJoin"
        (fun p -> (streamInputPorts p "in" byteLayout, streamOutputPorts p "final_out" byteLayout))
        (fun (inPorts, outPorts) ->
            let lowDepth = 5
            let highDepth = 10
            let low, high = byteSplitter "split" (streamSource inPorts)
            let lowPath = low |> Stream.stages lowDepth satInc
            let highPath = high |> Stream.stages highDepth (fun d -> lit 0xFFUL 8 - d)
            [ lowPath; highPath ] |> Stream.merge |> streamSink outPorts)

/// The frame-processor shape at toy scale: ONE command beat in, `rows` run
/// beats out (base, base+1, …), then ready for the next command — a stateful
/// beat expander, which is a 1→1 stream stage no matter how many beats it
/// mints. FramePod's row-run generator, extracted.
let private rowExpanderDef rows =
    defModule
        $"RowExpander%d{rows}"
        (fun p ->
            (p.inPort "cmd_data" 8,
             p.inPort "cmd_valid" 1,
             p.outPort "cmd_ready" 1,
             p.outPort "run_data" 8,
             p.outPort "run_valid" 1,
             p.inPort "run_ready" 1))
        (fun (cmdData, cmdValid, cmdReady, runData, runValid, runReady) ->
            let busy = regBit "busy"
            let baseReg = reg "base_v" 8
            let row = reg "row" 8

            bnot busy ==> cmdReady
            busy ==> runValid
            baseReg + row ==> runData

            ifElse [
                (cmdValid &&& bnot busy, fun () ->
                    lit 1UL 1 ==> busy
                    cmdData ==> baseReg
                    lit 0UL 8 ==> row)
                (otherwise, fun () ->
                If (busy &&& runReady) (fun () ->
                    row + lit 1UL 8 ==> row
                    If (eq row (lit (uint64 (rows - 1)) 8)) (fun () -> lit 0UL 1 ==> busy))) ])

let private rowExpander rows instName (s: Stream<Expr>) =
    let cmdData, cmdValid, cmdReady, runData, runValid, runReady = (rowExpanderDef rows).NewNamed instName

    s.payload ==> cmdData
    s.valid ==> cmdValid
    cmdReady ==> s.ready
    registerStreamReady runReady

    { payload = runData
      valid = runValid
      ready = runReady
      layout = byteLayout }

/// The row-gatherer shape at toy scale: consume every result beat, fold it
/// into state (a sum — order-insensitive, because the farm reorders), count,
/// and raise `frame_done` when a frame's worth has landed. Completion lives
/// where the results land — FramePod's written-count FSM, extracted.
let private beatGathererDef rows =
    defModule
        $"BeatGatherer%d{rows}"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "gathered" 8,
             p.outPort "beat_count" 16,
             p.outPort "frame_done" 1))
        (fun (inData, inValid, inReady, gathered, beatCount, frameDone) ->
            let sum = reg "sum" 8
            let count = reg "count" 16

            lit 1UL 1 ==> inReady

            If inValid (fun () ->
                sum + inData ==> sum
                count + lit 1UL 16 ==> count)

            sum ==> gathered
            count ==> beatCount
            eq count (lit (uint64 rows) 16) ==> frameDone)

let private beatGatherer rows instName (s: Stream<Expr>) =
    let inData, inValid, inReady, gathered, beatCount, frameDone = (beatGathererDef rows).NewNamed instName

    s.payload ==> inData
    s.valid ==> inValid
    inReady ==> s.ready
    (gathered, beatCount, frameDone)

/// SlowWorker's port bundle: one stream in, one stream out.
type private SlowWorkerIo =
    { input: StreamPorts
      output: StreamPorts }

/// The calculation, with no streams in sight — the recursion
///
///     let rec delay n out =
///         if n = 0 then out
///         else delay (n - 1) out
///
/// applied as `delay cycles (satInc beat)`, taken apart per `Iteration`: the
/// arguments `(n, out)` are the state, each recursive call is one cycle's
/// `step`, `n = 0` is the base case, and `out` is what it returns.
let private grind cycles : Iteration<Expr, Expr * Expr, Expr> =
    { state = layout2 ("remaining", 8) ("value", 8)
      init = fun beat -> lit (uint64 cycles) 8, satInc beat
      step = fun (n, out) -> n - lit 1UL 8, out
      finished = fun (n, _) -> eq n (lit 0UL 8)
      result = fun (_, out) -> out }

/// A worker that GRINDS: accepts a beat, works `cycles` cycles, then offers
/// the bumped result — throughput 1/(cycles+3) through the worker FSM's three
/// phases, the rowProcessor reality. A pipelined stage never needs replication
/// (1 beat/cycle already); this is the shape whose farm width is worth
/// sweeping.
let private slowWorker cycles =
    defModule
        $"SlowWorker%d{cycles}"
        (fun p ->
            { input = streamInPorts p "in" 8
              output = streamOutPorts p "out" 8 })
        (fun io ->
            streamOfPorts byteLayout io.input
            |> streamIterate byteLayout (grind cycles)
            |> streamToPorts io.output)

/// The stream feel — an ordinary function over the bundle, one named instance
/// per call.
let private slowWorkerOf cycles instName (s: Stream<Expr>) : Stream<Expr> =
    let c = (slowWorker cycles).NewNamed instName
    streamToPorts c.input s
    streamOfPorts byteLayout c.output

/// Test case 3: the decomposed frame pipeline — the FramePod refactor shape,
/// proven at toy scale. Command source, beat expander, a farm of three
/// workers with UNEQUAL depths (1/2/3 stages), and a gatherer that owns
/// completion. Every link is `|>` — the 1→1 wormhole IS reverse application —
/// and the two multiplicity changes live inside `farm`, invisible here.
let framePipeline =
    defModule
        "FramePipeline"
        (fun p ->
            (streamInputPorts p "cmd" byteLayout,
             p.outPort "gathered" 8,
             p.outPort "beat_count" 16,
             p.outPort "frame_done" 1))
        (fun (cmdPorts, gatheredOut, beatCountOut, frameDoneOut) ->
            let rows = 4

            let gathered, beatCount, frameDone =
                streamSource cmdPorts
                |> rowExpander rows "expand"
                |> Stream.farm 3 (fun i lane -> lane |> Stream.stages (i + 1) satInc)
                |> beatGatherer rows "gather"

            gathered ==> gatheredOut
            beatCount ==> beatCountOut
            frameDone ==> frameDoneOut)

/// Test case 4: the design-space sweep. The SAME pipeline, elaborated at a
/// worker count passed as data — so finding the right width is a plain loop
/// over configs reading the probes (see the sweep in Main). `runs` blocked
/// means every worker was busy (add workers); `results` starved means the
/// farm cannot feed the sink (add workers); `runs` starved at the same width
/// means the expander is the wall instead.
let sweepPipeline nWorkers =
    defModule
        $"SweepPipeline_w%d{nWorkers}"
        (fun p ->
            (streamInputPorts p "cmd" byteLayout,
             p.outPort "gathered" 8,
             p.outPort "beat_count" 16,
             p.outPort "frame_done" 1))
        (fun (cmdPorts, gatheredOut, beatCountOut, frameDoneOut) ->
            let rows = 8

            let gathered, beatCount, frameDone =
                streamSource cmdPorts
                |> Stream.pipeline
                    [ Stream.specOf "expand" (rowExpander rows)
                      Stream.specOf "worker" (slowWorkerOf 3) |> Stream.lanes nWorkers |> Stream.probed "runs" ]
                |> Stream.probe "results"
                |> beatGatherer rows "gather"

            gathered ==> gatheredOut
            beatCount ==> beatCountOut
            frameDone ==> frameDoneOut)

/// `lineWindow` at its Life-shaped corner: 3-row windows over 1-bit cells,
/// toroidal edges, four rows of eight cells. The loader's share of the
/// contract (the two vertical-halo beats) is whatever the poke loop sends
/// first and last. The living check drives two frames through it and asserts
/// the window's own claim — output row r's three rows equal input rows
/// r−1, r, r+1 with the edge columns right — plus the re-arm between frames
/// and that backpressure loses nothing.
let windowSweep =
    // The window's own output shape: three widened rows per beat (8 cells
    // plus one wrap column each side), named as `lineWindow` names them.
    let windowedRows: Layout<Expr list> =
        { fields = [ for i in 0..2 -> $"row%d{i}", 10 ]
          pack = fun rows -> rows
          unpack = fun nets -> nets }

    defModule
        "WindowSweep"
        (fun p -> (streamInputPorts p "in" (layout1 ("row", 8)), streamOutputPorts p "win" windowedRows))
        (fun (inPorts, winPorts) ->
            streamSource inPorts
            |> lineWindow
                { rows = 3
                  edgeColumns = 1
                  cellBits = 1
                  edge = Edge.Wrap }
                4
            |> streamSink winPorts)

/// `lineWindow` over multi-bit cells: a 3×3 box blur on rows of four 8-bit
/// pixels, borders replicated — each output pixel is the mean of its
/// neighbourhood, `sum >>> 3` standing in for /9 as blurs on silicon do. The
/// window hands back widened 48-bit rows; every slice below is plain bit
/// arithmetic on them, which is the point of widening at the window.
let pixelBlur =
    let pixels = 4

    defModule
        "PixelBlur"
        (fun p ->
            (streamInputPorts p "in" (layout1 ("row", pixels * 8)),
             streamOutputPorts p "out" (layout1 ("row", pixels * 8))))
        (fun (inPorts, outPorts) ->
            let blurRow (win: Expr list) =
                match win with
                | [ above; centre; below ] ->
                    catAll
                        [ for c in pixels - 1 .. -1 .. 0 ->
                              // Column c's 3×3 patch: widened bit groups c, c+1, c+2 of each row.
                              let cells =
                                  [ for row in [ above; centre; below ] do
                                        for k in 0..2 -> slice ((c + k) * 8 + 7) ((c + k) * 8) row ]

                              let sum = wire $"blur_sum%d{c}" 12
                              (cells |> List.map (pad 12) |> List.reduce (+)) ==> sum
                              // sum >>> 3, low 8 bits — one slice of the named sum.
                              slice 10 3 sum ]
                | _ -> failwith "pixelBlur: a 3-row window"

            streamSource inPorts
            |> lineWindow
                { rows = 3
                  edgeColumns = 1
                  cellBits = 8
                  edge = Edge.Clamp }
                4
            |> Stream.mapTo (layout1 ("row", pixels * 8)) blurRow
            |> streamSink outPorts)

/// `streamWindow` unframed — the 1D case: a four-tap moving average over
/// 16-bit samples, the shape of every FIR's front end. No frames, so the
/// window primes once and then slides forever, one output per input.
let movingAverage =
    defModule
        "MovingAverage"
        (fun p ->
            (streamInputPorts p "in" (layout1 ("sample", 16)),
             streamOutputPorts p "out" (layout1 ("sample", 16))))
        (fun (inPorts, outPorts) ->
            let average (taps: Expr list) =
                let sum = wire "tap_sum" 18
                (taps |> List.map (pad 18) |> List.reduce (+)) ==> sum
                shr 2 sum

            streamSource inPorts
            |> streamWindow None 4
            |> Stream.mapTo (layout1 ("sample", 16)) average
            |> streamSink outPorts)

/// Static-irregular access — the middle row of the access-class table
/// (`notes/STREAMING_ARCH.md`): the connection pattern is irregular but KNOWN
/// at elaboration, so the gather compiles to wiring. Four taps at fixed
/// positions over a LUTRAM activation vector: no window, no schedule, no
/// cycles — `memRead` at a literal address is a mux, and the whole sparse dot
/// is one combinational expression.
let sparseDot =
    defModule
        "SparseDot"
        (fun p -> (p.inPort "fill_addr" 3, p.inPort "fill_data" 8, p.inPort "fill_enable" 1, p.outPort "dot" 13))
        (fun (fillAddr, fillData, fillEnable, dot) ->
            // index, weight — a sparse row of a weight matrix, fixed at elaboration.
            let taps = [ 1, 3UL; 3, 1UL; 4, 2UL; 6, 5UL ]
            let activations = distributedMem "act" 3 8

            memWrite activations fillAddr fillData fillEnable

            (taps
             |> List.map (fun (i, w) -> pad 13 (mul (memRead activations (lit (uint64 i) 3)) (lit w 3)))
             |> List.reduce (+))
            ==> dot)

/// `rangeStream`: the read schedule of every scan, as a value — a start pulse
/// in, the indices 0…4 offered in order, then quiet until the next pulse. The
/// living check walks it twice with a stalling consumer: exactly five
/// indices per pulse, nothing between pulses.
let indexSweep =
    defModule
        "IndexSweep"
        (fun p -> (p.inPort "start" 1, streamOutputPorts p "out" (layout1 ("index", 3))))
        (fun (start, outPorts) -> streamSink outPorts (rangeStream start 5))

/// The pooling control's two states; the pass runs while `run` holds, one
/// full sweep per count of the generation counter.
type private PoolPhase =
    | Resting
    | Pooling

/// The pool's port bundle — the same free-running contract the Game of Life
/// sweep speaks: `run` as a level, `generation` counting completed passes,
/// fill and probe by global row.
type PoolIo =
    { run: Expr
      generation: Expr
      fillAddr: Expr
      fillData: Expr
      fillEnable: Expr
      probeAddr: Expr
      probeData: Expr }

/// 5×5 max pooling over an 8×8 map of 8-bit cells, stride 1 — iterated, it is
/// greyscale dilation, which is what makes it a living design rather than a
/// one-shot: each pass is a "generation" and the model is checkable across
/// several. The representative second user of `bandedWorld` beside Game of
/// Life, and the one that forced its honest parameters: a 5-row window means
/// **haloRows = 2**, and pooling wants **Zero** beyond the map's rim where
/// Life wanted a torus. The store, the schedule, the halo routing and the
/// double buffer are all `bandedWorld`'s; the fifteen lines here are pooling.
let maxPoolDilate =
    let mapRows = 8
    let columns = 8
    let cellBits = 8
    let rowBits = columns * cellBits
    let addrWidth = bitsToHold mapRows

    defModule
        "MaxPoolDilate"
        (fun p ->
            { run = p.inPort "run" 1
              generation = p.outPort "generation" 16
              fillAddr = p.inPort "fill_addr" addrWidth
              fillData = p.inPort "fill_data" rowBits
              fillEnable = p.inPort "fill_enable" 1
              probeAddr = p.inPort "probe_addr" addrWidth
              probeData = p.outPort "probe_data" rowBits })
        (fun io ->
            let generationCount = reg "generation_count" 16
            generationCount ==> io.generation

            let phase = machine "pool_phase" [ Resting; Pooling ]
            let startPulse = wireBit "pool_start"
            (phase.Is Resting &&& io.run) ==> startPulse
            If startPulse (fun () -> phase.Goto Pooling)

            let flip = wireBit "pool_flip"

            let world =
                bandedWorld
                    { engines = 2
                      bandRows = 4
                      rowBits = rowBits
                      haloRows = 2
                      verticalEdge = Edge.Zero }
                    startPulse
                    flip
                    io.fillAddr
                    io.fillData
                    (io.fillEnable &&& phase.Is Resting)
                    io.probeAddr

            world.probe ==> io.probeData

            // Each pairwise max lands on a named wire. Not cosmetic: `maxOf` uses
            // both operands twice, and a bare `Expr` is re-inlined at every use —
            // folded 25 deep, the unnamed form is 2^24 copies of the innermost
            // slices at emission, the `splitBudget` story re-enacted. Named, it
            // is 24 wires per column.
            let mutable maxIndex = 0

            let maxOf (a: Expr) (b: Expr) =
                let best = wire $"pool_max%d{maxIndex}" cellBits
                maxIndex <- maxIndex + 1
                mux (lt a b) b a ==> best
                best

            // Column c of the original map is widened group c+2, so its 5-wide
            // reach is groups c .. c+4 of each of the five rows: 25 slices, one
            // max tree, no modular arithmetic — the widening's whole point.
            let poolBeat (win: Expr list) =
                catAll
                    [ for c in columns - 1 .. -1 .. 0 ->
                          [ for row in win do
                                for k in 0..4 -> slice ((c + k) * cellBits + cellBits - 1) ((c + k) * cellBits) row ]
                          |> List.reduce maxOf ]

            for g in 0..1 do
                world.sweeps[g]
                |> lineWindow
                    { rows = 5
                      edgeColumns = 2
                      cellBits = cellBits
                      edge = Edge.Zero }
                    4
                |> Stream.mapTo (layout1 ("row", rowBits)) poolBeat
                |> world.landings[g]

            (phase.Is Pooling &&& world.swept) ==> flip

            If flip (fun () ->
                generationCount + lit 1UL 16 ==> generationCount
                phase.Goto Resting))

/// The combinational pool's bundle: a grid of byte ports in, a grid of byte
/// ports out, both indexed `[y][x]`. No start, no complete, no clock — the
/// answer is on the outputs the same cycle the inputs are.
type ArrayPoolIo =
    { inputArray: Expr list list
      outputArray: Expr list list }

/// The same 2×2/stride-2 max pool as `maxPool2x2`, as pure combinational
/// logic: an 8×8 grid of byte inputs, a 4×4 grid of byte outputs, every
/// output a three-comparator cone over its four bytes. No start, no
/// complete, no clock — the answer is on the outputs the same cycle the
/// inputs are. The pair is the area-buys-time lesson at layer scale, and
/// `maxPool2x2` also instantiates this block unchanged, three abreast, as
/// its per-window compute — one fixed module serving standalone and tiled.
///
/// The max tree here is UNNAMED expression code, deliberately, where
/// `maxPoolDilate`'s is landed on wires: `maxOf` duplicates its operands, so
/// an unnamed fold doubles per level — fatal at 25 values, and bounded and
/// harmless at four, where the whole cone is a handful of terms.
let maxPoolCombinational =
    let mapRows = 8
    let columns = 8
    let cellBits = 8
    let outRows = mapRows / 2
    let outColumns = columns / 2

    defModule
        "MaxPoolCombinational"
        (fun p ->
            { inputArray = inPortArray p "in" mapRows columns cellBits
              outputArray = outPortArray p "out" outRows outColumns cellBits })
        (fun io ->
            let maxOf a b = mux (lt a b) b a

            for y in 0 .. outRows - 1 do
                for x in 0 .. outColumns - 1 do
                    [ for dy in 0..1 do
                          for dx in 0..1 -> io.inputArray[2 * y + dy][2 * x + dx] ]
                    |> List.reduce maxOf
                    ==> io.outputArray[y][x])

/// A one-pass layer's port bundle: a start pulse in, `complete` high while
/// the result sits in the destination, fill and probe for the host. No
/// generation counter — a CNN layer runs once per input, and a counter would
/// dress a done flag up as a dynamic it does not have.
type LayerIo =
    { start: Expr
      complete: Expr
      fillAddr: Expr
      fillData: Expr
      fillEnable: Expr
      probeAddr: Expr
      probeData: Expr }

/// A CNN pooling layer as CNNs actually use one: 2×2 max, **stride 2** — a
/// 24×24 map of 8-bit cells in, a 12×12 map out, **one pass per start
/// pulse**, `complete` while the pooled map is valid.
///
/// **The compute is the unchanged 8×8 `maxPoolCombinational`, by tiling.**
/// A 24×24 map is a 3×3 grid of 8×8 tiles, and because 8 is even, no
/// 2×2/stride-2 window ever crosses a tile edge — so the block composes
/// without modification. Vertically: `streamWindow` holds 8 rows and
/// `streamStride` keeps every 8th window, one per tile row. Horizontally:
/// three instances of the block, side by side, pool a kept window in the
/// same cycle. Each kept window therefore yields FOUR pooled rows at once,
/// which land in the destination as one 384-bit word; the probe unpacks a
/// word and a lane back into rows, so the host still reads by row.
let maxPool2x2 =
    let mapRows = 24
    let columns = 24
    let cellBits = 8
    let rowBits = columns * cellBits
    // The fixed core's edge, and the tiling it induces.
    let tile = 8
    let tilesAcross = columns / tile
    let tilesDown = mapRows / tile
    let outRows = mapRows / 2
    let outColumns = columns / 2
    let outRowBits = outColumns * cellBits
    // Pooled rows per kept window, packed into one destination word.
    let groupRows = tile / 2
    let groupBits = groupRows * outRowBits
    let addrWidth = bitsToHold mapRows
    let groupAddrWidth = bitsToHold tilesDown
    let probeAddrWidth = bitsToHold outRows
    let laneWidth = bitsToHold groupRows
    // `streamWindow` over mapRows beats emits mapRows − 7 eight-row windows;
    // the stride keeps positions 0, 8, 16 — the tile rows.
    let windowsPerPass = mapRows - (tile - 1)
    let writtenWidth = bitsToHold (tilesDown + 1)

    defModule
        "MaxPool2x2"
        (fun p ->
            { start = p.inPort "start" 1
              complete = p.outPort "complete" 1
              fillAddr = p.inPort "fill_addr" addrWidth
              fillData = p.inPort "fill_data" rowBits
              fillEnable = p.inPort "fill_enable" 1
              probeAddr = p.inPort "probe_addr" probeAddrWidth
              probeData = p.outPort "probe_data" outRowBits })
        (fun io ->
            let phase = machine "pool_phase" [ Resting; Pooling ]
            let startPulse = wireBit "pool_start"
            (phase.Is Resting &&& io.start) ==> startPulse
            If startPulse (fun () -> phase.Goto Pooling)

            let src = blockMem "src" addrWidth rowBits
            let dst = blockMem "dst" groupAddrWidth groupBits
            memWrite src io.fillAddr io.fillData (io.fillEnable &&& phase.Is Resting)

            // The probe splits its row address into a word and a lane; the
            // lane select is registered to arrive with the synchronous word.
            let word = (memReadPort dst (slice (probeAddrWidth - 1) laneWidth io.probeAddr)).data
            let laneHeld = reg "probe_lane" laneWidth
            slice (laneWidth - 1) 0 io.probeAddr ==> laneHeld

            selectIndexed laneHeld [ for r in 0 .. groupRows - 1 -> slice ((r + 1) * outRowBits - 1) (r * outRowBits) word ]
            ==> io.probeData

            // Three unchanged 8×8 blocks pool a kept window side by side; the
            // slicing into their byte grids and the packing of their outputs
            // into the group word is all this layer writes.
            let tiledPool (win: Expr list) =
                // The newest window row is the live read-port word — name the
                // rows so they slice.
                let rows =
                    [ for r, row in List.indexed win ->
                          let named = wire $"pool_row%d{r}" rowBits
                          row ==> named
                          named ]

                let cores =
                    [ for t in 0 .. tilesAcross - 1 -> maxPoolCombinational.NewNamed $"tile%d{t}" ]

                for t in 0 .. tilesAcross - 1 do
                    for r in 0 .. tile - 1 do
                        for x in 0 .. tile - 1 do
                            let column = t * tile + x

                            slice (column * cellBits + cellBits - 1) (column * cellBits) rows[r]
                            ==> cores[t].inputArray[r][x]

                catAll
                    [ for r in groupRows - 1 .. -1 .. 0 ->
                          catAll
                              [ for j in outColumns - 1 .. -1 .. 0 ->
                                    cores[j / (tile / 2)].outputArray[r][j % (tile / 2)] ] ]

            let pooledGroups =
                rangeStream startPulse mapRows
                |> (blockReadWindow "src_port" src).read
                |> streamWindow (Some mapRows) tile
                |> streamStride (Some windowsPerPass) tile
                |> Stream.mapTo (layout1 ("group", groupBits)) tiledPool

            let written = reg "written" writtenWidth
            lit 1UL 1 ==> pooledGroups.ready
            memWrite dst (slice (groupAddrWidth - 1) 0 written) pooledGroups.payload pooledGroups.valid
            If pooledGroups.valid (fun () -> written + lit 1UL writtenWidth ==> written)
            If startPulse (fun () -> lit 0UL writtenWidth ==> written)

            let landed = eq written (lit (uint64 tilesDown) writtenWidth)
            If (phase.Is Pooling &&& landed) (fun () -> phase.Goto Resting)

            // High from the pass's end until the next start clears `written`.
            (phase.Is Resting &&& landed) ==> io.complete)

/// Dynamic-irregular access — the bottom row of the table: the second address
/// exists only after the first read answers, so no window can precompute the
/// reuse and the gather pays real cycles. A two-hop indirect load, B[A[i]],
/// as a stream worker: the protocol FSM owns the handshake, a hop counter
/// owns the walk, and each hop is one synchronous read of a block — the
/// "function that grabs what it needs", as hardware.
let indirectGather =
    defModule
        "IndirectGather"
        (fun p ->
            (p.inPort "fill_t_addr" 3,
             p.inPort "fill_t_data" 3,
             p.inPort "fill_t_enable" 1,
             p.inPort "fill_v_addr" 3,
             p.inPort "fill_v_data" 8,
             p.inPort "fill_v_enable" 1,
             streamInputPorts p "in" (layout1 ("index", 3)),
             streamOutputPorts p "out" (layout1 ("value", 8))))
        (fun (fillTAddr, fillTData, fillTEnable, fillVAddr, fillVData, fillVEnable, inPorts, outPorts) ->
            let pointers = blockMem "pointers" 3 3
            let values = blockMem "values" 3 8

            memWrite pointers fillTAddr fillTData fillTEnable
            memWrite values fillVAddr fillVData fillVEnable

            let requests = streamSource inPorts
            let st, out = streamFsm requests (layout1 ("value", 8))

            let index = reg "index" 3
            If (st.Is Accepting &&& requests.valid) (fun () -> requests.payload ==> index)

            // Both ports read continuously; each answer is a cycle behind its
            // address, so the chain settles two cycles into Working.
            let pointerWord = (memReadPort pointers index).data
            let valueWord = (memReadPort values pointerWord).data

            let hop = reg "hop" 2

            st.If Working (fun () ->
                ifElse
                    [ eq hop (lit 2UL 2),
                      fun () ->
                          lit 0UL 2 ==> hop
                          st.Goto Offering
                      otherwise, fun () -> hop + lit 1UL 2 ==> hop ])

            valueWord ==> out.payload
            streamSink outPorts out)

/// Byte → pair: the payload TYPE changes across this stage (Expr becomes
/// Expr * Expr), which is what the arity-typed pipelines exist for.
let private widenStageDef =
    defModule
        "WidenPair"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_a" 8,
             p.outPort "out_b" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (inData, inValid, inReady, outA, outB, outValid, outReady) ->
            inData ==> outA
            satInc inData ==> outB
            inValid ==> outValid
            outReady ==> inReady)

let private widenStage instName (s: Stream<Expr>) =
    let inData, inValid, inReady, outA, outB, outValid, outReady = widenStageDef.NewNamed instName

    s.payload ==> inData
    s.valid ==> inValid
    inReady ==> s.ready
    registerStreamReady outReady

    { payload = (outA, outB)
      valid = outValid
      ready = outReady
      layout = layout2 ("a", 8) ("b", 8) }

/// Pair → byte, the narrowing half — REGISTERED, because a farm worker is an
/// async boundary: a fully combinational worker couples the dispatch grant to
/// the merge arbitration into a valid/ready loop, which elaboration rejects
/// (the loop check caught exactly that when this stage was first written
/// combinational).
let private sumStageDef =
    defModule
        "PairSum"
        (fun p ->
            (p.inPort "in_a" 8,
             p.inPort "in_b" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_sum" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (inA, inB, inValid, inReady, outSum, outValid, outReady) ->
            let sumR = reg "sumR" 8
            let validR = regBit "validR"
            (bnot validR ||| outReady) ==> inReady
            mux inReady (inA + inB) sumR ==> sumR
            mux inReady inValid validR ==> validR
            sumR ==> outSum
            validR ==> outValid)

let private sumStage instName (s: Stream<Expr * Expr>) =
    let inA, inB, inValid, inReady, outSum, outValid, outReady = sumStageDef.NewNamed instName

    let a, b = s.payload
    a ==> inA
    b ==> inB
    s.valid ==> inValid
    inReady ==> s.ready
    registerStreamReady outReady

    { payload = outSum
      valid = outValid
      ready = outReady
      layout = layout1 ("sum", 8) }

/// The type-changing pipeline under the oracle: byte → pair → sum through
/// `pipeline2`, with the narrowing stage FARMED (2 lanes) — a spec farms
/// across a payload-type change exactly as it does within one.
let typedPipeline =
    defModule
        "TypedPipeline"
        (fun p -> (streamInputPorts p "in" byteLayout, streamOutputPorts p "out" (layout1 ("sum", 8))))
        (fun (inPorts, outPorts) ->
            streamSource inPorts
            |> Stream.pipeline2
                (Stream.specOf "widen" widenStage)
                (Stream.specOf "sum" sumStage |> Stream.lanes 2 |> Stream.probed "pairs")
            |> streamSink outPorts)

/// A probed link under the oracle: the counters ride output ports, so the
/// blocked/starved semantics are differentially verified against Verilator
/// under random ready/valid, not just asserted in the Sim.
let probedPipe =
    defModule
        "ProbedPipe"
        (fun p ->
            (streamInputPorts p "in" byteLayout,
             streamOutputPorts p "out" byteLayout,
             p.outPort "blocked_count" 32,
             p.outPort "starved_count" 32))
        (fun (inPorts, outPorts, blockedCount, starvedCount) ->
            let stage = streamStageFor byteLayout
            let source, counters = streamProbeCounters "ingress" (streamSource inPorts)
            streamSink outPorts (stage source)

            counters.blocked ==> blockedCount
            counters.starved ==> starvedCount)

/// A w1p pulse register under the oracle: each accepted write of 1 to offset 0
/// bumps a counter read back at offset 4 — write-fire gating, bit-0 decode and
/// read-as-zero all differentially exercised.
let axiPulse =
    defModuleClocked
        axiClock
        "AxiPulse"
        (fun p -> axiLiteSlavePorts p 4)
        (fun slavePorts ->
            let count = reg "pulse_count" 8

            let pulses, _ =
                axiLiteSlaveFullOn slavePorts [ "go", 0x0UL ] [] [ 0x4UL, count ] []

            match pulses with
            | [ go ] -> If go (fun () -> count + lit 1UL 8 ==> count)
            | _ -> failwith "expected exactly one pulse register")

/// The lifted neighborhood gather under the oracle: a 3x3 grid of input bits,
/// counted through every stencil/edge combination Life and its relatives use.
/// Purely combinational, so 50 random grids differentially pin the gather
/// order, the dead border, the wrap and the clamp all at once.
let neighborCount =
    defModule
        "NeighborCount"
        (fun p ->
            ([ for y in 0..2 -> [ for x in 0..2 -> p.inPort $"g{y}{x}" 1 ] ],
             p.outPort "moore" 4,
             p.outPort "corner" 4,
             p.outPort "vonNeumann" 3,
             p.outPort "wrapped" 4,
             p.outPort "clamped" 4))
        (fun (grid, moore, corner, vonNeumann, wrapped, clamped) ->
            let count w stencil edge y x = countWhere w id (neighborhood stencil edge grid y x)

            count 4 Stencil.Moore Edge.Zero 1 1 ==> moore
            count 4 Stencil.Moore Edge.Zero 0 0 ==> corner
            count 3 Stencil.VonNeumann Edge.Zero 1 1 ==> vonNeumann
            count 4 Stencil.Moore Edge.Wrap 0 0 ==> wrapped
            count 4 Stencil.Moore Edge.Clamp 2 2 ==> clamped)

/// The declarative register map exercised whole: every entry kind in one
/// aperture. 0x000 is the overlay word (ID reads, two pulse bits write);
/// `count` and `high` pack one read word; `wrapIrq` is the w1c + irq path;
/// `pattern` is a host-written window the hardware sync-reads back out
/// through `patLow`.
/// Built with `buildRegMapPinned`, which is also this map's second job: it is
/// the registered demonstration of the builder, and `regMapBuilderMatchesByHand`
/// asserts it is entry-for-entry the same map as the hand-written version.
///
/// No offset appears here. The two windows still land at 0x040 and 0x080 —
/// `RwWindow`/`RoWindow` round the cursor up to the window's own size, which is
/// the alignment rule that made them fiddly to place by hand.
type ScratchRegs =
    { id: RegEntry
      bump: RegEntry
      clear: RegEntry
      threshold: RegEntry
      count: RegEntry
      high: RegEntry
      wrapIrq: RegEntry
      patLow: RegEntry
      pattern: RegEntry
      trace: RegEntry }

let scratchRegs, scratchMap =
    buildRegMapPinned 8 (fun r ->
        // The overlay word: the identity answers reads, two pulse bits take
        // the write side.
        let id, bump, clear =
            r.Word(fun w -> w.Const("id", 0xF5C0FFEEUL), w.Pulse "bump", w.Pulse "clear")

        let threshold = r.RwReg("threshold", 16, 0UL)
        let count, high = r.Word(fun w -> w.Field("count", 8), w.Field("high", 1))
        let wrapIrq = r.Word(fun w -> w.W1c "wrapIrq")
        let patLow = r.RoField("patLow", 8)

        { id = id
          bump = bump
          clear = clear
          threshold = threshold
          count = count
          high = high
          wrapIrq = wrapIrq
          patLow = patLow
          pattern = r.RwArray("pattern", 16)
          trace = r.RoArray("trace", 16) })

let regMapScratch =
    defModuleClocked
        axiClock
        "RegMapScratch"
        (fun p -> (axiLiteSlavePorts p scratchMap.apertureAddrWidth, p.outPort "irq" 1))
        (fun (slavePorts, irqOut) ->
            let regs = regMapSlave slavePorts scratchMap

            let count = reg "count_reg" 8
            let bump = regs.pulse scratchRegs.bump

            ifElse [
                (regs.pulse scratchRegs.clear, fun () -> lit 0UL 8 ==> count)
                (otherwise, fun () -> If bump (fun () -> count + lit 1UL 8 ==> count)) ]

            regs.drive scratchRegs.count count
            regs.drive scratchRegs.high (bnot (lt count (slice 7 0 (regs.value scratchRegs.threshold))))
            regs.setBit scratchRegs.wrapIrq (bump &&& eq count (lit 255UL 8))

            // `hostTurn` is ignored on purpose: patternWord only drives a
            // read-only field, so the one-cycle glitch during a host readback is
            // observable only by the very host doing the read — mid-transaction,
            // on a different offset.
            let patternWord = (regs.readArray scratchRegs.pattern (slice 3 0 count)).read.data
            regs.drive scratchRegs.patLow (slice 7 0 patternWord)

            // The mirror window: the design writes, the host reads. Each bump
            // leaves a marked word at the count it happened on, so a host read of
            // trace[i] proves the design's write port and the host's read port are
            // the same array.
            let trace = regs.driveArray scratchRegs.trace
            memWrite trace (slice 3 0 count) (cat (lit 0xC5UL 24) count) bump

            regs.irq ==> irqOut)

/// Four animated rows (free-running counters, so every frame differs) through
/// `snapshotSource` and `streamConflate3` at ports: the testbench's random
/// capture/release/writer-idle/backpressure pokes differentially exercise the
/// slot rotation, the capture queue, the drain gate and the overrun counter.
let snapshotConflate =
    defModule
        "SnapshotConflate"
        (fun p ->
            (p.inPort "snap_capture" 1,
             p.inPort "snap_release" 1,
             p.inPort "writer_idle" 1,
             p.outPort "host_ready" 1,
             p.outPort "host_slot" 2,
             p.outPort "host_overrun" 8,
             p.outPort "host_irq" 1,
             streamOutputPorts p "beat" (layout3 ("slot", 2) ("index", 2) ("data", 8))))
        (fun (capture, release, writerIdle, readyOut, slotOut, overrunOut, irqOut, beatPorts) ->
            let rows =
                [ for i in 0..3 ->
                      let r = regInit $"row%d{i}" 8 (uint64 (i * 16))
                      r + lit 1UL 8 ==> r
                      r ]

            let beats, status =
                snapshotSource "snap" rows
                |> streamConflate3 "conflate" capture release writerIdle

            status.ready ==> readyOut
            status.readSlot ==> slotOut
            status.overrun ==> overrunOut
            status.irq ==> irqOut
            streamSink beatPorts beats)

/// The whole snapshot path closed through the write master: conflate beats
/// become AXI beats at `(slot, index)`-derived addresses, and the master's
/// own drained level feeds the conflate's publish gate — the coherency loop a
/// DDR-backed snapshot needs. `m_axi` at the boundary, so the oracle throws
/// random slave timing at it; the demo runs the same design against the fake
/// DDR and asserts frame coherency.
let snapshotDdr =
    defModule
        "SnapshotDdr"
        (fun p ->
            (p.inPort "snap_capture" 1,
             p.inPort "snap_release" 1,
             p.outPort "host_ready" 1,
             p.outPort "host_slot" 2,
             p.outPort "host_overrun" 8,
             axiWriteBusPorts p "m_axi" 32 32))
        (fun (capture, release, readyOut, slotOut, overrunOut, busPorts) ->
            let rows =
                [ for i in 0..3 ->
                      let r = regInit $"row%d{i}" 8 (uint64 (i * 16))
                      r + lit 1UL 8 ==> r
                      r ]

            let writerIdle = wireBit "writer_idle_w"

            let beats, status =
                snapshotSource "snap" rows
                |> streamConflate3 "conflate" capture release writerIdle

            status.ready ==> readyOut
            status.readSlot ==> slotOut
            status.overrun ==> overrunOut

            let axiBeats =
                beats
                |> streamMapTo (axiWriteBeatLayout 32 32) (fun (slot, index, data) ->
                    let wordAddr = cat (cat slot index) (lit 0UL 2) // (slot*4 + index) * 4 bytes
                    cat (lit 0UL 26) wordAddr, cat (lit 0UL 24) data, lit 0xFUL 4)

            axiMasterWriterWithIdleOn (axiWriteBusOf busPorts) 4 axiBeats ==> writerIdle)

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
type AudioOpsIo =
    { x: Input
      advance: Input
      b0: Input
      b1: Input
      b2: Input
      a1: Input
      a2: Input
      y: Output
      firOut: Output
      left: Output
      right: Output }

let audioOps =
    defModule
        "AudioOps"
        (fun p ->
            { x = p.inPort "x" sampleWidth
              advance = p.inPort "advance" 1
              b0 = p.inPort "coeff_b0" biquadCoeffWidth
              b1 = p.inPort "coeff_b1" biquadCoeffWidth
              b2 = p.inPort "coeff_b2" biquadCoeffWidth
              a1 = p.inPort "coeff_a1" biquadCoeffWidth
              a2 = p.inPort "coeff_a2" biquadCoeffWidth
              y = p.outPort "y" sampleWidth
              firOut = p.outPort "fir_out" 18
              left = p.outPort "left" sampleWidth
              right = p.outPort "right" sampleWidth })
        (fun io ->
            let section = biquadSection "BiquadSection" "section"
            section io.x io.advance { b0 = io.b0; b1 = io.b1; b2 = io.b2; a1 = io.a1; a2 = io.a2 } ==> io.y

            // 4-tap [1,2,2,1] low-pass over the low byte of the sample.
            let sampleByte = wire "sample_byte" 8
            slice 7 0 io.x ==> sampleByte
            fir 8 8 [ 1UL; 2UL; 2UL; 1UL ] sampleByte ==> io.firOut

            // The packed stereo encoding round-trips: pack then unpack must be the
            // identity, which is the property the AXI register path depends on.
            let stereo = wire "stereo" sampleBits
            packSample io.x (bnot io.x) ==> stereo
            sampleLeft stereo ==> io.left
            sampleRight stereo ==> io.right)

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
    defModule
        "AudioChain"
        (fun p ->
            (p.inPort "volume" 16,
             p.inPort "mute" 1,
             p.inPort "threshold" sampleWidth,
             p.inPort "ratio" 8,
             p.inPort "attack" 16,
             p.inPort "releaseRate" 16,
             p.inPort "makeup" 16,
             p.inPort "limit" sampleWidth,
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout))
        (fun (volume, mute, threshold, ratio, attack, releaseRate, makeup, limit, inPorts, outPorts) ->
            let gain = audioGain "AudioGain" "gain" volume mute
            let compressor = audioCompressor "AudioCompressor" "compressor" threshold ratio attack releaseRate makeup
            let limiter = audioLimiter "AudioLimiter" "limiter" limit

            streamSource inPorts
            |> gain
            |> compressor
            |> limiter
            |> streamSink outPorts)

/// The tone generator sourcing the tone-control FIR: a stream source feeding a
/// pipelined-MAC stage. Together they cover the two shapes the earlier audio
/// designs did not — a module that *originates* a stream (its phase advances
/// on the handshake, not the clock) and one whose latency comes from an
/// `adderTreePipelined` rather than a hand-placed register.
let audioTone =
    defModule
        "AudioTone"
        (fun p ->
            (p.inPort "enable" 1,
             p.inPort "step" tonePhaseWidth,
             p.inPort "preset" 2,
             streamOutputPorts p "out" sampleLayout))
        (fun (enable, step, preset, outPorts) ->
            let tone = toneGenerator "ToneGenerator" "tone" enable step
            let filter = audioToneFilter "AudioToneFilter" "filter" preset

            tone |> filter |> streamSink outPorts)

/// The tone-control FIR alone, stream-driven, so a test can choose the input
/// rather than take whatever the oscillator produces.
let audioFirStage =
    defModule
        "AudioFirStage"
        (fun p ->
            (p.inPort "preset" 2, streamInputPorts p "in" sampleLayout, streamOutputPorts p "out" sampleLayout))
        (fun (preset, inPorts, outPorts) ->
            let filter = audioToneFilter "AudioToneFilter" "filter" preset
            streamSource inPorts |> filter |> streamSink outPorts)

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
    defModule
        "I2sLoopback"
        (fun p ->
            (streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout,
             p.outPort "mclk" 1,
             p.outPort "sclk" 1,
             p.outPort "lrclk" 1,
             p.outPort "serial" 1))
        (fun (inPorts, outPorts, mclk, sclk, lrclk, serial) ->
            let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster")

            let sdin =
                i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk (streamSource inPorts)

            let line = wireBit "line"
            sdin ==> line

            i2sRx "I2sRx" "rx" clocks.sclkRxTick clocks.lrclk line
            |> streamSink outPorts

            // The codec-facing pins, so the loopback covers what a wrapper drives.
            clocks.mclk ==> mclk
            clocks.sclk ==> sclk
            clocks.lrclk ==> lrclk
            line ==> serial)

/// The I2S framers exposed bare, for the isolation checks: each driven by a
/// hand-built ideal frame rather than by the clock generator, which is how the
/// the twin verifies them and the only way to test one without the other.
let i2sRxStage =
    defModule
        "I2sRxStage"
        (fun p ->
            (p.inPort "sclkTick" 1, p.inPort "lrclk" 1, p.inPort "sdout" 1, streamOutputPorts p "out" sampleLayout))
        (fun (sclkTick, lrclk, sdout, outPorts) ->
            i2sRx "I2sRx" "rx" sclkTick lrclk sdout
            |> streamSink outPorts)

let i2sTxStage =
    defModule
        "I2sTxStage"
        (fun p ->
            (p.inPort "sclkTick" 1, p.inPort "lrclk" 1, p.outPort "sdin" 1, streamInputPorts p "in" sampleLayout))
        (fun (sclkTick, lrclk, sdin, inPorts) ->
            i2sTx "I2sTx" "tx" sclkTick lrclk (streamSource inPorts) ==> sdin)

/// The 8-band multiband compressor as a stream stage.
let multibandStage =
    defModule
        "MultibandStage"
        (fun p ->
            (p.inPort "threshold" sampleWidth,
             p.inPort "ratio" 8,
             p.inPort "attack" 16,
             p.inPort "releaseRate" 16,
             List.init multibandBands (fun i -> p.inPort $"lg{i}" 16),
             List.init multibandBands (fun i -> p.inPort $"rg{i}" 16),
             streamInputPorts p "in" sampleLayout,
             streamOutputPorts p "out" sampleLayout,
             p.outPort "envelope" sampleWidth))
        (fun (threshold, ratio, attack, releaseRate, leftGains, rightGains, inPorts, outPorts, envOut) ->
            let stage, envelope =
                multibandCompressor "MultibandCompressor8" "mb" threshold ratio attack releaseRate leftGains rightGains
                |> fun apply -> apply (streamSource inPorts)

            streamSink outPorts stage
            envelope ==> envOut)
