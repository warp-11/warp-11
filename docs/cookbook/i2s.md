# How do I send and receive audio over I2S?

**You want:** samples off a converter or a microphone, and samples back out to a
DAC — with your design owning the clock and the rest of it looking like ordinary
streams.

Three modules do the whole job. One generates the clocks, one turns the incoming
serial line into a stereo stream, one turns a stereo stream back into a serial
line. Everything between them is a [stream](../streams.md), so a filter, a gain
stage or a compressor drops in as a `|>`.

## What I2S is, in one section

**Inter-IC Sound** — a three-wire serial bus for moving PCM audio between chips,
from Philips in 1986 and still what almost every audio converter speaks. It
carries one stereo pair continuously, forever; there are no packets, no
addresses and no acknowledgement. If you are clocking, audio is flowing.

| wire | also called | what it does |
|---|---|---|
| **bit clock** | SCK, SCLK, BCLK | one pulse per bit. Data changes on its falling edge and is sampled on its rising edge |
| **word select** | WS, LRCLK, LRCK | which channel is on the wire right now. **Low is left, high is right** |
| **serial data** | SD, SDIN/SDOUT | the bits, MSB first, two's complement. One line *per direction* |

One device is the **master** and generates the two clocks; everything else
follows them. Data lines are driven by whoever is sourcing — a converter drives
its own output line, a DAC only listens. In Warp 11 your design is always the
master, which is why nothing here recovers a clock.

The sample rate is just the word-select rate: **one full cycle of WS is one
stereo frame**, so Fs is however often WS repeats.

### The one quirk worth knowing

Data starts **one bit clock after WS changes**. That single bit of delay is the
difference between I2S and the otherwise identical "left-justified" format, and
it is the source of most interoperability confusion in the field.

```
        ┌──┐  ┌──┐  ┌──┐  ┌──┐  ┌──┐  ┌──┐  ┌──┐
SCK  ───┘  └──┘  └──┘  └──┘  └──┘  └──┘  └──┘  └──
           ▲                     ▲
           │ falling: driver changes the line
           └────────── rising: receiver samples it

WS   ──────┐
   (right) └──────────────── left slot ────────────

SD   ──pad─┼──────┬─────┬─────┬─────┬─────┬─────┬──
           │  ×   │ b23 │ b22 │ b21 │ b20 │ b19 │
           │  ▲   │  ▲
           │  │   └─ MSB, one bit clock late
           └──┴───── this bit time carries nothing
```

That empty bit time is exactly what `i2sRx` treats as its no-data transition
tick, and it is why a fabric loopback shows `output = input << 1` further down
this page.

### Slots are usually wider than samples

A slot is a fixed number of bit clocks — 32 here — and the sample need not fill
it. Warp 11 carries **24-bit samples in 32-bit slots**, so eight zero bits pad
the tail of each channel. Receivers take the leading bits they want and ignore
the rest, which is why a 24-bit design and a 16-bit converter can share a wire.

### MCLK is not part of I2S

Many codecs additionally want a **master clock** — a much faster oversampling
clock, conventionally 256 × Fs — to run their internal converters and digital
filters. It is a separate signal that the I2S standard says nothing about.
`i2sMaster` generates one because the Pmod codecs need it; MEMS microphones and
self-clocking DACs do not, and there `mclk` simply goes unread.

## How Warp 11 does it

Because the design is the master, nothing has to recover a clock — there is no
PLL and no synchroniser anywhere in this. The consequence shows up in the module
boundaries: the framers are *told* when to sample and when to drive, by the
generator that owns both edges, rather than working it out from the line.

