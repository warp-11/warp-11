/// A prototype of `notes/DEVICES.md` §10b–§10d, built as new designs rather
/// than as a change to anything: a bus that is a **value**, carries its own
/// **name**, and is **claimed** by whoever drives it, with the arbiter falling
/// out of how many clients a mapping puts on a port.
///
/// **Nothing here is a library change**, deliberately. The stdlib's AXI masters
/// declare `m_axi_*` themselves, so they cannot be handed a bus, and giving
/// them that shape is the refactor this file exists to justify first. The two
/// masters below are therefore minimal ones of their own — single-outstanding,
/// about thirty lines each — written against a bus instead of against the
/// module they happen to be elaborated into. They are not better than
/// `axiMasterWriter`; they are the same idea with the ports taken away.
///
/// What the three designs demonstrate, in order:
///
/// | design | clients | ports | what appears |
/// |---|---|---|---|
/// | `oneOwnerOnePort` | 1 | 1 | nothing — the client is wired straight to the bus |
/// | `twoOwnersOnePort` | 2 | 1 | a round-robin merge, because two clients share |
/// | `twoOwnersTwoPorts` | 2 | 2 | nothing again, on two independently named buses |
///
/// The kernel is identical in all three and never learns which it got.
[<AutoOpen>]
module Warp11.Designs.BusCatalog

open Warp11

// ---------------------------------------------------------------------------
// A bus is a value: named, and owned by exactly one driver
// ---------------------------------------------------------------------------

/// The AW/W/B half of a master's boundary. `prefix` is what the ports are
/// called, which is the whole of what makes a second port sayable — the
/// stdlib's masters hardcode `m_axi`, so a design gets one bus and no more.
///
/// `claimed` is the ownership check. The one-driver rule already refuses two
/// masters that both drive `awvalid`, but that is a per-*signal* guarantee and
/// a bus needs a per-*bus* one: two modules each driving half a bus pass the
/// per-signal rule and produce nonsense (§10d). A claim is `checkStreams`'
/// argument applied to a bus — a stream has one consumer, a bus has one owner.
type AxiWriteBus =
    { prefix: string
      awaddr: Expr
      awvalid: Expr
      awready: Expr
      wdata: Expr
      wstrb: Expr
      wvalid: Expr
      wready: Expr
      bvalid: Expr
      bready: Expr
      claimed: bool ref }

/// The AR/R half. See `AxiWriteBus`.
type AxiReadBus =
    { prefix: string
      araddr: Expr
      arlen: Expr
      arvalid: Expr
      arready: Expr
      rdata: Expr
      rlast: Expr
      rvalid: Expr
      rready: Expr
      claimed: bool ref }

let private axiWidths (who: string) (addrWidth: int) (dataWidth: int) =
    if dataWidth <> 32 && dataWidth <> 64 && dataWidth <> 128 then
        failwith $"{who} dataWidth must be 32, 64 or 128, got %d{dataWidth}"

    if addrWidth < 12 || addrWidth > 40 then
        failwith $"{who} addrWidth must be 12..40, got %d{addrWidth}"

let private sizeEncoding (dataWidth: int) =
    match dataWidth with
    | 32 -> 2UL
    | 64 -> 3UL
    | _ -> 4UL

/// Take ownership of a bus, or say who already has it.
///
/// The message names the bus rather than one of its wires, which is the point:
/// two masters sharing a bus currently fail on `'aw_pending' is declared twice`,
/// an internal register name that says nothing about buses at all.
let private claim (prefix: string) (half: string) (owner: string) (claimed: bool ref) =
    if claimed.Value then
        failwith
            $"the {half} bus '{prefix}' already has an owner — {owner} is the second driver on it. A bus has exactly one master; put an arbiter in front of it and give the arbiter the bus"

    claimed.Value <- true

