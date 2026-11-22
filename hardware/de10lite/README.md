# The DE10-Lite on the bench

The board side of the `de10lite` preset: what to install once, how the one
cable goes on, and what to check before the first compile. The build itself
is a generated directory — with `Hello.board` set to `de10Lite`, `dotnet
run --project Warp11.Designs -- hello out/`, then `out/build.sh` — and
[`docs/boards.md`](../../docs/boards.md) is the page.

## Quartus

Quartus Prime Lite under `~/tools/intelFPGA_lite/<version>` (25.1std here),
with Quartus Prime itself and MAX 10 device support; nothing else. A
generated `build.sh` finds the newest `quartus/bin` there, or `QUARTUS_BIN`
names one.

## One-time: the USB-Blaster

`quartus_pgm` talks to the on-board USB-Blaster II over raw USB, root-owned
by default. Install the rule beside this README once:

```sh
sudo cp 51-usbblaster.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
# unplug and replug the board, then:
~/tools/intelFPGA_lite/25.1std/quartus/bin/quartus_pgm -l     # lists "USB-Blaster [...]"
```

If `jtagd` had already started as root before the rule, `killall jtagd`
once; it restarts under your user on the next `quartus_pgm`.

## The one cable

The DE10-Lite has no USB-UART of its own, so the host link is a **3.3 V**
USB-to-serial cable (FT232-style) on the 2×20 GPIO header JP1, three wires,
plugged once:

| cable | header pin | signal | preset port |
|---|---|---|---|
| TX (out of the cable) | 1 | GPIO_0, V10 | `host_rx` |
| RX (into the cable) | 2 | GPIO_1, W10 | `host_tx` |
| GND | 12 | ground | |

It must be a 3.3 V cable: the MAX 10's banks are 3.3 V and a 5 V TTL cable
would exceed them. Pin 11, beside the ground on pin 12, is 5 V: count twice.

**A Raspberry Pi Debug Probe is such a cable**, and is what the board was
brought up on (2026-11-21): its **U** port (not **D**), TX to header pin 1,
RX to pin 2, GND to pin 12. It is a CDC-ACM device, so it is `/dev/ttyACM0`
rather than `/dev/ttyUSB0`, and root's until `52-debug-probe.rules` beside
this file is installed the way the USB-Blaster's rule is, above.

**The link runs at 500 kbaud** (the seam says so; no tool is told), half
the other boards', and the reason is the cable. Measured 2026-11-21 with
two-way runs of 1.4 MB each way:

| | jumpers lying together | TX and RX separated |
|---|---|---|
| 500 kbaud | 2 of 10 runs lost bytes, then 1 of 10 | 0 of 36 |
| 1 Mbaud | 4 of 6, and the probe locked up once | 7 of 50 |

**Keep the TX and RX jumpers apart**: side by side they couple, and it
showed only with rows going both ways at once — one-way runs (a tone, a
Mandelbrot frame) never lost a byte. What is left at 1 Mbaud did not go
away with the wiring and may be the probe; an FTDI cable on the other four
boards never showed any. Every frame carries a checksum, so a run that
lost bytes stops and says so — `malformed reply […]` — rather than writing
a wrong file; run it again. `warp11_batch` clears what a run that died
left in the fabric's buffer before it plays anything.

## Before the first compile: the pins

The preset's pins were written from the board's user manual without the
maker's constraints file to hand, and the bench of 2026-11-21 found them
right as written: the 50 MHz oscillator on `P11`, LEDR0–9 on `A8 A9 A10 B10
D13 C13 E14 D14 A11 B11`, GPIO_0 on `V10`, GPIO_1 on `W10`. A board of your
own on this part should still be checked against Terasic's
`DE10_LITE_golden_top.qsf` (on the board's CD and on their site): a wrong
LED pin is a dark LED; a wrong GPIO pin is an hour on the bench.

The first compile also checks the gate against a real report: it reads
`output_files/<top>.fit.summary` for logic elements, memory bits and 9-bit
multiplier elements, and `<top>.sta.rpt` for Fmax, and refuses if the
board's facts (49 760 LEs, 182 M9Ks, 144 multipliers) disagree with what
Quartus says the 10M50 has.

## Loading

```sh
PROG=sram ./build.sh        # quartus_pgm -m jtag -o "p;output_files/<top>.sof"
```

SRAM only: the configuration flash takes a `.pof` through a flow that is not
built. A power cycle returns the board to whatever its flash holds.
