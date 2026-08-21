[<AutoOpen>]
module Warp11.Streams

/// Typed match over a union beat: each handler runs under If(tag == k) with its
/// variant's fields unpacked from the shared data bits — the conditions and the
/// slicing both managed, so a handler reads like a DU match arm.
let matchUnion (u: Union2<'a, 'b>) (beat: UnionBeat) (handle0: 'a -> unit) (handle1: 'b -> unit) =
    If (eq beat.tag (lit 0UL u.tagWidth)) (fun () -> handle0 (variant0 u beat.data))
    Else (fun () -> handle1 (variant1 u beat.data))

/// Broadcast fan-out: every consumer sees every beat, and a beat fires only when
/// every consumer is ready — the source's ready is the AND of theirs. The 1→N
/// modes beyond this: streamBalance below; CONFLATE stays parked, as in Kotlin.
let streamBroadcast (n: int) (s: Stream<'p>) : Stream<'p> list =
    let b = current ()
    let readies = [ for _ in 1..n -> wireBit (b.FreshName "fork_ready") ]
    List.reduce (&&&) readies ==> s.ready

    [ for i in 0 .. n - 1 ->
          let othersReady =
              match [ for j in 0 .. n - 1 do
                          if j <> i then yield readies[j] ] with
              | [] -> lit 1UL 1
              | rs -> List.reduce (&&&) rs

          b.RegisterStreamReady readies[i]

          { s with
              valid = s.valid &&& othersReady
              ready = readies[i] } ]

/// Two-to-one round-robin merge: when both offer, the side not served last wins.
/// Arrival order across inputs is arbitration order — beats should carry their
/// coordinates (the pixel-beat rule) rather than lean on it.
let streamMerge2 (sa: Stream<'p>) (sb: Stream<'p>) : Stream<'p> =
    let b = current ()
    let lastA = regBit (b.FreshName "mergeLastA")
    let takeA = wireBit (b.FreshName "mergeTakeA")
    let outReady = wireBit (b.FreshName "merge_ready")

    (sa.valid &&& (bnot sb.valid ||| bnot lastA)) ==> takeA
    (takeA &&& outReady) ==> sa.ready
    (bnot takeA &&& outReady) ==> sb.ready

    If ((sa.valid ||| sb.valid) &&& outReady) (fun () -> takeA ==> lastA)

    b.RegisterStreamReady outReady

    { payload = sa.layout.unpack (List.map2 (mux takeA) (sa.layout.pack sa.payload) (sa.layout.pack sb.payload))
      valid = sa.valid ||| sb.valid
      ready = outReady
      layout = sa.layout }

/// N→1 as a balanced tree of round-robin pairs — Mandelbrot's lane-results shape.
let rec streamMergeTree (streams: Stream<'p> list) : Stream<'p> =
    match streams with
    | [] -> failwith "merge of nothing"
    | [ s ] -> s
    | _ ->
        streams
        |> List.chunkBySize 2
        |> List.map (function
            | [ a; b ] -> streamMerge2 a b
            | [ s ] -> s
            | _ -> failwith "chunkBySize 2 gave a bigger chunk")
        |> streamMergeTree

/// A design-level stream source: one input port per field plus valid; ready leaves
/// as an output port.
let streamInput name (layout: Layout<'p>) : Stream<'p> =
    let ready = outputBit $"{name}_ready"
    (current ()).RegisterStreamReady ready

    { payload = layout.unpack [ for n, w in layout.fields -> input $"{name}_{n}" w ]
      valid = inputBit $"{name}_valid"
      ready = ready
      layout = layout }

/// A design-level stream sink — the consumer of `s`, driving its ready. Port
/// names and widths come from the stream's own layout.
let streamOutput name (s: Stream<'p>) =
    for (n, w), value in List.zip s.layout.fields (s.layout.pack s.payload) do
        let o = output $"{name}_{n}" w
        value ==> o

    let valid = outputBit $"{name}_valid"
    let ready = inputBit $"{name}_ready"
    s.valid ==> valid
    ready ==> s.ready

/// A combinational transform of the payload, zero cost — ready and valid pass
/// straight through: no module, no state. Shape-preserving: field names kept,
/// widths refreshed from the mapped exprs. A payload-TYPE change is
/// `streamMapTo`, which must be handed the new recipe.
let streamMap (f: 'p -> 'p) (s: Stream<'p>) : Stream<'p> =
    let payload = f s.payload

    { s with
        payload = payload
        layout =
            { s.layout with
                fields = List.map2 (fun (n, _) e -> n, width e) s.layout.fields (s.layout.pack payload) } }

/// The shape-changing map — projections and restructurings to a different
/// payload type. The new type needs a new pack/unpack recipe, so the caller
/// supplies its layout; handshake passes straight through.
let streamMapTo (l: Layout<'q>) (f: 'p -> 'q) (s: Stream<'p>) : Stream<'q> =
    { payload = f s.payload
      valid = s.valid
      ready = s.ready
      layout = l }

/// One ready/valid register stage per layout: holds a beat under backpressure,
/// accepts when empty or when downstream takes this cycle. Ready crosses it
/// combinationally. The apply view is where the two-way handshake meets one-way
/// application: it consumes the upstream stream (driving its ready) and returns
/// the downstream one, registering it for the exactly-one-consumer check.
///
/// Not memoized — a Layout holds functions, which have no useful equality to key
/// on. Instead the module name derives from the fields and two elaborations of one
/// layout produce structurally identical defs, which the emitter's
/// one-name-one-module check collapses: the Rust spike's arrangement, adopted
/// exactly where memoization stops being possible.
let streamStageFor (layout: Layout<'p>) : Stream<'p> -> Stream<'p> =
    let stem =
        layout.fields
        |> List.map (fun (n, w) -> $"{n}%d{w}")
        |> String.concat "_"

    liftStream (
        defineModule
            $"StreamStage_{stem}"
            (fun p ->
                let ins = [ for n, w in layout.fields -> p.inPort $"in_{n}" w ]
                let outs = [ for n, w in layout.fields -> p.outPort $"out_{n}" w ]

                ins, outs, p.inPort "in_valid" 1, p.outPort "in_ready" 1, p.outPort "out_valid" 1, p.inPort "out_ready" 1)
            (fun m (ins, outs, inValid, inReady, outValid, outReady) (s: Stream<'p>) ->
                for port, value in List.zip ins (layout.pack s.payload) do
                    value ==> port

                s.valid ==> inValid
                inReady ==> s.ready
                m.RegisterStreamReady outReady

                { payload = layout.unpack outs
                  valid = outValid
                  ready = outReady
                  layout = layout })
            (fun (ins, outs, inValid, inReady, outValid, outReady) _ ->
                let validR = regBit "validR"
                (bnot validR ||| outReady) ==> inReady

                for (n, w), i, o in List.zip3 layout.fields ins outs do
                    let r = reg $"{n}R" w
                    mux inReady i r ==> r
                    r ==> o

                mux inReady inValid validR ==> validR
                validR ==> outValid))

/// The depth at which a FIFO's storage stops being LUTs and becomes a block.
/// Not a limit — the crossover. Below it the head is a combinational read;
/// at or above it the head is a synchronous read behind a two-slot skid.
let streamFifoDistributedMax = 64

/// Storage-agnostic FIFO internals: the same three claims — order preserved,
/// nothing lost or duplicated, occupancy never above `depth` — over LUTRAM or
/// block RAM, chosen by depth and invisible from the outside.
///
/// The two differ in read latency and in nothing else a caller can observe,
/// which is the point: a `Stream` already hides latency, so the storage a FIFO
/// is built from was never part of its contract. It only looked like it was
/// because the first version could not build the deep one.
type private FifoPorts =
    { head: Expr
      outValid: Expr }

/// The ring both storages are a ring of: a write pointer, a read pointer, and
/// the index each one addresses the array with.
///
/// The pointers are one bit wider than the array, which buys two things at
/// once — a wrap becomes distinguishable from equality, so same-index-same-wrap
/// is empty and same-index-different-wrap is full; and their difference is the
/// occupancy, which is what the block form counts backpressure from.
let private ringPointers name addrWidth =
    let writePtr = reg $"{name}_write" (addrWidth + 1)
    let readPtr = reg $"{name}_read" (addrWidth + 1)

    let writeIndex = wire $"{name}_write_index" addrWidth
    slice (addrWidth - 1) 0 writePtr ==> writeIndex
    let readIndex = wire $"{name}_read_index" addrWidth
    slice (addrWidth - 1) 0 readPtr ==> readIndex

    writePtr, readPtr, writeIndex, readIndex

/// LUTRAM: the head is a combinational read, so a beat is visible the cycle it
/// lands and the whole FIFO is two pointers and an array.
let private distributedFifo name depth addrWidth payloadWidth (packed: Expr) (offered: Expr) (accept: Expr) =
    let store = distributedMem $"{name}_store" addrWidth payloadWidth

    let writePtr, readPtr, writeIndex, readIndex = ringPointers name addrWidth

    let empty = wireBit $"{name}_empty"
    eq writePtr readPtr ==> empty

    let full = wireBit $"{name}_full"

    (eq writeIndex readIndex
     &&& bnot (eq (slice addrWidth addrWidth writePtr) (slice addrWidth addrWidth readPtr)))
    ==> full

    let outReady = wireBit $"{name}_out_ready"
    let outValid = wireBit $"{name}_out_valid"
    bnot empty ==> outValid

    // Accept while there is room; hand over while there is a beat. Each side
    // moves its own pointer, which is the whole of the decoupling.
    bnot full ==> accept

    let push = wireBit $"{name}_push"
    (offered &&& bnot full) ==> push
    memWrite store writeIndex packed push
    If push (fun () -> (writePtr + lit 1UL (addrWidth + 1)) ==> writePtr)

    let pop = wireBit $"{name}_pop"
    (outValid &&& outReady) ==> pop
    If pop (fun () -> (readPtr + lit 1UL (addrWidth + 1)) ==> readPtr)

    let head = wire $"{name}_head" payloadWidth
    memRead store readIndex ==> head

    outReady,
    { head = head
      outValid = outValid }

/// Block RAM: the head is a synchronous read, so the array's word arrives a
/// cycle after its address. Two things follow, and they are the whole of the
/// difference.
///
/// **A two-slot skid**, because a one-slot output register would have to wait
/// for the consumer to take a beat before it could issue the read that replaces
/// it — a read every other cycle, and half the throughput of the LUTRAM form.
/// With two slots a read can be in flight while a beat is still being handed
/// over, which sustains one beat per cycle. That is what makes the storage swap
/// invisible rather than merely correct.
///
/// **Occupancy counted rather than inferred from the pointers.** The words in
/// the skid and the word in flight are as much in the FIFO as the ones in the
/// array, so backpressure is on their sum. Without that a `depth`-deep FIFO
/// would hold `depth + 2`, and "the same buffer, different storage" would be
/// false by three beats.
let private blockFifo name depth addrWidth payloadWidth (packed: Expr) (offered: Expr) (accept: Expr) =
    let store = blockMem $"{name}_store" addrWidth payloadWidth

    let writePtr, readPtr, writeIndex, readIndex = ringPointers name addrWidth

    let stored = wire $"{name}_stored" (addrWidth + 1)
    (writePtr - readPtr) ==> stored

    // The skid: two registers, a slot to write, a slot to read, and a count.
    let slotA = reg $"{name}_slot_a" payloadWidth
    let slotB = reg $"{name}_slot_b" payloadWidth
    let slotWrite = regBit $"{name}_slot_write"
    let slotRead = regBit $"{name}_slot_read"
    let held = reg $"{name}_held" 2
    let inFlight = regBit $"{name}_in_flight"

    let outReady = wireBit $"{name}_out_ready"
    let outValid = wireBit $"{name}_out_valid"
    bnot (eq held (lit 0UL 2)) ==> outValid

    let pop = wireBit $"{name}_pop"
    (outValid &&& outReady) ==> pop

    // Everything the FIFO is holding: the array, the skid, and the word the
    // array is in the middle of handing back.
    let countWidth = addrWidth + 2
    let occupancy = wire $"{name}_occupancy" countWidth

    (pad countWidth stored + pad countWidth held + pad countWidth inFlight)
    ==> occupancy

    let full = wireBit $"{name}_full"
    bnot (lt occupancy (lit (uint64 depth) countWidth)) ==> full
    bnot full ==> accept

    let push = wireBit $"{name}_push"
    (offered &&& bnot full) ==> push
    memWrite store writeIndex packed push
    If push (fun () -> (writePtr + lit 1UL (addrWidth + 1)) ==> writePtr)

    // Fetch whenever the array has a word and the skid will have somewhere to
    // put it a cycle from now — two slots minus what is already spoken for,
    // plus the one a consumer is taking this cycle.
    let reserved = wire $"{name}_reserved" 2
    (held + pad 2 inFlight) ==> reserved

    let fetch = wireBit $"{name}_fetch"
    (bnot (eq stored (lit 0UL (addrWidth + 1)))
     &&& (lt reserved (lit 2UL 2) ||| pop))
    ==> fetch

    If fetch (fun () -> (readPtr + lit 1UL (addrWidth + 1)) ==> readPtr)
    fetch ==> inFlight

    // The array's answer to last cycle's address, which is the word `inFlight`
    // is about to land in the skid.
    let landing = wire $"{name}_landing" payloadWidth
    (memReadPort store readIndex).data ==> landing

    If (inFlight &&& bnot slotWrite) (fun () -> landing ==> slotA)
    If (inFlight &&& slotWrite) (fun () -> landing ==> slotB)
    If inFlight (fun () -> (slotWrite + lit 1UL 1) ==> slotWrite)
    If pop (fun () -> (slotRead + lit 1UL 1) ==> slotRead)

    (held + pad 2 inFlight - pad 2 pop) ==> held

    let head = wire $"{name}_head" payloadWidth
    mux (bnot slotRead) slotA slotB ==> head

    outReady,
    { head = head
      outValid = outValid }

/// A stream FIFO: the same beats, later, with room for `depth` of them in
/// between. Producer and consumer decouple — a burst is absorbed, and a
/// consumer that pauses stops the producer only once the buffer is full.
///
/// First-word-fall-through: the head is readable the cycle it becomes
/// available, because the `Stream` contract says `payload` and `valid` come
/// together and a consumer must never wait a cycle after `valid` to see the
/// data.
///
/// **The storage is chosen from the depth and is not part of the contract.**
/// Up to `streamFifoDistributedMax` the words live in LUTs and the head is a
/// combinational read; above it they live in a block and the head is a
/// synchronous read behind a two-slot skid. Both hold exactly `depth` beats,
/// both sustain a beat per cycle, and both present the same `Stream` — so
/// changing a depth from 8 to 8192 changes where the bits sit and nothing a
/// caller can name. The one visible difference is how long an *empty* FIFO
/// takes to show its first beat, which a `Stream` exists to make nobody's
/// business.
///
/// This is the boundary the DSL draws generally: code may assume a
/// combinational read of something that is *always* LUTs — a register file, a
/// small table — and must not assume one of anything whose storage depends on
/// how big it got.
let streamFifo (name: string) (depth: int) (s: Stream<'p>) : Stream<'p> =
    if depth < 2 then
        failwith $"streamFifo '{name}' needs depth >= 2, got %d{depth} — for a single beat of slack, `stage` is the buffer"

    if not (isPowerOfTwo depth) then
        failwith
            $"streamFifo '{name}' needs a power-of-two depth, got %d{depth} — the array is 2^ceilLog2(depth) words and the pointers wrap on it, so a depth between two powers would quietly hand you the larger buffer"

    let addrWidth = ceilLog2 depth

    // A beat is stored as one word: the layout's fields concatenated, first
    // field at the top. `slice` needs a declared signal, which the read wire is.
    let packFields (fields: Expr list) =
        match fields with
        | [] -> failwith $"streamFifo '{name}': a payload with no fields"
        | first :: rest -> List.fold cat first rest

    let unpackFields (packed: Expr) =
        let widths = List.map snd s.layout.fields
        let mutable offset = List.sum widths

        [ for w in widths ->
              offset <- offset - w
              slice (offset + w - 1) offset packed ]

    let payloadWidth = s.layout.fields |> List.sumBy snd
    let packed = packFields (s.layout.pack s.payload)

    let build =
        if depth <= streamFifoDistributedMax then
            distributedFifo
        else
            blockFifo

    let outReady, ports = build name depth addrWidth payloadWidth packed s.valid s.ready

    (current ()).RegisterStreamReady outReady

    { payload = s.layout.unpack (unpackFields ports.head)
      valid = ports.outValid
      ready = outReady
      layout = s.layout }

/// Pair two streams beat for beat: a result leaves only when both sides have
/// one, and both are consumed together.
///
/// This is a *join*, which `streamMergeTree` is not — a merge arbitrates
/// between beats that are alternatives, and this synchronises beats that belong
/// together. The distinction matters enough to have cost a bug elsewhere: if
/// what you want is "one beat from each", a merge will happily hand you two
/// beats from the same side.
///
/// Both sides must produce in the same order for pairing to mean anything.
/// Where they might not, the payload carries its identity (the pixel-beat rule)
/// and this is the wrong tool.
let streamZip (joined: Layout<'z>) (combine: 'a -> 'b -> 'z) (a: Stream<'a>) (b: Stream<'b>) : Stream<'z> =
    let outReady = wire ((current ()).FreshName "zip_ready") 1

    // Each side is consumed only when the other can also go, which is what
    // keeps them in step without a buffer on either.
    (outReady &&& b.valid) ==> a.ready
    (outReady &&& a.valid) ==> b.ready

    (current ()).RegisterStreamReady outReady

    { payload = combine a.payload b.payload
      valid = a.valid &&& b.valid
      ready = outReady
      layout = joined }

/// Run a stage over part of a beat and carry the rest of it through.
///
/// ```fsharp
/// let results = withContext "dv" 4 operandLayout quotientLayout tagLayout (divider "dv" 8) requests
/// // requests : Stream<(dividend, divisor) * ctx>
/// // results  : Stream<(quotient, remainder) * ctx>
/// ```
///
/// **The stage never learns about the context**, which is the point. A divider
/// takes operands and returns a quotient; whatever else the beat was carrying —
/// the pixel it belongs to, the request id, the other half of a struct — rides
/// alongside in a FIFO and is handed back paired with the result. Without this,
/// every component that costs cycles would have to grow its own passthrough, and
/// every caller would keep a shadow queue and hope the orders matched.
///
/// `depth` is a **throughput** knob, not a correctness one. The context FIFO is
/// pushed and popped in lockstep with the stage's own accept and emit, so it can
/// never mismatch; too shallow only means the source is held off sooner. Deep
/// enough is "as many beats as the stage can have in flight" — 2 for a stage
/// that handles one at a time, more for a pipelined one.
///
/// The stage must produce in the order it accepted. That is true of a single
/// unit and false of a `farm`, which is why `farm` carries context itself
/// rather than being wrapped in this.
let withContext
    (name: string)
    (depth: int)
    (operands: Layout<'a>)
    (results: Layout<'b>)
    (context: Layout<'c>)
    (stage: Stream<'a> -> Stream<'b>)
    (s: Stream<'a * 'c>)
    : Stream<'b * 'c> =
    match streamBroadcast 2 s with
    | [ toStage; toContext ] ->
        // One beat, two destinations, and it moves only when both can take it —
        // so the FIFO holds exactly the contexts of the beats in flight.
        let stageOut = stage (streamMapTo operands fst toStage)
        let held = streamFifo $"{name}_context" depth (streamMapTo context snd toContext)

        streamZip (layoutJoin results context) (fun r c -> r, c) stageOut held
    | _ -> failwith $"withContext '{name}': broadcast 2 gave the wrong arity"

/// Dispatch fan-out: each beat goes to exactly ONE consumer — the lowest-index
/// ready one. The source's ready is the OR of consumer readies, so a beat
/// fires the cycle any consumer can take it, and an unready lane simply
/// receives nothing (the gating run-constant schemes lean on). Arrival order
/// across consumers is arbitration order — beats carry their coordinates (the
/// pixel-beat rule) rather than lean on it. Dispatch to ONE consumer is the
/// direct connection — no arbiter, no gating, the stream handed over as-is —
/// so a design scales from N lanes down to 1 without a degenerate arbiter in
/// the emitted Verilog.
let streamBalance (n: int) (s: Stream<'p>) : Stream<'p> list =
    if n = 1 then [ s ] else

    let b = current ()
    let readies = [ for _ in 1..n -> wireBit (b.FreshName "dispatch_ready") ]
    List.reduce (|||) readies ==> s.ready

    let chosen = oneHotLowest readies

    [ for i in 0 .. n - 1 ->
          b.RegisterStreamReady readies[i]

          { s with
              valid = s.valid &&& chosen[i]
              ready = readies[i] } ]

/// Widest fan the clustered helpers leave flat — Kotlin's `FAN_FLAT_MAX`,
/// set from measurement, not taste: a flat 64-way merge clocked ~182 MHz on
/// the KV260, so a 16-way chain sits comfortably inside a 6 ns period. The
/// deeper reason to stay flat as long as timing allows: a cluster's register
/// node accepts a beat the moment it is empty, PRE-COMMITTING work to that
/// cluster while its lanes are busy — measured as the F# full-scale pod's
/// +33% cycle finding (destroyed late-binding load balance). Flat keeps every
/// beat at the source until some consumer can actually take it.
let fanFlatMax = 16

/// Clustered dispatch above [fanFlatMax]: a top dispatch over ~√N registered
/// cluster nodes, a flat sub-dispatch per cluster — so no single net drives
/// all N consumers (Kotlin's ~106 MHz run-broadcast wall). Kotlin's exact
/// grouping: perNode = ⌈√n⌉ children per node, ⌈n/perNode⌉ nodes, sizes
/// differing by at most one, largest first. Returns exactly `n` streams,
/// cluster-major; the topology is the call's decision, by n — the caller
/// stays N-agnostic (Shape.Auto).
let streamBalanceClustered (n: int) (s: Stream<'p>) : Stream<'p> list =
    if n <= fanFlatMax then
        streamBalance n s
    else
        let perNode = int (ceil (sqrt (float n)))
        let clusters = (n + perNode - 1) / perNode

        let sizes =
            [ for g in 0 .. clusters - 1 -> n / clusters + (if g < n % clusters then 1 else 0) ]

        let stage = streamStageFor s.layout

        streamBalance clusters s
        |> List.map2 (fun size clusterStream -> streamBalance size (stage clusterStream)) sizes
        |> List.concat

/// The merge half of the clustered pair: flat (a bare tree) up to
/// [fanFlatMax], then per-cluster trees with a register stage per node and a
/// top tree — no single chain collects all N lanes (Kotlin's ~182 MHz
/// merge-chain wall). Same grouping as the dispatch side; a single stream
/// passes through direct.
let streamMergeClustered (streams: Stream<'p> list) : Stream<'p> =
    let n = List.length streams

    if n <= fanFlatMax then
        streamMergeTree streams
    else
        let perNode = int (ceil (sqrt (float n)))
        let clusters = (n + perNode - 1) / perNode

        let sizes =
            [ for g in 0 .. clusters - 1 -> n / clusters + (if g < n % clusters then 1 else 0) ]

        let groups, _ =
            sizes
            |> List.mapFold (fun rest size -> List.truncate size rest, List.skip size rest) streams

        let stage = streamStageFor (List.head streams).layout

        groups
        |> List.map (fun group -> stage (streamMergeTree group))
        |> streamMergeTree

/// A link's two stall counters, as wireable Exprs.
type StallCounters = { blocked: Expr; starved: Expr }

/// Telemetry on a stream link: two saturating 32-bit counters —
/// `{name}_blocked` (`valid && !ready`: the consumer is too slow) and
/// `{name}_starved` (`ready && !valid`: the producer is too slow). The stream
/// passes through untouched — the probe drives nothing and consumes nothing —
/// and the name is recorded on the module so `streamReport` finds it. This
/// variant returns the counters for wiring (a status register, an oracle
/// port); `streamProbe` is the fire-and-forget form.
let streamProbeCounters (name: string) (s: Stream<'p>) : Stream<'p> * StallCounters =
    let b = current ()
    let blocked = reg $"{name}_blocked" 32
    let starved = reg $"{name}_starved" 32
    let saturated (c: Expr) = eq c (lit 0xFFFFFFFFUL 32)

    If (s.valid &&& bnot s.ready &&& bnot (saturated blocked)) (fun () -> blocked + lit 1UL 32 ==> blocked)
    If (s.ready &&& bnot s.valid &&& bnot (saturated starved)) (fun () -> starved + lit 1UL 32 ==> starved)

    b.RegisterProbe name
    s, { blocked = blocked; starved = starved }

/// The fire-and-forget probe: the counters are planted and recorded, and
/// `streamReport` finds them later rather than the caller wiring them.
let streamProbe (name: string) (s: Stream<'p>) : Stream<'p> = fst (streamProbeCounters name s)

/// Every probe in the design with its counters, prefixed by instance path to
/// match the flattened Sim names: `(probe, blocked, starved)`. Pass `sim.Peek`
/// — learning where a design stalls costs a tick and a peek, not a Vivado run.
let streamReport (peek: string -> uint64) (design: ModuleDef) =
    let rec collect prefix (md: ModuleDef) =
        [ for p in md.probes -> prefix + p
          for i in md.instances do
              yield! collect (prefix + i.instName + "_") i.child ]

    [ for p in collect "" design -> p, peek $"{p}_blocked", peek $"{p}_starved" ]

// ---------------------------------------------------------------------------
// The snapshot pattern — a coherent view of a live register surface, streamed.
// The source half samples; the conflate half rotates slots so the host always
// gets the freshest COMPLETED frame (STREAM_API case 9, Akka's `conflate`).

/// A free-running coherent snapshot source over a register surface: LATCH
/// copies every row into a shadow in one cycle (the compute core is never
/// paused), then the shadows stream out as (index, data, last) beats under
/// ordinary backpressure, and the source re-latches. Frames that pass while a
/// copy is in flight are never latched at all — conflation by sampling, which
/// is the producer half of keep-latest semantics.
let snapshotSource (name: string) (rows: Expr list) : Stream<Expr * Expr * Expr> =
    let depth = List.length rows

    if depth < 2 then
        failwith "snapshotSource needs at least 2 rows"

    let rowWidth = width (List.head rows)

    if rows |> List.exists (fun r -> width r <> rowWidth) then
        failwith "snapshotSource rows must share one width"

    let addrWidth =
        let mutable w = 1
        while (1 <<< w) < depth do
            w <- w + 1
        w

    let copying = regBit $"{name}_copying"
    let index = reg $"{name}_index" addrWidth
    let shadows = [ for i in 0 .. depth - 1 -> reg $"{name}_shadow_%d{i}" rowWidth ]
    let ready = wireBit $"{name}_ready"
    (current ()).RegisterStreamReady ready

    let isLast = wireBit $"{name}_last"
    eq index (lit (uint64 (depth - 1)) addrWidth) ==> isLast

    If (bnot copying) (fun () ->
        for r, s in List.zip rows shadows do
            r ==> s

        lit 1UL 1 ==> copying
        lit 0UL addrWidth ==> index)

    Else (fun () ->
        If ready (fun () ->
            If isLast (fun () ->
                lit 0UL 1 ==> copying
                lit 0UL addrWidth ==> index)

            Else (fun () -> index + lit 1UL addrWidth ==> index)))

    let data = wire $"{name}_data" rowWidth

    selectIndexed index shadows ==> data

    { payload = (index, data, isLast)
      valid = copying
      ready = ready
      layout = layout3 ("index", addrWidth) ("data", rowWidth) ("last", 1) }

/// The host-facing half of the conflate handshake — the wrapper wires these to
/// slave registers.
type Conflate3Status =
    { ready: Expr // the host holds a slot
      readSlot: Expr // which slot (2 bits) — meaningful while ready is high
      overrun: Expr // capture pulses while already holding one; 8-bit saturating, cleared on release
      irq: Expr } // one-cycle pulse on every grant

/// Keep-latest over three rotating slots — the CONFLATE triple-buffer, slots
/// as address ranges downstream (a DDR framebuffer) rather than a BRAM. Beats
/// pass through tagged with the slot being filled; when a frame's last beat
/// has gone out AND `writerIdle` says the write path has drained (no write may
/// still be in flight toward a slot the host is about to read), the filled
/// slot is published as DONE and the writer rotates to a free one. CAPTURE
/// grants the host the freshest DONE slot; RELEASE returns it. A capture with
/// no DONE frame yet queues and is granted the moment one completes (the
/// Kotlin port dropped a same-cycle race here: a capture arriving in the
/// publish cycle itself queued nowhere and was lost — this one queues it).
let streamConflate3
    (name: string)
    (hostCapture: Expr)
    (hostRelease: Expr)
    (writerIdle: Expr)
    (frames: Stream<Expr * Expr * Expr>)
    : Stream<Expr * Expr * Expr> * Conflate3Status =
    let indexField, dataField =
        match frames.layout.fields with
        | [ (_, iw); (_, dw); (_, 1) ] -> iw, dw
        | _ -> failwith "streamConflate3 wants an (index, data, last) stream — snapshotSource's shape"

    let index, data, last = frames.payload
    let none = lit 3UL 2 // slots are 0/1/2; 3 is the no-slot sentinel

    let writeIdx = reg $"{name}_write_idx" 2
    let doneIdx = regInit $"{name}_done_idx" 2 3UL
    let readIdx = regInit $"{name}_read_idx" 2 3UL
    let captureQueued = regBit $"{name}_capture_queued"
    let overrun = reg $"{name}_overrun" 8
    let irqReg = regBit $"{name}_irq"
    let draining = regBit $"{name}_draining"

    let outReady = wireBit $"{name}_out_ready"
    (current ()).RegisterStreamReady outReady

    let flowing = wireBit $"{name}_flowing"
    bnot draining ==> flowing
    (outReady &&& flowing) ==> frames.ready

    let lastAccepted = wireBit $"{name}_last_accepted"
    (frames.valid &&& outReady &&& flowing &&& last) ==> lastAccepted

    // The frame completes when its beats are out AND the write path is empty —
    // only then may the host be offered the slot.
    let publish = wireBit $"{name}_publish"
    (draining &&& writerIdle) ==> publish

    If lastAccepted (fun () -> lit 1UL 1 ==> draining)
    Else (fun () -> If publish (fun () -> lit 0UL 1 ==> draining))

    let doneValid = wireBit $"{name}_done_valid"
    bnot (eq doneIdx none) ==> doneValid
    let readValid = wireBit $"{name}_read_valid"
    bnot (eq readIdx none) ==> readValid

    let canCapture = wireBit $"{name}_can_capture"
    (hostCapture &&& bnot readValid &&& doneValid) ==> canCapture
    let mustQueue = wireBit $"{name}_must_queue"
    (hostCapture &&& bnot readValid &&& bnot doneValid) ==> mustQueue
    let serviceQueued = wireBit $"{name}_service_queued"
    (captureQueued &&& bnot readValid &&& doneValid &&& bnot hostCapture) ==> serviceQueued

    // Free-slot pick: {0,1,2} minus the write slot and the host's slot. The
    // host's slot is `readIdx` — or, on the very cycle a capture is granted,
    // the `doneIdx` being handed over (using the old readIdx there would let
    // a same-cycle publish rotate the writer onto the slot the host just
    // received: torn reads on a changing grid). With a slot held the third
    // one is 3 - write - held (distinct, so it never underflows); with none,
    // (write+1) mod 3.
    let granting = wireBit $"{name}_granting"
    (canCapture ||| serviceQueued) ==> granting
    let heldSlot = wire $"{name}_held_slot" 2
    mux readValid readIdx doneIdx ==> heldSlot
    let freeSlot = wire $"{name}_free_slot" 2

    mux
            (readValid ||| granting)
            (lit 3UL 2 - writeIdx - heldSlot)
            (mux (eq writeIdx (lit 0UL 2)) (lit 1UL 2) (mux (eq writeIdx (lit 1UL 2)) (lit 2UL 2) (lit 0UL 2)))
    ==> freeSlot

    If publish (fun () -> freeSlot ==> writeIdx)

    If (canCapture &&& publish) (fun () -> writeIdx ==> doneIdx)

    Else (fun () ->
        If (canCapture ||| serviceQueued) (fun () -> none ==> doneIdx)

        Else (fun () -> If publish (fun () -> writeIdx ==> doneIdx)))

    If (canCapture ||| serviceQueued) (fun () -> doneIdx ==> readIdx)
    Else (fun () -> If hostRelease (fun () -> none ==> readIdx))

    If mustQueue (fun () -> lit 1UL 1 ==> captureQueued)

    Else (fun () ->
        If (serviceQueued ||| canCapture ||| hostRelease) (fun () -> lit 0UL 1 ==> captureQueued))

    If (canCapture ||| serviceQueued) (fun () -> lit 1UL 1 ==> irqReg)
    Else (fun () -> lit 0UL 1 ==> irqReg)

    let blocked = wireBit $"{name}_blocked"
    (hostCapture &&& readValid) ==> blocked

    If hostRelease (fun () -> lit 0UL 8 ==> overrun)

    Else (fun () ->
        If (blocked &&& bnot (eq overrun (lit 255UL 8))) (fun () -> overrun + lit 1UL 8 ==> overrun))

    { payload = (writeIdx, index, data)
      valid = (frames.valid &&& flowing)
      ready = outReady
      layout = layout3 ("slot", 2) ("index", indexField) ("data", dataField) },
    { ready = readValid
      readSlot = readIdx
      overrun = overrun
      irq = irqReg }

// ---------------------------------------------------------------------------
// The wormhole family — the one connect vocabulary over the primitives above.
// A sink is any `Stream -> 'r` function, and instances-as-functions means a
// module IS a sink. The fan-out form takes a sink FACTORY and runs it once
// per lane, so the wormhole owns the multiplicity — instance creation happens
// inside the connect, which is what a higher-level flow operator will need
// (it cannot take pre-created instances and still decide its own lane count).

/// What a one-to-many wormhole does with a beat. Required rather than
/// defaulted — the types cannot tell dispatch from broadcast, and guessing
/// wrong is a design that runs.
type FanOut =
    /// Each beat to exactly one lane — the lowest-index ready one wins.
    | Balance
    /// Every beat to every lane, in lockstep (a beat fires when ALL can take it).
    | Broadcast

/// 1→1 — the direct connection, named for the vocabulary:
/// `source |> wormhole sink`.
let wormhole (sink: Stream<'p> -> 'r) (source: Stream<'p>) : 'r = sink source

/// 1→N. `mode` is required — the types cannot decide between dispatch and
/// broadcast. The sink factory runs once per lane with its index; the
/// topology (direct at 1, flat at 2, clustered at ≥3) is decided by `n`.
let wormholeOut (mode: FanOut) (n: int) (sink: int -> Stream<'p> -> 'r) (source: Stream<'p>) : 'r list =
    let streams =
        match mode with
        | Balance -> streamBalanceClustered n source
        | Broadcast -> streamBroadcast n source

    List.mapi sink streams

/// N→1 — merge through the count-decided topology, then `stages` elastic
/// registers before the sink (0 = none; 1 breaks a combinational ready path,
/// the AXI-master arrangement).
let wormholeIn (stages: int) (sink: Stream<'p> -> 'r) (sources: Stream<'p> list) : 'r =
    let merged = streamMergeClustered sources

    let staged =
        List.fold (fun s _ -> streamStageFor s.layout s) merged [ 1..stages ]

    sink staged

/// Land a stream on already-declared boundary ports — the module-body half of
/// `streamOutput`, and the standard export sink for `wormhole`/`wormholeIn`.
let streamExport (portPayload: 'p) (portValid: Expr) (portReady: Expr) (s: Stream<'p>) =
    for target, value in List.zip (s.layout.pack portPayload) (s.layout.pack s.payload) do
        value ==> target

    s.valid ==> portValid
    portReady ==> s.ready

// ---------------------------------------------------------------------------
// The chain surface: thin, layout-aware spellings of the primitives above,
// named for the adopted vocabulary (STREAM_API.md). The layout rides the
// Stream record from its creation site, so a chain names nothing but the
// boundary.

module Stream =
    /// A design-level source with its layout attached.
    let input name (l: Layout<'p>) : Stream<'p> = streamInput name l

    /// A combinational payload transform, zero cost — shape-preserving:
    /// names kept, widths refreshed from the mapped exprs.
    let map (f: 'p -> 'p) (s: Stream<'p>) : Stream<'p> = streamMap f s

    /// The shape-changing map: a new payload type needs a new recipe.
    let mapTo (l: Layout<'q>) (f: 'p -> 'q) (s: Stream<'p>) : Stream<'q> = streamMapTo l f s

    /// Register the stream through `f`: one flop stage storing the transformed
    /// beat, `stage id` the bare elastic register. `stage` is the word that
    /// buys the flop (and its cycle); `map` stays free.
    let stage (f: 'p -> 'p) (s: Stream<'p>) : Stream<'p> =
        let mapped = streamMap f s
        streamStageFor mapped.layout mapped

    /// `n` registered stages, each applying `f` — a depth-`n` pipeline, and
    /// `n` cycles of latency. `stages 0 f` is a direct connection.
    let stages (n: int) (f: 'p -> 'p) (s: Stream<'p>) : Stream<'p> =
        List.fold (fun acc _ -> stage f acc) s [ 1 .. n ]

    /// Arbitrated N→1 join through the count-decided merge topology. Beats
    /// leave in completion order, not issue order — when paths have unequal
    /// latency the payload must carry its identity (the pixel-beat rule).
    let merge (streams: Stream<'p> list) : Stream<'p> = streamMergeClustered streams

    /// The parallel farm: dispatch across `n` workers, merge back to one. The
    /// worker factory runs once per lane with its index — the multiplicity is
    /// owned here, invisible on both sides. `farm 1` is a direct connection
    /// through the single worker. A worker must register something (it is an
    /// async boundary, as in `mapAsyncUnordered`): a fully combinational
    /// worker couples the dispatch grant to the merge arbitration and the
    /// elaboration loop check rejects it.
    let farm (n: int) (worker: int -> Stream<'p> -> Stream<'q>) (s: Stream<'p>) : Stream<'q> =
        streamBalanceClustered n s |> List.mapi worker |> merge

    /// The farm, carrying whatever else the beat holds — the last piece of the
    /// arrangement `withContext` starts.
    ///
    /// ```fsharp
    /// // four dividers, and every quotient still knows which pixel it is
    /// Stream.farmWith "div" 4 2 operands results context (fun _ -> divider "dv" 8) requests
    /// ```
    ///
    /// **No tags, and that is the point.** A farm's results leave in completion
    /// order rather than issue order, which is exactly the case that usually
    /// forces a tag — but the farm knows which lane produced each beat, because
    /// it owns both the dispatch and the merge. So each lane carries its own
    /// context in its own FIFO, and the merge interleaves beats that are already
    /// paired. A tag is only needed when results must be routed *back* to
    /// independent clients, which is `warpFu`.
    ///
    /// This is just `farm` of `withContext`, which is the whole argument for
    /// having built `withContext` as a combinator rather than teaching each
    /// component to carry a payload it never reads.
    ///
    /// `depth` is per lane, and each lane only needs room for the beats *it* has
    /// in flight — so a farm of four one-at-a-time workers wants 2, not 8.
    let farmWith
        (name: string)
        (n: int)
        (depth: int)
        (operands: Layout<'a>)
        (results: Layout<'b>)
        (context: Layout<'c>)
        (worker: int -> Stream<'a> -> Stream<'b>)
        (s: Stream<'a * 'c>)
        : Stream<'b * 'c> =
        streamBalanceClustered n s
        |> List.mapi (fun i lane -> withContext $"{name}_%d{i}" depth operands results context (worker i) lane)
        |> merge

    /// Stall telemetry on a link, chainable: counters ride the module, the
    /// stream passes through untouched (same ready, so the layout rides too).
    let probe (name: string) (s: Stream<'p>) : Stream<'p> = streamProbe name s

    /// A pipeline stage as data: the instance creator, its lane count, and its
    /// options. The connect layer builds everything else — probing, farming,
    /// instance naming — from the record; the options grow here (buffer,
    /// storage, …), never at the use sites.
    type StageSpec<'i, 'o> =
        { create: int option -> Stream<'i> -> Stream<'o>
          laneCount: int
          stallProbe: string option }

    /// The descriptor for one module as a stage: single lane, no probe —
    /// widen with `lanes` / `probed`. The lane index reaches the instance
    /// name only when there is more than one lane.
    let spec name (tm: TypedModule<'io, Stream<'i> -> Stream<'o>>) : StageSpec<'i, 'o> =
        { create =
            fun lane s ->
                let instName =
                    match lane with
                    | Some i -> $"{name}%d{i}"
                    | None -> name

                instanceNamed instName tm s
          laneCount = 1
          stallProbe = None }

    /// The same descriptor from a stream function rather than a module — an
    /// elastic register (`stage id`), a `map`, a hand-written combinator. Akka's
    /// `Flow.fromFunction`. Without this a function-shaped stage cannot join a
    /// pipeline at all, so whether a stage is a module or a function leaks to
    /// the pipeline that composes it; with it, both are `StageSpec` and `lanes`
    /// / `probed` apply to either. No name parameter: a function owns no
    /// instance to name, and anything it instantiates internally is named by
    /// the library (so lane counts above 1 get generated names, not `pod0`…).
    let specFromFunction (f: Stream<'i> -> Stream<'o>) : StageSpec<'i, 'o> =
        { create = fun _ s -> f s
          laneCount = 1
          stallProbe = None }

    /// Replicate a stage across `n` lanes. The stage says nothing about this; a
    /// lane count is a property of the pipeline that uses it, and `farm` owns the
    /// distribute and collect.
    let lanes n (s: StageSpec<'i, 'o>) = { s with laneCount = n }
    /// Probe this stage's intake, so `streamReport` can say whether it is the wall.
    let probed name (s: StageSpec<'i, 'o>) = { s with stallProbe = Some name }

    /// One stage, realized: probe the intake link if asked (blocked there
    /// means THIS stage is the wall), then direct at one lane, farm above.
    let runSpec (s: Stream<'i>) (spec: StageSpec<'i, 'o>) : Stream<'o> =
        let tapped =
            match spec.stallProbe with
            | Some n -> probe n s
            | None -> s

        if spec.laneCount = 1 then
            spec.create None tapped
        else
            farm spec.laneCount (fun i lane -> spec.create (Some i) lane) tapped

    /// The pipeline as a list of stage descriptors. A list keeps one payload
    /// type end to end; type-changing pipelines use the arity forms below —
    /// the layoutN pattern, one function per length.
    let pipeline (specs: StageSpec<'p, 'p> list) (s: Stream<'p>) : Stream<'p> = List.fold runSpec s specs

    /// The type-changing pipelines: same specs, arity-typed so each stage's
    /// payload type may differ from its neighbour's.
    let pipeline2 (a: StageSpec<'a, 'b>) (b: StageSpec<'b, 'c>) (s: Stream<'a>) : Stream<'c> =
        runSpec (runSpec s a) b

    /// Three stages, each free to change the payload type.
    let pipeline3 (a: StageSpec<'a, 'b>) (b: StageSpec<'b, 'c>) (c: StageSpec<'c, 'd>) (s: Stream<'a>) : Stream<'d> =
        runSpec (runSpec (runSpec s a) b) c

    /// Land the stream on named design outputs. The boundary is where a name
    /// is created, so the name is the one thing a chain still says.
    let out (name: string) (s: Stream<'p>) = streamOutput name s

// ---------------------------------------------------------------------------
// Flow — the valid-only half. See `Flow<'p>` in Layout.fs for when it is the
// honest type and when it is an excuse.

/// Consume a stream unconditionally: drive its `ready` high forever and keep
/// the forward half. This is the conversion that was already being written by
/// hand as `lit 1UL 1 ==> s.ready` — same emission, but now the type says the
/// sink cannot refuse, rather than leaving a reader to work out whether that
/// tie-high was load-bearing.
let streamToFlow (s: Stream<'p>) : Flow<'p> =
    lit 1UL 1 ==> s.ready

    { payload = s.payload
      valid = s.valid
      layout = s.layout }

/// Give a flow a `ready` it never had, and report what that costs. The returned
/// term is high on exactly the cycles a beat was dropped — `valid && !ready` —
/// because the flow's producer cannot be told to wait.
///
/// It is returned rather than swallowed on purpose: this is the one place a
/// design silently loses data, so the loss is a value the caller has to do
/// something with. Count it into a status register (`streamProbe`'s counters
/// are the model), assert it never happens, or buffer ahead of it — but not
/// nothing. Where a beat must never be lost, put a `streamFifo` deep enough
/// that `overflowed` is provably dead.
let flowToStream (f: Flow<'p>) : Stream<'p> * Expr =
    let b = current ()
    let ready = wireBit (b.FreshName "flowReady")
    b.RegisterStreamReady ready

    { payload = f.payload
      valid = f.valid
      ready = ready
      layout = f.layout },
    f.valid &&& bnot ready

/// A combinational transform of the payload — the `streamMap` of the valid-only
/// world, and just as free: valid passes straight through.
let flowMap (fn: 'p -> 'p) (f: Flow<'p>) : Flow<'p> =
    let mapped = fn f.payload

    { f with
        payload = mapped
        layout =
            { f.layout with
                fields = List.map2 (fun (n, _) v -> n, width v) f.layout.fields (f.layout.pack mapped) } }

/// One cycle of latency, and nothing else. Registering a stream needs a skid
/// buffer to avoid dropping a beat when the consumer stalls; a flow has no
/// stall to survive, so this is what the shape actually costs — one register
/// per field plus one for `valid`.
let flowStage (name: string) (f: Flow<'p>) : Flow<'p> =
    let validR = regBit $"{name}_valid"
    f.valid ==> validR

    let fields =
        [ for (n, w), value in List.zip f.layout.fields (f.layout.pack f.payload) ->
              let r = reg $"{name}_{n}" w
              value ==> r
              r ]

    { payload = f.layout.unpack fields
      valid = validR
      layout = f.layout }

/// A design-level flow source: ports in, no ready to declare.
let flowInput name (layout: Layout<'p>) : Flow<'p> =
    { payload = layout.unpack [ for n, w in layout.fields -> input $"{name}_{n}" w ]
      valid = inputBit $"{name}_valid"
      layout = layout }

/// A design-level flow sink. Nothing is driven back — which is the shape, and
/// the reason a flow crossing a module boundary costs one direction of wires.
let flowOutput name (f: Flow<'p>) =
    for (n, w), value in List.zip f.layout.fields (f.layout.pack f.payload) do
        let o = output $"{name}_{n}" w
        value ==> o

    let valid = outputBit $"{name}_valid"
    f.valid ==> valid
