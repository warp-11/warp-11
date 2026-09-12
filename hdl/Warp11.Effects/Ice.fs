/// The same audio front end as the KV260 apps next door, with nothing around
/// it: no AXI slave, no register map, no host.
///
/// **This is the second board the design surface has ever been asked to serve**,
/// and the point of it is that the hardware below the wrapper is unchanged.
/// `i2sPins`, `i2sLink` and `toneGenerator` are the same functions
/// `audioPassthruAxi` calls; what differs is the clock they are handed and the
/// fact that the two diagnostics land on LEDs rather than in a slave a driver
/// reads. If something here needed a different receiver or a different framer,
/// that would be a finding about the abstraction rather than about the board.
///
/// **What the iCEBreaker cannot do is carry its own controls.** Every KV260
/// audio app resets to a no-op and is then steered by a host; these two have no
/// host at all until the FTDI register window exists, so they are fixed
/// function by construction — the tone plays on load and the passthru passes on
/// load, which is the same "a freshly loaded bitstream works" property arrived
/// at from the other direction.
///
/// Both drive a Pmod I2S2 in PMOD1A: line level in, line level out. That is the
/// whole of what this file targets — `i2sPins` offers a second pinout, and a
/// design that wants it declares it at its own io factory.
[<AutoOpen>]
module Warp11.Effects.Ice

open Warp11

/// What these designs are built for: the iCEBreaker clocked from its
/// `SB_PLL40_PAD`, not from its crystal.
///
/// 24 MHz is the PLL output the hand-written top in `hardware/ice40/` programs,
/// and it is chosen by the converter rather than by preference — see
/// `iceSampleRate`. The board is bound here once and everything derives off it,
/// which is the same discipline `board = kv260` gets in `Wrappers.fs`.
let iceBoard = iceBreakerAt 24_000_000

/// The rate these designs frame at: 46 875 Hz.
///
/// **Not a choice, and not 48 kHz.** The CS5343 and CS4344 on a Pmod I2S2 lock
/// to MCLK at 256x, 384x or 512x the frame rate, and `i2sMasterHz` builds MCLK
/// by toggling a register — so the fastest MCLK a design can present is half
/// its fabric clock, and the highest frame rate it can support is therefore
/// `fabric / (2 * 256)`. At 24 MHz that is 46 875 Hz exactly, with the divisors
/// landing on whole numbers in both dividers rather than being rounded to it.
///
/// Asking for 48 kHz here is an elaboration error rather than a 2.3% detune,
/// which is `i2sMasterHz`'s doing and is the behaviour the living check pins.
let iceSampleRate = iceBoard.fabricHz / 512

/// The link every design in this file runs, so the board, the rate and the slot
/// are stated once rather than at each call.
let private iceLink prefix pins =
    i2sLink prefix pins iceBoard.fabricHz iceSampleRate stockBitsPerSlot

// ---------------------------------------------------------------------------
// The diagnostics.
//
// `audioPassthruAxi` carries two bring-up taps — a frame count that moves when
// the ADC is clocking, and a last-sample tap that is non-zero when it is
// hearing something — because "no sound" has two completely different causes
// and a board cannot be asked which. That reasoning does not weaken on a part
// with no host; it gets worse, because there is nothing to read the taps with.
// So they become the two LEDs the board already has.

/// The iCEBreaker's two on-board LEDs, as this file drives them.
///
/// **Active high, which is not what the board is.** LEDR_N and LEDG_N are
/// active low, and the inversion lives in the hand-written top rather than
/// here: a design that knew its indicator was wired to ground would be carrying
/// a fact about one board's schematic into hardware that is otherwise portable,
/// and the next board would need it removed rather than re-mapped.
type IceLeds =
    { /// Green. Toggles a few times a second while frames are arriving — the
      /// link is clocking.
      activity: Output
      /// Red. Lit while the signal is above `signalThresholdDb` — it is
      /// carrying audio rather than silence.
      signal: Output }

/// Declare the LED pins. Call from a module's io factory, like `i2sPins`.
let private iceLedPorts (p: Ports) : IceLeds =
    { activity = p.outPort "led_activity" 1
      signal = p.outPort "led_signal" 1 }

/// How loud counts as hearing something: -48 dBFS, which is quiet enough to
/// catch a line-level source turned well down and far enough above a converter's
/// noise floor that the LED is not simply on for ever.
let private signalThresholdDb = -48.0

let private signalThreshold =
    uint64 (float (1 <<< (sampleWidth - 1)) * (10.0 ** (signalThresholdDb / 20.0)))

