/// The front page's live demo: the same 64×64 Game of Life RTL the KV260 runs,
/// elaborated and simulated here, in the visitor's browser. The bus is
/// `HdlSimBus` — the design itself behind `IGolBus` — so what the page shows is
/// the RTL evolving, not an animation of what the RTL would do.
///
/// It arrives already seeded and running: a demonstration, not an instrument,
/// and a grid holding still until someone finds the Run button reads as broken.
module Warp11.GoL.Browser.Program

open Avalonia
open Avalonia.Browser
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open Warp11.GolView

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Light

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? ISingleViewApplicationLifetime as single ->
            let bus = new HdlSimBus.HdlSimBus(64, 64) :> Bus.IGolBus

            // A random soup, the same recipe as the view's own Soup button.
            let rand = System.Random()
            bus.Load [| for _ in 0..63 -> uint64 (rand.NextInt64()) |]

            single.MainView <- View.view bus None true
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main _ =
    AppBuilder.Configure<App>().UseBrowser().StartBrowserAppAsync "out" |> ignore
    0
