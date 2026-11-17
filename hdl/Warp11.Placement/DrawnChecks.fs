/// The port model's checks beyond a chain's bytes: the edits, the file, a
/// recording, an image and a table on the boundary, and the drawn design on
/// a board. UD17–UD22 are the field model's checks of the same names, the
/// design under them drawn with ports.
module Warp11.Placement.DrawnChecks

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Placement.Placement
open Warp11.Pedal
open Warp11.Factories
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate
open Warp11.Devices
open Warp11.Placement.ChainChecks

// ---------------------------------------------------------------------------
// UD5 — The boundary bound to a device. A tone plays into the gain design
// through the simulator's mapping; what comes out is the tone, exactly
// twice as loud, every frame. And a mapping that does not fit the boundary
// is refused before anything runs, naming the stream.

// CHECK
let wavPlaysThroughTheGraph () : bool =
    // A quarter of full scale, so twice as loud still fits a 16-bit sample.
    let tone = toneWav 48_000 200 440.0 0.25

    let heard =
        runInSim 10_000 gainGraph { source = tone; controls = [ "volume", 2UL * gainUnity; "mute", 0UL ]; outputPath = None }

    let doubled = heard.samples = (tone.samples |> Array.map (fun s -> s * 2s))

    let refuses (g: Graph) (m: SimMapping) (names: string list) =
        try
            runInSim 100 g m |> ignore
            false
        with e ->
            names |> List.forall (fun n -> e.Message.Contains n)

    doubled
    && heard.FrameCount = tone.FrameCount
    // the adder's boundary is not stereo audio
    && refuses Examples.adder { source = tone; controls = []; outputPath = None } [ "Adder"; "'in'"; "left" ]
    // a control the mapping forgot
    && refuses gainGraph { source = tone; controls = [ "volume", gainUnity ]; outputPath = None } [ "mute" ]
    // a second stream on the boundary has no device yet
    && refuses
        { gainGraph with inputs = gainGraph.inputs @ [ { name = "side"; fields = stereoPins.fields } ] }
        { source = tone; controls = []; outputPath = None }
        [ "one stream in"; "2 in" ]

// ---------------------------------------------------------------------------
// UD6 — The editor's changes are functions on the graph, and they compose
// into a design. The gain design built from an empty one by the edits a
// canvas makes elaborates to the bytes of the pipeline; the six examples
// are built the same way and every one elaborates. An edge's connection is
// an edit. Every refusal names the port; removing a box takes its edges;
// undo is exact.

