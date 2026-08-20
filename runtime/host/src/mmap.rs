//! A register window over `mmap`, which is the only way into physical registers
//! from Linux userspace: the MMU means a fixed base address is meaningless
//! there, so the address has to be acquired at runtime from a file descriptor.
//! That is the difference between this backend and a bare-metal one — the access
//! is identical, the *acquisition* is not.
//!
//! `mmap` is declared by hand rather than pulled from `libc`, so the workspace
//! has no dependencies.

use std::fs::File;
use std::os::fd::AsRawFd;
use warp11_runtime::RegisterWindow;

#[derive(Debug, PartialEq, Eq)]
pub enum MmapError {
    MapFailed,
    Unaligned(usize),
    OutOfRange { offset: usize, len: usize },
}

/// On Linux, opening the backing file `O_SYNC` is what makes the mapping
/// uncached device memory. Without it `/dev/mem` maps *cached*, and register
/// writes sit in a cache line instead of reaching the fabric — a bug warp11 has
/// already been bitten by on the udmabuf path.
pub const O_SYNC: i32 = 0x101000;

const PROT_READ: i32 = 1;
const PROT_WRITE: i32 = 2;
const MAP_SHARED: i32 = 1;

extern "C" {
    fn mmap(
        addr: *mut core::ffi::c_void,
        length: usize,
        prot: i32,
        flags: i32,
        fd: i32,
        offset: i64,
    ) -> *mut core::ffi::c_void;
    fn munmap(addr: *mut core::ffi::c_void, length: usize) -> i32;
}

pub struct MmapWindow {
    base: *mut u32,
    len: usize,
}

impl MmapWindow {
    /// Map `len` bytes of `file` starting at `offset`. On hardware `file` is
    /// `/dev/mem` opened `O_SYNC` and `offset` is the AXI base address; in a test
    /// it is an ordinary file, which exercises the same code path without a board.
    pub fn open(file: &File, offset: i64, len: usize) -> Result<Self, MmapError> {
        let mapped = unsafe {
            mmap(
                core::ptr::null_mut(),
                len,
                PROT_READ | PROT_WRITE,
                MAP_SHARED,
                file.as_raw_fd(),
                offset,
            )
        };
        if mapped as isize == -1 {
            return Err(MmapError::MapFailed);
        }
        Ok(MmapWindow {
            base: mapped.cast(),
            len,
        })
    }

    /// The whole mapping as a byte slice, for bulk copies (framebuffer
    /// readback). Cached-vs-uncached character comes from how the backing fd
    /// was opened, exactly as for the register accessors — O_SYNC gives the
    /// slow-but-always-correct device mapping, a plain open gives the cached
    /// one whose reader must own coherency (udmabuf's sync_for_cpu).
    pub fn bytes(&self) -> &[u8] {
        unsafe { core::slice::from_raw_parts(self.base.cast::<u8>(), self.len) }
    }

    /// The whole mapping as a mutable byte slice, for staging bulk input the
    /// fabric is about to read. The counterpart of [`bytes`], and the same
    /// coherency rule applies in the other direction: on a cached mapping the
    /// writer owns getting the bytes out of the CPU's cache before a
    /// non-snooping master goes looking for them (udmabuf's `sync_for_device`).
    pub fn bytes_mut(&mut self) -> &mut [u8] {
        unsafe { core::slice::from_raw_parts_mut(self.base.cast::<u8>(), self.len) }
    }

    fn word(&self, offset: usize) -> Result<*mut u32, MmapError> {
        if !offset.is_multiple_of(4) {
            return Err(MmapError::Unaligned(offset));
        }
        if offset + 4 > self.len {
            return Err(MmapError::OutOfRange {
                offset,
                len: self.len,
            });
        }
        Ok(unsafe { self.base.add(offset / 4) })
    }
}

impl RegisterWindow for MmapWindow {
    type Error = MmapError;

    /// Volatile so the compiler cannot cache, reorder, or elide the access. Note
    /// this constrains the *compiler* only — on a weakly ordered core like the
    /// KV260's A53s a correct backend also needs barriers, which uncached device
    /// mapping via `O_SYNC` is what makes unnecessary here.
    fn read32(&mut self, offset: usize) -> Result<u32, MmapError> {
        Ok(unsafe { self.word(offset)?.read_volatile() })
    }

    fn write32(&mut self, offset: usize, value: u32) -> Result<(), MmapError> {
        unsafe { self.word(offset)?.write_volatile(value) };
        Ok(())
    }
}

impl Drop for MmapWindow {
    fn drop(&mut self) {
        unsafe { munmap(self.base.cast(), self.len) };
    }
}
