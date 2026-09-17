# The design canvas and the build tool

*Rough draft, 2026-09-17. Everything here is a day or two old and will move;
`notes/DESIGN_EDITOR.md` and `notes/BUILD.md` are the plans it was built
from, with the findings.*

The canvas is a node editor over a Warp 11 design. You draw boxes and wires,
run the result in the simulator with a recording on its boundary, and build
it for a board — and the design you drew is the same kind of thing a typed
F# file describes: it elaborates to the same Verilog, byte for byte, and
exports as the F# a person would have written.

```sh
export PATH="$HOME/.dotnet:$PATH"
cd hdl
dotnet run --project Warp11.Placement.Canvas -- edit Warp11.Placement.Canvas/examples/gain.json Warp11.Effects/sine.wav
```

That opens the gain example with a recording on its boundary. Press **Open
in sim**, then **Play**, and type `512` into the `volume` number box in the
toolbar to hear the tone at twice its level.

---

## 1. The canvas

### What is on the screen

| region | what |
|---|---|
| **palette** (left) | every unit the library offers, the designs imported into this one, and the control boxes. Click one to add it, or double-click the canvas and type its name |
| **canvas** (middle) | boxes and wires. Drag a box to move it; drag from a pin to a pin to wire them; click to select; pan on empty space; wheel to zoom, Shift+wheel to scroll down the design, Ctrl+wheel across it. Scrollbars appear when a box is out of view and span the design |
| **panel** (right) | the selection's properties: a box's name, copies and arguments; a boundary box's pins; the design itself when nothing is selected — name, sample rate, streams, and the **target** section |
| **menus** (top) | **File**: New, Open…, Import Design…, Save, Save As… — the platform's own pickers. **Edit**: Undo, Redo, Delete. **Build**: Build…, Export to F#… |
| **toolbar** | **Open in sim**, then Play / Run / Step / Reset / Stop; a number box per control; the breakpoint entry; where the view is, and the file it is over |
| **status line** | what just happened, or why something was refused |

Keys: Delete or Backspace removes the selection, Ctrl+Z / Ctrl+Y undo and
redo, Ctrl+D duplicates a box, Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S are
the File menu. The three areas are resizable: drag the divider on either
side of the canvas. Every change is one history entry, and a
change the elaborator would refuse is refused before it lands, naming the
pin.

### Boxes

A **box** is a unit from the palette — `gain`, `eq`, `compressor`, `mixer`,
`tremolo`, `blur`, `add32` and so on — with:

- **signal inlets and outlets** (circles): the beat's fields. Every signal
  inlet must be wired.
- **control inlets** (squares, italic): values the unit holds between beats
  — `volume`, a threshold. Wire one from a control box or from the design's
  own control port, or leave it unwired and type its value in the panel,
  which the elaborator turns into an implicit control port named
  `{box}_{pin}`.
- **creation arguments** (in the panel): an `eq`'s shape, frequency, Q and
  gain; a `blur`'s columns and rows. They configure the unit at elaboration,
  so changing one is a new design and the running session stops.
- **copies**: how many instances of a sequential unit run side by side. A
  design with two streams needs two copies of each sequential box.

The **input** and **output** boxes are the boundary: the fields of a beat in
and out, with their formats (width, fraction bits, signedness), which the
panel lets you add and remove. The input box also carries the design's own
**control ports** — `volume`, `mute` on the gain example — which is how a
control comes from outside: a number box in the simulator, a register on a
board.

**Control boxes** — a number box, a toggle, a constant — sit on the canvas
with a control outlet each. A number box pokes the running design live.

### Designs inside designs

A saved design can be placed in another as a box: **File → Import Design…**
adds it to the palette's *designs* section, and the box's pins are that
design's boundary, its control ports its control inlets. **Double-click** such a box to drill
into it with the values live; the breadcrumb steps back out. The file
carries the sub-designs, so it is complete on its own.

### Writing a unit

*write a unit* in the palette opens an F# source box. Compile adds the unit
to the palette for the session and keeps its source with the design, so the
file opens again on a desktop canvas. Two seconds the first time, since it
starts the F# compiler service. A browser canvas has no compiler and refuses
such a file, naming the unit.

---

## 2. Running it

