/// The smallest design with an action in it: the host fires `bump` and a
/// count goes up by one, each time, however long the host takes between
/// writes. It is `pulseIn`'s toy — a value is a level the host leaves, a
/// pulse is an event the host sends — and what its check pins down is the
/// width of the event: one cycle, so one write is one count and not however
/// many cycles the bit happened to sit high.
module Warp11.Designs.Tally

open Warp11

/// What the tally asks of the world: the bump, a clear, and the count back.
type TallyIo =
    { bump: Expr
      clear: Expr
      count: Expr -> unit }

/// The design.
let tally: BoardTop.Design =
    BoardTop.design
        "Tally"
        (fun n ->
            { bump = n.pulseIn "bump"
              clear = n.pulseIn "clear"
              count = n.valueOut ("count", unsignedInt 16) })
        (fun io ->
            let count = reg "tally_count" 16

            ifElse
                [ (io.clear, fun () -> lit 0UL 16 ==> count)
                  (io.bump, fun () -> count + lit 1UL 16 ==> count) ]

            io.count count)

/// The tally over AXI-Lite, on the KV260.
let tallyOnKv260 = (BoardTop.boardTop kv260 viaRegisters tally).top

/// The same over the UART, on the iCEBreaker.
let tallyOnIceBreaker = (BoardTop.boardTop (iceBreakerAt 24_000_000) viaRegisters tally).top
