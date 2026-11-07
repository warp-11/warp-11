//! A design's surface, pulled whole over the wire it shares with its
//! register map — a Life world, a framebuffer — and shown or kept.
//!
//!   warp11_snapshot --serial /dev/ttyUSB1 --baud 1000000 --layout life64x64_link_layout.rs \
//!       [--need world] [--seed glider.txt] [--set run=1] [--set intervalCycles=1000000] \
//!       [--pull 10] [--every 100] [out.pbm]
//!
//! A surface bound to the host link (`notes/DEVICES.md`, the snapshot
//! carrier) is latched in one cycle when `…LinkPull` is pulsed and sent as
//! streamed frames, a row's words at a time; `…LinkMissed` counts pulls that
//! landed while a snapshot was still leaving. The seam says the shape —
//! `WORLD_ROWS`, `WORLD_WORDS` — so nothing here is told it.
//!
//! `--seed` plays a text picture into the design's input need (its rows as
//! `.`/`#`, top row first, bit 0 the leftmost cell) over the same link, the
//! way `warp11_batch --serial` plays rows: the input need's `…LinkArm` is
//! set around it. Each pull is printed as the same picture, or the last one
//! is written as a PBM when a path is given.

use std::path::{Path, PathBuf};
use warp11_host::seam::{read_aperture_words, read_const, read_layout, resolve};
use warp11_host::serial_window::SerialWindow;
use warp11_runtime::RegisterWindow;

fn usage() -> ! {
    eprintln!(
        "usage: warp11_snapshot --serial <tty> [--baud 1000000] --layout <seam.rs> [--need world] \
         [--seed <picture.txt>] [--set name=value]... [--pull 1] [--every <ms>] [out.pbm]"
    );
    std::process::exit(2)
}

struct Args {
    tty: PathBuf,
    baud: u32,
    layout: PathBuf,
    need: String,
    seed: Option<PathBuf>,
    sets: Vec<(String, u32)>,
    pulls: usize,
    every_ms: u64,
    out: Option<PathBuf>,
}

fn parse_args() -> Args {
    let mut args = std::env::args().skip(1);
    let mut parsed = Args {
        tty: PathBuf::new(),
        baud: 1_000_000,
        layout: PathBuf::new(),
        need: "world".to_string(),
        seed: None,
        sets: Vec::new(),
        pulls: 1,
        every_ms: 0,
        out: None,
    };
    while let Some(flag) = args.next() {
        let mut value = || args.next().unwrap_or_else(|| usage());
        match flag.as_str() {
            "--serial" => parsed.tty = PathBuf::from(value()),
            "--baud" => parsed.baud = value().parse().unwrap_or_else(|_| usage()),
            "--layout" => parsed.layout = PathBuf::from(value()),
            "--need" => parsed.need = value(),
            "--seed" => parsed.seed = Some(PathBuf::from(value())),
            "--pull" => parsed.pulls = value().parse().unwrap_or_else(|_| usage()),
            "--every" => parsed.every_ms = value().parse().unwrap_or_else(|_| usage()),
            "--set" => {
                let v = value();
                let (name, number) = v.split_once('=').unwrap_or_else(|| usage());
                let number = warp11_host::seam::parse_number(number).unwrap_or_else(|| usage());
                parsed.sets.push((name.to_string(), number));
            }
            other if !other.starts_with("--") && parsed.out.is_none() => parsed.out = Some(PathBuf::from(other)),
            _ => usage(),
        }
    }
    if parsed.tty.as_os_str().is_empty() || parsed.layout.as_os_str().is_empty() || parsed.pulls == 0 {
        usage();
    }
    parsed
}

/// A picture as rows of words: `#` a live cell, anything else dead, bit 0
/// the leftmost, `words` words a row, low word first.
fn picture_rows(text: &str, words: usize) -> Vec<Vec<u32>> {
    text.lines()
        .filter(|l| !l.trim().is_empty())
        .map(|line| {
            let mut row = vec![0u32; words];
            for (x, c) in line.trim_end().chars().enumerate() {
                if c == '#' && x / 32 < words {
                    row[x / 32] |= 1 << (x % 32);
                }
            }
            row
        })
        .collect()
}

/// The rows back as the picture, `width` cells a row.
fn picture(rows: &[Vec<u32>], width: usize) -> String {
    rows.iter()
        .map(|row| (0..width).map(|x| if row[x / 32] >> (x % 32) & 1 == 1 { '#' } else { '.' }).collect::<String>())
        .collect::<Vec<_>>()
        .join("\n")
}

