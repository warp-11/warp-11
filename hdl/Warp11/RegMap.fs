[<AutoOpen>]
module Warp11.RegMap

/// The declarative AXI-Lite register map: one definition consumed twice — by
/// `axiLiteSlaveOf` for the slave elaboration and by `regMapRsLines` for the
/// generated Rust layout — so the register file and the driver cannot
/// disagree. The Kotlin side's `AxiLiteRegs` (GoLRegs et al.), re-thought for
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

type RegEntry =
    { name: string
      offset: uint64
      kind: RegKind }

let pulseBit name offset bit =
    { name = name; offset = offset; kind = PulseBit bit }

let rwReg name offset regWidth init =
    { name = name; offset = offset; kind = RwReg(regWidth, init) }

let roField name offset bitOffset fieldWidth =
    { name = name; offset = offset; kind = RoField(bitOffset, fieldWidth) }

let w1cBit name offset bit =
    { name = name; offset = offset; kind = W1cBit bit }

let roConst name offset value =
    { name = name; offset = offset; kind = RoConst value }

let rwWindow name offset words =
    { name = name; offset = offset; kind = RwWindow words }

let roWindow name offset words =
    { name = name; offset = offset; kind = RoWindow words }

type RegMap =
    { apertureAddrWidth: int
      entries: RegEntry list }

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
      window: RegEntry -> Expr -> {| data: Expr; depth: int; through: string -> Expr -> Expr; hostTurn: Expr |}
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
let axiLiteSlaveOf (m: RegMap) : SlaveRegs =
    validate m
    let addrWidth = m.apertureAddrWidth
    let wordWidth = addrWidth - 2
    let wordOf (e: RegEntry) = e.offset >>> 2

    let positioned bitOffset (value: Expr) =
        let shifted =
            if bitOffset = 0 then value else cat value (lit 0UL bitOffset)

        zeroExtend32 shifted

    // No read source here costs a cycle — a window is written by the host and
    // read by the design, and reads of it answer 0.
    let ch = axiLiteChannel addrWidth 1
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
            If setWire (fun () -> lit 1UL 1 ==> r)
            Else (fun () -> If (writeHit e &&& slice b b wdata) (fun () -> lit 0UL 1 ==> r))
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
        System.Collections.Generic.Dictionary<string, {| designAddr: Expr
                                                         data: Expr
                                                         depth: int
                                                         through: string -> Expr -> Expr
                                                         hostTurn: Expr |}>()

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
                       data = port.data
                       depth = port.depth
                       through = port.through
                       hostTurn = hostTurn |}

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

            {| data = p.data
               depth = p.depth
               through = p.through
               hostTurn = p.hostTurn |}
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
