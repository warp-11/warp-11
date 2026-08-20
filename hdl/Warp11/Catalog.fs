/// What a debugger can be opened *on*: a named list of designs, plus the prose
/// and the source behind each.
///
/// A catalog rather than a module reference because there is more than one, and
/// they belong to different people. The tutorial's catalog is a teaching set;
/// `Warp11.Designs` is a differential-oracle inventory; GEP's would be its own
/// machines. A debugger hardwired to any one of them can never show the other
/// two, which is exactly the shape the view had before this existed.
module Warp11.Catalog

type Entry =
    { /// The only name the UI shows.
      label: string
      /// A thunk rather than the value, so a parameterized entry elaborates
      /// only when it is picked.
      build: unit -> ModuleDef
      /// The binding this entry *is*, supplied at the definition site as
      /// `nameof` so the compiler checks it. It is the key to both the prose
      /// and the source: one name rather than two that can drift apart.
      binding: string
      /// Inputs this entry's page assumes are set, poked when it opens — so
      /// the first thing a newcomer tries *does something*. The Counter's page
      /// is written around stepping and watching it count; a fresh session
      /// with `enable` at 0 makes the first Step look broken instead.
      pokes: (string * uint64) list
      /// Signals this entry's page talks about, watched when it opens.
      ///
      /// A debugger opening a design watches its ports, which is the interface
      /// and all it can know. What it cannot know is that the Counter page's
      /// first instruction is *watch `r` and `count`* — `r` being a register,
      /// one of an unbounded number a design might have. The catalog knows,
      /// because the catalog is the thing that owns the prose.
      watch: string list }

type Catalog =
    { /// What the picker lists, in the order it lists them.
      entries: Entry list
      /// The page for a binding, if one has been written.
      doc: string -> string option
      /// The source text that defines a binding, if it can be found.
      source: string -> string option }

let entry label binding build =
    { label = label
      binding = binding
      build = build
      pokes = []
      watch = [] }

/// The signals this entry's page discusses, on screen when it opens.
///
/// Named rather than defaulted, so an entry that wants them says so and every
/// other entry stays one line. A name the design does not have is ignored by
/// the session that receives it — which is what the tutorial's checks are for.
let watching signals entry = { entry with watch = signals }

/// Inputs the entry's page assumes, applied when the session opens. A name the
/// design does not have is ignored by the session, like a watch of one — and
/// the tutorial's checks hold both to a higher bar: the name must exist and
/// must be an input.
let poking pokes entry = { entry with pokes = pokes }

// ---- reading prose and source out of an assembly ------------------------
//
// Out of the assembly rather than off disk, because a published wasm bundle has
// no disk to read. The catalog's owner passes its own assembly, so each one
// carries its own pages and its own source.

let private resource (assembly: System.Reflection.Assembly) name =
    match assembly.GetManifestResourceStream(name: string) with
    | null -> None
    | stream ->
        use reader = new System.IO.StreamReader(stream)
        Some(reader.ReadToEnd())

/// The name a `let` line binds, if it binds one: the first identifier after
/// `let`, past `private` and `rec`.
let private bound (line: string) =
    if not (line.StartsWith "let ") then
        None
    else
        line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
        |> List.skipWhile (fun word -> word = "let" || word = "private" || word = "rec")
        |> List.tryHead
        |> Option.map (fun word -> word.TrimEnd(':', '='))

/// The text that defines one design — from its doc comment through to the line
/// before whatever is declared next.
///
/// Sliced out of the file rather than marked up inside it: a region marker is
/// one more thing to keep in step with the code it wraps, and the shape the
/// slicer looks for — a binding at column zero, ending where the next one
/// starts — is one F# already enforces.
let sliceFrom (text: string) (binding: string) =
    let lines = text.Replace("\r\n", "\n").Split '\n'

    lines
    |> Array.tryFindIndex (fun line -> bound line = Some binding)
    |> Option.map (fun start ->
        // The doc comment above the binding is part of what it says.
        let first =
            let rec back i =
                if i > 0 && lines.[i - 1].StartsWith "///" then back (i - 1) else i

            back start

        // The next declaration at column zero ends this one.
        let last =
            let rec forward i =
                if i >= lines.Length then lines.Length - 1
                elif lines.[i].Length > 0 && not (System.Char.IsWhiteSpace lines.[i].[0]) then i - 1
                else forward (i + 1)

            forward (start + 1)

        lines.[first..last] |> String.concat "\n" |> fun s -> s.TrimEnd())

/// Answer once and remember. A panel showing prose asks on every render, and
/// re-reading a manifest stream — or re-slicing a whole source file — thirty
/// times a second to produce the same string is work nobody sees.
let private remember f =
    let answers = System.Collections.Generic.Dictionary<string, string option>()

    fun key ->
        match answers.TryGetValue key with
        | true, answer -> answer
        | _ ->
            let answer = f key
            answers[key] <- answer
            answer

/// A catalog whose pages live at `doc/{binding}.md` and whose designs live in
/// one embedded source file — the arrangement every catalog here uses.
let embedded (assembly: System.Reflection.Assembly) (sourceFile: string) (entries: Entry list) =
    let text = lazy (resource assembly sourceFile)

    { entries = entries
      doc = remember (fun binding -> resource assembly $"doc/{binding}.md")
      source = remember (fun binding -> text.Value |> Option.bind (fun t -> sliceFrom t binding)) }
