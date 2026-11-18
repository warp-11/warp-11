//! The streaming generation loop, end to end, against the F# Sim or the
//! KV260, judged generation by generation by the software mirror:
//!
//!     gep_cluster_evolve <vector-dir> --sim [../hdl/Warp11.Gep/Warp11.Gep.fsproj]
//!     gep_cluster_evolve <vector-dir> --board <build-dir>                       # the KV260
//!     gep_cluster_evolve <vector-dir> --board <build-dir> --port /dev/ttyUSB0   # the Icepi
//!
//! The build directory — or the seam file out of it — is all a board needs
//! naming by: it says how the build is reached and where its regions are.
//!
//! The vector directory is written by `dotnet run --project Warp11.Gep --
//! streamvector <dir> <gens> [icepi]` — for the shape that was built: the population, the cases, the policy, and
//! for every generation the op-list the fabric must emit, the offspring it
//! must write back and their fitness. A pass here means the fabric's
//! selection, the host's marshal and the fabric's breeding agreed with the
//! mirror for every generation — the whole loop, bit-exact.

use std::fs::File;
use std::io::Read;
use std::process::exit;
use std::time::Duration;
use warp11_host::gep_evolve::{connect, device, evolve, holds, Generation, Policy, CONNECT_USAGE};
use warp11_host::regions::Regions;
use warp11_runtime::gep::{BreedRates, GepClusterDevice};
use warp11_runtime::RegisterWindow;

fn read_words(dir: &str, name: &str) -> Vec<u32> {
    let mut bytes = Vec::new();
    File::open(format!("{dir}/{name}"))
        .unwrap_or_else(|e| {
            eprintln!("cannot open {dir}/{name}: {e}");
            exit(2);
        })
        .read_to_end(&mut bytes)
        .expect("read");
    bytes.chunks(4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])).collect()
}

fn manifest_value(dir: &str, key: &str) -> usize {
    let text = std::fs::read_to_string(format!("{dir}/manifest.txt")).unwrap_or_else(|e| {
        eprintln!("cannot read {dir}/manifest.txt: {e}");
        exit(2);
    });
    text.lines()
        .find_map(|l| l.strip_prefix(&format!("{key} ")).and_then(|v| v.trim().parse().ok()))
        .unwrap_or_else(|| {
            eprintln!("manifest has no '{key}'");
            exit(2);
        })
}

fn fitness_words(words: &[u32]) -> Vec<u64> {
    words.chunks(2).map(|w| (w[1] as u64) << 32 | w[0] as u64).collect()
}

/// Judge every generation against the vector, print the verdict and the
/// timings, and say whether all of it held.
fn judge(dir: &str, pop: usize, record_words: usize, fabric_mhz: f64, initial: &[u64], gens: &[Generation]) -> bool {
    let record = |words: &[u32], e: usize| words[e * record_words..(e + 1) * record_words].to_vec();
    let expected0 = fitness_words(&read_words(dir, "fitness_0.bin"));
    let score_ok = initial.iter().zip(&expected0).filter(|(a, b)| a == b).count();
    println!("score round: {score_ok}/{pop} fitness bit-exact");
    let mut ok = score_ok == pop;

    for (g, gen) in gens.iter().enumerate() {
        let n = g + 1;
        let oplist = read_words(dir, &format!("oplist_{n}.bin"));
        let genomes = read_words(dir, &format!("genomes_{n}.bin"));
        let fitness = fitness_words(&read_words(dir, &format!("fitness_{n}.bin")));
        let entries_ok = (0..pop).filter(|&e| record(&gen.oplist, e) == record(&oplist, e)).count();
        let genomes_ok = (0..pop).filter(|&e| record(&gen.genomes, e) == record(&genomes, e)).count();
        let fitness_ok = gen.fitness.iter().zip(&fitness).filter(|(a, b)| a == b).count();
        let all = entries_ok == pop && genomes_ok == pop && fitness_ok == pop;
        ok &= all;
        println!(
            "gen {n}: op-list {entries_ok}/{pop}, genomes {genomes_ok}/{pop}, fitness {fitness_ok}/{pop}  |  emit {:.1} ms, marshal {:.2} ms, breed {:.1} ms ({} fabric cycles, {:.1} us/offspring at {fabric_mhz:.3} MHz), harvest {:.2} ms",
            gen.emit.as_secs_f64() * 1e3,
            gen.marshal.as_secs_f64() * 1e3,
            gen.breed.as_secs_f64() * 1e3,
            gen.breed_cycles,
            gen.breed_cycles as f64 / fabric_mhz / pop as f64,
            gen.harvest.as_secs_f64() * 1e3
        );
        if !all {
            if let Some(e) = (0..pop).find(|&e| gen.fitness[e] != fitness[e]) {
                eprintln!("  entry {e}: fitness {} != expected {}", gen.fitness[e], fitness[e]);
            }
            if let Some(e) = (0..pop).find(|&e| record(&gen.oplist, e) != record(&oplist, e)) {
                eprintln!("  entry {e}: op-list  {:x?}", record(&gen.oplist, e));
                eprintln!("  expected          {:x?}", record(&oplist, e));
            }
        }
    }
    ok
}

