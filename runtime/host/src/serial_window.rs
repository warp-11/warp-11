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
//! A map too wide for the command byte's seven bits spends **one more byte**,
//! right after the command and carrying the word's high bits — which is what a
//! design with a table in it needs, and what `Band`'s gain curves are:
//!
//! ```text
//! write   A5 | 80+wlo | whi | d0 d1 d2 d3 | xor
//! read    A5 | wlo    | whi |             | xor
//! ```
//!
//! How many address bytes a link uses is the *map's* property, not the word's: a
//! wide map's receiver waits for the second byte whatever word is asked for. So
//! it is fixed when the window is opened, from the aperture the seam states.
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

/// The widest map the command byte can address on its own: seven bits of word.
pub const MAX_WORDS: usize = 128;

/// The widest map a request can address at all, with the extra byte.
pub const MAX_WORDS_WIDE: usize = MAX_WORDS << 8;

/// Address bytes a map of `words` words needs. A map is either narrow enough
/// for the command byte or it is not; there is no third shape.
pub fn address_bytes_for(words: usize) -> usize {
    if words <= MAX_WORDS { 1 } else { 2 }
}

/// The status byte of a frame the fabric sent **unasked**, carrying one
/// streamed word — a recorder, a trace, a log sharing the wire the register
/// map is on (`serialRegMapSlaveWith` on the fabric side). 0 and 1 are a
/// reply's accepted and refused, so it cannot be mistaken for either.
///
/// Every request here has to cope with one arriving mid-exchange, which is
/// why the frames are read one at a time rather than a fixed count of bytes:
/// a design may start streaming at any moment, and a driver that did not
/// expect it would parse a streamed word as its reply.
pub const STREAM_STATUS: u8 = 0x02;

/// How many streamed frames a single request will step over before giving
/// up. A design streaming flat out must not be able to make `read32` hang
/// forever; at that point the link is oversubscribed and the caller should
/// know rather than block.
const MAX_STREAMED_PER_EXCHANGE: usize = 4096;

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
    /// The fabric streamed so much that the reply never got a turn.
    Flooded,
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
            SerialError::Flooded => write!(
                f,
                "the fabric streamed {MAX_STREAMED_PER_EXCHANGE} frames without answering — the link is oversubscribed"
            ),
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

/// The address bytes of a request: the command byte, with the word's high bits
/// after it when the map needs them.
fn address_of(address_bytes: usize, top: u8, offset: usize) -> Result<Vec<u8>, SerialError> {
    let limit = if address_bytes > 1 { MAX_WORDS_WIDE } else { MAX_WORDS };

    if offset % 4 != 0 || offset / 4 >= limit {
        return Err(SerialError::BadOffset(offset));
    }

    let word = offset / 4;

    Ok(match address_bytes {
        1 => vec![top | word as u8],
        _ => vec![top | (word & 0x7F) as u8, (word >> 7) as u8],
    })
}

/// The request that writes `value` at `offset`, on a map of `address_bytes`.
pub fn write_frame_at(address_bytes: usize, offset: usize, value: u32) -> Result<Vec<u8>, SerialError> {
    let mut body = address_of(address_bytes, 0x80, offset)?;
    body.extend_from_slice(&value.to_le_bytes());
    let mut frame = vec![SYNC];
    frame.extend_from_slice(&body);
    frame.push(xor_of(&body));
    Ok(frame)
}

/// The request that reads `offset`, on a map of `address_bytes`.
pub fn read_frame_at(address_bytes: usize, offset: usize) -> Result<Vec<u8>, SerialError> {
    let body = address_of(address_bytes, 0, offset)?;
    let mut frame = vec![SYNC];
    frame.extend_from_slice(&body);
    frame.push(xor_of(&body));
    Ok(frame)
}

/// The request that writes `value` at `offset`, on a map narrow enough for the
/// command byte to address it.
pub fn write_frame(offset: usize, value: u32) -> Result<[u8; 7], SerialError> {
    let word = word_of(offset)?;
    let [d0, d1, d2, d3] = value.to_le_bytes();
    let body = [0x80 | word, d0, d1, d2, d3];
    Ok([SYNC, body[0], body[1], body[2], body[3], body[4], xor_of(&body)])
}

/// The request that reads `offset`, on a map narrow enough for the command byte
/// to address it.
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

/// One frame off the wire, told apart by its status byte.
#[derive(Debug, PartialEq)]
pub enum Frame {
    /// A word the fabric sent unasked.
    Streamed(u32),
    /// The reply to the request in flight — `Some` for a read.
    Reply(Option<u32>),
}

/// How many bytes follow the status byte, the checksum included.
///
/// A streamed frame always carries a word; a reply carries one only when the
/// request was a read. So the length cannot be known from the request alone,
/// which is the whole reason frames are read a piece at a time here.
pub fn tail_length(status: u8, is_read: bool) -> usize {
    if status == STREAM_STATUS || (status == 0 && is_read) {
        5
    } else {
        1
    }
}

