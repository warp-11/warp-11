//! First light for the mixed design: the F#-elaborated Mandelbrot pod on the
//! KV260, driven by the same `MandelDevice` the FsSimWindow bridge already
//! proved bit-exact against the F# Sim. Run on the board after
//! `xmutil loadapp mandel-fs`:
//!
//!     sudo ./mandel_first_light                # /dev/mem @ 0xB0000000
//!     sudo ./mandel_first_light /dev/uio4      # or the uio node the dtbo declares
//!     sudo ./mandel_first_light /dev/mem /tmp/mandel.ppm
//!
//! `/dev/mem` maps at the AXI base; any other path (a uio node, or a plain
//! file in a test) maps at offset zero. The run-once pod finished ~259 µs
//! after the bitstream loaded, so `done` is long since high — the binary
//! verifies, renders, and reports; the render already happened.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use warp11_host::mandel_twin::twin;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_runtime::mandel::MandelDevice;
use warp11_runtime::mandel_layout as layout;

const AXI_BASE: i64 = 0xB000_0000;
const PL_CLOCK_MHZ: f64 = 50.0; // set in mandelfs_bd_bd.tcl

fn main() {
    let mut args = std::env::args().skip(1);
    let device_path = args.next().unwrap_or_else(|| "/dev/mem".into());
    let ppm_path = args.next().unwrap_or_else(|| "mandel-fs.ppm".into());
    let offset = if device_path == "/dev/mem" {
        AXI_BASE
    } else {
        0
    };

    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&device_path)
        .unwrap_or_else(|e| {
            eprintln!("cannot open {device_path}: {e} (run with sudo?)");
            exit(1);
        });

    let window = MmapWindow::open(&file, offset, layout::APERTURE_BYTES).unwrap_or_else(|e| {
        eprintln!("mmap of {device_path} failed: {e:?}");
        exit(1);
    });

    let mut device = MandelDevice::open(window).unwrap_or_else(|e| {
        eprintln!("device open failed: {e:?} — wrong bitstream loaded, or wrong base?");
        exit(1);
    });

    let echoed = device.scratch_round_trip(0xC0FF_EE11).expect("scratch");
    assert_eq!(echoed, 0xC0FF_EE11, "the write path round-trips");

    device
        .wait_done(1_000)
        .expect("done should long since be high");
    let count = device.result_count().expect("count");
    let cycles = device.frame_cycles().expect("cycles");

    let mut frame = [0u8; layout::FB_PIXELS];
    device.read_frame(&mut frame).expect("frame");

    // The PPM, F# runMandel's mapping exactly: interior black, escapes shaded
    // by iteration count.
    let shade = |v: u8| {
        if u32::from(v) >= layout::MAX_ITER {
            0
        } else {
            v
        }
    };
    let mut ppm = format!(
        "P2\n{} {}\n{}\n",
        layout::FRAME_WIDTH,
        layout::FRAME_HEIGHT,
        layout::MAX_ITER
    );
    for row in frame.chunks(layout::FRAME_WIDTH) {
        let line: Vec<String> = row.iter().map(|&v| shade(v).to_string()).collect();
        ppm.push_str(&line.join(" "));
        ppm.push('\n');
    }
    std::fs::write(&ppm_path, ppm).expect("write ppm");

    // ASCII preview, two pixel rows per character row.
    let ramp: &[u8] = b" .:-=+*#%@";
    for char_row in 0..layout::FRAME_HEIGHT / 2 {
        let line: String = (0..layout::FRAME_WIDTH)
            .map(|px| {
                let v = u32::from(frame[char_row * 2 * layout::FRAME_WIDTH + px]);
                if v >= layout::MAX_ITER {
                    '@'
                } else {
                    ramp[(v as usize * 9 / layout::MAX_ITER as usize).min(8)] as char
                }
            })
            .collect();
        println!("{line}");
    }

    let mismatches = frame
        .iter()
        .zip(twin().iter())
        .filter(|(a, b)| a != b)
        .count();

    println!(
        "rendered {}x{} @ max {} iterations in {} fabric cycles ({:.1} us at {} MHz) -> {}",
        layout::FRAME_WIDTH,
        layout::FRAME_HEIGHT,
        layout::MAX_ITER,
        cycles,
        f64::from(cycles) / PL_CLOCK_MHZ,
        PL_CLOCK_MHZ,
        ppm_path
    );
    println!(
        "result count:                 {count} of {}",
        layout::FB_PIXELS
    );
    println!(
        "twin mismatches:              {mismatches} of {}",
        layout::FB_PIXELS
    );

    if count as usize != layout::FB_PIXELS || mismatches != 0 {
        eprintln!("FIRST LIGHT FAILED: the fabric and the twin disagree");
        exit(1);
    }
    println!("FIRST LIGHT OK");
}
