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

/// What the design asks of the world, as handles.
type WorldIo =
    { /// The world to start from, a row a beat, top row first; the last
      /// row loads the grid whole.
      seed: BoardTop.Incoming<Expr list>
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
              tick = n.pulseIn "tick"
              tickCount = n.valueIn ("tickCount", unsignedInt 32, 1UL)
              intervalCycles = n.valueIn ("intervalCycles", unsignedInt 32, 1UL)
              stop = n.pulseIn "stop"
              reset = n.pulseIn "reset"
              ticking = n.valueOut ("ticking", unsignedInt 1)
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

            let seedLoad = regBit "seed_load"
            (arrived &&& lastRow) ==> seedLoad

            // ---- reset: the world emptied and the generation zeroed, through
            // the same load the seed takes. Registered first, because it fans
            // out to every cell and a decode straight off the host's write
            // was the hand top's worst path at 166 MHz.
            let resetQ = regBit "reset_q"
            io.reset ==> resetQ
            let load = wireBit "grid_load"
            (seedLoad ||| resetQ) ==> load

            let loadRows =
                [ for y in 0 .. gridHeight - 1 ->
                      let row = wire $"seed_row_%d{y}" gridWidth

                      match List.item y staging with
                      | low :: rest -> mux resetQ (lit 0UL gridWidth) (List.fold (fun acc w -> cat w acc) low rest) ==> row
                      | [] -> failwith "unreachable: a row has a word"

                      row ]

            // ---- the pace: `tick` starts a burst of `tickCount` generations
            // (0: until `stop`), one every `intervalCycles`; `stop` ends it
            // now. A tick during a burst is ignored, so the host cannot
            // double-count by writing twice.
            let running = regBit "burst_running"
            let continuous = regBit "burst_continuous"
            let remaining = reg "burst_remaining" 32
            let countdown = reg "pace_countdown" 32
            let due = eq countdown (lit 0UL 32)
            let intervalSeed = mux (eq io.intervalCycles (lit 0UL 32)) (lit 0UL 32) (io.intervalCycles - lit 1UL 32)
            let fire = wireBit "generation_fire"
            (running &&& due &&& bnot io.stop) ==> fire

            ifElse
                [ (io.stop,
                   fun () ->
                       lit 0UL 1 ==> running
                       lit 0UL 1 ==> continuous
                       lit 0UL 32 ==> remaining
                       lit 0UL 32 ==> countdown)
                  (bnot running,
                   fun () ->
                       If io.tick (fun () ->
                           lit 1UL 1 ==> running
                           eq io.tickCount (lit 0UL 32) ==> continuous
                           io.tickCount ==> remaining
                           intervalSeed ==> countdown))
                  (due,
                   fun () ->
                       intervalSeed ==> countdown

                       If (bnot continuous) (fun () ->
                           ifElse
                               [ (eq remaining (lit 1UL 32),
                                  fun () ->
                                      lit 0UL 1 ==> running
                                      lit 0UL 32 ==> remaining)
                                 (otherwise, fun () -> remaining - lit 1UL 32 ==> remaining) ]))
                  (otherwise, fun () -> countdown - lit 1UL 32 ==> countdown) ]

            io.ticking running

            // ---- the grid, and its rows named so they can be sliced.
            let grid = gameOfLifeGrid gridWidth gridHeight { load = load; tick = fire } loadRows

            let rows =
                [ for y, row in List.indexed grid.rows ->
                      let named = wire $"world_row_%d{y}" gridWidth
                      row ==> named
                      named ]

            io.world [ for row in rows -> [ for k in 0 .. words - 1 -> slice (min 32 (gridWidth - 32 * k) + 32 * k - 1) (32 * k) row ] ]

            let generation = reg "generation_count" 32

            ifElse
                [ (load, fun () -> lit 0UL 32 ==> generation)
                  (otherwise, fun () -> If fire (fun () -> generation + lit 1UL 32 ==> generation)) ]

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
