//! A WAV through a drawn design on a board's host memory — or through the
//! same design in the F# Sim, over the batch bridge, with the same driver.
//! Or a frame the design draws: `frame:<width>x<height>` in place of the
//! WAV is the counted path — nothing staged, the count and the view written,
//! the frame read back as a PGM.
//!
//!   warp11_batch in.wav out.wav --board gain_patch_batch --layout gain_patch_batch_layout.rs [--set volume=512]
//!   warp11_batch in.wav out.wav --sim gain.json [--preset kv260] [--fsproj ../hdl/Warp11.Placement/Warp11.Placement.fsproj] [--set volume=512]
//!   warp11_batch frame:1400x800 out.pgm --sim mandelbrot.json --chunk 128 --set cxOrigin=0xE0000000 ...
//!
//! `--chunk` is the design's chunk width in pixels: a frame is drawn in whole
//! chunks, so the width is rounded up to one and the PGM cropped back.
//!
//! On the board `--board` names the uio device the overlay made
//! (`gain_patch_batch`); the arena is the `u-dma-buf` named after it
//! (`udmabuf-gain-patch-batch`), input rows in its first half, output rows
//! in its second. `--layout` is the seam the build directory wrote, read for
//! the controls' offsets and the layout hash; against the Sim the bridge
//! answers those itself.

use std::fs::OpenOptions;
use std::path::PathBuf;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::mmap::MmapWindow;
use warp11_host::seam::{self, parse_number, read_layout};
use warp11_host::udmabuf::{find_uio, Udmabuf};
use warp11_host::wav::{from_sample, read_wav, to_sample, write_wav};
use warp11_runtime::batch::{BatchDevice, BatchError};
use warp11_host::pgm::write_pgm;

/// Rows a burst holds for a stereo boundary: what `frameCount` must be a
/// multiple of. Two rows a 128-bit beat, sixteen beats a burst.
const FRAMES_PER_BURST: usize = 32;
const BYTES_PER_FRAME: usize = 8;
/// Where the rows start in the bridge's fake DDR.
const SIM_SRC: usize = 0x1000;
/// Pixels a beat of a drawn frame carries, and the beats a burst holds —
/// what the count must be a multiple of.
const PIXELS_PER_BEAT: usize = 16;
const BEATS_PER_BURST: usize = 16;

/// What goes into the design: a recording, or a frame for it to draw.
enum Source {
    Wav(PathBuf),
    Frame { width: usize, height: usize },
}

struct Args {
    input: Source,
    output: PathBuf,
    board: Option<String>,
    layout: Option<PathBuf>,
    sim: Option<PathBuf>,
    preset: Option<String>,
    fsproj: PathBuf,
    chunk: usize,
    sets: Vec<(String, u32)>,
}

fn parse_frame(text: &str) -> Option<Source> {
    let size = text.strip_prefix("frame:")?;
    let (w, h) = size.split_once('x')?;
    let width = w.parse().ok().filter(|&w: &usize| w > 0)?;
    let height = h.parse().ok().filter(|&h: &usize| h > 0)?;
    Some(Source::Frame { width, height })
}

fn usage() -> ! {
    eprintln!("usage: warp11_batch (in.wav out.wav | frame:<w>x<h> out.pgm [--chunk <pixels>]) (--board <uio name> --layout <seam.rs> | --sim <design.json> [--preset kv260] [--fsproj <fsproj>]) [--set name=value]...");
    std::process::exit(2)
}

