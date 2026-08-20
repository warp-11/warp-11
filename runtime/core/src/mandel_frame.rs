//! The full-scale frame accelerator's driver, written once against
//! [`RegisterWindow`]: the same code runs against the F# Sim through the
//! `frameserve` bridge and against `/dev/mem` on the KV260. Unlike the mini
//! pod, the framebuffer is not in the register aperture — the fabric writes
//! it to PS DDR at the base this driver programs, and readback is the
//! backend's business (a DDR dump line on the bridge, an mmap on the board).

use crate::mandel_frame_layout as layout;
use crate::RegisterWindow;

/// What can go wrong talking to the accelerator, independent of the backend.
#[derive(Debug, PartialEq, Eq)]
pub enum MandelFrameError<E> {
    /// The window worked but the ID register is not the accelerator's magic —
    /// a wrong base address fails here rather than by producing nonsense.
    WrongId { found: u32 },
    /// `frameDone` did not rise within the poll budget. Against the Sim
    /// bridge each read advances the fabric a couple of cycles, so the budget
    /// is also a cycle budget; on hardware it is just patience.
    NeverFinished,
    /// The window itself failed.
    Window(E),
}

impl<E> From<E> for MandelFrameError<E> {
    fn from(inner: E) -> Self {
        MandelFrameError::Window(inner)
    }
}

/// A view in Q4.28 bit patterns: origin and per-pixel step, exactly the four
/// registers the fabric latches on start.
#[derive(Clone, Copy, Debug)]
pub struct View {
    pub cx_origin: u32,
    pub cy_origin: u32,
    pub dx: u32,
    pub dy: u32,
}

/// The re-renderable accelerator: program a view and a framebuffer base,
/// pulse start, poll the sticky `frameDone`, read the measured cycles —
/// then again, for the next frame.
pub struct MandelFrameDevice<W> {
    window: W,
}

impl<W: RegisterWindow> MandelFrameDevice<W> {
    pub fn open(mut window: W) -> Result<Self, MandelFrameError<W::Error>> {
        let found = window.read32(layout::ID_OFFSET)?;
        if found != layout::ID_MAGIC {
            return Err(MandelFrameError::WrongId { found });
        }
        Ok(MandelFrameDevice { window })
    }

    /// Program the view and the DDR framebuffer base, then pulse start —
    /// which also clears the sticky `frameDone` and the cycle counter.
    pub fn start_render(&mut self, view: View, fb_base: u32) -> Result<(), W::Error> {
        self.window.write32(layout::CX_ORIGIN_OFFSET, view.cx_origin)?;
        self.window.write32(layout::CY_ORIGIN_OFFSET, view.cy_origin)?;
        self.window.write32(layout::DX_OFFSET, view.dx)?;
        self.window.write32(layout::DY_OFFSET, view.dy)?;
        self.window.write32(layout::FB_BASE_ADDR_OFFSET, fb_base)?;
        self.window.write32(layout::START_OFFSET, 1)
    }

    pub fn busy(&mut self) -> Result<bool, W::Error> {
        Ok(self.window.read32(layout::BUSY_OFFSET)? == 1)
    }

    /// Poll the sticky done flag.
    pub fn wait_done(&mut self, poll_budget: usize) -> Result<(), MandelFrameError<W::Error>> {
        for _ in 0..poll_budget {
            if self.window.read32(layout::DONE_OFFSET)? == 1 {
                return Ok(());
            }
        }
        Err(MandelFrameError::NeverFinished)
    }

    /// Fabric cycles from start to `frameDone` — frozen until the next start,
    /// so this is the measured render time.
    pub fn last_frame_cycles(&mut self) -> Result<u32, W::Error> {
        self.window.read32(layout::LAST_FRAME_CYCLES_OFFSET)
    }

    /// The backend underneath, for what the register aperture cannot carry —
    /// the bridge's DDR dump, the board's mmap.
    pub fn window_mut(&mut self) -> &mut W {
        &mut self.window
    }
}
