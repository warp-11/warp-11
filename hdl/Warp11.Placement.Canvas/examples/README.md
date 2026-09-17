# Example designs

Design files for the canvas, one per milestone of `notes/DESIGN_EDITOR.md`.
They are **written, not hand-edited**: `Warp11.Placement/Examples.fs` builds
each one by the same edits a canvas makes, and the checks prove the same
graphs, so an example is exactly a design the checks hold. Regenerate them
after a change to the model with

```sh
cd hdl
dotnet run --project Warp11.Placement -- examples Warp11.Placement.Canvas/examples
```

Open one with a source on its boundary — a recording, a `.pgm` image or a
`.csv` table; the recordings beside `Warp11.Effects` are at 48 828 Hz, which
is the rate the audio examples are made for:

```sh
dotnet run --project Warp11.Placement.Canvas -- edit Warp11.Placement.Canvas/examples/twice.json Warp11.Effects/sine.wav
```

then **Open in sim**, then **Play** (or **Run**, which is not paced).

| file | what to look at |
|---|---|
| `gain.json` | the first design: a stereo stream through `gain`, `volume` and `mute` the design's own controls — number boxes in the toolbar poke them live. They start at 0, since the design's own controls come from outside: type 256 into `volume` (unity, Q8.8) to hear it |
| `three-band-eq.json` | three `eq` boxes with creation arguments; select one and change `fc` or `gain` in the panel — Enter or clicking away commits it (a change is a new design — Open in sim again) |
| `controls.json` | `volume` is an unwired inlet holding a setting (`volume = 256` on the box, an entry in the panel that pokes live); a toggle box on `mute` |
| `twice.json` | the three-band EQ imported and placed twice in series: **double-click** a `ThreeBandEq` box to drill into it with the values live, the breadcrumb steps back out; the palette's *designs* section places it again |
| `blur.json` | an image design: rows in, a 3×3 blur, rows out. Open it with `gradient.pgm`, Run, **Save heard** writes `gradient.heard.pgm` |
| `adder.json` | a table design: columns `x` and `y` in, `sum` out. Open it with `numbers.csv`, Run, Save heard writes `numbers.heard.csv` |
| `mandelbrot.json` | the Mandelbrot frame: a count in, `coords` minting each chunk's view, `mandel16` × 4 (the lane pod at one beat wide, farmed by *copies*), sixteen pixels a beat out. Open it with `frame:1400x800`, type the view into the control boxes as Q4.28 bits — −2.0 … 1.0 across and 1.0 … −1.0 down is `cxOrigin` 3758096384, `cyOrigin` 268435456, `dx` 575242, `dy` 4294296207 — Run, **Save heard** writes the PGM. Its mapping is the KV260's **counted** path: the host writes the count and the view, reads the frame from DDR |

With a design running, a box's panel shows its outlets as a waveform over
the last beats, every signal wire carries a level bar, and the toolbar's
**break when** takes a condition over the design's nets (the names the
panel shows, comparing as bits — `in1_valid && in1_left > 1000000` stops
Run at the next such cycle). The design panel's **streams** count puts a
beat on each stream; a sequential box then needs a copy per stream.

**Write a unit** in the palette column opens a box for F# defining one value,
a unit over typed pins; Compile adds it to the palette for the session and
keeps its source with the design, so the file opens again on a desktop
canvas (and refuses in a browser, which has no compiler).

**Build → Export to F#…** writes the typed source where you say; `dotnet run --project
Warp11.Placement -- export <file>` prints it, and `-- emit <file>` the Verilog.

**Target** (in the design panel, nothing selected) is where the design
goes: a preset (`kv260`, `icebreaker`) fills every row — part, build tool,
clock and fabric rate, data path (the boundary on the I2S pins, or in the
host's memory: a recording in, the design, a recording out, no codec
needed), host driver, loading, connectors — and any row edited makes it
custom. Save writes it beside the design as `gain.kv260.json`, and the
design names it; `gain.json` ships with that one. **Build → Build…** asks
for a folder and writes everything the board's toolchain builds from into
it, and says how to run it; `dotnet run --project Warp11.Placement --
build <file> <dir>` does the same from the shell. The iCEBreaker frames at
46 875 Hz, so a design goes there made for that rate (the refusal says so).
`notes/BUILD.md` is the plan.
