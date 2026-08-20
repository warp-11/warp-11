# site

A first cut at warp11.org, built from the markdown already in the repo.

```sh
# once, and again whenever the library or the debugger changes
dotnet publish hdl/Warp11.Tutorial.Browser -c Release          # the wasm debugger, ~40 s
dotnet publish hdl/Warp11.GoL.Browser -c Release               # the live GoL demo, ~40 s
dotnet build hdl/Warp11                                        # Debug: fsdocs reads that XML
dotnet fsdocs build --input site/api --output site/apibuild \
  --projects hdl/Warp11/Warp11.fsproj --parameters root /      # the API reference

# every edit
dotnet fsi site/build.fsx
python3 site/serve.py 8080
```

Then open <http://127.0.0.1:8080>. `build.fsx` copies the debugger to `/try/`,
the live Game of Life demo to `/live/gol/` and the reference to `/reference/`,
and says which is missing rather than failing.

## What it is

`build.fsx` is about a hundred lines: a page list, a template, and Markdig.
Output goes to `site/out`, which is gitignored — the site is built, never
committed.

**Pages are read from where they already live.** Nothing here is a copy. The
tutorial pages the site publishes are the same files the debugger's `about` tab
renders, and the example write-ups are the projects' own READMEs, so the site
cannot drift from the repo. Adding a page means adding one line to the `pages`
list in `build.fsx`.

## The hero

The front page opens on a warp jump: a starfield accelerates until the stars
are streaks, and the streaks resolve into lanes of a datapath, flowing left to
right on one clock. Warp 10 is the asymptote — every point in the universe at
once — so what lies past it is not a longer streak, it is everything advancing
at the same time, which is also the argument the front page is making.

`site/hero.js` is that animation: ~230 lines of canvas 2D, no dependencies, one
`requestAnimationFrame` loop that stops via `IntersectionObserver` when the hero
scrolls away. `prefers-reduced-motion` skips the journey and draws the
destination as a single static frame. Every word in the hero is also in the page
below it, and the canvas is `aria-hidden`, so a reader who never sees a pixel of
it loses nothing.

**It is deliberately not Blazor.** The site already ships a .NET runtime at
`/try/`, where it earns its download by being the product; a second one on the
front page would delay first paint to do what a few KB of canvas does at 60 fps
without crossing an interop boundary every frame.

**The hero is the one thing on this site authored here** rather than read from
the repo, which is why `build.fsx` also strips the README's `<h1>` — the hero is
that heading. Matched literally: retitle `README.md` and the heading reappears
in the body rather than vanishing from the site.

The bold lead under it — *"An F# HDL that runs on real FPGAs."* — is **not**
stripped, and that is deliberate. The hero sells the outcome and never says F#;
the sentence immediately below it is where the front page says plainly what this
is, twelve lines above the first F# it shows you.

## fsdocs, for the reference only

fsdocs generates the **API reference** at `/reference/` from the `///` comments
in `Warp11` — around a hundred pages, and not a word of it written twice. The guides and the
tutorial stay with `build.fsx`, because their content is prose we author and a
hundred lines we control iterates faster than a template system.

Three things make the two halves look like one site:

- `site/api/_template.html` is fsdocs' page template, rewritten to emit this
  site's header, nav and footer, and to load `style.css` rather than fsdocs'
  own stylesheet. The `main.api` rules at the end of `style.css` are the only
  fsdocs-specific styling.
- `--parameters root /` so generated links are absolute; the two trees are then
  copied side by side and cross-link normally.
- `<UsesMarkdownComments>` on `Warp11.fsproj`, because the doc comments are
  written in Markdown and without it the backticks print as backticks.

**fsdocs reads the Debug build's XML**, not Release, and says so in an
exception if you have only built Release.

## The debugger at `/try/`

The front page's strongest claim is "try it without installing anything", so the
WebAssembly build ships with the site. The markdown links to the canonical
`https://warp11.org/try/` so the README reads correctly on GitHub too, and
`build.fsx` rewrites that one URL for the local preview.

### Every tutorial page opens its own design

A tutorial page tells you to poke `enable` and press **Step**. Read on the site
rather than in the debugger, that instruction used to have nowhere to go: the
only way in was the front page's button, which opens whatever the catalog lists
first however far into the tutorial the reader had got. So `build.fsx` puts an
**Open *X* in the debugger** button on each of the 34 tutorial pages — once under
the title, where someone arriving from a search engine sees it, and again under
*What to look at*, which is where the instructions begin.

The link is `/try/#{design}`, and the fragment is the design's `label` in
`Warp11.Tutorial.Registry` — which is also the page's title in `build.fsx`'s
`pages` list. Two consequences worth knowing:

- **The browser head reads the fragment**, in `Warp11.Tutorial.Browser`'s
  `requestedDesign`, and hands it to `Debugger.window` as the design to open.
  That parameter already existed for the desktop head, which takes the same
  thing as `argv[0]`.
- **A label that does not match is silent** — `View.debugger` falls back to the
  first entry rather than failing, which is right for a reader typing a URL and
  wrong for a rename nobody noticed. So `build.fsx` reads the labels out of
  `Registry.fs` and prints which page has no design, rather than trusting that
  the two lists still agree.

### Caching will break this bundle, and the symptom lies

`dotnet.boot.js` carries a sha256 for every `.wasm` it loads. Republish, and a
browser still holding the *previous* `dotnet.boot.js` checks the new files
against the old hashes, fails every one, and refuses to load them. What you get
is a blank page and a console full of integrity errors — with the deployed files
perfectly consistent on disk. It reproduces in a normal window and not in a
private one, which is the tell.