fn write_pbm(path: &Path, rows: &[Vec<u32>], width: usize) -> Result<(), String> {
    let mut out = format!("P1\n{} {}\n", width, rows.len());
    for row in rows {
        let line: Vec<&str> = (0..width).map(|x| if row[x / 32] >> (x % 32) & 1 == 1 { "1" } else { "0" }).collect();
        out.push_str(&line.join(" "));
        out.push('\n');
    }
    std::fs::write(path, out).map_err(|e| format!("{}: {e}", path.display()))
}

fn run(args: &Args) -> Result<(), String> {
    let (registers, _hash) = read_layout(&args.layout)?;
    let upper = warp11_host::seam::screaming(&args.need);
    let rows = read_const(&args.layout, &format!("{upper}_ROWS"))?
        .ok_or_else(|| format!("the seam says nothing about {upper}_ROWS — is '{}' a surface over the link?", args.need))? as usize;
    let words = read_const(&args.layout, &format!("{upper}_WORDS"))?
        .ok_or_else(|| format!("the seam says nothing about {upper}_WORDS"))? as usize;
    let pull = resolve(&format!("{}LinkPull", args.need), &registers)?;
    let missed = resolve(&format!("{}LinkMissed", args.need), &registers)?;
    let aperture = read_aperture_words(&args.layout)?.unwrap_or(64);
    let mut window = SerialWindow::open_words(&args.tty, args.baud, 5, aperture).map_err(|e| format!("{}: {e}", args.tty.display()))?;
    let io = |e: warp11_host::serial_window::SerialError| e.to_string();
    // Cells a row: the last word may be partial, which the seam does not say,
    // so a picture shows whole words.
    let width = words * 32;

    if let Some(seed) = &args.seed {
        let text = std::fs::read_to_string(seed).map_err(|e| format!("{}: {e}", seed.display()))?;
        let seed_rows = picture_rows(&text, words);
        if seed_rows.len() != rows {
            return Err(format!("{}: {} rows for a surface of {rows}", seed.display(), seed_rows.len()));
        }
        // The input need is whichever has a player on the link.
        let player = registers
            .iter()
            .find_map(|(name, _)| name.strip_suffix("LinkFill").map(str::to_string))
            .ok_or("the seam has no played need to seed")?;
        let arm = resolve(&format!("{player}LinkArm"), &registers)?;
        let dropped = resolve(&format!("{player}LinkDropped"), &registers)?;
        window.write32(arm, 1).map_err(io)?;
        let played: Vec<u32> = seed_rows.iter().flatten().copied().collect();
        let result = window.play(&played).map_err(io);
        std::thread::sleep(std::time::Duration::from_millis(20));
        let _ = window.write32(arm, 0);
        result?;
        let lost = window.read32(dropped).map_err(io)?;
        if lost != 0 {
            return Err(format!("{lost} seed words were dropped on the way in"));
        }
    }

    for (name, value) in &args.sets {
        let offset = resolve(name, &registers)?;
        window.write32(offset, *value).map_err(io)?;
    }

    let mut last: Vec<Vec<u32>> = Vec::new();
    for k in 0..args.pulls {
        if k > 0 && args.every_ms > 0 {
            std::thread::sleep(std::time::Duration::from_millis(args.every_ms));
        }
        window.take_streamed();
        window.write32(pull, 1).map_err(io)?;
        let mut back: Vec<u32> = Vec::with_capacity(rows * words);
        while back.len() < rows * words {
            let more = window.read_streamed(rows * words - back.len()).map_err(io)?;
            if more.is_empty() {
                break;
            }
            back.extend(more);
        }
        if back.len() != rows * words {
            return Err(format!("{} words of a {rows}×{words} snapshot came back", back.len()));
        }
        last = back.chunks(words).map(|w| w.to_vec()).collect();
        if args.out.is_none() || k + 1 < args.pulls {
            println!("{}\n", picture(&last, width));
        }
    }

    let missed = window.read32(missed).map_err(io)?;
    if missed != 0 {
        eprintln!("{missed} pulls landed while a snapshot was still leaving and were missed");
    }
    if let Some(out) = &args.out {
        write_pbm(out, &last, width)?;
        println!("wrote {} ({rows} rows, {width} cells)", out.display());
    }
    Ok(())
}

fn main() {
    let args = parse_args();
    if let Err(e) = run(&args) {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
