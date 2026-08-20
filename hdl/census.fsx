/// The duplication census: what has been written more than once, ranked.
///
///     dotnet fsi census.fsx            # every project
///     dotnet fsi census.fsx 3          # only what appears 3+ times
///
/// Why this exists. The rule is "lift it to the stdlib when a second real user
/// appears" — but that trigger fires while you are deep inside the second user,
/// where stopping to refactor is a context switch away from the thing you came
/// to do, and nothing remembers afterwards. `delayChain` reached FOUR copies
/// that way, each written by someone who knew about the others. The rule was
/// never the problem; nothing was watching it.
///
/// So this reports and ranks, and never fails. Some duplication is correct and
/// deliberate — `windowedPixelStage` is copied between `blur` and `sobelX` on
/// purpose, waiting for a second real user to decide its shape — and a gate
/// would push toward exactly the pre-abstraction the original rule guards
/// against. The judgment stays yours; only the noticing is automated.
///
/// What it will not catch: copies that were retyped rather than pasted. Of
/// `delayChain`'s four, two looped and two folded — the same six lines, below
/// this thing's resolution. So a clean report is not a clean codebase; it means
/// nobody pasted. Treat a hit as somewhere to look, and keep reading around it.

open System
open System.IO
open System.Text.RegularExpressions

let minimumOccurrences =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.tryHead with
    | Some n -> int n
    | None -> 2

/// Lines long enough to mean something. A run of `let x = wire "n" 8` shares a
/// shape with every other declaration in the codebase and says nothing.
let windowLines = 5
let minimumWidth = 24

let root = __SOURCE_DIRECTORY__

let sources =
    Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun p ->
        let parts = p.Split(Path.DirectorySeparatorChar)
        not (Array.contains "obj" parts || Array.contains "bin" parts))
    |> Seq.sort
    |> Seq.toList

/// Literals vary between copies where nothing else does — a width, a name, a
/// reset value — so they are the one thing normalized away. Identifiers are
/// kept: normalizing those as well finds every port declaration in the project
/// and buries the real hits.
let normalize (line: string) =
    let noComment = Regex.Replace(line, @"//.*$", "")
    let noStrings = Regex.Replace(noComment, "\"([^\"\\\\]|\\\\.)*\"", "S")
    let noNumbers = Regex.Replace(noStrings, @"\b\d+(UL|L|u)?\b", "N")
    Regex.Replace(noNumbers, @"\s+", " ").Trim()

type Line =
    { file: string
      number: int
      /// Position among the file's KEPT lines. Window n and window n+1 differ by
      /// one of these, not by one file line — blank and short lines are gone —
      /// so this, not `number`, is what says two windows overlap.
      ordinal: int
      text: string }

let lines =
    [ for file in sources do
          let relative = Path.GetRelativePath(root, file)

          yield!
              File.ReadAllLines file
              |> Array.mapi (fun i raw ->
                  { file = relative
                    number = i + 1
                    ordinal = 0
                    text = normalize raw })
              |> Array.filter (fun l -> l.text.Length >= minimumWidth)
              |> Array.mapi (fun ordinal l -> { l with ordinal = ordinal }) ]

// A window is `windowLines` consecutive kept lines from one file. Blank and
// comment lines are already gone, so a copy survives being re-commented.
let windows =
    lines
    |> List.windowed windowLines
    |> List.filter (fun w -> w |> List.forall (fun l -> l.file = (List.head w).file))

let candidates =
    windows
    |> List.groupBy (fun w -> w |> List.map (fun l -> l.text) |> String.concat "\n")
    |> List.map (fun (body, hits) ->
        body,
        hits |> List.map (fun w -> (List.head w).file, (List.head w).ordinal, (List.head w).number))
    |> List.filter (fun (_, hits) -> List.length hits >= minimumOccurrences)

// A copied 20-line block produces 16 overlapping windows, each its own group.
// Keep only the group that *starts* the run: the one whose hits are not all
// shifted one line down from another reported group's. The set has to be global
// — a window and its successor are different bodies, so neither can see the
// other from inside its own hits, which is how the first cut reported the same
// block four times.
let reported =
    candidates
    |> List.collect snd
    |> List.map (fun (f, ordinal, _) -> f, ordinal)
    |> Set.ofList

let groups =
    candidates
    |> List.filter (fun (_, hits) ->
        hits
        |> List.exists (fun (f, ordinal, _) -> not (Set.contains (f, ordinal - 1) reported)))
    |> List.sortByDescending (fun (body, hits) ->
        List.length hits, (body: string).Length)

printfn "duplication census — %d files, windows of %d lines, %d+ occurrences\n" (List.length sources) windowLines minimumOccurrences

if List.isEmpty groups then
    printfn "nothing repeated at this window size."
else
    for body, hits in groups do
        let where =
            hits
            |> List.map (fun (f, _, n) -> sprintf "%s:%d" f n)
            |> String.concat "  "

        printfn "%d copies  %s" (List.length hits) where

        for line in (body: string).Split '\n' do
            printfn "    %s" line

        printfn ""

    printfn "%d repeated blocks. Lift what has a shared meaning; leave what is" (List.length groups)
    printfn "coincidence or a deliberate deferral, and say which in BACKLOG.md."