```
                    ┌──────────────────────────────────────────┐
                    │  i2sMasterDefault "I2sMaster"            │
                    │                                          │
   fabric clock ───►│  mclk  sclk  lrclk   sclkRxTick  sclkTxTick
                    └────┬─────┬─────┬───────────┬──────────┬──┘
                         │     │     │           │          │
                         │     │     ├───────────┼──────────┤
                         ▼     ▼     ▼           │          │
                        pins to the converter    │          │
                                                 │          │
     sdout  ──────────►┌──────────────────┐◄─────┘          │
   (from the ADC)      │  i2sRx           │                 │
                       └────────┬─────────┘                 │
                                │ Stream<left, right>       │
                          your DSP, or nothing              │
                                │                           │
                       ┌────────▼─────────┐◄────────────────┘
                       │  i2sTx           │
                       └────────┬─────────┘
                                │
     sdin  ◄────────────────────┘
   (to the DAC)
```

The two `*Tick` outputs are the part worth understanding, because getting them
backwards is the one wiring mistake nothing catches for you. Each is a single
fabric cycle, and they land on **opposite SCLK edges**: `sclkRxTick` on the
rising edge, where the converter's data is stable, and `sclkTxTick` on the
falling edge, where the DAC latches and LRCLK turns. Splitting them is what lets
receive and transmit share one frame without either sampling the other's
transition.

## The link — pins in, streams out

**Reach for this first.** `i2sLink` is the whole front end as one thing: it
declares the board's pins, builds the clock generator and both framers, routes
the edge ticks and drives every clock pin. What a design sees is two streams.

It is the same split as the register map — a declarer for the io factory, a
builder for the body:

```fsharp
defModuleClocked
    axiClock
    "AudioWdrcMems_axi"
    (fun p -> (axiLiteSlavePorts p aidMap.apertureAddrWidth, i2sPins p SharedBus))
    (fun (slavePorts, pins) ->
        let regs = regMapSlave slavePorts aidMap
        let i2s = i2sLink "i2s" pins kv260.fabricHz 48_828 32

        i2s.input
        |> audioGain "AudioGain" "gain" (regs.value aidRegs.volume) (regs.value aidRegs.mute)
        |> i2s.send)
```

That is a complete board design. No clock, no tick, no pin appears in it.

| you get | |
|---|---|
| `i2s.input` | the stereo stream off the converter |
| `i2s.send` | the other end. Call it exactly once |

Both are ordinary stream values, so a design reads as a pipeline from the
converter to the converter — `i2s.input |> … |> i2s.send` — and adding a stage
is adding a line in the middle.

**The reason to prefer it is not brevity.** The two edge ticks never reach the
caller, so the one mistake that this interface can otherwise invite — routing
`sclkTxTick` to the receiver — stops being expressible. That bug passes
elaboration, passes every stream check, and **cannot be caught in simulation at
all**, because it moves the receiver from the edge where the line is stable to
the edge where it changes and a zero-delay model has no opinion about that. It
is a bench failure. Here it cannot be written.

### The three pinouts

A design must declare exactly the pins its `.xdc` binds, so the pin set is a
board fact rather than a preference:

| | pins | for |
|---|---|---|
| `i2sPins p SharedBus` | `sd_in` `bclk` `ws` `sd_out` | MEMS microphones and a self-clocking DAC on one shared bus, no MCLK |
| `i2sPins p SeparateCodecs` | `sdout` `mclk` `sclk` `lrclk` `sdin` `mclk2` `sclk2` `lrclk2` | a Pmod I2S2 — two chips on two rows, each with its own clock trio |
| `i2sTxPins p <pinout>` | the same, minus the input | a DAC with nothing to listen to. The link type has no `input` field, so there is nothing to leave dangling |

For a transmit-only link the builder is `i2sTxLink` and the one field is
`sendOnly`.

## Underneath: the three modules

| you call | you hand it | you get back |
|---|---|---|
| `i2sMasterDefault "I2sMaster"` | nothing — `instanceNamed` it | a record of pins and ticks |
| `i2sRx "I2sRx" "rx" rxTick lrclk sdout` | the tick, the word clock, the line | `Stream<Expr * Expr>` |
| `i2sTx "I2sTx" "tx" txTick lrclk stream` | the tick, the word clock, a stream | the serial line, an `Expr` |

