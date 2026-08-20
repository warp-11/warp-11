//! First light for the `audio-batch` accelerator: a WAV file through the
//! fabric and back out as a WAV file.
//!
//! This is the example that answers "run a file through the board". Every other
//! audio app in public warp11 is a live I2S chain, which needs a codec on the
//! Pmod header and leaves you nothing to diff. This one takes bytes in and
//! produces bytes out, so its result can be compared exactly — against itself
//! bypassed, and against the F# simulator running the same DSP.
//!
//!     ./audio_batch_first_light in.wav out.wav
//!
//! Two runs happen, in this order, because they fail differently:
//!
//!   1. **flat** — configured for no gain reduction, the accelerator is a
//!      memcpy with a 128-bit beat in the middle. The output must equal the input byte for byte. A failure here
//!      is the DDR path: burst reads, the beat/frame unpack and repack, the
//!      write addresses, the cache syncs. Nothing to do with audio.
//!   2. **compressing** — the 8-band multiband compressor, written to
//!      `out.wav`. Compare that against the simulator's answer for the same
//!      input on the host, and the two halves of the toolchain have agreed
//!      about a real signal.
//!
//! The frames live in udmabuf: 8 bytes each, `[left i32][right i32]`, the
//! 24-bit sample sign-extended into each lane. Input at offset 0, output at
//! `OUT_OFFSET`. The fabric's masters drive AxCACHE=0 and therefore do not
//! snoop, so the syncs below are real work rather than ceremony — see the
//! app's dts, which deliberately does not claim `dma-coherent`.

use std::fs::OpenOptions;
use std::path::Path;
use warp11_host::mmap::MmapWindow;
use warp11_runtime::audio_batch_layout as layout;
use warp11_runtime::RegisterWindow;

/// Frames per burst in the elaborated design; `frameCount` must be a multiple.
const FRAMES_PER_BURST: usize = 32;
/// Where the processed frames land, well clear of any input we accept.
const OUT_OFFSET: usize = 4 << 20;
const BYTES_PER_FRAME: usize = 8;

// ---------------------------------------------------------------------------
// A minimal 16-bit stereo PCM WAV codec. `warp11-host` has no dependencies and
// this is not the place to acquire one.

fn read_wav(path: &Path) -> Result<(u32, Vec<(i16, i16)>), String> {
    let bytes = std::fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
    if bytes.len() < 44 || &bytes[0..4] != b"RIFF" || &bytes[8..12] != b"WAVE" {
        return Err("not a RIFF/WAVE file".into());
    }

    let u16at = |o: usize| u16::from_le_bytes([bytes[o], bytes[o + 1]]);
    let u32at = |o: usize| u32::from_le_bytes([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]);

    // Walk the chunks rather than assuming a 44-byte header: plenty of encoders
    // put a LIST chunk before the data, and guessing would read metadata as
    // audio and produce a burst of noise nobody could explain.
    let (mut rate, mut channels, mut bits) = (0u32, 0u16, 0u16);
    let mut frames = Vec::new();
    let mut pos = 12;

    while pos + 8 <= bytes.len() {
        let id = &bytes[pos..pos + 4];
        let size = u32at(pos + 4) as usize;
        let body = pos + 8;

        if id == b"fmt " && body + 16 <= bytes.len() {
            channels = u16at(body + 2);
            rate = u32at(body + 4);
            bits = u16at(body + 14);
        } else if id == b"data" {
            let end = (body + size).min(bytes.len());
            if channels != 2 || bits != 16 {
                return Err(format!("need 16-bit stereo, got {channels} ch / {bits} bit"));
            }
            let mut o = body;
            while o + 4 <= end {
                frames.push((
                    i16::from_le_bytes([bytes[o], bytes[o + 1]]),
                    i16::from_le_bytes([bytes[o + 2], bytes[o + 3]]),
                ));
                o += 4;
            }
        }

        pos = body + size + (size & 1);
    }

    if frames.is_empty() {
        return Err("no data chunk".into());
    }
    Ok((rate, frames))
}

