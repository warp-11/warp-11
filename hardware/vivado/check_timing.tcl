# check_timing.tcl — shared post-implementation timing gate for warp11 app builds.
#
# Vivado does NOT fail a run on unmet timing: `write_bitstream` proceeds happily
# even at WNS = -62 ns, and the run still reports PROGRESS = 100%. That bit us
# once — the AudioBatch DSP chain shipped a timing-failing bitstream (a single
# 183-logic-level combinational cone, WNS -62 ns) that corrupted audio on the
# KV260 while passing every cycle-accurate sim. The Kotlin simulator and
# Verilator have no timing model, so post-route WNS is the ONLY place this whole
# class of bug shows up. Source this file and call `warp11_assert_timing_met`
# right after `wait_on_run impl_1`, before claiming the build succeeded.
#
# Override for an intentional marginal / inspection build:
#   WARP11_ALLOW_BAD_TIMING=1 vivado -mode batch -source build_<app>_axi.tcl
proc warp11_assert_timing_met {run} {
    open_run $run
    set setupPath [get_timing_paths -setup -max_paths 1 -nworst 1]
    set holdPath  [get_timing_paths -hold  -max_paths 1 -nworst 1]
    if { $setupPath eq "" || $holdPath eq "" } {
        puts "WARNING: no timing paths found for $run — skipping timing gate."
        return
    }
    set wns [get_property SLACK $setupPath]
    set whs [get_property SLACK $holdPath]
    puts "================================================================"
    puts "post-route timing ($run): WNS=$wns ns   WHS=$whs ns"
    puts "================================================================"
    if { ($wns < 0 || $whs < 0) && ![info exists ::env(WARP11_ALLOW_BAD_TIMING)] } {
        error "TIMING NOT MET (WNS=$wns ns, WHS=$whs ns) — refusing to ship a\
 timing-failing bitstream (it will malfunction on hardware even though the\
 cycle sims pass). Pipeline the failing path or lower the PL clock. To build\
 anyway for inspection, set WARP11_ALLOW_BAD_TIMING=1."
    }
}
