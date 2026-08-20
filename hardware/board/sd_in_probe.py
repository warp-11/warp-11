#!/usr/bin/env python3
"""Live SD_IN line + mic-input probe for the audio-wdrc-mems front end.

Simplest-possible mic bring-up bisection: prove the FPGA input path + wiring
continuity with NO mic attached. The I2S clocks (bclk/ws) are generated inside
the fabric, so i2sRx keeps framing regardless of what's on the SD_IN line —
just tie the SD_IN test point to a rail and watch LAST_LEFT.

  SD_IN -> 3.3 V : LAST_LEFT == 0xFFFFFF   (every sampled bit = 1)
  SD_IN -> GND   : LAST_LEFT == 0x000000
  SD_IN floating : LAST_LEFT == 0x000000   (pin has a weak pulldown)

The meaningful assertion is the 3.3 V case -> 0xFFFFFF. If it stays 0, the path
from your test point to FPGA pin D10 (J2.3) is broken -- proven with no mic.

With a real mic wired, LAST_LEFT shows changing nonzero values (room noise /
taps / speech) and the IN_PEAK_* meters latch the loudest abs sample. With
wdrc-server STOPPED nothing pulses envClear, so IN_PEAK_* are sticky peak-holds
-- pass --clear to reset them at startup, or run clear_peak.py between tests.

Reads the AudioWdrcSlave register window over the UIO mmap (no sudo, safe to
run alongside wdrc-server). Offsets from AudioWdrcSlave (addrWidth=8):
  0x010 RX_COUNT    climbs ~48.8k/s if the fabric clock is alive
  0x014 SDOUT_SEEN  sticky: bit0=seen-high, bit1=seen-low, 3=line toggled
  0x018 LAST_LEFT   live raw upper-24-bit slot, overwrites every valid frame
  0x030 ENV_CLEAR   w1pBit: write 1 resets the peak-holds below
  0x034 IN_PEAK_L   per-channel input peak-hold (SEL=GND mics, left slot)
  0x038 IN_PEAK_R   per-channel input peak-hold (SEL=3V3 mics, right slot)

Usage:  python3 sd_in_probe.py [--clear] [/dev/uioN]   (default /dev/uio4)
"""
import mmap
import os
import struct
import sys
import time

args = sys.argv[1:]
do_clear = "--clear" in args
args = [a for a in args if a != "--clear"]
DEV = args[0] if args else "/dev/uio4"

RX_COUNT = 0x010
SDOUT_SEEN = 0x014
LAST_LEFT = 0x018
ENV_PEAK = 0x02C
ENV_CLEAR = 0x030
IN_PEAK_L = 0x034
IN_PEAK_R = 0x038

fd = os.open(DEV, os.O_RDWR)
m = mmap.mmap(fd, 0x1000)


def r(off):
    return struct.unpack_from("<I", m, off)[0]


if do_clear:
    struct.pack_into("<I", m, ENV_CLEAR, 1)
    print("envClear pulsed -- peak-holds reset")

print(f"probing {DEV} -- tie SD_IN (J2.3 / pin D10) to a rail and watch LAST_LEFT")
print("  3.3V -> 0xFFFFFF   GND/floating -> 0x000000   (Ctrl-C to stop)\n")

prev_key = None
try:
    while True:
        ll = r(LAST_LEFT) & 0xFFFFFF
        ss = r(SDOUT_SEEN) & 0x3
        rxc = r(RX_COUNT)
        pl = r(IN_PEAK_L) & 0xFFFFFF
        pr = r(IN_PEAK_R) & 0xFFFFFF
        ep = r(ENV_PEAK) & 0xFFFFFF
        key = (ll, ss, pl, pr, ep)
        if key != prev_key:
            print(
                f"LAST_LEFT=0x{ll:06X}  SDOUT_SEEN={ss}  "
                f"IN_PEAK_L=0x{pl:06X}  IN_PEAK_R=0x{pr:06X}  "
                f"ENV_PEAK=0x{ep:06X}  RX_COUNT={rxc}",
                flush=True,
            )
            prev_key = key
        time.sleep(0.05)
except KeyboardInterrupt:
    print()
