//! A build, reached: its registers and its regions, however the board it
//! was built for carries them. The seam says which — a baud for a map
//! spoken over a serial line, an app for one mapped through a uio node,
//! region ids for regions kept over the link, region offsets for regions in
//! memory both sides reach — so a host names a build directory and is
//! handed a [`Window`] and a [`Regions`], and what is underneath them is not
//! its business. The Sim's bridge is a third way to the same two things.

use std::cell::RefCell;
use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::rc::Rc;

use warp11_runtime::region::Arena;
use warp11_runtime::RegisterWindow;

use crate::arena::{BoardArena, SIM_ARENA_BASE};
use crate::fs_sim_window::{FsSimError, FsSimWindow};
use crate::mmap::{MmapWindow, O_SYNC};
use crate::regions::{LinkedRegions, MemoryRegions, NoRegions, Regions};
use crate::seam::Seam;
use crate::serial_window::SerialWindow;
use crate::udmabuf::{find_uio, Udmabuf};

/// A build's registers, whichever way they are reached. A handle: clones
/// are the same window.
#[derive(Clone)]
pub struct Window(Rc<RefCell<dyn RegisterWindow<Error = String>>>);

impl RegisterWindow for Window {
    type Error = String;

    fn read32(&mut self, offset: usize) -> Result<u32, String> {
        self.0.borrow_mut().read32(offset)
    }

    fn write32(&mut self, offset: usize, value: u32) -> Result<(), String> {
        self.0.borrow_mut().write32(offset, value)
    }
}

/// One window shared by the registers and whatever else rides the same
/// transport — the link's regions, the bridge's memory.
struct Shared<W>(Rc<RefCell<W>>);

impl<W: RegisterWindow> RegisterWindow for Shared<W>
where
    W::Error: std::fmt::Debug,
{
    type Error = String;

    fn read32(&mut self, offset: usize) -> Result<u32, String> {
        self.0.borrow_mut().read32(offset).map_err(|e| format!("reading 0x{offset:03x}: {e:?}"))
    }

    fn write32(&mut self, offset: usize, value: u32) -> Result<(), String> {
        self.0.borrow_mut().write32(offset, value).map_err(|e| format!("writing 0x{offset:03x}: {e:?}"))
    }
}

impl Arena for Shared<FsSimWindow> {
    type Error = FsSimError;

    fn base(&self) -> u32 {
        self.0.borrow().base()
    }

    fn read(&mut self, offset: usize, out: &mut [u8]) -> Result<(), FsSimError> {
        self.0.borrow_mut().read(offset, out)
    }

    fn write(&mut self, offset: usize, bytes: &[u8]) -> Result<(), FsSimError> {
        self.0.borrow_mut().write(offset, bytes)
    }
}

pub struct Connection {
    /// Everything the build says about itself.
    pub seam: Seam,
    pub window: Window,
    pub regions: Box<dyn Regions>,
    /// Where to write zero before the bitstream may be unloaded: the
    /// register that arms regions in memory, where there is one.
    armed: Option<usize>,
}

