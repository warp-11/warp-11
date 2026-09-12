# The iCE40 target

Everything needed to put a Warp 11 design on an iCEBreaker, through the open
Lattice toolchain: a top wrapper per design, a pin map per design, and one
argument-driven build script.

Two designs live here today, and they are the audio bring-up ladder
[`Warp11.Effects`](../../hdl/Warp11.Effects/README.md) describes, ported to a
part with no host on it: a tone into the D/A, then line in to line out. Both run
over a **Pmod I2S2 in PMOD1A** — known-good line-level audio hardware.

Both are elaborated from the same F# that drives the KV260 apps — `i2sPins`,
`i2sLink`, `toneGenerator` — and the whole of what is board-specific is in this
directory.

## Build one

```sh
cd hdl && dotnet run --project Warp11.Effects -- hardware ..   # emit the Verilog
cd ../hardware/ice40

./build.sh icebreaker_tone_top     audio_tone.pcf     icebreaker_tone_top.v     ../build/AudioToneIce.v
./build.sh icebreaker_passthru_top audio_passthru.pcf icebreaker_passthru_top.v ../build/AudioPassthruIce.v
```

`PROG=1` writes the SPI flash and is the path that works on this board; `PROG=sram` loads volatilely but **does not configure an iCEBreaker** (see below); `PART`, `PACKAGE` and `FREQ`
move the target off the iCEBreaker's defaults. Artifacts land in `build/`, which
is gitignored — the `.bin` is reproducible from the four files on the command
line.

Measured, 2026-09-12, on `iCE40UP5K-SG48`:

| design | logic cells | I/O | Fmax | margin at 24 MHz |
|---|---|---|---|---|
| `icebreaker_tone_top` | 282 / 5,280 (5%) | 7 | 40.9 MHz | 1.70x |
| `icebreaker_passthru_top` | 424 / 5,280 (8%) | 11 | 37.8 MHz | 1.58x |

Both use the part's single PLL. The passthru's critical path is the signal
indicator's hold counter, not the audio path — so the ~92% of the part still
free is free for whatever comes next rather than for closing timing.

## Before the first build: the board arrives bare

**No headers or Pmod sockets are fitted** (measured on the board in hand,
2026-09-12 — do not assume otherwise from photographs). Nothing is needed to
flash a bitstream: USB, the FT2232H and the configuration flash are all on
board, so the smoke test below costs no soldering at all. Connectors are only
needed once something has to be *attached*.

That turns out to be lucky rather than inconvenient, because it makes the
connector a mechanical decision instead of a given:

| footprint | fit | why |
|---|---|---|
| **PMOD1A** | female 2x6 socket | the debug port. Keep the Pmod I2S2 pluggable — a known-good line-level reference is worth having for the life of the project, not just for bring-up |
| **PMOD1B** | whatever a second front end wants | free. A vertical socket adds ~8.5 mm of stack height, which matters if the board is going into an enclosure; right-angle sends a harness out sideways instead |
| supply rail header | two wires, or a 2-pin JST | 5V / 3V3 / 1V2 / GND — this is where a battery enters |
| **PMOD2** | leave it | nothing needs it. The snap-off section's LEDs are not the ones these designs drive |

### One-time: USB permissions

`iceprog` talks to the FT2232H over raw USB, and the device node is root-owned
with no write bit for you. Install the rule beside this README once:

```sh
sudo cp 53-icebreaker.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
# unplug and replug the board
iceprog -t            # reads the flash ID and nothing else — the access test
```

**The symptom is worth knowing because it lies.** Without the rule, iceprog
prints `Can't find iCE FTDI USB device`, which reads like an absent board or a
dead cable. `ls -l /dev/bus/usb/<bus>/<dev>` showing `root root` with no group
write is what tells you it is permissions.

### `iceprog -S` does not work on this board

**Measured 2026-09-12.** `iceprog -S` writes the FPGA's configuration SRAM
directly, which would be the ideal first load — volatile, flash untouched. On an
iCEBreaker it completes, **exits 0**, and leaves `cdone: low`: the part does not
configure. The same bitstream over the same cable goes into flash and boots
first time with `VERIFY OK` and `cdone: high`.

So the exit code is not the check — **`cdone` is**. `PROG=sram` stays in the
build script because it is right on other boards, and it is not the path here.

### The zero-hardware smoke test

Flash `icebreaker_passthru_top` with nothing attached to the board at all. The
receiver frames off its own clocks whether or not a converter is listening, so:

- **green blinking at ~2 Hz** means the PLL locked, the reset released, the
  divisors are counting and the LED path works — the whole design except the
  codec.
- **red dark**, because an unconnected input settles to a level rather than to
  audio. Simulated against this design: the line held low gives 0%, held high
  gives 0% (all-ones decodes to -1, magnitude 1), and only a genuinely *noisy*
  line gives 100%. So a lit red on a bare board means the input is picking
  something up, and a dark one is the expected case.

**Identify the LEDs before reading anything into them.** The board carries more
than one red LED — there is a power indicator elsewhere on it, and the snap-off
section has five more. **The pair these designs drive are the adjacent red and
green next to each other**; the red is meaningless unless it is the one beside
the blinking green. This cost a wrong diagnosis on first bring-up: a faint red
somewhere else on the board was read as the signal indicator, and a floating-pin
story was constructed to explain it.

