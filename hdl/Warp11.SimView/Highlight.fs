/// F# syntax colouring for the panel that shows a design's source.
///
/// The tokens come from Ionide's F# TextMate grammar, embedded in this assembly
/// and read by TextMateSharp — see `Grammars/README.md` for what is vendored and
/// why. The colours do not: they are `Theme.Palette`'s, so the source tab is the
/// same window as the tab beside it in both variants.
module Warp11.SimView.Highlight

open System
open System.IO
open System.Reflection
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.FuncUI.DSL
open Avalonia.Media
open TextMateSharp.Grammars
open TextMateSharp.Registry
open TextMateSharp.Internal.Grammars.Reader
open TextMateSharp.Internal.Themes.Reader

let private mono = FontFamily "monospace"

let private scopeName = "source.fsharp"

let private grammarResource = "Warp11.SimView.Grammars.fsharp.tmLanguage.json"

/// A line the grammar is still chewing on when the limit runs out stops being
/// coloured; it does not stall the panel it is in.
let private budget = TimeSpan.FromSeconds 1.0

let private embedded (name: string) =
    Assembly.GetExecutingAssembly().GetManifestResourceStream name

/// What TextMateSharp asks its host for. This host has one grammar and no
/// theme — `Registry` insists on *a* theme to construct, and every colour here
/// comes from the palette instead, so it is handed an empty one.
type private OneGrammar() =
    interface IRegistryOptions with
        member _.GetGrammar(scope) =
            if scope <> scopeName then
                null
            else
                use reader = new StreamReader(embedded grammarResource)
                GrammarReader.ReadGrammarSync reader

        member _.GetInjections _ = null
        member _.GetTheme _ = null

        member _.GetDefaultTheme() =
            let empty = """{ "name": "none", "settings": [] }"""
            use reader = new StreamReader(new MemoryStream(Text.Encoding.UTF8.GetBytes empty))
            ThemeReader.ReadThemeSync reader

/// Read once, on the first source tab opened rather than at startup: a design
/// loads without ever asking for this. `None` if the grammar could not be read —
/// a source panel in one colour is worth more than a source panel that throws.
let private grammar =
    lazy
        (try
            Some(Registry(OneGrammar()).LoadGrammar scopeName)
         with _ ->
             None)

/// Which of the window's colours a scope means.
///
/// Scopes arrive outermost-first — `source.fsharp binding.fsharp keyword.fsharp`
/// — so the most specific one is the last, and that is the end this reads from.
/// `keyword.symbol` before `keyword`: the arrow and the `==>` are punctuation
/// that happens to be scoped as a keyword, and colouring them like `let` makes
/// the whole listing shout.
let private colorOf (palette: Theme.Palette) (scopes: string seq) =
    let syntax = palette.syntax

    let pick (scope: string) =
        if scope.StartsWith "comment" then Some syntax.comment
        elif scope.StartsWith "string" then Some syntax.stringLiteral
        elif scope.StartsWith "constant" then Some syntax.number
        elif scope.StartsWith "keyword.symbol" || scope.StartsWith "punctuation" then Some syntax.symbol
        elif scope.StartsWith "keyword" || scope.StartsWith "storage" then Some syntax.keyword
        elif scope.StartsWith "entity.name.type" || scope.StartsWith "support.type" then Some syntax.typeName
        elif scope.StartsWith "entity.name.function" || scope.StartsWith "support.function" then
            Some syntax.functionName
        else
            None

    scopes |> Seq.rev |> Seq.tryPick pick |> Option.defaultValue palette.text

/// The grammar scopes `0UL` as the number `0` followed by an unscoped `UL`, so
/// a half-coloured literal is what a faithful reading produces. Width-typed
/// literals are how every design in this repo is written, so the suffix is
/// stitched back onto the number it belongs to.
let private isLiteralSuffix (text: string) =
    text.Length > 0 && text |> Seq.forall (fun c -> "yusnlfmYUSNLFM".Contains c)

/// One line's tokens as runs. A token can be reported past the end of the line
/// it came from — the grammar matches an implied newline the string does not
/// have — so both ends are clamped rather than trusted.
let private lineRuns (palette: Theme.Palette) (line: string) (tokens: IToken seq) : Avalonia.FuncUI.Types.IView list =
    let step (afterNumber, gathered) (token: IToken) =
        let start = min token.StartIndex line.Length
        let stop = min token.EndIndex line.Length

        if stop <= start then
            afterNumber, gathered
        else
            let text = line.Substring(start, stop - start)
            let scoped = colorOf palette token.Scopes

            let color =
                if afterNumber && scoped = palette.text && isLiteralSuffix text then
                    palette.syntax.number
                else
                    scoped

            let run =
                Run.create [ Run.text text; TextElement.foreground color ] :> Avalonia.FuncUI.Types.IView

            color = palette.syntax.number, run :: gathered

    let _, gathered = Seq.fold step (false, []) tokens
    List.rev gathered

/// The source as coloured runs. Lines are read in order and carry the grammar's
/// state along, because that state is the only thing that knows this line is
/// inside a block comment or a triple-quoted string the line before it opened.
let private runs (palette: Theme.Palette) (text: string) : Avalonia.FuncUI.Types.IView list =
    match grammar.Value with
    | None -> [ Run.create [ Run.text text ] ]
    | Some grammar ->
        let lines = text.Replace("\r\n", "\n").Split '\n'
        let newline: Avalonia.FuncUI.Types.IView = Run.create [ Run.text "\n" ]

        let read (state: IStateStack, gathered) (line: string) =
            let result = grammar.TokenizeLine(LineText line, state, budget)
            result.RuleStack, lineRuns palette line result.Tokens :: gathered

        let _, gathered = Array.fold read (null, []) lines

        gathered
        |> List.rev
        |> List.mapi (fun index line -> if index < lines.Length - 1 then line @ [ newline ] else line)
        |> List.concat

/// The source, coloured and still selectable — a listing you cannot copy out of
/// is a picture of code.
let view (palette: Theme.Palette) (text: string) : Avalonia.FuncUI.Types.IView =
    SelectableTextBlock.create
        [ SelectableTextBlock.fontFamily mono
          SelectableTextBlock.fontSize 12.0
          SelectableTextBlock.inlines (runs palette text) ]
