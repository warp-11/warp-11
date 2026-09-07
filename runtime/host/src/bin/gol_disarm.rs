//! Quiesce the GoL accelerator before `xmutil unloadapp`: stop the pacing
//! FSM and zero fbBaseAddr, which stalls the conflate ahead of the write
//! master so nothing is in flight when the bitstream is torn down. Unloading
//! mid-transaction leaves the PS-side HP0 path with orphaned beats — a
//! permanent AW/W pairing skew (rotated frames) that survives reloads and
//! clears only on reboot. Run this after stopping any daemon, before unload.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_runtime::gol_layout as layout;
use warp11_runtime::RegisterWindow;

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
    let uio = find_uio("golfs").unwrap_or_else(|| {
        eprintln!("no uio node named 'golfs' — nothing to disarm");
        exit(0);
    });
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&uio)
        .expect("open uio");
    let mut regs = MmapWindow::open(&file, 0, layout::APERTURE_BYTES).expect("register mmap");

    // Checked here of all places, because this tool's whole job is to quiesce
    // the fabric's writer before the bitstream is unloaded — and unloading with
    // writes in flight leaves a PS-side HP0 pairing skew that survives every
    // app reload and clears only on a reboot. Writing STOP to an offset that
    // moved would report success and disarm nothing.
    let id = regs.read32(layout::ID_OFFSET).expect("read id");
    let hash = regs.read32(layout::LAYOUT_HASH_OFFSET).expect("read layout hash");

    if id != layout::ID_VALUE || hash != layout::LAYOUT_HASH_VALUE {
        eprintln!(
            "refusing to disarm: id {id:#010X} (want {:#010X}), layout {hash:#06X} (want {:#06X}). \
             These offsets are not this design's — disarming would write somewhere else and \
             report success. Do NOT unload the bitstream until this agrees.",
            layout::ID_VALUE,
            layout::LAYOUT_HASH_VALUE
        );
        exit(1);
    }

    regs.write32(layout::STOP_OFFSET, 1 << layout::STOP_BIT).unwrap();
    regs.write32(layout::FB_BASE_ADDR_OFFSET, 0).unwrap();
    std::thread::sleep(std::time::Duration::from_millis(1));
    println!("gol-fs disarmed: writer quiesced, safe to unload");
}
