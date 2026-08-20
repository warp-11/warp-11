//! First light for the full-scale frame accelerator: the F#-elaborated
//! 104-lane pod on the KV260, driven by the same `MandelFrameDevice` the
//! frameserve bridge already proved bit-exact against the F# Sim. Run on the
//! board after `xmutil loadapp mandel-frame`:
//!
//!     ./mandel_frame_first_light                 # registers via the uio node, no root
//!     ./mandel_frame_first_light /dev/mem        # root mode (sudo)
//!     ./mandel_frame_first_light auto /tmp/f.ppm
//!
//! Measures the two halves separately — the Kotlin scoreboard's split:
//!   fabric compute   — lastFrameCycles at the PL clock (Kotlin: 3.29 ms)
//!   fabric → host    — the framebuffer readback, on BOTH paths:
//!     O_SYNC /dev/mem   uncached device mapping, the always-correct baseline
//!     cached udmabuf    plain mmap of /dev/udmabuf0 + sync_for_cpu, the fast
//!                       path (Kotlin used the warp11-dma module, ~1 ms)
//! Renders twice (the pod is re-renderable), verifies bit-exact against the
//! full-frame twin, and reports end-to-end. Build with --release: the twin
//! iterates 1408×800 on the A53s.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use std::time::Instant;
use warp11_host::mandel_frame_twin::frame_twin;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_host::warp11_dma::Warp11Dma;
use warp11_runtime::mandel_frame::{MandelFrameDevice, View};
use warp11_runtime::mandel_frame_layout as layout;

const AXI_BASE: i64 = 0xB000_0000;
const PL_CLOCK_MHZ: f64 = 166.666_672; // set in mandelframe_bd_bd.tcl

fn q4_28(v: f64) -> u32 {
    (v * f64::from(1u32 << 28)) as i64 as u32
}

fn udmabuf_attr(name: &str, value: &str) -> std::io::Result<()> {
    std::fs::write(format!("/sys/class/u-dma-buf/udmabuf0/{name}"), value)
}

fn udmabuf_phys_addr() -> Option<u64> {
    let text = std::fs::read_to_string("/sys/class/u-dma-buf/udmabuf0/phys_addr").ok()?;
    let trimmed = text.trim().trim_start_matches("0x");
    u64::from_str_radix(trimmed, 16).ok()
}

fn mb_per_s(bytes: usize, ms: f64) -> f64 {
    bytes as f64 / 1e6 / (ms / 1e3)
}

/// The dtbo's uio node, found by name — the no-root register path.
fn find_uio(name: &str) -> Option<String> {
    let entries = std::fs::read_dir("/sys/class/uio").ok()?;
    for entry in entries.flatten() {
        if let Ok(n) = std::fs::read_to_string(entry.path().join("name")) {
            if n.trim() == name {
                return Some(format!("/dev/{}", entry.file_name().to_string_lossy()));
            }
        }
    }
    None
}

