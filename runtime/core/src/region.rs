//! The region contract: a top whose design keeps records in the host's
//! memory speaks these registers ahead of its own, so one driver opens any
//! of them (`notes/DEVICES.md` §13). The fabric is told where the arena
//! starts once — armed while that is non-zero — and every region sits at an
//! offset the build chose and the seam prints. No register names a region's
//! place, so a host cannot point the fabric somewhere wrong.
//!
//! A record is reached by name and index through [`Region`], over an
//! [`Arena`] — the board's `u-dma-buf`, or the Sim bridge's memory — so the
//! same driver runs against both.

use crate::RegisterWindow;

/// The identity every region top answers with; `hdl/Warp11/BoardTop.fs`
/// states the same number as `regionId`. Not the batch top's, so neither
/// driver opens the other's top.
pub const ID_VALUE: u32 = 0x7A11_5E60;

/// The contract's offsets: laid out first, in this order, by the F# side.
pub mod regmap {
    pub const ID: usize = 0x00;
    /// Which revision of the whole map the fabric was built from.
    pub const LAYOUT_HASH: usize = 0x04;
    /// Where the arena starts, as the fabric addresses it. Zero is disarmed:
    /// no region master moves.
    pub const REGIONS_BASE: usize = 0x08;
    /// Where the design's own words begin.
    pub const FIRST_CONTROL: usize = 0x0C;
}

#[derive(Debug, PartialEq, Eq)]
pub enum RegionError<E> {
    /// Not a region top at this base.
    WrongId { found: u32 },
    /// A region top built from a different revision of the map.
    WrongLayout { found: u32, expected: u32 },
    /// An index past the region's records.
    OutOfRange { index: usize, records: usize },
    /// A record handed in or asked for at the wrong size.
    WrongSize { bytes: usize, record_bytes: usize },
    Window(E),
}

impl<E> From<E> for RegionError<E> {
    fn from(inner: E) -> Self {
        RegionError::Window(inner)
    }
}

/// Memory the fabric's region masters and the host both reach.
pub trait Arena {
    type Error;

    /// The address the fabric uses for the arena's first byte — what the
    /// driver arms `REGIONS_BASE` with.
    fn base(&self) -> u32;

    /// Bytes from the arena, `offset` from its start. An implementation over
    /// cached memory makes them current first.
    fn read(&mut self, offset: usize, out: &mut [u8]) -> Result<(), Self::Error>;

    /// Bytes into the arena, made visible to the fabric before returning.
    fn write(&mut self, offset: usize, bytes: &[u8]) -> Result<(), Self::Error>;
}

/// A region as the seam places it: `{NAME}_REGION_OFFSET`, `_RECORDS`,
/// `_RECORD_BYTES`, `_STRIDE_BYTES`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Region {
    pub offset: usize,
    pub records: usize,
    pub record_bytes: usize,
    pub stride_bytes: usize,
}

impl Region {
    /// Where record `index` starts in the arena.
    pub fn record_offset<E>(&self, index: usize) -> Result<usize, RegionError<E>> {
        if index >= self.records {
            return Err(RegionError::OutOfRange { index, records: self.records });
        }
        Ok(self.offset + index * self.stride_bytes)
    }

    /// Record `index`'s bytes into `out`, which is exactly a record long.
    pub fn read<A: Arena>(&self, arena: &mut A, index: usize, out: &mut [u8]) -> Result<(), RegionError<A::Error>> {
        if out.len() != self.record_bytes {
            return Err(RegionError::WrongSize { bytes: out.len(), record_bytes: self.record_bytes });
        }
        let at = self.record_offset(index)?;
        arena.read(at, out)?;
        Ok(())
    }

    /// `record` — exactly a record's bytes — into record `index`.
    pub fn write<A: Arena>(&self, arena: &mut A, index: usize, record: &[u8]) -> Result<(), RegionError<A::Error>> {
        if record.len() != self.record_bytes {
            return Err(RegionError::WrongSize { bytes: record.len(), record_bytes: self.record_bytes });
        }
        let at = self.record_offset(index)?;
        arena.write(at, record)?;
        Ok(())
    }
}

