//! First light for the F#-elaborated Game of Life accelerator on the KV260.
//! Run on the board after `xmutil loadapp gol-fs`:
//!
//!     ./gol_first_light              # registers via the uio node, no root
//!     ./gol_first_light /dev/mem     # root mode (sudo)
//!
//! The sequence is the Sim rehearsal, on silicon: ID, a soup loaded through
//! the window and prefetched, a polled burst, one idle beat, then a conflate
//! capture read straight out of PS DDR over the cached udmabuf path and
//! compared bit-for-bit against the software twin. Ends with a continuous-run
//! throughput measurement (generations per second at interval 1).

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use std::time::{Duration, Instant};
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_runtime::gol_layout as layout;
use warp11_runtime::RegisterWindow;

const AXI_BASE: i64 = 0xB000_0000;
const PL_CLOCK_MHZ: f64 = 166.666672; // set in golfs_bd_bd.tcl (population-pipelined timing closure)

/// The software twin: B3/S23, dead border, rows as u64 bit masks.
fn life_step(rows: &[u64; 64]) -> [u64; 64] {
    let cell = |y: i32, x: i32| -> u32 {
        if (0..64).contains(&y) && (0..64).contains(&x) {
            ((rows[y as usize] >> x) & 1) as u32
        } else {
            0
        }
    };
    let mut next = [0u64; 64];
    for y in 0..64i32 {
        for x in 0..64i32 {
            let mut n = 0;
            for dy in -1..=1 {
                for dx in -1..=1 {
                    if dy != 0 || dx != 0 {
                        n += cell(y + dy, x + dx);
                    }
                }
            }
            if n == 3 || (cell(y, x) == 1 && n == 2) {
                next[y as usize] |= 1 << x;
            }
        }
    }
    next
}

fn population(rows: &[u64; 64]) -> u32 {
    rows.iter().map(|r| r.count_ones()).sum()
}

fn udmabuf_attr(name: &str, value: &str) -> std::io::Result<()> {
    std::fs::write(format!("/sys/class/u-dma-buf/udmabuf0/{name}"), value)
}

fn udmabuf_phys_addr() -> Option<u64> {
    let text = std::fs::read_to_string("/sys/class/u-dma-buf/udmabuf0/phys_addr").ok()?;
    u64::from_str_radix(text.trim().trim_start_matches("0x"), 16).ok()
}

fn find_uio(name: &str) -> Option<String> {
    for entry in std::fs::read_dir("/sys/class/uio").ok()?.flatten() {
        if let Ok(n) = std::fs::read_to_string(entry.path().join("name")) {
            if n.trim() == name {
                return Some(format!("/dev/{}", entry.file_name().to_string_lossy()));
            }
        }
    }
    None
}

