/// The board half of a mapping: a design on a board's boundary. The stereo
/// boundary becomes the I2S pins the board has, every control port becomes a
/// read-write register a host writes, and the register map is the same
/// definition the slave is elaborated from and the Rust seam is generated
/// from — so the host program and the fabric cannot disagree about it.
///
/// Two boards, two ways to reach the registers: the KV260 over AXI-Lite, the
/// iCEBreaker over a UART. The design between them is the one the canvas
/// drew; the top is what the board's toolchain builds.
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

/// What a board asks of a design before it will carry it: one stream, the
/// stereo boundary, and a rate the board's clock divides into — said with
/// the rate it would land on, so a design can be made for it.
let check (board: Board) (g: Graph) =
    if g.streams <> 1 then
        failwith $"{g.name}: a board carries one stream, and the design declares %d{g.streams}"

    for side, pins in [ "input", g.inputs; "output", g.outputs ] do
        if pins <> stereo then
            failwith $"{g.name}: the {board.name}'s converter needs the {side} box to be [{describePins stereo}], and it is [{describePins pins}]"

    let landed = boardRate board g.sampleRate

    if round landed <> round g.sampleRate then
        failwith $"{g.name}: the design is made for %g{g.sampleRate} Hz, and the {board.name}'s clock frames at %.3f{landed} Hz for it — make the design for that rate"

/// Register words the aperture holds: 256 bytes, as every audio app's.
let apertureAddrWidth = 8

/// The registers a design's control ports become: one read-write register
/// each, starting where the port starts (a number box's value, a setting;
/// the design's own controls at zero).
let registersOf (g: Graph) : (string * RegEntry) list * RegMap =
    let starting = startingValues g |> Map.ofList

    buildRegMapPinned apertureAddrWidth (fun r ->
        [ for name, f in controlPorts g ->
              name, r.RwReg(name, f.totalWidth, (starting |> Map.tryFind name |> Option.defaultValue 0UL)) ])

/// The design between the converter and the registers: the stream from the
/// link through the design's instance and back, every control port driven
/// from its register.
let private through (g: Graph) (regs: SlaveRegs) (entries: (string * RegEntry) list) (i2s: I2sLink) =
    // `design` is a Verilog reserved word; the instance is the drawn one.
    let io = (elaborate g).NewNamed "drawn"

    for (_, port), (_, entry) in List.zip io.controls entries do
        regs.value entry ==> port

    i2s.input
    |> Stream.mapTo (layoutOfList g.inputs) (fun (l, r) -> [ l; r ])
    |> streamThroughInstance io.ins.Head io.outs.Head
    |> Stream.mapTo sampleLayout (fun fields -> fields[0], fields[1])
    |> i2s.send

/// A design on a board, ready to build: the top module, the register map
/// and its entries by port name.
type BoardTop =
    { board: Board
      /// The top module's name, and the file's.
      name: string
      top: ModuleDef
      registers: (string * RegEntry) list
      map: RegMap
      /// The Verilog target the board's toolchain wants.
      target: Target }

/// The KV260: an AXI-Lite slave at the aperture every Warp 11 app lives at,
/// the Pmod I2S2's separate converters, the design between.
let kv260Top (board: Board) (g: Graph) : BoardTop =
    check board g
    let entries, map = registersOf g
    let name = $"{g.name}Axi"

    let top =
        defModuleClocked
            axiClock
            name
            (fun p -> axiLiteSlavePorts p map.apertureAddrWidth, i2sPins p SeparateCodecs)
            (fun (slavePorts, pins) ->
                let regs = regMapSlave slavePorts map
                let i2s = i2sLink "audio" pins board.fabricHz (int (round g.sampleRate)) stockBitsPerSlot
                through g regs entries i2s)

    { board = board
      name = name
      top = top.def
      registers = entries
      map = map
      target = Xilinx }

/// The baud the iCEBreaker's register map is spoken at.
let iceBaud = 115_200

/// The iCEBreaker: the same registers over a UART, the same converters.
let iceBreakerTop (board: Board) (g: Graph) : BoardTop =
    check board g
    let entries, map = registersOf g
    let name = $"{g.name}Ice"

    let top =
        defModule
            name
            (fun p -> uartPins p "host", i2sPins p SeparateCodecs)
            (fun (uart, pins) ->
                let regs = serialRegMapSlave "host" board.fabricHz iceBaud uart map
                let i2s = i2sLink "audio" pins board.fabricHz (int (round g.sampleRate)) stockBitsPerSlot
                through g regs entries i2s)

    { board = board
      name = name
      top = top.def
      registers = entries
      map = map
      target = Ice40 }

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
