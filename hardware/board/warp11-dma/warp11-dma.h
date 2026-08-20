/* SPDX-License-Identifier: GPL-2.0 */
/*
 * warp11-dma — user-space DMA copy proxy for the KV260 (ZynqMP PS GDMA).
 *
 * Shared ioctl ABI between the kernel module and userspace (the C test
 * tool and warp11's :runtime FFM wrapper).
 *
 * The module owns N physically-contiguous CACHED kernel buffers, mmap'd
 * into userspace at offset (buf_index * buf_size). One blocking ioctl
 * copies between a raw physical DDR address (a udmabuf region the fabric
 * writes) and a buffer, using a ZynqMP GDMA memcpy channel; the kernel
 * performs the cache maintenance on the buffer side, so userspace reads
 * run at cached speed with no coherency hardware required.
 */
#ifndef WARP11_DMA_H
#define WARP11_DMA_H

#include <linux/types.h>
#include <linux/ioctl.h>

#define W11DMA_DIR_PHYS_TO_BUF 0u /* fabric-written DDR -> cached buffer (read path) */
#define W11DMA_DIR_BUF_TO_PHYS 1u /* cached buffer -> DDR (write path) */

struct w11dma_copy {
	__u64 phys;   /* raw DDR physical address (e.g. udmabuf phys + offset) */
	__u32 buf;    /* buffer index, 0..nbufs-1 */
	__u32 offset; /* byte offset inside the buffer */
	__u32 len;    /* bytes to copy */
	__u32 dir;    /* W11DMA_DIR_* */
};

struct w11dma_info {
	__u32 nbufs;
	__u32 buf_size;
};

#define W11DMA_IOC_MAGIC 'W'
#define W11DMA_IOC_COPY _IOW(W11DMA_IOC_MAGIC, 1, struct w11dma_copy)
#define W11DMA_IOC_INFO _IOR(W11DMA_IOC_MAGIC, 2, struct w11dma_info)

#endif /* WARP11_DMA_H */