// CHECK
let editsBuildTheDesigns () : bool =
    let step (change: Graph -> Result<Graph, string>) (h: History) =
        match apply change h with
        | h, None -> h
        | _, Some why -> failwith why

    let connect (a: string) (b: string) =
        let ends (s: string) =
            match s.Split '.' with
            | [| box; p |] -> port box p
            | _ -> failwith s

        addEdge (ends a, Out) (ends b, In)

    let built =
        history (emptyGraph "GainPatch" defaultSampleRate stereoPins.fields)
        |> step (addControl ("volume", unsignedInt 16))
        |> step (addControl ("mute", unsignedInt 1))
        |> step (addBox "gain" (320.0, 120.0) >> Result.map fst)
        |> step (connect "input.in" "gain.in")
        |> step (connect "input.volume" "gain.volume")
        |> step (connect "input.mute" "gain.mute")
        |> step (connect "gain.out" "output.out")

    let g = built.present

    let refuses (change: Graph -> Result<Graph, string>) (names: string list) =
        match change g with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    // The first box of a unit takes the unit's name; the next is numbered.
    let withSecond, second = addBox "gain" (0.0, 0.0) g |> Result.defaultWith failwith
    // Renaming carries the edges.
    let renamed = renameBox "gain" "amp" g |> Result.defaultWith failwith
    // Removing the box takes its four edges with it.
    let removed = removeBox "gain" g |> Result.defaultWith failwith
    // A connection is set on the edge, and is a different design.
    let intoGain = wire (port "input" "in") (port "gain" "in")
    let registered = setConnection intoGain Connection.Forward g |> Result.defaultWith failwith
    // A boundary stream's fields change under its edge, which stays.
    let narrowed = setFields (port "input" "in") [ "left", signedInt sampleWidth ] g |> Result.defaultWith failwith

    let examples = Examples.all Examples.recordingRate

    emitDesign (elaborate g).def = emitDesign gainPipeline.def
    && g.boxes = gainGraph.boxes
    && set g.edges = set gainGraph.edges
    && built.past.Length = 7
    && (built |> undo |> undo).present.edges.Length = 2
    && (built |> undo |> undo |> redo |> redo).present = g
    && second = "gain2"
    && withSecond.boxes.Length = 2
    && renamed.edges |> List.forall (fun e -> e.from.box <> "gain" && e.``to``.box <> "gain")
    && emitDesign (elaborate renamed).def <> emitDesign gainPipeline.def
    && removed.edges = []
    && removed.boxes = []
    && registered.edges.Head.connection = Connection.Forward
    && emitDesign (elaborate registered).def <> emitDesign (elaborate g).def
    && narrowed.edges = g.edges
    && (match edgeFit narrowed intoGain with DoesNotFit _ -> true | _ -> false)
    // The examples: thirteen, each built by edits, each a design.
    && examples.Length = 13
    && examples |> List.forall (fun (_, example) -> (emitDesign (elaborate example).def).Contains $"module {example.name}")
    // ...and the two that carry a connection are the checked pipelines.
    && emitDesign (elaborate (Examples.threeBandEq rate)).def = emitDesign (threeBandEqPipeline Connection.Forward).def
    // both ends inputs
    && refuses (addEdge (port "gain" "in", In) (port "output" "out", In)) [ "inputs" ]
    // a box feeding itself
    && refuses (addEdge (port "gain" "out", Out) (port "gain" "in", In)) [ "gain"; "itself" ]
    // a stream into a control inlet
    && refuses (addEdge (port "input" "in", Out) (port "gain" "volume", In)) [ "input.in"; "gain.volume"; "a stream into a control" ]
    // the edge is already drawn; a control inlet takes one
    && refuses (addEdge (port "input" "in", Out) (port "gain" "in", In)) [ "input.in → gain.in"; "already" ]
    && refuses (addEdge (port "input" "volume", Out) (port "gain" "mute", In)) [ "gain.mute"; "16w" ]
    // a port that is not there
    && refuses (addEdge (port "input" "centre", Out) (port "gain" "in", In)) [ "input.centre" ]
    // a connection on a control edge
    && refuses (setConnection (wire (port "input" "mute") (port "gain" "mute")) Connection.Forward) [ "input.mute"; "held value" ]
    // a control named like an input stream shares the input box with it
    && refuses (addControl ("in", unsignedInt 8)) [ "in" ]
    // a box cannot be named for the boundary, and a unit must be in the palette
    && refuses (renameBox "gain" "output") [ "output" ]
    && refuses (addBox "biquad" (0.0, 0.0) >> Result.map fst) [ "biquad"; "palette" ]

// ---------------------------------------------------------------------------
// UD7 — A saved design opens as it was, positions and connections
// included, and elaborates to the same bytes. A file from before boxes had
// stream ports is refused, saying so. The palette is the type authority: a
// unit it does not have refuses the file; an edge that no longer fits opens
// with the design and is refused by name when the design is built.

