/// **A region: records a design reads or writes by index, placed by the board.**
///
/// `notes/DEVICES.md` §13. A design names a region — what a record is, how
/// many there are, whether it reads them, writes them or both — and the
/// mapping decides where they live. This file is the part of that which is
/// not about any one board: where a record's fields sit (the register map's
/// own allocator), how regions are laid out in an arena, and the carrier
/// that puts them in the host's memory over AXI masters shared by record.
///
/// What a design sees is a `RegionPort`: indices in and records out, or
/// records in and a beat out once each has landed. Bursts, strides, the 4 KB
/// rule and which port a region rides are all decided here and printed in
/// the seam, never in the design.
[<AutoOpen>]
module Warp11.Regions

open Warp11

/// What a design does with a region. The host's side is implied: it fills
/// what the design reads and drains what the design writes.
type RegionUse =
    | Reads
    | Writes
    | ReadsAndWrites

/// Whether anything outside the design sees a region. A private one has no
/// entry in the seam, and a board may keep it on the chip.
type RegionSharing =
    | SharedWithHost
    | Private

let regionReads =
    function
    | Reads
    | ReadsAndWrites -> true
    | Writes -> false

let regionWrites =
    function
    | Writes
    | ReadsAndWrites -> true
    | Reads -> false

/// A region as a design declares it.
type RegionSpec =
    { name: string
      fields: (string * NumberFormat) list
      records: int
      usage: RegionUse
      sharing: RegionSharing }

// ---------------------------------------------------------------------------
// A record's fields, placed in words
// ---------------------------------------------------------------------------

/// Where a record's fields sit: each its own word in declaration order, a
/// field over 32 bits the next words low first — the register map's
/// allocator, so the seam prints a record with the constants a host already
/// reads registers by.
type RecordShape =
    { fields: (string * NumberFormat) list
      /// One `RoField` entry a field, its offset in bytes from the record's
      /// first.
      entries: RegEntry list
      /// The record's 32-bit words.
      words: int }

let recordShape (fields: (string * NumberFormat) list) : RecordShape =
    if fields.IsEmpty then
        failwith "a record has at least one field"

    let r = RegBuilder()

    for name, f in fields do
        if f.totalWidth > 32 then r.Wide(name, f.totalWidth) |> ignore else r.RoField(name, f.totalWidth) |> ignore

    { fields = fields
      entries = r.EntriesFingerprinted 0
      words = int r.HighWater / 4 }

/// A record's bits, low first.
let recordWidth (shape: RecordShape) = shape.words * 32

/// Where an entry's bits start in the record, and how many there are.
let private bitsOf (e: RegEntry) =
    match e.kind with
    | RoField(bit, w) -> int e.offset * 8 + bit, w
    | _ -> failwith $"record field '{e.name}' is not a field"

/// Whether a field has its word (or words) to itself — and so is widened to
/// fill it, sign-extended when it is signed, as a row's lane is: the host
/// reads a plain `i32` and gets the number.
let private alone (shape: RecordShape) (e: RegEntry) =
    shape.entries |> List.forall (fun o -> o.name = e.name || o.offset <> e.offset)

/// The record's flat bits from its fields' values.
let packRecord (name: string) (shape: RecordShape) (values: Expr list) : Expr =
    if values.Length <> shape.fields.Length then
        failwith $"'{name}': a record of %d{shape.fields.Length} fields was handed %d{values.Length}"

    let segments =
        List.zip3 shape.fields shape.entries values
        |> List.map (fun ((field, f), e, v) ->
            if width v <> f.totalWidth then
                failwith $"'{name}': field '{field}' is %d{f.totalWidth} bits and was handed %d{width v}"

            let at, w = bitsOf e
            let span = if alone shape e then (w + 31) / 32 * 32 else w

            let value =
                if span = w then
                    if isSigned v then asUInt v else v
                elif f.signed then
                    let named =
                        if isNamed v then
                            v
                        else
                            let x = wire ((current ()).FreshName $"{name}_{field}") w
                            v ==> x
                            x

                    asUInt (pad span (asSInt named))
                else
                    pad span (if isSigned v then asUInt v else v)

            at, span, value)
        |> List.sortBy (fun (at, _, _) -> at)

    let pieces, top =
        (([], 0), segments)
        ||> List.fold (fun (acc, cursor) (at, span, value) ->
            let acc = if at > cursor then lit 0UL (at - cursor) :: acc else acc
            value :: acc, at + span)

    let total = recordWidth shape
    let pieces = if total > top then lit 0UL (total - top) :: pieces else pieces
    pieces |> List.reduce cat

/// The fields back out of a record's flat bits. `flat` is a named net — a
/// slice of anything else is refused by the named-operand rule.
let unpackRecord (shape: RecordShape) (flat: Expr) : Expr list =
    List.map2
        (fun (_, f: NumberFormat) e ->
            let at, w = bitsOf e
            let bits = slice (at + w - 1) at flat
            if f.signed then asSInt bits else bits)
        shape.fields
        shape.entries

// ---------------------------------------------------------------------------
// Regions in an arena
// ---------------------------------------------------------------------------

/// The page no burst may cross.
let axiPageBytes = 4096

/// A region placed: its record shape, how a carrier moves a record, and
/// where it starts.
type RegionPlace =
    { spec: RegionSpec
      shape: RecordShape
      /// Beats a record takes on a bus of the arena's width.
      beats: int
      /// Bytes from one record to the next: a power of two, at least a beat,
      /// so a record never straddles a 4 KB page.
      strideBytes: int
      /// Bytes from the arena's start.
      offset: int }

