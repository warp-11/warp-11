//! The GoL board daemon: the world's live face on the Zenoh mesh.
//! Runs on the KV260 after the world's app is loaded (`xmutil loadapp
//! life64x64-batch`), listens as a Zenoh peer, and speaks the two key spaces
//! the view has always known:
//!
//!   warp11/gol/frame            published at ~30 Hz, CongestionControl::Drop
//!                               (keep-latest on the wire — the same policy
//!                               the fabric's triple buffer applies):
//!                               [generation u32 LE][population u32 LE]
//!                               [rows × 8 bytes: each row a u64 LE, bit x = cell x]
//!   warp11/gol/ctl/load         payload = rows × 8 bytes; seeds the world
//!   warp11/gol/ctl/run          payload = [gens_per_sec u32 LE]; a burst that
//!                               runs until `stop`, paced IN FABRIC
//!   warp11/gol/ctl/burst        payload = [count u32 LE][gens_per_sec u32 LE]
//!   warp11/gol/ctl/stop         no payload
//!   warp11/gol/ctl/reset        no payload; empties the world, zeroes the generation
//!
//! It stands on the generated design's own contract and nothing hand-written.
//! The seam the build directory wrote (`--layout`) names every register and
//! the world's shape; the batch contract (`BatchDevice`) carries the seed rows
//! through the arena the app's overlay made; the surface's snapshot registers
//! hand over the triple buffer's slots. The pacing lives in the fabric —
//! `tick`, `tickCount`, `intervalCycles`, `stop` — so the daemon only
//! forwards control writes, captures and publishes.
//!
//!   gol-daemon --board life64x64_batch --layout life64x64_batch_layout.rs [--listen tcp/0.0.0.0:7447]
//!   gol-daemon --board … --layout … disarm
//!
//! `disarm` stops the world and points its write master at nothing, which is
//! the step before `xmutil unloadapp`: tearing down a bitstream with writes in
//! flight permanently skews the PS-side HP0 AW/W pairing, cured only by a
//! reboot.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::process::exit;
use std::sync::mpsc;
use std::time::Duration;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_host::seam::{read_const, read_layout, resolve};
use warp11_host::udmabuf::{find_uio, Udmabuf};
use warp11_runtime::batch::{BatchDevice, BatchError};
use warp11_runtime::RegisterWindow;
use zenoh::qos::CongestionControl;
use zenoh::Wait;

const FRAME_PERIOD: Duration = Duration::from_millis(33);
/// The host memory bus's beat, in bytes: the KV260's HP port is 128 bits wide,
/// and the generated top packs rows into beats of it.
const BEAT_BYTES: usize = 16;
/// A lane is one 32-bit word of a row.
const LANE_BYTES: usize = 4;
/// The triple buffer: three slots, as the surface's snapshot path builds it.
const SLOTS: usize = 3;

fn fail(message: impl std::fmt::Display) -> ! {
    eprintln!("{message}");
    exit(1)
}

fn usage() -> ! {
    fail("usage: gol-daemon --board <uio name> --layout <seam.rs> [--listen tcp/0.0.0.0:7447] [disarm]")
}

struct Args {
    board: String,
    layout: PathBuf,
    listen: String,
    disarm: bool,
}

fn parse_args() -> Args {
    let mut board = None;
    let mut layout = None;
    let mut listen = "tcp/0.0.0.0:7447".to_string();
    let mut disarm = false;
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--board" => board = args.next(),
            "--layout" => layout = args.next().map(PathBuf::from),
            "--listen" => listen = args.next().unwrap_or_else(|| usage()),
            "disarm" => disarm = true,
            _ => usage(),
        }
    }
    Args {
        board: board.unwrap_or_else(|| usage()),
        layout: layout.unwrap_or_else(|| usage()),
        listen,
        disarm,
    }
}

