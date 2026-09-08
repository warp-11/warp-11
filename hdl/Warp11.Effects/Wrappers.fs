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

/// What these designs are built for: the KV260 at the clock the audio apps'
/// overlays program. Bound here, once, the way every project binds the board
/// it targets — `kv260` is shared by the audio apps because they genuinely
/// share a clock, and the accelerators next door pin their own.
let board = kv260

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

/// The register map's values, pulled into ordinary values once.
///
/// The `aidSettings` move: `SlaveRegs` stops at the top of the design and every
/// stage below takes values it can understand, so nothing downstream has to
/// know there is a host at all. It also puts the five dynamics controls into
/// the record `audioCompressor` asks for, in one place where the names are
/// visible side by side.
type EffectsSettings =
    { volume: Expr
      mute: Expr
      eq: Expr list
      compressor: CompressorSettings
      limitThreshold: Expr }

let effectsSettings (regs: SlaveRegs) : EffectsSettings =
    { volume = regs.value effectsRegs.volume
      mute = regs.value effectsRegs.mute
      eq = List.map regs.value effectsRegs.eq
      compressor =
        { threshold = regs.value effectsRegs.compThreshold
          ratio = regs.value effectsRegs.compRatio
          attack = regs.value effectsRegs.compAttack
          releaseRate = regs.value effectsRegs.compRelease
          makeup = regs.value effectsRegs.compMakeup }
      limitThreshold = regs.value effectsRegs.limitThreshold }

// ---------------------------------------------------------------------------
// The designs.

/// The audio apps' link: the Pmod I2S2's two converters on their own connector
/// rows, at the rate the stock divisors make on this board.
///
/// `i2sLink` rather than a clock generator plus a receiver plus a transmitter
/// wired up here: the two edge ticks never reach this file, and wiring the
/// transmit tick to the receiver is a bug that passes elaboration, passes every
/// stream check, and cannot be caught in simulation at all — a zero-delay model
/// has no opinion about which edge a line is sampled on. The pins come from
/// `i2sPins` for the same reason: `mclk2`/`sclk2`/`lrclk2` were once declared
/// separately from `mclk`/`sclk`/`lrclk`, and three designs shipped with the
/// ADC unclocked because a top forgot the second trio.
let private audioLink prefix pins =
    i2sLink prefix pins board.fabricHz (int stockSampleRate) stockBitsPerSlot

/// Tone generator straight into the transmitter — no receiver, because there
/// is nothing to receive. The smallest thing that makes noise on the board.
let audioToneAxi =
    defModuleClocked
        axiClock
        "AudioToneAxi"
        (fun p -> (axiLiteSlavePorts p toneMap.apertureAddrWidth, i2sTxPins p SeparateCodecs))
        (fun (slavePorts, pins) ->
            let regs = regMapSlave slavePorts toneMap
            let i2s = i2sTxLink "audio" pins board.fabricHz (int stockSampleRate) stockBitsPerSlot

            toneGenerator "ToneGenerator" "tone" (regs.value toneRegs.enable) (regs.value toneRegs.step)
            |> i2s.sendOnly)

/// Line in to line out, with a mute and two bring-up taps. The taps exist
/// because "no sound" has two very different causes — a silent ADC and a dead
/// transmitter — and on a board you cannot see which.
let audioPassthruAxi =
    defModuleClocked
        axiClock
        "AudioPassthruAxi"
        (fun p -> (axiLiteSlavePorts p passthruMap.apertureAddrWidth, i2sPins p SeparateCodecs))
        (fun (slavePorts, pins) ->
            let regs = regMapSlave slavePorts passthruMap
            let i2s = audioLink "audio" pins

            let received = i2s.input

            let count = reg "received_count" 32
            let lastLeft = reg "last_left" sampleWidth
            let left, _right = received.payload

            If received.valid (fun () ->
                count + lit 1UL 32 ==> count
                left ==> lastLeft)

            regs.drive passthruRegs.receivedCount count
            regs.drive passthruRegs.lastLeft lastLeft

            // Mute here rather than through a gain stage: this app is the
            // signal-path bring-up, so it stays as close to a wire as it can.
            let muted = wireBit "muted"
            regs.value passthruRegs.mute ==> muted

            let mute (s: Stream<Expr * Expr>) =
                let left, right = s.payload
                let silence = lit 0UL (width left)

                { s with payload = (mux muted silence left, mux muted silence right) }

            received |> mute |> i2s.send)

/// Line in, master volume, line out.
let audioGainAxi =
    defModuleClocked
        axiClock
        "AudioGainAxi"
        (fun p -> (axiLiteSlavePorts p gainMap.apertureAddrWidth, i2sPins p SeparateCodecs))
        (fun (slavePorts, pins) ->
            let regs = regMapSlave slavePorts gainMap
            let i2s = audioLink "audio" pins

            let gain =
                audioGain "AudioGain" "gain" (regs.value gainRegs.volume) (regs.value gainRegs.mute)

            i2s.input |> gain |> i2s.send)

/// The full chain: volume, one EQ band, a compressor and a brick-wall limiter,
/// every stage host-controlled and every default a no-op.
let audioEffectsAxi =
    defModuleClocked
        axiClock
        "AudioEffectsAxi"
        (fun p -> (axiLiteSlavePorts p effectsMap.apertureAddrWidth, i2sPins p SeparateCodecs))
        (fun (slavePorts, pins) ->
            let regs = regMapSlave slavePorts effectsMap
            let settings = effectsSettings regs
            let i2s = audioLink "audio" pins

            let gain = audioGain "AudioGain" "gain" settings.volume settings.mute
            let equaliser = audioEqBand "AudioEqBand" "eq" settings.eq
            let compressor = audioCompressor "AudioCompressor" "compressor" settings.compressor
            let limiter = audioLimiter "AudioLimiter" "limiter" settings.limitThreshold

            i2s.input
            |> gain
            |> equaliser
            |> compressor
            |> limiter
            |> i2s.send)
