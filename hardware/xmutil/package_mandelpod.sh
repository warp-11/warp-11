#!/usr/bin/env bash
# Package the `mandelpod` warp11 design as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/mandelpod_axi/mandelpod_axi.runs/impl_1/mandelpod_bd_wrapper.bit
#   - hardware/xmutil/mandelpod/mandelpod.bif      (bootgen recipe)
#   - hardware/xmutil/mandelpod/mandelpod.dts      (device tree overlay source)
#   - hardware/xmutil/mandelpod/shell.json   (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/mandelpod/
#     ├── mandelpod_bd_wrapper.bit.bin
#     ├── mandelpod.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo mkdir -p /lib/firmware/xilinx/mandelpod
#   sudo cp mandelpod_bd_wrapper.bit.bin mandelpod.dtbo shell.json /lib/firmware/xilinx/mandelpod/
#   sudo xmutil unloadapp                  # if another app (e.g. counter) is loaded
#   sudo xmutil loadapp mandelpod
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/mandelpod"
BIT="$REPO_ROOT/hardware/vivado/mandelpod_axi/mandelpod_axi.runs/impl_1/mandelpod_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/mandelpod"

BOOTGEN="${BOOTGEN:-bootgen}"
DTC="${DTC:-dtc}"

if ! command -v "$BOOTGEN" >/dev/null; then
    echo "error: bootgen not on PATH. Source Vivado settings64.sh first:" >&2
    echo "  source ~/tools/Xilinx/Vivado/2025.2/settings64.sh" >&2
    exit 1
fi
if ! command -v "$DTC" >/dev/null; then
    echo "error: dtc not on PATH. Install with: sudo apt install device-tree-compiler" >&2
    exit 1
fi
if [[ ! -f "$BIT" ]]; then
    echo "error: bitstream not found at $BIT" >&2
    echo "  Run the Vivado bitstream flow first:" >&2
    echo "    cd hardware/vivado && vivado -mode batch -source build_mandelpod_axi.tcl" >&2
    exit 1
fi

mkdir -p "$OUT_DIR"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
cp "$BIT" "$WORK_DIR/mandelpod_bd_wrapper.bit"
cp "$SRC_DIR/mandelpod.bif" "$WORK_DIR/"

echo "[1/3] bootgen: mandelpod_bd_wrapper.bit -> mandelpod_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image mandelpod.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/mandelpod_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     mandelpod.dts -> mandelpod.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/mandelpod.dtbo" "$SRC_DIR/mandelpod.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/mandelpod-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo mkdir -p /lib/firmware/xilinx/mandelpod"
echo "  sudo cp /tmp/mandelpod-app/* /lib/firmware/xilinx/mandelpod/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp mandelpod"
