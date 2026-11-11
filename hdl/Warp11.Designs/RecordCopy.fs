/// The smallest design with regions in it: on `start`, every record of
/// `source` is read, one field changed, and written to the same index of
/// `copied`; `landed` counts the records that have reached memory. It is
/// `n.region`'s toy (`notes/DEVICES.md` §13, rung 1) — the design names two
/// regions and what a record is, and nothing in it says where they live.
///
/// The record crosses a beat — five words, so two beats on a 128-bit port —
/// so a copy is a real burst each way, and a signed field sits alone in its
/// word, so the host reads the number the fabric computed. `weight` goes up
/// by one on the way through: a copy of zeros, or no copy at all, cannot
/// pass for this one.
module Warp11.Designs.RecordCopy

open Warp11
open Warp11.BoardTop

/// A record: a key, a wide payload, a signed weight, a note narrower than
/// its word.
let entry =
    pins4 ("key", unsignedInt 32) ("payload", unsignedInt 64) ("weight", signedInt 16) ("note", unsignedInt 24)

/// How many records each region holds, and so how many a run copies.
let records = 64

/// What the copy asks of the world.
type RecordCopyIo =
    { start: Expr
      source: Region<Expr * Expr * Expr * Expr>
      copied: Region<Expr * Expr * Expr * Expr>
      landed: Expr -> unit }