/// Bytes the region spans.
let regionBytes (p: RegionPlace) = p.spec.records * p.strideBytes

/// Lay `regions` out in an arena of `arenaBytes` behind a bus `beatWidth`
/// bits wide: declaration order, each on a 4 KB page.
let placeRegions (beatWidth: int) (arenaBytes: int) (regions: RegionSpec list) : RegionPlace list =
    let beatBytes = beatWidth / 8

    let placed, used =
        ((0, []), regions)
        ||> List.fold (fun (cursor, acc) spec ->
            if spec.records < 1 then
                failwith $"region '{spec.name}': a region holds at least one record, not %d{spec.records}"

            let shape = recordShape spec.fields
            let beats = (recordWidth shape + beatWidth - 1) / beatWidth
            let stride = 1 <<< ceilLog2 (beats * beatBytes)

            if stride > axiPageBytes then
                failwith
                    $"region '{spec.name}': a record of %d{shape.words * 4} bytes is over a 4 KB page, and a record that splits into several bursts is not built"

            let offset = (cursor + axiPageBytes - 1) / axiPageBytes * axiPageBytes

            let p =
                { spec = spec
                  shape = shape
                  beats = beats
                  strideBytes = stride
                  offset = offset }

            offset + regionBytes p, p :: acc)
        |> fun (cursor, acc) -> List.rev acc, cursor

    if used > arenaBytes then
        let sizes =
            placed |> List.map (fun p -> $"'{p.spec.name}' %d{regionBytes p} B") |> String.concat ", "

        failwith $"the regions take %d{used} bytes ({sizes}) and the arena holds %d{arenaBytes}"

    placed

/// The seam's lines for one region: its place, its record's size and
/// stride, and every field as a register map prints one — under the
/// region's name.
let regionSeamLines (p: RegionPlace) : string list =
    let s = upperSnake p.spec.name

    [ $"pub const {s}_REGION_OFFSET: usize = 0x%x{p.offset};"
      $"pub const {s}_RECORDS: usize = %d{p.spec.records};"
      $"pub const {s}_RECORD_BYTES: usize = %d{p.shape.words * 4};"
      $"pub const {s}_STRIDE_BYTES: usize = %d{p.strideBytes};" ]
    @ regEntryRsLines $"{s}_" p.shape.entries

// ---------------------------------------------------------------------------
// What a design holds for a region
// ---------------------------------------------------------------------------

/// A region as a carrier hands it to the design, untyped: records are their
/// fields' values, in declaration order.
type RegionPort =
    { /// Indices in, records out, in the order they were asked for.
      read: Stream<Expr> -> Stream<Expr list>
      /// The index and then the record's fields in; one beat out per record
      /// once it has landed, in the order they went in.
      write: Stream<Expr list> -> Stream<Expr> }

/// A read or write a region was asked for that its declaration does not
/// allow — refused naming the region.
let refusedRegionPort (name: string) (usage: RegionUse) : RegionPort =
    { read =
        fun _ -> failwith $"region '{name}' is declared {usage}, and the design read it"
      write =
        fun _ -> failwith $"region '{name}' is declared {usage}, and the design wrote it" }

// ---------------------------------------------------------------------------
// The host's memory: regions behind shared AXI masters
// ---------------------------------------------------------------------------

/// Bursts a region read master keeps in flight — the batch top's reader's
/// figure, where four bursts of a record each hide the round trip.
let regionReadOutstanding = 4

/// The landed beats a write client may owe before it stalls: how many of its
/// records can be in flight or landed-and-untaken at once.
let regionWriteCredits = 16

type private ReadClient =
    { place: RegionPlace
      requests: Stream<Expr>
      /// Driven by the master: the record, as a flat net, and its handshake.
      flat: Expr
      recordValid: Expr
      recordReady: Expr }

type private WriteClient =
    { place: RegionPlace
      incoming: Stream<Expr list>
      /// Landed beats owed back, driven by the master.
      landedValid: Expr
      landedReady: Expr }

/// The owner of each transaction, pushed as it is issued and popped as its
/// answer comes home.
let private ownerWidth (clients: int) = max 1 (ceilLog2 clients)

/// A stream whose `ready` the caller drives, registered as such.
let private readyWire (name: string) =
    let r = wireBit name
    registerStreamReady r
    r

