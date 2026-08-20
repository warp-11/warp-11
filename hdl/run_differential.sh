#!/usr/bin/env bash
# The differential oracle: the F# Sim produces each design's trace under seeded
# random stimulus, and Verilator executes the emitted Verilog against a generated
# self-checking testbench asserting that exact trace. Any divergence fails.
# Each design project writes its own designs' testbenches; the loop covers both.
#
# When firtool is present a THIRD leg runs: the same design exported as low
# FIRRTL, compiled by firtool, verilated against the same testbench. Our Sim and
# firtool are strangers to each other, so agreement between them is the strongest
# available answer to "are we inventing IR?". firtool is optional on purpose —
# nothing a user does needs it, and its absence only narrows the oracle.
#
# Because it is optional it could go missing without anyone noticing, which is
# the failure this repo has had before. So the leg's presence and its testbench
# count are printed at the end: read those, not the exit code.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH"

out=${1:-/tmp/warp11-fsharp-diff}

# firtool's own codegen indexes an array with a conditional, which Verilator
# lints as WIDTHEXPAND. It is a style of the generator, not a divergence, so the
# suppression applies to the firtool leg only.
firtool_lint=(-Wno-DECLFILENAME -Wno-UNUSEDSIGNAL -Wno-WIDTHEXPAND)

# Where the time goes, measured 2026-08-16: firtool compiles the whole
# MandelPod hierarchy in 0.03 s and running a built model is instant, so ~99% of
# this script is Verilator turning each testbench into a C++ binary, 5.5 s a
# design. The third leg doubles the number of those compiles, which is the whole
# reason the run got slower.
#
# `-j 0` halves each compile (2.6 s measured) and is NOT used: on Verilator
# 5.020 it intermittently dies with "Internal Error: attempted to destroy locked
# Thread Pool" — twice in two full runs, never reproducible on the design it
# killed. An oracle that fails at random is worth less than a slow one. The
# speedup to reach for instead is running this loop's *designs* in parallel,
# each Verilator single-threaded, which never touches that code path.

# Named explicitly rather than found by mutating PATH, so this cannot pick up
# something else called firtool and cannot change what any other tool resolves.
# `tools/install-firtool.sh` puts a pinned one here.
firtool=${FIRTOOL:-$HOME/tools/bin/firtool}

# **Off by default**, because it doubles the run: one Verilator binary per
# design becomes two, and Verilator is ~99% of the cost. Turn it on when the
# thing it can speak to has moved — `Ir.fs`, `Firrtl.fs`, `Verilog.fs`, `Sim.fs`
# — or before a bitstream. It found a real emitter bug on first contact, so it
# is worth its 20 minutes occasionally and not worth it on every iteration.
#
#     FIRTOOL_LEG=1 ./run_differential.sh
have_firtool=0
if [ "${FIRTOOL_LEG:-0}" != "0" ]; then
    if [ -x "$firtool" ]; then
        have_firtool=1
    else
        echo "FIRTOOL_LEG is set but there is no firtool at $firtool"
        echo "  tools/install-firtool.sh installs a pinned one"
        exit 1
    fi
fi

ours=0
theirs=0
no_fir=0
foreign=0
completed=0

# The counts print from a trap, not from the bottom of the script, because the
# failure this runner has actually had is *stopping early and looking fine*: a
# `set -e` abort mid-loop left a truncated log, no summary, and an exit status
# that read as success. Printing on EXIT means a partial run says so in its own
# voice — and the ALL DIFF PASS line is gated on reaching the end, so its absence
# is the signal. Read the counts, and check that last line exists.
summary() {
    local status=$?
    echo
    echo "our Verilog:      $ours testbenches"

    if [ "$have_firtool" -eq 1 ]; then
        echo "firtool Verilog:  $theirs testbenches ($no_fir design(s) have no .fir — reasons above)"
        echo "foreign .fir:     $foreign read by our reader, judged by firtool"
    else
        echo "firtool Verilog:  not run — set FIRTOOL_LEG=1 to add the third leg"
    fi

    if [ "$completed" -eq 1 ]; then
        echo "ALL DIFF PASS"
    else
        echo "INCOMPLETE — the run stopped early (status $status). The counts above are"
        echo "what it got through, not what exists. Scroll up for the failing step."
    fi
}

trap summary EXIT