/// A record field as the seam places it — a register map's constants under
/// the region's name: `_OFFSET` and then `_SHIFT`/`_MASK`, or `_WORDS` for
/// a field wider than a word.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Field {
    /// Within one word.
    Bits { offset: usize, shift: u32, mask: u32 },
    /// `words` words from `offset`, low word first.
    Words { offset: usize, words: usize },
}

impl Field {
    /// The field's value from a record's bytes, low word first.
    pub fn get(&self, record: &[u8]) -> u64 {
        let word = |at: usize| u32::from_le_bytes([record[at], record[at + 1], record[at + 2], record[at + 3]]);
        match *self {
            Field::Bits { offset, shift, mask } => ((word(offset) & mask) >> shift) as u64,
            Field::Words { offset, words } => {
                let mut value = 0u64;
                for k in (0..words.min(2)).rev() {
                    value = (value << 32) | word(offset + 4 * k) as u64;
                }
                value
            }
        }
    }

    /// Set the field in a record's bytes, masked into its word — the fabric
    /// reads only the field's bits. (Going the other way, a signed field the
    /// fabric wrote alone in its word arrives sign-extended there; [`get`]
    /// returns the field's own bits, which the caller reads at its width.)
    ///
    /// [`get`]: Field::get
    pub fn set(&self, record: &mut [u8], value: u64) {
        fn put(record: &mut [u8], at: usize, v: u32) {
            record[at..at + 4].copy_from_slice(&v.to_le_bytes());
        }
        match *self {
            Field::Bits { offset, shift, mask } => {
                let old = u32::from_le_bytes([record[offset], record[offset + 1], record[offset + 2], record[offset + 3]]);
                put(record, offset, (old & !mask) | (((value as u32) << shift) & mask));
            }
            Field::Words { offset, words } => {
                for k in 0..words {
                    let v = if k < 2 { (value >> (32 * k)) as u32 } else { 0 };
                    put(record, offset + 4 * k, v);
                }
            }
        }
    }
}

pub struct RegionDevice<W> {
    window: W,
}

impl<W: RegisterWindow> RegionDevice<W> {
    /// Open the top behind a window, refusing anything that does not answer
    /// with the region identity and — when the caller knows its revision — a
    /// stale layout. The arena is left disarmed until [`Self::arm`].
    pub fn open(mut window: W, expected_layout: Option<u32>) -> Result<Self, RegionError<W::Error>> {
        let found = window.read32(regmap::ID)?;
        if found != ID_VALUE {
            return Err(RegionError::WrongId { found });
        }
        if let Some(expected) = expected_layout {
            let found = window.read32(regmap::LAYOUT_HASH)?;
            if found != expected {
                return Err(RegionError::WrongLayout { found, expected });
            }
        }
        Ok(RegionDevice { window })
    }

    /// Tell the fabric where the arena is — an [`Arena::base`] — and its
    /// masters may move from here. A number rather than the arena, because
    /// in the Sim the arena is this device's own window.
    pub fn arm(&mut self, base: u32) -> Result<(), W::Error> {
        self.window.write32(regmap::REGIONS_BASE, base)
    }

    /// Stop the masters taking new work — before the bitstream is unloaded,
    /// so nothing is in flight when it goes.
    pub fn disarm(&mut self) -> Result<(), W::Error> {
        self.window.write32(regmap::REGIONS_BASE, 0)
    }

    /// One of the design's own words, by its offset.
    pub fn set(&mut self, offset: usize, value: u32) -> Result<(), W::Error> {
        self.window.write32(offset, value)
    }

    pub fn get(&mut self, offset: usize) -> Result<u32, W::Error> {
        self.window.read32(offset)
    }

    /// The backend underneath — the bridge's memory is reached through it in
    /// the Sim.
    pub fn window_mut(&mut self) -> &mut W {
        &mut self.window
    }
}
