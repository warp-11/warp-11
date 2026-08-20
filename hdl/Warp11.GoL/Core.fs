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

let gameOfLifeGridUnrolled
    (gensPerCycle: int)
    (gridWidth: int)
    (gridHeight: int)
    (loadEnable: Expr)
    (tickEnable: Expr)
    (loadRows: Expr list)
    : Expr list * Expr =
    if gensPerCycle < 1 then
        failwith $"gameOfLifeGrid: gensPerCycle must be >= 1, got %d{gensPerCycle}"

    if gridWidth < 3 || gridWidth > 64 || gridHeight < 3 || gridHeight > 64 then
        failwith $"gameOfLifeGrid: width/height must be 3..64, got %d{gridWidth}x%d{gridHeight}"

    if List.length loadRows <> gridHeight then
        failwith $"gameOfLifeGrid: %d{List.length loadRows} load rows for %d{gridHeight} grid rows"

    for row in loadRows do
        if width row <> gridWidth then
            failwith $"gameOfLifeGrid: a %d{width row}-bit load row for a %d{gridWidth}-wide grid"

    let cells =
        [ for y in 0 .. gridHeight - 1 -> [ for x in 0 .. gridWidth - 1 -> regBit $"cell_%d{y}_%d{x}" ] ]

    // GoL's speed of light is one cell per generation, so k rule applications
    // compose combinationally: k-1 named intermediate layers, then the final
    // application lands in the cell register. k = 1 declares no wires and is
    // emission-identical to the un-unrolled form.
    let penultimate =
        (cells, [ 1 .. gensPerCycle - 1 ])
        ||> List.fold (fun grid stage ->
            [ for y in 0 .. gridHeight - 1 ->
                  [ for x in 0 .. gridWidth - 1 ->
                        let layer = wireBit $"gen_%d{stage}_%d{y}_%d{x}"
                        nextCell grid y x ==> layer
                        layer ] ])

    for y in 0 .. gridHeight - 1 do
        let loadRow = List.item y loadRows

        for x in 0 .. gridWidth - 1 do
            let cell = List.item x (List.item y cells)

            If loadEnable (fun () -> slice x x loadRow ==> cell)

            Else (fun () -> If tickEnable (fun () -> nextCell penultimate y x ==> cell))

    let packedRows =
        [ for row in cells ->
              match row with
              | low :: rest -> List.fold (fun acc c -> cat c acc) low rest
              | [] -> failwith "unreachable: width >= 3" ]

    let population =
        countWhere (bitsNeeded (gridWidth * gridHeight)) id (List.concat cells)

    packedRows, population

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
let gameOfLifeGrid (gridWidth: int) (gridHeight: int) = gameOfLifeGridUnrolled 1 gridWidth gridHeight

/// The grid at ports, for the Sim and the differential oracle: load rows in,
/// packed rows and the population out. The tutorial walks this at a small
/// grid; the silicon config only ever exists inside the AXI wrapper.
/// `gensPerCycle` composes the rule combinationally — every Sim tick and
/// silicon clock advances that many generations (the act-5 unroll).
let golHarnessUnrolled (gensPerCycle: int) (gridWidth: int) (gridHeight: int) =
    let suffix = if gensPerCycle = 1 then "" else $"X%d{gensPerCycle}"

    design $"GameOfLife%d{gridWidth}x%d{gridHeight}%s{suffix}" (fun () ->
        let loadEnable = inputBit "load_enable"
        let tickEnable = inputBit "tick_enable"

        let loadRows =
            [ for y in 0 .. gridHeight - 1 -> input $"load_row_%d{y}" gridWidth ]

        let rows, population =
            gameOfLifeGridUnrolled gensPerCycle gridWidth gridHeight loadEnable tickEnable loadRows

        for y, row in List.indexed rows do
            let rowOut = output $"row_%d{y}" gridWidth
            row ==> rowOut

        let populationOut = output "population" (width population)
        population ==> populationOut)

let golHarness = golHarnessUnrolled 1

/// The harness a live view drives: the same grid at ports, plus the generation
/// counter the board's wrapper already keeps in fabric. A host could count its
/// own ticks instead, but then "stop when the generation reaches 1000" would be
/// a question about the host rather than about the design — and the whole point
/// of stepping a design is to ask questions about the design. A load restarts
/// the count, as it does on the board.
let golLiveHarness (gridWidth: int) (gridHeight: int) =
    design $"GameOfLifeLive%d{gridWidth}x%d{gridHeight}" (fun () ->
        let loadEnable = inputBit "load_enable"
        let tickEnable = inputBit "tick_enable"

        let loadRows =
            [ for y in 0 .. gridHeight - 1 -> input $"load_row_%d{y}" gridWidth ]

        let rows, population =
            gameOfLifeGrid gridWidth gridHeight loadEnable tickEnable loadRows

        for y, row in List.indexed rows do
            let rowOut = output $"row_%d{y}" gridWidth
            row ==> rowOut

        let populationOut = output "population" (width population)
        population ==> populationOut

        let genCount = reg "gen_count" 32
        If loadEnable (fun () -> lit 0UL 32 ==> genCount)
        Else (fun () -> If tickEnable (fun () -> genCount + lit 1UL 32 ==> genCount))

        let generation = output "generation" 32
        genCount ==> generation)

/// The unroll probe: the grid alone at ports, no population — an OOC run on
/// this measures exactly the k-generation update cone, nothing else.
let golProbe (gensPerCycle: int) (gridWidth: int) (gridHeight: int) =
    design $"GolProbeX%d{gensPerCycle}" (fun () ->
        let loadEnable = inputBit "load_enable"
        let tickEnable = inputBit "tick_enable"

        let loadRows =
            [ for y in 0 .. gridHeight - 1 -> input $"load_row_%d{y}" gridWidth ]

        let rows, _ =
            gameOfLifeGridUnrolled gensPerCycle gridWidth gridHeight loadEnable tickEnable loadRows

        for y, row in List.indexed rows do
            let rowOut = output $"row_%d{y}" gridWidth
            row ==> rowOut)
