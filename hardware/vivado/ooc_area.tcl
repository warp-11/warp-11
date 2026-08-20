# ooc_area.tcl — report a module's resource usage, out of context, synth-only.
#
# Synthesises one module OOC (no PS, no BD, no implementation) and prints its
# post-synth utilization — DSP / LUT / FF / BRAM. Fast (minutes): the cheap way
# to size lane count against the ZU5EV budget (1248 DSP, ~117k CLB LUT) before
# committing to a full place-route-bitstream.
#
# Usage:
#   vivado -mode batch -source ooc_area.tcl -tclargs <top> <verilog>
set top  [lindex $argv 0]
set src  [lindex $argv 1]
set part xck26-sfvc784-2LV-c

read_verilog $src
synth_design -top $top -part $part -mode out_of_context
puts "================ OOC area: $top ================"
report_utilization
puts "================ end area: $top ================"
