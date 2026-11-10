//! The Mandelbrot board daemon: the frame's live face on the Zenoh mesh.
//! Runs on the KV260 after the frame's app is loaded (`xmutil loadapp
//! mandelbrot-batch`), listens as a Zenoh peer, and speaks two keys:
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
//! It stands on the generated design's own contract and nothing written by
//! hand. The seam the build directory wrote (`--layout`) names the view's
//! registers and states what the fabric was built with — the frame's width,
//! the chunk, the iteration cap, the clock; the batch contract (`BatchDevice`)
//! runs a render: the chunk count in, the rows into the arena the app's
//! overlay made, the fabric's own cycle count back.
//!
//!   mandel-daemon --board mandelbrot_batch --layout mandelbrot_batch_layout.rs [--height 800] [--listen tcp/0.0.0.0:7448]
//!   mandel-daemon --board … --layout … check
//!   mandel-daemon --board … --layout … bench
//!
//! `check` is the bring-up: one render of the default view, compared byte for
//! byte against the software twin, and its fabric cycles — then exit, 0 only
//! if bit-exact.
//! `bench` is the numbers the README quotes: two renders, the frame read
//! back three ways (uncached, cached and synced cold and warm, a pure read),
//! the mappings asserted equal, the twin compared.
//!
//! The accelerator is demand-driven, not free-running: its write master
//! moves only between `start` and done. So there is no arm gate to manage —
//! the loop blocks until a render is asked for. It does still hold the rule
//! that matters: never tear the bitstream down mid-render (see the unit file).
//!
//! Requests coalesce. Drag-release sends one view, but a client that sends
//! several while a render is in flight wants the LAST one, not a queue of
//! obsolete frames — so the loop drains to the newest request before starting.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::process::exit;
use std::sync::mpsc;
use std::time::Instant;
use warp11_host::mandel_frame_twin::pixel_twin;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_host::seam::{read_const, read_layout, resolve};
use warp11_host::udmabuf::{find_uio, Udmabuf};
use warp11_runtime::batch::{BatchDevice, BatchError};
use warp11_runtime::frame::FrameShape;
use zenoh::qos::CongestionControl;
use zenoh::Wait;

/// Generous: a render is ~700k cycles, and each poll is one register read.
const POLL_BUDGET: usize = 50_000_000;

/// The frame's height when nothing says otherwise: the live view's. The
/// fabric does not know it — a height is only how many chunks are asked for.
const DEFAULT_HEIGHT: usize = 800;

/// The view the fabric renders at startup, and the one a client gets if it
/// asks for nothing — the whole set.
const DEFAULT_VIEW: (f64, f64, f64, f64) = (-2.5, -1.0, 3.5, 2.0);


fn fail(message: impl std::fmt::Display) -> ! {
    eprintln!("{message}");
    exit(1)
}

fn usage() -> ! {
    fail("usage: mandel-daemon --board <uio name> --layout <seam.rs> [--height 800] [--listen tcp/0.0.0.0:7448] [check | bench]")
}

struct Args {
    board: String,
    layout: PathBuf,
    height: usize,
    listen: String,
    check: bool,
    bench: bool,
}

fn parse_args() -> Args {
    let mut board = None;
    let mut layout = None;
    let mut height = DEFAULT_HEIGHT;
    let mut listen = "tcp/0.0.0.0:7448".to_string();
    let mut check = false;
    let mut bench = false;
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--board" => board = args.next(),
            "--layout" => layout = args.next().map(PathBuf::from),
            "--height" => height = args.next().and_then(|h| h.parse().ok()).filter(|&h| h > 0).unwrap_or_else(|| usage()),
            "--listen" => listen = args.next().unwrap_or_else(|| usage()),
            "check" => check = true,
            "bench" => bench = true,
            _ => usage(),
        }
    }
    Args {
        board: board.unwrap_or_else(|| usage()),
        layout: layout.unwrap_or_else(|| usage()),
        height,
        listen,
        check,
        bench,
    }
}

/// A view in Q4.28 bit patterns: origin and per-pixel step, exactly the four
/// controls the fabric reads.
#[derive(Clone, Copy, Debug)]
struct View {
    cx_origin: u32,
    cy_origin: u32,
    dx: u32,
    dy: u32,
}

