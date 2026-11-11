//! The host's half of regions over the link (`notes/DEVICES.md` §13, rung 5):
//! the fabric streams a header word — the operation in the top four bits,
//! the region's id in the next four, the record's index in the low
//! twenty-four — and a write's record after it; this side keeps every
//! region's records, answers a read with the record's words and a write
//! with one word once it has stored it. That word is what *landed* means
//! over a link, so it goes back only after the record is kept.
//!
//! Every number is the seam's: `{NAME}_REGION_ID`, `_RECORDS`,
//! `_RECORD_BYTES`, `REGION_LINK_READ`/`_WRITE`.

use std::path::Path;

use crate::seam::{read_const, screaming};
use crate::serial_window::{SerialError, SerialWindow};

/// The operations a header word carries, as the F# side states them.
pub const LINK_READ: u32 = 1;
pub const LINK_WRITE: u32 = 2;
/// The word a write is answered with. Its value is nobody's business; that
/// it arrives is the whole message.
pub const LINK_LANDED: u32 = 0xAC;

/// One region the host keeps.
#[derive(Debug, Clone)]
pub struct LinkRegion {
    pub name: String,
    pub id: u32,
    pub records: usize,
    pub record_bytes: usize,
    /// The records, `record_bytes` each.
    pub bytes: Vec<u8>,
}

impl LinkRegion {
    /// A region by its name in the design, as the seam places it.
    pub fn from_seam(path: &Path, name: &str) -> Result<Self, String> {
        let s = screaming(name);
        let need = |what: &str| -> Result<u32, String> {
            read_const(path, &format!("{s}_{what}"))?
                .ok_or_else(|| format!("{}: no {s}_{what} — is '{name}' a region over the link?", path.display()))
        };
        let records = need("RECORDS")? as usize;
        let record_bytes = need("RECORD_BYTES")? as usize;
        Ok(LinkRegion { name: name.to_string(), id: need("REGION_ID")?, records, record_bytes, bytes: vec![0; records * record_bytes] })
    }

    pub fn record(&self, index: usize) -> &[u8] {
        &self.bytes[index * self.record_bytes..(index + 1) * self.record_bytes]
    }

    pub fn record_mut(&mut self, index: usize) -> &mut [u8] {
        &mut self.bytes[index * self.record_bytes..(index + 1) * self.record_bytes]
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum LinkRegionError {
    /// A header naming an operation, a region or a record this host does not have.
    BadHeader(u32),
}

/// Every region the design keeps over the link, and a write half-received.
pub struct LinkRegions {
    regions: Vec<LinkRegion>,
    writing: Option<(usize, usize, Vec<u32>)>,
}

impl LinkRegions {
    pub fn new(regions: Vec<LinkRegion>) -> Self {
        LinkRegions { regions, writing: None }
    }

    /// The regions named, from a seam.
    pub fn from_seam(path: &Path, names: &[&str]) -> Result<Self, String> {
        Ok(Self::new(names.iter().map(|n| LinkRegion::from_seam(path, n)).collect::<Result<_, _>>()?))
    }

    pub fn region(&self, name: &str) -> Option<&LinkRegion> {
        self.regions.iter().find(|r| r.name == name)
    }

    pub fn region_mut(&mut self, name: &str) -> Option<&mut LinkRegion> {
        self.regions.iter_mut().find(|r| r.name == name)
    }

    /// Take the words the fabric streamed, in order, and hand back the words
    /// to play: a read's record, or a write's landed word once its record is
    /// stored. A write split across calls is carried over.
    pub fn serve(&mut self, words: &[u32]) -> Result<Vec<u32>, LinkRegionError> {
        let mut answer = Vec::new();
        for &w in words {
            match self.writing.take() {
                None => {
                    let op = w >> 28;
                    let id = (w >> 24) & 0xF;
                    let index = (w & 0xFF_FFFF) as usize;
                    let at = self
                        .regions
                        .iter()
                        .position(|r| r.id == id && index < r.records)
                        .ok_or(LinkRegionError::BadHeader(w))?;
                    match op {
                        LINK_READ => {
                            let record = self.regions[at].record(index);
                            answer.extend(record.chunks(4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])));
                        }
                        LINK_WRITE => self.writing = Some((at, index, Vec::new())),
                        _ => return Err(LinkRegionError::BadHeader(w)),
                    }
                }
                Some((at, index, mut got)) => {
                    got.push(w);
                    let region = &mut self.regions[at];
                    if got.len() * 4 == region.record_bytes {
                        for (k, v) in got.iter().enumerate() {
                            region.record_mut(index)[4 * k..4 * k + 4].copy_from_slice(&v.to_le_bytes());
                        }
                        answer.push(LINK_LANDED);
                    } else {
                        self.writing = Some((at, index, got));
                    }
                }
            }
        }
        Ok(answer)
    }

    /// Serve the link until it goes quiet: every streamed word answered,
    /// whatever register traffic ran beside it. Returns how many words were
    /// served.
    pub fn pump(&mut self, link: &mut SerialWindow) -> Result<usize, LinkPumpError> {
        let mut served = 0;
        loop {
            let mut words = link.take_streamed();
            if words.is_empty() {
                words = link.read_streamed(64).map_err(LinkPumpError::Link)?;
            }
            if words.is_empty() {
                return Ok(served);
            }
            served += words.len();
            let answer = self.serve(&words).map_err(LinkPumpError::Region)?;
            if !answer.is_empty() {
                link.play(&answer).map_err(LinkPumpError::Link)?;
            }
        }
    }
}

#[derive(Debug)]
pub enum LinkPumpError {
    Link(SerialError),
    Region(LinkRegionError),
}

#[cfg(test)]
mod tests {
    use super::*;

    fn regions() -> LinkRegions {
        LinkRegions::new(vec![
            LinkRegion { name: "source".into(), id: 0, records: 4, record_bytes: 8, bytes: (0..32).collect() },
            LinkRegion { name: "copied".into(), id: 1, records: 4, record_bytes: 8, bytes: vec![0; 32] },
        ])
    }

    fn header(op: u32, id: u32, index: u32) -> u32 {
        op << 28 | id << 24 | index
    }

    #[test]
    fn a_read_is_answered_with_its_record() {
        let mut r = regions();
        let answer = r.serve(&[header(LINK_READ, 0, 2)]).unwrap();
        assert_eq!(answer, vec![u32::from_le_bytes([16, 17, 18, 19]), u32::from_le_bytes([20, 21, 22, 23])]);
    }

    #[test]
    fn a_write_lands_only_once_its_record_is_kept() {
        let mut r = regions();
        // Split across two calls, as words arrive in whatever pieces the link gives.
        assert!(r.serve(&[header(LINK_WRITE, 1, 3), 0x1111_2222]).unwrap().is_empty());
        assert_eq!(r.region("copied").unwrap().record(3), &[0; 8]);
        assert_eq!(r.serve(&[0x3333_4444]).unwrap(), vec![LINK_LANDED]);
        let kept = r.region("copied").unwrap().record(3);
        assert_eq!(kept, [0x22, 0x22, 0x11, 0x11, 0x44, 0x44, 0x33, 0x33]);
    }

    #[test]
    fn a_header_for_nothing_this_host_keeps_is_refused() {
        let mut r = regions();
        assert_eq!(r.serve(&[header(LINK_READ, 5, 0)]), Err(LinkRegionError::BadHeader(header(LINK_READ, 5, 0))));
        assert_eq!(r.serve(&[header(LINK_READ, 0, 4)]), Err(LinkRegionError::BadHeader(header(LINK_READ, 0, 4))));
    }
}
