module Warp11.Designs.Tests.BoardTopTests

open Expecto
open Warp11

let private word = [ "word", unsignedInt 32 ]
let private stereo = [ "left", signedInt sampleWidth; "right", signedInt sampleWidth ]

/// Rows straight through, a table the host writes, and the entry at a
/// host-chosen index reported back: what the host wrote is what the design
/// reads, whichever board the table sits behind.
let private tableDesign (starting: uint64 list) : BoardTop.Design =
    let needs =
        [ BoardTop.streamIn "in" word
          BoardTop.streamOut "out" word
          BoardTop.valueIn "at" (unsignedInt 4)
          BoardTop.tableIn "curve" (unsignedInt 16) 16 Distributed starting
          BoardTop.valueOut "seen" (unsignedInt 16) ]

    { name = "TableProbe"
      sampleRate = 0.0
      streams = 1
      needs = needs
      answers = 1
      atClocking = None
      body =
        fun boundary ->
            let rows = boundary.streamIn "in"
            let port = boundary.tableIn "curve" (boundary.valueIn "at")
            let seen = wire "seen_word" 16
            slice 15 0 port.read.data ==> seen
            boundary.valueOut "seen" seen
            rows |> boundary.streamOut "out" }

/// The same probe with its needs declared as handles.
type private Probe =
    { rows: BoardTop.Incoming<Expr>
      out: Stream<Expr> -> unit
      at: Expr
      curve: Expr -> HostArrayPort
      seen: Expr -> unit }

let private probe (starting: uint64 list) =
    let word = pins1 ("word", unsignedInt 32)

    BoardTop.design
        "TableProbe"
        (fun n ->
            { rows = n.streamIn ("in", word)
              out = n.streamOut ("out", word)
              at = n.valueIn ("at", unsignedInt 4)
              curve = n.tableIn ("curve", unsignedInt 16, 16, Distributed, starting)
              seen = n.valueOut ("seen", unsignedInt 16) })
        (fun p ->
            let port = p.curve p.at
            let seen = wire "seen_word" 16
            slice 15 0 port.read.data ==> seen
            p.seen seen
            p.rows.stream |> p.out)

type private Heard =
    { mics: BoardTop.Incoming<Expr * Expr>
      earphones: Stream<Expr * Expr> -> unit
      budget: Expr }

/// The aid's boundary at its smallest: microphones in, earphones out, and
/// a `record` stream of its own that the design fills with what it heard.
type private Aid =
    { mics: BoardTop.Incoming<Expr * Expr>
      earphones: Stream<Expr * Expr> -> unit
      record: Stream<Expr * Expr> -> unit }

let private aidShaped =
    let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

    BoardTop.design
        "AidShaped"
        (fun n ->
            { mics = n.streamIn ("mics", stereo)
              earphones = n.streamOut ("earphones", stereo)
              record = n.streamOut ("record", stereo) })
        (fun aid ->
            let heard = streamBroadcast 2 aid.mics.stream
            heard[0] |> aid.earphones
            heard[1] |> aid.record)

/// The aid's front end as its hub's Pmod plug carries it: the shared bus on
/// the top row.
let private frontEnd: Harness =
    { name = "front-end"
      plug = "pmod"
      positions =
        [ 1, (I2sSharedBus, "bclk")
          2, (I2sSharedBus, "ws")
          3, (I2sSharedBus, "sd_in")
          4, (I2sSharedBus, "sd_out") ] }

/// A design that reports one bit, for an LED.
type private Lit =
    { mics: BoardTop.Incoming<Expr * Expr>
      earphones: Stream<Expr * Expr> -> unit
      light: Expr
      limit: Expr -> unit }

let private lit =
    let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

    BoardTop.design
        "Lit"
        (fun n ->
            { mics = n.streamIn ("mics", stereo)
              earphones = n.streamOut ("earphones", stereo)
              light = n.valueIn ("light", unsignedInt 1, 1UL)
              limit = n.valueOut ("limit", unsignedInt 1) })
        (fun d ->
            d.light |> d.limit
            d.mics.stream |> d.earphones)

