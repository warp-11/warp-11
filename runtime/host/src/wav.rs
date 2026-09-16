//! A minimal 16-bit stereo PCM WAV codec, and the 16 ↔ 24-bit conversions
//! that must match `hdl/Warp11/Wav.fs` exactly or a comparison against the
//! simulator is meaningless. `warp11-host` has no dependencies and this is
//! not the place to acquire one.

use std::path::Path;

/// Frames rather than samples: one `(left, right)` per frame.
pub fn read_wav(path: &Path) -> Result<(u32, Vec<(i16, i16)>), String> {
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

pub fn write_wav(path: &Path, rate: u32, frames: &[(i16, i16)]) -> Result<(), String> {
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

/// Left-justify: 16-bit full scale is 24-bit full scale, not a signal 256×
/// too quiet. The fabric reads the low 24 bits of the lane.
pub fn to_sample(v: i16) -> i32 {
    (v as i32) << 8
}

/// Round rather than truncate, so a null test comes out exact instead of
/// biased half an LSB low. The fabric writes the 24-bit sample sign-extended
/// into its 32-bit lane.
pub fn from_sample(v: i32) -> i16 {
    let rounded = (v as i64 + 128) >> 8;
    rounded.clamp(-32768, 32767) as i16
}
