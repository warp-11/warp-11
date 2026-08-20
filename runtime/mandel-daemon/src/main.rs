//! The Mandelbrot board daemon: the frame accelerator's live face on the
//! Zenoh mesh. Runs on the KV260 after `xmutil loadapp mandel-frame`, listens
//! as a Zenoh peer, and speaks two keys:
//!
//!   warp11/mandel/ctl/render    payload = a view, four Q4.28 u32 LE:
//!                               [cx_origin][cy_origin][dx][dy]
//!   warp11/mandel/frame         published when a render completes,
//!                               CongestionControl::Drop (a stale frame is
//!                               worth nothing, and each is ~1.1 MB):
//!                               [cx_origin][cy_origin][dx][dy]  the view
//!                                   this frame ANSWERS — the client matches
//!                                   it against what it asked for rather than
//!                                   assuming the reply is to its last request
//!                               [cycles][width][height][max_iter]  all u32 LE
//!                               [width * height bytes]  escape counts,
//!                                   row-major, CROPPED — the fabric's row
//!                                   padding is a fabric detail and stops here
//!
//! Unlike gol-fs this accelerator is demand-driven, not free-running: the
//! write master moves only between `start` and `frameDone`. So there is no
//! conflate handshake to arbitrate and no arm gate to manage — the loop
//! blocks until a render is asked for. It does still hold the rule that
//! matters: never tear the bitstream down mid-render (see the unit file).
//!
//! Requests coalesce. Drag-release sends one view, but a client that sends
//! several while a render is in flight wants the LAST one, not a queue of
//! obsolete frames — so the loop drains to the newest request before starting.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use std::sync::mpsc;
use std::time::Instant;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_runtime::mandel_frame::{MandelFrameDevice, View};
use warp11_runtime::mandel_frame_layout as layout;
use zenoh::qos::CongestionControl;
use zenoh::Wait;

const PL_CLOCK_HZ: f64 = 166_666_672.0; // mandelframe_bd_bd.tcl PL0
/// Generous: a render is ~550k cycles, and each poll is one register read.
const POLL_BUDGET: usize = 10_000_000;

/// The view the fabric renders at startup, and the one a client gets if it
/// asks for nothing — the whole set, the same rectangle
/// `mandel_frame_first_light` defaults to.
const DEFAULT_VIEW: (f64, f64, f64, f64) = (-2.5, -1.0, 3.5, 2.0);

fn q4_28(v: f64) -> u32 {
    (v * f64::from(1u32 << 28)) as i64 as u32
}

fn find_uio(name: &str) -> Option<String> {
    for entry in std::fs::read_dir("/sys/class/uio").ok()?.flatten() {
        if let Ok(n) = std::fs::read_to_string(entry.path().join("name")) {
            if n.trim() == name {
                return Some(format!("/dev/{}", entry.file_name().to_string_lossy()));
            }
        }
    }
    None
}

fn udmabuf_attr(name: &str, value: &str) -> std::io::Result<()> {
    std::fs::write(format!("/sys/class/u-dma-buf/udmabuf0/{name}"), value)
}

fn udmabuf_phys_addr() -> Option<u64> {
    let text = std::fs::read_to_string("/sys/class/u-dma-buf/udmabuf0/phys_addr").ok()?;
    u64::from_str_radix(text.trim().trim_start_matches("0x"), 16).ok()
}

fn decode_view(payload: &[u8]) -> Option<View> {
    if payload.len() < 16 {
        return None;
    }
    let word = |i: usize| u32::from_le_bytes(payload[i * 4..i * 4 + 4].try_into().unwrap());
    Some(View {
        cx_origin: word(0),
        cy_origin: word(1),
        dx: word(2),
        dy: word(3),
    })
}

