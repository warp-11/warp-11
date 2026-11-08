//! A greyscale image as a file — PGM, binary `P5`, one byte a pixel — the
//! shape a drawn frame comes back in. The F# side reads and writes the same.

use std::path::Path;

pub fn write_pgm(path: &Path, width: usize, height: usize, pixels: &[u8]) -> Result<(), String> {
    if pixels.len() != width * height {
        return Err(format!("{}×{} is {} pixels, given {}", width, height, width * height, pixels.len()));
    }
    let mut bytes = format!("P5\n{width} {height}\n255\n").into_bytes();
    bytes.extend_from_slice(pixels);
    std::fs::write(path, bytes).map_err(|e| format!("{}: {e}", path.display()))
}

/// A binary `P5` PGM read back: width, height and one byte a pixel, the
/// comment lines a tool may have left stepped over.
pub fn read_pgm(path: &Path) -> Result<(usize, usize, Vec<u8>), String> {
    let bytes = std::fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut fields: Vec<String> = Vec::new();
    let mut at = 0;
    while fields.len() < 4 && at < bytes.len() {
        let c = bytes[at];
        if c == b'#' {
            while at < bytes.len() && bytes[at] != b'\n' {
                at += 1;
            }
        } else if c.is_ascii_whitespace() {
            at += 1;
        } else {
            let start = at;
            while at < bytes.len() && !bytes[at].is_ascii_whitespace() {
                at += 1;
            }
            fields.push(String::from_utf8_lossy(&bytes[start..at]).into_owned());
        }
    }
    if fields.len() < 4 || fields[0] != "P5" {
        return Err(format!("{}: not a binary P5 PGM", path.display()));
    }
    let width: usize = fields[1].parse().map_err(|_| format!("{}: bad width", path.display()))?;
    let height: usize = fields[2].parse().map_err(|_| format!("{}: bad height", path.display()))?;
    if fields[3] != "255" {
        return Err(format!("{}: a pixel is one byte here, maxval 255", path.display()));
    }
    // One whitespace byte after the header, then the pixels.
    at += 1;
    let pixels = bytes.get(at..at + width * height).ok_or_else(|| format!("{}: short of pixels", path.display()))?;
    Ok((width, height, pixels.to_vec()))
}
