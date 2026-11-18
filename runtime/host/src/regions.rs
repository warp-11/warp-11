//! A design's regions from the host's side, by name: records in, records
//! out, and nothing about where they are kept (`notes/DEVICES.md` §13). The
//! fabric's half is the same idea — a design names a region and the board
//! places it — so a host written against [`Regions`] runs unchanged whether
//! the board keeps them in memory both sides reach or the host keeps them
//! and answers for them over the link.

use std::cell::RefCell;
use std::rc::Rc;

use warp11_runtime::region::{Arena, Region};

use crate::link_regions::{LinkRegion, LinkRegions};
use crate::seam::{key, Seam};
use crate::serial_window::SerialWindow;

/// Records by region name and index, as words, back to back — a region's
/// padding between records, where its carrier wants any, never shows.
pub trait Regions {
    /// How many records the build gave a region.
    fn records(&self, region: &str) -> Result<usize, String>;
    /// `records` records from `first`.
    fn read(&mut self, region: &str, first: usize, records: usize) -> Result<Vec<u32>, String>;
    /// Whole records from `first`, where the fabric will find them.
    fn write(&mut self, region: &str, first: usize, words: &[u32]) -> Result<(), String>;
    /// Let the fabric get on while the host waits: answer what it has asked
    /// for over a link, advance a Sim. Called between a host's polls.
    fn serve(&mut self) -> Result<(), String>;
}

fn unknown(region: &str, known: impl Iterator<Item = String>) -> String {
    format!("no region '{region}' — the build has [{}]", known.collect::<Vec<_>>().join(", "))
}

fn span(region: &str, records: usize, first: usize, count: usize) -> Result<(), String> {
    if first + count > records {
        return Err(format!("records {first}..{} of '{region}', and the build gave it {records}", first + count));
    }
    Ok(())
}

/// Regions in memory the fabric's masters and the host both reach: the
/// board's `u-dma-buf`, the Sim bridge's memory.
pub struct MemoryRegions<A> {
    arena: A,
    places: Vec<(String, Region)>,
    idle: Box<dyn FnMut() -> Result<(), String>>,
}

impl<A: Arena> MemoryRegions<A>
where
    A::Error: std::fmt::Debug,
{
    /// Every region the seam places by offset, in `arena`; `idle` is what
    /// serving means here — nothing on silicon, cycles in the Sim.
    pub fn from_seam(seam: &Seam, arena: A, idle: Box<dyn FnMut() -> Result<(), String>>) -> Result<Self, String> {
        let places = seam
            .names_ending("_REGION_OFFSET")
            .into_iter()
            .map(|name| {
                let need = |what: &str| seam.need(&format!("{name}_{what}")).map(|v| v as usize);
                let place = Region {
                    offset: need("REGION_OFFSET")?,
                    records: need("RECORDS")?,
                    record_bytes: need("RECORD_BYTES")?,
                    stride_bytes: need("STRIDE_BYTES")?,
                };
                Ok((name, place))
            })
            .collect::<Result<_, String>>()?;
        Ok(MemoryRegions { arena, places, idle })
    }

    fn place(&self, region: &str) -> Result<Region, String> {
        self.places
            .iter()
            .find(|(name, _)| key(name) == key(region))
            .map(|(_, place)| *place)
            .ok_or_else(|| unknown(region, self.places.iter().map(|(n, _)| n.to_lowercase())))
    }
}

impl<A: Arena> Regions for MemoryRegions<A>
where
    A::Error: std::fmt::Debug,
{
    fn records(&self, region: &str) -> Result<usize, String> {
        Ok(self.place(region)?.records)
    }

    // One copy for the whole run of records either way: a record at a time
    // is a sync and a syscall each on the board.
    fn read(&mut self, region: &str, first: usize, records: usize) -> Result<Vec<u32>, String> {
        let place = self.place(region)?;
        span(region, place.records, first, records)?;
        if records == 0 {
            return Ok(Vec::new());
        }
        let mut bytes = vec![0u8; (records - 1) * place.stride_bytes + place.record_bytes];
        self.arena.read(place.offset + first * place.stride_bytes, &mut bytes).map_err(|e| format!("read '{region}': {e:?}"))?;
        Ok(bytes
            .chunks(place.stride_bytes)
            .flat_map(|record| record[..place.record_bytes].chunks(4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])))
            .collect())
    }

    fn write(&mut self, region: &str, first: usize, words: &[u32]) -> Result<(), String> {
        let place = self.place(region)?;
        let record_words = place.record_bytes / 4;
        if words.len() % record_words != 0 {
            return Err(format!("{} words into '{region}', whose records are {record_words}", words.len()));
        }
        let records = words.len() / record_words;
        span(region, place.records, first, records)?;
        // The padding a carrier put between records is written as zeros, so
        // a stale buffer cannot look like data.
        let mut bytes = vec![0u8; records * place.stride_bytes];
        for (record, at) in words.chunks(record_words).zip(bytes.chunks_mut(place.stride_bytes)) {
            for (word, out) in record.iter().zip(at.chunks_mut(4)) {
                out.copy_from_slice(&word.to_le_bytes());
            }
        }
        self.arena.write(place.offset + first * place.stride_bytes, &bytes).map_err(|e| format!("write '{region}': {e:?}"))
    }

    fn serve(&mut self) -> Result<(), String> {
        (self.idle)()
    }
}