/// Drive both LEDs from one beat of the stream: `beat` pulses once per accepted
/// stereo frame, `sample` is the left channel of it.
///
/// **Both indicators are paced by the frame rate, not by the fabric clock**, so
/// they read the same on any board this runs on. A blink derived from a cycle
/// count would be four times faster on the KV260 and would say nothing about
/// whether the *link* was alive, which is the entire question being asked.
let private driveIceLeds (leds: IceLeds) (beat: Expr) (sample: Expr) =
    // Four wraps a second, toggling on each: a 2 Hz blink, slow enough to read
    // as "alive" rather than as a lit LED.
    let blink = counter "blink_count" (iceSampleRate / 4) beat
    let activity = regBit "led_activity_reg"
    If blink.wrap (fun () -> bnot activity ==> activity)
    activity ==> leds.activity

    // The magnitude idiom the limiter and the compressor use: negate, then pick
    // on the sign bit.
    let negated = wire "negated" sampleWidth
    sub (lit 0UL sampleWidth) sample ==> negated

    let absolute = wire "absolute" sampleWidth
    mux (slice (sampleWidth - 1) (sampleWidth - 1) sample) negated sample ==> absolute

    // Held for an eighth of a second per loud frame, because a signal crossing
    // the threshold for one frame in a hundred is still audio and a one-frame
    // LED pulse is 21 microseconds of light.
    let holdFrames = iceSampleRate / 8
    let holdWidth = ceilLog2 (holdFrames + 1)
    let hold = reg "signal_hold" holdWidth
    // `lt` with the operands the other way round: the DSL's compare is
    // one-directional, and `Audio.fs` keeps a private `greaterThan` of its own.
    let loud = lt (lit signalThreshold sampleWidth) absolute
    let holding = lt (lit 0UL holdWidth) hold

    If beat (fun () ->
        mux loud (lit (uint64 holdFrames) holdWidth) (mux holding (hold - lit 1UL holdWidth) (lit 0UL holdWidth))
        ==> hold)

    // **Registered, not the comparator's output.** Both indicators leave the
    // design on a flip-flop, which costs two of them and takes the LEDs out of
    // the timing picture entirely: driven combinationally, a thirteen-bit
    // compare reaching a pad was the critical path of both iCEBreaker builds —
    // an indicator setting the frequency the audio has to meet. It is also
    // simply what an output pin should be, glitch-free by construction.
    let signal = regBit "led_signal_reg"
    holding ==> signal
    signal ==> leds.signal

// ---------------------------------------------------------------------------
// The designs. Two rungs of the bring-up ladder `Warp11.Effects/README.md`
// describes, in the order to build them: the one that only needs the D/A, then
// the one that also needs the A/D.

/// A 440 Hz tone into the transmitter, and nothing listening. The smallest
/// thing that makes a sound on this board — so a silent bench narrows to the
/// PLL, the divisors, the pin map or the D/A, with the receive side not yet in
/// the picture.
///
/// The step is derived from `iceSampleRate` rather than borrowed from the KV260
/// apps: `toneStep440` is 440 Hz at 48 828 Hz, and reused unchanged here it
/// would play a flat 422 Hz — right enough to sound like success.
let audioToneIce =
    defModule
        "AudioToneIce"
        (fun p -> (i2sTxPins p SeparateCodecs, iceLedPorts p))
        (fun (pins, leds) ->
            let i2s = i2sTxLink "audio" pins iceBoard.fabricHz iceSampleRate stockBitsPerSlot

            let step = lit (toneStepFor (float iceSampleRate) 440.0) tonePhaseWidth
            let tone = toneGenerator "ToneGenerator" "tone" (lit 1UL 1) step

            i2s.sendOnly tone

            // Tapped after `sendOnly`, which is what drives `ready`. The LEDs
            // then mean here exactly what they mean in the passthru — frames
            // are moving, and they are carrying something — except that the
            // something is this design's own, which is what makes them a check
            // on the indicators as much as on the link.
            let left, _right = tone.payload
            driveIceLeds leds (tone.valid &&& tone.ready) left)

/// Line in to line out: the receiver's stream handed straight to the
/// transmitter.
///
/// The body is one pipeline and one line long, which is the whole claim — the
/// `Stream` carries its own handshake, so passing audio through is `input` into
/// `send` with nothing in between, and adding a stage later is adding a line.
let audioPassthruIce =
    defModule
        "AudioPassthruIce"
        (fun p -> (i2sPins p SeparateCodecs, iceLedPorts p))
        (fun (pins, leds) ->
            let i2s = iceLink "audio" pins
            let received = i2s.input

            let left, _right = received.payload
            driveIceLeds leds received.valid left

            received |> i2s.send)
