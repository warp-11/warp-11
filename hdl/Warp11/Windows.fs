/// **A memory window: an area of memory read or written by index.**
///
/// The second of two levels. A *bus* (`AxiReadBus`/`AxiWriteBus` in
/// `Stdlib.fs`) is transport — a named boundary with exactly one master. A
/// *window* is what a design actually programs against, and it is the only one
/// of the two a client should ever see. Addresses, strides, sizing and where a
/// region begins all live behind it, so the same client runs against LUTs, a
/// block, or a region of a port on the far side of the chip and cannot tell
/// which it got. That is `notes/DEVICES.md`'s UC2 stated as a type.
///
/// **This is not `memReadPort`, and the two are not alternatives.** A
/// `MemReadPort` (`Combinators.fs`) is a *value* that arrives a known number of
/// cycles late, with `through` to carry a caller's own signals across the same
/// distance — right when the storage is fixed and fast, and the caller wants no
/// handshake. A `ReadWindow` is a `Stream`: it costs a ready/valid pair and it
/// buys not having to know. Reach for the port inside a datapath whose storage
/// is settled; reach for the window when something above the design decides
/// what the storage is.
///
/// Compiled after `Stdlib.fs` because the bus-backed constructors need the bus
/// types and the AXI masters. Everything else it uses — `memRead`, `memWrite`,
/// `memReadPort`, `streamMapTo`, `pad`, `ceilLog2` — comes from much earlier.
[<AutoOpen>]
module Warp11.Windows

open Warp11

// ---------------------------------------------------------------------------
// Level two: a memory window
// ---------------------------------------------------------------------------
//
// A bus is transport. A **window** is an area of memory addressed *by index*,
// and it is the only thing a client ever sees. Addresses, strides, sizing and
// where a region begins all live in here — which is the whole difference from
// the first cut of this file, where the client computed `baseAddr + index * 4`
// and thereby knew it was talking to a byte-addressed 32-bit AXI bus.
//
// The two levels compose one way only: a window may be built *over* a bus, and
// a client may be handed a window. A client never holds a bus, and a bus never
// learns what a client is.

/// Read `words` words of `wordWidth` bits, by index.
type ReadWindow =
    { words: int
      wordWidth: int
      /// Indices in, words out. Latency is the window's business, and the
      /// handshake is how it keeps it to itself.
      read: Stream<Expr> -> Stream<Expr> }

/// Write `words` words of `wordWidth` bits, by index.
///
/// **`idle` is what makes the type usable, not a convenience on it.** A design
/// that writes a result and then tells a host it is done must not say so while
/// the last words are still in flight, and a window that returned `unit` gave
/// its client nothing to gate on — so converting a design to windows used to
/// *cost* it the ability to be correct here (`notes/DEVICES.md` §10f).
type WriteWindow =
    { words: int
      wordWidth: int
      /// `(index, word)` in. Where index zero lives is the window's business.
      write: Stream<Expr * Expr> -> unit
      /// High when every word this window has accepted has reached memory.
      ///
      /// The only honest completion source: a signal taken from anywhere
      /// upstream of the storage — the beat count leaving a gatherer, say —
      /// asserts with writes still outstanding. How far "reached memory" is
      /// varies enormously (the same cycle for an array on this chip, a full
      /// AXI write response from DDR), which is exactly why it belongs to the
      /// window rather than to the client.
      ///
      /// **There is no per-word acknowledgement here**, deliberately. Several
      /// windows may share one master, and its write responses carry nothing
      /// saying which window a retiring write came from — attributing them
      /// would mean an owner tag through the master's ring, and nothing in this
      /// tree both shares a write bus and wants per-item granularity. A design
      /// that does (GEP advances a ring pointer per landed write) uses
      /// `axiMasterWriterTrackedOn` and owns its bus outright.
      idle: Expr }

/// How wide an index over `words` entries has to be. The count itself must be
/// *representable*, not merely reachable — `lit 16UL 4` is out of range, so a
/// walk compared against it would never end.
let indexWidthFor (words: int) = max 1 (ceilLog2 (words + 1))

