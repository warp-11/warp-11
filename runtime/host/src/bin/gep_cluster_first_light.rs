//! First light for the F#-elaborated WarpCPU genetic-programming cluster on the
//! KV260: 4 breeders x 8 DIV-resident 16-thread lanes, two fillers, inline
//! parents. Run on the board after `xmutil loadapp gep-cluster-fs`:
//!
//!     ./gep_cluster_first_light <vector-dir>              # uio node, no root
//!     ./gep_cluster_first_light <vector-dir> /dev/mem     # root mode (sudo)
//!
//! Silicon is judged by the same oracle the Sim is. The vector directory is
//! written by hdl's `dotnet run --project Warp11.Gep -- boardvector
//! <dir>`: a population, fitness cases, host-marshaled inline-parent work items,
//! and the answers — every offspring's genome and fitness, from
//! `hwBreedOffspring` + the software compile/run/score chain. A pass here means
//! the fabric bred, compiled, evaluated and wrote back bit-exactly.
//!
//! What it measures, which is the point of P5: fabric cycles per offspring at
//! the PL clock, against the Kotlin scoreboard's 0.96 ms/gen resident and
//! 332 us/gen streaming.

use std::fs::{File, OpenOptions};
use std::io::Read;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use std::time::Instant;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_runtime::RegisterWindow;
use warp11_runtime::gep::{GepClusterDevice, GepRegions, GepResult};
use warp11_runtime::gep_layout as layout;

const AXI_BASE: i64 = 0xB000_0000;
/// Set in gepclusterfs_bd_bd.tcl and pinned by the app's dtbo. The operator
/// engine's single-cycle Irwin-Hall multiply is what caps the design here.
const PL_CLOCK_MHZ: f64 = 99.999_001;

/// The queue holds inline-parent work items (three records each), so it is the
/// big region and it scales with the batch — hardcoding its size silently
/// overran the population at 512 entries and cost a debugging round.
fn region_offsets(entries: usize, pop_slots: usize) -> (usize, usize, usize) {
    let align = |v: usize| (v + 0xFFF) & !0xFFF;
    let queue = 0;
    let pop = align(queue + entries * layout::WORK_ITEM_BYTES);
    let ring = align(pop + (pop_slots + entries) * layout::RECORD_BYTES);
    (queue, pop, ring)
}

fn udmabuf_phys_addr(node: &str) -> Option<u64> {
    let text = std::fs::read_to_string(format!("/sys/class/u-dma-buf/{node}/phys_addr")).ok()?;
    u64::from_str_radix(text.trim().trim_start_matches("0x"), 16).ok()
}

