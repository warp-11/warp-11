//! First light for the F#-elaborated WarpCPU genetic-programming cluster:
//! breeders, DIV-resident lanes, fillers, inline parents — on whichever
//! board the build was for:
//!
//!     ./gep_cluster_first_light <vector-dir> --board <build-dir>                       # the KV260
//!     ./gep_cluster_first_light <vector-dir> --board <build-dir> --port /dev/ttyUSB0   # over a link
//!     ./gep_cluster_first_light <vector-dir> --sim
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

use std::fs::File;
use std::io::Read;
use std::process::exit;
use std::time::{Duration, Instant};
use warp11_host::gep_evolve::{connect, device as cluster, CONNECT_USAGE, POPULATION, QUEUE, RING};
use warp11_runtime::gep::GepResult;

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
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(dir) = args.first().filter(|a| !a.starts_with("--")).cloned() else {
        eprintln!("usage: gep_cluster_first_light <vector-dir> {CONNECT_USAGE}");
        exit(2);
    };
    let fail = |e: String| -> ! {
        eprintln!("{e}");
        exit(1)
    };

    let entries = manifest_value(&dir, "entries");
    let n_cases = manifest_value(&dir, "n_cases");
    let var_count = manifest_value(&dir, "var_count");
    let dest_base = manifest_value(&dir, "dest_base");
    let record_words = manifest_value(&dir, "record_words");
    // The vector's items are on the stride the host's memory wants; a
    // region takes the item alone, and its carrier pads it where it must.
    let item_stride_words = manifest_value(&dir, "work_item_words");

    let work_items = read_words(&dir, "workitems.bin");
    let population = read_words(&dir, "population.bin");
    let cases = read_words(&dir, "cases.bin");
    let expected_genomes = read_words(&dir, "expected_genomes.bin");
    let expected_fitness = read_words(&dir, "expected_fitness.bin");

    let mut connection = connect(&args[1..]).unwrap_or_else(|e| fail(e));
    let mut device = cluster(&connection).unwrap_or_else(|e| fail(e));
    let seam = device.seam().clone();
    let fabric_mhz = seam.fabric_hz as f64 / 1e6;
    assert_eq!(record_words, seam.record_words(), "vector/build disagree on a record");
    assert!(item_stride_words >= seam.work_item_words(), "vector/build disagree on a work item");
    let regions = connection.regions.as_mut();

    let items: Vec<u32> = work_items.chunks(item_stride_words).flat_map(|item| item[..seam.work_item_words()].to_vec()).collect();
    regions.write(QUEUE, 0, &items).unwrap_or_else(|e| fail(e));
    regions.write(POPULATION, 0, &population).unwrap_or_else(|e| fail(e));
    // Sentinel the destination slots so a missing writeback is visible rather
    // than passing on whatever was already there.
    let sentinel: Vec<u32> = (0..entries * record_words).map(|i| 0x5EAD_0000 | (i % record_words) as u32).collect();
    regions.write(POPULATION, dest_base, &sentinel).unwrap_or_else(|e| fail(e));

    println!(
        "{}: {} breeders x {} lanes at {fabric_mhz:.3} MHz, {entries} entries, {n_cases} cases",
        connection.seam.source, seam.n_breeders, seam.n_lanes
    );

    device.set_auto_mode(false).unwrap_or_else(|e| fail(e));

    // Cases before start: start zeroes the cycle counter, so loading them after
    // it put ~5,800 cycles of host register writes into every rate printed below.
    let rows: Vec<&[u32]> = (0..n_cases)
        .map(|k| &cases[k * (var_count + 1)..(k + 1) * (var_count + 1)])
        .collect();
    device.load_cases(&rows).unwrap_or_else(|e| fail(e));
    device.start(n_cases as u32).unwrap_or_else(|e| fail(e));

    let started = Instant::now();
    device.publish(entries as u32).unwrap_or_else(|e| fail(e));
    let patience = Duration::from_secs(600);
    let mut settled = false;
    while !settled && started.elapsed() < patience {
        settled = device.results_done().unwrap_or_else(|e| fail(e)) >= entries as u32 && device.all_idle().unwrap_or_else(|e| fail(e));
        regions.serve().unwrap_or_else(|e| fail(e));
    }
    if !settled {
        eprintln!("timed out at results_done = {:?}", device.results_done());
        exit(1);
    }
    let wall = started.elapsed().as_secs_f64() * 1e3;

    let telemetry = device.telemetry().unwrap_or_else(|e| fail(e));

    // ---- the verdict ----
    let ring = regions.read(RING, 0, entries).unwrap_or_else(|e| fail(e));
    let mut results = vec![
        GepResult { fitness: 0, entry_id: 0, seq: 0 };
        entries
    ];
    warp11_runtime::gep::parse_results(&seam, &ring, &mut results);

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

    let written = regions.read(POPULATION, dest_base, entries).unwrap_or_else(|e| fail(e));
    let mut genome_ok = 0;
    for e in 0..entries {
        let got = &written[e * record_words..(e + 1) * record_words];
        let want = &expected_genomes[e * record_words..(e + 1) * record_words];
        if got == want {
            genome_ok += 1;
        } else if genome_ok == e {
            eprintln!("entry {e}: genome writeback differs");
        }
    }

    let cycles = telemetry.cycle_count as f64;
    let fabric_ms = cycles / (fabric_mhz * 1e3);

    println!();
    println!("fabric   {:>10.0} cycles   {fabric_ms:>8.3} ms   ({:.1} us/offspring)",
        cycles, fabric_ms * 1e3 / entries as f64);
    println!("wall     {wall:>19.3} ms   (includes the register polling)");
    println!("stalls   feed {} breeder {} | busy fill {} pack {} emit {}",
        telemetry.feed_stall_cycles, telemetry.breeder_stall_cycles,
        telemetry.fill_busy_cycles, telemetry.pack_busy_cycles, telemetry.emit_busy_cycles);
    println!("occupancy breeders {:.2} lanes {:.2} (mean busy per cycle)",
        telemetry.busy_breeder_cycles as f64 / cycles,
        telemetry.busy_lane_cycles as f64 / cycles);

    for b in 0..seam.n_breeders {
        print!(" b{b} {:.0}%", 100.0 * device.breeder_busy(b).unwrap_or(0) as f64 / cycles);
    }
    println!();
    for l in 0..seam.n_lanes {
        print!(" l{l} {:.0}%", 100.0 * device.lane_busy(l).unwrap_or(0) as f64 / cycles);
    }
    println!();
    println!();
    println!("fitness  {fitness_ok}/{entries} bit-exact vs the software chain");
    println!("genomes  {genome_ok}/{entries} bit-exact writebacks");

    // Nothing published and the regions disarmed before anyone unloads the
    // bitstream: a master with writes in flight at teardown permanently
    // skews the PS HP0 pairing until reboot.
    device.publish(0).unwrap_or_else(|e| fail(e));
    if let Err(e) = connection.close() {
        eprintln!("disarm failed: {e} — do not unload the app until it is disarmed");
        exit(1);
    }

    if fitness_ok == entries && genome_ok == entries {
        println!("\nFIRST LIGHT OK");
    } else {
        println!("\nMISMATCH");
        exit(1);
    }
}
