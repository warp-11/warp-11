/// Microphones on a running design's PDM pins: a software sigma-delta
/// modulator per channel, answering the clock the design makes with one bit
/// per edge.
///
/// The model is what a MEMS microphone is — a second-order modulator — so the
/// bit stream the decimator sees carries the shaped noise a real one does, and
/// a check that passes here has met the noise it will meet on the bench.
/// Samples arrive at the decimated rate and are interpolated across the bits
/// of a frame with a windowed sinc — band-limited, so the model's own
/// response stays flat through the band and a level check measures the
/// decimator rather than the model. (A linear interpolation here read 0.77 dB
/// low at 8 kHz, which is sinc² at that fraction of the rate.)
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

/// Taps of the interpolating sinc, a Blackman window over them: flat to a
/// few hundredths of a decibel through a third of the rate, and sixteen
/// frames of look-ahead, which is the model's latency and nothing else's.
let pdmInterpolationTaps = 32

/// Two microphones on one bus, fed from `frames` at the decimated rate. The
/// left one — select pin low — answers the rising edge, the right the falling,
/// and each drives the line for the half period after its edge. After the
/// last frame the microphones hear silence.
type PdmMicSource(sim: Sim, pins: PdmSimPins, decimation: int, frames: seq<float * float>) =
    let source = frames.GetEnumerator()
    let left = SigmaDelta2()
    let right = SigmaDelta2()
    let taps = pdmInterpolationTaps
    let half = taps / 2
    let leftRing = Array.zeroCreate<float> taps
    let rightRing = Array.zeroCreate<float> taps
    let mutable newest = taps - 1
    let mutable consumed = 0

    // The next frame into the ring; silence past the end of the source.
    let advance () =
        newest <- newest + 1

        let l, r =
            if source.MoveNext() then
                consumed <- consumed + 1
                source.Current
            else
                (0.0, 0.0)

        leftRing[newest % taps] <- l
        rightRing[newest % taps] <- r

    // The kernel per phase of a frame, each phase normalised to unit sum so
    // the model's gain at DC is exactly one.
    let kernel =
        [| for bit in 0 .. decimation - 1 ->
               let phase = float bit / float decimation

               let weights =
                   [| for k in -half + 1 .. half ->
                          let t = float k - phase
                          let u = t / float half

                          if abs u >= 1.0 then
                              0.0
                          else
                              let sinc = if t = 0.0 then 1.0 else sin (System.Math.PI * t) / (System.Math.PI * t)
                              sinc * (0.42 + 0.5 * cos (System.Math.PI * u) + 0.08 * cos (2.0 * System.Math.PI * u)) |]

               let sum = Array.sum weights
               weights |> Array.map (fun w -> w / sum) |]

    let mutable previousClock = sim.Peek pins.clock
    let mutable line = 0UL
    let mutable bitOfFrame = 0

    // The frame being played is `half` behind the newest, so the kernel has
    // frames on both sides of it.
    let interpolate (ring: float[]) =
        let weights = kernel[bitOfFrame]
        let centre = newest - half
        let mutable acc = 0.0

        for i in 0 .. taps - 1 do
            let k = i - half + 1
            acc <- acc + weights[i] * ring[(centre + k) % taps]

        acc

    /// Put this half period's bit on the line. Call before the tick.
    member _.Drive() = sim.Poke(pins.data, line)

    /// Watch the clock; on an edge, work out the bit the microphone that owns
    /// it will drive. Call after the tick.
    member _.Sample() =
        let clock = sim.Peek pins.clock

        if clock = 1UL && previousClock = 0UL then
            line <- uint64 (left.Next(interpolate leftRing))
        elif clock = 0UL && previousClock = 1UL then
            line <- uint64 (right.Next(interpolate rightRing))
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

// ---------------------------------------------------------------------------
// A recording through a top whose microphones are PDM and whose output is the
// I2S bus — the pin-level pair of `WavI2sDevice`, for the boards whose harness
// carries `PdmMics`.

