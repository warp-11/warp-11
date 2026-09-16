/// The desktop head: a window around the FuncUI canvas.
module Warp11.Placement.Canvas.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Warp11
open Warp11.Placement
open Warp11.Graph
open Warp11.Devices

type CanvasWindow(o: FuncCanvas.Opening) as this =
    inherit HostWindow()

    do
        this.Title <- "warp11 — design canvas"
        this.Width <- 1400.0
        this.Height <- 800.0
        this.Content <- FuncCanvas.view o

type App(o: FuncCanvas.Opening) =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Light

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- CanvasWindow o
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

/// What to open:
///
/// - `play file.wav` — the gain design with the recording playing into it,
///   the canvas over a running design;
/// - `edit design.json [file.wav|.pgm|.csv]` — a design file (a new design
///   if there is no file yet), and with a source, `Open in sim` runs it;
/// - nothing — `mac`, to look at.
let private opening (argv: string[]) : FuncCanvas.Opening =
    let none: FuncCanvas.Opening =
        { graph = macGraph 1 1 1
          live = None
          opener = None
          file = None }

    match argv with
    | [| "play"; wavPath |] ->
        let live = Session.openGain wavPath (AudioSink.available ())
        { none with graph = gainGraph; live = Some live; opener = Some live.reopen }
    | [| "edit"; designPath |]
    | [| "edit"; designPath; _ |] ->
        // With a source — a recording, an image, a table — `Open in sim` runs
        // the design on it; a new design is made for a recording's rate.
        let source = if argv.Length = 3 then Some(Session.sourceOf argv[2] (AudioSink.available ())) else None

        let g =
            match DesignFile.load designPath with
            | Ok g -> g
            | Error why when not (System.IO.File.Exists designPath) ->
                ignore why
                emptyGraph (System.IO.Path.GetFileNameWithoutExtension designPath) (if argv.Length = 3 then Session.rateOf argv[2] else defaultSampleRate)
            | Error why -> failwith why

        let opener = source |> Option.map (fun (source, heard) -> fun g knobs -> Session.openWith g source knobs (Some heard))
        { graph = g; live = None; opener = opener; file = Some designPath }
    | _ -> none

[<EntryPoint>]
let main argv =
    // This head has a compiler: a design carrying a written unit opens here.
    Warp11.Factories.compileUnit <- Some Compiler.compileUnit

    match argv with
    // `unit file.fs`: compile a unit's source and say what it is — the
    // smoke test of the compiler service, from the shell.
    | [| "unit"; path |] ->
        let source = System.IO.File.ReadAllText path

        match Compiler.definedName source with
        | None ->
            eprintfn $"{path}: no `let` to name the unit by"
            1
        | Some name ->
            match Compiler.compileUnit name source with
            | Ok f ->
                match f.make defaultSampleRate Map.empty with
                | Ok u ->
                    printfn $"{u.name}: in [{describePins u.operands.fields}] out [{describePins u.results.fields}] controls [{describePins u.controls}]"
                    0
                | Error why ->
                    eprintfn $"{why}"
                    1
            | Error why ->
                eprintfn $"{path}: {why}"
                1
    | _ ->

    let o = opening argv
    AppBuilder.Configure<App>(fun () -> App(o)).UsePlatformDetect().StartWithClassicDesktopLifetime argv