// CHECK
let savedDesignOpensAsItWas () : bool =
    let laidOut =
        { gainGraph with
            positions = Map.ofList [ "input", (60.0, 120.0); "gain", (320.5, 140.25); "output", (580.0, 120.0) ] }

    let text = DesignFile.write laidOut

    let reopened =
        match DesignFile.parse text with
        | Ok g -> g
        | Error why -> failwith why

    let roundTrips (g: Graph) =
        match DesignFile.parse (DesignFile.write g) with
        | Ok back -> back = g
        | Error why ->
            printfn $"      {g.name}: {why}"
            false

    let refuses (edit: string -> string) (names: string list) =
        match DesignFile.parse (edit text) with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    // The edge that stopped fitting: it is in the opened design, and names
    // itself when the design is built.
    let opensThenRefuses (edit: string -> string) (names: string list) =
        match DesignFile.parse (edit text) with
        | Error _ -> false
        | Ok g ->
            try
                emitDesign (elaborate g).def |> ignore
                false
            with e ->
                names |> List.forall (fun n -> e.Message.Contains n)

    // A file as the field model wrote it.
    let fieldModel =
        """{ "name": "GainPatch", "streams": 1, "sampleRate": 48000, "inputs": [], "controls": [], "outputs": [],
             "boxes": [], "wires": [ { "from": "input.left", "to": "gain.left" } ], "positions": {} }"""

    let threeBands = DesignFile.write (Examples.threeBandEq rate)

    reopened = laidOut
    && emitDesign (elaborate reopened).def = emitDesign gainPipeline.def
    && Examples.all Examples.recordingRate |> List.forall (snd >> roundTrips)
    // a wire says nothing about its connection; a register stage does
    && not (text.Contains "\"connection\"")
    && threeBands.Contains "\"connection\": \"Forward\""
    // a design drawn field to field
    && (match DesignFile.parse fieldModel with
        | Error why -> why.Contains "field to field" && why.Contains "Redraw"
        | Ok _ -> false)
    && refuses (fun t -> t.Replace("\"format\": 2", "\"format\": 3")) [ "format 3" ]
    // a unit the palette does not have
    && refuses (fun t -> t.Replace("\"unit\": \"gain\"", "\"unit\": \"biquad\"")) [ "gain"; "biquad"; "palette" ]
    // a connection that is not one
    && (match DesignFile.parse (threeBands.Replace("\"Forward\"", "\"Sideways\"")) with
        | Error why -> why.Contains "Sideways"
        | Ok _ -> false)
    // an edge onto a port the unit does not have
    && opensThenRefuses (fun t -> t.Replace("\"to\": \"gain.mute\"", "\"to\": \"gain.bypass\"")) [ "gain.bypass" ]
    // an edge whose format the unit no longer agrees with
    && opensThenRefuses (fun t -> t.Replace("\"name\": \"volume\",\n      \"width\": 16", "\"name\": \"volume\",\n      \"width\": 8")) [ "input.volume → gain.volume" ]
    // not a design at all
    && refuses (fun _ -> "{ \"name\": 3 }") [ "name" ]
    && refuses (fun _ -> "nonsense") [ "JSON" ]

// ---------------------------------------------------------------------------
// UD14 — An image on the boundary. Rows in, rows out: the blur design built
// by edits, an image played through it by the image mapping a row a beat,
// the halo the unit's own, and what comes out is the 3×3 clamp blur
// computed by hand. The graph elaborates byte-identical to the typed form — the blur
// module as a unit, one stage, nothing around it. A PGM round-trips through the file, and an
// image of another width is refused naming the stream.

// CHECK
let imageOnTheBoundary () : bool =
    let columns, rows = 16, 8
    let g = Examples.imageBlur columns rows
    let seed = System.Random 7

    let image =
        { width = columns
          height = rows
          pixels = Array.init (columns * rows) (fun _ -> byte (seed.Next 256)) }

    let expected =
        [| for r in 0 .. rows - 1 do
               for c in 0 .. columns - 1 ->
                   let mutable sum = 0

                   for dr in -1..1 do
                       for dc in -1..1 do
                           let rr = max 0 (min (rows - 1) (r + dr))
                           let cc = max 0 (min (columns - 1) (c + dc))
                           sum <- sum + int image.pixels[rr * columns + cc]

                   byte ((sum >>> 3) &&& 0xFF) |]

    let heard = runImageInSim 10_000 g { image = image; outputPath = None }

    let typed =
        let rowPins = pins1 (Warp11.Devices.rowPin columns)

        defModule
            "ImageBlur"
            (fun p -> streamInputPorts p "in" rowPins, streamOutputPorts p "out" rowPins)
            (fun (inPorts, outPorts) -> streamSource inPorts |> stage [] (blurUnit columns rows) "blur" |> streamSink outPorts)

    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-pgm-{System.Guid.NewGuid()}.pgm")
    writePgm dir image
    let reread = readPgm dir
    System.IO.File.Delete dir

    let refused =
        try
            runImageInSim 100 g { image = { image with width = columns + 1; pixels = Array.zeroCreate ((columns + 1) * rows) }; outputPath = None } |> ignore
            false
        with e ->
            e.Message.Contains "row" && e.Message.Contains (string ((columns + 1) * 8))

    heard.pixels = expected
    && heard.width = columns
    && heard.height = rows
    && emitDesign (elaborate g).def = emitDesign typed.def
    && reread = image
    && refused

