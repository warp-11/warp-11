[<AutoOpen>]
module Warp11.Stdlib

/// Parameterised modules are built once per parameter set, so binding `mulOf 8` at
/// two use sites yields one `Mul8`, not two that the emitter has to reconcile.
let private memoize f =
    let cache = System.Collections.Generic.Dictionary<_, _>()

    fun k ->
        match cache.TryGetValue k with
        | true, v -> v
        | _ ->
            let v = f k
            cache[k] <- v
            v

// The semantics, independent of whether they become a Verilog module. Width is a
// parameter even where the logic ignores it — widths live in the values, so only
// the port declarations of a wrapped version ever need it.
let mulLogic (_: int) = mul
let adderLogic (_: int) = add

let satIncLogic w =
    let top = lit ((1UL <<< w) - 1UL) w
    fun x -> mux (eq x top) x (x + lit 1UL w)

/// The width-inferring form: widths live in the values, so a caller holding the
/// operand need not repeat what it already knows.
let satInc (x: Expr) = satIncLogic (width x) x

// The same semantics wrapped in a module. Same type as the logic above, so a use
// site cannot tell which it was given.
let mulOf =
    memoize (fun w -> liftBinary (fnModule2 $"Mul%d{w}" ("a", w) ("b", w) "product" (mulLogic w)))

let adderOf =
    memoize (fun w -> liftBinary (fnModule2 $"Adder%d{w}" ("a", w) ("b", w) "sum" (adderLogic w)))

let satIncOf =
    memoize (fun w -> liftUnary (fnModule1 $"SatInc%d{w}" ("x", w) "y" (satIncLogic w)))

// Stateful stdlib entries. Same use-site type as everything above — a delay register
// is Expr -> Expr exactly as an inline increment is — but applying one adds a cycle.
let delayOf =
    memoize (fun w ->
        liftUnary (
            stateModule1 $"Delay%d{w}" ("d", w) ("q", w) (fun d ->
                let r = reg "r" w
                d ==> r
                r)))

let counterOf =
    memoize (fun w ->
        liftUnary (
            stateModule1 $"Counter%d{w}" ("enable", 1) ("count", w) (fun enable ->
                let r = reg "r" w
                mux enable (r + lit 1UL w) r ==> r
                r)))

// ---------------------------------------------------------------------------
// Barrel (C-slow) threading — the house shape, to the extent it is code.

/// A deeply-pipelined cone kept full by interleaving `threads` independent work
/// items through it, one per cycle. This is not the barrel; it is the invariant
/// and the plumbing that follow from it, which is the part both barrels in this
/// codebase were writing out by hand.
///
/// **`threads > latency` is the whole reason a slot needs no handshake**: an
/// item's next issue always follows its own writeback, so slot state is
/// race-free by arithmetic rather than by arbitration. Both barrels asserted it
/// separately, in near-identical words; it is checked here once.
///
/// `Carry` is the second half. A result arrives `latency` cycles after the
/// issue that produced it, by which time the issuing thread id, its iteration
/// count and its valid bit are long gone — so each rides a delay chain of
/// exactly the cone's depth. Stating that depth once, with the cone, is what
/// this buys: the call sites keep their own names, and a cone whose latency
/// changes cannot leave a chain behind at the old depth.
///
/// What it deliberately does *not* abstract is the barrel's control. Mandelbrot
/// free-runs its turn counter and refills empty slots at issue; GEP nests
/// thread inside program counter inside case-wave under a six-state scheduler.
/// Those are different machines, and a shape that covered both would be a
/// parameter list, not an abstraction.
type Barrel =
    private
        { latency: int
          threads: int }

    /// Cycles from issue to writeback.
    member b.Latency = b.latency

    /// Interleaved work items in flight.
    member b.Threads = b.threads

    /// A value read at issue, as it reads at writeback: `stages = latency`.
    member b.Carry (name: string) (width: int) (source: Expr) =
        delayChain name width b.latency source

    /// A value that must meet a result one stage *before* the writeback — the
    /// shape a design gets when its last pipeline register is the writeback
    /// itself, so the context is already a cycle ahead.
    member b.CarryTo (stages: int) (name: string) (width: int) (source: Expr) =
        if stages > b.latency then
            failwith $"a barrel of latency %d{b.latency} cannot carry '{name}' %d{stages} stages"

        delayChain name width stages source

let barrel (latency: int) (threads: int) =
    if latency < 1 then
        failwith $"a barrel needs a pipelined cone, got latency %d{latency}"

    if threads <= latency then
        failwith
            $"need threads > %d{latency} to hide the pipeline latency, got %d{threads} — at or below it a thread's next issue races its own writeback"

    { latency = latency; threads = threads }

/// xoshiro128++ (Blackman/Vigna) as a synthesizable core: 4×32-bit state, one
/// 32-bit word per `step` — shifts, xors, rotates and two adds, no
/// multiplies. `word` is combinational from the CURRENT state
/// (`rotl(s0+s3,7)+s0`), so a consumer reads `word` and pulses `step` to
/// advance; `load` replaces the whole state in one cycle and wins over
/// `step`.
///
/// State arrives pre-expanded (host-side SplitMix64 of a 64-bit seed —
/// `Warp11.Gep.Rng.expandSeed`): keeping the expansion off-fabric avoids
/// 64×64 multiplies here. The all-zero state is the lattice's one degenerate
/// point; loaders must not supply it (the post-reset default state 1,2,3,4 is
/// nonzero). Host-side mirror + reference stream: `Warp11.Gep.Rng.GepRng`.
let xoshiro128pp name =
    defineModule
        name
        (fun p ->
            {| load = p.inPort "load" 1
               sIn = List.init 4 (fun i -> p.inPort $"s{i}_in" 32)
               step = p.inPort "step" 1
               word = p.outPort "word" 32 |})
        (fun m io ->
            fun (load: Expr) (sIn: Expr list) (step: Expr) ->
                load ==> io.load
                List.iter2 (fun port s -> s ==> port) io.sIn sIn
                step ==> io.step
                io.word)
        (fun io _ ->
            let s = List.init 4 (fun i -> regInit $"s{i}" 32 (uint64 (i + 1)))

            // word = rotl(s0 + s3, 7) + s0 — from the CURRENT state.
            let sum = wire "sum" 32
            s[0] + s[3] ==> sum
            let rot7 = wire "rot7" 32
            cat (slice 24 0 sum) (slice 31 25 sum) ==> rot7
            rot7 + s[0] ==> io.word

            // The reference update in SSA form:
            //   t = s1 << 9; s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3;
            //   s2 ^= t; s3 = rotl(s3, 11)
            let t = wire "t" 32
            cat (slice 22 0 s[1]) (lit 0UL 9) ==> t
            let s2a = wire "s2a" 32
            (s[2] ^^^ s[0]) ==> s2a
            let s3a = wire "s3a" 32
            (s[3] ^^^ s[1]) ==> s3a

            If io.load (fun () -> List.iter2 (==>) io.sIn s)

            Else (fun () ->
                If io.step (fun () ->
                    (s[0] ^^^ s3a) ==> s[0]
                    (s[1] ^^^ s2a) ==> s[1]
                    (s2a ^^^ t) ==> s[2]
                    cat (slice 20 0 s3a) (slice 31 21 s3a) ==> s[3])))

