//! A register window over the F# Sim: spawns hdl's `simserve` mode
//! and speaks its line protocol, so a driver written against `RegisterWindow`
//! runs against the F#-elaborated design with no board and no bitstream. This
//! is `sim_window`'s property extended across the language seam — the same
//! `MandelDevice` that will mmap `/dev/mem` exercises the real five-channel
//! handshakes in the other language's simulator.
//!
//! Protocol: `R <hexoff>` → `<hexvalue>`; `W <hexoff> <hexval>` → `OK`. The
//! F# side prints `SIMSERVE` when ready; anything before that is dotnet build
//! chatter and is discarded.

use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use warp11_runtime::RegisterWindow;

#[derive(Debug)]
pub enum FsSimError {
    Io(std::io::Error),
    /// The F# side answered something the protocol does not contain — its
    /// stderr will hold the handshake assertion that fired.
    Protocol(String),
}

pub struct FsSimWindow {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
}

impl FsSimWindow {
    /// Spawn `dotnet run --project <fsproj> -- simserve` and wait for the
    /// ready marker. Errors here usually mean dotnet is absent — a caller
    /// that wants to skip rather than fail checks that first.
    pub fn spawn(fsproj: &Path) -> Result<Self, FsSimError> {
        Self::spawn_mode(fsproj, "simserve", "SIMSERVE")
    }

    /// Spawn any serve mode with its ready marker — `frameserve`/`FRAMESERVE`
    /// is the full-scale wrapper's bridge, whose protocol adds `C` (free
    /// cycles) and `D` (DDR dump) to `R`/`W`.
    pub fn spawn_mode(fsproj: &Path, mode: &str, marker: &str) -> Result<Self, FsSimError> {
        let mut child = Command::new("dotnet")
            .arg("run")
            .arg("--project")
            .arg(fsproj)
            .arg("--")
            .arg(mode)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .map_err(FsSimError::Io)?;

        let stdin = child.stdin.take().expect("piped stdin");
        let mut stdout = BufReader::new(child.stdout.take().expect("piped stdout"));

        // Discard build chatter until the ready marker; a bounded scan so a
        // wedged build fails instead of hanging the test.
        for _ in 0..200 {
            let mut line = String::new();
            if stdout.read_line(&mut line).map_err(FsSimError::Io)? == 0 {
                return Err(FsSimError::Protocol(format!(
                    "{mode} exited before {marker}"
                )));
            }
            if line.trim() == marker {
                return Ok(FsSimWindow {
                    child,
                    stdin,
                    stdout,
                });
            }
        }
        Err(FsSimError::Protocol(format!("no {marker} marker in 200 lines")))
    }

    /// Run the fabric `n` free cycles — so a poll loop is not
    /// transaction-paced (frameserve only).
    pub fn free_cycles(&mut self, n: usize) -> Result<(), FsSimError> {
        let reply = self.round_trip(&format!("C {n:x}"))?;
        if reply == "OK" {
            Ok(())
        } else {
            Err(FsSimError::Protocol(reply))
        }
    }

    /// Dump `len` bytes of the fake DDR — the framebuffer readback the
    /// register aperture cannot carry (frameserve only).
    pub fn read_ddr(&mut self, offset: usize, len: usize) -> Result<Vec<u8>, FsSimError> {
        let reply = self.round_trip(&format!("D {offset:x} {len:x}"))?;
        if reply.len() != len * 2 {
            return Err(FsSimError::Protocol(reply));
        }
        (0..len)
            .map(|i| {
                u8::from_str_radix(&reply[i * 2..i * 2 + 2], 16)
                    .map_err(|_| FsSimError::Protocol(reply.clone()))
            })
            .collect()
    }

    fn round_trip(&mut self, request: &str) -> Result<String, FsSimError> {
        writeln!(self.stdin, "{request}").map_err(FsSimError::Io)?;
        self.stdin.flush().map_err(FsSimError::Io)?;
        let mut reply = String::new();
        if self.stdout.read_line(&mut reply).map_err(FsSimError::Io)? == 0 {
            return Err(FsSimError::Protocol(format!("simserve died on: {request}")));
        }
        Ok(reply.trim().to_string())
    }
}

impl RegisterWindow for FsSimWindow {
    type Error = FsSimError;

    fn read32(&mut self, offset: usize) -> Result<u32, FsSimError> {
        let reply = self.round_trip(&format!("R {offset:x}"))?;
        u32::from_str_radix(&reply, 16).map_err(|_| FsSimError::Protocol(reply))
    }

    fn write32(&mut self, offset: usize, value: u32) -> Result<(), FsSimError> {
        let reply = self.round_trip(&format!("W {offset:x} {value:x}"))?;
        if reply == "OK" {
            Ok(())
        } else {
            Err(FsSimError::Protocol(reply))
        }
    }
}

impl Drop for FsSimWindow {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
