/// A unit typed into the canvas, compiled where it is typed: the F# compiler
/// service evaluates the source in this process, against the library this
/// canvas runs on, and hands back the unit as a value. Desktop only — the
/// browser head keeps the palette it was built with, and a design carrying a
/// written unit refuses to open there.
module Warp11.Placement.Canvas.Compiler

open System.IO
open FSharp.Compiler.Interactive.Shell
open Warp11
open Warp11.Fu
open Warp11.Factories

/// The evaluation session, made on first use: the library referenced from
/// where it is loaded, its modules opened, so a source reads as a line of a
/// design file would.
let private session =
    lazy
        (let config = FsiEvaluationSession.GetDefaultConfiguration()
         let quiet = new StringWriter()

         let fsi =
             FsiEvaluationSession.Create(
                 config,
                 [| "fsi.exe"; "--noninteractive"; "--nologo"; "--gui-"; "--quiet" |],
                 new StringReader(""),
                 quiet,
                 quiet,
                 collectible = false
             )

         let library = typeof<Expr>.Assembly.Location
         let core = typeof<list<int>>.Assembly.Location

         let preamble =
             String.concat
                 "\n"
                 [ "#r @\"" + core + "\""
                   "#r @\"" + library + "\""
                   "open Warp11"
                   "open Warp11.Fu"
                   "open Warp11.Units"
                   "open Warp11.Pedal"
                   "open Warp11.Factories" ]

         match fsi.EvalInteractionNonThrowing preamble with
         | Choice1Of2 _, _ -> fsi
         | Choice2Of2 e, diagnostics ->
             let said = diagnostics |> Array.map (fun d -> d.Message) |> String.concat "; "
             failwith $"the compiler could not open the library: {e.Message} {said}")

/// The name the source defines: its first top-level `let`.
let definedName (source: string) : string option =
    let m = System.Text.RegularExpressions.Regex.Match(source, @"^\s*let\s+(?:inline\s+)?([A-Za-z_][A-Za-z0-9_]*)", System.Text.RegularExpressions.RegexOptions.Multiline)
    if m.Success then Some m.Groups[1].Value else None

/// The source compiled and its unit erased: a factory for the session, or
/// the first thing the compiler said. The value the source defines must be
/// a `Fu<'a, 'r>` — a unit over typed pins — and call itself by that name,
/// since a box is placed by the unit's name.
let compileUnit (name: string) (source: string) : Result<Factory, string> =
    try
        let fsi = session.Force()

        match fsi.EvalInteractionNonThrowing source with
        | Choice2Of2 e, diagnostics ->
            let said =
                diagnostics
                |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
                |> Array.map (fun d -> $"line %d{d.StartLine}: {d.Message}")
                |> String.concat "; "

            Error(if said = "" then e.Message else said)
        | Choice1Of2 _, _ ->
            match fsi.EvalExpressionNonThrowing $"Warp11.Fu.erase {name}" with
            | Choice1Of2(Some value), _ ->
                match value.ReflectionValue with
                | :? ErasedFu as unit ->
                    if unit.name <> name then
                        Error $"the source defines '{name}' but the unit calls itself '{unit.name}' — a box is placed by the unit's name, so make them the same"
                    else
                        Ok(written name unit source)
                | other -> Error $"'{name}' is a {other.GetType().Name}, not a unit"
            | Choice1Of2 None, _ -> Error $"'{name}' has no value"
            | Choice2Of2 e, diagnostics ->
                let said = diagnostics |> Array.map (fun d -> d.Message) |> String.concat "; "
                Error $"'{name}' is not a unit over typed pins: {e.Message} {said}"
    with e ->
        Error e.Message

/// What a fresh unit box starts as: the channels swapped, one line each way.
let template =
    String.concat
        "\n"
        [ "let swap ="
          "    fu \"swap\" (pins2 (\"left\", signedInt 24) (\"right\", signedInt 24)) (pins2 (\"left\", signedInt 24) (\"right\", signedInt 24))"
          "        (fun (l, r) -> r, l)" ]