/// Each index in, `source`'s record at it read, changed, and written to the
/// same index of `copied`; the indices back out as each record lands.
let private copyThrough
    (name: string)
    (layout: Layout<'r>)
    (change: 'r -> 'r)
    (source: Region<'r>)
    (copied: Region<'r>)
    (indices: Stream<Expr>)
    : Stream<Expr> =
    let iw = width indices.payload
    let at = layout1 ($"{name}_at", iw)
    let tag = layout1 ($"{name}_tag", iw)

    indices
    |> streamMapTo (layoutJoin (layout1 ($"{name}_index", iw)) at) (fun i -> i, i)
    |> withContext $"{name}_fetch" 16 (layout1 ($"{name}_index", iw)) layout at source.read
    |> streamMapTo (layoutJoin3 at layout tag) (fun (record, i) -> i, change record, i)
    |> copied.write tag

/// Take every landed beat, count them in `{name}_landed` from `start`, and
/// report the count.
let private counted (name: string) (start: Expr) (report: Expr -> unit) (landed: Stream<Expr>) =
    lit 1UL 1 ==> landed.ready
    let count = reg $"{name}_landed" 16

    ifElse
        [ (start, fun () -> lit 0UL 16 ==> count)
          (landed.valid, fun () -> count + lit 1UL 16 ==> count) ]

    report count

/// Every record of `source` to `copied`, through `change`, from `start`;
/// the landed records counted in `{name}_landed`.
let private copyAll
    (name: string)
    (start: Expr)
    (layout: Layout<'r>)
    (change: 'r -> 'r)
    (source: Region<'r>)
    (copied: Region<'r>)
    (landed: Expr -> unit)
    =
    rangeStream start source.records
    |> copyThrough name layout change source copied
    |> counted name start landed

let private heavier (key, payload, weight, note) = key, payload, weight + lit 1UL 16, note

/// The design.
let recordCopy: Design =
    design
        "RecordCopy"
        (fun n ->
            { start = n.pulseIn "start"
              source = n.region ("source", entry, records, Reads, SharedWithHost)
              copied = n.region ("copied", entry, records, Writes, SharedWithHost)
              landed = n.valueOut ("landed", unsignedInt 16) })
        (fun io -> copyAll "copy" io.start entry heavier io.source io.copied io.landed)

/// The copy on the KV260, its regions in the host's memory.
let recordCopyOnKv260 = BoardTop.boardTop kv260 viaHostMemory recordCopy

/// Its top, for the debugger and the differential.
let recordCopyTop = recordCopyOnKv260.top

/// The same copy on the Icepi, its regions kept by the host over the link.
let recordCopyOverLink = BoardTop.boardTop icepi viaLink recordCopy

/// A one-beat record, for the second copy: a pair of words.
let pair = pins2 ("left", unsignedInt 32) ("right", unsignedInt 32)

/// What two copies at once ask for.
type CopyPairIo =
    { start: Expr
      source: Region<Expr * Expr * Expr * Expr>
      copied: Region<Expr * Expr * Expr * Expr>
      pairs: Region<Expr * Expr>
      swapped: Region<Expr * Expr>
      landed: Expr -> unit
      pairsLanded: Expr -> unit }

/// The copy, and beside it a second over one-beat records that swaps each
/// pair's halves: two readers and two writers, so a mapping that puts them
/// on one port puts two clients on each master, with records of different
/// lengths — which is what routing by owner has to get right. Not a toy of
/// its own; what `RecordCopy`'s check runs to reach a shared master.
let recordCopyPair: Design =
    design
        "RecordCopyPair"
        (fun n ->
            { start = n.pulseIn "start"
              source = n.region ("source", entry, records, Reads, SharedWithHost)
              copied = n.region ("copied", entry, records, Writes, SharedWithHost)
              pairs = n.region ("pairs", pair, records, Reads, SharedWithHost)
              swapped = n.region ("swapped", pair, records, Writes, SharedWithHost)
              landed = n.valueOut ("landed", unsignedInt 16)
              pairsLanded = n.valueOut ("pairsLanded", unsignedInt 16) })
        (fun io ->
            copyAll "copy" io.start entry heavier io.source io.copied io.landed
            copyAll "swap" io.start pair (fun (l, r) -> r, l) io.pairs io.swapped io.pairsLanded)

/// Every region of the pair placed on one port by the mapping: two clients
/// on each master.
let recordCopyPairOnOnePort =
    BoardTop.boardTop
        kv260
        { viaHostMemory with
            bindings =
                viaHostMemory.bindings
                @ [ for need in [ "source"; "copied"; "pairs"; "swapped" ] -> { need = ByName need; carrier = InHostMemoryOn "S_AXI_HPC0_FPD" } ] }
        recordCopyPair

/// The pair spread by default: a region a master.
let recordCopyPairSpread = BoardTop.boardTop kv260 viaHostMemory recordCopyPair

/// The carrier itself under the differential: two read clients and two
/// write clients of different record lengths on one read master and one
/// write master, every handshake and the arena's base driven by the
/// testbench's random stimulus. Through a top the base is a register random
/// AXI-Lite writes almost never arm, so the masters would never move; here a
/// random base is armed nearly every cycle and every path of the arbiters,
/// the owner FIFOs, the assembly and the credits is under stimulus.
let regionCarrierShared =
    let specs =
        [ { name = "a"; fields = entry.fields; records = records; usage = Reads; sharing = SharedWithHost }
          { name = "b"; fields = pair.fields; records = records; usage = Reads; sharing = SharedWithHost }
          { name = "c"; fields = entry.fields; records = records; usage = Writes; sharing = SharedWithHost }
          { name = "d"; fields = pair.fields; records = records; usage = Writes; sharing = SharedWithHost } ]

    let places = placeRegions 128 (8 <<< 20) specs
    let place name = places |> List.find (fun p -> p.spec.name = name)
    let indexWidth = indexWidthFor records

    defModule
        "RegionCarrierShared"
        (fun p ->
            (p.inPort "base" 32,
             (streamInputPorts p "a_index" (layout1 ("index", indexWidth)), streamOutputPorts p "a_record" (layoutOfList entry.fields)),
             (streamInputPorts p "b_index" (layout1 ("index", indexWidth)), streamOutputPorts p "b_record" (layoutOfList pair.fields)),
             (streamInputPorts p "c_write" (layoutOfList (("index", unsignedInt indexWidth) :: entry.fields)),
              streamOutputPorts p "c_landed" (layout1 ("landed", 1))),
             (streamInputPorts p "d_write" (layoutOfList (("index", unsignedInt indexWidth) :: pair.fields)),
              streamOutputPorts p "d_landed" (layout1 ("landed", 1))),
             axiReadBusPorts p "m_axi_hpc0" 32 128,
             axiWriteBusPorts p "m_axi_hpc0" 32 128))
        (fun (baseAddr, (aIn, aOut), (bIn, bOut), (cIn, cOut), (dIn, dOut), readPorts, writePorts) ->
            let regions =
                hostMemoryRegions
                    baseAddr
                    [ axiReadBusOf readPorts, [ place "a"; place "b" ] ]
                    [ axiWriteBusOf writePorts, [ place "c"; place "d" ] ]
                    regionReadOutstanding
                    16

            streamSource aIn |> (regions.Port "a").read |> streamSink aOut
            streamSource bIn |> (regions.Port "b").read |> streamSink bOut
            streamSource cIn |> (regions.Port "c").write |> streamSink cOut
            streamSource dIn |> (regions.Port "d").write |> streamSink dOut
            regions.Finish())

/// What the copy through a scratch region asks for.
type CopyScratchIo =
    { start: Expr
      source: Region<Expr * Expr * Expr * Expr>
      scratch: Region<Expr * Expr * Expr * Expr>
      copied: Region<Expr * Expr * Expr * Expr>
      landed: Expr -> unit }

/// The copy by way of a private region: `source` into `scratch` with the
/// weight raised, and each record, once it has landed there, out of
/// `scratch` into `copied` as it is. Rung 2's toy — the scratch region is
/// the design's own, so the board keeps it on the chip, and moving it to
/// the host's memory by a mapping line changes nothing the host can see.
let recordCopyScratch: Design =
    design
        "RecordCopyScratch"
        (fun n ->
            { start = n.pulseIn "start"
              source = n.region ("source", entry, records, Reads, SharedWithHost)
              scratch = n.region ("scratch", entry, records, ReadsAndWrites, Private)
              copied = n.region ("copied", entry, records, Writes, SharedWithHost)
              landed = n.valueOut ("landed", unsignedInt 16) })
        (fun io ->
            rangeStream io.start records
            |> copyThrough "fill" entry heavier io.source io.scratch
            |> copyThrough "copy" entry id io.scratch io.copied
            |> counted "copy" io.start io.landed)

/// Its scratch on the chip — where a private region goes.
let recordCopyScratchOnChip = BoardTop.boardTop kv260 viaHostMemory recordCopyScratch

/// Its top, for the debugger and the differential.
let recordCopyScratchTop = recordCopyScratchOnChip.top

/// On the Icepi: the source and the copy kept by the host over the link,
/// the scratch in the board's SDRAM — one mapping line from its default on
/// the chip.
let recordCopyScratchOnIcepi =
    BoardTop.boardTop
        icepi
        { viaLink with bindings = viaLink.bindings @ [ { need = ByName "scratch"; carrier = OnBoardMemory } ] }
        recordCopyScratch

/// Its scratch in the host's memory instead, by one mapping line.
let recordCopyScratchInMemory =
    BoardTop.boardTop
        kv260
        { viaHostMemory with bindings = viaHostMemory.bindings @ [ { need = ByName "scratch"; carrier = InHostMemory } ] }
        recordCopyScratch

/// The on-chip carrier under the differential: one private region written
/// and read at random — a two-word record over a block RAM — with every
/// handshake the testbench's.
let regionOnChip =
    let spec =
        { name = "s"; fields = entry.fields; records = records; usage = ReadsAndWrites; sharing = Private }

    let indexWidth = indexWidthFor records

    defModule
        "RegionOnChip"
        (fun p ->
            (streamInputPorts p "s_index" (layout1 ("index", indexWidth)),
             streamOutputPorts p "s_record" (layoutOfList entry.fields),
             streamInputPorts p "s_write" (layoutOfList (("index", unsignedInt indexWidth) :: entry.fields)),
             streamOutputPorts p "s_landed" (layout1 ("landed", 1))))
        (fun (indexIn, recordOut, writeIn, landedOut) ->
            let regions = onChipRegions [ spec ]
            streamSource indexIn |> (regions.Port "s").read |> streamSink recordOut
            streamSource writeIn |> (regions.Port "s").write |> streamSink landedOut
            regions.Finish())

/// The link carrier under the differential: two readers and two writers of
/// different record lengths sharing the one engine, the words it streams
/// taken at random and the words the host plays back arriving at random —
/// every handshake the testbench's, so the header, payload and answer
/// states and the readers' buffers all run under stimulus.
let regionLinkShared =
    let specs =
        [ { name = "a"; fields = entry.fields; records = records; usage = Reads; sharing = SharedWithHost }
          { name = "b"; fields = pair.fields; records = records; usage = Reads; sharing = SharedWithHost }
          { name = "c"; fields = entry.fields; records = records; usage = Writes; sharing = SharedWithHost }
          { name = "d"; fields = pair.fields; records = records; usage = Writes; sharing = SharedWithHost } ]

    let indexWidth = indexWidthFor records

    defModule
        "RegionLinkShared"
        (fun p ->
            (streamOutputPorts p "words" (layout1 ("word", 32)),
             (p.inPort "played_word" 32, p.inPort "played_valid" 1),
             (streamInputPorts p "a_index" (layout1 ("index", indexWidth)), streamOutputPorts p "a_record" (layoutOfList entry.fields)),
             (streamInputPorts p "b_index" (layout1 ("index", indexWidth)), streamOutputPorts p "b_record" (layoutOfList pair.fields)),
             (streamInputPorts p "c_write" (layoutOfList (("index", unsignedInt indexWidth) :: entry.fields)),
              streamOutputPorts p "c_landed" (layout1 ("landed", 1))),
             (streamInputPorts p "d_write" (layoutOfList (("index", unsignedInt indexWidth) :: pair.fields)),
              streamOutputPorts p "d_landed" (layout1 ("landed", 1)))))
        (fun (wordsOut, (playedWord, playedValid), (aIn, aOut), (bIn, bOut), (cIn, cOut), (dIn, dOut)) ->
            let words = linkRegionWords ()
            words |> streamSink wordsOut

            let regions =
                linkRegions specs words { payload = playedWord; valid = playedValid; layout = layout1 ("word", 32) }

            streamSource aIn |> (regions.Port "a").read |> streamSink aOut
            streamSource bIn |> (regions.Port "b").read |> streamSink bOut
            streamSource cIn |> (regions.Port "c").write |> streamSink cOut
            streamSource dIn |> (regions.Port "d").write |> streamSink dOut
            regions.Finish())

/// The Icepi's SDRAM as the record copy's memory: one region, written and
/// read back, every word through the controller and a chip model that
/// refuses what a real one would get wrong. At the Icepi's 25 MHz.
let sdramHz = 25_000_000

/// The region over a chip, by name — the real one for the check, and one
/// that powers up in a microsecond for the differential, whose random
/// stimulus would otherwise spend its whole run in the 200 µs wait.
let regionInSdramOf (name: string) (chip: SdramChip) =
    let spec = { name = "s"; fields = entry.fields; records = records; usage = ReadsAndWrites; sharing = Private }
    let indexWidth = indexWidthFor records

    defModule
        name
        (fun p ->
            (sdramPins p chip,
             streamInputPorts p "s_index" (layout1 ("index", indexWidth)),
             streamOutputPorts p "s_record" (layoutOfList entry.fields),
             streamInputPorts p "s_write" (layoutOfList (("index", unsignedInt indexWidth) :: entry.fields)),
             streamOutputPorts p "s_landed" (layout1 ("landed", 1))))
        (fun (pins, indexIn, recordOut, writeIn, landedOut) ->
            let regions = sdramRegions sdramHz chip pins [ spec ]
            streamSource indexIn |> (regions.Port "s").read |> streamSink recordOut
            streamSource writeIn |> (regions.Port "s").write |> streamSink landedOut
            regions.Finish())

let regionInSdram = regionInSdramOf "RegionInSdram" sdram256Mb16

/// The same over a chip that is up after a microsecond and refreshes every
/// two: the differential's, so its run is records rather than waiting.
let regionInSdramQuick = regionInSdramOf "RegionInSdramQuick" { sdram256Mb16 with initUs = 1.0; tRefiNs = 2000.0 }
