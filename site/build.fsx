// A static site for warp11.org, built from the markdown already in the repo.
//
//     dotnet fsi site/build.fsx        # writes site/out
//     python3 -m http.server -d site/out 8080
//
// fsdocs owns the *API reference* at /reference/, generated from the library's
// doc comments and built separately (see `site/README.md`); this script copies
// it in. Everything else — the front page, the tutorial, the guides — is prose
// we author, and a hundred lines we control iterates faster on that than a
// template system would.
//
// The rule that keeps it honest: **pages are read from where they already
// live.** Nothing here is a copy. The tutorial pages the site publishes are
// the same files the debugger's `about` tab renders, so the two cannot drift.

#r "nuget: Markdig, 0.37.0"

open System.IO
open System.Text.RegularExpressions
open Markdig

let root = Path.Combine(__SOURCE_DIRECTORY__, "..")
let out = Path.Combine(__SOURCE_DIRECTORY__, "out")

let pipeline =
    MarkdownPipelineBuilder().UseAdvancedExtensions().UsePipeTables().Build()

/// One page of the site: where it comes from, where it lands, what it is called.
type Page =
    { source: string
      slug: string
      title: string
      section: string }

let page section title slug source =
    { source = Path.Combine(root, source)
      slug = slug
      title = title
      section = section }

