#!/usr/bin/env bash
# Package the `gep-unit` warp11 design (real-engine work-queue pool, gep_design.md §3) as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/gepunit_axi/gepunit_axi.runs/impl_1/gepunit_bd_wrapper.bit
#   - hardware/xmutil/gep-unit/gep-unit.bif  (bootgen recipe)
#   - hardware/xmutil/gep-unit/gep-unit.dts  (device tree overlay source)
#   - hardware/xmutil/gep-unit/shell.json   (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/gep-unit/
#     ├── gepunit_bd_wrapper.bit.bin
#     ├── gep-unit.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo mkdir -p /lib/firmware/xilinx/gep-unit
#   sudo cp gepunit_bd_wrapper.bit.bin gep-unit.dtbo shell.json /lib/firmware/xilinx/gep-unit/
#   sudo xmutil unloadapp                  # if another app (e.g. smartcam) is loaded
#   sudo xmutil loadapp gep-unit
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/gep-unit"
BIT="$REPO_ROOT/hardware/vivado/gepunit_axi/gepunit_axi.runs/impl_1/gepunit_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/gep-unit"

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
cp "$BIT" "$WORK_DIR/gepunit_bd_wrapper.bit"
cp "$SRC_DIR/gep-unit.bif" "$WORK_DIR/"

echo "[1/3] bootgen: gepunit_bd_wrapper.bit -> gepunit_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image gep-unit.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/gepunit_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     gep-unit.dts -> gep-unit.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/gep-unit.dtbo" "$SRC_DIR/gep-unit.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/gep-unit-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo mkdir -p /lib/firmware/xilinx/gep-unit"
echo "  sudo cp /tmp/gep-unit-app/* /lib/firmware/xilinx/gep-unit/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp gep-unit"
