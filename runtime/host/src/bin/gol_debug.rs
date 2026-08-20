//! Snapshot-path probe: load a soup, burst, then characterize what the DDR
//! frame actually holds — the twin at nearby generations, the raw soup, or
//! stale data — and whether repeated captures of a static grid agree.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::time::Duration;
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

fn udmabuf_attr(name: &str, value: &str) -> std::io::Result<()> {
    std::fs::write(format!("/sys/class/u-dma-buf/udmabuf0/{name}"), value)
}

fn life_step(rows: &[u64; 64]) -> [u64; 64] {
    let mut next = [0u64; 64];
    for y in 0..64 {
        for x in 0..64 {
            let mut n = 0;
            for dy in -1i32..=1 {
                for dx in -1i32..=1 {
                    if dy == 0 && dx == 0 {
                        continue;
                    }
                    let (yy, xx) = (y as i32 + dy, x as i32 + dx);
                    if (0..64).contains(&yy) && (0..64).contains(&xx) {
                        n += (rows[yy as usize] >> xx) & 1;
                    }
                }
            }
            if n == 3 || (n == 2 && (rows[y] >> x) & 1 == 1) {
                next[y] |= 1 << x;
            }
        }
    }
    next
}

fn main() {
    let uio = find_uio("golfs").expect("golfs uio");
    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&uio)
        .expect("open uio");
    let mut regs = MmapWindow::open(&reg_file, 0, layout::APERTURE_BYTES).expect("regs");
    let rd = |w: &mut MmapWindow, o: usize| w.read32(o).unwrap();
    let wr = |w: &mut MmapWindow, o: usize, v: u32| w.write32(o, v).unwrap();

    let fb_base = {
        let t = std::fs::read_to_string("/sys/class/u-dma-buf/udmabuf0/phys_addr").unwrap();
        u64::from_str_radix(t.trim().trim_start_matches("0x"), 16).unwrap()
    };
    wr(&mut regs, layout::FB_BASE_ADDR_OFFSET, fb_base as u32);

    // Same soup as first light.
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    let mut soup = [0u64; 64];
    for row in soup.iter_mut() {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        *row = seed;
    }
    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
    for (y, row) in soup.iter().enumerate() {
        wr(&mut regs, layout::LOAD_ROW_OFFSET + y * 8, *row as u32);
        wr(&mut regs, layout::LOAD_ROW_OFFSET + y * 8 + 4, (*row >> 32) as u32);
    }
    wr(&mut regs, layout::LOAD_OFFSET, 1 << layout::LOAD_BIT);
    std::thread::sleep(Duration::from_millis(2));

    wr(&mut regs, layout::TICK_COUNT_OFFSET, 100);
    wr(&mut regs, layout::INTERVAL_CYCLES_OFFSET, 1);
    wr(&mut regs, layout::TICK_OFFSET, 1 << layout::TICK_BIT);
    std::thread::sleep(Duration::from_millis(2));

    let capture = |regs: &mut MmapWindow| -> ([u64; 64], usize) {
        wr(regs, layout::SNAP_CAPTURE_OFFSET, 1 << layout::SNAP_CAPTURE_BIT);
        let mut guard = 0;
        while rd(regs, layout::SNAP_READY_OFFSET) & layout::SNAP_READY_MASK == 0 && guard < 1_000_000 {
            guard += 1;
        }
        let snap = rd(regs, layout::SNAP_READY_OFFSET);
        let slot = ((snap & layout::SNAP_SLOT_MASK) >> layout::SNAP_SLOT_SHIFT) as usize;
        let slot_offset = slot * layout::SLOT_STRIDE_BYTES;
        udmabuf_attr("sync_offset", &slot_offset.to_string()).unwrap();
        udmabuf_attr("sync_size", &layout::FRAME_BYTES.to_string()).unwrap();
        udmabuf_attr("sync_direction", "2").unwrap();
        udmabuf_attr("sync_for_cpu", "1").unwrap();
        let cached = OpenOptions::new().read(true).write(true).open("/dev/udmabuf0").unwrap();
        let map = MmapWindow::open(&cached, 0, 3 * layout::SLOT_STRIDE_BYTES).unwrap();
        let frame = &map.bytes()[slot_offset..slot_offset + layout::FRAME_BYTES];
        let mut rows = [0u64; 64];
        for (y, row) in rows.iter_mut().enumerate() {
            let base = (y / layout::ROWS_PER_BEAT) * 16 + (y % layout::ROWS_PER_BEAT) * 8;
            *row = u64::from_le_bytes(frame[base..base + 8].try_into().unwrap());
        }
        wr(regs, layout::SNAP_RELEASE_OFFSET, 1 << layout::SNAP_RELEASE_BIT);
        (rows, slot)
    };

    // The same physical bytes through the uncached window — if this disagrees
    // with the cached+sync read, the fabric is innocent and the readback is
    // the suspect.
    let read_osync = |slot: usize| -> [u64; 64] {
        let osync = OpenOptions::new()
            .read(true)
            .write(true)
            .custom_flags(O_SYNC)
            .open("/dev/udmabuf0")
            .unwrap();
        let map = MmapWindow::open(&osync, 0, 3 * layout::SLOT_STRIDE_BYTES).unwrap();
        let frame = &map.bytes()[slot * layout::SLOT_STRIDE_BYTES..][..layout::FRAME_BYTES];
        let mut rows = [0u64; 64];
        for (y, row) in rows.iter_mut().enumerate() {
            let base = (y / layout::ROWS_PER_BEAT) * 16 + (y % layout::ROWS_PER_BEAT) * 8;
            *row = u64::from_le_bytes(frame[base..base + 8].try_into().unwrap());
        }
        rows
    };

    let (frame1, slot1) = capture(&mut regs);
    let osync1 = read_osync(slot1);
    println!(
        "cached vs O_SYNC same slot:     {} rows differ",
        frame1.iter().zip(osync1.iter()).filter(|(a, b)| a != b).count()
    );
    std::thread::sleep(Duration::from_millis(5));
    let (frame2, slot2) = capture(&mut regs);

    let diff = |a: &[u64; 64], b: &[u64; 64]| a.iter().zip(b.iter()).filter(|(x, y)| x != y).count();
    println!("slots granted: {slot1} then {slot2}");
    println!("frame1 vs frame2 (static grid): {} rows differ", diff(&frame1, &frame2));
    println!("frame1 vs soup:                 {} rows differ", diff(&frame1, &soup));
    println!("frame1 population: {}", frame1.iter().map(|r| r.count_ones()).sum::<u32>());

    let mut twin = soup;
    for gen in 1..=105 {
        twin = life_step(&twin);
        if (95..=105).contains(&gen) {
            println!("frame1 vs twin gen {gen}: {} rows differ", diff(&frame1, &twin));
        }
    }
    let mut twin100 = soup;
    for _ in 0..100 {
        twin100 = life_step(&twin100);
    }
    let x_mirror = |g: &[u64; 64]| -> [u64; 64] { std::array::from_fn(|y| g[y].reverse_bits()) };
    let y_mirror = |g: &[u64; 64]| -> [u64; 64] { std::array::from_fn(|y| g[63 - y]) };
    println!("frame1 vs twin100 x-mirrored:   {} rows differ", diff(&frame1, &x_mirror(&twin100)));
    println!("frame1 vs twin100 y-mirrored:   {} rows differ", diff(&frame1, &y_mirror(&twin100)));
    println!(
        "frame1 vs twin100 xy-mirrored:  {} rows differ",
        diff(&frame1, &x_mirror(&y_mirror(&twin100)))
    );
    let transpose = |g: &[u64; 64]| -> [u64; 64] {
        std::array::from_fn(|y| (0..64).fold(0u64, |acc, x| acc | (((g[x] >> y) & 1) << x)))
    };
    let t = transpose(&twin100);
    println!("frame1 vs twin100 transposed:   {} rows differ", diff(&frame1, &t));
    println!("frame1 vs twin100 rot90:        {} rows differ", diff(&frame1, &y_mirror(&t)));
    println!("frame1 vs twin100 rot270:       {} rows differ", diff(&frame1, &x_mirror(&t)));
    println!(
        "frame1 vs twin100 anti-diag:    {} rows differ",
        diff(&frame1, &x_mirror(&y_mirror(&t)))
    );
    println!("twin100 population: {}", twin100.iter().map(|r| r.count_ones()).sum::<u32>());
    // Which twin row (under which per-word transform) does each frame row
    // hold? Nails the exact layout permutation.
    println!("frame row -> twin100 row matches (id / bswap / bitrev):");
    for (y, fr) in frame1.iter().enumerate() {
        if *fr == 0 {
            continue;
        }
        for (z, tw) in twin100.iter().enumerate() {
            if fr == tw {
                println!("  frame[{y:2}] == twin[{z:2}]");
            } else if *fr == tw.swap_bytes() {
                println!("  frame[{y:2}] == bswap(twin[{z:2}])");
            } else if *fr == tw.reverse_bits() {
                println!("  frame[{y:2}] == bitrev(twin[{z:2}])");
            }
        }
    }
    let status = rd(&mut regs, layout::BUSY_OFFSET);
    println!(
        "grid population (status reg): {}",
        (status & layout::POPULATION_MASK) >> layout::POPULATION_SHIFT
    );

    // The whole triple-buffer, mapped: which twin row does every 8-byte word
    // of each slot hold? The full layout the writer actually produced.
    udmabuf_attr("sync_offset", "0").unwrap();
    udmabuf_attr("sync_size", &(3 * layout::SLOT_STRIDE_BYTES).to_string()).unwrap();
    udmabuf_attr("sync_for_cpu", "1").unwrap();
    let cached = OpenOptions::new().read(true).write(true).open("/dev/udmabuf0").unwrap();
    let map = MmapWindow::open(&cached, 0, 3 * layout::SLOT_STRIDE_BYTES).unwrap();
    let all = map.bytes();
    for slot in 0..3 {
        let mut cells = String::new();
        for w in 0..64 {
            let base = slot * layout::SLOT_STRIDE_BYTES + w * 8;
            let word = u64::from_le_bytes(all[base..base + 8].try_into().unwrap());
            let tag = if word == 0 {
                ".".to_string()
            } else if let Some(z) = twin100.iter().position(|t| *t == word) {
                format!("{z}")
            } else if let Some(z) = soup.iter().position(|t| *t == word) {
                format!("s{z}")
            } else {
                "?".to_string()
            };
            cells.push_str(&format!("{tag} "));
        }
        println!("slot {slot}: {cells}");
    }
}
