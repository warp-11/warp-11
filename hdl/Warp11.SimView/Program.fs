/// The window and the application shell — the reusable half of "open a
/// debugger". Which designs it shows and which panels it grows are the host's
/// business, and arrive as arguments.
///
/// `DebugWindow` is public on purpose: another app opens one beside its own
/// window with a single call, which is the whole point of the session living in
/// the library rather than in a window.
module Warp11.SimView.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Warp11.Debug

type DebugWindow(source: View.Source, panels: View.Panel list) as this =
    inherit HostWindow()

    do
        this.Title <- "warp11 — debugger"
        this.Width <- 1500.0
        this.Height <- 820.0
        this.Content <- View.debugger source panels

    /// Open a debugger on a session someone else owns and is driving — the
    /// side-by-side case. Closing this window leaves the session running.
    new(session: IDebugSession, title: string) =
        DebugWindow(View.Attached(session, title), [])

type App(view: unit -> Avalonia.Controls.Control) =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        // Light until someone says otherwise. The chip in the header is the
        // only thing that says otherwise.
        this.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Light

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop -> desktop.MainWindow <- view () :?> Avalonia.Controls.Window
        | :? ISingleViewApplicationLifetime as single -> single.MainView <- view ()
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
