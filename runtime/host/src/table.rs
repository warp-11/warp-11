//! A table through a design: a CSV a row a beat, each column one of the
//! design's input fields, and what comes back written as a CSV of its output
//! fields. Only the file is this module's; where each field sits in a row is
//! the seam's (`rows::row_shape`), and how the rows travel is the carrier's.
//!
//! A field with fraction bits reads and writes as a decimal, scaled by
//! `2^fraction`.

use crate::rows::{get, put, range, RowField, RowShape};
use std::path::Path;

pub use crate::rows::{row_shape, Constants};

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
    let (low, high) = range(field);
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

/// The table's rows as the design's input rows, `shape.words` words each.
/// Every input field must be a column; the columns may come in any order.
pub fn to_rows(table: &Table, shape: &RowShape) -> Result<Vec<u32>, String> {
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
    let mut words = vec![0u32; table.rows.len() * shape.words];
    for (r, row) in table.rows.iter().enumerate() {
        let out = &mut words[r * shape.words..(r + 1) * shape.words];
        for (field, &column) in shape.fields.iter().zip(&columns) {
            put(out, field, encode(field, &row[column]).map_err(|e| format!("row {}: {e}", r + 1))?);
        }
    }
    Ok(words)
}

/// `rows` output rows back as a table, the fields as columns.
pub fn from_rows(words: &[u32], shape: &RowShape, rows: usize) -> Table {
    let rows = (0..rows)
        .map(|r| {
            let row = &words[r * shape.words..(r + 1) * shape.words];
            shape.fields.iter().map(|field| decode(field, get(row, field))).collect()
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

    fn shape(fields: Vec<RowField>) -> RowShape {
        let words = fields.iter().map(|f| f.lane + f.lanes).max().unwrap_or(0);
        RowShape { fields, words, bytes: None }
    }

    #[test]
    fn a_wide_field_takes_its_lanes_low_word_first() {
        let shape = shape(vec![field("sum", 0, 2, 33, 0, false)]);
        assert_eq!(from_rows(&[0xFFFF_FFFF, 1], &shape, 1).rows, vec![vec![((1u64 << 33) - 1).to_string()]]);
    }

    #[test]
    fn a_signed_field_round_trips_through_its_lanes() {
        let shape = shape(vec![field("v", 0, 1, 24, 0, true)]);
        let table = Table { header: vec!["v".into()], rows: vec![vec!["-5".into()], vec!["8388607".into()]] };
        let words = to_rows(&table, &shape).unwrap();
        // Sign-extended into the word, as the fabric writes it.
        assert_eq!(words[0], (-5i32) as u32);
        assert_eq!(from_rows(&words, &shape, 2).rows, table.rows);
    }

    #[test]
    fn columns_are_matched_by_name_not_order() {
        let shape = shape(vec![field("x", 0, 1, 32, 0, false), field("y", 1, 1, 32, 0, false)]);
        let table = Table { header: vec!["y".into(), "x".into()], rows: vec![vec!["2".into(), "1".into()]] };
        assert_eq!(to_rows(&table, &shape).unwrap(), vec![1, 2]);
    }

    #[test]
    fn a_fraction_reads_as_a_decimal() {
        let shape = shape(vec![field("g", 0, 1, 16, 8, false)]);
        let table = Table { header: vec!["g".into()], rows: vec![vec!["1.5".into()]] };
        let words = to_rows(&table, &shape).unwrap();
        assert_eq!(words[0], 384);
        assert_eq!(from_rows(&words, &shape, 1).rows, table.rows);
    }

    #[test]
    fn a_value_too_wide_or_a_missing_column_is_said() {
        let shape = shape(vec![field("x", 0, 1, 8, 0, false)]);
        let wide = Table { header: vec!["x".into()], rows: vec![vec!["256".into()]] };
        assert!(to_rows(&wide, &shape).unwrap_err().contains("does not fit"));
        let missing = Table { header: vec!["y".into()], rows: vec![vec!["1".into()]] };
        assert!(to_rows(&missing, &shape).unwrap_err().contains("no column 'x'"));
    }
}
