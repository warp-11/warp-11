/// What a design is being built *for*: the target, as a set of independent
/// choices, and a board as a preset over them.
///
/// This exists because two kinds of number were previously written down in
/// several places at once and could disagree. The fabric clock frequency
/// decides every derived rate — an I2S sample rate, a baud rate, a timer — and
/// a design moved to a board with a different clock keeps its old divisors and
/// runs at the wrong rate with nothing complaining. The rest of what a build
/// needs — the part, the register path and its base, how the bitstream loads,
/// which package pin each port lands on — was restated in the block design's
/// Tcl, the constraints and the device tree, none of them generated. Here it
/// is stated once, and `Build` writes all of those from it.
///
/// **The axes are independent; a board is not a case.** A KV260 is one
/// preset over them and an iCEBreaker another, and a board nobody here has
/// seen is a third record rather than a third branch in every generator.
/// What the axes cannot combine into — a PS clock on a part with no PS, host
/// memory on a part with no bus — `checkBoard` refuses by name rather than a
/// generator silently choosing.
///
/// **A `Board` is passed, not ambient.** SpinalHDL puts frequency on the clock
/// domain and reads it from an implicit `ClockDomain.current`, which is the most
/// developed answer in the field and is deliberately not what this is: an
/// ambient stack is a second piece of hidden elaboration state beside the
/// builder's, and the number of things that actually need a frequency is small
/// enough that handing it over is no burden.
///
/// **What it deliberately does not hold.** The register map's aperture is a
/// property of the *design*, not the board — the same map generates the same
/// Rust layout on either bus, which is what lets a driver survive the move. A
/// build-script generator computes the address range it needs from the design's
/// `RegMap` and takes only the base from here. `notes/BUILD.md` is the plan
/// this serves; `notes/DEVICES.md` is where the general per-board mapping is
/// designed.
[<AutoOpen>]
module Warp11.Boards

/// The part's family: what the toolchain is told, and what the part can do.
/// A family rather than a vendor because the two facts that matter — the
/// memory kinds the fabric has, and whether a processing system sits beside
/// it — are per family, not per vendor.
type Family =
    /// Zynq UltraScale+: a PS beside the fabric, LUTRAM, block RAM, UltraRAM.
    | UltraScalePlus
    /// Lattice iCE40 UltraPlus: no PS, no LUTRAM; EBR and SPRAM.
    | Ice40UltraPlus
    /// Lattice ECP5: no PS; LUT RAM for what is written, DP16KD block RAM,
    /// 18x18 multipliers.
    | Ecp5

/// The part, as the toolchain names it.
type Part =
    { family: Family
      /// Vivado's `-part` (`xck26-sfvc784-2LV-c`), nextpnr's `--up5k`,
      /// nextpnr-ecp5's `--25k`.
      device: string
      /// nextpnr's `--package`; empty where the device string carries it.
      package: string
      /// The toolchain's own name for the board: Vivado's board part
      /// (`xilinx.com:kv260_som:part0:1.4`), which owns the PS
      /// configuration, or openFPGALoader's `-b` (`icepi-zero`), which knows
      /// the board's programmer. None for a bare part.
      boardPart: string option }

/// Which flow builds the bitstream.
type BuildTool =
    | Vivado
    /// yosys → nextpnr → icepack.
    | OpenFlow

/// Where the fabric clock comes from. The rate it arrives at is `fabricHz`
/// beside it: a crystal through a PLL lands somewhere else than it starts.
type Clock =
    /// A processing-system clock the app's overlay pins: the `zynqmp_clk`
    /// index (71 is `pl0`).
    | PsClock of index: int
    /// A crystal the wrapper takes through a PLL to `fabricHz` — or straight
    /// in, when the two are equal.
    | Crystal of hz: int
    /// An oscillator driving the clock pin at `fabricHz` directly.
    | Oscillator of hz: int

