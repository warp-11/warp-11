//! A table through a design: a CSV a row a beat, each column one of the
//! design's input fields, and what comes back written as a CSV of its output
//! fields. Where each field sits in a row comes from the seam (`ROW_IN_*`,
//! `ROW_OUT_*`), so the host needs neither the design file nor a guess.
//!
//! A field takes `lanes` 32-bit words from `lane`, low word first; a signed
//! field is sign-extended through them, an unsigned one zero-extended — the
//! fabric's own placing. A field with fraction bits reads and writes as a
//! decimal, scaled by `2^fraction`.

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

/// A row of the boundary as the host's memory holds it.
#[derive(Clone, Debug, PartialEq)]
pub struct RowShape {
    pub fields: Vec<RowField>,
    pub bytes: usize,
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
    Ok(RowShape { fields, bytes: c.number(&format!("ROW_{side}_BYTES"))? as usize })
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

/// A CSV: its header and its rows, every cell as text.
#[derive(Clone, Debug, PartialEq)]
pub struct Table {
    pub header: Vec<String>,
    pub rows: Vec<Vec<String>>,
}

pub fn read_csv(path: &Path) -> Result<Table, String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut lines = text.lines().map(str::trim).filter(|l| !l.is_empty());
    let header: Vec<String> = lines
        .next()
        .ok_or_else(|| format!("{}: empty", path.display()))?
        .split(',')
        .map(|h| h.trim().to_string())
        .collect();
    let rows = lines
        .enumerate()
        .map(|(i, l)| {
            let cells: Vec<String> = l.split(',').map(|c| c.trim().to_string()).collect();
            if cells.len() == header.len() {
                Ok(cells)
            } else {
                Err(format!("{}: row {} has {} cells, the header {}", path.display(), i + 1, cells.len(), header.len()))
            }
        })
        .collect::<Result<Vec<_>, String>>()?;
    Ok(Table { header, rows })
}

pub fn write_csv(path: &Path, table: &Table) -> Result<(), String> {
    let mut text = table.header.join(",");
    text.push('\n');
    for row in &table.rows {
        text.push_str(&row.join(","));
        text.push('\n');
    }
    std::fs::write(path, text).map_err(|e| format!("{}: {e}", path.display()))
}

/// A cell as the field's integer — scaled by `2^fraction` — or why it does
/// not fit.
fn encode(field: &RowField, cell: &str) -> Result<i128, String> {
    let value: i128 = if field.fraction == 0 {
        cell.parse().map_err(|_| format!("'{cell}' is not an integer for '{}'", field.name))?
    } else {
        let real: f64 = cell.parse().map_err(|_| format!("'{cell}' is not a number for '{}'", field.name))?;
        (real * (1u128 << field.fraction) as f64).round() as i128
    };
    let (low, high) = if field.signed {
        (-(1i128 << (field.width - 1)), (1i128 << (field.width - 1)) - 1)
    } else {
        (0, (1i128 << field.width) - 1)
    };
    if value < low || value > high {
        return Err(format!("'{cell}' does not fit '{}' ({} bits{})", field.name, field.width, if field.signed { ", signed" } else { "" }));
    }
    Ok(value)
}

/// The field's integer back as a cell.
fn decode(field: &RowField, value: i128) -> String {
    if field.fraction == 0 {
        value.to_string()
    } else {
        (value as f64 / (1u128 << field.fraction) as f64).to_string()
    }
}

/// The table's rows as the fabric reads them, padded with zero rows to a
/// multiple of `rows_per_burst`; the count staged comes back with them.
/// Every input field must be a column; the columns may come in any order.
pub fn stage(table: &Table, shape: &RowShape, rows_per_burst: usize) -> Result<(Vec<u8>, usize), String> {
    let columns = shape
        .fields
        .iter()
        .map(|f| {
            table
                .header
                .iter()
                .position(|h| *h == f.name)
                .ok_or_else(|| format!("no column '{}' — the design's input is [{}]", f.name, names(shape)))
        })
        .collect::<Result<Vec<_>, String>>()?;
    let padded = table.rows.len().div_ceil(rows_per_burst).max(1) * rows_per_burst;
    let mut bytes = vec![0u8; padded * shape.bytes];
    for (r, row) in table.rows.iter().enumerate() {
        for (field, &column) in shape.fields.iter().zip(&columns) {
            let value = encode(field, &row[column]).map_err(|e| format!("row {}: {e}", r + 1))?;
            for k in 0..field.lanes {
                let word = (value >> (32 * k)) as u32;
                let at = r * shape.bytes + (field.lane + k) * 4;
                bytes[at..at + 4].copy_from_slice(&word.to_le_bytes());
            }
        }
    }
    Ok((bytes, padded))
}

