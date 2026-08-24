/// The window abstraction, shared by everything that has memory to offer.
///
/// A **window** is an area of memory addressed *by index*. Addresses, strides,
/// sizing and where a region begins all live behind it, so a client handed one
/// cannot tell LUTs from a block from a region of a port on the far side of the
/// chip. That is `notes/DEVICES.md`'s UC2 stated as a type.
///
/// It lives in its own file because two very different things implement it:
/// the on-chip constructors below, and the bus-backed ones in `BusDesigns.fs`.
/// Neither is the abstraction's home.
[<AutoOpen>]
module Warp11.Designs.WindowCatalog

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
type WriteWindow =
    { words: int
      wordWidth: int
      /// `(index, word)` in. Where index zero lives is the window's business.
      write: Stream<Expr * Expr> -> unit }

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
let memWriteWindow (m: Mem) : WriteWindow =
    { words = 1 <<< m.addrWidth
      wordWidth = m.memWidth
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
