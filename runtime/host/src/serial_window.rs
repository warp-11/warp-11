//! A register window over a serial line, for a board with no bus: the
//! `RegisterWindow` a driver was written against, reached through the framed
//! request/reply that `serialRegMapSlave` speaks on the fabric side.
//!
//! The framing (`hdl/Warp11/SerialRegMap.fs`, mirrored by `SimUart.fs` for
//! the checks) is one byte of sync, a command, the data for a write, and an
//! xor over everything after the sync:
//!
//! ```text
//! write   A5 | 80+word | d0 d1 d2 d3 | xor     reply  A5 | status | xor
//! read    A5 | word    |             | xor     reply  A5 | status | d0 d1 d2 d3 | xor
//! ```
//!
//! `word` is the register's byte offset divided by four, so the seam's
//! `*_OFFSET` constants address the map here exactly as they do over AXI-Lite.
//! Data is least significant byte first. Status 0 is accepted; 1 means the
//! fabric refused the request on its checksum and wrote nothing.
//!
//! The framing is pure functions, tested without a tty; the tty is `termios`
//! declared by hand, as `mmap.rs` declares `mmap`, so the workspace stays
//! dependency-free.

use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::os::fd::AsRawFd;
use std::path::Path;
use warp11_runtime::RegisterWindow;

/// The first byte of every request and every reply.
pub const SYNC: u8 = 0xA5;

/// The widest map the command byte can address: seven bits of word.
pub const MAX_WORDS: usize = 128;

#[derive(Debug)]
pub enum SerialError {
    Io(std::io::Error),
    /// The offset is not a word in a 128-word aperture.
    BadOffset(usize),
    /// The fabric refused the request: its checksum did not match on arrival.
    Refused,
    /// No reply, or not a whole one, before the timeout.
    Timeout { wanted: usize, got: usize },
    /// A reply arrived with the wrong sync byte or checksum — noise on the
    /// line, or not the design this window expects.
    Malformed([u8; 7], usize),
}

impl From<std::io::Error> for SerialError {
    fn from(e: std::io::Error) -> Self {
        SerialError::Io(e)
    }
}

impl std::fmt::Display for SerialError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SerialError::Io(e) => write!(f, "serial: {e}"),
            SerialError::BadOffset(o) => write!(f, "offset {o:#x} is not a word in the aperture"),
            SerialError::Refused => write!(f, "the fabric refused the request (checksum)"),
            SerialError::Timeout { wanted, got } => write!(f, "no reply: {got} of {wanted} bytes"),
            SerialError::Malformed(bytes, n) => write!(f, "malformed reply {:02x?}", &bytes[..*n]),
        }
    }
}

impl std::error::Error for SerialError {}

fn xor_of(bytes: &[u8]) -> u8 {
    bytes.iter().fold(0, |acc, b| acc ^ b)
}

fn word_of(offset: usize) -> Result<u8, SerialError> {
    if offset % 4 != 0 || offset / 4 >= MAX_WORDS {
        return Err(SerialError::BadOffset(offset));
    }
    Ok((offset / 4) as u8)
}

/// The request that writes `value` at `offset`.
pub fn write_frame(offset: usize, value: u32) -> Result<[u8; 7], SerialError> {
    let word = word_of(offset)?;
    let [d0, d1, d2, d3] = value.to_le_bytes();
    let body = [0x80 | word, d0, d1, d2, d3];
    Ok([SYNC, body[0], body[1], body[2], body[3], body[4], xor_of(&body)])
}

/// The request that reads `offset`.
pub fn read_frame(offset: usize) -> Result<[u8; 3], SerialError> {
    let word = word_of(offset)?;
    Ok([SYNC, word, word])
}

/// How long the reply to a request is, sync and checksum included.
pub fn reply_length(is_read: bool) -> usize {
    if is_read {
        7
    } else {
        3
    }
}

