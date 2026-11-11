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

/// The map's width in words, from the `APERTURE_BYTES` the seam states —
/// what decides the serial frame's shape, so a tool opens its link with
/// this rather than guessing narrow. `None` for a seam that does not say.
pub fn read_aperture_words(path: &Path) -> Result<Option<usize>, String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    for line in text.lines() {
        let line = line.trim();
        let Some(rest) = line.strip_prefix("pub const APERTURE_BYTES") else { continue };
        let Some((_, value)) = rest.split_once('=') else { continue };
        let value = value.trim().trim_end_matches(';').trim();
        let bytes = parse_number(value).ok_or_else(|| format!("bad aperture in {line}"))? as usize;
        return Ok(Some(bytes / 4));
    }
    Ok(None)
}

/// A need's name as the seam spells a constant about it: `world` is
/// `WORLD`, `cxOrigin` is `CX_ORIGIN`.
pub fn screaming(name: &str) -> String {
    let mut out = String::new();
    for (i, c) in name.chars().enumerate() {
        if c.is_ascii_uppercase() && i > 0 {
            out.push('_');
        }
        out.push(c.to_ascii_uppercase());
    }
    out
}

/// A `pub const NAME: usize = …;` the seam states beside the map — a
/// surface's rows, a link row's words — or `None` for a seam that does not.
pub fn read_const(path: &Path, name: &str) -> Result<Option<u32>, String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    for line in text.lines() {
        let line = line.trim();
        let Some(rest) = line.strip_prefix("pub const ") else { continue };
        let Some((found, value)) = rest.split_once(':') else { continue };
        if found.trim() != name {
            continue;
        }
        let Some((_, value)) = value.split_once('=') else { continue };
        let value = value.trim().trim_end_matches(';').trim();
        return parse_number(value).map(Some).ok_or_else(|| format!("bad value in {line}"));
    }
    Ok(None)
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

/// A region by its name in the design — `source` is `SOURCE_REGION_OFFSET`
/// and the rest — as the seam places it, or why not.
pub fn read_region(path: &Path, name: &str) -> Result<warp11_runtime::region::Region, String> {
    let s = screaming(name);
    let need = |what: &str| -> Result<usize, String> {
        let constant = format!("{s}_{what}");
        read_const(path, &constant)?
            .map(|v| v as usize)
            .ok_or_else(|| format!("{}: no {constant} — is '{name}' a region shared with the host?", path.display()))
    };
    Ok(warp11_runtime::region::Region {
        offset: need("REGION_OFFSET")?,
        records: need("RECORDS")?,
        record_bytes: need("RECORD_BYTES")?,
        stride_bytes: need("STRIDE_BYTES")?,
    })
}

/// A field of a region's record — `source`, `weight` is `SOURCE_WEIGHT_*`.
pub fn read_field(path: &Path, region: &str, field: &str) -> Result<warp11_runtime::region::Field, String> {
    use warp11_runtime::region::Field;
    let s = format!("{}_{}", screaming(region), screaming(field));
    let offset = read_const(path, &format!("{s}_OFFSET"))?
        .ok_or_else(|| format!("{}: no {s}_OFFSET — no field '{field}' in region '{region}'", path.display()))? as usize;
    if let Some(words) = read_const(path, &format!("{s}_WORDS"))? {
        return Ok(Field::Words { offset, words: words as usize });
    }
    let shift = read_const(path, &format!("{s}_SHIFT"))?.ok_or_else(|| format!("{}: no {s}_SHIFT", path.display()))?;
    let mask = read_const(path, &format!("{s}_MASK"))?.ok_or_else(|| format!("{}: no {s}_MASK", path.display()))?;
    Ok(Field::Bits { offset, shift, mask })
}
