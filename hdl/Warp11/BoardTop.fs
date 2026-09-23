/// The board half of a mapping: a design on a board's boundary. The stereo
/// boundary becomes the I2S pins the board has, every control port becomes a
/// read-write register a host writes, and the register map is the same
/// definition the slave is elaborated from and the Rust seam is generated
/// from — so the host program and the fabric cannot disagree about it.
///
/// The board says how the host reaches the registers — AXI-Lite, a UART, or
/// not at all — and the top takes that shape; the design between the
/// converter and the registers is the one the canvas drew, and the top is
/// what `Build` hands the board's toolchain.
module Warp11.BoardTop


/// The rate a board's I2S master frames at for a design's rate: the same
/// divisor arithmetic `i2sMasterHz` performs, so the number a design is
/// checked against is the number the clock generator will produce.
let boardRate (board: Board) (targetHz: float) : float =
    let sclkHalfDiv = max 1 (int (round (float board.fabricHz / (4.0 * targetHz * float stockBitsPerSlot))))
    sampleRateOf board.fabricHz sclkHalfDiv stockBitsPerSlot

/// The stereo boundary a board's converter needs.
let private stereo = [ "left", signedInt sampleWidth; "right", signedInt sampleWidth ]

/// A design's instance as a board top sees it: the stream through it, rows
/// of the boundary's fields in and out, and its control ports by name.
type Rig =
    { through: Stream<Expr list> -> Stream<Expr list>
      /// Control ports, driven *into* the design from its values.
      ports: (string * Expr) list
      /// Ports the design drives *out* — what it reports about itself, read
      /// back through whatever carries its `ValueOut` needs.
      readbacks: (string * Expr) list }

/// What a board top needs to know of a design, whether it was drawn or
/// written: its boundary, its controls with where they start, and how to
/// instantiate it. A drawn design is `ofGraph`; the export prints one for
/// the typed form, so the build follows from the code either way.
/// One thing a design asks the world for: named, with a direction, and a
/// shape. A boundary is a **list** of these rather than a fixed set of slots,
/// because a real one is — the hearing aid's is microphones, earphones, a
/// recorder, settings and telemetry, and no in/out pair says that
/// (`notes/DEVICES.md` UC4 and §10h).
///
/// Rung 1 of §10h declares only what the built form already had. A value the
/// design *reports* (telemetry) has no case here yet and arrives with the
/// binding that carries it, rather than sitting unused.
type Need =
    /// Rows into the design, as the fields of one row.
    | StreamIn of name: string * fields: (string * NumberFormat) list
    /// Rows out of it.
    | StreamOut of name: string * fields: (string * NumberFormat) list
    /// A value the host sets and the design reads, and where it starts.
    | ValueIn of name: string * format: NumberFormat * starting: uint64
    /// A value the design reports and the host reads — telemetry. The design
    /// drives it; nobody writes it.
    | ValueOut of name: string * format: NumberFormat

/// Rows into the design, under a name.
let streamIn name fields = StreamIn(name, fields)

/// Rows out of it.
let streamOut name fields = StreamOut(name, fields)

/// A value the host sets, starting at zero.
let valueIn name format = ValueIn(name, format, 0UL)

/// A value the host sets, starting where the design says.
let valueFrom name format starting = ValueIn(name, format, starting)

/// A value the design reports.
let valueOut name format = ValueOut(name, format)

type Design =
    { name: string
      sampleRate: float
      streams: int
      /// What the design asks of the world. The `…Of` views below read the
      /// three kinds out of it; nothing else should pattern-match the list.
      needs: Need list
      /// Beats out for one in — one, unless a unit in it answers several.
      answers: int
      /// The instance under a name.
      rig: string -> Rig }

/// What carries the design's input stream, and its output stream. Each asks
/// the path by the need's own name first, so a mapping can rebind one need
/// of a preset without restating the others.
let private carrierOfNeed (path: DataPath) (need: Need) =
    match need with
    | StreamIn(name, _) -> carrierFor path EveryStreamIn name
    | StreamOut(name, _) -> carrierFor path EveryStreamOut name
    | ValueIn(name, _, _) -> carrierFor path EveryValueIn name
    | ValueOut(name, _) -> carrierFor path EveryValueIn name

let internal carrierOfInput (path: DataPath) (d: Design) =
    d.needs
    |> List.tryPick (function
        | StreamIn _ as n -> carrierOfNeed path n
        | _ -> None)

let internal carrierOfOutput (path: DataPath) (d: Design) =
    d.needs
    |> List.tryPick (function
        | StreamOut _ as n -> carrierOfNeed path n
        | _ -> None)

/// The fields of the design's input row.
let inputsOf (d: Design) =
    d.needs
    |> List.collect (function
        | StreamIn(_, fields) -> fields
        | _ -> [])

/// The fields of its output row.
let outputsOf (d: Design) =
    d.needs
    |> List.collect (function
        | StreamOut(_, fields) -> fields
        | _ -> [])

/// The values the host sets, in declaration order — which is register order,
/// so the order needs are declared in is part of the seam.
let controlsOf (d: Design) =
    d.needs
    |> List.choose (function
        | ValueIn(name, format, _) -> Some(name, format)
        | _ -> None)

/// The values the design reports, in declaration order.
let telemetryOf (d: Design) =
    d.needs
    |> List.choose (function
        | ValueOut(name, format) -> Some(name, format)
        | _ -> None)

/// Where each of those starts.
let startingOf (d: Design) =
    d.needs
    |> List.choose (function
        | ValueIn(name, _, starting) -> Some(name, starting)
        | _ -> None)

/// The I2S pinout the board's connector says: the Pmod I2S2's separate
/// converters, or a shared bus. Both at once is refused — one header, one
/// thing plugged into it. A board with neither still elaborates the
/// Pmod shape, and the pin gate refuses it by name when it is built.
let pinoutOf (board: Board) : I2sPinout =
    match connectorFor I2sSharedBus board, connectorFor I2sSeparateCodecs board with
    | Some _, Some _ -> failwith $"{board.name}: both I2S connectors are given — a board has a shared bus or separate codecs on its header, not both"
    | Some _, None -> SharedBus
    | None, _ -> SeparateCodecs

