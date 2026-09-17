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

open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate
open Warp11.Devices

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
      ports: (string * Expr) list }

/// What a board top needs to know of a design, whether it was drawn or
/// written: its boundary, its controls with where they start, and how to
/// instantiate it. A drawn design is `ofGraph`; the export prints one for
/// the typed form, so the build follows from the code either way.
type Design =
    { name: string
      sampleRate: float
      streams: int
      inputs: (string * NumberFormat) list
      outputs: (string * NumberFormat) list
      controls: (string * NumberFormat) list
      starting: (string * uint64) list
      /// The instance under a name.
      rig: string -> Rig }

/// A drawn design, as a board top takes it.
let ofGraph (g: Graph) : Design =
    { name = g.name
      sampleRate = g.sampleRate
      streams = g.streams
      inputs = g.inputs
      outputs = g.outputs
      controls = controlPorts g
      starting = startingValues g
      rig =
        fun instance ->
            let io = (elaborate g).NewNamed instance

            { through = streamThroughInstance io.ins.Head io.outs.Head
              ports = io.controls } }

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

    for side, pins in [ "input", d.inputs; "output", d.outputs ] do
        if pins <> stereo then
            failwith $"{d.name}: the {board.name}'s converter needs the {side} box to be [{describePins stereo}], and it is [{describePins pins}]"

    let landed = boardRate board d.sampleRate

    if round landed <> round d.sampleRate then
        failwith $"{d.name}: the design is made for %g{d.sampleRate} Hz, and the {board.name}'s clock frames at %.3f{landed} Hz for it — make the design for that rate"

/// Register words the aperture holds: 256 bytes, as every audio app's.
let apertureAddrWidth = 8

/// The registers a design's control ports become: one read-write register
/// each, starting where the port starts (a number box's value, a setting;
/// the design's own controls at zero).
let registersOf (d: Design) : (string * RegEntry) list * RegMap =
    let starting = d.starting |> Map.ofList

    buildRegMapPinned apertureAddrWidth (fun r ->
        [ for name, f in d.controls ->
              name, r.RwReg(name, f.totalWidth, (starting |> Map.tryFind name |> Option.defaultValue 0UL)) ])

/// The design between the converter and the registers: the stream from the
/// link through the design's instance and back, every control port driven
/// from its register.
let private through (d: Design) (control: string -> Expr -> Expr) (i2s: I2sLink) =
    // `design` is a Verilog reserved word; the instance is the drawn one.
    let rig = d.rig "drawn"

    for name, port in rig.ports do
        control name port ==> port

    i2s.input
    |> Stream.mapTo (layoutOfList d.inputs) (fun (l, r) -> [ l; r ])
    |> rig.through
    |> Stream.mapTo sampleLayout (fun fields -> fields[0], fields[1])
    |> i2s.send

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
      frameCount: RegEntry }

/// The identity every batch top answers with, so a driver can refuse a
/// bitstream that is not one; the layout hash beside it says which design.
let batchId = 0x7A11BA7CUL

/// A lane is a host word: a field of a row sign-extended into 32 bits, so
/// the host reads a plain `i32` and gets the number the fabric computed.
let laneWidth = 32

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

    for name, f in pins do
        if f.totalWidth > laneWidth then
            failwith $"a lane is %d{laneWidth} bits and '{name}' is %d{f.totalWidth} — the host-memory path is not built for wider fields"

    let lanes = pins.Length

    if lanes < 1 || lanes > lanesPerBeat then
        failwith $"a row of %d{lanes} fields does not fit a %d{beatWidth}-bit beat — the host-memory path is not built for that"

    let lanesPerRow = 1 <<< ceilLog2 lanes
    let rowsPerBeat = lanesPerBeat / lanesPerRow

    { lanesPerRow = lanesPerRow
      rowsPerBeat = rowsPerBeat
      rowsPerBurst = rowsPerBeat * beatsPerBurst
      bytesPerRow = lanesPerRow * laneWidth / 8 }

