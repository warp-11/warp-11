//! The streaming generation loop's host half — the direction set 2026-07-24:
//! the host owns the population, the fabric never random-accesses DDR, and a
//! generation is three passes the host drives in turn:
//!
//! 1. **Emit.** Auto mode with the op-list base set: the fabric selects —
//!    binary tournament over its fitness table, the elite at index 0 — and
//!    streams one generation's pairing entries to the op-list ring instead
//!    of breeding them. Generation 0's pass scores the staged population
//!    first; later passes skip the score (`skip_score`, the table is there
//!    from the breed pass) and continue the selection stream
//!    (`rng_continue`).
//! 2. **Marshal.** Each entry followed by both parents' records, from the
//!    host's copy of the population ([`marshal_serial`]), into the queue.
//! 3. **Breed.** Queue mode: the fabric breeds, evaluates and writes every
//!    offspring back; the host reads the ring for their fitness and the
//!    other half of the population region for their genomes, which are the
//!    next generation.
//!
//! The fabric resets its region flag at every start, so the host keeps the
//! current population in the first half of the region and the offspring
//! land in the second, every generation. Written once over a
//! [`RegisterWindow`] and a [`Regions`] — what a `Connection` hands over —
//! so the same loop runs against the F# Sim, the KV260's memory and a board
//! that keeps its regions over a serial link.

use std::time::{Duration, Instant};
use warp11_runtime::gep::{marshal_serial, parse_results, BreedRates, GepClusterDevice, GepError, GepResult};
use std::path::Path;
use warp11_runtime::gep_seam::GepSeam;
use warp11_runtime::RegisterWindow;

use crate::connection::{Connection, Window};
use crate::regions::Regions;

/// The cluster's regions, as its design names them.
pub const QUEUE: &str = "queue";
pub const POPULATION: &str = "population";
pub const RING: &str = "ring";
pub const OPLIST: &str = "oplist";

/// How the cluster's tools say what to connect to, after their own
/// arguments.
pub const CONNECT_USAGE: &str = "(--board <build-dir> [--port <tty>] | --sim [fsproj])";

/// The cluster a tool was pointed at: a board by the build directory of
/// what is loaded on it, with the serial line where it is reached over one,
/// or the silicon shape in the Sim behind `Warp11.Gep -- simserve`.
pub fn connect(args: &[String]) -> Result<Connection, String> {
    let value = |flag: &str| args.iter().position(|a| a == flag).map(|at| args.get(at + 1).filter(|v| !v.starts_with("--")));
    match (value("--board"), value("--sim")) {
        (Some(Some(build)), None) => Connection::board(Path::new(build), value("--port").flatten().map(Path::new)),
        (None, Some(fsproj)) => {
            let fsproj = fsproj.map(String::as_str).unwrap_or("../hdl/Warp11.Gep/Warp11.Gep.fsproj");
            Connection::sim(Path::new(fsproj), &["simserve"], "GEPSERVE")
        }
        _ => Err(format!("say what to connect to: {CONNECT_USAGE}")),
    }
}

/// The cluster's driver over a connection, refusing a build of anything
/// else by the name its seam lacks.
pub fn device(connection: &Connection) -> Result<GepClusterDevice<Window>, String> {
    let seam = GepSeam::read(|name| connection.seam.number(name))
        .map_err(|name| format!("{}: no {name} — is this a build of the GEP cluster?", connection.seam.source))?;
    Ok(GepClusterDevice::new(connection.window.clone(), seam))
}

/// Whether a run of `pop` individuals fits the regions the build sized: a
/// queue item, a ring record and an op-list entry each, and both halves of
/// the population.
pub fn holds(regions: &dyn Regions, pop: usize) -> Result<(), String> {
    for (region, wanted) in [(QUEUE, pop), (POPULATION, 2 * pop), (RING, pop), (OPLIST, pop)] {
        let records = regions.records(region)?;
        if wanted > records {
            return Err(format!("a population of {pop} wants {wanted} records of '{region}', and the build gave it {records}"));
        }
    }
    Ok(())
}

/// The run's policy: the breeding rates, the selection seed, the cases.
pub struct Policy<'a> {
    pub rates: BreedRates,
    pub seed: [u32; 4],
    /// Each case as the fabric's row: the variables, then the target.
    pub cases: Vec<&'a [u32]>,
}

/// One generation as the fabric produced it.
#[derive(Clone, Debug, Default)]
pub struct Generation {
    /// The op-list the emit pass wrote: `pop` entries.
    pub oplist: Vec<u32>,
    /// Offspring fitness by entry id.
    pub fitness: Vec<u64>,
    /// Offspring genomes by entry id, a record each: the next population.
    pub genomes: Vec<u32>,
    pub emit: Duration,
    pub marshal: Duration,
    pub breed: Duration,
    pub harvest: Duration,
    /// Fabric cycles the breed pass took, from its own counter.
    pub breed_cycles: u32,
}

#[derive(Debug)]
pub enum EvolveError<W> {
    Device(GepError<W>),
    Regions(String),
    /// The ring came back with an entry id outside the population.
    StrayEntry(u32),
}

