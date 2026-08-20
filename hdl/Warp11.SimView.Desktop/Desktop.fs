/// Opening the debugger as a desktop application.
///
/// Split from `Warp11.SimView` because `UsePlatformDetect` lives in
/// `Avalonia.Desktop`, which carries the Win32, X11 and macOS backends — native
/// libraries that the WebAssembly build's `ManagedToNativeGenerator` cannot
/// scan and fails outright on. Keeping the view platform-neutral is what lets
/// the same debugger run in a browser at all.
module Warp11.SimView.Desktop

open Avalonia
open Warp11
open Warp11.Debug
open Warp11.SimView

/// Run a debugger as its own application, and block until it closes. This is
/// the call a desktop host's `main` makes.
let run (source: View.Source) (panels: View.Panel list) =
    AppBuilder
        .Configure<Program.App>(fun () -> Program.App(fun () -> Program.DebugWindow(source, panels)))
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime [||]

/// Open a debugger on a design this process owns, with no catalog behind it —
/// what a project reaches for when it has one design in hand and wants to watch
/// it run. `Warp11.Gep` and `Warp11.Mandelbrot` both call this.
let debug (title: string) (design: ModuleDef) =
    let session = new DebugSession(design) :> IDebugSession
    run (View.Attached(session, title)) []
