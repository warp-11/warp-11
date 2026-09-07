/// A software I2S codec on a running simulation's pins — the stimulus leg the
/// audio designs never had.
///
/// `SimAxi` lets a host drive a simulated design over AXI-Lite through real
/// handshakes; this is the same idea on the other boundary. A test hands over
/// stereo samples and reads stereo samples back, and the 2,048 fabric cycles of
/// framing under each one stop being the test's problem.
///
/// **Why this is worth having rather than a loopback.** The three verification
/// legs the audio chain already has all miss the same thing: the living checks
/// drive stream ports, so `i2sRx` and `i2sTx` are not in the path; the
/// differential drives every input with seeded noise, which is correct and not
/// about audio; and `I2sLoopback` wires the transmitter to the receiver, which
/// proves the two framers agree **with each other**. None of them can catch a
/// disagreement with a real converter's frame — and that is the bug the Kotlin
/// bring-up lost weeks to. `notes/DEVICES.md` §2 is the long version.
///
/// So this model is deliberately written against the **standard** rather than
/// against `i2sRx`/`i2sTx`: it changes its data line on each falling bit-clock
/// edge, samples the design's line on each rising edge, and places the MSB one
/// bit-time after the word select turns. Agreement is then evidence rather than
/// tautology.
///
/// **What it does not prove**: the same person can misread a datasheet twice,
/// so an independent implementation agreeing is not a real ICS-43434 agreeing.
/// This narrows the bench step; it does not replace it.
[<AutoOpen>]
module Warp11.SimI2s

/// The four pin names a codec model watches and drives. They differ per pinout
/// — a design declares exactly what its constraint file binds — so the model is
/// told the names rather than guessing them.
type I2sSimPins =
    { /// The bit clock the design drives. Data changes on its falling edge and
      /// is sampled on its rising edge.
      bitClock: string
      /// The word select. Low is the left slot, high is the right.
      wordSelect: string
      /// The line the model drives and the design receives on.
      toDesign: string
      /// The line the design drives and the model samples. }
      fromDesign: string }

/// The MEMS shape: `i2sPins p SharedBus`.
let sharedBusSimPins =
    { bitClock = "bclk"
      wordSelect = "ws"
      toDesign = "sd_in"
      fromDesign = "sd_out" }

/// The Pmod I2S2 shape: `i2sPins p SeparateCodecs`. Note the crossover — the
/// design's `sdout` pin is an *input*, named for the converter's output.
let separateCodecSimPins =
    { bitClock = "sclk"
      wordSelect = "lrclk"
      toDesign = "sdout"
      fromDesign = "sdin" }

/// A converter attached to a running `Sim`. Queue samples, tick, read back what
/// the design emitted.
///
/// Ticking is the caller's, not the model's, because a test usually wants to do
/// something else on the way past — write a register through `SimAxi`, flip a
/// port, watch a counter. `i2sExchange` is the shorthand for when it does not.
type I2sCodec(sim: Sim, pins: I2sSimPins, ?width: int) =
    // Defaults to the library's sample width rather than restating it. This
    // file is compiled after `Audio.fs` for exactly that reason: an audio
    // constant should have one definition, and a simulation helper is not a
    // good enough reason to give it two.
    let sampleWidth = defaultArg width sampleWidth
    let mask = (1UL <<< sampleWidth) - 1UL
    let queue = System.Collections.Generic.Queue<uint64 * uint64>()
    let received = ResizeArray<uint64 * uint64>()

    let mutable previousClock = sim.Peek pins.bitClock
    let mutable previousSelect = sim.Peek pins.wordSelect
    let mutable sendBit = 0
    let mutable takeBit = 0
    let mutable line = 0UL
    let mutable shift = 0UL
    let mutable heldLeft = 0UL
    let mutable current = (0UL, 0UL)
    let mutable sent = 0

    /// One fabric cycle: drive the line, advance the design, then read the pins
    /// back and work out what the next bit should be.
    member _.Tick() =
        sim.Poke(pins.toDesign, line)
        sim.Tick()

        let clock = sim.Peek pins.bitClock
        let select = sim.Peek pins.wordSelect

        if select <> previousSelect then
            // A slot boundary. The left slot's word is held until the right
            // one completes, so a pair lands in `received` together.
            if previousSelect = 0UL then
                heldLeft <- shift &&& mask
            else
                received.Add(heldLeft, shift &&& mask)

            // A new frame starts on the falling edge into the left slot.
            if select = 0UL && queue.Count > 0 then
                current <- queue.Dequeue()
                sent <- sent + 1

            sendBit <- 0
            takeBit <- 0
            shift <- 0UL
        elif clock = 1UL && previousClock = 0UL then
            // Rising: the design's line is stable, so this is where a receiver
            // samples. Bits past the sample's width are the slot's zero padding
            // and are dropped rather than shifted in.
            if takeBit < sampleWidth then
                shift <- (shift <<< 1) ||| (sim.Peek pins.fromDesign &&& 1UL)

            takeBit <- takeBit + 1
        elif clock = 0UL && previousClock = 1UL then
            // Falling: a converter changes its line here, so the design samples
            // a settled value on the rising edge that follows.
            let word = if select = 0UL then fst current else snd current

            line <-
                if sendBit < sampleWidth then
                    (word >>> (sampleWidth - 1 - sendBit)) &&& 1UL
                else
                    0UL

            sendBit <- sendBit + 1

        previousClock <- clock
        previousSelect <- select

    /// Hand the model samples to transmit, in order.
    member _.Queue(samples: (uint64 * uint64) seq) = for s in samples do queue.Enqueue s

    /// Every stereo pair decoded off the design's output line so far, oldest
    /// first — including the silent ones the link emits before the first queued
    /// sample has made it through.
    member _.Received = List.ofSeq received

    /// How many stereo pairs have been decoded — one per frame, including the
    /// silent ones before the first queued sample arrives. Cheap, so a run can
    /// be paced by it rather than by a cycle count the caller has to work out
    /// from the design's divisors.
    member _.Count = received.Count

    /// How many queued samples have started transmitting.
    member _.Sent = sent

    /// One decoded pair by position, so a lazy consumer can pull the next one
    /// without rebuilding the whole list on every tick.
    member _.Item
        with get (index: int) = received[index]