/// What a board asks of a design before it will carry it: one stream, the
/// stereo boundary, and a rate the board's clock divides into — said with
/// the rate it would land on, so a design can be made for it.
let check (board: Board) (d: Design) =
    if d.streams <> 1 then
        failwith $"{d.name}: a board carries one stream, and the design declares %d{d.streams}"

    for side, pins in [ "input", (inputsOf d); "output", (outputsOf d) ] do
        if pins <> stereo then
            failwith $"{d.name}: the {board.name}'s converter needs the {side} box to be [{describePins stereo}], and it is [{describePins pins}]"

    let landed = boardRate board d.sampleRate

    if round landed <> round d.sampleRate then
        failwith $"{d.name}: the design is made for %g{d.sampleRate} Hz, and the {board.name}'s clock frames at %.3f{landed} Hz for it — make the design for that rate"

/// Register words the aperture holds: 256 bytes, as every audio app's.
let apertureAddrWidth = 8

/// The design between the converter and the registers: the stream from the
/// link through the design's instance and back, every control port driven
/// from its register.
let private through
    (d: Design)
    (control: string -> Expr -> Expr)
    (report: (string -> Expr -> unit))
    (record: (Stream<Expr list> -> Expr -> unit) option)
    (i2s: I2sLink)
    =
    // `design` is a Verilog reserved word; the instance is the drawn one.
    let rig = d.rig "drawn"

    for name, port in rig.ports do
        control name port ==> port

    for name, port in rig.readbacks do
        report name port

    let produced =
        i2s.input
        |> Stream.mapTo (layoutOfList (inputsOf d)) (fun (l, r) -> [ l; r ])
        |> rig.through

    match record with
    | None -> produced |> Stream.mapTo sampleLayout (fun fields -> fields[0], fields[1]) |> i2s.send
    | Some sink ->
        // The converter blocks and the recorder drops. That asymmetry is the
        // whole point: an observer must not be able to change what it
        // observes, and at this boundary the two failure modes are not
        // comparable — a recording that skips a sample is a worse recording,
        // an earphone that skips one is a click.
        let fork = streamFork "record" [ blockingBranch 2; droppingBranch 16 ] produced

        fork.branches[0]
        |> Stream.mapTo sampleLayout (fun fields -> fields[0], fields[1])
        |> i2s.send

        sink fork.branches[1] fork.dropped[1]

/// The registers the host-memory path adds ahead of the design's controls:
/// the batch contract every such top speaks, so one driver runs any of them.
type BatchRegs =
    { id: RegEntry
      start: RegEntry
      busy: RegEntry
      doneIrq: RegEntry
      srcAddr: RegEntry
      dstAddr: RegEntry
      /// Rows, not bytes — the host stages a row a frame.
      frameCount: RegEntry
      /// Cycles the last batch took: cleared at `start`, counting while
      /// `busy`, frozen at done — so a host reads the fabric's own measure
      /// of the work rather than timing a poll loop over a bus. The drain
      /// past the last beat is a handful of cycles and belongs to whoever
      /// measures egress, as the frame design's own counter had it.
      cycles: RegEntry }

/// The identity every batch top answers with, so a driver can refuse a
/// bitstream that is not one; the layout hash beside it says which design.
let batchId = 0x7A11BA7CUL

/// A lane is a host word: a field of a row sign-extended into 32 bits, so
/// the host reads a plain `i32` and gets the number the fabric computed. A
/// field wider than a word takes the next lanes too, low word first.
let laneWidth = 32

/// The lanes a field takes.
let lanesOf (f: NumberFormat) = (f.totalWidth + laneWidth - 1) / laneWidth

/// Where each field starts, in lanes, and how many it takes.
let private laneSpans (pins: (string * NumberFormat) list) : (int * int) list =
    (0, pins)
    ||> List.mapFold (fun start (_, f) -> (start, lanesOf f), start + lanesOf f)
    |> fst

/// Beats per burst. At 128 bits a burst is 256 bytes, so a 256-byte-aligned
/// base can never cross AXI's 4 KB boundary — the rule `axiMasterReaderBurst`
/// leaves to its caller, satisfied by making it unreachable.
let beatsPerBurst = 16

/// How a row of the boundary sits in a beat: one lane per field, padded to a
/// power of two so rows tile beats evenly. Said as numbers a host and a
/// check can both derive.
type RowShape =
    { lanesPerRow: int
      rowsPerBeat: int
      rowsPerBurst: int
      bytesPerRow: int }

/// The shape a boundary's rows take in beats of `beatWidth` bits, or why they
/// cannot.
let rowShape (beatWidth: int) (pins: (string * NumberFormat) list) : RowShape =
    let lanesPerBeat = beatWidth / laneWidth
    let lanes = pins |> List.sumBy (fun (_, f) -> lanesOf f)

    if lanes < 1 || lanes > lanesPerBeat then
        failwith $"a row of %d{lanes} fields does not fit a %d{beatWidth}-bit beat — the host-memory path is not built for that"

    let lanesPerRow = 1 <<< ceilLog2 lanes
    let rowsPerBeat = lanesPerBeat / lanesPerRow

    { lanesPerRow = lanesPerRow
      rowsPerBeat = rowsPerBeat
      rowsPerBurst = rowsPerBeat * beatsPerBurst
      bytesPerRow = lanesPerRow * laneWidth / 8 }

/// The registers a **recording** binding adds: where the ring lives, how big
/// it is, how far the fabric has got, and the arm bit.
///
/// `arm` is not a convenience. Unloading a bitstream while a fabric master
/// has writes in flight leaves a permanent PS-side pairing skew that clears
/// only on reboot (`CLAUDE.md`, hardware gotchas), so a write path that the
/// host can switch off before unloading is the defence, and a recorder that
/// runs whenever the design does would have no such switch.
type RecordRegs =
    /// A ring in the host's memory: where it is, how big, how far the fabric
    /// has got, and how much the fork threw away.
    | RecordToMemory of arm: RegEntry * ringAddr: RegEntry * ringBeats: RegEntry * written: RegEntry * dropped: RegEntry
    /// Frames on the host link: there is no ring to describe, only whether it
    /// is running and what it has managed.
    | RecordToFrames of arm: RegEntry * sent: RegEntry * dropped: RegEntry

