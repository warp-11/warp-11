//! `RecordCopy` on a board: the bring-up for regions in the host's memory.
//!
//!   warp11_region_copy --layout record_copy_axi_layout.rs \
//!       [--uio record_copy_axi] [--buffer udmabuf-record-copy-axi]
//!
//! The same copy `tests/region_bridge.rs` runs against the Sim, over the uio
//! mapping and the app's `u-dma-buf` instead: fill `source` by the seam's
//! fields, start, wait for `landed`, and check every field of `copied`
//! (`weight` comes back one higher — the toy's own work). The arena is
//! disarmed on every way out, so nothing is in flight when the app unloads.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::path::PathBuf;
use std::process::exit;
use std::time::{Duration, Instant};

use warp11_host::arena::BoardArena;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_host::seam::{read_aperture_words, read_field, read_layout, read_region, resolve};
use warp11_host::udmabuf::{find_uio, Udmabuf};
use warp11_runtime::region::{Arena, RegionDevice};

fn usage() -> ! {
    eprintln!("usage: warp11_region_copy --layout <seam.rs> [--uio record_copy_axi] [--buffer udmabuf-record-copy-axi]");
    exit(2)
}

fn fail(what: impl std::fmt::Display) -> ! {
    eprintln!("{what}");
    exit(1)
}

fn main() {
    let mut layout = None;
    let mut uio = "record_copy_axi".to_string();
    let mut buffer = "udmabuf-record-copy-axi".to_string();
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--layout" => layout = Some(PathBuf::from(args.next().unwrap_or_else(|| usage()))),
            "--uio" => uio = args.next().unwrap_or_else(|| usage()),
            "--buffer" => buffer = args.next().unwrap_or_else(|| usage()),
            _ => usage(),
        }
    }
    let seam = layout.unwrap_or_else(|| usage());

    let (registers, hash) = read_layout(&seam).unwrap_or_else(|e| fail(format!("cannot read {}: {e:?}", seam.display())));
    let start = resolve("start", &registers).unwrap_or_else(|e| fail(e));
    let landed = resolve("landed", &registers).unwrap_or_else(|e| fail(e));
    let source = read_region(&seam, "source").unwrap_or_else(|e| fail(format!("no region 'source': {e:?}")));
    let copied = read_region(&seam, "copied").unwrap_or_else(|e| fail(format!("no region 'copied': {e:?}")));
    let field = |region: &str, name: &str| {
        read_field(&seam, region, name).unwrap_or_else(|e| fail(format!("no field {region}.{name}: {e:?}")))
    };
    let records = source.records;

    let buf = Udmabuf::find(&buffer)
        .ok()
        .flatten()
        .unwrap_or_else(|| fail(format!("no u-dma-buf '{buffer}' — is the app loaded?")));
    println!("arena: {buffer} at 0x{:x}, {} bytes", buf.phys_addr, buf.size);
    let mut arena = BoardArena::open(buf).unwrap_or_else(|e| fail(format!("cannot map the arena: {e:?}")));

    let reg_path = find_uio(&uio).ok().flatten().unwrap_or_else(|| fail(format!("no uio node named '{uio}'")));
    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&reg_path)
        .unwrap_or_else(|e| fail(format!("cannot open {}: {e}", reg_path.display())));
    let aperture = read_aperture_words(&seam).ok().flatten().map(|w| w * 4).unwrap_or(0x1000);
    let window = MmapWindow::open(&reg_file, 0, aperture).unwrap_or_else(|e| fail(format!("cannot map registers: {e:?}")));
    let mut device = RegionDevice::open(window, hash).unwrap_or_else(|e| fail(format!("the top did not answer as a region carrier: {e:?}")));

    let ok = run(&mut device, &mut arena, &source, &copied, &field, records, start, landed);
    if let Err(e) = device.disarm() {
        eprintln!("disarm failed: {e:?} — do not unload the app until it is disarmed");
        exit(1);
    }
    if ok {
        println!("\nREGION COPY OK: {records} records");
    } else {
        println!("\nMISMATCH");
        exit(1);
    }
}

#[allow(clippy::too_many_arguments)]
fn run(
    device: &mut RegionDevice<MmapWindow>,
    arena: &mut BoardArena,
    source: &warp11_runtime::region::Region,
    copied: &warp11_runtime::region::Region,
    field: &dyn Fn(&str, &str) -> warp11_runtime::region::Field,
    records: usize,
    start: usize,
    landed: usize,
) -> bool {
    let weight_of = |i: usize| -> i16 { [-1i16, 32767, -32768, 0][i % 4].wrapping_add((i / 4) as i16) };

    let clear = vec![0u8; copied.record_bytes];
    for i in 0..records {
        copied.write(arena, i, &clear).unwrap_or_else(|e| fail(format!("clear copied {i}: {e:?}")));
        let mut record = vec![0u8; source.record_bytes];
        field("source", "key").set(&mut record, 0xC0DE_0000 + i as u64);
        field("source", "payload").set(&mut record, 0xA5A5_0000_0000_0000 + i as u64 * 0x1_0000_0001);
        field("source", "weight").set(&mut record, weight_of(i) as u16 as u64);
        field("source", "note").set(&mut record, 0xABC000 + i as u64);
        source.write(arena, i, &record).unwrap_or_else(|e| fail(format!("stage source {i}: {e:?}")));
    }

    device.arm(arena.base()).unwrap_or_else(|e| fail(format!("arm: {e:?}")));
    let before = device.get(landed).unwrap_or_else(|e| fail(format!("landed: {e:?}")));
    println!("armed at 0x{:x}; landed before start = {before}", arena.base());
    let began = Instant::now();
    device.set(start, 1).unwrap_or_else(|e| fail(format!("start: {e:?}")));
    let mut count = before;
    while (count as usize) < records && began.elapsed() < Duration::from_secs(2) {
        count = device.get(landed).unwrap_or_else(|e| fail(format!("landed: {e:?}")));
    }
    println!("landed {count}/{records} in {:?}", began.elapsed());
    if (count as usize) < records {
        return false;
    }

    let mut bad = 0;
    for i in 0..records {
        let mut record = vec![0u8; copied.record_bytes];
        copied.read(arena, i, &mut record).unwrap_or_else(|e| fail(format!("read copied {i}: {e:?}")));
        let expected = [
            ("key", 0xC0DE_0000 + i as u64),
            ("payload", 0xA5A5_0000_0000_0000 + i as u64 * 0x1_0000_0001),
            ("weight", weight_of(i).wrapping_add(1) as u16 as u64),
            ("note", 0xABC000 + i as u64),
        ];
        for (name, want) in expected {
            let got = field("copied", name).get(&record);
            if got != want {
                bad += 1;
                if bad <= 10 {
                    println!("  record {i} {name}: got 0x{got:x}, want 0x{want:x}");
                }
            }
        }
    }
    println!("{bad} field mismatches over {records} records");
    bad == 0
}
