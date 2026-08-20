#!/usr/bin/env bash
# Package the `gep` warp11 design as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/gep_axi/gep_axi.runs/impl_1/gep_bd_wrapper.bit
#   - hardware/xmutil/gep/gep.bif  (bootgen recipe)
#   - hardware/xmutil/gep/gep.dts  (device tree overlay source)
#   - hardware/xmutil/gep/shell.json   (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/gep/
#     ├── gep_bd_wrapper.bit.bin
#     ├── gep.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo mkdir -p /lib/firmware/xilinx/gep
#   sudo cp gep_bd_wrapper.bit.bin gep.dtbo shell.json /lib/firmware/xilinx/gep/
#   sudo xmutil unloadapp                  # if another app (e.g. smartcam) is loaded
#   sudo xmutil loadapp gep
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/gep"
BIT="$REPO_ROOT/hardware/vivado/gep_axi/gep_axi.runs/impl_1/gep_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/gep"

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
    echo "  Run the Vivado bitstream flow first." >&2
    exit 1
fi

mkdir -p "$OUT_DIR"

# bootgen consumes a .bif that references the .bit by basename — easiest if we
# stage both into the same directory before invoking it.
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
cp "$BIT" "$WORK_DIR/gep_bd_wrapper.bit"
cp "$SRC_DIR/gep.bif" "$WORK_DIR/"

echo "[1/3] bootgen: gep_bd_wrapper.bit -> gep_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image gep.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/gep_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     gep.dts -> gep.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/gep.dtbo" "$SRC_DIR/gep.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/gep-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo mkdir -p /lib/firmware/xilinx/gep"
echo "  sudo cp /tmp/gep-app/* /lib/firmware/xilinx/gep/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp gep"