/// The arm bit, whichever kind of recorder it is.
let recordArmOf =
    function
    | RecordToMemory(arm, _, _, _, _) -> arm
    | RecordToFrames(arm, _, _) -> arm

/// Every carrier bound to a need, in binding order.
let internal carriersOfNeed (path: DataPath) (need: Need) =
    match need with
    | StreamIn(name, _) -> carriersFor path EveryStreamIn name
    | StreamOut(name, _) -> carriersFor path EveryStreamOut name
    | ValueIn(name, _, _) -> carriersFor path EveryValueIn name
    | ValueOut(name, _) -> carriersFor path EveryValueIn name

/// A design's output need bound **twice** — the recording case, whatever the
/// second carrier is. `None` when the output is singly bound.
let internal recordingCarrier (path: DataPath) (d: Design) =
    d.needs
    |> List.tryPick (function
        | StreamOut _ as n ->
            match carriersOfNeed path n with
            | _ :: second :: _ -> Some second
            | _ -> None
        | _ -> None)

/// What a carrier adds to the host's register map.
///
/// Only the host-memory carriers add anything today — the batch contract —
/// and that is the point of asking the question this way round. `start`,
/// `busy`, `srcAddr`, `dstAddr`, `frameCount` and `cycles` appear in no
/// design's needs because they were never the design's: they are what the
/// *binding* requires in order to work. Something above the design always had
/// to invent them, and now the binding contributes them
/// (`notes/DEVICES.md` §10h, rung 3).
let private contributesBatchContract =
    function
    | InHostRows
    | InHostRowsAt -> true
    | OnPins
    | AsBeatCount
    | InRegisterMap
    | InLinkFrames -> false

/// The host's register map, **assembled** from what the design's needs and
/// their bindings require rather than declared: the sink's contract first
/// where a binding wants one, then the design's own values in declaration
/// order. Declaration order is therefore register order, and so part of the
/// seam — which is why the map is pinned.
let private registersFor (path: DataPath) (d: Design) : BatchRegs option * RecordRegs option * (string * RegEntry) list * RegMap =
    let starting = startingOf d |> Map.ofList

    let wantsContract =
        d.needs
        |> List.exists (fun n ->
            carrierOfNeed path n
            |> Option.map contributesBatchContract
            |> Option.defaultValue false)

    let (batch, recorder, controls), map =
        buildRegMapPinned apertureAddrWidth (fun r ->
            let batch =
                if not wantsContract then
                    None
                else
                    let id, start = r.Word(fun w -> w.Const("id", batchId), w.Pulse "start")
                    r.LayoutHash "layoutHash"

                    Some
                        { id = id
                          start = start
                          busy = r.Word(fun w -> w.Field("busy", 1))
                          doneIrq = r.Word(fun w -> w.W1c "doneIrq")
                          srcAddr = r.RwReg("srcAddr", 32, 0UL)
                          dstAddr = r.RwReg("dstAddr", 32, 0UL)
                          frameCount = r.RwReg("frameCount", 32, 0UL)
                          cycles = r.RoField("cycles", 32) }

            // A second carrier on one need contributes too, after the
            // sink's contract and before the design's own values — which is
            // the generalisation rung 3 left for rung 4.
            // What the recording binding contributes depends on its carrier:
            // a ring in memory has to be described, frames on a link do not.
            let recorder =
                match recordingCarrier path d with
                | Some InHostRows ->
                    // The host writes the arm bit, so it is a register and
                    // not a field the fabric drives.
                    Some(
                        RecordToMemory(
                            r.RwReg("recordArm", 1, 0UL),
                            r.RwReg("recordAddr", 32, 0UL),
                            r.RwReg("recordBeats", 32, 0UL),
                            r.RoField("recordWritten", 32),
                            r.RoField("recordDropped", 32)
                        )
                    )
                | Some InLinkFrames ->
                    Some(RecordToFrames(r.RwReg("recordArm", 1, 0UL), r.RoField("recordSent", 32), r.RoField("recordDropped", 32)))
                | _ -> None

            let controls =
                [ for name, f in controlsOf d ->
                      name, r.RwReg(name, f.totalWidth, (starting |> Map.tryFind name |> Option.defaultValue 0UL)) ]

            // Telemetry is read-only by construction: the design drives it,
            // so there is nothing for the host to write and a writable
            // register would only invite someone to try.
            let telemetry = [ for name, f in telemetryOf d -> name, r.RoField(name, f.totalWidth) ]

            batch, recorder, controls @ telemetry)

    batch, recorder, controls, map

/// One beat becomes its rows, a row a cycle. The beat is not copied: `ready`
/// is asserted as the last row leaves, and the read master holds its payload
/// until then, so the handshake does the storing.
let private beatsToRows (shape: RowShape) (pins: (string * NumberFormat) list) (beats: Stream<Expr * Expr>) : Stream<Expr list> =
    let data, _last = beats.payload
    let beatWidth = width data
    let beat = wire "unpack_beat" beatWidth
    data ==> beat

    let outReady = wireBit "unpack_out_ready"
    registerStreamReady outReady
    let fire = beats.valid &&& outReady

    let laneAt (lane: int) (w: int) = slice (lane * laneWidth + w - 1) (lane * laneWidth) beat
    let spans = laneSpans pins

    let fields =
        if shape.rowsPerBeat = 1 then
            fire ==> beats.ready
            [ for (start, _), (_, f) in List.zip spans pins -> laneAt start f.totalWidth ]
        else
            let phaseWidth = ceilLog2 shape.rowsPerBeat
            let phase = reg "unpack_phase" phaseWidth
            let last = eq phase (lit (uint64 (shape.rowsPerBeat - 1)) phaseWidth)
            (fire &&& last) ==> beats.ready

            If fire (fun () -> mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase)

            [ for (start, _), (_, f) in List.zip spans pins ->
                  selectIndexed phase [ for row in 0 .. shape.rowsPerBeat - 1 -> laneAt (row * shape.lanesPerRow + start) f.totalWidth ] ]

    { payload = fields
      valid = beats.valid
      ready = outReady
      layout = layoutOfList pins }

