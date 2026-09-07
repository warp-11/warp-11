[<AutoOpen>]
module Warp11.RegMap

/// The declarative AXI-Lite register map: one definition consumed twice — by
/// `axiLiteSlaveOf` for the slave elaboration and by `regMapRsLines` for the
/// generated Rust layout — so the register file and the driver cannot
/// disagree. The original `AxiLiteRegs` (GoLRegs et al.), re-thought for
/// the F# surface: entries are plain values a wrapper holds on to, and the
/// slave handle is keyed by entry, never by a name spelled twice.
type RegKind =
    /// Write-1-pulse: a write of 1 to this bit pulses a wire for the accept
    /// cycle. Contributes nothing to reads, so it can share a word with
    /// read-side fields (the ID-overlay pattern).
    | PulseBit of bit: int
    /// A host-written register, readable back at its own offset. Owns its
    /// whole word.
    | RwReg of regWidth: int * init: uint64
    /// A hardware-driven read-only field at (bitOffset, fieldWidth) — several
    /// pack into one word. The design must `drive` it exactly once.
    | RoField of bitOffset: int * fieldWidth: int
    /// Write-1-clear interrupt-status bit: hardware sets it via `setBit`
    /// (set wins over a same-cycle clear), the host clears by writing 1.
    /// Every W1cBit joins the map's `irq` OR.
    | W1cBit of bit: int
    /// A constant word — the ID pattern. Owns the read side of its word.
    | RoConst of value: uint64
    /// A window of 32-bit words the *design* writes and the host reads — the
    /// mirror of `RwWindow`, and the declarative map's version of the list
    /// slave's `memWindows` (a result buffer, a trace, a small frame). No
    /// arbitration is needed in this direction: the design's write port and
    /// the host's read port are exactly a block RAM's two ports. Host writes
    /// in the range are ignored, as they are on any read-only entry. `words`
    /// must be a power of two and the window aligned to its own size.
    | RoWindow of words: int
    /// A host-writable window of 32-bit words backed by a mem the hardware
    /// reads. Host reads in the window return its contents, through the same
    /// single read port the design uses — a second port would cost the BRAM
    /// shape, so the port is *arbitrated*: the host borrows it for exactly the
    /// cycles a readback is in flight, and the design's side of the port says
    /// so (`hostTurn`). `words` must be a power of two and the window aligned
    /// to its own size.
    | RwWindow of words: int

/// One register in a map: what it is called, where the host finds it, and what
/// kind of thing it is. Built by the constructors below and then held on to —
/// the entry is the key every later access uses, which is what keeps a
/// register's name from being spelled a second time.
type RegEntry =
    { name: string
      offset: uint64
      kind: RegKind }

/// A write-1-pulse bit — `start`, `clear`, anything the host *does* rather
/// than sets.
let pulseBit name offset bit =
    { name = name; offset = offset; kind = PulseBit bit }

/// A host-written register that reads back what was written.
let rwReg name offset regWidth init =
    { name = name; offset = offset; kind = RwReg(regWidth, init) }

/// A hardware-driven field the host reads. Several share a word by taking
/// different bit offsets.
let roField name offset bitOffset fieldWidth =
    { name = name; offset = offset; kind = RoField(bitOffset, fieldWidth) }

/// An interrupt-status bit: hardware sets it, the host clears it by writing a
/// one. Every one of these joins the map's interrupt line.
let w1cBit name offset bit =
    { name = name; offset = offset; kind = W1cBit bit }

/// A constant the host can read — an identifying pattern, a version. Nothing
/// in the design drives it.
let roConst name offset value =
    { name = name; offset = offset; kind = RoConst value }

/// A block of words the host writes and the design reads. `words` must be a
/// power of two, and the window aligned to its own size.
let rwWindow name offset words =
    { name = name; offset = offset; kind = RwWindow words }

/// A block of words the design writes and the host reads, on the same
/// alignment rule.
let roWindow name offset words =
    { name = name; offset = offset; kind = RoWindow words }

/// A whole register map: its aperture, and the entries in it. One definition
/// elaborates the slave *and* emits the Rust layout, so host and fabric cannot
/// hold different addresses for the same register.
type RegMap =
    { apertureAddrWidth: int
      entries: RegEntry list }