fn main() {
    let reg_arg = std::env::args().nth(1).unwrap_or_else(|| "auto".into());

    let fb_base: u64 = udmabuf_phys_addr().unwrap_or_else(|| {
        eprintln!("no udmabuf0 phys_addr — is the u-dma-buf module loaded?");
        exit(1);
    });

    let (device_path, reg_offset) = if reg_arg == "auto" {
        let uio = find_uio("golfs").unwrap_or_else(|| {
            eprintln!("no uio node named 'golfs' — is the gol-fs app loaded?");
            exit(1);
        });
        (uio, 0i64)
    } else if reg_arg == "/dev/mem" {
        ("/dev/mem".to_string(), AXI_BASE)
    } else {
        (reg_arg, 0i64)
    };

    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&device_path)
        .unwrap_or_else(|e| {
            eprintln!("cannot open {device_path}: {e}");
            exit(1);
        });
    let mut regs = MmapWindow::open(&reg_file, reg_offset, layout::APERTURE_BYTES)
        .unwrap_or_else(|e| {
            eprintln!("register mmap of {device_path} failed: {e:?}");
            exit(1);
        });

    fn rd(w: &mut MmapWindow, offset: usize) -> u32 { w.read32(offset).unwrap() }
    fn wr(w: &mut MmapWindow, offset: usize, value: u32) { w.write32(offset, value).unwrap() }

    let id = rd(&mut regs, layout::ID_OFFSET);
    if id != layout::ID_VALUE {
        eprintln!("ID mismatch: read 0x{id:08X}, want 0x{:08X} — wrong bitstream?", layout::ID_VALUE);
        exit(1);
    }
    println!("ID ok (0x{id:08X}), fb base 0x{fb_base:08X}");

    wr(&mut regs, layout::FB_BASE_ADDR_OFFSET, fb_base as u32);

    // A deterministic soup (xorshift), dense enough to churn every border.
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    let mut soup = [0u64; 64];
    for row in soup.iter_mut() {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        *row = seed;
    }

    for (y, row) in soup.iter().enumerate() {
        wr(&mut regs, layout::LOAD_ROW_OFFSET + y * 8, *row as u32);
        wr(&mut regs, layout::LOAD_ROW_OFFSET + y * 8 + 4, (*row >> 32) as u32);
    }
    wr(&mut regs, layout::LOAD_OFFSET, 1 << layout::LOAD_BIT);
    std::thread::sleep(Duration::from_millis(1)); // prefetch: ~130 fabric cycles

    let status = rd(&mut regs, layout::BUSY_OFFSET);
    let pop = (status & layout::POPULATION_MASK) >> layout::POPULATION_SHIFT;
    let load_ok = pop == population(&soup) && status & layout::BUSY_MASK == 0;
    println!("load via window + prefetch:   {load_ok}  (population {pop})");

    // A 96-generation burst at interval 1, polled to completion. tickCount
    // counts fires and each fire is GENS_PER_CYCLE generations, so the
    // count divides (96 = 2^5 x 3 covers k = 1, 2, 3, 4).
    let generations = 96u32;
    wr(&mut regs, layout::TICK_COUNT_OFFSET, generations / layout::GENS_PER_CYCLE as u32);
    wr(&mut regs, layout::INTERVAL_CYCLES_OFFSET, 1);
    let wall = Instant::now();
    wr(&mut regs, layout::TICK_OFFSET, 1 << layout::TICK_BIT);
    let mut guard = 0;
    while rd(&mut regs, layout::BUSY_OFFSET) & layout::BUSY_MASK != 0 && guard < 1_000_000 {
        guard += 1;
    }
    let burst_ms = wall.elapsed().as_secs_f64() * 1e3;

    let mut twin = soup;
    for _ in 0..generations {
        twin = life_step(&twin);
    }
    let gen = rd(&mut regs, layout::GENERATION_OFFSET);
    let status = rd(&mut regs, layout::BUSY_OFFSET);
    let pop = (status & layout::POPULATION_MASK) >> layout::POPULATION_SHIFT;
    let burst_ok = gen == generations && pop == population(&twin);
    println!("burst of {generations} (interval 1): {burst_ok}  ({burst_ms:.3} ms host wall incl. polling)");

    // One idle beat so the settled grid publishes, then capture and read the
    // granted slot from DDR over the cached path.
    std::thread::sleep(Duration::from_millis(1));
    wr(&mut regs, layout::SNAP_CAPTURE_OFFSET, 1 << layout::SNAP_CAPTURE_BIT);
    let mut snap_guard = 0;
    while rd(&mut regs, layout::SNAP_READY_OFFSET) & layout::SNAP_READY_MASK == 0 && snap_guard < 1_000_000
    {
        snap_guard += 1;
    }
    let snap = rd(&mut regs, layout::SNAP_READY_OFFSET);
    let slot = ((snap & layout::SNAP_SLOT_MASK) >> layout::SNAP_SLOT_SHIFT) as usize;
    let overrun = (snap & layout::SNAP_OVERRUN_MASK) >> layout::SNAP_OVERRUN_SHIFT;
    println!("capture granted slot {slot} (overrun {overrun})");

    let slot_offset = slot * layout::SLOT_STRIDE_BYTES;
    let fb_bytes = 3 * layout::SLOT_STRIDE_BYTES;
    let frame = match udmabuf_attr("sync_offset", &slot_offset.to_string())
        .and_then(|()| udmabuf_attr("sync_size", &layout::FRAME_BYTES.to_string()))
        .and_then(|()| udmabuf_attr("sync_direction", "2"))
        .and_then(|()| udmabuf_attr("sync_for_cpu", "1"))
    {
        Ok(()) => {
            let cached = OpenOptions::new()
                .read(true)
                .write(true)
                .open("/dev/udmabuf0")
                .expect("open /dev/udmabuf0");
            let map = MmapWindow::open(&cached, 0, fb_bytes).expect("cached fb mmap");
            map.bytes()[slot_offset..slot_offset + layout::FRAME_BYTES].to_vec()
        }
        Err(e) => {
            println!("cached path unavailable ({e}); falling back to O_SYNC");
            let osync = OpenOptions::new()
                .read(true)
                .write(true)
                .custom_flags(O_SYNC)
                .open("/dev/udmabuf0")
                .expect("open /dev/udmabuf0 (O_SYNC)");
            let map = MmapWindow::open(&osync, 0, fb_bytes).expect("uncached fb mmap");
            map.bytes()[slot_offset..slot_offset + layout::FRAME_BYTES].to_vec()
        }
    };

    // Beat i carries rows 2i (low 8 bytes) and 2i+1 (high 8), little-endian.
    let mut fabric_rows = [0u64; 64];
    for (y, row) in fabric_rows.iter_mut().enumerate() {
        let base = (y / layout::ROWS_PER_BEAT) * 16 + (y % layout::ROWS_PER_BEAT) * 8;
        *row = u64::from_le_bytes(frame[base..base + 8].try_into().unwrap());
    }
    wr(&mut regs, layout::SNAP_RELEASE_OFFSET, 1 << layout::SNAP_RELEASE_BIT);

    let mismatches = fabric_rows
        .iter()
        .zip(twin.iter())
        .filter(|(a, b)| a != b)
        .count();
    println!("twin mismatches: {mismatches} of 64 rows");

    // Interrupt bits should both be pending (burst done + snapshot granted).
    let irq = rd(&mut regs, layout::BURST_IRQ_OFFSET);
    wr(&mut regs, layout::BURST_IRQ_OFFSET, 0x3);
    let irq_ok = irq == 0x3 && rd(&mut regs, layout::BURST_IRQ_OFFSET) == 0;
    println!("w1c irq set then cleared:     {irq_ok}");

    // Continuous mode for ~100 ms: the fabric ticks a generation per cycle.
    wr(&mut regs, layout::TICK_COUNT_OFFSET, 0);
    wr(&mut regs, layout::TICK_OFFSET, 1 << layout::TICK_BIT);
    let before = rd(&mut regs, layout::GENERATION_OFFSET);
    let t = Instant::now();
    std::thread::sleep(Duration::from_millis(100));
    let after = rd(&mut regs, layout::GENERATION_OFFSET);
    let elapsed = t.elapsed().as_secs_f64();
    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
    let per_second = f64::from(after.wrapping_sub(before)) / elapsed;
    println!(
        "continuous: {:.0}M generations/s (PL clock {PL_CLOCK_MHZ:.2} MHz x {} gens/cycle = {:.0}M)",
        per_second / 1e6,
        layout::GENS_PER_CYCLE,
        PL_CLOCK_MHZ * layout::GENS_PER_CYCLE as f64
    );

    // Disarm before exit: zeroing fbBaseAddr stalls the conflate ahead of the
    // write master, so no transaction is ever in flight when the app is later
    // unloaded. Tearing down the bitstream mid-transaction leaves the PS-side
    // HP0 path with orphaned beats — a permanent AW/W pairing skew (frames
    // arrive rotated by the in-flight count) that survives app reloads and
    // clears only on reboot. Verified by on-demand reproduction 2026-08-07.
    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
    wr(&mut regs, layout::FB_BASE_ADDR_OFFSET, 0);
    std::thread::sleep(Duration::from_millis(1));

    if !load_ok || !burst_ok || mismatches != 0 || !irq_ok {
        eprintln!("FAIL");
        exit(1);
    }
    println!("FIRST LIGHT OK: bit-exact against the twin");
}
