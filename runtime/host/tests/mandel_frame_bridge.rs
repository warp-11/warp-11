//! The frame accelerator's driver against the F# Sim, across the language
//! seam: `MandelFrameDevice` — the exact code that will mmap `/dev/mem` on
//! the board — programs a view over real five-channel AXI-Lite handshakes,
//! pulses start, polls the sticky done, and reads the framebuffer back from
//! the bridge's fake DDR, bit-exact against the software twin. The bridge
//! runs the SCALED config (64×48 / maxIter 48 / 4 lanes); the layout offsets
//! are identical to the silicon config's.

use std::path::PathBuf;
use std::process::Command;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::mandel_frame_twin::frame_twin;
use warp11_runtime::mandel_frame::{MandelFrameDevice, View};

// The scaled bridge config — NOT the layout's silicon constants.
const W: usize = 64;
const H: usize = 48;
const MAX_ITER: u32 = 48;

fn fsproj() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../hdl/Warp11.Mandelbrot/Warp11.Mandelbrot.fsproj")
}

fn q4_28(v: f64) -> u32 {
    (v * f64::from(1u32 << 28)) as i64 as u32
}

#[test]
fn the_driver_renders_the_frame_pod() {
    if Command::new("dotnet").arg("--version").output().is_err() {
        eprintln!("SKIPPED: dotnet not on PATH — the frameserve bridge needs the F# side");
        return;
    }

    let window = FsSimWindow::spawn_mode(&fsproj(), "frameserve", "FRAMESERVE")
        .expect("frameserve should come up");
    let mut device = MandelFrameDevice::open(window).expect("ID register should match");

    let view = View {
        cx_origin: q4_28(-2.25),
        cy_origin: q4_28(-1.125),
        dx: q4_28(3.0 / 64.0),
        dy: q4_28(3.0 / 64.0),
    };
    let fb_base = 0x100u32;

    device.start_render(view, fb_base).expect("view programs");
    // Each done poll is a real read (~2 fabric cycles); the free-cycle gaps
    // let the pod run at fabric speed between polls, as silicon would.
    for _ in 0..2000 {
        if device
            .window_mut()
            .free_cycles(50)
            .and(Ok(()))
            .is_err()
        {
            panic!("free_cycles failed");
        }
        if device.wait_done(1).is_ok() {
            break;
        }
    }
    device.wait_done(1).expect("frameDone should be sticky-high");

    // Flush the master's outstanding writes, then read the frame from DDR.
    device.window_mut().free_cycles(100).expect("flush");
    let frame = device
        .window_mut()
        .read_ddr(fb_base as usize, W * H)
        .expect("DDR dump");

    let cycles = device.last_frame_cycles().expect("cycles read");
    assert!(cycles > 0, "lastFrameCycles should be nonzero");

    let expected = frame_twin(W, H, MAX_ITER, view.cx_origin, view.cy_origin, view.dx, view.dy);
    assert_eq!(frame, expected, "the fabric and the twin must agree per pixel");

    println!("frame bridge OK: {W}x{H} bit-exact, lastFrameCycles={cycles}");
}