/// `rows` rows of the output shape back as a table, the fields as columns.
pub fn unstage(bytes: &[u8], shape: &RowShape, rows: usize) -> Table {
    let rows = (0..rows)
        .map(|r| {
            shape
                .fields
                .iter()
                .map(|field| {
                    let mut raw: u128 = 0;
                    for k in 0..field.lanes {
                        let at = r * shape.bytes + (field.lane + k) * 4;
                        let word = u32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]]);
                        raw |= (word as u128) << (32 * k);
                    }
                    let mask = (1u128 << field.width) - 1;
                    let raw = raw & mask;
                    let value = if field.signed && raw >> (field.width - 1) & 1 == 1 {
                        raw as i128 - (1i128 << field.width)
                    } else {
                        raw as i128
                    };
                    decode(field, value)
                })
                .collect()
        })
        .collect();
    Table { header: shape.fields.iter().map(|f| f.name.clone()).collect(), rows }
}

fn names(shape: &RowShape) -> String {
    shape.fields.iter().map(|f| f.name.as_str()).collect::<Vec<_>>().join(", ")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn field(name: &str, lane: usize, lanes: usize, width: u32, fraction: u32, signed: bool) -> RowField {
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
        assert_eq!(shape.bytes, 8);
        assert_eq!(shape.fields, vec![field("x", 0, 1, 32, 0, false), field("y", 1, 1, 32, 0, false)]);
    }

    #[test]
    fn a_wide_field_takes_its_lanes_low_word_first() {
        let shape = RowShape { fields: vec![field("sum", 0, 2, 33, 0, false)], bytes: 8 };
        let bytes = [0xFFu8, 0xFF, 0xFF, 0xFF, 0x01, 0, 0, 0];
        assert_eq!(unstage(&bytes, &shape, 1).rows, vec![vec![((1u64 << 33) - 1).to_string()]]);
    }

    #[test]
    fn a_signed_field_round_trips_through_its_lanes() {
        let shape = RowShape { fields: vec![field("v", 0, 1, 24, 0, true)], bytes: 4 };
        let table = Table { header: vec!["v".into()], rows: vec![vec!["-5".into()], vec!["8388607".into()]] };
        let (bytes, padded) = stage(&table, &shape, 4).unwrap();
        assert_eq!(padded, 4);
        // Sign-extended into the word, as the fabric writes it.
        assert_eq!(&bytes[0..4], &(-5i32).to_le_bytes());
        assert_eq!(unstage(&bytes, &shape, 2).rows, table.rows);
    }

    #[test]
    fn a_fraction_reads_as_a_decimal() {
        let shape = RowShape { fields: vec![field("g", 0, 1, 16, 8, false)], bytes: 4 };
        let table = Table { header: vec!["g".into()], rows: vec![vec!["1.5".into()]] };
        let (bytes, _) = stage(&table, &shape, 1).unwrap();
        assert_eq!(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]), 384);
        assert_eq!(unstage(&bytes, &shape, 1).rows, table.rows);
    }

    #[test]
    fn a_value_too_wide_or_a_missing_column_is_said() {
        let shape = RowShape { fields: vec![field("x", 0, 1, 8, 0, false)], bytes: 4 };
        let wide = Table { header: vec!["x".into()], rows: vec![vec!["256".into()]] };
        assert!(stage(&wide, &shape, 1).unwrap_err().contains("does not fit"));
        let missing = Table { header: vec!["y".into()], rows: vec![vec!["1".into()]] };
        assert!(stage(&missing, &shape, 1).unwrap_err().contains("no column 'x'"));
    }
}