/// The world's registers, resolved from the seam once: the design's own
/// controls by the names `World.fs` declares, and the surface's snapshot
/// registers by the surface's name plus the carrier's suffix.
struct Regs {
    tick: usize,
    tick_bit: u32,
    tick_count: usize,
    interval_cycles: usize,
    stop: usize,
    stop_bit: u32,
    reset: usize,
    reset_bit: u32,
    generation: usize,
    population: usize,
    population_shift: u32,
    population_mask: u32,
    mem_status: usize,
    ready_mask: u32,
    slot_shift: u32,
    slot_mask: u32,
    mem_base: usize,
    mem_capture: usize,
    capture_bit: u32,
    mem_release: usize,
    release_bit: u32,
}

/// The world's shape, from the seam: rows, words a row, and how the generated
/// top lays rows in beats — a row padded to a power of two lanes, rows tiling
/// a beat evenly — so the daemon reads a slot the way the fabric wrote it.
struct World {
    rows: usize,
    width: usize,
    rows_per_beat: usize,
    bytes_per_row: usize,
    frame_bytes: usize,
    slot_bytes: usize,
    fabric_hz: u64,
}

impl World {
    /// Where row `y` sits in a frame of beats.
    fn row_offset(&self, y: usize) -> usize {
        (y / self.rows_per_beat) * BEAT_BYTES + (y % self.rows_per_beat) * self.bytes_per_row
    }

    fn row_mask(&self) -> u64 {
        if self.width >= 64 {
            u64::MAX
        } else {
            (1u64 << self.width) - 1
        }
    }
}

fn constant(layout: &Path, name: &str) -> u32 {
    match read_const(layout, name) {
        Ok(Some(v)) => v,
        Ok(None) => fail(format!("{}: no constant {name} — not a world's seam?", layout.display())),
        Err(e) => fail(e),
    }
}

fn constant_or(layout: &Path, name: &str, default: u32) -> u32 {
    match read_const(layout, name) {
        Ok(Some(v)) => v,
        Ok(None) => default,
        Err(e) => fail(e),
    }
}

/// The seam read into the registers and the shape. The surface is whichever
/// need has snapshot registers — the one ending in `_MEM_CAPTURE` — so the
/// daemon serves a world by any name.
fn read_seam(layout: &Path) -> (Regs, World, Option<u32>) {
    let (registers, hash) = read_layout(layout).unwrap_or_else(|e| fail(e));
    let offset = |name: &str| resolve(name, &registers).unwrap_or_else(|e| fail(e));
    let surface = registers
        .iter()
        .find_map(|(r, _)| r.strip_suffix("_MEM_CAPTURE").map(str::to_string))
        .unwrap_or_else(|| fail(format!("{}: no surface in host memory — the world's top is `Life…Batch`", layout.display())));
    let s = |suffix: &str| format!("{surface}{suffix}");

    let regs = Regs {
        tick: offset("tick"),
        tick_bit: constant_or(layout, "TICK_BIT", 0),
        tick_count: offset("tickCount"),
        interval_cycles: offset("intervalCycles"),
        stop: offset("stop"),
        stop_bit: constant_or(layout, "STOP_BIT", 0),
        reset: offset("reset"),
        reset_bit: constant_or(layout, "RESET_BIT", 0),
        generation: offset("generation"),
        population: offset("population"),
        population_shift: constant_or(layout, "POPULATION_SHIFT", 0),
        population_mask: constant_or(layout, "POPULATION_MASK", u32::MAX),
        mem_status: offset(&s("_MEM_READY")),
        ready_mask: constant(layout, &s("_MEM_READY_MASK")),
        slot_shift: constant(layout, &s("_MEM_SLOT_SHIFT")),
        slot_mask: constant(layout, &s("_MEM_SLOT_MASK")),
        mem_base: offset(&s("_MEM_BASE")),
        mem_capture: offset(&s("_MEM_CAPTURE")),
        capture_bit: constant_or(layout, &s("_MEM_CAPTURE_BIT"), 0),
        mem_release: offset(&s("_MEM_RELEASE")),
        release_bit: constant_or(layout, &s("_MEM_RELEASE_BIT"), 0),
    };

    let rows = constant(layout, &s("_ROWS")) as usize;
    let words = constant(layout, &s("_WORDS")) as usize;
    let beats = constant(layout, &s("_MEM_BEATS")) as usize;
    let slot_bytes = constant(layout, &s("_MEM_SLOT_BYTES")) as usize;
    let fabric_hz = u64::from(constant(layout, "FABRIC_HZ"));
    if words > 2 {
        fail(format!("a row of {words} words does not fit the u64 a frame carries a row in"));
    }
    let lanes_per_beat = BEAT_BYTES / LANE_BYTES;
    let lanes_per_row = words.next_power_of_two();
    let world = World {
        rows,
        width: words * 32,
        rows_per_beat: lanes_per_beat / lanes_per_row,
        bytes_per_row: lanes_per_row * LANE_BYTES,
        frame_bytes: beats * BEAT_BYTES,
        slot_bytes,
        fabric_hz,
    };
    (regs, world, hash)
}