fn main() {
    let mut args = std::env::args().skip(1);
    let reg_arg = args.next().unwrap_or_else(|| "auto".into());
    let ppm_path = args.next().unwrap_or_else(|| "mandel-frame.ppm".into());
    // Optional view override: cx0 cy0 xspan yspan (floats). Default: the
    // sql-mandelbrot-benchmark view.
    let mut f = |d: f64| args.next().map_or(d, |s| s.parse().expect("view arg parses"));
    let (cx0, cy0, xspan, yspan) = (f(-2.5), f(-1.0), f(3.5), f(2.0));

    let fb_base: u64 = udmabuf_phys_addr().unwrap_or_else(|| {
        eprintln!("no udmabuf0 phys_addr — is the u-dma-buf module loaded?");
        exit(1);
    });

    let (device_path, reg_offset) = if reg_arg == "auto" {
        let uio = find_uio("mandelframe").unwrap_or_else(|| {
            eprintln!("no uio node named 'mandelframe' — is the mandel-frame app loaded?");
            exit(1);
        });
        (uio, 0i64)
    } else if reg_arg == "/dev/mem" {
        ("/dev/mem".to_string(), AXI_BASE)
    } else {
        (reg_arg, 0i64)
    };

    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&device_path)
        .unwrap_or_else(|e| {
            eprintln!("cannot open {device_path}: {e}");
            exit(1);
        });

    let window =
        MmapWindow::open(&reg_file, reg_offset, layout::APERTURE_BYTES).unwrap_or_else(|e| {
            eprintln!("register mmap of {device_path} failed: {e:?}");
            exit(1);
        });

    let mut device = MandelFrameDevice::open(window).unwrap_or_else(|e| {
        eprintln!("device open failed: {e:?} — wrong bitstream loaded, or wrong base?");
        exit(1);
    });
    println!("ID ok (0x{:08X}), fb base 0x{fb_base:08X}", layout::ID_MAGIC);

    println!("view: cx0={cx0} cy0={cy0} xspan={xspan} yspan={yspan}");
    let view = View {
        cx_origin: q4_28(cx0),
        cy_origin: q4_28(cy0),
        dx: q4_28(xspan / layout::FRAME_WIDTH as f64),
        dy: q4_28(yspan / layout::FRAME_HEIGHT as f64),
    };

    // ---- fabric compute, rendered twice: the pod is re-renderable ----
    let mut render = |label: &str| -> u32 {
        let wall = Instant::now();
        device.start_render(view, fb_base as u32).expect("view programs");
        device.wait_done(10_000_000).unwrap_or_else(|e| {
            eprintln!("frameDone never rose: {e:?}");
            exit(1);
        });
        let wall_ms = wall.elapsed().as_secs_f64() * 1e3;
        let cycles = device.last_frame_cycles().expect("cycles read");
        let fabric_ms = f64::from(cycles) / (PL_CLOCK_MHZ * 1_000.0);
        println!("{label}: {cycles} cycles = {fabric_ms:.3} ms fabric ({wall_ms:.3} ms host wall incl. polling)");
        cycles
    };
    let _first = render("render 1");
    let cycles = render("render 2");
    let fabric_ms = f64::from(cycles) / (PL_CLOCK_MHZ * 1_000.0);

    // ---- fabric → host, path A: O_SYNC udmabuf (uncached baseline) ----
    // The framebuffer IS udmabuf0 (fb_base = its phys_addr), so both readback
    // paths map the same bytes; O_SYNC selects the uncached device mapping.
    let osync_fb = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open("/dev/udmabuf0")
        .expect("open /dev/udmabuf0 (O_SYNC)");
    let osync_map = MmapWindow::open(&osync_fb, 0, layout::FB_BYTES).expect("uncached fb mmap");
    let t = Instant::now();
    let frame_osync = osync_map.bytes().to_vec();
    let osync_ms = t.elapsed().as_secs_f64() * 1e3;
    println!(
        "readback uncached (O_SYNC): {osync_ms:.3} ms  ({:.1} MB/s)",
        mb_per_s(layout::FB_BYTES, osync_ms)
    );
    println!(
        "end-to-end (fabric + uncached readback): {:.3} ms   [Kotlin: 3.29 fabric + ~1 dma ≈ 4.3]",
        fabric_ms + osync_ms
    );

    // ---- path B: cached udmabuf mmap + sync_for_cpu (the fast path) ----
    // Needs write access to the sync sysfs attrs; degrade gracefully if the
    // board's perms say no (the one-line udev fix is a board-setup item).
    let frame = match udmabuf_attr("sync_offset", "0")
        .and_then(|()| udmabuf_attr("sync_size", &layout::FB_BYTES.to_string()))
        .and_then(|()| udmabuf_attr("sync_direction", "2")) // DMA_FROM_DEVICE
    {
        Ok(()) => {
            let cached_file = OpenOptions::new()
                .read(true)
                .write(true)
                .open("/dev/udmabuf0")
                .expect("open /dev/udmabuf0");
            let cached_map =
                MmapWindow::open(&cached_file, 0, layout::FB_BYTES).expect("cached fb mmap");

            // A warmed destination, so page faults are paid once — then a
            // cold and a warm run, matching how a render loop would call it.
            let mut frame_cached = vec![0u8; layout::FB_BYTES];
            let mut cached_ms = 0f64;
            for run in ["cold", "warm"] {
                let t = Instant::now();
                udmabuf_attr("sync_for_cpu", "1").expect("sync_for_cpu");
                frame_cached.copy_from_slice(&cached_map.bytes()[..layout::FB_BYTES]);
                cached_ms = t.elapsed().as_secs_f64() * 1e3;
                println!(
                    "readback cached+sync ({run}): {cached_ms:.3} ms  ({:.1} MB/s)",
                    mb_per_s(layout::FB_BYTES, cached_ms)
                );
            }
            // Pure-read bandwidth of the same mapping (a u64 sum, no stores):
            // separates the mapping's read speed from memcpy's character.
            let t = Instant::now();
            let sum: u64 = cached_map.bytes()[..layout::FB_BYTES]
                .chunks_exact(8)
                .map(|c| u64::from_le_bytes(c.try_into().unwrap()))
                .fold(0u64, u64::wrapping_add);
            let sum_ms = t.elapsed().as_secs_f64() * 1e3;
            println!(
                "cached pure-read (sum):     {sum_ms:.3} ms  ({:.1} MB/s, checksum {sum:#x})",
                mb_per_s(layout::FB_BYTES, sum_ms)
            );
            println!(
                "end-to-end (fabric + fast readback): {:.3} ms",
                fabric_ms + cached_ms
            );
            assert_eq!(frame_cached, frame_osync, "the two readback paths must agree");
            frame_cached
        }
        Err(e) => {
            println!("cached-path skipped: sync sysfs not writable ({e}) — udev fix pending");
            frame_osync
        }
    };

    // ---- path C: warp11-dma GDMA into a cached kernel buffer ----
    // The Kotlin readback path, ported: root-only device, so degrade
    // gracefully when run as plain ubuntu.
    match Warp11Dma::open() {
        Ok(mut dma) => {
            let mut frame_dma = vec![0u8; layout::FB_BYTES];
            let mut total_ms = 0f64;
            for run in ["cold", "warm"] {
                let t = Instant::now();
                let (dma_ms, copy_ms) = dma
                    .read_phys_into(fb_base, &mut frame_dma)
                    .expect("dma read");
                total_ms = t.elapsed().as_secs_f64() * 1e3;
                println!(
                    "readback warp11-dma ({run}): {total_ms:.3} ms  ({:.1} MB/s)  [gdma {dma_ms:.3} + copy {copy_ms:.3}]",
                    mb_per_s(layout::FB_BYTES, total_ms)
                );
            }
            println!(
                "end-to-end (fabric + dma readback):  {:.3} ms   [Kotlin C bench: 1 MB = 0.899 ms incl. read]",
                fabric_ms + total_ms
            );
            assert_eq!(frame_dma, frame, "dma and uncached reads must agree");
        }
        Err(e) => println!("warp11-dma skipped: {e:?} (root-only, like /dev/mem)"),
    }

    // ---- verify against the software twin ----
    println!("computing the software twin (1408x800, up to 256 iters)...");
    let expected = frame_twin(
        layout::WIDTH_PADDED,
        layout::FRAME_HEIGHT,
        layout::MAX_ITER,
        view.cx_origin,
        view.cy_origin,
        view.dx,
        view.dy,
    );
    let mismatches = frame
        .iter()
        .zip(expected.iter())
        .filter(|(a, b)| a != b)
        .count();
    println!("twin mismatches: {mismatches} of {} bytes", layout::FB_BYTES);

    // PPM, cropped to the real width.
    let mut ppm = format!(
        "P2\n{} {}\n{}\n",
        layout::FRAME_WIDTH,
        layout::FRAME_HEIGHT,
        layout::MAX_ITER
    );
    for row in 0..layout::FRAME_HEIGHT {
        let line: Vec<String> = (0..layout::FRAME_WIDTH)
            .map(|col| {
                let v = u32::from(frame[row * layout::WIDTH_PADDED + col]);
                let shade = if v >= layout::MAX_ITER - 1 { 0 } else { v };
                shade.to_string()
            })
            .collect();
        ppm.push_str(&line.join(" "));
        ppm.push('\n');
    }
    std::fs::write(&ppm_path, ppm).expect("ppm writes");
    println!("wrote {ppm_path}");

    if mismatches != 0 {
        eprintln!("FAIL: the fabric and the software twin disagree");
        exit(1);
    }
    println!("FIRST LIGHT OK: bit-exact against the twin");
}