/// The microphones fed from a recording, the output line decoded: the device
/// a debugger session or a batch run attaches to such a top. The output
/// carries the link's pre-roll and the decimator's settling, left in for the
/// reason `WavI2sDevice` gives.
type WavPdmDevice(sim: Sim, mics: PdmSimPins, i2s: I2sSimPins, decimation: int, input: WavData) =
    let source = PdmMicSource(sim, mics, decimation, pdmFramesOfWav input)
    let codec = I2sCodec(sim, i2s, listenOnly = true)

    /// How many frames of the recording the microphones have taken.
    member _.FramesPlayed = source.Consumed

    /// How many frames have come back off the design's output line.
    member _.FramesHeard = codec.Count

    /// What has been heard so far, at the recording's rate and stereo.
    member _.Output: WavData =
        let heard = codec.Received
        let samples = Array.zeroCreate<int16> (heard.Length * 2)

        heard
        |> List.iteri (fun frame (left, right) ->
            samples[frame * 2] <- fromSampleBits left
            samples[frame * 2 + 1] <- fromSampleBits right)

        { input with channels = 2; samples = samples }

    /// One fabric cycle, for a caller that owns no loop of its own.
    member this.Tick() =
        (this :> ISimDevice).Drive()
        sim.Tick()
        (this :> ISimDevice).Sample()

    interface ISimDevice with
        member _.Drive() =
            source.Drive()
            codec.Drive()

        member _.Sample() =
            source.Sample()
            codec.Sample()

/// Play a recording into a top's PDM microphones and collect its I2S output —
/// `runWavThroughI2s` for the other front end. `slack` is how many frames
/// beyond the recording to keep running.
let runWavThroughPdm (sim: Sim) (mics: PdmSimPins) (i2s: I2sSimPins) (decimation: int) (slack: int) (input: WavData) : WavData =
    let device = WavPdmDevice(sim, mics, i2s, decimation, input)
    let target = input.FrameCount + slack
    let guard = target * 16_384
    let mutable cycles = 0

    while device.FramesHeard < target && cycles < guard do
        device.Tick()
        cycles <- cycles + 1

    if device.FramesHeard < target then
        failwith
            $"runWavThroughPdm: heard %d{device.FramesHeard} frames in %d{cycles} cycles, wanted %d{target} — is the design driving its word-select pin?"

    device.Output

/// A recording to hand a `DebugSession`, and the handle to read what came
/// back — `WavI2sSource` for a PDM front end.
type WavPdmSource(mics: PdmSimPins, i2s: I2sSimPins, decimation: int, input: WavData) =
    let mutable device: WavPdmDevice option = None

    /// Hand this to a `DebugSession`'s `devices`.
    member _.Attach: Sim -> ISimDevice =
        fun sim ->
            let d = WavPdmDevice(sim, mics, i2s, decimation, input)
            device <- Some d
            d :> ISimDevice

    /// What has been heard so far; `None` until a session has attached.
    member _.Output = device |> Option.map (fun d -> d.Output)

    /// How many frames of the recording have played.
    member _.FramesPlayed = device |> Option.map (fun d -> d.FramesPlayed) |> Option.defaultValue 0

/// `frames` into the microphones, the I2S output's stereo pairs out, lazily —
/// `i2sThrough` for a top with PDM microphones. The leading pairs are the
/// link's silence and the decimator's settling, so `Seq.skipWhile i2sSilence`
/// is still the usual second stage.
let pdmThroughI2s (sim: Sim) (mics: PdmSimPins) (i2s: I2sSimPins) (decimation: int) (frames: seq<float * float>) : seq<uint64 * uint64> =
    let source = PdmMicSource(sim, mics, decimation, frames)
    let codec = I2sCodec(sim, i2s, listenOnly = true)

    seq {
        let mutable taken = 0

        while true do
            source.Drive()
            sim.Tick()
            source.Sample()
            codec.Sample()

            while taken < codec.Count do
                yield codec[taken]
                taken <- taken + 1
    }
