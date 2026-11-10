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


/// The clock a board's converter divides from: the audio oscillator where
/// the harness carries one, the fabric clock otherwise. This one line is why
/// an oscillator makes every rate exact — 12.288 MHz divides to 48 000 Hz
/// where 100 MHz lands on 48 828.125.
let converterClockHz (board: Board) =
    defaultArg board.audioClockHz board.fabricHz

/// The rate a board's I2S master frames at for a design's rate: the same
/// divisor arithmetic `i2sMasterHz` performs, so the number a design is
/// checked against is the number the clock generator will produce.
let boardRate (board: Board) (targetHz: float) : float =
    let clockHz = converterClockHz board
    let sclkHalfDiv = max 1 (int (round (float clockHz / (4.0 * targetHz * float stockBitsPerSlot))))
    sampleRateOf clockHz sclkHalfDiv stockBitsPerSlot

/// The converter's clock domain, where a board carries an audio oscillator:
/// the one domain the generated tops put logic in. Its conjured pins are
/// `audio_clk`/`audio_rst`, which is what the generated block design drives —
/// the oscillator straight in, and a `proc_sys_reset` on it as the one reset
/// synchroniser the top owns.
let audioClockDomain = clockDomain "audio"

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
    /// An action the host fires: a bit the host writes that the design sees
    /// high for one cycle and never has to clear — a start, a load, a stop.
    /// A value is a level the host leaves; a pulse is an event the host
    /// sends, and a design that polled a level for an edge would be
    /// restating what the register map already does.
    | PulseIn of name: string
    /// A value the design reports and the host reads — telemetry. The design
    /// drives it; nobody writes it.
    | ValueOut of name: string * format: NumberFormat
    /// A table the host sets and the design reads by index: `entries` words
    /// of `format`, kept in `storage`, starting as `starting`. The aid's gain
    /// curves are one — 512 words a host refits while the design runs.
    | TableIn of name: string * format: NumberFormat * entries: int * storage: RamStyle * starting: uint64 list
    /// A coherent surface the design keeps live — `rows` rows of `fields`,
    /// every one of them a register that may change in the same cycle, a
    /// Life world or a framebuffer. A stream cannot say that: its rows leave
    /// one at a time, and a surface copied a row a cycle while it runs is
    /// torn. So a carrier takes a surface whole — latched in one cycle when
    /// the host asks, then streamed — and what passes between pulls is
    /// never seen, which is the conflation the snapshot path has always
    /// meant.
    | SurfaceOut of name: string * fields: (string * NumberFormat) list * rows: int
    /// A number the design was built with that a host has to agree with — a
    /// frame's width, an iteration cap. No wire and no register: it reaches
    /// the host as a constant in the seam, so a driver reads it rather than
    /// writing it down a second time.
    | Fact of name: string * value: uint64

/// Rows into the design, under a name.
let streamIn name fields = StreamIn(name, fields)

/// Rows out of it.
let streamOut name fields = StreamOut(name, fields)

/// A value the host sets, starting at zero.
let valueIn name format = ValueIn(name, format, 0UL)

/// A value the host sets, starting where the design says.
let valueFrom name format starting = ValueIn(name, format, starting)

/// An action the host fires.
let pulseIn name = PulseIn name

/// A number the host must agree with.
let fact name value = Fact(name, value)

/// A value the design reports.
let valueOut name format = ValueOut(name, format)

/// A table the host sets and the design reads.
let tableIn name format entries storage starting = TableIn(name, format, entries, storage, starting)

/// A surface the design keeps live, taken whole when the host asks.
let surfaceOut name fields rows = SurfaceOut(name, fields, rows)

/// A design's boundary as its body meets it: each need realised by whatever
/// the mapping bound it to, reached by the need's name. From inside, a
/// converter, a ring in the host's memory and a register are the same kind
/// of thing — which is `notes/DEVICES.md`'s goal seen from the design's side.
type Boundary =
    { /// The rows a `StreamIn` need delivers.
      streamIn: string -> Stream<Expr list>
      /// Hand a `StreamOut` need its rows. Call once a need.
      streamOut: string -> Stream<Expr list> -> unit
      /// What a `ValueIn` need holds: a register, or its starting value baked
      /// in where nothing can write it.
      valueIn: string -> Expr
      /// A `PulseIn` need as the design sees it: high for the one cycle after
      /// the host fires it, or never where nothing can.
      pulseIn: string -> Expr
      /// Report a `ValueOut` need.
      valueOut: string -> Expr -> unit
      /// A `TableIn` need's read port: hand it the index, once.
      tableIn: string -> Expr -> HostArrayPort
      /// Hand a `SurfaceOut` need its rows — the live registers, one list a
      /// row, each the row's fields. Call once a need.
      surfaceOut: string -> Expr list list -> unit }

/// What a board's clock makes of a design's streams: the rate its converter
/// frames at, and the fabric cycles each beat gets.
type Clocking =
    { rate: float
      cyclesPerBeat: int }

/// What a board makes of a design before it is built: its clocking, and
/// the part it is for — whose facts a design that scales reads its size
/// from. A size the application states instead is a value in its code,
/// beside the board (`lifeAt 32 32` in place of `life`), not a word in the
/// mapping: the board says what fits, the application says what it wants.
type Fit internal (clocking: Clocking, board: Board) =
    member _.clocking = clocking
    /// The board the design is being built for: what it has — LEDs, a link.
    member _.board = board
    /// Its part: what it holds.
    member _.part = board.part

type Design =
    { name: string
      sampleRate: float
      streams: int
      /// What the design asks of the world. The `…Of` views below read the
      /// three kinds out of it; nothing else should pattern-match the list.
      needs: Need list
      /// Beats out for one in — one, unless a unit in it answers several.
      answers: int
      /// The design itself, given its boundary.
      body: Boundary -> unit
      /// A design whose needs depend on the board — a starting coefficient
      /// computed from the rate its streams arrive at, a world sized from
      /// the part's cells — is only known once it meets one. `boardTop`
      /// hands it the fit first, and builds what comes back. `None` for a
      /// design that is the same on every board.
      atClocking: (Fit -> Design) option }

/// The body of a design that is one instance with one stream through it —
/// every drawn design, and the typed ones written before a boundary could
/// hold more than a pair. The instance is `drawn`, because `design` is a
/// Verilog reserved word.
let rigged (needs: Need list) (rig: string -> Rig) : Boundary -> unit =
    let first pick =
        match List.tryPick pick needs with
        | Some name -> name
        | None -> failwith "a rigged design has one stream in and one stream out"

    let inName = first (function StreamIn(name, _) -> Some name | _ -> None)
    let outName = first (function StreamOut(name, _) -> Some name | _ -> None)

    fun boundary ->
        let r = rig "drawn"

        for name, port in r.ports do
            boundary.valueIn name ==> port

        for name, port in r.readbacks do
            boundary.valueOut name port

        boundary.streamIn inName |> r.through |> boundary.streamOut outName

/// A stream the design takes, as its body sees it: the stream, and what the
/// board's clock made of it. `rate` is the one a coefficient is designed at —
/// it is the rate the binding delivers, never one the design asserts.
type Incoming<'p> =
    { stream: Stream<'p>
      rate: float
      /// Fabric cycles a beat — what a folded engine checks its schedule
      /// against.
      cyclesPerBeat: int }

/// A surface the host sends a row at a time, as the design sees it: the
/// rows as they last landed, top row first, and the one cycle the last of
/// them did — when the design takes the surface whole. Rows are never
/// refused: one that lands while the design is busy lands all the same.
type Landing<'p> =
    { rows: 'p list
      landed: Expr }

