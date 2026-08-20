/// The tutorial's debugger: the standard one, plus the two panels that make it
/// a tutorial.
///
/// This is the shape every project is meant to reach for. `Warp11.SimView`
/// knows how to watch signals, window a memory and draw a trace, and knows
/// nothing about what any particular design *means*. A project that does know
/// —  the tutorial and its prose, the Game of Life and its grid, an AXI slave
/// and its register map — brings that knowledge here, where it belongs, rather
/// than the debugger growing a special case for each.
module Warp11.Tutorial.Debugger

open Warp11.SimView

/// What the tutorial adds: the design's page, and the source that defines it.
///
/// The page tab is renamed here rather than in `Pages`, which offers it to any
/// host with a catalog: a design's prose page is "about" that design in
/// general, and is the tutorial only in this window.
let panels =
    [ { Pages.about Registry.catalog with
          label = "tutorial" }
      Pages.source Registry.catalog ]

/// The whole tutorial window, for a desktop host or a browser one.
let window initial =
    View.debugger (View.FromCatalog(Registry.catalog, initial)) panels

/// What a desktop host hands to `Warp11.SimView.Desktop.run`.
let source initial = View.FromCatalog(Registry.catalog, initial)
