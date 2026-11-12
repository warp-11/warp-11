//! A WAV through a drawn design on a board's host memory — or through the
//! same design in the F# Sim, over the batch bridge, with the same driver.
//! Or a frame the design draws: `frame:<width>x<height>` in place of the
//! WAV is the counted path — nothing staged, the count and the view written,
//! the frame read back as a PGM.
//!
//!   warp11_batch in.wav out.wav --board gain_patch_batch --layout gain_patch_batch_layout.rs [--set volume=512]
//!   warp11_batch in.wav out.wav --serial /dev/ttyUSB1 --baud 1000000 --layout gain_link_layout.rs [--set volume=512]
//!   warp11_batch in.wav out.wav --sim gain.json [--preset kv260] [--fsproj ../hdl/Warp11.Placement/Warp11.Placement.fsproj] [--set volume=512]
//!   warp11_batch frame:1400x800 out.pgm --sim mandelbrot.json --chunk 128 --set cxOrigin=0xE0000000 ...
//!   warp11_batch frame:320x200 out.pgm --serial /dev/ttyUSB1 --layout mandelbrot_link_layout.rs --chunk 128 --set ...
//!   warp11_batch count:48000 tone.wav --serial /dev/ttyUSB1 --layout tone_link_layout.rs [--rate 48000]
//!   warp11_batch gradient.pgm out.pgm --sim blur.json --preset kv260
//!
//! `in.pgm out.pgm` is an image a row a beat: the design's one input field
//! is the row — sixty-four pixels is 512 bits, four beats of the KV260's
//! memory or sixteen words of a link — and what it drew comes back as the
//! same shape, `dotnet run --project Warp11.Placement -- image design.json
//! in.pgm out.pgm` being the Sim's own answer to compare with.
//!
//! `in.csv out.csv` is a table a row a beat: each column one of the
//! design's input fields, placed in the row where the seam's `ROW_IN_*`
//! says, and what comes back written as a CSV of the `ROW_OUT_*` fields —
//! the seam file on a board, the bridge's `S` answer in the Sim. The link
//! does not carry a table yet.
//!
//! `count:<n>` is a design with nothing in — a tone — asked for `n` rows,
//! written as a WAV at `--rate`, which the rows do not carry.
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
//!
//! `--serial` is the third transport: a board whose only connection is a
//! UART, the design built on the link path (`viaLink`). Rows go in as
//! played words — one field a word, in the row's order, the sample
//! left-justified as it is in a lane — and come back as streamed frames the
//! same way, on the wire the register map is on. The link paces it: a chunk
//! of words is played, that many read back, and the fabric's `…LinkSent`
//! and `…LinkDropped` say afterwards whether every row made it. A frame
//! over the link is the counted path on the same wire (`viaCountLink`): the
//! count goes into `…CountTotal`, `…CountStart` pulses it, and the pixels
//! come back as streamed frames until the count's worth has.

use std::fs::OpenOptions;
use std::path::PathBuf;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::mmap::MmapWindow;
use warp11_host::seam::{self, parse_number, read_aperture_words, read_layout};
use warp11_host::serial_window::SerialWindow;
use warp11_runtime::RegisterWindow;
use warp11_host::udmabuf::{find_uio, Udmabuf};
use warp11_host::wav::{from_sample, read_wav, to_sample, write_wav};
use warp11_runtime::batch::{BatchDevice, BatchError};
use warp11_runtime::frame::{FrameShape, BEATS_PER_BURST, PIXELS_PER_BEAT};
use warp11_host::pgm::{read_pgm, write_pgm};
use warp11_host::table::{read_csv, row_shape, stage as stage_table, unstage as unstage_table, write_csv, Constants, RowShape, Table};

/// Rows a burst holds for a stereo boundary: what `frameCount` must be a
/// multiple of. Two rows a 128-bit beat, sixteen beats a burst.
const FRAMES_PER_BURST: usize = 32;
const BYTES_PER_FRAME: usize = 8;
/// Where the rows start in the bridge's fake DDR.
const SIM_SRC: usize = 0x1000;
/// Words played before their replies are read back: well inside the
/// fabric's played buffer (4 096 words) and inside a tty's input buffer at
/// seven bytes a streamed frame.
const LINK_CHUNK_WORDS: usize = 256;

