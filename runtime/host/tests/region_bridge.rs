//! The region driver against the F# Sim, across the language seam: the
//! `RecordCopy` toy's build directory is written, its seam read for every
//! place and field, and `RegionDevice` arms the bridge's arena, fills
//! `source`, starts the copy, waits for `landed`, and reads `copied` back —
//! the code that runs on the board over a `u-dma-buf`.

use std::path::PathBuf;
use std::process::Command;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::seam::{read_const, read_field, read_layout, read_region, resolve};
use warp11_runtime::region::{Arena, RegionDevice};

fn fsproj() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../hdl/Warp11.Designs/Warp11.Designs.fsproj")
}

#[test]
fn the_driver_copies_records_through_the_regions() {
    if Command::new("dotnet").arg("--version").output().is_err() {
        eprintln!("SKIPPED: dotnet not on PATH — the region bridge needs the F# side");
        return;
    }

    let dir = std::env::temp_dir().join(format!("warp11-region-bridge-{}", std::process::id()));
    let built = Command::new("dotnet")
        .args(["run", "--project"])
        .arg(fsproj())
        .args(["--", "recordcopy"])
        .arg(&dir)
        .output()
        .expect("dotnet runs");
    assert!(built.status.success(), "the build directory is written: {}", String::from_utf8_lossy(&built.stderr));
    let seam = dir.join("record_copy_axi_layout.rs");

    let (registers, hash) = read_layout(&seam).expect("the seam reads");
    let start = resolve("start", &registers).unwrap();
    let landed = resolve("landed", &registers).unwrap();
    let source = read_region(&seam, "source").unwrap();
    let copied = read_region(&seam, "copied").unwrap();
    let field = |region: &str, name: &str| read_field(&seam, region, name).unwrap();
    let records = source.records;
    assert_eq!(read_const(&seam, "SOURCE_RECORDS").unwrap(), Some(records as u32));

    let window = FsSimWindow::spawn_with(&fsproj(), &["regionserve", "recordcopy"], "REGIONSERVE")
        .expect("regionserve should come up");
    let mut device = RegionDevice::open(window, hash).expect("the region identity and layout should answer");
    let base = device.window_mut().base();
    device.arm(base).expect("arm");

    let weight_of = |i: usize| -> i16 { [-1i16, 32767, -32768, 0][i % 4].wrapping_add((i / 4) as i16) };
    for i in 0..records {
        let mut record = vec![0u8; source.record_bytes];
        field("source", "key").set(&mut record, 0xC0DE_0000 + i as u64);
        field("source", "payload").set(&mut record, 0xA5A5_0000_0000_0000 + i as u64 * 0x1_0000_0001);
        field("source", "weight").set(&mut record, weight_of(i) as u16 as u64);
        field("source", "note").set(&mut record, 0xABC000 + i as u64);
        source.write(device.window_mut(), i, &record).expect("stage a record");
    }

    device.set(start, 1).expect("start");
    let mut done = false;
    for _ in 0..200 {
        if device.get(landed).expect("landed") as usize == records {
            done = true;
            break;
        }
        device.window_mut().free_cycles(256).expect("cycles");
    }
    assert!(done, "every record landed");

    for i in 0..records {
        let mut record = vec![0u8; copied.record_bytes];
        copied.read(device.window_mut(), i, &mut record).expect("read a record back");
        assert_eq!(field("copied", "key").get(&record), 0xC0DE_0000 + i as u64, "key {i}");
        assert_eq!(field("copied", "payload").get(&record), 0xA5A5_0000_0000_0000 + i as u64 * 0x1_0000_0001, "payload {i}");
        assert_eq!(field("copied", "weight").get(&record) as u16 as i16, weight_of(i).wrapping_add(1), "weight {i}");
        assert_eq!(field("copied", "note").get(&record), 0xABC000 + i as u64, "note {i}");
    }

    device.disarm().expect("disarm");
    let _ = std::fs::remove_dir_all(&dir);
}

/// The link's protocol as the host speaks it and as the seam states it:
/// the same operation numbers, and every region the copy keeps over the
/// link found by name with the shape the fabric gave it.
#[test]
fn the_link_protocol_agrees_with_the_seam() {
    use warp11_host::link_regions::{LinkRegions, LINK_READ, LINK_WRITE};

    if Command::new("dotnet").arg("--version").output().is_err() {
        eprintln!("SKIPPED: dotnet not on PATH");
        return;
    }

    let dir = std::env::temp_dir().join(format!("warp11-region-link-{}", std::process::id()));
    let built = Command::new("dotnet")
        .args(["run", "--project"])
        .arg(fsproj())
        .args(["--", "recordcopylink"])
        .arg(&dir)
        .output()
        .expect("dotnet runs");
    assert!(built.status.success(), "the build directory is written: {}", String::from_utf8_lossy(&built.stderr));
    let seam = dir.join("record_copy_uart_layout.rs");

    assert_eq!(read_const(&seam, "REGION_LINK_READ").unwrap(), Some(LINK_READ));
    assert_eq!(read_const(&seam, "REGION_LINK_WRITE").unwrap(), Some(LINK_WRITE));
    let regions = LinkRegions::from_seam(&seam, &["source", "copied"]).expect("both regions are over the link");
    let source = regions.region("source").unwrap();
    assert_eq!(source.records, 64);
    assert_eq!(source.record_bytes, 20);
    assert_ne!(source.id, regions.region("copied").unwrap().id);
    let _ = std::fs::remove_dir_all(&dir);
}