// The site's shape. Order within a section is the order of the nav.
let pages =
    [ page "" "Warp 11" "index" "README.md"

      page "Tutorial" "Counter" "tutorial/counter" "hdl/Warp11.Tutorial/doc/counter.md"
      page "Tutorial" "Comparator" "tutorial/comparator" "hdl/Warp11.Tutorial/doc/comparator.md"
      page "Tutorial" "Priority mux" "tutorial/priority-mux" "hdl/Warp11.Tutorial/doc/priorityMux.md"
      page "Tutorial" "Dot product" "tutorial/dot-product" "hdl/Warp11.Tutorial/doc/dotProduct.md"
      page "Tutorial" "Your own modules" "tutorial/your-own-modules" "hdl/Warp11.Tutorial/doc/ownModules.md"
      page "Tutorial" "Bit shapes" "tutorial/bit-shapes" "hdl/Warp11.Tutorial/doc/bitShapes.md"
      page "Tutorial" "Signed operations" "tutorial/signed-operations" "hdl/Warp11.Tutorial/doc/signedOps.md"
      page "Tutorial" "Fixed-point" "tutorial/fixed-point" "hdl/Warp11.Tutorial/doc/fixedPoint.md"
      page "Tutorial" "RAM" "tutorial/ram" "hdl/Warp11.Tutorial/doc/ram.md"
      page "Tutorial" "ROM" "tutorial/rom" "hdl/Warp11.Tutorial/doc/romTable.md"
      page "Tutorial" "Assertions" "tutorial/assertions" "hdl/Warp11.Tutorial/doc/assertions.md"
      page "Tutorial" "Sequencer" "tutorial/sequencer" "hdl/Warp11.Tutorial/doc/sequencer.md"
      page "Tutorial" "Delay chain" "tutorial/delay-chain" "hdl/Warp11.Tutorial/doc/delayAlign.md"
      page "Tutorial" "Edge detect" "tutorial/edge-detect" "hdl/Warp11.Tutorial/doc/edges.md"
      page "Tutorial" "LFSR" "tutorial/lfsr" "hdl/Warp11.Tutorial/doc/noise.md"
      page "Tutorial" "Arbiter (one-hot)" "tutorial/arbiter" "hdl/Warp11.Tutorial/doc/arbiter.md"
      page "Tutorial" "Adder tree" "tutorial/adder-tree" "hdl/Warp11.Tutorial/doc/adderTree.md"
      page "Tutorial" "Wrap counters" "tutorial/wrap-counters" "hdl/Warp11.Tutorial/doc/wrapCounter.md"

      page "Tutorial" "Stream pipe" "tutorial/stream-pipe" "hdl/Warp11.Tutorial/doc/streamPipe.md"
      page "Tutorial" "Stream stages" "tutorial/stream-stages" "hdl/Warp11.Tutorial/doc/streamStages.md"
      page "Tutorial" "Buffering" "tutorial/buffering" "hdl/Warp11.Tutorial/doc/streamBuffer.md"
      page "Tutorial" "Fork and join" "tutorial/fork-and-join" "hdl/Warp11.Tutorial/doc/streamFork.md"
      page "Tutorial" "Farm" "tutorial/farm" "hdl/Warp11.Tutorial/doc/streamFarm.md"
      page "Tutorial" "Carrying context" "tutorial/carrying-context" "hdl/Warp11.Tutorial/doc/streamContext.md"
      page "Tutorial" "Stall probes" "tutorial/stall-probes" "hdl/Warp11.Tutorial/doc/streamProbes.md"
      page "Tutorial" "Pipeline as data" "tutorial/pipeline-as-data" "hdl/Warp11.Tutorial/doc/streamPipeline.md"
      page "Tutorial" "Flow (valid only)" "tutorial/flow" "hdl/Warp11.Tutorial/doc/flowSampler.md"

      page "Tutorial" "Barrel lane" "tutorial/barrel-lane" "hdl/Warp11.Tutorial/doc/barrelLane.md"
      page "Tutorial" "PRNG" "tutorial/prng" "hdl/Warp11.Tutorial/doc/prng.md"
      page "Tutorial" "FIR filter" "tutorial/fir-filter" "hdl/Warp11.Tutorial/doc/firFilter.md"
      page "Tutorial" "Neighborhood" "tutorial/neighborhood" "hdl/Warp11.Tutorial/doc/lifeCell.md"
      page "Tutorial" "Shared unit" "tutorial/shared-unit" "hdl/Warp11.Tutorial/doc/sharedUnit.md"
      page "Tutorial" "Register map" "tutorial/register-map" "hdl/Warp11.Tutorial/doc/registerMap.md"
      page "Tutorial" "DDR master" "tutorial/ddr-master" "hdl/Warp11.Tutorial/doc/ddrMaster.md"

      page "Examples" "Mandelbrot" "examples/mandelbrot" "hdl/Warp11.Mandelbrot/README.md"
      page "Examples" "GEP" "examples/gep" "hdl/Warp11.Gep/README.md"
      page "Examples" "Game of Life" "examples/game-of-life" "hdl/Warp11.GoL/README.md"
      page "Examples" "Audio" "examples/audio" "hdl/Warp11.Effects/README.md"
      page "Examples" "The tutorial project" "examples/tutorial" "hdl/Warp11.Tutorial/README.md"

      page "Guides" "How it fits together" "guides/architecture" "docs/architecture.md"
      page "Guides" "Start your own project" "guides/start-a-project" "docs/start-a-project.md"
      page "Guides" "Drive it from Rust" "guides/drive-it-from-rust" "docs/drive-it-from-rust.md"
      page "Guides" "Runtime and host drivers" "guides/runtime" "runtime/README.md"
      page "Guides" "Streams" "guides/streams" "docs/streams.md"
      page "Guides" "Hardware workflow" "guides/dev-workflow" "docs/dev-workflow.md"
      page "Guides" "Comparison to other HDLs" "guides/comparison" "docs/HDL_COMPARISON.md" ]

/// Sidebar order, which is not the order the page list is written in.
///
/// Guides leads because it holds the getting-started path — the map, a project
/// of your own, then a host program — and a reader who has just arrived wants
/// that before a catalogue of mechanisms. Tutorial is thirty-odd entries, so
/// anything below it is a long way down; on a narrow screen that was the whole
/// of the nav between the reader and the page.
let private sectionOrder = [ "Guides"; "Tutorial"; "Examples" ]

let sections =
    pages
    |> List.filter (fun p -> p.section <> "")
    |> List.groupBy (fun p -> p.section)
    |> List.sortBy (fun (name, _) ->
        match List.tryFindIndex ((=) name) sectionOrder with
        | Some i -> i
        | None -> List.length sectionOrder)