/// One read master and the regions on it. Requests merge round-robin a
/// record at a time; the winner's owner rides a FIFO beside the master, and
/// AXI's in-order R at one ID makes its head the owner of whatever beats
/// arrive. The beats assemble into one record register, which goes to its
/// owner whole.
let private buildReadMaster
    (addressOf: RegionPlace -> Expr -> Expr)
    (armed: Expr)
    (bus: AxiReadBus)
    (outstanding: int)
    (clients: ReadClient list)
    =
    let named (what: string) = $"{bus.prefix}_regions_rd_{what}"
    let beatWidth = width bus.rdata
    let ow = ownerWidth clients.Length
    let maxBeats = clients |> List.map (fun c -> c.place.beats) |> List.max

    let requestLayout = layoutOfList [ "addr", unsignedInt 32; "len", unsignedInt 8; "owner", unsignedInt ow ]

    let merged =
        clients
        |> List.mapi (fun i c ->
            c.requests
            |> streamMapTo requestLayout (fun index ->
                [ addressOf c.place index; lit (uint64 (c.place.beats - 1)) 8; lit (uint64 i) ow ]))
        |> streamMergeTree

    let addr, len, owner =
        match merged.payload with
        | [ a; l; o ] -> a, l, o
        | _ -> failwith "a read request is three fields"

    // One beat, two destinations: the master and the owner FIFO, moving
    // together or not at all.
    let requestReady = readyWire (named "request_ready")
    let ownerInReady = readyWire (named "owner_in_ready")

    let toMaster: Stream<Expr * Expr> =
        { payload = addr, len
          valid = merged.valid &&& armed &&& ownerInReady
          ready = requestReady
          layout = layout2 ("addr", 32) ("len", 8) }

    let ownersIn: Stream<Expr> =
        { payload = owner
          valid = merged.valid &&& armed &&& requestReady
          ready = ownerInReady
          layout = layout1 ("owner", ow) }

    let owners =
        ownersIn
        |> streamFifo (named "owners") (max 2 (2 * outstanding))

    (armed &&& requestReady &&& ownerInReady) ==> merged.ready

    let beats =
        if maxBeats < 2 then
            // A one-beat record has no burst to speak of; the burst master
            // still carries it, at ARLEN zero.
            axiMasterReaderBurstOn bus outstanding 2 toMaster
        else
            axiMasterReaderBurstOn bus outstanding (max 2 maxBeats) toMaster

    let data, last = beats.payload

    // --- assembly: a record a register, handed over whole ------------------
    let full = regBit (named "full")
    let heldOwner = reg (named "held_owner") ow
    let beatCount = reg (named "beat") (max 1 (ceilLog2 (maxBeats + 1)))
    let slots = [ for k in 0 .. maxBeats - 1 -> reg (named $"slot_%d{k}") beatWidth ]
    let record = wire (named "record") (maxBeats * beatWidth)
    (slots |> List.rev |> List.reduce cat) ==> record

    let taken = wireBit (named "taken")

    (clients
     |> List.map (fun c -> c.recordValid &&& c.recordReady)
     |> List.reduce (|||))
    ==> taken

    // A beat lands while the record is empty, or while its owner is taking it.
    (bnot full ||| taken) ==> beats.ready
    let landing = wireBit (named "landing")
    (beats.valid &&& (bnot full ||| taken)) ==> landing

    for k in 0 .. maxBeats - 1 do
        If (landing &&& eq beatCount (lit (uint64 k) (width beatCount))) (fun () -> data ==> slots[k])

    let complete = wireBit (named "complete")
    (landing &&& last) ==> complete

    If landing (fun () ->
        ifElse [
            (last, fun () -> lit 0UL (width beatCount) ==> beatCount)
            (otherwise, fun () -> beatCount + lit 1UL (width beatCount) ==> beatCount) ])

    ifElse [
        (complete, fun () ->
            lit 1UL 1 ==> full
            owners.payload ==> heldOwner)
        (taken, fun () -> lit 0UL 1 ==> full) ]

    // The owner leaves the FIFO with its record's last beat; it was pushed
    // when the request went out, so it is always there by then.
    complete ==> owners.ready

    for i, c in List.indexed clients do
        (full &&& eq heldOwner (lit (uint64 i) ow)) ==> c.recordValid
        slice (width c.flat - 1) 0 record ==> c.flat