/// An index presented at the width a window addresses in. Widening only:
/// narrowing an index silently drops the top of somebody's address space.
let private fitIndex (targetWidth: int) (index: Expr) =
    if width index = targetWidth then index
    elif width index < targetWidth then pad targetWidth index
    else failwith $"an index %d{width index} bits wide does not fit a window addressed in %d{targetWidth}"

// ---- windows over memory that is on this chip ------------------------------

/// LUTs: the word is there the cycle its index is, so the read is a
/// combinational map and the handshake passes through untouched.
let lutReadWindow (m: Mem) : ReadWindow =
    { words = 1 <<< m.addrWidth
      wordWidth = m.memWidth
      read =
        fun requests ->
            requests
            |> streamMapTo (layout1 ("word", m.memWidth)) (fun index -> memRead m (fitIndex m.addrWidth index)) }

/// An array as a write window: always ready, and the index *is* the address,
/// because for an array that is what an index is.
///
/// `idle` is the constant one. A write to an array on this chip has landed by
/// the end of the cycle it was accepted in, so there is never anything in
/// flight — the same client line that gates a host-visible done on `idle`
/// against DDR compiles to nothing at all here.
let memWriteWindow (m: Mem) : WriteWindow =
    { words = 1 <<< m.addrWidth
      wordWidth = m.memWidth
      idle = lit 1UL 1
      write =
        fun beats ->
            let index, word = beats.payload
            lit 1UL 1 ==> beats.ready
            If beats.valid (fun () -> memWrite m (fitIndex m.addrWidth index) word (lit 1UL 1)) }

/// A block: the word arrives a cycle after its index, and a block cannot be
/// stalled — so the port owes a two-slot skid, sized to hold the beat already
/// in flight plus the one being taken. Issue is credit-gated against that.
///
/// This is `blockFifo`'s mechanism (`Streams.fs:253`) written out again, which
/// is the point rather than an accident: the library already contains exactly
/// this and keeps it `private`, so a design wanting block RAM behind a `Stream`
/// has to rebuild it. That is missing piece 3 in `notes/DEVICES.md` §10b.
let blockReadWindow (name: string) (m: Mem) : ReadWindow =
    { words = 1 <<< m.addrWidth
      wordWidth = m.memWidth
      read =
        fun requests ->
            let outReady = wireBit $"{name}_out_ready"
            registerStreamReady outReady

            let slotA = reg $"{name}_a" m.memWidth
            let slotB = reg $"{name}_b" m.memWidth
            let slotWrite = regBit $"{name}_wsel"
            let slotRead = regBit $"{name}_rsel"
            let held = reg $"{name}_held" 2
            let inFlight = regBit $"{name}_inflight"

            let outValid = wireBit $"{name}_out_valid"
            bnot (eq held (lit 0UL 2)) ==> outValid
            let pop = wireBit $"{name}_pop"
            (outValid &&& outReady) ==> pop

            // Room for the beat this request will produce, counting the one already
            // on its way and the one a consumer is taking this cycle.
            let reserved = wire $"{name}_reserved" 2
            (held + pad 2 inFlight) ==> reserved
            let room = wireBit $"{name}_room"
            (lt reserved (lit 2UL 2) ||| pop) ==> room
            room ==> requests.ready

            (requests.valid &&& room) ==> inFlight

            let landing = wire $"{name}_landing" m.memWidth
            // `fitIndex`, exactly as `lutReadWindow` does: an index narrower
            // than the array's address is legal Verilog and survives emission,
            // but comes back from a FIRRTL round trip explicitly zero-extended
            // — so the two windows disagreed about a memory neither had chosen.
            (memReadPort m (fitIndex m.addrWidth requests.payload)).data ==> landing

            If (inFlight &&& bnot slotWrite) (fun () -> landing ==> slotA)
            If (inFlight &&& slotWrite) (fun () -> landing ==> slotB)
            If inFlight (fun () -> (slotWrite + lit 1UL 1) ==> slotWrite)
            If pop (fun () -> (slotRead + lit 1UL 1) ==> slotRead)
            (held + pad 2 inFlight - pad 2 pop) ==> held

            let head = wire $"{name}_head" m.memWidth
            mux (bnot slotRead) slotA slotB ==> head

            { payload = head
              valid = outValid
              ready = outReady
              layout = layout1 ("word", m.memWidth) } }

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
          opened: ResizeArray<Stream<Expr * Expr * Expr>>
          /// The master's quiescence, declared before the body runs so a window
          /// can hand it to a client, driven after the master exists. Every
          /// window on the bus gets this same signal: shared, it reads as
          /// "nothing from *anyone* here is in flight", which is stronger than
          /// a window needs but never wrong.
          busIdle: Expr }