/// Declare an AW/W/B boundary named `prefix`, and tie the transaction constants
/// that never vary.
let axiWriteBus (prefix: string) (addrWidth: int) (dataWidth: int) : AxiWriteBus =
    axiWidths "axiWriteBus" addrWidth dataWidth

    let awaddr = output $"{prefix}_awaddr" addrWidth
    let awlen = output $"{prefix}_awlen" 8
    let awsize = output $"{prefix}_awsize" 3
    let awburst = output $"{prefix}_awburst" 2
    let awcache = output $"{prefix}_awcache" 4
    let awprot = output $"{prefix}_awprot" 3
    let awvalid = outputBit $"{prefix}_awvalid"
    let awready = inputBit $"{prefix}_awready"
    let wdata = output $"{prefix}_wdata" dataWidth
    let wstrb = output $"{prefix}_wstrb" (dataWidth / 8)
    let wlast = outputBit $"{prefix}_wlast"
    let wvalid = outputBit $"{prefix}_wvalid"
    let wready = inputBit $"{prefix}_wready"
    input $"{prefix}_bresp" 2 |> ignore // trusted OKAY
    let bvalid = inputBit $"{prefix}_bvalid"
    let bready = outputBit $"{prefix}_bready"

    lit 0UL 8 ==> awlen
    lit (sizeEncoding dataWidth) 3 ==> awsize
    lit 1UL 2 ==> awburst // INCR
    lit 0UL 4 ==> awcache
    lit 0UL 3 ==> awprot
    lit 1UL 1 ==> wlast

    { prefix = prefix
      awaddr = awaddr
      awvalid = awvalid
      awready = awready
      wdata = wdata
      wstrb = wstrb
      wvalid = wvalid
      wready = wready
      bvalid = bvalid
      bready = bready
      claimed = ref false }

/// Declare an AR/R boundary named `prefix`. `arlen` is left untied: a
/// single-beat master zeroes it, a burst master drives it.
let axiReadBus (prefix: string) (addrWidth: int) (dataWidth: int) : AxiReadBus =
    axiWidths "axiReadBus" addrWidth dataWidth

    let araddr = output $"{prefix}_araddr" addrWidth
    let arlen = output $"{prefix}_arlen" 8
    let arsize = output $"{prefix}_arsize" 3
    let arburst = output $"{prefix}_arburst" 2
    let arcache = output $"{prefix}_arcache" 4
    let arprot = output $"{prefix}_arprot" 3
    let arvalid = outputBit $"{prefix}_arvalid"
    let arready = inputBit $"{prefix}_arready"
    let rdata = input $"{prefix}_rdata" dataWidth
    input $"{prefix}_rresp" 2 |> ignore
    let rlast = inputBit $"{prefix}_rlast"
    let rvalid = inputBit $"{prefix}_rvalid"
    let rready = outputBit $"{prefix}_rready"

    lit (sizeEncoding dataWidth) 3 ==> arsize
    lit 1UL 2 ==> arburst
    lit 0UL 4 ==> arcache
    lit 0UL 3 ==> arprot

    { prefix = prefix
      araddr = araddr
      arlen = arlen
      arvalid = arvalid
      arready = arready
      rdata = rdata
      rlast = rlast
      rvalid = rvalid
      rready = rready
      claimed = ref false }

// ---------------------------------------------------------------------------
// Masters that are handed a bus instead of declaring one
// ---------------------------------------------------------------------------
//
// Single-outstanding, which is all the prototype needs — the ring in
// `axiMasterWriter` is orthogonal to everything being demonstrated here.
// Every internal name carries the bus prefix, which is the other half of what
// lets two masters share a module: the stdlib's writer names its state
// `aw_pending`, `idle`, `accept`, `addr_q`, so a second one collides with the
// first and with any design that wanted a wire called `idle`.