fn parse_args() -> Args {
    let mut args = std::env::args().skip(1);
    let first = args.next().unwrap_or_else(|| usage());
    let input = parse_frame(&first).unwrap_or_else(|| Source::Wav(PathBuf::from(&first)));
    let output = args.next().map(PathBuf::from).unwrap_or_else(|| usage());
    let mut parsed = Args {
        input,
        output,
        board: None,
        layout: None,
        sim: None,
        preset: None,
        fsproj: PathBuf::from("../hdl/Warp11.Placement/Warp11.Placement.fsproj"),
        chunk: PIXELS_PER_BEAT,
        sets: Vec::new(),
    };
    while let Some(flag) = args.next() {
        let mut value = || args.next().unwrap_or_else(|| usage());
        match flag.as_str() {
            "--board" => parsed.board = Some(value()),
            "--layout" => parsed.layout = Some(PathBuf::from(value())),
            "--sim" => parsed.sim = Some(PathBuf::from(value())),
            "--preset" => parsed.preset = Some(value()),
            "--fsproj" => parsed.fsproj = PathBuf::from(value()),
            "--chunk" => {
                parsed.chunk = value().parse().ok().filter(|&c: &usize| c > 0 && c % PIXELS_PER_BEAT == 0).unwrap_or_else(|| usage())
            }
            "--set" => {
                let v = value();
                let (name, number) = v.split_once('=').unwrap_or_else(|| usage());
                let number = parse_number(number).unwrap_or_else(|| usage());
                parsed.sets.push((name.to_string(), number));
            }
            _ => usage(),
        }
    }
    if parsed.board.is_some() == parsed.sim.is_some() {
        usage();
    }
    parsed
}

fn resolve(sets: &[(String, u32)], registers: &[(String, usize)]) -> Result<Vec<(usize, u32)>, String> {
    sets.iter()
        .map(|(name, value)| seam::resolve(name, registers).map(|offset| (offset, *value)))
        .collect()
}

/// The rows as the fabric reads them: two lanes a frame, the 16-bit sample
/// left-justified into the lane, padded to whole bursts.
fn stage(frames: &[(i16, i16)]) -> (Vec<u8>, usize) {
    let padded = frames.len().div_ceil(FRAMES_PER_BURST) * FRAMES_PER_BURST;
    let mut bytes = Vec::with_capacity(padded * BYTES_PER_FRAME);
    for i in 0..padded {
        let (l, r) = frames.get(i).copied().unwrap_or((0, 0));
        bytes.extend_from_slice(&to_sample(l).to_le_bytes());
        bytes.extend_from_slice(&to_sample(r).to_le_bytes());
    }
    (bytes, padded)
}

fn unstage(bytes: &[u8], frames: usize) -> Vec<(i16, i16)> {
    (0..frames)
        .map(|i| {
            let o = i * BYTES_PER_FRAME;
            let lane = |at: usize| i32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]]);
            (from_sample(lane(o)), from_sample(lane(o + 4)))
        })
        .collect()
}

/// A frame's shape: the width rounded up to whole chunks; the chunks the
/// fabric is asked for, padded to whole bursts — the rows past the frame
/// are drawn and dropped, as a recording's padding is; and the beats that
/// come back, a chunk's worth each.
struct FrameShape {
    padded_width: usize,
    chunks: usize,
    padded_chunks: usize,
    beats_per_chunk: usize,
}

impl FrameShape {
    fn beats(&self) -> usize {
        self.chunks * self.beats_per_chunk
    }

    fn bytes_back(&self) -> usize {
        self.padded_chunks * self.beats_per_chunk * PIXELS_PER_BEAT
    }
}

fn frame_shape(width: usize, height: usize, chunk: usize) -> FrameShape {
    let padded_width = width.div_ceil(chunk) * chunk;
    let chunks = height * padded_width / chunk;
    let padded_chunks = chunks.div_ceil(BEATS_PER_BURST) * BEATS_PER_BURST;
    FrameShape { padded_width, chunks, padded_chunks, beats_per_chunk: chunk / PIXELS_PER_BEAT }
}

/// The frame out of the bytes the fabric wrote: rows of the padded width,
/// cropped to the width asked for.
fn crop(bytes: &[u8], width: usize, height: usize, padded_width: usize) -> Vec<u8> {
    let mut pixels = Vec::with_capacity(width * height);
    for r in 0..height {
        pixels.extend_from_slice(&bytes[r * padded_width..r * padded_width + width]);
    }
    pixels
}

