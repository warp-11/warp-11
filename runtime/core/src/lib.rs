//! Host-side runtime: the register-window abstraction and the drivers written
//! against it.
//!
//! `no_std` and dependency-free on purpose. This is the crate that has to
//! compile for a target with no operating system, so it may not reach for
//! `String`, a heap, or a syscall. The backends that do those things live
//! outside it — see `warp11-host`.
//!
//! The register map lives here rather than beside the HDL, and `warp11-hdl`
//! depends on this crate to get it. That is the one declaration both sides read,
//! so a design and its driver cannot disagree about an offset. warp11 does this
//! with codegen today (`HARDWARE_SLAVES` writes `GoLSlaveLayout.kt`, committed
//! because the build is circular); when the DSL and the driver are the same
//! language it is just a module.

#![no_std]

pub mod gep;
pub mod mandel;
pub mod mandel_frame;
/// The generated register maps — emitted by hdl's `dotnet run --
/// hardware`, committed here so the driver's diff shows layout changes. The
/// one place the two languages agree on offsets.
pub mod audio_batch_layout;
pub mod audio_effects_layout;
pub mod audio_gain_layout;
pub mod audio_passthru_layout;
pub mod audio_tone_layout;
pub mod gep_layout;
pub mod gol_layout;
pub mod mandel_frame_layout;
pub mod mandel_layout;

/// Byte offsets of the AXI-Lite register file. Both the elaborated slave and the
/// driver below index by these.
pub mod regmap {
    /// Read-only identifier, so a driver can tell it is talking to the right thing.
    pub const ID: usize = 0x0;
    /// Read/write scratch.
    pub const SCRATCH0: usize = 0x4;
    /// Read/write scratch.
    pub const SCRATCH1: usize = 0x8;
    /// Read-only cycle counter — evidence the fabric is running, and the one
    /// register whose value legitimately differs between a simulator and memory.
    pub const CYCLES: usize = 0xC;

    /// What [`ID`] reads back on a working device.
    pub const ID_MAGIC: u32 = 0x7A_11_00_01;

    /// Every offset, for a backend that needs to size a window.
    pub const ALL: [usize; 4] = [ID, SCRATCH0, SCRATCH1, CYCLES];
}

/// A 32-bit register aperture. The whole hardware-vs-simulator distinction lives
/// behind this trait; a driver written against it does not know which it has.
///
/// `Error` is an associated type rather than a boxed error so an implementation
/// can use a plain enum and stay allocation-free.
pub trait RegisterWindow {
    type Error;

    fn read32(&mut self, offset: usize) -> Result<u32, Self::Error>;
    fn write32(&mut self, offset: usize, value: u32) -> Result<(), Self::Error>;
}

/// What can go wrong in a driver, independent of the backend under it.
#[derive(Debug, PartialEq, Eq)]
pub enum DeviceError<E> {
    /// The window worked but the device is not the one expected.
    WrongId { found: u32 },
    /// The window itself failed.
    Window(E),
}

impl<E> From<E> for DeviceError<E> {
    fn from(inner: E) -> Self {
        DeviceError::Window(inner)
    }
}

/// A driver. Written once, against the trait — this is the code the probe exists
/// to compile twice and run against two different backends.
pub struct ScratchDevice<W> {
    window: W,
}

impl<W: RegisterWindow> ScratchDevice<W> {
    /// Bind to a window, checking the identifier first so a wrong base address
    /// fails here rather than by producing nonsense later.
    pub fn open(mut window: W) -> Result<Self, DeviceError<W::Error>> {
        let found = window.read32(regmap::ID)?;
        if found != regmap::ID_MAGIC {
            return Err(DeviceError::WrongId { found });
        }
        Ok(ScratchDevice { window })
    }

    pub fn scratch(&mut self, index: usize) -> Result<u32, W::Error> {
        self.window.read32(Self::scratch_offset(index))
    }

    pub fn set_scratch(&mut self, index: usize, value: u32) -> Result<(), W::Error> {
        self.window.write32(Self::scratch_offset(index), value)
    }

    pub fn cycles(&mut self) -> Result<u32, W::Error> {
        self.window.read32(regmap::CYCLES)
    }

    /// Write both scratch registers and read them back, which is the smallest
    /// thing that exercises a full write-then-read round trip through whatever
    /// backend is underneath.
    pub fn round_trip(&mut self, first: u32, second: u32) -> Result<(u32, u32), W::Error> {
        self.set_scratch(0, first)?;
        self.set_scratch(1, second)?;
        Ok((self.scratch(0)?, self.scratch(1)?))
    }

    fn scratch_offset(index: usize) -> usize {
        match index {
            0 => regmap::SCRATCH0,
            1 => regmap::SCRATCH1,
            other => panic!("scratch index {other} does not exist"),
        }
    }
}