/// Balanced lowest-index-first priority pick over parallel field lists:
/// returns (anyValid, one Expr per field for the lowest-index valid entry) as
/// log-depth mux trees — semantically a linear priority fold at depth
/// log2 n instead of n−1. `fields` is indexed [field][entry]. Graduated from
/// the Kotlin GEP rig, where the linear fold was the failing timing path at
/// 64 engines; the record router is its first F# user.
let priorityPick (valids: Expr list) (fields: Expr list list) : Expr * Expr list =
    let rec range lo hi =
        if lo = hi then
            valids[lo], [ for f in fields -> f[lo] ]
        else
            let mid = (lo + hi) / 2
            let lv, lf = range lo mid
            let rv, rf = range (mid + 1) hi
            (lv ||| rv), List.map2 (fun a b -> mux lv a b) lf rf

    range 0 (valids.Length - 1)

/// Round-robin pick: among the set request bits, the lowest index at or after
/// `baseIdx`, wrapping to the lowest set index overall when none are. Feeding
/// `baseIdx = lastGranted + 1` gives fair rotation — the property a
/// fixed-priority pick lacks, and what stops a shared resource starving a
/// requester under continuous load. Returns `(anyValid, idx)`.
let roundRobinPick (bits: Expr list) (baseIdx: Expr) (idxWidth: int) : Expr * Expr =
    let idxFields = [ [ for i in 0 .. bits.Length - 1 -> lit (uint64 i) idxWidth ] ]
    // `i >= baseIdx` is unconditionally true at the maximum representable index
    // (baseIdx cannot exceed it) — emit that one bare, so Verilator does not
    // flag the always-true compare as CMPCONST.
    let maxIdx = (1 <<< idxWidth) - 1

    let atOrAfter =
        bits
        |> List.mapi (fun i b ->
            if i = maxIdx then
                b
            else
                b &&& bnot (lt (lit (uint64 i) idxWidth) baseIdx))

    let hiValid, hiIdx = priorityPick atOrAfter idxFields
    let loValid, loIdx = priorityPick bits idxFields
    loValid, mux hiValid hiIdx[0] loIdx[0]

/// Balanced binary reduction over expressions — the adder-tree shape, lifted
/// from the designs catalog (`treeSum` is its oracle). Depth ⌈log2 n⌉ instead
/// of a fold's n-deep chain.
let rec reduceTree (combine: Expr -> Expr -> Expr) (exprs: Expr list) : Expr =
    match exprs with
    | [] -> failwith "reduceTree of nothing"
    | [ x ] -> x
    | _ ->
        exprs
        |> List.chunkBySize 2
        |> List.map (function
            | [ a; b ] -> combine a b
            | [ x ] -> x
            | _ -> failwith "chunkBySize 2 gave a bigger chunk")
        |> reduceTree combine

/// One-hot select: exactly one of `selects` is expected high, and the matching
/// value comes out. Zero-extends the losers and ORs through a balanced tree, so
/// depth is log2 n rather than the n−1 of a mux chain — the reason to reach for
/// this rather than `selectIndexed` when the grant is already one-hot.
///
/// With no select high the result is zero, which is the honest answer for a
/// caller that gates on its own `any` term (`warpFu` and `priorityPick` both do).
let mux1H (selects: Expr list) (values: Expr list) : Expr =
    if List.length selects <> List.length values then
        failwith $"mux1H: %d{List.length selects} selects for %d{List.length values} values"

    if List.isEmpty values then
        failwith "mux1H of nothing"

    let w = width (List.head values)

    reduceTree (|||) (List.map2 (fun sel v -> mux sel v (lit 0UL w)) selects values)

/// How many of `xs` satisfy `pred`, as a `resultWidth`-bit count through a
/// balanced adder tree — each 1-bit verdict zero-extended to the result width
/// before summing, so the tree cannot overflow (the caller sizes the width to
/// the population, e.g. 13 bits for 4096 cells). Above 256 inputs the tree
/// lands on named partial wires per 256-input chunk: the emitter writes one
/// expression per assign, and a 4096-leaf tree on one line overflows
/// Verilator's 40k-token line limit (found at the GoL population count).
/// Below the threshold the emission is untouched.
let countWhere (resultWidth: int) (pred: Expr -> Expr) (xs: Expr list) : Expr =
    if List.isEmpty xs then
        failwith "countWhere of nothing"

    let widen x =
        let verdict = pred x

        if width verdict <> 1 then
            failwith $"countWhere predicate must produce 1 bit, got %d{width verdict}"

        if resultWidth = 1 then
            verdict
        else
            cat (lit 0UL (resultWidth - 1)) verdict

    let widened = List.map widen xs

    if List.length widened <= 256 then
        reduceTree (+) widened
    else
        widened
        |> List.chunkBySize 256
        |> List.map (fun chunk ->
            let partial = wire ((current ()).FreshName "count_partial") resultWidth
            reduceTree (+) chunk ==> partial
            partial)
        |> reduceTree (+)

/// How many bits of `value` are set — Chisel's `PopCount`. The result is wide
/// enough that it cannot overflow (`ceilLog2 (w + 1)`), and it goes through the
/// same balanced adder tree `countWhere` uses, because that is what this is:
/// `countWhere` over a signal's own bits rather than over a list of signals.
///
/// The slice rule applies, so this takes a declared signal.
let popCount (value: Expr) =
    match value with
    | Ref (_, t) -> countWhere (ceilLog2 (t.Width + 1)) id [ for i in 0 .. t.Width - 1 -> slice i i value ]
    | _ -> failwith "popCount needs a declared signal — assign the computed value to a wire first"

/// An index as a one-hot vector — Chisel's `UIntToOH`, as a list so it feeds
/// `mux1H` and `priorityPick` directly. Element `i` is high when `idx` reads
/// `i`; an index past `n-1` leaves every element low.
let uintToOneHot (n: int) (idx: Expr) : Expr list =
    if n < 1 then failwith $"uintToOneHot needs at least one position, got %d{n}"
    [ for i in 0 .. n - 1 -> eq idx (lit (uint64 i) (width idx)) ]

/// A one-hot vector back to an index — Chisel's `OHToUInt`. With nothing set the
/// answer is zero, which is the same convention `mux1H` uses and for the same
/// reason: the caller that cares already has an `any` term.
let oneHotToUInt (bits: Expr list) : Expr =
    let w = max 1 (ceilLog2 (List.length bits))
    mux1H bits [ for i in 0 .. List.length bits - 1 -> lit (uint64 i) w ]

/// A counter that wraps, and — the part that earns it — says when.
///
/// Counts `0 .. n-1` while `enable` is high and returns to zero after `n-1`.
/// `wrap` is high on exactly the cycle that rollover happens, which is the
/// signal designs are actually after: a clock divider flips on it, a timer
/// fires on it, a barrel wave advances on it. The count itself is usually
/// incidental. Chisel's `Counter(cond, n)` is the same shape and the same
/// `(value, wrap)` pair.
///
/// Width is `ceilLog2 n`, derived rather than passed, because a counter whose
/// register is wider than its range is a bug waiting for the day someone
/// changes `n`.
///
/// Distinct from `counterOf`, which is a *module* that counts freely and wraps
/// only at its register width — that one exists to show a stateful stdlib entry
/// used as a plain function, and has no limit and no wrap.
/// An enable that is the constant 1. A counter told to run every cycle should
/// emit what a hand-written one emits — no `1'd1 &` on its wrap term and no
/// gate on its increment — which is the same rule the fan helpers follow when
/// N is 1: a combinator that degenerates cleanly costs nothing to reach for.
let private alwaysEnabled (enable: Expr) =
    match enable with
    | Lit (1UL, UInt 1) -> true
    | _ -> false

