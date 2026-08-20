#!/usr/bin/env bash
# Package the `gep` warp11 design as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/gepbarrel_axi/gepbarrel_axi.runs/impl_1/gepbarrel_bd_wrapper.bit
#   - hardware/xmutil/gep-barrel/gep-barrel.bif  (bootgen recipe)
#   - hardware/xmutil/gep-barrel/gep-barrel.dts  (device tree overlay source)
#   - hardware/xmutil/gep-barrel/shell.json   (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/gep-barrel/
#     ├── gepbarrel_bd_wrapper.bit.bin
#     ├── gep-barrel.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo mkdir -p /lib/firmware/xilinx/gep-barrel
#   sudo cp gepbarrel_bd_wrapper.bit.bin gep-barrel.dtbo shell.json /lib/firmware/xilinx/gep-barrel/
#   sudo xmutil unloadapp                  # if another app (e.g. smartcam) is loaded
#   sudo xmutil loadapp gep-barrel
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/gep-barrel"
BIT="$REPO_ROOT/hardware/vivado/gepbarrel_axi/gepbarrel_axi.runs/impl_1/gepbarrel_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/gep-barrel"

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
cp "$BIT" "$WORK_DIR/gepbarrel_bd_wrapper.bit"
cp "$SRC_DIR/gep-barrel.bif" "$WORK_DIR/"

echo "[1/3] bootgen: gepbarrel_bd_wrapper.bit -> gepbarrel_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image gep-barrel.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/gepbarrel_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     gep-barrel.dts -> gep-barrel.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/gep-barrel.dtbo" "$SRC_DIR/gep-barrel.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/gep-barrel-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo mkdir -p /lib/firmware/xilinx/gep-barrel"
echo "  sudo cp /tmp/gep-barrel-app/* /lib/firmware/xilinx/gep-barrel/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp gep-barrel"
