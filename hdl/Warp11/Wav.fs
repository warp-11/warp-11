/// WAV files in, WAV files out, with the simulated hardware in between.
///
/// The point is an oracle that judges a DSP design against something other than
/// itself. The differential proves the Sim and the emitted Verilog agree; a
/// property check pins one arithmetic relationship at a time. Neither can tell
/// you a filter *sounds* right, or that a compressor's attack behaves over a
/// real transient. Pushing an actual signal through the actual design and
/// getting a file back can — and the file is comparable against a float
/// reference computed the same way.
///
/// Host-side, so ordinary .NET IO is fine here; nothing in this file
/// elaborates.
[<AutoOpen>]
module Warp11.Wav

open System
open System.IO

/// Decoded PCM. `samples` is interleaved (L0, R0, L1, R1, ...) 16-bit signed.
type WavData =
    { sampleRate: int
      channels: int
      bitsPerSample: int
      samples: int16[] }

    /// Frames rather than samples: a stereo file has two samples per frame.
    member this.FrameCount = this.samples.Length / this.channels

/// Read a 16-bit PCM WAV. Chunk-walking rather than assuming a 44-byte header,
/// because real files carry LIST and fact chunks ahead of the data.
let readWav (bytes: byte[]) : WavData =
    if bytes.Length < 12 then failwith "not a WAV file: too short"
    if Text.Encoding.ASCII.GetString(bytes, 0, 4) <> "RIFF" then failwith "not a RIFF file"
    if Text.Encoding.ASCII.GetString(bytes, 8, 4) <> "WAVE" then failwith "not a WAVE file"

    let rec walk offset format data =
        if offset + 8 > bytes.Length then
            format, data
        else
            let id = Text.Encoding.ASCII.GetString(bytes, offset, 4)
            let size = BitConverter.ToInt32(bytes, offset + 4)
            let body = offset + 8
            // Chunks are word-aligned; an odd size carries a pad byte.
            let next = body + size + (size &&& 1)

            match id with
            | "fmt " -> walk next (Some(body, size)) data
            | "data" -> walk next format (Some(body, size))
            | _ -> walk next format data

    match walk 12 None None with
    | Some (fmtAt, fmtSize), Some (dataAt, dataSize) ->
        if fmtSize < 16 then failwith "malformed fmt chunk"
        let audioFormat = int (BitConverter.ToInt16(bytes, fmtAt))
        if audioFormat <> 1 then failwith $"only PCM is supported, got format {audioFormat}"
        let channels = int (BitConverter.ToInt16(bytes, fmtAt + 2))
        let sampleRate = BitConverter.ToInt32(bytes, fmtAt + 4)
        let bitsPerSample = int (BitConverter.ToInt16(bytes, fmtAt + 14))

        if bitsPerSample <> 16 then
            failwith $"only 16-bit PCM is supported, got {bitsPerSample}"

        let count = dataSize / 2
        let samples = Array.init count (fun i -> BitConverter.ToInt16(bytes, dataAt + 2 * i))

        { sampleRate = sampleRate
          channels = channels
          bitsPerSample = bitsPerSample
          samples = samples }
    | None, _ -> failwith "WAV has no fmt chunk"
    | _, None -> failwith "WAV has no data chunk"

/// The bytes of a 16-bit PCM WAV file. The inverse of `readWav`, and no more
/// general than it is.
let writeWav (w: WavData) : byte[] =
    let dataBytes = w.samples.Length * 2
    let output = new MemoryStream()
    let write (b: byte[]) = output.Write(b, 0, b.Length)
    let ascii (s: string) = write (Text.Encoding.ASCII.GetBytes s)

    ascii "RIFF"
    write (BitConverter.GetBytes(36 + dataBytes))
    ascii "WAVE"
    ascii "fmt "
    write (BitConverter.GetBytes 16)
    write (BitConverter.GetBytes 1s) // PCM
    write (BitConverter.GetBytes(int16 w.channels))
    write (BitConverter.GetBytes w.sampleRate)
    write (BitConverter.GetBytes(w.sampleRate * w.channels * 2)) // byte rate
    write (BitConverter.GetBytes(int16 (w.channels * 2))) // block align
    write (BitConverter.GetBytes 16s)
    ascii "data"
    write (BitConverter.GetBytes dataBytes)

    for s in w.samples do
        write (BitConverter.GetBytes s)

    output.ToArray()

