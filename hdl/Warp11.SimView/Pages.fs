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

/// Both, in the order they read.
let both (catalog: Catalog) = [ about catalog; source catalog ]