/// Consume write beats onto `bus`. Returns `bAck` — one pulse per response the
/// slave actually returned, which is the honest "this landed" event.
let busWriteMaster (bus: AxiWriteBus) (beats: Stream<Expr * Expr * Expr>) : Expr =
    claim bus.prefix "write" "this master" bus.claimed
    let p = bus.prefix

    let inAddr, inData, inStrb = beats.payload

    let awPending = regBit $"{p}_aw_pending"
    let wPending = regBit $"{p}_w_pending"
    let bPending = regBit $"{p}_b_pending"
    let addrQ = reg $"{p}_addr_q" (width bus.awaddr)
    let dataQ = reg $"{p}_data_q" (width bus.wdata)
    let strbQ = reg $"{p}_strb_q" (width bus.wstrb)

    let idle = wireBit $"{p}_idle"
    (bnot awPending &&& bnot wPending &&& bnot bPending) ==> idle
    idle ==> beats.ready

    let accept = wireBit $"{p}_accept"
    (beats.valid &&& idle) ==> accept

    If accept (fun () ->
        inAddr ==> addrQ
        inData ==> dataQ
        inStrb ==> strbQ
        lit 1UL 1 ==> awPending
        lit 1UL 1 ==> wPending
        lit 1UL 1 ==> bPending)

    Else (fun () ->
        If (awPending &&& bus.awready) (fun () -> lit 0UL 1 ==> awPending)
        If (wPending &&& bus.wready) (fun () -> lit 0UL 1 ==> wPending)
        If (bPending &&& bus.bvalid) (fun () -> lit 0UL 1 ==> bPending))

    addrQ ==> bus.awaddr
    awPending ==> bus.awvalid
    dataQ ==> bus.wdata
    strbQ ==> bus.wstrb
    wPending ==> bus.wvalid
    bPending ==> bus.bready

    let bAck = wireBit $"{p}_b_ack"
    (bPending &&& bus.bvalid) ==> bAck
    bAck

/// Turn byte addresses into words over `bus`.
let busReadMaster (bus: AxiReadBus) (requests: Stream<Expr>) : Stream<Expr> =
    claim bus.prefix "read" "this master" bus.claimed
    let p = bus.prefix

    lit 0UL 8 ==> bus.arlen // one beat per burst

    let respReady = wireBit $"{p}_resp_ready"
    registerStreamReady respReady

    let arPending = regBit $"{p}_ar_pending"
    let rPending = regBit $"{p}_r_pending"
    let respPending = regBit $"{p}_resp_pending"
    let addrQ = reg $"{p}_rd_addr_q" (width bus.araddr)
    let dataQ = reg $"{p}_rd_data_q" (width bus.rdata)

    let idle = wireBit $"{p}_rd_idle"
    (bnot arPending &&& bnot rPending &&& bnot respPending) ==> idle
    idle ==> requests.ready

    let accept = wireBit $"{p}_rd_accept"
    (requests.valid &&& idle) ==> accept

    If accept (fun () ->
        requests.payload ==> addrQ
        lit 1UL 1 ==> arPending
        lit 1UL 1 ==> rPending
        lit 0UL 1 ==> respPending)

    Else (fun () ->
        If (arPending &&& bus.arready) (fun () -> lit 0UL 1 ==> arPending)

        If (rPending &&& bus.rvalid) (fun () ->
            bus.rdata ==> dataQ
            lit 0UL 1 ==> rPending
            lit 1UL 1 ==> respPending)

        If (respPending &&& respReady) (fun () -> lit 0UL 1 ==> respPending))

    addrQ ==> bus.araddr
    arPending ==> bus.arvalid
    rPending ==> bus.rready

    { payload = dataQ
      valid = respPending
      ready = respReady
      layout = layout1 ("data", width bus.rdata) }

// ---- windows over a region of a bus ----------------------------------------

/// Bytes per beat and the shift that multiplies by it, from a bus's data width.
let private strideOf (dataWidth: int) =
    let bytes = dataWidth / 8

    let rec bits n acc = if n <= 1 then acc else bits (n >>> 1) (acc + 1)

    bytes, bits bytes 0

/// The windows opened on one bus, while they are being opened.
///
/// A token rather than the bus itself, so `createWriteWindow` can only be
/// called somewhere `defineWriteWindows` will afterwards build the master.
/// Opening a window with nowhere for its beats to go is then unrepresentable
/// rather than merely checked.
type WriteWindows =
    private
        { onBus: AxiWriteBus
          opened: ResizeArray<Stream<Expr * Expr * Expr>> }

