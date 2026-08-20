// SPDX-License-Identifier: GPL-2.0
/*
 * warp11-dma smoke test + benchmark (runs on the KV260, as root).
 *
 * Correctness: writes a pattern into a udmabuf region through its
 * (write-combine) mapping, DMA-copies it into the module's cached buffer,
 * and memcmp-verifies. Benchmark: times DMA-copy-then-read against direct
 * uncached/WC reads of the same region at several sizes.
 *
 *   sudo ./w11dma-test /dev/udmabuf0 <udmabuf_phys>
 *
 * (phys from /sys/class/u-dma-buf/udmabuf0/phys_addr)
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <fcntl.h>
#include <unistd.h>
#include <time.h>
#include <sys/mman.h>
#include <sys/ioctl.h>

#include "warp11-dma.h"

static double now_us(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return ts.tv_sec * 1e6 + ts.tv_nsec / 1e3;
}

int main(int argc, char **argv)
{
	if (argc != 3) {
		fprintf(stderr, "usage: %s /dev/udmabufN <phys_addr>\n", argv[0]);
		return 1;
	}
	uint64_t phys = strtoull(argv[2], NULL, 0);

	int dfd = open("/dev/warp11-dma", O_RDWR);
	if (dfd < 0) { perror("open /dev/warp11-dma"); return 1; }
	struct w11dma_info info;
	if (ioctl(dfd, W11DMA_IOC_INFO, &info)) { perror("INFO"); return 1; }
	printf("warp11-dma: %u bufs x %u KiB\n", info.nbufs, info.buf_size >> 10);

	uint8_t *cached = mmap(NULL, info.buf_size, PROT_READ | PROT_WRITE, MAP_SHARED, dfd, 0);
	if (cached == MAP_FAILED) { perror("mmap cached buf"); return 1; }

	/* O_SYNC is what makes u-dma-buf hand out the uncached/WC mapping —
	 * without it the mapping is CACHED and writes sit dirty in the CPU
	 * caches where no DMA master can see them. */
	int ufd = open(argv[1], O_RDWR | O_SYNC);
	if (ufd < 0) { perror("open udmabuf"); return 1; }
	size_t span = info.buf_size;
	uint8_t *wc = mmap(NULL, span, PROT_READ | PROT_WRITE, MAP_SHARED, ufd, 0);
	if (wc == MAP_FAILED) { perror("mmap udmabuf"); return 1; }

	/* Pattern in DDR via the WC mapping; DSB drains the write-combine
	 * buffers to DRAM before the DMA engine reads. */
	for (size_t i = 0; i < span / 4; i++)
		((volatile uint32_t *)wc)[i] = (uint32_t)(0x9E3779B9u * i ^ 0xC0FFEE11u);
	__asm__ volatile("dsb sy" ::: "memory");

	struct w11dma_copy req = { .phys = phys, .buf = 0, .offset = 0,
				   .len = info.buf_size, .dir = W11DMA_DIR_PHYS_TO_BUF };
	if (ioctl(dfd, W11DMA_IOC_COPY, &req)) { perror("COPY"); return 1; }
	if (memcmp(cached, (void *)wc, span) != 0) {
		size_t bad = 0, first = span;
		for (size_t i = 0; i < span / 4; i++) {
			if (((uint32_t *)cached)[i] != ((volatile uint32_t *)wc)[i]) {
				if (first == span) first = i;
				bad++;
			}
		}
		fprintf(stderr, "FAIL: %zu/%zu words differ, first at word %zu: got %08x want %08x\n",
			bad, span / 4, first,
			((uint32_t *)cached)[first], ((volatile uint32_t *)wc)[first]);
		return 1;
	}
	printf("correctness: %zu bytes verified OK\n", span);

	printf("%10s | %12s | %12s | %10s\n", "size", "WC read", "DMA+read", "speedup");
	size_t sizes[] = { 4 << 10, 16 << 10, 64 << 10, 256 << 10, 1 << 20 };
	for (unsigned s = 0; s < sizeof(sizes) / sizeof(*sizes); s++) {
		size_t len = sizes[s];
		if (len > span) break;
		int iters = len >= (256 << 10) ? 20 : 100;
		volatile uint64_t sink = 0;

		double t0 = now_us();
		for (int it = 0; it < iters; it++)
			for (size_t i = 0; i < len / 8; i++)
				sink += ((volatile uint64_t *)wc)[i];
		double wc_us = (now_us() - t0) / iters;

		req.len = len;
		t0 = now_us();
		for (int it = 0; it < iters; it++) {
			if (ioctl(dfd, W11DMA_IOC_COPY, &req)) { perror("COPY"); return 1; }
			for (size_t i = 0; i < len / 8; i++)
				sink += ((volatile uint64_t *)cached)[i];
		}
		double dma_us = (now_us() - t0) / iters;

		printf("%7zu KB | %9.1f us | %9.1f us | %9.1fx\n",
		       len >> 10, wc_us, dma_us, wc_us / dma_us);
		(void)sink;
	}
	return 0;
}
