# The design canvas and the build tool

> **Experimental.** The canvas works as described here, but it needs a lot
> more work before it is useful for real designs. The typed F# path is the
> one to build on; the canvas is a preview of a direction, not a tool to
> depend on yet.

*Rough draft, 2026-09-17; the model in §1–§3 rewritten 2026-11-15 for boxes with
ports (`notes/CANVAS_MODEL.md`). Everything here will move;
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

A drawing is boxes with **ports** and **edges** between ports. An edge is one
stream, or one control value, and nothing else: whatever code does between
two stages is a box. So a drawn chain is the typed pipeline, call for call —

```fsharp
streamSource inPorts
|> stage [] lowBand "low"
|> connectAs Connection.Forward
|> stage [] midBand "mid"
|> streamSink outPorts
```

— each box its unit applied once, each edge `connectAs` what the edge says
it registers.

### What is on the screen

| region | what |
|---|---|
| **palette** (left) | every unit the library offers, the boxes that sit between stages (`join`, `fields`, `fifo`), the designs imported into this one, and the control boxes. Click one to add it, or double-click the canvas and type its name |
| **canvas** (middle) | boxes and edges. Drag a box to move it; drag from a port to a port to connect them; click to select; pan on empty space; wheel to zoom, Shift+wheel to scroll down the design, Ctrl+wheel across it. Scrollbars appear when a box is out of view and span the design |
| **panel** (right) | the selection's properties: a box's name, copies and arguments; an edge's connection and, where several edges meet, the port's pattern; a boundary box's streams, fields and controls; the design itself when nothing is selected — name, sample rate, and the **target** section |
| **menus** (top) | **File**: New, Open…, Import Design…, Save, Save As… — the platform's own pickers. **Edit**: Undo, Redo, Delete. **Build**: Build…, Export to F#… |
| **toolbar** | **Open in sim**, then Play / Run / Step / Reset / Stop; a number box per control; the breakpoint entry; where the view is, and the file it is over |
| **status line** | what just happened, or why something was refused |

Keys: Delete or Backspace removes the selection, Ctrl+Z / Ctrl+Y undo and
redo, Ctrl+D duplicates a box, Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S are
the File menu. The three areas are resizable: drag the divider on either
side of the canvas. Every change is one history entry, and a change the
elaborator would refuse is refused before it lands, naming the port.

### Ports

| port | drawn | carries |
|---|---|---|
| **stream** in or out | a circle, `in` / `out` | one stream: its fields together, with their formats (width, fraction bits, signedness) |
| **control** in or out | a square, italic | one value held between beats — `volume`, a threshold |
| **socket** | on a box's right, `multiply` | what the box's unit needs done for it and does not do itself: the gain's two channel multiplies, an `eq`'s ten products |
| **service** | on a unit's left, `service` | the unit itself, offered to sockets |

### Boxes

A **box** is a unit from the palette — `gain`, `eq`, `compressor`, `mixer`,
`tremolo`, `blur`, `add32` and so on — with one stream in, one stream out,
and:

- **control inlets**: wire one from a control box or from the design's own
  control port. Left unwired, it is the value typed for it in the panel,
  **fixed when the design is built**, as an argument written in code is. A
  value the host should be able to change is a number box.
- **creation arguments** (in the panel): an `eq`'s shape, frequency, Q and
  gain; a `blur`'s columns and rows. They configure the unit at elaboration,
  so changing one is a new design and the running session stops.
- **copies**: how many of the unit there are. Above one the box is a
  **farm**: a beat goes to whichever copy is free, and with **in order**
  ticked the beats leave in the order they came. A box that is a design
  takes copies the same way, which is how a chain becomes several lanes.
- **answers**: a unit may emit several beats for one it takes — `mandelChunk`
  drains a 128-pixel chunk as eight beats of sixteen. Nothing about it shows
  on the canvas but the count of beats out.

The **input** and **output** boxes are the boundary: the design's streams in
and out, each with a name (a new design starts with `in` and `out`) and its
fields, which the panel lets you add and remove. The input box also carries
the design's own **control ports** — `volume`, `mute` on the gain example —
which is how a control comes from outside: a number box in the simulator, a
register on a board.

**Control boxes** — a number box, a toggle, a constant — sit on the canvas
with a control outlet each. A number box pokes the running design live.

Three boxes are what code does between stages rather than units. They have
no fields of their own; each carries whatever is connected to it:

| box | does | in code |
|---|---|---|
| **join** | several one-field streams into one beat; a beat leaves when every side has one | `streamJoin` |
| **fields** | picks, renames and regroups a stream's fields (`sum = product, c`); every bit out went in unchanged | `streamMapTo` |
| **fifo** | a buffer of `depth` beats | `streamFifo` |