/// Rows become a beat. All but the last row of a beat are held in registers
/// — unavoidable, because the producer has already been told its row was
/// taken.
let private rowsToBeats (shape: RowShape) (pins: (string * NumberFormat) list) (beatWidth: int) (rows: Stream<Expr list>) : Stream<Expr> =
    // A field fills its lanes, sign-extended to the last.
    let lane (f: NumberFormat) (e: Expr) =
        let w = lanesOf f * laneWidth
        if f.signed then asUInt (pad w (asSInt e)) else pad w e

    let lanes = List.map2 (fun (_, f) e -> lane f e) pins rows.payload

    let rowBits =
        let padding = shape.lanesPerRow - (pins |> List.sumBy (fun (_, f) -> lanesOf f))
        let padded = lanes @ List.replicate padding (lit 0UL laneWidth)
        // Lane 0 at the low end.
        padded |> List.rev |> List.reduce cat

    let rowWidth = shape.lanesPerRow * laneWidth
    let outValid = wireBit "pack_out_valid"
    let outReady = wireBit "pack_out_ready"
    registerStreamReady outReady

    if shape.rowsPerBeat = 1 then
        rows.valid ==> outValid
        outReady ==> rows.ready

        { payload = rowBits
          valid = outValid
          ready = outReady
          layout = layout1 ("data", beatWidth) }
    else
        let phaseWidth = ceilLog2 shape.rowsPerBeat
        let phase = reg "pack_phase" phaseWidth
        let last = eq phase (lit (uint64 (shape.rowsPerBeat - 1)) phaseWidth)
        let held = [ for k in 0 .. shape.rowsPerBeat - 2 -> reg $"pack_held%d{k}" rowWidth ]

        // Every row but the last is always accepted; the last only when the
        // beat can leave.
        (bnot last ||| outReady) ==> rows.ready
        (rows.valid &&& last) ==> outValid

        let fire = rows.valid &&& (bnot last ||| outReady)

        If fire (fun () ->
            mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase

            held
            |> List.iteri (fun k h -> If (eq phase (lit (uint64 k) phaseWidth)) (fun () -> rowBits ==> h)))

        { payload = List.fold (fun acc h -> cat acc h) rowBits (List.rev held)
          valid = outValid
          ready = outReady
          layout = layout1 ("data", beatWidth) }

/// A design on a board, ready to build: the top module, the register map
/// and its entries by port name.
type BoardTop =
    { board: Board
      /// The top module's name, and the file's.
      name: string
      top: ModuleDef
      /// Empty when the board has no host: the controls are then baked at
      /// their starting values.
      registers: (string * RegEntry) list
      /// The batch contract's registers, on the host-memory path.
      batch: BatchRegs option
      map: RegMap
      /// The Verilog target the board's toolchain wants.
      target: Target }

/// The emitter's target for a part.
let targetOf (part: Part) =
    match part.family with
    | UltraScalePlus -> Xilinx
    | Ice40UltraPlus -> Ice40

/// The design on the board's I2S pins, the registers reached the way the
/// board's host reaches them.
///
/// The top's name says the host path — `GainPatchAxi`, `GainPatchUart`,
/// `GainPatchTop` — because that is what changes its port list; the board
/// is the file's business, not the module's.
/// The recording branch's write side: rows into a ring in the host's memory,
/// only while the host has armed it.
///
/// It is a ring rather than a one-shot because a recorder has no idea how
/// long it will be wanted for; the host reads behind the write pointer and
/// `recordWritten` tells it where that is. `recordDropped` is the fork's own
/// count, so a host that cannot keep up learns that it could not rather than
/// silently getting a shorter recording.
let private recordInto
    (regs: SlaveRegs)
    (armReg: RegEntry)
    (ringAddrReg: RegEntry)
    (ringBeatsReg: RegEntry)
    (writtenReg: RegEntry)
    (droppedReg: RegEntry)
    (memory: HostMemoryFacts)
    (bus: AxiWriteBus)
    (pins: (string * NumberFormat) list)
    (rows: Stream<Expr list>)
    (dropped: Expr)
    =
    let shape = rowShape memory.width pins
    let beatBytes = memory.width / 8
    let beatAddrShift = ceilLog2 beatBytes
    let armed = regs.value armReg
    let ringBeats = regs.value ringBeatsReg

    // The branch's fields arrive computed — they came back out of the fork's
    // buffer — and packing sign-extends them, which wants declared bits.
    let named =
        rows.payload
        |> List.mapi (fun i e ->
            let w = wire $"record_field%d{i}" (width e)
            e ==> w
            w)

    // Rows are only taken while armed; a disarmed recorder presents no beats
    // and its master goes idle, which is what makes it safe to unload the
    // bitstream after the host clears the bit.
    let beats =
        rowsToBeats shape pins memory.width
            { rows with
                payload = named
                valid = rows.valid &&& armed }

    let index = reg "record_index" 32
    let written = reg "record_written" 32
    let wrAddr = wire "record_addr" 32
    (regs.value ringAddrReg + asUInt (shl beatAddrShift (slice (31 - beatAddrShift) 0 index))) ==> wrAddr

    let wrReady = wireBit "record_wr_ready"

    let writeBeats: Stream<Expr * Expr * Expr> =
        { payload = (wrAddr, beats.payload, lit ((1UL <<< beatBytes) - 1UL) beatBytes)
          valid = beats.valid
          ready = wrReady
          layout = axiWriteBeatLayout 32 memory.width }

    wrReady ==> beats.ready
    axiMasterWriterWithIdleOn bus memory.writeOutstanding writeBeats |> ignore

    let fire = beats.valid &&& wrReady
    let atEnd = eq (index + lit 1UL 32) ringBeats

    If fire (fun () ->
        mux atEnd (lit 0UL 32) (index + lit 1UL 32) ==> index
        written + lit 1UL 32 ==> written)

    // Disarming rewinds the ring, so a recording always starts at its head.
    If (bnot armed) (fun () ->
        lit 0UL 32 ==> index
        lit 0UL 32 ==> written)

    regs.drive writtenReg written
    regs.drive droppedReg dropped

