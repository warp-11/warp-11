/// Runs every use case and says which are green, which are red, and which
/// stop at a part of the surface that is not built yet.
module Warp11.Placement.Main

open Warp11
open Warp11.Placement.Placement
open Warp11.Placement.GraphChecks
open Warp11.Devices

let private run (name: string) (check: unit -> bool) =
    let verdict =
        try
            if check () then "ok" else "FAIL"
        with e ->
            $"stopped — {e.Message}"

    printfn $"%-44s{name} {verdict}"

[<EntryPoint>]
let main argv =
    match argv with
    | [| "show"; s; m; a |] ->
        printf $"{Warp11.Verilog.emitDesign (mac (int s) (int m) (int a)).def}"
        0
    // A recording through the gain design: `play in.wav out.wav [volume]`,
    // volume in Q8.8 where 256 is unity.
    | [| "play"; inPath; outPath |]
    | [| "play"; inPath; outPath; _ |] ->
        let volume = if argv.Length = 4 then uint64 argv[3] else gainUnity
        let source = readWavFile inPath

        let heard =
            runInSim 100_000 Warp11.Graph.gainGraph { source = source; controls = [ "volume", volume; "mute", 0UL ]; outputPath = Some outPath }

        let peak (w: WavData) = w.samples |> Array.map (fun s -> abs (int s)) |> Array.max
        printfn $"{inPath}: %d{source.FrameCount} frames, peak %d{peak source} → {outPath}: %d{heard.FrameCount} frames, peak %d{peak heard}, volume %d{volume}/256"
        0
    // The Verilog of a saved design, or of the typed gain patch — so the two
    // can be diffed: `emit design.json`, `emit gain`.
    | [| "emit"; "gain" |] ->
        printf $"{Warp11.Verilog.emitDesign gainPatch.def}"
        0
    | [| "emit"; path |] ->
        match Warp11.DesignFile.load path with
        | Ok g ->
            printf $"{Warp11.Verilog.emitDesign (Warp11.Elaborate.elaborate g).def}"
            0
        | Error why ->
            eprintfn $"{path}: {why}"
            1
    // A saved design as typed F# source: `export design.json`.
    | [| "export"; path |] ->
        match Warp11.DesignFile.load path |> Result.bind Warp11.Export.export with
        | Ok source ->
            printf $"{source}"
            0
        | Error why ->
            eprintfn $"{path}: {why}"
            1
    // The example designs as files: `examples <dir>`.
    | [| "examples"; dir |] ->
        Warp11.Placement.Examples.write dir

        for file, _ in Warp11.Placement.Examples.all Warp11.Placement.Examples.recordingRate do
            printfn $"{System.IO.Path.Combine(dir, file)}"

        0
    // A saved design's build directory: `build design.json <preset> pins|memory <dir>`
    // writes everything the board's toolchain needs and says how to run it.
    | [| "build"; path; which; way; dir |] ->
        let dataPath =
            match way with
            | "pins" -> Ok Warp11.BoardTop.Pins
            | "memory" -> Ok Warp11.BoardTop.HostMemory
            | other -> Error $"a data path is pins or memory, not '{other}'"

        match Warp11.DesignFile.load path, preset which, dataPath with
        | Error why, _, _ ->
            eprintfn $"{path}: {why}"
            1
        | _, Error why, _
        | _, _, Error why ->
            eprintfn $"{why}"
            1
        | Ok g, Ok board, Ok dataPath ->
            try
                let top = Warp11.BoardTop.boardTop board dataPath g
                let out = Warp11.Build.write dir top

                for file in out.files do
                    printfn $"wrote {file}"

                for name, entry in top.registers do
                    printfn $"  register {name} at 0x%02x{entry.offset}"

                printfn $"build with: {out.run}"
                0
            with e ->
                eprintfn $"{e.Message}"
                1
    // A saved design on a board: `board design.json <preset> <dir>` writes
    // the top's Verilog and the Rust seam for one of the preset boards.
    | [| "board"; path; which; dir |] ->
        match Warp11.DesignFile.load path, preset which with
        | Error why, _ ->
            eprintfn $"{path}: {why}"
            1
        | _, Error why ->
            eprintfn $"{why}"
            1
        | Ok g, Ok board ->
            try
                let top = Warp11.BoardTop.boardTop board Warp11.BoardTop.Pins g

                for file in Warp11.BoardTop.write dir top do
                    printfn $"wrote {file}"

                for name, entry in top.registers do
                    printfn $"  register {name} at 0x%02x{entry.offset}"

                0
            with e ->
                eprintfn $"{e.Message}"
                1
    | [| "throughput" |] ->
        printfn $"{throughputReport ()}"
        0
    | _ ->

    run "UC1 in place is the expression" inPlaceIsTheExpression
    run "UC2 sharing is one unit" sharingIsOneUnit
    run "UC3 surplus combinational copies refuse" surplusCombinationalCopiesRefuse
    run "UC4 copies buy throughput" copiesBuyThroughput
    run "UC5 counts are independent" countsAreIndependent
    run "UC6 parameter tracks" parameterTracks
    run "UC7 gain module scales" gainScales
    run "UD1 data is the typed design" dataIsTheTypedDesign
    run "UD2 bad wires refuse" badWiresRefuse
    run "UD3 palette is the units" paletteIsTheUnits
    run "UD4 gain graph is the gain patch" gainGraphIsTheGainPatch
    run "UD5 wav plays through the graph" wavPlaysThroughTheGraph
    run "UD6 edits build the gain design" editsBuildTheGainDesign
    run "UD7 a saved design opens as it was" savedDesignOpensAsItWas
    run "UD8 arguments make the unit" argumentsMakeTheUnit
    run "UD9 the pedal units do what they say" pedalUnitsDoWhatTheySay
    run "UD10 controls hold values, boxes share them" controlsHoldValues
    run "UD11 the export is the design" exportIsTheDesign
    run "UD12 a design is a box" designIsABox
    run "UD13 streams are stages" streamsAreStages
    run "UD14 an image on the boundary" imageOnTheBoundary
    run "UD15 a table on the boundary" tableOnTheBoundary
    run "UD16 a written unit travels with the design" writtenUnitTravels
    run "UD17 the design on a board" designOnABoard
    run "UD18 the design on the host's memory" designOnHostMemory
    run "UD19 the build directory, Vivado" buildDirectoryVivado
    run "UD20 the build directory, the open flow" buildDirectoryOpenFlow
    0