Two names at each call: the **module** name, then the **instance** name. The
clock generator takes only the first, because it is a bare `TypedModule` with no
call wrapper — hence `instanceNamed`.

## Hand-wiring it

`i2sLink` is these three modules and nothing else — the living check asserts the
two emit **byte-identical** Verilog, so choosing one over the other is invisible
below the call site. Reach for the pieces directly when you need something the
link does not offer: two links in one design at different rates, a receiver with
no transmitter, or a pin set neither pinout describes.

A hand-wired pass-through, for comparison:

```fsharp
type CodecPins =
    { mclk: Output
      sclk: Output
      lrclk: Output
      sdin: Output }

let passthru =
    defModule
        "Passthru"
        (fun p ->
            ({ mclk = p.outPort "mclk" 1
               sclk = p.outPort "sclk" 1
               lrclk = p.outPort "lrclk" 1
               sdin = p.outPort "sdin" 1 },
             p.inPort "sdout" 1))
        (fun (pins, sdout) ->
            let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster")

            let serial =
                i2sRx "I2sRx" "rx" clocks.sclkRxTick clocks.lrclk sdout
                |> i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk

            clocks.mclk ==> pins.mclk
            clocks.sclk ==> pins.sclk
            clocks.lrclk ==> pins.lrclk
            serial ==> pins.sdin)
```

That is the whole shape. Everything below is a variation on it.

**The pins are a record because the boundary seals when the io factory
returns** — a port declared from body depth is an elaboration error. Declaring
them in a named type and driving them in the body is the pattern the shipped
audio designs use, and it is what lets one bundle serve both halves.

## Receiving

This is what `i2s.input` is, and everything here applies to it equally.

`i2sRx` hands you a `Stream<Expr * Expr>`: the payload is `(left, right)`, each
`sampleWidth` = **24 bits, two's complement**, and `valid` pulses for one fabric
cycle when a stereo pair completes.

```fsharp
let received = i2sRx "I2sRx" "rx" clocks.sclkRxTick clocks.lrclk sdout
let left, right = received.payload

let count = reg "received_count" 32
let lastLeft = reg "last_left" sampleWidth

If received.valid (fun () ->
    count + lit 1UL 32 ==> count
    left ==> lastLeft)
```

Those two registers are worth having from the first bring-up. A count that
climbs at about 48.8k/s says the fabric clock is alive and the frame is
completing; a last-sample tap says the line is carrying something other than
silence. On a board you cannot see either, and "no sound" has too many causes.

**The receiver does not stall.** It ignores `ready` — at 48 kHz against a fabric
clock three orders of magnitude faster, a consumer has around a thousand cycles
to take each sample, so backpressure would be machinery for a case that does not
arise. If your consumer genuinely needs to hold beats, put a `streamFifo` after
the receiver rather than expecting the framer to wait.

## Sending

`i2sTx` takes a `Stream<Expr * Expr>` and hands back the serial line as an
`Expr` you drive onto a pin. Through a link this is `i2s.send`, which does the
driving for you.

Unlike the receiver, **the transmitter does honour backpressure**: a one-slot
pending buffer holds the next sample, `ready` is high whenever that slot is
empty, and the slot commits to the shift registers on entry to a new left slot.
That is what lets a producer hand over a sample at any point in the frame
without tearing one in half.

Three ways to get a stream to feed it:

**Transform one you received.** Rebuilding the record is the idiom — the
handshake travels inside the value, so keeping it and replacing the payload is
the whole edit:

```fsharp
let gated =
    { received with
        payload = (mux muted (lit 0UL sampleWidth) left,
                   mux muted (lit 0UL sampleWidth) right) }
```

**Generate one.** `toneGenerator` is a source with no input at all — the
smallest thing that makes noise on a board, and the right first test when you
have a DAC but no converter yet:

```fsharp
let tone = toneGenerator "ToneGenerator" "tone" enable step
i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk tone ==> pins.sdin
```