/// What a design declares its boundary with. Each call records a need and
/// hands back its handle; the design's own record of handles is then its io,
/// and its body reads `aid.attack` and writes `processed |> aid.earphones`
/// with no name in sight.
///
/// **The factory that calls these runs twice** — once to learn the needs,
/// once inside the top to hand out the real handles — the same re-runnable
/// factory that types a module's io. So it declares and does nothing else:
/// the handles of the first run are stand-ins, and a stream touched there is
/// an error. What it *may* read on either run is the clocking, which is the
/// point — a starting value derived from the rate is right on every board.
type Needs internal (fit: Fit, boundary: Boundary option) =
    let declared = ResizeArray<Need>()

    member internal _.Declared = List.ofSeq declared

    /// The rate the board's clock makes of this design's streams.
    member _.clocking = fit.clocking

    /// The part the design is being built for: its facts size what scales.
    member _.part = fit.part

    /// Rows in, typed by their layout.
    member _.streamIn(name: string, layout: Layout<'p>) : Incoming<'p> =
        declared.Add(StreamIn(name, layout.fields))

        let stream =
            match boundary with
            | Some b -> b.streamIn name |> streamMapTo layout layout.unpack
            | None -> Unchecked.defaultof<Stream<'p>>

        { stream = stream
          rate = fit.clocking.rate
          cyclesPerBeat = fit.clocking.cyclesPerBeat }

    /// Rows out: hand it the stream, once.
    member _.streamOut(name: string, layout: Layout<'p>) : Stream<'p> -> unit =
        declared.Add(StreamOut(name, layout.fields))

        match boundary with
        | Some b -> fun s -> s |> streamMapTo (layoutOfList layout.fields) layout.pack |> b.streamOut name
        | None -> fun _ -> failwith $"'{name}': a needs factory declares its streams, and does not drive them"

    /// A value the host sets, starting at `starting`.
    member _.valueIn(name: string, format: NumberFormat, ?starting: uint64) : Expr =
        declared.Add(ValueIn(name, format, defaultArg starting 0UL))

        match boundary with
        | Some b -> b.valueIn name
        | None -> lit 0UL format.totalWidth

    /// An action the host fires: high for one cycle when it does.
    member _.pulseIn(name: string) : Expr =
        declared.Add(PulseIn name)

        match boundary with
        | Some b -> b.pulseIn name
        | None -> lit 0UL 1

    /// A number the host must agree with, stated once here and read from the
    /// seam there.
    member _.fact(name: string, value: uint64) : unit = declared.Add(Fact(name, value))

    /// A value the design reports: hand it the value, once.
    member _.valueOut(name: string, format: NumberFormat) : Expr -> unit =
        declared.Add(ValueOut(name, format))

        match boundary with
        | Some b -> b.valueOut name
        | None -> fun _ -> failwith $"'{name}': a needs factory declares what it reports, and does not report it"

    /// A table the host sets and the design reads: hand it the index, once.
    member _.tableIn(name: string, format: NumberFormat, entries: int, storage: RamStyle, ?starting: uint64 list) : Expr -> HostArrayPort =
        declared.Add(TableIn(name, format, entries, storage, defaultArg starting []))

        match boundary with
        | Some b -> b.tableIn name
        | None -> fun _ -> failwith $"'{name}': a needs factory declares its tables, and does not read them"

    /// A surface the design keeps live — `rows` rows of the layout, taken
    /// whole when the host asks: hand it the rows, once.
    member _.surfaceOut(name: string, layout: Layout<'p>, rows: int) : 'p list -> unit =
        declared.Add(SurfaceOut(name, layout.fields, rows))

        match boundary with
        | Some b -> fun surface -> surface |> List.map layout.pack |> b.surfaceOut name
        | None -> fun _ -> failwith $"'{name}': a needs factory declares its surfaces, and does not hand them over"

    /// A surface the host sends a row at a time, top row first — `rows` rows
    /// of the layout — and the design takes whole once the last has landed:
    /// `surfaceOut`'s dual. On the wire it is rows in, so any carrier that
    /// delivers a stream carries it; the landing is the design's side. Rows
    /// shift in at the bottom, so the first to arrive is at the top when the
    /// last arrives, and no row is ever refused.
    member this.surfaceIn(name: string, layout: Layout<'p>, rows: int) : Landing<'p> =
        if rows < 1 then
            failwith $"'{name}': a surface has at least one row, got %d{rows}"

        let incoming = this.streamIn(name, layout)

        match boundary with
        | Some _ ->
            let stream = incoming.stream
            1UL ==> stream.ready

            let staging =
                [ for y in 0 .. rows - 1 -> [ for field in layout.fields -> reg $"{name}_{y}_{fst field}" (fieldWidth field) ] ]

            let arriving = layout.pack stream.payload
            let count = counter $"{name}_landing" rows stream.valid

            If stream.valid (fun () ->
                for below, above in List.pairwise staging do
                    for b, a in List.zip below above do
                        a ==> b

                for last, word in List.zip (List.last staging) arriving do
                    word ==> last)

            { rows = staging |> List.map layout.unpack
              landed = count.wrap }
        | None -> { rows = []; landed = lit 0UL 1 }

/// The converter's rate when nothing says otherwise: a board frames at the
/// nearest rate its clock divides to from here — 46 875 Hz at 24 MHz,
/// 48 828.125 Hz at 100 MHz.
let nominalRate = 48_000.0

/// A design as its boundary and its body: `needs` declares the boundary and
/// returns the handles, `body` is the design given them. Nothing in it names
/// a pin, a bus or a clock — the board says those, and the design takes the
/// rate it is handed.
let design (name: string) (needs: Needs -> 'io) (body: 'io -> unit) : Design =
    let atClocking (fit: Fit) =
        let collecting = Needs(fit, None)
        needs collecting |> ignore

        { name = name
          sampleRate = fit.clocking.rate
          streams = 1
          needs = collecting.Declared
          answers = 1
          body = fun boundary -> body (needs (Needs(fit, Some boundary)))
          atClocking = None }

    { name = name
      sampleRate = 0.0
      streams = 1
      needs = []
      answers = 1
      body = fun _ -> failwith $"{name}: a design takes its needs from the board it is on — build it through `boardTop`"
      atClocking = Some atClocking }

/// A design chosen once the board is known — its size from the part's
/// facts or the mapping's word, its name with the size in it. `choose` is
/// handed the fit and returns the design for it, which may itself be one
/// that waits for the fit.
let sized (name: string) (choose: Fit -> Design) : Design =
    { name = name
      sampleRate = 0.0
      streams = 1
      needs = []
      answers = 1
      body = fun _ -> failwith $"{name}: a design sized for its board is built through `boardTop`"
      atClocking =
        Some(fun fit ->
            let chosen = choose fit

            match chosen.atClocking with
            | Some resolve -> resolve fit
            | None -> chosen) }

/// What carries the design's input stream, and its output stream. Each asks
/// the path by the need's own name first, so a mapping can rebind one need
/// of a preset without restating the others.
let private carrierOfNeed (path: DataPath) (need: Need) =
    match need with
    | StreamIn(name, _) -> carrierFor path EveryStreamIn name
    | StreamOut(name, _) -> carrierFor path EveryStreamOut name
    | ValueIn(name, _, _) -> carrierFor path EveryValueIn name
    | PulseIn name -> carrierFor path EveryValueIn name
    | ValueOut(name, _) -> carrierFor path EveryValueIn name
    | TableIn(name, _, _, _, _) -> carrierFor path EveryValueIn name
    // A surface leaves the design the way its rows out do.
    | SurfaceOut(name, _, _) -> carrierFor path EveryStreamOut name
    // Nothing carries a fact; the seam states it.
    | Fact _ -> None

/// Every carrier bound to a need, in binding order.
let internal carriersOfNeed (path: DataPath) (need: Need) =
    match need with
    | StreamIn(name, _) -> carriersFor path EveryStreamIn name
    | StreamOut(name, _) -> carriersFor path EveryStreamOut name
    | ValueIn(name, _, _) -> carriersFor path EveryValueIn name
    | PulseIn name -> carriersFor path EveryValueIn name
    | ValueOut(name, _) -> carriersFor path EveryValueIn name
    | TableIn(name, _, _, _, _) -> carriersFor path EveryValueIn name
    | SurfaceOut(name, _, _) -> carriersFor path EveryStreamOut name
    | Fact _ -> []

let internal carrierOfInput (path: DataPath) (d: Design) =
    d.needs
    |> List.tryPick (function
        | StreamIn _ as n -> carrierOfNeed path n
        | _ -> None)

let internal carrierOfOutput (path: DataPath) (d: Design) =
    d.needs
    |> List.tryPick (function
        | StreamOut _
        | SurfaceOut _ as n -> carrierOfNeed path n
        | _ -> None)

/// The fields of the stream need called `name`.
let fieldsOf (d: Design) (name: string) =
    d.needs
    |> List.tryPick (function
        | StreamIn(n, fields)
        | StreamOut(n, fields) when n = name -> Some fields
        | _ -> None)
    |> Option.defaultWith (fun () -> failwith $"{d.name}: no stream called '{name}'")

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

/// The actions the host fires, in declaration order.
let pulsesOf (d: Design) =
    d.needs
    |> List.choose (function
        | PulseIn name -> Some name
        | _ -> None)

/// The numbers the host must agree with, in declaration order.
let factsOf (d: Design) =
    d.needs
    |> List.choose (function
        | Fact(name, value) -> Some(name, value)
        | _ -> None)

/// The values the design reports, in declaration order.
let telemetryOf (d: Design) =
    d.needs
    |> List.choose (function
        | ValueOut(name, format) -> Some(name, format)
        | _ -> None)

/// The tables the host sets, in declaration order.
let tablesOf (d: Design) =
    d.needs
    |> List.choose (function
        | TableIn(name, format, entries, _, _) -> Some(name, format, entries)
        | _ -> None)

/// The surfaces the design keeps live, in declaration order: name, the
/// fields of a row, and the rows.
let surfacesOf (d: Design) =
    d.needs
    |> List.choose (function
        | SurfaceOut(name, fields, rows) -> Some(name, fields, rows)
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
    | None, _ ->
        // With the oscillator on the harness, it is the converters' master
        // clock too, and the fabric-driven MCLK pins leave the boundary.
        if board.audioClockHz.IsSome then SeparateCodecsExternalMclk else SeparateCodecs

/// What a board asks of a design before it will carry it on its converter:
/// one stream, the stereo boundary on whichever stream needs the converter
/// carries, and a rate the board's clock divides into — said with the rate it
/// would land on, so a design can be made for it.
let check (board: Board) (path: DataPath) (d: Design) =
    if d.streams <> 1 then
        failwith $"{d.name}: a board carries one stream, and the design declares %d{d.streams}"

    let onPins side pick =
        d.needs
        |> List.choose (fun need ->
            match pick need with
            | Some fields when List.contains OnPins (carriersOfNeed path need) -> Some fields
            | _ -> None)
        |> function
            | [] -> ()
            | [ pins ] ->
                // A one-field input on the converter is the converter's pace
                // and nothing heard: a beat a frame, the frame's index in the
                // field — a tone with nothing listening.
                if pins <> stereo && not (side = "input" && pins.Length = 1) then
                    failwith $"{d.name}: the {board.name}'s converter needs the {side} box to be [{describePins stereo}], and it is [{describePins pins}]"
            | several -> failwith $"{d.name}: %d{several.Length} {side} streams are bound to the {board.name}'s one converter"

    onPins "input" (function StreamIn(_, fields) -> Some fields | _ -> None)
    onPins "output" (function StreamOut(_, fields) -> Some fields | _ -> None)

    let landed = boardRate board d.sampleRate

    if round landed <> round d.sampleRate then
        failwith $"{d.name}: the design is made for %g{d.sampleRate} Hz, and the {board.name}'s clock frames at %.3f{landed} Hz for it — make the design for that rate"

/// The smallest aperture an assembled map takes: 256 bytes, as every audio
/// app's. A map with a table in it that does not fit grows to the next power
/// of two.
let apertureAddrWidth = 8

/// The design's boundary on the converter: its stream in off the link, its
/// stream out onto it — forked first when the need is bound to a recorder as
/// well — and its values from wherever `values` says.
let private onPins
    (d: Design)
    (path: DataPath)
    (values: string -> Expr)
    (pulses: string -> Expr)
    (report: string -> Expr -> unit)
    (tables: string -> Expr -> HostArrayPort)
    (record: (Stream<Expr list> -> Expr -> unit) option)
    (play: (string * (Stream<Expr list> -> Stream<Expr list>)) option)
    (i2s: I2sLink)
    : Boundary =
    { streamIn =
        fun name ->
            let beats =
                match fieldsOf d name with
                | [ _, f ] ->
                    // The converter's pace with nothing heard: the frame's
                    // index on the one field, which `check` allowed.
                    let framed = reg "converter_frame" 32
                    If (i2s.input.valid &&& i2s.input.ready) (fun () -> framed + lit 1UL 32 ==> framed)
                    let index = if f.totalWidth >= 32 then pad f.totalWidth framed else slice (f.totalWidth - 1) 0 framed
                    i2s.input |> Stream.mapTo (layoutOfList [ fieldsOf d name |> List.head ]) (fun _ -> [ index ])
                | fields -> i2s.input |> Stream.mapTo (layoutOfList fields) (fun (l, r) -> [ l; r ])

            match play with
            | Some(played, into) when played = name -> into beats
            | _ -> beats
      streamOut =
        fun name produced ->
            let toConverter (s: Stream<Expr list>) =
                s |> Stream.mapTo sampleLayout (fun fields -> fields[0], fields[1]) |> i2s.send

            let need = d.needs |> List.find (function StreamOut(n, _) -> n = name | _ -> false)

            match carriersOfNeed path need, record with
            | [ OnPins ], _ -> toConverter produced
            | [ OnPins; _ ], Some sink ->
                // The converter blocks and the recorder drops. That asymmetry is the
                // whole point: an observer must not be able to change what it
                // observes, and at this boundary the two failure modes are not
                // comparable — a recording that skips a sample is a worse recording,
                // an earphone that skips one is a click.
                let fork = streamFork "record" [ blockingBranch 2; droppingBranch 16 ] produced
                toConverter fork.branches[0]
                sink fork.branches[1] fork.dropped[1]
            | [ _ ], Some sink ->
                // A stream the design sends only to be kept: the recorder still
                // drops rather than blocks, so a host that falls behind costs
                // the recording samples and never costs the design a stall.
                let fork = streamFork "record" [ droppingBranch 16 ] produced
                sink fork.branches[0] fork.dropped[0]
            | carriers, _ -> failwith $"{d.name}: nothing builds '{name}' carried by %A{carriers} on the converter top"
      valueIn = values
      pulseIn = pulses
      valueOut = report
      tableIn = tables
      surfaceOut = fun name _ -> failwith $"{d.name}: '{name}' is a surface, and the converter top carries none — bind it to the host link" }

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

/// How a row of the boundary sits in beats: one lane per field. A row no
/// wider than a beat is padded to a power of two lanes so rows tile beats
/// evenly (`rowsPerBeat` of them, `beatsPerRow` 1); a row wider than a beat
/// — an image row of sixty-four pixels is 512 bits — is padded to whole
/// beats and takes `beatsPerRow` of them (`rowsPerBeat` 1). Said as numbers
/// a host and a check can both derive.
type RowShape =
    { lanesPerRow: int
      rowsPerBeat: int
      beatsPerRow: int
      rowsPerBurst: int
      bytesPerRow: int }

/// The shape a boundary's rows take in beats of `beatWidth` bits, or why they
/// cannot.
let rowShape (beatWidth: int) (pins: (string * NumberFormat) list) : RowShape =
    let lanesPerBeat = beatWidth / laneWidth
    let lanes = pins |> List.sumBy (fun (_, f) -> lanesOf f)

    if lanes < 1 then
        failwith $"a row of no fields has no shape in a %d{beatWidth}-bit beat"

    if lanes <= lanesPerBeat then
        let lanesPerRow = 1 <<< ceilLog2 lanes
        let rowsPerBeat = lanesPerBeat / lanesPerRow

        { lanesPerRow = lanesPerRow
          rowsPerBeat = rowsPerBeat
          beatsPerRow = 1
          rowsPerBurst = rowsPerBeat * beatsPerBurst
          bytesPerRow = lanesPerRow * laneWidth / 8 }
    else
        let beatsPerRow = (lanes + lanesPerBeat - 1) / lanesPerBeat

        if beatsPerBurst % beatsPerRow <> 0 then
            failwith
                $"a row of %d{lanes} lanes takes %d{beatsPerRow} beats, which does not divide the %d{beatsPerBurst} a burst holds — the host-memory path is not built for that"

        { lanesPerRow = beatsPerRow * lanesPerBeat
          rowsPerBeat = 1
          beatsPerRow = beatsPerRow
          rowsPerBurst = beatsPerBurst / beatsPerRow
          bytesPerRow = beatsPerRow * beatWidth / 8 }

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

/// The registers a **playing** binding adds — a stream in bound to the
/// converter and to the host link both, the host's samples taking the
/// converter's place while armed: the switch, what the host paces by, and
/// what it checks after.
type PlayRegs =
    { /// 1: the need's beats carry the host's samples; 0: the converter's.
      arm: RegEntry
      /// Words waiting in the buffer — what a host tops up against.
      fill: RegEntry
      /// Words the host sent that did not land: the buffer was full, or the
      /// frame's checksum did not match.
      dropped: RegEntry
      /// Beats taken while armed with nothing waiting: silence went out.
      starved: RegEntry }

/// The registers a **counting** binding adds where no batch contract is
/// there to count for it — beats minted in fabric on a board with no host
/// memory, the rows they produce going out over the link: how many, the
/// go, and whether it is still going.
type CountRegs =
    { /// Beats to mint: `0 …` this less one.
      total: RegEntry
      /// Pulse: start minting. The design's rows then follow on the link.
      start: RegEntry
      /// 1 from the start until the last row the count produces has left.
      busy: RegEntry }

/// The registers a **surface** binding adds — a surface taken whole and
/// sent as frames on the link: the pull, and what it managed.
type SnapshotRegs =
    /// Frames on the host link: the pull latches the surface and sends it a
    /// row at a time; `sent` counts the words; `missed` the pulls that
    /// arrived while a snapshot was still leaving — ignored, and counted, so
    /// a host pulling faster than the link carries learns it here rather
    /// than from a torn picture.
    | SnapshotToFrames of pull: RegEntry * sent: RegEntry * missed: RegEntry
    /// Slots in the host's memory — the keep-latest triple buffer
    /// (`snapshotSource` + `streamConflate3`): `base` is where the three
    /// slots start (and the arm: nothing is written while it is zero),
    /// `capture` asks for the freshest complete snapshot and `release` hands
    /// its slot back, `ready`/`slot` say which slot the host holds,
    /// `overrun` counts captures that landed while one was held, and `irq`
    /// pulses on every grant.
    | SnapshotToMemory of baseReg: RegEntry * capture: RegEntry * release: RegEntry * ready: RegEntry * overrun: RegEntry * slot: RegEntry * irq: RegEntry

/// What the bindings contributed to the register map, with the design's own
/// entries and the map itself.
type private BindingRegs =
    { batchRegs: BatchRegs option
      recorderRegs: RecordRegs option
      playerRegs: PlayRegs option
      counterRegs: CountRegs option
      snapshotRegs: SnapshotRegs option
      designEntries: (string * RegEntry) list
      regMap: RegMap }

/// The output need a converter top records, and what carries the recording:
/// a need bound twice — the converter and a recorder, the fork case — or a
/// need bound to a recorder alone, a stream the design sends only to be kept.
/// `None` when nothing is recorded. One recorder a top is what is built.
let internal recordingCarrier (path: DataPath) (d: Design) =
    let recorders =
        d.needs
        |> List.choose (function
            | StreamOut(name, _) as n ->
                match carriersOfNeed path n with
                | [ OnPins; second ] -> Some(name, second)
                | [ (InHostRows | InLinkFrames) as only ] -> Some(name, only)
                | _ -> None
            | _ -> None)

    match recorders with
    | [] -> None
    | [ one ] -> Some one
    | several ->
        failwith $"""{d.name}: {several |> List.map fst |> String.concat " and "} are each bound to a recorder, and a top builds one"""

/// The input need a converter top plays into from the host link: a stream
/// in bound to the converter and to link frames both, the converter still
/// pacing the beats. `None` when nothing is played. One a top, as one
/// recorder.
let internal playingCarrier (path: DataPath) (d: Design) =
    let players =
        d.needs
        |> List.choose (function
            | StreamIn(name, _) as n ->
                match carriersOfNeed path n with
                | [ OnPins; InLinkFrames ] -> Some name
                | carriers when List.contains InLinkFrames carriers ->
                    failwith
                        $"{d.name}: '{name}' is played over the host link, which paces it by the converter — bind it [OnPins, InLinkFrames]"
                | _ -> None
            | _ -> None)

    match players with
    | [] -> None
    | [ one ] -> Some one
    | several -> failwith $"""{d.name}: {several |> String.concat " and "} are each played over the host link, and a top plays one"""

/// A register a binding contributes, named for the need it serves and the
/// carrier serving it — `outMemArm`, `recordLinkSent` — so it can never
/// collide with a design's own values, and the seam says whose each one is
/// (`notes/DEVICES.md` §10i).
let private bindingRegister (need: string) (carrier: string) (what: string) = need + carrier + what

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
    | InLinkFrames
    | OnPin _ -> false

/// The host's register map, **assembled** from what the design's needs and
/// their bindings require rather than declared: the sink's contract first
/// where a binding wants one, then the design's own values and reports in
/// declaration order, then what a recording binding needs, then the design's
/// tables. Declaration order is therefore register order, and so part of the
/// seam — which is why the map is pinned.
let private registersFor
    (path: DataPath)
    (recording: (string * Carrier) option)
    (playing: string option)
    (counting: string option)
    (snapshotting: (string * Carrier) option)
    (d: Design)
    : BindingRegs =
    // A recorder brings its own registers, and never the batch contract: a
    // stream bound to the host's memory only to be kept is not a batch.
    let recorded = recording |> Option.map fst

    let wantsContract =
        d.needs
        |> List.exists (fun n ->
            match n with
            | StreamOut(name, _) when Some name = recorded -> false
            | _ ->
                carrierOfNeed path n
                |> Option.map contributesBatchContract
                |> Option.defaultValue false)

    let (batch, recorder, player, counter, snapshot, controls), map =
        buildRegMapAtLeast apertureAddrWidth (fun r ->
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

            // The design's own words first, values and reports in the order
            // they were declared — telemetry read-only by construction, since
            // the design drives it and a writable register would only invite
            // someone to try. A report bound to nothing but a pin takes none.
            let scalars =
                [ for need in d.needs do
                      match need with
                      | ValueIn(name, f, starting) -> yield name, r.RwReg(name, f.totalWidth, starting)
                      | PulseIn name -> yield name, r.Word(fun w -> w.Pulse name)
                      | ValueOut(name, f) when List.contains InRegisterMap (carriersOfNeed path need) -> yield name, r.RoField(name, f.totalWidth)
                      | _ -> () ]

            // A recording binding contributes too — **after** the design's
            // own words, so those sit at the same offsets on every board and
            // one driver's layout serves them all; only what the carrier
            // needs moves with the carrier (`notes/DEVICES.md` §10i).
            // What the recording binding contributes depends on its carrier:
            // a ring in memory has to be described, frames on a link do not.
            let recorder =
                match recording with
                | Some(need, InHostRows) ->
                    let named = bindingRegister need "Mem"
                    // The host writes the arm bit, so it is a register and
                    // not a field the fabric drives.
                    Some(
                        RecordToMemory(
                            r.RwReg(named "Arm", 1, 0UL),
                            r.RwReg(named "Addr", 32, 0UL),
                            r.RwReg(named "Beats", 32, 0UL),
                            r.RoField(named "Written", 32),
                            r.RoField(named "Dropped", 32)
                        )
                    )
                | Some(need, InLinkFrames) ->
                    let named = bindingRegister need "Link"
                    Some(RecordToFrames(r.RwReg(named "Arm", 1, 0UL), r.RoField(named "Sent", 32), r.RoField(named "Dropped", 32)))
                | _ -> None

            // A playing binding contributes the same way, after the recorder's.
            let player =
                playing
                |> Option.map (fun need ->
                    let named = bindingRegister need "Link"

                    { arm = r.RwReg(named "Arm", 1, 0UL)
                      fill = r.RoField(named "Fill", 32)
                      dropped = r.RoField(named "Dropped", 32)
                      starved = r.RoField(named "Starved", 32) })

            // A counting binding contributes only where nothing else counts:
            // on the host-memory path the batch contract's `frameCount` and
            // `start` are the count, and these would be a second one.
            let counter =
                counting
                |> Option.map (fun need ->
                    let named = bindingRegister need "Count"

                    { total = r.RwReg(named "Total", 32, 0UL)
                      start = r.Word(fun w -> w.Pulse(named "Start"))
                      busy = r.Word(fun w -> w.Field(named "Busy", 1)) })

            // A surface's binding, the same way: what it contributes depends
            // on its carrier, as a recorder's does.
            let snapshot =
                match snapshotting with
                | Some(need, InLinkFrames) ->
                    let named = bindingRegister need "Link"
                    Some(SnapshotToFrames(r.Word(fun w -> w.Pulse(named "Pull")), r.RoField(named "Sent", 32), r.RoField(named "Missed", 32)))
                | Some(need, InHostRows) ->
                    let named = bindingRegister need "Mem"
                    let ready, overrun, slot = r.Word(fun w -> w.Field(named "Ready", 1), w.Field(named "Overrun", 8), w.Field(named "Slot", 2))

                    Some(
                        SnapshotToMemory(
                            r.RwReg(named "Base", 32, 0UL),
                            r.Word(fun w -> w.Pulse(named "Capture")),
                            r.Word(fun w -> w.Pulse(named "Release")),
                            ready,
                            overrun,
                            slot,
                            r.Word(fun w -> w.W1c(named "Irq"))
                        )
                    )
                | Some(need, other) -> failwith $"{d.name}: '{need}' is a surface, and nothing carries one as {other}"
                | None -> None

            // Tables last: each is aligned to its own size, so it lands at
            // the same offset whatever the scalars before it came to, and the
            // scalars after it would otherwise start past it and double the
            // aperture.
            let tables =
                d.needs
                |> List.choose (function
                    | TableIn(name, f, entries, storage, starting) ->
                        if f.totalWidth > 32 then
                            failwith $"{d.name}: the table '{name}' holds %d{f.totalWidth}-bit words, and a register map's words are 32"

                        match starting with
                        | [] -> Some(name, r.RwArray(name, entries, storage))
                        | words -> Some(name, r.RwArray(name, entries, storage, words))
                    | _ -> None)

            batch, recorder, player, counter, snapshot, scalars @ tables)

    { batchRegs = batch
      recorderRegs = recorder
      playerRegs = player
      counterRegs = counter
      snapshotRegs = snapshot
      designEntries = controls
      regMap = map }

/// `e` times a constant known at elaboration: shifts and adds, so it need
/// not be a power of two — which `answers` stops being as soon as a chunk is
/// sized to divide its row rather than to be a round number.
let private timesConstant (k: int) (e: Expr) =
    let shifted i =
        if i = 0 then e else cat (slice (31 - i) 0 e) (lit 0UL i)

    [ for i in 0..31 do
          if (k >>> i) &&& 1 = 1 then
              yield shifted i ]
    |> List.reduce (+)

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

    let fields, rowValid =
        if shape.beatsPerRow > 1 then
            // A row wider than a beat: every beat but the last is held as it
            // arrives, and the row is offered with its last beat, which
            // leaves with the row — the handshake does the storing.
            let phaseWidth = ceilLog2 shape.beatsPerRow
            let phase = reg "unpack_phase" phaseWidth
            let last = eq phase (lit (uint64 (shape.beatsPerRow - 1)) phaseWidth)
            let held = [ for k in 0 .. shape.beatsPerRow - 2 -> reg $"unpack_held%d{k}" beatWidth ]
            let take = beats.valid &&& (bnot last ||| outReady)
            (bnot last ||| outReady) ==> beats.ready

            If take (fun () ->
                mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase

                held
                |> List.iteri (fun k h -> If (eq phase (lit (uint64 k) phaseWidth)) (fun () -> beat ==> h)))

            // The row, the first beat at the low end.
            let row = wire "unpack_row" (shape.lanesPerRow * laneWidth)
            ((held @ [ beat ]) |> List.rev |> List.reduce cat) ==> row

            [ for (start, _), (_, f) in List.zip spans pins -> slice (start * laneWidth + f.totalWidth - 1) (start * laneWidth) row ],
            beats.valid &&& last
        elif shape.rowsPerBeat = 1 then
            fire ==> beats.ready
            [ for (start, _), (_, f) in List.zip spans pins -> laneAt start f.totalWidth ], beats.valid
        else
            let phaseWidth = ceilLog2 shape.rowsPerBeat
            let phase = reg "unpack_phase" phaseWidth
            let last = eq phase (lit (uint64 (shape.rowsPerBeat - 1)) phaseWidth)
            (fire &&& last) ==> beats.ready

            If fire (fun () -> mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase)

            [ for (start, _), (_, f) in List.zip spans pins ->
                  selectIndexed phase [ for row in 0 .. shape.rowsPerBeat - 1 -> laneAt (row * shape.lanesPerRow + start) f.totalWidth ] ],
            beats.valid

    { payload = fields
      valid = rowValid
      ready = outReady
      layout = layoutOfList pins }

/// Rows become a beat. All but the last row of a beat are held in registers
/// — unavoidable, because the producer has already been told its row was
/// taken.
let private rowsToBeats (shape: RowShape) (pins: (string * NumberFormat) list) (beatWidth: int) (rows: Stream<Expr list>) : Stream<Expr> =
    // A field fills its lanes, sign-extended to the last. Sign extension
    // replicates a named bit, so a field that arrives computed — or is the
    // input's own slice, as the identity's is — is named first; one that
    // already is stays as it is.
    let lane (name: string) (f: NumberFormat) (e: Expr) =
        let w = lanesOf f * laneWidth

        let e =
            if f.signed && not (isNamed e) then
                let named = wire $"pack_field_{name}" f.totalWidth
                e ==> named
                named
            else
                e

        if f.signed then asUInt (pad w (asSInt e)) else pad w e

    let lanes = List.map2 (fun (name, f) e -> lane name f e) pins rows.payload

    let rowBits =
        let padding = shape.lanesPerRow - (pins |> List.sumBy (fun (_, f) -> lanesOf f))
        let padded = lanes @ List.replicate padding (lit 0UL laneWidth)
        // Lane 0 at the low end.
        padded |> List.rev |> List.reduce cat

    let rowWidth = shape.lanesPerRow * laneWidth
    let outValid = wireBit "pack_out_valid"
    let outReady = wireBit "pack_out_ready"
    registerStreamReady outReady

    if shape.beatsPerRow > 1 then
        // A row wider than a beat leaves as several, the first from the low
        // end; the row is held by its producer until the last has gone.
        let row = wire "pack_row" rowWidth
        rowBits ==> row
        let phaseWidth = ceilLog2 shape.beatsPerRow
        let phase = reg "pack_phase" phaseWidth
        let last = eq phase (lit (uint64 (shape.beatsPerRow - 1)) phaseWidth)
        let fire = rows.valid &&& outReady
        If fire (fun () -> mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase)
        (fire &&& last) ==> rows.ready
        rows.valid ==> outValid

        { payload = selectIndexed phase [ for k in 0 .. shape.beatsPerRow - 1 -> slice ((k + 1) * beatWidth - 1) (k * beatWidth) row ]
          valid = outValid
          ready = outReady
          layout = layout1 ("data", beatWidth) }
    elif shape.rowsPerBeat = 1 then
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
      target: Target
      /// Rust lines the design adds to its seam after the register map's —
      /// its own constants (a grid's size, a frame's dimensions, a cluster's
      /// shape), so the host compiles against the numbers the fabric was
      /// built with rather than a copy of them. Empty for a design whose
      /// register map is the whole of what the host needs.
      seamExtras: string list }

/// The emitter's target for a part.
let targetOf (part: Part) =
    match part.family with
    | UltraScalePlus -> Xilinx
    | Family.Ice40 -> Ice40
    | Family.Ecp5 -> Target.Ecp5
    | Family.Max10 -> Target.Max10

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
    (armed: Expr)
    (sentReg: RegEntry)
    (droppedReg: RegEntry)
    (pins: (string * NumberFormat) list)
    (rows: Stream<Expr list>)
    (dropped: Expr)
    : Stream<Expr> =
    // Each field, sign-extended into its word the way the seam will read it.
    // The field is named first because it arrives computed — out of the
    // fork's buffer — and sign extension replicates a named bit. A field
    // wider than a lane goes as `lanesOf` words, low word first, the top one
    // padded — the order the host-memory path packs it in a beat.
    let words =
        List.zip pins rows.payload
        |> List.collect (fun ((name: string, f: NumberFormat), e) ->
            let w = wire $"record_field_{name}" f.totalWidth
            e ==> w

            if f.totalWidth <= laneWidth then
                [ if f.signed then asUInt (pad laneWidth (asSInt w)) else pad laneWidth w ]
            else
                [ for k in 0 .. lanesOf f - 1 ->
                      let part = slice (min (laneWidth * (k + 1)) f.totalWidth - 1) (laneWidth * k) w
                      if width part = laneWidth then part else pad laneWidth part ])

    let n = words.Length

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

/// Words a row of these fields takes on the link.
let linkWordsOf (pins: (string * NumberFormat) list) =
    pins |> List.sumBy (fun (_, f) -> lanesOf f)

/// A surface taken whole when the host pulls, then sent a row at a time as
/// frames on the link. The latch is one cycle, so the copy is coherent
/// however fast the design runs; the rows then leave at the link's pace
/// while the design goes on, and a pull that lands mid-copy is counted and
/// ignored rather than tearing the one in flight. What the design did
/// between two pulls is never seen — the conflation the snapshot path has
/// always meant, with the host's pull as the pace.
let private snapshotFrames
    (regs: SlaveRegs)
    (pullReg: RegEntry, sentReg: RegEntry, missedReg: RegEntry)
    (pins: (string * NumberFormat) list)
    (surface: Expr list list)
    : Stream<Expr> =
    let rows = surface.Length

    if rows < 2 then
        failwith $"a surface over the link has at least two rows, and this one has %d{rows}"

    for row in surface do
        if row.Length <> pins.Length then
            failwith $"a surface's row has the %d{pins.Length} fields it declared, and one has %d{row.Length}"

    let copying = regBit "snapshot_copying"
    let indexWidth = ceilLog2 rows
    let index = reg "snapshot_index" indexWidth

    let shadows =
        [ for r in 0 .. rows - 1 -> [ for name, f in pins -> reg $"snapshot_%d{r}_{name}" f.totalWidth ] ]

    let pull = regs.pulse pullReg
    let outReady = wireBit "snapshot_ready"
    registerStreamReady outReady
    let last = eq index (lit (uint64 (rows - 1)) indexWidth)
    let missed = reg "snapshot_missed" 32

    ifElse
        [ (bnot copying,
           fun () ->
               If pull (fun () ->
                   for row, shadow in List.zip surface shadows do
                       for e, sh in List.zip row shadow do
                           e ==> sh

                   lit 1UL 1 ==> copying
                   lit 0UL indexWidth ==> index))
          (otherwise,
           fun () ->
               If pull (fun () -> missed + lit 1UL 32 ==> missed)

               If outReady (fun () ->
                   ifElse
                       [ (last,
                          fun () ->
                              lit 0UL 1 ==> copying
                              lit 0UL indexWidth ==> index)
                         (otherwise, fun () -> index + lit 1UL indexWidth ==> index) ])) ]

    let fields =
        [ for k, (name, f) in List.indexed pins ->
              let w = wire $"snapshot_row_{name}" f.totalWidth
              selectIndexed index [ for shadow in shadows -> List.item k shadow ] ==> w
              w ]

    let rowStream: Stream<Expr list> =
        { payload = fields
          valid = copying
          ready = outReady
          layout = layoutOfList pins }

    recordFrames regs (lit 1UL 1) sentReg missedReg pins rowStream missed

/// Words a played stream buffers between the link and the beats: two 16-bit
/// samples a word, so 170 ms at 48 kHz. The slack is against the host, not
/// the link — a register exchange the player waits behind, a scheduler
/// pause — and 1024 words (42 ms) measured as a fifth of the beats starved
/// on a Linux host (2026-10-12). Eight block RAMs on an ECP5.
let playBufferWords = 4096

/// The buffer a part affords: `playBufferWords` where the block RAM is
/// there, else the largest power of two that takes no more than a quarter
/// of it — 512 words on an HX1K (16 blocks of 4 kbit, where 4 096 words
/// would be 24) and on a UP5K, where 4 096 words were measured taking 24 of
/// its 30 blocks (2026-11-07). The slack is against the host's pauses, and
/// a link slow enough for a small part is a host that cannot outrun it.
let playBufferWordsFor (part: Part) =
    let quarter = part.facts.blockRamBits / 4 / laneWidth
    let mutable words = 1
    while words * 2 <= quarter do words <- words * 2
    min playBufferWords (max 64 words)

/// The width of a sample a played frame carries, two a word. Stereo 24-bit
/// at a converter's rate does not fit a 3 Mbaud link; 16-bit mono does with
/// the register traffic beside it, so that is what the link plays — into
/// every field of the beat.
let playedSampleWidth = 16

/// The host's samples into a need's beats in place of the converter's while
/// armed. The converter keeps pacing: a beat still leaves when it did, and
/// only its payload is switched, so the design cannot tell. Words are taken
/// whether or not armed, so a host primes the buffer before it arms; a beat
/// taken armed with the buffer empty carries silence and is counted.
let private playInto
    (part: Part)
    (regs: SlaveRegs)
    (p: PlayRegs)
    (played: SerialPlayed)
    (pins: (string * NumberFormat) list)
    (beats: Stream<Expr list>)
    : Stream<Expr list> =
    for name, f in pins do
        if f.totalWidth < playedSampleWidth then
            failwith $"playing over the link: '{name}' is %d{f.totalWidth} bits and a played sample is %d{playedSampleWidth}"

    let armed = regs.value p.arm

    let incoming: Stream<Expr> =
        { payload = played.words.payload
          valid = played.words.valid
          ready = wireBit "play_in_ready"
          layout = layout1 ("word", 32) }

    let buffered = streamFifo "play_buffer" (playBufferWordsFor part) incoming
    let landed = incoming.valid &&& incoming.ready
    let lost = incoming.valid &&& bnot incoming.ready

    let taking = beats.valid &&& beats.ready &&& armed
    // A word holds two samples, the low half first; the word leaves with
    // its second. Disarmed, the next word starts at its first again.
    let second = regBit "play_second_half"
    If (taking &&& buffered.valid) (fun () -> bnot second ==> second)
    If (bnot armed) (fun () -> lit 0UL 1 ==> second)
    (taking &&& second) ==> buffered.ready
    let drained = buffered.valid &&& buffered.ready

    let fill = reg "play_fill" 32

    ifElse
        [ (landed &&& bnot drained, fun () -> fill + lit 1UL 32 ==> fill)
          (drained &&& bnot landed, fun () -> fill - lit 1UL 32 ==> fill) ]

    let dropped = reg "play_dropped" 32
    If (lost ||| played.refused) (fun () -> dropped + lit 1UL 32 ==> dropped)
    let starved = reg "play_starved" 32
    If (taking &&& bnot buffered.valid) (fun () -> starved + lit 1UL 32 ==> starved)
    regs.drive p.fill fill
    regs.drive p.dropped dropped
    regs.drive p.starved starved

    let word = wire "play_word" 32
    buffered.payload ==> word
    let sample = wire "play_sample" playedSampleWidth
    mux second (slice 31 playedSampleWidth word) (slice (playedSampleWidth - 1) 0 word) ==> sample
    let heard = wire "play_heard" playedSampleWidth
    mux buffered.valid sample (lit 0UL playedSampleWidth) ==> heard

    // Widened by shift, so full scale stays full scale.
    let widened (f: NumberFormat) =
        if f.totalWidth = playedSampleWidth then heard else cat heard (lit 0UL (f.totalWidth - playedSampleWidth))

    // Each field as a declared signal, since what the design does with its
    // input — a slice, a sign — needs one.
    { beats with
        payload =
            List.zip pins beats.payload
            |> List.map (fun ((name, f), e) ->
                let field = wire $"play_field_{name}" f.totalWidth
                mux armed (widened f) e ==> field
                field) }

/// The host's words as a design's rows, for a top with no converter: the
/// mirror of `recordFrames` — one field a word, in the row's order, the
/// word's low bits the field's — buffered as a played stream is, and paced
/// by nothing but the design taking rows. A word that arrives with the
/// buffer full is dropped and counted; `fill` is what the host paces by.
/// Nothing is starved here: a design with no row waiting simply waits.
let private playRows
    (part: Part)
    (regs: SlaveRegs)
    (p: PlayRegs)
    (played: SerialPlayed)
    (pins: (string * NumberFormat) list)
    : Stream<Expr list> =
    let armed = regs.value p.arm

    let incoming: Stream<Expr> =
        { payload = played.words.payload
          valid = played.words.valid
          ready = wireBit "play_in_ready"
          layout = layout1 ("word", 32) }

    let buffered = streamFifo "play_buffer" (playBufferWordsFor part) incoming
    let landed = incoming.valid &&& incoming.ready
    let lost = incoming.valid &&& bnot incoming.ready

    // A field wider than a lane arrives as `lanesOf` words, low word
    // first — the mirror of how the recorder sends one.
    let n = linkWordsOf pins
    let outReady = wireBit "play_row_ready"
    registerStreamReady outReady
    let word = wire "play_word" 32
    buffered.payload ==> word

    // All but a row's last word are held as they arrive; the row is offered
    // while its last word is at the buffer's head, and that word leaves with
    // the row — the handshake does the storing, as it does everywhere here.
    let sources, takeWord, rowValid =
        if n = 1 then
            [ word ], (outReady &&& armed), (buffered.valid &&& armed)
        else
            let phaseWidth = ceilLog2 n
            let phase = reg "play_phase" phaseWidth
            let last = eq phase (lit (uint64 (n - 1)) phaseWidth)
            let held = [ for k in 0 .. n - 2 -> reg $"play_held%d{k}" 32 ]
            let fire = buffered.valid &&& armed &&& (bnot last ||| outReady)

            If fire (fun () ->
                mux last (lit 0UL phaseWidth) (phase + lit 1UL phaseWidth) ==> phase

                held
                |> List.iteri (fun k h -> If (eq phase (lit (uint64 k) phaseWidth)) (fun () -> word ==> h)))

            // Disarming rewinds the row, so the next word starts one.
            If (bnot armed) (fun () -> lit 0UL phaseWidth ==> phase)

            held @ [ word ], fire, (buffered.valid &&& armed &&& last)

    takeWord ==> buffered.ready
    let drained = buffered.valid &&& buffered.ready

    let fill = reg "play_fill" 32

    ifElse
        [ (landed &&& bnot drained, fun () -> fill + lit 1UL 32 ==> fill)
          (drained &&& bnot landed, fun () -> fill - lit 1UL 32 ==> fill) ]

    let dropped = reg "play_dropped" 32
    If (lost ||| played.refused) (fun () -> dropped + lit 1UL 32 ==> dropped)
    regs.drive p.fill fill
    regs.drive p.dropped dropped
    regs.drive p.starved (lit 0UL 32)

    // Each field as a declared signal, since what the design does with its
    // input — a slice, a sign — needs one; a wide field's lanes are joined
    // first, the later word the higher.
    let fields =
        pins
        |> List.fold
            (fun (fields, remaining: Expr list) (name, f) ->
                let lanes, rest = List.splitAt (lanesOf f) remaining
                let field = wire $"play_field_{name}" f.totalWidth

                let joined =
                    match lanes with
                    | [ one ] -> one
                    | several ->
                        let all = wire $"play_field_{name}_lanes" (laneWidth * several.Length)
                        (several |> List.rev |> List.reduce cat) ==> all
                        all

                slice (f.totalWidth - 1) 0 joined ==> field
                fields @ [ field ], rest)
            ([], sources)
        |> fst

    { payload = fields
      valid = rowValid
      ready = outReady
      layout = layoutOfList pins }

/// The I2S link on the clock the board's converter actually runs at. With no
/// oscillator this is `mk board.fabricHz`, exactly as before. With one, the
/// whole link — clock generator, framers — elaborates inside the audio
/// domain at the oscillator's rate, and the streams cross at the boundary:
/// an `asyncFifo` each way, so the design's own logic stays wholly on the
/// fabric clock and never learns a second clock exists. Depth 8 is plenty —
/// the crossing's rate mismatch is bounded by the converter's own frame
/// pace, so the FIFOs idle near empty and never drop.
let private linkOnConverterClock (board: Board) (mk: int -> I2sLink) : I2sLink =
    match board.audioClockHz with
    | None -> mk board.fabricHz
    | Some audioHz ->
        let mutable raw = Unchecked.defaultof<I2sLink>
        let mutable crossedIn = Unchecked.defaultof<Stream<Expr * Expr>>

        withDomain audioClockDomain (fun () ->
            raw <- mk audioHz
            crossedIn <- asyncFifo defaultDomain "i2s_in_cross" 8 raw.input)

        { input = crossedIn
          send =
            fun s ->
                let toConverter = asyncFifo audioClockDomain "i2s_out_cross" 8 s
                withDomain audioClockDomain (fun () -> raw.send toConverter) }

/// Bits a PDM microphone gives per frame of the converter it shares a clock
/// with: the bit clock is the I2S bit clock, and a frame is two slots of it.
/// Stated as that ratio rather than as 64, so the decimation and the frame
/// cannot be two numbers.
let pdmBitsPerFrame = 2 * stockBitsPerSlot

/// Whether a board's harness puts PDM microphones on the design's input.
let micsOnPdm (board: Board) = (connectorFor PdmMics board).IsSome

/// The pins the converter half of a top declares: the duplex I2S bus, or —
/// when the harness carries PDM microphones — the bus's transmit side and
/// the microphones' two pins. Called from the io factory.
type private ConverterPins =
    | Duplex of I2sPins
    | PdmIn of I2sTxPins * PdmPins

let private converterPins (board: Board) (p: Ports) : ConverterPins =
    if micsOnPdm board then
        PdmIn(i2sTxPins p (pinoutOf board), pdmPins p)
    else
        Duplex(i2sPins p (pinoutOf board))

/// The link those pins make, as the design sees it: samples in, somewhere to
/// put samples out. With PDM microphones the input is the decimator's and the
/// bit clock it makes is the I2S one — the same divisor from the same rate —
/// so the microphones and the amplifiers frame together from one crystal.
let private converterLink (board: Board) (pins: ConverterPins) (rate: int) : I2sLink =
    linkOnConverterClock board (fun hz ->
        match pins with
        | Duplex pins -> i2sLink "audio" pins hz rate stockBitsPerSlot
        | PdmIn(tx, mics) ->
            let out = i2sTxLink "audio" tx hz rate stockBitsPerSlot
            let pdm = pdmLink "mics" mics hz (rate * pdmBitsPerFrame) pdmBitsPerFrame

            // The decimator declares its samples signed; the I2S receiver hands
            // out bits. The boundary deals in bits, as the design's layout says
            // what they mean — and the play link muxes the host's words against
            // these, which the IR allows only between one reading.
            let left, right = pdm.input.payload

            { input = { pdm.input with payload = (asUInt left, asUInt right) }
              send = out.sendOnly })

/// The pins a design's reported values are bound to — an LED — each one
/// bit, and each its own output port on the top.
let private pinnedOutputs (d: Design) (path: DataPath) =
    [ for need in d.needs do
          match need with
          | ValueOut(name, f) ->
              for carrier in carriersOfNeed path need do
                  match carrier with
                  | OnPin port ->
                      if f.totalWidth <> 1 then
                          failwith $"{d.name}: '{name}' is %d{f.totalWidth} bits, and a pin carries one"

                      yield name, port
                  | _ -> ()
          | _ -> () ]

let private pinOutsOf (pinned: (string * string) list) (p: Ports) =
    [ for _, port in pinned -> port, p.outPort port 1 ]

/// Whether what is on a pin lights when driven low: the board's wiring, so
/// the design's `limit` means lit whichever way the LED is wired.
let private activeLowOn (board: Board) (port: string) =
    board.connectors
    |> List.collect (fun c -> c.pins)
    |> List.tryFind (fun (p, _) -> p = port)
    |> Option.map (fun (_, pin) -> pin.activeLow)
    |> Option.defaultValue false

/// What the design reports goes to every carrier it is bound to — its
/// read-only field, a pin. With no host there is no field, and a register
/// binding reaches nothing.
let private reporterFor
    (board: Board)
    (d: Design)
    (path: DataPath)
    (entries: (string * RegEntry) list)
    (regs: SlaveRegs option)
    (outs: (string * Expr) list)
    =
    let byName = Map.ofList entries
    let outs = Map.ofList outs

    fun (name: string) (value: Expr) ->
        let need = d.needs |> List.find (function ValueOut(n, _) -> n = name | _ -> false)

        for carrier in carriersOfNeed path need do
            match carrier, regs with
            | InRegisterMap, Some regs -> regs.drive byName[name] value
            | InRegisterMap, None -> ()
            | OnPin port, _ -> (if activeLowOn board port then bnot value else value) ==> outs[port]
            | other, _ -> failwith $"{d.name}: '{name}' is a value it reports, and nothing carries one as {other}"

/// The design's values, by need name, out of the slave's registers.
let private valuesOf (entries: (string * RegEntry) list) (regs: SlaveRegs) =
    let byName = Map.ofList entries
    fun (name: string) -> regs.value byName[name]

/// The design's pulses, by need name.
let private pulsesInOf (entries: (string * RegEntry) list) (regs: SlaveRegs) =
    let byName = Map.ofList entries
    fun (name: string) -> regs.pulse byName[name]

/// The design's tables' read ports, by need name.
let private tablesInOf (entries: (string * RegEntry) list) (regs: SlaveRegs) =
    let byName = Map.ofList entries
    fun (name: string) (index: Expr) -> regs.readArray byName[name] index

/// A surface goes out whole, on a pull, and only the link top takes one.
let private refuseSurfaces (where: string) (d: Design) =
    match surfacesOf d with
    | (name, _, _) :: _ -> failwith $"{d.name}: '{name}' is a surface, and {where} — bind it to the host link"
    | [] -> ()

let private pinsTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    check board path d
    refuseSurfaces "the converter top carries none" d
    // No binding here wants a contract, so the map is exactly the design's
    // own values — which is what the pins path always had, now by assembly
    // rather than by a second builder.
    let recording = recordingCarrier path d
    let playing = playingCarrier path d
    let contributed = registersFor path recording playing None None d
    let recorder, player, entries, map = contributed.recorderRegs, contributed.playerRegs, contributed.designEntries, contributed.regMap

    // The played need's own row, as the recorder's.
    let playedFields () =
        match playing with
        | Some name -> fieldsOf d name
        | None -> []

    // The recorded need's own row — not every output's fields run together,
    // which only ever agreed while a design had one output.
    let recordedFields () =
        match recording with
        | Some(name, _) -> fieldsOf d name
        | None -> []
    let rate = int (round d.sampleRate)

    let fromRegisters (regs: SlaveRegs) = valuesOf entries regs
    let pulsesFrom (regs: SlaveRegs) = pulsesInOf entries regs
    /// With no host nothing fires: a pulse that never comes.
    let unfired (_: string) = lit 0UL 1

    let pinned = pinnedOutputs d path
    let pinOuts (p: Ports) = pinOutsOf pinned p
    let reporter (regs: SlaveRegs option) (outs: (string * Expr) list) = reporterFor board d path entries regs outs
    let toRegisters (regs: SlaveRegs) = reporter (Some regs)
    let tablesIn (regs: SlaveRegs) = tablesInOf entries regs

    let baked =
        let starting = (startingOf d) |> Map.ofList
        let formats = controlsOf d |> Map.ofList
        fun (name: string) -> lit (starting |> Map.tryFind name |> Option.defaultValue 0UL) formats[name].totalWidth

    // A table on a board with no host has nothing to write it and nothing to
    // read it back through; baking it as a ROM is sayable, and not built.
    let unwritable (name: string) (_: Expr) : HostArrayPort =
        failwith $"{d.name}: the table '{name}' needs a host to write it, and the {board.name} has none — a table baked at its starting words is not built"

    // A recorder this board cannot carry is refused rather than dropped: the
    // design would build, and the recording it was bound to would silently
    // not exist.
    match recorder, board.host with
    | Some(RecordToMemory _), (UartAt _ | NoHost) ->
        failwith $"{d.name}: its output is bound to the host's memory as well as the pins, and the {board.name}'s host has no memory the fabric can write"
    | Some(RecordToFrames _), (AxiLiteAt _ | NoHost) ->
        failwith $"{d.name}: its output is bound to frames on the host link as well as the pins, and the {board.name} has no host link to carry them"
    | _ -> ()

    match playing, board.host with
    | Some name, (AxiLiteAt _ | NoHost) ->
        failwith $"{d.name}: '{name}' is played over the host link as well as taken from the pins, and the {board.name} has no host link to play it over"
    | _ -> ()

    // What the recording binding added to the map, whichever kind it is, so
    // both host branches expose it the same way.
    let recorderEntries: (string * RegEntry) list =
        match recorder with
        | None -> []
        | Some(RecordToMemory(arm, ringAddr, ringBeats, written, dropped)) -> [ arm; ringAddr; ringBeats; written; dropped ] |> List.map (fun e -> e.name, e)
        | Some(RecordToFrames(arm, sent, dropped)) -> [ arm; sent; dropped ] |> List.map (fun e -> e.name, e)

    let playerEntries: (string * RegEntry) list =
        match player with
        | None -> []
        | Some p -> [ p.arm; p.fill; p.dropped; p.starved ] |> List.map (fun e -> e.name, e)

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
                        converterPins board p,
                        (match recorder, memory with
                         | Some _, Some m -> Some(axiWriteBusPorts p "m_axi" 32 m.width)
                         | _ -> None),
                        pinOuts p)
                    (fun (slavePorts, pins, writeBusPorts, outs) ->
                        let regs = regMapSlave slavePorts map
                        let i2s = converterLink board pins rate

                        let sink =
                            match recorder, writeBusPorts, memory with
                            | Some (RecordToMemory(arm, ringAddr, ringBeats, written, dropped)), Some busPorts, Some m ->
                                Some(recordInto regs arm ringAddr ringBeats written dropped m (axiWriteBusOf busPorts) (recordedFields ()))
                            | _ -> None

                        d.body (onPins d path (fromRegisters regs) (pulsesFrom regs) (toRegisters regs outs) (tablesIn regs) sink None i2s))

            name, top.def, recorderEntries @ playerEntries @ entries
        | UartAt baud ->
            let name = $"{d.name}Uart"

            let top =
                defModule
                    name
                    (fun p -> uartPins p "host", converterPins board p, pinOuts p)
                    (fun (uart, pins, outs) ->
                        let i2s = converterLink board pins rate

                        match recorder, player with
                        | Some(RecordToFrames _), _
                        | _, Some _ ->
                            // The recorder's words, the played words and the
                            // register map share the one wire, so the slave
                            // is built *around* them. The recorder's words
                            // are produced by the fork inside `through`,
                            // which is below this, so that stream is
                            // threaded back through a wire pair.
                            let frames =
                                match recorder with
                                | Some(RecordToFrames(arm, sent, dropped)) ->
                                    let words = wire "record_frame_word" 32
                                    let wordsValid = wireBit "record_frame_valid"
                                    let wordsReady = wireBit "record_frame_ready_back"

                                    let stream: Stream<Expr> =
                                        { payload = words
                                          valid = wordsValid
                                          ready = wordsReady
                                          layout = layout1 ("word", 32) }

                                    Some(stream, (arm, sent, dropped), (words, wordsValid, wordsReady))
                                | _ -> None

                            let regs, played =
                                serialRegMapSlaveLink
                                    "host"
                                    board.fabricHz
                                    baud
                                    uart
                                    map
                                    (frames |> Option.map (fun (stream, _, _) -> stream))
                                    player.IsSome

                            let sink =
                                frames
                                |> Option.map (fun (_, (arm, sent, dropped), (words, wordsValid, wordsReady)) ->
                                    fun (rows: Stream<Expr list>) (dropCount: Expr) ->
                                        let produced = recordFrames regs (regs.value arm) sent dropped (recordedFields ()) rows dropCount
                                        produced.payload ==> words
                                        produced.valid ==> wordsValid
                                        wordsReady ==> produced.ready)

                            let play =
                                match playing, player, played with
                                | Some name, Some p, Some words -> Some(name, playInto board.part regs p words (playedFields ()))
                                | _ -> None

                            d.body (onPins d path (fromRegisters regs) (pulsesFrom regs) (toRegisters regs outs) (tablesIn regs) sink play i2s)
                        | _ ->
                            let regs = serialRegMapSlave "host" board.fabricHz baud uart map
                            d.body (onPins d path (fromRegisters regs) (pulsesFrom regs) (toRegisters regs outs) (tablesIn regs) None None i2s))

            name, top.def, recorderEntries @ playerEntries @ entries
        | NoHost ->
            let name = $"{d.name}Top"

            let top =
                defModule
                    name
                    (fun p -> converterPins board p, pinOuts p)
                    (fun (pins, outs) ->
                        let i2s = converterLink board pins rate
                        d.body (onPins d path baked unfired (reporter None outs) unwritable None None i2s))

            name, top.def, []

    { board = board
      name = name
      top = top
      registers = registers
      batch = None
      map = map
      target = targetOf board.part
      seamExtras = [] }

/// The design with no converter, on the host link alone: the host's words
/// in as its rows, its rows out as frames, both on the wire its register map
/// is on. The batch path of a board whose only connection is a UART — and
/// built from the player's and the recorder's registers, so the tools that
/// drive those drive this.
///
/// Nothing paces the design but the link: a row is taken when its words have
/// arrived and the design is ready, and a row goes out as the transmitter
/// takes it, back-pressuring the design when the link is the slow part.
/// What the link tops share: the UART, the one stream in (played words or
/// a count), the one output (rows as frames, or a surface as snapshots),
/// the map assembled from what the bindings want.
let private linkBaud (board: Board) (d: Design) =
    match board.host with
    | UartAt baud -> baud
    | _ -> failwith $"{d.name}: rows over the host link need a UART host, and the {board.name}'s is %A{board.host}"

/// The one input need and the one output need a link top carries, the
/// output a stream or a surface.
let private linkEnds (d: Design) =
    if d.streams <> 1 then
        failwith $"{d.name}: a board carries one stream, and the design declares %d{d.streams}"

    let one side (names: string list) =
        match names with
        | [ name ] -> name
        | names -> failwith $"{d.name}: the link carries one {side}, and %d{names.Length} are bound to it"

    let inName = one "input stream" (d.needs |> List.choose (function StreamIn(name, _) -> Some name | _ -> None))

    let outputs =
        d.needs
        |> List.choose (function
            | StreamOut(name, _) -> Some(name, None)
            | SurfaceOut(name, fields, rows) -> Some(name, Some(fields, rows))
            | _ -> None)

    let outName, surface =
        match outputs with
        | [ one ] -> one
        | several -> failwith $"{d.name}: the link carries one output, and %d{several.Length} are bound to it"

    inName, outName, surface

/// The seam's word of what each link-carried need takes on the wire: words
/// a row, and for a surface its rows — so a host reads a snapshot without
/// a number of its own.
let private linkSeamExtras (part: Part) (d: Design) (inName: string option) (outName: string) (surface: ((string * NumberFormat) list * int) option) =
    [ match inName with
      | Some name ->
          yield $"pub const {upperSnake name}_WORDS: usize = %d{linkWordsOf (fieldsOf d name)};"
          // What the player holds, so a host plays no more than fits
          // between its reads of `…LinkFill`.
          yield $"pub const {upperSnake name}_BUFFER_WORDS: usize = %d{playBufferWordsFor part};"
      | None -> ()

      match surface with
      | Some(fields, rows) ->
          yield $"pub const {upperSnake outName}_WORDS: usize = %d{linkWordsOf fields};"
          yield $"pub const {upperSnake outName}_ROWS: usize = %d{rows};"
      | None -> yield $"pub const {upperSnake outName}_WORDS: usize = %d{linkWordsOf (fieldsOf d outName)};" ]

/// The output half of a link top: the design's rows out as frames, or its
/// surface as snapshots, either way threaded back through a wire pair to
/// the slave that was built before the design's body ran.
let private linkOutput
    (d: Design)
    (regs: SlaveRegs)
    (contributed: BindingRegs)
    (outName: string)
    (surface: ((string * NumberFormat) list * int) option)
    (words: Expr, wordsValid: Expr, wordsReady: Expr)
    =
    let send (out: Stream<Expr>) =
        out.payload ==> words
        out.valid ==> wordsValid
        wordsReady ==> out.ready

    let streamOut (n: string) (produced: Stream<Expr list>) =
        if n <> outName || surface.IsSome then
            failwith $"{d.name}: no stream out called '{n}'"

        match contributed.recorderRegs with
        | Some(RecordToFrames(arm, sent, dropped)) ->
            // Nothing drops on this path: the link's pace reaches the design
            // as back-pressure.
            send (recordFrames regs (regs.value arm) sent dropped (fieldsOf d outName) produced (lit 0UL 32))
        | _ -> failwith $"{d.name}: the link top's recorder is frames on the link"

    let surfaceOut (n: string) (rows: Expr list list) =
        match surface, contributed.snapshotRegs with
        | Some(fields, declared), Some(SnapshotToFrames(pull, sent, missed)) when n = outName ->
            if rows.Length <> declared then
                failwith $"{d.name}: '{n}' declared %d{declared} rows and handed over %d{rows.Length}"

            send (snapshotFrames regs (pull, sent, missed) fields rows)
        | _ -> failwith $"{d.name}: no surface called '{n}'"

    streamOut, surfaceOut

/// The registers a link top's bindings contributed, by name, for the seam.
let private linkRegisters (contributed: BindingRegs) =
    let recorder =
        match contributed.recorderRegs with
        | Some(RecordToFrames(arm, sent, dropped)) -> [ arm; sent; dropped ]
        | _ -> []

    let snapshot =
        match contributed.snapshotRegs with
        | Some(SnapshotToFrames(pull, sent, missed)) -> [ pull; sent; missed ]
        | Some(SnapshotToMemory(baseReg, capture, release, ready, overrun, slot, irq)) -> [ baseReg; capture; release; ready; overrun; slot; irq ]
        | None -> []

    let player =
        match contributed.playerRegs with
        | Some p -> [ p.arm; p.fill; p.dropped; p.starved ]
        | None -> []

    let counter =
        match contributed.counterRegs with
        | Some c -> [ c.total; c.start; c.busy ]
        | None -> []

    (recorder @ snapshot @ player @ counter |> List.map (fun e -> e.name, e)) @ contributed.designEntries

let private linkTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    let baud = linkBaud board d
    let inName, outName, surface = linkEnds d

    let contributed =
        registersFor
            path
            (if surface.IsNone then Some(outName, InLinkFrames) else None)
            (Some inName)
            None
            (if surface.IsSome then Some(outName, InLinkFrames) else None)
            d

    let map = contributed.regMap

    let player =
        match contributed.playerRegs with
        | Some p -> p
        | None -> failwith $"{d.name}: the link top's player is words on the link"

    let pinned = pinnedOutputs d path
    let name = $"{d.name}Link"

    let top =
        defModule
            name
            (fun p -> uartPins p "host", pinOutsOf pinned p)
            (fun (uart, outs) ->
                // The recorder's words are produced by the design's output,
                // which is wired below the slave, so that stream is threaded
                // back through a wire pair — as the converter top does.
                let words = wire "record_frame_word" 32
                let wordsValid = wireBit "record_frame_valid"
                let wordsReady = wireBit "record_frame_ready_back"

                let stream: Stream<Expr> =
                    { payload = words
                      valid = wordsValid
                      ready = wordsReady
                      layout = layout1 ("word", 32) }

                let regs, played = serialRegMapSlaveLink "host" board.fabricHz baud uart map (Some stream) true

                let played =
                    match played with
                    | Some p -> p
                    | None -> failwith $"{d.name}: the link was asked to take played words"

                let rows = playRows board.part regs player played (fieldsOf d inName)
                let streamOut, surfaceOut = linkOutput d regs contributed outName surface (words, wordsValid, wordsReady)

                let boundary: Boundary =
                    { streamIn =
                        fun n ->
                            if n <> inName then
                                failwith $"{d.name}: no stream in called '{n}'"

                            rows
                      streamOut = streamOut
                      valueIn = valuesOf contributed.designEntries regs
                      pulseIn = pulsesInOf contributed.designEntries regs
                      valueOut = reporterFor board d path contributed.designEntries (Some regs) outs
                      tableIn = tablesInOf contributed.designEntries regs
                      surfaceOut = surfaceOut }

                d.body boundary)

    { board = board
      name = name
      top = top.def
      registers = linkRegisters contributed
      batch = None
      map = map
      target = targetOf board.part
      seamExtras = linkSeamExtras board.part d (Some inName) outName surface }

/// The counted path over the link: beats minted in fabric — `0 …` as many
/// as the host asked for — and the rows they produce out as frames, a frame
/// drawn on a board with no host memory. The count is the design's input
/// box's one field, as on the host-memory path; the rows leave at the
/// link's pace and the count waits with them, so nothing in fabric runs
/// ahead of the wire.
let private countedLinkTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    let baud = linkBaud board d
    let inName, outName, surface = linkEnds d

    if surface.IsSome then
        failwith $"{d.name}: a surface is taken when the host pulls, and a count would mint beats nobody asked for — bind '{outName}' with the link path"

    if (inputsOf d).Length <> 1 then
        failwith $"{d.name}: the counted path puts the beat index on the input box's one field, and the design declares [{describePins (inputsOf d)}]"

    let contributed = registersFor path (Some(outName, InLinkFrames)) None (Some inName) None d
    let map = contributed.regMap

    let counter =
        match contributed.counterRegs with
        | Some c -> c
        | None -> failwith $"{d.name}: the counted link top's count is a register"

    let pinned = pinnedOutputs d path
    let name = $"{d.name}Link"

    let top =
        defModule
            name
            (fun p -> uartPins p "host", pinOutsOf pinned p)
            (fun (uart, outs) ->
                let words = wire "record_frame_word" 32
                let wordsValid = wireBit "record_frame_valid"
                let wordsReady = wireBit "record_frame_ready_back"

                let stream: Stream<Expr> =
                    { payload = words
                      valid = wordsValid
                      ready = wordsReady
                      layout = layout1 ("word", 32) }

                let regs, _ = serialRegMapSlaveLink "host" board.fabricHz baud uart map (Some stream) false

                // --- the count: `total` beats, the index each ------------
                let total = regs.value counter.total
                let running = regBit "count_running"
                let issued = reg "count_issued" 32
                let more = wireBit "count_more"
                (running &&& lt issued total) ==> more
                let countReady = wireBit "count_ready"
                registerStreamReady countReady
                If (more &&& countReady) (fun () -> issued + lit 1UL 32 ==> issued)

                let _, f = (inputsOf d).Head
                let index = if f.totalWidth >= 32 then pad f.totalWidth issued else slice (f.totalWidth - 1) 0 issued

                let beats: Stream<Expr list> =
                    { payload = [ index ]
                      valid = more
                      ready = countReady
                      layout = layoutOfList (inputsOf d) }

                // Busy until the rows the count produces — `answers` a beat —
                // have all been taken by the link.
                let rowsTotal = wire "count_rows_total" 32
                timesConstant d.answers total ==> rowsTotal
                let rowsOut = reg "count_rows_out" 32
                let rowTaken = wireBit "count_row_taken"

                ifElse
                    [ (regs.pulse counter.start,
                       fun () ->
                           lit 1UL 1 ==> running
                           lit 0UL 32 ==> issued
                           lit 0UL 32 ==> rowsOut)
                      (otherwise,
                       fun () ->
                           If rowTaken (fun () -> rowsOut + lit 1UL 32 ==> rowsOut)
                           If (running &&& eq rowsOut rowsTotal) (fun () -> lit 0UL 1 ==> running)) ]

                regs.drive counter.busy running

                let streamOut, surfaceOut = linkOutput d regs contributed outName None (words, wordsValid, wordsReady)

                let boundary: Boundary =
                    { streamIn =
                        fun n ->
                            if n <> inName then
                                failwith $"{d.name}: no stream in called '{n}'"

                            beats
                      streamOut =
                        fun n produced ->
                            (produced.valid &&& produced.ready) ==> rowTaken
                            streamOut n produced
                      valueIn = valuesOf contributed.designEntries regs
                      pulseIn = pulsesInOf contributed.designEntries regs
                      valueOut = reporterFor board d path contributed.designEntries (Some regs) outs
                      tableIn = tablesInOf contributed.designEntries regs
                      surfaceOut = surfaceOut }

                d.body boundary)

    { board = board
      name = name
      top = top.def
      registers = linkRegisters contributed
      batch = None
      map = map
      target = targetOf board.part
      seamExtras = linkSeamExtras board.part d None outName None }

/// The counters a batch top's read and write sides share.
type private BatchCounters =
    { frameCount: Expr
      bursts: Expr
      beatsTotal: Expr
      running: Expr
      arIssued: Expr
      beatsWritten: Expr
      cycleCount: Expr
      burstAddrShift: int }

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
    // A surface is the output where the design has one: the keep-latest
    // triple buffer in the host's memory, the design's rows out being then
    // none — a design has one output, a stream or a surface.
    let surface = surfacesOf d |> List.tryHead

    match surface, outputsOf d with
    | Some(name, _, _), _ :: _ -> failwith $"{d.name}: '{name}' is a surface and the design also has rows out, and a top carries one output"
    | _ -> ()

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

    // Rows out are rows in times the answers.
    let timesAnswers = timesConstant d.answers

    // On the scatter path the design's FIRST output field is where the beat
    // goes; the rest are the beat itself.
    if scattered && (outputsOf d).Length < 2 then
        failwith
            $"{d.name}: the scatter path puts the destination beat index on the output box's first field and the payload on the rest, and the design declares [{describePins (outputsOf d)}]"

    let destPin = if scattered then Some (outputsOf d).Head else None
    let payloadPins = if scattered then List.tail (outputsOf d) else (outputsOf d)

    let contributed =
        registersFor path None None None (surface |> Option.map (fun (name, _, _) -> name, InHostRows)) d
    let batchOption, entries, map = contributed.batchRegs, contributed.designEntries, contributed.regMap

    let batch =
        match batchOption with
        | Some b -> b
        | None -> failwith $"{d.name}: the host-memory path needs the batch contract, and no binding contributed one"
    let inShape = rowShape memory.width (inputsOf d)
    // Rows out have a shape; a surface has its own, below.
    let outShape = if payloadPins.IsEmpty then None else Some(rowShape memory.width payloadPins)

    // A scattered beat carries one address, so it has to BE one beat: there is
    // nowhere to put a second address if two rows shared a word, and no reason
    // to buffer rows that are not going to the same place.
    match outShape with
    | Some shape when scattered && shape.rowsPerBeat <> 1 ->
        failwith
            $"{d.name}: the scatter path writes one payload per beat, and [{describePins payloadPins}] packs %d{shape.rowsPerBeat} of them into a %d{memory.width}-bit word — widen the payload to fill one"
    | _ -> ()

    // The surface's rows in beats: a lane a field, rows packed into beats,
    // padded to whole beats and to the two a snapshot source needs at least.
    let surfaceShape =
        surface
        |> Option.map (fun (name, fields, rows) ->
            let shape = rowShape memory.width fields

            if shape.beatsPerRow > 1 then
                failwith $"{d.name}: a surface's row is at most a beat, and '{name}' is %d{shape.lanesPerRow} lanes"

            let beats = max 2 ((rows + shape.rowsPerBeat - 1) / shape.rowsPerBeat)
            name, fields, rows, shape, beats)

    match destPin with
    | Some (n, f) when f.totalWidth > 32 ->
        failwith $"{d.name}: the scatter path's destination '{n}' is a beat index and the bus addresses 32 bits, not %d{f.totalWidth}"
    | _ -> ()
    let beatBytes = memory.width / 8
    let burstBytes = beatsPerBurst * beatBytes
    let beatAddrShift = ceilLog2 beatBytes
    let name = $"{d.name}Batch"

    let top =
        defModuleClocked
            axiClock
            name
            (fun p ->
                axiLiteSlavePorts p map.apertureAddrWidth,
                (if counted then None else Some(axiReadBusPorts p "m_axi" 32 memory.width)),
                axiWriteBusPorts p "m_axi" 32 memory.width,
                // A surface grants snapshots the host waits for, so it has an
                // interrupt; a batch is polled on `busy`, as it always was.
                (if surface.IsSome then Some(p.outPort "irq" 1) else None))
            (fun (slavePorts, readBusPorts, writeBusPorts, irqPort) ->
                let regs = regMapSlave slavePorts map
                let byName = Map.ofList entries

                // The counters both sides share are built by whichever side the
                // body asks for first — its rows in, as it happens — so they
                // land after the instance, where they always were.
                let counters =
                    lazy
                        let frameCount = regs.value batch.frameCount

                        // Rows → bursts on the way in, rows → beats on the way out;
                        // both shapes are powers of two, so each is a shift.
                        let bursts = wire "bursts" 32
                        let burstShift = ceilLog2 inShape.rowsPerBurst
                        pad 32 (slice 31 burstShift frameCount) ==> bursts

                        // `frameCount` is rows in; the rows out are `answers` as many.
                        let rowsOut = wire "rows_out" 32

                        timesAnswers frameCount ==> rowsOut

                        // Beats out: rows over the rows a beat holds, or rows
                        // times the beats a row takes. A surface writes no
                        // beats of the batch's: its total is the rows taken.
                        let beatsTotal = wire "beats_total" 32

                        match outShape with
                        | Some shape when shape.beatsPerRow > 1 ->
                            let outShift = ceilLog2 shape.beatsPerRow
                            cat (slice (31 - outShift) 0 rowsOut) (lit 0UL outShift) ==> beatsTotal
                        | Some shape ->
                            let outShift = ceilLog2 shape.rowsPerBeat
                            pad 32 (slice 31 outShift rowsOut) ==> beatsTotal
                        | None -> frameCount ==> beatsTotal

                        let running = regBit "running"
                        let arIssued = reg "ar_issued" 32
                        let beatsWritten = reg "beats_written" 32
                        let cycleCount = reg "cycle_count" 32

                        let burstAddrShift = ceilLog2 burstBytes

                        { frameCount = frameCount
                          bursts = bursts
                          beatsTotal = beatsTotal
                          running = running
                          arIssued = arIssued
                          beatsWritten = beatsWritten
                          cycleCount = cycleCount
                          burstAddrShift = burstAddrShift }

                let writerIdle = ref None

                let rowsIn (_: string) : Stream<Expr list> =
                    let { frameCount = frameCount; bursts = bursts; running = running; arIssued = arIssued; burstAddrShift = burstAddrShift; beatsWritten = beatsWritten } =
                        counters.Force()

                    let rows =
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

                    // With a surface for an output the batch writes nothing, and
                    // it is done when the design has taken every row it was sent.
                    if surface.IsSome then
                        If (rows.valid &&& rows.ready) (fun () -> beatsWritten + lit 1UL 32 ==> beatsWritten)

                    rows

                let rowsOut (_: string) (produced: Stream<Expr list>) =
                    let { beatsWritten = beatsWritten } = counters.Force()

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

                    let shape =
                        match outShape with
                        | Some shape -> shape
                        | None -> failwith $"{d.name}: the design handed rows out and declares none"

                    let outBeats = payloadRows |> rowsToBeats shape payloadPins memory.width

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
                    let idle =
                        axiMasterWriterWithIdleOn (axiWriteBusOf writeBusPorts) memory.writeOutstanding writeBeats

                    If (outBeats.valid &&& wrReady) (fun () -> beatsWritten + lit 1UL 32 ==> beatsWritten)
                    writerIdle.Value <- Some idle

                // --- the surface: the keep-latest triple buffer -----------
                // The surface is latched whole in one cycle, streamed as beats,
                // and written into one of three slots in the host's memory; the
                // host captures the freshest complete slot and releases it. The
                // write path is armed by a non-zero base, as GoL's top always
                // was: nothing enters the interconnect until the host has said
                // where, which is also what keeps an unarmed master off address
                // zero.
                let surfaceOut (name: string) (rows: Expr list list) =
                    match surfaceShape, contributed.snapshotRegs with
                    | Some(sname, fields, declared, shape, beats), Some(SnapshotToMemory(baseReg, capture, release, ready, overrun, slot, irq)) when name = sname ->
                        if rows.Length <> declared then
                            failwith $"{d.name}: '{name}' declared %d{declared} rows and handed over %d{rows.Length}"

                        let rowWidth = shape.lanesPerRow * laneWidth

                        let packRow (r: int) (row: Expr list) =
                            let lanes =
                                List.map2
                                    (fun (fname: string, f: NumberFormat) (e: Expr) ->
                                        let w = lanesOf f * laneWidth

                                        let e =
                                            if f.signed && not (isNamed e) then
                                                let named = wire $"surface_%d{r}_{fname}" f.totalWidth
                                                e ==> named
                                                named
                                            else
                                                e

                                        if f.signed then asUInt (pad w (asSInt e)) else pad w e)
                                    fields
                                    row

                            let padding = shape.lanesPerRow - (fields |> List.sumBy (fun (_, f) -> lanesOf f))
                            let packed = wire $"surface_row_%d{r}" rowWidth
                            (lanes @ List.replicate padding (lit 0UL laneWidth) |> List.rev |> List.reduce cat) ==> packed
                            packed

                        let rowWords = rows |> List.mapi packRow
                        let rowsPadded = rowWords @ List.replicate ((shape.rowsPerBeat - rows.Length % shape.rowsPerBeat) % shape.rowsPerBeat) (lit 0UL rowWidth)

                        let beatRows =
                            rowsPadded
                            |> List.chunkBySize shape.rowsPerBeat
                            |> List.map (fun chunk ->
                                match chunk with
                                | low :: rest -> List.fold (fun acc r -> cat r acc) low rest
                                | [] -> failwith "unreachable: a beat has a row")

                        let beatRows = beatRows @ List.replicate (beats - beatRows.Length) (lit 0UL memory.width)
                        let idle = wireBit "snapshot_writer_idle"

                        let frames, status =
                            snapshotSource "snapshot" beatRows
                            |> streamConflate3 "conflate" (regs.pulse capture) (regs.pulse release) idle

                        let baseAddr = regs.value baseReg
                        let armed = wireBit "snapshot_armed"
                        bnot (eq baseAddr (lit 0UL 32)) ==> armed

                        (frames
                         |> streamMapTo (axiWriteBeatLayout 32 memory.width) (fun (slotIndex, index, data) ->
                             let offset = cat (cat slotIndex index) (lit 0UL beatAddrShift)
                             baseAddr + cat (lit 0UL (32 - width offset)) offset, data, lit ((1UL <<< beatBytes) - 1UL) beatBytes)
                         |> fun s ->
                             let armedReady = wireBit "snapshot_armed_ready"
                             (armedReady &&& armed) ==> s.ready

                             axiMasterWriterWithIdleOn
                                 (axiWriteBusOf writeBusPorts)
                                 memory.writeOutstanding
                                 { s with
                                     valid = s.valid &&& armed
                                     ready = armedReady })
                        ==> idle

                        regs.drive ready status.ready
                        regs.drive overrun status.overrun
                        regs.drive slot status.readSlot
                        regs.setBit irq status.irq
                    | _ -> failwith $"{d.name}: no surface called '{name}'"

                // --- the design ---------------------------------------------
                d.body
                    { streamIn = rowsIn
                      streamOut = rowsOut
                      valueIn = fun name -> regs.value byName[name]
                      pulseIn = fun name -> regs.pulse byName[name]
                      valueOut = fun name value -> regs.drive byName[name] value
                      tableIn = fun name index -> regs.readArray byName[name] index
                      surfaceOut = surfaceOut }

                match irqPort with
                | Some port -> regs.irq ==> port
                | None -> ()

                let { running = running; arIssued = arIssued; beatsWritten = beatsWritten; beatsTotal = beatsTotal; cycleCount = cycleCount; frameCount = frameCount; burstAddrShift = burstAddrShift } =
                    counters.Force()

                // With a surface the batch has no write side: it is done when the
                // rows it was sent have been taken.
                let writerIdle =
                    match writerIdle.Value, surface with
                    | Some idle, _ -> idle
                    | None, Some _ -> lit 1UL 1
                    | None, None -> failwith $"{d.name}: the design never handed its rows out, so the batch has nothing to write"

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

                let rowsPerBurst =
                    match outShape with
                    | Some shape when counted -> shape.rowsPerBurst
                    | Some shape -> max inShape.rowsPerBurst shape.rowsPerBurst
                    | None -> inShape.rowsPerBurst

                let rowShift = ceilLog2 rowsPerBurst

                assertThat
                    (bnot running ||| eq (slice (rowShift - 1) 0 frameCount) (lit 0UL rowShift))
                    $"frameCount must be a multiple of the rows a burst holds (%d{rowsPerBurst})")

    let snapshotEntries =
        match contributed.snapshotRegs with
        | Some(SnapshotToMemory(baseReg, capture, release, ready, overrun, slot, irq)) -> [ baseReg; capture; release; ready; overrun; slot; irq ] |> List.map (fun e -> e.name, e)
        | _ -> []

    // The seam's word of the surface's shape in memory: beats a snapshot,
    // bytes a slot (three slots from the base), rows and words a row.
    let surfaceExtras =
        match surfaceShape with
        | Some(sname, _, rows, shape, beats) ->
            [ $"pub const {upperSnake sname}_ROWS: usize = %d{rows};"
              $"pub const {upperSnake sname}_WORDS: usize = %d{shape.lanesPerRow};"
              $"pub const {upperSnake sname}_MEM_BEATS: usize = %d{beats};"
              $"pub const {upperSnake sname}_MEM_SLOT_BYTES: usize = %d{1 <<< (ceilLog2 beats + beatAddrShift)};" ]
        | None -> []

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
        @ snapshotEntries
      batch = Some batch
      map = map
      target = targetOf board.part
      seamExtras = surfaceExtras }

/// The design on a board, the way the path says: the converter on the pins,
/// or the host's memory.
/// A design with no stream and no surface: values the host sets and reads,
/// bits on pins — `hello`, the first design on any board. The host reaches
/// it through whatever the board's link is; with no host the values are
/// baked at their starting values and only the pins remain.
let private registersTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    let contributed = registersFor path None None None None d
    let map = contributed.regMap
    let entries = contributed.designEntries
    let pinned = pinnedOutputs d path
    let starting = Map.ofList (startingOf d)
    let formats = Map.ofList (controlsOf d)

    let body (regs: SlaveRegs option) (outs: (string * Expr) list) =
        let boundary: Boundary =
            { streamIn = fun n -> failwith $"{d.name}: no stream in called '{n}'"
              streamOut = fun n _ -> failwith $"{d.name}: no stream out called '{n}'"
              surfaceOut = fun n _ -> failwith $"{d.name}: no surface called '{n}'"
              valueIn =
                match regs with
                | Some regs -> valuesOf entries regs
                | None -> fun name -> lit starting[name] formats[name].totalWidth
              pulseIn =
                match regs with
                | Some regs -> pulsesInOf entries regs
                | None -> fun _ -> lit 0UL 1
              valueOut = reporterFor board d path entries regs outs
              tableIn =
                match regs with
                | Some regs -> tablesInOf entries regs
                | None -> fun name _ -> failwith $"{d.name}: the table '{name}' is the host's to fill, and the {board.name} has no host" }

        d.body boundary

    let name, top =
        match board.host with
        | AxiLiteAt _ ->
            let name = $"{d.name}Axi"

            name,
            (defModuleClocked
                axiClock
                name
                (fun p -> axiLiteSlavePorts p map.apertureAddrWidth, pinOutsOf pinned p)
                (fun (slavePorts, outs) -> body (Some(regMapSlave slavePorts map)) outs))
                .def
        | UartAt baud ->
            let name = $"{d.name}Uart"

            name,
            (defModule
                name
                (fun p -> uartPins p "host", pinOutsOf pinned p)
                (fun (uart, outs) -> body (Some(serialRegMapSlave "host" board.fabricHz baud uart map)) outs))
                .def
        | NoHost ->
            let name = $"{d.name}Top"
            name, (defModule name (fun p -> pinOutsOf pinned p) (fun outs -> body None outs)).def

    { board = board
      name = name
      top = top
      registers = (if board.host = NoHost then [] else entries)
      batch = None
      map = map
      target = targetOf board.part
      seamExtras = [] }

/// The design handed its clocking on this board and path, where it asks
/// for one.
let private clocked (board: Board) (path: DataPath) (d: Design) : Design =
    // The rate the design is made for: the converter's where a converter
    // paces the rows, and the nominal one where the host does — rows in a
    // file or on a link carry no rate of their own, and a coefficient
    // designed for the converter the board happens to have would be wrong
    // for the 48 kHz WAV the host actually sends.
    let d =
        match d.atClocking with
        | None -> d
        | Some resolve ->
            // Asked of the path rather than the design's needs, which are
            // not known until it has been handed its clocking.
            let onConverter = path.bindings |> List.exists (fun b -> b.carrier = OnPins)
            let rate = if onConverter then boardRate board nominalRate else nominalRate
            resolve (Fit({ rate = rate; cyclesPerBeat = int (float board.fabricHz / rate) }, board))

    d

/// The top that carries a clocked design's needs.
let private placed (board: Board) (path: DataPath) (d: Design) : BoardTop =
    let carriesRows =
        d.needs
        |> List.exists (function
            | StreamIn _
            | StreamOut _
            | SurfaceOut _ -> true
            | _ -> false)

    if not carriesRows then
        registersTop board path d
    else

    // Six combinations of carriers are built. The rest are sayable — which
    // is the point of binding per need — and are refused here by name, so
    // "not built yet" is distinguishable from "cannot be described".
    match carrierOfInput path d, carrierOfOutput path d with
    | None, _
    | _, None ->
        failwith
            $"{d.name}: nothing carries one of the design's streams on this path — every need wants a binding, by name or by kind"
    | Some OnPins, Some OnPins -> pinsTop board path d
    | Some InLinkFrames, Some InLinkFrames -> linkTop board path d
    | Some AsBeatCount, Some InLinkFrames -> countedLinkTop board path d
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

/// The design on a board, the way the path says. Its facts join the seam
/// here, whichever top carries it, so every path states them the same way.
let boardTop (board: Board) (path: DataPath) (d: Design) : BoardTop =
    checkBoard board
    let d = clocked board path d
    let top = placed board path d

    { top with
        seamExtras =
            top.seamExtras
            @ [ for name, value in factsOf d -> $"pub const {upperSnake name}: u64 = %d{value};" ] }

/// The design on a board, the way the path says: the converter on the pins,
/// or the host's memory, or its link. Sizes derived, none named.
/// The design as a mapping says: its board with every harness plugged in,
/// its path.
let boardTopFor (m: Mapping.Mapping) (d: Design) : BoardTop = boardTop (Mapping.boardOf m) m.path d

/// The register map as the Rust seam prints it, headed for the top it serves.
let seamLines (t: BoardTop) : string list =
    [ $"//! Register map for the `{t.name}` slave on the {t.board.name}."
      "//! Generated from the design's own register map. Do not edit by hand."
      "" ]
    @ regMapRsLines t.map
    // The clock the design was built for, from the board. Every wall-clock
    // figure a host quotes divides by it — a pace in cycles from one in
    // generations a second — and a host that wrote it down separately could
    // disagree with the fabric about how fast the fabric is (it did, by five
    // hertz, for the hand GoL top).
    @ [ ""; $"pub const FABRIC_HZ: u64 = %d{t.board.fabricHz};" ]
    @ (if List.isEmpty t.seamExtras then [] else "" :: t.seamExtras)

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

/// The bridge loop behind every `*serve` verb: a top in the Sim with a
/// behavioural DDR behind its AXI master, spoken to over stdin the way
/// `SimAxi.serve` is — `R`/`W` are AXI-Lite transactions, `C <hexn>` runs
/// free cycles, `D <hexoff> <hexlen>` dumps DDR as hex, `L <hexoff> <hex>`
/// loads it, and `M` names the registers with their offsets. `marker` is
/// the ready line the Rust `FsSimWindow` waits for.
let ddrServe (top: ModuleDef) (registers: (string * RegEntry) list) (arenaBytes: int) (dataBytes: int) (marker: string) =
    let sim = Sim top

    // A top that reads nothing has no read channel, so its DDR is the
    // write-only slave; one with both channels gets the model that answers both.
    let reads = top.decls |> List.exists (function Input(n, _) -> n = "m_axi_arready" | _ -> false)

    let cycle, ddrMemory =
        if reads then
            let ddr = SimAxiDdr(sim, arenaBytes, dataBytes = dataBytes)
            ddr.Cycle, ddr.Memory
        else
            let ddr = SimAxiWriteSlave(sim, arenaBytes, dataBytes = dataBytes)
            ddr.Cycle, ddr.Memory

    let axi = SimAxi.clientWith sim cycle
    let out = System.Console.Out
    out.WriteLine marker
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
             out.WriteLine(registers |> List.map (fun (n, e) -> sprintf "%s:%x" n e.offset) |> String.concat " ")
         | _ -> out.WriteLine "ERR")

        out.Flush()
        line <- System.Console.In.ReadLine()

/// The F# half of the batch bridge: the host-memory top in the Sim, its
/// DDR the behavioural model — `ddrServe` with the board's memory and the
/// `BATCHSERVE` marker. The same Rust driver then runs against this before
/// any bitstream exists.
let batchServe (t: BoardTop) =
    let memory =
        match t.batch, t.board.hostMemory with
        | Some _, Some m -> m
        | _ -> failwith $"{t.name}: the batch bridge serves a host-memory top"

    ddrServe t.top t.registers memory.arenaBytes (memory.width / 8) "BATCHSERVE"