/// A LUT RAM and a LUT ROM side by side, for what each emits on ECP5.
let private lutMemories =
    defModule
        "LutMemories"
        (fun p -> (p.inPort "addr" 4, p.inPort "data" 8, p.inPort "we" 1, p.outPort "ram" 8, p.outPort "rom" 8))
        (fun (addr, data, we, ramOut, romOut) ->
            let ram = distributedMem "scratch" 4 8
            memWrite ram addr data we
            memRead ram addr ==> ramOut
            let rom = distributedRom "squares" 8 [| for k in 0UL .. 15UL -> k * 3UL |]
            memRead rom addr ==> romOut)

let private registerOf (top: BoardTop.BoardTop) (name: string) =
    top.registers |> List.find (fun (n, _) -> n = name) |> snd

let private settle (ddr: SimAxiDdr) =
    for _ in 1..8 do
        ddr.Cycle()

let tests =
    testList
        "Board tops"
        [ testCase "a table need boots at its starting words and reads what the host writes" <| fun _ ->
              let starting = [ for k in 0UL .. 15UL -> 0x100UL + k ]
              let top = BoardTop.boardTop kv260 viaHostMemory (tableDesign starting)
              let sim = Sim top.top
              let ddr = SimAxiDdr(sim, 0x1000)
              let axi = SimAxi.clientWith sim ddr.Cycle
              let at = registerOf top "at"
              let curve = registerOf top "curve"
              let seen = registerOf top "seen"

              let readAt (k: int) =
                  axi.write32 at.offset (uint64 k)
                  settle ddr
                  axi.read32 seen.offset

              let booted = [ for k in 0..15 -> readAt k ]
              Expect.equal booted starting "every entry reads its starting word before the host writes anything"

              axi.write32 (curve.offset + 4UL * 5UL) 0xBEEFUL
              axi.write32 (curve.offset + 4UL * 12UL) 0x1234UL
              Expect.equal (readAt 5) 0xBEEFUL "the design reads the word the host wrote"
              Expect.equal (readAt 12) 0x1234UL "…at the entry it wrote it to"
              Expect.equal (readAt 6) 0x106UL "and the entries around it are untouched"

          testCase "a design declared as handles is the design written against its boundary" <| fun _ ->
              let starting = [ for k in 0UL .. 15UL -> 0x100UL + k ]
              let byHand = BoardTop.boardTop kv260 viaHostMemory (tableDesign starting)
              let byHandles = BoardTop.boardTop kv260 viaHostMemory (probe starting)
              Expect.equal (emitDesign byHandles.top) (emitDesign byHand.top) "the same Verilog, byte for byte"
              Expect.equal (BoardTop.seamLines byHandles) (BoardTop.seamLines byHand) "and the same seam"

          testCase "the rate a design is handed is the board's, and a starting value derived from it follows" <| fun _ ->
              let observed = ref []
              let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

              let heard =
                  BoardTop.design
                      "Heard"
                      (fun n ->
                          let mics = n.streamIn ("mics", stereo)

                          { mics = mics
                            earphones = n.streamOut ("earphones", stereo)
                            budget = n.valueIn ("budget", unsignedInt 16, uint64 mics.cyclesPerBeat) })
                      (fun p ->
                          observed.Value <- p.mics.rate :: observed.Value
                          p.budget |> ignore
                          p.mics.stream |> p.earphones)

              let onBoard board =
                  let top = BoardTop.boardTop board viaPins heard
                  top, BoardTop.seamLines top |> List.find (fun l -> l.Contains "BUDGET_RESET")

              let _, kv = onBoard kv260
              let _, ice = onBoard (iceBreakerAt 24_000_000)
              Expect.equal (List.rev observed.Value) [ 48_828.125; 46_875.0 ] "the rate each board's clock frames at"
              Expect.stringContains kv "0x800" "2048 cycles a beat at 100 MHz"
              Expect.stringContains ice "0x200" "512 at 24 MHz"

          testCase "a recording is its own need: the converter plays what it hears, the host's memory keeps it" <| fun _ ->
              let path =
                  { viaPins with bindings = viaPins.bindings @ [ { need = ByName "record"; carrier = InHostRows } ] }

              let top = BoardTop.boardTop kv260 path aidShaped
              Expect.isFalse
                  (BoardTop.seamLines top |> List.exists (fun l -> l.Contains "START_OFFSET" || l.Contains "SRC_ADDR"))
                  "a stream kept in the host's memory is not a batch: no contract in the seam"
              let sim = Sim top.top
              let ddr = SimAxiWriteSlave(sim, 0x2000, dataBytes = 16)
              let codec = I2sCodec(sim, separateCodecSimPins)

              let step () =
                  codec.Drive()
                  ddr.Capture()
                  sim.Tick()
                  ddr.Pace()
                  codec.Sample()

              let axi = SimAxi.clientWith sim step
              let ring = 0x1000
              axi.write32 (registerOf top "recordMemAddr").offset (uint64 ring)
              axi.write32 (registerOf top "recordMemBeats").offset 64UL
              axi.write32 (registerOf top "recordMemArm").offset 1UL

              let samples = [ for k in 1UL .. 8UL -> k * 0x1000UL, k * 0x10UL ]
              codec.Queue samples

              while codec.Count < samples.Length + 8 do
                  step ()

              let played = codec.Received |> List.skipWhile i2sSilence |> List.truncate samples.Length
              Expect.equal played samples "the earphones hear the microphones"

              // Two rows a 128-bit beat, a 32-bit lane a field, left first.
              let lane (row: int) (field: int) =
                  let at = ring + row * 8 + field * 4
                  uint64 (System.BitConverter.ToUInt32(ddr.Memory, at)) &&& ((1UL <<< sampleWidth) - 1UL)

              let kept = [ for row in 0..31 -> lane row 0, lane row 1 ] |> List.skipWhile i2sSilence |> List.truncate samples.Length
              Expect.equal kept samples "the ring holds the same samples, in order"
              Expect.equal (axi.read32 (registerOf top "recordMemDropped").offset) 0UL "a host that keeps up loses nothing"

          testCase "a harness plugged into a socket lands on the pins behind its positions" <| fun _ ->
              let board = plug frontEnd "J2" kv260
              let bus = board.connectors |> List.find (fun c -> c.role = I2sSharedBus)
              Expect.equal [ for port, pin in bus.pins -> port, pin.pin ] [ "bclk", "H12"; "ws", "E10"; "sd_in", "D10"; "sd_out", "C11" ] "J2's top row"
              Expect.isFalse (board.connectors |> List.exists (fun c -> c.role = I2sSeparateCodecs)) "the Pmod I2S2 that was in J2 is unplugged"
              Expect.equal (BoardTop.pinoutOf board) SharedBus "so the top takes the pinout the harness gives"

              Expect.throwsC (fun () -> plug frontEnd "J3" kv260 |> ignore) (fun e -> Expect.stringContains e.Message "no socket 'J3'" "names the socket")
              Expect.throwsC (fun () -> plug { frontEnd with plug = "pi40" } "J2" kv260 |> ignore) (fun e -> Expect.stringContains e.Message "a pmod socket" "names the mismatch")

              Expect.throwsC
                  (fun () -> plug { frontEnd with positions = frontEnd.positions @ [ 5, (Leds, "led") ] } "J2" kv260 |> ignore)
                  (fun e -> Expect.stringContains e.Message "position 5 is not wired" "a Pmod's power pins carry nothing")

          testCase "a mapping says its bindings and its harnesses, and reads back as written" <| fun _ ->
              let m: Mapping.Mapping =
                  { board = kv260
                    path =
                      { viaPins with
                          bindings =
                              viaPins.bindings
                              @ [ { need = ByName "record"; carrier = InHostRows }
                                  { need = ByName "limit"; carrier = OnPin "led" }
                                  { need = ByName "limit"; carrier = InRegisterMap } ] }
                    plugged = [ frontEnd, "J2" ] }

              let text = Mapping.write m

              match Mapping.parse text with
              | Ok back ->
                  Expect.equal back m "every binding and harness survives the file"
                  Expect.equal (Mapping.write back) text "and writes the same bytes again"
              | Error why -> failtest why

              let presetOnly: Mapping.Mapping = { board = kv260; path = viaPins; plugged = [] }
              Expect.isFalse ((Mapping.write presetOnly).Contains "bind") "a preset-only mapping reads as it always did"

          testCase "a reported bit lights an LED the way the board has it wired" <| fun _ ->
              let bare = { iceBreakerAt 24_000_000 with host = NoHost }

              let path =
                  { viaPins with bindings = viaPins.bindings @ [ { need = ByName "limit"; carrier = OnPin "ledr_n" } ] }

              let driven (board: Board) =
                  let top = BoardTop.boardTop board path lit
                  let sim = Sim top.top
                  sim.Tick()
                  sim.Peek "ledr_n", top

              let low, top = driven bare
              Expect.equal low 0UL "active low: lit is driven low"
              Expect.isEmpty top.registers "a value bound only to a pin takes no register"

              let rewired =
                  { bare with
                      connectors =
                          bare.connectors
                          |> List.map (fun c ->
                              if c.role = Leds then { c with pins = [ for port, pin in c.pins -> port, { pin with activeLow = false } ] } else c) }

              Expect.equal (fst (driven rewired)) 1UL "the same design, an LED wired the other way"

              let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-led-{System.Guid.NewGuid()}")
              let out = Warp11.Build.write dir top
              let pcf = System.IO.Directory.GetFiles(dir, "*.pcf") |> Array.head |> System.IO.File.ReadAllText
              Expect.stringContains pcf "ledr_n 11" "one LED of the board's two is pinned, and the pin gate lets it be"
              Expect.isNonEmpty out.files "the build directory is written"

          testCase "the Icepi's PLL takes 50 MHz to 25 exactly, as ecppll does, and refuses what it cannot reach" <| fun _ ->
              let pll = Warp11.Build.ecp5Pll 50_000_000 25_000_000
              Expect.equal (pll.referenceDiv, pll.feedbackDiv, pll.outputDiv) (2, 1, 24) "ecppll -i 50 -o 25: CLKI_DIV 2, CLKFB_DIV 1, CLKOP_DIV 24"
              Expect.equal pll.achievedHz 25e6 "exactly"
              Expect.equal (BoardTop.boardRate icepi 48_000.0) (BoardTop.boardRate kv260 48_000.0) "and the converter frames where the KV260's does"

              let short = Warp11.Build.ecp5Pll 50_000_000 24_000_000
              Expect.isTrue (abs (short.achievedHz - 23_333_333.0) < 1.0) "24 MHz is out of reach: ecppll lands on 23.333 too"

              let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

              let through =
                  BoardTop.design
                      "Through"
                      (fun n -> n.streamIn ("in", stereo), n.streamOut ("out", stereo))
                      (fun (i, o) -> i.stream |> o)

              let piI2s: Harness =
                  { name = "pi-i2s"
                    plug = "pi40"
                    positions = [ 12, (I2sSharedBus, "bclk"); 35, (I2sSharedBus, "ws"); 38, (I2sSharedBus, "sd_in"); 40, (I2sSharedBus, "sd_out") ] }

              let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-icepi-{System.Guid.NewGuid()}")

              Expect.throwsC
                  (fun () -> Warp11.Build.write dir (BoardTop.boardTop (plug piI2s "PI40" (icepiAt 24_000_000)) viaPins through) |> ignore)
                  (fun e -> Expect.stringContains e.Message "23.3333 MHz" "names where it would land")

              let out = Warp11.Build.write dir (BoardTop.boardTop (plug piI2s "PI40" icepi) viaPins through)
              let lpf = System.IO.Directory.GetFiles(dir, "*.lpf") |> Array.head |> System.IO.File.ReadAllText
              Expect.stringContains lpf "LOCATE COMP \"bclk\" SITE \"N4\";" "header pin 12 is N4"
              Expect.stringContains lpf "LOCATE COMP \"clk50\" SITE \"M1\";" "the oscillator"
              Expect.stringContains lpf "FREQUENCY PORT \"clk50\" 50 MHZ;" "constrained at its rate"
              Expect.stringContains (System.IO.File.ReadAllText out.run) "openFPGALoader -b icepi-zero" "programmed the way the board's own Makefile does"

          testCase "on ECP5 a LUT RAM stays distributed and a LUT ROM becomes logic, as measured" <| fun _ ->
              let verilog = emitDesignFor Target.Ecp5 lutMemories.def
              Expect.stringContains verilog "(* ram_style = \"distributed\" *) reg [7:0] scratch" "a written memory takes TRELLIS_DPR16X4"
              Expect.stringContains verilog "(* ram_style = \"logic\" *) reg [7:0] squares" "a ROM cannot, and is LUTs read the same way"
              Expect.stringContains (emitDesignFor Xilinx lutMemories.def) "(* ram_style = \"distributed\" *) reg [7:0] squares" "Vivado's emission is unchanged"

          testCase "a table larger than the aperture grows it without moving what came before" <| fun _ ->
              let small = BoardTop.boardTop kv260 viaHostMemory (tableDesign [])

              let big =
                  let d = tableDesign []

                  { d with
                      needs =
                          d.needs
                          |> List.map (function
                              | BoardTop.TableIn(name, format, _, storage, starting) -> BoardTop.tableIn name format 512 storage starting
                              | need -> need) }
                  |> BoardTop.boardTop kv260 viaHostMemory

              Expect.equal small.map.apertureAddrWidth BoardTop.apertureAddrWidth "a map that fits keeps the pinned aperture"
              Expect.isGreaterThan big.map.apertureAddrWidth BoardTop.apertureAddrWidth "a 512-word table grows it"
              Expect.equal (registerOf big "at").offset (registerOf small "at").offset "a value declared before the table keeps its offset"

          testCase "a recorder's registers are named for the need it records" <| fun _ ->
              let needs = [ BoardTop.streamIn "mics" stereo; BoardTop.streamOut "record" stereo ]

              let design: BoardTop.Design =
                  { name = "Recorded"
                    sampleRate = BoardTop.boardRate kv260 48_000.0
                    streams = 1
                    needs = needs
                    answers = 1
                    atClocking = None
                    body = fun boundary -> boundary.streamIn "mics" |> boundary.streamOut "record" }

              let path =
                  { viaPins with
                      bindings =
                          viaPins.bindings
                          @ [ { need = ByName "record"; carrier = OnPins }
                              { need = ByName "record"; carrier = InHostRows } ] }

              let names = (BoardTop.boardTop kv260 path design).registers |> List.map fst
              Expect.equal names [ "recordMemArm"; "recordMemAddr"; "recordMemBeats"; "recordMemWritten"; "recordMemDropped" ] "need, carrier, what"

          testCase "a recorder the board cannot carry is refused, not dropped" <| fun _ ->
              let g = tableDesign []

              let design: BoardTop.Design =
                  { g with
                      name = "Unrecorded"
                      sampleRate = BoardTop.boardRate (iceBreakerAt 24_000_000) 48_000.0
                      needs = [ BoardTop.streamIn "in" stereo; BoardTop.streamOut "out" stereo ]
                      body = fun boundary -> boundary.streamIn "in" |> boundary.streamOut "out" }

              let path =
                  { viaPins with
                      bindings =
                          viaPins.bindings
                          @ [ { need = ByName "out"; carrier = OnPins }
                              { need = ByName "out"; carrier = InHostRows } ] }

              Expect.throwsC
                  (fun () -> BoardTop.boardTop (iceBreakerAt 24_000_000) path design |> ignore)
                  (fun e -> Expect.stringContains e.Message "no memory the fabric can write" "the refusal says why") ]