let counter (name: string) (n: int) (enable: Expr) =
    if n < 1 then
        failwith $"counter '{name}' needs a positive period, got %d{n}"

    let w = max 1 (ceilLog2 n)
    let count = reg name w
    let last = lit (uint64 (n - 1)) w
    let atLast = eq count last

    let step () =
        If atLast (fun () -> lit 0UL w ==> count)
        Else (fun () -> count + lit 1UL w ==> count)

    if alwaysEnabled enable then
        step ()
        {| count = count; wrap = atLast |}
    else
        If enable step
        {| count = count; wrap = enable &&& atLast |}

/// The same, with a bound the design computes at runtime: counts `0 .. last`
/// **inclusive** and wraps after it.
///
/// The bound is the final value rather than a period, which is the opposite
/// convention to `counter` and is deliberate — a design with a runtime limit
/// almost always already holds the last index (a program's instruction count, a
/// row's final column), and making it pass `last + 1` so this could subtract it
/// again would buy an adder and an off-by-one. The two names differ so the two
/// meanings cannot be confused at a call site.
///
/// Chisel has no equivalent; `Counter` is compile-time-`n` only.
let counterTo (name: string) (last: Expr) (enable: Expr) =
    let w = width last
    let count = reg name w
    let atLast = eq count last

    let step () =
        If atLast (fun () -> lit 0UL w ==> count)
        Else (fun () -> count + lit 1UL w ==> count)

    if alwaysEnabled enable then
        step ()
        {| count = count; wrap = atLast |}
    else
        If enable step
        {| count = count; wrap = enable &&& atLast |}

// ---------------------------------------------------------------------------
// State machines. The encoded state register every sequencer in this codebase
// hand-rolls — `let sIdle = 0UL`, `let inState k = eq st (lit k w)`,
// `lit sNext w ==> st` — with the encoding, the decode and the checks coming
// from one declaration instead of a block of numbers.

