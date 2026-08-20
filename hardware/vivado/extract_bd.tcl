# extract_bd.tcl
#
# One-time extraction script: opens an existing Vivado project (built via the
# GUI) and dumps a self-contained regeneration script + a BD recreation
# script. Run once per legacy project so future bitstreams are
# `vivado -mode batch -source build_<name>.tcl` instead of GUI clicks.
#
# Usage (from the project directory containing the .xpr):
#
#   cd hardware/vivado/counter_axi
#   vivado -mode batch -source ../extract_bd.tcl
#
# Outputs (written to the project's parent dir, hardware/vivado/):
#
#   build_<project>.tcl  — recreates the project (sources, IP, board part)
#   <bd_name>_bd.tcl     — recreates the block design
#
# After extraction, the legacy project dir can be deleted; the two scripts
# are the source of truth.

set xpr_files [glob -nocomplain *.xpr]
if {[llength $xpr_files] != 1} {
    error "expected exactly one .xpr in [pwd]; found: $xpr_files"
}
set xpr [lindex $xpr_files 0]
set project_name [file rootname $xpr]

open_project $xpr

# Dump the BD recreation script.
set bd_files [get_files -filter {FILE_TYPE == "Block Designs"}]
if {[llength $bd_files] != 1} {
    error "expected exactly one BD; found: $bd_files"
}
open_bd_design [lindex $bd_files 0]
set bd_name [current_bd_design]
set bd_tcl_out [file normalize "../${bd_name}_bd.tcl"]
write_bd_tcl -force -include_layout $bd_tcl_out
puts "wrote $bd_tcl_out"

# Dump the project recreation script. `-paths_relative_to` makes the script
# portable so paths are anchored to wherever the .tcl ends up living.
set project_tcl_out [file normalize "../build_${project_name}.tcl"]
write_project_tcl \
    -force \
    -no_copy_sources \
    -use_bd_files \
    -paths_relative_to [file dirname $project_tcl_out] \
    $project_tcl_out
puts "wrote $project_tcl_out"

close_project
puts "done. Generated scripts live in [file dirname $project_tcl_out]."
