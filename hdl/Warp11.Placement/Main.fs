/// Runs every use case and says which are green, which are red, and which
/// stop at a part of the surface that is not built yet.
module Warp11.Placement.Main

open Warp11
open Warp11.Placement.Placement
open Warp11.Placement.ChainChecks
open Warp11.Placement.MeetingChecks
open Warp11.Placement.SocketChecks
open Warp11.Placement.BoxChecks
open Warp11.Placement.DrawnChecks
open Warp11.Placement.ExportChecks
open Warp11.Placement.PaletteChecks
open Warp11.Devices

let private run (name: string) (check: unit -> bool) =
    let verdict =
        try
            if check () then "ok" else "FAIL"
        with e ->
            $"stopped — {e.GetBaseException().Message}"

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
    // The typed gain design's Verilog, to diff a drawn one against.
    | [| "emit"; "gain" |] ->
        printf $"{Warp11.Verilog.emitDesign gainPipeline.def}"
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
    run "CD1 the drawn gain is the typed pipeline" drawnGainIsThePipeline
    run "CD2 an edge is its connection" edgeIsItsConnection
    run "CD3 a design is a box" designIsABox
    run "CD4 controls hold values" controlsHoldValues
    run "CD5 beats through the drawn gain" beatsThroughTheDrawnGain
    run "CD6 an edge fits, fits by another name, or does not" edgeFits
    run "CD7 a side that changes leaves its edge" changedSideLeavesItsEdge
    run "CD8 bad edges refuse" badEdgesRefuse
    run "MD1 a fan-out, then a merge" fanOutThenMerge
    run "MD2 a merge is the port's arbitration" mergeIsThePortsArbitration
    run "MD3 copies are a farm" copiesAreAFarm
    run "MD4 a design takes copies" designTakesCopies
    run "MD5 meetings are edits" meetingsAreEdits
    run "SD1 sockets share a unit" socketsShareAUnit
    run "SD2 bands share multipliers" bandsShareMultipliers
    run "SD3 a socket fits its service" socketsFitTheirService
    run "SD4 sharing is an edit" sharingIsAnEdit
    run "BD1 joins make the beat" joinsMakeTheBeat
    run "BD2 fields are regrouped" fieldsAreRegrouped
    run "BD3 a buffer is a box" bufferIsABox
    run "BD4 the boxes are edits" boxesAreEdits
    run "BD5 the wide beat is groups" wideBeatIsGroups
    run "UD3 palette is the units" paletteIsTheUnits
    run "UD5 wav plays through the graph" wavPlaysThroughTheGraph
    run "UD6 edits build the designs" editsBuildTheDesigns
    run "UD7 a saved design opens as it was" savedDesignOpensAsItWas
    run "UD8 arguments make the unit" argumentsMakeTheUnit
    run "UD9 the pedal units do what they say" pedalUnitsDoWhatTheySay
    run "UD11 the export is the design" exportIsTheDesign
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
    run "UD22 every example on every board that holds it" everyExampleOnItsBoards
    0
