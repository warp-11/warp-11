//! The GoL software engine on the Zenoh mesh: the same key spaces as
//! `gol-daemon`, no fabric behind them — the tutorial's Rust rung. The GUI
//! cannot tell this process from the board daemon; only the number moves.
//!
//!   warp11/gol/frame      published at ~30 Hz, CongestionControl::Drop:
//!                         [generation u32 LE][population u32 LE]
//!                         [512 bytes: 64 rows of u64 LE, bit x = cell x]
//!   warp11/gol/ctl/load   payload = 512 bytes of rows
//!   warp11/gol/ctl/run    payload = [gens_per_sec u32 LE]; 0 = flat out
//!   warp11/gol/ctl/burst  payload = [count u32 LE][gens_per_sec u32 LE]
//!   warp11/gol/ctl/stop   no payload
//!   warp11/gol/ctl/reset  no payload; stop, clear grid and generation
//!
//! Flat out, the loop conflates exactly like the fabric's triple buffer:
//! it steps as fast as the engine allows and publishes the latest completed
//! state each beat.

use std::process::exit;
use std::sync::mpsc;
use std::time::{Duration, Instant};
use zenoh::qos::CongestionControl;
use zenoh::Wait;

const H: usize = 64;
const FRAME_BYTES: usize = 8 * H;
const FRAME_PERIOD: Duration = Duration::from_millis(33);

#[inline(always)]
fn ha(a: u64, b: u64) -> (u64, u64) {
    (a ^ b, a & b)
}

#[inline(always)]
fn fa(a: u64, b: u64, c: u64) -> (u64, u64) {
    let s = a ^ b;
    (s ^ c, (a & b) | (c & s))
}

/// B3/S23 with a dead border, one whole generation per call: carry-save
/// adders over the row bitmasks count every column's neighbors at once, and
/// shifting left aligns the x-1 neighbor onto column x so the border falls
/// out of the shifts for free — the same adder network the fabric elaborates
/// in parallel.
fn step(rows: &[u64; H]) -> [u64; H] {
    let mut next = [0u64; H];
    for y in 0..H {
        let u = if y > 0 { rows[y - 1] } else { 0 };
        let s = rows[y];
        let d = if y < H - 1 { rows[y + 1] } else { 0 };
        let (us, uc) = fa(u << 1, u, u >> 1);
        let (ds, dc) = fa(d << 1, d, d >> 1);
        let (ss, sc) = ha(s << 1, s >> 1);
        // Bit-planes of the neighbor count: n0 ones, n1 twos, n2/n3 above.
        let (n0, c1) = fa(us, ds, ss);
        let (t, c2) = fa(uc, dc, sc);
        let (n1, c2b) = ha(t, c1);
        let (n2, n3) = ha(c2, c2b);
        // N == 3, or N == 2 and already alive.
        next[y] = !n2 & !n3 & n1 & (n0 | s);
    }
    next
}

enum Control {
    Load([u8; FRAME_BYTES]),
    Run { gens_per_sec: u32 },
    Burst { count: u32, gens_per_sec: u32 },
    Stop,
    Reset,
}

fn main() {
    let listen = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "tcp/0.0.0.0:7447".into());

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

    let (control_send, control_recv) = mpsc::channel::<Control>();
    let _ctl = session
        .declare_subscriber("warp11/gol/ctl/*")
        .callback(move |sample| {
            let key = sample.key_expr().as_str().to_string();
            let payload = sample.payload().to_bytes().into_owned();
            let parsed = match key.rsplit('/').next() {
                Some("load") if payload.len() == FRAME_BYTES => {
                    let mut rows = [0u8; FRAME_BYTES];
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

    println!("gol-engine up (software bitboard), listening on {listen}");

    let mut rows = [0u64; H];
    let mut generation = 0u32;
    let mut running = false;
    let mut gens_per_sec = 0u32;
    let mut remaining: Option<u32> = None; // Some(n) while a burst runs
    let mut credit = 0.0f64;
    let clock = Instant::now();
    let mut last_accrual = clock.elapsed().as_secs_f64();
    let mut next_publish = 0.0f64;

    loop {
        while let Ok(control) = control_recv.try_recv() {
            match control {
                Control::Load(bytes) => {
                    for (y, row) in rows.iter_mut().enumerate() {
                        *row = u64::from_le_bytes(bytes[y * 8..y * 8 + 8].try_into().unwrap());
                    }
                    generation = 0;
                }
                Control::Run { gens_per_sec: rate } => {
                    gens_per_sec = rate;
                    remaining = None;
                    running = true;
                }
                Control::Burst { count, gens_per_sec: rate } => {
                    gens_per_sec = rate;
                    remaining = Some(count);
                    running = true;
                }
                Control::Stop => running = false,
                Control::Reset => {
                    running = false;
                    rows = [0u64; H];
                    generation = 0;
                }
            }
        }

        let now = clock.elapsed().as_secs_f64();
        credit = if running && gens_per_sec > 0 {
            (credit + f64::from(gens_per_sec) * (now - last_accrual)).min(65536.0)
        } else {
            0.0
        };
        last_accrual = now;

        let due = if !running {
            0
        } else if gens_per_sec == 0 {
            4096 // flat out: one batch between control polls
        } else {
            credit as u32
        };
        let due = remaining.map_or(due, |r| due.min(r));

        for _ in 0..due {
            rows = step(&rows);
            generation = generation.wrapping_add(1);
        }
        if gens_per_sec > 0 {
            credit -= f64::from(due);
        }
        if let Some(r) = remaining {
            let left = r - due;
            remaining = if left == 0 { None } else { Some(left) };
            if left == 0 {
                running = false;
            }
        }

        if now >= next_publish {
            let population: u32 = rows.iter().map(|r| r.count_ones()).sum();
            let mut payload = Vec::with_capacity(8 + FRAME_BYTES);
            payload.extend_from_slice(&generation.to_le_bytes());
            payload.extend_from_slice(&population.to_le_bytes());
            for row in &rows {
                payload.extend_from_slice(&row.to_le_bytes());
            }
            let _ = frames.put(payload).wait();
            next_publish = now + FRAME_PERIOD.as_secs_f64();
        }

        if due == 0 {
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}
