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
let private through (g: Graph) (control: string -> Expr -> Expr) (i2s: I2sLink) =
    // `design` is a Verilog reserved word; the instance is the drawn one.
    let io = (elaborate g).NewNamed "drawn"

    for name, port in io.controls do
        control name port ==> port

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
      /// Empty when the board has no host: the controls are then baked at
      /// their starting values.
      registers: (string * RegEntry) list
      map: RegMap
      /// The Verilog target the board's toolchain wants.
      target: Target }

/// The emitter's target for a part.
let targetOf (part: Part) =
    match part.family with
    | UltraScalePlus -> Xilinx
    | Ice40UltraPlus -> Ice40

/// The design on a board: the converter on the board's I2S pins, the
/// registers reached the way the board's host reaches them.
///
/// The top's name says the host path — `GainPatchAxi`, `GainPatchUart`,
/// `GainPatchTop` — because that is what changes its port list; the board
/// is the file's business, not the module's.
let boardTop (board: Board) (g: Graph) : BoardTop =
    checkBoard board
    check board g
    let entries, map = registersOf g
    let rate = int (round g.sampleRate)

    let fromRegisters (regs: SlaveRegs) =
        let byName = Map.ofList entries
        fun (name: string) (_: Expr) -> regs.value byName[name]

    let baked =
        let starting = startingValues g |> Map.ofList
        fun (name: string) (port: Expr) -> lit (starting |> Map.tryFind name |> Option.defaultValue 0UL) (width port)

    let name, top, registers =
        match board.host with
        | AxiLiteAt _ ->
            let name = $"{g.name}Axi"

            let top =
                defModuleClocked
                    axiClock
                    name
                    (fun p -> axiLiteSlavePorts p map.apertureAddrWidth, i2sPins p SeparateCodecs)
                    (fun (slavePorts, pins) ->
                        let regs = regMapSlave slavePorts map
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through g (fromRegisters regs) i2s)

            name, top.def, entries
        | UartAt baud ->
            let name = $"{g.name}Uart"

            let top =
                defModule
                    name
                    (fun p -> uartPins p "host", i2sPins p SeparateCodecs)
                    (fun (uart, pins) ->
                        let regs = serialRegMapSlave "host" board.fabricHz baud uart map
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through g (fromRegisters regs) i2s)

            name, top.def, entries
        | NoHost ->
            let name = $"{g.name}Top"

            let top =
                defModule
                    name
                    (fun p -> i2sPins p SeparateCodecs)
                    (fun pins ->
                        let i2s = i2sLink "audio" pins board.fabricHz rate stockBitsPerSlot
                        through g baked i2s)

            name, top.def, []

    { board = board
      name = name
      top = top
      registers = registers
      map = map
      target = targetOf board.part }

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
