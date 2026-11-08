# How do I put a design on a board?

**You want:** a design you have running in the Sim built for a board you have,
driven from the host the same way — and the same design on another board by
changing one file.

The whole of it is a **mapping**: a JSON file beside the design that names the
board and how the design's needs are carried. The design does not change, and
neither does the host program, because the register map and the row shapes
the host reads come out of the build directory as a Rust seam.

## The shortest thing that works

A design that is a stream through a unit, in the Sim:

```sh
dotnet run --project Warp11.Placement.Canvas -- edit my.json Warp11.Effects/sine.wav
```

The same design on the Icepi, over its UART:

```json
{ "board": { "name": "icepi", … }, "path": "link" }
```

```sh
dotnet run --project Warp11.Placement -- build my.json my.icepi.json out/
cd out && ./build.sh
warp11_batch in.wav out.wav --serial /dev/ttyUSB1 --layout out/my_link_layout.rs --set volume=512
```

And on the KV260, through its DDR — a different file, the same design, the
same driver, the same bytes out:

```json
{ "board": { "name": "kv260", … }, "path": "memory" }
```

```sh
warp11_batch in.wav out.wav --board my_batch --layout out/my_batch_layout.rs --set volume=512
```

The board object is a preset's name filled in — `Warp11.Mapping.ofBoard
"icepi" viaLink` writes one, and the canvas's **Target** panel saves one —
or a file of your own ([How to add a new board](../add-a-board.md)).

## The path: how each need is carried

A design declares needs — rows in, rows out, values, tables, a surface — and
the mapping's `path` says what carries them. The presets:

| `"path"` | rows in | rows out | for |
|---|---|---|---|
| `"memory"` | rows from the host's DDR | rows to the host's DDR | a batch through the KV260: a WAV, a CSV, a PGM |
| `"count"` | a beat index the fabric mints | rows to DDR | a frame the design draws |
| `"link"` | words the host plays over the UART | frames on the UART | the same batch on a board with one wire |
| `"countlink"` | a count | frames on the UART | a frame over the wire |
| `"pins"` | the I2S converter on the header | the converter | audio heard, with a Pmod I2S2 plugged in |
| `"registers"` | — | — | a design with no stream |

Values travel in the register map on every path. A need may be rebound by
name, over the preset:

```json
{ "board": { … }, "path": "pins",
  "bind": { "out": ["OnPins", "InHostRows"], "limit": ["OnPin:led1"] } }
```

Here `out` goes to the converter *and* is recorded into DDR, and the reported
bit `limit` lights `led1`. The binding's registers appear in the map named for
the need and the carrier — `outMemArm`, `outMemWritten` — so the seam says
whose each one is.

## The harness: something plugged into a socket

A board's sockets are where a harness plugs in — a Pmod, a Raspberry Pi
header — each position a package pin. A harness is a file too: what it puts
on which position, by role:

```json
{ "name": "front-end", "plug": "pmod",
  "positions": { "1": ["I2sSharedBus", "bclk"], "2": ["I2sSharedBus", "ws"],
                 "3": ["I2sSharedBus", "sd_in"], "4": ["I2sSharedBus", "sd_out"] } }
```

and the mapping's `plugged` list seats it:

```json
{ "board": { … }, "path": "pins", "plugged": [ { "socket": "PMOD1A", "harness": { … } } ] }
```

After which the harness's pins are the board's, by role, and a harness written
for one board's Pmod plugs into another's unchanged, because the two meet at
the position number. A connector the board carries on its own — the
iCEBreaker's Pmod I2S2 — needs no harness.

## The size: derived, or named

A design that scales — Life's world, Mandelbrot's lanes — reads its size from
the part's facts, and a mapping may say otherwise:

```json
{ "board": { … }, "path": "link", "sizes": { "world": 16 } }
```

A size named for a design that does not ask for it is refused by name.

## What the build directory gives the host

`build.sh` writes the bitstream and ends with the gate (`build/report.json`:
cells, block RAMs, multipliers, the clock achieved). Beside it, the seam —
`my_link_layout.rs` — names every register's offset and the shape of what
crosses the link: `IN_WORDS`, `OUT_WORDS`, `IN_BUFFER_WORDS`, a surface's
`_ROWS` and `_MEM_SLOT_BYTES`. `warp11_regs` and `warp11_batch` read it, so
they need telling nothing but which file.

## Rules that bite

- **A board with no host memory refuses `memory` and `count`** by name; use
  `link` or `countlink`.
- **A field wider than a lane** crosses as several words on the link and
  several beats in memory, low first — a 512-bit image row is sixteen words or
  four beats — and the host tools know the rule.
- **The play buffer is sized from the part's block RAM**, and the seam says
  how many words it holds; the host plays no more than that between reads of
  `…LinkFill`.
- **The first snapshot of a surface is the one latched at reset.** A host
  releases it and the next is live.
- **A converter paces the rows on `pins`; the host paces them on every other
  path.** A design handed its rate is given the converter's on `pins` and
  the nominal 48 kHz elsewhere, because a WAV carries no rate of its own.

## See also

- [Boards](../boards.md) — the four boards, what each carries, what fits.
- [How to add a new board](../add-a-board.md) — the board file.
- [Create a register map](register-map.md) — the values' half.
- [The design canvas](../design-canvas.md) — the same mapping from the
  **Target** panel.
