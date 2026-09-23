/// The verbs a drawn design answers to from a shell, apart from any one
/// project's head: emit its Verilog, export it as typed F#, draw a frame
/// through it, write the build directory its board's toolchain wants, serve
/// it to a Rust driver over the batch bridge.
///
/// Here rather than in `Warp11.Placement` so that a project which brings its
/// own units gets them too — it registers, then delegates. `run` answers
/// `Some code` for a verb it knows and `None` for anything else, so a head
/// keeps its own verbs and falls through for these.
module Warp11.Design.Verbs

open Warp11
open Warp11.Graph
open Warp11.Devices
open Warp11.Elaborate

/// `frame design.json <w>x<h> out.pgm [name=value ...]`.
let private frame (path: string) (size: string) (out: string) (sets: string[]) : int =
    let parsed =
        match size.Split 'x' with
        | [| w; h |] ->
            match System.Int32.TryParse w, System.Int32.TryParse h with
            | (true, w), (true, h) when w > 0 && h > 0 -> Some(w, h)
            | _ -> None
        | _ -> None

    let controls =
        [ for set in sets ->
              match set.Split '=' with
              | [| name; value |] ->
                  let value =
                      if value.StartsWith "0x" then System.Convert.ToUInt64(value.Substring 2, 16) else System.UInt64.Parse value

                  name, value
              | _ -> failwith $"'{set}': a control is name=value" ]

    match parsed, Warp11.DesignFile.load path with
    | None, _ ->
        eprintfn $"'{size}': a frame is <width>x<height>"
        1
    | _, Error why ->
        eprintfn $"{path}: {why}"
        1
    | Some(width, height), Ok g ->
        let m: Warp11.Devices.FrameMapping = { width = width; height = height; controls = controls; outputPath = Some out }
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let drawn = Warp11.Devices.runFrameInSim 100_000 g m
        printfn $"wrote {out}: %d{drawn.width}×%d{drawn.height} in %.1f{sw.Elapsed.TotalSeconds} s"
        0

