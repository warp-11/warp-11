//! The batch driver against the F# Sim, across the language seam: the same
//! `BatchDevice` that mmaps a uio device on the board stages rows into the
//! bridge's fake DDR, sets the drawn design's `volume` by name, starts,
//! polls busy with free cycles between, and reads the rows back doubled.

use std::path::PathBuf;
use std::process::Command;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_runtime::batch::BatchDevice;

fn fsproj() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../hdl/Warp11.Placement/Warp11.Placement.fsproj")
}

fn design() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../hdl/Warp11.Placement.Canvas/examples/gain.json")
}

#[test]
fn the_driver_runs_a_drawn_design_through_ddr() {
    if Command::new("dotnet").arg("--version").output().is_err() {
        eprintln!("SKIPPED: dotnet not on PATH — the batchserve bridge needs the F# side");
        return;
    }

    let design = design().to_string_lossy().to_string();
    let window = FsSimWindow::spawn_with(&fsproj(), &["batchserve", &design, "kv260"], "BATCHSERVE")
        .expect("batchserve should come up");
    let mut device = BatchDevice::open(window, None).expect("the batch identity should answer");

    let registers = device.window_mut().register_map().expect("the map");
    let volume = registers
        .iter()
        .find(|(n, _)| n == "volume")
        .map(|(_, o)| *o)
        .expect("the gain design has a volume control");
    // Q8.8: 512 is twice unity.
    device.set(volume, 512).expect("set volume");

    // 64 frames — two bursts — of a ramp, two 32-bit lanes each, the sample
    // in the low 24 bits.
    let frames: Vec<(i32, i32)> = (0..64).map(|i| (i * 1000, -i * 1000)).collect();
    let mut bytes = Vec::new();
    for (l, r) in &frames {
        bytes.extend_from_slice(&l.to_le_bytes());
        bytes.extend_from_slice(&r.to_le_bytes());
    }
    let src = 0x1000usize;
    let dst = 0x9000usize;
    device.window_mut().write_ddr(src, &bytes).expect("stage the rows");

    device.start(src as u32, dst as u32, frames.len() as u32).expect("start");
    device.wait_done(2_000, |w| w.free_cycles(512)).expect("the batch finishes");

    let out = device.window_mut().read_ddr(dst, bytes.len()).expect("read the rows back");
    let mask = (1i32 << 24) - 1;
    for (i, (l, r)) in frames.iter().enumerate() {
        let lane = |at: usize| i32::from_le_bytes([out[at], out[at + 1], out[at + 2], out[at + 3]]) & mask;
        assert_eq!(lane(i * 8), (2 * l) & mask, "left frame {i}");
        assert_eq!(lane(i * 8 + 4), (2 * r) & mask, "right frame {i}");
    }
}