/// gens/second → the fabric interval that paces them. 0 means as fast as the
/// fabric goes: one generation a cycle.
fn interval_for(world: &World, gens_per_sec: u32) -> u32 {
    if gens_per_sec == 0 {
        1
    } else {
        (world.fabric_hz / u64::from(gens_per_sec)).clamp(1, u64::from(u32::MAX)) as u32
    }
}

enum Control {
    Load(Vec<u64>),
    Run { gens_per_sec: u32 },
    Burst { count: u32, gens_per_sec: u32 },
    Stop,
    Reset,
}

/// B3/S23 with a dead border — the canary's software twin (the same
/// carry-save network as gol-engine), at the world's width.
fn life_step(world: &World, rows: &[u64]) -> Vec<u64> {
    let ha = |a: u64, b: u64| (a ^ b, a & b);
    let fa = |a: u64, b: u64, c: u64| {
        let s = a ^ b;
        (s ^ c, (a & b) | (c & s))
    };
    let mask = world.row_mask();
    let n = rows.len();
    (0..n)
        .map(|y| {
            let u = if y > 0 { rows[y - 1] } else { 0 };
            let s = rows[y];
            let d = if y + 1 < n { rows[y + 1] } else { 0 };
            let (us, uc) = fa((u << 1) & mask, u, u >> 1);
            let (ds, dc) = fa((d << 1) & mask, d, d >> 1);
            let (ss, sc) = ha((s << 1) & mask, s >> 1);
            let (n0, c1) = fa(us, ds, ss);
            let (t, c2) = fa(uc, dc, sc);
            let (n1, c2b) = ha(t, c1);
            let (n2, n3) = ha(c2, c2b);
            !n2 & !n3 & n1 & (n0 | s) & mask
        })
        .collect()
}

/// The accelerator as the daemon holds it: the batch device over the
/// registers, the arena the rows and the slots live in, and where each is.
struct Fabric {
    device: BatchDevice<MmapWindow>,
    arena: Udmabuf,
    arena_map: MmapWindow,
    /// The seed rows' half of the arena, at its start.
    seed_bytes: usize,
    /// The slots' half, where the fabric writes snapshots.
    slots_at: usize,
    regs: Regs,
    world: World,
}

impl Fabric {
    fn rd(&mut self, offset: usize) -> u32 {
        self.device.window_mut().read32(offset).unwrap_or_else(|e| fail(format!("register read at 0x{offset:x}: {e:?}")))
    }

    fn wr(&mut self, offset: usize, value: u32) {
        self.device.window_mut().write32(offset, value).unwrap_or_else(|e| fail(format!("register write at 0x{offset:x}: {e:?}")))
    }

    fn pulse(&mut self, offset: usize, bit: u32) {
        self.wr(offset, 1 << bit)
    }

    fn stop(&mut self) {
        let (o, b) = (self.regs.stop, self.regs.stop_bit);
        self.pulse(o, b)
    }

