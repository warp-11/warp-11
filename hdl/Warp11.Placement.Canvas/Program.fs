/// The desktop head: a window around the FuncUI canvas.
module Warp11.Placement.Canvas.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Warp11.Placement.Graph

type SpikeWindow(g: Graph, live: FuncCanvas.Live option) as this =
    inherit HostWindow()

    do
        this.Title <- "warp11 — canvas spike"
        this.Width <- 1200.0
        this.Height <- 700.0
        this.Content <- FuncCanvas.view g live

type App(g: Graph, live: FuncCanvas.Live option) =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Light

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- SpikeWindow(g, live)
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

/// `--unwired` opens the graph with one wire missing — the skip wire
/// `input.c → sum.y` — so that drawing a wire can be tried by hand.
let private startingGraph (argv: string[]) =
    let g = macGraph 1 1 1

    if Array.contains "--unwired" argv then
        { g with edges = g.edges |> List.filter (fun e -> e.``to`` <> pin "sum" "y") }
    else
        g

/// `patch file.wav` opens the gain patch with the recording playing into it
/// — the canvas over a running design. Anything else opens `mac`, still.
[<EntryPoint>]
let main argv =
    let g, live =
        match argv with
        | [| "patch"; wavPath |] -> gainGraph, Some(Patch.openGain wavPath (AudioSink.available ()))
        | _ -> startingGraph argv, None

    AppBuilder.Configure<App>(fun () -> App(g, live)).UsePlatformDetect().StartWithClassicDesktopLifetime argv
