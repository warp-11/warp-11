//! The batch contract: any design the build generator puts on a board's
//! host memory speaks these registers ahead of its own controls, so one
//! driver runs every such design. Written once against [`RegisterWindow`],
//! it runs against the F# Sim through the `batchserve` bridge and against a
//! uio mapping on the board.
//!
//! The rows themselves are not the driver's business — they sit in a buffer
//! the backend owns (the bridge's fake DDR, the board's `u-dma-buf`), and the
//! driver only tells the fabric where they are.

use crate::RegisterWindow;

/// The identity every batch top answers with; `hdl/Warp11/BoardTop.fs`
/// states the same number as `batchId`.
pub const ID_VALUE: u32 = 0x7A11_BA7C;

/// The contract's offsets. Fixed by construction on the F# side — the batch
/// registers are laid out first, in this order — and pinned there by a check,
/// which is what lets a driver with no seam file for the design find them.
pub mod regmap {
    /// Reads the identity; a write of bit 0 starts a batch.
    pub const ID: usize = 0x00;
    pub const START: usize = 0x00;
    /// Which revision of the whole map the fabric was built from.
    pub const LAYOUT_HASH: usize = 0x04;
    /// Bit 0: a batch is running.
    pub const BUSY: usize = 0x08;
    /// Bit 0: a batch finished; write 1 to clear.
    pub const DONE: usize = 0x0C;
    /// Byte address of the first input row; 256-byte aligned.
    pub const SRC_ADDR: usize = 0x10;
    /// Byte address of the first output row; 256-byte aligned.
    pub const DST_ADDR: usize = 0x14;
    /// Rows to process; a multiple of the rows a burst holds.
    pub const FRAME_COUNT: usize = 0x18;
    /// Cycles the last batch took: cleared at start, counting while busy,
    /// frozen at done. The fabric's own measure of the work, as against
    /// timing a poll loop over a bus.
    pub const CYCLES: usize = 0x1C;
    /// Where the design's own controls begin.
    pub const FIRST_CONTROL: usize = 0x20;
}

#[derive(Debug, PartialEq, Eq)]
pub enum BatchError<E> {
    /// Not a batch top at this base.
    WrongId { found: u32 },
    /// A batch top, built from a different revision of the map than the
    /// caller was told to expect.
    WrongLayout { found: u32, expected: u32 },
    /// `busy` did not clear within the poll budget.
    NeverFinished,
    Window(E),
}

impl<E> From<E> for BatchError<E> {
    fn from(inner: E) -> Self {
        BatchError::Window(inner)
    }
}

pub struct BatchDevice<W> {
    window: W,
}

impl<W: RegisterWindow> BatchDevice<W> {
    /// Open the device behind a window, refusing anything that does not
    /// answer with the batch identity, and — when the caller knows which
    /// revision it was built for — a stale layout.
    pub fn open(mut window: W, expected_layout: Option<u32>) -> Result<Self, BatchError<W::Error>> {
        let found = window.read32(regmap::ID)?;
        if found != ID_VALUE {
            return Err(BatchError::WrongId { found });
        }
        if let Some(expected) = expected_layout {
            let found = window.read32(regmap::LAYOUT_HASH)?;
            if found != expected {
                return Err(BatchError::WrongLayout { found, expected });
            }
        }
        Ok(BatchDevice { window })
    }

    /// The map revision the fabric reports.
    pub fn layout_hash(&mut self) -> Result<u32, W::Error> {
        self.window.read32(regmap::LAYOUT_HASH)
    }

    /// Set one of the design's own controls by its offset.
    pub fn set(&mut self, offset: usize, value: u32) -> Result<(), W::Error> {
        self.window.write32(offset, value)
    }

    /// Point the fabric at the rows and start the batch.
    pub fn start(&mut self, src_addr: u32, dst_addr: u32, frames: u32) -> Result<(), W::Error> {
        self.window.write32(regmap::SRC_ADDR, src_addr)?;
        self.window.write32(regmap::DST_ADDR, dst_addr)?;
        self.window.write32(regmap::FRAME_COUNT, frames)?;
        self.window.write32(regmap::START, 1)
    }

    pub fn busy(&mut self) -> Result<bool, W::Error> {
        Ok(self.window.read32(regmap::BUSY)? & 1 != 0)
    }

    /// Cycles the last batch took, as the fabric counted them. Meaningful
    /// once it is done; while it runs, it is how far it has got.
    pub fn cycles(&mut self) -> Result<u32, W::Error> {
        self.window.read32(regmap::CYCLES)
    }

    /// Poll `busy` until it clears, with `between` called between polls — a
    /// no-op on the board, free cycles against the bridge, where every read
    /// is a transaction and the fabric would otherwise crawl at the pace of
    /// the pipe.
    pub fn wait_done(
        &mut self,
        poll_budget: usize,
        mut between: impl FnMut(&mut W) -> Result<(), W::Error>,
    ) -> Result<(), BatchError<W::Error>> {
        for _ in 0..poll_budget {
            if !self.busy()? {
                return Ok(());
            }
            between(&mut self.window)?;
        }
        Err(BatchError::NeverFinished)
    }

    /// Clear the done flag.
    pub fn acknowledge(&mut self) -> Result<(), W::Error> {
        self.window.write32(regmap::DONE, 1)
    }

    /// The backend underneath, for the rows the aperture cannot carry.
    pub fn window_mut(&mut self) -> &mut W {
        &mut self.window
    }
}
