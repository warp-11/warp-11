/// The build generator: a design on a board, as the directory its toolchain
/// builds from. Everything in it is derived — from the top's port list, the
/// register map's aperture and the board's axes — so the block design, the
/// constraints, the overlay and the flow cannot disagree with the design or
/// with each other, which is what the hand-kept copies under `hardware/`
/// could not promise.
///
/// A build directory is self-contained: the flow scripts are written beside
/// the design rather than referenced from the repository, so it builds
/// anywhere the tools are. `notes/BUILD.md` is the plan.
module Warp11.Build

open Warp11.BoardTop

/// One of the top's pin ports and where the board puts it.
type Pinned =
    { port: string
      input: bool
      width: int
      pin: Pin }

/// What `write` produced: the directory, every file in it, and the command
/// that builds it.
type BuildOutput =
    { dir: string
      files: string list
      run: string }

let private portsOf (m: ModuleDef) =
    m.decls
    |> List.choose (function
        | Input(n, t) -> Some(n, true, t.Width)
        | Output(n, t) -> Some(n, false, t.Width)
        | _ -> None)

/// A port that is a bus rather than a pin: the AXI-Lite slave's and the
/// masters' nets go into the block design, never to a package pin.
let private isBusPort (n: string) =
    n.StartsWith "s_axi_" || n.StartsWith "m_axi_"

/// The pin gate: every pin port of the top lands on a pin the board's
/// connectors name, and a connector the top uses is used whole — LEDs aside. Refused by
/// name, before any tool runs — nextpnr places an unpinned port wherever it
/// likes and carries on with a warning, and Vivado's version of the same
/// mistake is a constraints warning nobody reads.
let pinGate (t: BoardTop) : Pinned list =
    let board = t.board
    let ports = portsOf t.top |> List.filter (fun (n, _, _) -> not (isBusPort n))
    let table = board.connectors |> List.collect (fun c -> c.pins)

    let unpinned =
        ports
        |> List.filter (fun (n, _, _) -> not (table |> List.exists (fun (p, _) -> p = n)))
        |> List.map (fun (n, _, _) -> n)

    if not unpinned.IsEmpty then
        let names = String.concat ", " unpinned
        failwith $"{t.name} on the {board.name}: no pin for {names} — the board's connectors do not place them"

    // LEDs are the exception: each is its own device, and a design lighting
    // one of a board's two is using what it asked for.
    for c in board.connectors |> List.filter (fun c -> c.role <> Leds) do
        let present, missing =
            c.pins |> List.map fst |> List.partition (fun p -> ports |> List.exists (fun (n, _, _) -> n = p))

        if not present.IsEmpty && not missing.IsEmpty then
            let names = String.concat ", " missing
            failwith $"{t.name} on the {board.name}: the {c.role} connector is used, but the top has no {names}"

    [ for n, input, width in ports ->
          { port = n
            input = input
            width = width
            pin = table |> List.find (fun (p, _) -> p = n) |> snd } ]

/// `GainPatchAxi` → `gain_patch_axi`.
let snakeOf (name: string) =
    name
    |> Seq.mapi (fun i c ->
        if System.Char.IsUpper c && i > 0 then
            $"_{System.Char.ToLower c}"
        else
            string (System.Char.ToLower c))
    |> String.concat ""

/// The PS slave ports an AXI master can land on, as Vivado names their
/// halves: the `SAXIGPn` the configuration knows, the DDR segment the
/// address map knows, the clock pin the block design wires.
let private psSlavePort (name: string) =
    match name with
    | "S_AXI_HPC0_FPD" -> 0, "HPC0_DDR_LOW", "saxihpc0_fpd_aclk"
    | "S_AXI_HPC1_FPD" -> 1, "HPC1_DDR_LOW", "saxihpc1_fpd_aclk"
    | "S_AXI_HP0_FPD" -> 2, "HP0_DDR_LOW", "saxihp0_fpd_aclk"
    | "S_AXI_HP1_FPD" -> 3, "HP1_DDR_LOW", "saxihp1_fpd_aclk"
    | "S_AXI_HP2_FPD" -> 4, "HP2_DDR_LOW", "saxihp2_fpd_aclk"
    | "S_AXI_HP3_FPD" -> 5, "HP3_DDR_LOW", "saxihp3_fpd_aclk"
    | other -> failwith $"no PS slave port called '{other}' — one of S_AXI_HPC0_FPD, S_AXI_HPC1_FPD, S_AXI_HP0_FPD … S_AXI_HP3_FPD"

/// The smallest address range a smartconnect segment can be: 4 KB, however
/// small the aperture.
let private apertureRange (m: RegMap) = max 4096 (1 <<< m.apertureAddrWidth)

/// Vivado's `X_INTERFACE_PARAMETER` on the clock port, so it clocks the
/// master as well as the slave. Without it Vivado guesses `s_axi_aclk`
/// clocks only `s_axi`, leaves `m_axi` at a default 100 MHz, and
/// `validate_bd_design` fails on a frequency mismatch that says nothing
/// about the cause. The emitter has no attribute surface, so it is put on
/// the emitted text here, once, where the master is known to exist.
let private withBusInterfaceAttribute (verilog: string) =
    let attribute = "(* X_INTERFACE_PARAMETER = \"ASSOCIATED_BUSIF s_axi:m_axi, ASSOCIATED_RESET s_axi_aresetn\" *) "
    let marker = "input s_axi_aclk"
    let at = verilog.IndexOf marker

    if at < 0 then
        failwith "the top has no s_axi_aclk to attribute"

    verilog.Insert(at, attribute)

/// Whether the top masters the host's memory — a batch top, or a converter
/// top with a recorder bound there. Asked of the ports, since that is what
/// the block design has to wire, whichever binding put them there.
let private hasMaster (t: BoardTop) =
    t.top.decls |> List.exists (function
        | Output(n, _) -> n = "m_axi_awaddr" || n = "m_axi_araddr"
        | _ -> false)

/// `100`, `99.999001`, `166.666672`: the megahertz the PS configuration
/// takes.
let private megahertz (hz: int) = (float hz / 1e6).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)