/// The bulk data path to the host, where the part has one: the PS slave port
/// an AXI master writes DDR through, its width, and the arena a `u-dma-buf`
/// node reserves for a design's buffers.
type HostMemoryFacts =
    { /// `S_AXI_HPC0_FPD`, `S_AXI_HP0_FPD`, …
      port: string
      width: int
      arenaBytes: int
      /// How many single-beat writes a master keeps in flight on this port.
      ///
      /// A fact about the port, not about the design, which is why it lives
      /// here: the cost of a write is a full AW+W+B round trip, and what hides
      /// that round trip is how many of them overlap. `Stdlib.fs`'s write
      /// master puts the sweet spot for an HP port at 8..16.
      ///
      /// It is derived rather than written at the call site because it was
      /// got wrong there: the batch top passed `4`, matching the *reader*'s
      /// `4` on the line above — where 4 counts bursts of 16 beats, not
      /// beats — and every hand-built design in the tree passed 8 or 16.
      writeOutstanding: int }

/// What satisfies a need on a target.
///
/// A carrier is the "how", and it is chosen per need rather than per design:
/// the point is that two needs may be carried differently, and the same need
/// may be carried two ways at once (`notes/DEVICES.md` §10h, rung 4). Which
/// *transport* a carrier then uses is the board's business — `InRegisterMap`
/// says a word in the host's register map, and `board.host` says whether the
/// host reaches it over AXI-Lite or a UART.
type Carrier =
    /// The converter on the board's pins.
    | OnPins
    /// A beat index minted in fabric — `0 …` as many as the host asks for —
    /// and nothing read. Only carries a stream *in*.
    | AsBeatCount
    /// Rows in the host's memory, position by order: the nth row is the nth
    /// beat. Carries a stream either way.
    | InHostRows
    /// Rows in the host's memory, position stated by the design — its first
    /// output field is the destination index. Only carries a stream *out*,
    /// and is what lets the work behind it finish in any order.
    | InHostRowsAt
    /// A word in the host's register map.
    | InRegisterMap
    /// Frames on the host link itself, interleaved with the register map's
    /// replies — what a board with one wire and no bus has to offer. The
    /// link back-pressures, so a design that outruns the baud waits unless
    /// something in front of it is allowed to drop.
    | InLinkFrames
    /// A one-bit value the design reports, on the package pin behind a
    /// connector port — an LED. The pin's own polarity decides whether the
    /// top inverts it, so a design never knows which way an LED is wired.
    | OnPin of port: string

/// Which need a binding is about.
///
/// A preset cannot name a design's needs — `viaCount` has to work for a
/// design whose stream is called `beats` and one whose stream is called `in`
/// — so a binding may address a need by name *or* by kind. By-name wins over
/// by-kind, which is what lets a mapping override one need of a preset
/// without restating the rest.
type NeedRef =
    | ByName of string
    | EveryStreamIn
    | EveryStreamOut
    | EveryValueIn

/// What carries one of a design's needs.
type Binding = { need: NeedRef; carrier: Carrier }

/// Which way a design's boundary reaches the world on a board: a binding per
/// need.
///
/// This was one enum of four values until 2026-09-22 (Jason: *"DataPath is
/// combining input / outputs into the same enum, for no good reason"*), then
/// briefly a source/sink pair, which UC4 showed was still too few slots — a
/// real boundary is a list. The four names below are the four combinations
/// anyone has built, kept as presets over the list exactly as boards are
/// presets over their axes.
type DataPath = { bindings: Binding list }

let private everything inCarrier outCarrier =
    { bindings =
        [ { need = EveryStreamIn; carrier = inCarrier }
          { need = EveryStreamOut; carrier = outCarrier }
          { need = EveryValueIn; carrier = InRegisterMap } ] }

/// The converter on the pins, both ways.
let viaPins = everything OnPins OnPins

/// Rows in from the host's memory, rows back out to it.
let viaHostMemory = everything InHostRows InHostRows

