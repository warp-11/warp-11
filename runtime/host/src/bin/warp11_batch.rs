//! A file through a design — a WAV, a PGM or a CSV in, the same kind out —
//! on a board's host memory, over its link, or in the F# Sim over the batch
//! bridge, with the same driver. Or a frame the design draws:
//! `frame:<width>x<height>` in place of the file is the counted path —
//! nothing staged, the count and the view written, the frame read back as a
//! PGM.
//!
//!   warp11_batch in.wav out.wav --board gain_patch_batch --layout gain_patch_batch_layout.rs [--set volume=512]
//!   warp11_batch in.wav out.wav --serial /dev/ttyUSB1 --baud 1000000 --layout gain_link_layout.rs [--set volume=512]
//!   warp11_batch in.wav out.wav --sim gain.json [--preset kv260] [--fsproj ../hdl/Warp11.Placement/Warp11.Placement.fsproj] [--set volume=512]
//!   warp11_batch numbers.csv sums.csv --serial /dev/ttyUSB0 --layout adder_link_layout.rs
//!   warp11_batch gradient.pgm out.pgm --sim blur.json --preset kv260
//!   warp11_batch frame:1400x800 out.pgm --sim mandelbrot.json --chunk 128 --set cxOrigin=0xE0000000 ...
//!   warp11_batch frame:320x200 out.pgm --serial /dev/ttyUSB1 --layout mandelbrot_link_layout.rs --chunk 128 --set ...
//!   warp11_batch count:48000 tone.wav --serial /dev/ttyUSB1 --layout tone_link_layout.rs [--rate 48000]
//!
//! Three layers, and none reaches into another. **The file** is this tool's
//! alone: a recording's channels fill the design's input fields in order,
//! two a frame (`wav`); an image's row is the row's bytes (`pgm`); a table's
//! columns are the fields by name (`table`). **The row** is the design's,
//! as the seam states it on every carrier (`ROW_IN_*`, `ROW_OUT_*`): which
//! lanes each field takes, and its format (`rows`). **The carrier** only
//! moves lane words: host memory pads each row to `ROW_*_BYTES` and the
//! count to whole bursts (`ROWS_PER_BURST`); the link sends the words as
//! they are. So a file that runs on one board runs on every board that
//! holds the design, with nothing written for the pair.
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
//! the controls' offsets, the rows and the layout hash; against the Sim the
//! bridge answers those itself.
//!
//! `--serial` is a board whose only connection is a UART, the design built
//! on the link path (`viaLink`): rows go in as played words and come back
//! as streamed frames, on the wire the register map is on. The link paces
//! it: a chunk of rows is played, their answers read back, and the fabric's
//! `…LinkSent` and `…LinkDropped` say afterwards whether every row made it.
//! A frame over the link is the counted path on the same wire
//! (`viaCountLink`): the count goes into `…CountTotal`, `…CountStart`
//! pulses it, and the pixels come back as streamed frames until the count's
//! worth has.

use std::fs::OpenOptions;
use std::path::PathBuf;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::mmap::MmapWindow;
use warp11_host::pgm::{image_to_rows, read_pgm, rows_to_image, write_pgm};
use warp11_host::rows::{self, row_shape, Constants, RowShape};
use warp11_host::seam::{self, parse_number, read_aperture_words, read_layout};
use warp11_host::serial_window::SerialWindow;
use warp11_host::table::{self, read_csv, write_csv, Table};
use warp11_host::udmabuf::{find_uio, Udmabuf};
use warp11_host::wav::{frames_to_rows, read_wav, rows_to_frames, write_wav};
use warp11_runtime::batch::{BatchDevice, BatchError};
use warp11_runtime::frame::{FrameShape, BEATS_PER_BURST, PIXELS_PER_BEAT};
use warp11_runtime::RegisterWindow;

/// Where the rows start in the bridge's fake DDR.
const SIM_SRC: usize = 0x1000;
/// Words played before their replies are read back: well inside the
/// fabric's played buffer (4 096 words) and inside a tty's input buffer at
/// seven bytes a streamed frame.
const LINK_CHUNK_WORDS: usize = 256;