// ---------------------------------------------------------------------------
// UD15 — A table on the boundary. Rows in, a row a beat, columns by pin
// name: the adder design fed the numbers table sums every row. A cell reads
// in its pin's format and writes back the same way — a negative fixed-point
// value round-trips through the text — and a table missing a column is
// refused naming it.

// CHECK
let tableOnTheBoundary () : bool =
    let out = runCsvInSim 10_000 Examples.adder { table = Examples.numbers; controls = []; outputPath = None }

    let sums =
        Examples.numbers.rows |> List.map (fun row -> string (int row[0] + int row[1]))

    let q = signedFixed 16 4

    let roundTrips =
        [ "-3.25"; "0"; "12.5"; "-2047.9375" ]
        |> List.forall (fun text ->
            match parseCell q text with
            | Ok bits -> formatCell q bits = text
            | Error _ -> false)

    let refuses (text: string) (names: string list) =
        match parseCell q text with
        | Error why -> names |> List.forall (fun n -> why.Contains n)
        | Ok _ -> false

    let missing =
        try
            runCsvInSim 100 Examples.adder { table = { columns = [ "x" ]; rows = [ [ "1" ] ] }; controls = []; outputPath = None } |> ignore
            false
        with e ->
            e.Message.Contains "'y'"

    out.columns = [ "sum" ]
    && (out.rows |> List.map List.head) = sums
    && roundTrips
    && refuses "2048" [ "2048"; "16w/4f/signed" ]
    && refuses "lots" [ "lots"; "not a number" ]
    && missing

// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// UD17 — The design on a board. The gain design made for the KV260's rate
// becomes an AXI-Lite top with the Pmod's converters around it and one
// register per control port; in the simulator, a host writes `volume`
// through the AXI-Lite bridge and a tone through the software codec comes
// back doubled. The register map is the seam's, one entry per port; the
// same design on the iCEBreaker preset takes the UART shape and emits for
// iCE40 at the board's own rate; with no host at all the controls are baked
// at their starting values; a design made for another rate is refused,
// saying the rate the board lands on.

// CHECK
let designOnABoard () : bool =
    let onKv260 = { gainGraph with name = "GainBoard"; sampleRate = stockSampleRate }
    let top = Warp11.Elaborate.boardTopOf kv260 viaPins onKv260
    let sim = Sim top.top
    let axi = SimAxi.client sim
    let volume = top.registers |> List.find (fun (n, _) -> n = "volume") |> snd
    axi.write32 volume.offset (2UL * gainUnity)

    let samples = [ 0x001000UL, 0x002000UL; 0x000123UL, 0x000456UL; 0x010000UL, 0x020000UL ]

    let heard =
        samples
        |> i2sThrough sim separateCodecSimPins
        |> Seq.skipWhile i2sSilence
        |> Seq.take samples.Length
        |> List.ofSeq

    let seam = Warp11.BoardTop.seamLines top |> String.concat "\n"

    let iceBoard = iceBreakerAt 24_000_000
    let onIce = { onKv260 with name = "GainIce"; sampleRate = Warp11.BoardTop.boardRate iceBoard 48_000.0 }
    let ice = Warp11.Elaborate.boardTopOf iceBoard viaPins onIce
    let bare = Warp11.Elaborate.boardTopOf { iceBoard with host = NoHost } viaPins onIce

    let refused =
        try
            Warp11.Elaborate.boardTopOf kv260 viaPins { onKv260 with sampleRate = 48_000.0 } |> ignore
            false
        with e ->
            e.Message.Contains "48000" && e.Message.Contains "48828.125"

    let impossible =
        try
            Warp11.Elaborate.boardTopOf { iceBoard with host = AxiLiteAt 0UL } viaPins onIce |> ignore
            false
        with e ->
            e.Message.Contains "no processing system"

    heard = (samples |> List.map (fun (l, r) -> 2UL * l, 2UL * r))
    && (top.registers |> List.map fst) = [ "volume"; "mute" ]
    && seam.Contains "VOLUME"
    && seam.Contains "MUTE"
    && (emitDesignFor ice.target ice.top).Contains "module GainIceUart"
    && ice.board.name = iceBoard.name
    && bare.registers.IsEmpty
    && (emitDesignFor bare.target bare.top).Contains "module GainIceTop"
    && refused
    && impossible

