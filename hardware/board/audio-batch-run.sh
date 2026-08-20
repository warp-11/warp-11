#!/usr/bin/env bash
# Run a WAV file through the audio-batch accelerator on a KV260 and bring the
# result back.
#
#     hardware/board/audio-batch-run.sh in.wav out.wav [board-ip]
#
# The three commands this replaces — an scp up, an ssh run, an scp down — are
# the whole "hardware path" for this example. There is no daemon and no GUI:
# the accelerator is demand-driven, does one block and stops, so a long-lived
# service would have nothing to do between calls.
#
# What it prints is the accelerator's own report, which is worth reading rather
# than skipping:
#
#   compressed:  15.471 ms for 480000 frames (3.22 cycles/frame @ 100 MHz)
#   flat copy:   true  (0 frames differ)
#
# The second line is the accelerator configured for no gain reduction — a memcpy
# with a 128-bit beat in the middle. Byte-exact output there says the DDR path
# is sound with the DSP taken out of the question, which is the bisection you
# want before believing the first line.
#
# Requires: the `audio-batch` app loaded, and `audio_batch_first_light` deployed
# to ~/warp11-driver on the board. See hardware/board/README.md section 3.
set -euo pipefail

if [ $# -lt 2 ]; then
    sed -n '2,20p' "$0" | sed 's/^# \?//'
    exit 1
fi

IN=$1
OUT=$2
BOARD=${3:-${WARP11_BOARD:-192.168.1.172}}
USER=${WARP11_BOARD_USER:-ubuntu}
DIR=warp11-driver

[ -f "$IN" ] || { echo "no such file: $IN" >&2; exit 1; }

# 16-bit stereo PCM is what the driver's codec reads, and a wrong format fails
# on the board rather than here unless we look. `file` is enough to catch the
# common mistakes (mp3 renamed, mono, 24-bit) without a dependency.
if command -v file > /dev/null && ! file -b "$IN" | grep -qi "WAVE.*16 bit.*stereo"; then
    echo "warning: $IN does not look like 16-bit stereo WAV — the driver will refuse it" >&2
    echo "         (ffmpeg -i $IN -c:a pcm_s16le -ac 2 fixed.wav)" >&2
fi

# The arena is 8 MiB with the output half at 4 MiB, so the input is bounded at
# 4 MiB = 524,288 frames = about 10.9 s of 48 kHz stereo. Saying so here beats
# the driver's "file needs N bytes of arena" after two file copies.
BYTES=$(stat -c%s "$IN")
if [ "$BYTES" -gt $((4 * 1024 * 1024)) ]; then
    echo "$IN is $((BYTES / 1024)) KiB; the udmabuf arena holds 4 MiB of input" >&2
    echo "  — about 10.9 s of 48 kHz stereo. Split it, or grow the size in" >&2
    echo "    hardware/xmutil/audio-batch/audio-batch.dts and rebuild." >&2
    exit 1
fi

BASE=$(basename "$IN")
scp -q "$IN" "$USER@$BOARD:$DIR/$BASE"
ssh "$USER@$BOARD" "cd $DIR && ./audio_batch_first_light '$BASE' '$BASE.out.wav'"
scp -q "$USER@$BOARD:$DIR/$BASE.out.wav" "$OUT"
ssh "$USER@$BOARD" "cd $DIR && rm -f '$BASE' '$BASE.out.wav'"

echo "wrote $OUT"
