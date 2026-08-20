//! The mixed design, pre-silicon: the same `MandelDevice` that will mmap
//! `/dev/mem` on the KV260 runs here against the F#-elaborated pod in the F#
//! Sim, through the `FsSimWindow` bridge. Everything the first-light session
//! will do on the board happens in this test — ID check, scratch smoke, poll
//! `done`, read the frame — plus the assertion silicon cannot give us cheaply:
//! bit-exactness against a Rust port of the software twin, per pixel.
//!
//! Skips (with a note) when `dotnet` is not on PATH, the Verilator-absent
//! pattern from the Kotlin side.

use std::path::PathBuf;
use std::process::Command;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::mandel_twin::twin;
use warp11_runtime::mandel::MandelDevice;
use warp11_runtime::mandel_layout as layout;

fn fsproj() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../hdl/Warp11.Mandelbrot/Warp11.Mandelbrot.fsproj")
}

#[test]
fn the_driver_runs_the_fsharp_pod() {
    if Command::new("dotnet").arg("--version").output().is_err() {
        eprintln!("SKIPPED: dotnet not on PATH — the FsSimWindow bridge needs the F# spike");
        return;
    }

    let window = FsSimWindow::spawn(&fsproj()).expect("simserve spawns and reports ready");
    let mut device = MandelDevice::open(window).expect("the ID register reads the pod's magic");

    assert_eq!(
        device.scratch_round_trip(0xC0FF_EE11).expect("scratch"),
        0xC0FF_EE11,
        "the write path round-trips"
    );

    // Each poll advances the Sim two cycles; the standalone render takes
    // 12,933, so this budget is ~3x the frame.
    device.wait_done(20_000).expect("the pod finishes");
    assert_eq!(
        device.result_count().expect("count"),
        layout::FB_PIXELS as u32
    );

    // The pod ignores the bus, so its cycle count to done is the standalone
    // number exactly — the strongest cross-check the bridge can make: the
    // Rust-driven F# Sim reproduces the F#-driven render to the cycle.
    assert_eq!(device.frame_cycles().expect("cycles"), 12_933);

    let mut frame = [0u8; layout::FB_PIXELS];
    device.read_frame(&mut frame).expect("frame reads");
    let expected = twin();
    assert_eq!(
        &frame[..],
        &expected[..],
        "every pixel bit-exact against the twin"
    );
}