/// A coordinate as the fabric reads it: fixed point with the seam's
/// fraction bits.
fn fixed(v: f64, fraction_bits: u32) -> u32 {
    (v * (1u64 << fraction_bits) as f64) as i64 as u32
}

/// What the seam says about the frame: where the view's controls are, and
/// what the fabric was built with.
struct Seam {
    cx_origin: usize,
    cy_origin: usize,
    dx: usize,
    dy: usize,
    width: usize,
    chunk: usize,
    max_iterations: u32,
    fraction_bits: u32,
    fabric_hz: f64,
    layout_hash: Option<u32>,
}

fn constant(layout: &Path, name: &str) -> u32 {
    match read_const(layout, name) {
        Ok(Some(v)) => v,
        Ok(None) => fail(format!("{}: no constant {name} — not the frame's seam, or one built before the design stated it?", layout.display())),
        Err(e) => fail(e),
    }
}

fn read_seam(layout: &Path) -> Seam {
    let (registers, layout_hash) = read_layout(layout).unwrap_or_else(|e| fail(e));
    let offset = |name: &str| resolve(name, &registers).unwrap_or_else(|e| fail(e));
    Seam {
        cx_origin: offset("cxOrigin"),
        cy_origin: offset("cyOrigin"),
        dx: offset("dx"),
        dy: offset("dy"),
        width: constant(layout, "FRAME_WIDTH") as usize,
        chunk: constant(layout, "CHUNK_PIXELS") as usize,
        max_iterations: constant(layout, "MAX_ITERATIONS"),
        fraction_bits: constant(layout, "FRACTION_BITS"),
        fabric_hz: f64::from(constant(layout, "FABRIC_HZ")),
        layout_hash,
    }
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

/// The published frame: an eight-word header then the cropped pixels. The
/// view is echoed back so a client can tell which request this answers —
/// with coalescing, the reply to your last put may be the reply to someone
/// else's. `Warp11.MandelView/Bus.fs` decodes exactly this.
fn encode_frame(view: View, cycles: u32, shape: &FrameShape, max_iterations: u32, pixels: &[u8]) -> Vec<u8> {
    let header = [
        view.cx_origin,
        view.cy_origin,
        view.dx,
        view.dy,
        cycles,
        shape.width as u32,
        shape.height as u32,
        max_iterations,
    ];
    let mut payload = Vec::with_capacity(header.len() * 4 + pixels.len());
    for word in header {
        payload.extend_from_slice(&word.to_le_bytes());
    }
    payload.extend_from_slice(pixels);
    payload
}

fn describe<E: std::fmt::Debug>(e: BatchError<E>) -> String {
    match e {
        BatchError::WrongId { found } => format!("not a batch top: ID reads 0x{found:08X} — wrong bitstream?"),
        BatchError::WrongLayout { found, expected } => {
            format!("layout mismatch: the fabric says 0x{found:04X}, the seam 0x{expected:04X} — rebuild the bitstream or copy the seam the build wrote")
        }
        BatchError::NeverFinished => "busy never cleared".into(),
        BatchError::Window(e) => format!("{e:?}"),
    }
}

/// The accelerator as the daemon holds it: the batch device over the
/// registers, and the arena the rows land in.
struct Fabric {
    device: BatchDevice<MmapWindow>,
    arena: Udmabuf,
    arena_map: MmapWindow,
    seam: Seam,
    shape: FrameShape,
}

impl Fabric {
    fn open(args: &Args) -> Fabric {
        let seam = read_seam(&args.layout);
        let shape = FrameShape::new(seam.width, args.height, seam.chunk);

        let uio = find_uio(&args.board)
            .unwrap_or_else(|e| fail(e))
            .unwrap_or_else(|| fail(format!("no uio device called '{}' — is the app loaded?", args.board)));
        let arena_name = format!("udmabuf-{}", args.board.replace('_', "-"));
        let arena = Udmabuf::find(&arena_name)
            .unwrap_or_else(|e| fail(e))
            .unwrap_or_else(|| fail(format!("no u-dma-buf called '{arena_name}' — is the app loaded?")));

        let regs_file = OpenOptions::new()
            .read(true)
            .write(true)
            .custom_flags(O_SYNC)
            .open(&uio)
            .unwrap_or_else(|e| fail(format!("{}: {e}", uio.display())));
        let window = MmapWindow::open(&regs_file, 0, 0x1000).unwrap_or_else(|e| fail(format!("register mmap: {e:?}")));
        let device = BatchDevice::open(window, seam.layout_hash).unwrap_or_else(|e| fail(describe(e)));

        // The cached mapping plus an explicit sync_for_cpu after each render:
        // the fabric's master does not snoop.
        let arena_file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(&arena.dev_path)
            .unwrap_or_else(|e| fail(format!("{}: {e}", arena.dev_path.display())));
        let arena_map = MmapWindow::open(&arena_file, 0, arena.size).unwrap_or_else(|e| fail(format!("arena mmap: {e:?}")));

        if shape.bytes_back() > arena.size {
            fail(format!("a {}x{} frame writes {} bytes; the arena holds {}", shape.width, shape.height, shape.bytes_back(), arena.size));
        }
        if arena.phys_addr + arena.size as u64 > u64::from(u32::MAX) {
            fail("the arena sits above 4 GB, which a 32-bit master cannot reach");
        }

        Fabric { device, arena, arena_map, seam, shape }
    }

    fn set(&mut self, offset: usize, value: u32) {
        self.device.set(offset, value).unwrap_or_else(|e| fail(format!("register write at 0x{offset:x}: {e:?}")));
    }

    /// One render, start to cropped pixels in `out`. Returns the fabric cycle
    /// count, or `None` if it never finished — the daemon refuses to start on
    /// that at boot and skips the frame in the loop.
    fn render(&mut self, view: View, out: &mut [u8]) -> Option<u32> {
        let wall = Instant::now();
        self.set(self.seam.cx_origin, view.cx_origin);
        self.set(self.seam.cy_origin, view.cy_origin);
        self.set(self.seam.dx, view.dx);
        self.set(self.seam.dy, view.dy);
        // The counted path reads nothing: the source address is unused.
        self.device
            .start(0, self.arena.phys_addr as u32, self.shape.padded_chunks as u32)
            .unwrap_or_else(|e| fail(format!("start: {e:?}")));
        if let Err(e) = self.device.wait_done(POLL_BUDGET, |_| Ok(())) {
            eprintln!("the render never finished: {}", describe(e));
            return None;
        }
        let wall_ms = wall.elapsed().as_secs_f64() * 1e3;
        let cycles = self.device.cycles().unwrap_or_else(|e| fail(format!("cycles: {e:?}")));
        self.device.acknowledge().unwrap_or_else(|e| fail(format!("acknowledge: {e:?}")));
        self.arena
            .sync_for_cpu(0, self.shape.bytes_back())
            .unwrap_or_else(|e| fail(format!("sync_for_cpu: {e}")));
        self.shape.crop_into(&self.arena_map.bytes()[..self.shape.bytes_back()], out);
        let fabric_ms = f64::from(cycles) / self.seam.fabric_hz * 1e3;
        println!("render: {cycles} cycles = {fabric_ms:.3} ms fabric ({wall_ms:.3} ms wall)");
        Some(cycles)
    }
}

/// The frame the fabric should have drawn, from the whole-pixel twin: each
/// pixel's coordinate stepped the way the fabric steps it, wrapping in 32 bits.
fn twin(seam: &Seam, shape: &FrameShape, view: View) -> Vec<u8> {
    let mut frame = Vec::with_capacity(shape.pixels());
    for r in 0..shape.height {
        let cy = view.cy_origin.wrapping_add((r as u32).wrapping_mul(view.dy));
        for c in 0..shape.width {
            let cx = view.cx_origin.wrapping_add((c as u32).wrapping_mul(view.dx));
            frame.push(pixel_twin(seam.fraction_bits, seam.max_iterations, cx, cy) as u8);
        }
    }
    frame
}

/// The bring-up: one render against the twin, and its cycles. Exits 0 only
/// when every pixel matches.
fn check(fabric: &mut Fabric, view: View) -> ! {
    let mut drawn = vec![0u8; fabric.shape.pixels()];
    let Some(cycles) = fabric.render(view, &mut drawn) else {
        fail("check: the render never finished");
    };
    let started = Instant::now();
    let expected = twin(&fabric.seam, &fabric.shape, view);
    println!("twin: {:.1} s on the host", started.elapsed().as_secs_f64());

    let width = fabric.shape.width;
    let wrong: Vec<usize> = (0..expected.len()).filter(|&i| drawn[i] != expected[i]).collect();
    if let Some(&first) = wrong.first() {
        let rows: std::collections::BTreeSet<usize> = wrong.iter().map(|i| i / width).collect();
        println!(
            "check: {} of {} pixels differ, on {} rows; first at ({}, {}): fabric {} twin {}",
            wrong.len(),
            expected.len(),
            rows.len(),
            first / width,
            first % width,
            drawn[first],
            expected[first]
        );
    } else {
        println!("check: {}x{} bit-exact against the twin", width, fabric.shape.height);
    }
    println!("cycles: {cycles} = {:.3} ms fabric", f64::from(cycles) / fabric.seam.fabric_hz * 1e3);
    exit(if wrong.is_empty() { 0 } else { 1 })
}

/// Megabytes a second for `bytes` in `ms`.
fn mb_per_s(bytes: usize, ms: f64) -> f64 {
    bytes as f64 / 1e6 / (ms / 1e3)
}

/// The numbers the README quotes, measured: two renders, the frame read back
/// three ways, the twin compared. Every readback reads the whole of what the
/// fabric wrote, padding included, as the daemon's does.
fn bench(fabric: &mut Fabric, view: View) -> ! {
    let mut drawn = vec![0u8; fabric.shape.pixels()];
    let mut cycles = 0;
    for label in ["render 1", "render 2"] {
        let wall = Instant::now();
        let Some(c) = fabric.render(view, &mut drawn) else {
            fail("bench: the render never finished");
        };
        cycles = c;
        println!("{label}: {c} cycles, {:.3} ms wall start to cropped frame", wall.elapsed().as_secs_f64() * 1e3);
    }
    let fabric_ms = f64::from(cycles) / fabric.seam.fabric_hz * 1e3;
    let bytes = fabric.shape.bytes_back();

    let uncached_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&fabric.arena.dev_path)
        .unwrap_or_else(|e| fail(format!("{}: {e}", fabric.arena.dev_path.display())));
    let uncached = MmapWindow::open(&uncached_file, 0, bytes).unwrap_or_else(|e| fail(format!("uncached mmap: {e:?}")));
    let t = Instant::now();
    let by_uncached = uncached.bytes()[..bytes].to_vec();
    let uncached_ms = t.elapsed().as_secs_f64() * 1e3;
    println!("readback uncached (O_SYNC):  {uncached_ms:.3} ms  {:.0} MB/s", mb_per_s(bytes, uncached_ms));

    let mut by_cached = vec![0u8; bytes];
    let mut cached_ms = 0.0;
    for run in ["cold", "warm"] {
        let t = Instant::now();
        fabric.arena.sync_for_cpu(0, bytes).unwrap_or_else(|e| fail(format!("sync_for_cpu: {e}")));
        by_cached.copy_from_slice(&fabric.arena_map.bytes()[..bytes]);
        cached_ms = t.elapsed().as_secs_f64() * 1e3;
        println!("readback cached+sync ({run}): {cached_ms:.3} ms  {:.0} MB/s", mb_per_s(bytes, cached_ms));
    }
    let t = Instant::now();
    let sum = fabric.arena_map.bytes()[..bytes]
        .chunks_exact(8)
        .map(|c| u64::from_le_bytes(c.try_into().unwrap()))
        .fold(0u64, u64::wrapping_add);
    let sum_ms = t.elapsed().as_secs_f64() * 1e3;
    println!("cached pure read (a u64 sum): {sum_ms:.3} ms  {:.0} MB/s  (sum {sum:#x})", mb_per_s(bytes, sum_ms));
    if by_cached != by_uncached {
        fail("bench: the cached and uncached mappings disagree");
    }
    println!("{bytes} bytes read back, both mappings identical");
    println!("end to end: {:.3} ms fabric + {cached_ms:.3} ms warm readback = {:.3} ms", fabric_ms, fabric_ms + cached_ms);
    println!("end to end, uncached: {:.3} ms", fabric_ms + uncached_ms);

    let expected = twin(&fabric.seam, &fabric.shape, view);
    let wrong = (0..expected.len()).filter(|&i| drawn[i] != expected[i]).count();
    println!("twin: {wrong} of {} pixels differ", expected.len());
    exit(if wrong == 0 { 0 } else { 1 })
}