/// Open a window on `baseAddr`, `words` entries long, indexed from zero.
///
/// The beat wires are declared now and filled in by the window's `write` later,
/// which is what lets the master be built before any client exists. A window
/// nobody writes to would leave them floating, and an undriven wire is *not* an
/// elaboration error here (measured — only a stream net is), so the valid is
/// registered as one:
///
/// ```
/// stream ready 'b_window_valid' driven 0 times (a stream has exactly one consumer)
/// ```
///
/// That wording is `checkStreams`', not this function's, which is why the
/// signal is named for the window: the name is the only part of that sentence
/// able to say which window was left open.
let createWriteWindow (windows: WriteWindows) (name: string) (baseAddr: uint64) (words: int) : WriteWindow =
    let bus = windows.onBus
    let wordWidth = width bus.wdata
    let addrWidth = width bus.awaddr
    let strideBytes, shift = strideOf wordWidth

    let addr = wire $"{name}_addr" addrWidth
    let data = wire $"{name}_data" wordWidth
    let valid = wireBit $"{name}_window_valid"
    let ready = wireBit $"{name}_window_ready"
    registerStreamReady valid // filled in by `write`
    registerStreamReady ready // driven by the merge

    windows.opened.Add
        { payload = addr, data, lit ((1UL <<< strideBytes) - 1UL) strideBytes
          valid = valid
          ready = ready
          layout = axiWriteBeatLayout addrWidth wordWidth }

    { words = words
      wordWidth = wordWidth
      write =
        fun beats ->
            let index, word = beats.payload
            ready ==> beats.ready
            (lit baseAddr addrWidth + pad addrWidth (cat index (lit 0UL shift))) ==> addr
            word ==> data
            beats.valid ==> valid }

