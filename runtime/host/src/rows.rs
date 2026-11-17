//! A row of a design's boundary as the seam states it, and the one packing
//! every file format and every carrier shares. A file format turns its file
//! into rows of lane words (`table`, `wav`, `pgm`); a carrier only moves the
//! words — the link as they are, host memory padded to the row's bytes and
//! whole bursts (`to_memory`, `from_memory`). Neither knows the other.
//!
//! A field takes `lanes` 32-bit words from `lane`, low word first; a signed
//! field is sign-extended through them, an unsigned one zero-extended — the
//! fabric's own placing (`laneSpans` in `BoardTop.fs`).

use std::collections::HashMap;
use std::path::Path;

/// The seam's constants by name, from the file a build wrote or from the
/// Sim bridge, which answers the same text.
pub struct Constants(HashMap<String, String>);

impl Constants {
    /// Every `pub const NAME: T = value;` in a seam file.
    pub fn from_seam(path: &Path) -> Result<Constants, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
        let mut map = HashMap::new();
        for line in text.lines() {
            let Some(rest) = line.trim().strip_prefix("pub const ") else { continue };
            let Some((name, rest)) = rest.split_once(':') else { continue };
            let Some((_, value)) = rest.split_once('=') else { continue };
            let value = value.trim().trim_end_matches(';').trim().trim_matches('"');
            map.insert(name.trim().to_string(), value.to_string());
        }
        Ok(Constants(map))
    }

    /// `NAME=value` pairs separated by spaces — the bridge's `S` answer.
    pub fn from_pairs(text: &str) -> Constants {
        Constants(
            text.split_whitespace()
                .filter_map(|p| p.split_once('='))
                .map(|(n, v)| (n.to_string(), v.to_string()))
                .collect(),
        )
    }

    pub fn text(&self, name: &str) -> Result<&str, String> {
        self.0.get(name).map(String::as_str).ok_or_else(|| format!("the seam has no {name}"))
    }

    pub fn number(&self, name: &str) -> Result<u64, String> {
        let text = self.text(name)?.replace('_', "");
        match text.strip_prefix("0x") {
            Some(hex) => u64::from_str_radix(hex, 16),
            None => text.parse(),
        }
        .map_err(|_| format!("{name} = {text} is not a number"))
    }
}

/// One field of a row: where it starts, how many words it takes, and the
/// format those words hold.
#[derive(Clone, Debug, PartialEq)]
pub struct RowField {
    pub name: String,
    pub lane: usize,
    pub lanes: usize,
    pub width: u32,
    pub fraction: u32,
    pub signed: bool,
}

/// A row of the boundary: its fields, the words they take, and — where the
/// carrier pads a row, as host memory does — the bytes it takes there.
#[derive(Clone, Debug, PartialEq)]
pub struct RowShape {
    pub fields: Vec<RowField>,
    /// Lane words a row takes: what the link sends, and what every format
    /// fills.
    pub words: usize,
    /// The row's footprint in host memory; `None` from a link's seam.
    pub bytes: Option<usize>,
}

/// The rows in (`side` = `"IN"`) or out (`"OUT"`) as the seam states them.
pub fn row_shape(c: &Constants, side: &str) -> Result<RowShape, String> {
    let names = c.text(&format!("ROW_{side}_FIELDS"))?;
    let fields = names
        .split(',')
        .filter(|n| !n.is_empty())
        .map(|name| {
            let s = format!("ROW_{side}_{}", screaming(name));
            Ok(RowField {
                name: name.to_string(),
                lane: c.number(&format!("{s}_LANE"))? as usize,
                lanes: c.number(&format!("{s}_LANES"))? as usize,
                width: c.number(&format!("{s}_WIDTH"))? as u32,
                fraction: c.number(&format!("{s}_FRACTION"))? as u32,
                signed: c.number(&format!("{s}_SIGNED"))? != 0,
            })
        })
        .collect::<Result<Vec<_>, String>>()?;
    let words = fields.iter().map(|f| f.lane + f.lanes).max().unwrap_or(0);
    let bytes = c.number(&format!("ROW_{side}_BYTES")).ok().map(|b| b as usize);
    if let Some(b) = bytes {
        if b < words * 4 {
            return Err(format!("ROW_{side}_BYTES = {b} cannot hold a row of {words} words"));
        }
    }
    Ok(RowShape { fields, words, bytes })
}

/// `camelCase` as the seam spells it: `SCREAMING_SNAKE`.
fn screaming(name: &str) -> String {
    let mut out = String::new();
    for (i, ch) in name.chars().enumerate() {
        if ch.is_uppercase() && i > 0 {
            out.push('_');
        }
        out.push(ch.to_ascii_uppercase());
    }
    out
}

/// The values a field holds: its range as integers.
pub fn range(field: &RowField) -> (i128, i128) {
    if field.signed {
        (-(1i128 << (field.width - 1)), (1i128 << (field.width - 1)) - 1)
    } else {
        (0, (1i128 << field.width) - 1)
    }
}

/// `value` into the field's lanes of `row`, sign- or zero-extended through
/// them. The caller has checked the range.
pub fn put(row: &mut [u32], field: &RowField, value: i128) {
    for k in 0..field.lanes {
        row[field.lane + k] = if k < 4 { (value >> (32 * k)) as u32 } else if value < 0 { u32::MAX } else { 0 };
    }
}

