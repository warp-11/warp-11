//! The Mandelbrot pod's driver, written once against [`RegisterWindow`]: the
//! same code runs against the F# Sim through the `FsSimWindow` bridge and
//! against `/dev/mem` on the KV260. This is the mixed design's runtime half —
//! the fabric side is elaborated by `hdl`, and the only thing the two
//! share is the generated [`crate::mandel_layout`].

use crate::mandel_layout as layout;
use crate::RegisterWindow;

/// What can go wrong talking to the pod, independent of the backend.
#[derive(Debug, PartialEq, Eq)]
pub enum MandelError<E> {
    /// The window worked but the ID register is not the pod's magic — a wrong
    /// base address fails here rather than by producing nonsense later.
    WrongId { found: u32 },
    /// `done` did not rise within the poll budget. Against the Sim bridge each
    /// read advances the fabric a couple of cycles, so the budget is also the
    /// run's cycle budget; on hardware it is just patience.
    NeverFinished,
    /// The window itself failed.
    Window(E),
}

impl<E> From<E> for MandelError<E> {
    fn from(inner: E) -> Self {
        MandelError::Window(inner)
    }
}

/// The run-once pod: after reset it renders one frame and freezes. A driver
/// binds, smokes the scratch path, waits for `done`, and reads the frame back.
pub struct MandelDevice<W> {
    window: W,
}

impl<W: RegisterWindow> MandelDevice<W> {
    pub fn open(mut window: W) -> Result<Self, MandelError<W::Error>> {
        let found = window.read32(layout::ID_OFFSET)?;
        if found != layout::ID_MAGIC {
            return Err(MandelError::WrongId { found });
        }
        Ok(MandelDevice { window })
    }

    /// Write-then-read through the scratch register — the smallest full round
    /// trip through whatever backend is underneath.
    pub fn scratch_round_trip(&mut self, value: u32) -> Result<u32, W::Error> {
        self.window.write32(layout::SCRATCH_OFFSET, value)?;
        self.window.read32(layout::SCRATCH_OFFSET)
    }

    pub fn wait_done(&mut self, poll_budget: usize) -> Result<(), MandelError<W::Error>> {
        for _ in 0..poll_budget {
            if self.window.read32(layout::DONE_OFFSET)? == 1 {
                return Ok(());
            }
        }
        Err(MandelError::NeverFinished)
    }

    pub fn result_count(&mut self) -> Result<u32, W::Error> {
        self.window.read32(layout::RESULT_COUNT_OFFSET)
    }

    /// Fabric cycles from reset to `done` — frozen once the frame is finished,
    /// so this is the measured render time.
    pub fn frame_cycles(&mut self) -> Result<u32, W::Error> {
        self.window.read32(layout::FRAME_CYCLES_OFFSET)
    }

    /// Read the frame: one iteration count per pixel, row-major. One register
    /// read per pixel — the run-once scope's egress; bulk paths are a later
    /// problem, on purpose.
    pub fn read_frame(&mut self, out: &mut [u8; layout::FB_PIXELS]) -> Result<(), W::Error> {
        for (i, pixel) in out.iter_mut().enumerate() {
            *pixel = self.window.read32(layout::FB_OFFSET + 4 * i)? as u8;
        }
        Ok(())
    }
}