`python3 -m http.server` walks straight into it: no `Cache-Control`, no `ETag`,
only `Last-Modified`, so browsers apply heuristic freshness and reuse the
manifest without asking. `site/serve.py` sends `no-store` instead, which is why
the preview command above is not the one-liner.

**Whatever serves warp11.org has the same obligation**, and `build.fsx` now
discharges it: it emits a `_headers` file into `site/out` putting
`Cache-Control: no-cache` on `/try/_framework/*` and, since nothing on this site
is content-addressed, on everything else too. Revalidation is cheap — an
unchanged file costs a conditional request and a 304, not its bytes.

`_headers` is the format Cloudflare Pages and Netlify both read. It is
*generated* rather than committed because `site/out` is deleted and rebuilt on
every run, so a hand-placed file would vanish on the next build. If the site
ever moves somewhere that wants a different format, that function is the one
place to change.

The lasting fix is fingerprinting: if the bundle published its files as
`Avalonia.Base.<hash>.wasm`, they could be cached forever and the manifest alone
would need to revalidate. That is a change to how the bundle is published, not a
header, which is why it is not done here.

**Published without AOT, for stability first and payload second.**

Non-AOT is not "the slow one" — it is the *simpler* one. F# compiles to ordinary
IL, `dotnet.native.wasm` is the Mono runtime compiled to WebAssembly, and Mono
interprets that IL in the browser. Since .NET 8 a jiterpreter compiles hot
interpreter traces to wasm at run time, so it is not purely interpreted either;
that is the difference between the 66k cycles/s the original spike measured and
the 116k the deployed bundle does.

AOT does not replace any of that — it **adds** to it. `mono-aot-cross` and LLVM
precompile IL to wasm ahead of time and embed it (8.3 MB → 29.9 MB), but the
interpreter still ships, because AOT cannot cover everything; the build line
ends in `llvmonly,interp` and uncovered methods fall back at run time. So AOT is
everything non-AOT has, plus precompiled code, plus a compilation stage that can
fail on its own — as it did here, on a stale assembly left in `obj/`, with an
unresolved typeref that named nothing useful.

Its known rough edges also cluster where this codebase lives: generic
instantiation, closure- and delegate-heavy code, `System.Linq.Expressions`. The
Sim compiles each assign into a thunk over a flat array, which is precisely that
neighbourhood. The usual failure is a silent fallback to interpretation — slower,
not wrong — but it is more machinery between the code and the browser for a
throughput win nobody on a documentation page is waiting for.

It happens to cost half the download too: 6 MB gzip against 12.

`-p:RunAOTCompilation=true` if that trade ever changes. Note that neither mode
changes the two things most likely to actually bite — the single-threaded
runtime and the Avalonia browser backend are the same either way.

## Deploying

The build is not one a hosting platform can run for you — it needs the .NET SDK,
the `wasm-tools` workload and `fsdocs`, none of which are in a stock build image.
So build locally and upload the result. Git-push-to-deploy is not the workflow,
and that is fine: the site is generated from six projects and a reference tree,
and you already rebuild it by hand.

```sh
dotnet fsi site/build.fsx
cd site && npx wrangler deploy
```

`wrangler` finds `wrangler.jsonc` in the working directory, which is why the
second command runs from `site/` rather than passing `--config`.

### Getting wrangler

**A project-local dev dependency, not a global install.** Wrangler's behaviour
changes between versions, and a global one drifts silently until a deploy
behaves differently for no visible reason. `site/package.json` pins it, and
`package-lock.json` is committed for the same reason — only `site/node_modules/`
is ignored.

```sh
cd site && npm install
```

It needs **Node 20 or newer**, which Ubuntu does not package: `apt`'s `nodejs`
on 24.04 is 18.19, past end of life and under the minimum. Install it the way
the rest of this repo's toolchain is installed — user-local, no root — with
[nvm](https://github.com/nvm-sh/nvm) (`nvm install --lts`) or, if you would
rather not pipe an installer into a shell, `cargo install fnm` using the Rust
toolchain that is already here.

### Settings worth knowing

`site/wrangler.jsonc` keeps the deploy configuration in the repo rather than in
a dashboard. Two of its settings were found by deploying and then measuring the
result, not by reading documentation:

- **`html_handling` stays at the default**, having tried the alternative.
  `"none"` does remove the 307 that the default issues for every one of this
  site's ~5,700 explicit `.html` links — and it also removes directory-index
  handling, which measured as `/`, `/try/` and `/reference/` returning **404**
  on a real deployment. A front page that 404s beats a redirect that works. The
  fix that gets both is to stop emitting `.html` in hrefs so links are already
  canonical, which is a change to `build.fsx` and `serve.py` rather than to the
  deploy config.
- **One `_headers` rule, not two.** Cloudflare applies *every* matching rule and
  concatenates, so a specific `/try/_framework/*` entry beside `/*` produced a
  literal `Cache-Control: no-cache, no-cache` rather than overriding anything.

### Keeping it private before launch

The site deploys as a **Worker with static assets** — a `*.workers.dev`
hostname, not Cloudflare Pages, so the Pages-specific "protect preview
deployments" toggle does not apply. Put a Zero Trust **Access application** over
the hostname instead: self-hosted, policy allowing your own email, which puts
the site behind a login rather than merely leaving it unlisted. A fresh
deployment is public the moment it exists, so this is worth doing before the URL
is shared anywhere.

Leave the custom domain unattached until launch. When it is attached, the Access
application has to cover the new hostname too — one more policy, and much better
discovered now than on launch day.

## Known rough edges

- No search, no syntax highlighting, no dark/light toggle — the site follows
  the reader's system preference.