fn describe<E: std::fmt::Debug>(e: BatchError<E>) -> String {
    match e {
        BatchError::WrongId { found } => format!("not a batch design: id reads 0x{found:08x}"),
        BatchError::WrongLayout { found, expected } => {
            format!("the fabric's register map is revision 0x{found:08x}, the seam says 0x{expected:08x} — rebuild one of them")
        }
        BatchError::NeverFinished => "the batch never finished — busy stayed high".to_string(),
        BatchError::Window(e) => format!("{e:?}"),
    }
}

/// What the batch produces: a recording's frames, or a frame's pixels.
enum Output {
    Frames(Vec<(i16, i16)>),
    Pixels(Vec<u8>),
}

/// The staged input and its count: a recording's rows, or nothing and the
/// beats of a frame.
fn staged(args: &Args, frames: &[(i16, i16)]) -> (Vec<u8>, usize, usize) {
    match &args.input {
        Source::Wav(_) => {
            let (bytes, padded) = stage(frames);
            let out_len = bytes.len();
            (bytes, padded, out_len)
        }
        Source::Frame { width, height } => {
            let shape = frame_shape(*width, *height, args.chunk);
            (Vec::new(), shape.padded_chunks, shape.bytes_back())
        }
    }
}

fn unstaged(args: &Args, frames: &[(i16, i16)], bytes: &[u8]) -> Output {
    match &args.input {
        Source::Wav(_) => Output::Frames(unstage(bytes, frames.len())),
        Source::Frame { width, height } => {
            let shape = frame_shape(*width, *height, args.chunk);
            Output::Pixels(crop(&bytes[..shape.beats() * PIXELS_PER_BEAT], *width, *height, shape.padded_width))
        }
    }
}

fn run_sim(args: &Args, frames: &[(i16, i16)]) -> Result<Output, String> {
    let design = args.sim.as_ref().unwrap().to_string_lossy().to_string();
    // A preset names the host-memory path; a frame is the counted one, and
    // without a preset the design's own mapping says which.
    let mut serve = vec!["batchserve".to_string(), design];
    if let Some(preset) = &args.preset {
        serve.push(preset.clone());
        if matches!(args.input, Source::Frame { .. }) {
            serve.push("count".to_string());
        }
    }
    let serve: Vec<&str> = serve.iter().map(String::as_str).collect();
    let window = FsSimWindow::spawn_with(&args.fsproj, &serve, "BATCHSERVE")
        .map_err(|e| format!("batchserve did not come up: {e:?}"))?;
    let mut device = BatchDevice::open(window, None).map_err(describe)?;
    let registers = device.window_mut().register_map().map_err(|e| format!("{e:?}"))?;
    for (offset, value) in resolve(&args.sets, &registers)? {
        device.set(offset, value).map_err(|e| format!("{e:?}"))?;
    }

    let (bytes, count, out_len) = staged(args, frames);
    let dst = SIM_SRC + bytes.len();
    if !bytes.is_empty() {
        device.window_mut().write_ddr(SIM_SRC, &bytes).map_err(|e| format!("{e:?}"))?;
    }
    device.start(SIM_SRC as u32, dst as u32, count as u32).map_err(|e| format!("{e:?}"))?;
    // Every poll is a transaction over a pipe; the fabric runs between them.
    device
        .wait_done(2_000_000, |w| w.free_cycles(2048))
        .map_err(describe)?;
    let out = device.window_mut().read_ddr(dst, out_len).map_err(|e| format!("{e:?}"))?;
    Ok(unstaged(args, frames, &out))
}