fn write_wav(path: &Path, rate: u32, frames: &[(i16, i16)]) -> Result<(), String> {
    let data_len = (frames.len() * 4) as u32;
    let mut out = Vec::with_capacity(44 + data_len as usize);
    out.extend_from_slice(b"RIFF");
    out.extend_from_slice(&(36 + data_len).to_le_bytes());
    out.extend_from_slice(b"WAVEfmt ");
    out.extend_from_slice(&16u32.to_le_bytes());
    out.extend_from_slice(&1u16.to_le_bytes()); // PCM
    out.extend_from_slice(&2u16.to_le_bytes()); // stereo
    out.extend_from_slice(&rate.to_le_bytes());
    out.extend_from_slice(&(rate * 4).to_le_bytes()); // byte rate
    out.extend_from_slice(&4u16.to_le_bytes()); // block align
    out.extend_from_slice(&16u16.to_le_bytes()); // bits
    out.extend_from_slice(b"data");
    out.extend_from_slice(&data_len.to_le_bytes());
    for (l, r) in frames {
        out.extend_from_slice(&l.to_le_bytes());
        out.extend_from_slice(&r.to_le_bytes());
    }
    std::fs::write(path, out).map_err(|e| format!("{}: {e}", path.display()))
}

// ---------------------------------------------------------------------------
// The 16-bit ↔ 24-bit conversions, which must match `Warp11/Wav.fs` exactly or
// the comparison against the simulator is meaningless.

/// Left-justify: 16-bit full scale is 24-bit full scale, not a signal 256×
/// too quiet.
fn to_sample(v: i16) -> i32 {
    (v as i32) << 8
}

/// Round rather than truncate, so a null test comes out exact instead of
/// biased half an LSB low.
fn from_sample(v: i32) -> i16 {
    // The fabric writes the 24-bit sample sign-extended into 32 bits.
    let rounded = (v as i64 + 128) >> 8;
    rounded.clamp(-32768, 32767) as i16
}

// ---------------------------------------------------------------------------

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

fn udmabuf_dev() -> Option<(String, u64, usize)> {
    for entry in std::fs::read_dir("/sys/class/u-dma-buf").ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if !name.contains("audio-batch") {
            continue;
        }
        let phys = std::fs::read_to_string(entry.path().join("phys_addr")).ok()?;
        let size = std::fs::read_to_string(entry.path().join("size")).ok()?;
        return Some((
            name.clone(),
            u64::from_str_radix(phys.trim().trim_start_matches("0x"), 16).ok()?,
            size.trim().parse().ok()?,
        ));
    }
    None
}

fn sync(dev: &str, attr: &str, value: &str) {
    let _ = std::fs::write(format!("/sys/class/u-dma-buf/{dev}/{attr}"), value);
}

