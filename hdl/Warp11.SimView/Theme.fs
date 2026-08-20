/// The colours the debugger picks by hand, in both variants.
///
/// Avalonia's Fluent theme handles the chrome — panels, buttons, the text you
/// did not colour. What it cannot know is what *this* window means by a colour:
/// that a register is purple, that a fired breakpoint is red, that a code block
/// is a code block. Those are the ones here, and they are the ones that would
/// otherwise be invisible in the variant they were not written for.
module Warp11.SimView.Theme

open Avalonia
open Avalonia.Media
open Avalonia.Styling

/// The colours a panel showing *code* needs, which no other panel does.
///
/// Here rather than taken from a TextMate theme along with the grammar: a theme
/// would be a third palette beside Fluent's and this one, tuned for an editor
/// nobody is looking at, and the source tab would stop matching the tab beside
/// it. These are the window's own hues — a keyword is the purple a register is,
/// a string is the green a code span is — so the two agree by construction.
type Syntax =
    { keyword: IBrush
      stringLiteral: IBrush
      comment: IBrush
      number: IBrush
      typeName: IBrush
      functionName: IBrush
      /// Operators and punctuation: `=`, `->`, and the DSL's own `==>`.
      symbol: IBrush }

type Palette =
    { input: IBrush
      output: IBrush
      reg: IBrush
      wire: IBrush
      /// The selected chip, and the row the picker is sitting on.
      accent: IBrush
      /// What reads against `accent`.
      onAccent: IBrush
      /// Ordinary text this window sets explicitly rather than inheriting.
      text: IBrush
      /// Present but secondary — counts, hints, an empty list's excuse.
      muted: IBrush
      /// Something happened that the reader has to see: a breakpoint fired, a
      /// width did not check, a design would not load.
      alert: IBrush
      code: IBrush
      codeBackground: IBrush
      rule: IBrush
      /// What the application paints behind this window — Fluent's, not ours.
      /// Only wanted where something has to be drawn *over* a line and hide
      /// it, which is the one thing a transparent brush cannot do. Sampled
      /// from a running window rather than guessed; if Fluent ever changes it,
      /// the patch shows up as a rectangle and this is the value to fix.
      background: IBrush
      syntax: Syntax }

let private brush (hex: string) = SolidColorBrush(Color.Parse hex) :> IBrush

let dark =
    { input = brush "#64B5F6"
      output = brush "#81C784"
      reg = brush "#BA92DB"
      wire = brush "#9E9E9E"
      accent = brush "#3A6EA5"
      onAccent = brush "#FFFFFF"
      text = brush "#FFFFFF"
      muted = brush "#9E9E9E"
      alert = brush "#FF4500"
      code = brush "#C8E6A0"
      codeBackground = brush "#141414"
      rule = brush "#3A3A3A"
      background = brush "#000000"
      syntax =
        { keyword = brush "#BA92DB"
          stringLiteral = brush "#C8E6A0"
          comment = brush "#7A8A7A"
          number = brush "#64B5F6"
          typeName = brush "#6FD3C4"
          functionName = brush "#E4D08A"
          symbol = brush "#9E9E9E" } }

/// The same meanings at a contrast that survives a white background — the
/// 800-weight end of each hue rather than the 300-weight end, which is the
/// whole difference between the two lists.
let light =
    { input = brush "#1565C0"
      output = brush "#2E7D32"
      reg = brush "#6A1B9A"
      wire = brush "#546E7A"
      accent = brush "#2C5F92"
      onAccent = brush "#FFFFFF"
      text = brush "#1A1A1A"
      muted = brush "#6B7280"
      alert = brush "#C62828"
      code = brush "#33691E"
      codeBackground = brush "#F3F5F0"
      rule = brush "#CBD1D6"
      background = brush "#FFFFFF"
      syntax =
        { keyword = brush "#6A1B9A"
          stringLiteral = brush "#33691E"
          comment = brush "#6B7280"
          number = brush "#1565C0"
          typeName = brush "#00695C"
          functionName = brush "#8A6D00"
          symbol = brush "#546E7A" } }

let ofVariant (variant: ThemeVariant) = if variant = ThemeVariant.Dark then dark else light

/// What the application is showing right now. `Application.Current` is null in
/// a process that never started one — a check harness elaborating a design —
/// so this answers rather than throwing.
///
/// The *actual* variant, not the requested one: an app that never asked for
/// either — the live views do not — requests `Default` and is shown whatever
/// the platform prefers. Reading the request there says Light while Fluent
/// paints a dark window, which is invisible until something has to match the
/// background rather than contrast with it.
let currentVariant () =
    match Application.Current with
    | null -> ThemeVariant.Light
    | app -> app.ActualThemeVariant

let apply (variant: ThemeVariant) =
    match Application.Current with
    | null -> ()
    | app -> app.RequestedThemeVariant <- variant