    /// The seed through the batch contract: rows into the arena the way the
    /// fabric reads them, then one batch of them. The contract reads whole
    /// bursts, so a world shorter than a burst is sent repeated — every copy
    /// loads the same world, and the last one is what stands.
    fn load(&mut self, rows: &[u64]) {
        self.stop();
        let rows_per_burst = self.world.rows_per_beat * 16;
        let sent = rows.len().div_ceil(rows_per_burst) * rows_per_burst;
        let bytes = sent.div_ceil(self.world.rows_per_beat) * BEAT_BYTES;
        if bytes > self.seed_bytes {
            fail(format!("{sent} seed rows need {bytes} bytes; the arena's half is {}", self.seed_bytes));
        }
        let region = &mut self.arena_map.bytes_mut()[..bytes];
        region.fill(0);
        for y in 0..sent {
            let at = self.world.row_offset(y);
            let row = rows[y % rows.len()] & self.world.row_mask();
            region[at..at + 8].copy_from_slice(&row.to_le_bytes());
        }
        self.arena.sync_for_device(0, bytes).unwrap_or_else(|e| fail(format!("sync_for_device: {e}")));
        let src = self.arena.phys_addr as u32;
        self.device.start(src, src, sent as u32).unwrap_or_else(|e| fail(format!("start: {e:?}")));
        if let Err(e) = self.device.wait_done(10_000_000, |_| Ok(())) {
            fail(format!("the seed never landed: {}", describe(e)));
        }
        self.device.acknowledge().unwrap_or_else(|e| fail(format!("acknowledge: {e:?}")));
    }

    fn burst(&mut self, count: u32, gens_per_sec: u32) {
        self.stop();
        let interval = interval_for(&self.world, gens_per_sec);
        let (ic, tc, tick, bit) = (self.regs.interval_cycles, self.regs.tick_count, self.regs.tick, self.regs.tick_bit);
        self.wr(ic, interval);
        self.wr(tc, count);
        self.pulse(tick, bit);
    }

    fn reset(&mut self) {
        self.stop();
        let (o, b) = (self.regs.reset, self.regs.reset_bit);
        self.pulse(o, b)
    }

    /// The freshest completed frame, if the fabric grants one within the
    /// poll bound: the rows, the generation and the population it belongs
    /// to. The slot is released once read.
    fn capture(&mut self) -> Option<(Vec<u64>, u32, u32)> {
        let (cap, cap_bit) = (self.regs.mem_capture, self.regs.capture_bit);
        self.pulse(cap, cap_bit);
        let status_at = self.regs.mem_status;
        let mut status = 0;
        for _ in 0..100_000 {
            status = self.rd(status_at);
            if status & self.regs.ready_mask != 0 {
                break;
            }
        }
        if status & self.regs.ready_mask == 0 {
            return None;
        }
        let slot = ((status & self.regs.slot_mask) >> self.regs.slot_shift) as usize;
        let at = self.slots_at + slot * self.world.slot_bytes;
        self.arena
            .sync_for_cpu(at, self.world.frame_bytes)
            .unwrap_or_else(|e| fail(format!("sync_for_cpu: {e}")));
        let frame = &self.arena_map.bytes()[at..at + self.world.frame_bytes];
        let rows: Vec<u64> = (0..self.world.rows)
            .map(|y| {
                let o = self.world.row_offset(y);
                u64::from_le_bytes(frame[o..o + 8].try_into().unwrap()) & self.world.row_mask()
            })
            .collect();
        let generation = self.rd(self.regs.generation);
        let population = (self.rd(self.regs.population) & self.regs.population_mask) >> self.regs.population_shift;
        let (rel, rel_bit) = (self.regs.mem_release, self.regs.release_bit);
        self.pulse(rel, rel_bit);
        Some((rows, generation, population))
    }