Green dark with a successful `iceprog` means the PLL or the reset, and nothing
downstream of them has been reached yet.

## Wiring

Pin numbers are in the `.pcf` files with the cross-check that produced them.
**The map is not a translation of the KV260's** — the Pmod's own row ordering
carries over, but the package pins are new work, which is why `build.sh` fails
rather than warns when a port has no `set_io`.

A Digilent **Pmod I2S2** in **PMOD1A**, the connector nearest the FPGA. It is a
12-pin Pmod, so it fills the header: the D/A converter on the top row and the
A/D on the bottom, each with its own MCLK/LRCK/SCLK. Line in to the A/D's 3.5 mm
jack, headphones or an amplifier on the D/A's.

For the tone build only the top row matters; the A/D sits unclocked.

## What the LEDs say

The KV260 passthru carries two bring-up taps in its register map, because "no
sound" has two completely different causes — a silent ADC and a dead transmitter
— and a board cannot be asked which. There is no host here to read a tap with,
so they are the two on-board LEDs:

| LED | lit when |
|---|---|
| **green**, blinking ~2 Hz | frames are arriving — the link is clocking |
| **red** | the signal is above -48 dBFS — it is hearing something |

Green dark is a clocking problem: the PLL, the divisors or the pin map. Green
blinking with red dark means the link runs and the input is silent, which points
at the A/D, the cable or the source. Both lit and still no sound points at the
D/A or what is plugged into it.

The blink is paced by the *frame rate*, not the fabric clock, so it keeps its
meaning on any board this runs on — and a living check pins that, because a
counter advanced on the clock would keep blinking with the converter unplugged.

## Why there is a hand-written Verilog file here at all

Two things the elaborator cannot emit, both in
[`icebreaker_pll_reset.vh`](icebreaker_pll_reset.vh):

**The PLL.** Warp 11 has no blackbox or foreign-module facility — `Instance`
holds a `ModuleDef`, not a name — so `SB_PLL40_PAD` cannot be instantiated from
F#. That absence is deliberate: a design able to name a vendor primitive stops
being the same design on the other board.

The PLL itself is not optional. A CS5343 locks to MCLK at 256x, 384x or 512x
the frame rate; MCLK is made by toggling a register, so the fastest one a design
can present is half its fabric clock; the bare 12 MHz crystal therefore tops out
at 128x, which nothing on the Pmod will lock to. 24 MHz makes the ratio exactly
256 and the frame rate 46 875 Hz. (48 kHz is not available from this crystal at
all, and asking for it is an elaboration error rather than a 2.3% detune.)

**The reset.** The board has no reset pin, so the design's is made from a
counter that releases fifteen cycles after the PLL locks. Releasing on *lock*
rather than on a fixed count is the part that matters: a clock divider that
starts counting on an unlocked PLL's output produces a first frame of the wrong
length, exactly while the converter is trying to acquire.

The wrapper is also the natural home for the third board fact — that the LEDs
are wired to ground. The inversion is here, so the design keeps active-high
indicators and stays portable.

## The three toolchain traps

All three are encoded in `build.sh` rather than written down for someone to
remember. They cost real time when rediscovered:

- **`/usr/bin/yosys` is 0.33** and shadows the pinned 0.68. It dies with
  ``Assert `nusers(O.extract_end(i)) <= 1' failed in ice40_dsp_pm.h:388`` in the
  iCE40 DSP pass on any wide multiply. The script puts `~/tools/bin` first and
  prints which one it used.
- **Do not set `LD_LIBRARY_PATH`** — the exact opposite of the firtool wrapper's
  rule. These binaries carry their own RPATH and ship a complete `lib/`; putting
  it on the loader path segfaults yosys at startup. The script clears it.
- **`-dsp` on `synth_ice40`**, or every area number is meaningless:
  `AudioEffectsAxi` reports 41,246 LUT4 bare against 3,014 LUT4 + 131 SB_MAC16
  with the flag.

Install the toolchain with [`hdl/tools/install-oss-cad-suite.sh`](../../hdl/tools/install-oss-cad-suite.sh)
— pinned, checksummed, no root. It is a development dependency with the same
standing as firtool: nothing user-facing reaches for it.

## The two gates

`build.sh` fails rather than warns on either:

- **Every port is pinned.** A `set_io` naming a port that does not exist is
  already an error in nextpnr, which is why no line in these `.pcf` files
  carries `-nowarn`. The other direction is the script's: a port with no
  `set_io` is the failure that silently produces a working-looking bitstream
  driving a codec clock out of nowhere.
- **Timing.** nextpnr's reported Fmax against `FREQ`. The repo's top hardware
  lesson is that the Sim and Verilator have no timing model, so a cone several
  times too slow looks perfectly correct right up until silicon latches
  unsettled values. On this design the symptom would be audio that is almost
  right, which is the worst kind. Note that nextpnr writes an `.asc` even when
  timing fails, so its exit code is not the gate — the log is.
