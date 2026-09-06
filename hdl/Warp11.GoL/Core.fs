/// Conway's Game of Life — the tutorial core. Every cell is a 1-bit register
/// updated in a single clock cycle: B3/S23 over the Moore neighborhood with a
/// dead border (`neighborhood Stencil.Moore Edge.Zero` + `countWhere` — the
/// library pieces this design drove into the stdlib).
module Warp11.GoL.Core

open Warp11

/// Bits to count 0..n inclusive — the population port's width.
let bitsNeeded (n: int) =
    let mutable b = 1

    while (1UL <<< b) <= uint64 n do
        b <- b + 1

    b

/// One application of B3/S23 over a grid of 1-bit expressions — the
/// composable form the unrolled elaboration chains: 8 neighbors, so 4 bits
/// carries the count.
let private nextCell (grid: Expr list list) (y: int) (x: int) : Expr =
    let neighbors =
        countWhere 4 id (neighborhood Stencil.Moore Edge.Zero grid y x)

    eq neighbors (lit 3UL 4)
    ||| (List.item x (List.item y grid) &&& eq neighbors (lit 2UL 4))

/// The grid, elaborated inline in the current module (the `axiLiteSlave`
/// pattern): declares the cell registers, returns the packed rows (bit x of
/// row y = cell (y, x)) and the live-cell count. The caller owns the boundary
/// — a harness lands rows on ports, the AXI wrapper feeds them to
/// `snapshotSource` — so the 64×64 config never pays for 8k bits of module
/// ports it would not use.
///
/// `loadEnable` wins over `tickEnable` (a load must land regardless of the
/// pacing FSM); with neither high every cell holds. `loadRows` must be
/// declared signals (ports or regs) — `slice` takes named operands.
let gameOfLifeGrid (gridWidth: int) (gridHeight: int) (loadEnable: Expr) (tickEnable: Expr) (loadRows: Expr list) : Expr list * Expr =
    if gridWidth < 3 || gridWidth > 64 || gridHeight < 3 || gridHeight > 64 then
        failwith $"gameOfLifeGrid: width/height must be 3..64, got %d{gridWidth}x%d{gridHeight}"

    if List.length loadRows <> gridHeight then
        failwith $"gameOfLifeGrid: %d{List.length loadRows} load rows for %d{gridHeight} grid rows"

    for row in loadRows do
        if width row <> gridWidth then
            failwith $"gameOfLifeGrid: a %d{width row}-bit load row for a %d{gridWidth}-wide grid"

    let cells =
        [ for y in 0 .. gridHeight - 1 -> [ for x in 0 .. gridWidth - 1 -> regBit $"cell_%d{y}_%d{x}" ] ]

    for y in 0 .. gridHeight - 1 do
        let loadRow = List.item y loadRows

        for x in 0 .. gridWidth - 1 do
            let cell = List.item x (List.item y cells)

            ifElse [
                (loadEnable, fun () -> slice x x loadRow ==> cell)
                (otherwise, fun () -> If tickEnable (fun () -> nextCell cells y x ==> cell)) ]

    let packedRows =
        [ for row in cells ->
              match row with
              | low :: rest -> List.fold (fun acc c -> cat c acc) low rest
              | [] -> failwith "unreachable: width >= 3" ]

    let population =
        countWhere (bitsNeeded (gridWidth * gridHeight)) id (List.concat cells)

    packedRows, population

/// The grid at ports, for the Sim and the differential oracle: load rows in,
/// packed rows and the population out. The tutorial walks this at a small
/// grid; the silicon config only ever exists inside the AXI wrapper.
let golHarness (gridWidth: int) (gridHeight: int) =
    defModule
        $"GameOfLife%d{gridWidth}x%d{gridHeight}"
        (fun p ->
            (p.inPort "load_enable" 1,
             p.inPort "tick_enable" 1,
             [ for y in 0 .. gridHeight - 1 -> p.inPort $"load_row_%d{y}" gridWidth ],
             [ for y in 0 .. gridHeight - 1 -> p.outPort $"row_%d{y}" gridWidth ],
             p.outPort "population" (bitsNeeded (gridWidth * gridHeight))))
        (fun (loadEnable, tickEnable, loadRows, rowOuts, populationOut) ->
            let rows, population =
                gameOfLifeGrid gridWidth gridHeight loadEnable tickEnable loadRows

            for rowOut, row in List.zip rowOuts rows do
                row ==> rowOut

            population ==> populationOut)

/// The harness a live view drives: the same grid at ports, plus the generation
/// counter the board's wrapper already keeps in fabric. A host could count its
/// own ticks instead, but then "stop when the generation reaches 1000" would be
/// a question about the host rather than about the design — and the whole point
/// of stepping a design is to ask questions about the design. A load restarts
/// the count, as it does on the board.
let golLiveHarness (gridWidth: int) (gridHeight: int) =
    defModule
        $"GameOfLifeLive%d{gridWidth}x%d{gridHeight}"
        (fun p ->
            (p.inPort "load_enable" 1,
             p.inPort "tick_enable" 1,
             [ for y in 0 .. gridHeight - 1 -> p.inPort $"load_row_%d{y}" gridWidth ],
             [ for y in 0 .. gridHeight - 1 -> p.outPort $"row_%d{y}" gridWidth ],
             p.outPort "population" (bitsNeeded (gridWidth * gridHeight)),
             p.outPort "generation" 32))
        (fun (loadEnable, tickEnable, loadRows, rowOuts, populationOut, generation) ->
            let rows, population =
                gameOfLifeGrid gridWidth gridHeight loadEnable tickEnable loadRows

            for rowOut, row in List.zip rowOuts rows do
                row ==> rowOut

            population ==> populationOut

            let genCount = reg "gen_count" 32
            ifElse [
                (loadEnable, fun () -> lit 0UL 32 ==> genCount)
                (otherwise, fun () -> If tickEnable (fun () -> genCount + lit 1UL 32 ==> genCount)) ]

            genCount ==> generation)