/// The first design on any board: the LEDs show a register the host writes
/// over the board's link. It needs nothing but the board and its USB, and
/// it proves the clock, the link and the LEDs together before anything
/// else is asked of them — which is why it is the bring-up order for every
/// board here and for any board a person adds (`docs/add-a-board.md`).
///
/// It declares one value and, per LED the board has, one bit it reports;
/// the mapping binds each bit to its LED's pin, so the design never names
/// one. A board with no LEDs of its own — the KV260's are the processing
/// system's — gets the register alone, which the host reads back.
module Warp11.Designs.Hello

open Warp11

/// The LEDs a board has, by the port names its connector gives them.
let ledsOf (board: Board) : string list =
    connectorFor Leds board |> Option.map (List.map fst) |> Option.defaultValue []

/// What hello asks of the world: the pattern, and a light per LED.
type HelloIo =
    { pattern: Expr
      lights: (Expr -> unit) list }

/// The design, sized by the board's LEDs.
let hello: BoardTop.Design =
    BoardTop.sized "Hello" (fun fit ->
        let leds = ledsOf fit.board
        let width = max 1 leds.Length

        BoardTop.design
            "Hello"
            (fun n ->
                { pattern = n.valueIn ("pattern", unsignedInt width)
                  lights = [ for led in leds -> n.valueOut (led, unsignedInt 1) ] })
            (fun io ->
                let pattern = wire "pattern_bits" width
                io.pattern ==> pattern
                io.lights |> List.iteri (fun k light -> light (slice k k pattern))))

/// Hello's mapping for a board: the host's registers, and each reported bit
/// on the LED of its name.
let mappingFor (board: Board) : Mapping.Mapping =
    { board = board
      path = { viaRegisters with bindings = viaRegisters.bindings @ [ for led in ledsOf board -> { need = ByName led; carrier = OnPin led } ] }
      plugged = []
      sizes = [] }

/// Hello on the iCEBreaker: over the UART, three LEDs.
let helloOnIceBreaker = (BoardTop.boardTopFor (mappingFor (iceBreakerAt 24_000_000)) hello).top

/// Hello on the KV260: over AXI-Lite, the register alone.
let helloOnKv260 = (BoardTop.boardTopFor (mappingFor kv260) hello).top