fn run<W: RegisterWindow>(dir: &str, device: &mut GepClusterDevice<W>, regions: &mut dyn Regions) -> bool
where
    W::Error: std::fmt::Debug,
{
    let pop = manifest_value(dir, "pop");
    let gens = manifest_value(dir, "gens");
    let var_count = manifest_value(dir, "var_count");
    let n_cases = manifest_value(dir, "n_cases");
    let population = read_words(dir, "population.bin");
    let cases = read_words(dir, "cases.bin");
    let rates = read_words(dir, "rates.bin");
    let seed = read_words(dir, "seed.bin");
    let record_words = device.seam().record_words();
    let fabric_mhz = device.seam().fabric_hz as f64 / 1e6;
    assert_eq!(population.len(), pop * record_words, "population.bin is {pop} records");
    assert_eq!(var_count, device.seam().var_count, "the vector and the build disagree about the variables");
    let policy = Policy {
        rates: BreedRates { words: [rates[0], rates[1], rates[2], rates[3], rates[4]], sigma_fx: rates[5], range_fx: rates[6] },
        seed: [seed[0], seed[1], seed[2], seed[3]],
        cases: (0..n_cases).map(|k| &cases[k * (var_count + 1)..(k + 1) * (var_count + 1)]).collect(),
    };

    match evolve(device, regions, &policy, &population, gens, Duration::from_secs(600)) {
        Ok((initial, generations)) => judge(dir, pop, record_words, fabric_mhz, &initial, &generations),
        Err(e) => {
            eprintln!("the loop failed: {e:?}");
            false
        }
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(dir) = args.first().filter(|a| !a.starts_with("--")) else {
        eprintln!("usage: gep_cluster_evolve <vector-dir> {CONNECT_USAGE}");
        exit(2);
    };
    let pop = manifest_value(dir, "pop");
    let fail = |e: String| -> ! {
        eprintln!("{e}");
        exit(1)
    };

    let mut connection = connect(&args[1..]).unwrap_or_else(|e| fail(e));
    let mut device = device(&connection).unwrap_or_else(|e| fail(e));
    holds(connection.regions.as_ref(), pop).unwrap_or_else(|e| fail(e));
    println!(
        "{}: {} breeders x {} lanes at {:.3} MHz, population {pop}",
        connection.seam.source,
        device.seam().n_breeders,
        device.seam().n_lanes,
        device.seam().fabric_hz as f64 / 1e6
    );

    let ok = run(dir, &mut device, connection.regions.as_mut());
    if let Err(e) = connection.close() {
        eprintln!("disarm failed: {e} — do not unload the app until it is disarmed");
        exit(1);
    }

    if ok {
        println!("\nSTREAMING LOOP OK");
    } else {
        println!("\nMISMATCH");
        exit(1);
    }
}