/// Open a window on `baseAddr`, `words` entries long, indexed from zero.
///
/// **`baseAddr` is an `Expr`, not a constant.** On a board where the host owns
/// the buffer — udmabuf hands out an address at run time — the base arrives in
/// a register the host wrote, and a window that could only take a literal could
/// not describe the ordinary case. A design with a fixed region passes
/// `lit 0x1000UL 32`.
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
let createWriteWindow (windows: WriteWindows) (name: string) (baseAddr: Expr) (words: int) : WriteWindow =
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
      idle = windows.busIdle
      write =
        fun beats ->
            let index, word = beats.payload
            ready ==> beats.ready
            (baseAddr + pad addrWidth (cat index (lit 0UL shift))) ==> addr
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
///     defineWriteWindows bus 4 (fun windows ->
///         createWriteWindow windows "a" (lit 0x100UL 32) 16,
///         createWriteWindow windows "b" (lit 0x200UL 32) 16)
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
let defineWriteWindows (bus: AxiWriteBus) (maxOutstanding: int) (body: WriteWindows -> 'r) : 'r =
    // Declared first and driven last: a window has to hand its client an `idle`
    // while it is being opened, and the master that knows the answer cannot be
    // built until the body has finished opening windows.
    let busIdle = wireBit $"{bus.prefix}_windows_idle"

    let windows =
        { onBus = bus
          opened = ResizeArray()
          busIdle = busIdle }

    let result = body windows

    if windows.opened.Count = 0 then
        failwith
            $"defineWriteWindows '{bus.prefix}': the body opened no windows, so the bus has no master and nothing reaches memory"

    axiMasterWriterWithIdleOn bus maxOutstanding (streamMergeTree (List.ofSeq windows.opened))
    ==> busIdle

    result

/// One window on a bus of its own — which is almost every use.
///
/// `defineWriteWindows` is scoped because several windows share one master and
/// the master cannot be built until the last of them exists. At one window
/// there is nothing to wait for, and making every caller write a lambda and a
/// temporary to say so was the scoped form's constraint leaking into the case
/// that does not have it. This is the read side's shape, on the write side.
let writeWindowOn
    (bus: AxiWriteBus)
    (maxOutstanding: int)
    (name: string)
    (baseAddr: Expr)
    (words: int)
    : WriteWindow =
    defineWriteWindows bus maxOutstanding (fun windows -> createWriteWindow windows name baseAddr words)

/// A read window over a region of a read bus.
///
/// One window per read bus for now: sharing a read port means routing responses
/// back to whoever asked, and the demux for that lives inside `warpFu` and
/// nowhere public — `notes/DEVICES.md` §10b, missing piece 2.
let readWindowOn (bus: AxiReadBus) (maxOutstanding: int) (baseAddr: Expr) (words: int) : ReadWindow =
    let addrWidth = width bus.araddr
    let _, shift = strideOf (width bus.rdata)

    { words = words
      wordWidth = width bus.rdata
      read =
        fun requests ->
            requests
            |> streamMapTo (layout1 ("addr", addrWidth)) (fun index ->
                baseAddr + pad addrWidth (cat index (lit 0UL shift)))
            |> axiMasterReaderOn bus maxOutstanding }
