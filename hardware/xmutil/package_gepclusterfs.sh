#!/usr/bin/env bash
# Package the `gep-cluster-fs` warp11 design (WarpCPU cluster, gep_plan.md Track 2 step 4) as an xmutil-loadable app for KV260.
#
# Inputs (host):
#   - hardware/vivado/gepclusterfs_axi/gepclusterfs_axi.runs/impl_1/gepclusterfs_bd_wrapper.bit
#   - hardware/xmutil/gep-cluster-fs/gep-cluster-fs.bif  (bootgen recipe)
#   - hardware/xmutil/gep-cluster-fs/gep-cluster-fs.dts  (device tree overlay source)
#   - hardware/xmutil/gep-cluster-fs/shell.json   (xmutil app metadata)
#
# Output (host):
#   hardware/xmutil/build/gep-cluster-fs/
#     ├── gepclusterfs_bd_wrapper.bit.bin
#     ├── gep-cluster-fs.dtbo
#     └── shell.json
#
# Then on the KV260:
#   sudo mkdir -p /lib/firmware/xilinx/gep-cluster-fs
#   sudo cp gepclusterfs_bd_wrapper.bit.bin gep-cluster-fs.dtbo shell.json /lib/firmware/xilinx/gep-cluster-fs/
#   sudo xmutil unloadapp                  # if another app (e.g. smartcam) is loaded
#   sudo xmutil loadapp gep-cluster-fs
#
# Requirements (host):
#   - bootgen on PATH (source Vivado settings64.sh, or pass via $BOOTGEN)
#   - dtc on PATH      (apt install device-tree-compiler)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/hardware/xmutil/gep-cluster-fs"
BIT="$REPO_ROOT/hardware/vivado/gepclusterfs_axi/gepclusterfs_axi.runs/impl_1/gepclusterfs_bd_wrapper.bit"
OUT_DIR="$REPO_ROOT/hardware/xmutil/build/gep-cluster-fs"

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
cp "$BIT" "$WORK_DIR/gepclusterfs_bd_wrapper.bit"
cp "$SRC_DIR/gep-cluster-fs.bif" "$WORK_DIR/"

echo "[1/3] bootgen: gepclusterfs_bd_wrapper.bit -> gepclusterfs_bd_wrapper.bit.bin"
(cd "$WORK_DIR" && "$BOOTGEN" -image gep-cluster-fs.bif -arch zynqmp -process_bitstream bin -w)
cp "$WORK_DIR/gepclusterfs_bd_wrapper.bit.bin" "$OUT_DIR/"

echo "[2/3] dtc:     gep-cluster-fs.dts -> gep-cluster-fs.dtbo"
"$DTC" -@ -O dtb -o "$OUT_DIR/gep-cluster-fs.dtbo" "$SRC_DIR/gep-cluster-fs.dts"

echo "[3/3] copy:    shell.json"
cp "$SRC_DIR/shell.json" "$OUT_DIR/"

echo
echo "Packaged app at: $OUT_DIR"
ls -la "$OUT_DIR"
echo
echo "Deploy to KV260:"
echo "  scp $OUT_DIR/* ubuntu@<board-ip>:/tmp/gep-cluster-fs-app/"
echo "  ssh ubuntu@<board-ip>"
echo "  sudo mkdir -p /lib/firmware/xilinx/gep-cluster-fs"
echo "  sudo cp /tmp/gep-cluster-fs-app/* /lib/firmware/xilinx/gep-cluster-fs/"
echo "  sudo xmutil unloadapp     # if a previous app is loaded"
echo "  sudo xmutil loadapp gep-cluster-fs"
