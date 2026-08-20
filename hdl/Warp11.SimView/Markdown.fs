/// Just enough Markdown for a cookbook page: headings, paragraphs, bullets,
/// fenced code, inline code, bold, links (shown as their text — the panel has
/// no navigation), and small tables. Not a Markdown implementation — a reader
/// for the subset the pages are written in, which is the subset that survives
/// being read in a panel three inches wide.
///
/// Hand-rolled rather than taken from a package because the alternative is a
/// dependency that has to survive wasm and trimming, to render eight
/// constructs. The header once said "the day a page needs a table, revisit
/// that" — the day came twice before anyone noticed the mush it rendered,
/// which is its own small lesson about unexercised paths.
module Warp11.SimView.Markdown

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Documents
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media

let private ofChars (chars: char list) = System.String(Array.ofList chars)

let private mono = FontFamily "monospace"

type private Block =
    | Heading of level: int * text: string
    | Paragraph of string
    | Bullet of string
    | Code of string list
    | Table of header: string list option * rows: string list list

/// Group the lines into blocks. A fence swallows everything to its partner, so
/// a `#` or `-` inside a code block stays code.
let private blocks (text: string) =
    let lines = text.Replace("\r\n", "\n").Split '\n' |> Array.toList

    // Pending holds the run of lines being gathered and what it will become, so
    // a wrapped line continues the bullet it belongs to instead of falling out
    // as a paragraph of its own — which is how every one of these pages is
    // written, and how none of them meant to be read.
    let rec go acc pending lines =
        let flush acc =
            match pending with
            | _, [] -> acc
            | asBullet, parts ->
                let text = List.rev parts |> String.concat " "
                (if asBullet then Bullet text else Paragraph text) :: acc

        match lines with
        | [] -> List.rev (flush acc)
        | (line: string) :: rest ->
            let trimmed = line.Trim()
            let continuing = snd pending |> List.isEmpty |> not

            if trimmed.StartsWith "```" then
                let body = rest |> List.takeWhile (fun l -> not ((l: string).Trim().StartsWith "```"))
                let after = rest |> List.skipWhile (fun l -> not ((l: string).Trim().StartsWith "```"))
                go (Code body :: flush acc) (false, []) (List.skip (min 1 (List.length after)) after)
            elif trimmed = "" then
                go (flush acc) (false, []) rest
            elif trimmed.StartsWith "#" then
                let level = trimmed |> Seq.takeWhile ((=) '#') |> Seq.length
                go (Heading(level, trimmed.TrimStart('#').Trim()) :: flush acc) (false, []) rest
            elif trimmed.StartsWith "|" then
                let isTableLine (l: string) = l.Trim().StartsWith "|"
                let run = trimmed :: (rest |> List.takeWhile isTableLine |> List.map (fun l -> l.Trim()))
                let after = rest |> List.skipWhile isTableLine

                let cellsOf (l: string) =
                    l.Trim('|').Split '|' |> Array.toList |> List.map (fun c -> c.Trim())

                let isSeparator cells =
                    cells
                    |> List.forall (fun (c: string) -> c <> "" && c |> Seq.forall (fun ch -> ch = '-' || ch = ':'))

                let table =
                    match List.map cellsOf run with
                    | head :: sep :: body when isSeparator sep -> Table(Some head, body)
                    | body -> Table(None, body)

                go (table :: flush acc) (false, []) after
            elif trimmed.StartsWith "- " then
                go (flush acc) (true, [ trimmed.Substring 2 ]) rest
            elif continuing then
                go acc (fst pending, trimmed :: snd pending) rest
            else
                go acc (false, [ trimmed ]) rest

    go [] (false, []) lines

type private Piece =
    | Plain of string
    | Coded of string
    | Strong of string
    | Emphasis of string

/// Split a line on the inline markers: `code`, **bold** and *italic*. An
/// unclosed marker stays literal text rather than swallowing the rest of the
/// line, which is the failure people actually make while writing a page.
let private pieces (text: string) =
    let rec go acc (current: string) (chars: char list) =
        let flush acc = if current = "" then acc else Plain current :: acc

        /// Everything up to the next occurrence of `closing`, if there is one.
        let upTo (closing: char list) chars =
            let rec search seen chars =
                match chars with
                | [] -> None
                | _ when List.truncate (List.length closing) chars = closing ->
                    Some(List.rev seen |> ofChars, List.skip (List.length closing) chars)
                | c :: after -> search (c :: seen) after

            search [] chars

        let marked closing wrap opening rest =
            match upTo closing rest with
            | Some (body, after) -> go (wrap body :: flush acc) "" after
            | None -> go acc (current + opening) rest

        match chars with
        | [] -> List.rev (flush acc)
        | '`' :: rest -> marked [ '`' ] Coded "`" rest
        | '*' :: '*' :: rest -> marked [ '*'; '*' ] Strong "**" rest
        | '*' :: rest -> marked [ '*' ] Emphasis "*" rest
        | c :: rest -> go acc (current + string c) rest

    go [] "" (List.ofSeq text)

/// `[text](target)` becomes just the text. The panel cannot navigate, so the
/// target is noise here; the site renderer keeps the link.
let private withoutLinks (text: string) =
    System.Text.RegularExpressions.Regex.Replace(text, """\[([^\]]+)\]\([^)]*\)""", "$1")