/// The fabric writes rows padded to `WIDTH_PADDED`; the wire carries only the
/// real pixels. Done here rather than in the view because the padding exists
/// for the coalescer's 16-byte beats and means nothing to a client.
fn crop_rows(raw: &[u8], out: &mut Vec<u8>) {
    out.clear();
    for row in 0..layout::FRAME_HEIGHT {
        let start = row * layout::WIDTH_PADDED;
        out.extend_from_slice(&raw[start..start + layout::FRAME_WIDTH]);
    }
}

/// The published frame: an eight-word header then the cropped pixels. The
/// view is echoed back so a client can tell which request this answers —
/// with coalescing, the reply to your last put may be the reply to someone
/// else's. `Warp11.MandelView/Bus.fs` decodes exactly this.
fn encode_frame(view: View, cycles: u32, pixels: &[u8]) -> Vec<u8> {
    let header = [
        view.cx_origin,
        view.cy_origin,
        view.dx,
        view.dy,
        cycles,
        layout::FRAME_WIDTH as u32,
        layout::FRAME_HEIGHT as u32,
        layout::MAX_ITER,
    ];
    let mut payload = Vec::with_capacity(header.len() * 4 + pixels.len());
    for word in header {
        payload.extend_from_slice(&word.to_le_bytes());
    }
    payload.extend_from_slice(pixels);
    payload
}

/// One render, start to cropped pixels in `out`. Returns the fabric cycle
/// count, or `None` if `frameDone` never rose — the daemon refuses to start
/// on that at boot and skips the frame in the loop.
fn render(
    device: &mut MandelFrameDevice<MmapWindow>,
    fb: &MmapWindow,
    fb_base: u64,
    view: View,
    out: &mut Vec<u8>,
) -> Option<u32> {
    let wall = Instant::now();
    device
        .start_render(view, fb_base as u32)
        .expect("view programs");
    if let Err(e) = device.wait_done(POLL_BUDGET) {
        eprintln!("frameDone never rose: {e:?}");
        return None;
    }
    let wall_ms = wall.elapsed().as_secs_f64() * 1e3;
    let cycles = device.last_frame_cycles().expect("cycles read");
    let fabric_ms = f64::from(cycles) / PL_CLOCK_HZ * 1e3;
    udmabuf_attr("sync_for_cpu", "1").expect("sync_for_cpu");
    crop_rows(fb.bytes(), out);
    println!("render: {cycles} cycles = {fabric_ms:.3} ms fabric ({wall_ms:.3} ms wall)");
    Some(cycles)
}