**Open in sim** elaborates the current design and runs it on the mapping the
file was opened with: a `.wav` for a stereo boundary, a `.pgm` image for a
row boundary, a `.csv` table for a table boundary. Then:

- **Play** runs the design at real time through the speaker; the speaker
  paces the simulator by back-pressure. **Run** is unpaced. **Step** is one
  cycle. **Reset** reopens. **Save heard** writes what the output box heard
  beside the source (`sine.heard.wav`).
- Every signal wire carries a **level meter**; a selected box's panel shows
  its outlets as a **waveform** over the last beats and every signal it
  owns, live.
- **break when** in the toolbar takes a condition over the design's nets in
  the names the panel shows, comparing as bits — `in1_valid && in1_left >
  1000000` stops Run at the next such cycle.
- **streams** in the design panel puts a beat on each stream; a wire draws
  as one strand per stream, and every stream gets the recording.

A structural edit while running stops the session; a number box does not.

---

## 3. The design file

A design saves as JSON — the graph as data: the boundary's pins with their
formats, the boxes by unit name with their arguments and settings, the
control boxes, the wires, the positions, the sub-designs and written units,
and the name of its default mapping (below). It holds no types: the
**palette is the type authority**, resolved by name on every load, so a unit
whose pins have changed refuses an old file at the exact wire.

`hdl/Warp11.Placement.Canvas/examples/` has one per feature, written by
`Warp11.Placement -- examples`, with a README saying what to look at in each.

**Build → Export to F#…** asks where to write the typed form: the units by their F#
names, the beat as a tuple named after the pins, the stages in wire order —
the source a person would have written, which elaborates to the same bytes.
It is one-way: the typed form cannot be read back as a graph.

---

## 4. The target, the mapping, the build

A design goes to a board through a **mapping**: which board, as a record of
independent choices, and which way its boundary reaches the world. The
mapping is a file beside the design (`gain.kv260.json`), so one design can
have several, and the design names its default.

### The target section

With nothing selected, the design panel ends in **target**:

```
 preset      [ kv260 ▾ ]     kv260 · icebreaker · custom · simulator only
 part        xck26-sfvc784-2LV-c (xilinx.com:kv260_som:part0:1.4)
 build tool  Vivado
 clock       PS clock 71   [ 100000000 ] Hz
 the design's rate lands at   48828.125 Hz
 data path   [ memory ▾ ]    pins · memory
 host memory S_AXI_HPC0_FPD, 128 bits, arena [ 8 ] MiB
 host driver AXI-Lite at [ 0xB0000000 ]
 loading     OS app under /lib/firmware/xilinx
 connectors  I2sSeparateCodecs   mclk H12  lrclk E10  sclk D10 …
 mapping file  gain.kv260.json
```

A **preset** fills every row. Any row edited makes it custom, and a
combination the part cannot honour — host memory on a part with no
processing system, a PS clock on an iCE40 — is refused in the status line
by name. The rows:

| row | what it decides |
|---|---|
| part | the toolchain's part arguments, what memory the fabric has, whether there is a processing system |
| build tool | Vivado for Xilinx parts, yosys → nextpnr → icepack for iCE40 |
| clock | where the fabric clock comes from — a PS clock the overlay pins, a crystal through a PLL — and its rate, which every derived rate divides |
| data path | **pins**: the boundary on the I2S pins, a converter on the header. **memory**: the boundary in the host's DDR — rows in a DMA buffer the fabric reads, runs the design over and writes back, no converter needed |
| host memory | which PS slave port the fabric's master lands on, its width, the arena the overlay reserves |
| host driver | how a host reaches the registers: AXI-Lite at a base (a uio device on the OS), a UART at a baud, or none (a fixed-function design with its controls baked at their starting values) |
| loading | an OS app the FPGA manager loads (`xmutil`), or SRAM / SPI flash (`iceprog`) |
| connectors | per device role, which package pin each port lands on. The I2S pinout follows the connector: the Pmod I2S2's separate converters, or a shared bus |

**File → Save** writes the mapping file beside the design and the design's default.
A board edited here can be saved as a `*.board.json` file and picked by
other designs.

### Build

**Build → Build…** asks for a folder and writes everything the board's
toolchain builds from into it; the status line says how to run it. From the
shell:

