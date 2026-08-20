//! The GoL board daemon: the accelerator's live face on the Zenoh mesh.
//! Runs on the KV260 after `xmutil loadapp gol-fs`, listens as a Zenoh peer,
//! and speaks two key spaces:
//!
//!   warp11/gol/frame            published at ~30 Hz, CongestionControl::Drop
//!                               (keep-latest on the wire — the same policy
//!                               the fabric's conflate triple-buffer applies):
//!                               [generation u32 LE][population u32 LE]
//!                               [512 bytes: 64 rows of u64 LE, bit x = cell x]
//!   warp11/gol/ctl/load         payload = 512 bytes of rows; loads the grid
//!   warp11/gol/ctl/run          payload = [gens_per_sec u32 LE]; continuous
//!                               ticking, paced IN FABRIC by intervalCycles
//!   warp11/gol/ctl/burst        payload = [count u32 LE][gens_per_sec u32 LE]
//!   warp11/gol/ctl/stop         no payload
//!   warp11/gol/ctl/reset        no payload; clears the grid and generation
//!
//! The pacing lives in the fabric's burst/interval FSM (the Kotlin GoL
//! model — evenly spaced generations regardless of host jitter); the daemon
//! only captures, publishes and forwards control writes.

use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::process::exit;
use std::sync::mpsc;
use std::time::Duration;
use warp11_host::mmap::{MmapWindow, O_SYNC};
use warp11_runtime::gol_layout as layout;
use warp11_runtime::RegisterWindow;
use zenoh::qos::CongestionControl;
use zenoh::Wait;

const PL_CLOCK_HZ: u64 = 166_666_667; // golfs_bd_bd.tcl PL0 (166.666672 MHz)
const FRAME_PERIOD: Duration = Duration::from_millis(33);

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

fn udmabuf_phys_addr() -> Option<u64> {
    let text = std::fs::read_to_string("/sys/class/u-dma-buf/udmabuf0/phys_addr").ok()?;
    u64::from_str_radix(text.trim().trim_start_matches("0x"), 16).ok()
}

fn rd(w: &mut MmapWindow, offset: usize) -> u32 {
    w.read32(offset).unwrap()
}

fn wr(w: &mut MmapWindow, offset: usize, value: u32) {
    w.write32(offset, value).unwrap()
}

/// gens/second → the fabric interval that paces them. The interval paces
/// FIRES and every fire advances GENS_PER_CYCLE generations, so the
/// requested rate divides by that. 0 caps at one fire per cycle.
fn interval_for(gens_per_sec: u32) -> u32 {
    if gens_per_sec == 0 {
        1
    } else {
        (PL_CLOCK_HZ * layout::GENS_PER_CYCLE as u64 / u64::from(gens_per_sec))
            .clamp(1, u64::from(u32::MAX)) as u32
    }
}

enum Control {
    Load([u8; layout::FRAME_BYTES]),
    Run { gens_per_sec: u32 },
    Burst { count: u32, gens_per_sec: u32 },
    Stop,
    Reset,
}

/// B3/S23 with a dead border — the canary's software twin (the same
/// carry-save network as gol-engine).
fn life_step(rows: &[u64; 64]) -> [u64; 64] {
    let ha = |a: u64, b: u64| (a ^ b, a & b);
    let fa = |a: u64, b: u64, c: u64| {
        let s = a ^ b;
        (s ^ c, (a & b) | (c & s))
    };
    let mut next = [0u64; 64];
    for y in 0..64 {
        let u = if y > 0 { rows[y - 1] } else { 0 };
        let s = rows[y];
        let d = if y < 63 { rows[y + 1] } else { 0 };
        let (us, uc) = fa(u << 1, u, u >> 1);
        let (ds, dc) = fa(d << 1, d, d >> 1);
        let (ss, sc) = ha(s << 1, s >> 1);
        let (n0, c1) = fa(us, ds, ss);
        let (t, c2) = fa(uc, dc, sc);
        let (n1, c2b) = ha(t, c1);
        let (n2, n3) = ha(c2, c2b);
        next[y] = !n2 & !n3 & n1 & (n0 | s);
    }
    next
}

