/// The export against the design: the printed F# is compiled, and what it
/// elaborates to is compared with what the drawing does, byte for byte.
module Warp11.Placement.ExportChecks

open Warp11
open Warp11.Fu
open Warp11.Graph
open Warp11.Elaborate
open Warp11.Placement.Placement
open Warp11.Placement.ChainChecks
open Warp11.Placement.MeetingChecks
open Warp11.Placement.SocketChecks
open Warp11.Placement.BoxChecks

/// Each design's source compiled by `dotnet fsi` against these very
/// assemblies, in one run: the Verilog each prints.
let compileExports (sources: (Graph * string) list) : string list =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"warp11-export-{System.Guid.NewGuid()}")
    System.IO.Directory.CreateDirectory dir |> ignore

    let files =
        sources
        |> List.map (fun (g, source) ->
            let path = System.IO.Path.Combine(dir, $"{g.name}.fs")
            System.IO.File.WriteAllText(path, source)
            path)

    let fence = "=====DESIGN====="

    let script =
        [ $"#r \"{typeof<Expr>.Assembly.Location}\""
          $"#r \"{typeof<Graph>.Assembly.Location}\""
          $"#load " + (files |> List.map (fun f -> $"\"{f}\"") |> String.concat " ")
          "open Warp11" ]
        @ [ for g, _ in sources ->
                let value = string (System.Char.ToLowerInvariant g.name[0]) + g.name.Substring 1
                "printf \"%s%s\" \"" + fence + "\" (emitDesign Exported." + g.name + "." + value + ".def)" ]
        |> String.concat "\n"

    let scriptPath = System.IO.Path.Combine(dir, "compare.fsx")
    System.IO.File.WriteAllText(scriptPath, script)

    // The `dotnet` host beside the runtime this process runs on — never the
    // process's own module, which under an apphost is this program itself,
    // and would re-run these checks without end.
    let dotnet =
        let beside =
            System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet")
            )

        match System.Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
        | null
        | "" when System.IO.File.Exists beside -> beside
        | null
        | "" -> failwith $"no dotnet host at {beside} and DOTNET_HOST_PATH is not set"
        | path -> path

    let info = System.Diagnostics.ProcessStartInfo(dotnet, $"fsi \"{scriptPath}\"", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    use fsi = System.Diagnostics.Process.Start info
    let errors = fsi.StandardError.ReadToEndAsync()
    let output = fsi.StandardOutput.ReadToEnd()
    fsi.WaitForExit()

    if fsi.ExitCode <> 0 then
        failwith $"dotnet fsi ({dir}): {errors.Result}"

    output.Split(fence, System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

// ---------------------------------------------------------------------------
// UD11 — The export is the design. Every shape the model has, printed as
// typed F#, compiled, and each one's Verilog compared byte for byte with
// the drawing's own elaboration: a chain with a connection on each edge; a
// design as a box, and as lanes; controls from the design's ports, a number
// box, a constant and a setting; a broadcast and a balance into a merge;
// five streams merged by priority; a farm in order and not; a unit shared
// by one box's sockets, by three boxes, and a multiplier a band; a
// combinational unit that reads its operands; joins, fields and a buffer;
// and the examples.

// CHECK
let exportIsTheDesign () : bool =
    let priority =
        { policy = ArbitrationPolicy.Priority
          timing = ArbitrationTiming.Registered }

    // A signed multiplier as a stage straight off the boundary: its
    // operands are read at their formats, and the export prints the read.
    let reads =
        { emptyGraph "Reads" rate Warp11.Audio.gainMultiplier.operands.fields with
            outputs = [ { name = "product"; fields = Warp11.Audio.gainMultiplier.results.fields } ]
            boxes = [ box "multiplier" Warp11.Audio.gainMultiplier.name [] ]
            edges = [ wire (port "input" "in") (port "multiplier" streamInlet); wire (port "multiplier" streamOutlet) (port "output" "product") ] }

    let designs =
        [ gainGraph
          threeBandEq Connection.Forward
          { threeBandEq Connection.Both with name = "ThreeBandEqBoth" }
          twice
          controlsGraph NumberBox
          { controlsGraph ConstantBox with name = "ControlsConstant" }
          diamond FanOut.Broadcast
          diamond FanOut.Balance
          merged priority
          farmed 4 true
          farmed 4 false
          lanes 2
          sharedGain Arbitration.roundRobin
          sharedGain priority
          sharedEq 3
          sharedEq 1
          reads
          macDrawn
          crossed
          splitAndJoined
          buffered 4 ]
        // (`GainWide4` and `GainWide16` are left out: their streams are more
        // than six fields, where the typed form's `pinsN` stop, which the
        // export says. `Copy` is a boundary wired to itself, which the
        // typed form has nothing to say about.)
        @ [ for _, example in Examples.all Examples.recordingRate do
                if not (List.contains example.name [ "GainPatch"; "ThreeBandEq"; "Twice"; "Controls"; "Mac"; "GainWide4"; "GainWide16"; "Copy" ]) then
                    yield example ]

    // The gain design carries a mapping, so the export's board, path and
    // build function are compiled too.
    let mappingFor (g: Graph) : Warp11.Mapping.Mapping option =
        if g.name = gainGraph.name then Some { board = kv260; path = viaHostMemory; plugged = [] } else None

    let sources =
        designs
        |> List.map (fun g ->
            match Warp11.Export.exportWith (mappingFor g) g with
            | Ok source -> g, source
            | Error why -> failwith $"{g.name}: {why}")

    let compiled = compileExports sources
    let expected = designs |> List.map (fun g -> emitDesign (elaborate g).def)

    for g, (c, e) in List.zip designs (List.zip compiled expected) do
        if c <> e then
            let lc, le = c.Split '\n', e.Split '\n'
            let at = Seq.zip lc le |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min lc.Length le.Length)
            let line (lines: string array) = (if at < lines.Length then lines[at] else "(end)") |> fun l -> l.Substring(0, min l.Length 150)
            printfn $"      {g.name}: first difference at line %d{at + 1}"
            printfn $"        exported: {line lc}"
            printfn $"        drawn:    {line le}"

    printfn $"      %d{designs.Length} designs exported, compiled and compared"
    compiled.Length = designs.Length && List.forall2 (fun (c: string) (e: string) -> c = e) compiled expected