// ---------------------------------------------------------------------------
// Building a map: the registers, in order, with the offsets allocated rather
// than written.
//
// The constructors above take an offset because an offset is sometimes an ABI —
// a layout something outside this repository already agreed to. Most of the
// time it is arithmetic nobody wanted to do, and it is written three times per
// register (the binding, the record field, the entries list) with nothing
// checking that the three agree. This is the same map, allocated.

/// FNV-1a over a canonical rendering of the map, folded to 16 bits.
///
/// Rolled by hand rather than reached for, because **.NET's own hashing is not
/// deterministic**: `String.GetHashCode` is randomized per process, so the same
/// map would hash differently on two runs. This value is emitted into committed
/// Rust, so it has to be stable forever, not merely within one process.
///
/// The rendering is **sorted by address**, which makes this a hash of the
/// *layout* rather than of the source. Declaring the same registers in a
/// different order with the same offsets is the same ABI and hashes the same;
/// moving, resizing, adding, removing or renaming one does not.
let private layoutFingerprint (apertureAddrWidth: int) (entries: RegEntry list) : uint64 =
    let kindTag (k: RegKind) =
        match k with
        | PulseBit b -> $"p{b}"
        | RwReg (w, init) -> $"rw{w},{init}"
        | RoField (bo, w) -> $"rf{bo},{w}"
        | W1cBit b -> $"w1c{b}"
        | RoConst v -> $"c{v}"
        | RoWindow w -> $"row{w}"
        | RwWindow w -> $"rww{w}"

    let canonical =
        entries
        |> List.sortBy (fun e -> e.offset, e.name)
        |> List.map (fun e -> $"{e.name}:{e.offset}:{kindTag e.kind}")
        |> String.concat ";"
        |> sprintf "a%d|%s" apertureAddrWidth

    let mutable h = 2166136261u

    for c in canonical do
        h <- (h ^^^ uint32 c) * 16777619u

    // Folded to sixteen bits: the value shares a word with nothing and the
    // population of revisions a design goes through is small, so the extra
    // bits would buy nothing a reader could use.
    uint64 ((h >>> 16) ^^^ (h &&& 0xFFFFu))

/// One 32-bit word being packed — several small fields, or a constant and a
/// pulse, sharing an address. Handed to `RegBuilder.Word`.
///
/// Packing is deliberate rather than incidental: a host reading `running` and
/// `allIdle` out of one word gets a coherent snapshot, where two reads could
/// straddle a change. That is why it is a scope you enter rather than
/// something the allocator does when things happen to fit.
type WordBuilder internal (offset: uint64, entries: ResizeArray<RegEntry>) =
    let mutable bit = 0

    let take width =
        let at = bit
        bit <- bit + width
        at

    /// A hardware-driven field, at the next free bit of this word.
    member _.Field(name: string, width: int) =
        let e = roField name offset (take width) width
        entries.Add e
        e

    /// A write-1-pulse bit, at the next free bit.
    member _.Pulse(name: string) =
        let e = pulseBit name offset (take 1)
        entries.Add e
        e

    /// An interrupt-status bit, at the next free bit.
    member _.W1c(name: string) =
        let e = w1cBit name offset (take 1)
        entries.Add e
        e

    /// A constant sharing this word. It owns the word's **read** side, so it
    /// composes with `Pulse` — which owns only the write side — and that pair
    /// is the ID-overlay: reads answer the identity, writes start the design.
    /// It does not compose with `Field`, and the map's validation says so.
    member _.Const(name: string, value: uint64) =
        let e = roConst name offset value
        entries.Add e
        e

    // The explicit-bit forms — `RegBuilder.At` at bit granularity, and unused
    // here for the same reason. An outside layout that fixes a word's address
    // usually fixes the bits inside it too, so a compat story that could place
    // a word but not a bit would be half a story.

    /// A hardware-driven field at a stated bit.
    member _.FieldAt(name: string, bitOffset: int, width: int) =
        let e = roField name offset bitOffset width
        entries.Add e
        e

    /// A write-1-pulse bit at a stated bit.
    member _.PulseAt(name: string, bitOffset: int) =
        let e = pulseBit name offset bitOffset
        entries.Add e
        e

    /// An interrupt-status bit at a stated bit.
    member _.W1cAt(name: string, bitOffset: int) =
        let e = w1cBit name offset bitOffset
        entries.Add e
        e