// UD18 — The design on the host's memory. The same gain design takes the
// batch shape on the KV260: rows staged in DDR at `srcAddr`, read in
// bursts, unpacked a row a cycle through the design, packed and written to
// `dstAddr`, with `volume` a register like any control. In the simulator,
// against the behavioural DDR, what comes back is what the WAV mapping
// hears — sample for sample — so the DDR plumbing and the converter path
// agree on the design between them. A board with no host memory refuses.

// CHECK
let designOnHostMemory () : bool =
    let g = { gainGraph with name = "GainMemory"; sampleRate = stockSampleRate }
    let top = Warp11.Elaborate.boardTopOf kv260 viaHostMemory g
    let batch = top.batch.Value
    let sim = Sim top.top
    let ddr = SimAxiDdr(sim, 0x20000)
    let axi = SimAxi.clientWith sim ddr.Cycle

    // One burst of rows, and a burst is the board's to size: two stereo
    // frames a beat. Source and destination on the burst's own boundary.
    let burstBeats = kv260.hostMemory.Value.burstBeats
    let burstBytes = burstBeats * kv260.hostMemory.Value.width / 8
    let frames = 2 * burstBeats
    let source = toneWav (int (round stockSampleRate)) frames 1000.0 0.25
    let src = burstBytes
    let dst = 4 * burstBytes

    for i in 0 .. frames - 1 do
        ddr.WriteWord(src + i * 8, uint32 (int source.samples[2 * i] <<< 8))
        ddr.WriteWord(src + i * 8 + 4, uint32 (int source.samples[2 * i + 1] <<< 8))

    let idOk = axi.read32 batch.id.offset = Warp11.BoardTop.batchId
    let volume = top.registers |> List.find (fun (n, _) -> n = "volume") |> snd
    axi.write32 volume.offset (2UL * gainUnity)
    axi.write32 batch.srcAddr.offset (uint64 src)
    axi.write32 batch.dstAddr.offset (uint64 dst)
    axi.write32 batch.frameCount.offset (uint64 frames)
    axi.write32 batch.start.offset 1UL

    let mutable spins = 0

    while axi.read32 batch.busy.offset <> 0UL && spins < 400_000 do
        ddr.Cycle()
        spins <- spins + 1

    // The cycle counter's defining property: it counts the work, stops, and
    // starts over. Frozen once done — two reads agree with cycles passing
    // between them — and an identical second batch counts *the same*, which
    // is what tells cleared-at-start from accumulating. Not compared against
    // the host's poll count: a poll is a bus transaction that runs the
    // fabric several cycles, so the fabric's own count is the larger.
    let counted = axi.read32 batch.cycles.offset
    for _ in 1..50 do ddr.Cycle()
    let frozen = axi.read32 batch.cycles.offset = counted

    // A second batch clears it and counts again rather than accumulating.
    axi.write32 batch.start.offset 1UL
    let mutable again = 0

    while axi.read32 batch.busy.offset <> 0UL && again < 400_000 do
        ddr.Cycle()
        again <- again + 1

    let recounted = axi.read32 batch.cycles.offset

    let mask = (1UL <<< sampleWidth) - 1UL

    let heard =
        [| for i in 0 .. frames - 1 do
               fromSampleBits (uint64 (ddr.ReadWord(dst + i * 8)) &&& mask)
               fromSampleBits (uint64 (ddr.ReadWord(dst + i * 8 + 4)) &&& mask) |]

    let expected =
        Warp11.Devices.runInSim
            10_000
            g
            { source = source
              controls = [ "volume", 2UL * gainUnity; "mute", 0UL ]
              outputPath = None }

    let refused =
        try
            Warp11.Elaborate.boardTopOf (iceBreakerAt 24_000_000) viaHostMemory g |> ignore
            false
        with e ->
            e.Message.Contains "no host memory"

    idOk
    && spins < 400_000
    && again < 400_000
    && counted > 0UL
    && frozen
    && recounted = counted
    && heard = expected.samples
    && top.name = "GainMemoryBatch"
    && (top.registers |> List.map fst |> List.take 8) = [ "id"; "start"; "busy"; "doneIrq"; "srcAddr"; "dstAddr"; "frameCount"; "cycles" ]
    // The contract the Rust driver is written against, offset by offset.
    // The batch contract's offsets, pinned: `runtime/core/src/batch.rs` hard-codes
    // them so a driver can find them with no seam file for the design.
    && (top.registers |> List.map (fun (_, e) -> e.offset) |> List.take 8) = [ 0UL; 0UL; 0x8UL; 0xcUL; 0x10UL; 0x14UL; 0x18UL; 0x1cUL ]
    && refused