/// The recording branch's other shape: rows out as **words on the host
/// link**, one field a word, for a board whose only wire is the one its
/// register map is already on.
///
/// A field takes a whole 32-bit word rather than being packed, because the
/// link's frame carries 32 bits and a host that has to unpack sub-word fields
/// needs to know the layout twice — once in the seam and once in its
/// unpacker. The cost is bandwidth on a link that is already the slow part,
/// which is why the fork in front of this drops.
let private recordFrames
    (regs: SlaveRegs)
    (armReg: RegEntry)
    (sentReg: RegEntry)
    (droppedReg: RegEntry)
    (pins: (string * NumberFormat) list)
    (rows: Stream<Expr list>)
    (dropped: Expr)
    : Stream<Expr> =
    for name, f in pins do
        if f.totalWidth > laneWidth then
            failwith $"recording over the link: '{name}' is %d{f.totalWidth} bits and a frame carries %d{laneWidth}"

    let armed = regs.value armReg
    let n = pins.Length

    // Each field, sign-extended into its word the way the seam will read it.
    // The field is named first because it arrives computed — out of the
    // fork's buffer — and sign extension replicates a named bit.
    let words =
        List.zip pins rows.payload
        |> List.map (fun ((name: string, f: NumberFormat), e) ->
            let w = wire $"record_field_{name}" f.totalWidth
            e ==> w
            if f.signed then asUInt (pad laneWidth (asSInt w)) else pad laneWidth w)

    let outReady = wireBit "record_frame_ready"
    registerStreamReady outReady
    let sent = reg "record_sent" 32

    let payload, takeRow =
        if n = 1 then
            List.head words, outReady
        else
            let phaseWidth = ceilLog2 n
            let phase = reg "record_phase" phaseWidth
            let last = eq phase (lit (uint64 (n - 1)) phaseWidth)
            let fire = rows.valid &&& armed &&& outReady

            If fire (fun () -> mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase)

            // The row is held until its last field has gone: the handshake
            // does the storing, as it does everywhere else here.
            selectIndexed phase words, (last &&& outReady)

    (takeRow &&& armed) ==> rows.ready
    If (rows.valid &&& armed &&& outReady) (fun () -> sent + lit 1UL 32 ==> sent)
    regs.drive sentReg sent
    regs.drive droppedReg dropped

    { payload = payload
      valid = rows.valid &&& armed
      ready = outReady
      layout = layout1 ("word", 32) }

let private pinsTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    check board d
    // No binding here wants a contract, so the map is exactly the design's
    // own values — which is what the pins path always had, now by assembly
    // rather than by a second builder.
    let _, recorder, entries, map = registersFor path d
    let rate = int (round d.sampleRate)

    let fromRegisters (regs: SlaveRegs) =
        let byName = Map.ofList entries
        fun (name: string) (_: Expr) -> regs.value byName[name]

    // The other direction: what the design reports goes into its read-only
    // field. With no host there is nowhere for it to go, and the port is left
    // for whatever else the board top does with it.
    let toRegisters (regs: SlaveRegs) =
        let byName = Map.ofList entries
        fun (name: string) (port: Expr) -> regs.drive byName[name] port

    let baked =
        let starting = (startingOf d) |> Map.ofList
        fun (name: string) (port: Expr) -> lit (starting |> Map.tryFind name |> Option.defaultValue 0UL) (width port)

    // What the recording binding added to the map, whichever kind it is, so
    // both host branches expose it the same way.
    let recorderEntries: (string * RegEntry) list =
        match recorder with
        | None -> []
        | Some(RecordToMemory(arm, ringAddr, ringBeats, written, dropped)) ->
            [ "recordArm", arm
              "recordAddr", ringAddr
              "recordBeats", ringBeats
              "recordWritten", written
              "recordDropped", dropped ]
        | Some(RecordToFrames(arm, sent, dropped)) -> [ "recordArm", arm; "recordSent", sent; "recordDropped", dropped ]

    let name, top, registers =
        match board.host with
        | AxiLiteAt _ ->
            let name = $"{d.name}Axi"

            // The recorder needs somewhere to put the rows, so it needs the
            // board to have host memory at all.
            let memory =
                match recorder, board.hostMemory with
                | Some _, None ->
                    failwith
                        $"{d.name}: its output is bound to the host's memory as well as the pins, and the {board.name} has no host memory to record into"
                | _, m -> m

            let top =
                defModuleClocked
                    axiClock
                    name
                    (fun p ->
                        axiLiteSlavePorts p map.apertureAddrWidth,
                        i2sPins p (pinoutOf board),
                        (match recorder, memory with
                         | Some _, Some m -> Some(axiWriteBusPorts p "m_axi" 32 m.width)
                         | _ -> None))
                    (fun (slavePorts, pins, writeBusPorts) ->
                        let regs = regMapSlave slavePorts map
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot

                        let sink =
                            match recorder, writeBusPorts, memory with
                            | Some (RecordToMemory(arm, ringAddr, ringBeats, written, dropped)), Some busPorts, Some m ->
                                Some(recordInto regs arm ringAddr ringBeats written dropped m (axiWriteBusOf busPorts) (outputsOf d))
                            | _ -> None

                        through d (fromRegisters regs) (toRegisters regs) sink i2s)

            name, top.def, recorderEntries @ entries
        | UartAt baud ->
            let name = $"{d.name}Uart"

            let top =
                defModule
                    name
                    (fun p -> uartPins p "host", i2sPins p (pinoutOf board))
                    (fun (uart, pins) ->
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot

                        match recorder with
                        | Some(RecordToFrames(arm, sent, dropped)) ->
                            // The recorder's words and the register map share
                            // the one wire, so the slave is built *around*
                            // the stream: the words are produced by the fork
                            // inside `through`, which is below this, so the
                            // stream is threaded back through a wire pair.
                            let words = wire "record_frame_word" 32
                            let wordsValid = wireBit "record_frame_valid"
                            let wordsReady = wireBit "record_frame_ready_back"

                            let stream: Stream<Expr> =
                                { payload = words
                                  valid = wordsValid
                                  ready = wordsReady
                                  layout = layout1 ("word", 32) }

                            let regs = serialRegMapSlaveWith "host" board.fabricHz baud uart map stream

                            let sink (rows: Stream<Expr list>) (dropCount: Expr) =
                                let produced = recordFrames regs arm sent dropped (outputsOf d) rows dropCount
                                produced.payload ==> words
                                produced.valid ==> wordsValid
                                wordsReady ==> produced.ready

                            through d (fromRegisters regs) (toRegisters regs) (Some sink) i2s
                        | _ ->
                            let regs = serialRegMapSlave "host" board.fabricHz baud uart map
                            through d (fromRegisters regs) (toRegisters regs) None i2s)

            name, top.def, recorderEntries @ entries
        | NoHost ->
            let name = $"{d.name}Top"

            let top =
                defModule
                    name
                    (fun p -> i2sPins p (pinoutOf board))
                    (fun pins ->
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through d baked (fun _ _ -> ()) None i2s)

            name, top.def, []

    { board = board
      name = name
      top = top
      registers = registers
      batch = None
      map = map
      target = targetOf board.part }