/// One write master and the regions on it. Records merge round-robin; the
/// winner is held, its descriptor and beats go to the burst master, and its
/// owner rides a FIFO popped by each write response — AXI's in-order B at
/// one ID makes the head the owner of the burst that just landed. Each
/// client counts what it is owed and is stalled while it owes its credits.
let private buildWriteMaster
    (addressOf: RegionPlace -> Expr -> Expr)
    (armed: Expr)
    (bus: AxiWriteBus)
    (outstanding: int)
    (clients: WriteClient list)
    =
    let named (what: string) = $"{bus.prefix}_regions_wr_{what}"
    let beatWidth = width bus.wdata
    let ow = ownerWidth clients.Length
    let maxBeats = clients |> List.map (fun c -> c.place.beats) |> List.max
    let flatWidth = maxBeats * beatWidth
    let creditWidth = ceilLog2 (regionWriteCredits + 1)

    // --- per client: credits, and the landed beats owed back ---------------
    let gated =
        [ for i, c in List.indexed clients ->
              let n (what: string) = $"{c.place.spec.name}_region_{what}"
              let owed = reg (n "owed") creditWidth
              let landedOwed = reg (n "landed_owed") creditWidth
              let room = wireBit (n "room")
              lt owed (lit (uint64 regionWriteCredits) creditWidth) ==> room

              let index, values =
                  match c.incoming.payload with
                  | index :: values -> index, values
                  | [] -> failwith $"region '{c.place.spec.name}': a write is an index and a record"

              let flat = wire (n "write_record") (recordWidth c.place.shape)
              packRecord c.place.spec.name c.place.shape values ==> flat

              let toMerge = readyWire (n "write_ready")
              (toMerge &&& room) ==> c.incoming.ready
              let accepted = wireBit (n "accepted")
              (c.incoming.valid &&& toMerge &&& room) ==> accepted

              let landedHere = wireBit (n "landed_here")
              let tokenTaken = wireBit (n "token_taken")
              (c.landedValid &&& c.landedReady) ==> tokenTaken
              bnot (eq landedOwed (lit 0UL creditWidth)) ==> c.landedValid

              owed + pad creditWidth accepted - pad creditWidth tokenTaken ==> owed
              landedOwed + pad creditWidth landedHere - pad creditWidth tokenTaken ==> landedOwed

              landedHere,
              ({ payload = [ addressOf c.place index; pad flatWidth flat; lit (uint64 i) ow ]
                 valid = c.incoming.valid &&& room
                 ready = toMerge
                 layout = layoutOfList [ "addr", unsignedInt 32; "record", unsignedInt flatWidth; "owner", unsignedInt ow ] }: Stream<Expr list>) ]

    let merged = gated |> List.map snd |> streamMergeTree

    let addr, record, owner =
        match merged.payload with
        | [ a; r; o ] -> a, r, o
        | _ -> failwith "a write is three fields"

    // --- the held record: its descriptor, then its beats ---------------------
    let held = regBit (named "held")
    let heldAddr = reg (named "held_addr") 32
    let heldRecord = reg (named "held_record") flatWidth
    let heldOwner = reg (named "held_owner") ow
    let descriptorSent = regBit (named "descriptor_sent")
    let beatCount = reg (named "beat") 8

    bnot held ==> merged.ready
    let load = wireBit (named "load")
    (merged.valid &&& bnot held) ==> load

    let heldLen = wire (named "held_len") 8

    (clients
     |> List.mapi (fun i c -> i, lit (uint64 (c.place.beats - 1)) 8)
     |> List.rev
     |> List.fold (fun acc (i, len) -> if clients.Length = 1 then len else mux (eq heldOwner (lit (uint64 i) ow)) len acc) (lit 0UL 8))
    ==> heldLen

    let descriptorReady = readyWire (named "descriptor_ready")
    let ownerInReady = readyWire (named "owner_in_ready")
    let descriptorWanted = wireBit (named "descriptor_wanted")
    (held &&& bnot descriptorSent &&& armed) ==> descriptorWanted

    let descriptors: Stream<Expr * Expr> =
        { payload = heldAddr, heldLen
          valid = descriptorWanted &&& ownerInReady
          ready = descriptorReady
          layout = layout2 ("addr", 32) ("len", 8) }

    let ownersIn: Stream<Expr> =
        { payload = heldOwner
          valid = descriptorWanted &&& descriptorReady
          ready = ownerInReady
          layout = layout1 ("owner", ow) }

    let owners =
        ownersIn
        |> streamFifo (named "owners") (max 2 (2 * outstanding))

    let descriptorFire = wireBit (named "descriptor_fire")
    (descriptorWanted &&& descriptorReady &&& ownerInReady) ==> descriptorFire

    // A beat goes once its descriptor has: the burst master takes no beat
    // before the burst it belongs to.
    let beatSlices = [ for k in 0 .. maxBeats - 1 -> slice ((k + 1) * beatWidth - 1) (k * beatWidth) heldRecord ]
    let beatData = wire (named "beat_data") beatWidth
    (if maxBeats = 1 then beatSlices.Head else selectIndexed beatCount beatSlices) ==> beatData
    let beatReady = readyWire (named "beat_ready")
    let beatValid = wireBit (named "beat_valid")
    (held &&& descriptorSent) ==> beatValid

    let beatsOut: Stream<Expr * Expr> =
        { payload = beatData, lit ((1UL <<< (beatWidth / 8)) - 1UL) (beatWidth / 8)
          valid = beatValid
          ready = beatReady
          layout = layout2 ("data", beatWidth) ("strb", beatWidth / 8) }

    let beatFire = wireBit (named "beat_fire")
    (beatValid &&& beatReady) ==> beatFire
    let lastBeat = wireBit (named "last_beat")
    eq beatCount heldLen ==> lastBeat

    ifElse [
        (load, fun () ->
            lit 1UL 1 ==> held
            addr ==> heldAddr
            record ==> heldRecord
            owner ==> heldOwner
            lit 0UL 1 ==> descriptorSent
            lit 0UL 8 ==> beatCount)
        (beatFire &&& lastBeat, fun () ->
            lit 0UL 1 ==> held
            lit 0UL 8 ==> beatCount)
        (beatFire, fun () -> beatCount + lit 1UL 8 ==> beatCount)
        (descriptorFire, fun () -> lit 1UL 1 ==> descriptorSent) ]

    let w = axiMasterWriterBurstOn bus outstanding descriptors beatsOut

    // Each response is the head owner's burst landing.
    w.landed ==> owners.ready

    for i, (landedHere, _) in List.indexed gated do
        (w.landed &&& eq owners.payload (lit (uint64 i) ow)) ==> landedHere

