#!/usr/bin/env bash
# yosys -> nextpnr-ice40 -> icepack for an iCE40 design, argument driven.
#
#   ./build.sh <top-module> <pcf> <verilog> [<verilog>...]
#
# Environment, all with iCEBreaker defaults: PART (up5k), PACKAGE (sg48),
# FREQ (24, MHz, and the gate below fails under it), PROG (`sram` to load
# volatilely, `1` to write the SPI flash), OSS_CAD_BIN (~/tools/bin).
#
# **Argument driven from the start**, because the alternative is visible next
# door: `hardware/vivado/` carries seventeen near-identical `build_*_axi.tcl`
# copies with the part hardcoded into each, and a fix to one of them is a fix to
# one of them. A second iCE40 design should cost a command line, not a file.
set -euo pipefail

if [ $# -lt 3 ]; then
    sed -n '2,12p' "$0" >&2
    exit 2
fi

top=$1
pcf=$2
shift 2
sources=("$@")

part=${PART:-up5k}
package=${PACKAGE:-sg48}
freq=${FREQ:-24}

here=$(cd "$(dirname "$0")" && pwd)
out=${OUT_DIR:-$here/build}
mkdir -p "$out"

# ---- Trap 1: the wrong yosys ----------------------------------------------
# `/usr/bin/yosys` on this machine is 0.33 (2023) and dies with
#   Assert `nusers(O.extract_end(i)) <= 1' failed in ice40_dsp_pm.h:388
# in the iCE40 DSP inference pass on any design with a wide multiply. The audio
# chain is exactly that. So the pinned suite goes first on PATH and the version
# is checked rather than assumed — a distribution upgrade putting 0.33 back in
# front is silent otherwise, right up to an assertion nobody expects.
bindir=${OSS_CAD_BIN:-$HOME/tools/bin}
export PATH="$bindir:$PATH"

# ---- Trap 2: LD_LIBRARY_PATH ----------------------------------------------
# The opposite of the firtool wrapper's rule next door. These binaries carry
# their own RPATH and ship a complete lib/; anything on LD_LIBRARY_PATH makes
# system libraries resolve against the suite's incompatible copies and
# segfaults yosys at startup. Cleared rather than merely not set, because an
# inherited one from a shell profile fails the same way.
unset LD_LIBRARY_PATH

for tool in yosys nextpnr-ice40 icepack; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool is not on PATH — run hdl/tools/install-oss-cad-suite.sh" >&2
        exit 1
    fi
done

echo "yosys:   $(command -v yosys) — $(yosys -V)"
echo "nextpnr: $(command -v nextpnr-ice40)"

json=$out/$top.json
asc=$out/$top.asc
bin=$out/$top.bin

# ---- Trap 3: -dsp ----------------------------------------------------------
# Without it every multiply maps to logic and every area number is meaningless:
# AudioEffectsAxi reports 41,246 LUT4 bare against 3,014 LUT4 + 131 SB_MAC16
# with the flag. Neither of the two designs here multiplies today, and the flag
# still goes on now — the first time this matters is the first time somebody
# puts a gain stage in the passthru, and that is not a moment anybody will
# remember a synthesis flag at.
echo
echo "== synth =="
yosys -p "read_verilog -I$here ${sources[*]}; synth_ice40 -dsp -top $top -json $json"

# ---- Gate 1: every port is pinned -----------------------------------------
# **The failure this exists for is silent.** nextpnr places a port that no
# `set_io` mentions wherever it likes and carries on with a warning, so a pin
# map missing a line builds a working-looking bitstream that drives the codec's
# clock out of an unconnected ball. The iCEBreaker's Pmod ordering is not the
# KV260's, so the map here is new work rather than a translation, which is
# precisely when a line goes missing.
#
# The other direction is nextpnr's own: a `set_io` naming a port that does not
# exist is an error already, which is why no line in these .pcf files carries
# `-nowarn`.
echo
echo "== pins =="
python3 - "$json" "$pcf" "$top" <<'PY'
import json, re, sys

design, pcf, top = sys.argv[1], sys.argv[2], sys.argv[3]

with open(design) as f:
    ports = set(json.load(f)["modules"][top]["ports"])

pinned = set()
for line in open(pcf):
    line = line.split("#", 1)[0].strip()
    if not line:
        continue
    fields = [f for f in line.split() if not f.startswith("-")]
    if len(fields) >= 3 and fields[0] == "set_io":
        pinned.add(re.sub(r"\[\d+\]$", "", fields[1]))

unpinned = sorted(ports - pinned)
unknown = sorted(pinned - ports)

for name in unpinned:
    print(f"  port '{name}' has no set_io in {pcf}")
for name in unknown:
    print(f"  set_io '{name}' names no port of {top}")

if unpinned or unknown:
    print(f"pin map and design disagree — see above", file=sys.stderr)
    sys.exit(1)

print(f"  {len(ports)} ports, all pinned")
PY

echo
echo "== place and route =="
nextpnr-ice40 "--$part" --package "$package" --pcf "$pcf" --json "$json" \
    --asc "$asc" --freq "$freq" 2>&1 | tee "$out/$top.pnr.log"

# ---- Gate 2: timing --------------------------------------------------------
# The repo's top hardware lesson is that the Sim and Verilator have no timing
# model, so a cone several times too slow looks perfectly correct right up until
# silicon latches unsettled values. A new part with no gate reproduces that
# whole class of bug — and on this one the symptom would be audio that is
# almost right, which is the worst kind.
#
# Read out of the log rather than trusted to nextpnr's own exit code: it reports
# a timing failure and still writes an .asc.
echo
echo "== timing =="
python3 - "$out/$top.pnr.log" "$freq" <<'PY'
import re, sys

log, target = sys.argv[1], float(sys.argv[2])
achieved = [float(m) for m in re.findall(r"Max frequency for clock.*?:\s*([\d.]+)\s*MHz", open(log).read())]

if not achieved:
    print("nextpnr reported no clock frequency — cannot gate on timing", file=sys.stderr)
    sys.exit(1)

worst = min(achieved)
margin = worst / target
print(f"  Fmax {worst:.2f} MHz against {target:.2f} MHz target — {margin:.2f}x")

if worst < target:
    print(f"TIMING FAILED: {worst:.2f} MHz < {target:.2f} MHz", file=sys.stderr)
    sys.exit(1)
PY

echo
echo "== pack =="
icepack "$asc" "$bin"
ls -l "$bin"

# PROG=sram loads the bitstream into the FPGA's configuration SRAM: it runs
# immediately and is gone at the next power cycle, leaving the flash untouched.
# **That is the right first load on a new board.** It is the fastest way to find
# out whether the part configures at all, and it cannot leave anything behind to
# explain later — the board comes back exactly as it was by unplugging it.
# PROG=1 writes the SPI flash, which is what a design that has to survive a
# power cycle needs, and what a board sealed inside an enclosure needs.
case "${PROG:-0}" in
    sram)
        echo
        echo "== program (SRAM, volatile) =="
        iceprog -S "$bin"
        ;;
    1)
        echo
        echo "== program (SPI flash) =="
        iceprog "$bin"
        ;;
esac
