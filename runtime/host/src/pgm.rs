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
