# build_audio_tone_axi.tcl
#
# From-scratch reproducible build of the GameOfLife64 KV260 bitstream.
#
# Usage:
#   cd hardware/vivado
#   vivado -mode batch -source build_audio_tone_axi.tcl
#
# Inputs (must exist before running):
#   ../build/AudioToneAxi.v     — emitted by `dotnet run --project Warp11.Effects -- hardware <repo-root>`
#   ./audio_tone_bd_bd.tcl                — BD recreation script (sibling file)
#
# Output:
#   ./audio_tone_axi/audio_tone_axi.runs/impl_1/audio_tone_bd_wrapper.bit
#
# Project + BD layout mirrors the counter_axi reference. Differences from
# counter:
#   - Project name: audio_tone_axi
#   - BD name:      audio_tone_bd
#   - DUT sources:  AudioToneAxi.v (single emitted file: wrapper + i2sMaster
#                   + toneGenerator + i2sTx + AudioToneSlave)
#   - Constraints:  audio_tone_pins.xdc (J2 Pmod codec pins)
#   - Address:      0xB0000000 / 0x1000 (set in audio_tone_bd_bd.tcl, sufficient
#                   for the 4 KB GoL register map; CELL_ROW ends at 0x4FF)
#
# After build, run `cd ../xmutil && ./package_audio_tone.sh` (TODO: clone of
# package_counter.sh) to wrap into an xmutil-loadable app.

set origin_dir [file normalize [file dirname [info script]]]
set proj_name  "audio_tone_axi"
set proj_dir   "${origin_dir}/${proj_name}"

# Wipe any previous attempt so the build is truly from-scratch.
if { [file exists $proj_dir] } {
    puts "removing existing $proj_dir"
    file delete -force $proj_dir
}

create_project $proj_name $proj_dir -part xck26-sfvc784-2LV-c
set_property board_part xilinx.com:kv260_som:part0:1.4 [current_project]
set_property target_language Verilog [current_project]
set_property xpm_libraries {XPM_CDC XPM_FIFO XPM_MEMORY} [current_project]

# Add the DSL-emitted core + hand-written AXI wrapper to sources_1.
set rtl_files [list \
    [file normalize "${origin_dir}/../build/AudioToneAxi.v"] \
]
foreach f $rtl_files {
    if { ![file isfile $f] } {
        error "required source not found: $f"
    }
}
add_files -norecurse -fileset sources_1 $rtl_files

# Physical pin constraints for the J2 Pmod codec pins (mclk/lrclk/sclk/sdin).
set xdc [file normalize "${origin_dir}/audio_tone_pins.xdc"]
if { ![file isfile $xdc] } {
    error "required constraints not found: $xdc"
}
add_files -norecurse -fileset constrs_1 $xdc

# Recreate the block design from the captured Tcl. The script creates the
# BD inside the current project, validates it, and saves it.
source [file normalize "${origin_dir}/audio_tone_bd_bd.tcl"]

# Generate the HDL wrapper around the BD and use it as the top. Target the
# top-level audio_tone_bd.bd by path — `get_files -filter {FILE_TYPE == "Block
# Designs"}` also picks up the smartconnect's internal sub-BD, which
# make_wrapper can't wrap and aborts on.
set top_bd [get_files "${proj_dir}/${proj_name}.srcs/sources_1/bd/audio_tone_bd/audio_tone_bd.bd"]
make_wrapper -fileset sources_1 -files $top_bd -top
add_files -norecurse -fileset sources_1 \
    [file normalize "${proj_dir}/${proj_name}.gen/sources_1/bd/audio_tone_bd/hdl/audio_tone_bd_wrapper.v"]
set_property top "audio_tone_bd_wrapper" [get_filesets sources_1]
update_compile_order -fileset sources_1

# Synthesize, implement, write bitstream. Each launch_runs blocks because of
# the wait_on_run; if anything errors, the build aborts with non-zero exit.
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

# Gate: refuse to ship a timing-failing bitstream — Vivado won't on its own.
source [file normalize "${origin_dir}/check_timing.tcl"]
warp11_assert_timing_met impl_1

set bit [file normalize "${proj_dir}/${proj_name}.runs/impl_1/audio_tone_bd_wrapper.bit"]
if { ![file isfile $bit] } {
    error "build claimed success but bitstream missing: $bit"
}
puts ""
puts "================================================================"
puts "BUILD OK"
puts "bitstream: $bit"
puts "================================================================"
close_project