/// How many `../` a page needs to reach the site root.
let upTo (slug: string) =
    String.replicate (slug.Split('/').Length - 1) "../"

/// A slug as it appears in an href — **without** the `.html`.
///
/// Pages are still written to disk as `<slug>.html`; only the links drop the
/// extension, because that is the URL the host serves them at. Cloudflare's
/// default asset handling resolves `/guides/architecture` to
/// `guides/architecture.html` and `/try/` to `try/index.html`, so a link that
/// already says the canonical thing is answered directly. Emitting `.html`
/// instead cost a 307 on every one of this site's ~5,700 internal links, and
/// the setting that suppressed those redirects also turned off directory
/// indexes and 404'd the front page — this is the fix that gets both.
///
/// `site/serve.py` resolves the same shapes for local preview. If one changes,
/// change both.
let href (slug: string) =
    if slug = "index" then "./"
    elif slug.EndsWith "/index" then slug.Substring(0, slug.Length - 5)
    else slug

/// The site root, from a page `up` levels down. `../` on its own is a directory
/// URL, which is exactly what the root is; from the front page itself there is
/// nowhere to go up to, so it names the current directory instead.
let home (up: string) = if up = "" then "./" else up

let nav (current: Page) =
    let up = upTo current.slug

    let link (p: Page) =
        let here = if p.slug = current.slug then " class=\"here\"" else ""
        $"<li><a href=\"{up}{href p.slug}\"%s{here}>{p.title}</a></li>"

    let section (name, items) =
        let links = items |> List.map link |> String.concat "\n"
        $"<div class=\"group\"><h3>{name}</h3><ul>\n{links}\n</ul></div>"

    sections |> List.map section |> String.concat "\n"

// ---- the hero -------------------------------------------------------------
//
// The front page's chrome, and the one place on the site that is authored here
// rather than read from the repo. `hero.js` runs the animation; the markup
// below is what a reader without it — or with reduced motion, or with no
// JavaScript at all — still gets.

let heroHtml =
    """<section class="hero">
  <canvas id="warp" aria-hidden="true"></canvas>
  <div class="hero-inner">
    <p class="eyebrow">warp 11</p>
    <h1>Impossibly Fast Software</h1>
    <p class="lede">Write FPGA-accelerated full-stack applications. The accelerator,
      the cycle-accurate debugger that steps it, and the host program that drives it —
      all from one source.</p>
    <p class="cta">
      <a class="primary" href="try/">▶ Try the debugger</a>
      <a href="tutorial/counter">Read the tutorial</a>
      <a href="https://github.com/warp-11/warp-11">GitHub</a>
    </p>
  </div>
</section>
"""

/// The hero *is* the front page's title, so the body must not print it again.
/// Matched literally: retitle `README.md` and the heading reappears in the body
/// rather than vanishing from the site.
///
/// Only the heading. The README's bold lead — "An F# HDL that runs on real
/// FPGAs." — is the page's one plain statement of what this is, and the hero
/// stopped repeating it when it stopped being the headline, so it stays.
let dedupeHero (html: string) =
    html.Replace("<h1 id=\"warp-11\">Warp 11</h1>\n", "")

let template (current: Page) (body: string) =
    let up = upTo current.slug
    // The hero leads with what warp11 is *for*; the tab and the search result
    // still have to say what it is.
    let title =
        if current.slug = "index" then
            "Warp 11 — an F# HDL for FPGAs"
        else
            $"{current.title} — Warp 11"
    let isFront = current.slug = "index"
    let hero = if isFront then heroHtml else ""
    let script = if isFront then "<script src=\"hero.js\" defer></script>\n" else ""

    $"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title>