/// Counted in, rows out in the order they leave: a frame the design draws,
/// nothing read.
let viaCount = everything AsBeatCount InHostRows

/// Counted in, and each beat written where the design says.
let viaScatter = everything AsBeatCount InHostRowsAt

/// **Everything** that carries a need. A need may be bound more than once —
/// the aid's processed audio goes to the earphones *and* to a recorder — so
/// this is a list, and the single-carrier `carrierFor` below is the common
/// case of it.
///
/// By-name bindings shadow the by-kind ones rather than adding to them: a
/// mapping that names a need is saying where that one goes, and a preset's
/// "every stream out" should not then smuggle in a destination nobody asked
/// for. So naming a need once overrides, and naming it twice forks.
let carriersFor (path: DataPath) (kind: NeedRef) (name: string) : Carrier list =
    match path.bindings |> List.filter (fun b -> b.need = ByName name) with
    | [] -> path.bindings |> List.filter (fun b -> b.need = kind) |> List.map (fun b -> b.carrier)
    | named -> named |> List.map (fun b -> b.carrier)

/// What carries a need, where exactly one thing does. `None` when nothing
/// does, which `boardTop` refuses rather than guessing.
let carrierFor (path: DataPath) (kind: NeedRef) (name: string) : Carrier option =
    carriersFor path kind name |> List.tryHead

/// How a host reaches the design's registers.
///
/// A union rather than an optional base address because a generator has to
/// handle every case, and "no base address means a UART" is a convention a
/// reader would have to be told. The cases are `AxiLite`-with-a-base rather
/// than a bare `AxiLite` because `Warp11.AxiLite` is an `AutoOpen` module.
type HostDriver =
    /// No register slave at all: a fixed-function design.
    | NoHost
    /// A memory-mapped AXI-Lite aperture at this base. The *size* of the
    /// aperture is not here: it comes from the design's register map.
    | AxiLiteAt of baseAddr: uint64
    /// The register map spoken over a UART at this baud.
    | UartAt of baud: int

/// How a bitstream reaches the part.
type Loading =
    /// An app the OS's FPGA manager programs from this directory (`xmutil`).
    | OsApp of firmwareDir: string
    /// Into configuration SRAM, gone at power-off (`iceprog -S`).
    | Sram
    /// Into the SPI flash, surviving power-off (`iceprog`).
    | Flash

/// A device role a design's pins serve, as the bundles declare them: the
/// connector table is keyed by role, and the role fixes the port names.
type DeviceRole =
    /// `i2sPins p SeparateCodecs`: the Pmod I2S2's two rows.
    | I2sSeparateCodecs
    /// `i2sPins p SharedBus`: one bus every chip shares, no master clock —
    /// `bclk`, `ws`, `sd_in`, `sd_out`. A board has one I2S connector or
    /// the other, and the top takes the pinout the connector says.
    | I2sSharedBus
    /// `uartPins p "host"`.
    | HostUart
    /// The board's own clock input, for a wrapper that takes a crystal.
    | ClockIn
    /// The board's LEDs, active low where the wrapper says so.
    | Leds

/// One package pin, with the IO standard where the toolchain wants one, and
/// whether what is on it is lit (or asserted) by driving it low — a fact
/// about the board's wiring, never the design's.
type Pin =
    { pin: string
      standard: string option
      activeLow: bool }

/// Where a device role's ports land on this board.
type Connector =
    { role: DeviceRole
      /// Port name → package pin.
      pins: (string * Pin) list }

/// A socket on the board something plugs into — a Pmod, a Pi header — as the
/// package pin behind each of its positions. What is plugged in is a
/// `Harness`, which says what is on each position; the two meet at the
/// position number, so a cable moves between boards unchanged.
type Socket =
    { name: string
      /// `pmod`, `pi40`: a harness plugs only into a socket of its own kind.
      kind: string
      positions: (int * Pin) list }

