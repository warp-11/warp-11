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

/// **The cell: a view in, its next state out.**
///
/// A real module, instantiated per call — `cell view` inside a design elaborates
/// a `Cell` instance, wires the view onto its ports and hands back its outputs.
/// So a caller composes with it rather than opening it, and `cell.def` is the
/// same module for the debugger, the tests and the differential. There is no
/// separate harness, because there is nothing left for one to do.
///
/// **No position, and no grid.** The rule does not read where the cell is — an
/// edge policy belongs to whatever gathers the neighbourhood. Position is
/// *context*, carried around this by its caller (`withContext` is the general
/// form; for a combinational stage it is just wires not routed through here).
/// One `Cell` serves every grid, at every size.
///
/// Neighbours arrive as one word, bit `i` being neighbour `i` of
/// `neighborhood Stencil.Moore` — row-major, centre excluded. Life only counts
/// them, so the order is a convention here rather than a constraint; a rule that
/// cared would read it.
let cell =
    defineModule
        "Cell"
        (fun p ->
            (p.inPort "state" 1,
             p.inPort "neighbors" neighborCount,
             p.outPort "state_out" 1))
        (fun m (stateIn, neighborsIn, stateOut) (view: CellView) ->
            if List.length view.neighbors <> neighborCount then
                failwith
                    $"cell: a Moore neighbourhood is %d{neighborCount} cells, got %d{List.length view.neighbors}"

            m.Assign(stateIn, view.state)
            // `catAll` puts its first element at the most significant end, so
            // the list is reversed to land neighbour `i` on bit `i`.
            m.Assign(neighborsIn, catAll (List.rev view.neighbors))

            stateOut)
        (fun (stateIn, neighborsIn, stateOut) m ->
            let next =
                nextState
                    { state = stateIn
                      neighbors = [ for i in 0 .. neighborCount - 1 -> slice i i neighborsIn ] }

            m.Assign(stateOut, next))
