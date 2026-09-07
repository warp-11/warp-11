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
// the original slaves so a host reading either stack finds the same words.

type ToneRegs =
    { enable: RegEntry
      step: RegEntry }

let toneRegs, toneMap =
    buildRegMapPinned audioApertureAddrWidth (fun r ->
        // enable defaults to 1: the tone plays on bitstream load, which is the
        // whole point of a tone test — no driver required to hear something.
        { enable = r.RwReg("enable", 1, 1UL)
          step = r.RwReg("step", tonePhaseWidth, toneStep440) })

type PassthruRegs =
    { mute: RegEntry
      receivedCount: RegEntry
      lastLeft: RegEntry }

let passthruRegs, passthruMap =
    buildRegMapPinned audioApertureAddrWidth (fun r ->
        // Bring-up diagnostics: a count that moves proves the ADC is clocking,
        // and a last-sample tap proves it is carrying something other than
        // silence.
        { mute = r.RwReg("mute", 1, 0UL)
          receivedCount = r.RoField("receivedCount", 32)
          lastLeft = r.RoField("lastLeft", sampleWidth) })

type GainRegs =
    { volume: RegEntry
      mute: RegEntry }

let gainRegs, gainMap =
    buildRegMapPinned audioApertureAddrWidth (fun r ->
        { volume = r.RwReg("volume", 16, gainUnity)
          mute = r.RwReg("mute", 1, 0UL) })

type EffectsRegs =
    { volume: RegEntry
      mute: RegEntry
      eq: RegEntry list
      compThreshold: RegEntry
      compRatio: RegEntry
      compAttack: RegEntry
      compRelease: RegEntry
      compMakeup: RegEntry
      limitThreshold: RegEntry }

let effectsRegs, effectsMap =
    buildRegMapPinned audioApertureAddrWidth (fun r ->
        { volume = r.RwReg("volume", 16, gainUnity)
          mute = r.RwReg("mute", 1, 0UL)

          // The EQ band initialises to the identity kernel, so it is flat until
          // a host writes RBJ-designed coefficients over it.
          eq =
            [ "eq_b0"; "eq_b1"; "eq_b2"; "eq_a1"; "eq_a2" ]
            |> List.mapi (fun i n ->
                let init = if i = 0 then biquadUnity else 0UL
                r.RwReg(n, biquadCoeffWidth, init))

          // Threshold at full scale with ratio 0 is "no gain reduction whatever
          // the signal", and unity makeup leaves the level alone.
          compThreshold = r.RwReg("comp_threshold", sampleWidth, sampleMaxUnsigned)
          compRatio = r.RwReg("comp_ratio", 8, 0UL)
          compAttack = r.RwReg("comp_attack", 16, 0UL)
          compRelease = r.RwReg("comp_release", 16, 0UL)
          compMakeup = r.RwReg("comp_makeup", 16, gainUnity)
          // Full-scale limit: never clamps until a host tightens it.
          limitThreshold = r.RwReg("limit_threshold", sampleWidth, sampleMaxSigned) })

// ---------------------------------------------------------------------------
// The designs.

/// The codec-facing pins, declared once in each top's io factory. Every audio
/// app drives exactly these.
type CodecPorts =
    { mclk: Output
      sclk: Output
      lrclk: Output
      sdin: Output }

let codecPorts (p: Ports) : CodecPorts =
    { mclk = p.outPort "mclk" 1
      sclk = p.outPort "sclk" 1
      lrclk = p.outPort "lrclk" 1
      sdin = p.outPort "sdin" 1 }

/// The body half: the clock generator's pins and the serial line onto the
/// boundary.
let private driveCodec (pins: CodecPorts) mclkPin sclkPin lrclkPin serial =
    mclkPin ==> pins.mclk
    sclkPin ==> pins.sclk
    lrclkPin ==> pins.lrclk
    serial ==> pins.sdin

/// The A/D converter's own clock pins.
///
/// The Pmod I2S2's two converters are **separate chips on separate connector
/// rows**, each with its own MCLK/LRCK/SCLK input — the DAC on J2.1-4, the ADC
/// on J2.7-10. One clock generator drives both, but the pins are physically
/// distinct and all six have to be driven.
///
/// A design that only transmits (`audioToneAxi`) does not declare these, and
/// its `.xdc` binds four pins to match. Every design that *receives* does, and
/// omitting them is what kept `audioPassthruAxi`, `audioGainAxi` and
/// `audioEffectsAxi` from building for the board: their constraint files bind
/// eight pins against five declared ones, so the ADC sat unclocked.
type AdcClockPorts =
    { mclk2: Output
      sclk2: Output
      lrclk2: Output }

let adcClockPorts (p: Ports) : AdcClockPorts =
    { mclk2 = p.outPort "mclk2" 1
      sclk2 = p.outPort "sclk2" 1
      lrclk2 = p.outPort "lrclk2" 1 }

/// The same three clocks the DAC gets. Receiver and transmitter share one
/// frame, so they must share one clock — a second generator would drift.
let private driveAdcClocks (pins: AdcClockPorts) mclkPin sclkPin lrclkPin =
    mclkPin ==> pins.mclk2
    sclkPin ==> pins.sclk2
    lrclkPin ==> pins.lrclk2