/// The batch registers, then one read-write register per control port.
let private batchRegistersOf (d: Design) : BatchRegs * (string * RegEntry) list * RegMap =
    let starting = d.starting |> Map.ofList

    let (batch, controls), map =
        buildRegMapPinned apertureAddrWidth (fun r ->
            let id, start = r.Word(fun w -> w.Const("id", batchId), w.Pulse "start")
            r.LayoutHash "layoutHash"

            let batch =
                { id = id
                  start = start
                  busy = r.Word(fun w -> w.Field("busy", 1))
                  doneIrq = r.Word(fun w -> w.W1c "doneIrq")
                  srcAddr = r.RwReg("srcAddr", 32, 0UL)
                  dstAddr = r.RwReg("dstAddr", 32, 0UL)
                  frameCount = r.RwReg("frameCount", 32, 0UL) }

            let controls =
                [ for name, f in d.controls ->
                      name, r.RwReg(name, f.totalWidth, (starting |> Map.tryFind name |> Option.defaultValue 0UL)) ]

            batch, controls)

    batch, controls, map

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

    let fields =
        if shape.rowsPerBeat = 1 then
            fire ==> beats.ready
            [ for i, (_, f) in List.indexed pins -> laneAt i f.totalWidth ]
        else
            let phaseWidth = ceilLog2 shape.rowsPerBeat
            let phase = reg "unpack_phase" phaseWidth
            let last = eq phase (lit (uint64 (shape.rowsPerBeat - 1)) phaseWidth)
            (fire &&& last) ==> beats.ready

            If fire (fun () -> mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase)

            [ for i, (_, f) in List.indexed pins ->
                  selectIndexed phase [ for row in 0 .. shape.rowsPerBeat - 1 -> laneAt (row * shape.lanesPerRow + i) f.totalWidth ] ]

    { payload = fields
      valid = beats.valid
      ready = outReady
      layout = layoutOfList pins }

/// Rows become a beat. All but the last row of a beat are held in registers
/// — unavoidable, because the producer has already been told its row was
/// taken.
let private rowsToBeats (shape: RowShape) (pins: (string * NumberFormat) list) (beatWidth: int) (rows: Stream<Expr list>) : Stream<Expr> =
    let lane (f: NumberFormat) (e: Expr) =
        if f.signed then asUInt (pad laneWidth (asSInt e)) else pad laneWidth e

    let lanes = List.map2 (fun (_, f) e -> lane f e) pins rows.payload

    let rowBits =
        let padding = shape.lanesPerRow - lanes.Length
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
let private pinsTop (board: Board) (d: Design) : BoardTop =
    check board d
    let entries, map = registersOf d
    let rate = int (round d.sampleRate)

    let fromRegisters (regs: SlaveRegs) =
        let byName = Map.ofList entries
        fun (name: string) (_: Expr) -> regs.value byName[name]

    let baked =
        let starting = d.starting |> Map.ofList
        fun (name: string) (port: Expr) -> lit (starting |> Map.tryFind name |> Option.defaultValue 0UL) (width port)

    let name, top, registers =
        match board.host with
        | AxiLiteAt _ ->
            let name = $"{d.name}Axi"

            let top =
                defModuleClocked
                    axiClock
                    name
                    (fun p -> axiLiteSlavePorts p map.apertureAddrWidth, i2sPins p (pinoutOf board))
                    (fun (slavePorts, pins) ->
                        let regs = regMapSlave slavePorts map
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through d (fromRegisters regs) i2s)

            name, top.def, entries
        | UartAt baud ->
            let name = $"{d.name}Uart"

            let top =
                defModule
                    name
                    (fun p -> uartPins p "host", i2sPins p (pinoutOf board))
                    (fun (uart, pins) ->
                        let regs = serialRegMapSlave "host" board.fabricHz baud uart map
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through d (fromRegisters regs) i2s)

            name, top.def, entries
        | NoHost ->
            let name = $"{d.name}Top"

            let top =
                defModule
                    name
                    (fun p -> i2sPins p (pinoutOf board))
                    (fun pins ->
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through d baked i2s)

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
let private hostMemoryTop (board: Board) (d: Design) : BoardTop =
    let memory =
        match board.hostMemory with
        | Some m -> m
        | None -> failwith $"{d.name}: the {board.name} has no host memory — the design's rows have nowhere to go but the pins"

    match board.host with
    | AxiLiteAt _ -> ()
    | _ -> failwith $"{d.name}: the host-memory path needs an AXI-Lite host to be told where the rows are, and the {board.name} has none"

    if d.streams <> 1 then
        failwith $"{d.name}: the host-memory path carries one stream, and the design declares %d{d.streams}"

    let batch, entries, map = batchRegistersOf d
    let inShape = rowShape memory.width d.inputs
    let outShape = rowShape memory.width d.outputs
    let beatBytes = memory.width / 8
    let burstBytes = beatsPerBurst * beatBytes
    let name = $"{d.name}Batch"

    let top =
        defModuleClocked
            axiClock
            name
            (fun p ->
                axiLiteSlavePorts p map.apertureAddrWidth,
                axiReadBusPorts p "m_axi" 32 memory.width,
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

                let beatsTotal = wire "beats_total" 32
                let outShift = ceilLog2 outShape.rowsPerBeat
                pad 32 (slice 31 outShift frameCount) ==> beatsTotal

                let running = regBit "running"
                let arIssued = reg "ar_issued" 32
                let beatsWritten = reg "beats_written" 32

                // --- the read side: one descriptor per burst ---------------
                let reqReady = wireBit "req_ready"
                let arMore = wireBit "ar_more"
                (running &&& lt arIssued bursts) ==> arMore

                let reqAddr = wire "req_addr" 32
                let burstAddrShift = ceilLog2 burstBytes
                (regs.value batch.srcAddr + asUInt (shl burstAddrShift (slice (31 - burstAddrShift) 0 arIssued))) ==> reqAddr

                let requests: Stream<Expr * Expr> =
                    { payload = (reqAddr, lit (uint64 (beatsPerBurst - 1)) 8)
                      valid = arMore
                      ready = reqReady
                      layout = layout2 ("addr", 32) ("len", 8) }

                If (arMore &&& reqReady) (fun () -> arIssued + lit 1UL 32 ==> arIssued)

                let beats = axiMasterReaderBurstOn (axiReadBusOf readBusPorts) 4 beatsPerBurst requests

                // --- the design ---------------------------------------------
                let outBeats =
                    beats
                    |> beatsToRows inShape d.inputs
                    |> rig.through
                    |> rowsToBeats outShape d.outputs memory.width

                // --- the write side -----------------------------------------
                let wrAddr = wire "wr_addr" 32
                let beatAddrShift = ceilLog2 beatBytes
                (regs.value batch.dstAddr + asUInt (shl beatAddrShift (slice (31 - beatAddrShift) 0 beatsWritten))) ==> wrAddr

                let wrReady = wireBit "wr_ready"

                let writeBeats: Stream<Expr * Expr * Expr> =
                    { payload = (wrAddr, outBeats.payload, lit ((1UL <<< beatBytes) - 1UL) beatBytes)
                      valid = outBeats.valid
                      ready = wrReady
                      layout = axiWriteBeatLayout 32 memory.width }

                wrReady ==> outBeats.ready

                let writerIdle = axiMasterWriterWithIdleOn (axiWriteBusOf writeBusPorts) 4 writeBeats

                If (outBeats.valid &&& wrReady) (fun () -> beatsWritten + lit 1UL 32 ==> beatsWritten)

                // --- control ------------------------------------------------
                let finished = wireBit "finished"
                (running &&& eq beatsWritten beatsTotal &&& writerIdle) ==> finished

                ifElse
                    [ (regs.pulse batch.start,
                       fun () ->
                           lit 1UL 1 ==> running
                           lit 0UL 32 ==> arIssued
                           lit 0UL 32 ==> beatsWritten)
                      (otherwise, fun () -> If finished (fun () -> lit 0UL 1 ==> running)) ]

                regs.drive batch.busy running
                regs.setBit batch.doneIrq finished

                // The caller's half of the contract, said out loud: checked
                // every cycle in simulation, compiled out of the silicon.
                let aligned (e: RegEntry) =
                    bnot running ||| eq (slice (burstAddrShift - 1) 0 (regs.value e)) (lit 0UL burstAddrShift)

                assertThat (aligned batch.srcAddr) $"srcAddr must be %d{burstBytes}-byte aligned"
                assertThat (aligned batch.dstAddr) $"dstAddr must be %d{burstBytes}-byte aligned"

                let rowsPerBurst = max inShape.rowsPerBurst outShape.rowsPerBurst
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
          "frameCount", batch.frameCount ]
        @ entries
      batch = Some batch
      map = map
      target = targetOf board.part }