/// A cable, or a hub, as its plug sees it: the device role and port on each
/// position. Written once, from the wiring, and shared by every board whose
/// socket it plugs into.
type Harness =
    { name: string
      plug: string
      positions: (int * (DeviceRole * string)) list }

/// One target: every choice a build needs, stated once.
type Board =
    { /// The preset's name, as the generated scaffolding refers to it; a
      /// hand-made record says `custom`.
      name: string
      part: Part
      tool: BuildTool
      clock: Clock
      /// The fabric clock, in hertz. Every derived rate divides this.
      fabricHz: int
      /// The bulk path to the host, where the part has one.
      hostMemory: HostMemoryFacts option
      host: HostDriver
      loading: Loading
      connectors: Connector list
      /// Where a harness can plug in. A board with none takes only what its
      /// connectors already carry.
      sockets: Socket list }

/// Whether the part has a processing system beside the fabric.
let hasPs (part: Part) =
    match part.family with
    | UltraScalePlus -> true
    | Ice40UltraPlus
    | Ecp5 -> false

/// Whether the fabric has LUT RAM — the storage `distributedMem` asks for.
let hasLutRam (part: Part) =
    match part.family with
    | UltraScalePlus
    | Ecp5 -> true
    | Ice40UltraPlus -> false

/// The pins a board gives a role, if it has that role at all.
let connectorFor (role: DeviceRole) (board: Board) =
    board.connectors |> List.tryFind (fun c -> c.role = role) |> Option.map (fun c -> c.pins)

/// What the axes cannot combine into, refused by name.
let checkBoard (board: Board) =
    let refuse (why: string) = failwith $"{board.name}: {why}"

    match board.tool, board.part.family with
    | Vivado, Ice40UltraPlus -> refuse "Vivado does not build an iCE40 part"
    | Vivado, Ecp5 -> refuse "Vivado does not build an ECP5 part"
    | OpenFlow, UltraScalePlus -> refuse "the open flow does not build an UltraScale+ part"
    | _ -> ()

    match board.clock with
    | PsClock _ when not (hasPs board.part) -> refuse "a PS clock on a part with no processing system"
    | Crystal hz when board.fabricHz < 1 || hz < 1 -> refuse "a crystal needs a rate"
    | Oscillator hz when hz <> board.fabricHz ->
        refuse $"an oscillator at %d{hz} Hz drives the fabric at %d{board.fabricHz} Hz — there is no PLL between them"
    | _ -> ()

    match board.host with
    | AxiLiteAt _ when not (hasPs board.part) -> refuse "AXI-Lite on a part with no processing system"
    | _ -> ()

    match board.hostMemory with
    | Some _ when not (hasPs board.part) -> refuse "host memory on a part with no processing system"
    | _ -> ()

    match board.loading, board.tool with
    | OsApp _, OpenFlow -> refuse "an OS app loads through the FPGA manager, which the open flow has no part for"
    | (Sram | Flash), Vivado -> refuse "SRAM and flash loading are the open flow's; Vivado programs through JTAG or an OS app"
    | _ -> ()