/// Regions in the host's memory, on the masters a top built for them. Clients
/// register as the design calls `read`/`write`; `Finish` builds each master
/// once every client exists — the scoped shape `defineWriteWindows` has, for
/// the same reason.
type HostMemoryRegions
    internal
    (
        regionsBase: Expr,
        armedBy: Expr option,
        readMasters: (AxiReadBus * RegionPlace list) list,
        writeMasters: (AxiWriteBus * RegionPlace list) list,
        readOutstanding: int,
        writeOutstanding: int
    ) =
    let readClients = System.Collections.Generic.Dictionary<string, ReadClient>()
    let writeClients = System.Collections.Generic.Dictionary<string, WriteClient>()
    let addrWidth = 32

    // Nothing reaches the interconnect until the host has said where the
    // arena is: a zero base is disarmed, which also keeps an unarmed master
    // off address zero.
    let armed = wireBit "regions_armed"

    do
        match armedBy with
        | Some a -> a ==> armed
        | None -> bnot (eq regionsBase (lit 0UL (width regionsBase))) ==> armed

    let places =
        (readMasters |> List.collect snd) @ (writeMasters |> List.collect snd)
        |> List.distinctBy (fun p -> p.spec.name)

    let addressOf (p: RegionPlace) (index: Expr) =
        let shift = ceilLog2 p.strideBytes

        if width index + shift > addrWidth then
            failwith $"region '{p.spec.name}': an index %d{width index} bits wide does not address a 32-bit arena"

        let index = if isSigned index then asUInt index else index
        regionsBase + lit (uint64 p.offset) addrWidth + pad addrWidth (cat index (lit 0UL shift))

    member _.Port(name: string) : RegionPort =
        let p =
            match places |> List.tryFind (fun p -> p.spec.name = name) with
            | Some p -> p
            | None -> failwith $"no region called '{name}' in the host's memory"

        let read (requests: Stream<Expr>) : Stream<Expr list> =
            if readClients.ContainsKey name then
                failwith $"region '{name}' is read once: hand every index to one `read`"

            let flat = wire $"{name}_region_record" (recordWidth p.shape)
            let valid = wireBit $"{name}_region_record_valid"
            let ready = readyWire $"{name}_region_record_ready"

            readClients[name] <-
                { place = p
                  requests = requests
                  flat = flat
                  recordValid = valid
                  recordReady = ready }

            ({ payload = unpackRecord p.shape flat
               valid = valid
               ready = ready
               layout = layoutOfList p.shape.fields }: Stream<Expr list>)

        let write (incoming: Stream<Expr list>) : Stream<Expr> =
            if writeClients.ContainsKey name then
                failwith $"region '{name}' is written once: hand every record to one `write`"

            let landedValid = wireBit $"{name}_region_landed_valid"
            let landedReady = readyWire $"{name}_region_landed_ready"

            writeClients[name] <-
                { place = p
                  incoming = incoming
                  landedValid = landedValid
                  landedReady = landedReady }

            ({ payload = lit 1UL 1
               valid = landedValid
               ready = landedReady
               layout = layout1 ("landed", 1) }: Stream<Expr>)

        match p.spec.usage with
        | Reads -> { refusedRegionPort name Reads with read = read }
        | Writes -> { refusedRegionPort name Writes with write = write }
        | ReadsAndWrites -> { read = read; write = write }

    /// Build the masters, now that every client has registered. A declared
    /// use the design never exercised is refused: its declaration says the
    /// design reads the region, and nothing does.
    member _.Finish() =
        for p in places do
            if regionReads p.spec.usage && not (readClients.ContainsKey p.spec.name) then
                failwith $"region '{p.spec.name}' is declared {p.spec.usage} and the design never reads it"

            if regionWrites p.spec.usage && not (writeClients.ContainsKey p.spec.name) then
                failwith $"region '{p.spec.name}' is declared {p.spec.usage} and the design never writes it"

        for bus, regions in readMasters do
            buildReadMaster addressOf armed bus readOutstanding [ for p in regions -> readClients[p.spec.name] ]

        for bus, regions in writeMasters do
            buildWriteMaster addressOf armed bus writeOutstanding [ for p in regions -> writeClients[p.spec.name] ]

/// Regions in the host's memory behind these masters, `regionsBase` the
/// arena's address. Call `Finish` once the design has taken its ports.
let hostMemoryRegions regionsBase readMasters writeMasters readOutstanding writeOutstanding =
    HostMemoryRegions(regionsBase, None, readMasters, writeMasters, readOutstanding, writeOutstanding)

/// Regions in a memory the board carries rather than the host's — armed
/// from the start, at addresses from zero.
let boardMemoryRegions readMasters writeMasters readOutstanding writeOutstanding =
    HostMemoryRegions(lit 0UL 32, Some(lit 1UL 1), readMasters, writeMasters, readOutstanding, writeOutstanding)

// ---------------------------------------------------------------------------
// On the chip: a region a block RAM
// ---------------------------------------------------------------------------

/// An index at a memory's address width. Wider is sliced to the low bits —
/// an index past the region is the caller's bug on every carrier, and the
/// host's memory wraps it the same way within the region's stride.
let private atWidth (name: string) (addrWidth: int) (index: Expr) =
    let index = if isSigned index then asUInt index else index

    if width index = addrWidth then
        index
    elif width index < addrWidth then
        pad addrWidth index
    else
        let named = wire ((current ()).FreshName $"{name}_index") (width index)
        index ==> named
        slice (addrWidth - 1) 0 named

