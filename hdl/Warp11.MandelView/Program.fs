/// The composition root: pick the bus, open the window. `--sim` (the default
/// when no endpoint is given) renders locally with the software twin — the
/// same `laneTwin` the fabric is judged against, so the picture is identical
/// and only the clock differs. An endpoint argument
/// (`tcp/192.168.1.172:7448`) goes to the board daemon.
module Warp11.MandelView.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Warp11.MandelView.Bus

/// The frame the `mandel-frame` bitstream renders. The daemon reports these
/// in every frame header, but the window needs them before the first one
/// arrives — and the twin needs them to render at all.
let frameWidth = 1400
let frameHeight = 800
let maxIter = 256

type MainWindow(bus: IMandelBus) as this =
    inherit HostWindow()

    do
        this.Title <- "warp11 — mandelbrot"
        this.Width <- 1200.0
        this.Height <- 800.0
        this.Content <- View.view bus frameWidth frameHeight
        this.Closed.Add(fun _ -> bus.Dispose())

type App() =
    inherit Application()

    override this.Initialize() = this.Styles.Add(FluentTheme())

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let bus =
                match desktop.Args |> Option.ofObj |> Option.defaultValue [||] |> Array.tryHead with
                | Some "--sim"
                | None ->
                    new SimulatedBus.SimulatedBus(frameWidth, frameHeight, maxIter) :> IMandelBus
                | Some endpoint -> new ZenohBus.ZenohBus(endpoint) :> IMandelBus

            desktop.MainWindow <- MainWindow(bus)
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main argv =
    AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(argv)