/// The I2S half of `streamThrough`: samples in, samples out, lazily.
///
/// Pulling a pair ticks the codec until another frame completes, so a test reads
/// as a pipeline and states no cycle budget:
///
///     samples
///     |> i2sThrough sim sharedBusSimPins
///     |> Seq.skipWhile i2sSilence
///     |> Seq.take 3
///
/// The leading beats are silence — the link is a pipeline and the first queued
/// sample takes a frame or two to come back — so `Seq.skipWhile i2sSilence` is
/// the usual second stage. **Skipping rather than filtering** matters: a
/// filter would also drop a genuinely silent sample in the middle of the run,
/// which is a real value a design might emit.
let i2sThrough (sim: Sim) (pins: I2sSimPins) (samples: (uint64 * uint64) list) : seq<uint64 * uint64> =
    seq {
        let codec = I2sCodec(sim, pins)
        codec.Queue samples

        let mutable emitted = 0
        let mutable idle = 0

        while idle < 100_000 do
            if codec.Count > emitted then
                yield codec[emitted]
                emitted <- emitted + 1
                idle <- 0
            else
                codec.Tick()
                idle <- idle + 1
    }

/// A decoded pair carrying nothing — the pre-roll a link emits before the first
/// real sample reaches its output.
let i2sSilence (left: uint64, right: uint64) = left = 0UL && right = 0UL

/// Send `samples` through a design and return what came back, dropping the
/// leading silence.
///
/// **The run is paced by frames observed, not by a cycle count**, which is what
/// keeps a caller from restating the design's divisors. The codec already
/// watches the word select, so it knows when a frame has passed; a test that
/// had to compute `2 * bitsPerSlot * 2 * sclkHalfDiv` would go silently wrong
/// the moment the design's clocking changed, which is exactly the coupling this
/// helper exists to remove.
///
/// `slack` is how many frames beyond the last queued sample to keep running —
/// the link is a pipeline, so the last sample needs a frame or two to reach the
/// output. `guardCycles` only stops a design that never frames at all from
/// hanging the test.
let i2sExchangeWith (sim: Sim) (pins: I2sSimPins) (slack: int) (samples: (uint64 * uint64) list) =
    let codec = I2sCodec(sim, pins)
    codec.Queue samples

    let target = List.length samples + slack
    let guardCycles = (List.length samples + slack + 4) * 8192
    let mutable cycles = 0

    while codec.Count < target && cycles < guardCycles do
        codec.Tick()
        cycles <- cycles + 1

    if codec.Count < target then
        failwith (
            $"i2sExchange: saw %d{codec.Count} frames in %d{cycles} cycles, expected %d{target} — "
            + "is the design driving its word-select pin?")

    // `skipWhile`, not `filter`: only the leading pre-roll is not data. A
    // filter would also swallow a silent sample in the middle of a run, which
    // is a value a design may legitimately emit.
    codec.Received |> List.skipWhile i2sSilence

/// The common case: four frames of slack past the last queued sample.
let i2sExchange (sim: Sim) (pins: I2sSimPins) (samples: (uint64 * uint64) list) =
    i2sExchangeWith sim pins 4 samples