/// The KV260's fixed facts, at a fabric clock **the caller pins**: the
/// AXI-Lite aperture is at 0xB0000000, where every Warp 11 slave app on this
/// board lives, and the clock is whatever that app's device-tree overlay
/// programs on `pl0` (`zynqmp_clk` 71). The bulk path is `S_AXI_HPC0_FPD` at
/// 128 bits with an 8 MiB arena, which is what the batch apps use. The Pmod
/// I2S2 sits on carrier connector J2: the D/A on the top row, the A/D on the
/// bottom, both LVCMOS33 (bank 45 at 3.3 V).
///
/// **The clock is not one of the board's facts, and this is the reason a board
/// is created per project rather than shared.** Loading a bitstream does not
/// program the PS clock registers — each app's dtbo pins its own PL0 — so the
/// apps in this repository run the same board at 99.999001 MHz (the audio
/// apps, `gol`, `mandel`, most of GEP), 166.666672 MHz (`golfs`,
/// `mandelframe`, `mandelpod`, `gepbarrel`) and 249.997498 MHz (`gepunit`,
/// `gepiorig`). A single `kv260` value would be right for one of those and
/// silently wrong for the rest — and "silently" is the whole problem: a design
/// that keeps its old divisors on a new clock runs at the wrong rate with
/// nothing complaining.
///
/// So a project writes `let board = kv260At <its own clock>` once, beside the
/// design, and everything derived comes off that.
let kv260At (fabricHz: int) =
    let lvcmos33 (pin: string) = { pin = pin; standard = Some "LVCMOS33"; activeLow = false }

    { name = "kv260"
      part =
        { family = UltraScalePlus
          device = "xck26-sfvc784-2LV-c"
          package = ""
          boardPart = Some "xilinx.com:kv260_som:part0:1.4" }
      tool = Vivado
      clock = PsClock 71
      fabricHz = fabricHz
      hostMemory =
        Some
            { port = "S_AXI_HPC0_FPD"
              width = 128
              arenaBytes = 8 <<< 20
              // The top of `Stdlib.fs`'s stated 8..16 sweet spot for an HP
              // port, and what the frame design and Game of Life both use.
              writeOutstanding = 16 }
      host = AxiLiteAt 0xB0000000UL
      loading = OsApp "/lib/firmware/xilinx"
      connectors =
        [ { role = I2sSeparateCodecs
            pins =
              [ "mclk", lvcmos33 "H12"
                "lrclk", lvcmos33 "E10"
                "sclk", lvcmos33 "D10"
                "sdin", lvcmos33 "C11"
                "mclk2", lvcmos33 "B10"
                "lrclk2", lvcmos33 "E12"
                "sclk2", lvcmos33 "D11"
                "sdout", lvcmos33 "B11" ] } ]
      // Carrier connector J2, the one Pmod: the Pmod I2S2 above is what is
      // usually in it, and a harness plugged here replaces it.
      sockets =
        [ { name = "J2"
            kind = "pmod"
            positions =
              [ 1, lvcmos33 "H12"
                2, lvcmos33 "E10"
                3, lvcmos33 "D10"
                4, lvcmos33 "C11"
                7, lvcmos33 "B10"
                8, lvcmos33 "E12"
                9, lvcmos33 "D11"
                10, lvcmos33 "B11" ] } ] }

/// The KV260 at the clock the **audio** apps' overlays program — the one
/// binding shared by more than one project, because `audio-tone`,
/// `audio-gain`, `audio-passthru` and the toy I2S designs are all built for
/// the same 100 MHz and derive their sample rate from it.
///
/// The overlays actually say 99.999001 MHz. The round number is kept because
/// every rate in this repository is quoted from it and the difference is one
/// part in a hundred thousand — well inside `i2sRateTolerance`, and it picks
/// the same divisors. It is a rounding, not an assumption: a design that
/// cared would pass the exact figure.
let kv260 = kv260At 100_000_000

/// The baud the iCEBreaker's register map is spoken at over the FTDI's second
/// channel.
let iceBreakerBaud = 115_200

