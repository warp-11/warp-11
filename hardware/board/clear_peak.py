#!/usr/bin/env python3
"""Pulse envClear on the AudioWdrcSlave to reset the peak-hold meters.

Clears envPeak (0x02C) + inPeakL (0x034) + inPeakR (0x038) by writing 1 to the
w1pBit envClear register (0x030). Handy during mic bring-up when wdrc-server is
stopped (nothing else pulses envClear), so the IN_PEAK_* meters latch their max
and need a manual reset between tests.

Usage:  python3 clear_peak.py [/dev/uioN]   (default /dev/uio4)
"""
import mmap
import os
import struct
import sys

DEV = sys.argv[1] if len(sys.argv) > 1 else "/dev/uio4"
ENV_CLEAR = 0x030

m = mmap.mmap(os.open(DEV, os.O_RDWR), 0x1000)
struct.pack_into("<I", m, ENV_CLEAR, 1)
print(f"envClear pulsed on {DEV} — envPeak / inPeakL / inPeakR reset")
