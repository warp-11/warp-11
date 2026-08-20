// SPDX-License-Identifier: GPL-2.0
/*
 * warp11-dma — user-space DMA copy proxy for the KV260 (ZynqMP PS GDMA).
 *
 * Purpose: fast bulk READS of fabric-written DDR from userspace without
 * hardware coherency. A CPU mapping of a udmabuf region is uncached or
 * write-combine on this image (~430 ns/word reads); the coherent (HPC +
 * CCI) path needs a boot-time SLCR setting that is fragile to keep true.
 * Instead, a PS GDMA memcpy channel copies the region into a CACHED
 * kernel buffer mmap'd to userspace; the kernel does the buffer-side
 * cache maintenance (the streaming DMA API), so no boot configuration,
 * no coherency hardware, and reads run at cache speed.
 *
 * Derived in spirit from Xilinx's dma-proxy example ("Linux DMA From
 * User Space 2.0"), with the two changes that example does not make:
 * memcpy channels on the PS GDMA instead of AXI-DMA slave channels (so
 * no PL IP and no device-tree plumbing — channels are requested by
 * capability), and streaming-API cached buffers instead of
 * dma_alloc_coherent (whose userspace mapping is uncached precisely
 * when there is no coherency hardware — the case this module exists for).
 *
 * Interface (see warp11-dma.h): /dev/warp11-dma; W11DMA_IOC_INFO reports
 * geometry; mmap at pgoff = buf_index * buf_size/PAGE_SIZE maps one
 * cached buffer; W11DMA_IOC_COPY runs one blocking copy. The source
 * physical address is unvalidated — the device node is root-only, same
 * trust model as /dev/mem, which the warp11 drivers already require.
 *
 * Module params: nbufs (default 4), bufsize (default 1 MiB, power of two
 * of PAGE_SIZE granularity; physically contiguous, allocated at load).
 */

#include <linux/module.h>
#include <linux/miscdevice.h>
#include <linux/fs.h>
#include <linux/mm.h>
#include <linux/slab.h>
#include <linux/uaccess.h>
#include <linux/dmaengine.h>
#include <linux/dma-mapping.h>
#include <linux/completion.h>
#include <linux/mutex.h>

#include "warp11-dma.h"

static int nbufs = 4;
module_param(nbufs, int, 0444);
MODULE_PARM_DESC(nbufs, "number of cached copy buffers");

static int bufsize = 1 << 20;
module_param(bufsize, int, 0444);
MODULE_PARM_DESC(bufsize, "bytes per buffer (page-multiple, power of two)");

struct w11dma {
	struct dma_chan *chan;
	struct device *dma_dev;
	void **bufs; /* nbufs kernel-virtual buffer bases (cached) */
	struct mutex lock; /* serializes transfers (v1: one in flight) */
	struct completion done;
};

static struct w11dma w11;

static void w11dma_callback(void *arg)
{
	complete(arg);
}

static long w11dma_copy(struct w11dma_copy *req)
{
	struct dma_async_tx_descriptor *desc;
	dma_addr_t buf_dma, phys_dma, src, dst;
	enum dma_data_direction buf_dir;
	dma_cookie_t cookie;
	enum dma_status status;
	void *buf_virt;
	long ret = 0;

	if (req->buf >= (u32)nbufs || req->len == 0 ||
	    req->offset > (u32)bufsize || req->len > (u32)bufsize - req->offset)
		return -EINVAL;
	if (req->dir != W11DMA_DIR_PHYS_TO_BUF && req->dir != W11DMA_DIR_BUF_TO_PHYS)
		return -EINVAL;

	buf_virt = w11.bufs[req->buf] + req->offset;
	buf_dir = req->dir == W11DMA_DIR_PHYS_TO_BUF ? DMA_FROM_DEVICE : DMA_TO_DEVICE;

	mutex_lock(&w11.lock);

	/* Streaming mapping of the cached buffer: map cleans/invalidates as
	 * the direction requires; unmap after completion invalidates again
	 * for FROM_DEVICE (drops lines speculated in during the transfer). */
	buf_dma = dma_map_single(w11.dma_dev, buf_virt, req->len, buf_dir);
	if (dma_mapping_error(w11.dma_dev, buf_dma)) {
		ret = -ENOMEM;
		goto out_unlock;
	}
	/* The raw DDR side: fabric-owned memory no CPU cache line covers
	 * (userspace maps it uncached/WC) — no maintenance, map as a
	 * resource. On this SoC (no SMMU translation for the GDMA) this is
	 * the identity mapping. */
	phys_dma = dma_map_resource(w11.dma_dev, (phys_addr_t)req->phys, req->len,
				    buf_dir == DMA_FROM_DEVICE ? DMA_TO_DEVICE : DMA_FROM_DEVICE, 0);
	if (dma_mapping_error(w11.dma_dev, phys_dma)) {
		ret = -ENOMEM;
		goto out_unmap_buf;
	}

	if (req->dir == W11DMA_DIR_PHYS_TO_BUF) {
		src = phys_dma;
		dst = buf_dma;
	} else {
		src = buf_dma;
		dst = phys_dma;
	}

	desc = dmaengine_prep_dma_memcpy(w11.chan, dst, src, req->len,
					 DMA_CTRL_ACK | DMA_PREP_INTERRUPT);
	if (!desc) {
		ret = -EIO;
		goto out_unmap_res;
	}
	reinit_completion(&w11.done);
	desc->callback = w11dma_callback;
	desc->callback_param = &w11.done;
	cookie = dmaengine_submit(desc);
	if (dma_submit_error(cookie)) {
		ret = -EIO;
		goto out_unmap_res;
	}
	dma_async_issue_pending(w11.chan);

	if (!wait_for_completion_timeout(&w11.done, msecs_to_jiffies(1000))) {
		dmaengine_terminate_sync(w11.chan);
		ret = -ETIMEDOUT;
		goto out_unmap_res;
	}
	status = dma_async_is_tx_complete(w11.chan, cookie, NULL, NULL);
	if (status != DMA_COMPLETE)
		ret = -EIO;

out_unmap_res:
	dma_unmap_resource(w11.dma_dev, phys_dma, req->len,
			   buf_dir == DMA_FROM_DEVICE ? DMA_TO_DEVICE : DMA_FROM_DEVICE, 0);
out_unmap_buf:
	dma_unmap_single(w11.dma_dev, buf_dma, req->len, buf_dir);
out_unlock:
	mutex_unlock(&w11.lock);
	return ret;
}