/// A whole frame, given its status byte and everything after it.
pub fn parse_frame(status: u8, tail: &[u8], is_read: bool) -> Result<Frame, SerialError> {
    let malformed = || {
        let mut copy = [0u8; 7];
        copy[0] = SYNC;
        copy[1] = status;
        let n = tail.len().min(5);
        copy[2..2 + n].copy_from_slice(&tail[..n]);
        SerialError::Malformed(copy, 2 + n)
    };

    if tail.len() != tail_length(status, is_read) {
        return Err(malformed());
    }

    // The xor covers the status byte and any data, but not itself.
    let body_xor = tail[..tail.len() - 1].iter().fold(status, |a, b| a ^ b);
    if body_xor != tail[tail.len() - 1] {
        return Err(malformed());
    }

    match status {
        STREAM_STATUS => Ok(Frame::Streamed(u32::from_le_bytes([tail[0], tail[1], tail[2], tail[3]]))),
        0 if is_read => Ok(Frame::Reply(Some(u32::from_le_bytes([tail[0], tail[1], tail[2], tail[3]])))),
        0 => Ok(Frame::Reply(None)),
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
    /// Words the fabric streamed while this window was doing something else.
    /// They are kept rather than dropped: a recorder's data arriving during a
    /// `wdrc show` is still the recorder's data, and throwing it away would
    /// put a hole in the recording for every register read.
    streamed: std::collections::VecDeque<u32>,
    /// Address bytes every request on this link carries — the map's shape, fixed
    /// when the window was opened.
    address_bytes: usize,
}

impl SerialWindow {
    /// Open `path` at `baud`, 8N1, raw, with a read timeout of `timeout_tenths`
    /// tenths of a second — the reply to any request is under a millisecond
    /// on the wire, so a timeout means the board is not there.
    pub fn open(path: &Path, baud: u32, timeout_tenths: u8) -> Result<Self, SerialError> {
        Self::open_for(path, baud, timeout_tenths, 1)
    }

    /// The same, for a map of `words` words — which is what a design with a
    /// table in it has, and what decides the frame's shape.
    pub fn open_words(path: &Path, baud: u32, timeout_tenths: u8, words: usize) -> Result<Self, SerialError> {
        Self::open_for(path, baud, timeout_tenths, address_bytes_for(words))
    }

    fn open_for(path: &Path, baud: u32, timeout_tenths: u8, address_bytes: usize) -> Result<Self, SerialError> {
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

        Ok(SerialWindow { tty, streamed: std::collections::VecDeque::new(), address_bytes })
    }

    fn fill(&mut self, buf: &mut [u8]) -> Result<(), SerialError> {
        let wanted = buf.len();
        let mut got = 0;
        while got < wanted {
            let n = self.tty.read(&mut buf[got..])?;
            if n == 0 {
                return Err(SerialError::Timeout { wanted, got });
            }
            got += n;
        }
        Ok(())
    }

    /// One frame off the wire. The status byte decides how much follows it,
    /// so a streamed word and a reply can share the link without the reader
    /// having to know in advance which is coming.
    fn next_frame(&mut self, is_read: bool) -> Result<Frame, SerialError> {
        let mut head = [0u8; 2];
        self.fill(&mut head)?;

        if head[0] != SYNC {
            return Err(SerialError::Malformed([head[0], head[1], 0, 0, 0, 0, 0], 2));
        }

        let mut tail = [0u8; 5];
        let n = tail_length(head[1], is_read);
        self.fill(&mut tail[..n])?;
        parse_frame(head[1], &tail[..n], is_read)
    }

    fn exchange(&mut self, request: &[u8], is_read: bool) -> Result<Option<u32>, SerialError> {
        self.tty.write_all(request)?;
        self.tty.flush()?;

        // Step over whatever the fabric streams in the meantime, keeping it.
        for _ in 0..MAX_STREAMED_PER_EXCHANGE {
            match self.next_frame(is_read)? {
                Frame::Streamed(word) => self.streamed.push_back(word),
                Frame::Reply(value) => return Ok(value),
            }
        }

        Err(SerialError::Flooded)
    }

    /// Words the fabric has streamed so far, taken out of the window.
    ///
    /// They accumulate during ordinary register traffic, so a recorder loses
    /// nothing to a `wdrc show` running beside it.
    pub fn take_streamed(&mut self) -> Vec<u32> {
        self.streamed.drain(..).collect()
    }

    /// Read streamed words until `want` of them have arrived or the link goes
    /// quiet, sending nothing. This is the recording loop: no request is in
    /// flight, so every frame should be a streamed one, and a reply arriving
    /// here would mean something else is talking to the same tty.
    pub fn read_streamed(&mut self, want: usize) -> Result<Vec<u32>, SerialError> {
        while self.streamed.len() < want {
            match self.next_frame(false) {
                Ok(Frame::Streamed(word)) => self.streamed.push_back(word),
                Ok(Frame::Reply(_)) => continue,
                Err(SerialError::Timeout { got: 0, .. }) => break,
                Err(e) => return Err(e),
            }
        }

        Ok(self.streamed.drain(..self.streamed.len().min(want)).collect())
    }
}

impl RegisterWindow for SerialWindow {
    type Error = SerialError;

    fn read32(&mut self, offset: usize) -> Result<u32, SerialError> {
        let request = read_frame_at(self.address_bytes, offset)?;
        Ok(self.exchange(&request, true)?.expect("a read's reply carries a value"))
    }

    fn write32(&mut self, offset: usize, value: u32) -> Result<(), SerialError> {
        let request = write_frame_at(self.address_bytes, offset, value)?;
        self.exchange(&request, false).map(|_| ())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A map too wide for the command byte spends one more address byte, and a
    /// narrow one must be byte-for-byte what it always was — which is what lets
    /// every existing design keep its frames while a design with a table gets
    /// reachable ones. The fabric's side is `serialAddressBytes` in
    /// `SerialRegMap.fs`.
    #[test]
    fn a_wide_map_spends_one_more_address_byte() {
        assert_eq!(address_bytes_for(128), 1);
        assert_eq!(address_bytes_for(129), 2);

        // Narrow: identical to the frames that have no idea this exists.
        assert_eq!(read_frame_at(1, 0x14).unwrap(), read_frame(0x14).unwrap().to_vec());
        assert_eq!(write_frame_at(1, 0x14, 0xDEADBEEF).unwrap(), write_frame(0x14, 0xDEADBEEF).unwrap().to_vec());

        // Wide, at a word the command byte could have held on its own: the
        // second byte goes anyway, because the receiver is waiting for it.
        let read = read_frame_at(2, 4 * 5).unwrap();
        assert_eq!(read[..3], [SYNC, 5, 0]);
        assert_eq!(read[3], 5 ^ 0);

        // Wide, past 127: the low seven bits in the command, the rest above.
        let word = 0x123;
        let read = read_frame_at(2, 4 * word).unwrap();
        assert_eq!(read[..3], [SYNC, (word & 0x7F) as u8, (word >> 7) as u8]);

        let write = write_frame_at(2, 4 * word, 0x0A0B0C0D).unwrap();
        assert_eq!(write.len(), 8);
        assert_eq!(write[..3], [SYNC, 0x80 | (word & 0x7F) as u8, (word >> 7) as u8]);
        assert_eq!(write[3..7], [0x0D, 0x0C, 0x0B, 0x0A]);
        assert_eq!(write[7], xor_of(&write[1..7]));

        // And the far ends of each shape.
        assert!(read_frame_at(1, 4 * MAX_WORDS).is_err());
        assert!(read_frame_at(2, 4 * (MAX_WORDS_WIDE - 1)).is_ok());
        assert!(read_frame_at(2, 4 * MAX_WORDS_WIDE).is_err());
    }

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

    // A streamed frame is `A5 | 02 | d0 d1 d2 d3 | xor`, and the xor covers
    // the status byte as well as the data — the same rule the fabric's
    // `replyXor` follows for a read's reply.
    #[test]
    fn a_streamed_frame_parses_as_a_word() {
        let word: u32 = 0x1234_5678;
        let [d0, d1, d2, d3] = word.to_le_bytes();
        let xor = STREAM_STATUS ^ d0 ^ d1 ^ d2 ^ d3;
        assert_eq!(parse_frame(STREAM_STATUS, &[d0, d1, d2, d3, xor], false).unwrap(), Frame::Streamed(word));
        // ...and it parses the same while a *read* is in flight, which is the
        // case that matters: the status byte decides, not the request.
        assert_eq!(parse_frame(STREAM_STATUS, &[d0, d1, d2, d3, xor], true).unwrap(), Frame::Streamed(word));
    }

    // The length of what follows the status byte cannot be known from the
    // request alone once frames are interleaved.
    #[test]
    fn the_status_byte_decides_the_frame_length() {
        assert_eq!(tail_length(STREAM_STATUS, false), 5, "a streamed frame always carries a word");
        assert_eq!(tail_length(STREAM_STATUS, true), 5);
        assert_eq!(tail_length(0, true), 5, "a read's reply carries its value");
        assert_eq!(tail_length(0, false), 1, "a write's reply is status and checksum");
        assert_eq!(tail_length(1, true), 1, "a refusal carries nothing, whatever was asked");
    }

    #[test]
    fn a_replys_frame_still_parses_as_it_did() {
        let value: u32 = 0xDEAD_BEEF;
        let [d0, d1, d2, d3] = value.to_le_bytes();
        assert_eq!(parse_frame(0, &[d0, d1, d2, d3, d0 ^ d1 ^ d2 ^ d3], true).unwrap(), Frame::Reply(Some(value)));
        assert_eq!(parse_frame(0, &[0], false).unwrap(), Frame::Reply(None));
        assert!(matches!(parse_frame(1, &[1], false), Err(SerialError::Refused)));
    }

    #[test]
    fn a_streamed_frame_with_a_bad_checksum_is_malformed() {
        let xor = STREAM_STATUS ^ 1 ^ 2 ^ 3 ^ 4;
        assert!(matches!(parse_frame(STREAM_STATUS, &[1, 2, 3, 4, xor ^ 0xFF], false), Err(SerialError::Malformed(..))));
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
