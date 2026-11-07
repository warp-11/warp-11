# The iCEBreaker on the bench

What was learned putting designs on this board that is still true now that
the build directory is generated (`Warp11.Build`, from a mapping file) and
the hand tops that used to live in `hardware/ice40/` are gone. Nothing here
is a build step: `build.sh` in any generated directory runs the pinned
yosys → nextpnr-ice40 → icepack flow and loads the result (`PROG=1` for the
flash, `PROG=sram` to load volatilely), and the gate at its end reads
nextpnr's own report.

## One-time: USB permissions

`iceprog` talks to the FT2232H over raw USB, and the device node is
root-owned with no write bit for you. Install the rule beside this README
once:

```sh
sudo cp 53-icebreaker.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
# unplug and replug the board
iceprog -t            # reads the flash ID and nothing else — the access test
```

**The symptom is worth knowing because it lies.** Without the rule, iceprog
prints `Can't find iCE FTDI USB device`, which reads like an absent board or
a dead cable. `ls -l /dev/bus/usb/<bus>/<dev>` showing `root root` with no
group write is what tells you it is permissions.

## `iceprog -S` does not work on this board

**Measured 2026-09-12.** `iceprog -S` writes the FPGA's configuration SRAM
directly, which would be the ideal first load — volatile, flash untouched. On
an iCEBreaker it completes, **exits 0**, and leaves `cdone: low`: the part
does not configure. The same bitstream over the same cable goes into flash
and boots first time with `VERIFY OK` and `cdone: high`.

So the exit code is not the check — **`cdone` is**. `PROG=sram` stays in the
generated script because it is right on other boards; `PROG=1` is the path
here.

## The board arrives bare

**No headers or Pmod sockets are fitted** (measured on the board in hand,
2026-09-12 — do not assume otherwise from photographs). Nothing is needed to
flash a bitstream: USB, the FT2232H and the configuration flash are all on
board. Connectors are only needed once something has to be *attached*, and
the release's examples attach nothing: every one reaches the board over its
USB — the register map and the rows on the FTDI's second channel, as
`warp11_regs` and `warp11_batch --serial` speak it.

If you do fit a socket: **PMOD1A** is the one nearest the FPGA, and the
preset's `I2sSeparateCodecs` connector is a Pmod I2S2 there — the D/A on the
top row, the A/D on the bottom, each with its own MCLK/LRCK/SCLK. The
converter path (`"path": "pins"` in a mapping) builds the two rungs of the
audio ladder for it exactly as the hand tops did, framing at 46 875 Hz from
the 24 MHz PLL, which `i2sMasterHz` derives and the living check pins.

## Identify the LEDs before reading anything into them

The board carries more than one red LED — a power indicator elsewhere on
it, and five more on the snap-off section. The pair a design's `Leds`
connector drives are the adjacent red and green next to each other. This
cost a wrong diagnosis on first bring-up: a faint red somewhere else on the
board was read as a signal indicator, and a floating-pin story was
constructed to explain it.

A dark green with a successful `iceprog` means the PLL or the reset, and
nothing downstream of them has been reached yet.

## Measured, for the record

The two hand tops on `iCE40UP5K-SG48`, 2026-09-12: the tone 282 / 5 280
logic cells at 40.9 MHz, the passthru 424 at 37.8 MHz, against 24 MHz. The
same designs through the generator land in `build/report.json` now, which
is where the current numbers are.