fn main() {
    let listen = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "tcp/0.0.0.0:7448".into());

    // ---- the accelerator ----
    let uio = find_uio("mandelframe").unwrap_or_else(|| {
        eprintln!("no uio node named 'mandelframe' — is the mandel-frame app loaded?");
        exit(1);
    });
    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&uio)
        .unwrap_or_else(|e| {
            eprintln!("cannot open {uio}: {e}");
            exit(1);
        });
    let regs = MmapWindow::open(&reg_file, 0, layout::APERTURE_BYTES).unwrap_or_else(|e| {
        eprintln!("register mmap failed: {e:?}");
        exit(1);
    });
    let mut device = MandelFrameDevice::open(regs).unwrap_or_else(|e| {
        eprintln!("device open failed: {e:?} — wrong bitstream loaded?");
        exit(1);
    });

    let fb_base = udmabuf_phys_addr().unwrap_or_else(|| {
        eprintln!("no udmabuf0 phys_addr — is u-dma-buf loaded?");
        exit(1);
    });

    // The cached mapping plus an explicit sync_for_cpu — measured faster than
    // the uncached one for render-then-read, and the two are asserted equal in
    // `mandel_frame_first_light`.
    let fb_file = OpenOptions::new()
        .read(true)
        .write(true)
        .open("/dev/udmabuf0")
        .expect("open /dev/udmabuf0");
    let fb = MmapWindow::open(&fb_file, 0, layout::FB_BYTES).expect("fb mmap");
    udmabuf_attr("sync_direction", "2").expect("sync_direction (udev rule installed?)");
    udmabuf_attr("sync_offset", "0").expect("sync_offset");
    udmabuf_attr("sync_size", &layout::FB_BYTES.to_string()).expect("sync_size");

    let mut cropped = Vec::with_capacity(layout::FRAME_WIDTH * layout::FRAME_HEIGHT);

    let (cx0, cy0, xspan, yspan) = DEFAULT_VIEW;
    let default_view = View {
        cx_origin: q4_28(cx0),
        cy_origin: q4_28(cy0),
        dx: q4_28(xspan / layout::FRAME_WIDTH as f64),
        dy: q4_28(yspan / layout::FRAME_HEIGHT as f64),
    };
    // One render before any client is served. The ID register proves the
    // aperture; only a completed render proves the fabric is clocked and the
    // write path reaches DDR. Cheap, unlike GoL's canary — this design has a
    // cycle counter to judge, so a wrong PL clock shows up as a wall time
    // that disagrees with it.
    if render(&mut device, &fb, fb_base, default_view, &mut cropped).is_none() {
        eprintln!("startup render failed — refusing to serve");
        exit(1);
    }
    println!("startup render ok: the fabric is clocked and reaching DDR");

    // ---- the mesh ----
    let mut config = zenoh::Config::default();
    config
        .insert_json5("listen/endpoints", &format!("[\"{listen}\"]"))
        .expect("listen endpoint");
    let session = zenoh::open(config).wait().unwrap_or_else(|e| {
        eprintln!("zenoh open failed: {e}");
        exit(1);
    });
    let frames = session
        .declare_publisher("warp11/mandel/frame")
        .congestion_control(CongestionControl::Drop)
        .wait()
        .expect("frame publisher");

    // Requests arrive on a Zenoh thread; the register file stays owned by the
    // main loop, so they cross over a channel.
    let (request_send, request_recv) = mpsc::channel::<View>();
    let _ctl = session
        .declare_subscriber("warp11/mandel/ctl/render")
        .callback(move |sample| {
            let payload = sample.payload().to_bytes().into_owned();
            match decode_view(&payload) {
                Some(view) => {
                    let _ = request_send.send(view);
                }
                None => eprintln!("ignoring malformed render request ({} bytes)", payload.len()),
            }
        })
        .wait()
        .expect("render subscriber");

    println!("mandel-daemon up: fb 0x{fb_base:08X}, listening on {listen}");

    // ---- the loop: block for a request, coalesce, render, publish ----
    loop {
        let Ok(first) = request_recv.recv() else {
            eprintln!("control channel closed");
            exit(1);
        };
        // Everything queued behind it is obsolete: a client that dragged
        // twice wants the second view, not both.
        let mut view = first;
        let mut skipped = 0;
        while let Ok(newer) = request_recv.try_recv() {
            view = newer;
            skipped += 1;
        }
        if skipped > 0 {
            println!("coalesced {skipped} superseded request(s)");
        }

        let Some(cycles) = render(&mut device, &fb, fb_base, view, &mut cropped) else {
            continue;
        };

        let _ = frames.put(encode_frame(view, cycles, &cropped)).wait();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Column 255 would be 0xFF under a plain `col as u8`, which collides with
    /// the padding sentinel below and makes a correct crop look wrong. 251 is
    /// the largest prime under 255, so the pattern stays positional (it
    /// repeats only every 251 columns, and 251 does not divide the stride)
    /// while leaving 0xFF exclusively to the padding.
    const PIXEL_MODULUS: usize = 251;
    const PADDING: u8 = 0xFF;

    /// A framebuffer where every byte says where it lives: pixel (row, col)
    /// holds `col % 251`, and the padding columns hold 0xFF. A stride bug
    /// then shows up as a shear — the same failure the Kotlin Mandelbrot's
    /// first render hit — rather than as bytes that merely look plausible.
    fn synthetic_fb() -> Vec<u8> {
        let mut raw = vec![PADDING; layout::FB_BYTES];
        for row in 0..layout::FRAME_HEIGHT {
            for col in 0..layout::FRAME_WIDTH {
                raw[row * layout::WIDTH_PADDED + col] = (col % PIXEL_MODULUS) as u8;
            }
        }
        raw
    }

    #[test]
    fn crop_drops_exactly_the_padding() {
        let mut out = Vec::new();
        crop_rows(&synthetic_fb(), &mut out);

        assert_eq!(out.len(), layout::FRAME_WIDTH * layout::FRAME_HEIGHT);
        // No padding byte survived, and every pixel is at its own index.
        assert!(!out.contains(&PADDING), "a padding byte reached the wire");
        for row in 0..layout::FRAME_HEIGHT {
            for col in 0..layout::FRAME_WIDTH {
                assert_eq!(
                    out[row * layout::FRAME_WIDTH + col],
                    (col % PIXEL_MODULUS) as u8,
                    "pixel ({row}, {col}) came from the wrong offset"
                );
            }
        }
    }

    #[test]
    fn the_shear_a_stride_bug_causes_is_visible_to_that_check() {
        // The negative control. Copying the first width*height bytes — the
        // obvious wrong crop — passes row 0 and shears from row 1 on, so the
        // check above has to be able to see that. If this ever stops holding,
        // `synthetic_fb` has become too uniform to catch anything.
        let raw = synthetic_fb();
        let naive = &raw[..layout::FRAME_WIDTH * layout::FRAME_HEIGHT];
        let row1 = &naive[layout::FRAME_WIDTH..2 * layout::FRAME_WIDTH];
        assert_ne!(
            row1[0], 0,
            "row 1 of a naive crop must not start at column 0, or the padding is invisible"
        );
    }

    #[test]
    fn frame_round_trips_through_the_header() {
        let view = View {
            cx_origin: 0xF000_0000,
            cy_origin: 0x1234_5678,
            dx: 0x0000_0111,
            dy: 0x0000_0222,
        };
        let pixels: Vec<u8> = (0..1000u32).map(|i| i as u8).collect();
        let payload = encode_frame(view, 548_809, &pixels);

        let word = |i: usize| u32::from_le_bytes(payload[i * 4..i * 4 + 4].try_into().unwrap());
        assert_eq!(word(0), view.cx_origin);
        assert_eq!(word(1), view.cy_origin);
        assert_eq!(word(2), view.dx);
        assert_eq!(word(3), view.dy);
        assert_eq!(word(4), 548_809);
        assert_eq!(word(5), layout::FRAME_WIDTH as u32);
        assert_eq!(word(6), layout::FRAME_HEIGHT as u32);
        assert_eq!(word(7), layout::MAX_ITER);
        assert_eq!(&payload[32..], &pixels[..]);
    }

    #[test]
    fn a_render_request_decodes_to_the_view_that_was_sent() {
        // The other half of the same seam: what MandelView puts on
        // ctl/render must arrive as the view it meant.
        let view = View {
            cx_origin: q4_28(-2.5),
            cy_origin: q4_28(-1.0),
            dx: q4_28(3.5 / layout::FRAME_WIDTH as f64),
            dy: q4_28(2.0 / layout::FRAME_HEIGHT as f64),
        };
        let wire: Vec<u8> = [view.cx_origin, view.cy_origin, view.dx, view.dy]
            .iter()
            .flat_map(|w| w.to_le_bytes())
            .collect();

        let back = decode_view(&wire).expect("a 16-byte request decodes");
        assert_eq!(back.cx_origin, view.cx_origin);
        assert_eq!(back.cy_origin, view.cy_origin);
        assert_eq!(back.dx, view.dx);
        assert_eq!(back.dy, view.dy);
        assert!(
            decode_view(&wire[..12]).is_none(),
            "a short request is refused, not read past"
        );
    }
}
