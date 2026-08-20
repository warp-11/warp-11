#*****************************************************************************************
# build_gepiorig_axi.tcl — create + build the GEP IO-rig bitstream (gep_design.md §5).
#
# Reads the emitted RTL from ../build/GepIoRig_axi.v (run
# `./gradlew :examples:gep:emitHardware` first), recreates the block design via
# gepiorig_bd_bd.tcl (Zynq PS + smartconnect + the GepIoRig_axi module reference at
# AXI base 0xB0000000, PL clock 250 MHz), then runs synth + impl + write_bitstream.
#
# The rig's m_axi carries BOTH directions on one interface — AR/R 32-bit unit-queue
# reads and AW/W/B 128-bit result-ring writes — routed through one smartconnect to
# HP0_FPD. 250 MHz is the scaling architecture's target clock: the rig's fake
# engines are near-free logic, so this build also tests the "all-local memories
# close timing" claim (check_timing gates the build if they don't).
#
# Usage:
#   cd hardware/vivado && vivado -mode batch -source build_gepiorig_axi.tcl
#
# Output:
#   gepiorig_axi/gepiorig_axi.runs/impl_1/gepiorig_bd_wrapper.bit
#*****************************************************************************************

set origin_dir [file normalize [file dirname [info script]]]
set proj_name  "gepiorig_axi"
set proj_dir   "${origin_dir}/${proj_name}"

if { [file exists $proj_dir] } {
    puts "removing existing $proj_dir"
    file delete -force $proj_dir
}

create_project $proj_name $proj_dir -part xck26-sfvc784-2LV-c
set_property board_part xilinx.com:kv260_som:part0:1.4 [current_project]
set_property target_language Verilog [current_project]
set_property xpm_libraries {XPM_CDC XPM_FIFO XPM_MEMORY} [current_project]

set src_v [file normalize "${origin_dir}/../build/GepIoRig_axi.v"]
if { ![file isfile $src_v] } {
    error "required source not found: $src_v  (run './gradlew :examples:gep:emitHardware')"
}
# The warp11 emitter doesn't emit Vivado X_INTERFACE_PARAMETER attributes, so
# without help Vivado guesses that s_axi_aclk clocks only s_axi and leaves the
# m_axi master unclocked — it defaults to 100 MHz and FREQ_HZ-mismatches the
# HP smartconnect, failing validate_bd_design. Patch the emitted .v in place to
# tell Vivado s_axi_aclk clocks BOTH interfaces (same fix as build_mandel_axi.tcl).
set patched_v "${proj_dir}/GepIoRig_axi_patched.v"
set fin [open $src_v r]; set body [read $fin]; close $fin
if { [string first "ASSOCIATED_BUSIF s_axi:m_axi" $body] < 0 } {
    set attr "(* X_INTERFACE_PARAMETER = \"ASSOCIATED_BUSIF s_axi:m_axi, ASSOCIATED_RESET s_axi_aresetn\" *)"
    regsub {input wire s_axi_aclk,} $body "$attr\n  input wire s_axi_aclk," body
}
set fout [open $patched_v w]; puts -nonewline $fout $body; close $fout
add_files -norecurse -fileset sources_1 [list $patched_v]

# Recreate the block design (instantiates the GepIoRig_axi module reference).
source [file normalize "${origin_dir}/gepiorig_bd_bd.tcl"]

set top_bd [get_files "${proj_dir}/${proj_name}.srcs/sources_1/bd/gepiorig_bd/gepiorig_bd.bd"]
make_wrapper -fileset sources_1 -files $top_bd -top
add_files -norecurse -fileset sources_1 \
    [file normalize "${proj_dir}/${proj_name}.gen/sources_1/bd/gepiorig_bd/hdl/gepiorig_bd_wrapper.v"]
set_property top "gepiorig_bd_wrapper" [get_filesets sources_1]
update_compile_order -fileset sources_1

launch_runs synth_1 -jobs 4
wait_on_run synth_1
if { [get_property PROGRESS [get_runs synth_1]] != "100%" } {
    error "synth_1 failed: see ${proj_dir}/${proj_name}.runs/synth_1/runme.log"
}

launch_runs impl_1 -to_step write_bitstream -jobs 4
wait_on_run impl_1
if { [get_property PROGRESS [get_runs impl_1]] != "100%" } {
    error "impl_1 failed: see ${proj_dir}/${proj_name}.runs/impl_1/runme.log"
}

# Gate on post-route timing: at 250 MHz a miss here is the locality claim
# failing (or a rig bug) — either way it must be loud, not silent corruption.
source [file normalize "${origin_dir}/check_timing.tcl"]
warp11_assert_timing_met impl_1

set bit [file normalize "${proj_dir}/${proj_name}.runs/impl_1/gepiorig_bd_wrapper.bit"]
if { ![file isfile $bit] } {
    error "build claimed success but bitstream missing: $bit"
}
puts ""
puts "================================================================"
puts "BUILD OK"
puts "bitstream: $bit"
puts "================================================================"
close_project