/// The design on the host's memory: rows at `srcAddr`, through the design,
/// rows at `dstAddr`. The batch contract is `Warp11.Effects.Batch`'s, with
/// the row shape derived from the boundary rather than fixed at stereo:
/// `frameCount` rows, a multiple of the rows a burst holds, both addresses
/// 256-byte aligned — the host driver pads and checks, because a design
/// that silently processed a truncated block would be worse than one that
/// refused.
///
/// `counted` is the same top with nothing read: the input rows are the beat
/// index, `0 … frameCount - 1`, on the input box's one field, so the host
/// writes the count and the view and reads the frame back. The registers
/// are the same, `srcAddr` unused, so one driver speaks to both.
///
/// `frameCount` is rows *in*; a design that answers several beats for one
/// writes that many times as many rows, and the host reads them back.
let private hostMemoryTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    // What carries each half, asked per need rather than decoded from a name:
    // nothing is read when the input is a beat count, and the design says
    // where a beat goes only when the output is carried at an address.
    let counted = carrierOfInput path d = Some AsBeatCount
    let scattered = carrierOfOutput path d = Some InHostRowsAt
    let memory =
        match board.hostMemory with
        | Some m -> m
        | None -> failwith $"{d.name}: the {board.name} has no host memory — the design's rows have nowhere to go but the pins"

    match board.host with
    | AxiLiteAt _ -> ()
    | _ -> failwith $"{d.name}: the host-memory path needs an AXI-Lite host to be told where the rows are, and the {board.name} has none"

    if d.streams <> 1 then
        failwith $"{d.name}: the host-memory path carries one stream, and the design declares %d{d.streams}"

    if counted && (inputsOf d).Length <> 1 then
        failwith $"{d.name}: the counted path puts the beat index on the input box's one field, and the design declares [{describePins (inputsOf d)}]"

    // Rows out are rows in times the answers. `answers` is a constant at
    // elaboration, so this is shifts and adds and need not be a power of two
    // — which it stops being as soon as a chunk is sized to divide its row
    // rather than to be a round number.
    let timesAnswers (e: Expr) =
        let shifted i =
            if i = 0 then e else cat (slice (31 - i) 0 e) (lit 0UL i)

        [ for i in 0..31 do
              if (d.answers >>> i) &&& 1 = 1 then
                  yield shifted i ]
        |> List.reduce (+)

    // On the scatter path the design's FIRST output field is where the beat
    // goes; the rest are the beat itself.
    if scattered && (outputsOf d).Length < 2 then
        failwith
            $"{d.name}: the scatter path puts the destination beat index on the output box's first field and the payload on the rest, and the design declares [{describePins (outputsOf d)}]"

    let destPin = if scattered then Some (outputsOf d).Head else None
    let payloadPins = if scattered then List.tail (outputsOf d) else (outputsOf d)

    let batchOption, _, entries, map = registersFor path d

    let batch =
        match batchOption with
        | Some b -> b
        | None -> failwith $"{d.name}: the host-memory path needs the batch contract, and no binding contributed one"
    let inShape = rowShape memory.width (inputsOf d)
    let outShape = rowShape memory.width payloadPins

    // A scattered beat carries one address, so it has to BE one beat: there is
    // nowhere to put a second address if two rows shared a word, and no reason
    // to buffer rows that are not going to the same place.
    if scattered && outShape.rowsPerBeat <> 1 then
        failwith
            $"{d.name}: the scatter path writes one payload per beat, and [{describePins payloadPins}] packs %d{outShape.rowsPerBeat} of them into a %d{memory.width}-bit word — widen the payload to fill one"

    match destPin with
    | Some (n, f) when f.totalWidth > 32 ->
        failwith $"{d.name}: the scatter path's destination '{n}' is a beat index and the bus addresses 32 bits, not %d{f.totalWidth}"
    | _ -> ()
    let beatBytes = memory.width / 8
    let burstBytes = beatsPerBurst * beatBytes
    let name = $"{d.name}Batch"

    let top =
        defModuleClocked
            axiClock
            name
            (fun p ->
                axiLiteSlavePorts p map.apertureAddrWidth,
                (if counted then None else Some(axiReadBusPorts p "m_axi" 32 memory.width)),
                axiWriteBusPorts p "m_axi" 32 memory.width)
            (fun (slavePorts, readBusPorts, writeBusPorts) ->
                let regs = regMapSlave slavePorts map
                let byName = Map.ofList entries
                let rig = d.rig "drawn"

                for n, port in rig.ports do
                    regs.value byName[n] ==> port

                let frameCount = regs.value batch.frameCount

                // Rows → bursts on the way in, rows → beats on the way out;
                // both shapes are powers of two, so each is a shift.
                let bursts = wire "bursts" 32
                let burstShift = ceilLog2 inShape.rowsPerBurst
                pad 32 (slice 31 burstShift frameCount) ==> bursts

                // `frameCount` is rows in; the rows out are `answers` as many.
                let rowsOut = wire "rows_out" 32

                timesAnswers frameCount ==> rowsOut

                let beatsTotal = wire "beats_total" 32
                let outShift = ceilLog2 outShape.rowsPerBeat
                pad 32 (slice 31 outShift rowsOut) ==> beatsTotal

                let running = regBit "running"
                let arIssued = reg "ar_issued" 32
                let beatsWritten = reg "beats_written" 32
                let cycleCount = reg "cycle_count" 32

                let burstAddrShift = ceilLog2 burstBytes

                let rows: Stream<Expr list> =
                    match readBusPorts with
                    | Some readBusPorts ->
                        // --- the read side: one descriptor per burst -------
                        let reqReady = wireBit "req_ready"
                        let arMore = wireBit "ar_more"
                        (running &&& lt arIssued bursts) ==> arMore

                        let reqAddr = wire "req_addr" 32
                        (regs.value batch.srcAddr + asUInt (shl burstAddrShift (slice (31 - burstAddrShift) 0 arIssued))) ==> reqAddr

                        let requests: Stream<Expr * Expr> =
                            { payload = (reqAddr, lit (uint64 (beatsPerBurst - 1)) 8)
                              valid = arMore
                              ready = reqReady
                              layout = layout2 ("addr", 32) ("len", 8) }

                        If (arMore &&& reqReady) (fun () -> arIssued + lit 1UL 32 ==> arIssued)

                        axiMasterReaderBurstOn (axiReadBusOf readBusPorts) 4 beatsPerBurst requests
                        |> beatsToRows inShape (inputsOf d)
                    | None ->
                        // --- the count: `frameCount` beats, the index each -
                        // `arIssued` counts them, as it counts bursts read.
                        let more = wireBit "count_more"
                        (running &&& lt arIssued frameCount) ==> more
                        let countReady = wireBit "count_ready"
                        registerStreamReady countReady
                        If (more &&& countReady) (fun () -> arIssued + lit 1UL 32 ==> arIssued)

                        let _, f = (inputsOf d).Head
                        let index = if f.totalWidth >= 32 then pad f.totalWidth arIssued else slice (f.totalWidth - 1) 0 arIssued

                        { payload = [ index ]
                          valid = more
                          ready = countReady
                          layout = layoutOfList (inputsOf d) }

                // --- the design ---------------------------------------------
                let produced = rows |> rig.through

                // On the scatter path the first field is the destination and
                // the rest are the payload; the index is read in the same
                // cycle as the beat it belongs to, which holds because a
                // scattered payload fills a whole word (checked above), so
                // the pack below is combinational and buffers nothing.
                let destIndex, payloadRows =
                    if scattered then
                        Some produced.payload.Head, { produced with payload = List.tail produced.payload; layout = layoutOfList payloadPins }
                    else
                        None, produced

                let outBeats = payloadRows |> rowsToBeats outShape payloadPins memory.width

                // --- the write side -----------------------------------------
                let wrAddr = wire "wr_addr" 32
                let beatAddrShift = ceilLog2 beatBytes

                // Where the beat goes: the count of beats written so far, or —
                // on the scatter path — where the design said. That single
                // choice is the whole of the difference, and it is what lets
                // the farm above be unordered: position is stated, not implied
                // by arrival.
                let destBeat =
                    match destIndex with
                    | Some index -> pad 32 index
                    | None -> beatsWritten

                (regs.value batch.dstAddr + asUInt (shl beatAddrShift (slice (31 - beatAddrShift) 0 destBeat))) ==> wrAddr

                let wrReady = wireBit "wr_ready"

                let writeBeats: Stream<Expr * Expr * Expr> =
                    { payload = (wrAddr, outBeats.payload, lit ((1UL <<< beatBytes) - 1UL) beatBytes)
                      valid = outBeats.valid
                      ready = wrReady
                      layout = axiWriteBeatLayout 32 memory.width }

                wrReady ==> outBeats.ready

                // How many writes stay in flight is the board's port's
                // business, not this top's: the cost of a write here is a full
                // AW+W+B round trip, and overlapping them is what hides it.
                let writerIdle =
                    axiMasterWriterWithIdleOn (axiWriteBusOf writeBusPorts) memory.writeOutstanding writeBeats

                If (outBeats.valid &&& wrReady) (fun () -> beatsWritten + lit 1UL 32 ==> beatsWritten)

                // --- control ------------------------------------------------
                let finished = wireBit "finished"
                (running &&& eq beatsWritten beatsTotal &&& writerIdle) ==> finished

                ifElse
                    [ (regs.pulse batch.start,
                       fun () ->
                           lit 1UL 1 ==> running
                           lit 0UL 32 ==> arIssued
                           lit 0UL 32 ==> beatsWritten
                           lit 0UL 32 ==> cycleCount)
                      (otherwise,
                       fun () ->
                           If finished (fun () -> lit 0UL 1 ==> running)
                           If running (fun () -> cycleCount + lit 1UL 32 ==> cycleCount)) ]

                regs.drive batch.busy running
                regs.drive batch.cycles cycleCount
                regs.setBit batch.doneIrq finished

                // The caller's half of the contract, said out loud: checked
                // every cycle in simulation, compiled out of the silicon.
                let aligned (e: RegEntry) =
                    bnot running ||| eq (slice (burstAddrShift - 1) 0 (regs.value e)) (lit 0UL burstAddrShift)

                assertThat (aligned batch.srcAddr) $"srcAddr must be %d{burstBytes}-byte aligned"
                assertThat (aligned batch.dstAddr) $"dstAddr must be %d{burstBytes}-byte aligned"

                let rowsPerBurst = if counted then outShape.rowsPerBurst else max inShape.rowsPerBurst outShape.rowsPerBurst
                let rowShift = ceilLog2 rowsPerBurst

                assertThat
                    (bnot running ||| eq (slice (rowShift - 1) 0 frameCount) (lit 0UL rowShift))
                    $"frameCount must be a multiple of the rows a burst holds (%d{rowsPerBurst})")

    { board = board
      name = name
      top = top.def
      registers =
        [ "id", batch.id
          "start", batch.start
          "busy", batch.busy
          "doneIrq", batch.doneIrq
          "srcAddr", batch.srcAddr
          "dstAddr", batch.dstAddr
          "frameCount", batch.frameCount
          "cycles", batch.cycles ]
        @ entries
      batch = Some batch
      map = map
      target = targetOf board.part }