// ---------------------------------------------------------------------------
// The Vivado flow: a block design around the top, constraints, the build.

/// The audio oscillator as the flow needs it — rate and pin — present only
/// when the top actually carries the audio domain: a board may offer the
/// oscillator to a design whose mapping never put anything on it.
let private audioClockOf (t: BoardTop) : (int * Pin) option =
    match t.board.audioClockHz with
    | Some hz when t.top.foreignDomains |> List.exists (fun fd -> fd.domainName = "audio") ->
        match connectorFor AudioClockIn t.board with
        | Some [ (_, pin) ] -> Some(hz, pin)
        | _ -> failwith $"{t.board.name}: an audio clock rate with no AudioClockIn pin"
    | _ -> None

let private blockDesignTcl (t: BoardTop) (pinned: Pinned list) (psClock: int) =
    let cell = $"{t.name}_0"
    let bd = $"{snakeOf t.name}_bd"

    let baseAddr =
        match t.board.host with
        | AxiLiteAt b -> b
        | _ -> failwith "a block design wants an AXI-Lite host"

    let memory =
        match hasMaster t, t.board.hostMemory with
        | true, Some m -> Some m
        | true, None -> failwith "a top that masters the host's memory, on a board with none"
        | false, _ -> None

    let psConfig =
        [ "CONFIG.PSU__USE__M_AXI_GP0 {1}"
          "CONFIG.PSU__USE__M_AXI_GP1 {1}"
          "CONFIG.PSU__USE__M_AXI_GP2 {0}"
          "CONFIG.PSU__USE__IRQ0 {1}"
          "CONFIG.PSU__CRL_APB__PL0_REF_CTRL__SRCSEL {IOPLL}"
          $"CONFIG.PSU__CRL_APB__PL0_REF_CTRL__FREQMHZ {{{megahertz t.board.fabricHz}}}" ]
        @ (match memory with
           | Some m ->
               let gp, _, _ = psSlavePort m.port
               [ $"CONFIG.PSU__USE__S_AXI_GP%d{gp} {{1}}"; $"CONFIG.PSU__SAXIGP%d{gp}__DATA_WIDTH {{%d{m.width}}}" ]
           | None -> [])

    let configLines = psConfig |> List.map (fun l -> $"    {l} \\") |> String.concat "\n"

    // The audio oscillator arrives as an external clock port, and its domain
    // gets its own proc_sys_reset — the one reset synchroniser the top owns,
    // async-asserted with the PS reset and released synchronously to the
    // audio clock. `peripheral_reset` is the active-high form, which is what
    // the conjured `audio_rst` pin is.
    let audioCells, audioNets, audioReset =
        match audioClockOf t with
        | Some (hz, _) ->
            $"set rst_audio [ create_bd_cell -type ip -vlnv xilinx.com:ip:proc_sys_reset:5.0 rst_audio ]\ncreate_bd_port -dir I -type clk -freq_hz %d{hz} audio_clk_in",

            $"connect_bd_net -net audio_clk_in [get_bd_ports audio_clk_in] \\\n  [get_bd_pins {cell}/audio_clk] \\\n  [get_bd_pins rst_audio/slowest_sync_clk]\nconnect_bd_net -net rst_audio_peripheral_reset [get_bd_pins rst_audio/peripheral_reset] [get_bd_pins {cell}/audio_rst]",

            " \\\n  [get_bd_pins rst_audio/ext_reset_in]"
        | None -> "", "", ""

    let ports =
        [ for p in pinned ->
              let dir = if p.input then "I" else "O"
              $"create_bd_port -dir {dir} {p.port}\nconnect_bd_net [get_bd_pins {cell}/{p.port}] [get_bd_ports {p.port}]" ]
        |> String.concat "\n"

    let masterCells, masterNets, masterClocks, masterResets, masterAddress =
        match memory with
        | Some m ->
            let _, segment, aclk = psSlavePort m.port

            "set axi_smc_hp [ create_bd_cell -type ip -vlnv xilinx.com:ip:smartconnect:1.0 axi_smc_hp ]\nset_property -dict [list CONFIG.NUM_SI {1} CONFIG.NUM_MI {1}] $axi_smc_hp",
            $"connect_bd_intf_net -intf_net {cell}_m_axi [get_bd_intf_pins {cell}/m_axi] [get_bd_intf_pins axi_smc_hp/S00_AXI]\nconnect_bd_intf_net -intf_net axi_smc_hp_M00_AXI [get_bd_intf_pins axi_smc_hp/M00_AXI] [get_bd_intf_pins zynq_ultra_ps_e_0/{m.port}]",
            $" \\\n  [get_bd_pins axi_smc_hp/aclk] \\\n  [get_bd_pins zynq_ultra_ps_e_0/{aclk}]",
            " \\\n  [get_bd_pins axi_smc_hp/aresetn]",
            $"assign_bd_address -offset 0x00000000 -range 0x80000000 -target_address_space [get_bd_addr_spaces {cell}/m_axi] [get_bd_addr_segs zynq_ultra_ps_e_0/SAXIGP%d{let gp, _, _ = psSlavePort m.port in gp}/{segment}] -force"
        | None -> "", "", "", "", ""

    $"""# {bd}.tcl — the block design around {t.name}, generated by Warp11.Build.
# The processing system is the board part's preset; everything else is
# derived from the design: its ports, its register map, its master.

create_bd_design {bd}
current_bd_instance [get_bd_cells /]

set zynq_ultra_ps_e_0 [ create_bd_cell -type ip -vlnv xilinx.com:ip:zynq_ultra_ps_e:3.5 zynq_ultra_ps_e_0 ]
apply_bd_automation -rule xilinx.com:bd_rule:zynq_ultra_ps_e -config {{apply_board_preset "1"}} $zynq_ultra_ps_e_0
set_property -dict [list \
{configLines}
  ] $zynq_ultra_ps_e_0

set {cell} [ create_bd_cell -type module -reference {t.name} {cell} ]

set axi_smc [ create_bd_cell -type ip -vlnv xilinx.com:ip:smartconnect:1.0 axi_smc ]
set_property CONFIG.NUM_SI {{1}} $axi_smc
{masterCells}

set rst_ps8_0 [ create_bd_cell -type ip -vlnv xilinx.com:ip:proc_sys_reset:5.0 rst_ps8_0 ]
{audioCells}

# pl_ps_irq0 stays driven even where nothing raises it.
set irq_concat [ create_bd_cell -type ip -vlnv xilinx.com:ip:xlconcat:2.1 irq_concat ]
set_property -dict [list CONFIG.NUM_PORTS {{8}} CONFIG.IN0_WIDTH {{1}}] $irq_concat

connect_bd_intf_net -intf_net axi_smc_M00_AXI [get_bd_intf_pins axi_smc/M00_AXI] [get_bd_intf_pins {cell}/s_axi]
connect_bd_intf_net -intf_net zynq_ultra_ps_e_0_M_AXI_HPM1_FPD [get_bd_intf_pins zynq_ultra_ps_e_0/M_AXI_HPM1_FPD] [get_bd_intf_pins axi_smc/S00_AXI]
{masterNets}

connect_bd_net -net rst_ps8_0_peripheral_aresetn [get_bd_pins rst_ps8_0/peripheral_aresetn] \
  [get_bd_pins {cell}/s_axi_aresetn] \
  [get_bd_pins axi_smc/aresetn]{masterResets}
connect_bd_net -net zynq_ultra_ps_e_0_pl_clk0 [get_bd_pins zynq_ultra_ps_e_0/pl_clk0] \
  [get_bd_pins zynq_ultra_ps_e_0/maxihpm1_fpd_aclk] \
  [get_bd_pins zynq_ultra_ps_e_0/maxihpm0_fpd_aclk] \
  [get_bd_pins axi_smc/aclk] \
  [get_bd_pins {cell}/s_axi_aclk] \
  [get_bd_pins rst_ps8_0/slowest_sync_clk]{masterClocks}
connect_bd_net -net zynq_ultra_ps_e_0_pl_resetn0 [get_bd_pins zynq_ultra_ps_e_0/pl_resetn0] \
  [get_bd_pins rst_ps8_0/ext_reset_in]{audioReset}
connect_bd_net -net irq_concat_dout [get_bd_pins irq_concat/dout] [get_bd_pins zynq_ultra_ps_e_0/pl_ps_irq0]
{audioNets}

{ports}

assign_bd_address -offset 0x%08X{baseAddr} -range 0x%08X{apertureRange t.map} -target_address_space [get_bd_addr_spaces zynq_ultra_ps_e_0/Data] [get_bd_addr_segs {cell}/s_axi/reg0] -force
{masterAddress}

validate_bd_design
save_bd_design
"""