### Edges

Select an edge and the panel sets what its **connection** registers:

| connection | registers | costs |
|---|---|---|
| `Wire` | nothing | no cycle |
| `Forward` | data and valid | one cycle; breaks a long forward path, such as a multiply into a multiply |
| `Backward` | ready | no cycle while nothing stalls; breaks `ready` rippling back through stages |
| `Both` | everything | one cycle |

An edge **fits** when its two ports carry the same formats in the same
order. Names are never refused: an edge whose fields go by different names
at its two ends is drawn amber and says which. An edge that does not fit is
red, stays where it is — a box's argument can change its fields under an
edge that was fine — and the design does not build until it is fixed.

### Where edges meet

Several edges at one stream port are a meeting, and the port says what it
does with them. The panel of any edge there sets it.

| where | pattern | in code |
|---|---|---|
| several edges leave a port | **Broadcast** (the default): every edge gets every beat | `streamForkEager` |
| | **Balance**: each beat goes to one edge that is free | `streamBalance` |
| several edges arrive at a port | **Merge**, round-robin or priority, in the same cycle or registered | `streamMergeWith` |

The edges' order is the lanes' order, and what priority serves first;
**Raise** in the panel puts an edge first.

### Sharing a unit

A unit's box becomes a **service** when a socket is connected to it: draw an
edge from a box's `multiply` socket to a multiplier's `service` port. One
socket on a service is the unit in place; several share the one copy behind
an arbiter, whose policy and timing are set on the edge's panel. A socket
nobody connected is served in place, a copy of its own for each — which is
why a plain `gain` box needs nothing drawn for its multiplies.

`examples/three-band-eq-shared.json` is three bands each on a multiplier
box of its own: three multipliers where the same chain in place has thirty.

### Designs inside designs

A saved design can be placed in another as a box: **File → Import Design…**
adds it to the palette's *designs* section, and the box's ports are that
design's boundary, its control ports its control inlets. **Double-click**
such a box to drill into it with the values live; the breadcrumb steps back
out. The file carries the sub-designs, so it is complete on its own.

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
row boundary, a `.csv` table for a table boundary — or `frame:<w>x<h>`, a
count in place of a file, for a design that draws: the input stream's one
field is the beat index and what comes out is assembled into a PGM. A source
drives a design with one stream in and one out. Then:

- **Play** runs the design at real time through the speaker; the speaker
  paces the simulator by back-pressure. **Run** is unpaced. **Step** is one
  cycle. **Reset** reopens. **Save heard** writes what the output heard
  beside the source (`sine.heard.wav`).
- Every stream edge carries a **level meter**; a selected box's panel shows
  its stream out as a **waveform** over the last beats and every signal it
  owns, live.
- **break when** in the toolbar takes a condition over the design's nets in
  the names the panel shows, comparing as bits — `in_valid && in_left >
  1000000` stops Run at the next such cycle.

A structural edit while running stops the session; a number box does not.
An unwired inlet's setting is part of the design, so changing one is a
structural edit.

---

## 3. The design file

A design saves as JSON — the graph as data: the boundary's streams with
their fields, the boxes by unit name with their arguments and settings, the
control boxes, the edges with what each registers, the meetings that are
not the default, the positions, the sub-designs and written units, and the
name of its default mapping (below). It holds no types: the **palette is the
type authority**, resolved by name on every load. A unit whose ports have
changed since the file was saved opens with the edge that no longer fits
drawn red.

The file says `"format": 2`. A design saved before boxes had stream ports
has no `format` and is refused, saying so: it is redrawn, one edge a stream.

`hdl/Warp11.Placement.Canvas/examples/` has one per feature, written by
`Warp11.Placement -- examples`, with a README saying what to look at in each.

**Build → Export to F#…** asks where to write the typed form: the units by
their F# names and each call the elaboration makes, in its order — `stage`,
`farm`, `connectAs`, `streamMergeWith`, `sharingWith` — which elaborates to
the same bytes. It is one-way: the typed form cannot be read back as a graph.

---

## 4. The target, the mapping, the build

A design goes to a board through a **mapping**: which board, as a record of
independent choices, and which way its boundary reaches the world. The
mapping is a file beside the design (`gain.kv260.json`), so one design can
have several, and the design names its default.

### The target section

With nothing selected, the design panel ends in **target**:

```
 board       [ kv260 ▾ ]     kv260 · icebreaker · icepi · goboard · de10lite · custom · simulator only
 part        xck26-sfvc784-2LV-c (xilinx.com:kv260_som:part0:1.4)
 build tool  Vivado
 clock       PS clock 71   [ 100000000 ] Hz
 the design's rate lands at   48828.125 Hz
 data path   [ memory ▾ ]    pins · memory · count · scatter · link · countlink · registers
 host memory S_AXI_HPC0_FPD, 128 bits, arena [ 8 ] MiB
 host driver AXI-Lite at [ 0xB0000000 ]
 loading     OS app under /lib/firmware/xilinx
 connectors  I2sSeparateCodecs   mclk H12  lrclk E10  sclk D10 …
 mapping file  gain.kv260.json
```

A **board** picked from the menu fills every row. Any row edited makes it custom, and a
combination the part cannot honour — host memory on a part with no
processing system, a PS clock on an iCE40 — is refused in the status line
by name. The rows:

| row | what it decides |
|---|---|
| part | the toolchain's part arguments, what memory the fabric has, whether there is a processing system |
| build tool | Vivado for Xilinx parts, yosys → nextpnr → icepack for iCE40 |
| clock | where the fabric clock comes from — a PS clock the overlay pins, a crystal through a PLL — and its rate, which every derived rate divides |
| data path | **pins**: the boundary on the I2S pins, a converter on the header. **memory**: the boundary in the host's DDR — rows in a DMA buffer the fabric reads, runs the design over and writes back, no converter needed. **count**: nothing read — the input box's one field is the beat index, the host writes how many and reads the rows back; a generator's path, the Mandelbrot example's |
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
dotnet run --project Warp11.Placement -- build my.json kv260 memory out/    # a board by name (or a board file) and a path
```

What lands in the directory depends on the build tool:

**Vivado** (the KV260):

| file | what |
|---|---|
| `{Name}.v` | the top: the design's instance between the register slave and the boundary |
| `{name}_layout.rs` | the register map as Rust constants — the seam a driver reads |
| `{name}_bd.tcl` | the block design: the processing system from the board, the top as a module reference, the aperture at the base; with host memory, the PS slave port and the master's DDR segment |
| `{name}_pins.xdc` | the constraints from the connector table (pins path only) |
| `build.tcl`, `check_timing.tcl` | the flow, and the gate that refuses a timing-failing bitstream |
| `{name}.dts` | the overlay: firmware name, the uio node, the clock pinned at the rate the design was elaborated for, the DMA arena |
| `{name}.bif`, `shell.json` | the packaging |
| `build.sh` | Vivado, bootgen, dtc → `app/`, then the deploy commands |

**The open flow** (the iCEBreaker, Go Board and Icepi — the
Icepi's files are the ECP5's, `.lpf` for `.pcf`, `openFPGALoader` for
`iceprog`):

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

A design on the **count** path draws a frame instead: `frame:<w>x<h>` in
place of the WAV, the design's chunk width so the host knows how many beats
come back for each it asks for, the view as registers, a PGM out —

```sh
warp11_batch frame:1400x800 out.pgm --sim my.json --chunk 128 \
    --set cxOrigin=0xE0000000 --set cyOrigin=0x10000000 --set dx=575219 --set dy=0xFFF5C28F
warp11_batch frame:1400x800 out.pgm --board my_batch --layout my_batch_layout.rs --chunk 128 --set ...
```

and `-- frame my.json 1400x800 out.pgm cxOrigin=…` draws the same through
the simulator's own device, no driver in the loop.
Either way the driver reports the fabric's **own** cycle count for the batch,
read from the `cycles` register the contract carries — the work's measure,
as against timing a poll loop over a bus.

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

The KV260 and iCEBreaker rows are from 2026-09-17, when a module box was
elaborated with a context FIFO beside it. The canvas no longer builds that
FIFO — in the simulator the same gain design now takes a cycle a frame — and
those two rows have not been measured again on silicon.

| | |
|---|---|
| a drawn gain design on the KV260 through DDR, 97 664 frames | 3.173 ms, byte-identical to the simulator |
| the same design's iCEBreaker build | 7 ports pinned, Fmax 40 MHz against 24 |
| the same design heard on the iCEBreaker | microphones to headphone through the shared-bus header |
| `three-band-eq-shared` built for the Icepi from the drawing (2026-11-14) | 12 of 28 multiplier blocks, Fmax 67.5 MHz against 25; not loaded onto a board |
| `twice-shared` built for the Icepi from the drawing (2026-11-14) | 24 of 28 multiplier blocks, Fmax 55.5 MHz against 25; not loaded onto a board |