/// Read a WAV file from disk.
let readWavFile (path: string) = readWav (File.ReadAllBytes path)
/// Write a WAV file to disk.
let writeWavFile (path: string) (w: WavData) = File.WriteAllBytes(path, writeWav w)

/// A 16-bit sample widened to the design's 24-bit sample, as raw two's
/// complement bits ready to poke.
let private toSampleBits (v: int16) : uint64 =
    // Left-justify: 16-bit full scale should be 24-bit full scale, not a
    // signal 256x too quiet.
    uint64 ((int v) <<< 8) &&& ((1UL <<< sampleWidth) - 1UL)

/// The design's 24-bit output back to 16 bits, rounding rather than truncating
/// so a null test comes out exact instead of biased half an LSB low.
let private fromSampleBits (bits: uint64) : int16 =
    let signed =
        if bits >= (1UL <<< (sampleWidth - 1)) then
            int64 bits - (1L <<< sampleWidth)
        else
            int64 bits

    let rounded = (signed + 128L) >>> 8
    int16 (max -32768L (min 32767L rounded))

/// How a stereo design's sample ports are named. Defaults match the stream
/// stages in `Warp11.Audio`.
type WavPorts =
    { inLeft: string
      inRight: string
      outLeft: string
      outRight: string
      inValid: string
      outValid: string
      outReady: string }

/// The port names the audio stages in `Warp11.Audio` actually declare, so a
/// design built out of them needs no port map at all.
let defaultWavPorts =
    { inLeft = "in_left"
      inRight = "in_right"
      outLeft = "out_left"
      outRight = "out_right"
      inValid = "in_valid"
      outValid = "out_valid"
      outReady = "out_ready" }

/// Stream a stereo signal through a simulated design, one frame per cycle.
///
/// `settleCycles` runs the input's first frame through before recording, which
/// is what fills a filterbank's delay lines and lets an envelope reach steady
/// state; without it every output starts with a transient that has nothing to
/// do with the signal. Output frames are collected whenever the design flags
/// one valid, so a design with pipeline latency simply yields fewer frames than
/// it was given rather than needing its latency declared here.
let runWavThroughSim (sim: Sim) (ports: WavPorts) (settleCycles: int) (input: WavData) : WavData =
    if input.channels <> 2 then
        failwith $"the stereo harness needs 2 channels, got {input.channels}"

    sim.Poke(ports.outReady, 1UL)
    sim.Poke(ports.inValid, 1UL)

    let first = if input.FrameCount > 0 then 0 else -1

    if first >= 0 then
        sim.Poke(ports.inLeft, toSampleBits input.samples[0])
        sim.Poke(ports.inRight, toSampleBits input.samples[1])

        for _ in 1..settleCycles do
            sim.Tick()

    let output = ResizeArray<int16>(input.samples.Length)

    for frame in 0 .. input.FrameCount - 1 do
        sim.Poke(ports.inLeft, toSampleBits input.samples[frame * 2])
        sim.Poke(ports.inRight, toSampleBits input.samples[frame * 2 + 1])
        sim.Tick()

        if sim.Peek ports.outValid = 1UL then
            output.Add(fromSampleBits (sim.Peek ports.outLeft))
            output.Add(fromSampleBits (sim.Peek ports.outRight))

    { input with samples = output.ToArray() }

/// A stereo test signal: two tones an octave apart, one per channel, at a given
/// amplitude. Deterministic, so a reference and a run can be compared exactly.
let toneWav (sampleRate: int) (frames: int) (hz: float) (amplitude: float) : WavData =
    let samples = Array.zeroCreate<int16> (frames * 2)

    for i in 0 .. frames - 1 do
        let t = float i / float sampleRate
        let left = amplitude * sin (2.0 * Math.PI * hz * t)
        let right = amplitude * sin (2.0 * Math.PI * hz * 2.0 * t)
        samples[i * 2] <- int16 (max -32768.0 (min 32767.0 (left * 32767.0)))
        samples[i * 2 + 1] <- int16 (max -32768.0 (min 32767.0 (right * 32767.0)))

    { sampleRate = sampleRate
      channels = 2
      bitsPerSample = 16
      samples = samples }

/// Peak absolute sample per channel — the measurement most of these runs want,
/// since a compressor's job is visible in what it does to peaks.
let peaks (w: WavData) : int * int =
    let peakOf offset =
        seq { offset .. 2 .. w.samples.Length - 1 }
        |> Seq.fold (fun acc i -> max acc (abs (int w.samples[i]))) 0

    peakOf 0, peakOf 1

