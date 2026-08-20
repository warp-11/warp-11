# Audio

**A WAV file goes through the FPGA and comes back out.** Frames land in PS DDR,
the fabric burst-reads them, runs an 8-band multiband compressor over them, and
writes them back — no codec, no wiring, nothing to plug in.

*Status: on silicon, and bit-exact against the simulator on every frame.*

## Try it without a board

The whole example runs in the cycle-accurate simulator, on the same source that
becomes the bitstream:

```sh
cd hdl
dotnet run -c Release --project Warp11.Effects -- wav in.wav out.wav
```

```
in:  48000 frames, 48000 Hz, 2 ch
out: 47994 frames, peaks 23592/23592 -> 18191/18088
```

Those peaks are the compressor working: the loud passage is pulled down, the
quiet one is not. 16-bit stereo WAV in, 16-bit stereo WAV out — you can listen
to both.

## Then run it on the board

```sh
hardware/board/audio-batch-run.sh in.wav out.wav [board-ip]
```

```
compressed:  15.465 ms for 480000 frames (3.22 cycles/frame @ 100 MHz)
flat copy:   true  (0 frames differ)
FIRST LIGHT OK: a file went through the fabric and came back
```

Ten seconds of 48 kHz stereo in **15.5 ms — 646× faster than real time**, and
the cost per frame does not move with the file: 3.22 cycles a frame at 480,000
frames is the same 3.22 it is at 4,800.

**The two agree exactly.** Board output against simulator output, 47,994 of
47,994 frames identical. That is the property the whole toolkit is for — one
description, two executions, no divergence — and it is checked rather than
asserted.

### Against ffmpeg

The same job, as close as two different implementations can be put: eight bands
split at the same crossovers, a compressor per band, summed back.

```sh
ffmpeg -i in.wav -filter_complex \
  "acrossover=split=320 525 860 1410 2320 3810 6250[b0][b1]...[b7]; \
   [b0]acompressor=threshold=0.09:ratio=4[c0]; ... ; \
   [c0][c1]...[c7]amix=inputs=8:normalize=0[out]" -map "[out]" -f null -
```

| | 10 s of 48 kHz stereo | what the timer wraps |
|---|---:|---|
| **KV260 fabric @ 100 MHz** | **15.5 ms** | the accelerator, start to done |
| ffmpeg on an i7-11800H | 152.8 ms | the filter chain alone — median of 7, with the 42.1 ms decode-and-null baseline subtracted |
| ffmpeg, as you would run it | 194.9 ms | everything, including startup and decode |

**About 10× faster, at 1/23rd the clock.** That is the trade an FPGA makes:
100 MHz against 2.3 GHz, and it wins because the whole chain is laid out in
space — seven biquad crossovers and eight compressors all settling at once,
every cycle, rather than a CPU walking them in sequence.

**And that 10× is an unoptimized number.** The design spends about 69% of every
frame waiting on its write channel, not computing. `dotnet run --project
Warp11.Effects -- batch-perf` runs the same design against DDR models of varying
quality and shows where the time goes:

| DDR model | cycles per frame |
|---|---:|
| ideal — zero latency, always ready | **1.02** |
| read latency 8 | 1.03 |
| read latency 32 | 1.08 |
| write accepts every 4th cycle | 2.02 |
| write response delayed 16 | 2.28 |

The datapath's floor is one frame per cycle, and the board measures 3.22. Reads
cost nothing even at 32 cycles of latency, because the read master bursts 16
beats with 4 outstanding and hides it. The write master does not burst — it
issues one single-beat AXI4 write per beat, so every 16 bytes pays a full
AW + W + B round trip. Bursting it, plus the 166.67 MHz the other accelerators
run at, is worth several times this number; it is on the backlog rather than
done, because changing it means re-verifying every bitstream, and an honest
unoptimized figure is worth more than a rushed optimized one.

Two honest qualifications. The filters are not identical — ffmpeg's
`acrossover` is Linkwitz-Riley and ours is a subtractive split of Butterworth
low-passes, and ffmpeg computes in float where we use fixed point — so this
compares *implementations of the same job*, not the same arithmetic. And the
comparison is compute against compute: getting a 1.9 MB file to the board and
back costs about 300 ms over the network, which is twenty times the processing.
The wire is the bottleneck here exactly as it is for Mandelbrot, and for the
same reason.