let private xdc (pinned: Pinned list) (audio: (int * Pin) option) =
    [ for p in pinned ->
          let standard = p.pin.standard |> Option.map (fun s -> $" IOSTANDARD {s}") |> Option.defaultValue ""
          $"set_property -dict {{PACKAGE_PIN {p.pin.pin}{standard}}} [get_ports {p.port}]"
      match audio with
      | Some (hz, pin) ->
          let standard = pin.standard |> Option.map (fun s -> $" IOSTANDARD {s}") |> Option.defaultValue ""

          let period =
              (1e9 / float hz).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)

          yield $"set_property -dict {{PACKAGE_PIN {pin.pin}{standard}}} [get_ports audio_clk_in]"
          yield $"create_clock -name audio_clk -period {period} [get_ports audio_clk_in]"
          // Genuinely asynchronous to the fabric clock, and every crossing is
          // a synchroniser or an async FIFO — checked at elaboration — so the
          // paths between the two are false by construction.
          yield "set_clock_groups -asynchronous -group [get_clocks audio_clk]"
      | None -> () ]
    |> String.concat "\n"

/// The post-implementation timing gate, verbatim from `hardware/vivado/`:
/// Vivado does not fail a run on unmet timing, and the simulators have no
/// timing model, so post-route slack is the only place that class of bug
/// shows.
let checkTimingTcl =
    """# check_timing.tcl — the post-implementation timing gate.
#
# Vivado does NOT fail a run on unmet timing: `write_bitstream` proceeds happily
# even at WNS = -62 ns, and the run still reports PROGRESS = 100%. The
# cycle-accurate simulators have no timing model, so post-route WNS is the
# ONLY place this whole class of bug shows up.
#
# Override for an intentional marginal / inspection build:
#   WARP11_ALLOW_BAD_TIMING=1 vivado -mode batch -source build.tcl
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
"""

let private buildTcl (t: BoardTop) (hasPins: bool) =
    let snake = snakeOf t.name
    let bd = $"{snake}_bd"

    let boardPart =
        match t.board.part.boardPart with
        | Some bp -> $"set_property board_part {bp} [current_project]"
        | None -> ""

    let constraints =
        if hasPins then
            $"add_files -norecurse -fileset constrs_1 [file normalize \"${{origin_dir}}/{snake}_pins.xdc\"]"
        else
            ""

    $"""# build.tcl — {t.name} for the {t.board.name}, generated by Warp11.Build.
#
#   vivado -mode batch -source build.tcl
#
# Output: ./{snake}/{snake}.runs/impl_1/{bd}_wrapper.bit

set origin_dir [file normalize [file dirname [info script]]]
set proj_name  "{snake}"
set proj_dir   "${{origin_dir}}/${{proj_name}}"

if {{ [file exists $proj_dir] }} {{
    puts "removing existing $proj_dir"
    file delete -force $proj_dir
}}

create_project $proj_name $proj_dir -part {t.board.part.device}
{boardPart}
set_property target_language Verilog [current_project]
set_property xpm_libraries {{XPM_CDC XPM_FIFO XPM_MEMORY}} [current_project]

add_files -norecurse -fileset sources_1 [file normalize "${{origin_dir}}/{t.name}.v"]
{constraints}

source [file normalize "${{origin_dir}}/{bd}.tcl"]

set top_bd [get_files "${{proj_dir}}/${{proj_name}}.srcs/sources_1/bd/{bd}/{bd}.bd"]
make_wrapper -fileset sources_1 -files $top_bd -top
add_files -norecurse -fileset sources_1 \
    [file normalize "${{proj_dir}}/${{proj_name}}.gen/sources_1/bd/{bd}/hdl/{bd}_wrapper.v"]
set_property top "{bd}_wrapper" [get_filesets sources_1]
update_compile_order -fileset sources_1

launch_runs synth_1 -jobs 4
wait_on_run synth_1
if {{ [get_property PROGRESS [get_runs synth_1]] != "100%%" }} {{
    error "synth_1 failed: see ${{proj_dir}}/${{proj_name}}.runs/synth_1/runme.log"
}}

launch_runs impl_1 -to_step write_bitstream -jobs 4
wait_on_run impl_1
if {{ [get_property PROGRESS [get_runs impl_1]] != "100%%" }} {{
    error "impl_1 failed: see ${{proj_dir}}/${{proj_name}}.runs/impl_1/runme.log"
}}

source [file normalize "${{origin_dir}}/check_timing.tcl"]
warp11_assert_timing_met impl_1

set bit [file normalize "${{proj_dir}}/${{proj_name}}.runs/impl_1/{bd}_wrapper.bit"]
if {{ ![file isfile $bit] }} {{
    error "build claimed success but bitstream missing: $bit"
}}
puts ""
puts "================================================================"
puts "BUILD OK"
puts "bitstream: $bit"
puts "================================================================"
close_project
"""