    /// A known soup round-tripped through the whole write path before any
    /// client is served. Tearing down a bitstream with writes in flight
    /// permanently skews the PS-side HP0 AW/W pairing — every frame then
    /// arrives rotated by the in-flight burst count, reload-proof, cured only
    /// by reboot. The canary turns that silent corruption into a loud refusal
    /// at the earliest possible moment.
    fn skew_canary(&mut self) -> Result<(), String> {
        let mut seed = 0x9E37_79B9_7F4A_7C15u64;
        let soup: Vec<u64> = (0..self.world.rows)
            .map(|_| {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                seed & self.world.row_mask()
            })
            .collect();
        self.load(&soup);
        self.burst(96, 0);
        std::thread::sleep(Duration::from_millis(2)); // 96 generations at a cycle each, and a settled frame

        let (rows, generation, _) = self.capture().ok_or("snapshot capture never granted")?;
        let twin = (0..96).fold(soup, |g, _| life_step(&self.world, &g));
        let mismatches = twin.iter().zip(&rows).filter(|(a, b)| a != b).count();

        // Leave an empty world for the first client.
        self.reset();

        if generation != 96 {
            Err(format!("the burst ran {generation} generations, not 96"))
        } else if mismatches != 0 {
            Err(format!(
                "{mismatches}/{} rows wrong (rotated frame): the HP write path is skewed — a \
                 previous session was torn down with writes in flight. REBOOT the board; then \
                 always `gol-daemon … disarm` before `xmutil unloadapp`.",
                self.world.rows
            ))
        } else {
            Ok(())
        }
    }
}

fn describe<E: std::fmt::Debug>(e: BatchError<E>) -> String {
    match e {
        BatchError::WrongId { found } => format!("not a batch top: ID reads 0x{found:08X} — wrong bitstream?"),
        BatchError::WrongLayout { found, expected } => {
            format!("layout mismatch: the fabric says 0x{found:04X}, the seam 0x{expected:04X} — rebuild the bitstream or copy the seam the build wrote")
        }
        BatchError::NeverFinished => "busy never cleared".into(),
        BatchError::Window(e) => format!("{e:?}"),
    }
}

fn open_fabric(args: &Args) -> Fabric {
    let (regs, world, hash) = read_seam(&args.layout);

    let uio = find_uio(&args.board)
        .unwrap_or_else(|e| fail(e))
        .unwrap_or_else(|| fail(format!("no uio device called '{}' — is the app loaded?", args.board)));
    let arena_name = format!("udmabuf-{}", args.board.replace('_', "-"));
    let arena = Udmabuf::find(&arena_name)
        .unwrap_or_else(|e| fail(e))
        .unwrap_or_else(|| fail(format!("no u-dma-buf called '{arena_name}' — is the app loaded?")));

    let regs_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&uio)
        .unwrap_or_else(|e| fail(format!("{}: {e}", uio.display())));
    let window = MmapWindow::open(&regs_file, 0, 0x1000).unwrap_or_else(|e| fail(format!("register mmap: {e:?}")));
    let device = BatchDevice::open(window, hash).unwrap_or_else(|e| fail(describe(e)));

    let arena_file = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&arena.dev_path)
        .unwrap_or_else(|e| fail(format!("{}: {e}", arena.dev_path.display())));
    let arena_map = MmapWindow::open(&arena_file, 0, arena.size).unwrap_or_else(|e| fail(format!("arena mmap: {e:?}")));

    let half = arena.size / 2;
    if SLOTS * world.slot_bytes > half {
        fail(format!("{SLOTS} slots of {} bytes do not fit the arena's half of {half}", world.slot_bytes));
    }
    if arena.phys_addr + arena.size as u64 > u64::from(u32::MAX) {
        fail("the arena sits above 4 GB, which a 32-bit master cannot reach");
    }

    Fabric {
        device,
        arena,
        arena_map,
        seed_bytes: half,
        slots_at: half,
        regs,
        world,
    }
}

