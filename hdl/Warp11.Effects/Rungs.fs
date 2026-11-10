/// The first two rungs of the bring-up ladder as designs: a tone with
/// nothing listening, and line in to line out. Neither names a converter, a
/// bus, a pin or a board — the mapping says how the rows reach the host:
/// through a board's memory, over its link, or through the I2S converter on
/// its pins, which is where these two started (the iCEBreaker's hand tops
/// in `hardware/ice40/`, retired 2026-11-07) and is still what they build
/// to for anyone who wires a Pmod I2S2.
///
/// **What a batch path cannot do is make a sound**, and that is the point of
/// the release matrix: audio is a WAV on the host, through the design, to a
/// WAV on the host, on every board the same, byte for byte against the Sim.
/// The tone is then `warp11_batch count:N` — nothing in, the count of
/// samples asked for, the samples back — and the passthru is the identity
/// over the same path. The converter path keeps them honest: the living
/// checks still frame the passthru through the iCEBreaker's I2S and count
/// the ratios off its pins.
[<AutoOpen>]
module Warp11.Effects.Rungs

open Warp11

/// The iCEBreaker clocked from its `SB_PLL40_PAD` at 24 MHz — the clock the
/// converter chooses rather than a preference, see `iceSampleRate`. Bound
/// once, so everything derives off it.
let iceBoard = iceBreakerAt 24_000_000

/// The rate the converter on that board frames at: 46 875 Hz.
///
/// **Not a choice, and not 48 kHz.** The CS5343 and CS4344 on a Pmod I2S2
/// lock to MCLK at 256x, 384x or 512x the frame rate, and `i2sMasterHz`
/// builds MCLK by toggling a register — so the fastest MCLK a design can
/// present is half its fabric clock, and the highest frame rate it can
/// support is `fabric / (2 * 256)`. At 24 MHz that is 46 875 Hz exactly.
/// Asking for 48 kHz there is an elaboration error rather than a 2.3%
/// detune, which the living check pins.
let iceSampleRate = iceBoard.fabricHz / 512

/// The stereo row every rung speaks.
let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

/// What the tone asks of the world.
type ToneIo =
    { /// One sample a beat: the host asks for a count, or plays rows it
      /// does not care about, or the converter frames.
      beats: BoardTop.Incoming<Expr>
      out: Stream<Expr * Expr> -> unit
      /// The phase step, starting at 440 Hz for the rate the rows run at.
      step: Expr }

/// A 440 Hz tone, one sample a beat, nothing listened to: the smallest thing
/// that makes a sound, or a WAV. The step starts at 440 Hz **for the rate
/// the board hands the design** — the converter's on the pins path, the
/// nominal 48 kHz on a host path — so `toneStep440` borrowed from the KV260
/// apps, which would play a flat 422 Hz on the iCEBreaker's converter, is
/// not what it starts from.
let tone: BoardTop.Design =
    BoardTop.design
        "Tone"
        (fun n ->
            { beats = n.streamIn ("beats", pins1 ("beat", unsignedInt 32))
              out = n.streamOut ("out", stereo)
              step = n.valueIn ("step", unsignedInt tonePhaseWidth, toneStepFor n.clocking.rate 440.0) })
        (fun io ->
            // The generator runs while a beat is offered, and the beat
            // leaves with its sample.
            let beats = io.beats.stream
            let generated = toneGenerator "ToneGenerator" "tone" beats.valid io.step
            generated.ready ==> beats.ready
            io.out generated)

/// What the passthru asks of the world: rows in, the same rows out, and two
/// readings of what arrived.
type PassthruIo =
    { input: BoardTop.Incoming<Expr * Expr>
      out: Stream<Expr * Expr> -> unit
      /// Rows taken in since load. Moving says the input is clocking.
      receivedCount: Expr -> unit
      /// The left sample of the last row taken. Not zero says it carries
      /// something other than silence.
      lastLeft: Expr -> unit }

/// Line in to line out, or a WAV to the same WAV: the receiver's stream
/// handed straight to the transmitter. The pipeline is one line long, which
/// is the whole claim — the `Stream` carries its own handshake, so passing
/// audio through is `input` into `out` with nothing in between, and adding
/// a stage later is adding a line.
///
/// The two readings are the bring-up's: "no sound" has two causes that look
/// the same from a speaker — an input that never clocks and an output that
/// never sends — and these say which, from the host, on any board.
let passthru: BoardTop.Design =
    BoardTop.design
        "Passthru"
        (fun n ->
            { input = n.streamIn ("in", stereo)
              out = n.streamOut ("out", stereo)
              receivedCount = n.valueOut ("receivedCount", unsignedInt 32)
              lastLeft = n.valueOut ("lastLeft", signedInt sampleWidth) })
        (fun io ->
            let received = io.input.stream
            let left, _ = received.payload
            let count = reg "received_count" 32
            let lastLeft = reg "last_left" (width left)

            If (received.valid &&& received.ready) (fun () ->
                count + lit 1UL 32 ==> count
                left ==> lastLeft)

            io.receivedCount count
            io.lastLeft lastLeft
            received |> io.out)

/// A rung by the name a verb takes, with its mapping for a board: the tone
/// is a frame the design draws, so it is counted in; the passthru is rows
/// both ways. Either reaches the host through its memory or over its link,
/// as the board has.
type Rung =
    { name: string
      design: BoardTop.Design
      mapping: Mapping.Mapping }

/// The board the ladder is built for. Moving it to another board is this line.
let board = iceBreakerAt 24_000_000

let private mappingOn (path: Board -> DataPath) : Mapping.Mapping =
    { board = board; path = path board; plugged = [] }

let rungs: Rung list =
    [ { name = "tone"; design = tone; mapping = mappingOn viaCountHost }
      { name = "passthru"; design = passthru; mapping = mappingOn viaHost } ]

/// A rung by name, or the names there are.
let rungNamed (name: string) : Result<Rung, string> =
    match rungs |> List.tryFind (fun r -> r.name = name) with
    | Some r -> Ok r
    | None ->
        let names = rungs |> List.map (fun r -> r.name) |> String.concat " or "
        Error $"a rung is {names}, not '{name}'"