/// Regions the host keeps and answers for over the link: a write here is a
/// write to the host's own copy, and the fabric sees it when it next asks.
pub struct LinkedRegions {
    link: Rc<RefCell<SerialWindow>>,
    kept: LinkRegions,
    names: Vec<String>,
}

impl LinkedRegions {
    /// Every region the seam gives an id, kept here and served over `link`.
    pub fn from_seam(seam: &Seam, link: Rc<RefCell<SerialWindow>>) -> Result<Self, String> {
        let names = seam.names_ending("_REGION_ID");
        let kept = names
            .iter()
            .map(|name| {
                let need = |what: &str| seam.need(&format!("{name}_{what}"));
                let (records, record_bytes) = (need("RECORDS")? as usize, need("RECORD_BYTES")? as usize);
                Ok(LinkRegion { name: name.clone(), id: need("REGION_ID")? as u32, records, record_bytes, bytes: vec![0; records * record_bytes] })
            })
            .collect::<Result<_, String>>()?;
        Ok(LinkedRegions { link, kept: LinkRegions::new(kept), names })
    }

    fn name(&self, region: &str) -> Result<&str, String> {
        self.names
            .iter()
            .find(|name| key(name) == key(region))
            .map(String::as_str)
            .ok_or_else(|| unknown(region, self.names.iter().map(|n| n.to_lowercase())))
    }
}

impl Regions for LinkedRegions {
    fn records(&self, region: &str) -> Result<usize, String> {
        Ok(self.kept.region(self.name(region)?).expect("a named region is kept").records)
    }

    fn read(&mut self, region: &str, first: usize, records: usize) -> Result<Vec<u32>, String> {
        let kept = self.kept.region(self.name(region)?).expect("a named region is kept");
        span(region, kept.records, first, records)?;
        let bytes = &kept.bytes[first * kept.record_bytes..(first + records) * kept.record_bytes];
        Ok(bytes.chunks(4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect())
    }

    fn write(&mut self, region: &str, first: usize, words: &[u32]) -> Result<(), String> {
        let name = self.name(region)?.to_string();
        let kept = self.kept.region_mut(&name).expect("a named region is kept");
        if (words.len() * 4) % kept.record_bytes != 0 {
            return Err(format!("{} words into '{region}', whose records are {}", words.len(), kept.record_bytes / 4));
        }
        span(region, kept.records, first, words.len() * 4 / kept.record_bytes)?;
        let at = first * kept.record_bytes;
        for (word, out) in words.iter().zip(kept.bytes[at..].chunks_mut(4)) {
            out.copy_from_slice(&word.to_le_bytes());
        }
        Ok(())
    }

    fn serve(&mut self) -> Result<(), String> {
        self.kept.pump(&mut self.link.borrow_mut()).map(|_| ()).map_err(|e| format!("serving the regions over the link: {e:?}"))
    }
}

/// A build with no regions at all.
pub struct NoRegions;

impl Regions for NoRegions {
    fn records(&self, region: &str) -> Result<usize, String> {
        Err(unknown(region, std::iter::empty()))
    }

    fn read(&mut self, region: &str, _: usize, _: usize) -> Result<Vec<u32>, String> {
        Err(unknown(region, std::iter::empty()))
    }

    fn write(&mut self, region: &str, _: usize, _: &[u32]) -> Result<(), String> {
        Err(unknown(region, std::iter::empty()))
    }

    fn serve(&mut self) -> Result<(), String> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Bytes(Vec<u8>);

    impl Arena for Bytes {
        type Error = ();

        fn base(&self) -> u32 {
            0x1000
        }

        fn read(&mut self, offset: usize, out: &mut [u8]) -> Result<(), ()> {
            out.copy_from_slice(&self.0[offset..offset + out.len()]);
            Ok(())
        }

        fn write(&mut self, offset: usize, bytes: &[u8]) -> Result<(), ()> {
            self.0[offset..offset + bytes.len()].copy_from_slice(bytes);
            Ok(())
        }
    }

    /// A region whose carrier pads its records: eight bytes a record on a
    /// stride of sixteen, sixteen bytes into the arena.
    fn padded() -> MemoryRegions<Bytes> {
        let seam = Seam::from_pairs("test", "ITEMS_REGION_OFFSET=0x10 ITEMS_RECORDS=3 ITEMS_RECORD_BYTES=8 ITEMS_STRIDE_BYTES=16");
        MemoryRegions::from_seam(&seam, Bytes(vec![0xEE; 0x10 + 3 * 16]), Box::new(|| Ok(()))).unwrap()
    }

    #[test]
    fn records_go_in_and_come_back_without_the_padding() {
        let mut regions = padded();
        regions.write("items", 1, &[1, 2, 3, 4]).unwrap();
        assert_eq!(regions.read("items", 1, 2).unwrap(), vec![1, 2, 3, 4]);
        // The padding was zeroed, and the record before was not touched.
        assert_eq!(&regions.arena.0[0x10 + 16 + 8..0x10 + 32], &[0; 8]);
        assert_eq!(&regions.arena.0[0x10..0x10 + 8], &[0xEE; 8]);
    }

    #[test]
    fn a_run_past_the_region_and_a_name_it_lacks_are_refused() {
        let mut regions = padded();
        assert!(regions.write("items", 2, &[1, 2, 3, 4]).unwrap_err().contains("gave it 3"));
        assert!(regions.read("other", 0, 1).unwrap_err().contains("[items]"));
        assert!(regions.write("items", 0, &[1, 2, 3]).is_err());
    }
}