/// The design on a board, the way the path says: the converter on the pins,
/// or the host's memory.
let boardTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    checkBoard board

    // Four combinations of carriers are built. The rest are sayable — which
    // is the point of binding per need — and are refused here by name, so
    // "not built yet" is distinguishable from "cannot be described".
    match carrierOfInput path d, carrierOfOutput path d with
    | None, _
    | _, None ->
        failwith
            $"{d.name}: nothing carries one of the design's streams on this path — every need wants a binding, by name or by kind"
    | Some OnPins, Some OnPins -> pinsTop board path d
    | Some InHostRows, Some InHostRows
    | Some AsBeatCount, Some InHostRows
    | Some AsBeatCount, Some InHostRowsAt -> hostMemoryTop board path d
    | Some OnPins, Some (InHostRows | InHostRowsAt) ->
        failwith
            $"{d.name}: pins in and the host's memory out is not built yet — it is the recording path (a design taps its converter and the host reads what it heard), and it needs the pins top to grow a write side"
    | Some (InHostRows | AsBeatCount), Some OnPins ->
        failwith
            $"{d.name}: the host's memory in and the pins out is not built yet — it is the playback path (the host stages samples and the design plays them), and it needs the batch top to grow a converter"
    | Some InHostRows, Some InHostRowsAt ->
        failwith
            $"{d.name}: rows in and addressed beats out is not built yet — the read side and the scatter side are each built, and nothing has yet wanted both"
    | Some inCarrier, Some outCarrier ->
        failwith $"{d.name}: nothing builds {inCarrier} in and {outCarrier} out"