/// What goes into the design: a recording, or a frame for it to draw.
enum Source {
    Wav(PathBuf),
    Frame { width: usize, height: usize },
    /// Nothing in: a count of beats the design answers with rows — a tone.
    Count(usize),
    /// An image, a row a beat: the design's one field is the row, `width`
    /// pixels of a byte each.
    Pgm(PathBuf),
    /// A table, a row a beat: each column one of the design's input fields,
    /// placed where the seam says.
    Csv(PathBuf),
}

/// A table's run: the rows read once, and their shapes in and out as the
/// seam states them.
struct CsvRun {
    table: Table,
    input: RowShape,
    output: RowShape,
    rows_per_burst: usize,
}

impl CsvRun {
    fn new(args: &Args, constants: &Constants) -> Result<CsvRun, String> {
        let Source::Csv(path) = &args.input else { return Err("not a table".into()) };
        Ok(CsvRun {
            table: read_csv(path)?,
            input: row_shape(constants, "IN")?,
            output: row_shape(constants, "OUT")?,
            rows_per_burst: constants.number("ROWS_PER_BURST")? as usize,
        })
    }
}

/// How a row of `width` pixels sits in the host's memory — the fabric's
/// `rowShape`, derived the same way: one 32-bit lane per field, so the
/// row's one field takes `ceil(width / 4)` lanes; padded to a power of two
/// within a beat, or to whole beats beyond one. Bytes per row, and rows a
/// burst holds (what the count must be a multiple of).
fn pgm_row_shape(width: usize) -> (usize, usize) {
    let lanes = width.div_ceil(4);
    let lanes_per_beat = 4;
    if lanes <= lanes_per_beat {
        let lanes_per_row = lanes.next_power_of_two();
        let rows_per_beat = lanes_per_beat / lanes_per_row;
        (lanes_per_row * 4, rows_per_beat * BEATS_PER_BURST)
    } else {
        let beats_per_row = lanes.div_ceil(lanes_per_beat);
        (beats_per_row * 16, BEATS_PER_BURST / beats_per_row)
    }
}

struct Args {
    input: Source,
    output: PathBuf,
    board: Option<String>,
    serial: Option<PathBuf>,
    baud: u32,
    layout: Option<PathBuf>,
    sim: Option<PathBuf>,
    preset: Option<String>,
    fsproj: PathBuf,
    chunk: usize,
    /// The rate a counted recording is written at: the rows carry none.
    rate: u32,
    sets: Vec<(String, u32)>,
}

fn parse_frame(text: &str) -> Option<Source> {
    if text.ends_with(".csv") {
        return Some(Source::Csv(PathBuf::from(text)));
    }
    if text.ends_with(".pgm") {
        return Some(Source::Pgm(PathBuf::from(text)));
    }
    if let Some(n) = text.strip_prefix("count:") {
        return n.parse().ok().filter(|&n: &usize| n > 0).map(Source::Count);
    }
    let size = text.strip_prefix("frame:")?;
    let (w, h) = size.split_once('x')?;
    let width = w.parse().ok().filter(|&w: &usize| w > 0)?;
    let height = h.parse().ok().filter(|&h: &usize| h > 0)?;
    Some(Source::Frame { width, height })
}

