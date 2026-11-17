/// A functional unit as a law over typed pins, and a stage that places it by
/// counting streams against copies. A unit's pins are a `Layout` whose
/// fields carry their format — what a palette reads to decide whether two
/// pins connect — declared with `pinsN` where the reading matters and
/// `layoutN` where only the width does.
module Warp11.Fu

open Warp11

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
/// copies of it a design may spend. `answers` is how many beats it emits
/// for one it takes — one, but for a unit that drains a chunk as beats or
/// unpacks a row; the placement carries the beat's context and keeps order
/// across all of them.
type Fu<'a, 'r> =
    { name: string
      operands: Layout<'a>
      results: Layout<'r>
      controls: (string * NumberFormat) list
      law: Law<'a, 'r>
      copies: int
      answers: int }

/// A combinational unit with no controls, one copy.
let fu (name: string) (operands: Layout<'a>) (results: Layout<'r>) (law: 'a -> 'r) : Fu<'a, 'r> =
    { name = name
      operands = operands
      results = results
      controls = []
      law = Combinational(fun _ a -> law a)
      copies = 1
      answers = 1 }

/// A multiply as a combinational unit, at the operands' formats, the
/// product's format derived from them: the whole product, nothing dropped.
let multiplier (a: NumberFormat) (b: NumberFormat) : Fu<Expr * Expr, Expr> =
    let sign (f: NumberFormat) = if f.signed then "s" else "u"

    fu
        $"multiply_{sign a}%d{a.totalWidth}x{sign b}%d{b.totalWidth}"
        (pins2 ("a", a) ("b", b))
        (pins1 ("product", Warp11.Number.productFormat a b))
        (fun (x, y) -> mul x y)

/// A sequential unit with no controls, one copy.
let fuSequential (name: string) (operands: Layout<'a>) (results: Layout<'r>) (law: string -> Stream<'a> -> Stream<'r>) : Fu<'a, 'r> =
    { name = name
      operands = operands
      results = results
      controls = []
      law = Sequential(fun instance _ s -> law instance s)
      copies = 1
      answers = 1 }

