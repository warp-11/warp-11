/// The desktop head: a window around the FuncUI canvas.
module Warp11.Placement.Canvas.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Warp11.Placement
open Warp11.Placement.Graph

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
/// - `patch file.wav` — the gain design with the recording playing into it,
///   the canvas over a running design;
/// - `edit design.json [file.wav]` — a design file (a new design if there is
///   no file yet), and with a recording, `Open in sim` runs it;
/// - nothing — `mac`, to look at.
let private opening (argv: string[]) : FuncCanvas.Opening =
    let none: FuncCanvas.Opening =
        { graph = macGraph 1 1 1
          live = None
          opener = None
          file = None }

    match argv with
    | [| "patch"; wavPath |] ->
        let live = Patch.openGain wavPath (AudioSink.available ())
        { none with graph = gainGraph; live = Some live; opener = Some live.reopen }
    | [| "edit"; designPath |]
    | [| "edit"; designPath; _ |] ->
        let g =
            match DesignFile.load designPath with
            | Ok g -> g
            | Error why when not (System.IO.File.Exists designPath) -> ignore why; emptyGraph (System.IO.Path.GetFileNameWithoutExtension designPath)
            | Error why -> failwith why

        let opener = if argv.Length = 3 then Some(Patch.wavOpener argv[2] (AudioSink.available ())) else None
        { graph = g; live = None; opener = opener; file = Some designPath }
    | _ -> none

[<EntryPoint>]
let main argv =
    let o = opening argv
    AppBuilder.Configure<App>(fun () -> App(o)).UsePlatformDetect().StartWithClassicDesktopLifetime argv