**Take one from your module's ports.** `streamSource` turns a declared input
group into a `Stream`, which is how a design under test gets samples from the
simulator rather than from a converter:

```fsharp
(fun p -> streamInputPorts p "in" sampleLayout)
(fun inPorts -> i2sTx "I2sTx" "tx" txTick lrclk (streamSource inPorts) ==> sdin)
```

`sampleLayout` is the public stereo layout — `left` and `right`, 24 bits each —
so a stream you build by hand is `{ payload = (l, r); valid = v; ready = r; layout = sampleLayout }`.

## Putting something in between

Because both ends are streams, a processing stage is a pipe. Stages from the
library chain directly:

```fsharp
let i2s = i2sLink "i2s" pins kv260.fabricHz 48_828 32

i2s.input
|> audioGain "AudioGain" "gain" volume mute
|> audioLimiter "AudioLimiter" "limiter" threshold
|> i2s.send
```

### Writing your own stage

A stage is a function from a stream to a stream. Here is one that halves the
volume:

```fsharp
let reduceVolume (s: Stream<Expr * Expr>) : Stream<Expr * Expr> =
    let left, right = s.payload
    { s with payload = (sra 1 left, sra 1 right) }
```

and here it is in a complete design:

```fsharp
defModule
    "I2sLinkHalfVolume"
    (fun p -> i2sPins p SharedBus)
    (fun pins ->
        let i2s = i2sLink "i2s" pins kv260.fabricHz 48_828 32
        i2s.input |> reduceVolume |> i2s.send)
```

Two things in those four lines are worth pulling out, because both generalise
to every stage you will write.

**Rebuild the record; do not construct a new stream.** The handshake travels
*inside* the stream value — `ready` flows backwards through the same record
that carries `payload` and `valid` forwards. So `{ s with payload = … }` keeps
the handshake correct by construction, and it is why a stage can be a plain
function rather than something that has to be wired.

**`sra`, not `shr`.** Halving a sample is an *arithmetic* shift: the sign bit
must fill from the top. A logical shift halves every positive sample correctly
and turns every negative one into a large positive one — which destroys the
audio while still looking plausible on a peak meter. `sra` reads its operand as
signed whatever it was declared as, so this needs no cast.

It also floors rather than truncating toward zero, so -1 halves to -1. That is
correct, and it is the value worth putting in a test: it is the one that tells
you which shift the emitter chose.

Anything heavier than this — a filter, a compressor, something that takes
cycles — is a module rather than a function, and it presents a `Stream` for the
same reason everything else here does. `Warp11.Designs` has `i2sLinkHalfVolume`
registered in the debugger if you want to step through this one.

The values `volume`, `mute` and `threshold` are ordinary `Expr`s, which in a
board design come from a [register map](register-map.md) — `regs.value
myRegs.volume` — so the host can turn them while the audio runs.

A stage that returns more than a stream binds rather than pipes.
`multibandCompressor` hands back `(stream, envelope)`, the envelope being its
registered max tree over the band detectors, which is worth exposing as a
read-only register during bring-up.

## Choosing the sample rate

`i2sMaster name mclkHalfDiv sclkHalfDiv bitsPerSlot` is three nested dividers,
and the sample rate falls out of two of them:

```
Fs = fabric / (4 * sclkHalfDiv * bitsPerSlot)
```

`i2sMasterDefault` is `i2sMaster name 4 16 32`, so on a 100 MHz fabric clock:

| | |
|---|---|
| MCLK | toggles every 4 cycles → 12.5 MHz, which is 256 × Fs |
| SCLK | toggles every 16 cycles → period 32 |
| LRCLK | toggles every 1024 cycles → **period 2,048 fabric cycles** |
| Fs | 100 MHz / 2048 = **48.828 kHz** |