## Why it is worth reading

**Bytes in, bytes out — so the answer can be diffed.** Every other audio design
here is a live chain: you plug in a codec, play something, and listen. That
tells you it works, not that it is *right*. This one produces a file, which can
be compared against the simulator sample for sample, and is.

**The sample rate is the handshake, not the clock.** Every stage advances on
`valid && ready` rather than on the clock edge, so the same design behaves
identically at 100 MHz and 166.67 MHz, and stalling the stream *freezes* filter
state rather than smearing it. A biquad clocked by the fabric would change its
cutoff frequency when you retimed the board.

That is not a claim, it is a checked property: `audio: every stage is
stall-independent` runs each stage under six stall patterns and demands the
identical output. It was added because the multiband compressor *failed* it —
two paths through the stage advanced on different events, and the signal shifted
the first time a producer paused. Invisible in every always-ready simulation,
and the reason a jittered memory model now exists.

**Every register resets to a no-op.** The compressor threshold is full scale,
ratio zero, gains unity — so a freshly loaded bitstream passes audio through
untouched, *bit-exactly*. Measured, not assumed, and it is what lets "did the
DDR path work" stay a separate question from "did the compressor work".

## The apps

`audioBatchAxi` is the one above: a block of frames in DDR, through the 8-band
compressor, back to DDR. No codec, no real time, and a result you can diff.

The other four are a live I2S chain over a Pmod I2S2, and they form a bring-up
ladder — the order to build them in, if you have the hardware:

| app | what it proves |
|---|---|
| `audioToneAxi` | the DAC works. A tone generator into the transmitter, playing 440 Hz on load — the smallest thing that makes a sound. |
| `audioPassthruAxi` | the ADC works. Line in to line out, plus two diagnostic taps. |
| `audioGainAxi` | the DSP seam works. One stage between receiver and transmitter. |
| `audioEffectsAxi` | the whole chain: volume → EQ → compressor → limiter. |

The passthru's taps exist because **"no sound" has two completely different
causes** — a silent ADC and a dead transmitter — and on a board you cannot see
which. `receivedCount` moving proves the ADC is clocking; `lastLeft` carrying
something other than zero proves it is hearing.