/// A reply's value — `Some` for a read — or why it is not one.
pub fn parse_reply(is_read: bool, reply: &[u8]) -> Result<Option<u32>, SerialError> {
    let malformed = || {
        let mut copy = [0u8; 7];
        let n = reply.len().min(7);
        copy[..n].copy_from_slice(&reply[..n]);
        SerialError::Malformed(copy, n)
    };

    if reply.len() != reply_length(is_read) || reply[0] != SYNC {
        return Err(malformed());
    }
    let body = &reply[1..reply.len() - 1];
    if xor_of(body) != reply[reply.len() - 1] {
        return Err(malformed());
    }
    match body[0] {
        0 if is_read => Ok(Some(u32::from_le_bytes([body[1], body[2], body[3], body[4]]))),
        0 => Ok(None),
        1 => Err(SerialError::Refused),
        _ => Err(malformed()),
    }
}

// ---- the tty ---------------------------------------------------------------

/// glibc's `struct termios` on Linux, both x86_64 and aarch64.
#[repr(C)]
struct Termios {
    c_iflag: u32,
    c_oflag: u32,
    c_cflag: u32,
    c_lflag: u32,
    c_line: u8,
    c_cc: [u8; 32],
    c_ispeed: u32,
    c_ospeed: u32,
}

extern "C" {
    fn tcgetattr(fd: i32, termios: *mut Termios) -> i32;
    fn tcsetattr(fd: i32, optional_actions: i32, termios: *const Termios) -> i32;
    fn cfmakeraw(termios: *mut Termios);
    fn cfsetispeed(termios: *mut Termios, speed: u32) -> i32;
    fn cfsetospeed(termios: *mut Termios, speed: u32) -> i32;
    fn tcflush(fd: i32, queue_selector: i32) -> i32;
}

const TCSANOW: i32 = 0;
const TCIOFLUSH: i32 = 2;
const CLOCAL: u32 = 0o4000;
const CREAD: u32 = 0o200;
const VTIME: usize = 5;
const VMIN: usize = 6;

/// Linux `Bnnn` codes for the rates a fabric link is likely to run at.
fn speed_code(baud: u32) -> Option<u32> {
    Some(match baud {
        9_600 => 0o15,
        19_200 => 0o16,
        38_400 => 0o17,
        57_600 => 0o10001,
        115_200 => 0o10002,
        230_400 => 0o10003,
        460_800 => 0o10004,
        500_000 => 0o10005,
        921_600 => 0o10007,
        1_000_000 => 0o10010,
        1_152_000 => 0o10011,
        1_500_000 => 0o10012,
        2_000_000 => 0o10013,
        3_000_000 => 0o10015,
        _ => return None,
    })
}

/// A `RegisterWindow` over a tty. One request in flight at a time: a write
/// waits for its acknowledgement and a read for its value, so a driver's
/// `write32` returning means the register holds the value.
pub struct SerialWindow {
    tty: File,
}

impl SerialWindow {
    /// Open `path` at `baud`, 8N1, raw, with a read timeout of `timeout_tenths`
    /// tenths of a second — the reply to any request is under a millisecond
    /// on the wire, so a timeout means the board is not there.
    pub fn open(path: &Path, baud: u32, timeout_tenths: u8) -> Result<Self, SerialError> {
        let speed = speed_code(baud).ok_or_else(|| {
            SerialError::Io(std::io::Error::new(std::io::ErrorKind::InvalidInput, format!("no termios code for {baud} baud")))
        })?;
        let tty = OpenOptions::new().read(true).write(true).open(path)?;
        let fd = tty.as_raw_fd();

        let mut attributes = Termios {
            c_iflag: 0,
            c_oflag: 0,
            c_cflag: 0,
            c_lflag: 0,
            c_line: 0,
            c_cc: [0; 32],
            c_ispeed: 0,
            c_ospeed: 0,
        };
        let failed = |what: &str| SerialError::Io(std::io::Error::new(std::io::ErrorKind::Other, format!("{what} failed")));

        unsafe {
            if tcgetattr(fd, &mut attributes) != 0 {
                return Err(failed("tcgetattr"));
            }
            cfmakeraw(&mut attributes);
            attributes.c_cflag |= CLOCAL | CREAD;
            attributes.c_cc[VMIN] = 0;
            attributes.c_cc[VTIME] = timeout_tenths;
            if cfsetispeed(&mut attributes, speed) != 0 || cfsetospeed(&mut attributes, speed) != 0 {
                return Err(failed("cfsetspeed"));
            }
            if tcsetattr(fd, TCSANOW, &attributes) != 0 {
                return Err(failed("tcsetattr"));
            }
            tcflush(fd, TCIOFLUSH);
        }

        Ok(SerialWindow { tty })
    }

