/// The surface under trial: a functional unit as a law over typed pins, and
/// a stage that places it by counting streams against copies.
///
/// Nothing in `Warp11/` moves. `Pins` carries what a palette needs — a number
/// format per field — and `lower` drops it to the width-only `Layout` the
/// library speaks, at the one place a stage hands a stream to the library.
/// If the trial earns its place, `Layout` becomes `Pins` and `lower` the
/// identity; if not, this directory is what gets deleted.
module Warp11.Placement.Fu

open Warp11

type NumberFormat = Warp11.Number.NumberFormat

/// An unsigned integer of `w` bits — the format a bare width has always meant.
let uint (w: int) : NumberFormat = { totalWidth = w; fracBits = 0; signed = false }

/// A signed integer of `w` bits.
let sint (w: int) : NumberFormat = { totalWidth = w; fracBits = 0; signed = true }

/// A payload's pins: name and number format per field, and the pack/unpack
/// pair that turns the typed payload into nets and back. `Layout` with the
/// reading kept — what a palette reads to decide whether two pins connect.
type Pins<'p> =
    { pins: (string * NumberFormat) list
      pack: 'p -> Expr list
      unpack: Expr list -> 'p }

/// The one bridge to the library: widths only, the reading dropped.
let lower (p: Pins<'p>) : Layout<'p> =
    { fields = [ for n, f in p.pins -> n, f.totalWidth ]
      pack = p.pack
      unpack = p.unpack }

/// One pin; the payload is the `Expr` itself.
let pins1 (an: string, af: NumberFormat) : Pins<Expr> =
    { pins = [ an, af ]
      pack = fun x -> [ x ]
      unpack =
        fun nets ->
            match nets with
            | [ x ] -> x
            | _ -> failwith $"pins1 {an}: expected 1 net" }

/// Two pins, as a pair.
let pins2 (an: string, af: NumberFormat) (bn: string, bf: NumberFormat) : Pins<Expr * Expr> =
    { pins = [ an, af; bn, bf ]
      pack = fun (x, y) -> [ x; y ]
      unpack =
        fun nets ->
            match nets with
            | [ x; y ] -> x, y
            | _ -> failwith $"pins2 {an},{bn}: expected 2 nets" }

/// Three pins, as a triple.
let pins3 (an: string, af: NumberFormat) (bn: string, bf: NumberFormat) (cn: string, cf: NumberFormat) : Pins<Expr * Expr * Expr> =
    { pins = [ an, af; bn, bf; cn, cf ]
      pack = fun (x, y, z) -> [ x; y; z ]
      unpack =
        fun nets ->
            match nets with
            | [ x; y; z ] -> x, y, z
            | _ -> failwith $"pins3 {an},{bn},{cn}: expected 3 nets" }

/// Pins with no typed payload: the nets themselves, in order. What a graph
/// works in, and what a typed unit erases to.
let pinsOfList (pins: (string * NumberFormat) list) : Pins<Expr list> =
    { pins = pins
      pack = id
      unpack =
        fun nets ->
            if nets.Length <> pins.Length then
                failwith $"pins: expected %d{pins.Length} nets, got %d{nets.Length}"

            nets }

/// What a unit does. A combinational unit is a function of its operands and
/// costs nothing to copy. A sequential unit costs cycles and says so the only
/// way anything here may — by presenting a stream. It takes an instance name,
/// because a design may spend copies of it.
///
/// Both take the unit's **controls** first: values held across beats rather
/// than carried in them — a volume, a mute, a coefficient. Pure Data's split
/// between a signal inlet and a control inlet, and the split between a
/// stream port and a plain port here.
type Law<'a, 'r> =
    | Combinational of (Expr list -> 'a -> 'r)
    | Sequential of (string -> Expr list -> Stream<'a> -> Stream<'r>)

/// A functional unit: a law over typed pins, its controls, and how many
/// copies of it a design may spend.
type Fu<'a, 'r> =
    { name: string
      operands: Pins<'a>
      results: Pins<'r>
      controls: (string * NumberFormat) list
      law: Law<'a, 'r>
      copies: int }

/// A combinational unit with no controls, one copy.
let fu (name: string) (operands: Pins<'a>) (results: Pins<'r>) (law: 'a -> 'r) : Fu<'a, 'r> =
    { name = name
      operands = operands
      results = results
      controls = []
      law = Combinational(fun _ a -> law a)
      copies = 1 }

/// A sequential unit with no controls, one copy.
let fuSequential (name: string) (operands: Pins<'a>) (results: Pins<'r>) (law: string -> Stream<'a> -> Stream<'r>) : Fu<'a, 'r> =
    { name = name
      operands = operands
      results = results
      controls = []
      law = Sequential(fun instance _ s -> law instance s)
      copies = 1 }

/// A module as a unit: a stream through it, controls beside it, an instance
/// per copy. `audioGain "AudioGain" instance volume mute` is the shape — an
/// instance as a function, which is what makes any `defModule` a box.
let moduleUnit
    (name: string)
    (operands: Pins<'a>)
    (results: Pins<'r>)
    (controls: (string * NumberFormat) list)
    (law: string -> Expr list -> Stream<'a> -> Stream<'r>)
    : Fu<'a, 'r> =
    { name = name
      operands = operands
      results = results
      controls = controls
      law = Sequential law
      copies = 1 }

/// The same unit, `n` copies.
let copies (n: int) (unit: Fu<'a, 'r>) : Fu<'a, 'r> = { unit with copies = n }

/// A unit over bare nets — what a graph holds. Made only by `erase`, from a
/// typed unit through its pins, so a palette cannot disagree with the unit
/// the typed API elaborates: same pins, same law, same bits.
type ErasedFu = Fu<Expr list, Expr list>

let erase (unit: Fu<'a, 'r>) : ErasedFu =
    let results = pinsOfList unit.results.pins

    { name = unit.name
      operands = pinsOfList unit.operands.pins
      results = results
      controls = unit.controls
      copies = unit.copies
      law =
        match unit.law with
        | Combinational law -> Combinational(fun controls nets -> unit.results.pack (law controls (unit.operands.unpack nets)))
        | Sequential law ->
            Sequential(fun instance controls s ->
                s
                |> streamMapTo (lower unit.operands) unit.operands.unpack
                |> law instance controls
                |> streamMapTo (lower results) unit.results.pack) }

/// A layout's `unpack` may hand a field back in its reading (`asSInt` over
/// the net); to drive the net itself, take the reading off again.
let rec private bare (e: Expr) =
    match e with
    | AsSInt v
    | AsUInt v -> bare v
    | v -> v

/// One stream's end of a shared unit: the wires the stage drives with its
/// operands and its request, and the wires the pool answers on.
type private Client =
    { operands: Expr list
      issue: Expr
      grant: Expr
      results: Expr list
      landed: Expr }

/// A stage whose work is one issue to a shared unit: the beat held while
/// the work is out, the operands presented from it, and the beat handed on
/// as `finish results beat` when they land. One beat in flight per stage —
/// which is what lets the pool never buffer a writeback, and what keeps each
/// stream's beats in order without a tag.
///
/// The generic form of the folded compressor's `podStage`: same three-state
/// worker, same registered request, the operand and result wires sized from
/// the unit's pins rather than from a multiplier's.
let private clientStage
    (unit: Fu<'a, 'r>)
    (name: string)
    (outPins: Pins<'q>)
    (operands: 'p -> 'a)
    (finish: 'r -> 'p -> 'q)
    (s: Stream<'p>)
    : Stream<'q> * Client =
    let st = machine $"{name}_state" [ Accepting; Working; Offering ]
    let ready = wireBit $"{name}_ready"
    registerStreamReady ready

    // A stage whose offer is taken this cycle accepts the next beat this
    // cycle too, rather than spending a cycle in `Accepting` first.
    let taken = st.Is Offering &&& ready
    (st.Is Accepting ||| taken) ==> s.ready
    let accept = s.valid &&& s.ready

    ifElse
        [ (accept, fun () -> st.Goto Working)
          (taken, fun () -> st.Goto Accepting) ]

    // Hold the beat, in registers named for the stage.
    let held = [ for n, w in s.layout.fields -> reg $"{name}_{n}" w ]
    If accept (fun () -> List.iter2 (fun r v -> bare v ==> r) held (s.layout.pack s.payload))
    let beat = s.layout.unpack held

    // The client's wires, one per pin of the unit.
    let operandWires = [ for n, f in unit.operands.pins -> wire $"{name}_{unit.name}_{n}" f.totalWidth ]
    List.iter2 (fun w v -> bare v ==> w) operandWires (unit.operands.pack (operands beat))
    let resultWires = [ for n, f in unit.results.pins -> wire $"{name}_{unit.name}_{n}" f.totalWidth ]
    let issue = wireBit $"{name}_issue"
    let grant = wireBit $"{name}_grant"
    let landed = wireBit $"{name}_landed"

    // The request is a register, not a decode of the state: it heads the
    // arbiter's path into the unit, which is the longest path there is.
    let issued = regBit $"{name}_issued"
    If accept (fun () -> lit 0UL 1 ==> issued)
    let request = regBit $"{name}_request"
    (accept ||| (st.Is Working &&& bnot issued &&& bnot grant)) ==> request
    request ==> issue
    If (issue &&& grant) (fun () -> lit 1UL 1 ==> issued)

    // The results are held here — the pool's registers are every client's —
    // and the output is finished from them and the held beat.
    let heldResults = [ for n, f in unit.results.pins -> reg $"{name}_{n}_held" f.totalWidth ]

    If (st.Is Working &&& landed) (fun () ->
        List.iter2 (fun r v -> v ==> r) heldResults resultWires
        st.Goto Offering)

    let outLayout = lower outPins
    let outWires = [ for n, w in outLayout.fields -> wire $"{name}_out_{n}" w ]
    List.iter2 (fun w v -> bare v ==> w) outWires (outLayout.pack (finish (unit.results.unpack heldResults) beat))

    { payload = outLayout.unpack outWires
      valid = st.Is Offering
      ready = ready
      layout = outLayout },
    { operands = operandWires
      issue = issue
      grant = grant
      results = resultWires
      landed = landed }

/// One copy of a combinational unit behind `warpFu`, for every client at
/// once. The core is the law between operand registers and result
/// registers — the shape a DSP block absorbs — and it reports that depth to
/// `warpFu` rather than being asked for it.
let private pool (unit: Fu<'a, 'r>) (law: 'a -> 'r) (name: string) (clients: Client list) =
    let tagWidth = 1
    let operandPorts = [ for n, f in unit.operands.pins -> n, f.totalWidth ]
    let resultPorts = [ for n, f in unit.results.pins -> n, f.totalWidth ]
    let issueLayout = fuLayout tagWidth operandPorts

    let issues: Stream<FuBeat> list =
        [ for c in clients ->
              { payload = { tag = lit 0UL tagWidth; fields = c.operands }
                valid = c.issue
                ready = c.grant
                layout = issueLayout } ]

    let core (operands: Expr list) =
        let held =
            [ for (n, f), o in List.zip unit.operands.pins operands ->
                  let r = reg $"{name}_{n}" f.totalWidth
                  o ==> r
                  r ]

        let results = unit.results.pack (law (unit.operands.unpack held))

        [ for (n, f), v in List.zip unit.results.pins results ->
              let r = reg $"{name}_{n}" f.totalWidth
              bare v ==> r
              r ],
        2

    let results = warpFu name resultPorts core issues

    for c, r in List.zip clients results do
        List.iter2 (fun w v -> v ==> w) c.results r.payload.fields
        r.valid ==> c.landed
        // A client holds one beat, so a landed result always has a home.
        lit 1UL 1 ==> r.ready

/// The same layout under prefixed field names, so two layouts that both
/// call a field `a` can be joined side by side.
let private prefixed (prefix: string) (l: Layout<'x>) : Layout<'x> =
    { l with fields = [ for n, w in l.fields -> $"{prefix}_{n}", w ] }

/// `n` copies of a stage over one stream, IN ORDER: beats go to lanes round
/// robin, and leave round robin in the same rotation, so beat `i` comes out
/// of lane `i mod n` and before beat `i + 1` whatever the lanes are doing. A
/// slow lane stalls the merge rather than being overtaken — which is the
/// property `farmWith` gives up for throughput and a sample stream cannot
/// give up. At one lane it is the stage itself.
let private orderedFarm (name: string) (n: int) (worker: int -> Stream<'p> -> Stream<'q>) (s: Stream<'p>) : Stream<'q> =
    if n = 1 then
        worker 0 s
    else
        let laneBits = ceilLog2 n
        let lane (i: int) = lit (uint64 i) laneBits

        // Dispatch: the next lane in rotation gets the beat.
        let feeding = counter $"{name}_feed" n (s.valid &&& s.ready)
        let laneReadies = [ for i in 0 .. n - 1 -> wireBit $"{name}_lane%d{i}_ready" ]
        selectIndexed feeding.count laneReadies ==> s.ready

        let lanes =
            [ for i in 0 .. n - 1 ->
                  { s with
                      valid = s.valid &&& eq feeding.count (lane i)
                      ready = laneReadies[i] } ]

        let outs = List.mapi worker lanes
        let layout = outs.Head.layout

        // Merge: take from the lanes in the same rotation.
        let ready = wireBit $"{name}_ready"
        registerStreamReady ready
        let valid = wireBit $"{name}_valid"
        let taking = counter $"{name}_take" n (valid &&& ready)
        selectIndexed taking.count [ for o in outs -> o.valid ] ==> valid

        for i, o in List.indexed outs do
            (ready &&& eq taking.count (lane i)) ==> o.ready

        let fields =
            [ for j, (fieldName, w) in List.indexed layout.fields ->
                  let f = wire $"{name}_{fieldName}" w
                  selectIndexed taking.count [ for o in outs -> (layout.pack o.payload)[j] ] ==> f
                  f ]

        { payload = layout.unpack fields
          valid = valid
          ready = ready
          layout = layout }

/// A stage over a sequential unit, `k` copies for this stream. The unit sees
/// only its operands; the rest of the beat waits beside it in a context FIFO
/// and is handed back paired with the result — `withContext`, per lane. At
/// `k` = 1 there is no farm; the copy is the stage.
let private sequentialStage
    (unit: Fu<'a, 'r>)
    (law: string -> Stream<'a> -> Stream<'r>)
    (name: string)
    (outPins: Pins<'q>)
    (operands: 'p -> 'a)
    (finish: 'r -> 'p -> 'q)
    (k: int)
    (s: Stream<'p>)
    : Stream<'q> =
    let opLayout = prefixed "op" (lower unit.operands)
    let resLayout = prefixed "res" (lower unit.results)
    let ctxLayout = prefixed "beat" s.layout
    let src = streamMapTo (layoutJoin opLayout ctxLayout) (fun p -> operands p, p) s

    let copy (i: int) =
        let instance = if k = 1 then $"{name}_{unit.name}" else $"{name}_{unit.name}%d{i}"
        withContext instance 2 opLayout resLayout ctxLayout (law instance)

    orderedFarm name k copy src |> streamMapTo (lower outPins) (fun (r, p) -> finish r p)

/// The M streams that need a unit, handed over together, so the row of the
/// matrix is decided here:
///
///                    combinational unit          sequential unit
///     N = M          M copies, in place          M units, one each
///     N < M          M clients on N units        the same
///     N > M          elaboration error           a farm
///
/// `controls` are the unit's held values, in the order its `controls` list
/// names them; `operands` says what the unit sees of a beat, `finish` what
/// the next stage sees. Built: every combinational row at one shared copy,
/// and the sequential rows at one copy per stream or a multiple. The rest
/// say so.
let fuStagesWith
    (controls: Expr list)
    (unit: Fu<'a, 'r>)
    (name: string)
    (outPins: Pins<'q>)
    (operands: 'p -> 'a)
    (finish: 'r -> 'p -> 'q)
    (streams: Stream<'p> list)
    : Stream<'q> list =
    let m = streams.Length
    let n = unit.copies

    if controls.Length <> unit.controls.Length then
        failwith $"'{name}': unit '{unit.name}' has %d{unit.controls.Length} control(s), given %d{controls.Length}"

    match unit.law with
    | Combinational law when n = m ->
        // In place: the law applied inside the payload, the handshake the
        // upstream's own. No net is declared.
        streams |> List.map (streamMapTo (lower outPins) (fun p -> finish (law controls (operands p)) p))
    | Combinational law when n = 1 ->
        // Shared: every stream a client, the one copy behind an arbiter.
        let staged =
            streams |> List.mapi (fun i s -> clientStage unit $"{name}%d{i}" outPins operands finish s)

        pool unit (law controls) $"{name}_pool" (List.map snd staged)
        List.map fst staged
    | Combinational _ when n > m ->
        failwith
            $"'{name}': unit '{unit.name}' has %d{n} copies for %d{m} stream(s) — a combinational unit cannot use more copies than there are streams"
    | Combinational _ ->
        failwith $"'{name}': %d{m} streams on %d{n} copies of '{unit.name}' — more than one shared copy is not built yet (Q2)"
    | Sequential law when n >= m && n % m = 0 ->
        // One copy per stream, or k per stream farmed in order.
        streams
        |> List.mapi (fun i s -> sequentialStage unit (fun instance -> law instance controls) $"{name}%d{i}" outPins operands finish (n / m) s)
    | Sequential _ when n < m ->
        // Not `warpFu`: a sequential unit accepts a beat every few cycles and
        // has no pipeline to run a tag delay-line beside. The shape is
        // `wormholeIn` (the merge is the arbiter) + `withContext` with the
        // lane as context + a demux back to each stream's own beat FIFO.
        failwith $"'{name}': %d{m} streams on %d{n} copies of sequential unit '{unit.name}' — sharing a sequential unit is not built yet"
    | Sequential _ ->
        failwith
            $"'{name}': %d{n} copies of '{unit.name}' over %d{m} streams — copies must be a multiple of streams (Q2)"

/// `fuStagesWith` for a unit with no controls.
let fuStages (unit: Fu<'a, 'r>) name outPins operands finish streams =
    fuStagesWith [] unit name outPins operands finish streams
