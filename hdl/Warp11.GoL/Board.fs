/// What the Game of Life accelerator is built for.
///
/// `golfs_bd_bd.tcl` programs PL0 at 166.666672 MHz, which is the whole
/// headline: one generation per cycle is 167 million generations a second at
/// this clock and nothing like it at another. The number lives here so the
/// generated seam carries it to the daemon rather than the daemon restating
/// it — which it did, as 166_666_667, five hertz adrift of the overlay.
///
/// **The same board runs the older `gol` app at 99.999001 MHz.** That is why
/// the board is the project's rather than the library's — see `kv260At`.
module Warp11.GoL.Board

open Warp11

let board = kv260At 166_666_672
