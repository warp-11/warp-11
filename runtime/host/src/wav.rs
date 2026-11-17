//! A minimal 16-bit stereo PCM WAV codec, and a recording as a design's
//! rows: the sample scaled to its field's width — 16 to 24 bits must match
//! `hdl/Warp11/Wav.fs` exactly or a comparison against the simulator is
//! meaningless. `warp11-host` has no dependencies and this is
//! not the place to acquire one.

use crate::rows::{get, put, RowField, RowShape};
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

/// A recording as the design's input rows: the row's fields taken in
/// order, two a frame — left, then right — so a row of `2k` fields holds
/// `k` frames side by side, and the last row is filled out with silence.
/// Each sample is scaled to its field's width, as a 16-bit sample is
/// left-justified in a 24-bit one. The rows come back with their count.
pub fn frames_to_rows(frames: &[(i16, i16)], shape: &RowShape) -> Result<(Vec<u32>, usize), String> {
    let per_row = frames_per_row(shape)?;
    let rows = frames.len().div_ceil(per_row);
    let mut words = vec![0u32; rows * shape.words];
    for (i, &(left, right)) in frames.iter().enumerate() {
        let row = &mut words[(i / per_row) * shape.words..][..shape.words];
        let j = 2 * (i % per_row);
        put(row, &shape.fields[j], into_field(left, &shape.fields[j]));
        put(row, &shape.fields[j + 1], into_field(right, &shape.fields[j + 1]));
    }
    Ok((words, rows))
}

/// Output rows back as frames, the fields read two a frame in order.
pub fn rows_to_frames(words: &[u32], shape: &RowShape, rows: usize) -> Result<Vec<(i16, i16)>, String> {
    let per_row = frames_per_row(shape)?;
    Ok((0..rows * per_row)
        .map(|i| {
            let row = &words[(i / per_row) * shape.words..][..shape.words];
            let j = 2 * (i % per_row);
            (out_of_field(get(row, &shape.fields[j]), &shape.fields[j]), out_of_field(get(row, &shape.fields[j + 1]), &shape.fields[j + 1]))
        })
        .collect())
}

/// Frames a row holds, or why the row is not a stereo recording's.
fn frames_per_row(shape: &RowShape) -> Result<usize, String> {
    let names = || shape.fields.iter().map(|f| f.name.as_str()).collect::<Vec<_>>().join(", ");
    if shape.fields.is_empty() || shape.fields.len() % 2 != 0 {
        return Err(format!("a recording is two fields a frame, and the row is [{}]", names()));
    }
    if let Some(f) = shape.fields.iter().find(|f| !f.signed || f.width < 16 || f.width > 64) {
        return Err(format!("'{}' is {} bits{} — a sample is a signed field of 16 to 64 bits", f.name, f.width, if f.signed { "" } else { ", unsigned" }));
    }
    Ok(shape.fields.len() / 2)
}

fn into_field(sample: i16, field: &RowField) -> i128 {
    (sample as i128) << (field.width - 16)
}

/// Rounded rather than truncated, so a null test comes out exact instead
/// of biased half an LSB low.
fn out_of_field(value: i128, field: &RowField) -> i16 {
    let shift = field.width - 16;
    let rounded = if shift == 0 { value } else { (value + (1i128 << (shift - 1))) >> shift };
    rounded.clamp(-32768, 32767) as i16
}