/// The iCEBreaker at a fabric clock **the caller pins**, for the same reason
/// `kv260At` exists: the number a design divides is the clock reaching its
/// `clk` port, and on this board that is rarely the crystal.
///
/// The part has one 12 MHz oscillator and no AXI, so every design here is
/// clocked either straight off the crystal or off an `SB_PLL40_PAD` in the
/// board's top wrapper — and the PLL is not optional for audio. `i2sMasterHz`
/// derives MCLK as `fabric / (2 * 256 * Fs)`, so 12 MHz can only ever present
/// 128x Fs, which is outside every ratio a CS5343 or CS4344 locks to. At
/// 24 MHz the same arithmetic lands on 256x exactly. A design that took the
/// crystal as its fabric clock would therefore frame perfectly and go silent
/// on the bench, with nothing in simulation to say why.
///
/// The Pmod I2S2 sits in PMOD1A; the host UART is the FTDI's second channel
/// on pins 6 (into the fabric) and 9 (out); the LEDs are wired to ground, so
/// the wrapper drives them low to light them.
let iceBreakerAt (fabricHz: int) =
    let bare (pin: int) = { pin = string pin; standard = None; activeLow = false }

    { name = "icebreaker"
      part =
        { family = Ice40UltraPlus
          device = "up5k"
          package = "sg48"
          boardPart = None }
      tool = OpenFlow
      clock = Crystal 12_000_000
      fabricHz = fabricHz
      hostMemory = None
      host = UartAt iceBreakerBaud
      loading = Sram
      connectors =
        [ { role = ClockIn; pins = [ "clk12", bare 35 ] }
          { role = I2sSeparateCodecs
            pins =
              [ "mclk", bare 4
                "lrclk", bare 2
                "sclk", bare 47
                "sdin", bare 45
                "mclk2", bare 3
                "lrclk2", bare 48
                "sclk2", bare 46
                "sdout", bare 44 ] }
          { role = HostUart
            pins = [ "host_rx", bare 6; "host_tx", bare 9 ] }
          { role = Leds
            pins =
              [ "ledr_n", { bare 11 with activeLow = true }
                "ledg_n", { bare 37 with activeLow = true } ] } ]
      sockets =
        [ { name = "PMOD1B"
            kind = "pmod"
            positions = [ 1, bare 43; 2, bare 38; 3, bare 34; 4, bare 31; 7, bare 42; 8, bare 36; 9, bare 32; 10, bare 28 ] } ] }

/// The iCEBreaker clocked straight off its 12 MHz crystal — no PLL, the shape
/// a design with no converter attached to it takes. No AXI on this part, which
/// is what makes it the useful second board: it is the one that catches an
/// assumption about the KV260 baked into something generic.
let iceBreaker = iceBreakerAt 12_000_000

/// The Icepi Zero at a fabric clock **the caller pins**: a Lattice ECP5-25F
/// (CABGA256) in a Raspberry Pi Zero footprint, a 50 MHz oscillator on M1,
/// the FT231X's UART on K16 (into the fabric) and K15 (out), five LEDs wired
/// pin → 1 kΩ → LED → ground, so **active high**, and the Pi's 40-pin header
/// as the one socket a harness plugs into. All of it from the board's own
/// `gateware/icepi-zero.lpf` and schematic (`cheyao/icepi-zero`, v1.4), every
/// I/O bank at 3.3 V.
///
/// **24 MHz, the iCEBreaker's clock, is not reachable from here**: the PLL's
/// phase detector wants 3.125 MHz or more, and 24/50 = 12/25 would need a
/// reference divisor of 25 — 2 MHz. `ecppll` lands on 23.333. 25 MHz is
/// 50/2 exactly, and a 48 kHz converter frames at 48 828.125 Hz there —
/// the KV260's rate at 100 MHz, so the two boards agree sample for sample.
///
/// The host link runs at 1 Mbaud: 25 cycles a bit at 25 MHz, and well inside
/// what the FT231X does.
let icepiAt (fabricHz: int) =
    let pin (site: string) = { pin = site; standard = Some "LVCMOS33"; activeLow = false }

    { name = "icepi"
      part =
        { family = Ecp5
          device = "25k"
          package = "CABGA256"
          boardPart = Some "icepi-zero" }
      tool = OpenFlow
      // An oscillator on the board, but `Crystal` in this record's sense:
      // what reaches the fabric goes through the wrapper's PLL.
      clock = Crystal 50_000_000
      fabricHz = fabricHz
      hostMemory = None
      host = UartAt 1_000_000
      loading = Sram
      connectors =
        [ { role = ClockIn; pins = [ "clk50", pin "M1" ] }
          { role = HostUart
            pins = [ "host_rx", pin "K16"; "host_tx", pin "K15" ] }
          { role = Leds
            pins = [ "led0", pin "E13"; "led1", pin "D14"; "led2", pin "E12"; "led3", pin "C13"; "led4", pin "D13" ] } ]
      // The header by physical pin number, as the board's constraints
      // comment each GPIO; power and ground positions carry nothing.
      sockets =
        [ { name = "PI40"
            kind = "pi40"
            positions =
              [ 3, pin "T2"; 5, pin "R2"; 7, pin "R1"; 8, pin "P1"; 10, pin "N1"
                11, pin "R3"; 12, pin "N4"; 13, pin "P3"; 15, pin "P2"; 16, pin "M2"
                18, pin "L1"; 19, pin "L2"; 21, pin "J1"; 22, pin "J2"; 23, pin "G2"
                24, pin "H2"; 26, pin "G1"; 27, pin "G3"; 28, pin "K3"; 29, pin "E1"
                31, pin "F3"; 32, pin "J3"; 33, pin "E3"; 35, pin "E4"; 36, pin "H3"
                37, pin "D4"; 38, pin "F1"; 40, pin "F2" ] } ] }

