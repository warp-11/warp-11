# Warp11.SimView

A step-through debugger for [Warp 11](https://warp11.org) designs. Watch any
signal at any depth, poke inputs, page through memories, set breakpoints written
as expressions over your own signal names, see a per-cycle waveform, export VCD.

**It is a component, not an application.** It knows how to watch a signal, window
a memory and draw a trace — the things true of *any* design — and nothing about
what a particular design means. Your project supplies the design and, if you want
them, your own panels.

```
dotnet add package Warp11.SimView.Desktop --prerelease
```

That is the package to reference from a desktop app; it pulls in `Warp11.SimView`
and `Warp11` with it. Reference `Warp11.SimView` directly only if you are
building a non-desktop head — the browser one, say.

## One line, on a design you already have

```fsharp
open Warp11

let blinker =
    design "Blinker" (fun () ->
        let enable = inputBit "enable"
        let led = outputBit "led"
        let count = reg "count" 24
        If enable (fun () -> count + 1UL ==> count)
        slice 23 23 count ==> led)

[<EntryPoint>]
let main _ =
    Warp11.SimView.Desktop.debug "Blinker" blinker
    0
```

Run it and the debugger opens on *your* design: your signals down the left, a
watch list, a memory pane, a waveform, and a breakpoint box that takes
expressions over your own names. Try `count == 0x7fffff` and press **Run**.

That is the whole integration. `Warp11.Gep` and `Warp11.Mandelbrot` open their
debuggers exactly this way.

## Several designs, with pages and presets

`debug` opens one design with nothing behind it. A **catalog** gives the picker
several, each able to arrive with signals already watched and inputs already
driven:

```fsharp
open Warp11.Catalog

let catalog =
    embedded (System.Reflection.Assembly.GetExecutingAssembly()) "Designs.fs"
        [ entry "Blinker" (nameof blinker) (fun () -> blinker)
          |> watching [ "count" ]
          |> poking [ "enable", 1UL ]

          entry "Counter" (nameof counter) (fun () -> counter) ]

[<EntryPoint>]
let main _ =
    Warp11.SimView.Desktop.run (View.FromCatalog(catalog, None)) []
    0
```

`embedded` reads the design's own source out of your assembly, so the source pane
shows the bytes the design was elaborated from rather than a copy that can drift.
Add `Designs.fs` as an `EmbeddedResource` in your `.fsproj` for that to work, and
pass `Some "Blinker"` instead of `None` to open on a particular entry.

The second argument is your panels — see below. `[]` is fine.

## Your own panels

What you actually want to look at is specific to what you built: a Game of Life
grid, a register-map view, an image the design just rendered. Those arrive as
ordinary values:

```fsharp
open Warp11.SimView

let grid : View.Panel =
    { label = "grid"
      placement = View.WithInstruments
      view = fun ctx ->
        // ctx.session drives the design, ctx.snapshot is the current frame,
        // ctx.theme is the window's colours so a panel matches rather than
        // guessing. Draw whatever you like.
        TextBlock.create [ TextBlock.text $"cycle {ctx.snapshot.cycle}" ] }

Warp11.SimView.Desktop.run (View.Attached(session, "Life")) [ grid ]
```

**Placement is the interesting parameter.**

- `WithInstruments` — a tab beside `watch`, `memory` and `waveform`. For a thing
  you look *at*, in the space the instruments already use.
- `Alongside` — its own column, on screen the whole time you work the
  instruments. For reference you read *while* stepping: the tutorial's prose sits
  here so a page can say *poke `enable` and press Step* and you can do it without
  leaving the words.

A host that offers no `Alongside` panel keeps the plain two-column layout; an
empty third of a window is worse than no column at all.

If your catalog has written pages, `Pages.both catalog` gives you the `about` and
`source` panels ready-made.

## Attaching to a design already running

`View.Attached` opens a debugger on a session **someone else owns and is
driving** — an application that is already running your design and wants to look
inside it. The picker becomes a label, and closing the window does not stop the
design.

That is how the Game of Life view offers a **Debugger** button that opens on the
very session it is rendering.

## What you need

- .NET 10
- An Avalonia desktop app. `Warp11.SimView.Desktop` brings `Avalonia.Desktop`
  with it; you supply `main`.

The split between the two packages is not tidiness: `UsePlatformDetect` lives in
`Avalonia.Desktop`, which carries native Win32/X11/macOS backends that a
WebAssembly build cannot scan. Keeping the view platform-neutral is what lets the
same debugger run in a browser — as it does at
[warp11.org/try](https://warp11.org/try/).

## Status

Prerelease. The API is not stable. See
[what is not built](https://github.com/warp-11/warp-11#what-is-not-built).