<link rel="stylesheet" href="{up}style.css">
{script}</head>
<body>
<header>
  <a class="brand" href="{home up}">warp<span>11</span></a>
  <!-- On every page, not just the front one. A reader arriving at a tutorial
       page from a search engine never sees the front page's status callout, and
       "the API is not stable" is exactly the thing they need before they build
       against it. It links to what that means rather than just asserting it. -->
  <a class="prerelease" href="{home up}#what-is-not-built"
     title="The API is not stable and there is no published package yet">pre-release</a>
  <nav class="top">
    <a href="{up}guides/start-a-project">Guides</a>
    <a href="{up}tutorial/counter">Tutorial</a>
    <a href="{up}examples/mandelbrot">Examples</a>
    <a href="{up}reference/">Reference</a>
    <a href="https://github.com/warp-11/warp-11">GitHub</a>
  </nav>
</header>
{hero}<div class="shell">
  <main>
{body}
  </main>
  <aside>
{nav current}
  </aside>
</div>
<footer>warp11 — an F# HDL that runs on real FPGAs. Pre-release.</footer>
</body>
</html>
"""

// ---- the debugger ---------------------------------------------------------
//
// The front page's strongest claim is "try it without installing anything", so
// the WebAssembly build ships with the site rather than beside it. Published by
// hand rather than from here — a wasm publish is a two-minute step and this
// script should stay something you can run on every edit.

let tryPath = "hdl/Warp11.Tutorial.Browser/bin/Release/net10.0-browser/browser-wasm/AppBundle"

/// The Game of Life demo, same deal: the 64×64 RTL elaborated and simulated in
/// the visitor's browser, published by hand, shipped at `/live/gol/`.
let liveGolPath = "hdl/Warp11.GoL.Browser/bin/Release/net10.0-browser/browser-wasm/AppBundle"

/// Where `dotnet fsdocs build` was told to put the API reference. Generated by
/// hand for the same reason as the wasm bundle: it needs the library built and
/// a tool run, and this script should stay something you run on every edit.
let referencePath = "site/apibuild"

/// Where the built page says it lives. Absolute so the repo's markdown reads
/// correctly on GitHub too, and rewritten below for the local preview.
let canonicalTry = "https://warp11.org/try/"

let copyDebugger () =
    let bundle = Path.Combine(root, tryPath)

    if not (Directory.Exists bundle) then
        printfn "  no debugger bundle — run:"
        printfn "    dotnet publish hdl/Warp11.Tutorial.Browser -c Release"
        false
    else
        let target = Path.Combine(out, "try")

        for source in Directory.GetFiles(bundle, "*", SearchOption.AllDirectories) do
            let relative = source.Substring(bundle.Length + 1)
            let destination = Path.Combine(target, relative)
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            File.Copy(source, destination, true)

        true

let copyLiveGol () =
    let bundle = Path.Combine(root, liveGolPath)

    if not (Directory.Exists bundle) then
        printfn "  no live GoL bundle — run:"
        printfn "    dotnet publish hdl/Warp11.GoL.Browser -c Release"
        false
    else
        let target = Path.Combine(out, "live", "gol")

        for source in Directory.GetFiles(bundle, "*", SearchOption.AllDirectories) do
            let relative = source.Substring(bundle.Length + 1)
            let destination = Path.Combine(target, relative)
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            File.Copy(source, destination, true)

        true

/// The generated API reference, if it has been built. fsdocs emits its pages
/// under `reference/` and its assets under `content/`, and the template it was
/// given points at this site's own stylesheet — so the two halves land side by
/// side and look like one site.
let copyReference () =
    let built = Path.Combine(root, referencePath)

    let copyTree (name: string) =
        let source = Path.Combine(built, name)

        if Directory.Exists source then
            for file in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
                let destination = Path.Combine(out, name, file.Substring(source.Length + 1))
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore

                // fsdocs links its own pages as `/reference/x.html`, and it is
                // not ours to configure. Dropping the extension on the way past
                // keeps the reference on the same URLs as the rest of the site,
                // which is the difference between a click costing a redirect and
                // not — and there are ~1,700 of these links. Only hrefs into
                // `/reference/` are touched; stylesheets and images are left
                // exactly as generated.
                if file.EndsWith ".html" then
                    let html = File.ReadAllText file

                    let rewritten =
                        Regex.Replace(html, "href=\"(/reference/[^\"#?]+)\\.html", "href=\"$1")

                    // Each member's parameter and return TYPES sit inside its
                    // `<details>`, which fsdocs ships closed — so a reference
                    // that shows names and no types is the default. fsdocs'
                    // own answer is an expand/collapse button, which pulls Lit
                    // from a CDN and iconify for its icon; opening the element
                    // here instead costs a regex and works with JS off.
                    File.WriteAllText(destination, rewritten.Replace("<details>", "<details open>"))
                else
                    File.Copy(file, destination, true)

            true
        else
            false

    // fsdocs puts its own bare "Available Namespaces" table at
    // `reference/index.html` and renders `site/api/index.md` — the map of what
    // is in the library — to the output root, where nothing copies it. So the
    // authored page takes the landing slot, and fsdocs' namespace listing stays
    // reachable one click in at `/reference/warp11`.
    let copyAuthoredIndex () =
        let source = Path.Combine(built, "index.html")

        if File.Exists source then
            let destination = Path.Combine(out, "reference", "index.html")

            let rewritten =
                Regex.Replace(File.ReadAllText source, "href=\"(/reference/[^\"#?]+)\\.html", "href=\"$1")

            File.WriteAllText(destination, rewritten)

    if copyTree "reference" then
        copyTree "content" |> ignore
        copyAuthoredIndex ()
        true
    else
        printfn "  no API reference — run:"
        printfn "    dotnet build hdl/Warp11 && dotnet fsdocs build --input site/api \\"
        printfn "      --output site/apibuild --projects hdl/Warp11/Warp11.fsproj --parameters root /"
        false

/// Images live at `docs/images/`, which is where the markdown that uses them
/// can reach them on GitHub too, and are served from `/images/` — a page two
/// directories deep must not be reaching into `docs/`.
let imageSource = Path.Combine(root, "docs", "images")

let copyImages () =
    if not (Directory.Exists imageSource) then
        false
    else
        for file in Directory.GetFiles imageSource do
            let destination = Path.Combine(out, "images", Path.GetFileName file)
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            File.Copy(file, destination, true)

        true

/// Every `.md` link is resolved the way it resolves in the repo — against the
/// directory of the file being rendered — and then sent one of two places:
/// a published page becomes a site link, and anything else becomes a GitHub
/// link. Nothing is left pointing at a `.md` that the site does not serve,
/// which is what the first cut got wrong: a relative link that was correct in
/// the repo shipped as a 404.
let private githubBlob = "https://github.com/warp-11/warp-11/blob/main/"

let private publishedBySource =
    pages
    |> List.map (fun p -> Path.GetFullPath p.source, p.slug)
    |> dict

let rewriteLinks (html: string) (current: Page) =
    let up = upTo current.slug
    let sourceDir = Path.GetDirectoryName current.source

    let html =
        html.Replace($"href=\"{canonicalTry}\"", $"href=\"{up}try/\"")

    // An image is resolved against the same directory its markdown is, then
    // pointed at `/images/`. Anything outside `docs/images/` is left alone
    // rather than guessed at.
    let html =
        Regex.Replace(
            html,
            "src=\"([^\":#?]+\\.(?:png|jpg|jpeg|gif|svg|webp))\"",
            fun m ->
                let full = Path.GetFullPath(Path.Combine(sourceDir, m.Groups[1].Value))

                if full.StartsWith(Path.GetFullPath imageSource) then
                    $"src=\"{up}images/{Path.GetFileName full}\""
                else
                    m.Value
        )

    Regex.Replace(
        html,
        "href=\"([^\":#?]+\\.md)\"",
        fun m ->
            let relative = m.Groups[1].Value
            let full = Path.GetFullPath(Path.Combine(sourceDir, relative))

            match publishedBySource.TryGetValue full with
            | true, slug -> $"href=\"{up}{href slug}\""
            | _ ->
                let repoRelative = Path.GetRelativePath(root, full).Replace('\\', '/')
                $"href=\"{githubBlob}{repoRelative}\"")

// ---- "open this in the debugger" ------------------------------------------
//
// A tutorial page says to poke `enable` and press Step. Read here rather than
// in the debugger that instruction had nowhere to go: the only way in was the
// front page's button, which opens whatever the catalog lists first however far
// into the tutorial the reader has got.
//
// The fragment is the design's `label` in the tutorial's registry, which is
// also the page's title here. That coupling is silent when it breaks — an
// unknown label opens the first design rather than failing — so it is checked
// against the registry rather than trusted.

let private catalogLabels =
    let source = Path.Combine(root, "hdl", "Warp11.Tutorial", "Registry.fs")

    if File.Exists source then
        Regex.Matches(File.ReadAllText source, "entry \"([^\"]+)\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Set.ofSeq
    else
        Set.empty

let private tryButton (p: Page) =
    let target = $"{upTo p.slug}try/#{System.Uri.EscapeDataString p.title}"
    $"""<p class="try-it"><a href="{target}">▶ Open <b>{p.title}</b> in the debugger</a></p>"""

/// Twice: under the title, where a reader arriving from a search engine sees it
/// before anything else, and again under "What to look at", which is the
/// heading every one of these pages starts giving instructions at.
let private lookingAt = "<h2 id=\"what-to-look-at\">What to look at</h2>"

let addTryButtons (p: Page) (html: string) =
    if p.section <> "Tutorial" then
        html
    else
        if not (Set.contains p.title catalogLabels) then
            printfn "  no design labelled %s — its button would open the first one" p.title

        let button = tryButton p
        let titled = Regex("</h1>").Replace(html, $"</h1>\n{button}", 1)
        titled.Replace(lookingAt, $"{lookingAt}\n{button}")

// ---- the live GoL demo -----------------------------------------------------
//
// In the repo, the Game of Life screenshot is a screenshot. On the site, where
// `/live/gol/` can serve the same design elaborated and simulated in the
// visitor's browser, the screenshot becomes the demo's cover: a play button
// over it swaps in an iframe. The swap is a click away rather than automatic
// for the same reason the hero is not Blazor — the front page must not fetch a
// .NET runtime to paint itself. The caption keeps the two claims apart: the
// picture is silicon at 503M generations/s, the button is the simulator.
//
// Matched by filename after `rewriteLinks`, so it fires on every page that
// shows this screenshot — the front page and the GoL example — and nowhere
// else.

let addLiveGolDemo (p: Page) (html: string) =
    let up = upTo p.slug

    Regex.Replace(
        html,
        "<img src=\"([^\"]*gol-500m\\.png)\" alt=\"([^\"]*)\" />",
        fun m ->
            let src = m.Groups[1].Value
            let alt = m.Groups[2].Value

            let boot =
                $"var s=this.parentNode;var f=document.createElement('iframe');f.src='{up}live/gol/';f.className='live-gol-frame';f.title='Game of Life, live in the simulator';s.replaceChild(f,this)"

            $"""<span class="live-gol"><button type="button" class="live-gol-boot" onclick="{boot}"><img src="{src}" alt="{alt}" /><span class="live-gol-play">▶ Run this design live in your browser</span></button><span class="live-gol-caption">The picture is the KV260 doing 503 million generations a second. Play elaborates the same RTL here and runs it in your browser's simulator — much slower, and the same design.</span></span>"""
    )

