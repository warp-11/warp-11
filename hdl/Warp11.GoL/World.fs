/// The Game of Life as a design: the world a surface the host takes whole,
/// the seed rows the host sends, the pace a register. Nothing in it names
/// a bus or a wire — the mapping says how the world reaches the host, and
/// on a board whose only connection is a UART that is snapshots as frames
/// on it, pulled by the host. The KV260's conflate into DDR stays
/// `Wrapper.fs`'s own top until the host-memory path carries a surface.
module Warp11.GoL.World

open Warp11
open Warp11.GoL.Core

/// Words a row of the world takes, as the host reads it: bit x of word k
/// is cell (y, 32k + x).
let wordsPerRow (gridWidth: int) = (gridWidth + 31) / 32

/// A row as the host sees it: whole words, low word first.
let rowLayout (gridWidth: int) : Layout<Expr list> =
    layoutOfList
        [ for k in 0 .. wordsPerRow gridWidth - 1 -> $"cells%d{k}", unsignedInt (min 32 (gridWidth - 32 * k)) ]

/// What the design asks of the world, as handles.
type WorldIo =
    { /// The world to start from, a row a beat, top row first; the last
      /// row loads the grid whole.
      seed: BoardTop.Incoming<Expr list>
      /// The world as it is, taken when the host asks.
      world: Expr list list -> unit
      /// Generations advance while set.
      run: Expr
      /// Cycles between generations while running; 0 and 1 both mean every
      /// cycle.
      intervalCycles: Expr
      /// Generations since the last seed.
      generation: Expr -> unit
      /// Live cells.
      population: Expr -> unit }

/// The world at a size the caller states, as a design any board top builds.
let lifeAt (gridWidth: int) (gridHeight: int) : BoardTop.Design =
    let layout = rowLayout gridWidth
    let words = wordsPerRow gridWidth
    let populationWidth = bitsNeeded (gridWidth * gridHeight)

    BoardTop.design
        $"Life%d{gridWidth}x%d{gridHeight}"
        (fun n ->
            { seed = n.streamIn ("seed", layout)
              world = n.surfaceOut ("world", layout, gridHeight)
              run = n.valueIn ("run", unsignedInt 1)
              intervalCycles = n.valueIn ("intervalCycles", unsignedInt 32, 1UL)
              generation = n.valueOut ("generation", unsignedInt 32)
              population = n.valueOut ("population", unsignedInt populationWidth) })
        (fun io ->
            // ---- the seed: rows land in staging as they arrive, and the
            // last one hands the whole surface to the grid in one cycle, as
            // the grid wants it. A seed is never refused: a row that arrives
            // mid-generation lands in staging and the generation goes on.
            let seed = io.seed.stream
            lit 1UL 1 ==> seed.ready
            let rowIndexWidth = ceilLog2 gridHeight
            let landing = reg "seed_landing" rowIndexWidth

            let staging =
                [ for y in 0 .. gridHeight - 1 -> [ for k in 0 .. words - 1 -> reg $"seed_%d{y}_%d{k}" (min 32 (gridWidth - 32 * k)) ] ]

            let arrived = seed.valid
            let lastRow = eq landing (lit (uint64 (gridHeight - 1)) rowIndexWidth)

            If arrived (fun () ->
                for y in 0 .. gridHeight - 1 do
                    If (eq landing (lit (uint64 y) rowIndexWidth)) (fun () ->
                        for k in 0 .. words - 1 do
                            List.item k seed.payload ==> List.item k (List.item y staging))

                mux lastRow (lit 0UL rowIndexWidth) (landing + lit 1UL rowIndexWidth) ==> landing)

            let load = regBit "seed_load"
            (arrived &&& lastRow) ==> load

            let loadRows =
                [ for y in 0 .. gridHeight - 1 ->
                      let row = wire $"seed_row_%d{y}" gridWidth

                      match List.item y staging with
                      | low :: rest -> List.fold (fun acc w -> cat w acc) low rest ==> row
                      | [] -> failwith "unreachable: a row has a word"

                      row ]

            // ---- the pace: a generation every `intervalCycles` while running.
            let countdown = reg "pace_countdown" 32
            let due = eq countdown (lit 0UL 32)
            let tick = wireBit "tick"
            (io.run &&& due) ==> tick

            ifElse
                [ (bnot io.run, fun () -> lit 0UL 32 ==> countdown)
                  (due, fun () -> mux (eq io.intervalCycles (lit 0UL 32)) (lit 0UL 32) (io.intervalCycles - lit 1UL 32) ==> countdown)
                  (otherwise, fun () -> countdown - lit 1UL 32 ==> countdown) ]

            // ---- the grid, and its rows named so they can be sliced.
            let grid = gameOfLifeGrid gridWidth gridHeight { load = load; tick = tick } loadRows

            let rows =
                [ for y, row in List.indexed grid.rows ->
                      let named = wire $"world_row_%d{y}" gridWidth
                      row ==> named
                      named ]

            io.world [ for row in rows -> [ for k in 0 .. words - 1 -> slice (min 32 (gridWidth - 32 * k) + 32 * k - 1) (32 * k) row ] ]

            let generation = reg "generation_count" 32

            ifElse
                [ (load, fun () -> lit 0UL 32 ==> generation)
                  (otherwise, fun () -> If tick (fun () -> generation + lit 1UL 32 ==> generation)) ]

            io.generation generation

            // The count in two stages, each row where it lives and then the
            // sum, as the KV260 top counts: one flat count off the whole
            // grid was that top's worst path.
            let rowCountWidth = bitsNeeded gridWidth

            let rowCounts =
                [ for y, row in List.indexed rows ->
                      let rowCount = reg $"pop_row_count_%d{y}" rowCountWidth
                      countWhere rowCountWidth id [ for x in 0 .. gridWidth - 1 -> slice x x row ] ==> rowCount
                      rowCount ]

            let population = reg "population_count" populationWidth
            reduceTree (+) [ for c in rowCounts -> cat (lit 0UL (populationWidth - rowCountWidth)) c ] ==> population
            io.population population)