fn main() {
    let args = parse_args();
    let mut fabric = Fabric::open(&args);
    let mut cropped = vec![0u8; fabric.shape.pixels()];

    let (cx0, cy0, xspan, yspan) = DEFAULT_VIEW;
    let bits = fabric.seam.fraction_bits;
    let default_view = View {
        cx_origin: fixed(cx0, bits),
        cy_origin: fixed(cy0, bits),
        dx: fixed(xspan / fabric.shape.width as f64, bits),
        dy: fixed(yspan / fabric.shape.height as f64, bits),
    };
    if args.check {
        check(&mut fabric, default_view);
    }
    if args.bench {
        bench(&mut fabric, default_view);
    }
    // One render before any client is served. The ID register proves the
    // aperture; only a completed render proves the fabric is clocked and the
    // write path reaches DDR. Cheap — and a wrong PL clock shows up as a wall
    // time that disagrees with the cycle count.
    if fabric.render(default_view, &mut cropped).is_none() {
        fail("startup render failed — refusing to serve");
    }
    println!("startup render ok: the fabric is clocked and reaching DDR");

    // ---- the mesh ----
    let mut config = zenoh::Config::default();
    config
        .insert_json5("listen/endpoints", &format!("[\"{}\"]", args.listen))
        .expect("listen endpoint");
    let session = zenoh::open(config).wait().unwrap_or_else(|e| fail(format!("zenoh open failed: {e}")));
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

    println!(
        "mandel-daemon up: {}x{} frame, arena 0x{:08X}, listening on {}",
        fabric.shape.width, fabric.shape.height, fabric.arena.phys_addr, args.listen
    );

    // ---- the loop: block for a request, coalesce, render, publish ----
    loop {
        let Ok(first) = request_recv.recv() else {
            fail("control channel closed");
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

        let Some(cycles) = fabric.render(view, &mut cropped) else {
            continue;
        };

        let _ = frames
            .put(encode_frame(view, cycles, &fabric.shape, fabric.seam.max_iterations, &cropped))
            .wait();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_round_trips_through_the_header() {
        let view = View {
            cx_origin: 0xF000_0000,
            cy_origin: 0x1234_5678,
            dx: 0x0000_0111,
            dy: 0x0000_0222,
        };
        let shape = FrameShape::new(1400, 800, 128);
        let pixels: Vec<u8> = (0..1000u32).map(|i| i as u8).collect();
        let payload = encode_frame(view, 548_809, &shape, 256, &pixels);

        let word = |i: usize| u32::from_le_bytes(payload[i * 4..i * 4 + 4].try_into().unwrap());
        assert_eq!(word(0), view.cx_origin);
        assert_eq!(word(1), view.cy_origin);
        assert_eq!(word(2), view.dx);
        assert_eq!(word(3), view.dy);
        assert_eq!(word(4), 548_809);
        assert_eq!(word(5), 1400);
        assert_eq!(word(6), 800);
        assert_eq!(word(7), 256);
        assert_eq!(&payload[32..], &pixels[..]);
    }

    #[test]
    fn a_render_request_decodes_to_the_view_that_was_sent() {
        // The other half of the same seam: what MandelView puts on
        // ctl/render must arrive as the view it meant.
        let view = View {
            cx_origin: fixed(-2.5, 28),
            cy_origin: fixed(-1.0, 28),
            dx: fixed(3.5 / 1400.0, 28),
            dy: fixed(2.0 / 800.0, 28),
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
        assert!(decode_view(&wire[..12]).is_none(), "a short request is refused, not read past");
    }

    /// The daemon reads the seam the build directory writes; this is that
    /// file's shape, so a renamed register or a dropped fact fails here
    /// rather than on the board.
    #[test]
    fn the_seam_the_build_writes_resolves() {
        let dir = std::env::temp_dir().join(format!("mandel-daemon-seam-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("mandelbrot_batch_layout.rs");
        std::fs::write(&path, SEAM).unwrap();
        let seam = read_seam(&path);
        assert_eq!((seam.cx_origin, seam.cy_origin, seam.dx, seam.dy), (0x20, 0x24, 0x28, 0x2c));
        assert_eq!((seam.width, seam.chunk, seam.max_iterations, seam.fraction_bits), (1400, 128, 256, 28));
        assert_eq!(seam.fabric_hz, 166_666_672.0);
        std::fs::remove_dir_all(&dir).ok();
    }

    const SEAM: &str = include_str!("../testdata/mandelbrot_batch_layout.rs");
}
