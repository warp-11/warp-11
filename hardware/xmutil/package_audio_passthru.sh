#!/usr/bin/env bash
# Package the `audio-passthru` warp11 design as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/audio_passthru_axi/audio_passthru_axi.runs/impl_1/audio_passthru_bd_wrapper.bit
#   - hardware/xmutil/audio-passthru/audio-passthru.bif      (bootgen recipe)
#   - hardware/xmutil/audio-passthru/audio-passthru.dts      (device tree overlay source)
#   - hardware/xmutil/audio-passthru/shell.json          (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/audio-passthru/
#     ├── audio_passthru_bd_wrapper.bit.bin
#     ├── audio-passthru.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo install -d -m 755 -o root -g root /lib/firmware/xilinx/audio-passthru
#   sudo install -m 644 -o root -g root audio_passthru_bd_wrapper.bit.bin audio-passthru.dtbo shell.json /lib/firmware/xilinx/audio-passthru/
#   sudo xmutil unloadapp                  # if another app is loaded
#   sudo xmutil loadapp audio-passthru
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/audio-passthru"
BIT="$REPO_ROOT/hardware/vivado/audio_passthru_axi/audio_passthru_axi.runs/impl_1/audio_passthru_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/audio-passthru"

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
    echo "    cd hardware/vivado && vivado -mode batch -source build_audio_passthru_axi.tcl" >&2
    exit 1
fi

mkdir -p "$OUT_DIR"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
cp "$BIT" "$WORK_DIR/audio_passthru_bd_wrapper.bit"
cp "$SRC_DIR/audio-passthru.bif" "$WORK_DIR/"

echo "[1/3] bootgen: audio_passthru_bd_wrapper.bit -> audio_passthru_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image audio-passthru.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/audio_passthru_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     audio-passthru.dts -> audio-passthru.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/audio-passthru.dtbo" "$SRC_DIR/audio-passthru.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/audio-passthru-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo install -d -m 755 -o root -g root /lib/firmware/xilinx/audio-passthru"
echo "  sudo install -m 644 -o root -g root /tmp/audio-passthru-app/* /lib/firmware/xilinx/audio-passthru/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp audio-passthru"