/// What a cell costs on a part, in the cells the toolchain counts — the
/// rule, the snapshot's shadow, the seed's staging and their muxes —
/// measured 2026-11-07 through the open flow: 18.3 a cell on the ECP5
/// (64×64 at 75 109, 32×32 at 18 697) and ~19 on the HX1K (16×16 at
/// 5 575), plus what the link and the counters take on their own. Stated
/// a little high, so a derived world builds first time.
let cellsPerCell = 20

/// What the world's surroundings take: the link, the seed's landing, the
/// generation and population counters.
let cellsBesideTheWorld = 700

/// The share of a part a derived world may take: the rest is routing.
let partShare = 0.9

/// The largest world a part holds — a square, a multiple of eight on a
/// side, 64 at most — or none, when the smallest (8×8) does not fit.
let largestWorld (part: Part) : int option =
    let budget = int (float part.facts.cells * partShare) - cellsBesideTheWorld
    let side = int (sqrt (float (max 0 budget) / float cellsPerCell)) / 8 * 8 |> min 64
    if side >= 8 then Some side else None

/// The world sized for the board it is built on: the largest square the
/// part holds, or the mapping's `world`. The name says the size, so the
/// top and the app do.
let life: BoardTop.Design =
    BoardTop.sized "Life" (fun fit ->
        let derived =
            match largestWorld fit.part with
            | Some side -> side
            | None ->
                failwith
                    $"Life: the smallest world, 8×8, takes about %d{8 * 8 * cellsPerCell + cellsBesideTheWorld} of the {fit.part.device}'s %d{fit.part.facts.cells} cells — impossible on this part"

        let side = fit.size ("world", derived)
        lifeAt side side)