/// Tone generator straight into the transmitter — no receiver, because there
/// is nothing to receive. The smallest thing that makes noise on the board.
let audioToneAxi =
    defModuleClocked
        axiClock
        "AudioToneAxi"
        (fun p -> (axiLiteSlavePorts p toneMap.apertureAddrWidth, codecPorts p))
        (fun (slavePorts, pins) ->
        let regs = regMapSlave slavePorts toneMap
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster")

        let tone =
            toneGenerator "ToneGenerator" "tone" (regs.value toneRegs.enable) (regs.value toneRegs.step)

        let serial = i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk tone
        driveCodec pins clocks.mclk clocks.sclk clocks.lrclk serial)

/// Line in to line out, with a mute and two bring-up taps. The taps exist
/// because "no sound" has two very different causes — a silent ADC and a dead
/// transmitter — and on a board you cannot see which.
let audioPassthruAxi =
    defModuleClocked
        axiClock
        "AudioPassthruAxi"
        (fun p ->
            (axiLiteSlavePorts p passthruMap.apertureAddrWidth,
             p.inPort "sdout" 1,
             codecPorts p,
             adcClockPorts p))
        (fun (slavePorts, sdout, pins, adcPins) ->
        let regs = regMapSlave slavePorts passthruMap
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster")

        let received = i2sRx "I2sRx" "rx" clocks.sclkRxTick clocks.lrclk sdout

        let count = reg "received_count" 32
        let lastLeft = reg "last_left" sampleWidth
        let left, right = received.payload

        If received.valid (fun () ->
            count + lit 1UL 32 ==> count
            left ==> lastLeft)

        regs.drive passthruRegs.receivedCount count
        regs.drive passthruRegs.lastLeft lastLeft

        // Mute here rather than through a gain stage: this app is the
        // signal-path bring-up, so it stays as close to a wire as it can.
        let muted = wireBit "muted"
        regs.value passthruRegs.mute ==> muted

        let gated =
            { received with
                payload = (mux muted (lit 0UL sampleWidth) left, mux muted (lit 0UL sampleWidth) right) }

        let serial = i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk gated
        driveCodec pins clocks.mclk clocks.sclk clocks.lrclk serial
        driveAdcClocks adcPins clocks.mclk clocks.sclk clocks.lrclk)

/// Line in, master volume, line out.
let audioGainAxi =
    defModuleClocked
        axiClock
        "AudioGainAxi"
        (fun p ->
            (axiLiteSlavePorts p gainMap.apertureAddrWidth,
             p.inPort "sdout" 1,
             codecPorts p,
             adcClockPorts p))
        (fun (slavePorts, sdout, pins, adcPins) ->
        let regs = regMapSlave slavePorts gainMap
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster")

        let gain =
            audioGain "AudioGain" "gain" (regs.value gainRegs.volume) (regs.value gainRegs.mute)

        let serial =
            i2sRx "I2sRx" "rx" clocks.sclkRxTick clocks.lrclk sdout
            |> gain
            |> i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk

        driveCodec pins clocks.mclk clocks.sclk clocks.lrclk serial
        driveAdcClocks adcPins clocks.mclk clocks.sclk clocks.lrclk)

/// The full chain: volume, one EQ band, a compressor and a brick-wall limiter,
/// every stage host-controlled and every default a no-op.
let audioEffectsAxi =
    defModuleClocked
        axiClock
        "AudioEffectsAxi"
        (fun p ->
            (axiLiteSlavePorts p effectsMap.apertureAddrWidth,
             p.inPort "sdout" 1,
             codecPorts p,
             adcClockPorts p))
        (fun (slavePorts, sdout, pins, adcPins) ->
        let regs = regMapSlave slavePorts effectsMap
        let clocks = instanceNamed "clocks" (i2sMasterDefault "I2sMaster")

        let gain =
            audioGain "AudioGain" "gain" (regs.value effectsRegs.volume) (regs.value effectsRegs.mute)

        let equaliser = audioEqBand "AudioEqBand" "eq" (List.map regs.value effectsRegs.eq)

        let compressor =
            audioCompressor
                "AudioCompressor"
                "compressor"
                (regs.value effectsRegs.compThreshold)
                (regs.value effectsRegs.compRatio)
                (regs.value effectsRegs.compAttack)
                (regs.value effectsRegs.compRelease)
                (regs.value effectsRegs.compMakeup)

        let limiter =
            audioLimiter "AudioLimiter" "limiter" (regs.value effectsRegs.limitThreshold)

        let serial =
            i2sRx "I2sRx" "rx" clocks.sclkRxTick clocks.lrclk sdout
            |> gain
            |> equaliser
            |> compressor
            |> limiter
            |> i2sTx "I2sTx" "tx" clocks.sclkTxTick clocks.lrclk

        driveCodec pins clocks.mclk clocks.sclk clocks.lrclk serial
        driveAdcClocks adcPins clocks.mclk clocks.sclk clocks.lrclk)