fn main() {
    let mut args = std::env::args().skip(1);
    let in_path = args.next().unwrap_or_else(|| "in.wav".into());
    let out_path = args.next().unwrap_or_else(|| "out.wav".into());

    let (rate, mut frames) = match read_wav(Path::new(&in_path)) {
        Ok(v) => v,
        Err(e) => {
            eprintln!("input: {e}");
            std::process::exit(1);
        }
    };

    // The design processes whole bursts. Pad with silence rather than refusing
    // a file for being the wrong length; the padding is trimmed on the way out.
    let real_frames = frames.len();
    while frames.len() % FRAMES_PER_BURST != 0 {
        frames.push((0, 0));
    }
    println!(
        "in:  {real_frames} frames, {rate} Hz, 2 ch  (padded to {} for {FRAMES_PER_BURST}-frame bursts)",
        frames.len()
    );

    let uio = find_uio("audiobatch").unwrap_or_else(|| {
        eprintln!("no uio node named 'audiobatch' — is the audio-batch app loaded?");
        std::process::exit(1);
    });
    let (dev, phys, arena) = udmabuf_dev().unwrap_or_else(|| {
        eprintln!("no udmabuf-audio-batch — is u-dma-buf loaded and the overlay applied?");
        std::process::exit(1);
    });

    let need = OUT_OFFSET + frames.len() * BYTES_PER_FRAME;
    if need > arena {
        eprintln!("file needs {need} bytes of arena, have {arena}");
        std::process::exit(1);
    }

    let uio_file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&uio)
        .expect("open uio");
    let mut regs = MmapWindow::open(&uio_file, 0, 0x1000).expect("register mmap");

    let id = regs.read32(layout::ID_OFFSET).expect("read id");
    if id != layout::ID_VALUE {
        eprintln!("ID mismatch: {id:#010X} — wrong bitstream? dumping the aperture:");
        // Scan the whole aperture for the ID magic. This found a real problem
        // once and is worth keeping: a stale bitstream from the *Kotlin* app of
        // the same name was still sitting in /lib/firmware/xilinx/audio-batch,
        // and dfx-mgr loaded that instead. `xmutil loadapp` reported success,
        // the uio node appeared, and the aperture answered — with somebody
        // else's registers. If the magic turns up at another offset the decode
        // is shifted; if the aperture answers but the magic is nowhere, you are
        // talking to a different design.
        let mut found = Vec::new();
        let mut nonzero = 0;
        for w in 0..(0x1000 / 4) {
            let v = regs.read32(w * 4).unwrap_or(0xDEAD);
            if v != 0 {
                nonzero += 1;
            }
            if v == layout::ID_VALUE {
                found.push(w * 4);
            }
        }
        eprintln!("  aperture scan: {nonzero} non-zero words of 1024");
        eprintln!("  ID magic found at: {found:#x?}");
        for w in 0..12 {
            eprintln!("  +{:#05x}: {:#010X}", w * 4, regs.read32(w * 4).unwrap_or(0xDEAD));
        }
        // Does anything stick? A slave that is alive but reading wrong looks
        // very different from one that is not clocked at all.
        regs.write32(layout::SRC_ADDR_OFFSET, 0xA5A50000).ok();
        eprintln!(
            "  wrote srcAddr=0xA5A50000, reads back {:#010X}",
            regs.read32(layout::SRC_ADDR_OFFSET).unwrap_or(0xDEAD)
        );
        std::process::exit(1);
    }
    println!("ID ok ({id:#010X}), arena {arena} B at {phys:#x}");

    let buf_file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(format!("/dev/{dev}"))
        .expect("open udmabuf");
    let mut arena_map = MmapWindow::open(&buf_file, 0, need).expect("arena mmap");

    // ---- stage the input ------------------------------------------------
    for (i, (l, r)) in frames.iter().enumerate() {
        let o = i * BYTES_PER_FRAME;
        arena_map.bytes_mut()[o..o + 4].copy_from_slice(&to_sample(*l).to_le_bytes());
        arena_map.bytes_mut()[o + 4..o + 8].copy_from_slice(&to_sample(*r).to_le_bytes());
    }

    let run = |regs: &mut MmapWindow, flat: bool| {
        // The fabric does not snoop, so the staged input has to be pushed out
        // of the CPU's cache before the masters go looking for it.
        sync(&dev, "sync_offset", "0");
        sync(&dev, "sync_size", &need.to_string());
        sync(&dev, "sync_direction", "1"); // DMA_TO_DEVICE
        sync(&dev, "sync_for_device", "1");

        regs.write32(layout::SRC_ADDR_OFFSET, phys as u32).unwrap();
        regs.write32(layout::DST_ADDR_OFFSET, (phys as usize + OUT_OFFSET) as u32)
            .unwrap();
        regs.write32(layout::FRAME_COUNT_OFFSET, frames.len() as u32)
            .unwrap();
        // "Flat" is a configuration, not a route around the DSP: threshold at
        // full scale with ratio 0 is no gain reduction whatever the signal, and
        // this chain reassembles bit-exactly under it. The bypass mux that used
        // to do this job was a second data path that had to be kept in step
        // with the first, and was removed once the equivalence was measured.
        let (threshold, ratio) = if flat { (0x00FF_FFFF, 0) } else { (200_000, 4) };
        regs.write32(layout::THRESHOLD_OFFSET, threshold).unwrap();
        regs.write32(layout::RATIO_OFFSET, ratio).unwrap();
        regs.write32(layout::ATTACK_OFFSET, 1 << 14).unwrap();
        regs.write32(layout::RELEASE_RATE_OFFSET, 1 << 12).unwrap();

        let t = std::time::Instant::now();
        regs.write32(layout::START_OFFSET, 1).unwrap();

        // Bounded: a design that never lowers busy is a bug, not a reason to
        // hang a board.
        let mut spins = 0u64;
        while regs.read32(layout::BUSY_OFFSET).unwrap() & 1 != 0 {
            spins += 1;
            if spins > 500_000_000 {
                eprintln!("busy never cleared — the fabric is stuck");
                std::process::exit(1);
            }
        }
        let ms = t.elapsed().as_secs_f64() * 1e3;

        // And pulled back in before the CPU reads what the fabric wrote.
        sync(&dev, "sync_direction", "2"); // DMA_FROM_DEVICE
        sync(&dev, "sync_for_cpu", "1");
        ms
    };

    let read_out = |arena_map: &MmapWindow| -> Vec<(i16, i16)> {
        (0..frames.len())
            .map(|i| {
                let o = OUT_OFFSET + i * BYTES_PER_FRAME;
                let b = arena_map.bytes();
                let l = i32::from_le_bytes(b[o..o + 4].try_into().unwrap());
                let r = i32::from_le_bytes(b[o + 4..o + 8].try_into().unwrap());
                (from_sample(l), from_sample(r))
            })
            .collect()
    };

    // ---- 1. compressing, from the state a fresh bitstream is in ---------
    // This runs FIRST and deliberately so. The filterbank and the envelope
    // followers carry state across runs — they reset only when the bitstream
    // is loaded — so a compressed pass that followed the bypassed one would
    // start from an already-adapted envelope and could not be compared with a
    // simulator starting from reset. Ordering is not a workaround for that; it
    // is what makes the comparison mean something. Running this binary twice
    // without reloading the app will give different numbers the second time.
    let ms = run(&mut regs, false);
    let processed = read_out(&arena_map);

    let peak = |v: &[(i16, i16)]| {
        v.iter().fold((0i32, 0i32), |(l, r), (a, b)| {
            (l.max((*a as i32).abs()), r.max((*b as i32).abs()))
        })
    };
    let (il, ir) = peak(&frames);
    let (ol, or) = peak(&processed);

    let cycles_per_frame = ms * 1e-3 * 100.0e6 / frames.len() as f64;
    println!(
        "compressed:     {:.3} ms for {} frames ({cycles_per_frame:.2} cycles/frame @ 100 MHz)",
        ms,
        frames.len()
    );
    println!("peaks: {il}/{ir} -> {ol}/{or}");

    // ---- 2. flat: the DDR path alone ------------------------------------
    // The accelerator as a memcpy with a 128-bit beat in the middle. Byte-exact
    // output is what says the burst read, the beat/frame unpack, the repack and
    // the write addresses all agree, with the DSP taken out of the question.
    let ms = run(&mut regs, true);
    let copied = read_out(&arena_map);
    let bypass_ok = copied == frames;
    let mismatches = copied
        .iter()
        .zip(frames.iter())
        .filter(|(a, b)| a != b)
        .count();
    println!("flat copy:      {bypass_ok}  ({mismatches} frames differ, {ms:.3} ms)");

    if !bypass_ok {
        eprintln!("the DDR path is wrong");
        std::process::exit(1);
    }

    match write_wav(Path::new(&out_path), rate, &processed[..real_frames]) {
        Ok(()) => println!("wrote {out_path}"),
        Err(e) => {
            eprintln!("output: {e}");
            std::process::exit(1);
        }
    }

    println!("FIRST LIGHT OK: a file went through the fabric and came back");
}