/// The design on a board, the way the path says: the converter on the pins,
/// or the host's memory.
let boardTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    checkBoard board

    match path with
    | Pins -> pinsTop board d
    | HostMemory -> hostMemoryTop board d

/// `boardTop` for a drawn design.
let boardTopOf (board: Board) (path: DataPath) (g: Graph) : BoardTop = boardTop board path (ofGraph g)

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
    let ddr = SimAxiDdr(sim, memory.arenaBytes, dataBytes = memory.width / 8)
    let axi = SimAxi.clientWith sim ddr.Cycle
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
                 ddr.Cycle()

             out.WriteLine "OK"
         | [| "D"; off; len |] ->
             let start = int (System.Convert.ToUInt64(off, 16))
             let count = int (System.Convert.ToUInt64(len, 16))
             out.WriteLine(ddr.Memory[start .. start + count - 1] |> Array.map (sprintf "%02x") |> String.concat "")
         | [| "L"; off; hex |] ->
             let start = int (System.Convert.ToUInt64(off, 16))

             for i in 0 .. hex.Length / 2 - 1 do
                 ddr.Memory[start + i] <- System.Convert.ToByte(hex.Substring(i * 2, 2), 16)

             out.WriteLine "OK"
         | [| "M" |] ->
             out.WriteLine(t.registers |> List.map (fun (n, e) -> sprintf "%s:%x" n e.offset) |> String.concat " ")
         | _ -> out.WriteLine "ERR")

        out.Flush()
        line <- System.Console.In.ReadLine()
