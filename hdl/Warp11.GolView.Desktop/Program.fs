/// The composition root: pick the bus, open the window. `--sim` (default
/// when no endpoint is given) runs the idiomatic software engine locally;
/// `--sim-arrays` / `--sim-bitboard` swap in its faster siblings — the
/// tutorial's software acts, one flag apart. An endpoint argument
/// (`tcp/192.168.1.172:7447`) goes to the board daemon.
module Warp11.GolView.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Warp11.GolView.Bus

type MainWindow(bus: IGolBus, openDebugger: (unit -> unit) option) as this =
    inherit HostWindow()

    do
        this.Title <- "warp11 — game of life"
        this.Width <- 800.0
        this.Height <- 640.0
        this.Content <- View.view bus openDebugger false
        this.Closed.Add(fun _ -> bus.Dispose())

type App() =
    inherit Application()

    override this.Initialize() = this.Styles.Add(FluentTheme())

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let bus, openDebugger =
                match desktop.Args |> Option.ofObj |> Option.defaultValue [||] |> Array.tryHead with
                | Some "--sim"
                | None -> new SimulatedBus.SimulatedBus(Engine.stepIdiomatic) :> IGolBus, None
                | Some "--sim-arrays" -> new SimulatedBus.SimulatedBus(Engine.stepArrays) :> IGolBus, None
                | Some "--sim-bitboard" -> new SimulatedBus.SimulatedBus(Engine.stepBitboard) :> IGolBus, None
                | Some "--hdl" ->
                    // The elaborated design itself, and therefore the one world
                    // a debugger can be opened on.
                    let hdl = new HdlSimBus.HdlSimBus(64, 64)

                    let openDebugger () =
                        Warp11.SimView.Program.DebugWindow(hdl.Session, "game of life — 64x64 RTL")
                            .Show()

                    hdl :> IGolBus, Some openDebugger
                | Some endpoint -> new ZenohBus.ZenohBus(endpoint) :> IGolBus, None

            desktop.MainWindow <- MainWindow(bus, openDebugger)
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main argv =
    AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(argv)
