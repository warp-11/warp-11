/// The audio example's board-facing designs: an I2S front end, a DSP stage,
/// an I2S back end, and an AXI-Lite control surface over the lot.
///
/// Every register initialises to a no-op, so a freshly loaded bitstream passes
/// audio cleanly with no driver writes at all. That is a deliberate property of
/// this example rather than a convenience — the board apps here have no host
/// daemon in public warp11, so "load the bitstream and listen" has to work.
[<AutoOpen>]
module Warp11.Effects.Wrappers

open Warp11

/// Every audio slave uses a 256-byte aperture; none of them needs more.
let private audioApertureAddrWidth = 8

let private sampleMaxUnsigned = (1UL <<< sampleWidth) - 1UL
let private sampleMaxSigned = (1UL <<< (sampleWidth - 1)) - 1UL

// ---------------------------------------------------------------------------
// The register maps. One value per entry, so the wrapper and the generated
// Rust name each register exactly once and cannot drift apart. Offsets mirror
// the Kotlin slaves so a host reading either stack finds the same words.

type ToneMap =
    { enable: RegEntry
      step: RegEntry
      map: RegMap }

let toneMap: ToneMap =
    // enable defaults to 1: the tone plays on bitstream load, which is the
    // whole point of a tone test — no driver required to hear something.
    let enable = rwReg "enable" 0x000UL 1 1UL
    let step = rwReg "step" 0x004UL tonePhaseWidth toneStep440

    { enable = enable
      step = step
      map =
        { apertureAddrWidth = audioApertureAddrWidth
          entries = [ enable; step ] } }

type PassthruMap =
    { mute: RegEntry
      receivedCount: RegEntry
      lastLeft: RegEntry
      map: RegMap }

let passthruMap: PassthruMap =
    let mute = rwReg "mute" 0x000UL 1 0UL
    // Bring-up diagnostics: a count that moves proves the ADC is clocking, and
    // a last-sample tap proves it is carrying something other than silence.
    let receivedCount = roField "receivedCount" 0x004UL 0 32
    let lastLeft = roField "lastLeft" 0x008UL 0 sampleWidth

    { mute = mute
      receivedCount = receivedCount
      lastLeft = lastLeft
      map =
        { apertureAddrWidth = audioApertureAddrWidth
          entries = [ mute; receivedCount; lastLeft ] } }

type GainMap =
    { volume: RegEntry
      mute: RegEntry
      map: RegMap }

let gainMap: GainMap =
    let volume = rwReg "volume" 0x000UL 16 gainUnity
    let mute = rwReg "mute" 0x004UL 1 0UL

    { volume = volume
      mute = mute
      map =
        { apertureAddrWidth = audioApertureAddrWidth
          entries = [ volume; mute ] } }

type EffectsMap =
    { volume: RegEntry
      mute: RegEntry
      eq: RegEntry list
      compThreshold: RegEntry
      compRatio: RegEntry
      compAttack: RegEntry
      compRelease: RegEntry
      compMakeup: RegEntry
      limitThreshold: RegEntry
      map: RegMap }

let effectsMap: EffectsMap =
    let volume = rwReg "volume" 0x000UL 16 gainUnity
    let mute = rwReg "mute" 0x004UL 1 0UL

    // The EQ band initialises to the identity kernel, so it is flat until a
    // host writes RBJ-designed coefficients over it.
    let eqNames = [ "eq_b0"; "eq_b1"; "eq_b2"; "eq_a1"; "eq_a2" ]

    let eq =
        eqNames
        |> List.mapi (fun i n ->
            let init = if i = 0 then biquadUnity else 0UL
            rwReg n (0x008UL + uint64 (i * 4)) biquadCoeffWidth init)

    // Threshold at full scale with ratio 0 is "no gain reduction whatever the
    // signal", and unity makeup leaves the level alone.
    let compThreshold = rwReg "comp_threshold" 0x01CUL sampleWidth sampleMaxUnsigned
    let compRatio = rwReg "comp_ratio" 0x020UL 8 0UL
    let compAttack = rwReg "comp_attack" 0x024UL 16 0UL
    let compRelease = rwReg "comp_release" 0x028UL 16 0UL
    let compMakeup = rwReg "comp_makeup" 0x02CUL 16 gainUnity
    // Full-scale limit: never clamps until a host tightens it.
    let limitThreshold = rwReg "limit_threshold" 0x030UL sampleWidth sampleMaxSigned

    { volume = volume
      mute = mute
      eq = eq
      compThreshold = compThreshold
      compRatio = compRatio
      compAttack = compAttack
      compRelease = compRelease
      compMakeup = compMakeup
      limitThreshold = limitThreshold
      map =
        { apertureAddrWidth = audioApertureAddrWidth
          entries =
            [ volume; mute ]
            @ eq
            @ [ compThreshold; compRatio; compAttack; compRelease; compMakeup; limitThreshold ] } }

