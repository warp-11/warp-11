#!/usr/bin/env bash
# Package the `audio-gain` warp11 design as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/audio_gain_axi/audio_gain_axi.runs/impl_1/audio_gain_bd_wrapper.bit
#   - hardware/xmutil/audio-gain/audio-gain.bif      (bootgen recipe)
#   - hardware/xmutil/audio-gain/audio-gain.dts      (device tree overlay source)
#   - hardware/xmutil/audio-gain/shell.json          (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/audio-gain/
#     ├── audio_gain_bd_wrapper.bit.bin
#     ├── audio-gain.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo install -d -m 755 -o root -g root /lib/firmware/xilinx/audio-gain
#   sudo install -m 644 -o root -g root audio_gain_bd_wrapper.bit.bin audio-gain.dtbo shell.json /lib/firmware/xilinx/audio-gain/
#   sudo xmutil unloadapp                  # if another app is loaded
#   sudo xmutil loadapp audio-gain
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/audio-gain"
BIT="$REPO_ROOT/hardware/vivado/audio_gain_axi/audio_gain_axi.runs/impl_1/audio_gain_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/audio-gain"

BOOTGEN="${BOOTGEN:-bootgen}"
DTC="${DTC:-dtc}"

if ! command -v "$BOOTGEN" >/dev/null; then
    echo "error: bootgen not on PATH. Source Vivado settings64.sh first:" >&2
    echo "  source ~/tools/Xilinx/2025.2/settings64.sh" >&2
    exit 1
fi
if ! command -v "$DTC" >/dev/null; then
    echo "error: dtc not on PATH. Install with: sudo apt install device-tree-compiler" >&2
    exit 1
fi
if [[ ! -f "$BIT" ]]; then
    echo "error: bitstream not found at $BIT" >&2
    echo "  Run the Vivado bitstream flow first:" >&2
    echo "    cd hardware/vivado && vivado -mode batch -source build_audio_gain_axi.tcl" >&2
    exit 1
fi

mkdir -p "$OUT_DIR"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
cp "$BIT" "$WORK_DIR/audio_gain_bd_wrapper.bit"
cp "$SRC_DIR/audio-gain.bif" "$WORK_DIR/"

echo "[1/3] bootgen: audio_gain_bd_wrapper.bit -> audio_gain_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image audio-gain.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/audio_gain_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     audio-gain.dts -> audio-gain.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/audio-gain.dtbo" "$SRC_DIR/audio-gain.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/audio-gain-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo install -d -m 755 -o root -g root /lib/firmware/xilinx/audio-gain"
echo "  sudo install -m 644 -o root -g root /tmp/audio-gain-app/* /lib/firmware/xilinx/audio-gain/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp audio-gain"