/// Private regions in block RAM: a record a word, `records` deep. A read is
/// `blockReadWindow`'s — a cycle late, behind a two-slot skid — and a write
/// lands the cycle it is taken, its landed beat held until the caller takes
/// it. Nothing outside the design sees these, so the seam has no entry for
/// them and the host has nothing to do.
type OnChipRegions internal (specs: RegionSpec list) =
    let read = System.Collections.Generic.HashSet<string>()
    let written = System.Collections.Generic.HashSet<string>()
    let memories = System.Collections.Generic.Dictionary<string, Mem * RecordShape>()

    member _.Holds(name: string) = specs |> List.exists (fun s -> s.name = name)

    member _.Port(name: string) : RegionPort =
        let spec = specs |> List.find (fun s -> s.name = name)

        let mem, shape =
            match memories.TryGetValue name with
            | true, found -> found
            | _ ->
                let shape = recordShape spec.fields
                let created = blockMem $"{name}_region" (max 1 (ceilLog2 spec.records)) (recordWidth shape), shape
                memories[name] <- created
                created

        let readPort (requests: Stream<Expr>) : Stream<Expr list> =
            if not (read.Add name) then
                failwith $"region '{name}' is read once: hand every index to one `read`"

            let window = blockReadWindow $"{name}_region_read" mem

            requests
            |> streamMapTo (layout1 ("index", mem.addrWidth)) (atWidth name mem.addrWidth)
            |> window.read
            |> streamMapTo (layoutOfList shape.fields) (unpackRecord shape)

        let writePort (incoming: Stream<Expr list>) : Stream<Expr> =
            if not (written.Add name) then
                failwith $"region '{name}' is written once: hand every record to one `write`"

            let index, values =
                match incoming.payload with
                | index :: values -> index, values
                | [] -> failwith $"region '{name}': a write is an index and a record"

            let flat = wire $"{name}_region_write_record" (recordWidth shape)
            packRecord name shape values ==> flat

            let landedValid = regBit $"{name}_region_landed_valid"
            let landedReady = readyWire $"{name}_region_landed_ready"
            let room = wireBit $"{name}_region_room"
            (bnot landedValid ||| landedReady) ==> room
            room ==> incoming.ready
            let accept = wireBit $"{name}_region_accept"
            (incoming.valid &&& room) ==> accept

            memWrite mem (atWidth name mem.addrWidth index) flat accept

            ifElse
                [ (accept, fun () -> lit 1UL 1 ==> landedValid)
                  (landedReady, fun () -> lit 0UL 1 ==> landedValid) ]

            { payload = lit 1UL 1
              valid = landedValid
              ready = landedReady
              layout = layout1 ("landed", 1) }

        match spec.usage with
        | Reads -> { refusedRegionPort name Reads with read = readPort }
        | Writes -> { refusedRegionPort name Writes with write = writePort }
        | ReadsAndWrites -> { read = readPort; write = writePort }

    /// The same rule as the host's memory: a declared use the design never
    /// exercised is refused.
    member _.Finish() =
        for spec in specs do
            if regionReads spec.usage && not (read.Contains spec.name) then
                failwith $"region '{spec.name}' is declared {spec.usage} and the design never reads it"

            if regionWrites spec.usage && not (written.Contains spec.name) then
                failwith $"region '{spec.name}' is declared {spec.usage} and the design never writes it"

/// Private regions on the chip.
let onChipRegions (specs: RegionSpec list) = OnChipRegions specs

// ---------------------------------------------------------------------------
// Over the host's link: regions the host keeps, a record a request
// ---------------------------------------------------------------------------
//
// The link already carries words the fabric streams out unasked and words the
// host plays in unasked (`SerialRegMap.fs`). A region rides those: the fabric
// streams a header word — what, which region, which record — and for a write
// the record's words after it; the host answers a read with the record's
// words and a write with one word, which is what *landed* means here: the
// host has it. One operation is in flight at a time, because a played word
// cannot be refused and one is all the fabric then has to buffer — correct,
// and as slow as a round trip over the link (`notes/DEVICES.md` §13, fork 4).

/// A header's operation, in its top four bits.
let linkRegionRead = 1UL
let linkRegionWrite = 2UL

/// A header word: the operation, the region's id, the record's index.
let linkRegionIndexBits = 24
let linkRegionIdBits = 4