```sh
dotnet run --project Warp11.Placement -- build my.json out/                 # the design's default mapping
dotnet run --project Warp11.Placement -- build my.json my.kv260.json out/   # a named mapping
dotnet run --project Warp11.Placement -- build my.json kv260 memory out/    # a preset (or a board file) and a path
```

What lands in the directory depends on the build tool:

**Vivado** (the KV260 preset):

| file | what |
|---|---|
| `{Name}.v` | the top: the design's instance between the register slave and the boundary |
| `{name}_layout.rs` | the register map as Rust constants — the seam a driver reads |
| `{name}_bd.tcl` | the block design: the processing system from the board preset, the top as a module reference, the aperture at the base; with host memory, the PS slave port and the master's DDR segment |
| `{name}_pins.xdc` | the constraints from the connector table (pins path only) |
| `build.tcl`, `check_timing.tcl` | the flow, and the gate that refuses a timing-failing bitstream |
| `{name}.dts` | the overlay: firmware name, the uio node, the clock pinned at the rate the design was elaborated for, the DMA arena |
| `{name}.bif`, `shell.json` | the packaging |
| `build.sh` | Vivado, bootgen, dtc → `app/`, then the deploy commands |

**The open flow** (the iCEBreaker preset):

| file | what |
|---|---|
| `{Name}.v` | the top |
| `{name}_top.v` | the wrapper: the PLL from the crystal to the fabric rate, computed as `icepll` computes it, a reset released on lock, the design instance |
| `{name}_top.pcf` | the pin map from the connector table |
| `build.sh` | yosys, a pin gate, nextpnr, a timing gate, icepack; `PROG=1 ./build.sh` writes the flash |

Before any tool runs, the **pin gate** checks that every pin port of the
top has a pin on the board and every pin the board names is a port, refusing
by name.

Then, with Vivado's `settings64.sh` sourced and `dtc` installed:

```sh
cd out && ./build.sh        # ten to twenty minutes for the KV260; a minute for the iCEBreaker
```

`build.sh` prints the `scp`, `install` and `xmutil loadapp` lines for the
board.

### On the board

Two host tools, in `runtime/`, both reading the seam the build wrote:

```sh
# a WAV through the design's host-memory top, and back
warp11_batch in.wav out.wav --board gain_patch_batch --layout gain_patch_batch_layout.rs --set volume=512

# the design's registers by name, over a uio device or a UART
warp11_regs --uio gain_patch_axi --layout gain_patch_axi_layout.rs --set volume=256 --get volume
warp11_regs --serial /dev/ttyUSB1 --layout gain_ice_uart_layout.rs --set volume=256
```

The same batch binary runs against the simulator, through a bridge, before
any bitstream exists — which is how the DDR path was checked to the byte:

```sh
warp11_batch in.wav out.wav --sim my.json --preset kv260 --set volume=512
```

Cross-build for the KV260 with `cargo build --release --target
aarch64-unknown-linux-musl -p warp11-host`.

### The same thing from code

The export with a mapping prints the board as an F# literal, the path, the
design as a board top takes it, and a `build` function — so an exported
`.fsx` builds the same directory the canvas does. The canvas generates the
code; the build follows from the code.

---

## 5. Things to know

- **A drawn design's controls start at zero.** A freshly loaded board is
  silent until `volume` is written; 256 is unity in Q8.8. Power cycles and
  reloads reset it.
- **Each board frames at its own rate.** The KV260 audio apps run at
  48 828.125 Hz, the iCEBreaker at 24 MHz frames at 46 875 Hz. A design is
  made for one rate — the panel says where the design's rate lands on the
  chosen board, and a build for a board whose rate rounds differently is
  refused naming both. Make the design at the board's rate.
- **`iceprog -S` does not work on the iCEBreaker** we have; use the flash
  (`PROG=1`).
- **The host-memory path pads to whole bursts** — 32 frames for a stereo
  boundary — and needs 256-byte-aligned buffers; the driver does both.
- **The differential** (`hdl/run_differential.sh`) verifies the toolchain,
  not a design. Run it before a release or a bitstream build when the
  library has moved.

## What was measured

| | |
|---|---|
| a drawn gain design on the KV260 through DDR, 97 664 frames | 3.173 ms, byte-identical to the simulator |
| the same design's iCEBreaker build | 7 ports pinned, Fmax 40 MHz against 24 |
| the same design heard on the iCEBreaker | microphones to headphone through the shared-bus header |
