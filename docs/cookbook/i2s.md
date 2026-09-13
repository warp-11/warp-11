# How do I send and receive audio over I2S?

**You want:** samples off a converter, and samples back out to one — with your
design owning the clock and the rest of it looking like ordinary streams.

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

### A frame is one stereo sample

A **slot** is one channel's turn on the wire. A **frame** is both slots — one
left sample and one right — and it is the unit that repeats at the sample rate,
which is why WS is a square wave *at* Fs.

With the 32-bit slots Warp 11 uses, that is **64 bit clocks per frame**:

```
WS    ______________________|‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾|________
        left slot, 32 bits      right slot, 32 bits

      |<--------- one frame = 64 bit clocks ------>|
                                                      one stereo sample

BCLK  ┐┌┐┌┐┌┐┌┐┌┐┌┐┌┐┌  ... 64 of these, every frame
```

So a 48 kHz link with 32-bit slots runs its bit clock at 48 000 × 64 =
**3.072 MHz**, and anything reading that line sees a new sample pair once every
64 bit clocks and nothing whatever in between. **Audio arrives slowly, and in
pieces** — which is the fact `i2sRx` exists to hide.

### Why it is serial at all

A reasonable first reaction is that 24 wires would be simpler than one wire and
a clock, and for some converters that is exactly right — a video or RF ADC
really does put its bits out in parallel. Audio converters are serial for
reasons specific to audio:

- **Pins are the cost.** 24 bits × 2 channels is 48 pins, and a modern stereo
  audio ADC is a 24-pin 4 mm package *in total*. Serial turns 24 wires into one.
- **There is no speed pressure.** 48 kHz is 20.8 µs per sample; 64 bit times at
  3 MHz fill a small fraction of it, so serial costs nothing you needed.
  Converters that *are* fast go serial too — a gigasample part runs multi-gigabit
  serdes, because parallel cannot be routed at that rate either.
- **The chip is already serial inside.** A delta-sigma modulator emits one bit
  at a time at a few MHz, and a decimation filter assembles words from them.
  Serial out is the shape the architecture already has.
- **Noise.** Two dozen fast-switching parallel lines beside a 119 dB analog
  front end is a problem you would otherwise have to design away.

And when you genuinely need more data the answer is *more* sharing rather than
less: **TDM** puts eight channels on one wire by giving the frame more slots.