Only `audioBatchAxi` and `audioToneAxi` have a working bitstream path today;
the two that *receive* audio do not, for one specific reason — see
[To the board](#to-the-board).

## The shape of it

```
i2sMaster              MCLK / SCLK / LRCLK, plus two internal edge ticks
  ├── i2sRx            ADC serial line  → stereo stream   (rising-edge tick)
  │     └── audioGain        volume and mute         combinational
  │     └── audioEqBand      one biquad per channel  combinational + state
  │     └── audioCompressor  envelope and gain       2 cycles
  │     └── audioLimiter     brick wall              combinational
  └── i2sTx            stereo stream → DAC serial line   (falling-edge tick)
axiLiteSlaveOf         the register map, and the Rust layout generated from it
```

A biquad's output is combinational from its input — only the four state
registers advance, and only on the sample handshake — so a cascade costs no
cycles it was not asked for. The compressor's two are the whole chain's latency.

The two edge ticks are the part worth understanding. `i2sMaster` emits one
fabric-cycle pulses on *opposite* SCLK edges — receive on the rising edge, where
the ADC's data is stable, transmit on the falling edge, where the DAC latches.
Splitting them is what lets both directions share one frame without either
sampling the other's transition.

Sample rate is `fabric / (4 · sclkHalfDiv · bitsPerSlot)`: at 100 MHz with the
stock divisors that is **48.828 kHz**, inside codec tolerance. Exactly 48 kHz
wants a 12.288 MHz MMCM clock driving the generator instead.

## Numbers are fixed-point, and each stage picks its own

| value | format | unity |
|---|---|---|
| sample | 24-bit signed | — |
| volume, makeup | Q8.8 | 256 |
| biquad coefficients | Q2.30 | `0x40000000` |
| envelope attack/release | Q1.15 | — |
| compressor gain | Q0.24 | `1 << 24` |

The compressor is the interesting one: its gain reduction is **division-free**.
`ratio` is a slope of gain against how far the envelope exceeds the threshold,
rather than a traditional N:1 knob, which turns the whole gain computer into one
multiply and a clip. Its three multiplies in series are split one per stage,
which is why it costs two cycles where gain and limiter cost none.

Coefficients are designed **host-side**: `rbjDesign` implements the RBJ cookbook
in floating point and `toQ230` quantises it, so nothing on the chip does
transcendental arithmetic.

## Running it

```sh
cd hdl
dotnet run -c Release --project Warp11.Effects              # checks + Verilog line counts
dotnet run -c Release --project Warp11.Effects -- hardware <repo-root>
./run_differential.sh                                       # includes these four
```

`hardware` writes each design's Verilog to `hardware/build/` **and** a Rust
register layout to `runtime/core/src/audio_*_layout.rs`, both from the same
map the slave was elaborated from. Nobody transcribes an offset by hand, which
matters here more than usual: a wrong offset on this board is not a wrong
answer, it is a hang that needs a power cycle.

## To the board

### `audio-batch` — runs today

The batch design needs no codec, so nothing about the Pmod stands between it
and a bitstream — it is the one audio app whose path to the board is clear.

```sh
cd hdl && dotnet run -c Release --project Warp11.Effects -- hardware <repo-root>
cd hardware/vivado && vivado -stack 2000 -mode batch -source build_audiobatch_axi.tcl
cd ../xmutil && ./package_audio-batch.sh
# then load the app and:
ssh ubuntu@<board> 'cd warp11-driver && ./audio_batch_first_light in.wav out.wav'
```

Its Vivado scaffolding is cloned from `gepclusterfs`, which is the other design
here carrying both an AXI-Lite slave and a read+write AXI4 master. The two top
modules have **identical port sets** — checked, not assumed — so the clone is a
pure rename.

One thing its device tree does deliberately, and differently from
`gep-cluster-fs`: it does **not** claim `dma-coherent`, even though its master
also lands on `S_AXI_HPC0_FPD`. Coherence is a property of the transaction, not
the port, and the F# AXI masters drive `AxCACHE=0` (`Stdlib.fs`), so fabric
accesses never snoop the APU caches whichever slave they reach. Claiming
coherence would turn `sync_for_cpu`/`sync_for_device` into no-ops and the host
would read stale audio out of its own cache — silently, and only sometimes. The
driver does the syncs for real instead.

### `audio-tone` — builds, needs a codec to hear

Retargeted 2026-08-19. The scaffolding had been written against the hand-written
Kotlin wrapper, so the TCL sourced `../build/AudioTone_axi.v` and the block
design referenced a module of that name; both now name `AudioToneAxi`, which is
what this project emits. The BD's pin loop was already `{mclk lrclk sclk sdin}`
— exactly the four ports the emitted top drives and the four the XDC constrains
— so nothing structural needed changing.

```sh
cd hardware/vivado && vivado -stack 2000 -mode batch -source build_audio_tone_axi.tcl
cd ../xmutil && ./package_audio_tone.sh
```

### The receiving designs — one real gap

`audioPassthruAxi`, `audioGainAxi` and `audioEffectsAxi` still do not build, and
it is **not** a filename this time.

**The ADC side is unclocked.** The Pmod I2S2's converters are separate chips
with separate clock inputs, and `audio_gain_pins.xdc` / `audio_passthru_pins.xdc`
bind eight pins — `mclk2`, `sclk2`, `lrclk2` for the ADC and `sdout` — where
`codecPins` drives only the DAC's four and takes `sdout` as an input. The ADC's
three clocks would be left undriven. Fixing it means `codecPins` driving all six
clock pins, which is a change to the design rather than to a build script.

There is also no `audio-effects` xmutil app — only `audio-tone`, `audio-gain`
and `audio-passthru` exist, and they predate this code.

## Files

- `Wrappers.fs` — the four register maps and the four designs
- `Main.fs` — the checks, `diff`, and `hardware`
- `../Warp11/Audio.fs` — the stdlib tier all of this is assembled from
- `../Warp11.Designs/Designs.fs` — `audioChain`, `audioTone`, `i2sLoopback`:
  the same parts under the differential oracle, wired for testing rather than
  for a board
