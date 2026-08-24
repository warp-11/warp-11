/// The two panels a catalog can offer about the design on screen: what it is,
/// and what it is written as.
///
/// Offered rather than built in. The debugger's own three panels are about any
/// design at all — its signals, its memories, its trace — and these two are
/// about a design someone has written *prose* for, which is a different claim.
/// A host that has a catalog with pages includes them; a host debugging one
/// live design does not, and gets a window with no empty tabs in it.
module Warp11.SimView.Pages

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.FuncUI.DSL
open Avalonia.Media
open Warp11
open Warp11.Catalog

let private mono = FontFamily "monospace"

let private missing note : Avalonia.FuncUI.Types.IView =
    TextBlock.create
        [ TextBlock.opacity 0.5
          TextBlock.textWrapping TextWrapping.Wrap
          TextBlock.text note ]

/// The design's page, rendered from Markdown.
let about (catalog: Catalog) : View.Panel =
    { label = "about"
      placement = View.Alongside
      view =
        fun ctx ->
            ScrollViewer.create
                [ ScrollViewer.padding (Thickness(0.0, 0.0, 12.0, 0.0))
                  ScrollViewer.content (
                      // Inside the scrolled content rather than on the viewer,
                      // because the viewer's own padding sits behind the scroll
                      // bar: without this the last character of a wrapped line
                      // ends up under it.
                      Border.create
                          [ Border.padding (Thickness(0.0, 0.0, 8.0, 0.0))
                            Border.child (
                                match ctx.entry |> Option.bind (fun e -> catalog.doc e.binding) with
                                | Some text -> Markdown.view ctx.palette text
                                | None -> missing "no page for this design yet"
                            ) ]
                  ) ] }

/// The text that defines the design, sliced out of the catalog's own source.
let source (catalog: Catalog) : View.Panel =
    { label = "source"
      placement = View.Alongside
      view =
        fun ctx ->
            ScrollViewer.create
                [ ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                  ScrollViewer.content (
                      match ctx.entry |> Option.bind (fun e -> catalog.source e.binding) with
                      | Some text -> Highlight.view ctx.palette text
                      | None -> missing "this design is not in the catalog's source file"
                  ) ] }

/// What the design *became*: the Verilog `emitDesign` produces, beside the F#
/// it was elaborated from.
///
/// The claim the whole toolchain rests on is that one source drives the
/// simulator and the silicon. This is the only place both halves of it are on
/// screen at once — `source` is what somebody wrote, this is what a synthesiser
/// is handed, and they are two buttons in the same column.
///
/// **Unlike `about` and `source`, this takes no catalog.** It reads the design
/// off the session, which is the only thing it needs, so an *attached* debugger
/// gets it too: `Warp11.Gep`'s `-- debug operator-engine` and the Game of Life
/// view's Debugger button both open on a design no catalog carries, and both
/// can show this.
///
/// Emission is remembered per design, keyed by reference, and it does two jobs
/// rather than one. Emitting is not free — measured across the oracle catalog
/// it is 0.1 to 3.5 ms, but GEP's cluster is **203 ms and 2.9 MB of text**, and
/// a panel re-renders on every snapshot. The second job is the one that
/// actually keeps the window alive: handing back the *same string instance*
/// every render means the diff sees an unchanged property and leaves the text
/// control alone. With both, the cluster runs at 24.5k cycles/s with this tab
/// open and its whole 2.9 MB on screen.
///
/// A design that will not emit shows why, rather than throwing. `emitDesign`
/// runs `checkWidths`, `checkNames` and `checkStreams`, so this doubles as the
/// place their refusals are readable — and a design someone has a debugger open
/// on is exactly the kind that might not be finished.
let verilog: View.Panel =
    let emitted = System.Collections.Generic.Dictionary<ModuleDef, string>(HashIdentity.Reference)

    { label = "verilog"
      placement = View.Alongside
      view =
        fun ctx ->
            ScrollViewer.create
                [ ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                  ScrollViewer.content (
                      match ctx.session with
                      | None -> missing "no design loaded"
                      | Some session ->
                          let text =
                              match emitted.TryGetValue session.Design with
                              | true, cached -> cached
                              | _ ->
                                  let rendered =
                                      try
                                          emitDesign session.Design
                                      with ex ->
                                          $"this design does not emit — elaboration refused it:\n\n{ex.Message}"

                                  emitted[session.Design] <- rendered
                                  rendered

                          SelectableTextBlock.create
                              [ SelectableTextBlock.fontFamily mono
                                SelectableTextBlock.fontSize 12.0
                                SelectableTextBlock.foreground ctx.palette.code
                                SelectableTextBlock.text text ]
                  ) ] }

/// The catalog pair, in the order they read.  is not in it: it needs
/// no catalog, and a host that wants all three says .
let both (catalog: Catalog) = [ about catalog; source catalog ]