/// A state machine over a value type: one register, and what its codes mean.
///
/// States are *values* — an ordinary union — so a transition names something the
/// compiler knows rather than a string the elaborator looks up. The emitted
/// logic is exactly what the hand-encoded form emits (`eq st (lit k w)` and
/// `lit k w ==> st`), so converting a design moves no Verilog; what is new is
/// that elaboration now knows the names, and can say both that a state is
/// unreachable and, to the debugger, that code 35 means `sXaCut`.
type Machine<'s when 's: equality> =
    private
        { stateValue: Expr
          codes: ('s * uint64) list
          record: StateMachineRecord }

    /// The state register, for reading — a status port, or a mux across a row
    /// of machines. Transitions go through `Goto`, which is what records that
    /// the destination state can be entered at all.
    member m.Value = m.stateValue

    member private m.CodeOf(state: 's) =
        match m.codes |> List.tryFind (fun (s, _) -> s = state) with
        | Some (_, code) -> code
        | None -> failwith $"%A{state} is not a state of '{m.record.stateReg}'"

    /// This state's code as a literal — for a transition the design computes
    /// (`mux back (m.Code Done) (m.Code Scan) ==> m.Value`) or a comparison
    /// against a copy of the register that travelled somewhere else.
    member m.Code(state: 's) =
        let code = m.CodeOf state
        m.record.reached.Add code |> ignore
        lit code (width m.stateValue)

    /// True for the cycles the machine is in this state.
    member m.Is(state: 's) =
        eq m.stateValue (lit (m.CodeOf state) (width m.stateValue))

    /// True when a *copy* of the register holds this state — one muxed out of a
    /// row of identical machines, or latched a stage downstream. A comparison,
    /// not a transition, so unlike `Code` it is not a way into the state.
    member m.Holds (value: Expr) (state: 's) =
        eq value (lit (m.CodeOf state) (width m.stateValue))

    /// Enter this state at the next edge. Written inside `If`, it is
    /// conditional like any other assign.
    member m.Goto(state: 's) = m.Code state ==> m.stateValue

    /// What this state does: `If (m.Is state)`, named for what it is.
    member m.If (state: 's) (body: unit -> unit) = If (m.Is state) body

    /// Claim the register only ever holds a code some state was given. Worth
    /// planting where a transition is computed rather than named — that is the
    /// one way a state register reaches a value nobody wrote down.
    member m.AssertValid() =
        let valid =
            m.codes
            |> List.map (fun (_, code) -> eq m.stateValue (lit code (width m.stateValue)))
            |> List.reduce (fun a b -> a ||| b)

        assertThat valid $"{m.record.stateReg} holds a code no state was given"

/// A state machine with the encoding spelled out — for a machine whose codes are
/// fixed by something outside the design: a host that writes a state, or a
/// retired state that left a hole the remaining ones must not close.
/// The first entry is the reset state.
let machineCoded (name: string) (states: ('s * uint64) list) : Machine<'s> =
    if List.isEmpty states then
        failwith $"state machine '{name}' needs at least one state"

    let duplicateState =
        states |> List.map fst |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1)

    match duplicateState with
    | Some (s, _) -> failwith $"state machine '{name}' lists %A{s} twice"
    | None -> ()

    let duplicateCode =
        states |> List.map snd |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1)

    match duplicateCode with
    | Some (code, _) -> failwith $"state machine '{name}' gives code %d{code} to two states"
    | None -> ()

    let stateWidth = max 1 (ceilLog2 (int (List.max (List.map snd states)) + 1))
    let reset = snd (List.head states)

    let record =
        { stateReg = name
          states = [ for s, code in states -> code, $"%A{s}" ]
          // The reset state is entered without anything transitioning to it.
          reached = System.Collections.Generic.HashSet<uint64>([ reset ]) }

    (current ()).RegisterStateMachine record

    { stateValue = regInit name stateWidth reset
      codes = states
      record = record }

/// A state machine over a list of states: `ceilLog2 n` bits, codes 0..n-1 in
/// declaration order, reset to the first.
let machine (name: string) (states: 's list) : Machine<'s> =
    machineCoded name [ for i, s in List.indexed states -> s, uint64 i ]

/// `reduceTree` with every level registered, gated by `enable` so the whole
/// tree freezes together on backpressure. Returns the reduced value and its
/// latency in cycles (= the number of levels); the caller must delay its own
/// `valid` by that much, through registers gated by the same `enable`.
///
/// **This is not the combinational tree with registers bolted on — the
/// registers are the point.** A combinational tree feeding DSPs gets
/// re-flattened by synthesis into a deep DSP cascade, because the DSP48's
/// `PCOUT -> PCIN` path is the tool's preferred MAC and that path is linear.
/// Registers are the one thing it cannot move across. So anything that maps to
/// DSPs — FIR and convolution MACs, dot products — wants this form, and the
/// depth-log2 combinational `reduceTree` is not a substitute (CLAUDE.md, "when
/// a design is sim-clean but corrupts on hardware").
///
/// An odd level carries its unpaired element forward through a register too,
/// so it stays aligned with the paired sums rather than arriving a cycle early.
let reduceTreePipelined
    (name: string)
    (accWidth: int)
    (enable: Expr)
    (combine: Expr -> Expr -> Expr)
    (items: Expr list)
    : Expr * int =
    let hold holdName value =
        let r = reg holdName accWidth
        If enable (fun () -> value ==> r)
        r

    let rec level stage items =
        match items with
        | [] -> failwith "reduceTreePipelined of nothing"
        | [ x ] -> x, stage
        | _ ->
            items
            |> List.chunkBySize 2
            |> List.mapi (fun i chunk ->
                match chunk with
                | [ a; b ] ->
                    let s = wire $"{name}_s%d{stage}_%d{i}" accWidth
                    combine a b ==> s
                    hold $"{name}_r%d{stage}_%d{i}" s
                | [ x ] -> hold $"{name}_c%d{stage}" x
                | _ -> failwith "chunkBySize 2 gave a bigger chunk")
            |> level (stage + 1)

    level 0 items

/// Sum alias — the common, timing-critical case.
let adderTreePipelined (name: string) (accWidth: int) (enable: Expr) (items: Expr list) =
    reduceTreePipelined name accWidth enable add items

/// An integer divider, as a stream stage.
///
/// ```fsharp
/// let results = divider "dv" 32 requests
/// // requests : Stream<Expr * Expr>   (dividend, divisor)
/// // results  : Stream<Expr * Expr>   (quotient, remainder)
/// ```
///
/// **No latency crosses the boundary**, which is the point. This is radix-2
/// restoring division: one subtractor reused for `width` iterations, so the unit
/// genuinely cannot take a new operand pair every cycle — and there is no number
/// a caller could be told that would express that. It holds `ready` low while it
/// works. That is the same argument by which `pipe(latency)` was rejected for
/// stream stages, applied to a unit that actually needs it.
///
/// One subtractor rather than `width` of them is the area/throughput point this
/// picks: a fully pipelined divider is `width` subtractors for a result every
/// cycle, which is the right trade only when the divides are back to back.
///
/// **Division by zero** is not special-cased, because the algorithm already
/// answers it the way hardware does: every trial subtraction succeeds, so the
/// quotient comes out all-ones — saturation rather than a trap. `divideBy`
/// refuses a literal zero at elaboration; a divisor that is a signal can only be
/// answered at run time, and this is the answer.
let divider (name: string) (width: int) (requests: Stream<Expr * Expr>) : Stream<Expr * Expr> =
    if width < 1 then failwith $"divider '{name}' needs a width of at least 1, got %d{width}"

    let steps = width
    let countWidth = max 1 (ceilLog2 (steps + 1))

    let state = machine $"{name}_state" [ "Idle"; "Busy"; "Done" ]

    // The working registers. `rem` is one bit wider than the operands because a
    // trial subtraction needs the bit shifted in from the dividend.
    let rem = reg $"{name}_rem" (width + 1)
    let quotient = reg $"{name}_quotient" width
    let dividend = reg $"{name}_dividend" width
    let divisor = reg $"{name}_divisor" width
    let remaining = reg $"{name}_remaining" countWidth

    let requestDividend, requestDivisor = requests.payload

    // Idle takes work; Done holds the result until it is taken. Both are the
    // handshake, and nothing else in here knows about it.
    let outReady = wireBit $"{name}_out_ready"
    (state.Is "Idle") ==> requests.ready

    state.If "Idle" (fun () ->
        If requests.valid (fun () ->
            lit 0UL (width + 1) ==> rem
            lit 0UL width ==> quotient
            requestDividend ==> dividend
            requestDivisor ==> divisor
            lit (uint64 steps) countWidth ==> remaining
            state.Goto "Busy"))

    // One restoring step: shift the next dividend bit into the remainder, try
    // the subtraction, and keep it if it did not go negative.
    let shifted = wire $"{name}_shifted" (width + 1)
    cat (slice (width - 1) 0 rem) (slice (width - 1) (width - 1) dividend) ==> shifted

    let divisorWide = wire $"{name}_divisor_wide" (width + 1)
    cat (lit 0UL 1) divisor ==> divisorWide

    let fits = wireBit $"{name}_fits"
    bnot (lt shifted divisorWide) ==> fits

    let nextRem = wire $"{name}_next_rem" (width + 1)
    mux fits (shifted - divisorWide) shifted ==> nextRem

    // Shift a bit into the low end, dropping the top one — the same move for the
    // quotient (taking the trial result) and the dividend (advancing to the next
    // bit). At one bit wide there is nothing left to keep, which is the only
    // reason this is a function.
    let shiftIn (value: Expr) (bit: Expr) =
        if width = 1 then bit else cat (slice (width - 2) 0 value) bit

    state.If "Busy" (fun () ->
        nextRem ==> rem
        shiftIn quotient fits ==> quotient
        shiftIn dividend (lit 0UL 1) ==> dividend
        (remaining - lit 1UL countWidth) ==> remaining

        If (eq remaining (lit 1UL countWidth)) (fun () -> state.Goto "Done"))

    state.If "Done" (fun () -> If outReady (fun () -> state.Goto "Idle"))

    (current ()).RegisterStreamReady outReady

    { payload = quotient, slice (width - 1) 0 rem
      valid = state.Is "Done"
      ready = outReady
      layout = layout2 ("quotient", width) ("remainder", width) }

/// N-tap FIR with elaboration-time coefficients: `y[n] = sum_i c[i]*x[n-i]`,
/// unsigned data and coefficients. The accumulator is sized so it cannot
/// overflow — `dataWidth + coeffWidth + ceil(log2 N)` — which is also the
/// output width, so no caller has to reason about saturation here.
///
/// The delay line is N−1 applications of `delayOf`, and the products sum
/// through `reduceTree` rather than the linear chain the Kotlin original used:
/// an N-tap linear sum is N adder delays deep, which is exactly the shape that
/// passes a cycle-accurate sim and then misses timing on silicon (CLAUDE.md's
/// first hardware gotcha). Same arithmetic — integer addition is associative —
/// at depth log2 N.
let fir (dataWidth: int) (coeffWidth: int) (coeffs: uint64 list) : Expr -> Expr =
    if List.isEmpty coeffs then failwith "fir requires at least one tap"
    let taps = List.length coeffs
    let productWidth = dataWidth + coeffWidth
    let accWidth = productWidth + ceilLog2 taps

    fun x ->
        let line = List.scan (fun previous _ -> delayOf dataWidth previous) x [ 2..taps ]

        // Zero-extension by concatenation, not `signExtend`: every product is a
        // computed value, and only `cat` accepts one.
        let widen p =
            if accWidth = productWidth then p
            else cat (lit 0UL (accWidth - productWidth)) p

        List.map2 (fun c tap -> widen (mul (lit c coeffWidth) tap)) coeffs line
        |> reduceTree add

// ---------------------------------------------------------------------------
// Shared functional units. Whether an expensive arm is per-client or pooled is
// a sharing RATIO, not two hand-built designs — see `FuSharing`.

/// What a client hands a functional unit, and what comes back: the payload
/// fields plus an opaque routing `tag`, echoed through unchanged. One record
/// serves both directions — a writeback is the same shape with the core's
/// results where its operands were.
///
/// Deliberately NOT request/response: nothing correlates a reply to a call and
/// nothing waits. The two halves are independent streams through a
/// fixed-latency pipe; the tag (a thread id, say) is opaque payload the pod
/// only routes by, which is what leaves writeback order and latency free.
type FuBeat = { tag: Expr; fields: Expr list }

/// The stream layout for one end of an FU link, from the tag width and the
/// core's port names. Both ends build theirs from the same description, so
/// they cannot disagree about field order.
let fuLayout (tagWidth: int) (ports: (string * int) list) : Layout<FuBeat> =
    { fields = ("tag", tagWidth) :: ports
      pack = fun b -> b.tag :: b.fields
      unpack =
        fun nets ->
            match nets with
            | tag :: rest when rest.Length = ports.Length -> { tag = tag; fields = rest }
            | _ -> failwith $"fuLayout: expected %d{ports.Length + 1} nets" }

/// How many clients share one functional unit — the wormhole's sharing ratio,
/// and the whole surface a call site sees. A design names the ratio it wants;
/// which machinery that costs is decided here, not at the use site.
///
/// **Pick by cost × rarity.** Pooling is only ever a win for a unit that is
/// big *and* rare (transcendentals). Measured on the GEP cluster: the divider
/// is 697 LUT — 5.6% of the design across all 8 lanes — and pooling it cost
/// 44–118% throughput while saving nothing on the binding resource. Cheap FUs
/// are `PerLane`, always.
type FuSharing =
    /// K = N: one unit per client. In a barrel (C-slow) datapath with
    /// `nThreads >= latency` this is as cheap as fusing the arm into the
    /// pipeline, because that is what it elaborates to — the unit's latency is
    /// absorbed by delay-aligning the other arms, and alignment costs flops
    /// where arbitration would cost throughput. No arbiter, no FIFO, no stall:
    /// a result lands in a statically known slot, so nothing has to be routed.
    | PerLane
    /// K = 1: one unit behind `warpFu` for every client. Returns become
    /// non-deterministic — arbitration decides *when* — so the client needs a
    /// tagged socket and the pod needs the tag delay-line. That cost is
    /// fundamental to sharing, not an artifact of this implementation.
    | Pooled

/// Share one fixed-latency, II=1 functional unit across N clients WITHOUT
/// touching the unit. `core` is applied verbatim to the winning client's
/// operands; everything here is wrapper:
///
///  - a round-robin **issue arbiter** feeds the core from whichever client
///    wins this cycle — one accept per cycle, which is the core's II;
///  - a **tag delay-line** matched to the core's own depth carries
///    `(client, tag)` alongside its pipeline, so a result knows where it came
///    from. The core *reports* that depth — `Expr list -> Expr list * int` —
///    rather than the caller passing it: nothing here could check a number it
///    was handed, and a wrong one misroutes every result. That is the same
///    unchecked declaration `pipe(latency)` was rejected for.
///  - a **writeback demux** routes each result back to its issuer.
///
/// **At one client the pod is the unit** (the connect layer scales to one):
/// no arbiter, no grant register, no demux — `ready` is tied high because an
/// II=1 core accepts every cycle, and only the tag delay-line survives. So a
/// design parameterised by client count does not grow a degenerate arbiter in
/// its Verilog at N = 1.
///
/// **No writeback buffer**, keeping the wrapper thin: each client must hold no
/// more outstanding than its own writeback FIFO absorbs, so a presented result
/// is always accepted. The returned streams therefore *declare* `ready` and
/// this wrapper never reads it. There is no `assert` yet, so that contract
/// lives here rather than in the design; a client that cannot honour it wants
/// per-client skid buffers, still a wrapper-only change.
let warpFu
    (prefix: string)
    (respPorts: (string * int) list)
    (core: Expr list -> Expr list * int)
    (issues: Stream<FuBeat> list)
    : Stream<FuBeat> list =
    let n = issues.Length

    if n < 1 then
        failwith "warpFu needs at least one client"

    let tagWidth = width issues.Head.payload.tag
    let laneW = if n <= 1 then 1 else 32 - System.Numerics.BitOperations.LeadingZeroCount(uint (n - 1))
    let wbLayout = fuLayout tagWidth respPorts

    // ---- Issue: pick this cycle's client and feed the core ----
    let grantValid, grantLane, grantTag, operands =
        if n = 1 then
            // One client: the socket takes whatever it offers, every cycle.
            let only = streamToFlow issues.Head
            only.valid, lit 0UL laneW, only.payload.tag, only.payload.fields
        else
            let lastGrant = reg $"{prefix}_lastGrant" laneW
            let rrBase = wire $"{prefix}_rrBase" laneW

            mux
                    (eq lastGrant (lit (uint64 (n - 1)) laneW))
                    (lit 0UL laneW)
                    (lastGrant + lit 1UL laneW)
            ==> rrBase

            let anyValid, pickedLane = roundRobinPick [ for s in issues -> s.valid ] rrBase laneW
            let valid = wireBit $"{prefix}_grantValid"
            anyValid ==> valid
            let lane = wire $"{prefix}_grantLane" laneW
            pickedLane ==> lane
            If valid (fun () -> lane ==> lastGrant)

            // The one-hot grant IS each client's ready — a client fires exactly
            // when it won — so the same bits select its payload onto the core.
            let grants =
                [ for l in 0 .. n - 1 ->
                      let g = wireBit $"{prefix}_grant%d{l}"
                      (valid &&& eq lane (lit (uint64 l) laneW)) ==> g
                      g ==> issues[l].ready
                      g ]

            let oneHot (name: string) (per: int -> Expr) =
                let picked = wire $"{prefix}_{name}" (width (per 0))
                mux1H grants [ for l in 0 .. n - 1 -> per l ] ==> picked
                picked

            let tag = oneHot "grantTag" (fun l -> issues[l].payload.tag)

            let ops =
                [ for f in 0 .. issues.Head.payload.fields.Length - 1 ->
                      oneHot $"grantOperand%d{f}" (fun l -> issues[l].payload.fields[f]) ]

            valid, lane, tag, ops

    // The core reports its own depth rather than being told one. A wrapper that
    // asks its caller how deep the thing it was handed is has no way to check
    // the answer, and a wrong one misroutes every result — the same unchecked
    // declaration that got `pipe(latency)` rejected for stream stages. Here the
    // number comes from the construction that built the stages.
    let results, latency = core operands

    if latency < 1 then
        failwith $"warpFu '%s{prefix}': the core reports latency %d{latency}, and a shared unit must be pipelined"

    if results.Length <> respPorts.Length then
        failwith $"warpFu '%s{prefix}': core returned %d{results.Length} results for %d{respPorts.Length} respPorts"

    // ---- Tag delay-line: (valid, client, tag) shifts alongside the core's
    // pipeline, so the result emerges already routed. ----
    let delayed (name: string) (w: int) (source: Expr) =
        delayChain $"{prefix}_{name}" w latency source

    let outValid = delayed "dvValid" 1 grantValid
    let outTag = delayed "dvTag" tagWidth grantTag
    let outLane = if n = 1 then grantLane else delayed "dvLane" laneW grantLane

    // ---- Writeback: the result broadcasts, and lands on the one client whose
    // tag emerged this cycle. `ready` is declared and deliberately unread. ----
    [ for l in 0 .. n - 1 ->
          let rdy = wireBit $"{prefix}_l%d{l}_wb_ready"
          registerStreamReady rdy

          { payload = { tag = outTag; fields = results }
            valid =
              if n = 1 then
                  outValid
              else
                  outValid &&& eq outLane (lit (uint64 l) laneW)
            ready = rdy
            layout = wbLayout } ]

/// Neighbor set: the 8 surrounding cells (diagonals included) or the 4
/// orthogonal ones.
[<RequireQualifiedAccess>]
type Stencil =
    | Moore
    | VonNeumann

/// What an off-grid neighbor reads as at the borders.
[<RequireQualifiedAccess>]
type Edge =
    /// Dead border: a 0 literal of the cell width.
    | Zero
    /// Toroidal: indices wrap to the opposite edge.
    | Wrap
    /// Indices clamp to the nearest border cell.
    | Clamp

/// The `stencil` neighborhood of cell (`y`, `x`) in a rectangular grid, in
/// fixed row-major order with the center excluded, under the `edge` policy.
/// Pure elaboration-time gather — it reads the grid lists and (for
/// `Edge.Zero`) fabricates dead literals; the caller reduces the result
/// (Life's count is `countWhere` over `neighborhood Stencil.Moore Edge.Zero`).
let neighborhood (stencil: Stencil) (edge: Edge) (grid: Expr list list) (y: int) (x: int) : Expr list =
    let h = List.length grid

    if h = 0 || List.isEmpty (List.head grid) then
        failwith "neighborhood needs a non-empty grid"

    let w = List.length (List.head grid)

    if grid |> List.exists (fun row -> List.length row <> w) then
        failwith "neighborhood requires a rectangular grid"

    let cellWidth = grid |> List.head |> List.head |> width

    let offsets =
        match stencil with
        | Stencil.Moore -> [ -1, -1; -1, 0; -1, 1; 0, -1; 0, 1; 1, -1; 1, 0; 1, 1 ]
        | Stencil.VonNeumann -> [ -1, 0; 0, -1; 0, 1; 1, 0 ]

    let sample yy xx =
        match edge with
        | Edge.Zero ->
            if yy >= 0 && yy < h && xx >= 0 && xx < w then
                grid[yy][xx]
            else
                lit 0UL cellWidth
        | Edge.Wrap -> grid[((yy % h) + h) % h][((xx % w) + w) % w]
        | Edge.Clamp -> grid[max 0 (min (h - 1) yy)][max 0 (min (w - 1) xx)]

    [ for dy, dx in offsets -> sample (y + dy) (x + dx) ]


let private log2 n =
    if n <= 0 || n &&& (n - 1) <> 0 then failwith $"log2 requires a power of two, got %d{n}"
    System.Numerics.BitOperations.Log2(uint n) |> int

/// The beat an AXI write master consumes: where it goes, the data, which bytes.
let axiWriteBeatLayout (addrWidth: int) (dataWidth: int) : Layout<Expr * Expr * Expr> =
    layout3 ("addr", addrWidth) ("data", dataWidth) ("strb", dataWidth / 8)

/// AXI4 write master, elaborated inline in the current design (the scratch-
/// slave scheme, like `axiLiteSlave`): consumes a ready/valid stream of
/// (addr, data, strb) beats and issues one single-beat AXI4 write per beat —
/// AWLEN=0, INCR, WLAST=1, AWCACHE/AWPROT=0 — on `m_axi_*` ports declared at
/// the design boundary. `maxOutstanding` slots pipeline writes through an
/// AW/W/B pointer ring (per-slot REGS, not a mem: a mem read port would add a
/// cycle and break the combinational AW/W presents); AXI guarantees in-order B
/// at constant AWID, so one counter serves the response side. BRESP is
/// trusted. Port of Kotlin's `axiMasterWriter`; N=8..16 is the HP-port sweet
/// spot, N=1 degenerates to simple pending flags.
let private axiMasterWriterCore
    (exposeIdle: bool)
    (addrWidth: int)
    (dataWidth: int)
    (maxOutstanding: int)
    (beats: Stream<Expr * Expr * Expr>)
    : {| idle: Expr option; bAck: Expr |} =
    if dataWidth <> 32 && dataWidth <> 64 && dataWidth <> 128 then
        failwith $"axiMasterWriter dataWidth must be 32, 64 or 128, got %d{dataWidth}"

    if addrWidth < 12 || addrWidth > 40 then
        failwith $"axiMasterWriter addrWidth must be 12..40, got %d{addrWidth}"

    if maxOutstanding < 1 || maxOutstanding > 32 then
        failwith $"maxOutstanding must be 1..32, got %d{maxOutstanding}"

    if maxOutstanding > 1 && maxOutstanding &&& (maxOutstanding - 1) <> 0 then
        failwith $"maxOutstanding must be a power of two (or 1), got %d{maxOutstanding}"

    let strbWidth = dataWidth / 8

    let sizeEnc =
        match dataWidth with
        | 32 -> 2UL
        | 64 -> 3UL
        | _ -> 4UL

    let inAddr, inData, inStrb = beats.payload

    let awaddr = output "m_axi_awaddr" addrWidth
    let awlen = output "m_axi_awlen" 8
    let awsize = output "m_axi_awsize" 3
    let awburst = output "m_axi_awburst" 2
    let awcache = output "m_axi_awcache" 4
    let awprot = output "m_axi_awprot" 3
    let awvalid = outputBit "m_axi_awvalid"
    let awready = inputBit "m_axi_awready"
    let wdata = output "m_axi_wdata" dataWidth
    let wstrb = output "m_axi_wstrb" strbWidth
    let wlast = outputBit "m_axi_wlast"
    let wvalid = outputBit "m_axi_wvalid"
    let wready = inputBit "m_axi_wready"
    input "m_axi_bresp" 2 |> ignore // trusted OKAY
    let bvalid = inputBit "m_axi_bvalid"
    let bready = outputBit "m_axi_bready"

    lit 0UL 8 ==> awlen // 1 beat per burst
    lit sizeEnc 3 ==> awsize
    lit 1UL 2 ==> awburst // INCR
    lit 0UL 4 ==> awcache
    lit 0UL 3 ==> awprot
    lit 1UL 1 ==> wlast // every beat is last

    if maxOutstanding = 1 then
        // simple pending flags — single-outstanding, single-cycle accept
        let awPending = regBit "aw_pending"
        let wPending = regBit "w_pending"
        let bPending = regBit "b_pending"
        let addrQ = reg "addr_q" addrWidth
        let dataQ = reg "data_q" dataWidth
        let strbQ = reg "strb_q" strbWidth

        let idle = wireBit "idle"
        (bnot awPending &&& bnot wPending &&& bnot bPending) ==> idle
        idle ==> beats.ready
        let accept = wireBit "accept"
        (beats.valid &&& idle) ==> accept

        If accept (fun () ->
            inAddr ==> addrQ
            inData ==> dataQ
            inStrb ==> strbQ
            lit 1UL 1 ==> awPending
            lit 1UL 1 ==> wPending
            lit 1UL 1 ==> bPending)

        Else (fun () ->
            If (awPending &&& awready) (fun () -> lit 0UL 1 ==> awPending)
            If (wPending &&& wready) (fun () -> lit 0UL 1 ==> wPending)
            If (bPending &&& bvalid) (fun () -> lit 0UL 1 ==> bPending))

        addrQ ==> awaddr
        awPending ==> awvalid
        dataQ ==> wdata
        strbQ ==> wstrb
        wPending ==> wvalid
        bPending ==> bready
        let bAck = wireBit "b_ack"
        (bPending &&& bvalid) ==> bAck
        {| idle = Some idle; bAck = bAck |}
    else
        // ring of N slots; enq/aw/w/b pointers carry an extra bit so full is
        // distinguishable from empty, and modular subtraction is the count
        let n = maxOutstanding
        let log2N = log2 n
        let ptrWidth = log2N + 1
        let addrSlots = [ for i in 0 .. n - 1 -> reg $"addr_q_%d{i}" addrWidth ]
        let dataSlots = [ for i in 0 .. n - 1 -> reg $"data_q_%d{i}" dataWidth ]
        let strbSlots = [ for i in 0 .. n - 1 -> reg $"strb_q_%d{i}" strbWidth ]
        let enqPtr = reg "enq_ptr" ptrWidth
        let awPtr = reg "aw_ptr" ptrWidth
        let wPtr = reg "w_ptr" ptrWidth
        let bPtr = reg "b_ptr" ptrWidth

        let inFlight = wire "in_flight" ptrWidth
        enqPtr - bPtr ==> inFlight
        let notFull = wireBit "not_full"
        lt inFlight (lit (uint64 n) ptrWidth) ==> notFull
        notFull ==> beats.ready
        let accept = wireBit "accept"
        (beats.valid &&& notFull) ==> accept

        let enqSlot = wire "enq_slot" log2N
        slice (log2N - 1) 0 enqPtr ==> enqSlot

        for i in 0 .. n - 1 do
            If (accept &&& eq enqSlot (lit (uint64 i) log2N)) (fun () ->
                inAddr ==> addrSlots[i]
                inData ==> dataSlots[i]
                inStrb ==> strbSlots[i])

        If accept (fun () -> enqPtr + lit 1UL ptrWidth ==> enqPtr)

        let awSlot = wire "aw_slot" log2N
        slice (log2N - 1) 0 awPtr ==> awSlot
        let awHasWork = wireBit "aw_has_work"
        bnot (eq awPtr enqPtr) ==> awHasWork
        awHasWork ==> awvalid
        selectIndexed awSlot addrSlots ==> awaddr
        If (awHasWork &&& awready) (fun () -> awPtr + lit 1UL ptrWidth ==> awPtr)

        let wSlot = wire "w_slot" log2N
        slice (log2N - 1) 0 wPtr ==> wSlot
        let wHasWork = wireBit "w_has_work"
        bnot (eq wPtr enqPtr) ==> wHasWork
        wHasWork ==> wvalid
        selectIndexed wSlot dataSlots ==> wdata
        selectIndexed wSlot strbSlots ==> wstrb
        If (wHasWork &&& wready) (fun () -> wPtr + lit 1UL ptrWidth ==> wPtr)

        lit 1UL 1 ==> bready // a slot frees only when bPtr advances, so room always exists
        If bvalid (fun () -> bPtr + lit 1UL ptrWidth ==> bPtr)

        // bready is tied high above, so a response is accepted the cycle it
        // arrives and bvalid IS the acceptance.
        let bAck = wireBit "b_ack"
        bvalid ==> bAck

        {| idle =
            (if exposeIdle then
                 let idle = wireBit "writer_idle"
                 eq enqPtr bPtr ==> idle
                 Some idle
             else
                 None)
           bAck = bAck |}

let private axiReadValidate (addrWidth: int) (dataWidth: int) (maxOutstanding: int) =
    if dataWidth <> 32 && dataWidth <> 64 && dataWidth <> 128 then
        failwith $"axiMasterReader dataWidth must be 32, 64 or 128, got %d{dataWidth}"

    if addrWidth < 12 || addrWidth > 40 then
        failwith $"axiMasterReader addrWidth must be 12..40, got %d{addrWidth}"

    if maxOutstanding < 1 || maxOutstanding > 32 then
        failwith $"maxOutstanding must be 1..32, got %d{maxOutstanding}"

    if maxOutstanding > 1 && maxOutstanding &&& (maxOutstanding - 1) <> 0 then
        failwith $"maxOutstanding must be a power of two (or 1), got %d{maxOutstanding}"

/// The AR/R boundary ports plus the tied transaction constants; returns
/// (araddr, arlen, arvalid, arready, rdata, rlast, rvalid, rready).
let private axiReadPorts (addrWidth: int) (dataWidth: int) =
    let sizeEnc =
        match dataWidth with
        | 32 -> 2UL
        | 64 -> 3UL
        | _ -> 4UL

    let araddr = output "m_axi_araddr" addrWidth
    let arlen = output "m_axi_arlen" 8
    let arsize = output "m_axi_arsize" 3
    let arburst = output "m_axi_arburst" 2
    let arcache = output "m_axi_arcache" 4
    let arprot = output "m_axi_arprot" 3
    let arvalid = outputBit "m_axi_arvalid"
    let arready = inputBit "m_axi_arready"
    let rdata = input "m_axi_rdata" dataWidth
    input "m_axi_rresp" 2 |> ignore // trusted OKAY
    let rlast = inputBit "m_axi_rlast"
    let rvalid = inputBit "m_axi_rvalid"
    let rready = outputBit "m_axi_rready"

    lit sizeEnc 3 ==> arsize
    lit 1UL 2 ==> arburst // INCR
    lit 0UL 4 ==> arcache
    lit 0UL 3 ==> arprot

    araddr, arlen, arvalid, arready, rdata, rlast, rvalid, rready

/// AXI4 read master, elaborated inline in the current design — the symmetric
/// counterpart to `axiMasterWriter`: consumes a ready/valid stream of byte
/// addresses and produces a ready/valid stream of read data, one single-beat
/// AXI4 read per request (ARLEN=0, INCR) on `m_axi_ar*`/`m_axi_r*` ports
/// declared at the design boundary. `maxOutstanding` = 1 runs three pending
/// flags; a power-of-two ring otherwise (per-slot regs, not a mem — a mem
/// read port would add a cycle on the resp drain). AXI4 guarantees in-order R
/// at constant ARID, so pointer comparison replaces per-slot validity. RRESP
/// is trusted. Internal names carry an `rd_` prefix so a design can hold this
/// reader beside the writer. Port of Kotlin's `axiMasterReader`; the
/// ARCACHE/ARPROT attributes stay 0 until an HPC consumer needs them.
let axiMasterReader (addrWidth: int) (dataWidth: int) (maxOutstanding: int) (requests: Stream<Expr>) : Stream<Expr> =
    axiReadValidate addrWidth dataWidth maxOutstanding
    let araddr, arlen, arvalid, arready, rdata, _, rvalid, rready = axiReadPorts addrWidth dataWidth
    lit 0UL 8 ==> arlen // 1 beat per burst

    let respReady = wireBit "rd_resp_ready"
    (current ()).RegisterStreamReady respReady

    if maxOutstanding = 1 then
        // Lifecycle of one read: accept → arPending + rPending; ARREADY fires
        // → arPending clear; RVALID fires → data captured, respPending;
        // consumer fires → idle. req_ready requires all three clear.
        let arPending = regBit "rd_ar_pending"
        let rPending = regBit "rd_r_pending"
        let respPending = regBit "rd_resp_pending"
        let addrQ = reg "rd_addr_q" addrWidth
        let dataQ = reg "rd_data_q" dataWidth

        let idle = wireBit "rd_idle"
        (bnot arPending &&& bnot rPending &&& bnot respPending) ==> idle
        idle ==> requests.ready
        let accept = wireBit "rd_accept"
        (requests.valid &&& idle) ==> accept

        If accept (fun () ->
            requests.payload ==> addrQ
            lit 1UL 1 ==> arPending
            lit 1UL 1 ==> rPending
            lit 0UL 1 ==> respPending)

        Else (fun () ->
            If (arPending &&& arready) (fun () -> lit 0UL 1 ==> arPending)

            If (rPending &&& rvalid) (fun () ->
                rdata ==> dataQ
                lit 0UL 1 ==> rPending
                lit 1UL 1 ==> respPending)

            If (respPending &&& respReady) (fun () -> lit 0UL 1 ==> respPending))

        addrQ ==> araddr
        arPending ==> arvalid
        rPending ==> rready // accept R only while waiting

        { payload = dataQ
          valid = respPending
          ready = respReady
          layout = layout1 ("data", dataWidth) }
    else
        // Ring of N slots, four pointers with an extra bit so full ≠ empty:
        // enq (request accept), ar (issue), r (reception), deq (drain).
        let n = maxOutstanding
        let log2N = log2 n
        let ptrWidth = log2N + 1
        let addrSlots = [ for i in 0 .. n - 1 -> reg $"rd_addr_q_%d{i}" addrWidth ]
        let dataSlots = [ for i in 0 .. n - 1 -> reg $"rd_data_q_%d{i}" dataWidth ]
        let enqPtr = reg "rd_enq_ptr" ptrWidth
        let arPtr = reg "rd_ar_ptr" ptrWidth
        let rPtr = reg "rd_r_ptr" ptrWidth
        let deqPtr = reg "rd_deq_ptr" ptrWidth

        let inFlight = wire "rd_in_flight" ptrWidth
        enqPtr - deqPtr ==> inFlight
        let notFull = wireBit "rd_not_full"
        lt inFlight (lit (uint64 n) ptrWidth) ==> notFull
        notFull ==> requests.ready
        let accept = wireBit "rd_accept"
        (requests.valid &&& notFull) ==> accept

        let enqSlot = wire "rd_enq_slot" log2N
        slice (log2N - 1) 0 enqPtr ==> enqSlot

        for i in 0 .. n - 1 do
            If (accept &&& eq enqSlot (lit (uint64 i) log2N)) (fun () -> requests.payload ==> addrSlots[i])

        If accept (fun () -> enqPtr + lit 1UL ptrWidth ==> enqPtr)

        let arSlot = wire "rd_ar_slot" log2N
        slice (log2N - 1) 0 arPtr ==> arSlot
        let arHasWork = wireBit "rd_ar_has_work"
        bnot (eq arPtr enqPtr) ==> arHasWork
        arHasWork ==> arvalid
        selectIndexed arSlot addrSlots ==> araddr
        If (arHasWork &&& arready) (fun () -> arPtr + lit 1UL ptrWidth ==> arPtr)

        // The slot was reserved when AR issued, so room always exists for R.
        lit 1UL 1 ==> rready
        let rSlot = wire "rd_r_slot" log2N
        slice (log2N - 1) 0 rPtr ==> rSlot

        for i in 0 .. n - 1 do
            If (rvalid &&& eq rSlot (lit (uint64 i) log2N)) (fun () -> rdata ==> dataSlots[i])

        If rvalid (fun () -> rPtr + lit 1UL ptrWidth ==> rPtr)

        let deqSlot = wire "rd_deq_slot" log2N
        slice (log2N - 1) 0 deqPtr ==> deqSlot
        let respHasData = wireBit "rd_resp_has_data"
        bnot (eq rPtr deqPtr) ==> respHasData
        let respData = wire "rd_resp_data" dataWidth
        selectIndexed deqSlot dataSlots ==> respData
        If (respHasData &&& respReady) (fun () -> deqPtr + lit 1UL ptrWidth ==> deqPtr)

        { payload = respData
          valid = respHasData
          ready = respReady
          layout = layout1 ("data", dataWidth) }

/// The burst-mode AXI4 read master: requests carry (addr, len) — ARLEN
/// encoding, beats − 1, capped by `maxBurstLen` — and responses carry
/// (data, last). The AR side is the same slot ring, holding descriptors only;
/// the response path is a **streaming passthrough** — resp mirrors the R
/// channel and RREADY mirrors resp ready, so a stalled consumer stalls the
/// interconnect (which has its own buffering) instead of slot registers,
/// which cannot hold a burst. A transaction retires on its RLAST beat. The
/// caller owns AXI's rules: a burst must not cross a 4 KB boundary.
let axiMasterReaderBurst
    (addrWidth: int)
    (dataWidth: int)
    (maxOutstanding: int)
    (maxBurstLen: int)
    (requests: Stream<Expr * Expr>)
    : Stream<Expr * Expr> =
    axiReadValidate addrWidth dataWidth maxOutstanding

    if maxBurstLen < 2 || maxBurstLen > 256 then
        failwith $"maxBurstLen must be 2..256, got %d{maxBurstLen} (use axiMasterReader for single-beat)"

    if maxOutstanding = 1 then
        failwith "burst mode requires a multi-outstanding ring (maxOutstanding > 1)"

    let araddr, arlen, arvalid, arready, rdata, rlast, rvalid, rready = axiReadPorts addrWidth dataWidth
    let reqAddr, reqLen = requests.payload

    let respReady = wireBit "rd_resp_ready"
    (current ()).RegisterStreamReady respReady

    let n = maxOutstanding
    let log2N = log2 n
    let ptrWidth = log2N + 1
    let addrSlots = [ for i in 0 .. n - 1 -> reg $"rd_addr_q_%d{i}" addrWidth ]
    let lenSlots = [ for i in 0 .. n - 1 -> reg $"rd_len_q_%d{i}" 8 ]
    let enqPtr = reg "rd_enq_ptr" ptrWidth
    let arPtr = reg "rd_ar_ptr" ptrWidth
    let donePtr = reg "rd_done_ptr" ptrWidth // freed on the RLAST beat

    let inFlight = wire "rd_in_flight" ptrWidth
    enqPtr - donePtr ==> inFlight
    let notFull = wireBit "rd_not_full"
    lt inFlight (lit (uint64 n) ptrWidth) ==> notFull
    notFull ==> requests.ready
    let accept = wireBit "rd_accept"
    (requests.valid &&& notFull) ==> accept

    let enqSlot = wire "rd_enq_slot" log2N
    slice (log2N - 1) 0 enqPtr ==> enqSlot

    for i in 0 .. n - 1 do
        If (accept &&& eq enqSlot (lit (uint64 i) log2N)) (fun () ->
            reqAddr ==> addrSlots[i]
            reqLen ==> lenSlots[i])

    If accept (fun () -> enqPtr + lit 1UL ptrWidth ==> enqPtr)

    let arSlot = wire "rd_ar_slot" log2N
    slice (log2N - 1) 0 arPtr ==> arSlot
    let arHasWork = wireBit "rd_ar_has_work"
    bnot (eq arPtr enqPtr) ==> arHasWork
    arHasWork ==> arvalid
    selectIndexed arSlot addrSlots ==> araddr
    selectIndexed arSlot lenSlots ==> arlen
    If (arHasWork &&& arready) (fun () -> arPtr + lit 1UL ptrWidth ==> arPtr)

    // Streaming passthrough; a transaction retires on its RLAST beat.
    respReady ==> rready
    If (rvalid &&& respReady &&& rlast) (fun () -> donePtr + lit 1UL ptrWidth ==> donePtr)

    { payload = (rdata, rlast)
      valid = rvalid
      ready = respReady
      layout = layout2 ("data", dataWidth) ("last", 1) }

/// AXI4 write master, elaborated inline — see `axiMasterWriterCore` above for
/// the scheme. This is the common form; a caller that must know when the ring
/// has fully drained (every B collected — the coherency gate before a
/// slot-rotating consumer publishes a frame) uses `axiMasterWriterWithIdle`.
let axiMasterWriter (addrWidth: int) (dataWidth: int) (maxOutstanding: int) (beats: Stream<Expr * Expr * Expr>) =
    axiMasterWriterCore false addrWidth dataWidth maxOutstanding beats |> ignore

/// The writer plus both levels a completion-tracking master needs: `idle`
/// (nothing in flight) and `bAck`, one pulse per accepted write response.
/// `bAck` is the honest "this write has reached memory" event — a design that
/// must not report a result before its payload landed counts these, rather
/// than counting beats it merely handed to the master.
let axiMasterWriterTracked
    (addrWidth: int)
    (dataWidth: int)
    (maxOutstanding: int)
    (beats: Stream<Expr * Expr * Expr>)
    : {| idle: Expr; bAck: Expr |} =
    let w = axiMasterWriterCore true addrWidth dataWidth maxOutstanding beats
    {| idle = w.idle.Value; bAck = w.bAck |}

/// The writer plus its quiescence level: high when no write is in flight
/// (`enq_ptr = b_ptr` on the ring; all three pendings clear at N=1).
let axiMasterWriterWithIdle
    (addrWidth: int)
    (dataWidth: int)
    (maxOutstanding: int)
    (beats: Stream<Expr * Expr * Expr>)
    : Expr =
    (axiMasterWriterCore true addrWidth dataWidth maxOutstanding beats).idle.Value
