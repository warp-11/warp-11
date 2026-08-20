module Warp11.Tutorial.Browser.Program

open Avalonia
open Avalonia.Browser
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open System.Runtime.InteropServices.JavaScript

/// The design named in the URL fragment, if there is one. Every tutorial page
/// on the site links here with its own design — `/try/#Priority%20mux` — so a
/// page that says "press Step" arrives beside the design it is describing
/// rather than at whatever the catalog lists first.
///
/// The fragment is the design's `label` in `Warp11.Tutorial.Registry`, which is
/// what `View.debugger` matches against. An unknown one is not an error there:
/// it falls back to the first entry, which is where a reader arriving without a
/// fragment lands anyway.
let private requestedDesign () =
    match JSHost.GlobalThis.GetPropertyAsJSObject "location" with
    | null -> None
    | location ->
        match location.GetPropertyAsString "hash" with
        | null | "" | "#" -> None
        | hash -> Some(System.Uri.UnescapeDataString(hash.TrimStart '#'))

/// The tutorial in a browser: the same composed window the desktop head opens,
/// handed to a single-view lifetime instead of to a `HostWindow`.
type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Light

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? ISingleViewApplicationLifetime as single ->
            single.MainView <- Warp11.Tutorial.Debugger.window (requestedDesign ())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

[<EntryPoint>]
let main _ =
    AppBuilder.Configure<App>().UseBrowser().StartBrowserAppAsync "out" |> ignore
    0
