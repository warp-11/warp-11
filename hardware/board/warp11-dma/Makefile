# warp11-dma kernel module + test tool. Build ON the KV260 (needs
# linux-headers for the running kernel; see README.md).
obj-m := warp11-dma.o

KDIR ?= /lib/modules/$(shell uname -r)/build

all: module w11dma-test

module:
	$(MAKE) -C $(KDIR) M=$(CURDIR) modules

w11dma-test: w11dma-test.c warp11-dma.h
	$(CC) -O2 -Wall -o $@ w11dma-test.c

clean:
	$(MAKE) -C $(KDIR) M=$(CURDIR) clean
	rm -f w11dma-test

.PHONY: all module clean