static long w11dma_ioctl(struct file *f, unsigned int cmd, unsigned long arg)
{
	switch (cmd) {
	case W11DMA_IOC_COPY: {
		struct w11dma_copy req;

		if (copy_from_user(&req, (void __user *)arg, sizeof(req)))
			return -EFAULT;
		return w11dma_copy(&req);
	}
	case W11DMA_IOC_INFO: {
		struct w11dma_info info = { .nbufs = nbufs, .buf_size = bufsize };

		if (copy_to_user((void __user *)arg, &info, sizeof(info)))
			return -EFAULT;
		return 0;
	}
	default:
		return -ENOTTY;
	}
}

/* mmap one buffer, CACHED (default vm_page_prot for normal RAM), at
 * pgoff = buf_index * (bufsize >> PAGE_SHIFT). */
static int w11dma_mmap(struct file *f, struct vm_area_struct *vma)
{
	unsigned long pages_per_buf = bufsize >> PAGE_SHIFT;
	unsigned long size = vma->vm_end - vma->vm_start;
	unsigned long buf;

	if (vma->vm_pgoff % pages_per_buf)
		return -EINVAL;
	buf = vma->vm_pgoff / pages_per_buf;
	if (buf >= (unsigned long)nbufs || size > (unsigned long)bufsize)
		return -EINVAL;

	return remap_pfn_range(vma, vma->vm_start,
			       virt_to_phys(w11.bufs[buf]) >> PAGE_SHIFT,
			       size, vma->vm_page_prot);
}

static const struct file_operations w11dma_fops = {
	.owner = THIS_MODULE,
	.unlocked_ioctl = w11dma_ioctl,
	.mmap = w11dma_mmap,
};

static struct miscdevice w11dma_misc = {
	.minor = MISC_DYNAMIC_MINOR,
	.name = "warp11-dma",
	.fops = &w11dma_fops,
	.mode = 0600,
};

static int __init w11dma_init(void)
{
	dma_cap_mask_t mask;
	int order, i, ret;

	if (bufsize < PAGE_SIZE || (bufsize & (bufsize - 1)) || nbufs < 1 || nbufs > 64)
		return -EINVAL;
	order = get_order(bufsize);

	dma_cap_zero(mask);
	dma_cap_set(DMA_MEMCPY, mask);
	w11.chan = dma_request_chan_by_mask(&mask);
	if (IS_ERR(w11.chan)) {
		pr_err("warp11-dma: no DMA_MEMCPY channel (%ld)\n", PTR_ERR(w11.chan));
		return PTR_ERR(w11.chan);
	}
	w11.dma_dev = w11.chan->device->dev;

	w11.bufs = kcalloc(nbufs, sizeof(*w11.bufs), GFP_KERNEL);
	if (!w11.bufs) {
		ret = -ENOMEM;
		goto err_chan;
	}
	for (i = 0; i < nbufs; i++) {
		w11.bufs[i] = (void *)__get_free_pages(GFP_KERNEL, order);
		if (!w11.bufs[i]) {
			ret = -ENOMEM;
			goto err_bufs;
		}
	}
	mutex_init(&w11.lock);
	init_completion(&w11.done);

	ret = misc_register(&w11dma_misc);
	if (ret)
		goto err_bufs;

	pr_info("warp11-dma: %d x %d KiB cached buffers on %s\n",
		nbufs, bufsize >> 10, dma_chan_name(w11.chan));
	return 0;

err_bufs:
	while (--i >= 0)
		free_pages((unsigned long)w11.bufs[i], order);
	kfree(w11.bufs);
err_chan:
	dma_release_channel(w11.chan);
	return ret;
}

static void __exit w11dma_exit(void)
{
	int order = get_order(bufsize);
	int i;

	misc_deregister(&w11dma_misc);
	for (i = 0; i < nbufs; i++)
		free_pages((unsigned long)w11.bufs[i], order);
	kfree(w11.bufs);
	dma_release_channel(w11.chan);
}

module_init(w11dma_init);
module_exit(w11dma_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("warp11");
MODULE_DESCRIPTION("User-space DMA copy proxy (ZynqMP GDMA memcpy, cached buffers)");
