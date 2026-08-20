# warp11-dma — user-space DMA copy proxy (KV260)

Fast bulk **reads** of fabric-written DDR from userspace, with no
coherency hardware and no boot-firmware configuration.

## Why

The host's mapping of a udmabuf region is uncached/write-combine on the
Kria Ubuntu image (~430 ns/word reads — ms-scale for the per-generation
streams the WarpCPU host loop needs). The hardware-coherent alternative
(HPC + CCI snooping + `dma-coherent`) requires `LPD_SLCR.lpd_apu = 0x3`
set at boot — a fragile multi-link chain that silently degrades when any
link is missing. This module trades that for ~200 lines of maintained C:
a ZynqMP PS **GDMA memcpy channel** copies the region into a **cached**
kernel buffer mmap'd to userspace; the kernel does the buffer-side cache
maintenance (streaming DMA API). Works on any boot.

Derived in spirit from Xilinx's `dma-proxy` ("Linux DMA From User Space
2.0"), with the two changes that example lacks: PS GDMA memcpy channels
(no PL IP, no device-tree edits — requested by capability) and cached
streaming buffers instead of `dma_alloc_coherent` (whose mapping is
uncached exactly when there is no coherency hardware).

## Build + install (on the board)

```sh
sudo apt install linux-headers-$(uname -r) dkms   # headers usually present
cd ~/warp11-dma && make                            # module + test tool
sudo insmod warp11-dma.ko                          # one-off load
dmesg | tail -1                                    # "warp11-dma: 4 x 1024 KiB cached buffers on ..."

# Permanent (rebuilds automatically on kernel updates):
sudo mkdir -p /usr/src/warp11-dma-1.0 && sudo cp warp11-dma.c warp11-dma.h Makefile dkms.conf /usr/src/warp11-dma-1.0/
sudo dkms add -m warp11-dma -v 1.0 && sudo dkms install -m warp11-dma -v 1.0
echo warp11-dma | sudo tee /etc/modules-load.d/warp11-dma.conf
```

## Smoke test + benchmark

```sh
sudo ./w11dma-test /dev/udmabuf0 $(cat /sys/class/u-dma-buf/udmabuf0/phys_addr)
```

Verifies a full buffer bit-for-bit, then prints WC-read vs DMA+read
timings at 4 KB..1 MB.

## Interface

`/dev/warp11-dma` (root, 0600 — same trust model as `/dev/mem`):

- `W11DMA_IOC_INFO` → `{nbufs, buf_size}` (defaults 4 × 1 MiB; module
  params `nbufs=`/`bufsize=`).
- `mmap(len, pgoff = buf_index * buf_size/PAGE_SIZE)` → cached view of
  buffer `buf_index`.
- `W11DMA_IOC_COPY {phys, buf, offset, len, dir}` — blocking single copy;
  `dir` 0 = DDR→buffer (read path), 1 = buffer→DDR. Source/destination
  `phys` is unvalidated (root-only device).

JVM side: `org.warp11.runtime.Warp11Dma` is the FFM wrapper (open + INFO
ioctl, per-buffer cached mmap, blocking COPY in both directions);
`org.warp11.runtime.DmaCopyBuffer` wraps any `DmaBuffer` so bulk
`readWordsInto` goes through a copy buffer while writes stay on the
write-combine mapping. `Warp11Dma.openOrNull()` returns null when the
module isn't loaded, so callers keep a slow-but-correct fallback.

## Measured (KV260, 5.15.0-1070-xilinx-zynqmp, 2026-07-23)

Correctness: 1 MiB verified bit-exact (pattern written via the WC
mapping, DMA-copied, memcmp'd). WC-read vs DMA-copy-then-cached-read:

| size | WC read | DMA+read | speedup |
|---|---|---|---|
| 4 KB | 56.3 µs | 21.4 µs | 2.6× |
| 16 KB | 224.2 µs | 32.2 µs | 7.0× |
| 64 KB | 895.6 µs | 72.6 µs | 12.3× |
| 256 KB | 3587.4 µs | 283.8 µs | 12.6× |
| 1 MB | 14324.4 µs | 898.5 µs | 15.9× |

Fixed overhead ≈ 20 µs/copy — below ~4 KB it dominates, so keep
small/frequent reads (status registers) on the AXI-Lite path. The
DMA+read column includes fully reading the copied data from the cached
buffer.

Gotcha that bit during bring-up (now baked into the test): u-dma-buf's
mmap is CACHED unless the fd is opened with `O_SYNC` — pattern writes
through a cached mapping sit dirty in the CPU caches where no DMA
master sees them. Warp 11's `UdmabufDmaBuffer` already opens O_SYNC.