for proj in Warp11.Tutorial.App Warp11.Designs Warp11.Mandelbrot Warp11.GoL.App Warp11.Gep Warp11.Effects; do
    sub="$out/$proj"
    mkdir -p "$sub"
    dotnet run --project "$proj/$proj.fsproj" -- diff "$sub"

    for tb in "$sub"/*_diff_tb.v; do
        top=$(basename "$tb" .v)
        design=${top%_diff_tb}
        mdir="$sub/obj_$top"
        rm -rf "$mdir"
        # Kept rather than discarded: a Verilator failure used to abort the
        # run under `set -e` with nothing printed at all, which is a bad way to
        # find out anything.
        if ! verilator --binary -Wno-DECLFILENAME --Mdir "$mdir" --top-module "$top" \
            "$tb" "$sub/modules.v" > "$mdir.log" 2>&1; then
            echo "verilator failed on our Verilog for $design:"
            grep -iE "%Error|Error:" "$mdir.log" | head -10
            exit 1
        fi
        "$mdir/V$top"
        ours=$((ours + 1))

        fir="$sub/$design.fir"
        if [ "$have_firtool" -eq 0 ]; then
            continue
        elif [ ! -f "$fir" ]; then
            # The export said why, in the writer's own output above.
            no_fir=$((no_fir + 1))
            continue
        fi

        ftv="$sub/${design}_firtool.v"
        ftdir="$sub/obj_ft_$design"
        rm -rf "$ftdir"
        # firtool exits 0 on a parse error, so its own status proves nothing and
        # the real check is that the Verilog it claims to have written exists.
        # But a *verification* error (say `cat` on operands that disagree about
        # signedness) exits non-zero, and under `set -e` that killed the run
        # before this guard could say so — silently, with no summary. So the
        # status is explicitly discarded and the guard below is left to speak.
        "$firtool" "$fir" --disable-all-randomization -o "$ftv" > "$sub/$design.firtool.log" 2>&1 || true
        if ! [ -s "$ftv" ]; then
            echo "firtool produced no Verilog for $design:"
            grep "error:" "$sub/$design.firtool.log" | head -5
            exit 1
        fi
        if ! verilator --binary "${firtool_lint[@]}" --Mdir "$ftdir" --top-module "$top" \
            "$tb" "$ftv" > "$ftdir.log" 2>&1; then
            echo "verilator failed on firtool's Verilog for $design:"
            grep -iE "%Error|Error:" "$ftdir.log" | head -10
            exit 1
        fi
        "$ftdir/V$top"
        theirs=$((theirs + 1))
    done
done

# FIRRTL nobody here wrote, in `firrtl-foreign/`: our reader imports it, our Sim
# produces the trace, and Verilator runs *firtool's* Verilog from the same source
# text against that trace. The round-trip check in Warp11.Designs proves our
# reader and our writer agree; only this can catch a construct we misread, since
# the second opinion is not ours. It found one on the first file: FIRRTL's `sub`
# on unsigned operands is UInt<w+1>, not signed.
if [ "$have_firtool" -eq 1 ]; then
    fsub="$out/foreign"
    mkdir -p "$fsub"
    dotnet run --project Warp11.Designs/Warp11.Designs.fsproj -- firrtl-foreign firrtl-foreign "$fsub"

    for fir in "$fsub"/*.fir; do
        design=$(basename "$fir" .fir)
        top="${design}_diff_tb"
        fdir="$fsub/obj_$design"
        rm -rf "$fdir"
        # Same reason as the leg above: discard the status, let the guard speak.
        "$firtool" "$fir" --disable-all-randomization -o "$fsub/${design}_firtool.v" > "$fdir.fir.log" 2>&1 || true

        if ! [ -s "$fsub/${design}_firtool.v" ]; then
            echo "firtool produced no Verilog for foreign $design:"
            grep "error:" "$fdir.fir.log" | head -5
            exit 1
        fi

        if ! verilator --binary "${firtool_lint[@]}" --Mdir "$fdir" --top-module "$top" \
            "$fsub/$top.v" "$fsub/${design}_firtool.v" > "$fdir.log" 2>&1; then
            echo "verilator failed on foreign $design:"
            grep -iE "%Error|Error:" "$fdir.log" | head -10
            exit 1
        fi

        "$fdir/V$top"
        foreign=$((foreign + 1))
    done
fi

# Reaching here is the only thing that earns the ALL DIFF PASS line; the counts
# themselves are printed by the EXIT trap either way.
completed=1