That is inside every converter's tolerance and it is what the shipped designs
use. It is *not* exactly 48 kHz, and it cannot be: 100 MHz does not divide into
48 000. **An exact rate wants an MMCM giving this module a 12.288 MHz clock
instead** — worth doing if you are matching an external clock domain, and not
worth doing otherwise.

### Naming the rate instead of the divisors

Divisors chosen by hand are silently wrong the moment the design moves to a
board with a different fabric clock — the numbers still elaborate, the frame
still looks right in simulation, and the converter is the only thing that
disagrees. `i2sMasterHz` takes the clock and the rate instead, derives the
divisors, and checks what it actually achieved:

```fsharp
// the stock case — picks 4 / 16 / 32, byte-identical to i2sMasterDefault
instanceNamed "clocks" (i2sMasterHz kv260.fabricHz 48_828 32 "I2sMaster")
```

`kv260` is a `Board` — a fabric frequency plus a host bus binding, and the
record an application holds per target. **The constructors take the frequency,
not the board**, so a fact about targets stays out of the signature of
everything that divides a clock; a call site with a board in hand writes
`kv260.fabricHz` and one without writes the number.

Either way it is the rate that is named rather than the divisors, which is what
stops the design going stale on a board with a different clock.

A rate the clock cannot make within 1% is an **elaboration error**, naming the
divisor it tried and how far out it landed:

```
i2sMasterHz 'I2sMaster': 100000000 Hz cannot make 48000 Hz at 32 bits per slot
— the nearest divisor (16) gives 48828.125 Hz, off by 1.73%.
```

Which is the same fact the paragraph above states, moved from a comment into
the build. `sampleRateOf fabricHz sclkHalfDiv bitsPerSlot` computes the rate on
its own if you want to assert it in a check.

The achieved rate is deliberately not returned to the caller. A design that
threads it onward is re-deriving frame timing that belongs to the framers.

The 2,048 number is the one to remember, because it sets the scale of every
simulation you will write: one stereo frame is 2,048 ticks, so a test that
covers a few frames runs about 12,000.

## Wiring it to a board

Two physical shapes turn up, and they need different port sets.

**A codec Pmod — two chips.** The Pmod I2S2 carries a separate ADC and DAC on
separate connector rows, and **each has its own MCLK/SCLK/LRCK input**, so a
design that both sends and receives declares eight pins: the four for the DAC,
three more clocks for the ADC, and the data line coming back. They are all
driven from the *same* generator:

```fsharp
clocks.mclk ==> pins.mclk      ;  clocks.mclk ==> adcPins.mclk2
clocks.sclk ==> pins.sclk      ;  clocks.sclk ==> adcPins.sclk2
clocks.lrclk ==> pins.lrclk    ;  clocks.lrclk ==> adcPins.lrclk2
```

**A MEMS front end — one shared bus.** Digital microphones and a DAC like the
UDA1334A share one clock bus and need no MCLK at all (the mics do not want one,
and the DAC makes its own), so the whole interface is four pins: `bclk`, `ws`,
`sd_in`, `sd_out`. `clocks.mclk` simply goes unread — **an instance output
nobody consumes is legal**; only a *module* output nobody drives is an error.

Whichever shape you have, **declare exactly the pins your `.xdc` binds**. A
constraint file binding a pin the design never declared is a pin left floating,
and the design will not build for the board.

One constraint detail that is silently ignored if you get it wrong: on
UltraScale+ the weak pulldown on an input data line is **`PULLTYPE PULLDOWN`**,
not the legacy `PULLDOWN TRUE`. With it an unused slot reads a clean zero;
without it you get a floating input reading `0xFFFFFF` and looking exactly like
a broken microphone.

**Never instantiate two clock generators.** Receive and transmit share one
frame, so they must share one generator — a second would drift against the
first, and the symptom is intermittent noise rather than an obvious failure.

## Testing it without hardware

The Sim can close the link in software — but **only if there is a signal in it**.

