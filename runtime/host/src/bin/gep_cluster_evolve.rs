//! The streaming generation loop, end to end, against the F# Sim or the
//! KV260, judged generation by generation by the software mirror:
//!
//!     gep_cluster_evolve <vector-dir> --sim [../hdl/Warp11.Gep/Warp11.Gep.fsproj]
//!     gep_cluster_evolve <vector-dir>              # on the board: uio node + udmabuf
//!     gep_cluster_evolve <vector-dir> /dev/mem     # root mode
//!
//! The vector directory is written by `dotnet run --project Warp11.Gep --
//! streamvector <dir> <gens>`: the population, the cases, the policy, and
//! for every generation the op-list the fabric must emit, the offspring it
//! must write back and their fitness. A pass here means the fabric's
//! selection, the host's marshal and the fabric's breeding agreed with the
//! mirror for every generation — the whole loop, bit-exact.

use std::fs::{File, OpenOptions};
use std::io::Read;
use std::os::unix::fs::OpenOptionsExt;
use std::path::PathBuf;
use std::process::exit;
use warp11_host::fs_sim_window::FsSimWindow;
use warp11_host::gep_evolve::{board, evolve, sim, Arena, Ddr, Generation, Policy};
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_host::udmabuf;
use warp11_runtime::gep::{BreedRates, GepClusterDevice};
use warp11_runtime::gep_layout as layout;
use warp11_runtime::RegisterWindow;

const AXI_BASE: i64 = 0xB000_0000;
const PL_CLOCK_MHZ: f64 = 99.999_001;

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

fn record(words: &[u32], e: usize) -> &[u32] {
    &words[e * layout::RECORD_WORDS..(e + 1) * layout::RECORD_WORDS]
}

/// Judge every generation against the vector, print the verdict and the
/// timings, and say whether all of it held.
fn judge(dir: &str, pop: usize, initial: &[u64], gens: &[Generation]) -> bool {
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
            "gen {n}: op-list {entries_ok}/{pop}, genomes {genomes_ok}/{pop}, fitness {fitness_ok}/{pop}  |  emit {:.1} ms, marshal {:.2} ms, breed {:.1} ms ({} fabric cycles, {:.1} us/offspring at {PL_CLOCK_MHZ} MHz), harvest {:.2} ms",
            gen.emit.as_secs_f64() * 1e3,
            gen.marshal.as_secs_f64() * 1e3,
            gen.breed.as_secs_f64() * 1e3,
            gen.breed_cycles,
            gen.breed_cycles as f64 / PL_CLOCK_MHZ / pop as f64,
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

fn run<W: RegisterWindow, D: Ddr>(dir: &str, device: &mut GepClusterDevice<W>, ddr: &mut D, arena: Arena, polls: u32) -> bool
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
    assert_eq!(population.len(), pop * layout::RECORD_WORDS, "population.bin is {pop} records");
    let policy = Policy {
        rates: BreedRates { words: [rates[0], rates[1], rates[2], rates[3], rates[4]], sigma_fx: rates[5], range_fx: rates[6] },
        seed: [seed[0], seed[1], seed[2], seed[3]],
        cases: (0..n_cases).map(|k| &cases[k * (var_count + 1)..(k + 1) * (var_count + 1)]).collect(),
    };

    match evolve(device, ddr, arena, &policy, &population, gens, polls) {
        Ok((initial, generations)) => judge(dir, pop, &initial, &generations),
        Err(e) => {
            eprintln!("the loop failed: {e:?}");
            false
        }
    }
}

fn main() {
    let mut args = std::env::args().skip(1);
    let dir = args.next().unwrap_or_else(|| {
        eprintln!("usage: gep_cluster_evolve <vector-dir> [--sim [fsproj] | /dev/mem]");
        exit(2);
    });
    let mode = args.next().unwrap_or_else(|| "auto".into());
    let pop = manifest_value(&dir, "pop");

    let ok = if mode == "--sim" {
        let fsproj = args.next().map(PathBuf::from).unwrap_or_else(|| PathBuf::from("../hdl/Warp11.Gep/Warp11.Gep.fsproj"));
        let window = FsSimWindow::spawn_with(&fsproj, &["simserve"], "GEPSERVE").unwrap_or_else(|e| {
            eprintln!("simserve did not come up: {e:?}");
            exit(1);
        });
        let shared = sim::Shared(std::rc::Rc::new(std::cell::RefCell::new(window)));
        let mut device = GepClusterDevice::open(shared.clone()).unwrap_or_else(|e| {
            eprintln!("the Sim's cluster did not answer with its identity: {e:?}");
            exit(1);
        });
        let mut ddr = shared;
        let arena = Arena::for_population(0, pop);
        println!("sim: {} breeders x {} lanes, population {pop}", layout::N_BREEDERS, layout::N_LANES);
        run(&dir, &mut device, &mut ddr, arena, 200_000)
    } else {
        let buf = udmabuf::Udmabuf::find("udmabuf-gep-cluster-axi").ok().flatten().unwrap_or_else(|| {
            eprintln!("no udmabuf for 'udmabuf-gep-cluster-axi' — is the app loaded and u-dma-buf up?");
            exit(1);
        });
        let buf_file = OpenOptions::new().read(true).write(true).custom_flags(O_SYNC).open(&buf.dev_path).expect("open udmabuf");
        let arena = Arena::for_population(buf.phys_addr as u32, pop);
        let needed = arena.bytes_needed(pop);
        assert!(needed <= buf.size, "the loop needs {needed} bytes and the arena is {}", buf.size);
        let mapping = MmapWindow::open(&buf_file, 0, needed.max(4096)).expect("mmap udmabuf");
        let mut ddr = board::Mapped(mapping);

        let reg_path = if mode == "auto" {
            udmabuf::find_uio("gep_cluster_axi").ok().flatten().unwrap_or_else(|| {
                eprintln!("no uio node named 'gep_cluster_axi' — pass /dev/mem to use root mode");
                exit(1);
            })
        } else {
            PathBuf::from(&mode)
        };
        let reg_file = OpenOptions::new().read(true).write(true).custom_flags(O_SYNC).open(&reg_path).expect("open register file");
        let offset = if reg_path.to_string_lossy() == "/dev/mem" { AXI_BASE } else { 0 };
        let window = MmapWindow::open(&reg_file, offset, layout::APERTURE_BYTES).expect("mmap registers");
        let mut device = GepClusterDevice::open(window).expect("the cluster answers with its identity");
        println!("board: {} breeders x {} lanes, population {pop}, arena at 0x{:x}", layout::N_BREEDERS, layout::N_LANES, buf.phys_addr);
        run(&dir, &mut device, &mut ddr, arena, 50_000_000)
    };

    if ok {
        println!("\nSTREAMING LOOP OK");
    } else {
        println!("\nMISMATCH");
        exit(1);
    }
}