/// The skew canary: a known soup round-tripped through the whole write path
/// before any client is served. Tearing down a bitstream with writes in
/// flight permanently skews the PS-side HP0 AW/W pairing — every frame then
/// arrives rotated by the in-flight burst count, reload-proof, cured only by
/// reboot. The canary turns that silent corruption into a loud refusal at
/// the earliest possible moment.
fn skew_canary(regs: &mut MmapWindow, fb: &MmapWindow) -> Result<(), String> {
    let mut seed = 0x9E37_79B9_7F4A_7C15u64;
    let mut soup = [0u64; 64];
    for row in soup.iter_mut() {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        *row = seed;
    }

    wr(regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
    for (y, row) in soup.iter().enumerate() {
        wr(regs, layout::LOAD_ROW_OFFSET + y * 8, *row as u32);
        wr(regs, layout::LOAD_ROW_OFFSET + y * 8 + 4, (*row >> 32) as u32);
    }
    wr(regs, layout::LOAD_OFFSET, 1 << layout::LOAD_BIT);
    std::thread::sleep(Duration::from_millis(2));

    let fires = 96 / layout::GENS_PER_CYCLE as u32;
    wr(regs, layout::TICK_COUNT_OFFSET, fires);
    wr(regs, layout::INTERVAL_CYCLES_OFFSET, 1);
    wr(regs, layout::TICK_OFFSET, 1 << layout::TICK_BIT);
    std::thread::sleep(Duration::from_millis(2)); // burst + one settled frame

    wr(regs, layout::SNAP_CAPTURE_OFFSET, 1 << layout::SNAP_CAPTURE_BIT);
    let mut guard = 0;
    while rd(regs, layout::SNAP_READY_OFFSET) & layout::SNAP_READY_MASK == 0 && guard < 1_000_000 {
        guard += 1;
    }
    if rd(regs, layout::SNAP_READY_OFFSET) & layout::SNAP_READY_MASK == 0 {
        return Err("snapshot capture never granted".into());
    }
    let snap = rd(regs, layout::SNAP_READY_OFFSET);
    let slot = ((snap & layout::SNAP_SLOT_MASK) >> layout::SNAP_SLOT_SHIFT) as usize;
    let slot_offset = slot * layout::SLOT_STRIDE_BYTES;
    udmabuf_attr("sync_offset", &slot_offset.to_string()).map_err(|e| e.to_string())?;
    udmabuf_attr("sync_size", &layout::FRAME_BYTES.to_string()).map_err(|e| e.to_string())?;
    udmabuf_attr("sync_for_cpu", "1").map_err(|e| e.to_string())?;
    let frame = &fb.bytes()[slot_offset..slot_offset + layout::FRAME_BYTES];

    let mut twin = soup;
    for _ in 0..96 {
        twin = life_step(&twin);
    }
    let mismatches = twin
        .iter()
        .enumerate()
        .filter(|(y, row)| {
            let base = (y / layout::ROWS_PER_BEAT) * 16 + (y % layout::ROWS_PER_BEAT) * 8;
            u64::from_le_bytes(frame[base..base + 8].try_into().unwrap()) != **row
        })
        .count();
    wr(regs, layout::SNAP_RELEASE_OFFSET, 1 << layout::SNAP_RELEASE_BIT);

    // Leave a clean grid for the first client.
    wr(regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
    wr(regs, layout::RESET_OFFSET, 1 << layout::RESET_BIT);
    wr(regs, layout::BURST_IRQ_OFFSET, 0x3);

    if mismatches != 0 {
        Err(format!(
            "{mismatches}/64 rows wrong (rotated frame): the HP write path is skewed — a \
             previous session was torn down with writes in flight. REBOOT the board; then \
             always stop + `gol-disarm` before `xmutil unloadapp`."
        ))
    } else {
        Ok(())
    }
}

fn main() {
    let listen = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "tcp/0.0.0.0:7447".into());

    // ---- the accelerator ----
    let uio = find_uio("golfs").unwrap_or_else(|| {
        eprintln!("no uio node named 'golfs' — is the gol-fs app loaded?");
        exit(1);
    });
    let reg_file = OpenOptions::new()
        .read(true)
        .write(true)
        .custom_flags(O_SYNC)
        .open(&uio)
        .unwrap_or_else(|e| {
            eprintln!("cannot open {uio}: {e}");
            exit(1);
        });
    let mut regs = MmapWindow::open(&reg_file, 0, layout::APERTURE_BYTES).unwrap_or_else(|e| {
        eprintln!("register mmap failed: {e:?}");
        exit(1);
    });

    let id = rd(&mut regs, layout::ID_OFFSET);
    if id != layout::ID_VALUE {
        eprintln!("ID mismatch: 0x{id:08X} — wrong bitstream?");
        exit(1);
    }

    let fb_base = udmabuf_phys_addr().unwrap_or_else(|| {
        eprintln!("no udmabuf0 phys_addr — is u-dma-buf loaded?");
        exit(1);
    });
    wr(&mut regs, layout::FB_BASE_ADDR_OFFSET, fb_base as u32);

    let fb_file = OpenOptions::new()
        .read(true)
        .write(true)
        .open("/dev/udmabuf0")
        .expect("open /dev/udmabuf0");
    let fb = MmapWindow::open(&fb_file, 0, 3 * layout::SLOT_STRIDE_BYTES).expect("fb mmap");
    udmabuf_attr("sync_direction", "2").expect("sync_direction (udev rule installed?)");

    if let Err(reason) = skew_canary(&mut regs, &fb) {
        eprintln!("skew canary FAILED: {reason}");
        exit(1);
    }
    println!("skew canary ok: known soup round-tripped bit-exact through DDR");

    // ---- the mesh ----
    let mut config = zenoh::Config::default();
    config
        .insert_json5("listen/endpoints", &format!("[\"{listen}\"]"))
        .expect("listen endpoint");
    let session = zenoh::open(config).wait().unwrap_or_else(|e| {
        eprintln!("zenoh open failed: {e}");
        exit(1);
    });
    let frames = session
        .declare_publisher("warp11/gol/frame")
        .congestion_control(CongestionControl::Drop)
        .wait()
        .expect("frame publisher");

    // Control arrives on a Zenoh thread; the register file stays owned by the
    // main loop, so commands cross over a channel.
    let (control_send, control_recv) = mpsc::channel::<Control>();
    let _ctl = session
        .declare_subscriber("warp11/gol/ctl/*")
        .callback(move |sample| {
            let key = sample.key_expr().as_str().to_string();
            let payload = sample.payload().to_bytes().into_owned();
            let parsed = match key.rsplit('/').next() {
                Some("load") if payload.len() == layout::FRAME_BYTES => {
                    let mut rows = [0u8; layout::FRAME_BYTES];
                    rows.copy_from_slice(&payload);
                    Some(Control::Load(rows))
                }
                Some("run") if payload.len() >= 4 => Some(Control::Run {
                    gens_per_sec: u32::from_le_bytes(payload[0..4].try_into().unwrap()),
                }),
                Some("burst") if payload.len() >= 8 => Some(Control::Burst {
                    count: u32::from_le_bytes(payload[0..4].try_into().unwrap()),
                    gens_per_sec: u32::from_le_bytes(payload[4..8].try_into().unwrap()),
                }),
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
        "gol-daemon up: id 0x{id:08X}, fb 0x{fb_base:08X}, listening on {listen}"
    );

    // ---- the loop: apply control, capture, publish ----
    loop {
        while let Ok(control) = control_recv.try_recv() {
            match control {
                Control::Load(rows) => {
                    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
                    for word in 0..layout::LOAD_ROW_WORDS {
                        let value =
                            u32::from_le_bytes(rows[word * 4..word * 4 + 4].try_into().unwrap());
                        wr(&mut regs, layout::LOAD_ROW_OFFSET + word * 4, value);
                    }
                    wr(&mut regs, layout::LOAD_OFFSET, 1 << layout::LOAD_BIT);
                }
                Control::Run { gens_per_sec } => {
                    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
                    wr(&mut regs, layout::INTERVAL_CYCLES_OFFSET, interval_for(gens_per_sec));
                    wr(&mut regs, layout::TICK_COUNT_OFFSET, 0); // continuous
                    wr(&mut regs, layout::TICK_OFFSET, 1 << layout::TICK_BIT);
                }
                Control::Burst { count, gens_per_sec } => {
                    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
                    wr(&mut regs, layout::INTERVAL_CYCLES_OFFSET, interval_for(gens_per_sec));
                    wr(&mut regs, layout::TICK_COUNT_OFFSET, count);
                    wr(&mut regs, layout::TICK_OFFSET, 1 << layout::TICK_BIT);
                }
                Control::Stop => wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT),
                Control::Reset => {
                    wr(&mut regs, layout::STOP_OFFSET, 1 << layout::STOP_BIT);
                    wr(&mut regs, layout::RESET_OFFSET, 1 << layout::RESET_BIT);
                }
            }
        }

        // Capture the freshest completed frame; skip the beat if the fabric
        // has nothing new to grant within the poll bound (it always does).
        wr(&mut regs, layout::SNAP_CAPTURE_OFFSET, 1 << layout::SNAP_CAPTURE_BIT);
        let mut guard = 0;
        while rd(&mut regs, layout::SNAP_READY_OFFSET) & layout::SNAP_READY_MASK == 0
            && guard < 100_000
        {
            guard += 1;
        }
        if rd(&mut regs, layout::SNAP_READY_OFFSET) & layout::SNAP_READY_MASK != 0 {
            let snap = rd(&mut regs, layout::SNAP_READY_OFFSET);
            let slot =
                ((snap & layout::SNAP_SLOT_MASK) >> layout::SNAP_SLOT_SHIFT) as usize;
            let slot_offset = slot * layout::SLOT_STRIDE_BYTES;

            udmabuf_attr("sync_offset", &slot_offset.to_string()).expect("sync_offset");
            udmabuf_attr("sync_size", &layout::FRAME_BYTES.to_string()).expect("sync_size");
            udmabuf_attr("sync_for_cpu", "1").expect("sync_for_cpu");
            let raw = &fb.bytes()[slot_offset..slot_offset + layout::FRAME_BYTES];

            // Beat i carries rows 2i (low 8 bytes) and 2i+1 (high 8): already
            // row-major bytes, so the frame ships as-is after the header.
            let generation = rd(&mut regs, layout::GENERATION_OFFSET);
            let status = rd(&mut regs, layout::BUSY_OFFSET);
            let population = (status & layout::POPULATION_MASK) >> layout::POPULATION_SHIFT;

            let mut payload = Vec::with_capacity(8 + layout::FRAME_BYTES);
            payload.extend_from_slice(&generation.to_le_bytes());
            payload.extend_from_slice(&population.to_le_bytes());
            payload.extend_from_slice(raw);
            wr(&mut regs, layout::SNAP_RELEASE_OFFSET, 1 << layout::SNAP_RELEASE_BIT);

            let _ = frames.put(payload).wait();
        }

        std::thread::sleep(FRAME_PERIOD);
    }
}