fn usage() -> ! {
    eprintln!("usage: warp11_batch (in.wav out.wav | in.pgm out.pgm | in.csv out.csv | frame:<w>x<h> out.pgm [--chunk <pixels>] | count:<n> out.wav [--rate 48000]) (--board <uio name> --layout <seam.rs> | --serial <tty> [--baud 1000000] --layout <seam.rs> | --sim <design.json> [--preset kv260] [--fsproj <fsproj>]) [--set name=value]...");
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
        serial: None,
        baud: 1_000_000,
        layout: None,
        sim: None,
        preset: None,
        fsproj: PathBuf::from("../hdl/Warp11.Placement/Warp11.Placement.fsproj"),
        chunk: PIXELS_PER_BEAT,
        rate: 48_000,
        sets: Vec::new(),
    };
    while let Some(flag) = args.next() {
        let mut value = || args.next().unwrap_or_else(|| usage());
        match flag.as_str() {
            "--board" => parsed.board = Some(value()),
            "--serial" => parsed.serial = Some(PathBuf::from(value())),
            "--baud" => parsed.baud = value().parse().unwrap_or_else(|_| usage()),
            "--layout" => parsed.layout = Some(PathBuf::from(value())),
            "--sim" => parsed.sim = Some(PathBuf::from(value())),
            "--preset" => parsed.preset = Some(value()),
            "--fsproj" => parsed.fsproj = PathBuf::from(value()),
            "--rate" => parsed.rate = value().parse().unwrap_or_else(|_| usage()),
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
    let transports = [parsed.board.is_some(), parsed.serial.is_some(), parsed.sim.is_some()];
    if transports.iter().filter(|&&t| t).count() != 1 {
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

/// The frame out of the bytes the fabric wrote.
fn crop(bytes: &[u8], shape: &FrameShape) -> Vec<u8> {
    let mut pixels = vec![0u8; shape.pixels()];
    shape.crop_into(bytes, &mut pixels);
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
    Table(Table),
}

/// The image a `Source::Pgm` names, read once.
struct Image {
    width: usize,
    height: usize,
    pixels: Vec<u8>,
}

fn image_of(args: &Args) -> Result<Image, String> {
    match &args.input {
        Source::Pgm(path) => {
            let (width, height, pixels) = read_pgm(path)?;
            Ok(Image { width, height, pixels })
        }
        _ => Err("not an image".into()),
    }
}

/// The image's rows as the fabric reads them: pixel `c` at byte `c` of a
/// row padded to the row shape, rows padded to whole bursts.
fn stage_image(image: &Image) -> (Vec<u8>, usize, usize) {
    let (bytes_per_row, rows_per_burst) = pgm_row_shape(image.width);
    let padded = image.height.div_ceil(rows_per_burst) * rows_per_burst;
    let mut bytes = vec![0u8; padded * bytes_per_row];
    for r in 0..image.height {
        bytes[r * bytes_per_row..r * bytes_per_row + image.width].copy_from_slice(&image.pixels[r * image.width..(r + 1) * image.width]);
    }
    (bytes, padded, bytes_per_row)
}

/// The staged input and its count: a recording's rows, or nothing and the
/// beats of a frame.
fn staged(args: &Args, frames: &[(i16, i16)], csv: Option<&CsvRun>) -> Result<(Vec<u8>, usize, usize), String> {
    Ok(match &args.input {
        Source::Csv(_) => {
            let run = csv.ok_or("a table needs the seam's row shapes")?;
            let (bytes, padded) = stage_table(&run.table, &run.input, run.rows_per_burst)?;
            (bytes, padded, padded * run.output.bytes)
        }
        Source::Wav(_) => {
            let (bytes, padded) = stage(frames);
            let out_len = bytes.len();
            (bytes, padded, out_len)
        }
        Source::Frame { width, height } => {
            let shape = FrameShape::new(*width, *height, args.chunk);
            (Vec::new(), shape.padded_chunks, shape.bytes_back())
        }
        // Padded to whole bursts as a recording is; the rows past the count
        // are drawn and dropped.
        Source::Count(n) => {
            let padded = n.div_ceil(FRAMES_PER_BURST) * FRAMES_PER_BURST;
            (Vec::new(), padded, padded * BYTES_PER_FRAME)
        }
        Source::Pgm(_) => {
            let image = image_of(args).unwrap_or_else(|e| {
                eprintln!("{e}");
                std::process::exit(1);
            });
            let (bytes, padded, _) = stage_image(&image);
            let out_len = bytes.len();
            (bytes, padded, out_len)
        }
    })
}

fn unstaged(args: &Args, frames: &[(i16, i16)], bytes: &[u8], csv: Option<&CsvRun>) -> Output {
    match &args.input {
        Source::Csv(_) => {
            let run = csv.expect("staged checked the table's run");
            Output::Table(unstage_table(bytes, &run.output, run.table.rows.len()))
        }
        Source::Wav(_) => Output::Frames(unstage(bytes, frames.len())),
        Source::Frame { width, height } => {
            let shape = FrameShape::new(*width, *height, args.chunk);
            Output::Pixels(crop(bytes, &shape))
        }
        Source::Count(n) => Output::Frames(unstage(bytes, *n)),
        Source::Pgm(_) => {
            let image = image_of(args).unwrap_or_else(|e| {
                eprintln!("{e}");
                std::process::exit(1);
            });
            let (bytes_per_row, _) = pgm_row_shape(image.width);
            let mut pixels = Vec::with_capacity(image.width * image.height);
            for r in 0..image.height {
                pixels.extend_from_slice(&bytes[r * bytes_per_row..r * bytes_per_row + image.width]);
            }
            Output::Pixels(pixels)
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
        if matches!(args.input, Source::Frame { .. } | Source::Count(_)) {
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
    let csv = match args.input {
        Source::Csv(_) => {
            let pairs = device.window_mut().seam_constants().map_err(|e| format!("{e:?}"))?;
            Some(CsvRun::new(args, &Constants::from_pairs(&pairs))?)
        }
        _ => None,
    };

    let (bytes, count, out_len) = staged(args, frames, csv.as_ref())?;
    let dst = SIM_SRC + bytes.len();
    if !bytes.is_empty() {
        device.window_mut().write_ddr(SIM_SRC, &bytes).map_err(|e| format!("{e:?}"))?;
    }
    device.start(SIM_SRC as u32, dst as u32, count as u32).map_err(|e| format!("{e:?}"))?;
    // Every poll is a transaction over a pipe; the fabric runs between them.
    device
        .wait_done(2_000_000, |w| w.free_cycles(2048))
        .map_err(describe)?;
    eprintln!("{} rows in {} fabric cycles", count, device.cycles().map_err(|e| format!("{e:?}"))?);
    let out = device.window_mut().read_ddr(dst, out_len).map_err(|e| format!("{e:?}"))?;
    Ok(unstaged(args, frames, &out, csv.as_ref()))
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

    let csv = match args.input {
        Source::Csv(_) => Some(CsvRun::new(args, &Constants::from_seam(layout)?)?),
        _ => None,
    };
    let (bytes, count, out_len) = staged(args, frames, csv.as_ref())?;
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
    let cycles = device.cycles().map_err(|e| format!("{e:?}"))?;
    device.acknowledge().map_err(|e| format!("{e:?}"))?;

    arena.sync_for_cpu(half, out_len).map_err(|e| format!("sync_for_cpu: {e}"))?;
    let out = unstaged(args, frames, &arena_map.bytes()[half..half + out_len], csv.as_ref());
    eprintln!("{} rows in {:.3} ms wall, {} fabric cycles", count, elapsed.as_secs_f64() * 1e3, cycles);
    Ok(out)
}

/// The register, by name, whose name ends in `suffix` — the player's and the
/// recorder's are named for the need they serve plus the carrier, and the
/// need's name is the design's to choose.
fn ending_in<'a>(registers: &'a [(String, usize)], suffix: &str) -> Result<(&'a str, usize), String> {
    let mut found = registers.iter().filter(|(name, _)| name.ends_with(suffix));
    let first = found.next().ok_or_else(|| format!("the seam has no register ending in '{suffix}' — is the design built on the link path?"))?;
    if found.next().is_some() {
        return Err(format!("the seam has more than one register ending in '{suffix}'"));
    }
    Ok((first.0.strip_suffix(suffix).unwrap(), first.1))
}

fn run_serial(args: &Args, frames: &[(i16, i16)]) -> Result<Output, String> {
    let tty = args.serial.as_ref().unwrap();
    let layout = args.layout.as_ref().ok_or("--serial needs --layout <seam.rs> for the registers' offsets")?;
    let (registers, _hash) = read_layout(layout)?;
    let sets = resolve(&args.sets, &registers)?;
    // The recorder's registers are the need's name plus `Link…`, and `Sent`
    // is the recorder's alone.
    let (recorder, sent) = ending_in(&registers, "LinkSent")?;
    let record_arm = seam::resolve(&format!("{recorder}LinkArm"), &registers)?;
    let record_dropped = seam::resolve(&format!("{recorder}LinkDropped"), &registers)?;

    let words = read_aperture_words(layout)?.unwrap_or(64);
    let mut window = SerialWindow::open_words(tty, args.baud, 5, words).map_err(|e| format!("{}: {e}", tty.display()))?;
    let io = |e: warp11_host::serial_window::SerialError| e.to_string();

    for (offset, value) in sets {
        window.write32(offset, value).map_err(io)?;
    }

    if let Source::Frame { width, height } = args.input {
        let beat_words = seam::read_const(layout, &format!("{}_WORDS", seam::screaming(&recorder)))?;
        return draw_over_link(args, &mut window, &registers, width, height, beat_words, record_arm, sent, record_dropped);
    }
    if let Source::Count(n) = args.input {
        let words = count_over_link(args, &mut window, &registers, n, 2 * n, record_arm, sent, record_dropped)?;
        return Ok(Output::Frames(words.chunks(2).map(|w| (from_sample(w[0] as i32), from_sample(w[1] as i32))).collect()));
    }
    if matches!(args.input, Source::Csv(_)) {
        return Err("a table over the link is not built yet: --board or --sim".into());
    }
    if matches!(args.input, Source::Pgm(_)) {
        let image = image_of(args)?;
        // The row's one field as `lanesOf` words, low word first, pixel `c`
        // at byte `c`: four pixels a word.
        let lanes = image.width.div_ceil(4);
        let staged: Vec<u32> = (0..image.height)
            .flat_map(|r| {
                let row = &image.pixels[r * image.width..(r + 1) * image.width];
                (0..lanes).map(move |k| {
                    let mut w = [0u8; 4];
                    for j in 0..4 {
                        if k * 4 + j < row.len() {
                            w[j] = row[k * 4 + j];
                        }
                    }
                    u32::from_le_bytes(w)
                })
            })
            .collect();
        let back = play_and_read(args, &mut window, &registers, &staged)?;
        let pixels: Vec<u8> = (0..image.height)
            .flat_map(|r| back[r * lanes..(r + 1) * lanes].iter().flat_map(|w| w.to_le_bytes()).take(image.width).collect::<Vec<u8>>())
            .collect();
        return Ok(Output::Pixels(pixels));
    }


    // Two words a row, the fields in the row's order, each the sample as
    // the lane carries it.
    let staged: Vec<u32> = frames.iter().flat_map(|&(l, r)| [to_sample(l) as u32, to_sample(r) as u32]).collect();
    let back = play_and_read(args, &mut window, &registers, &staged)?;
    Ok(Output::Frames(back.chunks(2).map(|w| (from_sample(w[0] as i32), from_sample(w[1] as i32))).collect()))
}

/// Words played into the link and as many read back as streamed frames,
/// the player and the recorder armed around it: the rows go in a chunk at
/// a time — what the fabric's buffer holds with room to spare, where the
/// seam says how much — and the fabric's own counts say afterwards whether
/// every word made it.
fn play_and_read(args: &Args, window: &mut SerialWindow, registers: &[(String, usize)], staged: &[u32]) -> Result<Vec<u32>, String> {
    let tty = args.serial.as_ref().unwrap();
    let layout = args.layout.as_ref().unwrap();
    let io = |e: warp11_host::serial_window::SerialError| e.to_string();
    let (player, _fill) = ending_in(registers, "LinkFill")?;
    let (recorder, sent) = ending_in(registers, "LinkSent")?;
    let play_arm = seam::resolve(&format!("{player}LinkArm"), registers)?;
    let play_dropped = seam::resolve(&format!("{player}LinkDropped"), registers)?;
    let record_arm = seam::resolve(&format!("{recorder}LinkArm"), registers)?;
    let record_dropped = seam::resolve(&format!("{recorder}LinkDropped"), registers)?;
    let chunk_words = match seam::read_const(layout, &format!("{}_BUFFER_WORDS", seam::screaming(player)))? {
        Some(buffer) => LINK_CHUNK_WORDS.min((buffer as usize / 2).max(2)),
        None => LINK_CHUNK_WORDS,
    };
    let started = std::time::Instant::now();
    window.write32(record_arm, 1).map_err(io)?;
    window.write32(play_arm, 1).map_err(io)?;

    // Disarm whatever happens next — a recorder left armed keeps the fabric
    // writing the wire — then report.
    let exchanged = (|| -> Result<Vec<u32>, String> {
        let mut back: Vec<u32> = Vec::with_capacity(staged.len());
        for chunk in staged.chunks(chunk_words) {
            window.play(chunk).map_err(io)?;
            back.extend(window.read_streamed(chunk.len()).map_err(io)?);
        }
        // A design that holds rows — a window, a pipeline — answers the last
        // of them only now.
        while back.len() < staged.len() {
            let more = window.read_streamed(staged.len() - back.len()).map_err(io)?;
            if more.is_empty() {
                break;
            }
            back.extend(more);
        }
        Ok(back)
    })();
    let _ = window.write32(play_arm, 0);
    let _ = window.write32(record_arm, 0);
    let back = exchanged?;
    let elapsed = started.elapsed();

    let fabric_sent = window.read32(sent).map_err(io)?;
    let lost_in = window.read32(play_dropped).map_err(io)?;
    let lost_out = window.read32(record_dropped).map_err(io)?;
    if back.len() != staged.len() || lost_in != 0 || lost_out != 0 {
        return Err(format!(
            "{} words played, {} came back; the fabric sent {fabric_sent}, dropped {lost_in} on the way in and {lost_out} on the way out",
            staged.len(),
            back.len()
        ));
    }

    eprintln!("{} words in {:.3} s over {}", staged.len(), elapsed.as_secs_f64(), tty.display());
    Ok(back)
}

/// The counted path over the link: `total` beats minted in fabric from
/// `…CountTotal` / `…CountStart`, named for the input need, and `words`
/// streamed words back, the recorder armed around it.
fn count_over_link(
    args: &Args,
    window: &mut SerialWindow,
    registers: &[(String, usize)],
    total: usize,
    words: usize,
    record_arm: usize,
    sent: usize,
    record_dropped: usize,
) -> Result<Vec<u32>, String> {
    let (counter, total_reg) = ending_in(registers, "CountTotal")?;
    let start = seam::resolve(&format!("{counter}CountStart"), registers)?;
    let busy = seam::resolve(&format!("{counter}CountBusy"), registers)?;
    let io = |e: warp11_host::serial_window::SerialError| e.to_string();

    let started = std::time::Instant::now();
    window.write32(record_arm, 1).map_err(io)?;
    window.write32(total_reg, total as u32).map_err(io)?;
    window.write32(start, 1).map_err(io)?;

    let drawn = (|| -> Result<Vec<u32>, String> {
        let mut back: Vec<u32> = Vec::with_capacity(words);
        while back.len() < words {
            let more = window.read_streamed(words - back.len()).map_err(io)?;
            if more.is_empty() {
                break;
            }
            back.extend(more);
        }
        Ok(back)
    })();
    let _ = window.write32(record_arm, 0);
    let back = drawn?;
    let elapsed = started.elapsed();

    let still_busy = window.read32(busy).map_err(io)?;
    let fabric_sent = window.read32(sent).map_err(io)?;
    let lost = window.read32(record_dropped).map_err(io)?;
    if back.len() != words || still_busy != 0 || lost != 0 {
        return Err(format!(
            "{} words expected, {} came back; the fabric sent {fabric_sent}, dropped {lost}, busy {still_busy}",
            words,
            back.len()
        ));
    }

    eprintln!("{} beats in {:.3} s over {}", total, elapsed.as_secs_f64(), args.serial.as_ref().unwrap().display());
    Ok(back)
}

/// A frame over the link: the counted path with the link as the carrier.
/// The count is a register the fabric mints beats from — `…CountTotal`,
/// `…CountStart`, `…CountBusy`, named for the input need — and the pixels
/// come back as streamed frames, four a word, low byte first, a chunk's
/// beats in order. No burst padding: the link takes rows one at a time.
/// A frame drawn over the link. A beat comes back as its pixels — in
/// raster order, the order the design kept — or as the index of the beat it
/// belongs in and then its pixels, in whatever order the lanes finished, for
/// the host to place. The seam's words a beat says which: one more than the
/// pixels' words is the index.
fn draw_over_link(
    args: &Args,
    window: &mut SerialWindow,
    registers: &[(String, usize)],
    width: usize,
    height: usize,
    beat_words: Option<u32>,
    record_arm: usize,
    sent: usize,
    record_dropped: usize,
) -> Result<Output, String> {
    let shape = FrameShape::new(width, height, args.chunk);
    let pixel_words = PIXELS_PER_BEAT / 4;
    let per_beat = beat_words.map_or(pixel_words, |w| w as usize);
    let indexed = match per_beat - pixel_words {
        0 => false,
        1 => true,
        _ => return Err(format!("a beat of {per_beat} words is neither {pixel_words} words of pixels nor an index and them")),
    };
    let words = shape.beats() * per_beat;
    let back = count_over_link(args, window, registers, shape.chunks, words, record_arm, sent, record_dropped)?;
    let bytes = if indexed { place_by_index(&back, shape.beats())? } else { back.iter().flat_map(|w| w.to_le_bytes()).collect() };
    Ok(Output::Pixels(crop(&bytes, &shape)))
}

/// Beats that each name where they go, as the frame they make: an index
/// word, then the pixels low word first. Every place filled exactly once, or
/// it says which was not.
fn place_by_index(words: &[u32], beats: usize) -> Result<Vec<u8>, String> {
    let per_beat = 1 + PIXELS_PER_BEAT / 4;
    let mut frame = vec![0u8; beats * PIXELS_PER_BEAT];
    let mut filled = vec![false; beats];
    for beat in words.chunks_exact(per_beat) {
        let index = beat[0] as usize;
        if index >= beats {
            return Err(format!("a beat names place {index}, past the frame's {beats}"));
        }
        if std::mem::replace(&mut filled[index], true) {
            return Err(format!("two beats name place {index}"));
        }
        for (k, w) in beat[1..].iter().enumerate() {
            frame[index * PIXELS_PER_BEAT + 4 * k..][..4].copy_from_slice(&w.to_le_bytes());
        }
    }
    match filled.iter().position(|f| !f) {
        Some(missing) => Err(format!("no beat came for place {missing}")),
        None => Ok(frame),
    }
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
        Source::Count(_) => (args.rate, Vec::new()),
        Source::Pgm(_) | Source::Csv(_) => (0, Vec::new()),
    };

    let result = if args.sim.is_some() {
        run_sim(&args, &frames)
    } else if args.serial.is_some() {
        run_serial(&args, &frames)
    } else {
        run_board(&args, &frames)
    };

    let written = result.and_then(|out| match (out, &args.input) {
        (Output::Frames(out), _) => {
            let n = out.len();
            write_wav(&args.output, rate, &out).map(|()| format!("{n} frames"))
        }
        (Output::Pixels(pixels), Source::Frame { width, height }) => {
            write_pgm(&args.output, *width, *height, &pixels).map(|()| format!("{width}×{height}"))
        }
        (Output::Pixels(pixels), Source::Pgm(_)) => {
            let image = image_of(&args)?;
            write_pgm(&args.output, image.width, image.height, &pixels).map(|()| format!("{}×{}", image.width, image.height))
        }
        (Output::Pixels(_), _) => Err("pixels came back for something that is not an image".to_string()),
        (Output::Table(table), _) => {
            let n = table.rows.len();
            write_csv(&args.output, &table).map(|()| format!("{n} rows"))
        }
    });

    match written {
        Ok(what) => println!("wrote {} ({what})", args.output.display()),
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A beat for place `i`: the index, then four words whose bytes all say `i`.
    fn beat(i: u32) -> Vec<u32> {
        let w = u32::from_le_bytes([i as u8; 4]);
        vec![i, w, w, w, w]
    }

    #[test]
    fn beats_land_where_they_say_whatever_order_they_came_in() {
        let words: Vec<u32> = [2, 0, 3, 1].iter().flat_map(|&i| beat(i)).collect();
        let frame = place_by_index(&words, 4).unwrap();
        for i in 0..4 {
            assert!(frame[i * PIXELS_PER_BEAT..(i + 1) * PIXELS_PER_BEAT].iter().all(|&b| b == i as u8), "place {i}");
        }
    }

    #[test]
    fn a_place_filled_twice_or_never_is_said() {
        let twice: Vec<u32> = [0, 1, 1].iter().flat_map(|&i| beat(i)).collect();
        assert_eq!(place_by_index(&twice, 3).unwrap_err(), "two beats name place 1");
        let missing: Vec<u32> = [0, 2].iter().flat_map(|&i| beat(i)).collect();
        assert_eq!(place_by_index(&missing, 3).unwrap_err(), "no beat came for place 1");
        let past: Vec<u32> = beat(5);
        assert_eq!(place_by_index(&past, 3).unwrap_err(), "a beat names place 5, past the frame's 3");
    }
}