// ---------------------------------------------------------------------------
// The designs.

/// The codec-facing pins, declared once. Every audio app drives exactly these.
/// Taken as four values rather than the clock generator's IO record: an
/// anonymous record does not unify across an assembly boundary, so naming the
/// pins is both simpler and the only thing that compiles here.
let private codecPins mclkPin sclkPin lrclkPin serial =
    let mclk = outputBit "mclk"
    mclkPin ==> mclk
    let sclk = outputBit "sclk"
    sclkPin ==> sclk
    let lrclk = outputBit "lrclk"
    lrclkPin ==> lrclk
    let sdin = outputBit "sdin"
    serial ==> sdin

/// Tone generator straight into the transmitter — no receiver, because there
/// is nothing to receive. The smallest thing that makes noise on the board.
let audioToneAxi =
    designClocked axiClock "AudioToneAxi" (fun () ->
        let regs = axiLiteSlaveOf toneMap.map
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster") ()

        let tone =
            instanceNamed "tone" (toneGenerator "ToneGenerator") (regs.value toneMap.enable) (regs.value toneMap.step)

        let serial = instanceNamed "tx" (i2sTx "I2sTx") clocks.sclkTxTick clocks.lrclk tone
        codecPins clocks.mclk clocks.sclk clocks.lrclk serial)

/// Line in to line out, with a mute and two bring-up taps. The taps exist
/// because "no sound" has two very different causes — a silent ADC and a dead
/// transmitter — and on a board you cannot see which.
let audioPassthruAxi =
    designClocked axiClock "AudioPassthruAxi" (fun () ->
        let regs = axiLiteSlaveOf passthruMap.map
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster") ()
        let sdout = inputBit "sdout"

        let received = instanceNamed "rx" (i2sRx "I2sRx") clocks.sclkRxTick clocks.lrclk sdout

        let count = reg "received_count" 32
        let lastLeft = reg "last_left" sampleWidth
        let left, right = received.payload

        If received.valid (fun () ->
            count + lit 1UL 32 ==> count
            left ==> lastLeft)

        regs.drive passthruMap.receivedCount count
        regs.drive passthruMap.lastLeft lastLeft

        // Mute here rather than through a gain stage: this app is the
        // signal-path bring-up, so it stays as close to a wire as it can.
        let muted = wireBit "muted"
        regs.value passthruMap.mute ==> muted

        let gated =
            { received with
                payload = (mux muted (lit 0UL sampleWidth) left, mux muted (lit 0UL sampleWidth) right) }

        let serial = instanceNamed "tx" (i2sTx "I2sTx") clocks.sclkTxTick clocks.lrclk gated
        codecPins clocks.mclk clocks.sclk clocks.lrclk serial)

/// Line in, master volume, line out.
let audioGainAxi =
    designClocked axiClock "AudioGainAxi" (fun () ->
        let regs = axiLiteSlaveOf gainMap.map
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster") ()
        let sdout = inputBit "sdout"

        let gain =
            instanceNamed "gain" (audioGain "AudioGain") (regs.value gainMap.volume) (regs.value gainMap.mute)

        let serial =
            instanceNamed "rx" (i2sRx "I2sRx") clocks.sclkRxTick clocks.lrclk sdout
            |> gain
            |> instanceNamed "tx" (i2sTx "I2sTx") clocks.sclkTxTick clocks.lrclk

        codecPins clocks.mclk clocks.sclk clocks.lrclk serial)

/// The full chain: volume, one EQ band, a compressor and a brick-wall limiter,
/// every stage host-controlled and every default a no-op.
let audioEffectsAxi =
    designClocked axiClock "AudioEffectsAxi" (fun () ->
        let regs = axiLiteSlaveOf effectsMap.map
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster") ()
        let sdout = inputBit "sdout"

        let gain =
            instanceNamed "gain" (audioGain "AudioGain") (regs.value effectsMap.volume) (regs.value effectsMap.mute)

        let equaliser = instanceNamed "eq" (audioEqBand "AudioEqBand") (List.map regs.value effectsMap.eq)

        let compressor =
            instanceNamed
                "compressor"
                (audioCompressor "AudioCompressor")
                (regs.value effectsMap.compThreshold)
                (regs.value effectsMap.compRatio)
                (regs.value effectsMap.compAttack)
                (regs.value effectsMap.compRelease)
                (regs.value effectsMap.compMakeup)

        let limiter =
            instanceNamed "limiter" (audioLimiter "AudioLimiter") (regs.value effectsMap.limitThreshold)

        let serial =
            instanceNamed "rx" (i2sRx "I2sRx") clocks.sclkRxTick clocks.lrclk sdout
            |> gain
            |> equaliser
            |> compressor
            |> limiter
            |> instanceNamed "tx" (i2sTx "I2sTx") clocks.sclkTxTick clocks.lrclk

        codecPins clocks.mclk clocks.sclk clocks.lrclk serial)
