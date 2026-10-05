//! A design's recorder, off the wire it shares with its register map.
//!
//!   warp11_record --serial /dev/ttyUSB1 --baud 1000000 \
//!       --layout aid_uart_layout.rs --seconds 10 --rate 46875 out.wav
//!
//! A design whose output need is bound twice — once to its converter, once to
//! the host link (`notes/DEVICES.md` §10h) — streams what it produced as
//! frames beside its register traffic. This arms that recorder, reads the
//! frames until it has enough, disarms it, and writes a WAV.
//!
//! **Disarming is not tidiness.** The arm bit is what lets the host stop the
//! fabric writing before anything is unloaded or unplugged, so the tool
//! clears it on the way out of every path, including the error ones.
//!
//! The link's words are one field of the design's output row each, in the
//! order the row declares them, so `--channels` says how many fields make a
//! frame and `--bits` how wide they are. Neither is in the seam — the seam
//! carries registers, and this is the boundary — so both are stated rather
//! than guessed.
//!
//! The recorder's registers are named for the need it records and the
//! carrier — `outLinkArm`, `outLinkSent`, `outLinkDropped` for a need called
//! `out` — so `--need` says which need, `out` unless told otherwise.

use std::path::{Path, PathBuf};
use warp11_host::seam::{read_layout, resolve};
use warp11_host::serial_window::SerialWindow;
use warp11_host::wav::write_wav;
use warp11_runtime::RegisterWindow;

fn usage() -> ! {
    eprintln!(
        "usage: warp11_record --serial <tty> [--baud 1000000] --layout <seam.rs> \
         (--seconds <n> | --frames <n>) [--rate 46875] [--channels 2] [--bits 24] [--need out] <out.wav>"
    );
    std::process::exit(2)
}

struct Args {
    tty: PathBuf,
    baud: u32,
    layout: PathBuf,
    frames: usize,
    rate: u32,
    channels: usize,
    bits: u32,
    need: String,
    out: PathBuf,
}

fn parse() -> Args {
    let argv: Vec<String> = std::env::args().skip(1).collect();
    let (mut tty, mut layout, mut out) = (None, None, None);
    let (mut baud, mut rate, mut channels, mut bits) = (1_000_000u32, 46_875u32, 2usize, 24u32);
    let (mut seconds, mut frames) = (None::<f64>, None::<usize>);
    let mut need = String::from("out");
    let mut i = 0;

    while i < argv.len() {
        // The value after a flag, by index rather than a closure: a closure
        // that bumps `i` borrows it for as long as the match needs it.
        let value = |i: usize| argv.get(i + 1).cloned().unwrap_or_else(|| usage());
        let mut took_value = true;

        match argv[i].as_str() {
            "--serial" => tty = Some(PathBuf::from(value(i))),
            "--layout" => layout = Some(PathBuf::from(value(i))),
            "--baud" => baud = value(i).parse().unwrap_or_else(|_| usage()),
            "--rate" => rate = value(i).parse().unwrap_or_else(|_| usage()),
            "--channels" => channels = value(i).parse().unwrap_or_else(|_| usage()),
            "--bits" => bits = value(i).parse().unwrap_or_else(|_| usage()),
            "--seconds" => seconds = Some(value(i).parse().unwrap_or_else(|_| usage())),
            "--frames" => frames = Some(value(i).parse().unwrap_or_else(|_| usage())),
            "--need" => need = value(i),
            other if other.starts_with("--") => usage(),
            other => {
                out = Some(PathBuf::from(other));
                took_value = false;
            }
        }

        i += if took_value { 2 } else { 1 };
    }

    let frames = match (frames, seconds) {
        (Some(n), _) => n,
        (None, Some(s)) => (s * rate as f64) as usize,
        (None, None) => usage(),
    };

    match (tty, layout, out) {
        (Some(tty), Some(layout), Some(out)) if channels > 0 && frames > 0 => {
            Args { tty, baud, layout, frames, rate, channels, bits, need, out }
        }
        _ => usage(),
    }
}

/// A streamed word back to a sample. The fabric sign-extended the field into
/// its 32-bit word, so the top bits are the sign and the value is `bits` wide.
fn sample(word: u32, bits: u32) -> i32 {
    let shift = 32 - bits;
    ((word << shift) as i32) >> shift
}

fn record(args: &Args, registers: &[(String, usize)]) -> Result<(), String> {
    let arm = resolve(&format!("{}LinkArm", args.need), registers)?;
    let sent = resolve(&format!("{}LinkSent", args.need), registers).ok();
    let dropped = resolve(&format!("{}LinkDropped", args.need), registers).ok();

    let mut window = SerialWindow::open(&args.tty, args.baud, 5).map_err(|e| e.to_string())?;
    let wanted = args.frames * args.channels;

    window.write32(arm, 1).map_err(|e| e.to_string())?;

    // Disarm whatever happens next, then report what went wrong.
    let collected = window.read_streamed(wanted).map_err(|e| e.to_string());
    let _ = window.write32(arm, 0);
    let words = collected?;

    if let (Some(sent), Some(dropped)) = (sent, dropped) {
        let s = window.read32(sent).unwrap_or(0);
        let d = window.read32(dropped).unwrap_or(0);
        println!("fabric offered {s} rows, the fork dropped {d}");

        if d > 0 {
            eprintln!("warning: {d} rows were dropped — the link could not keep up with the design");
        }
    }

    if words.len() < wanted {
        return Err(format!("the link went quiet after {} of {wanted} words", words.len()));
    }

    // Frames out of the interleaved fields, as the design's row declares them.
    let pairs: Vec<(i16, i16)> = words
        .chunks(args.channels)
        .map(|frame| {
            let scale = |w: u32| (sample(w, args.bits) >> (args.bits as i32 - 16)) as i16;
            (scale(frame[0]), scale(frame[frame.len().min(2) - 1]))
        })
        .collect();

    write_wav(Path::new(&args.out), args.rate, &pairs)?;
    println!("wrote {} ({} frames at {} Hz)", args.out.display(), pairs.len(), args.rate);
    Ok(())
}

fn main() -> std::process::ExitCode {
    let args = parse();

    let registers = match read_layout(&args.layout) {
        Ok((r, _)) => r,
        Err(why) => {
            eprintln!("{}: {why}", args.layout.display());
            return std::process::ExitCode::FAILURE;
        }
    };

    match record(&args, &registers) {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(why) => {
            eprintln!("{why}");
            std::process::ExitCode::FAILURE
        }
    }
}
