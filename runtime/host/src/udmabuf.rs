//! A `u-dma-buf` arena: the physically contiguous buffer a fabric master and
//! the host both reach, found by the name the app's overlay gave it.
//!
//! The mapping is cached unless the file is opened `O_SYNC`, and the fabric's
//! masters drive AxCACHE=0 and do not snoop — so a caller either opens with
//! `O_SYNC`, or keeps the cached mapping and calls [`Udmabuf::sync_for_device`]
//! before a run and [`Udmabuf::sync_for_cpu`] after it. The sync attributes
//! are writable by non-root only after `hardware/board/setup-udmabuf-udev.sh`;
//! a failure to write one is returned, not swallowed, because a missing udev
//! rule otherwise shows up as stale audio with nothing to explain it.

use std::fs;
use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct Udmabuf {
    /// The sysfs entry's name (`udmabuf-gain-patch-batch`).
    pub name: String,
    /// The character device to mmap.
    pub dev_path: PathBuf,
    /// Where the arena sits for the fabric.
    pub phys_addr: u64,
    /// The arena's size in bytes.
    pub size: usize,
}

const SYSFS: &str = "/sys/class/u-dma-buf";

impl Udmabuf {
    /// The arena whose device name is exactly `name`, if the overlay made one.
    pub fn find(name: &str) -> std::io::Result<Option<Udmabuf>> {
        let entries = match fs::read_dir(SYSFS) {
            Ok(entries) => entries,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(e),
        };
        for entry in entries.flatten() {
            let entry_name = entry.file_name().to_string_lossy().to_string();
            if entry_name != name {
                continue;
            }
            let phys = fs::read_to_string(entry.path().join("phys_addr"))?;
            let size = fs::read_to_string(entry.path().join("size"))?;
            let phys_addr = u64::from_str_radix(phys.trim().trim_start_matches("0x"), 16)
                .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
            let size = size
                .trim()
                .parse::<usize>()
                .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
            return Ok(Some(Udmabuf {
                name: entry_name.clone(),
                dev_path: PathBuf::from(format!("/dev/{entry_name}")),
                phys_addr,
                size,
            }));
        }
        Ok(None)
    }

    fn attr(&self, name: &str, value: &str) -> std::io::Result<()> {
        fs::write(format!("{SYSFS}/{}/{name}", self.name), value)
    }

    fn range(&self, offset: usize, size: usize) -> std::io::Result<()> {
        self.attr("sync_offset", &offset.to_string())?;
        self.attr("sync_size", &size.to_string())
    }

    /// Push the CPU's writes in `offset..offset+size` out to DDR before the
    /// fabric reads them.
    pub fn sync_for_device(&self, offset: usize, size: usize) -> std::io::Result<()> {
        self.range(offset, size)?;
        self.attr("sync_direction", "1")?;
        self.attr("sync_for_device", "1")
    }

    /// Drop the CPU's stale view of `offset..offset+size` after the fabric
    /// wrote it.
    pub fn sync_for_cpu(&self, offset: usize, size: usize) -> std::io::Result<()> {
        self.range(offset, size)?;
        self.attr("sync_direction", "2")?;
        self.attr("sync_for_cpu", "1")
    }
}

/// The uio device whose name is `name`, as the overlay's node named it.
pub fn find_uio(name: &str) -> std::io::Result<Option<PathBuf>> {
    let entries = match fs::read_dir("/sys/class/uio") {
        Ok(entries) => entries,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e),
    };
    for entry in entries.flatten() {
        if let Ok(n) = fs::read_to_string(entry.path().join("name")) {
            if n.trim() == name {
                return Ok(Some(PathBuf::from(format!(
                    "/dev/{}",
                    entry.file_name().to_string_lossy()
                ))));
            }
        }
    }
    Ok(None)
}
