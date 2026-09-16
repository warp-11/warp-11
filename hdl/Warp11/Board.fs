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

/// The part, as the toolchain names it.
type Part =
    { family: Family
      /// Vivado's `-part` (`xck26-sfvc784-2LV-c`), nextpnr's `--up5k`.
      device: string
      /// nextpnr's `--package`; empty where the device string carries it.
      package: string
      /// Vivado's board part (`xilinx.com:kv260_som:part0:1.4`), which owns
      /// the PS configuration; none for a bare part.
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
type HostMemory =
    { /// `S_AXI_HPC0_FPD`, `S_AXI_HP0_FPD`, …
      port: string
      width: int
      arenaBytes: int }

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
    /// `uartPins p "host"`.
    | HostUart
    /// The board's own clock input, for a wrapper that takes a crystal.
    | ClockIn
    /// The board's LEDs, active low where the wrapper says so.
    | Leds

/// One package pin, with the IO standard where the toolchain wants one.
type Pin =
    { pin: string
      standard: string option }

/// Where a device role's ports land on this board.
type Connector =
    { role: DeviceRole
      /// Port name → package pin.
      pins: (string * Pin) list }

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
      hostMemory: HostMemory option
      host: HostDriver
      loading: Loading
      connectors: Connector list }

/// Whether the part has a processing system beside the fabric.
let hasPs (part: Part) =
    match part.family with
    | UltraScalePlus -> true
    | Ice40UltraPlus -> false

/// Whether the fabric has LUT RAM — the storage `distributedMem` asks for.
let hasLutRam (part: Part) =
    match part.family with
    | UltraScalePlus -> true
    | Ice40UltraPlus -> false

/// The pins a board gives a role, if it has that role at all.
let connectorFor (role: DeviceRole) (board: Board) =
    board.connectors |> List.tryFind (fun c -> c.role = role) |> Option.map (fun c -> c.pins)

/// What the axes cannot combine into, refused by name.
let checkBoard (board: Board) =
    let refuse (why: string) = failwith $"{board.name}: {why}"

    match board.tool, board.part.family with
    | Vivado, Ice40UltraPlus -> refuse "Vivado does not build an iCE40 part"
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
    let lvcmos33 (pin: string) = { pin = pin; standard = Some "LVCMOS33" }

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
              arenaBytes = 8 <<< 20 }
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
                "sdout", lvcmos33 "B11" ] } ] }

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
    let bare (pin: int) = { pin = string pin; standard = None }

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
            pins = [ "ledr_n", bare 11; "ledg_n", bare 37 ] } ] }

/// The iCEBreaker clocked straight off its 12 MHz crystal — no PLL, the shape
/// a design with no converter attached to it takes. No AXI on this part, which
/// is what makes it the useful second board: it is the one that catches an
/// assumption about the KV260 baked into something generic.
let iceBreaker = iceBreakerAt 12_000_000

/// The presets by name, for a verb or a dialog: the two boards this
/// repository has been built for, at the clocks their audio designs use.
let presets: (string * Board) list =
    [ "kv260", kv260
      "icebreaker", iceBreakerAt 24_000_000 ]

/// A preset by name, or the names there are.
let preset (name: string) : Result<Board, string> =
    match presets |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, b) -> Ok b
    | None ->
        let names = presets |> List.map fst |> String.concat ", "
        Error $"no board '{name}' — one of: {names}"
