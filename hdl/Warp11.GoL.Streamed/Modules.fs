module Warp11.GoL.Streamed.Modules

open Warp11

/// What one cell sees when its turn comes.
type CellView =
    { /// This cell's own state.
      state: Expr
      /// Its neighbourhood, in `neighborhood`'s fixed row-major order —
      /// gathered by the stream, not fetched here.
      neighbors: Expr list }

/// A Moore neighbourhood: eight cells, centre excluded.
let neighborCount = 8

/// Width of the live count. `+ 1` because the count runs 0..n *inclusive*:
/// eight live neighbours needs four bits, not the three that hold 0..7. Life
/// would have survived the narrower count — all-eight is dead either way, and
/// so is the zero it wraps to — which is exactly why it is worth getting right
/// here rather than discovering it in a rule that does care.
let liveWidth = bitsToHold (neighborCount + 1)

/// B3/S23 over a view, as ordinary logic.
///
/// **The live count is a named wire, and that is not cosmetic.** As a bare
/// `Expr` it is used three times — once per comparison and once more if anyone
/// reads it — and the emitter inlines an `Expr` at each use, so the eight-input
/// adder tree was being written into the Verilog three times over. Naming it
/// emits the tree once and references it, which is also what makes the module
/// readable at the point somebody checks what synthesis did with it.
///
/// It is a wire rather than an output port because it is B3/S23's internal, not
/// the cell's interface: a rule that is not Life has no "live count" at all, and
/// a port for one would be on every instance whether or not anything reads it.
/// The debugger and the tests still see it — a wire is in the inventory.
///
/// Private because [cell] is the way to reach it: exposing both the inline form
/// and the instantiated one would let a call site tell them apart, which is the
/// thing call-site invariance exists to prevent. If a datapath ever wants the
/// cone fused into a wider beat rather than instantiated per cell, that is a
/// change here and at no call site.
let private nextState (view: CellView) : Expr =
    let live = wire "live" liveWidth
    countWhere liveWidth id view.neighbors ==> live

    eq live (lit 3UL liveWidth) ||| (view.state &&& eq live (lit 2UL liveWidth))

/// The cell's port bundle. Neighbours arrive as one word, bit `i` being
/// neighbour `i` of `neighborhood Stencil.Moore` — row-major, centre excluded.
/// Life only counts them, so the order is a convention here rather than a
/// constraint; a rule that cared would read it.
type CellIo =
    { state: Expr
      neighbors: Expr
      stateOut: Expr }

/// **The cell: a module that is exactly its wires.**
///
/// `defModule`'s two parts: the bundle, and one body over it — the slicing of
/// the packed neighbour word happens where it is used, and `cell.def` is the
/// same module for the debugger, the tests and the differential.
///
/// **No position, and no grid.** The rule does not read where the cell is — an
/// edge policy belongs to whatever gathers the neighbourhood. Position is
/// *context*, carried around this by its caller (`withContext` is the general
/// form; for a combinational stage it is just wires not routed through here).
/// One `Cell` serves every grid, at every size.
let cell =
    defModule
        "Cell"
        (fun p ->
            { state = p.inPort "state" 1
              neighbors = p.inPort "neighbors" neighborCount
              stateOut = p.outPort "state_out" 1 })
        (fun io ->
            nextState
                { state = io.state
                  neighbors = [ for i in 0 .. neighborCount - 1 -> slice i i io.neighbors ] }
            ==> io.stateOut)

/// The function feel, as ordinary code beside the module: a view in, the next
/// state out, one auto-named `Cell` instance per call.
let cellOf (view: CellView) =
    if List.length view.neighbors <> neighborCount then
        failwith
            $"cell: a Moore neighbourhood is %d{neighborCount} cells, got %d{List.length view.neighbors}"

    let c = cell.New
    view.state ==> c.state
    // `catAll` puts its first element at the most significant end, so the
    // list is reversed to land neighbour `i` on bit `i`.
    catAll (List.rev view.neighbors) ==> c.neighbors
    c.stateOut

/// One next-generation row from a 3-row window: one `Cell` instance per
/// column, each handed its neighbourhood in `neighborhood`'s row-major order.
/// The window's rows arrive widened — bit c is column c−1 — so column c's
/// cells are bits c, c+1, c+2 of each row, plain slices with the wrap already
/// resolved where the window was formed. `Cell` still knows no position and
/// no grid, which is the contract this stage exists to honour.
let lifeBeat (columns: int) (cells: Expr list) : Expr =
    match cells with
    | [ above; centre; below ] ->
        catAll
            [ for column in columns - 1 .. -1 .. 0 ->
                  cellOf
                      { state = slice (column + 1) (column + 1) centre
                        neighbors =
                          [ slice column column above
                            slice (column + 1) (column + 1) above
                            slice (column + 2) (column + 2) above
                            slice column column centre
                            slice (column + 2) (column + 2) centre
                            slice column column below
                            slice (column + 1) (column + 1) below
                            slice (column + 2) (column + 2) below ] } ]
    | win -> failwith $"lifeBeat: a 3-row window, got %d{List.length win} rows"


/// Which storage the world lives in — the *mapping* of
/// `notes/STREAMING_ARCH.md` §One design across the storage tiers.
/// `Registers` is the whole-world beat: every cell in flip-flops, one
/// generation per cycle, the all-parallel design as the degenerate sweep.
/// `Bands` is the row beat: the world striped into per-band block memories,
/// ping-ponged, rows + ε cycles per generation. The host contract is
/// identical across tiers.
type WorldTier =
    | Registers
    | Bands

