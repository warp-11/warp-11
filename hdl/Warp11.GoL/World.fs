/// The Game of Life as a design: the world a surface the host takes whole,
/// the seed rows the host sends, the pace a register. Nothing in it names
/// a bus or a wire — the mapping says how the world reaches the host: on a
/// board whose only connection is a UART, snapshots as frames on it, pulled
/// by the host; on the KV260, the keep-latest triple buffer in its DDR
/// (`-- life out/`), which `runtime/gol-daemon` serves to the live view. The
/// board is the project's one board, `Board.board`. The hand-written
/// `GolAxi` top this grew out of was retired 2026-11-08, once this design
/// carried its control surface: a counted burst, a stop, a reset.
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

/// A named row cut into the layout's words, low word first.
let private wordsOf (layout: Layout<Expr list>) (row: Expr) : Expr list =
    (0, layout.fields)
    ||> List.mapFold (fun low field ->
        let high = low + fieldWidth field - 1
        slice high low row, high + 1)
    |> fst

/// What the design asks of the world, as handles.
type WorldIo =
    { /// The world to start from, a row a beat, top row first; the last
      /// row loads the grid whole.
      seed: BoardTop.Landing<Expr list>
      /// The world as it is, taken when the host asks.
      world: Expr list list -> unit
      /// Start a burst of `tickCount` generations. Ignored while one runs.
      tick: Expr
      /// Generations a burst runs; 0 runs until `stop`.
      tickCount: Expr
      /// Cycles between generations inside a burst; 0 and 1 both mean every
      /// cycle.
      intervalCycles: Expr
      /// End the burst now. Wins over everything.
      stop: Expr
      /// The world to empty and the generation to zero. A burst in progress
      /// goes on, over the empty world.
      reset: Expr
      /// A burst is in progress.
      ticking: Expr -> unit
      /// Generations since the last seed or reset.
      generation: Expr -> unit
      /// Live cells.
      population: Expr -> unit }

/// What the world is doing: nothing, a counted burst, or a free one that
/// only `stop` ends.
type private Burst =
    | Idle
    | Counted
    | Free

/// The world at a size the caller states, as a design any board top builds.
let lifeAt (gridWidth: int) (gridHeight: int) : BoardTop.Design =
    let layout = rowLayout gridWidth
    let populationWidth = bitsNeeded (gridWidth * gridHeight)

    BoardTop.design
        $"Life%d{gridWidth}x%d{gridHeight}"
        (fun n ->
            { seed = n.surfaceIn ("seed", layout, gridHeight)
              world = n.surfaceOut ("world", layout, gridHeight)
              tick = n.pulseIn "tick"
              tickCount = n.valueIn ("tickCount", unsignedInt 32, 1UL)
              intervalCycles = n.valueIn ("intervalCycles", unsignedInt 32, 1UL)
              stop = n.pulseIn "stop"
              reset = n.pulseIn "reset"
              ticking = n.valueOut ("ticking", unsignedInt 1)
              generation = n.valueOut ("generation", unsignedInt 32)
              population = n.valueOut ("population", unsignedInt populationWidth) })
        (fun io ->
            // ---- the load: the seed's last row landing, or a reset — the
            // world emptied and the generation zeroed through the same load.
            // Both registered first, because the load fans out to every cell
            // and a decode straight off the host's write was the hand top's
            // worst path at 166 MHz.
            let seedLoad = regBit "seed_load"
            io.seed.landed ==> seedLoad
            let resetQ = regBit "reset_q"
            io.reset ==> resetQ
            let load = wireBit "grid_load"
            (seedLoad ||| resetQ) ==> load

            let loadRows =
                [ for y, staged in List.indexed io.seed.rows ->
                      let row = wire $"seed_row_%d{y}" gridWidth

                      match staged with
                      | low :: rest -> mux resetQ (lit 0UL gridWidth) (List.fold (fun acc w -> cat w acc) low rest) ==> row
                      | [] -> failwith "unreachable: a row has a word"

                      row ]

            // ---- the pace: `tick` starts a burst of `tickCount` generations
            // (0: free, until `stop`), one every `intervalCycles`; `stop`
            // ends it now. A tick during a burst is ignored, so the host
            // cannot double-count by writing twice.
            let burst = machine "burst" [ Idle; Counted; Free ]
            let running = bnot (burst.Is Idle)
            let remaining = reg "burst_remaining" (width io.tickCount)
            let countdown = reg "pace_countdown" (width io.intervalCycles)
            let due = eq countdown 0UL
            let interval = mux (eq io.intervalCycles 0UL) (lit 0UL (width countdown)) (io.intervalCycles - 1UL)
            let fire = wireBit "generation_fire"
            (running &&& due &&& bnot io.stop) ==> fire

            burst.Switch
                [ Idle,
                  fun () ->
                      If io.tick (fun () ->
                          io.tickCount ==> remaining
                          interval ==> countdown

                          ifElse
                              [ (eq io.tickCount 0UL, fun () -> burst.Goto Free)
                                (otherwise, fun () -> burst.Goto Counted) ])
                  Counted,
                  fun () ->
                      ifElse
                          [ (bnot due, fun () -> countdown - 1UL ==> countdown)
                            (eq remaining 1UL, fun () -> burst.Goto Idle)
                            (otherwise,
                             fun () ->
                                 interval ==> countdown
                                 remaining - 1UL ==> remaining) ]
                  Free,
                  fun () ->
                      ifElse
                          [ (bnot due, fun () -> countdown - 1UL ==> countdown)
                            (otherwise, fun () -> interval ==> countdown) ] ]

            // Written last, so it wins over the burst's own transition.
            If io.stop (fun () -> burst.Goto Idle)

            io.ticking running

            // ---- the grid, and its rows named so they can be sliced.
            let grid = gameOfLifeGrid gridWidth gridHeight { load = load; tick = fire } loadRows

            let rows =
                [ for y, row in List.indexed grid.rows ->
                      let named = wire $"world_row_%d{y}" gridWidth
                      row ==> named
                      named ]

            io.world [ for row in rows -> wordsOf layout row ]

            let generation = reg "generation_count" 32

            ifElse
                [ (load, fun () -> 0UL ==> generation)
                  (otherwise, fun () -> If fire (fun () -> generation + 1UL ==> generation)) ]

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
            reduceTree (+) [ for c in rowCounts -> pad populationWidth c ] ==> population
            io.population population)

/// The world's side: a design parameter, stated here and nothing to do
/// with the board. 64 is what the KV260's app and daemon are built around;
/// the Icepi holds 32 (a cell is about 20 of the toolchain's cells, so
/// 32×32 placed at 18 697 of its 24 288), the DE10-Lite 40. The board's
/// gate says whether what is stated fits.
let side = 64

/// The world the project builds, at `side`. The name says the size, so the
/// top and the app do.
let design = lifeAt side side

/// Life's mapping for a board: the world through the host's memory or over
/// its link, as the board has. Where the design is code, so is its mapping.
let mappingFor (board: Board) : Mapping.Mapping =
    { board = board; path = viaHost board; plugged = [] }

/// Life's mapping: the project's board (`Board.board`, the one line that
/// moves the project), the world through its host.
let mapping = mappingFor Board.board