/// Regions the host keeps, reached over the link. `out` is the stream of
/// words the link sends — declared by the top before the link exists and
/// driven here at `Finish` — and `played` the words the host sends back.
type LinkRegions internal (specs: RegionSpec list, out: Stream<Expr>, played: Flow<Expr>) =
    let readClients = System.Collections.Generic.Dictionary<string, Stream<Expr> * Expr * Expr * Expr>()
    let writeClients = System.Collections.Generic.Dictionary<string, Stream<Expr list> * Expr * Expr>()

    do
        if specs.Length > (1 <<< linkRegionIdBits) then
            failwith $"the link names a region in %d{linkRegionIdBits} bits, and the design has %d{specs.Length}"

        for spec in specs do
            if spec.records > (1 <<< linkRegionIndexBits) then
                failwith $"region '{spec.name}': the link names a record in %d{linkRegionIndexBits} bits, and it has %d{spec.records}"

    let idOf (name: string) = specs |> List.findIndex (fun s -> s.name = name)
    let shapeOf (spec: RegionSpec) = recordShape spec.fields

    member _.Holds(name: string) = specs |> List.exists (fun s -> s.name = name)

    member _.Port(name: string) : RegionPort =
        let spec = specs |> List.find (fun s -> s.name = name)
        let shape = shapeOf spec

        let read (requests: Stream<Expr>) : Stream<Expr list> =
            if readClients.ContainsKey name then
                failwith $"region '{name}' is read once: hand every index to one `read`"

            let flat = wire $"{name}_region_record" (recordWidth shape)
            let valid = wireBit $"{name}_region_record_valid"
            let ready = readyWire $"{name}_region_record_ready"
            readClients[name] <- (requests, flat, valid, ready)

            { payload = unpackRecord shape flat
              valid = valid
              ready = ready
              layout = layoutOfList shape.fields }

        let write (incoming: Stream<Expr list>) : Stream<Expr> =
            if writeClients.ContainsKey name then
                failwith $"region '{name}' is written once: hand every record to one `write`"

            let landedValid = wireBit $"{name}_region_landed_valid"
            let landedReady = readyWire $"{name}_region_landed_ready"
            writeClients[name] <- (incoming, landedValid, landedReady)

            { payload = lit 1UL 1
              valid = landedValid
              ready = landedReady
              layout = layout1 ("landed", 1) }

        match spec.usage with
        | Reads -> { refusedRegionPort name Reads with read = read }
        | Writes -> { refusedRegionPort name Writes with write = write }
        | ReadsAndWrites -> { read = read; write = write }

    /// Build the one engine every region shares, now that every client has
    /// registered.
    member _.Finish() =
        for spec in specs do
            if regionReads spec.usage && not (readClients.ContainsKey spec.name) then
                failwith $"region '{spec.name}' is declared {spec.usage} and the design never reads it"

            if regionWrites spec.usage && not (writeClients.ContainsKey spec.name) then
                failwith $"region '{spec.name}' is declared {spec.usage} and the design never writes it"

        let named (what: string) = $"link_regions_{what}"
        let maxWords = specs |> List.map (fun s -> (shapeOf s).words) |> List.max
        let flatWidth = maxWords * 32
        let creditWidth = ceilLog2 (regionWriteCredits + 1)

        // Every client as an operation: (write, region, index, record, owner),
        // in the order the clients are listed. Reads first, then writes.
        let readers = [ for spec in specs do if readClients.ContainsKey spec.name then yield spec, readClients[spec.name] ]
        let writers = [ for spec in specs do if writeClients.ContainsKey spec.name then yield spec, writeClients[spec.name] ]
        let owners = readers.Length + writers.Length
        let ow = ownerWidth owners

        let opLayout =
            layoutOfList
                [ "write", unsignedInt 1
                  "region", unsignedInt linkRegionIdBits
                  "index", unsignedInt linkRegionIndexBits
                  "record", unsignedInt flatWidth
                  "owner", unsignedInt ow ]

        let indexOf (spec: RegionSpec) (index: Expr) =
            let index = if isSigned index then asUInt index else index

            if width index > linkRegionIndexBits then
                failwith $"region '{spec.name}': an index %d{width index} bits wide does not fit the link's %d{linkRegionIndexBits}"

            pad linkRegionIndexBits index

        // A record that has come back waits in its reader's own buffer, so
        // the engine is free the moment the last word lands: a reader that
        // takes its record only once a write it feeds is accepted would
        // otherwise hold the engine that write needs. A reader whose buffer
        // is full asks for nothing more.
        let buffered =
            [ for spec, _ in readers ->
                  regBit $"{spec.name}_region_buffered", reg $"{spec.name}_region_buffer" (recordWidth (shapeOf spec)) ]

        let readOps =
            readers
            |> List.mapi (fun i (spec, (requests, _, _, _)) ->
                let full, _ = buffered[i]
                let gatedReady = readyWire $"{spec.name}_region_ask_ready"
                (gatedReady &&& bnot full) ==> requests.ready

                ({ payload = requests.payload
                   valid = requests.valid &&& bnot full
                   ready = gatedReady
                   layout = requests.layout }
                : Stream<Expr>)
                |> streamMapTo opLayout (fun index ->
                    [ lit 0UL 1
                      lit (uint64 (idOf spec.name)) linkRegionIdBits
                      indexOf spec index
                      lit 0UL flatWidth
                      lit (uint64 i) ow ]))

        // A write client is held to its credits, as on the host's memory: it
        // stalls once it owes as many landed beats as it may.
        let landedHere = [ for _ in writers -> wireBit ((current ()).FreshName (named "landed_here")) ]

        let writeOps =
            writers
            |> List.mapi (fun j (spec, (incoming, landedValid, landedReady)) ->
                let n (what: string) = $"{spec.name}_region_{what}"
                let shape = shapeOf spec
                let owed = reg (n "owed") creditWidth
                let landedOwed = reg (n "landed_owed") creditWidth
                let room = wireBit (n "room")
                lt owed (lit (uint64 regionWriteCredits) creditWidth) ==> room

                let index, values =
                    match incoming.payload with
                    | index :: values -> index, values
                    | [] -> failwith $"region '{spec.name}': a write is an index and a record"

                let flat = wire (n "write_record") (recordWidth shape)
                packRecord spec.name shape values ==> flat

                let toMerge = readyWire (n "write_ready")
                (toMerge &&& room) ==> incoming.ready
                let accepted = wireBit (n "accepted")
                (incoming.valid &&& toMerge &&& room) ==> accepted
                let tokenTaken = wireBit (n "token_taken")
                (landedValid &&& landedReady) ==> tokenTaken
                bnot (eq landedOwed (lit 0UL creditWidth)) ==> landedValid
                owed + pad creditWidth accepted - pad creditWidth tokenTaken ==> owed
                landedOwed + pad creditWidth landedHere[j] - pad creditWidth tokenTaken ==> landedOwed

                ({ payload =
                    [ lit 1UL 1
                      lit (uint64 (idOf spec.name)) linkRegionIdBits
                      indexOf spec index
                      pad flatWidth flat
                      lit (uint64 (readers.Length + j)) ow ]
                   valid = incoming.valid &&& room
                   ready = toMerge
                   layout = opLayout }
                : Stream<Expr list>))

        let merged = readOps @ writeOps |> streamMergeTree

        let opWrite, opRegion, opIndex, opRecord, opOwner =
            match merged.payload with
            | [ w; r; i; d; o ] -> w, r, i, d, o
            | _ -> failwith "an operation is five fields"

        // ---- the engine: header, payload, answer --------------------------
        let idle, header, payload, awaiting = 0UL, 1UL, 2UL, 3UL
        let state = regInit (named "state") 2 idle
        let isIn (s: uint64) = eq state (lit s 2)
        let heldWrite = regBit (named "write")
        let heldRegion = reg (named "region") linkRegionIdBits
        let heldIndex = reg (named "index") linkRegionIndexBits
        let heldRecord = reg (named "record") flatWidth
        let heldOwner = reg (named "owner") ow
        let count = reg (named "count") (max 1 (ceilLog2 (maxWords + 1)))
        let cw = width count

        // Words a record of the held region takes.
        let heldWords = wire (named "words") cw

        specs
        |> List.mapi (fun i s -> i, (shapeOf s).words)
        |> List.rev
        |> List.fold (fun acc (i, words) -> mux (eq heldRegion (lit (uint64 i) linkRegionIdBits)) (lit (uint64 words) cw) acc) (lit 1UL cw)
        ==> heldWords

        let lastWord = wireBit (named "last_word")
        eq count (heldWords - lit 1UL cw) ==> lastWord

        isIn idle ==> merged.ready
        let load = wireBit (named "load")
        (merged.valid &&& isIn idle) ==> load

        let headerWord = wire (named "header") 32
        let op = mux heldWrite (lit linkRegionWrite 4) (lit linkRegionRead 4)
        cat op (cat heldRegion heldIndex) ==> headerWord

        let recordWord = wire (named "record_word") 32
        selectIndexed count [ for k in 0 .. maxWords - 1 -> slice (32 * k + 31) (32 * k) heldRecord ] ==> recordWord

        mux (isIn header) headerWord recordWord ==> out.payload
        (isIn header ||| isIn payload) ==> out.valid
        let sent = wireBit (named "sent")
        (out.valid &&& out.ready) ==> sent

        let answerWords = [ for k in 0 .. maxWords - 1 -> reg (named $"answer_%d{k}") 32 ]
        let answer = wire (named "answer") flatWidth
        (answerWords |> List.rev |> List.reduce cat) ==> answer
        let arriving = wireBit (named "arriving")
        (played.valid &&& isIn awaiting) ==> arriving

        for k in 0 .. maxWords - 1 do
            If (arriving &&& bnot heldWrite &&& eq count (lit (uint64 k) cw)) (fun () -> played.payload ==> answerWords[k])

        ifElse
            [ (load,
               fun () ->
                   opWrite ==> heldWrite
                   opRegion ==> heldRegion
                   opIndex ==> heldIndex
                   opRecord ==> heldRecord
                   opOwner ==> heldOwner
                   lit 0UL cw ==> count
                   lit header 2 ==> state)
              (isIn header &&& sent,
               fun () ->
                   lit 0UL cw ==> count
                   mux heldWrite (lit payload 2) (lit awaiting 2) ==> state)
              (isIn payload &&& sent,
               fun () ->
                   ifElse
                       [ (lastWord,
                          fun () ->
                              lit 0UL cw ==> count
                              lit awaiting 2 ==> state)
                         (otherwise, fun () -> count + lit 1UL cw ==> count) ])
              (arriving,
               fun () ->
                   ifElse
                       [ (heldWrite, fun () -> lit idle 2 ==> state)
                         (lastWord, fun () -> lit idle 2 ==> state)
                         (otherwise, fun () -> count + lit 1UL cw ==> count) ]) ]

        // The last word of a read: the answer, whole, into its reader's buffer.
        let answered = wireBit (named "answered")
        (arriving &&& bnot heldWrite &&& lastWord) ==> answered

        // The word arriving now is the answer's last, so it is taken from the
        // played word itself rather than from its register, which fills on
        // the same edge.
        let complete = wire (named "complete") flatWidth

        (answerWords
         |> List.mapi (fun k w -> mux (eq count (lit (uint64 k) cw)) played.payload w)
         |> List.rev
         |> List.reduce cat)
        ==> complete

        for i, (_, (_, flat, valid, ready)) in List.indexed readers do
            let full, buffer = buffered[i]
            full ==> valid
            buffer ==> flat

            ifElse
                [ (answered &&& eq heldOwner (lit (uint64 i) ow),
                   fun () ->
                       lit 1UL 1 ==> full
                       slice (width buffer - 1) 0 complete ==> buffer)
                  (valid &&& ready, fun () -> lit 0UL 1 ==> full) ]

        for j, here in List.indexed landedHere do
            (arriving &&& heldWrite &&& eq heldOwner (lit (uint64 (readers.Length + j)) ow)) ==> here