let render (p: Page) =
    if not (File.Exists p.source) then
        printfn "  MISSING  %s" p.source
        None
    else
        let markdown = File.ReadAllText p.source
        let body = Markdown.ToHtml(markdown, pipeline)
        let body = if p.slug = "index" then dedupeHero body else body
        let body = addTryButtons p body
        let html = template p (addLiveGolDemo p (rewriteLinks body p))
        let target = Path.Combine(out, p.slug + ".html")
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.WriteAllText(target, html)
        Some target

/// Stands in only when the reference has not been generated, so the nav link is
/// never dead. When it has, fsdocs' own `reference/index.html` is the page.
let referencePlaceholder () =
    let p = page "" "Reference" "reference/index" "README.md"

    let body =
        """<h1>API reference</h1>
<p>Not generated in this build. Run <code>dotnet fsdocs build</code> — the
command is in <code>site/README.md</code> — and it will appear here.</p>
<p>Until then, the <a href="/tutorial/counter">tutorial</a> is the place to
start.</p>"""

    Directory.CreateDirectory(Path.Combine(out, "reference")) |> ignore
    File.WriteAllText(Path.Combine(out, "reference", "index.html"), template p body)

if Directory.Exists out then Directory.Delete(out, true)
Directory.CreateDirectory out |> ignore