/// The register map as the Rust seam prints it, headed for the top it serves.
let seamLines (t: BoardTop) : string list =
    [ $"//! Register map for the `{t.name}` slave on the {t.board.name}."
      "//! Generated from the design's own register map. Do not edit by hand."
      "" ]
    @ regMapRsLines t.map

/// The top's Verilog and its seam, written into `dir`: `{Name}.v` and
/// `{name}_layout.rs`. The paths written come back.
let write (dir: string) (t: BoardTop) : string list =
    System.IO.Directory.CreateDirectory dir |> ignore
    let verilog = System.IO.Path.Combine(dir, $"{t.name}.v")
    System.IO.File.WriteAllText(verilog, emitDesignFor t.target t.top + "\n")

    let snake =
        t.name
        |> Seq.mapi (fun i c -> if System.Char.IsUpper c && i > 0 then $"_{System.Char.ToLower c}" else string (System.Char.ToLower c))
        |> String.concat ""

    let seam = System.IO.Path.Combine(dir, $"{snake}_layout.rs")
    System.IO.File.WriteAllText(seam, String.concat "\n" (seamLines t) + "\n")
    [ verilog; seam ]

/// The F# half of the batch bridge: the host-memory top in the Sim, its
/// DDR the behavioural model, spoken to over stdin the way `SimAxi.serve`
/// is — `R`/`W` are AXI-Lite transactions, `C <hexn>` runs free cycles,
/// `D <hexoff> <hexlen>` dumps DDR as hex, `L <hexoff> <hex>` loads it, and
/// `M` names the design's registers with their offsets, so a host tool
/// needs no seam file to set a control by name. The same Rust driver then
/// runs against this before any bitstream exists.
let batchServe (t: BoardTop) =
    let memory =
        match t.batch, t.board.hostMemory with
        | Some _, Some m -> m
        | _ -> failwith $"{t.name}: the batch bridge serves a host-memory top"

    let sim = Sim t.top

    // The counted top reads nothing and has no read channel, so its DDR is
    // the write-only slave; the memory path's answers both.
    let reads = t.top.decls |> List.exists (function Input(n, _) -> n = "m_axi_arready" | _ -> false)

    let cycle, ddrMemory =
        if reads then
            let ddr = SimAxiDdr(sim, memory.arenaBytes, dataBytes = memory.width / 8)
            ddr.Cycle, ddr.Memory
        else
            let ddr = SimAxiWriteSlave(sim, memory.arenaBytes, dataBytes = memory.width / 8)
            ddr.Cycle, ddr.Memory

    let axi = SimAxi.clientWith sim cycle
    let out = System.Console.Out
    out.WriteLine "BATCHSERVE"
    out.Flush()

    let mutable line = System.Console.In.ReadLine()

    while line <> null do
        (match line.Split(' ') with
         | [| "R"; off |] -> out.WriteLine(sprintf "%08x" (axi.read32 (System.Convert.ToUInt64(off, 16))))
         | [| "W"; off; value |] ->
             axi.write32 (System.Convert.ToUInt64(off, 16)) (System.Convert.ToUInt64(value, 16))
             out.WriteLine "OK"
         | [| "C"; n |] ->
             for _ in 1 .. int (System.Convert.ToUInt64(n, 16)) do
                 cycle ()

             out.WriteLine "OK"
         | [| "D"; off; len |] ->
             let start = int (System.Convert.ToUInt64(off, 16))
             let count = int (System.Convert.ToUInt64(len, 16))
             out.WriteLine(ddrMemory[start .. start + count - 1] |> Array.map (sprintf "%02x") |> String.concat "")
         | [| "L"; off; hex |] ->
             let start = int (System.Convert.ToUInt64(off, 16))

             for i in 0 .. hex.Length / 2 - 1 do
                 ddrMemory[start + i] <- System.Convert.ToByte(hex.Substring(i * 2, 2), 16)

             out.WriteLine "OK"
         | [| "M" |] ->
             out.WriteLine(t.registers |> List.map (fun (n, e) -> sprintf "%s:%x" n e.offset) |> String.concat " ")
         | _ -> out.WriteLine "ERR")

        out.Flush()
        line <- System.Console.In.ReadLine()