// UD19 — The build directory, Vivado. The gain design on the KV260, both
// ways: the generator writes the block design around the top (the module
// reference, one port per pin, the aperture at the board's base), the
// constraints from the connector table, the flow with its timing gate, the
// overlay with the uio node and the clock the design was elaborated for,
// and the packaging. The host-memory way adds the PS slave port at its
// width, the master's smartconnect and DDR segment, the arena node, and
// the bus-interface attribute on the emitted clock. A board whose connector
// lacks a pin the top needs is refused naming the port, before any file.

// CHECK
let buildDirectoryVivado () : bool =
    let g = { gainGraph with name = "GainBuild"; sampleRate = stockSampleRate }
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-build-{System.Guid.NewGuid()}")

    let read (sub: string) (file: string) =
        System.IO.File.ReadAllText(System.IO.Path.Combine(dir, sub, file))

    try
        let pins = Warp11.Build.write (System.IO.Path.Combine(dir, "pins")) (Warp11.Elaborate.boardTopOf kv260 viaPins g)
        let memory = Warp11.Build.write (System.IO.Path.Combine(dir, "memory")) (Warp11.Elaborate.boardTopOf kv260 viaHostMemory g)

        let bd = read "pins" "gain_build_axi_bd.tcl"
        let xdc = read "pins" "gain_build_axi_pins.xdc"
        let dts = read "pins" "gain_build_axi.dts"
        let flow = read "pins" "build.tcl"
        let sh = read "pins" "build.sh"

        let bdM = read "memory" "gain_build_batch_bd.tcl"
        let dtsM = read "memory" "gain_build_batch.dts"
        let vM = read "memory" "GainBuildBatch.v"

        let pinsOk =
            bd.Contains "create_bd_cell -type module -reference GainBuildAxi GainBuildAxi_0"
            && bd.Contains "apply_board_preset"
            && bd.Contains "assign_bd_address -offset 0xB0000000 -range 0x00001000"
            && bd.Contains "PL0_REF_CTRL__FREQMHZ {100}"
            && [ "mclk"; "lrclk"; "sclk"; "sdin"; "mclk2"; "lrclk2"; "sclk2" ]
               |> List.forall (fun p -> bd.Contains $"create_bd_port -dir O {p}")
            && bd.Contains "create_bd_port -dir I sdout"
            && not (bd.Contains "S_AXI_HPC0_FPD")
            && xdc.Contains "set_property -dict {PACKAGE_PIN B11 IOSTANDARD LVCMOS33} [get_ports sdout]"
            && dts.Contains "reg = <0x0 0xb0000000 0x0 0x1000>"
            && dts.Contains "assigned-clock-rates = <100000000>"
            && dts.Contains "firmware-name = \"xilinx/gain-build-axi/gain_build_axi_bd_wrapper.bit.bin\""
            && not (dts.Contains "u-dma-buf")
            && flow.Contains "set_property board_part xilinx.com:kv260_som:part0:1.4"
            && flow.Contains "gain_build_axi_pins.xdc"
            && flow.Contains "warp11_assert_timing_met impl_1"
            && sh.Contains "xmutil loadapp gain-build-axi"
            && pins.run.EndsWith "build.sh"

        let memoryOk =
            bdM.Contains "CONFIG.PSU__USE__S_AXI_GP0 {1}"
            && bdM.Contains "CONFIG.PSU__SAXIGP0__DATA_WIDTH {128}"
            && bdM.Contains "[get_bd_intf_pins zynq_ultra_ps_e_0/S_AXI_HPC0_FPD]"
            && bdM.Contains "SAXIGP0/HPC0_DDR_LOW"
            && bdM.Contains "saxihpc0_fpd_aclk"
            && not (bdM.Contains "create_bd_port")
            && dtsM.Contains "size = <0x00800000>"
            && dtsM.Contains "device-name = \"udmabuf-gain-build-batch\""
            && vM.Contains "ASSOCIATED_BUSIF s_axi:m_axi"
            && not (memory.files |> List.exists (fun f -> f.EndsWith ".xdc"))

        let shortBoard =
            { kv260 with
                connectors =
                    [ { role = I2sSeparateCodecs
                        pins = (connectorFor I2sSeparateCodecs kv260).Value |> List.filter (fun (p, _) -> p <> "sdout") } ] }

        let refused =
            try
                Warp11.Build.write (System.IO.Path.Combine(dir, "short")) (Warp11.Elaborate.boardTopOf shortBoard viaPins g)
                |> ignore

                false
            with e ->
                e.Message.Contains "no pin for sdout"

        pinsOk && memoryOk && refused
    finally
        if System.IO.Directory.Exists dir then
            System.IO.Directory.Delete(dir, true)

