/// The spike in a browser: the same `Canvas.view`, handed to a single-view
/// lifetime instead of to a window.
module Warp11.Placement.Canvas.Browser.Program

open Avalonia
open Avalonia.Browser
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open Warp11.Graph

type App() =
    inherit Application()

    // Every step reports to the browser console: a fault in a wasm app's
    // start-up task is otherwise silent, which is a blank page and no clue.
    override this.Initialize() =
        try
            System.Console.WriteLine "spike: Initialize"
            this.Styles.Add(FluentTheme())
            this.Styles.Add(nodeEditorTheme "Warp11.Placement.Canvas.Browser")
            this.RequestedThemeVariant <- Styling.ThemeVariant.Light
            System.Console.WriteLine "spike: styles added"
        with e ->
            System.Console.Error.WriteLine $"spike: Initialize failed: {e}"
            reraise ()

    override this.OnFrameworkInitializationCompleted() =
        try
            match this.ApplicationLifetime with
            | :? ISingleViewApplicationLifetime as single ->
                System.Console.WriteLine "spike: building view"
                single.MainView <- ContentControl(Content = Warp11.Placement.Canvas.FuncCanvas.view
                        { graph = macGraph 1 1 1; live = None; opener = None; file = None })
                System.Console.WriteLine "spike: view set"
            | _ -> System.Console.WriteLine "spike: no single-view lifetime"
        with e ->
            System.Console.Error.WriteLine $"spike: view failed: {e}"
            reraise ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main _ =
    let start = AppBuilder.Configure<App>().UseBrowser().StartBrowserAppAsync "out"

    start.ContinueWith(fun (t: System.Threading.Tasks.Task) ->
        if t.IsFaulted then
            System.Console.Error.WriteLine $"spike: start-up faulted: {t.Exception}"
        else
            System.Console.WriteLine "spike: start-up completed")
    |> ignore

    0
