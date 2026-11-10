//! Register map for the `MandelbrotBatch` slave on the kv260.
//! Generated from the design's own register map. Do not edit by hand.

pub const APERTURE_BYTES: usize = 256;
pub const ID_OFFSET: usize = 0x000;
pub const ID_VALUE: u32 = 0x7a11ba7c;
pub const START_OFFSET: usize = 0x000;
pub const START_BIT: u32 = 0;
pub const LAYOUT_HASH_OFFSET: usize = 0x004;
pub const LAYOUT_HASH_VALUE: u32 = 0x0000e893;
pub const BUSY_OFFSET: usize = 0x008;
pub const BUSY_SHIFT: u32 = 0;
pub const BUSY_MASK: u32 = 0x1;
pub const DONE_IRQ_OFFSET: usize = 0x00c;
pub const DONE_IRQ_BIT: u32 = 0;
pub const SRC_ADDR_OFFSET: usize = 0x010;
pub const SRC_ADDR_WIDTH: u32 = 32;
pub const SRC_ADDR_RESET: u32 = 0x0;
pub const DST_ADDR_OFFSET: usize = 0x014;
pub const DST_ADDR_WIDTH: u32 = 32;
pub const DST_ADDR_RESET: u32 = 0x0;
pub const FRAME_COUNT_OFFSET: usize = 0x018;
pub const FRAME_COUNT_WIDTH: u32 = 32;
pub const FRAME_COUNT_RESET: u32 = 0x0;
pub const CYCLES_OFFSET: usize = 0x01c;
pub const CYCLES_SHIFT: u32 = 0;
pub const CYCLES_MASK: u32 = 0xffffffff;
pub const CX_ORIGIN_OFFSET: usize = 0x020;
pub const CX_ORIGIN_WIDTH: u32 = 32;
pub const CX_ORIGIN_RESET: u32 = 0x0;
pub const CY_ORIGIN_OFFSET: usize = 0x024;
pub const CY_ORIGIN_WIDTH: u32 = 32;
pub const CY_ORIGIN_RESET: u32 = 0x0;
pub const DX_OFFSET: usize = 0x028;
pub const DX_WIDTH: u32 = 32;
pub const DX_RESET: u32 = 0x0;
pub const DY_OFFSET: usize = 0x02c;
pub const DY_WIDTH: u32 = 32;
pub const DY_RESET: u32 = 0x0;

pub const FABRIC_HZ: u64 = 166666672;

pub const FRAME_WIDTH: u64 = 1400;
pub const CHUNK_PIXELS: u64 = 128;
pub const MAX_ITERATIONS: u64 = 256;
pub const FRACTION_BITS: u64 = 28;