// UD20 — The build directory, the open flow. The gain design on the
// iCEBreaker at the board's rate: the generator computes the PLL that takes
// the crystal to the fabric clock the way `icepll` does, writes the wrapper
// around the design with that PLL and a reset released on lock, the pin map
// from the connector table with the crystal and the UART on it, and the
// flow script with the part, package and clock as values.

// CHECK
let buildDirectoryOpenFlow () : bool =
    let pll = Warp11.Build.icePll 12_000_000 24_000_000
    let board = iceBreakerAt 24_000_000
    let g = { gainGraph with name = "GainIce"; sampleRate = Warp11.BoardTop.boardRate board 48_000.0 }
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-ice-{System.Guid.NewGuid()}")

    let read (file: string) =
        System.IO.File.ReadAllText(System.IO.Path.Combine(dir, file))

    try
        let out = Warp11.Build.write dir (Warp11.Elaborate.boardTopOf board viaPins g)
        let wrapper = read "gain_ice_uart_top.v"
        let pins = read "gain_ice_uart_top.pcf"
        let sh = read "build.sh"

        // The same design on a shared-bus header: the pinout follows the
        // connector, and the pin map names its four lines.
        let shared =
            { board with
                connectors =
                    (board.connectors |> List.filter (fun c -> c.role <> I2sSeparateCodecs))
                    @ [ { role = I2sSharedBus
                          pins =
                            [ "bclk", { pin = "43"; standard = None; activeLow = false }
                              "ws", { pin = "38"; standard = None; activeLow = false }
                              "sd_in", { pin = "34"; standard = None; activeLow = false }
                              "sd_out", { pin = "31"; standard = None; activeLow = false } ] } ] }

        let sharedDir = System.IO.Path.Combine(dir, "shared")
        Warp11.Build.write sharedDir (Warp11.Elaborate.boardTopOf shared viaPins g) |> ignore
        let sharedPins = System.IO.File.ReadAllText(System.IO.Path.Combine(sharedDir, "gain_ice_uart_top.pcf"))

        let both =
            try
                Warp11.Elaborate.boardTopOf { shared with connectors = shared.connectors @ (board.connectors |> List.filter (fun c -> c.role = I2sSeparateCodecs)) } viaPins g
                |> ignore

                false
            with e ->
                e.Message.Contains "not both"

        (pll.divr, pll.divf, pll.divq, pll.filterRange) = (0, 63, 5, 1)
        && sharedPins.Contains "set_io bclk 43"
        && sharedPins.Contains "set_io sd_in 34"
        && not (sharedPins.Contains "mclk")
        && both
        && pll.achievedHz = 24_000_000.0
        && wrapper.Contains "module gain_ice_uart_top ("
        && wrapper.Contains ".DIVF(7'b0111111)"
        && wrapper.Contains ".PACKAGEPIN(clk12)"
        && wrapper.Contains "GainIceUart inner ("
        && wrapper.Contains ".host_rx(host_rx)"
        && pins.Contains "set_io clk12 35"
        && pins.Contains "set_io host_rx 6"
        && pins.Contains "set_io sdout 44"
        && not (pins.Contains "ledr_n")
        && sh.Contains "part=up5k"
        && sh.Contains "package=sg48"
        && sh.Contains "freq=24"
        && out.run.EndsWith "build.sh"
        && (out.files |> List.exists (fun f -> f.EndsWith "gain_ice_uart_layout.rs"))
    finally
        if System.IO.Directory.Exists dir then
            System.IO.Directory.Delete(dir, true)

