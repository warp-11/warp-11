//! The `warp11-dma` kernel module's userspace half, ported from Kotlin's FFM
//! `Warp11Dma`: the bulk-transfer path between raw DDR the fabric owns and
//! CPU-cached memory. One blocking ioctl drives a ZynqMP PS GDMA memcpy from
//! a physical address into one of the module's cached kernel buffers (the
//! kernel does the buffer-side cache maintenance), and the subsequent CPU
//! reads run at cache speed — 12–16× over write-combine at 64 KB..1 MB, with
//! ~20 µs fixed cost per copy, so small status reads still belong on
//! AXI-Lite. Root-only (`/dev/warp11-dma` is 0600), like `/dev/mem`; callers
//! keep a slow-but-correct uncached fallback.

use crate::mmap::MmapWindow;
use std::fs::{File, OpenOptions};
use std::os::fd::AsRawFd;

#[repr(C)]
struct W11dmaCopy {
    phys: u64,
    buf: u32,
    offset: u32,
    len: u32,
    dir: u32,
}

#[repr(C)]
struct W11dmaInfo {
    nbufs: u32,
    buf_size: u32,
}

/// Linux generic `_IOC` encoding — mirrors `warp11-dma.h`'s `_IOW`/`_IOR`.
const fn ioc(dir: u32, ty: u8, nr: u8, size: usize) -> u64 {
    ((dir << 30) | ((size as u32) << 16) | ((ty as u32) << 8) | nr as u32) as u64
}

const IOC_COPY: u64 = ioc(1 /* _IOC_WRITE */, b'W', 1, core::mem::size_of::<W11dmaCopy>());
const IOC_INFO: u64 = ioc(2 /* _IOC_READ */, b'W', 2, core::mem::size_of::<W11dmaInfo>());
const DIR_PHYS_TO_BUF: u32 = 0;

extern "C" {
    fn ioctl(fd: i32, request: u64, arg: *mut core::ffi::c_void) -> i32;
}

#[derive(Debug)]
pub enum DmaError {
    Io(std::io::Error),
    Ioctl { request: u64, rc: i32 },
    Map,
}

pub struct Warp11Dma {
    file: File,
    pub nbufs: u32,
    pub buf_size: usize,
    buffer0: MmapWindow,
}

impl Warp11Dma {
    /// Open the device and map buffer 0 (a CACHED mapping — the whole point;
    /// no O_SYNC here). `Err` usually means the module isn't loaded or the
    /// caller isn't root.
    pub fn open() -> Result<Self, DmaError> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open("/dev/warp11-dma")
            .map_err(DmaError::Io)?;

        let mut info = W11dmaInfo {
            nbufs: 0,
            buf_size: 0,
        };
        let rc = unsafe {
            ioctl(
                file.as_raw_fd(),
                IOC_INFO,
                (&mut info as *mut W11dmaInfo).cast(),
            )
        };
        if rc != 0 {
            return Err(DmaError::Ioctl {
                request: IOC_INFO,
                rc,
            });
        }

        let buffer0 =
            MmapWindow::open(&file, 0, info.buf_size as usize).map_err(|_| DmaError::Map)?;
        Ok(Warp11Dma {
            file,
            nbufs: info.nbufs,
            buf_size: info.buf_size as usize,
            buffer0,
        })
    }

    fn dma_chunk(&mut self, phys: u64, len: usize) -> Result<(), DmaError> {
        let mut req = W11dmaCopy {
            phys,
            buf: 0,
            offset: 0,
            len: len as u32,
            dir: DIR_PHYS_TO_BUF,
        };
        let rc = unsafe {
            ioctl(
                self.file.as_raw_fd(),
                IOC_COPY,
                (&mut req as *mut W11dmaCopy).cast(),
            )
        };
        if rc != 0 {
            return Err(DmaError::Ioctl {
                request: IOC_COPY,
                rc,
            });
        }
        Ok(())
    }

    /// Read raw DDR at `phys` into a caller-owned buffer through GDMA + the
    /// cached copy buffer, chunked by the buffer size — the Kotlin device
    /// drivers' bulk-read loop. The caller reuses `out` across frames, so the
    /// page-fault cost of a fresh allocation is paid once, not per read.
    /// Returns (dma_ms, copy_ms) so the GDMA and the CPU-copy halves are
    /// separately visible — the module's own C benchmark reports the sum.
    pub fn read_phys_into(&mut self, phys: u64, out: &mut [u8]) -> Result<(f64, f64), DmaError> {
        let mut dma_ms = 0f64;
        let mut copy_ms = 0f64;
        let mut done = 0usize;
        while done < out.len() {
            let chunk = (out.len() - done).min(self.buf_size);
            let t = std::time::Instant::now();
            self.dma_chunk(phys + done as u64, chunk)?;
            dma_ms += t.elapsed().as_secs_f64() * 1e3;
            let t = std::time::Instant::now();
            out[done..done + chunk].copy_from_slice(&self.buffer0.bytes()[..chunk]);
            copy_ms += t.elapsed().as_secs_f64() * 1e3;
            done += chunk;
        }
        Ok((dma_ms, copy_ms))
    }

    /// Convenience form: allocate and read. Timing-honest callers use
    /// [`Self::read_phys_into`] with a warmed buffer.
    pub fn read_phys(&mut self, phys: u64, len: usize) -> Result<Vec<u8>, DmaError> {
        let mut out = vec![0u8; len];
        self.read_phys_into(phys, &mut out)?;
        Ok(out)
    }
}
