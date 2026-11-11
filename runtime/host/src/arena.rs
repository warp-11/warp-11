//! The two arenas a region driver runs over: the Sim bridge's memory, and the
//! board's `u-dma-buf`. Both put the regions where the seam says and tell the
//! fabric where they start; a driver written against
//! [`warp11_runtime::region::Arena`] cannot tell which it has.

use std::fs::OpenOptions;

use warp11_runtime::region::Arena;

use crate::fs_sim_window::{FsSimError, FsSimWindow};
use crate::mmap::MmapWindow;
use crate::udmabuf::Udmabuf;

/// Where the bridge's arena starts: a page in, because a zero base is a
/// disarmed one. The F# bridge allocates the page beside the arena.
pub const SIM_ARENA_BASE: usize = 0x1000;

impl Arena for FsSimWindow {
    type Error = FsSimError;

    fn base(&self) -> u32 {
        SIM_ARENA_BASE as u32
    }

    fn read(&mut self, offset: usize, out: &mut [u8]) -> Result<(), FsSimError> {
        let bytes = self.read_ddr(SIM_ARENA_BASE + offset, out.len())?;
        out.copy_from_slice(&bytes);
        Ok(())
    }

    fn write(&mut self, offset: usize, bytes: &[u8]) -> Result<(), FsSimError> {
        self.write_ddr(SIM_ARENA_BASE + offset, bytes)
    }
}

#[derive(Debug)]
pub enum BoardArenaError {
    Io(std::io::Error),
    Map(String),
    /// The fabric's masters address 32 bits.
    AboveFourGigabytes(u64),
    OutOfRange { offset: usize, len: usize, size: usize },
}

/// A `u-dma-buf` mapped cached, kept coherent with the non-snooping masters
/// by syncing each range as it is read or written.
pub struct BoardArena {
    buffer: Udmabuf,
    map: MmapWindow,
}

impl BoardArena {
    /// The app's arena by the name its overlay gave it.
    pub fn open(buffer: Udmabuf) -> Result<Self, BoardArenaError> {
        if buffer.phys_addr + buffer.size as u64 > u32::MAX as u64 {
            return Err(BoardArenaError::AboveFourGigabytes(buffer.phys_addr));
        }
        let file = OpenOptions::new().read(true).write(true).open(&buffer.dev_path).map_err(BoardArenaError::Io)?;
        let map = MmapWindow::open(&file, 0, buffer.size).map_err(|e| BoardArenaError::Map(format!("{e:?}")))?;
        Ok(BoardArena { buffer, map })
    }

    fn check(&self, offset: usize, len: usize) -> Result<(), BoardArenaError> {
        if offset + len > self.buffer.size {
            return Err(BoardArenaError::OutOfRange { offset, len, size: self.buffer.size });
        }
        Ok(())
    }
}

impl Arena for BoardArena {
    type Error = BoardArenaError;

    fn base(&self) -> u32 {
        self.buffer.phys_addr as u32
    }

    fn read(&mut self, offset: usize, out: &mut [u8]) -> Result<(), BoardArenaError> {
        self.check(offset, out.len())?;
        self.buffer.sync_for_cpu(offset, out.len()).map_err(BoardArenaError::Io)?;
        out.copy_from_slice(&self.map.bytes()[offset..offset + out.len()]);
        Ok(())
    }

    fn write(&mut self, offset: usize, bytes: &[u8]) -> Result<(), BoardArenaError> {
        self.check(offset, bytes.len())?;
        self.map.bytes_mut()[offset..offset + bytes.len()].copy_from_slice(bytes);
        self.buffer.sync_for_device(offset, bytes.len()).map_err(BoardArenaError::Io)
    }
}