impl Connection {
    /// The build loaded on a board, by its build directory or its seam
    /// file. `port` is the serial line, for a build reached over one.
    pub fn board(build: &Path, port: Option<&Path>) -> Result<Connection, String> {
        let seam = Seam::load(build)?;
        let aperture = seam.need("APERTURE_BYTES")? as usize;

        if let Some(baud) = seam.number("HOST_BAUD") {
            let port = port.ok_or_else(|| format!("{}: this build is reached over a serial line — say which with --port", seam.source))?;
            let link = SerialWindow::open_words(port, baud as u32, 1, aperture / 4).map_err(|e| format!("{}: {e}", port.display()))?;
            let link = Rc::new(RefCell::new(link));
            let regions: Box<dyn Regions> = if seam.names_ending("_REGION_ID").is_empty() {
                Box::new(NoRegions)
            } else {
                Box::new(LinkedRegions::from_seam(&seam, link.clone())?)
            };
            return Connection { seam, window: Window(Rc::new(RefCell::new(Shared(link)))), regions, armed: None }.identified();
        }

        let app = seam
            .text("HOST_APP")
            .ok_or_else(|| format!("{}: no HOST_BAUD and no HOST_APP — write the build directory again; this seam is from before it said how it is reached", seam.source))?
            .to_string();
        let uio = app.replace('-', "_");
        let node = find_uio(&uio).ok().flatten().ok_or_else(|| format!("no uio node named '{uio}' — is the app '{app}' loaded?"))?;
        let file = OpenOptions::new().read(true).write(true).custom_flags(O_SYNC).open(&node).map_err(|e| format!("{}: {e}", node.display()))?;
        let registers = MmapWindow::open(&file, 0, aperture).map_err(|e| format!("mapping {}: {e:?}", node.display()))?;
        let window = Window(Rc::new(RefCell::new(Shared(Rc::new(RefCell::new(registers))))));

        match seam.number("REGIONS_BASE_OFFSET") {
            None => Connection { seam, window, regions: Box::new(NoRegions), armed: None }.identified(),
            Some(base_offset) => {
                let buffer = format!("udmabuf-{app}");
                let buffer = Udmabuf::find(&buffer).ok().flatten().ok_or_else(|| format!("no u-dma-buf '{buffer}' — is the app '{app}' loaded?"))?;
                let arena = BoardArena::open(buffer).map_err(|e| format!("mapping the arena: {e:?}"))?;
                let base = arena.base();
                let regions = Box::new(MemoryRegions::from_seam(&seam, arena, Box::new(|| Ok(())))?);
                Connection { seam, window, regions, armed: Some(base_offset as usize) }.identified()?.arm(base)
            }
        }
    }

    /// A design in the Sim, behind the bridge `dotnet run --project fsproj
    /// -- args` serves; `marker` is the line it prints when it is up.
    pub fn sim(fsproj: &Path, args: &[&str], marker: &str) -> Result<Connection, String> {
        let mut bridge = FsSimWindow::spawn_with(fsproj, args, marker).map_err(|e| format!("the bridge did not come up: {e:?}"))?;
        let pairs = bridge.seam_constants().map_err(|e| format!("the bridge did not state its seam: {e:?}"))?;
        let seam = Seam::from_pairs(&format!("{} in the Sim", fsproj.display()), &pairs);
        let bridge = Rc::new(RefCell::new(bridge));
        let window = Window(Rc::new(RefCell::new(Shared(bridge.clone()))));

        match seam.number("REGIONS_BASE_OFFSET") {
            None => Connection { seam, window, regions: Box::new(NoRegions), armed: None }.identified(),
            Some(base_offset) => {
                let cycles = bridge.clone();
                let idle = Box::new(move || cycles.borrow_mut().free_cycles(2000).map_err(|e| format!("advancing the Sim: {e:?}")));
                let regions = Box::new(MemoryRegions::from_seam(&seam, Shared(bridge), idle)?);
                Connection { seam, window, regions, armed: Some(base_offset as usize) }.identified()?.arm(SIM_ARENA_BASE as u32)
            }
        }
    }

    /// Refuse a fabric that is not this build, where the seam states an
    /// identity to tell by. Offsets in a map are allocated, so every read
    /// of the wrong build would succeed; this is the one that says so.
    fn identified(mut self) -> Result<Connection, String> {
        for (what, hint) in [("ID", "a different design is loaded, or none"), ("LAYOUT_HASH", "the same design, built from a different revision of its map")] {
            let (Some(offset), Some(expected)) = (self.seam.number(&format!("{what}_OFFSET")), self.seam.number(&format!("{what}_VALUE"))) else {
                continue;
            };
            let found = self.window.read32(offset as usize)?;
            if found as u64 != expected {
                return Err(format!("{}: {what} reads 0x{found:08x} and the seam says 0x{expected:08x} — {hint}", self.seam.source));
            }
        }
        Ok(self)
    }

    fn arm(mut self, base: u32) -> Result<Connection, String> {
        if let Some(offset) = self.armed {
            self.window.write32(offset, base)?;
        }
        Ok(self)
    }

    /// Stop the fabric's memory masters taking new work — before the
    /// bitstream is unloaded, so nothing is in flight when it goes. Also
    /// done, unreported, when the connection is dropped.
    pub fn close(mut self) -> Result<(), String> {
        match self.armed.take() {
            Some(offset) => self.window.write32(offset, 0),
            None => Ok(()),
        }
    }
}

impl Drop for Connection {
    fn drop(&mut self) {
        if let Some(offset) = self.armed.take() {
            let _ = self.window.write32(offset, 0);
        }
    }
}
