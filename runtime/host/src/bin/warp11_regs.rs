//! A design's registers by name, on whichever board it was built for: the
//! build directory — or the seam file out of it — says how it is reached.
//!
//!   warp11_regs --board out/ --port /dev/ttyUSB1 --set volume=512 --get volume
//!   warp11_regs --board gain_patch_axi_layout.rs --get mute
//!
//! `--port` is the serial line, for a build reached over one. Every `--set`
//! and `--get` is done in the order given; a `--get` prints `name = value`.

use std::path::{Path, PathBuf};
use warp11_host::connection::Connection;
use warp11_host::seam::{parse_number, read_layout, resolve};
use warp11_runtime::RegisterWindow;

enum Action {
    Set(String, u32),
    Get(String),
}

fn usage() -> ! {
    eprintln!("usage: warp11_regs --board <build-dir> [--port <tty>] [--set name=value | --get name]...");
    std::process::exit(2)
}

fn run(board: &Path, port: Option<&Path>, actions: &[Action]) -> Result<(), String> {
    let mut connection = Connection::board(board, port)?;
    let (registers, _) = read_layout(Path::new(&connection.seam.source))?;
    for action in actions {
        match action {
            Action::Set(name, value) => connection.window.write32(resolve(name, &registers)?, *value)?,
            Action::Get(name) => {
                let value = connection.window.read32(resolve(name, &registers)?)?;
                println!("{name} = {value} (0x{value:x})");
            }
        }
    }
    Ok(())
}

fn main() {
    let mut args = std::env::args().skip(1);
    let mut board: Option<PathBuf> = None;
    let mut port: Option<PathBuf> = None;
    let mut actions = Vec::new();

    while let Some(flag) = args.next() {
        let mut value = || args.next().unwrap_or_else(|| usage());
        match flag.as_str() {
            "--board" => board = Some(PathBuf::from(value())),
            "--port" => port = Some(PathBuf::from(value())),
            "--set" => {
                let v = value();
                let (name, number) = v.split_once('=').unwrap_or_else(|| usage());
                actions.push(Action::Set(name.to_string(), parse_number(number).unwrap_or_else(|| usage())));
            }
            "--get" => actions.push(Action::Get(value())),
            _ => usage(),
        }
    }

    if let Err(e) = run(&board.unwrap_or_else(|| usage()), port.as_deref(), &actions) {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