fn run_board(args: &Args, frames: &[(i16, i16)]) -> Result<Output, String> {
    let name = args.board.as_ref().unwrap();
    let layout = args.layout.as_ref().ok_or("--board needs --layout <seam.rs> for the controls' offsets")?;
    let (registers, hash) = read_layout(layout)?;
    let sets = resolve(&args.sets, &registers)?;

    let uio = find_uio(name)
        .map_err(|e| format!("{e}"))?
        .ok_or_else(|| format!("no uio device called '{name}' — is the app loaded?"))?;
    let arena_name = format!("udmabuf-{}", name.replace('_', "-"));
    let arena = Udmabuf::find(&arena_name)
        .map_err(|e| format!("{e}"))?
        .ok_or_else(|| format!("no u-dma-buf called '{arena_name}' — is the app loaded?"))?;

    let (bytes, count, out_len) = staged(args, frames);
    let half = arena.size / 2;
    if bytes.len().max(out_len) > half {
        return Err(format!("{} rows need {} bytes; the arena's half is {}", count, bytes.len().max(out_len), half));
    }
    let src_phys = arena.phys_addr;
    let dst_phys = arena.phys_addr + half as u64;
    if dst_phys + out_len as u64 > u32::MAX as u64 {
        return Err("the arena sits above 4 GB, which a 32-bit master cannot reach".into());
    }

    let regs_file = OpenOptions::new().read(true).write(true).open(&uio).map_err(|e| format!("{}: {e}", uio.display()))?;
    let regs = MmapWindow::open(&regs_file, 0, 0x1000).map_err(|e| format!("{e:?}"))?;
    let arena_file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&arena.dev_path)
        .map_err(|e| format!("{}: {e}", arena.dev_path.display()))?;
    let mut arena_map = MmapWindow::open(&arena_file, 0, arena.size).map_err(|e| format!("{e:?}"))?;

    let mut device = BatchDevice::open(regs, hash).map_err(describe)?;
    for (offset, value) in sets {
        device.set(offset, value).map_err(|e| format!("{e:?}"))?;
    }

    if !bytes.is_empty() {
        arena_map.bytes_mut()[..bytes.len()].copy_from_slice(&bytes);
        arena.sync_for_device(0, bytes.len()).map_err(|e| format!("sync_for_device: {e}"))?;
    }

    let started = std::time::Instant::now();
    device
        .start(src_phys as u32, dst_phys as u32, count as u32)
        .map_err(|e| format!("{e:?}"))?;
    device.wait_done(50_000_000, |_| Ok(())).map_err(describe)?;
    let elapsed = started.elapsed();
    device.acknowledge().map_err(|e| format!("{e:?}"))?;

    arena.sync_for_cpu(half, out_len).map_err(|e| format!("sync_for_cpu: {e}"))?;
    let out = unstaged(args, frames, &arena_map.bytes()[half..half + out_len]);
    eprintln!("{} rows in {:.3} ms", count, elapsed.as_secs_f64() * 1e3);
    Ok(out)
}

fn main() {
    let args = parse_args();
    let (rate, frames) = match &args.input {
        Source::Wav(path) => match read_wav(path) {
            Ok(v) => v,
            Err(e) => {
                eprintln!("{e}");
                std::process::exit(1);
            }
        },
        Source::Frame { .. } => (0, Vec::new()),
    };

    let result = if args.sim.is_some() { run_sim(&args, &frames) } else { run_board(&args, &frames) };

    let written = result.and_then(|out| match (out, &args.input) {
        (Output::Frames(out), _) => write_wav(&args.output, rate, &out).map(|()| format!("{} frames", frames.len())),
        (Output::Pixels(pixels), Source::Frame { width, height }) => {
            write_pgm(&args.output, *width, *height, &pixels).map(|()| format!("{width}×{height}"))
        }
        (Output::Pixels(_), Source::Wav(_)) => Err("a recording came back as pixels".to_string()),
    });

    match written {
        Ok(what) => println!("wrote {} ({what})", args.output.display()),
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    }
}
