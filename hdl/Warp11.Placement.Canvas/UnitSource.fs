/// A unit's source as the canvas holds it: what a fresh one says, and the
/// name it defines. Both heads read source; only the desktop compiles it.
module Warp11.Placement.Canvas.UnitSource

/// The name the source defines: its first top-level `let`.
let definedName (source: string) : string option =
    let m = System.Text.RegularExpressions.Regex.Match(source, @"^\s*let\s+(?:inline\s+)?([A-Za-z_][A-Za-z0-9_]*)", System.Text.RegularExpressions.RegexOptions.Multiline)
    if m.Success then Some m.Groups[1].Value else None

/// What a fresh unit box starts as: the channels swapped, one line each way.
let template =
    String.concat
        "\n"
        [ "let swap ="
          "    fu \"swap\" (pins2 (\"left\", signedInt 24) (\"right\", signedInt 24)) (pins2 (\"left\", signedInt 24) (\"right\", signedInt 24))"
          "        (fun (l, r) -> r, l)" ]