fn main() {
    let args = parse_args();
    let mut fabric = open_fabric(&args);
    let slots_phys = (fabric.arena.phys_addr + fabric.slots_at as u64) as u32;

    if args.disarm {
        fabric.stop();
        let base = fabric.regs.mem_base;
        fabric.wr(base, 0);
        println!("disarmed: the world stopped, its master pointed at nothing — safe to `xmutil unloadapp`");
        return;
    }

    // Arm the surface's write master at the slots. From here until `disarm`
    // the fabric may write DDR whenever a snapshot is captured.
    let base = fabric.regs.mem_base;
    fabric.wr(base, slots_phys);

    if let Err(reason) = fabric.skew_canary() {
        fail(format!("skew canary FAILED: {reason}"));
    }
    println!(
        "skew canary ok: a {}×{} soup round-tripped bit-exact through DDR at {} Hz",
        fabric.world.width, fabric.world.rows, fabric.world.fabric_hz
    );

    // ---- the mesh ----
    let mut config = zenoh::Config::default();
    config
        .insert_json5("listen/endpoints", &format!("[\"{}\"]", args.listen))
        .expect("listen endpoint");
    let session = zenoh::open(config).wait().unwrap_or_else(|e| fail(format!("zenoh open failed: {e}")));
    let frames = session
        .declare_publisher("warp11/gol/frame")
        .congestion_control(CongestionControl::Drop)
        .wait()
        .expect("frame publisher");

    // Control arrives on a Zenoh thread; the fabric stays owned by the main
    // loop, so commands cross over a channel.
    let row_bytes = fabric.world.rows * 8;
    let (control_send, control_recv) = mpsc::channel::<Control>();
    let _ctl = session
        .declare_subscriber("warp11/gol/ctl/*")
        .callback(move |sample| {
            let key = sample.key_expr().as_str().to_string();
            let payload = sample.payload().to_bytes().into_owned();
            let word = |at: usize| u32::from_le_bytes(payload[at..at + 4].try_into().unwrap());
            let parsed = match key.rsplit('/').next() {
                Some("load") if payload.len() == row_bytes => Some(Control::Load(
                    payload.chunks_exact(8).map(|c| u64::from_le_bytes(c.try_into().unwrap())).collect(),
                )),
                Some("run") if payload.len() >= 4 => Some(Control::Run { gens_per_sec: word(0) }),
                Some("burst") if payload.len() >= 8 => Some(Control::Burst { count: word(0), gens_per_sec: word(4) }),
                Some("stop") => Some(Control::Stop),
                Some("reset") => Some(Control::Reset),
                _ => None,
            };
            match parsed {
                Some(c) => {
                    let _ = control_send.send(c);
                }
                None => eprintln!("ignoring malformed control on {key} ({} bytes)", payload.len()),
            }
        })
        .wait()
        .expect("control subscriber");

    println!(
        "gol-daemon up: {} on {}, slots at 0x{slots_phys:08X}, listening on {}",
        args.board,
        uio_name(&args.layout),
        args.listen
    );

    // ---- the loop: apply control, capture, publish ----
    loop {
        while let Ok(control) = control_recv.try_recv() {
            match control {
                Control::Load(rows) => fabric.load(&rows),
                Control::Run { gens_per_sec } => fabric.burst(0, gens_per_sec),
                Control::Burst { count, gens_per_sec } => fabric.burst(count, gens_per_sec),
                Control::Stop => fabric.stop(),
                Control::Reset => fabric.reset(),
            }
        }

        if let Some((rows, generation, population)) = fabric.capture() {
            let mut payload = Vec::with_capacity(8 + rows.len() * 8);
            payload.extend_from_slice(&generation.to_le_bytes());
            payload.extend_from_slice(&population.to_le_bytes());
            for row in rows {
                payload.extend_from_slice(&row.to_le_bytes());
            }
            let _ = frames.put(payload).wait();
        }

        std::thread::sleep(FRAME_PERIOD);
    }
}

/// The seam's file name, for the greeting.
fn uio_name(layout: &Path) -> String {
    layout.file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_default()
}