let private overlayDts (t: BoardTop) (app: string) (psClock: int) =
    let snake = snakeOf t.name
    let bd = $"{snake}_bd"

    let baseAddr =
        match t.board.host with
        | AxiLiteAt b -> b
        | _ -> failwith "an overlay wants an AXI-Lite host"

    let arena =
        match hasMaster t, t.board.hostMemory with
        | true, Some m ->
            $"""
    udmabuf_{snake}: udmabuf-{app} {{
        compatible = "ikwzm,u-dma-buf";
        device-name = "udmabuf-{app}";
        size = <0x%08X{m.arenaBytes}>;
    }};
"""
        | _ -> ""

    $"""/dts-v1/;
/plugin/;

/* {t.name} on the {t.board.name}, generated by Warp11.Build. */

&fpga_full {{
    firmware-name = "xilinx/{app}/{bd}_wrapper.bit.bin";
}};

&{{/}} {{{arena}
    {snake}@%x{baseAddr} {{
        compatible = "generic-uio";
        reg = <0x0 0x%x{baseAddr} 0x0 0x%x{apertureRange t.map}>;
        /* Loading a bitstream does not program the PS clock registers, so
         * the clock the design was elaborated for is pinned here. */
        clocks = <&zynqmp_clk %d{psClock}>;
        assigned-clocks = <&zynqmp_clk %d{psClock}>;
        assigned-clock-rates = <%d{t.board.fabricHz}>;
    }};
}};
"""

let private packageSh (t: BoardTop) (app: string) (firmwareDir: string) =
    let snake = snakeOf t.name
    let bd = $"{snake}_bd"

    $"""#!/usr/bin/env bash
# build.sh — {t.name} for the {t.board.name}: the bitstream, then the app
# the board's FPGA manager loads. Generated by Warp11.Build.
#
# Needs: vivado, bootgen (both from Vivado's settings64.sh) and dtc.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
app="$here/app"

for tool in vivado bootgen dtc; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool is not on PATH — source Vivado's settings64.sh; dtc is apt's device-tree-compiler" >&2
        exit 1
    fi
done

cd "$here"
vivado -mode batch -source build.tcl

bit="$here/{snake}/{snake}.runs/impl_1/{bd}_wrapper.bit"
if [ ! -f "$bit" ]; then
    echo "no bitstream at $bit" >&2
    exit 1
fi

mkdir -p "$app"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cp "$bit" "$work/{bd}_wrapper.bit"
cp "$here/{snake}.bif" "$work/"
(cd "$work" && bootgen -image {snake}.bif -arch zynqmp -process_bitstream bin -w)
cp "$work/{bd}_wrapper.bit.bin" "$app/"
dtc -@ -O dtb -o "$app/{app}.dtbo" "$here/{snake}.dts"
cp "$here/shell.json" "$app/"

echo
echo "packaged: $app"
echo
echo "deploy to the board:"
echo "  scp $app/* ubuntu@<board>:/tmp/{app}/"
echo "  ssh ubuntu@<board> 'sudo install -d -m 755 -o root -g root {firmwareDir}/{app} && \\"
echo "    sudo install -m 644 -o root -g root /tmp/{app}/* {firmwareDir}/{app}/ && \\"
echo "    sudo xmutil unloadapp; sudo xmutil loadapp {app}'"
"""

/// Every file the Vivado flow needs for this top, into `dir`.
let private writeVivado (dir: string) (t: BoardTop) : BuildOutput =
    let snake = snakeOf t.name
    let app = snake.Replace('_', '-')
    let pinned = pinGate t
    let audio = audioClockOf t

    let psClock =
        match t.board.clock with
        | PsClock i -> i
        | _ -> failwith $"{t.board.name}: a Vivado build here takes its fabric clock from the PS"

    let firmwareDir =
        match t.board.loading with
        | OsApp d -> d
        | _ -> failwith $"{t.board.name}: a Vivado build here loads as an OS app"

    let verilog =
        let text = emitDesignFor t.target t.top + "\n"
        if hasMaster t then withBusInterfaceAttribute text else text

    let files =
        [ $"{t.name}.v", verilog
          $"{snake}_layout.rs", String.concat "\n" (seamLines t) + "\n"
          $"{snake}_bd.tcl", blockDesignTcl t pinned psClock
          "build.tcl", buildTcl t (not pinned.IsEmpty || audio.IsSome)
          "check_timing.tcl", checkTimingTcl
          $"{snake}.dts", overlayDts t app psClock
          $"{snake}.bif", $"all:\n{{\n  [destination_device = pl] {snake}_bd_wrapper.bit\n}}\n"
          "shell.json", "{\n  \"shell_type\": \"XRT_FLAT\",\n  \"num_slots\": \"1\"\n}\n"
          "build.sh", packageSh t app firmwareDir ]
        @ (if pinned.IsEmpty && audio.IsNone then
               []
           else
               [ $"{snake}_pins.xdc", xdc pinned audio + "\n" ])

    System.IO.Directory.CreateDirectory dir |> ignore

    let written =
        [ for name, text in files ->
              let path = System.IO.Path.Combine(dir, name)
              System.IO.File.WriteAllText(path, text)
              path ]

    let script = System.IO.Path.Combine(dir, "build.sh")
    System.IO.File.SetUnixFileMode(script, System.IO.File.GetUnixFileMode script ||| System.IO.UnixFileMode.UserExecute ||| System.IO.UnixFileMode.GroupExecute)

    { dir = dir
      files = written
      run = $"{script}" }

