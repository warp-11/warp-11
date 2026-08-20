#!/usr/bin/env bash
#
# Persistent udev rule so /dev/udmabuf* is user-accessible (0666).
#
# The u-dma-buf module creates the device node root-only (crw------- root root)
# by default. The mandel/gol servers run as the `ubuntu` user and mmap the DMA
# framebuffer directly, so they need read/write on the node — otherwise a render
# fails with "open(/dev/udmabuf0) failed (-1). Run as root?". This rule fixes it
# for good (survives reboots / module reloads), and the loop below also applies
# it to any node that already exists this boot.
#
# Run once as root:
#   sudo bash setup-udmabuf-udev.sh
set -euo pipefail

# SUBSYSTEM, not KERNEL=="udmabuf[0-9]*": a buffer declared by an app's own
# device-tree overlay gets its device-name as the node name (udmabuf-gep-cluster-fs),
# which never matched the numbered pattern — so exactly the buffers an app
# declares for itself stayed root-only while the boot-time ones were open.
RULE=/etc/udev/rules.d/99-udmabuf.rules
echo 'SUBSYSTEM=="u-dma-buf", MODE="0666"' > "$RULE"
echo "wrote $RULE:"
cat "$RULE"

# The sync_* sysfs attrs (offset/size/direction/for_cpu/for_device) are how an
# unprivileged reader owns cache coherency on the cached mmap path — measured
# 0.86 ms/frame vs 10.6 ms uncached on the Mandelbrot readback. They are
# root-only on module load; sysfs attrs take no MODE=, so chmod them from a
# RUN rule each time a u-dma-buf device appears.
SYNC_RULE=/etc/udev/rules.d/99-udmabuf-sync.rules
SYNC_ATTRS="sync_offset sync_size sync_direction sync_for_cpu sync_for_device"
printf 'SUBSYSTEM=="u-dma-buf", ACTION=="add", RUN+="/bin/sh -c '\''cd /sys/class/u-dma-buf/%%k && chmod 0666 %s'\''"\n' \
    "$SYNC_ATTRS" > "$SYNC_RULE"
echo "wrote $SYNC_RULE:"
cat "$SYNC_RULE"

udevadm control --reload
udevadm trigger

# udev trigger may not re-chmod an already-created node — apply directly too.
for c in /sys/class/u-dma-buf/*; do
    [ -e "$c" ] || continue
    d="/dev/$(basename "$c")"
    [ -e "$d" ] && chmod 666 "$d"
    for a in $SYNC_ATTRS; do [ -e "$c/$a" ] && chmod 0666 "$c/$a"; done
done

echo "--- result ---"
ls -la /dev/udmabuf* 2>/dev/null || echo "(no udmabuf device present — is u-dma-buf loaded?)"
ls -la /sys/class/u-dma-buf/udmabuf0/sync_* 2>/dev/null || echo "(no u-dma-buf sysfs class — is the module loaded?)"
