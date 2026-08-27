/// The desktop head: the catalog this project's debugger opens on, and the
/// call that opens it.
module Warp11.GoL.Streamed.App.Main

open Warp11.Catalog
open Warp11.GoL.Streamed.Modules

let catalog =
    designs
        [ entry "Cell" (nameof cell) (fun () -> cell.def)
          |> watching [ "live"; "state_out" ]
          |> poking [ "state", 1UL; "neighbors", 0b00000011UL ] ]

[<EntryPoint>]
let main _ = Warp11.SimView.Desktop.debugCatalog catalog