/// Open windows on one write bus, and hand back **whatever the body returns**.
///
/// The shape is the caller's: one window, a tuple of two, a list of eight. That
/// matters more than it looks, because `let [ a; b ] = …` is an incomplete
/// pattern and `Directory.Build.props` makes FS0025 an error across this tree —
/// so a function returning a list forces a `match` at every call site that
/// cannot fail. A tuple is complete by construction and destructures cleanly.
///
/// ```fsharp
/// let wa, wb =
///     defineWriteWindows bus (fun windows ->
///         createWriteWindow windows "a" 0x100UL 16,
///         createWriteWindow windows "b" 0x200UL 16)
/// ```
///
/// **The arbiter is here, and it is implied by how many windows the body
/// opened.** One and the client's beats reach the master directly, because
/// `streamMergeTree` at a single stream returns its argument. Two and a
/// round-robin merge appears. Nobody asks for an arbiter; a mapping says how
/// many windows share a port.
///
/// Scoped rather than free-standing because the master can only be built once
/// the last window exists, and "open the windows, then remember to finish the
/// bus" is a step somebody forgets.
let defineWriteWindows (bus: AxiWriteBus) (body: WriteWindows -> 'r) : 'r =
    let windows = { onBus = bus; opened = ResizeArray() }
    let result = body windows

    if windows.opened.Count = 0 then
        failwith
            $"defineWriteWindows '{bus.prefix}': the body opened no windows, so the bus has no master and nothing reaches memory"

    busWriteMaster bus (streamMergeTree (List.ofSeq windows.opened)) |> ignore
    result

/// A read window over a region of a read bus.
///
/// One window per read bus for now: sharing a read port means routing responses
/// back to whoever asked, and the demux for that lives inside `warpFu` and
/// nowhere public — `notes/DEVICES.md` §10b, missing piece 2.
let readWindowOn (bus: AxiReadBus) (baseAddr: uint64) (words: int) : ReadWindow =
    let addrWidth = width bus.araddr
    let _, shift = strideOf (width bus.rdata)

    { words = words
      wordWidth = width bus.rdata
      read =
        fun requests ->
            requests
            |> streamMapTo (layout1 ("addr", addrWidth)) (fun index ->
                lit baseAddr addrWidth + pad addrWidth (cat index (lit 0UL shift)))
            |> busReadMaster bus }

// ---------------------------------------------------------------------------
// The client: it sees two windows and nothing else
// ---------------------------------------------------------------------------

/// Walk `count` words of `source`, keep a running sum, write each partial sum
/// to the matching index of `sink`.
///
/// **Every parameter is a window or a scalar.** No bus, no address, no stride,
/// no base — so the same client runs against LUTs, against a block, or against
/// a region of a port on the far side of the chip, and cannot tell.
///
/// `name` prefixes every signal it owns, which is what lets a design hold two.
let sumClient (name: string) (count: int) (run: Expr) (source: ReadWindow) (sink: WriteWindow) =
    if count > source.words then
        failwith $"sumClient '{name}': asked for %d{count} words of a %d{source.words}-word source window"

    if count > sink.words then
        failwith $"sumClient '{name}': asked to write %d{count} words to a %d{sink.words}-word sink window"

    if source.wordWidth <> sink.wordWidth then
        failwith
            $"sumClient '{name}': a %d{source.wordWidth}-bit source and a %d{sink.wordWidth}-bit sink"

    let wordWidth = source.wordWidth
    let indexWidth = indexWidthFor count

    // Ask for index 0, 1, ... while `run` holds.
    let index = reg $"{name}_index" indexWidth
    let asking = wireBit $"{name}_asking"
    registerStreamReady asking

    let more = wireBit $"{name}_more"
    (run &&& lt index (lit (uint64 count) indexWidth)) ==> more
    If (more &&& asking) (fun () -> index + lit 1UL indexWidth ==> index)

    let words =
        source.read
            { payload = index
              valid = more
              ready = asking
              layout = layout1 ("index", indexWidth) }

    // Results come back in order, so a second counter is all the bookkeeping
    // the write side needs — no tag, no shadow queue.
    let outIndex = reg $"{name}_out_index" indexWidth
    let writing = wireBit $"{name}_writing"
    registerStreamReady writing
    writing ==> words.ready

    let acc = reg $"{name}_acc" wordWidth
    let total = wire $"{name}_total" wordWidth
    (acc + words.payload) ==> total

    If (words.valid &&& writing) (fun () ->
        total ==> acc
        outIndex + lit 1UL indexWidth ==> outIndex)

    sink.write
        { payload = outIndex, total
          valid = words.valid
          ready = writing
          layout = layout2 ("index", indexWidth) ("word", wordWidth) }

// ---------------------------------------------------------------------------
// Three topologies. The client is the same line in all of them.
// ---------------------------------------------------------------------------

let private busCount = 16
let private busIndexWidth = 5
let private busWordWidth = 32

/// A client's own source array, with fill ports so a check can stage data.
///
/// `distributedMem` because `lutReadWindow` reads combinationally and the DSL
/// refuses that on a block. Nothing about the bus depends on this: a client's
/// source and its sink are independent choices, which is the point of there
/// being two windows rather than one memory.
let private sourceFor (name: string) =
    let m = distributedMem $"{name}_src" busIndexWidth busWordWidth

    memWrite
        m
        (input $"{name}_fill_addr" busIndexWidth)
        (input $"{name}_fill_data" busWordWidth)
        (inputBit $"{name}_fill_enable")

    lutReadWindow m

/// **One client, one window, one port — and no arbiter.**
let oneOwnerOnePort =
    design "OneOwnerOnePort" (fun () ->
        let bus = axiWriteBus "m_axi" 32 busWordWidth
        let run = inputBit "run"

        let wa =
            defineWriteWindows bus (fun windows -> createWriteWindow windows "a" 0x100UL busCount)

        sumClient "a" busCount run (sourceFor "a") wa)

/// **Two clients, two windows, one port.** The client lines are unchanged; the
/// body opened a second window and a round-robin merge appeared behind it.
let twoOwnersOnePort =
    design "TwoOwnersOnePort" (fun () ->
        let bus = axiWriteBus "m_axi" 32 busWordWidth
        let run = inputBit "run"

        let wa, wb =
            defineWriteWindows bus (fun windows ->
                createWriteWindow windows "a" 0x100UL busCount,
                createWriteWindow windows "b" 0x200UL busCount)

        sumClient "a" busCount run (sourceFor "a") wa
        sumClient "b" busCount run (sourceFor "b") wb)

/// **Two clients, two windows, two ports.** Same two client lines again, each
/// window now on a bus of its own — `m_axi_hp0` and `m_axi_hp1`, two of the
/// independent paths into DDR this repo has never used two of at once.
let twoOwnersTwoPorts =
    design "TwoOwnersTwoPorts" (fun () ->
        let busA = axiWriteBus "m_axi_hp0" 32 busWordWidth
        let busB = axiWriteBus "m_axi_hp1" 32 busWordWidth
        let run = inputBit "run"

        let wa =
            defineWriteWindows busA (fun windows -> createWriteWindow windows "a" 0x100UL busCount)

        let wb =
            defineWriteWindows busB (fun windows -> createWriteWindow windows "b" 0x100UL busCount)

        sumClient "a" busCount run (sourceFor "a") wa
        sumClient "b" busCount run (sourceFor "b") wb)

/// Entirely on chip: the same client with an array for a sink instead of a
/// window onto a port. No bus in the design at all, and the client is the same
/// line it is above.
let sumWhollyOnChip =
    design "SumWhollyOnChip" (fun () ->
        let results = distributedMem "dst" busIndexWidth busWordWidth
        let probe = input "probe_addr" busIndexWidth
        memRead results probe ==> output "probe_data" busWordWidth

        sumClient "a" busCount (inputBit "run") (sourceFor "a") (memWriteWindow results))

/// Two masters on one bus with no arbiter between them. A function, not a
/// value: elaboration refuses, and refuses *naming the bus*.
///
/// Reaching this takes going around the window layer to the master directly,
/// which is itself the finding — `defineWriteWindows` cannot produce it,
/// because a bus with two windows builds one master over a merge.
let onBusWithTwoOwners () =
    design "OnBusWithTwoOwners" (fun () ->
        let bus = axiWriteBus "m_axi" 32 busWordWidth

        let beats name =
            { payload = input $"{name}_addr" 32, input $"{name}_data" busWordWidth, lit 0xFUL 4
              valid = inputBit $"{name}_valid"
              ready =
                (let r = wireBit $"{name}_ready" in
                 registerStreamReady r
                 r)
              layout = axiWriteBeatLayout 32 busWordWidth }

        busWriteMaster bus (beats "a") |> ignore
        busWriteMaster bus (beats "b") |> ignore)

/// A window opened and never written to. A function, not a value: the merge
/// would otherwise take a floating input, and nothing about the emitted Verilog
/// would say so — `checkStreams` is what turns it into an error.
let onWindowNeverWritten () =
    design "OnWindowNeverWritten" (fun () ->
        let bus = axiWriteBus "m_axi" 32 busWordWidth
        let run = inputBit "run"

        let wa, _unused =
            defineWriteWindows bus (fun windows ->
                createWriteWindow windows "a" 0x100UL busCount,
                createWriteWindow windows "b" 0x200UL busCount)

        sumClient "a" busCount run (sourceFor "a") wa)

/// A read window put to work, so the read half is exercised: the same client,
/// its source now a region of a port rather than an array.
let sumFromReadWindow =
    design "SumFromReadWindow" (fun () ->
        let readBus = axiReadBus "m_axi_hp0" 32 busWordWidth
        let writeBus = axiWriteBus "m_axi_hp1" 32 busWordWidth
        let run = inputBit "run"

        let wa =
            defineWriteWindows writeBus (fun windows -> createWriteWindow windows "a" 0x1000UL busCount)

        sumClient "a" busCount run (readWindowOn readBus 0x0UL busCount) wa)