    fn exchange(&mut self, request: &[u8], is_read: bool) -> Result<Option<u32>, SerialError> {
        self.tty.write_all(request)?;
        self.tty.flush()?;

        let wanted = reply_length(is_read);
        let mut reply = [0u8; 7];
        let mut got = 0;
        while got < wanted {
            let n = self.tty.read(&mut reply[got..wanted])?;
            if n == 0 {
                return Err(SerialError::Timeout { wanted, got });
            }
            got += n;
        }
        parse_reply(is_read, &reply[..wanted])
    }
}

impl RegisterWindow for SerialWindow {
    type Error = SerialError;

    fn read32(&mut self, offset: usize) -> Result<u32, SerialError> {
        let request = read_frame(offset)?;
        Ok(self.exchange(&request, true)?.expect("a read's reply carries a value"))
    }

    fn write32(&mut self, offset: usize, value: u32) -> Result<(), SerialError> {
        let request = write_frame(offset, value)?;
        self.exchange(&request, false).map(|_| ())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // The frames the fabric's own check sends and expects (`SerialFrame` in
    // `SimUart.fs`), byte for byte.
    #[test]
    fn write_frame_matches_the_fabric_side() {
        assert_eq!(write_frame(0x004, 0xBEEF).unwrap(), [0xA5, 0x81, 0xEF, 0xBE, 0x00, 0x00, 0x81 ^ 0xEF ^ 0xBE]);
    }

    #[test]
    fn read_frame_matches_the_fabric_side() {
        assert_eq!(read_frame(0x000).unwrap(), [0xA5, 0x00, 0x00]);
        assert_eq!(read_frame(0x04C).unwrap(), [0xA5, 0x13, 0x13]);
    }

    #[test]
    fn offsets_must_be_words_in_the_aperture() {
        assert!(matches!(read_frame(0x002), Err(SerialError::BadOffset(2))));
        assert!(matches!(read_frame(0x200), Err(SerialError::BadOffset(0x200))));
    }

    // What the board answered from the shell on 2026-09-13: the identity,
    // then a table word.
    #[test]
    fn parses_replies_the_board_sent() {
        assert_eq!(parse_reply(true, &[0xA5, 0x00, 0x1D, 0x0A, 0x00, 0x00, 0x17]).unwrap(), Some(0x0A1D));
        assert_eq!(parse_reply(true, &[0xA5, 0x00, 0xE8, 0x03, 0x00, 0x00, 0xEB]).unwrap(), Some(0x3E8));
        assert_eq!(parse_reply(false, &[0xA5, 0x00, 0x00]).unwrap(), None);
    }

    #[test]
    fn a_refusal_and_noise_are_told_apart() {
        assert!(matches!(parse_reply(false, &[0xA5, 0x01, 0x01]), Err(SerialError::Refused)));
        assert!(matches!(parse_reply(false, &[0xA5, 0x00, 0x01]), Err(SerialError::Malformed(_, 3))));
        assert!(matches!(parse_reply(true, &[0xA5, 0x00, 0x00]), Err(SerialError::Malformed(_, 3))));
        assert!(matches!(parse_reply(false, &[0x00, 0x00, 0x00]), Err(SerialError::Malformed(_, 3))));
    }
}
