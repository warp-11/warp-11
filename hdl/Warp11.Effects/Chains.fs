/// The two audio chains as designs: the effects chain (volume, one EQ band, a
/// compressor, a limiter) and the 8-band multiband compressor. Neither names
/// a converter, a bus or a board — `build effects kv260 pins` is the chain on
/// the Pmod I2S2, `build multiband kv260 memory` is the compressor through the
/// host's DDR, and the same designs go over a link on a board with one wire.
///
/// Every control starts at a no-op, so a freshly loaded bitstream passes audio
/// with nothing written to it: "load it and listen" has to work, and on the
/// memory path "did the DDR plumbing work" stays a separate question from "did
/// the DSP work".
[<AutoOpen>]
module Warp11.Effects.Chains

open Warp11
open Warp11.Audio

/// The stereo row every rung and chain speaks.
let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

let private sampleMaxUnsigned = (1UL <<< sampleWidth) - 1UL
let private sampleMaxSigned = (1UL <<< (sampleWidth - 1)) - 1UL

/// What the effects chain asks of the world: rows in, rows out, and every
/// stage's settings.
type EffectsIo =
    { input: BoardTop.Incoming<Expr * Expr>
      out: Stream<Expr * Expr> -> unit
      volume: Expr
      mute: Expr
      /// b0, b1, b2, a1, a2 — the identity kernel until a host writes
      /// RBJ-designed coefficients over it.
      eq: Expr list
      compressor: CompressorSettings
      limitThreshold: Expr }

/// The chain as a design under `name`: the controls every form of it has,
/// and `chain` between the rows in and the rows out.
let private effectsDesign (name: string) (chain: EffectsIo -> Stream<Expr * Expr> -> Stream<Expr * Expr>) : BoardTop.Design =
    BoardTop.design
        name
        (fun n ->
            { input = n.streamIn ("in", stereo)
              out = n.streamOut ("out", stereo)
              volume = n.valueIn ("volume", unsignedInt 16, gainUnity)
              mute = n.valueIn ("mute", unsignedInt 1, 0UL)
              eq =
                [ "eqB0"; "eqB1"; "eqB2"; "eqA1"; "eqA2" ]
                |> List.mapi (fun i name ->
                    n.valueIn (name, unsignedInt biquadCoeffWidth, (if i = 0 then biquadUnity else 0UL)))
              // Threshold at full scale with ratio 0 is "no gain reduction
              // whatever the signal", and unity makeup leaves the level alone.
              compressor =
                { threshold = n.valueIn ("compThreshold", unsignedInt sampleWidth, sampleMaxUnsigned)
                  ratio = n.valueIn ("compRatio", unsignedInt 8, 0UL)
                  attack = n.valueIn ("compAttack", unsignedInt 16, 0UL)
                  releaseRate = n.valueIn ("compRelease", unsignedInt 16, 0UL)
                  makeup = n.valueIn ("compMakeup", unsignedInt 16, gainUnity) }
              // Full-scale limit: never clamps until a host tightens it.
              limitThreshold = n.valueIn ("limitThreshold", unsignedInt sampleWidth, sampleMaxSigned) })
        (fun io -> io.input.stream |> chain io |> io.out)

/// The chain's multiplier pools, by format: the application's choice, as
/// Mandelbrot's lanes are. Each is a socket a multiplier of its own
/// (`InPlace`) or a pool of copies the sockets take turns on (`Shared n`).
type EffectsMultiplies =
    { /// A sample times a Q8.8 gain, s24 × s17: the gain's two and the
      /// compressor's two boosts. Two of a part's 18×18 blocks a copy.
      sample: Fu.Serving
      /// A sample times a Q2.30 coefficient, s24 × s32: the band's ten. Four
      /// blocks a copy.
      coefficient: Fu.Serving
      /// A boosted sample times the compressor's gain, s33 × s26: its two
      /// applies. Four blocks a copy.
      apply: Fu.Serving }

/// Every multiply its own, a sample a cycle: 60 of the KV260's 1 248 DSPs.
/// The Icepi's 28 blocks hold `{ sample = Shared 2; coefficient = Shared 3;
/// apply = InPlace }` — 4 + 12 + 8, and 4 for the envelope step and the
/// ratio, which are the only multiplies of their formats and keep their own.
let multiplies =
    { sample = Fu.InPlace
      coefficient = Fu.InPlace
      apply = Fu.InPlace }

/// The full chain, every stage host-controlled and every default a no-op,
/// its multiplies served as `multiplies` says.
///
/// The stages are connected `Both`: a stage served by a shared multiply
/// passes `ready` straight through, so every signal between two of them
/// comes from a register.
let effects: BoardTop.Design =
    effectsDesign "Effects" (fun io s ->
        Fu.serving multiplies.sample gainMultiplier "multiply_sample" (fun sampleMultiply ->
            Fu.serving multiplies.coefficient eqMultiplier "multiply_coefficient" (fun coefficientMultiply ->
                Fu.serving multiplies.apply (multiplierFor compressorApplyOperands) "multiply_apply" (fun applyMultiply ->
                    let compressor =
                        { compressorInPlace with
                            boost = sampleMultiply
                            apply = applyMultiply }

                    s
                    |> audioGainWith sampleMultiply "AudioGain" "gain" io.volume io.mute
                    |> connectAs Connection.Both
                    |> audioEqBandWith coefficientMultiply "AudioEqBand" "eq" io.eq
                    |> connectAs Connection.Both
                    |> audioCompressorWith compressor "AudioCompressor" "compressor" io.compressor
                    |> connectAs Connection.Both
                    |> audioLimiter "AudioLimiter" "limiter" io.limitThreshold))))

/// What the multiband compressor asks of the world.
type MultibandIo =
    { input: BoardTop.Incoming<Expr * Expr>
      out: Stream<Expr * Expr> -> unit
      /// The rate the rows run at, which the crossovers are designed for.
      rate: float
      settings: MultibandSettings }

/// The 8-band compressor. Unity settings are an all-pass rather than a copy —
/// the crossover is a phase-matched tree — so a check compares against the
/// bare stage at the same settings, not against what went in.
let multiband: BoardTop.Design =
    BoardTop.design
        "Multiband"
        (fun n ->
            let gains side =
                List.init multibandBands (fun i -> n.valueIn ($"{side}Gain{i}", unsignedInt 16, gainUnity))

            { input = n.streamIn ("in", stereo)
              out = n.streamOut ("out", stereo)
              rate = n.clocking.rate
              settings =
                { threshold = n.valueIn ("threshold", unsignedInt sampleWidth, sampleMaxUnsigned)
                  ratio = n.valueIn ("ratio", unsignedInt 8, 0UL)
                  attack = n.valueIn ("attack", unsignedInt 16, 0UL)
                  releaseRate = n.valueIn ("releaseRate", unsignedInt 16, 0UL)
                  leftGains = gains "left"
                  rightGains = gains "right" } })
        (fun io ->
            let processed, _envelope =
                io.input.stream
                |> multibandCompressor "MultibandCompressor8" io.rate "mb" io.settings

            io.out processed)
