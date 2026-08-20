/// Small pieces of chrome that more than one window wants, and that Avalonia
/// does not have.
///
/// Here rather than in `View` because `View` is the debugger's own window: the
/// live views are separate apps that happen to want the same box round a group
/// of controls, and reaching into a window for it would be the wrong seam.
module Warp11.SimView.Controls

open Avalonia
open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.Layout

/// A group of controls with its name set into the top border, the way an HTML
/// fieldset sets its legend.
///
/// Avalonia has no such control and no way to interrupt a border, so this is
/// the same trick the CSS is: an ordinary box, and the name drawn over its top
/// line on a patch of the colour behind it. The patch is why `Palette` has to
/// know the window's background — a transparent label would show the line
/// straight through the text.
///
/// It follows that the patch is only right over that background. Put one of
/// these inside a panel that paints its own fill and the legend will sit on a
/// rectangle of the wrong colour.
let fieldset (palette: Theme.Palette) (name: string) (content: Avalonia.FuncUI.Types.IView) =
    Grid.create
        [ Grid.children
              [ Border.create
                    [ Border.borderThickness 1.0
                      Border.borderBrush palette.rule
                      Border.cornerRadius 4.0
                      // Half the legend's height, so the line runs through its
                      // middle rather than under it.
                      Border.margin (Thickness(0.0, 7.0, 0.0, 0.0))
                      Border.padding (Thickness(8.0, 6.0, 8.0, 6.0))
                      Border.child content ]
                TextBlock.create
                    [ TextBlock.horizontalAlignment HorizontalAlignment.Left
                      TextBlock.verticalAlignment VerticalAlignment.Top
                      TextBlock.margin (Thickness(9.0, 0.0, 0.0, 0.0))
                      TextBlock.padding (Thickness(4.0, 0.0, 4.0, 0.0))
                      TextBlock.background palette.background
                      // Foreground rather than opacity: opacity would take the
                      // patch with it and the line would show through.
                      TextBlock.foreground palette.muted
                      TextBlock.fontSize 11.0
                      TextBlock.text name ] ] ]