/// The sweep's port bundle: free-running — `run` is a level, `generation`
/// counts completed generations, and the host fills and probes by global row
/// address while `run` is low.
type SweepIo =
    { run: Expr
      generation: Expr
      fillAddr: Expr
      fillData: Expr
      fillEnable: Expr
      probeAddr: Expr
      probeData: Expr }

/// The banded sweep's control states: waiting for `run`, or a sweep in
/// flight. The register tier needs no machine — its whole sweep is a cycle.
type private SweepPhase =
    | Idle
    | Sweeping

/// The streamed Game of Life, free-running: generations proceed back to back
/// while `run` holds, `generation` counting them — Life's specialisation of
/// the band architecture (`notes/STREAMING_ARCH.md`), at `engines` parallel
/// band engines, over the storage `tier` the mapping names.
///
/// **`Registers`**: the world is `gridSize` row registers; every cone fires
/// every cycle, `run` is the clock enable, and one generation costs one
/// cycle — the all-parallel design, reached as the degenerate sweep with the
/// same `lifeBeat` cones and the same host contract.
///
/// **`Bands`**: the world is striped into `engines` band memories **twice
/// over** — two buffer sets, ping-ponged by a parity bit, because the wrap
/// rows are read again at the far end of a sweep after their next-generation
/// values were written, so in-place is wrong by construction. The engines run
/// in lockstep off ONE `rangeStream` schedule; every band memory serves at
/// most one reader per cycle (its own engine mid-band, a neighbour on the two
/// halo beats), so parallelism costs cones and window registers, never extra
/// storage beyond the double buffer. The vertical halo is the loader's job:
/// beat i of a band reads local row (i − 1) mod bandRows, and an engine's
/// first and last beats take the neighbouring bands' edge rows. A small
/// Idle/Sweeping machine restarts the schedule while `run` holds and flips
/// the parity as each sweep completes.
let generationSweep (gridSize: int) (engines: int) (tier: WorldTier) =
    let addrWidth = bitsToHold gridSize

    if (1 <<< addrWidth) <> gridSize then
        failwith
            $"generationSweep: the halo address wraps by truncation, so the grid must be a power of two rows — got %d{gridSize}"

    match tier with
    | Registers ->
        if engines <> 1 then
            failwith
                $"generationSweep: the register tier updates every cell every cycle — engines is meaningless there, got %d{engines}"
    | Bands ->
        if engines < 1 || gridSize % engines <> 0 || gridSize / engines < 2 then
            failwith
                $"generationSweep: %d{engines} engines over %d{gridSize} rows — engines must divide the grid into bands of at least two rows"

    let tierTag =
        match tier with
        | Registers -> "reg"
        | Bands -> "mem"

    defModule
        $"GolStreamed%d{gridSize}x%d{engines}_%s{tierTag}"
        (fun p ->
            { run = p.inPort "run" 1
              generation = p.outPort "generation" 16
              fillAddr = p.inPort "fill_addr" addrWidth
              fillData = p.inPort "fill_data" gridSize
              fillEnable = p.inPort "fill_enable" 1
              probeAddr = p.inPort "probe_addr" addrWidth
              probeData = p.outPort "probe_data" gridSize })
        (fun io ->
            let generationCount = reg "generation_count" 16
            generationCount ==> io.generation

            match tier with
            | Registers ->
                let rows = [ for r in 0 .. gridSize - 1 -> reg $"world%d{r}" gridSize ]

                // The same widening the line window applies — horizontal wrap,
                // bit c is column c−1 — so `lifeBeat` is the identical rule.
                let widened =
                    [ for r, row in List.indexed rows ->
                          let wide = wire $"world_wide%d{r}" (gridSize + 2)
                          catAll [ slice 0 0 row; row; slice (gridSize - 1) (gridSize - 1) row ] ==> wide
                          wide ]

                for r in 0 .. gridSize - 1 do
                    let next =
                        lifeBeat
                            gridSize
                            [ widened[(r - 1 + gridSize) % gridSize]
                              widened[r]
                              widened[(r + 1) % gridSize] ]

                    If io.run (fun () -> next ==> rows[r])

                    If (io.fillEnable &&& eq io.fillAddr (lit (uint64 r) addrWidth)) (fun () ->
                        io.fillData ==> rows[r])

                If io.run (fun () -> generationCount + lit 1UL 16 ==> generationCount)
                selectIndexed io.probeAddr rows ==> io.probeData

            | Bands ->
                let bandRows = gridSize / engines

                let phase = machine "phase" [ Idle; Sweeping ]
                let startPulse = wireBit "sweep_start"
                (phase.Is Idle &&& io.run) ==> startPulse
                If startPulse (fun () -> phase.Goto Sweeping)

                let flip = wireBit "flip"

                let world =
                    bandedWorld
                        { engines = engines
                          bandRows = bandRows
                          rowBits = gridSize
                          haloRows = 1
                          verticalEdge = Edge.Wrap }
                        startPulse
                        flip
                        io.fillAddr
                        io.fillData
                        (io.fillEnable &&& phase.Is Idle)
                        io.probeAddr

                world.probe ==> io.probeData

                for g in 0 .. engines - 1 do
                    world.sweeps[g]
                    |> lineWindow
                        { rows = 3
                          edgeColumns = 1
                          cellBits = 1
                          edge = Edge.Wrap }
                        bandRows
                    |> Stream.mapTo (layout1 ("row", gridSize)) (lifeBeat gridSize)
                    |> world.landings[g]

                (phase.Is Sweeping &&& world.swept) ==> flip

                If flip (fun () ->
                    generationCount + lit 1UL 16 ==> generationCount
                    phase.Goto Idle))
