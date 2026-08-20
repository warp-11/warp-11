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
    regs.write32(layout::STOP_OFFSET, 1 << layout::STOP_BIT).unwrap();
    regs.write32(layout::FB_BASE_ADDR_OFFSET, 0).unwrap();
    std::thread::sleep(std::time::Duration::from_millis(1));
    println!("gol-fs disarmed: writer quiesced, safe to unload");
}