let private inlines (palette: Theme.Palette) (text: string) (size: float) : Avalonia.FuncUI.Types.IView list =
    [ for piece in pieces (withoutLinks text) do
          match piece with
          | Coded body ->
              Run.create
                  [ Run.text body
                    TextElement.fontFamily mono
                    TextElement.fontSize (size - 1.0)
                    TextElement.foreground palette.code ]
          | Strong body -> Run.create [ Run.text body; TextElement.fontWeight FontWeight.Bold ]
          | Emphasis body -> Run.create [ Run.text body; TextElement.fontStyle FontStyle.Italic ]
          | Plain body -> Run.create [ Run.text body ] ]

let private paragraph (palette: Theme.Palette) text size opacity (margin: Thickness) : Avalonia.FuncUI.Types.IView =
    TextBlock.create
        [ TextBlock.textWrapping TextWrapping.Wrap
          TextBlock.fontSize size
          TextBlock.opacity opacity
          TextBlock.margin margin
          TextBlock.inlines (inlines palette text size) ]

let private render (palette: Theme.Palette) block : Avalonia.FuncUI.Types.IView =
    match block with
    // Headings go through `inlines` too — a heading naming a function shows
    // the name in code face, not the backticks around it.
    | Heading (1, text) ->
        TextBlock.create
            [ TextBlock.inlines (inlines palette text 20.0)
              TextBlock.fontSize 20.0
              TextBlock.margin (Thickness(0.0, 4.0, 0.0, 8.0)) ]
    | Heading (2, text) ->
        TextBlock.create
            [ TextBlock.inlines (inlines palette text 15.0)
              TextBlock.fontSize 15.0
              TextBlock.fontWeight FontWeight.SemiBold
              TextBlock.margin (Thickness(0.0, 14.0, 0.0, 5.0)) ]
    | Heading (_, text) ->
        TextBlock.create
            [ TextBlock.inlines (inlines palette text 13.0)
              TextBlock.fontSize 13.0
              TextBlock.fontWeight FontWeight.SemiBold
              TextBlock.margin (Thickness(0.0, 10.0, 0.0, 4.0)) ]
    | Paragraph text -> paragraph palette text 13.0 0.9 (Thickness(0.0, 0.0, 0.0, 8.0))
    | Bullet text ->
        // A DockPanel rather than a horizontal StackPanel: stacking hands each
        // child unbounded width along its axis, so the text measures to one
        // long line, never wraps, and is clipped by the panel instead.
        DockPanel.create
            [ DockPanel.lastChildFill true
              DockPanel.margin (Thickness(6.0, 0.0, 0.0, 5.0))
              DockPanel.children
                  [ TextBlock.create
                        [ DockPanel.dock Dock.Left
                          TextBlock.text "·"
                          TextBlock.width 14.0
                          TextBlock.opacity 0.6
                          TextBlock.verticalAlignment VerticalAlignment.Top ]
                    paragraph palette text 13.0 0.9 (Thickness 0.0) ] ]
    | Table (header, rows) ->
        let all = (match header with Some h -> [ h ] | None -> []) @ rows
        let cols = all |> List.map List.length |> List.max

        // Equal star columns so the grid can never outgrow the panel — cells
        // wrap instead. Both tables in the tree are three short columns; the
        // day one needs measured columns, revisit that.
        Grid.create
            [ Grid.columnDefinitions (String.concat "," (List.replicate cols "*"))
              Grid.rowDefinitions (String.concat "," (List.replicate (List.length all) "Auto"))
              Grid.margin (Thickness(0.0, 2.0, 0.0, 10.0))
              Grid.children
                  [ for r, row in List.indexed all do
                        for c in 0 .. cols - 1 do
                            let text = if c < List.length row then row[c] else ""
                            let isHeader = Option.isSome header && r = 0

                            TextBlock.create
                                [ Grid.row r
                                  Grid.column c
                                  TextBlock.textWrapping TextWrapping.Wrap
                                  TextBlock.fontSize 12.0
                                  TextBlock.fontWeight (if isHeader then FontWeight.SemiBold else FontWeight.Normal)
                                  TextBlock.opacity (if isHeader then 1.0 else 0.85)
                                  TextBlock.margin (Thickness(0.0, 2.0, 8.0, 2.0))
                                  TextBlock.inlines (inlines palette text 12.0) ] ] ]
    | Code body ->
        Border.create
            [ Border.background palette.codeBackground
              Border.borderThickness 1.0
              Border.borderBrush palette.rule
              Border.cornerRadius 3.0
              Border.padding (Thickness(10.0, 8.0, 10.0, 8.0))
              Border.margin (Thickness(0.0, 2.0, 0.0, 10.0))
              Border.child (
                  // Sideways scroll rather than clipping: the panel is narrow
                  // and code does not wrap, so a long line must be reachable.
                  ScrollViewer.create
                      [ ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                        ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Disabled
                        ScrollViewer.content (
                            TextBlock.create
                                [ TextBlock.fontFamily mono
                                  TextBlock.fontSize 12.0
                                  TextBlock.foreground palette.code
                                  TextBlock.text (String.concat "\n" body) ]
                        ) ]
              ) ]

/// The page, as a stack of blocks. The caller supplies the scroll.
let view (palette: Theme.Palette) (text: string) : Avalonia.FuncUI.Types.IView =
    StackPanel.create [ StackPanel.children [ for block in blocks text -> render palette block ] ]
