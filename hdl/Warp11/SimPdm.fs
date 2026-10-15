/// Microphones on a running design's PDM pins: a software sigma-delta
/// modulator per channel, answering the clock the design makes with one bit
/// per edge.
///
/// The model is what a MEMS microphone is — a second-order modulator — so the
/// bit stream the decimator sees carries the shaped noise a real one does, and
/// a check that passes here has met the noise it will meet on the bench.
/// Samples arrive at the decimated rate and are interpolated linearly across
/// the bits of a frame, which is what a diaphragm does between samples that
/// never existed; the interpolation's own droop at 1 kHz is a hundredth of a
/// decibel.
///
/// Two halves, `Drive` and `Sample`, for the reason `SimDevice.fs` gives: the
/// device does not own the clock.
[<AutoOpen>]
module Warp11.SimPdm

/// The two pin names the model watches and drives.
type PdmSimPins =
    { /// The bit clock the design drives.
      clock: string
      /// The line the model drives and the design reads.
      data: string }

/// The shape `pdmPins p` declares.
let defaultPdmSimPins = { clock = "pdm_clk"; data = "pdm_data" }

/// A second-order sigma-delta modulator: two delaying integrators, the
/// quantiser fed back with weight one into the first and two into the second.
/// That pair of weights is what makes the noise transfer exactly `(1 − z⁻¹)²`
/// — the textbook's half-and-half loop is easier to build in silicon but
/// leaves the in-band noise 12 dB higher, which cost this check its margin
/// before the weights were derived rather than copied. Stable for inputs to
/// about 0.9 of full scale, the range a microphone's own modulator is
/// specified over.
type SigmaDelta2() =
    let mutable first = 0.0
    let mutable second = 0.0
    let mutable fed = 1.0

    /// The next bit for an input in −1 .. 1: `1` for a positive quantiser
    /// output, `0` for a negative one.
    member _.Next(x: float) : int =
        let x = max -1.0 (min 1.0 x)
        let held = first
        first <- first + (x - fed)
        second <- second + (held - 2.0 * fed)
        fed <- if second >= 0.0 then 1.0 else -1.0
        if fed > 0.0 then 1 else 0

/// Two microphones on one bus, fed from `frames` at the decimated rate. The
/// left one — select pin low — answers the rising edge, the right the falling,
/// and each drives the line for the half period after its edge. After the
/// last frame the microphones hear silence.
type PdmMicSource(sim: Sim, pins: PdmSimPins, decimation: int, frames: seq<float * float>) =
    let source = frames.GetEnumerator()
    let left = SigmaDelta2()
    let right = SigmaDelta2()
    let mutable current = (0.0, 0.0)
    let mutable next = (0.0, 0.0)
    let mutable consumed = 0

    let advance () =
        current <- next

        if source.MoveNext() then
            next <- source.Current
            consumed <- consumed + 1
        else
            next <- (0.0, 0.0)

    do
        advance ()
        advance ()

    let mutable previousClock = sim.Peek pins.clock
    let mutable line = 0UL
    let mutable bitOfFrame = 0

    let interpolate (now: float) (soon: float) =
        now + (soon - now) * float bitOfFrame / float decimation

    /// Put this half period's bit on the line. Call before the tick.
    member _.Drive() = sim.Poke(pins.data, line)

    /// Watch the clock; on an edge, work out the bit the microphone that owns
    /// it will drive. Call after the tick.
    member _.Sample() =
        let clock = sim.Peek pins.clock

        if clock = 1UL && previousClock = 0UL then
            line <- uint64 (left.Next(interpolate (fst current) (fst next)))
        elif clock = 0UL && previousClock = 1UL then
            line <- uint64 (right.Next(interpolate (snd current) (snd next)))
            bitOfFrame <- bitOfFrame + 1

            if bitOfFrame = decimation then
                bitOfFrame <- 0
                advance ()

        previousClock <- clock

    /// Frames taken from the source so far.
    member _.Consumed = consumed

    interface ISimDevice with
        member this.Drive() = this.Drive()
        member this.Sample() = this.Sample()

/// A WAV as what the microphones hear: each channel in −1 .. 1, a mono file
/// heard by both.
let pdmFramesOfWav (w: WavData) : seq<float * float> =
    seq {
        for i in 0 .. w.FrameCount - 1 do
            let left = float w.samples[i * w.channels] / 32768.0

            let right =
                if w.channels = 1 then
                    left
                else
                    float w.samples[i * w.channels + 1] / 32768.0

            yield (left, right)
    }

/// `frames` through the microphones into a design whose decimated pair leaves
/// on `output`, yielded as signed samples one beat at a time, ticking only as
/// far as each pulled beat requires — `streamThrough`'s shape, for a source
/// that is a bus rather than a port group.
let pdmThrough (sim: Sim) (pins: PdmSimPins) (decimation: int) (output: StreamPins) (frames: seq<float * float>) : seq<int64 * int64> =
    let mics = PdmMicSource(sim, pins, decimation, frames)

    // The pair is `sampleLayout`, so the width is the library's sample width.
    let signedOf (name: string) =
        let bits = sim.Peek name

        if bits >>> (sampleWidth - 1) = 1UL then
            int64 bits - (1L <<< sampleWidth)
        else
            int64 bits

    seq {
        sim.Poke(output.ready, 1UL)

        while true do
            mics.Drive()
            sim.Tick()
            mics.Sample()

            if sim.Peek output.valid = 1UL then
                match output.fields with
                | [ left; right ] -> yield (signedOf left, signedOf right)
                | fields -> failwith $"pdmThrough: a stereo pair has two fields, got %d{List.length fields}"
    }
