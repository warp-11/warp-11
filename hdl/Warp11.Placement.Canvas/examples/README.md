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

Open one with a recording playing into it (the recordings beside
`Warp11.Effects` are at 48 828 Hz, which is the rate the examples are made
for):

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

With a design running, a box's panel shows its outlets as a waveform over
the last beats, every signal wire carries a level bar, and the toolbar's
**break when** takes a condition over the design's nets (the names the
panel shows, comparing as bits — `in1_valid && in1_left > 1000000` stops
Run at the next such cycle). The design panel's **streams** count puts a
beat on each stream; a sequential box then needs a copy per stream.

**Export F#** writes the typed source beside the file; `dotnet run --project
Warp11.Placement -- export <file>` prints it, and `-- emit <file>` the Verilog.
