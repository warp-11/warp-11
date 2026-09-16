/// Runs every use case and says which are green, which are red, and which
/// stop at a part of the surface that is not built yet.
module Warp11.Placement.Main

open Warp11
open Warp11.Placement.Placement
open Warp11.Placement.GraphChecks
open Warp11.Placement.Devices

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
    // A recording through the gain patch: `patch in.wav out.wav [volume]`,
    // volume in Q8.8 where 256 is unity.
    | [| "patch"; inPath; outPath |]
    | [| "patch"; inPath; outPath; _ |] ->
        let volume = if argv.Length = 4 then uint64 argv[3] else gainUnity
        let source = readWavFile inPath

        let heard =
            runInSim 100_000 Warp11.Placement.Graph.gainGraph { source = source; controls = [ "volume", volume; "mute", 0UL ]; outputPath = Some outPath }

        let peak (w: WavData) = w.samples |> Array.map (fun s -> abs (int s)) |> Array.max
        printfn $"{inPath}: %d{source.FrameCount} frames, peak %d{peak source} → {outPath}: %d{heard.FrameCount} frames, peak %d{peak heard}, volume %d{volume}/256"
        0
    // The Verilog of a saved design, or of the typed gain patch — so the two
    // can be diffed: `emit design.json`, `emit gain`.
    | [| "emit"; "gain" |] ->
        printf $"{Warp11.Verilog.emitDesign gainPatch.def}"
        0
    | [| "emit"; path |] ->
        match Warp11.Placement.DesignFile.load path with
        | Ok g ->
            printf $"{Warp11.Verilog.emitDesign (Warp11.Placement.Elaborate.elaborate g).def}"
            0
        | Error why ->
            eprintfn $"{path}: {why}"
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
    0