// ---------------------------------------------------------------------------
// The open flow: a wrapper with the PLL, the pin map, yosys → nextpnr → icepack.

/// `SB_PLL40_PAD`'s parameters for a crystal to a fabric rate, chosen the
/// way `icepll` chooses them: simple feedback, the VCO between 533 and
/// 1066 MHz, the divider input between 10 and 133 MHz, the closest output
/// to the target, and the filter range from the divider input's band.
type IcePll =
    { divr: int
      divf: int
      divq: int
      filterRange: int
      /// What the parameters land on, in hertz.
      achievedHz: float }

let icePll (crystalHz: int) (targetHz: int) : IcePll =
    let fin = float crystalHz
    let target = float targetHz

    let filterRangeOf (pfdHz: float) =
        let mhz = pfdHz / 1e6

        if mhz < 17.0 then 1
        elif mhz < 26.0 then 2
        elif mhz < 44.0 then 3
        elif mhz < 66.0 then 4
        elif mhz < 101.0 then 5
        else 6

    let candidates =
        [ for divr in 0..15 do
              let pfd = fin / float (divr + 1)

              if pfd >= 10e6 && pfd <= 133e6 then
                  for divf in 0..63 do
                      let vco = pfd * float (divf + 1)

                      if vco >= 533e6 && vco <= 1066e6 then
                          for divq in 1..6 do
                              let out = vco / float (1 <<< divq)

                              yield
                                  { divr = divr
                                    divf = divf
                                    divq = divq
                                    filterRange = filterRangeOf pfd
                                    achievedHz = out } ]

    if candidates.IsEmpty then
        failwith $"no iCE40 PLL setting takes %d{crystalHz} Hz anywhere near %d{targetHz} Hz"

    candidates |> List.minBy (fun c -> abs (c.achievedHz - target))

/// The wrapper an iCE40 design needs and the elaborator cannot emit: the
/// PLL from the crystal (a vendor primitive, so a design that could name it
/// would stop being the same design on the other board), a reset released
/// on lock, and the design instance with everything else passed straight
/// through. The clock reaches the design as `clk`, the reset as `rst`.
let private iceWrapper (t: BoardTop) (crystalPort: string) (crystalHz: int) (passthrough: Pinned list) =
    let top = $"{snakeOf t.name}_top"

    let portDecls =
        [ yield $"    input  wire {crystalPort},"
          for p in passthrough ->
              let dir = if p.input then "input " else "output"
              let range = if p.width = 1 then "" else $"[%d{p.width - 1}:0] "
              $"    {dir} wire {range}{p.port}," ]
        |> String.concat "\n"

    let portDecls = portDecls.TrimEnd(',')

    let connections =
        [ yield "        .clk(clk),"
          yield "        .rst(rst),"
          for p in passthrough -> $"        .{p.port}({p.port})," ]
        |> String.concat "\n"

    let connections = connections.TrimEnd(',')

    let clock =
        if crystalHz = t.board.fabricHz then
            $"""    // The crystal is the fabric clock: no PLL, and the reset counts from
    // power-on alone.
    wire clk = {crystalPort};
    wire pll_locked = 1'b1;
"""
        else
            let pll = icePll crystalHz t.board.fabricHz
            let divr = System.Convert.ToString(pll.divr, 2).PadLeft(4, '0')
            let divf = System.Convert.ToString(pll.divf, 2).PadLeft(7, '0')
            let divq = System.Convert.ToString(pll.divq, 2).PadLeft(3, '0')
            let filter = System.Convert.ToString(pll.filterRange, 2).PadLeft(3, '0')
            let achieved = (pll.achievedHz / 1e6).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)

            $"""    // {megahertz crystalHz} MHz in, {achieved} MHz out — the parameters `icepll -i {megahertz crystalHz} -o {megahertz t.board.fabricHz}` picks.
    wire clk;
    wire pll_locked;

    SB_PLL40_PAD #(
        .FEEDBACK_PATH("SIMPLE"),
        .DIVR(4'b{divr}),
        .DIVF(7'b{divf}),
        .DIVQ(3'b{divq}),
        .FILTER_RANGE(3'b{filter})
    ) pll (
        .PACKAGEPIN({crystalPort}),
        .PLLOUTCORE(clk),
        .LOCK(pll_locked),
        .RESETB(1'b1),
        .BYPASS(1'b0)
    );
"""

    $"""// {top}.v — {t.name} on the {t.board.name}, generated by Warp11.Build.
//
// The board's clock and reset, which the design cannot make for itself, and
// the design instance; every other port passes straight through to a pin.

`default_nettype none

module {top} (
{portDecls}
);

{clock}
    // The board has no reset pin, so one is made here: held while the PLL is
    // unlocked, and for fifteen cycles after it locks, so every divider in
    // the design starts from its reset value on a clock that is already
    // steady. The counter powers up at zero because iCE40 flip-flops honour
    // their initial value out of the bitstream.
    reg [3:0] por = 4'd0;

    always @(posedge clk) begin
        if (!pll_locked)
            por <= 4'd0;
        else if (por != 4'd15)
            por <= por + 4'd1;
    end

    wire rst = (por != 4'd15);

    {t.name} design (
{connections}
    );

endmodule

`default_nettype wire
"""

let private pcf (pinned: Pinned list) =
    pinned |> List.map (fun p -> $"set_io {p.port} {p.pin.pin}") |> String.concat "\n"