// UD21 — The mapping is a file. A board with every axis and the data path
// round-trip through the mapping file, preset and custom alike; a board
// alone round-trips as a board file; the design names its default mapping
// and its file carries the name; (the export's half of this check returns with the export) the
// board, the path and a build function; a mapping naming an impossible
// combination is refused on load.

// CHECK
let mappingIsAFile () : bool =
    let custom = { kv260 with fabricHz = 99_999_001; name = "custom" }

    let roundTrips (m: Warp11.Mapping.Mapping) =
        Warp11.Mapping.parse (Warp11.Mapping.write m) = Ok m

    let boardRoundTrips (b: Board) =
        Warp11.Mapping.parseBoard (Warp11.Mapping.writeBoard b) = Ok b

    let ice = iceBreakerAt 24_000_000
    let g = { gainGraph with mapping = Some "gain.kv260.json" }
    let carried = Warp11.DesignFile.parse (Warp11.DesignFile.write g)

    let refused =
        match Warp11.Mapping.parse (Warp11.Mapping.write { board = { ice with host = AxiLiteAt 0UL }; path = viaPins; plugged = [] }) with
        | Error why -> why.Contains "no processing system"
        | Ok _ -> false

    roundTrips { board = kv260; path = viaHostMemory; plugged = [] }
    && roundTrips { board = kv260; path = viaPins; plugged = [] }
    && roundTrips { board = ice; path = viaPins; plugged = [] }
    && roundTrips { board = custom; path = viaHostMemory; plugged = [] }
    && boardRoundTrips kv260
    && boardRoundTrips ice
    && boardRoundTrips { ice with connectors = [ { role = I2sSharedBus; pins = [ "bclk", { pin = "43"; standard = None; activeLow = false } ] } ] }
    && Warp11.Boards.nameOf kv260 = Some "kv260"
    && Warp11.Boards.nameOf custom = None
    && (match carried with
        | Ok g' -> g'.mapping = Some "gain.kv260.json"
        | Error _ -> false)
    && Warp11.Mapping.fileFor "/tmp/x/gain.json" "kv260" = "/tmp/x/gain.kv260.json"
    && refused

// UD22 — Every example on every board that holds it. Each mapping file
// written beside an example builds that example's board top with nothing
// but the file: the memory path on the KV260, the link on the boards with
// one wire — the same design, a different carrier, no change to the design.
// The boards a design is not written for are the matrix's ✗ cells, each
// with its reason beside `Examples.mappings`.

// CHECK
let everyExampleOnItsBoards () : bool =
    let examples = Warp11.Placement.Examples.all Warp11.Placement.Examples.recordingRate |> Map.ofList

    Warp11.Placement.Examples.mappings
    |> List.map (fun (file, m) ->
        let example = file.Substring(0, file.IndexOf '.') + ".json"
        let g = examples[example]

        try
            let top = Warp11.Elaborate.boardTopFor m g
            let expectedSuffix = if m.path = viaHostMemory then "Batch" else "Link"
            let ok = top.name.EndsWith expectedSuffix && top.board.name = m.board.name
            printfn $"      {file}: {top.name} on the {top.board.name}"
            ok
        with e ->
            printfn $"      {file}: {e.Message}"
            false)
    |> List.forall id
