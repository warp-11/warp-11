# build_mandelframe_axi.tcl
#
# From-scratch reproducible build of the Mandelbrot accelerator KV260
# bitstream. Mirrors build_gol_axi.tcl — the only structural differences
# are name substitutions (gol_axi → mandelframe_axi, gol_bd → mandelframe_bd,
# GameOfLife64_axi → MandelFrameAxi) and the address range (2 MB instead
# of 4 KB, because the framebuffer at +1 MB pushes the total above the
# default 64 KB slot).
#
# Usage:
#   cd hardware/vivado
#   vivado -mode batch -source build_mandelframe_axi.tcl
#
# Inputs (must exist before running):
#   ../build/MandelFrameAxi.v      — emitted by `dotnet run -- hardware ../..`
#                                    (single file containing the wrapper
#                                    plus all transitively-referenced
#                                    submodules — engines, slave, etc.)
#   ./mandelframe_bd_bd.tcl             — BD recreation script (sibling file)
#
# Output:
#   ./mandelframe_axi/mandelframe_axi.runs/impl_1/mandelframe_bd_wrapper.bit
#
# After build, run `cd ../xmutil && ./package_mandel-frame.sh` to wrap the
# bitstream + DT overlay into an xmutil-loadable app, then deploy with
# `./gradlew :driver:deployBitstreamToBoard -Pkv260.app=mandelframe`.

set origin_dir [file normalize [file dirname [info script]]]
set proj_name  "mandelframe_axi"
set proj_dir   "${origin_dir}/${proj_name}"

if { [file exists $proj_dir] } {
    puts "removing existing $proj_dir"
    file delete -force $proj_dir
}

create_project $proj_name $proj_dir -part xck26-sfvc784-2LV-c
set_property board_part xilinx.com:kv260_som:part0:1.4 [current_project]
set_property target_language Verilog [current_project]
set_property xpm_libraries {XPM_CDC XPM_FIFO XPM_MEMORY} [current_project]

set rtl_files [list \
    [file normalize "${origin_dir}/../build/MandelFrameAxi.v"] \
]
foreach f $rtl_files {
    if { ![file isfile $f] } {
        error "required source not found: $f  (did you run 'dotnet run -- hardware ../..'?)"
    }
}

# The warp11 emitter doesn't yet emit Vivado X_INTERFACE_PARAMETER
# attributes, so without help Vivado guesses that s_axi_aclk clocks
# only the s_axi interface — leaving m_axi unassociated, which a
# downstream smartconnect rejects with a FREQ_HZ mismatch. Decorate
# the emitted .v in place to tell Vivado that s_axi_aclk clocks BOTH
# interfaces. Idempotent (skipped if the attribute is already present).
set src_v   [file normalize "${origin_dir}/../build/MandelFrameAxi.v"]
set patched_v "${proj_dir}/MandelFrameAxi_patched.v"
file mkdir $proj_dir
set fin  [open $src_v r]
set body [read $fin]; close $fin
if { [string first "ASSOCIATED_BUSIF s_axi:m_axi" $body] < 0 } {
    set attr "(* X_INTERFACE_PARAMETER = \"ASSOCIATED_BUSIF s_axi:m_axi, ASSOCIATED_RESET s_axi_aresetn\" *)"
    regsub {input s_axi_aclk,} $body "$attr\n  input s_axi_aclk," body
}
set fout [open $patched_v w]; puts -nonewline $fout $body; close $fout
set rtl_files [list $patched_v]
add_files -norecurse -fileset sources_1 $rtl_files

source [file normalize "${origin_dir}/mandelframe_bd_bd.tcl"]

set top_bd [get_files "${proj_dir}/${proj_name}.srcs/sources_1/bd/mandelframe_bd/mandelframe_bd.bd"]
make_wrapper -fileset sources_1 -files $top_bd -top
add_files -norecurse -fileset sources_1 \
    [file normalize "${proj_dir}/${proj_name}.gen/sources_1/bd/mandelframe_bd/hdl/mandelframe_bd_wrapper.v"]
set_property top "mandelframe_bd_wrapper" [get_filesets sources_1]
update_compile_order -fileset sources_1

launch_runs synth_1 -jobs 4
wait_on_run synth_1
if { [get_property PROGRESS [get_runs synth_1]] != "100%" } {
    error "synth_1 failed: see ${proj_dir}/${proj_name}.runs/synth_1/runme.log"
}

# Enable physical optimization (register replication / retiming / critical-path
# fixup) after place and after route — reclaims clock on routing-dominated
# designs like this one with no RTL change.
set_property STEPS.PHYS_OPT_DESIGN.IS_ENABLED true [get_runs impl_1]
set_property STEPS.POST_ROUTE_PHYS_OPT_DESIGN.IS_ENABLED true [get_runs impl_1]

launch_runs impl_1 -to_step write_bitstream -jobs 4
wait_on_run impl_1
if { [get_property PROGRESS [get_runs impl_1]] != "100%" } {
    error "impl_1 failed: see ${proj_dir}/${proj_name}.runs/impl_1/runme.log"
}

# Gate: refuse to ship a timing-failing bitstream — Vivado won't on its own.
source [file normalize "${origin_dir}/check_timing.tcl"]
warp11_assert_timing_met impl_1

set bit [file normalize "${proj_dir}/${proj_name}.runs/impl_1/mandelframe_bd_wrapper.bit"]
if { ![file isfile $bit] } {
    error "build claimed success but bitstream missing: $bit"
}
puts ""
puts "================================================================"
puts "BUILD OK"
puts "bitstream: $bit"
puts "================================================================"
close_project