/// The shared verbs. `None` when `argv` is none of them.
let run (argv: string[]) : int option =
    match argv with
    | [| "emit"; path |] ->
        match Warp11.DesignFile.load path with
        | Ok g ->
            printf $"{Warp11.Verilog.emitDesign (Warp11.Elaborate.elaborate g).def}"
            Some 0
        | Error why ->
            eprintfn $"{path}: {why}"
            Some 1
    // A saved design as typed F# source: `export design.json`.
    | [| "export"; path |] ->
        // With the design's default mapping when it names one: the board
        // and a build function ride along.
        let exportWithDefault (g: Warp11.Graph.Graph) =
            let mapping = g.mapping |> Option.bind (fun _ -> Warp11.Mapping.defaultFor path g.mapping |> Result.toOption)
            Warp11.Export.exportWith mapping g

        match Warp11.DesignFile.load path |> Result.bind exportWithDefault with
        | Ok source ->
            printf $"{source}"
            Some 0
        | Error why ->
            eprintfn $"{path}: {why}"
            Some 1
    // A frame the design draws, from the shell: `frame design.json <w>x<h>
    // out.pgm [name=value ...]` — the count in, the pixels out as a PGM, the
    // design's own controls set by name.
    | [| "frame"; path; size; out |] ->
        frame path size out [||] |> Some
    | [| "frame"; path; size; out; _ |]
    | [| "frame"; path; size; out; _; _ |]
    | [| "frame"; path; size; out; _; _; _ |]
    | [| "frame"; path; size; out; _; _; _; _ |] ->
        frame path size out argv[4..] |> Some
    // The batch bridge: `batchserve design.json <preset>` serves the design's
    // host-memory top in the Sim to a Rust driver on stdin.
    // A preset alone is the host-memory path; `<preset> count` the counted one;
    // the design's own mapping says which when nothing is named.
    | [| "batchserve"; path |]
    | [| "batchserve"; path; _ |]
    | [| "batchserve"; path; _; "count" |] ->
        let which = if argv.Length >= 3 then Some argv[2] else None
        let dataPath = if argv.Length = 4 then viaCount else viaHostMemory

        let mapping (g: Warp11.Graph.Graph) =
            match which with
            | None -> Warp11.Mapping.defaultFor path g.mapping
            | Some w when w.EndsWith ".json" -> Warp11.Mapping.load w
            | Some w -> Warp11.Mapping.ofBoard w dataPath

        match Warp11.DesignFile.load path with
        | Error why ->
            eprintfn $"{path}: {why}"
            Some 1
        | Ok g ->
            match mapping g with
            | Error why ->
                eprintfn $"{why}"
                Some 1
            | Ok m ->
                match Warp11.Mapping.pathText m.path with
                | "pins" ->
                    eprintfn $"{path}: the batch bridge serves a design on the host's memory, and the mapping puts it on the pins"
                    Some 1
                | _ ->
                    let path = m.path
                    Warp11.BoardTop.batchServe (Warp11.Elaborate.boardTopOf m.board path g)
                    Some 0
    // A saved design's build directory: `build design.json [mapping.json] <dir>`
    // — the design's default mapping when none is named — writes everything
    // the board's toolchain needs and says how to run it.
    | [| "build"; path; dir |]
    | [| "build"; path; _; dir |] ->
        let mappingFile = if argv.Length = 4 then Some argv[2] else None

        match Warp11.DesignFile.load path with
        | Error why ->
            eprintfn $"{path}: {why}"
            Some 1
        | Ok g ->
            let mapping =
                match mappingFile with
                | Some file -> Warp11.Mapping.load file
                | None -> Warp11.Mapping.defaultFor path g.mapping

            match mapping with
            | Error why ->
                eprintfn $"{why}"
                Some 1
            | Ok m ->
                try
                    let top = Warp11.Elaborate.boardTopOf m.board m.path g
                    let out = Warp11.Build.write dir top

                    for file in out.files do
                        printfn $"wrote {file}"

                    for name, entry in top.registers do
                        printfn $"  register {name} at 0x%02x{entry.offset}"

                    printfn $"build with: {out.run}"
                    Some 0
                with e ->
                    eprintfn $"{e.Message}"
                    Some 1
    // ...or for a preset and a path named on the line.
    | [| "build"; path; which; way; dir |] ->
        let dataPath =
            match way with
            | "pins" -> Ok viaPins
            | "memory" -> Ok viaHostMemory
            | other -> Error $"a data path is pins or memory, not '{other}'"

        // `which` is a preset's name or a board file's path.
        match Warp11.DesignFile.load path, dataPath |> Result.bind (Warp11.Mapping.ofBoard which) with
        | Error why, _ ->
            eprintfn $"{path}: {why}"
            Some 1
        | _, Error why ->
            eprintfn $"{why}"
            Some 1
        | Ok g, Ok m ->
            try
                let top = Warp11.Elaborate.boardTopOf m.board m.path g
                let out = Warp11.Build.write dir top

                for file in out.files do
                    printfn $"wrote {file}"

                for name, entry in top.registers do
                    printfn $"  register {name} at 0x%02x{entry.offset}"

                printfn $"build with: {out.run}"
                Some 0
            with e ->
                eprintfn $"{e.Message}"
                Some 1
    // A saved design on a board: `board design.json <preset> <dir>` writes
    // the top's Verilog and the Rust seam for one of the preset boards.
    | [| "board"; path; which; dir |] ->
        match Warp11.DesignFile.load path, preset which with
        | Error why, _ ->
            eprintfn $"{path}: {why}"
            Some 1
        | _, Error why ->
            eprintfn $"{why}"
            Some 1
        | Ok g, Ok board ->
            try
                let top = Warp11.Elaborate.boardTopOf board viaPins g

                for file in Warp11.BoardTop.write dir top do
                    printfn $"wrote {file}"

                for name, entry in top.registers do
                    printfn $"  register {name} at 0x%02x{entry.offset}"

                Some 0
            with e ->
                eprintfn $"{e.Message}"
                Some 1
    | _ -> None
