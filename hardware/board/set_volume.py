#!/usr/bin/env python3
"""Set the AudioWdrcSlave master volume (safety attenuation during DAC bring-up).

volume is a Q8.8 register at 0x000: 256 = unity (0 dB), 128 = -6 dB, 32 ~ -18 dB,
0 = silence. Use a small value to keep the DAC output quiet while you confirm the
output path works, then ramp up carefully. Works with wdrc-server stopped.

Usage:  python3 set_volume.py <value> [/dev/uioN]   (default dev /dev/uio4)
        python3 set_volume.py 16          # ~ -24 dB, safe first-listen level
"""
import mmap
import os
import struct
import sys

if len(sys.argv) < 2:
    sys.exit("usage: set_volume.py <0..256+> [/dev/uioN]")

val = int(sys.argv[1], 0) & 0xFFFF
DEV = sys.argv[2] if len(sys.argv) > 2 else "/dev/uio4"
VOLUME = 0x000

m = mmap.mmap(os.open(DEV, os.O_RDWR), 0x1000)
struct.pack_into("<I", m, VOLUME, val)
print(f"volume set to {val}/256 ({100.0 * val / 256.0:.0f}% linear) on {DEV}")
