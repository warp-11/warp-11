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
open Warp11.Catalog
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
///
/// No catalog means no `about` and no `source`: both slice text out of a
/// catalog's embedded source file, and a design reached this way is not in one.
/// `Pages.verilog` is the exception and so it is here — it reads the design off
/// the session, and what a design emits is knowable from the design alone.
let debug (title: string) (design: ModuleDef) =
    let session = new DebugSession(design) :> IDebugSession
    run (View.Attached(session, title)) [ Pages.verilog ]

/// Open a debugger on a catalog this process owns — the call a project makes
/// once it has designs of its own.
///
/// **The difference from `debug` is not just the picker.** A catalog-opened
/// design gets its ports watched on open, its entry's `watching` signals added
/// on top of those, and its entry's `poking` inputs applied — so the first Step
/// a newcomer presses moves something. `debug` attaches to a bare session,
/// which has none of that: an empty watch list and every input at zero, where a
/// design gated on an `enable` looks broken rather than idle.
///
/// Panels are `verilog` alone, as `debug`'s are. A catalog carrying real prose
/// wants `Pages.about` and `Pages.source` beside it, and that is a panel list
/// rather than a default — `Warp11.Tutorial.Debugger` is the worked example.
let debugCatalog (catalog: Catalog) =
    run (View.FromCatalog(catalog, None)) [ Pages.verilog ]