None of this reaches your design. `i2sRx` hands you a 24-bit value per channel,
in parallel, once per frame. The wide word you wanted does exist — the receiver
assembles it, and the serial bus stops at the framer.

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
`i2sMaster` generates one because the Pmod codecs need it; devices that derive
everything from the bit clock do not, and there `mclk` simply goes unread.

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
    "AudioGainShared"
    (fun p -> (axiLiteSlavePorts p gainMap.apertureAddrWidth, i2sPins p SharedBus))
    (fun (slavePorts, pins) ->
        let regs = regMapSlave slavePorts gainMap
        let i2s = i2sLink "i2s" pins kv260.fabricHz 48_828 32

        i2s.input
        |> audioGain "AudioGain" "gain" (regs.value gainRegs.volume) (regs.value gainRegs.mute)
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
| `i2sPins p SharedBus` | `sd_in` `bclk` `ws` `sd_out` | every device on one shared bus, no MCLK — a source that wants none, and a sink that makes its own |
| `i2sPins p SeparateCodecs` | `sdout` `mclk` `sclk` `lrclk` `sdin` `mclk2` `sclk2` `lrclk2` | a Pmod I2S2 — two chips on two rows, each with its own clock trio |
| `i2sTxPins p <pinout>` | the same, minus the input | a transmit-only link with nothing to listen to. The link type has no `input` field, so there is nothing to leave dangling |

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
use.

### Read it as a counting chain

Every one of those dividers is a counter, so the rate is the fabric clock
counted down twice — once to the bit clock, once to the frame:

```
fabric clock      100,000,000 Hz
     ÷ 32          SCLK toggles every 16 cycles, so a period is 32
SCLK                3,125,000 Hz
     ÷ 64          32 bits × 2 slots = one frame
Fs                     48,828 Hz
```

**And that is why some rates are simply unavailable.** A counter counts whole
cycles: it can divide by 16, never by 15.6. Exact 48 kHz needs SCLK at
48 000 × 64 = 3.072 MHz, which from 100 MHz means toggling every 16.28 cycles.
No such counter exists.

So an exact rate is not a matter of cleverer logic — it is a different input
frequency. 48 kHz wants a clock from the 12.288 MHz family (12.288, 24.576,
49.152 …), every one of which divides down to 48 000 exactly, and 44.1 kHz wants
the 11.2896 family. **Arrange that when a converter has a *table* of supported
rates rather than a tolerance** — many do, and a rate that is 2% off is then not
slightly wrong but unsupported. Otherwise the nearest divisor is fine.

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

**A board is created per app, not shared.** Loading a bitstream does not
program the PS clock registers — each app's device-tree overlay pins its own
PL0 — so the apps in this repository run the *same KV260* at 99.999001 MHz,
166.666672 MHz and 249.997498 MHz. `kv260At <hz>` is the constructor and a
project writes `let board = kv260At …` once beside its design; `kv260` is the
100 MHz binding the audio apps share because they genuinely share a clock. A
single board value for the part would be right for one app and silently wrong
for the rest, and silently is the problem: a wrong clock costs no samples, it
only makes every rate and every reported time wrong.

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

**One shared bus.** Where every device on the bus derives its timing from the
bit and word clocks, no MCLK is needed at all — the source wants none and the
sink makes its own — so the whole interface is four pins: `bclk`, `ws`,
`sd_in`, `sd_out`. `clocks.mclk` simply goes unread — **an instance output
nobody consumes is legal**; only a *module* output nobody drives is an error.

Whichever shape you have, **declare exactly the pins your `.xdc` binds**. A
constraint file binding a pin the design never declared is a pin left floating,
and the design will not build for the board.

One constraint detail that is silently ignored if you get it wrong: on
UltraScale+ the weak pulldown on an input data line is **`PULLTYPE PULLDOWN`**,
not the legacy `PULLDOWN TRUE`. With it an unused slot reads a clean zero;
without it you get a floating input reading `0xFFFFFF` and looking exactly like
a broken converter.

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

## Real audio through the pins

Every loop above tests the link against stimulus someone made up. `Wav.fs`
closes the last gap: a real recording played into the design's converter pins by
the software codec, and whatever leaves the other line collected as a recording
of its own.

```fsharp
let input = readWavFile "speech.wav"      // or toneWav 48_828 400 440.0 0.6
let heard = runWavThroughI2s (Sim design.def) separateCodecSimPins 4 input
writeWavFile "heard.wav" heard
```

The pin record is the one your design declared — `sharedBusSimPins` for
`SharedBus`, `separateCodecSimPins` for `SeparateCodecs` — and `slack` is how
many frames beyond the recording to keep running, so the last samples have time
to reach the output line.

**It is the pin-level counterpart of `runWavThroughSim`**, and the difference is
the point. `runWavThroughSim` drives a design's `in_left`/`in_right` *stream*
ports, which is right for judging a filter and puts neither `i2sRx` nor `i2sTx`
in the path. This drives the pins, so the clock generator, both framers and
everything between them are carrying real audio.

| worth knowing | |
|---|---|
| **16 → 24 → 16 is lossless** | the conversion left-justifies into the 24-bit sample and rounds rather than truncating on the way back, so a null test through a pass-through is *exact*. A check written with a tolerance here would pass a design that quietly lost a bit |
| **the output carries the link's pre-roll** | a design cannot answer before it has heard. Two frames for a bare link, more once there is a pipeline in the middle. They are left in rather than trimmed, because trimming needs the latency declared, and it would silently eat a recording that genuinely starts quiet |
| **the model holds its last frame** | run past the end of the file and the codec keeps sending the frame it finished on. A tail that is neither the recording nor silence is that, not a fault in your design |
| **the link frames at its own rate** | the file's header rate is carried into the output file untouched; the hardware frames at whatever the divisors make. A 44.1 kHz file through a 48.8 kHz link plays fine, about 10% fast |

### The same recording in the debugger

`WavI2sDevice` is an `ISimDevice` — something attached to a running design's
pins — so the object that serves the harness above serves the step-through
debugger too. Attached there, the recording advances with the design: **step one
cycle and the audio steps one cycle**, and a breakpoint stops the signal where
it stops the logic.

The session builds its own `Sim`, so what it takes is a *factory* rather than a
device. `WavI2sSource` holds both ends — `Attach` is what the session gets,
`Output` is what you read once the window has closed:

```fsharp
let source = WavI2sSource(separateCodecSimPins, readWavFile "speech.wav")

Warp11.SimView.Desktop.debugWith "a WAV on the codec pins" design.def [ source.Attach ]
|> ignore

source.Output |> Option.iter (writeWavFile "heard.wav")
```

`Warp11.Effects` ships that as a verb, and a sample recording to point it at, so
there is something to run before you write any of it:

```sh
dotnet run --project Warp11.Effects -- listen Warp11.Effects/in.wav heard.wav
```

It opens `AudioEffectsAxi` — gain, one EQ band, a compressor, a limiter — with
the file on its codec pins. Filter the signal list for `left` and the chain is
there to watch a sample move down: `audio_rx_out_left`, `gain_out_left`,
`eq_out_left`, `compressor_out_left`, `limiter_out_left`, `audio_tx_in_left`.
Every register resets to a no-op, so at rest the sample arrives at the
transmitter exactly as the converter delivered it, and the output file is
sample-for-sample the input after two frames of pre-roll.

**Turn the knobs while it plays.** The settings are AXI-Lite registers, and the
watch panel gives a register a field just as it gives one to an input — filter
for `mute`, type `0x1`, and the next frame leaves as silence; filter for
`volume` and type `0x80` against a unity of `0x100` to halve it. A register is
state the design only writes on a tick, so a value typed there is where it
carries on from, and one nothing else drives simply stays. It is not the same as
writing over AXI — a field with a side effect, a `start` bit that clears itself,
still wants a host on the bus — but for a setting it is the shorter path.

**What it is not is a way to listen to a file.** A frame is
`fabricHz / sampleRate` cycles — 2,048 on this board — and the debugger runs
this design at about **153k cycles/s**, so a second of wall clock is 75 frames:
1.5 ms of 48 kHz audio. Open a short clip, stop where it matters, and reach for
`runWavThroughI2s` when what you want is the whole file processed.

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
| a WAV that is not stereo | `runWavThroughI2s` and `WavI2sSource` both refuse it by name. The link carries two slots; a mono file has no second one |
| expecting the debugger to play a file | 1.5 ms of audio a second — see above. The debugger is for stopping on a frame, not for listening |
| `source.Output` read while the window is open | it is what has been heard *so far*. Read it after `debugWith` returns |
| a `reg` sharing a name with a port | *declared twice … an output port, then a reg*. One declaration per name, ports included — name the register `last_left_reg` |

## See also

- [Streams](../streams.md) — the ready/valid layer both framers speak
- [Create a register map](register-map.md) — how `volume` and `mute` reach the fabric from a host
- [Drive it from Rust](../drive-it-from-rust.md) — the host half
- [Hardware workflow](../dev-workflow.md) — deploy, and what to check when a board is silent