/// Allocates offsets as registers are declared. Reached through `buildRegMap`,
/// which reads it once the registers have been built — so there is no way to
/// observe a half-filled map.
type RegBuilder internal () =
    let entries = ResizeArray<RegEntry>()
    let mutable cursor = 0UL
    let mutable hashEntryName : string option = None

    member private _.Take() =
        let offset = cursor
        cursor <- cursor + 4UL
        offset

    /// The entries, with any `LayoutHash` word's value computed over the rest.
    /// Done here rather than in the member because a fingerprint of a map that
    /// is still being written would be a fingerprint of nothing.
    member internal _.EntriesFingerprinted(apertureAddrWidth: int) =
        let all = List.ofSeq entries

        match hashEntryName with
        | None -> all
        | Some name ->
            let others = all |> List.filter (fun e -> e.name <> name)
            let value = layoutFingerprint apertureAddrWidth others

            all
            |> List.map (fun e ->
                if e.name = name then
                    { e with kind = RoConst value }
                else
                    e)

    /// The byte after the last one allocated — what the derived aperture is
    /// computed from.
    member _.HighWater = cursor

    /// Move the cursor, pinning what comes next to a stated address.
    ///
    /// **No map in this repository calls this, deliberately.** It is here for
    /// one situation: a layout something *outside* the F# already agreed to and
    /// cannot be regenerated with — a shipped host binary, a board script that
    /// pokes offsets by hand, a device tree. Everything inside this repository
    /// reads its offsets from the generated Rust layout, which comes off this
    /// same map, so moving a register moves the host with it and nothing needs
    /// pinning.
    ///
    /// It nearly went in for a reason that did not survive checking. GEP's map
    /// has conditional sections, and it looked as though their addresses had to
    /// hold steady so one driver could serve a build with the auto engine and a
    /// build without. They do not: only one shape is ever elaborated to
    /// hardware, and the layout is generated from that same shape, so the two
    /// cannot disagree. The hand-written offsets it used to carry were a
    /// property of that code, not a requirement on it.
    ///
    /// So: reaching for this means naming the outside thing whose addresses you
    /// are matching. If you cannot name one, do not call it — allocation in
    /// declaration order is the point.
    member _.At(offset: uint64) = cursor <- offset

    /// A host-written register, reading back what was written. Owns its word.
    member this.RwReg(name: string, width: int, init: uint64) =
        let e = rwReg name (this.Take()) width init
        entries.Add e
        e

    /// A hardware-driven field owning a whole word. Pack several into one with
    /// `Word` instead.
    member this.RoField(name: string, width: int) =
        let e = roField name (this.Take()) 0 width
        entries.Add e
        e

    /// A constant owning a whole word. To overlay one with a pulse, use `Word`.
    member this.RoConst(name: string, value: uint64) =
        let e = roConst name (this.Take()) value
        entries.Add e
        e

    /// A word answering a fingerprint of this map's layout — **which revision**
    /// of the map the fabric was built from, where an identity constant says
    /// only which design it is.
    ///
    /// That distinction is the whole reason it exists. Offsets here are
    /// allocated, so adding a register moves everything after it; a driver
    /// compiled against the newer layout and talking to the older bitstream
    /// passes an identity check and then reads every register at the wrong
    /// address — successfully, because an unmapped read inside the aperture
    /// answers zero rather than faulting. This is the read that says so.
    ///
    /// **Returns nothing on purpose.** Nothing in a design body has any use for
    /// the value, and not handing it back keeps the entry from being one the
    /// caller holds — which matters, because the value is not known until the
    /// map is closed, and a caller holding the pre-patched entry would be
    /// holding a key that no longer matches anything.
    member this.LayoutHash(name: string) : unit =
        // A placeholder value, replaced by `buildRegMap` once every other entry
        // is known. It cannot hash itself.
        entries.Add(roConst name (this.Take()) 0UL)
        hashEntryName <- Some name

    /// A word shared by several small entries — see `WordBuilder`.
    member this.Word(build: WordBuilder -> 'r) : 'r =
        build (WordBuilder(this.Take(), entries))

    member private _.TakeWindow(words: int) =
        // A window is aligned to its own size, so the cursor rounds up first.
        // Doing it here rather than making the caller do it is most of why
        // windows were fiddly to place by hand.
        let bytes = uint64 words * 4UL
        cursor <- (cursor + bytes - 1UL) / bytes * bytes
        let offset = cursor
        cursor <- cursor + bytes
        offset

    /// A block of words the host writes and the design reads.
    member this.RwWindow(name: string, words: int) =
        let e = rwWindow name (this.TakeWindow words) words
        entries.Add e
        e

    /// A block of words the design writes and the host reads.
    member this.RoWindow(name: string, words: int) =
        let e = roWindow name (this.TakeWindow words) words
        entries.Add e
        e

/// Build a map whose aperture is stated. Pin it when the aperture is itself an
/// ABI — the block design's address segment and the device tree both name it,
/// and both are written outside F#.
///
/// The builder is handed to `build` and read after it returns, so the map
/// cannot be captured half-filled. That matters: were the map a field of the
/// record being built, it would be correct only while it was written last.
let buildRegMapPinned (apertureAddrWidth: int) (build: RegBuilder -> 'a) : 'a * RegMap =
    let b = RegBuilder()
    let value = build b

    value,
    { apertureAddrWidth = apertureAddrWidth
      entries = b.EntriesFingerprinted apertureAddrWidth }

/// Build a map, deriving the smallest aperture that holds it — a 16-byte floor,
/// then the next power of two. Reach for `buildRegMapPinned` where the aperture
/// is fixed by something outside this repository, or where a map should have
/// room to grow without the boundary moving.
let buildRegMap (build: RegBuilder -> 'a) : 'a * RegMap =
    let b = RegBuilder()
    let value = build b
    let aperture = max 4 (ceilLog2 (int b.HighWater))

    value,
    { apertureAddrWidth = aperture
      entries = b.EntriesFingerprinted aperture }

/// A host-writable window's read port, as the design sees it. `MemReadPort`
/// plus the one thing that is different here: the port is shared with the
/// host, so a design consuming the window statefully has to know whose cycle
/// it is.
type WindowPort =
    { /// The word, `depth` cycles after the address was presented.
      data: Expr
      /// How many cycles late `data` is.
      depth: int
      /// Carry a signal across the read so it arrives beside `data`.
      through: string -> Expr -> Expr
      /// High on the cycles the host has borrowed the port. A design that
      /// consumes the window statefully gates on this; one that only derives
      /// combinational values from it may ignore a one-cycle glitch only the
      /// reading host could observe.
      hostTurn: Expr }

/// The elaborated slave, handed back as typed access keyed by entry — the one
/// name-keyed lookup lives inside (the entry IS the key), so a call site
/// never spells a register name a second time.
type SlaveRegs =
    { pulse: RegEntry -> Expr
      value: RegEntry -> Expr
      drive: RegEntry -> Expr -> unit
      setBit: RegEntry -> Expr -> unit
      /// The arbitrated read port onto a `RwWindow`: the design hands over its
      /// address and gets a `memReadPort`-shaped record back — `data`,
      /// `through` — plus `hostTurn`, the cycles the port is serving a host
      /// readback instead. During those cycles `data` is the host's word.
      ///
      /// **The host wins, for exactly the in-flight read, and never more.**
      /// The alternative — the design wins, the host waits — hangs the AXI bus
      /// the moment a design reads its window every cycle, and one already
      /// does. A stolen cycle is bounded and announced; a design that consumes
      /// the window statefully gates on `hostTurn`, and one that only derives
      /// combinational values from it may ignore a one-cycle glitch that only
      /// the reading host could ever observe.
      ///
      /// Call it once: the address wire underneath takes one driver, so a
      /// second call is the one-driver error, and never calling it leaves the
      /// wire undriven, which fails at emission — a window nobody reads is a
      /// bug, not a default.
      window: RegEntry -> Expr -> WindowPort
      /// The backing mem of a `RoWindow`, for the design to `memWrite` — its
      /// write port is exclusively the design's, so the raw mem is the honest
      /// interface and several writes fold as they do anywhere. Reading it
      /// from the design costs a second read port; the host's readback rides
      /// the read channel for free.
      driveWindow: RegEntry -> Mem
      irq: Expr }

let private log2 n =
    let mutable w = 0
    let mutable v = 1

    while v < n do
        w <- w + 1
        v <- v * 2

    w

let private validate (m: RegMap) =
    let wordWidth = m.apertureAddrWidth - 2

    let wordOf (e: RegEntry) =
        if e.offset % 4UL <> 0UL then
            failwith $"regMap '{e.name}': offset 0x%x{e.offset} is not word-aligned"

        let word = e.offset >>> 2

        if word >= (1UL <<< wordWidth) then
            failwith $"regMap '{e.name}': offset 0x%x{e.offset} is outside the %d{m.apertureAddrWidth}-bit aperture"

        word

    match m.entries |> List.countBy (fun e -> e.name) |> List.filter (fun (_, c) -> c > 1) with
    | [] -> ()
    | (dup, _) :: _ -> failwith $"regMap: '{dup}' is declared twice"

    // Per-entry shape checks, then per-word compatibility.
    for e in m.entries do
        match e.kind with
        | PulseBit b
        | W1cBit b ->
            if b < 0 || b > 31 then
                failwith $"regMap '{e.name}': bit %d{b} is outside 0..31"
        | RwReg (w, _) ->
            if w < 1 || w > 32 then
                failwith $"regMap '{e.name}': width %d{w} is outside 1..32"
        | RoField (bo, w) ->
            if bo < 0 || w < 1 || bo + w > 32 then
                failwith $"regMap '{e.name}': field [%d{bo + w - 1}:%d{bo}] does not fit a 32-bit word"
        | RoConst _ -> ()
        | RwWindow words
        | RoWindow words ->
            if words < 2 || words &&& (words - 1) <> 0 then
                failwith $"regMap '{e.name}': window words must be a power of two >= 2, got %d{words}"

            if (wordOf e) % uint64 words <> 0UL then
                failwith $"regMap '{e.name}': window at 0x%x{e.offset} is not aligned to its %d{words}-word size"

    let windowRange (e: RegEntry) =
        match e.kind with
        | RwWindow words
        | RoWindow words -> Some(wordOf e, wordOf e + uint64 words - 1UL)
        | _ -> None

    for e in m.entries do
        match windowRange e with
        | None -> ()
        | Some (lo, hi) ->
            for other in m.entries do
                if other.name <> e.name then
                    let ow = wordOf other

                    let clashes =
                        match windowRange other with
                        | Some (olo, ohi) -> olo <= hi && lo <= ohi
                        | None -> ow >= lo && ow <= hi

                    if clashes then
                        failwith $"regMap: '{other.name}' lands inside window '{e.name}'"

    // Word sharing: an RwReg owns its word; an RoConst owns the read side of
    // its word; RoField/W1cBit read bits must not overlap; PulseBit/W1cBit
    // write bits must not overlap.
    let byWord =
        m.entries
        |> List.filter (fun e ->
            match e.kind with
            | RwWindow _
            | RoWindow _ -> false
            | _ -> true)
        |> List.groupBy wordOf

    for word, entries in byWord do
        let describe = entries |> List.map (fun e -> e.name) |> String.concat ", "

        for e in entries do
            match e.kind with
            | RwReg _ when List.length entries > 1 ->
                failwith $"regMap: rw register '{e.name}' must own word 0x%x{word * 4UL} alone (also there: {describe})"
            | _ -> ()

        let readBits =
            [ for e in entries do
                  match e.kind with
                  | RoField (bo, w) -> yield e.name, [ bo .. bo + w - 1 ]
                  | W1cBit b -> yield e.name, [ b ]
                  | RoConst _ -> yield e.name, [ 0..31 ]
                  | _ -> () ]

        let writeBits =
            [ for e in entries do
                  match e.kind with
                  | PulseBit b
                  | W1cBit b -> yield e.name, [ b ]
                  | _ -> () ]

        for bits in [ readBits; writeBits ] do
            let taken = System.Collections.Generic.Dictionary<int, string>()

            for owner, bs in bits do
                for b in bs do
                    match taken.TryGetValue b with
                    | true, prior -> failwith $"regMap: '{owner}' and '{prior}' overlap at bit %d{b} of word 0x%x{word * 4UL}"
                    | _ -> taken[b] <- owner

/// The slave elaborated from a map — the same one-outstanding scratch-slave
/// scheme as `axiLiteSlaveFull`, with the register file, decode, read mux and
/// interrupt OR all derived from the entries.
let regMapSlave (ports: AxiLiteSlavePorts) (m: RegMap) : SlaveRegs =
    validate m
    let addrWidth = m.apertureAddrWidth

    if ports.addrWidth <> addrWidth then
        failwith
            $"regMapSlave: the boundary was declared %d{ports.addrWidth} bits wide but the map's aperture needs %d{addrWidth}"

    let wordWidth = addrWidth - 2
    let wordOf (e: RegEntry) = e.offset >>> 2

    let positioned bitOffset (value: Expr) =
        let shifted =
            if bitOffset = 0 then value else cat value (lit 0UL bitOffset)

        zeroExtend32 shifted

    // No read source here costs a cycle — a window is written by the host and
    // read by the design, and reads of it answer 0.
    let ch = axiLiteChannelOn ports 1
    let wdata = ch.wdata
    let writeFire = ch.writeFire
    let awWord = ch.awWord

    let writeHit (e: RegEntry) = writeFire &&& eq awWord (lit (wordOf e) wordWidth)

    let pulses = System.Collections.Generic.Dictionary<string, Expr>()
    let rwRegs = System.Collections.Generic.Dictionary<string, Expr>()
    let roWires = System.Collections.Generic.Dictionary<string, Expr>()
    let w1cState = System.Collections.Generic.Dictionary<string, Expr>()
    let w1cSets = System.Collections.Generic.Dictionary<string, Expr>()
    let windows = System.Collections.Generic.Dictionary<string, Mem * int * uint64 * uint64>()
    let outWindows = System.Collections.Generic.Dictionary<string, Mem * int * uint64 * uint64>()

    for e in m.entries do
        match e.kind with
        | PulseBit b ->
            let p = wire e.name 1
            (writeHit e &&& slice b b wdata) ==> p
            pulses[e.name] <- p
        | RwReg (w, init) ->
            let r = regInit e.name w init
            If (writeHit e) (fun () -> slice (w - 1) 0 wdata ==> r)
            rwRegs[e.name] <- r
        | RoField (_, w) -> roWires[e.name] <- wire e.name w
        | W1cBit b ->
            let setWire = wireBit $"{e.name}_set"
            let r = regBit e.name
            // Set beats a same-cycle host clear — a hardware event is never lost.
            ifElse [
                (setWire, fun () -> lit 1UL 1 ==> r)
                (otherwise, fun () -> If (writeHit e &&& slice b b wdata) (fun () -> lit 0UL 1 ==> r)) ]
            w1cState[e.name] <- r
            w1cSets[e.name] <- setWire
        | RoConst _ -> ()
        | RwWindow words ->
            let aw = log2 words
            let backing = distributedMem e.name aw 32
            let inWrite = wireBit $"{e.name}_write_hit"

            let baseWord = wordOf e
            let aboveBase = bnot (lt awWord (lit baseWord wordWidth))

            let below =
                if baseWord + uint64 words >= (1UL <<< wordWidth) then
                    aboveBase
                else
                    aboveBase &&& lt awWord (lit (baseWord + uint64 words) wordWidth)

            (writeFire &&& below) ==> inWrite
            memWrite backing (slice (aw - 1) 0 awWord) wdata inWrite
            windows[e.name] <- (backing, aw, baseWord, uint64 words)
        | RoWindow words ->
            // The design's to write, the host's to read: two exclusive ports,
            // which is exactly what a block RAM has, so no arbitration and no
            // host-write decode — writes landing here are ignored like writes
            // to any read-only entry.
            let aw = log2 words
            let backing = distributedMem e.name aw 32
            outWindows[e.name] <- (backing, aw, wordOf e, uint64 words)

    let rd = ch.beginRead ()
    let readWord = rd.word

    let wordValues =
        m.entries
        |> List.choose (fun e ->
            match e.kind with
            | RwReg _ -> Some(wordOf e, zeroExtend32 rwRegs[e.name])
            | RoField (bo, _) -> Some(wordOf e, positioned bo roWires[e.name])
            | W1cBit b -> Some(wordOf e, positioned b w1cState[e.name])
            | RoConst v -> Some(wordOf e, lit v 32)
            | PulseBit _
            | RwWindow _
            | RoWindow _ -> None)
        |> List.groupBy fst
        |> List.map (fun (word, contributions) ->
            word, contributions |> List.map snd |> List.reduce (|||))

    let regData =
        List.fold (fun acc (word, value) -> mux (eq readWord (lit word wordWidth)) value acc) (lit 0UL 32) wordValues

    // Each window is one read port, shared: the design's address most cycles,
    // the host's while a readback is in flight. The port is created here — it
    // needs the held read word — and the design plugs its address in later
    // through the `window` accessor, which drives the wire declared for it.
    let windowPorts =
        System.Collections.Generic.Dictionary<string, {| designAddr: Expr; port: WindowPort |}>()

    // Deterministic on purpose: the fold walks the map's own entry order, not
    // a dictionary's, so two elaborations of one map emit identical Verilog.
    let readData =
        (regData, m.entries)
        ||> List.fold (fun below e ->
            let hit () =
                let name = e.name
                let _, _, baseWord, size = (if windows.ContainsKey name then windows[name] else outWindows[name])
                let windowHit = wireBit $"{name}_host_hit"
                let aboveBase = bnot (lt readWord (lit baseWord wordWidth))

                (if baseWord + size >= (1UL <<< wordWidth) then
                     aboveBase
                 else
                     aboveBase &&& lt readWord (lit (baseWord + size) wordWidth))
                ==> windowHit

                windowHit

            match e.kind with
            | RwWindow _ ->
                let name = e.name
                let backing, aw, _, _ = windows[name]
                let windowHit = hit ()

                let hostTurn = wireBit $"{name}_host_turn"
                (rd.inFlight &&& windowHit) ==> hostTurn

                let designAddr = wire $"{name}_design_addr" aw
                let port = memReadPort backing (mux hostTurn (slice (aw - 1) 0 readWord) designAddr)
                requireSourceFits ch.answersAfter $"window '{name}'" port.depth

                windowPorts[name] <-
                    {| designAddr = designAddr
                       port =
                        { data = port.data
                          depth = port.depth
                          through = port.through
                          hostTurn = hostTurn } |}

                mux windowHit (zeroExtend32 port.data) below
            | RoWindow _ ->
                let name = e.name
                let backing, aw, _, _ = outWindows[name]
                let windowHit = hit ()

                // The held read word is the only address this port ever sees,
                // so the data holds while RVALID waits, like any other source.
                let port = memReadPort backing (slice (aw - 1) 0 readWord)
                requireSourceFits ch.answersAfter $"window '{name}'" port.depth

                mux windowHit (zeroExtend32 port.data) below
            | _ -> below)

    readData ==> ch.rdata

    let irqLevel =
        match [ for e in m.entries do
                    match e.kind with
                    | W1cBit _ -> yield w1cState[e.name]
                    | _ -> () ] with
        | [] -> lit 0UL 1
        | bits -> List.reduce (|||) bits

    let find (kindName: string) (d: System.Collections.Generic.Dictionary<string, 'v>) (e: RegEntry) : 'v =
        match d.TryGetValue e.name with
        | true, v -> v
        | _ -> failwith $"regMap: '{e.name}' is not {kindName} in this map"

    { pulse = find "a pulse bit" pulses
      value = find "an rw register" rwRegs
      drive = fun e v -> v ==> (find "a read-only field" roWires e)
      setBit = fun e v -> v ==> (find "a w1c bit" w1cSets e)
      driveWindow = (fun e -> let m, _, _, _ = find "a design-written window" outWindows e in m)
      window =
        fun e designAddr ->
            let p = find "a window" windowPorts e
            designAddr ==> p.designAddr
            p.port
      irq = irqLevel }

let private upperSnake (name: string) =
    [ for i, c in Seq.indexed name do
          if System.Char.IsUpper c && i > 0 then yield '_'
          yield System.Char.ToUpperInvariant c ]
    |> System.String.Concat

/// The Rust half of the seam, derived from the same map: offset consts plus
/// the per-kind extras (bit positions, field masks, window sizes). Returns
/// lines — the app supplies its header and appends its own constants.
let regMapRsLines (m: RegMap) : string list =
    [ yield $"pub const APERTURE_BYTES: usize = %d{1 <<< m.apertureAddrWidth};"

      for e in m.entries do
          let s = upperSnake e.name
          yield $"pub const {s}_OFFSET: usize = 0x%03x{e.offset};"

          match e.kind with
          | PulseBit b -> yield $"pub const {s}_BIT: u32 = %d{b};"
          | W1cBit b -> yield $"pub const {s}_BIT: u32 = %d{b};"
          | RoField (bo, w) ->
              yield $"pub const {s}_SHIFT: u32 = %d{bo};"
              yield $"pub const {s}_MASK: u32 = 0x%x{((1UL <<< w) - 1UL) <<< bo};"
          | RoConst v -> yield $"pub const {s}_VALUE: u32 = 0x%08x{v};"
          | RwWindow words
          | RoWindow words -> yield $"pub const {s}_WORDS: usize = %d{words};"
          | RwReg _ -> () ]