// ---------------------------------------------------------------------------
// A recording attached to a design's I2S pins.
//
// `runWavThroughSim` above drives a design's *stream* ports, which is right for
// testing a DSP stage and wrong for testing a front end: `i2sRx` and `i2sTx`
// are not in that path. This drives the pins instead, so the framers, the clock
// generator and whatever sits between them are all exercised by real audio.
//
// It is an `ISimDevice`, so the same object serves a headless run and the
// debugger — attach it to a `DebugSession` and stepping the design steps the
// recording with it.

/// A WAV playing into a design's microphone line, and what leaves its converter
/// line collected as a new one.
///
/// **The output carries the link's pre-roll.** A design cannot answer before it
/// has heard, so the first frames out are silence — two of them for a bare
/// link, more once there is a pipeline in the middle. They are left in rather
/// than trimmed, because trimming would need the latency declared and would
/// silently eat a recording that genuinely starts quiet. At 48 kHz two frames
/// is 41 µs; nothing that matters to listen to.
type WavI2sDevice(sim: Sim, pins: I2sSimPins, input: WavData) =
    do
        if input.channels <> 2 then
            failwith $"the I2S device needs a stereo recording, got {input.channels} channel(s)"

    let codec = I2sCodec(sim, pins)

    do
        codec.Queue
            [ for frame in 0 .. input.FrameCount - 1 ->
                toSampleBits input.samples[frame * 2], toSampleBits input.samples[frame * 2 + 1] ]

    /// The codec underneath, for a caller that wants to tick it directly.
    member _.Codec = codec

    /// How many frames of the recording have started playing.
    member _.FramesPlayed = codec.Sent

    /// How many frames have come back off the design's output line.
    member _.FramesHeard = codec.Count

    /// What has been heard so far, as a recording in its own right. Same rate
    /// and channel count as the input, so it can be written straight out.
    member _.Output : WavData =
        let heard = codec.Received
        let samples = Array.zeroCreate<int16> (heard.Length * 2)

        heard
        |> List.iteri (fun frame (left, right) ->
            samples[frame * 2] <- fromSampleBits left
            samples[frame * 2 + 1] <- fromSampleBits right)

        { input with samples = samples }

    interface ISimDevice with
        member _.Drive() = codec.Drive()
        member _.Sample() = codec.Sample()

/// Play a recording through a design's I2S pins and collect what comes back —
/// the pin-level counterpart of `runWavThroughSim`.
///
/// `slack` is how many frames beyond the recording to keep running, so the last
/// samples have time to reach the output line.
let runWavThroughI2s (sim: Sim) (pins: I2sSimPins) (slack: int) (input: WavData) : WavData =
    let device = WavI2sDevice(sim, pins, input)
    let target = input.FrameCount + slack

    // Generous, and only here so a design that never frames fails rather than
    // hanging: a frame is a few thousand cycles, not tens of thousands.
    let guard = target * 16_384
    let mutable cycles = 0

    while device.FramesHeard < target && cycles < guard do
        device.Codec.Tick()
        cycles <- cycles + 1

    if device.FramesHeard < target then
        failwith
            $"runWavThroughI2s: heard %d{device.FramesHeard} frames in %d{cycles} cycles, wanted %d{target} — is the design driving its word-select pin?"

    device.Output

/// A recording to hand a `DebugSession`, and the handle to read what came back.
///
/// The session builds its own `Sim`, so what it takes is a factory rather than
/// a device. This holds both ends: `Attach` is what the session gets, `Output`
/// is what the caller reads once the window has closed.
///
///     let source = WavI2sSource(sharedBusSimPins, readWavFile "speech.wav")
///     debugWith "hearing aid" design [ source.Attach ]
///     source.Output |> Option.iter (writeWavFile "heard.wav")
type WavI2sSource(pins: I2sSimPins, input: WavData) =
    let mutable device: WavI2sDevice option = None

    /// Hand this to a `DebugSession`'s `devices`.
    member _.Attach: Sim -> ISimDevice =
        fun sim ->
            let d = WavI2sDevice(sim, pins, input)
            device <- Some d
            d :> ISimDevice

    /// What has been heard so far. `None` until a session has attached — which
    /// is the honest answer rather than an empty recording, because the two
    /// mean different things when a run produced nothing.
    member _.Output = device |> Option.map (fun d -> d.Output)

    /// How many frames of the recording have played, for a progress readout.
    member _.FramesPlayed = device |> Option.map (fun d -> d.FramesPlayed) |> Option.defaultValue 0
