# ooc_fmax.tcl — measure a leaf module's achievable clock, out of context.
#
# Synthesises + places + routes one module OOC at a tight target clock and
# reports the achieved Fmax from the post-route worst setup slack. I/O is
# constrained to 0 external delay (the engine is fed by registered pool logic in
# the real design), so the reported WNS reflects the internal reg-to-reg
# datapath — the multiply cone that sets the GEP engine's clock.
#
# Usage:
#   vivado -mode batch -source ooc_fmax.tcl -tclargs <top> <verilog> <period_ns>
set top    [lindex $argv 0]
set src    [lindex $argv 1]
set period [lindex $argv 2]
set part   xck26-sfvc784-2LV-c

read_verilog $src
synth_design -top $top -part $part -mode out_of_context
create_clock -name clk -period $period [get_ports clk]
set_input_delay  0 -clock clk [get_ports -filter {DIRECTION == IN && NAME != clk}]
set_output_delay 0 -clock clk [all_outputs]
opt_design
place_design
route_design

set wns      [get_property SLACK [get_timing_paths -setup -max_paths 1 -nworst 1]]
set achieved [expr {$period - $wns}]
set fmax     [expr {1000.0 / $achieved}]
puts "================ OOC Fmax: $top ================"
puts [format "target %.3f ns   WNS %.3f ns   achieved period %.3f ns   Fmax %.1f MHz" \
        $period $wns $achieved $fmax]
puts "worst setup path:"
report_timing -setup -max_paths 1 -nworst 1
puts "================================================"