/// What goes into the design: a file, or a frame for it to draw.
enum Source {
    Wav(PathBuf),
    Frame { width: usize, height: usize },
    /// Nothing in: a count of beats the design answers with rows — a tone.
    Count(usize),
    Pgm(PathBuf),
    Csv(PathBuf),
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

/// Where a batch's cycles went, when the top says: the cycles the design
/// waited for a row from the host's memory, and the cycles it held one the
/// write side would not take. A top built before these existed has neither.
fn waits(registers: &[(String, usize)]) -> Option<(usize, usize)> {
    match (seam::resolve("readWaits", registers), seam::resolve("writeWaits", registers)) {
        (Ok(read), Ok(write)) => Some((read, write)),
        _ => None,
    }
}

fn describe_waits(cycles: u32, read: u32, write: u32) -> String {
    let share = |n: u32| if cycles == 0 { 0.0 } else { 100.0 * n as f64 / cycles as f64 };
    format!(
        "waiting on reads {} ({:.0}%), on writes {} ({:.0}%)",
        read,
        share(read),
        write,
        share(write)
    )
}

fn resolve(sets: &[(String, u32)], registers: &[(String, usize)]) -> Result<Vec<(usize, u32)>, String> {
    sets.iter()
        .map(|(name, value)| seam::resolve(name, registers).map(|offset| (offset, *value)))
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

/// What the batch produces: a recording's frames, an image's pixels, or a
/// table.
enum Output {
    Frames(Vec<(i16, i16)>),
    Pixels(Vec<u8>),
    Table(Table),
}

/// The input file, read once before anything is asked of a board.
enum File {
    Recording { rate: u32, frames: Vec<(i16, i16)> },
    Image { width: usize, height: usize, pixels: Vec<u8> },
    Table(Table),
    /// A count or a frame: nothing goes in.
    Nothing,
}

fn read_file(args: &Args) -> Result<File, String> {
    Ok(match &args.input {
        Source::Wav(path) => {
            let (rate, frames) = read_wav(path)?;
            File::Recording { rate, frames }
        }
        Source::Pgm(path) => {
            let (width, height, pixels) = read_pgm(path)?;
            File::Image { width, height, pixels }
        }
        Source::Csv(path) => File::Table(read_csv(path)?),
        Source::Frame { .. } | Source::Count(_) => File::Nothing,
    })
}

/// A batch as rows: the input's lane words (none for a counted design),
/// how many rows, and the rows' shapes both ways as the seam states them.
struct Rows {
    input: Option<RowShape>,
    output: RowShape,
    words: Vec<u32>,
    count: usize,
}

/// The file as the design's input rows — the format's business alone.
fn to_rows(args: &Args, file: &File, c: &Constants) -> Result<Rows, String> {
    let output = row_shape(c, "OUT")?;
    let input = |words: Vec<u32>, count: usize, shape: RowShape| Rows { input: Some(shape), output: output.clone(), words, count };
    Ok(match file {
        File::Recording { frames, .. } => {
            let shape = row_shape(c, "IN")?;
            let (words, count) = frames_to_rows(frames, &shape)?;
            input(words, count, shape)
        }
        File::Image { width, height, pixels } => {
            let shape = row_shape(c, "IN")?;
            input(image_to_rows(*width, *height, pixels, &shape)?, *height, shape)
        }
        File::Table(t) => {
            let shape = row_shape(c, "IN")?;
            input(table::to_rows(t, &shape)?, t.rows.len(), shape)
        }
        File::Nothing => match args.input {
            Source::Count(n) => Rows { input: None, output, words: Vec::new(), count: n },
            _ => return Err("a frame is drawn, not read as rows".into()),
        },
    })
}

/// The output rows back as the kind of file that went in.
fn from_rows(file: &File, rows: &Rows, out: &[u32]) -> Result<Output, String> {
    Ok(match file {
        File::Recording { frames, .. } => {
            let mut back = rows_to_frames(out, &rows.output, rows.count)?;
            // The last row was filled out with silence.
            back.truncate(frames.len());
            Output::Frames(back)
        }
        File::Image { width, height, .. } => Output::Pixels(rows_to_image(out, &rows.output, *width, *height)?),
        File::Table(t) => Output::Table(table::from_rows(out, &rows.output, t.rows.len())),
        File::Nothing => Output::Frames(rows_to_frames(out, &rows.output, rows.count)?),
    })
}

/// A host-memory run: the bytes staged, the count the fabric is given, the
/// bytes it writes back, and how to read them.
struct MemoryRun {
    bytes: Vec<u8>,
    count: usize,
    out_len: usize,
    reading: Reading,
}

enum Reading {
    Rows(Rows),
    Frame(FrameShape),
}

/// The batch as host memory takes it: rows padded to the row's bytes and
/// the count to whole bursts — or, for a frame, the chunks it is drawn in.
fn memory_run(args: &Args, file: &File, c: &Constants) -> Result<MemoryRun, String> {
    if let Source::Frame { width, height } = args.input {
        let burst = c.number("ROWS_PER_BURST").ok().map_or(BEATS_PER_BURST, |rows| rows as usize);
        let frame = FrameShape::with_burst(width, height, args.chunk, burst);
        return Ok(MemoryRun { bytes: Vec::new(), count: frame.padded_chunks, out_len: frame.bytes_back(), reading: Reading::Frame(frame) });
    }
    let rows = to_rows(args, file, c)?;
    let burst = c.number("ROWS_PER_BURST")? as usize;
    let (bytes, count) = match &rows.input {
        Some(shape) => rows::to_memory(&rows.words, shape, rows.count, burst)?,
        // Padded to whole bursts as a recording is; the rows past the count
        // are drawn and dropped.
        None => (Vec::new(), rows.count.div_ceil(burst).max(1) * burst),
    };
    let out_bytes = rows.output.bytes.ok_or("host memory needs the output row's bytes, and the seam does not state them")?;
    Ok(MemoryRun { bytes, count, out_len: count * out_bytes, reading: Reading::Rows(rows) })
}

fn memory_output(run: &MemoryRun, file: &File, out: &[u8]) -> Result<Output, String> {
    match &run.reading {
        Reading::Frame(frame) => Ok(Output::Pixels(crop(out, frame))),
        Reading::Rows(rows) => from_rows(file, rows, &rows::from_memory(out, &rows.output, rows.count)?),
    }
}

fn run_sim(args: &Args, file: &File) -> Result<Output, String> {
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

    let pairs = device.window_mut().seam_constants().map_err(|e| format!("{e:?}"))?;
    let run = memory_run(args, file, &Constants::from_pairs(&pairs))?;
    let dst = SIM_SRC + run.bytes.len();
    if !run.bytes.is_empty() {
        device.window_mut().write_ddr(SIM_SRC, &run.bytes).map_err(|e| format!("{e:?}"))?;
    }
    device.start(SIM_SRC as u32, dst as u32, run.count as u32).map_err(|e| format!("{e:?}"))?;
    // Every poll is a transaction over a pipe; the fabric runs between them.
    device
        .wait_done(2_000_000, |w| w.free_cycles(2048))
        .map_err(describe)?;
    let cycles = device.cycles().map_err(|e| format!("{e:?}"))?;
    eprintln!("{} rows in {} fabric cycles", run.count, cycles);
    if let Some((read, write)) = waits(&registers) {
        let read = device.window_mut().read32(read).map_err(|e| format!("{e:?}"))?;
        let write = device.window_mut().read32(write).map_err(|e| format!("{e:?}"))?;
        eprintln!("{}", describe_waits(cycles, read, write));
    }
    let out = device.window_mut().read_ddr(dst, run.out_len).map_err(|e| format!("{e:?}"))?;
    memory_output(&run, file, &out)
}

fn run_board(args: &Args, file: &File) -> Result<Output, String> {
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

    let run = memory_run(args, file, &Constants::from_seam(layout)?)?;
    let half = arena.size / 2;
    if run.bytes.len().max(run.out_len) > half {
        return Err(format!("{} rows need {} bytes; the arena's half is {}", run.count, run.bytes.len().max(run.out_len), half));
    }
    let src_phys = arena.phys_addr;
    let dst_phys = arena.phys_addr + half as u64;
    if dst_phys + run.out_len as u64 > u32::MAX as u64 {
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

    if !run.bytes.is_empty() {
        arena_map.bytes_mut()[..run.bytes.len()].copy_from_slice(&run.bytes);
        arena.sync_for_device(0, run.bytes.len()).map_err(|e| format!("sync_for_device: {e}"))?;
    }

    let started = std::time::Instant::now();
    device
        .start(src_phys as u32, dst_phys as u32, run.count as u32)
        .map_err(|e| format!("{e:?}"))?;
    device.wait_done(50_000_000, |_| Ok(())).map_err(describe)?;
    let elapsed = started.elapsed();
    let cycles = device.cycles().map_err(|e| format!("{e:?}"))?;
    let waited = match waits(&registers) {
        Some((read, write)) => Some((
            device.window_mut().read32(read).map_err(|e| format!("{e:?}"))?,
            device.window_mut().read32(write).map_err(|e| format!("{e:?}"))?,
        )),
        None => None,
    };
    device.acknowledge().map_err(|e| format!("{e:?}"))?;

    arena.sync_for_cpu(half, run.out_len).map_err(|e| format!("sync_for_cpu: {e}"))?;
    let out = memory_output(&run, file, &arena_map.bytes()[half..half + run.out_len])?;
    eprintln!("{} rows in {:.3} ms wall, {} fabric cycles", run.count, elapsed.as_secs_f64() * 1e3, cycles);
    if let Some((read, write)) = waited {
        eprintln!("{}", describe_waits(cycles, read, write));
    }
    Ok(out)
}

/// The register, by name, whose name ends in `suffix` — the player's and the
/// recorder's are named for the need they serve plus the carrier, and the
/// need's name is the design's to choose.
/// The one register whose name ends in `suffix`, as the need's name the
/// rest of a link's registers are built from, and its offset. The seam
/// writes `OUT_LINK_SENT`; the need is `out`, so `{need}LinkArm` resolves and
/// `screaming(need)` is the seam's own prefix again.
fn ending_in(registers: &[(String, usize)], suffix: &str) -> Result<(String, usize), String> {
    let wanted = seam::key(suffix);
    let mut found = registers.iter().filter(|(name, _)| seam::key(name).ends_with(&wanted));
    let first = found.next().ok_or_else(|| format!("the seam has no register ending in '{suffix}' — is the design built on the link path?"))?;
    if found.next().is_some() {
        return Err(format!("the seam has more than one register ending in '{suffix}'"));
    }
    let name = &first.0;
    let need = if name.contains('_') {
        let segments: Vec<&str> = name.split('_').collect();
        let drop = seam::screaming(suffix).split('_').count();
        segments[..segments.len() - drop]
            .iter()
            .enumerate()
            .map(|(i, s)| {
                let lower = s.to_ascii_lowercase();
                if i == 0 { lower } else { lower[..1].to_ascii_uppercase() + &lower[1..] }
            })
            .collect()
    } else {
        name.strip_suffix(suffix).unwrap_or(name).to_string()
    };
    Ok((need, first.1))
}

fn run_serial(args: &Args, file: &File) -> Result<Output, String> {
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

    // The link sends a row's lanes as they are: nothing to pad.
    let rows = to_rows(args, file, &Constants::from_seam(layout)?)?;
    let back_words = rows.count * rows.output.words;
    let out = match &rows.input {
        None => count_over_link(args, &mut window, &registers, rows.count, back_words, record_arm, sent, record_dropped)?,
        Some(input) => play_and_read(args, &mut window, &registers, &rows.words, input.words, rows.output.words)?,
    };
    from_rows(file, &rows, &out)
}

/// Rows played into the link and their answers read back as streamed
/// frames, the player and the recorder armed around it: the rows go in a
/// chunk at a time — whole rows, what the fabric's buffer holds with room
/// to spare, where the seam says how much — and the fabric's own counts say
/// afterwards whether every word made it. `in_words` and `out_words` are a
/// row's words each way.
fn play_and_read(args: &Args, window: &mut SerialWindow, registers: &[(String, usize)], staged: &[u32], in_words: usize, out_words: usize) -> Result<Vec<u32>, String> {
    let tty = args.serial.as_ref().unwrap();
    let layout = args.layout.as_ref().unwrap();
    let io = |e: warp11_host::serial_window::SerialError| e.to_string();
    let (player, _fill) = ending_in(registers, "LinkFill")?;
    let (recorder, sent) = ending_in(registers, "LinkSent")?;
    let play_arm = seam::resolve(&format!("{player}LinkArm"), registers)?;
    let play_dropped = seam::resolve(&format!("{player}LinkDropped"), registers)?;
    let record_arm = seam::resolve(&format!("{recorder}LinkArm"), registers)?;
    let record_dropped = seam::resolve(&format!("{recorder}LinkDropped"), registers)?;
    let chunk_words = match seam::read_const(layout, &format!("{}_BUFFER_WORDS", seam::screaming(&player)))? {
        Some(buffer) => LINK_CHUNK_WORDS.min((buffer as usize / 2).max(2)),
        None => LINK_CHUNK_WORDS,
    };
    let chunk_rows = (chunk_words / in_words.max(1)).max(1);
    let rows = staged.len() / in_words.max(1);
    let expected = rows * out_words;
    let started = std::time::Instant::now();
    window.write32(record_arm, 1).map_err(io)?;
    window.write32(play_arm, 1).map_err(io)?;

    // Disarm whatever happens next — a recorder left armed keeps the fabric
    // writing the wire — then report.
    let exchanged = (|| -> Result<Vec<u32>, String> {
        let mut back: Vec<u32> = Vec::with_capacity(expected);
        for chunk in staged.chunks(chunk_rows * in_words) {
            window.play(chunk).map_err(io)?;
            back.extend(window.read_streamed(chunk.len() / in_words * out_words).map_err(io)?);
        }
        // A design that holds rows — a window, a pipeline — answers the last
        // of them only now.
        while back.len() < expected {
            let more = window.read_streamed(expected - back.len()).map_err(io)?;
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
    if back.len() != expected || lost_in != 0 || lost_out != 0 {
        return Err(format!(
            "{rows} rows played, {} of their {expected} words came back; the fabric sent {fabric_sent}, dropped {lost_in} on the way in and {lost_out} on the way out",
            back.len()
        ));
    }

    eprintln!("{rows} rows, {} words in and {expected} out, in {:.3} s over {}", staged.len(), elapsed.as_secs_f64(), tty.display());
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
    let result = read_file(&args).and_then(|file| {
        let out = if args.sim.is_some() {
            run_sim(&args, &file)
        } else if args.serial.is_some() {
            run_serial(&args, &file)
        } else {
            run_board(&args, &file)
        }?;
        write_output(&args, &file, out)
    });

    match result {
        Ok(what) => println!("wrote {} ({what})", args.output.display()),
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    }
}

/// What came back, written as the kind of file that went in.
fn write_output(args: &Args, file: &File, out: Output) -> Result<String, String> {
    match (out, file, &args.input) {
        (Output::Frames(out), _, _) => {
            let rate = match file {
                File::Recording { rate, .. } => *rate,
                _ => args.rate,
            };
            let n = out.len();
            write_wav(&args.output, rate, &out).map(|()| format!("{n} frames"))
        }
        (Output::Pixels(pixels), _, Source::Frame { width, height }) => {
            write_pgm(&args.output, *width, *height, &pixels).map(|()| format!("{width}×{height}"))
        }
        (Output::Pixels(pixels), File::Image { width, height, .. }, _) => {
            write_pgm(&args.output, *width, *height, &pixels).map(|()| format!("{width}×{height}"))
        }
        (Output::Pixels(_), _, _) => Err("pixels came back for something that is not an image".to_string()),
        (Output::Table(table), _, _) => {
            let n = table.rows.len();
            write_csv(&args.output, &table).map(|()| format!("{n} rows"))
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
