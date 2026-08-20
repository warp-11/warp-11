#!/usr/bin/env bash
# gep-marshal-bench.sh — on-board measurement of the GEP host-marshal path
# (gep_plan.md Track 2 item 4, gate 2) and the warp11-dma read path (gate 1).
#
# Runs the deployed `gep-hw` binary through:
#   1. --unit --memprobe   : read-path microbench — write-combine vs warp11-dma
#      (12–16x is the claim; this is where it either shows up on silicon or not)
#   2. --marshal ...        : read op list -> gather inline-parent work items ->
#      publish, swept over thread counts, across the publish A/B knobs:
#        batch  vs  --stream   (tail-chasing publication)
#        write-combine writes  vs  --dma  (warp11-dma staged burst)
#
# The gather half is already known cheap off-board (~15 us serial at pop 1024,
# far under the ~1 ms fabric budget); the open question this answers is the
# read + publish cost on the A53, and which publish knob wins there.
#
# Needs NO bitstream — the marshal is pure host memory + DMA over a scratch
# udmabuf. Run it after `./gradlew :examples:gep:deployToBoard`, or let
# `:examples:gep:benchMarshalOnBoard` deploy + invoke it for you.
#
# Usage (on the board):
#   sudo ~/warp11-gep/bin/... is handled internally; just:
#     bash gep-marshal-bench.sh [--pops "1024 4096"] [--reps N] [--buf NAME]
set -uo pipefail

BIN="${GEP_HW_BIN:-$HOME/warp11-gep/bin/gep-hw}"
POPS="1024 4096"
REPS=50
BUF="udmabuf0"

while [ $# -gt 0 ]; do
  case "$1" in
    --pops) POPS="$2"; shift 2 ;;
    --reps) REPS="$2"; shift 2 ;;
    --buf)  BUF="$2";  shift 2 ;;
    --bin)  BIN="$2";  shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [ ! -x "$BIN" ]; then
  echo "gep-hw not found at $BIN — run ':examples:gep:deployToBoard' first" >&2
  exit 1
fi

# `sudo -n`: never prompt. gep-hw is in /etc/sudoers.d/warp11 NOPASSWD, and it
# needs root for /dev/warp11-dma + /dev/udmabuf* (both 0600).
run() { echo "+ gep-hw $*"; sudo -n "$BIN" "$@"; echo; }

echo "=== environment ==="
uptime
echo -n "load average now: "; cut -d' ' -f1 /proc/loadavg
LOAD1=$(cut -d' ' -f1 /proc/loadavg)
# Post-boot storm inflates benches for 2-3 min (README §9). Warn, don't block.
awk -v l="$LOAD1" 'BEGIN{ if (l+0 > 1.0) print "  WARNING: load > 1.0 — wait for the boot storm to settle before trusting numbers" }'
echo -n "warp11-dma:  "; [ -e /dev/warp11-dma ] && echo "present" || echo "ABSENT (reads fall back to write-combine — modprobe warp11-dma)"
echo -n "scratch buf: "; [ -e "/dev/$BUF" ] && echo "/dev/$BUF present" || echo "/dev/$BUF ABSENT (marshal degrades to gather-only)"
echo

echo "=== 1. read-path microbench (write-combine vs warp11-dma) ==="
# --unit --memprobe times a 16 KB read across udmabuf0 (WC), udmabuf1 (cached),
# and udmabuf0+warp11-dma. Tolerate a missing udmabuf1 (|| true).
run --unit --memprobe || echo "  (memprobe skipped/failed — often just a missing udmabuf1)"

echo "=== 2. host marshal — read + gather + publish ==="
for pop in $POPS; do
  echo "----- pop $pop -----"
  # Each run sweeps threads internally (serial/1/2/4/ncpu). Four publish arms:
  run --marshal --pop "$pop" --reps "$REPS" --buf "$BUF"                 # batch, write-combine
  run --marshal --pop "$pop" --reps "$REPS" --buf "$BUF" --stream        # tail-chasing, write-combine
  run --marshal --pop "$pop" --reps "$REPS" --buf "$BUF" --dma           # batch, warp11-dma writes
  run --marshal --pop "$pop" --reps "$REPS" --buf "$BUF" --stream --dma  # tail-chasing, warp11-dma writes
done

echo "=== done ==="
echo "gate 2 passes if total (read + gather + publish) <= ~1000 us/gen at pop 1024,"
echo "with margin — the fabric breed pass it hides behind is ~1 ms."
