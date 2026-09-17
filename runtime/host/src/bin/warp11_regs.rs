//! A design's registers by name, over whichever link the board has: the
//! UART a standalone board's register slave speaks, or the uio mapping an OS
//! app exposes. The same `RegisterWindow` either way.
//!
//!   warp11_regs --serial /dev/ttyUSB1 --baud 115200 --layout gain_patch_uart_layout.rs --set volume=512 --get volume
//!   warp11_regs --uio gain_patch_axi --layout gain_patch_axi_layout.rs --get mute
//!
//! Every `--set` and `--get` is done in the order given; a `--get` prints
//! `name = value`.

use std::fs::OpenOptions;
use std::path::{Path, PathBuf};
use warp11_host::mmap::MmapWindow;
use warp11_host::seam::{parse_number, read_layout, resolve};
use warp11_host::serial_window::SerialWindow;
use warp11_host::udmabuf::find_uio;
use warp11_runtime::RegisterWindow;

enum Action {
    Set(String, u32),
    Get(String),
}

fn usage() -> ! {
    eprintln!("usage: warp11_regs (--serial <tty> [--baud 115200] | --uio <name>) --layout <seam.rs> [--set name=value | --get name]...");
    std::process::exit(2)
}

fn run<W: RegisterWindow>(mut window: W, registers: &[(String, usize)], actions: &[Action]) -> Result<(), String>
where
    W::Error: std::fmt::Debug,
{
    for action in actions {
        match action {
            Action::Set(name, value) => {
                let offset = resolve(name, registers)?;
                window.write32(offset, *value).map_err(|e| format!("write {name}: {e:?}"))?;
            }
            Action::Get(name) => {
                let offset = resolve(name, registers)?;
                let value = window.read32(offset).map_err(|e| format!("read {name}: {e:?}"))?;
                println!("{name} = {value} (0x{value:x})");
            }
        }
    }
    Ok(())
}

fn main() {
    let mut args = std::env::args().skip(1);
    let mut serial: Option<PathBuf> = None;
    let mut baud = 115_200u32;
    let mut uio: Option<String> = None;
    let mut layout: Option<PathBuf> = None;
    let mut actions = Vec::new();

    while let Some(flag) = args.next() {
        let mut value = || args.next().unwrap_or_else(|| usage());
        match flag.as_str() {
            "--serial" => serial = Some(PathBuf::from(value())),
            "--baud" => baud = value().parse().unwrap_or_else(|_| usage()),
            "--uio" => uio = Some(value()),
            "--layout" => layout = Some(PathBuf::from(value())),
            "--set" => {
                let v = value();
                let (name, number) = v.split_once('=').unwrap_or_else(|| usage());
                actions.push(Action::Set(name.to_string(), parse_number(number).unwrap_or_else(|| usage())));
            }
            "--get" => actions.push(Action::Get(value())),
            _ => usage(),
        }
    }

    let layout = layout.unwrap_or_else(|| usage());
    let (registers, _) = match read_layout(&layout) {
        Ok(v) => v,
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    };

    let result = match (serial, uio) {
        (Some(tty), None) => match SerialWindow::open(Path::new(&tty), baud, 10) {
            Ok(window) => run(window, &registers, &actions),
            Err(e) => Err(format!("{}: {e}", tty.display())),
        },
        (None, Some(name)) => match find_uio(&name) {
            Ok(Some(path)) => match OpenOptions::new().read(true).write(true).open(&path) {
                Ok(file) => match MmapWindow::open(&file, 0, 0x1000) {
                    Ok(window) => run(window, &registers, &actions),
                    Err(e) => Err(format!("{e:?}")),
                },
                Err(e) => Err(format!("{}: {e}", path.display())),
            },
            Ok(None) => Err(format!("no uio device called '{name}'")),
            Err(e) => Err(format!("{e}")),
        },
        _ => usage(),
    };

    if let Err(e) = result {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