The obvious loop does not work, and it is worth knowing why before you write it.
Feeding the output line back into the input line over a *pass-through* circulates
silence: from reset the line is zero, the receiver decodes zero, the transmitter
sends zero, and nothing ever breaks the symmetry. Measured over eight frames, the
decoded sample never leaves `0` — **and a design with the two edge ticks
deliberately swapped produces exactly the same result**, so the check cannot
fail and proves nothing.

Two loops that do work.

**Put a source in the design.** A tone generator into the transmitter gives the
link something to carry, and then the software loop is meaningful:

```fsharp
let sim = Sim toneLoop.def
let mutable line = 0UL
let taps = ResizeArray<uint64>()

for _ in 1 .. 12 * 2048 do
    sim.Poke("sd_in", line)
    sim.Tick()
    line <- sim.Peek "sd_out"
    taps.Add(sim.Peek "tap")
```

With the ticks wired correctly the tap follows the tone; with them swapped it
follows a different sequence entirely, because the receiver is now sampling on
the edge where the line is changing rather than where it is stable. That
difference is the check.

**Or drive the stream ports directly.** `I2sLoopback` in `Warp11.Designs` is the
shipped version: it exposes `in_*` and `out_*` stream ports, transmits onto an
internal wire and receives from it, so a test pokes a known sample in and reads
one out with no framing to hand-build.

That check — `i2sLoopbackRoundTrip` — asserts **`output = input << 1`, not
equality**, and the reason is worth knowing because it looks like a bug and is
not. `i2sTx` drives the MSB on the tick where LRCLK turns; `i2sRx`, by its own
correct rule, treats that tick as the no-data transition and discards what is on
the line — which is that MSB. It then takes bits 22..0 plus a trailing pad zero,
giving exactly `input << 1`.

**Each framer is right against a converter, and they are off by one against each
other**, because on real hardware the converter defines the timing rather than
the other framer. So a check asserting equality is one you could only pass by
breaking a framer. Assert the measured relationship instead — that way it still
fails if the link breaks in some *other* way.

Whichever loop you write, drive several frames. A framer that gets the first
frame right and then drifts is exactly what a single-frame check misses, and the
first frame catches the transmitter mid-slot anyway, so judge the settled link
rather than the first thing out of it.

`i2sRxDecodes` and `i2sTxEmits` are the third pattern: each framer alone, driven
by a hand-built ideal frame. That is the only way to test one without the other,
and it is what pins the frame convention itself.

## Gotchas

| | |
|---|---|
| `sclkRxTick` and `sclkTxTick` swapped | passes elaboration and every gate, and **is not detectable in simulation** — it is a setup/hold property and there is no timing model. Only a bench or a fabric loopback shows it. **Use `i2sLink` and it is not expressible** |
| two links in one design sharing a prefix | instance names collide. Give each its own `prefix` |
| two clock generators | drift. One generator, driven to every clock pin, always |
| asserting equality in a software loopback | fails for a correct design — see above |
| a pin in the `.xdc` that no port declares | will not build for the board |
| `PULLDOWN TRUE` on UltraScale+ | silently ignored; use `PULLTYPE PULLDOWN` |
| expecting the receiver to stall | it ignores `ready`. Add a `streamFifo` if you need slack |
| a design that only transmits | declares no `sdout` and its `.xdc` binds fewer pins — do not copy a receiving design's constraints |
| consuming a received stream without a sink | leaves its `ready` undriven, and elaboration fails with *child inputs were never driven: 'out_ready'*. Drive it: `lit 1UL 1 ==> received.ready` |
| a `reg` sharing a name with a port | *declared twice … an output port, then a reg*. One declaration per name, ports included — name the register `last_left_reg` |

## See also

- [Streams](../streams.md) — the ready/valid layer both framers speak
- [Create a register map](register-map.md) — how `volume` and `mute` reach the fabric from a host
- [Drive it from Rust](../drive-it-from-rust.md) — the host half
- [Hardware workflow](../dev-workflow.md) — deploy, and what to check when a board is silent
