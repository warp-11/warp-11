/// The boards the drawn-design tools offer by name: the canvas's target
/// menu, and the verbs over a design file, which has no code to bind a
/// board in. A typed design binds its board in its own code — `let board =
/// icepi` — and never names one; a menu is the GUI's, not the library's.
module Warp11.Boards

open Warp11

/// The boards this repository has built on, at the clocks their designs use.
let boards: (string * Board) list =
    [ "kv260", kv260
      "icebreaker", iceBreakerAt 24_000_000
      "icepi", icepi
      "goboard", goBoard
      "de10lite", de10Lite ]

/// A board named in a dialog or on a command line: one of `boards`, or a
/// board file's path.
let named (nameOrFile: string) : Result<Board, string> =
    match boards |> List.tryFind (fun (n, _) -> n = nameOrFile) with
    | Some(_, b) -> Ok b
    | None when System.IO.File.Exists nameOrFile -> Mapping.loadBoard nameOrFile
    | None ->
        let names = boards |> List.map fst |> String.concat ", "
        Error $"no board '{nameOrFile}' — one of: {names}, or a board file"

/// The name a board is listed under, if it is one of `boards`: the
/// provenance a dialog shows.
let nameOf (board: Board) : string option =
    boards |> List.tryFind (fun (_, b) -> b = board) |> Option.map fst

/// A mapping for a board named on a command line, on a path.
let mappingOn (nameOrFile: string) (path: DataPath) : Result<Mapping.Mapping, string> =
    named nameOrFile
    |> Result.map (fun b ->
        let m: Mapping.Mapping = { board = b; path = path; plugged = [] }
        m)