/// The Icepi Zero at 25 MHz, 50/2 exactly — see `icepiAt`.
let icepi = icepiAt 25_000_000

/// A harness plugged into one of a board's sockets: the board with the
/// connectors that plugging it in gives it — each role the harness carries,
/// its ports on the pins behind their positions, in place of whatever was in
/// that socket before.
/// Refused by name when the socket does not exist, is the wrong kind, or
/// lacks a position the harness uses.
let plug (harness: Harness) (socketName: string) (board: Board) : Board =
    let socket =
        match board.sockets |> List.tryFind (fun s -> s.name = socketName) with
        | Some s -> s
        | None ->
            let names = board.sockets |> List.map (fun s -> s.name) |> String.concat ", "
            failwith $"{harness.name}: the {board.name} has no socket '{socketName}' — it has [{names}]"

    if socket.kind <> harness.plug then
        failwith $"{harness.name}: a {harness.plug} plug does not go into {socketName}, a {socket.kind} socket"

    let pinAt (position: int) =
        match socket.positions |> List.tryFind (fun (p, _) -> p = position) with
        | Some(_, pin) -> pin
        | None -> failwith $"{harness.name}: position %d{position} is not wired on the {board.name}'s {socketName}"

    let given =
        harness.positions
        |> List.groupBy (fun (_, (role, _)) -> role)
        |> List.map (fun (role, positions) -> { role = role; pins = [ for position, (_, port) in positions -> port, pinAt position ] })

    // A socket holds one thing: whatever the board had on its pins — the
    // KV260's Pmod I2S2 in J2 — is unplugged, as is any connector of a role
    // the harness now gives.
    let roles = given |> List.map (fun c -> c.role)
    let socketPins = socket.positions |> List.map (fun (_, pin) -> pin.pin) |> Set.ofList

    let kept =
        board.connectors
        |> List.filter (fun c ->
            not (List.contains c.role roles)
            && not (c.pins |> List.exists (fun (_, pin) -> socketPins.Contains pin.pin)))

    { board with connectors = kept @ given }

/// The presets by name, for a verb or a dialog: the two boards this
/// repository has been built for, at the clocks their audio designs use.
let presets: (string * Board) list =
    [ "kv260", kv260
      "icebreaker", iceBreakerAt 24_000_000
      "icepi", icepi ]

/// A preset by name, or the names there are.
let preset (name: string) : Result<Board, string> =
    match presets |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, b) -> Ok b
    | None ->
        let names = presets |> List.map fst |> String.concat ", "
        Error $"no board '{name}' — one of: {names}"
