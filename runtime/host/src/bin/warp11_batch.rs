//! A WAV through a drawn design on a board's host memory — or through the
//! same design in the F# Sim, over the batch bridge, with the same driver.
//!
//!   warp11_batch in.wav out.wav --board gain_patch_batch --layout gain_patch_batch_layout.rs [--set volume=512]
//!   warp11_batch in.wav out.wav --sim gain.json [--preset kv260] [--fsproj ../hdl/Warp11.Placement/Warp11.Placement.fsproj] [--set volume=512]
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

/// Rows a burst holds for a stereo boundary: what `frameCount` must be a
/// multiple of. Two rows a 128-bit beat, sixteen beats a burst.
const FRAMES_PER_BURST: usize = 32;
const BYTES_PER_FRAME: usize = 8;
/// Where the rows start in the bridge's fake DDR.
const SIM_SRC: usize = 0x1000;

struct Args {
    input: PathBuf,
    output: PathBuf,
    board: Option<String>,
    layout: Option<PathBuf>,
    sim: Option<PathBuf>,
    preset: String,
    fsproj: PathBuf,
    sets: Vec<(String, u32)>,
}

fn usage() -> ! {
    eprintln!("usage: warp11_batch in.wav out.wav (--board <uio name> --layout <seam.rs> | --sim <design.json> [--preset kv260] [--fsproj <fsproj>]) [--set name=value]...");
    std::process::exit(2)
}

fn parse_args() -> Args {
    let mut args = std::env::args().skip(1);
    let input = args.next().map(PathBuf::from).unwrap_or_else(|| usage());
    let output = args.next().map(PathBuf::from).unwrap_or_else(|| usage());
    let mut parsed = Args {
        input,
        output,
        board: None,
        layout: None,
        sim: None,
        preset: "kv260".to_string(),
        fsproj: PathBuf::from("../hdl/Warp11.Placement/Warp11.Placement.fsproj"),
        sets: Vec::new(),
    };
    while let Some(flag) = args.next() {
        let mut value = || args.next().unwrap_or_else(|| usage());
        match flag.as_str() {
            "--board" => parsed.board = Some(value()),
            "--layout" => parsed.layout = Some(PathBuf::from(value())),
            "--sim" => parsed.sim = Some(PathBuf::from(value())),
            "--preset" => parsed.preset = value(),
            "--fsproj" => parsed.fsproj = PathBuf::from(value()),
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

fn run_sim(args: &Args, frames: &[(i16, i16)]) -> Result<Vec<(i16, i16)>, String> {
    let design = args.sim.as_ref().unwrap().to_string_lossy().to_string();
    let window = FsSimWindow::spawn_with(&args.fsproj, &["batchserve", &design, &args.preset], "BATCHSERVE")
        .map_err(|e| format!("batchserve did not come up: {e:?}"))?;
    let mut device = BatchDevice::open(window, None).map_err(describe)?;
    let registers = device.window_mut().register_map().map_err(|e| format!("{e:?}"))?;
    for (offset, value) in resolve(&args.sets, &registers)? {
        device.set(offset, value).map_err(|e| format!("{e:?}"))?;
    }

    let (bytes, padded) = stage(frames);
    let dst = SIM_SRC + bytes.len();
    device.window_mut().write_ddr(SIM_SRC, &bytes).map_err(|e| format!("{e:?}"))?;
    device.start(SIM_SRC as u32, dst as u32, padded as u32).map_err(|e| format!("{e:?}"))?;
    // Every poll is a transaction over a pipe; the fabric runs between them.
    device
        .wait_done(20_000, |w| w.free_cycles(2048))
        .map_err(describe)?;
    let out = device.window_mut().read_ddr(dst, bytes.len()).map_err(|e| format!("{e:?}"))?;
    Ok(unstage(&out, frames.len()))
}

fn run_board(args: &Args, frames: &[(i16, i16)]) -> Result<Vec<(i16, i16)>, String> {
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

    let (bytes, padded) = stage(frames);
    let half = arena.size / 2;
    if bytes.len() > half {
        return Err(format!("{} frames need {} bytes; the arena's half is {}", padded, bytes.len(), half));
    }
    let src_phys = arena.phys_addr;
    let dst_phys = arena.phys_addr + half as u64;
    if dst_phys + bytes.len() as u64 > u32::MAX as u64 {
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

    arena_map.bytes_mut()[..bytes.len()].copy_from_slice(&bytes);
    arena.sync_for_device(0, bytes.len()).map_err(|e| format!("sync_for_device: {e}"))?;

    let started = std::time::Instant::now();
    device
        .start(src_phys as u32, dst_phys as u32, padded as u32)
        .map_err(|e| format!("{e:?}"))?;
    device.wait_done(50_000_000, |_| Ok(())).map_err(describe)?;
    let elapsed = started.elapsed();
    device.acknowledge().map_err(|e| format!("{e:?}"))?;

    arena.sync_for_cpu(half, bytes.len()).map_err(|e| format!("sync_for_cpu: {e}"))?;
    let out = unstage(&arena_map.bytes()[half..half + bytes.len()], frames.len());
    eprintln!("{} frames in {:.3} ms", padded, elapsed.as_secs_f64() * 1e3);
    Ok(out)
}

fn main() {
    let args = parse_args();
    let (rate, frames) = match read_wav(&args.input) {
        Ok(v) => v,
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    };

    let result = if args.sim.is_some() { run_sim(&args, &frames) } else { run_board(&args, &frames) };

    match result.and_then(|out| write_wav(&args.output, rate, &out)) {
        Ok(()) => println!("wrote {} ({} frames)", args.output.display(), frames.len()),
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    }
}
