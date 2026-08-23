/// The composition root: pick the bus, open the window.
///
/// **The default is `--hdl`: the elaborated design, in the simulator**, which
/// is the only bus a debugger can attach to and the thing anyone opening this
/// app is almost certainly here to see. The software engines are the tutorial's
/// optimization ladder and are opt-in — `--software` for the idiomatic one,
/// `--software-arrays` / `--software-bitboard` for its faster siblings.
/// An endpoint argument (`tcp/192.168.1.172:7447`) goes to the board daemon.
///
/// They were called `--sim*` until 2026-08-22, which read as "the simulator"
/// and meant the opposite: the simulator is what `--hdl` runs, and `--sim` was
/// the one mode with no design in it at all.
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
                | Some "--hdl"
                | None ->
                    // The elaborated design itself, and therefore the one world
                    // a debugger can be opened on. The default, because it is
                    // what this app exists to show.
                    let hdl = new HdlSimBus.HdlSimBus(64, 64)

                    let openDebugger () =
                        Warp11.SimView.Program.DebugWindow(hdl.Session, "game of life — 64x64 RTL")
                            .Show()

                    hdl :> IGolBus, Some openDebugger
                | Some "--software" -> new SimulatedBus.SimulatedBus(Engine.stepIdiomatic) :> IGolBus, None
                | Some "--software-arrays" -> new SimulatedBus.SimulatedBus(Engine.stepArrays) :> IGolBus, None
                | Some "--software-bitboard" -> new SimulatedBus.SimulatedBus(Engine.stepBitboard) :> IGolBus, None
                // An endpoint never starts with `--`, so anything that does and
                // got this far is a typo. Falling through to Zenoh would have it
                // quietly dial a board that is not there and show a view that
                // never updates.
                | Some flag when flag.StartsWith "--" ->
                    failwith $"unknown option {flag} — expected --hdl, --software, --software-arrays, --software-bitboard, or a Zenoh endpoint"
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