for asset in [ "style.css"; "hero.js" ] do
    File.Copy(Path.Combine(__SOURCE_DIRECTORY__, asset), Path.Combine(out, asset))

/// `_headers` — the caching rules, emitted rather than committed because
/// `site/out` is deleted and rebuilt on every run.
///
/// **The rule that matters is the WebAssembly one, and getting it wrong is not
/// a slow page, it is a blank one.** `try/_framework/dotnet.boot.js` lists a
/// content hash for every file it loads, and *none of those files are
/// fingerprinted* — they are `Avalonia.Base.wasm`, not
/// `Avalonia.Base.<hash>.wasm`. So a browser holding yesterday's manifest
/// against today's `.wasm` checks each one against the wrong hash, fails every
/// integrity check, and renders nothing, with a console full of errors and
/// nothing wrong on disk. The whole directory has to revalidate together.
///
/// Nothing else on the site is content-addressed either — `style.css`,
/// `hero.js` and the images keep their names across builds — so the honest
/// answer for all of it is revalidate-always. That is cheaper than it sounds: an
/// unchanged file costs a conditional request and a 304, not its bytes. The
/// alternative is fingerprinting the output, which is a change to how the
/// bundle is published rather than a header.
///
/// `site/serve.py` carries the same rule for local preview, and says why at
/// more length. If one of them changes, change both.
let writeHeaders () =
    let text =
        [ "# Generated by site/build.fsx. Edit that, not this."
          ""
          "# One rule, because Cloudflare applies *every* matching rule and"
          "# concatenates the values — a second, more specific entry for"
          "# /try/_framework/* produced a literal `no-cache, no-cache` rather"
          "# than overriding anything. Measured against a real deployment."
          "#"
          "# Nothing on this site is content-addressed — not style.css, not"
          "# hero.js, and not the 70 files under /try/_framework/, which are"
          "# Avalonia.Base.wasm rather than Avalonia.Base.<hash>.wasm. So"
          "# everything revalidates, and an unchanged file costs a conditional"
          "# request and a 304 rather than its bytes."
          "#"
          "# If this is ever relaxed to cache anything for real, /try/_framework/"
          "# is the directory that must NOT be: dotnet.boot.js lists a content"
          "# hash for every file it loads, so a manifest one build out of step"
          "# with its .wasm fails every integrity check at once and renders a"
          "# blank page."
          "/*"
          "  Cache-Control: no-cache"
          "" ]

    File.WriteAllText(Path.Combine(out, "_headers"), String.concat "\n" text)

let written = pages |> List.choose render
let images = copyImages ()
let debugger = copyDebugger ()
let liveGol = copyLiveGol ()
let reference = copyReference ()
if not reference then referencePlaceholder ()
writeHeaders ()

printfn
    "%d pages%s%s%s%s -> %s"
    (List.length written + 1)
    (if images then " + images" else "")
    (if debugger then " + the debugger" else "")
    (if liveGol then " + the live GoL" else "")
    (if reference then " + the API reference" else "")
    out
