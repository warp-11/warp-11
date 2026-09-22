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
    // The verbs any drawn design answers to, whoever's units it names.
    match Warp11.Design.Verbs.run argv with
    | Some code -> code
    | None ->

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

        // The design is made for the recording's rate, as a canvas would make it.
        let design = { Warp11.Graph.gainGraph with sampleRate = float source.sampleRate }

        let heard =
            runInSim 100_000 design { source = source; controls = [ "volume", volume; "mute", 0UL ]; outputPath = Some outPath }

        let peak (w: WavData) = w.samples |> Array.map (fun s -> abs (int s)) |> Array.max
        printfn $"{inPath}: %d{source.FrameCount} frames, peak %d{peak source} → {outPath}: %d{heard.FrameCount} frames, peak %d{peak heard}, volume %d{volume}/256"
        0
    // The Verilog of a saved design, or of the typed gain patch — so the two
    // can be diffed: `emit design.json`, `emit gain`.
    // The example designs as files: `examples <dir>`.
    // The typed gain design's Verilog, to diff a drawn one against.
    | [| "emit"; "gain" |] ->
        printf $"{Warp11.Verilog.emitDesign gainPatch.def}"
        0
    | [| "examples"; dir |] ->
        Warp11.Placement.Examples.write dir

        for file, _ in Warp11.Placement.Examples.all Warp11.Placement.Examples.recordingRate do
            printfn $"{System.IO.Path.Combine(dir, file)}"

        0
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
    // Exactly the built-in units: the check is about a design naming one
    // nobody registered.
    run "UD16 a written unit travels with the design" (fun () -> Factories.only [] writtenUnitTravels)
    run "UD17 the design on a board" designOnABoard
    run "UD18 the design on the host's memory" designOnHostMemory
    run "UD19 the build directory, Vivado" buildDirectoryVivado
    run "UD20 the build directory, the open flow" buildDirectoryOpenFlow
    run "UD21 the mapping is a file" mappingIsAFile
    0
