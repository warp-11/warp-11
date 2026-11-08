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

          testCase "a harness with PDM microphones puts the design's input on the decimator" <| fun _ ->
              // The Pi header's PCM pins for the bus out, two free positions for
              // the microphones, and no line in: the ICS-43434s are unplugged.
              let pdmFront: Harness =
                  { name = "front-end-pdm"
                    plug = "pi40"
                    positions =
                      [ 12, (I2sSharedBus, "bclk")
                        35, (I2sSharedBus, "ws")
                        40, (I2sSharedBus, "sd_out")
                        16, (PdmMics, "pdm_clk")
                        18, (PdmMics, "pdm_data") ] }

              let board = { plug pdmFront "PI40" (icepiAt 50_000_000) with host = NoHost }
              Expect.isTrue (BoardTop.micsOnPdm board) "the harness says so"

              let top = BoardTop.boardTop board viaPins lit
              let sim = Sim top.top
              Expect.equal (sim.Peek "pdm_clk") 0UL "the top drives the microphones' clock"
              Expect.throws (fun () -> sim.Peek "sd_in" |> ignore) "and has no I2S line in"

              // A -20 dBFS tone through the modulator model and the decimator
              // comes out of the I2S line at the level the CIC's droop predicts.
              let rate = BoardTop.boardRate board BoardTop.nominalRate
              let hz = 1_000.0
              let amplitude = 0.1

              let tone =
                  Seq.initInfinite (fun i ->
                      let v = amplitude * sin (2.0 * System.Math.PI * hz * float i / rate)
                      v, v)

              let signed (v: uint64) =
                  if v >= 0x800000UL then float (int64 v - 0x1000000L) else float v

              let heard =
                  pdmThroughI2s sim defaultPdmSimPins sharedBusSimPins BoardTop.pdmBitsPerFrame tone
                  |> Seq.skip 400
                  |> Seq.take 1024
                  |> Seq.map (fun (l, _) -> signed l / float (1 <<< (sampleWidth - 1)))
                  |> Array.ofSeq

              let rms = sqrt (Array.averageBy (fun v -> v * v) heard)
              let levelDb = 20.0 * log10 (rms * sqrt 2.0)
              let expectedDb = 20.0 * log10 amplitude + cicDroopDb pdmCicOrder BoardTop.pdmBitsPerFrame rate hz
              Expect.isLessThan (abs (levelDb - expectedDb)) 0.5 $"heard %.2f{levelDb} dBFS, expected %.2f{expectedDb}"

              let m: Mapping.Mapping =
                  { board = kv260
                    path =
                      { viaPins with
                          bindings =
                              viaPins.bindings
                              @ [ { need = ByName "record"; carrier = InHostRows }
                                  { need = ByName "limit"; carrier = OnPin "led" }
                                  { need = ByName "limit"; carrier = InRegisterMap } ] }
                    plugged = [ frontEnd, "J2" ]
                    sizes = [] }

              let text = Mapping.write m

              match Mapping.parse text with
              | Ok back ->
                  Expect.equal back m "every binding and harness survives the file"
                  Expect.equal (Mapping.write back) text "and writes the same bytes again"
              | Error why -> failtest why

              let presetOnly: Mapping.Mapping = { board = kv260; path = viaPins; plugged = []; sizes = [] }
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
              let sh = System.IO.File.ReadAllText out.run
              Expect.stringContains sh "openFPGALoader -b icepi-zero" "programmed the way the board's own Makefile does"
              Expect.stringContains sh "usage(\"TRELLIS_COMB\")" "the gate reads nextpnr-ecp5's utilisation"
              Expect.stringContains sh "cells=24288, blockRams=56, multipliers=28" "against the part's facts"

              let kvDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-kv260-gate-{System.Guid.NewGuid()}")
              let kv = Warp11.Build.write kvDir (BoardTop.boardTop kv260 viaHostMemory (tableDesign []))
              let kvSh = System.IO.File.ReadAllText kv.run
              Expect.stringContains kvSh "usage(\"CLB LUTs\")" "the gate reads Vivado's placed utilisation"
              Expect.stringContains kvSh "cells=117120, blockRams=144, multipliers=1248" "against the part's facts"
              Expect.stringContains kvSh "timing_summary_routed.rpt" "and the routed slack"

          testCase "the Go Board: the oscillator straight in, no multipliers asked for, every pin from its constraints" <| fun _ ->
              checkBoard goBoard
              Expect.isFalse (hasMultipliers goBoard.part) "HX has no multipliers"
              Expect.isFalse (hasLutRam goBoard.part) "and no LUT RAM"
              Expect.isFalse (hasPs goBoard.part) "and no processing system"

              match Mapping.parseBoard (Mapping.writeBoard goBoard) with
              | Ok back -> Expect.equal back goBoard "a board file carries the part and its facts"
              | Error why -> failtest why

              // The facts are data on the part, so a board file can say them —
              // and may leave them out on a part the library knows.
              let written = Mapping.writeBoard goBoard
              Expect.stringContains written "\"multipliers\": 0" "the file states what the part has"
              let factless = System.Text.RegularExpressions.Regex.Replace(written, ",\\s*\"facts\": \\{[^}]*\\}", "")
              Expect.isFalse (factless.Contains "facts") "stripped"
              match Mapping.parseBoard factless with
              | Ok back -> Expect.equal back goBoard "a known part fills its own facts in"
              | Error why -> failtest why
              match Mapping.parseBoard (factless.Replace("\"hx1k\"", "\"hx8k\"")) with
              | Ok _ -> failtest "an unknown part with no facts should be refused"
              | Error why -> Expect.stringContains why "facts" "naming what the file has to state"
              match Mapping.parseBoard (written.Replace("\"Ice40\"", "\"Ice40Hx\"")) with
              | Ok back -> Expect.equal back.part.family Family.Ice40 "the family's older names still read"
              | Error why -> failtest why

              Expect.throwsC
                  (fun () -> checkBoard { goBoard with fabricHz = 50_000_000 })
                  (fun e -> Expect.stringContains e.Message "no PLL" "an oscillator is the fabric clock, not a choice")

              // A design with streams needs a converter, and the board has none of
              // its own: a shared-bus I2S harness in the Pmod is where one goes.
              // The no-stream shape — one bit the host writes, on an LED — is
              // `hello`'s, and `boardTop` does not build it yet.
              let pmodI2s: Harness =
                  { name = "pmod-i2s"
                    plug = "pmod"
                    positions = [ 1, (I2sSharedBus, "bclk"); 2, (I2sSharedBus, "ws"); 3, (I2sSharedBus, "sd_in"); 4, (I2sSharedBus, "sd_out") ] }

              let path =
                  { viaPins with bindings = viaPins.bindings @ [ { need = ByName "limit"; carrier = OnPin "led1" } ] }

              let top = BoardTop.boardTop (plug pmodI2s "PMOD" goBoard) path lit
              let sim = Sim top.top
              sim.Tick()
              Expect.equal (sim.Peek "led1") 1UL "active high: lit is driven high"
              Expect.isNonEmpty top.registers "the bit the host writes is a register over the UART"

              let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-goboard-{System.Guid.NewGuid()}")
              let out = Warp11.Build.write dir top
              let sh = System.IO.File.ReadAllText out.run
              Expect.stringContains sh "synth_ice40 -top" "no -dsp on a part with no multipliers"
              Expect.stringContains sh "part=hx1k" "nextpnr's --hx1k"
              Expect.stringContains sh "package=vq100" "and its package"
              Expect.stringContains sh "iceprog -S" "loaded the way the FT2232H's channel A does"
              Expect.stringContains sh "usage(\"ICESTORM_LC\")" "the gate reads nextpnr-ice40's utilisation"
              Expect.stringContains sh "cells=1280, blockRams=16, multipliers=0" "against the part's facts"
              Expect.stringContains sh "report.json" "and writes the report the boards table is written from"
              let wrapper = System.IO.Directory.GetFiles(dir, "*_top.v") |> Array.head |> System.IO.File.ReadAllText
              Expect.stringContains wrapper "wire clk = clk25;" "25 MHz is the fabric clock: no PLL between them"
              Expect.isFalse (wrapper.Contains "SB_PLL40_PAD") "and no PLL primitive, which this package has not bonded"
              let pcf = System.IO.Directory.GetFiles(dir, "*.pcf") |> Array.head |> System.IO.File.ReadAllText
              Expect.stringContains pcf "set_io clk25 15" "the oscillator"
              Expect.stringContains pcf "set_io host_rx 73" "the UART in"
              Expect.stringContains pcf "set_io host_tx 74" "and out"
              Expect.stringContains pcf "set_io led1 56" "LED 1"
              Expect.stringContains pcf "set_io bclk 65" "Pmod position 1"

          testCase "a hand-written AXI top goes through the generator: its irq is wired, its constants reach the seam" <| fun _ ->
              // The shape GoL and GEP take: a complete top with a slave, a
              // master and an interrupt, handed to the generator as a record.
              let level, map = buildRegMap (fun r -> r.RwReg("level", 8, 0UL))
              let top =
                  defModuleClocked
                      axiClock
                      "IrqTop"
                      (fun p -> (axiLiteSlavePorts p map.apertureAddrWidth, p.outPort "irq" 1))
                      (fun (slave, irq) ->
                          let regs = regMapSlave slave map
                          anyBitSet (regs.value level) ==> irq)

              let t: BoardTop.BoardTop =
                  { board = kv260
                    name = "IrqTop"
                    top = top.def
                    registers = []
                    batch = None
                    map = map
                    target = BoardTop.targetOf kv260.part
                    seamExtras = [ "pub const GRID_WIDTH: usize = 64;" ] }

              let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-irq-{System.Guid.NewGuid()}")
              let out = Warp11.Build.write dir t
              let read name = System.IO.Directory.GetFiles(dir, name) |> Array.head |> System.IO.File.ReadAllText
              Expect.stringContains (read "*_bd.tcl") "[get_bd_pins IrqTop_0/irq] [get_bd_pins irq_concat/In0]" "the interrupt into pl_ps_irq0"
              Expect.stringContains (read "*.dts") "interrupts = <0x0 0x59 0x4>;" "and declared to the kernel as SPI 89"
              Expect.stringContains (read "*_layout.rs") "pub const GRID_WIDTH: usize = 64;" "the design's own constant, after the map's"
              Expect.stringContains (read "*_layout.rs") "LEVEL_OFFSET" "which is still there"
              Expect.isNonEmpty out.files "written"

          testCase "rows over the host link alone: the host's words in, the design's rows out, no converter" <| fun _ ->
              // The batch path of a board whose only connection is a UART —
              // the Icepi here, with no harness in its socket, since nothing
              // is on the pins.
              let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

              let swap =
                  BoardTop.design
                      "Swap"
                      (fun n -> n.streamIn ("in", stereo), n.streamOut ("out", stereo), n.valueIn ("gain", unsignedInt 2, 1UL))
                      (fun (i, o, _) -> i.stream |> Stream.map (fun (l, r) -> r, l) |> o)

              let top = BoardTop.boardTop icepi viaLink swap
              Expect.equal top.name "SwapLink" "the top's name says the host path"
              Expect.isTrue (top.registers |> List.exists (fun (n, _) -> n = "inLinkArm")) "the player's registers"
              Expect.isTrue (top.registers |> List.exists (fun (n, _) -> n = "outLinkSent")) "and the recorder's"
              Expect.isTrue (top.registers |> List.exists (fun (n, _) -> n = "gain")) "and the design's own"

              let sim = Sim top.top
              let cyclesPerBit = uartCyclesPerBit icepi.fabricHz 1_000_000
              let link, uart = serialClientForMap top.map sim (uartSimPins "host") cyclesPerBit
              let device = uart :> ISimDevice
              let offset name = (top.registers |> List.find (fun (n, _) -> n = name) |> snd).offset

              link.write32 (offset "inLinkArm") 1UL
              link.write32 (offset "outLinkArm") 1UL

              // Eight stereo rows, one field a word, sign-extended as the seam
              // reads them — the recorder's own convention, mirrored.
              let rows = [ for k in 1 .. 8 -> int64 (k * 1000), int64 (-k * 777) ]
              let word (v: int64) = uint64 (uint32 v)

              for l, r in rows do
                  uart.Send(SerialFrame.play (word l) @ SerialFrame.play (word r))

              let budget = 16 * 7 * 10 * cyclesPerBit * 3

              for _ in 1 .. budget do
                  device.Drive()
                  sim.Tick()
                  device.Sample()

              let words, _ = SerialFrame.streamedWords uart.Received
              let back = words |> List.map (fun w -> int64 (int32 (uint32 w)))
              Expect.equal back (rows |> List.collect (fun (l, r) -> [ r; l ])) "every row came back, swapped, in order"
              Expect.equal (link.read32 (offset "outLinkSent")) 16UL "the recorder counted every word"
              Expect.equal (link.read32 (offset "inLinkDropped")) 0UL "and nothing was lost on the way in"
              Expect.equal (link.read32 (offset "inLinkFill")) 0UL "the buffer drained"

              // And the same design on the Go Board builds for its UART, with no harness.
              let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-link-{System.Guid.NewGuid()}")
              let out = Warp11.Build.write dir (BoardTop.boardTop goBoard viaLink swap)
              Expect.stringContains (System.IO.File.ReadAllText out.run) "part=hx1k" "built for the part"
              let m: Mapping.Mapping = { board = goBoard; path = viaLink; plugged = []; sizes = [] }
              Expect.stringContains (Mapping.write m) "\"link\"" "and the mapping file names the path"

          testCase "the DE10-Lite: the oscillator straight in, Quartus's files from the board file, the gate on its reports" <| fun _ ->
              checkBoard de10Lite
              Expect.isTrue (hasMultipliers de10Lite.part) "144 multipliers"
              Expect.isFalse (hasLutRam de10Lite.part) "no MLAB on a MAX 10"
              Expect.isFalse (hasPs de10Lite.part) "no processing system"

              match Mapping.parseBoard (Mapping.writeBoard de10Lite) with
              | Ok back -> Expect.equal back de10Lite "a board file carries the part and its facts"
              | Error why -> failtest why

              Expect.stringContains (Mapping.writeBoard de10Lite) "\"Max10\"" "the family by its name"

              Expect.throwsC
                  (fun () -> checkBoard { de10Lite with fabricHz = 100_000_000 })
                  (fun e -> Expect.stringContains e.Message "no PLL" "an oscillator is the fabric clock")

              Expect.throwsC
                  (fun () -> checkBoard { de10Lite with tool = OpenFlow })
                  (fun e -> Expect.stringContains e.Message "Quartus does" "the open flow refuses a MAX 10")

              // Quartus's own spelling of the memory attribute, both kinds.
              let verilog = emitDesignFor Target.Max10 lutMemories.def
              Expect.stringContains verilog "(* ramstyle = \"logic\" *) reg [7:0] scratch" "a LUT RAM is logic on a MAX 10"
              Expect.stringContains verilog "(* ramstyle = \"logic\" *) reg [7:0] squares" "and so is a LUT ROM"

              // hello for it: the directory, from the mapping, with the flow's files.
              let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-de10-{System.Guid.NewGuid()}")
              let top = BoardTop.boardTopFor (Warp11.Designs.Hello.mappingFor de10Lite) Warp11.Designs.Hello.hello
              Expect.equal top.name "HelloUart" "over the UART cable"
              let out = Warp11.Build.write dir top
              let read name = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, name))
              let settings = read "hello_uart_top.qsf"
              Expect.stringContains settings "set_global_assignment -name DEVICE 10M50DAF484C7G" "the device"
              Expect.stringContains settings "set_location_assignment PIN_P11 -to clk50" "the oscillator's pin"
              Expect.stringContains settings "set_location_assignment PIN_V10 -to host_rx" "the cable's pins"
              Expect.stringContains settings "set_location_assignment PIN_A8 -to led0" "the LEDs"
              Expect.stringContains settings "set_instance_assignment -name IO_STANDARD \"3.3-V LVTTL\" -to led9" "and every pin's standard"
              Expect.stringContains (read "hello_uart_top.sdc") "create_clock -name clk50 -period 20.000" "the clock's period"
              Expect.stringContains (read "hello_uart_top.v") "wire clk = clk50;" "no PLL: the oscillator is the clock"
              let script = read "build.sh"
              Expect.stringContains script "quartus_sh --flow compile" "the flow"
              Expect.stringContains script "Total logic elements" "the gate reads the fit summary"
              Expect.stringContains script "Embedded Multiplier 9-bit elements" "two 9-bit elements to a multiplier"
              Expect.stringContains script "quartus_pgm -m jtag" "and loads over the USB-Blaster"
              Expect.equal out.run (System.IO.Path.Combine(dir, "build.sh")) "run is the script"

          testCase "a frame over the host link: beats counted in fabric, the rows out as frames, a wide field as lanes" <| fun _ ->
              // The counted path on a board with no host memory: the host
              // writes a count and a start, and reads the rows the design
              // draws for `0 … count-1` as streamed frames.
              let triple =
                  BoardTop.design
                      "Triple"
                      (fun n -> n.streamIn ("beats", pins1 ("beat", unsignedInt 16)), n.streamOut ("rows", pins2 ("twice", unsignedInt 17) ("wide", unsignedInt 40)))
                      (fun (i, o) -> i.stream |> Stream.mapTo (pins2 ("twice", unsignedInt 17) ("wide", unsignedInt 40)) (fun b -> pad 17 b + pad 17 b, pad 40 (cat b b)) |> o)

              let top = BoardTop.boardTop icepi viaCountLink triple
              Expect.equal top.name "TripleLink" "the top's name says the host path"
              let names = top.registers |> List.map fst
              Expect.equal names [ "rowsLinkArm"; "rowsLinkSent"; "rowsLinkDropped"; "beatsCountTotal"; "beatsCountStart"; "beatsCountBusy" ] "the recorder's registers, then the count's"
              Expect.contains top.seamExtras "pub const ROWS_WORDS: usize = 3;" "the seam says a row is three words: one, and two for the 40-bit field"

              let sim = Sim top.top
              let cyclesPerBit = uartCyclesPerBit icepi.fabricHz 1_000_000
              let link, uart = serialClientForMap top.map sim (uartSimPins "host") cyclesPerBit
              let device = uart :> ISimDevice
              let offset name = (top.registers |> List.find (fun (n, _) -> n = name) |> snd).offset

              link.write32 (offset "rowsLinkArm") 1UL
              link.write32 (offset "beatsCountTotal") 5UL
              link.write32 (offset "beatsCountStart") 1UL
              Expect.equal (link.read32 (offset "beatsCountBusy")) 1UL "busy while the rows leave"

              let budget = 15 * 7 * 10 * cyclesPerBit * 3

              for _ in 1 .. budget do
                  device.Drive()
                  sim.Tick()
                  device.Sample()

              let words, _ = SerialFrame.streamedWords uart.Received

              let expected =
                  [ for k in 0UL .. 4UL do
                        yield 2UL * k
                        yield (k <<< 16) ||| k
                        yield 0UL ]

              Expect.equal words expected "five rows, three words each: the sum, the wide field's low word, its padded top"
              Expect.equal (link.read32 (offset "beatsCountBusy")) 0UL "and done once the last row has gone"
              Expect.equal (link.read32 (offset "rowsLinkSent")) 15UL "the recorder counted every word"

              let m: Mapping.Mapping = { board = goBoard; path = viaCountLink; plugged = []; sizes = [] }
              Expect.stringContains (Mapping.write m) "\"countlink\"" "the mapping file names the path"
              Expect.equal (Mapping.parse (Mapping.write m)) (Ok m) "and reads it back"

          testCase "a field wider than a lane crosses the link as several words, low word first, both ways" <| fun _ ->
              let wide =
                  BoardTop.design
                      "Wide"
                      (fun n -> n.streamIn ("in", pins1 ("x", unsignedInt 48)), n.streamOut ("out", pins1 ("y", unsignedInt 48)))
                      (fun (i, o) -> i.stream |> o)

              let top = BoardTop.boardTop icepi viaLink wide
              Expect.contains top.seamExtras "pub const IN_WORDS: usize = 2;" "two words a row in"
              Expect.contains top.seamExtras "pub const OUT_WORDS: usize = 2;" "and out"
              let sim = Sim top.top
              let cyclesPerBit = uartCyclesPerBit icepi.fabricHz 1_000_000
              let link, uart = serialClientForMap top.map sim (uartSimPins "host") cyclesPerBit
              let device = uart :> ISimDevice
              let offset name = (top.registers |> List.find (fun (n, _) -> n = name) |> snd).offset
              link.write32 (offset "inLinkArm") 1UL
              link.write32 (offset "outLinkArm") 1UL
              let rows = [ 0x0000_ABCD_1234_5678UL; 0x0000_0001_0000_0002UL; 0x0000_FFFF_FFFF_FFFFUL ]

              for v in rows do
                  uart.Send(SerialFrame.play (v &&& 0xFFFF_FFFFUL) @ SerialFrame.play (v >>> 32))

              for _ in 1 .. 6 * 7 * 10 * cyclesPerBit * 3 do
                  device.Drive()
                  sim.Tick()
                  device.Sample()

              let words, _ = SerialFrame.streamedWords uart.Received
              let back = words |> List.chunkBySize 2 |> List.map (fun w -> w[0] ||| (w[1] <<< 32))
              Expect.equal back rows "every 48-bit row back whole, the high word's top sixteen bits zero"

          testCase "a row wider than a beat crosses the host's memory as several beats, both ways" <| fun _ ->
              // An image row of sixty-four pixels is one 512-bit field: four
              // beats of the KV260's memory in, four out, the identity between.
              let wide =
                  BoardTop.design
                      "WideRows"
                      (fun n -> n.streamIn ("in", pins1 ("row", unsignedInt 512)), n.streamOut ("out", pins1 ("row", unsignedInt 512)))
                      (fun (i, o) -> i.stream |> o)

              let top = BoardTop.boardTop kv260 viaHostMemory wide
              let batch = top.batch.Value
              let sim = Sim top.top
              let rows = 8
              let src, dst = 0x1000, 0x4000
              let ddr = SimAxiDdr(sim, 0x8000, dataBytes = 16, awEvery = 3, bDelay = 6)
              let axi = SimAxi.clientWith sim ddr.Cycle

              // Row r: byte k is (r * 16 + k) mod 251, so every byte of every beat
              // is accounted for in the order it crossed.
              let staged = [| for r in 0 .. rows - 1 -> [| for k in 0..63 -> byte ((r * 16 + k) % 251) |] |]

              for r in 0 .. rows - 1 do
                  Array.blit staged[r] 0 ddr.Memory (src + r * 64) 64

              axi.write32 batch.srcAddr.offset (uint64 src)
              axi.write32 batch.dstAddr.offset (uint64 dst)
              axi.write32 batch.frameCount.offset (uint64 rows)
              axi.write32 batch.start.offset 1UL
              let mutable spins = 0

              while axi.read32 batch.busy.offset <> 0UL && spins < 100_000 do
                  ddr.Cycle()
                  spins <- spins + 1

              Expect.isLessThan spins 100_000 "the batch finished"
              let back = [| for r in 0 .. rows - 1 -> ddr.Memory[dst + r * 64 .. dst + r * 64 + 63] |]
              Expect.equal back staged "every row back whole, beat for beat"

          testCase "a surface over the host link: latched whole when the host pulls, sent as frames, the pace the host's" <| fun _ ->
              // Four rows of registers that all move every cycle. A copy a row
              // a cycle would tear; a latch does not, and the rows' relation
              // holds in what comes back whatever cycle the pull landed on.
              let counters =
                  BoardTop.design
                      "Counters"
                      (fun n -> n.streamIn ("in", pins1 ("x", unsignedInt 8)), n.surfaceOut ("grid", pins2 ("a", unsignedInt 8) ("b", unsignedInt 8), 4))
                      (fun (i, surface) ->
                          Ir.lit 1UL 1 ==> i.stream.ready
                          let t = reg "t" 8
                          t + Ir.lit 1UL 8 ==> t

                          surface
                              [ for r in 0UL .. 3UL ->
                                    let a = reg $"a%d{r}" 8
                                    let b = reg $"b%d{r}" 8
                                    t + Ir.lit (r + 1UL) 8 ==> a
                                    Ir.lit 255UL 8 - (t + Ir.lit (r + 1UL) 8) ==> b
                                    a, b ])

              let top = BoardTop.boardTop icepi viaLink counters
              let names = top.registers |> List.map fst
              Expect.equal names [ "gridLinkPull"; "gridLinkSent"; "gridLinkMissed"; "inLinkArm"; "inLinkFill"; "inLinkDropped"; "inLinkStarved" ] "the surface's registers, then the player's"
              Expect.contains top.seamExtras "pub const GRID_ROWS: usize = 4;" "the seam says how many rows a snapshot is"
              Expect.contains top.seamExtras "pub const GRID_WORDS: usize = 2;" "and how many words a row"

              let sim = Sim top.top
              let cyclesPerBit = uartCyclesPerBit icepi.fabricHz 1_000_000
              let link, uart = serialClientForMap top.map sim (uartSimPins "host") cyclesPerBit
              let device = uart :> ISimDevice
              let offset name = (top.registers |> List.find (fun (n, _) -> n = name) |> snd).offset

              let drain cycles =
                  for _ in 1 .. cycles do
                      device.Drive()
                      sim.Tick()
                      device.Sample()

              let snapshot () =
                  let words, _ = SerialFrame.streamedWords (uart.TakeReceived())
                  words |> List.chunkBySize 2 |> List.map (fun w -> w[0], w[1])

              link.write32 (offset "gridLinkPull") 1UL
              link.write32 (offset "gridLinkPull") 1UL
              drain (8 * 7 * 10 * cyclesPerBit * 2)
              let first = snapshot ()
              Expect.equal first.Length 4 "one snapshot came back from two pulls"
              Expect.equal (link.read32 (offset "gridLinkMissed")) 1UL "the second pull landed mid-copy and was counted"

              let coherent (rows: (uint64 * uint64) list) =
                  let t = fst rows[0] - 1UL
                  Expect.equal rows [ for r in 0UL .. 3UL -> (t + r + 1UL) &&& 255UL, (255UL - (t + r + 1UL)) &&& 255UL ] "every row is from the same cycle"
                  t

              let t1 = coherent first
              link.write32 (offset "gridLinkPull") 1UL
              drain (8 * 7 * 10 * cyclesPerBit * 2)
              let t2 = coherent (snapshot ())
              Expect.notEqual t1 t2 "the surface moved on between pulls"
              Expect.equal (link.read32 (offset "gridLinkSent")) 16UL "two snapshots, eight words each"

          testCase "a surface into the host's memory: the freshest complete snapshot in a slot the host holds, then lets go" <| fun _ ->
              // The same four moving rows, into the KV260's DDR through the
              // keep-latest triple buffer: capture grants a slot holding a
              // whole coherent snapshot, release hands it back, and a second
              // capture sees a later one.
              let counters =
                  BoardTop.design
                      "Counters"
                      (fun n -> n.streamIn ("in", pins1 ("x", unsignedInt 8)), n.surfaceOut ("grid", pins2 ("a", unsignedInt 8) ("b", unsignedInt 8), 4))
                      (fun (i, surface) ->
                          Ir.lit 1UL 1 ==> i.stream.ready
                          let t = reg "t" 8
                          t + Ir.lit 1UL 8 ==> t

                          surface
                              [ for r in 0UL .. 3UL ->
                                    let a = reg $"a%d{r}" 8
                                    let b = reg $"b%d{r}" 8
                                    t + Ir.lit (r + 1UL) 8 ==> a
                                    Ir.lit 255UL 8 - (t + Ir.lit (r + 1UL) 8) ==> b
                                    a, b ])

              let top = BoardTop.boardTop kv260 viaHostMemory counters
              Expect.equal top.name "CountersBatch" "the host-memory top"
              let names = top.registers |> List.map fst
              Expect.contains names "gridMemBase" "the surface's base"
              Expect.contains names "gridMemCapture" "its capture"
              Expect.contains top.seamExtras "pub const GRID_MEM_BEATS: usize = 2;" "two rows a beat, four rows: two beats a snapshot"
              Expect.contains top.seamExtras "pub const GRID_MEM_SLOT_BYTES: usize = 32;" "a slot is the snapshot rounded to a power of two"
              Expect.isTrue (top.top.decls |> List.exists (function Output(n, _) -> n = "irq" | _ -> false)) "a surface top has an interrupt"

              let sim = Sim top.top
              let ddr = SimAxiDdr(sim, 0x4000, dataBytes = 16, awEvery = 3, bDelay = 6)
              let axi = SimAxi.clientWith sim ddr.Cycle
              let entry name = top.registers |> List.find (fun (n, _) -> n = name) |> snd

              let field name =
                  let e = entry name

                  match e.kind with
                  | RoField(bit, w) -> (axi.read32 e.offset >>> bit) &&& ((1UL <<< w) - 1UL)
                  | _ -> failwith $"{name} is not a field"

              let baseAddr = 0x2000UL
              axi.write32 (entry "gridMemBase").offset baseAddr

              let capture () =
                  axi.write32 (entry "gridMemCapture").offset 1UL
                  let mutable spins = 0

                  while field "gridMemReady" = 0UL && spins < 20_000 do
                      ddr.Cycle()
                      spins <- spins + 1

                  Expect.isLessThan spins 20_000 "a snapshot was granted"
                  let slot = int (field "gridMemSlot")
                  // Row r is lanes 2r and 2r+1 of the slot's two beats: a in the
                  // low byte of the first lane, b in the low byte of the second.
                  let at = int baseAddr + slot * 32

                  [ for r in 0..3 -> uint64 ddr.Memory[at + r * 8], uint64 ddr.Memory[at + r * 8 + 4] ]

              let coherent (rows: (uint64 * uint64) list) =
                  let t = fst rows[0] - 1UL
                  Expect.equal rows [ for r in 0UL .. 3UL -> (t + r + 1UL) &&& 255UL, (255UL - (t + r + 1UL)) &&& 255UL ] "every row is from the same cycle"
                  t

              let release () =
                  axi.write32 (entry "gridMemRelease").offset 1UL

                  for _ in 1..50 do
                      ddr.Cycle()

              // The first snapshot is the one latched at reset, before the
              // surface had moved — every row zero, as the hand top's always
              // was. The host lets it go and the next is live.
              Expect.equal (capture ()) [ for _ in 0..3 -> 0UL, 0UL ] "the first snapshot is the surface at reset"
              release ()
              let t1 = coherent (capture ())
              release ()
              let t2 = coherent (capture ())
              Expect.notEqual t1 t2 "the surface moved on between captures"
              Expect.equal (field "gridMemOverrun") 0UL "no capture landed while one was held"

          testCase "a design sized for its board: derived from the part, named by the mapping, refused when nobody asks" <| fun _ ->
              // A design whose one field is as wide as the part's multiplier
              // count lets it be sized, and refuses a part with none.
              let scaled =
                  BoardTop.sized "Scaled" (fun fit ->
                      let derived =
                          if fit.part.facts.multipliers = 0 then
                              failwith $"Scaled: a lane takes a multiplier and the {fit.part.device} has none — impossible on this part"
                          else
                              fit.part.facts.multipliers

                      let lanes = fit.size ("lanes", derived)

                      BoardTop.design
                          $"Scaled%d{lanes}"
                          (fun n -> n.streamIn ("in", pins1 ("x", unsignedInt lanes)), n.streamOut ("out", pins1 ("y", unsignedInt lanes)))
                          (fun (i, o) -> i.stream |> o))

              Expect.equal (BoardTop.boardTop icepi viaLink scaled).name "Scaled28Link" "derived from the Icepi's 28 multipliers"
              Expect.equal (BoardTop.boardTopSized icepi viaLink [ "lanes", 4 ] scaled).name "Scaled4Link" "or the mapping's word"

              Expect.throwsC
                  (fun () -> BoardTop.boardTopSized icepi viaLink [ "world", 4 ] scaled |> ignore)
                  (fun e -> Expect.stringContains e.Message "names a size 'world', and the design sizes nothing by that name" "a size nobody reads is refused")

              Expect.throwsC
                  (fun () -> BoardTop.boardTop goBoard viaLink scaled |> ignore)
                  (fun e -> Expect.stringContains e.Message "the hx1k has none — impossible" "and a part that cannot hold the smallest is refused by number")

              let m: Mapping.Mapping = { board = icepi; path = viaLink; plugged = []; sizes = [ "lanes", 4 ] }
              Expect.stringContains (Mapping.write m) "\"sizes\"" "the mapping file names the size"
              Expect.equal (Mapping.parse (Mapping.write m)) (Ok m) "and reads it back"
              Expect.equal (BoardTop.boardTopFor m scaled).name "Scaled4Link" "the mapping builds it at that size"

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