let private openFlowSh (t: BoardTop) (top: string) (sources: string list) =
    let part = t.board.part.device
    let package = t.board.part.package
    let freq = megahertz t.board.fabricHz
    let sourceList = sources |> List.map (fun f -> $"\"$here/{f}\"") |> String.concat " "

    $"""#!/usr/bin/env bash
# build.sh — {t.name} on the {t.board.name}: yosys → nextpnr-ice40 → icepack,
# generated by Warp11.Build.
#
#   ./build.sh            builds {top}.bin
#   PROG=sram ./build.sh  ...and loads it into the FPGA's SRAM (gone at power-off)
#   PROG=1 ./build.sh     ...and writes the SPI flash
#
# OSS_CAD_BIN (default ~/tools/bin) is where the pinned oss-cad-suite lives;
# hdl/tools/install-oss-cad-suite.sh puts it there.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
top={top}
pcf="$here/{top}.pcf"
sources=({sourceList})
part={part}
package={package}
freq={freq}
out="$here/build"
mkdir -p "$out"

# The pinned suite goes first on PATH: a distribution yosys can be years
# older and die in the DSP inference pass on any design with a wide multiply.
bindir=${{OSS_CAD_BIN:-$HOME/tools/bin}}
export PATH="$bindir:$PATH"
# These binaries carry their own RPATH and ship a complete lib/; anything on
# LD_LIBRARY_PATH makes system libraries resolve against the suite's copies.
unset LD_LIBRARY_PATH

for tool in yosys nextpnr-ice40 icepack; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool is not on PATH — run hdl/tools/install-oss-cad-suite.sh" >&2
        exit 1
    fi
done

echo "yosys:   $(command -v yosys) — $(yosys -V)"
echo "nextpnr: $(command -v nextpnr-ice40)"

json="$out/$top.json"
asc="$out/$top.asc"
bin="$out/$top.bin"

# -dsp, or every multiply maps to logic and every area number is meaningless.
echo
echo "== synth =="
yosys -p "read_verilog ${{sources[*]}}; synth_ice40 -dsp -top $top -json $json"

# Every port is pinned: nextpnr places an unpinned port wherever it likes and
# carries on with a warning, so a pin map missing a line builds a
# working-looking bitstream that drives a clock out of an unconnected ball.
# The generator checked this before writing the map; the netlist is checked
# again here, against what synthesis kept.
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
    print(f"  port '{{name}}' has no set_io in {{pcf}}")
for name in unknown:
    print(f"  set_io '{{name}}' names no port of {{top}}")

if unpinned or unknown:
    print("pin map and design disagree — see above", file=sys.stderr)
    sys.exit(1)

print(f"  {{len(ports)}} ports, all pinned")
PY

echo
echo "== place and route =="
nextpnr-ice40 "--$part" --package "$package" --pcf "$pcf" --json "$json" \
    --asc "$asc" --freq "$freq" 2>&1 | tee "$out/$top.pnr.log"

# The simulators have no timing model, so this is the only place a cone too
# slow for the clock shows up. Read out of the log rather than trusted to
# nextpnr's exit code: it reports a timing failure and still writes an .asc.
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
print(f"  Fmax {{worst:.2f}} MHz against {{target:.2f}} MHz target — {{worst / target:.2f}}x")

if worst < target:
    print(f"TIMING FAILED: {{worst:.2f}} MHz < {{target:.2f}} MHz", file=sys.stderr)
    sys.exit(1)
PY

echo
echo "== pack =="
icepack "$asc" "$bin"
ls -l "$bin"

case "${{PROG:-0}}" in
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
"""

// ---------------------------------------------------------------------------
// The ECP5 flow: the same shape — a wrapper with the PLL, the pin map, and
// yosys → nextpnr-ecp5 → ecppack — with the family's primitive and its
// constraint format.

/// `EHXPLLL`'s parameters for an oscillator to a fabric rate, chosen under
/// the rules `ecppll` applies: the phase detector between 3.125 and 400 MHz,
/// the VCO between 400 and 800 MHz, the closest output to the target and,
/// among equals, the VCO nearest 600 MHz.
type Ecp5Pll =
    { referenceDiv: int
      feedbackDiv: int
      outputDiv: int
      achievedHz: float }

let ecp5Pll (inputHz: int) (targetHz: int) : Ecp5Pll =
    let fin = float inputHz
    let target = float targetHz

    let candidates =
        [ for referenceDiv in 1..128 do
              let pfd = fin / float referenceDiv

              if pfd >= 3.125e6 && pfd <= 400e6 then
                  for feedbackDiv in 1..80 do
                      let out = pfd * float feedbackDiv

                      for outputDiv in 1..128 do
                          let vco = out * float outputDiv

                          if vco >= 400e6 && vco <= 800e6 then
                              yield
                                  { referenceDiv = referenceDiv
                                    feedbackDiv = feedbackDiv
                                    outputDiv = outputDiv
                                    achievedHz = out } ]

    if candidates.IsEmpty then
        failwith $"no ECP5 PLL setting takes %d{inputHz} Hz anywhere near %d{targetHz} Hz"

    candidates
    |> List.minBy (fun c -> abs (c.achievedHz - target), abs (c.achievedHz * float c.outputDiv - 600e6))