/// The app's own buffer, found by the device name its dtbo declares — so a
/// leftover buffer from another app is not silently used instead.
fn find_udmabuf(device_name: &str) -> Option<String> {
    for entry in std::fs::read_dir("/sys/class/u-dma-buf").ok()?.flatten() {
        let node = entry.file_name().to_string_lossy().into_owned();
        let dn = std::fs::read_to_string(entry.path().join("device_name")).unwrap_or_default();
        if dn.trim() == device_name || node == device_name {
            return Some(node);
        }
    }
    None
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

fn read_words(dir: &str, name: &str) -> Vec<u32> {
    let mut bytes = Vec::new();
    File::open(format!("{dir}/{name}"))
        .unwrap_or_else(|e| {
            eprintln!("cannot open {dir}/{name}: {e}");
            exit(2);
        })
        .read_to_end(&mut bytes)
        .expect("read vector file");
    bytes
        .chunks_exact(4)
        .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
        .collect()
}

fn manifest_value(dir: &str, key: &str) -> usize {
    let text = std::fs::read_to_string(format!("{dir}/manifest.txt")).expect("manifest");
    for line in text.lines() {
        let mut parts = line.split_whitespace();
        if parts.next() == Some(key) {
            return parts.next().and_then(|v| v.parse().ok()).expect("manifest value");
        }
    }
    panic!("manifest has no '{key}'");
}

fn main() {
    let mut args = std::env::args().skip(1);
    let dir = args.next().unwrap_or_else(|| {
        eprintln!("usage: gep_cluster_first_light <vector-dir> [/dev/mem]");
        exit(2);
    });
    let reg_arg = args.next().unwrap_or_else(|| "auto".into());

    let entries = manifest_value(&dir, "entries");
    let pop_slots = manifest_value(&dir, "pop_slots");
    let n_cases = manifest_value(&dir, "n_cases");
    let var_count = manifest_value(&dir, "var_count");
    let dest_base = manifest_value(&dir, "dest_base");
    let record_words = manifest_value(&dir, "record_words");
    let work_item_words = manifest_value(&dir, "work_item_words");
    assert_eq!(record_words, layout::RECORD_WORDS, "vector/layout disagree on a record");
    assert_eq!(work_item_words, layout::WORK_ITEM_WORDS, "vector/layout disagree on a work item");

    let (queue_off, pop_off, ring_off) = region_offsets(entries, pop_slots);
    let needed = ring_off + entries * layout::RESULT_BYTES;

    let work_items = read_words(&dir, "workitems.bin");
    let population = read_words(&dir, "population.bin");
    let cases = read_words(&dir, "cases.bin");
    let expected_genomes = read_words(&dir, "expected_genomes.bin");
    let expected_fitness = read_words(&dir, "expected_fitness.bin");

    // ---- the DDR buffer ----
    let node = find_udmabuf("udmabuf-gep-cluster-fs").unwrap_or_else(|| {
        eprintln!("no udmabuf for 'udmabuf-gep-cluster-fs' — is the app loaded and u-dma-buf up?");
        exit(1);
    });
    let phys = udmabuf_phys_addr(&node).unwrap_or_else(|| {
        eprintln!("no phys_addr for {node}");
        exit(1);
    });
    // O_SYNC, and it is not optional: u-dma-buf hands out a CACHED mapping
    // otherwise, and the fabric's masters drive AxCACHE=0 — so they do not
    // snoop the APU caches even through the coherent HPC port. Staging writes
    // would sit in cache while the fabric read stale DDR underneath. Measured:
    // without this the fitnesses come back wrong and most ring records carry
    // entry id 0.
    let buf_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(format!("/dev/{node}"))
        .expect("open udmabuf");
    let mut ddr = MmapWindow::open(&buf_file, 0, 4 << 20).expect("mmap udmabuf");
    assert!(needed <= 4 << 20, "batch needs {needed} bytes, buffer is 4 MiB");

    let put = |ddr: &mut MmapWindow, off: usize, words: &[u32]| {
        for (i, w) in words.iter().enumerate() {
            ddr.write32(off + i * 4, *w).expect("ddr write");
        }
    };
    let get = |ddr: &mut MmapWindow, off: usize, n: usize| -> Vec<u32> {
        (0..n).map(|i| ddr.read32(off + i * 4).expect("ddr read")).collect()
    };

    put(&mut ddr, queue_off, &work_items);
    put(&mut ddr, pop_off, &population);
    // Sentinel the destination slots so a missing writeback is visible rather
    // than passing on whatever was already there.
    let sentinel: Vec<u32> = (0..record_words).map(|i| 0x5EAD_0000 | i as u32).collect();
    for e in 0..entries {
        put(&mut ddr, pop_off + (dest_base + e) * record_words * 4, &sentinel);
    }

    // ---- the register window ----
    let reg_path = if reg_arg == "auto" {
        find_uio("gepclusterfs").unwrap_or_else(|| {
            eprintln!("no uio node named 'gepclusterfs' — pass /dev/mem to use root mode");
            exit(1);
        })
    } else {
        reg_arg
    };
    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&reg_path)
        .expect("open register file");
    let offset = if reg_path == "/dev/mem" { AXI_BASE } else { 0 };
    let window = MmapWindow::open(&reg_file, offset, layout::APERTURE_BYTES).expect("mmap registers");
    let mut device = GepClusterDevice::new(window);

    println!("cluster: {} breeders x {} lanes, {} entries, {} cases",
        layout::N_BREEDERS, layout::N_LANES, entries, n_cases);
    println!("ddr: phys 0x{phys:x} via /dev/{node}");

    let regions = GepRegions {
        queue_base: (phys as usize + queue_off) as u32,
        pop_base: (phys as usize + pop_off) as u32,
        ring_base: (phys as usize + ring_off) as u32,
        queue_entries: entries as u32,
        ring_records: entries as u32,
    };

    device.set_auto_mode(false).expect("auto off");
    device.start(regions, n_cases as u32).expect("start");

    let rows: Vec<&[u32]> = (0..n_cases)
        .map(|k| &cases[k * (var_count + 1)..(k + 1) * (var_count + 1)])
        .collect();
    device.load_cases(&rows).expect("load cases");

    let started = Instant::now();
    device.publish(entries as u32).expect("publish");
    if device.await_results(entries as u32, 50_000_000).is_err() {
        eprintln!("timed out at results_done = {:?}", device.results_done());
        exit(1);
    }
    device.await_idle(1_000_000).expect("quiesce");
    let wall = started.elapsed().as_secs_f64() * 1e3;

    let telemetry = device.telemetry().expect("telemetry");

    // ---- the verdict ----
    let ring = get(&mut ddr, ring_off, entries * layout::RESULT_WORDS);
    let mut results = vec![
        GepResult { fitness: 0, entry_id: 0, seq: 0 };
        entries
    ];
    warp11_runtime::gep::parse_results(&ring, &mut results);

    // Diagnostics that earned their keep: 2026-08-19's silicon failure showed
    // 64 distinct entry ids, sequence numbers in order, and NOT ONE fitness
    // value that the oracle expected anywhere — which is what says the fabric
    // routed every result correctly and computed the values wrong, rather than
    // computing fine values and pairing them with the wrong entries. Those need
    // opposite fixes, and the ids are what tell them apart.
    let ids: Vec<u32> = results.iter().map(|r| r.entry_id).collect();
    let distinct = { let mut d = ids.clone(); d.sort_unstable(); d.dedup(); d.len() };
    let expected_set: std::collections::HashSet<u64> = (0..entries)
        .map(|e| (expected_fitness[e * 2 + 1] as u64) << 32 | expected_fitness[e * 2] as u64)
        .collect();
    let plausible = results.iter().filter(|r| expected_set.contains(&r.fitness)).count();
    let zeros = results.iter().filter(|r| r.fitness == 0).count();
    eprintln!(
        "ring: {distinct}/{entries} distinct entry ids, {plausible}/{entries} fitness values the oracle expected somewhere, {zeros} zero"
    );

    let mut fitness_ok = 0;
    for r in &results {
        let e = r.entry_id as usize;
        if e < entries {
            let want = (expected_fitness[e * 2 + 1] as u64) << 32 | expected_fitness[e * 2] as u64;
            if r.fitness == want {
                fitness_ok += 1;
            } else {
                eprintln!("entry {e}: fitness {} != expected {want}", r.fitness);
            }
        }
    }

    let mut genome_ok = 0;
    for e in 0..entries {
        let got = get(&mut ddr, pop_off + (dest_base + e) * record_words * 4, record_words);
        let want = &expected_genomes[e * record_words..(e + 1) * record_words];
        if got == want {
            genome_ok += 1;
        } else if genome_ok == e {
            eprintln!("entry {e}: genome writeback differs");
        }
    }

    let cycles = telemetry.cycle_count as f64;
    let fabric_ms = cycles / (PL_CLOCK_MHZ * 1e3);

    println!();
    println!("fabric   {:>10.0} cycles   {fabric_ms:>8.3} ms   ({:.1} us/offspring)",
        cycles, fabric_ms * 1e3 / entries as f64);
    println!("wall     {wall:>19.3} ms   (includes staging + AXI-Lite polling)");
    println!("stalls   feed {} breeder {} | busy fill {} pack {} emit {}",
        telemetry.feed_stall_cycles, telemetry.breeder_stall_cycles,
        telemetry.fill_busy_cycles, telemetry.pack_busy_cycles, telemetry.emit_busy_cycles);
    println!("occupancy breeders {:.2} lanes {:.2} (mean busy per cycle)",
        telemetry.busy_breeder_cycles as f64 / cycles,
        telemetry.busy_lane_cycles as f64 / cycles);

    for b in 0..layout::N_BREEDERS {
        print!(" b{b} {:.0}%", 100.0 * device.breeder_busy(b).unwrap_or(0) as f64 / cycles);
    }
    println!();
    for l in 0..layout::N_LANES {
        print!(" l{l} {:.0}%", 100.0 * device.lane_busy(l).unwrap_or(0) as f64 / cycles);
    }
    println!();
    println!();
    println!("fitness  {fitness_ok}/{entries} bit-exact vs the software chain");
    println!("genomes  {genome_ok}/{entries} bit-exact writebacks");

    // Disarm before anyone unloads the bitstream: a master with writes in
    // flight at teardown permanently skews the PS HP0 pairing until reboot.
    device.publish(0).expect("disarm");
    device.await_idle(1_000_000).ok();

    if fitness_ok == entries && genome_ok == entries {
        println!("\nFIRST LIGHT OK");
    } else {
        println!("\nMISMATCH");
        exit(1);
    }
}
