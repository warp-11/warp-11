//! The seam a build directory writes, read back by a tool: the `*_OFFSET`
//! constants name a design's registers, and `LAYOUT_HASH_VALUE` says which
//! revision of the map the fabric was built from. A generic tool cannot
//! compile the seam in, so it reads it — and a person's spelling of a
//! register (`volume`, `srcAddr`) meets the seam's (`VOLUME`, `SRC_ADDR`)
//! through [`key`].

use std::path::Path;

/// `volume`, `VOLUME_OFFSET`, `srcAddr`, `SRC_ADDR` all compare as `volume`
/// and `srcaddr`.
pub fn key(name: &str) -> String {
    name.chars()
        .filter(|c| *c != '_')
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

pub fn parse_number(s: &str) -> Option<u32> {
    if let Some(hex) = s.strip_prefix("0x") {
        u32::from_str_radix(hex, 16).ok()
    } else {
        s.parse().ok()
    }
}

/// The registers a seam file names, and the layout hash it was built
/// against.
pub fn read_layout(path: &Path) -> Result<(Vec<(String, usize)>, Option<u32>), String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut registers = Vec::new();
    let mut hash = None;
    for line in text.lines() {
        let line = line.trim();
        let Some(rest) = line.strip_prefix("pub const ") else { continue };
        let Some((name, value)) = rest.split_once(':') else { continue };
        let Some((_, value)) = value.split_once('=') else { continue };
        let value = value.trim().trim_end_matches(';').trim();
        if let Some(reg) = name.strip_suffix("_OFFSET") {
            let offset = parse_number(value).ok_or_else(|| format!("bad offset in {line}"))? as usize;
            registers.push((reg.to_string(), offset));
        } else if name == "LAYOUT_HASH_VALUE" {
            hash = Some(parse_number(value).ok_or_else(|| format!("bad hash in {line}"))?);
        }
    }
    Ok((registers, hash))
}

/// A register named the person's way, resolved to its offset — or which
/// names the seam does know.
pub fn resolve(name: &str, registers: &[(String, usize)]) -> Result<usize, String> {
    registers
        .iter()
        .find(|(r, _)| key(r) == key(name))
        .map(|(_, offset)| *offset)
        .ok_or_else(|| {
            let known: Vec<&str> = registers.iter().map(|(r, _)| r.as_str()).collect();
            format!("no register '{name}' — the design has [{}]", known.join(", "))
        })
}