/// The field's value out of `row`: its lanes masked to its width, and
/// sign-extended if it is signed. A field wider than 127 bits is lanes of
/// bytes, not a number — read it with `bytes_of`.
pub fn get(row: &[u32], field: &RowField) -> i128 {
    let mut raw: u128 = 0;
    for k in 0..field.lanes.min(4) {
        raw |= (row[field.lane + k] as u128) << (32 * k);
    }
    let raw = if field.width >= 128 { raw } else { raw & ((1u128 << field.width) - 1) };
    if field.signed && field.width < 128 && (raw >> (field.width - 1)) & 1 == 1 {
        raw as i128 - (1i128 << field.width)
    } else {
        raw as i128
    }
}

/// A row's words as bytes, low word first, low byte first.
pub fn bytes_of(row: &[u32]) -> Vec<u8> {
    row.iter().flat_map(|w| w.to_le_bytes()).collect()
}

/// Rows of `shape.words` words as host memory takes them: each row padded
/// with zeros to `shape.bytes`, the rows padded with zero rows to a
/// multiple of `rows_per_burst`. The rows staged come back with them.
pub fn to_memory(words: &[u32], shape: &RowShape, rows: usize, rows_per_burst: usize) -> Result<(Vec<u8>, usize), String> {
    let stride = shape.bytes.ok_or("host memory needs the row's bytes, and the seam does not state them")?;
    let padded = rows.div_ceil(rows_per_burst).max(1) * rows_per_burst;
    let mut bytes = vec![0u8; padded * stride];
    for (r, row) in words.chunks(shape.words.max(1)).take(rows).enumerate() {
        bytes[r * stride..r * stride + 4 * row.len()].copy_from_slice(&bytes_of(row));
    }
    Ok((bytes, padded))
}

/// `rows` rows back out of host memory's bytes, each row's padding dropped.
pub fn from_memory(bytes: &[u8], shape: &RowShape, rows: usize) -> Result<Vec<u32>, String> {
    let stride = shape.bytes.ok_or("host memory needs the row's bytes, and the seam does not state them")?;
    Ok((0..rows)
        .flat_map(|r| {
            (0..shape.words).map(move |k| {
                let at = r * stride + 4 * k;
                u32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]])
            })
        })
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    pub(crate) fn field(name: &str, lane: usize, lanes: usize, width: u32, fraction: u32, signed: bool) -> RowField {
        RowField { name: name.into(), lane, lanes, width, fraction, signed }
    }

    #[test]
    fn the_seam_states_the_shape() {
        let c = Constants::from_pairs(
            "ROW_IN_FIELDS=x,y ROW_IN_BYTES=8 ROW_IN_X_LANE=0 ROW_IN_X_LANES=1 ROW_IN_X_WIDTH=32 \
             ROW_IN_X_FRACTION=0 ROW_IN_X_SIGNED=0 ROW_IN_Y_LANE=1 ROW_IN_Y_LANES=1 ROW_IN_Y_WIDTH=32 \
             ROW_IN_Y_FRACTION=0 ROW_IN_Y_SIGNED=0",
        );
        let shape = row_shape(&c, "IN").unwrap();
        assert_eq!(shape.bytes, Some(8));
        assert_eq!(shape.words, 2);
        assert_eq!(shape.fields, vec![field("x", 0, 1, 32, 0, false), field("y", 1, 1, 32, 0, false)]);
    }

    #[test]
    fn a_link_seam_states_the_row_without_its_bytes() {
        let c = Constants::from_pairs("ROW_OUT_FIELDS=sum ROW_OUT_SUM_LANE=0 ROW_OUT_SUM_LANES=2 ROW_OUT_SUM_WIDTH=33 ROW_OUT_SUM_FRACTION=0 ROW_OUT_SUM_SIGNED=0");
        let shape = row_shape(&c, "OUT").unwrap();
        assert_eq!((shape.words, shape.bytes), (2, None));
    }

    #[test]
    fn a_signed_value_is_sign_extended_through_its_lanes_and_back() {
        let f = field("v", 0, 2, 40, 0, true);
        let mut row = [0u32; 2];
        put(&mut row, &f, -5);
        assert_eq!(row, [(-5i32) as u32, u32::MAX]);
        assert_eq!(get(&row, &f), -5);
    }

    #[test]
    fn memory_pads_each_row_and_the_rows_to_a_burst_and_takes_it_off_again() {
        let shape = RowShape { fields: vec![field("x", 0, 1, 32, 0, false), field("y", 1, 2, 40, 0, false)], words: 3, bytes: Some(16) };
        let words = [1, 2, 3, 4, 5, 6];
        let (bytes, padded) = to_memory(&words, &shape, 2, 4).unwrap();
        assert_eq!((bytes.len(), padded), (64, 4));
        assert_eq!(&bytes[16..28], &bytes_of(&[4, 5, 6])[..]);
        assert!(bytes[12..16].iter().all(|&b| b == 0));
        assert_eq!(from_memory(&bytes, &shape, 2).unwrap(), words);
    }
}