/// A module as a unit: a stream through it, controls beside it, an instance
/// per copy. `audioGain "AudioGain" instance volume mute` is the shape — an
/// instance as a function, which is what makes any `defModule` a box.
let moduleUnit
    (name: string)
    (operands: Layout<'a>)
    (results: Layout<'r>)
    (controls: (string * NumberFormat) list)
    (law: string -> Expr list -> Stream<'a> -> Stream<'r>)
    : Fu<'a, 'r> =
    { name = name
      operands = operands
      results = results
      controls = controls
      law = Sequential law
      copies = 1
      answers = 1 }

/// The same unit, `n` copies.
let copies (n: int) (unit: Fu<'a, 'r>) : Fu<'a, 'r> = { unit with copies = n }

/// The same unit, answering `k` beats for one. A combinational unit is a
/// function of its operands and answers exactly once; only a sequential one
/// can spread an answer over beats.
let answering (k: int) (unit: Fu<'a, 'r>) : Fu<'a, 'r> =
    match unit.law with
    | Combinational _ when k <> 1 -> failwith $"'{unit.name}': a combinational unit answers once for one beat, not %d{k} times"
    | _ when k < 1 -> failwith $"'{unit.name}': a unit answers at least once, not %d{k} times"
    | _ -> { unit with answers = k }

/// A combinational unit as a stage: its law applied to each beat, `valid`
/// and `ready` straight through, nothing declared but what the law builds.
/// What a parent serves a socket with when it spends a copy on that socket
/// alone.
///
/// The beat must arrive at the unit's operand formats, width and reading,
/// because a signed multiply handed unsigned operands is a different multiply
/// with the same bits.
let inPlace (unit: Fu<'a, 'r>) : Stream<'a> -> Stream<'r> =
    match unit.law with
    | Sequential _ -> failwith $"'{unit.name}' is sequential: it is a stage of its own, not a law to apply in place"
    | Combinational _ when not unit.controls.IsEmpty ->
        let names = unit.controls |> List.map fst |> String.concat ", "
        failwith $"'{unit.name}' has controls ({names}); in place takes a unit without them"
    | Combinational law ->
        fun s ->
            let arriving = [ for e in s.layout.pack s.payload -> formatOf e ]
            let expected = [ for _, f in unit.operands.fields -> { f with fracBits = 0 } ]

            if arriving <> expected then
                failwith $"'{unit.name}' in place: operands arrive as %A{arriving}, the unit takes %A{expected}"

            streamMapTo unit.results (law []) s

/// A unit over bare nets — what a graph holds. Made only by `erase`, from a
/// typed unit through its pins, so a palette cannot disagree with the unit
/// the typed API elaborates: same pins, same law, same bits.
type ErasedFu = Fu<Expr list, Expr list>

let erase (unit: Fu<'a, 'r>) : ErasedFu =
    let results = layoutOfList unit.results.fields

    { name = unit.name
      operands = layoutOfList unit.operands.fields
      results = results
      controls = unit.controls
      copies = unit.copies
      answers = unit.answers
      law =
        match unit.law with
        | Combinational law -> Combinational(fun controls nets -> unit.results.pack (law controls (unit.operands.unpack nets)))
        | Sequential law ->
            Sequential(fun instance controls s ->
                s
                |> streamMapTo (unit.operands) unit.operands.unpack
                |> law instance controls
                |> streamMapTo (results) unit.results.pack) }

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
    (outPins: Layout<'q>)
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
    let held = [ for n, f in s.layout.fields -> reg $"{name}_{n}" f.totalWidth ]
    If accept (fun () -> List.iter2 (fun r v -> bare v ==> r) held (s.layout.pack s.payload))
    let beat = s.layout.unpack held

    // The client's wires, one per pin of the unit.
    let operandWires = [ for n, f in unit.operands.fields -> wire $"{name}_{unit.name}_{n}" f.totalWidth ]
    List.iter2 (fun w v -> bare v ==> w) operandWires (unit.operands.pack (operands beat))
    let resultWires = [ for n, f in unit.results.fields -> wire $"{name}_{unit.name}_{n}" f.totalWidth ]
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
    let heldResults = [ for n, f in unit.results.fields -> reg $"{name}_{n}_held" f.totalWidth ]

    If (st.Is Working &&& landed) (fun () ->
        List.iter2 (fun r v -> v ==> r) heldResults resultWires
        st.Goto Offering)

    let outLayout = outPins
    let outWires = [ for n, f in outLayout.fields -> wire $"{name}_out_{n}" f.totalWidth ]
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
let private pool (arbitration: Arbitration) (unit: Fu<'a, 'r>) (law: 'a -> 'r) (name: string) (clients: Client list) =
    let tagWidth = 1
    let operandPorts = [ for n, f in unit.operands.fields -> n, f.totalWidth ]
    let resultPorts = [ for n, f in unit.results.fields -> n, f.totalWidth ]
    let issueLayout = fuLayout tagWidth operandPorts

    let issues: Stream<FuBeat> list =
        [ for c in clients ->
              { payload = { tag = lit 0UL tagWidth; fields = c.operands }
                valid = c.issue
                ready = c.grant
                layout = issueLayout } ]

    let core (operands: Expr list) =
        let held =
            // Held at the operands' readings, so a signed law multiplies
            // signed: the bits arrive through the issue mux as plain bits.
            [ for (n, f), o in List.zip unit.operands.fields operands ->
                  let r = reg $"{name}_{n}" (Warp11.Number.groundType f)
                  o ==> r
                  r ]

        let results = unit.results.pack (law (unit.operands.unpack held))

        [ for (n, f), v in List.zip unit.results.fields results ->
              let r = reg $"{name}_{n}" f.totalWidth
              bare v ==> r
              r ],
        2

    let results = warpFuWith arbitration name resultPorts core issues

    for c, r in List.zip clients results do
        List.iter2 (fun w v -> v ==> w) c.results r.payload.fields
        r.valid ==> c.landed
        // A client holds one beat, so a landed result always has a home.
        lit 1UL 1 ==> r.ready

/// One copy of a sequential unit — a stream stage that takes cycles, as many
/// as it takes — for every client at once. The clients' requests, each
/// tagged with the client's index, are merged behind `arbitration`; the tag
/// rides through the unit beside the beat (`withContext`), and the answer
/// lands at the client it names. Nothing counts the unit's cycles: it may
/// stall, hold `ready` low while it works, or take a different time for each
/// beat.
///
/// A client holds one beat in flight, so a landed answer always has a home
/// and the unit's output is never held up by a client that is not ready —
/// two clients of one unit joined downstream cannot wait on each other. It
/// also sizes the tag FIFO: at most one beat a client is inside.
let private streamPool
    (arbitration: Arbitration)
    (unit: Fu<'a, 'r>)
    (law: string -> Stream<'a> -> Stream<'r>)
    (name: string)
    (clients: Client list)
    =
    let tagWidth = max 1 (ceilLog2 clients.Length)
    let tagLayout = pins1 ("client", unsignedInt tagWidth)
    let issueLayout = layoutJoin unit.operands tagLayout

    let issues: Stream<'a * Expr> list =
        [ for i, c in List.indexed clients ->
              { payload = unit.operands.unpack c.operands, lit (uint64 i) tagWidth
                valid = c.issue
                ready = c.grant
                layout = issueLayout } ]

    let answered =
        streamMergeWith arbitration issues
        |> withContext $"{name}_tags" clients.Length unit.operands unit.results tagLayout (law name)

    let results, tag = answered.payload
    lit 1UL 1 ==> answered.ready

    for i, c in List.indexed clients do
        List.iter2 (fun w v -> bare v ==> w) c.results (unit.results.pack results)
        (answered.valid &&& eq tag (lit (uint64 i) tagWidth)) ==> c.landed

/// The law a shared unit runs, or why it cannot be shared.
let private sharedLaw (unit: Fu<'a, 'r>) =
    if not unit.controls.IsEmpty then
        let names = unit.controls |> List.map fst |> String.concat ", "
        failwith $"'{unit.name}' has controls ({names}); sharing takes a unit without them"

    unit.law

/// One copy of `unit` serving `clients`: a combinational unit between
/// registers behind `warpFu`, a sequential one as the stream it is.
let private copyFor (arbitration: Arbitration) (unit: Fu<'a, 'r>) (name: string) (clients: Client list) =
    match sharedLaw unit with
    | Combinational law -> pool arbitration unit (law []) name clients
    | Sequential law -> streamPool arbitration unit (fun instance s -> law instance [] s) name clients

/// The beat's formats against the unit's operands: width and reading.
let private checkOperands (unit: Fu<'a, 'r>) (what: string) (s: Stream<'a>) =
    let arriving = [ for e in s.layout.pack s.payload -> formatOf e ]
    let expected = [ for _, f in unit.operands.fields -> { f with fracBits = 0 } ]

    if arriving <> expected then
        failwith $"'{unit.name}' {what}: operands arrive as %A{arriving}, the unit takes %A{expected}"

/// A unit as one stage of a pipeline: the call a drawn box is. A module
/// unit is its instance, named `instance`, with `controls` beside the
/// stream; a combinational unit is its law over each beat, `valid` and
/// `ready` straight through. Nothing is added around either, so
/// `s |> stage [ volume; mute ] gainModule "gain"` is
/// `s |> audioGain "AudioGain" "gain" volume mute`.
///
/// One copy: how many there are, and what arbitrates them, is the parent's
/// to say where the streams meet.
let stage (controls: Expr list) (unit: Fu<'a, 'r>) (instance: string) : Stream<'a> -> Stream<'r> =
    if controls.Length <> unit.controls.Length then
        failwith $"'{instance}': unit '{unit.name}' has %d{unit.controls.Length} control(s), given %d{controls.Length}"

    if unit.copies <> 1 then
        failwith $"'{instance}': unit '{unit.name}' has %d{unit.copies} copies, and a stage is one"

    match unit.law with
    | Sequential law -> law instance controls
    | Combinational law ->
        fun s ->
            checkOperands unit $"as the stage '{instance}'" s
            streamMapTo unit.results (law controls) s

/// One copy of a unit, shared by every stage `body` makes from it. `body` is
/// handed a stage, `Stream<operands> -> Stream<results>`, of the same type
/// `inPlace` returns; each place it is applied is one client of the copy, so
/// a parent that served two sockets with it has two clients and one unit.
/// When `body` returns, the copy is built with every client it was given: a
/// combinational unit between registers behind an arbiter (`warpFu`), a
/// sequential one — a stream stage that takes cycles — as its own stream,
/// the client's index riding through it beside the beat (`streamPool`).
///
/// A client holds one beat while its operation is out, so a stream through
/// one manages a beat every few cycles. The clients must be made in the
/// module that shares the unit: a socket in a child reaches it by being
/// passed up to this module's boundary, not by the child reaching out.
///
/// `arbitration` is who is served next and when the arbiter says so; the
/// clients are numbered in the order `body` made them, which is the order
/// `Priority` serves them in.
let sharingWith
    (arbitration: Arbitration)
    (unit: Fu<'a, 'r>)
    (name: string)
    (body: (Stream<'a> -> Stream<'r>) -> 'x)
    : 'x =
    sharedLaw unit |> ignore
    let home = current ()
    let clients = ResizeArray<Client>()

    let client (s: Stream<'a>) : Stream<'r> =
        if not (obj.ReferenceEquals(current (), home)) then
            failwith $"'{name}': a client of a shared '{unit.name}' was made inside another module; pass that module's socket up to the one that shares the unit"

        checkOperands unit "shared" s
        let results, c = clientStage unit $"{name}_client%d{clients.Count}" unit.results id (fun r _ -> r) s
        clients.Add c
        results

    let result = body client

    if clients.Count = 0 then
        failwith $"'{name}': a shared '{unit.name}' with no clients"

    copyFor arbitration unit name (List.ofSeq clients)
    result

/// `sharingWith`, round-robin in the same cycle.
let sharing (unit: Fu<'a, 'r>) (name: string) (body: (Stream<'a> -> Stream<'r>) -> 'x) : 'x =
    sharingWith Arbitration.roundRobin unit name body

/// `copies` copies of `unit`, sharing the sockets `body` makes: a pool. The
/// sockets are dealt to the copies in turn as `body` makes them — the nth to
/// copy `n mod copies` — and each copy arbitrates its own share behind
/// `arbitration`, so a stream's beats stay in order with no tag. One copy is
/// `sharingWith`, to the byte. A pool with more copies than sockets is
/// refused: a copy with nothing to do is a mistake about the design, not a
/// spare.
let sharingAmong
    (arbitration: Arbitration)
    (copies: int)
    (unit: Fu<'a, 'r>)
    (name: string)
    (body: (Stream<'a> -> Stream<'r>) -> 'x)
    : 'x =
    if copies < 1 then
        failwith $"'{name}': a pool has at least one copy, not %d{copies}"

    if copies = 1 then
        sharingWith arbitration unit name body
    else
        sharedLaw unit |> ignore
        let home = current ()
        let shares = Array.init copies (fun _ -> ResizeArray<Client>())
        let mutable made = 0

        let client (s: Stream<'a>) : Stream<'r> =
            if not (obj.ReferenceEquals(current (), home)) then
                failwith $"'{name}': a client of a shared '{unit.name}' was made inside another module; pass that module's socket up to the one that shares the unit"

            checkOperands unit "shared" s
            let copy = made % copies
            made <- made + 1
            let share = shares[copy]
            let results, c = clientStage unit $"{name}%d{copy}_client%d{share.Count}" unit.results id (fun r _ -> r) s
            share.Add c
            results

        let result = body client

        if made < copies then
            failwith $"'{name}': a pool of %d{copies} copies of '{unit.name}' with %d{made} client(s) — a copy would have nothing to do"

        for copy, share in Array.indexed shares do
            copyFor arbitration unit $"{name}%d{copy}" (List.ofSeq share)

        result

/// How a design serves the sockets that ask for one kind of unit — the
/// application's choice, stated beside the design the way Mandelbrot states
/// its lanes. `InPlace` gives every socket a copy of its own: the plain
/// operation, full speed, as much hardware as there are sockets. `Shared n`
/// is a pool of `n` copies the sockets take turns on (`sharingAmong`). A part
/// too small for what is asked is refused by the build's gate with the
/// number, and the answer is a smaller pool.
type Serving =
    | InPlace
    | Shared of copies: int

/// `body` given what serves its sockets of `unit`: the unit in place for
/// each, or a pool of copies under `name`, round-robin.
let serving (how: Serving) (unit: Fu<'a, 'r>) (name: string) (body: (Stream<'a> -> Stream<'r>) -> 'x) : 'x =
    match how with
    | InPlace -> body (inPlace unit)
    | Shared copies -> sharingAmong Arbitration.roundRobin copies unit name body

/// The same layout under prefixed field names, so two layouts that both
/// call a field `a` can be joined side by side.
let private prefixed (prefix: string) (l: Layout<'x>) : Layout<'x> =
    { l with fields = [ for n, w in l.fields -> $"{prefix}_{n}", w ] }

/// `n` copies of a stage over one stream, IN ORDER: a beat goes to whichever
/// lane is free and leaves in the order it came, so beat `i` is out before
/// beat `i + 1` whatever the lanes cost — a sample stream cannot give that
/// up, and a frame's raster is the same rule. `streamFarmOrdered` is the
/// mechanism: a queue of lane indices the merge follows, and above
/// `fanFlatMax` lanes the clustered fan the flat one would not clock at. At
/// one lane it is the stage itself. The queue holds two beats a lane, the
/// beat a lane holds and the one it stages.
let private orderedFarm (name: string) (n: int) (k: int) (worker: int -> Stream<'p> -> Stream<'q>) (s: Stream<'p>) : Stream<'q> =
    streamFarmOrdered name n (2 * n) k worker s

/// `n` copies over one stream, in **no** order: a beat goes to whichever lane
/// is free and leaves whenever its lane is done. Nothing records where a beat
/// went, because nothing has to — this is for a stream whose beats say where
/// they belong (`DataPath.Scatter`), and it is the whole of what ordering
/// costs, removed: no queue of lane indices, no per-lane answer FIFO, and a
/// merge that never waits on one lane while another has a beat ready.
///
/// Use it only where the consumer does not read position from arrival. A
/// sample stream cannot; a frame whose beats carry their own address can.
let private unorderedFarm (name: string) (n: int) (worker: int -> Stream<'p> -> Stream<'q>) (s: Stream<'p>) : Stream<'q> =
    if n = 1 then
        worker 0 s
    else
        streamBalanceClustered n s |> List.mapi worker |> streamMergeClustered

/// Whether a farm hands beats on in the order they came.
[<RequireQualifiedAccess>]
type Farm =
    /// Beat `i` leaves before beat `i + 1`, whatever the copies cost.
    | InOrder
    /// A beat leaves when its copy is done. For beats that say where they
    /// belong; a sample stream cannot use it.
    | AsDone

/// `copies` of a unit over one stream: the call a drawn box with copies is.
/// A beat goes to whichever copy is free; the copies are instances
/// `{name}0`, `{name}1`, …. At one copy it is `stage controls unit name`,
/// and nothing else is built.
///
/// A combinational unit is refused above one copy: it answers in the cycle
/// it is asked, so there is nothing for a second copy to do.
///
/// Each copy sits between two registers: `ready` registered where the beat
/// is handed to it (`Connection.Backward`) and a register stage on its
/// result (`Connection.Forward`). A unit never knows it is farmed, so its
/// `ready` may wait on its `valid` and its `valid` on its `ready` — and the
/// dispatch offers a beat to a lane that is ready while the merge is ready
/// for a lane that offers, which through such a unit is a loop either way
/// round. The registers are what let any unit be scaled.
///
/// Both, because one is not enough in order: the `ready` register holds a
/// second beat behind a busy copy, and without somewhere for a finished
/// result to wait the ordered merge serialises the lanes. Measured, 64
/// products on four sixteen-cycle multipliers: 276 cycles with nothing, 593
/// with `ready` registered alone, 283 with both.
let farm (order: Farm) (controls: Expr list) (unit: Fu<'a, 'r>) (name: string) (copies: int) : Stream<'a> -> Stream<'r> =
    if copies < 1 then
        failwith $"'{name}': copies is at least 1, not %d{copies}"
    elif copies = 1 then
        stage controls unit name
    else
        match unit.law with
        | Combinational _ ->
            failwith $"'{name}': unit '{unit.name}' is combinational, and %d{copies} copies of it over one stream have nothing to wait for"
        | Sequential _ ->
            let worker (i: int) =
                connectAs Connection.Backward
                >> stage controls unit $"{name}%d{i}"
                >> connectAs Connection.Forward

            match order with
            | Farm.InOrder -> orderedFarm name copies unit.answers worker
            | Farm.AsDone -> unorderedFarm name copies worker

/// A stage over a sequential unit, `k` copies for this stream. The unit sees
/// only its operands; the rest of the beat waits beside it in a context FIFO
/// and is handed back paired with the result — `withContext`, per lane. At
/// `k` = 1 there is no farm; the copy is the stage.
let private sequentialStage
    (ordered: bool)
    (unit: Fu<'a, 'r>)
    (law: string -> Stream<'a> -> Stream<'r>)
    (name: string)
    (outPins: Layout<'q>)
    (operands: 'p -> 'a)
    (finish: 'r -> 'p -> 'q)
    (k: int)
    (s: Stream<'p>)
    : Stream<'q> =
    let opLayout = prefixed "op" (unit.operands)
    let resLayout = prefixed "res" (unit.results)
    let ctxLayout = prefixed "beat" s.layout
    let src = streamMapTo (layoutJoin opLayout ctxLayout) (fun p -> operands p, p) s

    let copy (i: int) =
        let instance = if k = 1 then $"{name}_{unit.name}" else $"{name}_{unit.name}%d{i}"
        withContextExpanding unit.answers instance 2 opLayout resLayout ctxLayout (law instance)

    let farmed =
        if ordered then
            orderedFarm name k unit.answers copy src
        else
            unorderedFarm name k copy src

    farmed |> streamMapTo (outPins) (fun (r, p) -> finish r p)

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
let private fuStagesOrderedBy
    (ordered: bool)
    (controls: Expr list)
    (unit: Fu<'a, 'r>)
    (name: string)
    (outPins: Layout<'q>)
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
        streams |> List.map (streamMapTo (outPins) (fun p -> finish (law controls (operands p)) p))
    | Combinational law when n = 1 ->
        // Shared: every stream a client, the one copy behind an arbiter.
        let staged =
            streams |> List.mapi (fun i s -> clientStage unit $"{name}%d{i}" outPins operands finish s)

        pool Arbitration.roundRobin unit (law controls) $"{name}_pool" (List.map snd staged)
        List.map fst staged
    | Combinational _ when n > m ->
        failwith
            $"'{name}': unit '{unit.name}' has %d{n} copies for %d{m} stream(s) — a combinational unit cannot use more copies than there are streams"
    | Combinational _ ->
        failwith $"'{name}': %d{m} streams on %d{n} copies of '{unit.name}' — more than one shared copy is not built yet (Q2)"
    | Sequential law when n >= m && n % m = 0 ->
        // One copy per stream, or k per stream farmed in order.
        streams
        |> List.mapi (fun i s -> sequentialStage ordered unit (fun instance -> law instance controls) $"{name}%d{i}" outPins operands finish (n / m) s)
    | Sequential _ when n < m ->
        // Not `warpFu`: a sequential unit accepts a beat every few cycles and
        // has no pipeline to run a tag delay-line beside. The shape is
        // `wormholeIn` (the merge is the arbiter) + `withContext` with the
        // lane as context + a demux back to each stream's own beat FIFO.
        failwith $"'{name}': %d{m} streams on %d{n} copies of sequential unit '{unit.name}' — sharing a sequential unit is not built yet"
    | Sequential _ ->
        failwith
            $"'{name}': %d{n} copies of '{unit.name}' over %d{m} streams — copies must be a multiple of streams (Q2)"

/// The placement above, **in order**: beat `i` leaves before beat `i + 1`
/// whatever the lanes cost. The default, and what a sample stream needs.
let fuStagesWith controls unit name outPins operands finish streams =
    fuStagesOrderedBy true controls unit name outPins operands finish streams

/// The placement above, in **no** order: a beat leaves when its lane is done.
/// For a stream whose beats carry their own destination — `DataPath.Scatter`
/// — where putting them back in order would cost a queue and a merge that
/// stalls behind the slowest lane, to rebuild a position the beat already
/// states.
let fuStagesUnorderedWith controls unit name outPins operands finish streams =
    fuStagesOrderedBy false controls unit name outPins operands finish streams

/// `fuStagesWith` for a unit with no controls.
let fuStages (unit: Fu<'a, 'r>) name outPins operands finish streams =
    fuStagesWith [] unit name outPins operands finish streams

/// `fuStagesUnorderedWith` for a unit with no controls.
let fuStagesUnordered (unit: Fu<'a, 'r>) name outPins operands finish streams =
    fuStagesUnorderedWith [] unit name outPins operands finish streams