/// The stream a top hands its link for the regions' words, declared before
/// the link exists: its payload and valid are driven at `Finish`.
let linkRegionWords () : Stream<Expr> =
    let ready = wireBit "link_regions_out_ready"
    registerStreamReady ready

    { payload = wire "link_regions_out" 32
      valid = wireBit "link_regions_out_valid"
      ready = ready
      layout = layout1 ("word", 32) }

/// Regions over the link: the words out, and the words the host plays back.
let linkRegions (specs: RegionSpec list) (out: Stream<Expr>) (played: Flow<Expr>) = LinkRegions(specs, out, played)

/// The seam's lines for a region the host keeps over the link: its id in a
/// header word, its records, and every field — the host stores it however it
/// likes, a record `RECORD_BYTES` long.
let linkRegionSeamLines (id: int) (spec: RegionSpec) : string list =
    let s = upperSnake spec.name
    let shape = recordShape spec.fields

    [ $"pub const {s}_REGION_ID: u32 = %d{id};"
      $"pub const {s}_RECORDS: usize = %d{spec.records};"
      $"pub const {s}_RECORD_BYTES: usize = %d{shape.words * 4};"
      $"pub const {s}_STRIDE_BYTES: usize = %d{shape.words * 4};" ]
    @ regEntryRsLines $"{s}_" shape.entries