impl<W> From<GepError<W>> for EvolveError<W> {
    fn from(e: GepError<W>) -> Self {
        EvolveError::Device(e)
    }
}

/// Poll `done` until it answers true, serving the regions between polls —
/// which is where a link's requests are answered and a Sim advances.
/// `patience` is how long to keep at it.
fn await_with<W: RegisterWindow>(
    regions: &mut dyn Regions,
    patience: Duration,
    mut done: impl FnMut() -> Result<bool, W::Error>,
) -> Result<(), EvolveError<W::Error>> {
    let began = Instant::now();
    while began.elapsed() < patience {
        if done().map_err(|e| EvolveError::Device(GepError::Window(e)))? {
            return Ok(());
        }
        regions.serve().map_err(EvolveError::Regions)?;
    }
    Err(EvolveError::Device(GepError::Timeout))
}

/// Every offspring's fitness by entry id, from the ring.
fn fitness_by_entry<W: RegisterWindow>(
    device: &GepClusterDevice<W>,
    regions: &mut dyn Regions,
    pop: usize,
) -> Result<Vec<u64>, EvolveError<W::Error>> {
    let ring = regions.read(RING, 0, pop).map_err(EvolveError::Regions)?;
    let mut results = vec![GepResult { fitness: 0, entry_id: 0, seq: 0 }; pop];
    parse_results(device.seam(), &ring, &mut results);
    let mut fitness = vec![0u64; pop];
    for r in &results {
        match fitness.get_mut(r.entry_id as usize) {
            Some(slot) => *slot = r.fitness,
            None => return Err(EvolveError::StrayEntry(r.entry_id)),
        }
    }
    Ok(fitness)
}

/// The whole loop: `generations` of emit, marshal, breed, from `population`
/// (`pop` records), returning every generation's op-list, fitness and
/// genomes. Generation 0's score-round fitness of the staged population
/// comes back first, as `initial_fitness`.
pub fn evolve<W: RegisterWindow>(
    device: &mut GepClusterDevice<W>,
    regions: &mut dyn Regions,
    policy: &Policy,
    population: &[u32],
    generations: usize,
    patience: Duration,
) -> Result<(Vec<u64>, Vec<Generation>), EvolveError<W::Error>> {
    let pop = population.len() / device.seam().record_words();
    let mut current: Vec<u32> = population.to_vec();
    let mut initial_fitness = Vec::new();
    let mut out = Vec::with_capacity(generations);

    regions.write(POPULATION, 0, &current).map_err(EvolveError::Regions)?;

    for g in 0..generations {
        let mut gen = Generation::default();

        // ---- emit ----
        let t = Instant::now();
        device.set_auto_mode(true).map_err(GepError::Window)?;
        device.configure_auto(pop as u32, 1, &policy.rates, policy.seed).map_err(GepError::Window)?;
        device.set_pass_flags(g > 0, g > 0).map_err(GepError::Window)?;
        // The cases go in before the start: the emit pass scores the staged
        // population the moment it starts, and a case loaded after that is
        // a case the score never saw.
        if g == 0 {
            device.load_cases(&policy.cases).map_err(GepError::Window)?;
        }
        device.start(policy.cases.len() as u32).map_err(GepError::Window)?;
        await_with::<W>(regions, patience, || device.oplist_done())?;
        await_with::<W>(regions, patience, || device.all_idle())?;
        gen.oplist = regions.read(OPLIST, 0, pop).map_err(EvolveError::Regions)?;
        if g == 0 {
            initial_fitness = fitness_by_entry(device, regions, pop)?;
        }
        gen.emit = t.elapsed();

        // ---- marshal ----
        let t = Instant::now();
        let mut items = vec![0u32; pop * device.seam().work_item_words()];
        marshal_serial(device.seam(), &gen.oplist, pop, &current, &mut items);
        regions.write(QUEUE, 0, &items).map_err(EvolveError::Regions)?;
        gen.marshal = t.elapsed();

        // ---- breed ----
        let t = Instant::now();
        device.set_auto_mode(false).map_err(GepError::Window)?;
        device.start(policy.cases.len() as u32).map_err(GepError::Window)?;
        device.publish(pop as u32).map_err(GepError::Window)?;
        await_with::<W>(regions, patience, || Ok(device.results_done()? >= pop as u32))?;
        await_with::<W>(regions, patience, || device.all_idle())?;
        gen.breed_cycles = device.telemetry().map_err(GepError::Window)?.cycle_count;
        gen.breed = t.elapsed();

        // ---- harvest ----
        let t = Instant::now();
        gen.fitness = fitness_by_entry(device, regions, pop)?;
        gen.genomes = regions.read(POPULATION, pop, pop).map_err(EvolveError::Regions)?;
        gen.harvest = t.elapsed();

        // The offspring are the next generation: in the host's copy, and in
        // the first half of the region, which keeps the region truthful for
        // anything reading it even though the next emit pass, skipping the
        // score, reads nothing from it.
        current = gen.genomes.clone();
        regions.write(POPULATION, 0, &current).map_err(EvolveError::Regions)?;
        // No entries published between passes, so nothing is in flight at
        // teardown.
        device.publish(0).map_err(GepError::Window)?;
        out.push(gen);
    }

    Ok((initial_fitness, out))
}