let private ecp5Wrapper (t: BoardTop) (clockPort: string) (clockHz: int) (passthrough: Pinned list) =
    let top = $"{snakeOf t.name}_top"

    let portDecls =
        [ yield $"    input  wire {clockPort},"
          for p in passthrough ->
              let dir = if p.input then "input " else "output"
              let range = if p.width = 1 then "" else $"[%d{p.width - 1}:0] "
              $"    {dir} wire {range}{p.port}," ]
        |> String.concat "\n"

    let portDecls = portDecls.TrimEnd(',')

    let connections =
        [ yield "        .clk(clk),"
          yield "        .rst(rst),"
          for p in passthrough -> $"        .{p.port}({p.port})," ]
        |> String.concat "\n"

    let connections = connections.TrimEnd(',')

    let clock =
        if clockHz = t.board.fabricHz then
            $"""    // The oscillator is the fabric clock: no PLL, and the reset counts from
    // power-on alone.
    wire clk = {clockPort};
    wire pll_locked = 1'b1;
"""
        else
            let pll = ecp5Pll clockHz t.board.fabricHz
            let mhz (hz: float) = (hz / 1e6).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            let achieved = mhz pll.achievedHz

            if round pll.achievedHz <> float t.board.fabricHz then
                failwith
                    $"{t.board.name}: the ECP5 PLL cannot make %d{t.board.fabricHz} Hz from {mhz (float clockHz)} MHz — the nearest it lands on is {achieved} MHz; pin the board at that, or at a rate it reaches exactly"

            $"""    // {megahertz clockHz} MHz in, {achieved} MHz out — the parameters `ecppll -i {megahertz clockHz} -o {megahertz t.board.fabricHz}` picks.
    wire clk;
    wire pll_locked;

    (* FREQUENCY_PIN_CLKI="{megahertz clockHz}" *)
    (* FREQUENCY_PIN_CLKOP="{achieved}" *)
    (* ICP_CURRENT="12" *) (* LPF_RESISTOR="8" *) (* MFG_ENABLE_FILTEROPAMP="1" *) (* MFG_GMCREF_SEL="2" *)
    EHXPLLL #(
        .PLLRST_ENA("DISABLED"),
        .INTFB_WAKE("DISABLED"),
        .STDBY_ENABLE("DISABLED"),
        .DPHASE_SOURCE("DISABLED"),
        .OUTDIVIDER_MUXA("DIVA"),
        .OUTDIVIDER_MUXB("DIVB"),
        .OUTDIVIDER_MUXC("DIVC"),
        .OUTDIVIDER_MUXD("DIVD"),
        .CLKI_DIV(%d{pll.referenceDiv}),
        .CLKOP_ENABLE("ENABLED"),
        .CLKOP_DIV(%d{pll.outputDiv}),
        .CLKOP_CPHASE(%d{pll.outputDiv / 2 - 1}),
        .CLKOP_FPHASE(0),
        .FEEDBK_PATH("CLKOP"),
        .CLKFB_DIV(%d{pll.feedbackDiv})
    ) pll (
        .RST(1'b0),
        .STDBY(1'b0),
        .CLKI({clockPort}),
        .CLKOP(clk),
        .CLKFB(clk),
        .CLKINTFB(),
        .PHASESEL0(1'b0),
        .PHASESEL1(1'b0),
        .PHASEDIR(1'b1),
        .PHASESTEP(1'b1),
        .PHASELOADREG(1'b1),
        .PLLWAKESYNC(1'b0),
        .ENCLKOP(1'b0),
        .LOCK(pll_locked)
    );
"""

    $"""// {top}.v — {t.name} on the {t.board.name}, generated by Warp11.Build.
//
// The board's clock and reset, which the design cannot make for itself, and
// the design instance; every other port passes straight through to a pin.

`default_nettype none

module {top} (
{portDecls}
);

{clock}
    // The board has no reset pin, so one is made here: held while the PLL is
    // unlocked, and for fifteen cycles after it locks, so every divider in
    // the design starts from its reset value on a clock that is already
    // steady. The counter powers up at zero because ECP5 flip-flops take
    // their initial value from the bitstream.
    reg [3:0] por = 4'd0;

    always @(posedge clk) begin
        if (!pll_locked)
            por <= 4'd0;
        else if (por != 4'd15)
            por <= por + 4'd1;
    end

    wire rst = (por != 4'd15);

    {t.name} design (
{connections}
    );

endmodule

`default_nettype wire
"""

/// The pin map in the ECP5's own format: a site and an IO type a port, and
/// the oscillator's frequency so nextpnr constrains what the PLL is fed.
let private lpf (clockPort: string) (clockHz: int) (pinned: Pinned list) =
    [ yield "BLOCK RESETPATHS;"
      yield "BLOCK ASYNCPATHS;"
      for p in pinned do
          yield $"LOCATE COMP \"{p.port}\" SITE \"{p.pin.pin}\";"

          match p.pin.standard with
          | Some standard -> yield $"IOBUF PORT \"{p.port}\" IO_TYPE={standard};"
          | None -> ()
      yield $"FREQUENCY PORT \"{clockPort}\" {megahertz clockHz} MHZ;" ]
    |> String.concat "\n"

let private ecp5FlowSh (t: BoardTop) (top: string) (sources: string list) =
    let device = t.board.part.device
    let package = t.board.part.package
    let freq = megahertz t.board.fabricHz
    let loader = t.board.part.boardPart |> Option.defaultWith (fun () -> failwith $"{t.board.name}: an ECP5 board names its openFPGALoader board in `boardPart`")
    let sourceList = sources |> List.map (fun f -> $"\"$here/{f}\"") |> String.concat " "

    $"""#!/usr/bin/env bash
# build.sh — {t.name} on the {t.board.name}: yosys → nextpnr-ecp5 → ecppack,
# generated by Warp11.Build.
#
#   ./build.sh            builds {top}.bit
#   PROG=sram ./build.sh  ...and loads it into the FPGA (gone at power-off)
#   PROG=1 ./build.sh     ...and writes the SPI flash
#
# OSS_CAD_BIN (default ~/tools/bin) is where the pinned oss-cad-suite lives;
# hdl/tools/install-oss-cad-suite.sh puts it there.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
top={top}
lpf="$here/{top}.lpf"
sources=({sourceList})
device={device}
package={package}
freq={freq}
out="$here/build"
mkdir -p "$out"

# The pinned suite goes first on PATH, and nothing on LD_LIBRARY_PATH: these
# binaries carry their own RPATH and ship a complete lib/.
bindir=${{OSS_CAD_BIN:-$HOME/tools/bin}}
export PATH="$bindir:$PATH"
unset LD_LIBRARY_PATH

for tool in yosys nextpnr-ecp5 ecppack; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool is not on PATH — run hdl/tools/install-oss-cad-suite.sh" >&2
        exit 1
    fi
done

echo "yosys:   $(command -v yosys) — $(yosys -V)"
echo "nextpnr: $(command -v nextpnr-ecp5)"

json="$out/$top.json"
config="$out/$top.config"
bit="$out/$top.bit"

echo
echo "== synth =="
yosys -p "read_verilog ${{sources[*]}}; synth_ecp5 -top $top -json $json"

# Every port is pinned: nextpnr places an unpinned port wherever it likes, so
# the netlist is checked against the map, against what synthesis kept.
echo
echo "== pins =="
python3 - "$json" "$lpf" "$top" <<'PY'
import json, re, sys

design, lpf, top = sys.argv[1], sys.argv[2], sys.argv[3]

with open(design) as f:
    ports = set(json.load(f)["modules"][top]["ports"])

pinned = set(re.findall(r'LOCATE COMP "([^"\[]+)', open(lpf).read()))

unpinned = sorted(ports - pinned)
unknown = sorted(pinned - ports)

for name in unpinned:
    print(f"  port '{{name}}' has no LOCATE in {{lpf}}")
for name in unknown:
    print(f"  LOCATE '{{name}}' names no port of {{top}}")

if unpinned or unknown:
    print("pin map and design disagree — see above", file=sys.stderr)
    sys.exit(1)

print(f"  {{len(ports)}} ports, all pinned")
PY

echo
echo "== place and route =="
nextpnr-ecp5 "--$device" --package "$package" --lpf "$lpf" --json "$json" \
    --textcfg "$config" --freq "$freq" 2>&1 | tee "$out/$top.pnr.log"

# The simulators have no timing model, so this is the only place a cone too
# slow for the clock shows up — read out of the log, not the exit code.
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
print(f"  Fmax {{worst:.2f}} MHz against {{target:.2f}} MHz target — {{worst / target:.2f}}x")

if worst < target:
    print(f"TIMING FAILED: {{worst:.2f}} MHz < {{target:.2f}} MHz", file=sys.stderr)
    sys.exit(1)
PY

echo
echo "== pack =="
ecppack --compress "$config" "$bit"
ls -l "$bit"

case "${{PROG:-0}}" in
    sram)
        echo
        echo "== program (volatile) =="
        openFPGALoader -b {loader} "$bit"
        ;;
    1)
        echo
        echo "== program (SPI flash) =="
        openFPGALoader -b {loader} --write-flash "$bit"
        ;;
esac
"""

/// Every file the open flow needs for this top, into `dir`.
let private writeOpenFlow (dir: string) (t: BoardTop) : BuildOutput =
    // The generated wrapper drives clk and rst and nothing else, so a top
    // with a second domain would leave its conjured pair floating — refused
    // here rather than discovered as a silent bitstream.
    match t.top.foreignDomains with
    | fd :: _ ->
        failwith
            $"{t.board.name}: '{t.name}' uses clock domain '{fd.domainName}', and the open flow's wrapper is not built for a second clock yet — the KV260 flow carries the first audio domain (notes/CLOCK_DOMAINS.md increment 7)"
    | [] -> ()

    let snake = snakeOf t.name
    let top = $"{snake}_top"

    let crystalPort, crystalHz =
        match t.board.clock, connectorFor ClockIn t.board with
        | Crystal hz, Some [ port, _ ] -> port, hz
        | Oscillator hz, Some [ port, _ ] -> port, hz
        | (Crystal _ | Oscillator _), _ -> failwith $"{t.board.name}: the clock-in connector must name exactly one pin"
        | PsClock _, _ -> failwith $"{t.board.name}: the open flow takes its clock from a pin, not a PS"

    // The design's own clock and reset are the wrapper's to make; every
    // other port of the design passes through the wrapper to a pin. The
    // gate runs on the wrapper's ports: the crystal in, and those.
    let designPorts = portsOf t.top |> List.filter (fun (n, _, _) -> n <> "clk" && n <> "rst")

    let wrapperTop =
        { t.top with
            decls =
                [ yield Input(crystalPort, UInt 1)
                  for n, input, width in designPorts do
                      if input then
                          yield Input(n, UInt width)
                      else
                          yield Output(n, UInt width) ] }

    let pinned = pinGate { t with top = wrapperTop }
    let passthrough = pinned |> List.filter (fun p -> p.port <> crystalPort)

    let familyFiles =
        match t.board.part.family with
        | Ice40UltraPlus ->
            [ $"{t.name}.v", emitDesignFor t.target t.top + "\n"
              $"{top}.v", iceWrapper t crystalPort crystalHz passthrough
              $"{top}.pcf", pcf pinned + "\n"
              "build.sh", openFlowSh t top [ $"{t.name}.v"; $"{top}.v" ] ]
        | Family.Ecp5 ->
            [ $"{t.name}.v", emitDesignFor t.target t.top + "\n"
              $"{top}.v", ecp5Wrapper t crystalPort crystalHz passthrough
              $"{top}.lpf", lpf crystalPort crystalHz pinned + "\n"
              "build.sh", ecp5FlowSh t top [ $"{t.name}.v"; $"{top}.v" ] ]
        | UltraScalePlus -> failwith $"{t.board.name}: the open flow does not build an UltraScale+ part"

    let files =
        familyFiles
        @ (match t.board.host with
           | NoHost -> []
           | _ -> [ $"{snake}_layout.rs", String.concat "\n" (seamLines t) + "\n" ])

    System.IO.Directory.CreateDirectory dir |> ignore

    let written =
        [ for name, text in files ->
              let path = System.IO.Path.Combine(dir, name)
              System.IO.File.WriteAllText(path, text)
              path ]

    let script = System.IO.Path.Combine(dir, "build.sh")
    System.IO.File.SetUnixFileMode(script, System.IO.File.GetUnixFileMode script ||| System.IO.UnixFileMode.UserExecute ||| System.IO.UnixFileMode.GroupExecute)

    { dir = dir
      files = written
      run = script }

/// The build directory for a top, by the board's build tool.
let write (dir: string) (t: BoardTop) : BuildOutput =
    match t.board.tool with
    | Vivado -> writeVivado dir t
    | OpenFlow -> writeOpenFlow dir t
