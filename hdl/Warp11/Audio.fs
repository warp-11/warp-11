/// Audio DSP primitives — the stdlib tier the audio example is built from.
///
/// A sample is signed, and says so where it is declared: `SInt 24`. Multiply and
/// compare then read it correctly without being told twice, which is why nothing
/// in here picks between a signed and an unsigned operation.
///
/// The unsigned values are the control registers — volume, threshold, the filter
/// coefficients' magnitude — and each is zero-padded a bit wider before entering
/// signed arithmetic, so it can never present as negative.
[<AutoOpen>]
module Warp11.Audio

/// Per-channel sample width. 24-bit matches Pmod I2S2 (CS5343/CS4344).
let sampleWidth = 24

/// Packed stereo payload width — left in the high bits, right in the low.
let sampleBits = sampleWidth * 2

/// A stereo sample as a typed stream payload. Stages carry this rather than a
/// packed vector: the stream layer already names and widths its fields, so
/// nothing slices by hand and the two ends of a link cannot disagree about
/// which half is left.
let sampleLayout: Layout<Expr * Expr> =
    layout2 ("left", sampleWidth) ("right", sampleWidth)

/// The packed form, for the places that genuinely need one flat bus — an AXI
/// register, a memory word, a payload crossing as a single wire. Left occupies
/// the high bits, matching the Kotlin encoding byte for byte so a host that
/// reads either stack's registers sees the same layout.
let packSample (left: Expr) (right: Expr) : Expr = cat left right

/// Left channel of a packed stereo sample. Slice takes a declared signal, so
/// the caller wires a computed value first.
let sampleLeft (packed: Expr) : Expr = slice (sampleBits - 1) sampleWidth packed

/// Right channel of a packed stereo sample.
let sampleRight (packed: Expr) : Expr = slice (sampleWidth - 1) 0 packed

/// Biquad coefficient encoding: Q2.30 in 32 bits, so 0x40000000 is +1.0 and the
/// representable range is about ±2.0.
let biquadCoeffWidth = 32

let biquadCoeffFrac = 30

/// Q2.30 representation of +1.0 — the identity `b0`.
let biquadUnity = 1UL <<< biquadCoeffFrac

/// The five coefficients of one biquad section, in difference-equation order.
type BiquadCoeffs =
    { b0: Expr
      b1: Expr
      b2: Expr
      a1: Expr
      a2: Expr }

/// Single-section Direct Form I biquad over a mono sample.
///
///     y[n] = b0*x[n] + b1*x[n-1] + b2*x[n-2] − a1*y[n-1] − a2*y[n-2]
///
/// which is the RBJ Audio EQ Cookbook convention — note the minus signs on the
/// feedback terms, and `a0` normalised to 1.
///
/// `advance` is a one-cycle pulse: while it is high on a clock edge the four
/// state registers shift one sample. Holding it low freezes the section, which
/// is how a stream stage stalls without disturbing the filter's state. `y` is
/// combinational from `x`, so a cascade costs no cycles it was not asked for.
///
/// Widths: sample*coefficient products are `sampleWidth + coeffWidth`; the sum
/// of five needs three more bits; the arithmetic shift by `coeffFrac` that
/// recovers the Q format is a *narrowing* shift, which in this IR is a plain
/// slice whose top bit is the sign; signed saturation clips back to a sample.
///
/// Identity coefficients (`b0 = biquadUnity`, the rest zero) give `y[n] = x[n]`
/// exactly — the shift and saturate round-trip losslessly when nothing else
/// contributes.
let biquad (name: string) (sampleWidth: int) (coeffWidth: int) (coeffFrac: int) =
    if sampleWidth < 4 || sampleWidth > 32 then
        failwith $"biquad sampleWidth must be 4..32, got {sampleWidth}"

    if coeffWidth < 16 || coeffWidth > 32 then
        failwith $"biquad coeffWidth must be 16..32, got {coeffWidth}"

    if coeffFrac < 1 || coeffFrac >= coeffWidth then
        failwith $"biquad coeffFrac must be 1..{coeffWidth - 1}, got {coeffFrac}"

    let productWidth = sampleWidth + coeffWidth
    let accWidth = productWidth + 3
    let scaledWidth = accWidth - coeffFrac

    defineModule
        name
        (fun p ->
            {| x = p.inPortAs "x" (SInt sampleWidth)
               advance = p.inPort "advance" 1
               b0 = p.inPortAs "b0" (SInt coeffWidth)
               b1 = p.inPortAs "b1" (SInt coeffWidth)
               b2 = p.inPortAs "b2" (SInt coeffWidth)
               a1 = p.inPortAs "a1" (SInt coeffWidth)
               a2 = p.inPortAs "a2" (SInt coeffWidth)
               y = p.outPortAs "y" (SInt sampleWidth) |})
        (fun m io ->
            fun (x: Expr) (advance: Expr) (c: BiquadCoeffs) ->
                x ==> io.x
                advance ==> io.advance
                c.b0 ==> io.b0
                c.b1 ==> io.b1
                c.b2 ==> io.b2
                c.a1 ==> io.a1
                c.a2 ==> io.a2
                io.y)
        (fun io _ ->
            let xd1 = reg "x_d1" (SInt sampleWidth)
            let xd2 = reg "x_d2" (SInt sampleWidth)
            let yd1 = reg "y_d1" (SInt sampleWidth)
            let yd2 = reg "y_d2" (SInt sampleWidth)

            // Each product lands on a named wire: the sign-extension below
            // replicates a named bit, which the slice rule requires.
            let product name a b =
                let w = wire name (SInt productWidth)
                mul a b ==> w
                signExtend accWidth w

            let feedForward =
                [ product "p_b0" io.x io.b0
                  product "p_b1" xd1 io.b1
                  product "p_b2" xd2 io.b2 ]
                |> reduceTree add

            let feedBack =
                [ product "p_a1" yd1 io.a1
                  product "p_a2" yd2 io.a2 ]
                |> reduceTree add

            let acc = wire "acc" (SInt accWidth)
            sub feedForward feedBack ==> acc

            let scaled = wire "scaled" (SInt scaledWidth)
            shr coeffFrac acc ==> scaled

            let y = wire "y_value" (SInt sampleWidth)
            saturate sampleWidth scaled ==> y
            y ==> io.y

            If io.advance (fun () ->
                xd1 ==> xd2
                io.x ==> xd1
                yd1 ==> yd2
                y ==> yd1))

/// The default section: a 24-bit sample and Q2.30 coefficients.
let biquadSection name = biquad name sampleWidth biquadCoeffWidth biquadCoeffFrac

// ---------------------------------------------------------------------------
// Stereo stream stages. Each is a module whose controls are curried ahead of
// the stream, so partial application yields the `Stream -> Stream` a pipeline
// stage is — the controls are wired at the instantiation site and invisible
// downstream.

/// The stream half of a stereo stage's ports. Factored because all three
/// stages declare exactly this and differ only in their control inputs. Public
/// because it appears in each stage's module type, though nothing outside
/// needs to build one.
type StereoPorts =
    { inLeft: Expr
      inRight: Expr
      inValid: Expr
      inReady: Expr
      outLeft: Expr
      outRight: Expr
      outValid: Expr
      outReady: Expr }

let private stereoPorts (p: Ports) : StereoPorts =
    { inLeft = p.inPortAs "in_left" (SInt sampleWidth)
      inRight = p.inPortAs "in_right" (SInt sampleWidth)
      inValid = p.inPort "in_valid" 1
      inReady = p.outPort "in_ready" 1
      outLeft = p.outPortAs "out_left" (SInt sampleWidth)
      outRight = p.outPortAs "out_right" (SInt sampleWidth)
      outValid = p.outPort "out_valid" 1
      outReady = p.inPort "out_ready" 1 }

/// Drive the instance's input ports from an incoming stream and hand back the
/// outgoing one. The ready net travels the other way, which is the whole
/// reason this is written once.
let private stereoSplice (m: Builder) (sp: StereoPorts) (s: Stream<Expr * Expr>) : Stream<Expr * Expr> =
    let left, right = s.payload
    left ==> sp.inLeft
    right ==> sp.inRight
    s.valid ==> sp.inValid
    sp.inReady ==> s.ready
    m.RegisterStreamReady sp.outReady

    { payload = (sp.outLeft, sp.outRight)
      valid = sp.outValid
      ready = sp.outReady
      layout = sampleLayout }

/// The zero-latency handshake: a combinational stage passes both directions
/// straight through, so it adds no cycle to the pipeline and no state to stall.
let private spliceHandshake (sp: StereoPorts) =
    sp.inValid ==> sp.outValid
    sp.outReady ==> sp.inReady

/// Zero-extend to `target` bits. Concatenation rather than `signExtend`, which
/// would replicate the sign — an unsigned control value read as signed must
/// stay non-negative, and that is exactly what the zero pad guarantees.
let private widenUnsigned (target: int) (x: Expr) : Expr =
    let w = width x
    if target = w then x else cat (lit 0UL (target - w)) x

/// Greater-than. The IR carries `lt` only; `a > b` is `b < a`, and whether the
/// compare is signed is the operands' business, not two functions'.
let private greaterThan a b = lt b a

/// Q8.8 fractional bits in the gain register: `gainUnity` is 1.0x
/// pass-through, 512 is 2.0x, 128 is 0.5x, 0 is silence.
let gainFracBits = 8

let gainUnity = 1UL <<< gainFracBits

/// Combinational stereo volume / mute stage.
///
///     out = saturate24((sample * volume) >> 8), or 0 when muted
///
/// `volume` is unsigned and zero-padded to 17 bits for the signed multiply, so
/// it can never present as negative. Saturation only bites above unity gain —
/// at or below `gainUnity` the shift cannot overflow a sample.
///
/// Mute is a separate gate rather than "write volume 0" so a host can silence
/// the output and restore the previous level without having stored it.
let audioGain (name: string) =
    let productWidth = sampleWidth + 17
    let scaledWidth = productWidth - gainFracBits

    defineModule
        name
        (fun p ->
            {| s = stereoPorts p
               volume = p.inPort "volume" 16
               mute = p.inPort "mute" 1 |})
        (fun m io ->
            fun (volume: Expr) (mute: Expr) (s: Stream<Expr * Expr>) ->
                volume ==> io.volume
                mute ==> io.mute
                stereoSplice m io.s s)
        (fun io _ ->
            spliceHandshake io.s

            let volumeSigned = wire "volume_signed" (SInt 17)
            widenUnsigned 17 io.volume ==> volumeSigned

            let channel channelName x =
                let product = wire $"{channelName}_product" (SInt productWidth)
                mul x volumeSigned ==> product
                let scaled = wire $"{channelName}_scaled" (SInt scaledWidth)
                shr gainFracBits product ==> scaled
                let saturated = wire $"{channelName}_saturated" (SInt sampleWidth)
                saturate sampleWidth scaled ==> saturated
                mux io.mute (lit 0UL sampleWidth) saturated

            channel "left" io.s.inLeft ==> io.s.outLeft
            channel "right" io.s.inRight ==> io.s.outRight)

/// One EQ band: a biquad section per channel, both fed the same five
/// host-written coefficients.
///
/// The sections are clock-enabled by the sample handshake rather than by the
/// fabric clock, so the filter advances once per arriving sample whatever the
/// clock rate — the same design behaves identically at 100 and 166.67 MHz, and
/// stalling the stream freezes the filter state rather than smearing it.
///
/// Identity coefficients (`b0 = biquadUnity`, the rest zero) make the band
/// flat, which is what its registers reset to.
let audioEqBand (name: string) =
    defineModule
        name
        (fun p ->
            {| s = stereoPorts p
               coefficients = List.init 5 (fun i -> p.inPort $"c{i}" biquadCoeffWidth) |})
        (fun m io ->
            fun (coefficients: Expr list) (s: Stream<Expr * Expr>) ->
                List.iter2 (fun port c -> c ==> port) io.coefficients coefficients
                stereoSplice m io.s s)
        (fun io _ ->
            spliceHandshake io.s

            let advance = wireBit "advance"
            (io.s.inValid &&& io.s.outReady) ==> advance

            let section = biquadSection $"{name}Section"

            let coefficients: BiquadCoeffs =
                { b0 = io.coefficients[0]
                  b1 = io.coefficients[1]
                  b2 = io.coefficients[2]
                  a1 = io.coefficients[3]
                  a2 = io.coefficients[4] }

            instanceNamed "left" section io.s.inLeft advance coefficients ==> io.s.outLeft
            instanceNamed "right" section io.s.inRight advance coefficients ==> io.s.outRight)

/// Hard brick-wall stereo limiter — the chain's final safety stage.
///
///     out = clamp(in, -threshold, +threshold)
///
/// Combinational, no envelope, no smoothing. It adds harmonic distortion when
/// it engages, which is the correct behaviour here: audible limiter distortion
/// means the stage is doing the job it exists for. `threshold` is a positive
/// sample value; a host writing a negative one gets a nonsensical limit pair,
/// so the register is treated as effectively unsigned.
let audioLimiter (name: string) =
    defineModule
        name
        (fun p ->
            {| s = stereoPorts p
               threshold = p.inPortAs "threshold" (SInt sampleWidth) |})
        (fun m io ->
            fun (threshold: Expr) (s: Stream<Expr * Expr>) ->
                threshold ==> io.threshold
                stereoSplice m io.s s)
        (fun io _ ->
            spliceHandshake io.s

            let negativeThreshold = wire "negative_threshold" (SInt sampleWidth)
            sub (lit 0UL sampleWidth) io.threshold ==> negativeThreshold

            let clamp x =
                mux
                    (greaterThan x io.threshold)
                    io.threshold
                    (mux (lt x negativeThreshold) negativeThreshold x)

            clamp io.s.inLeft ==> io.s.outLeft
            clamp io.s.inRight ==> io.s.outRight)

// The two halves every compressor in this file shares. Kept as functions
// rather than modules deliberately: they declare into whichever module body
// calls them, so a per-band unit and a stereo one emit the same nets under the
// same names, and the shape stays a definition-site choice.

/// Widths of the envelope/gain datapath, all determined by the sample width.
let private wideWidth = sampleWidth + 1
let private stepWidth = wideWidth + 17
let private envNextWidth = sampleWidth + 4
let private gainRedWidth = sampleWidth + 8
let private gainWidth = sampleWidth + 1
let private gainCap = 1UL <<< sampleWidth

/// Envelope follower: `env += alpha*(peak - env)` with `alpha` the attack
/// coefficient while the signal is rising and the release one while it falls,
/// clipped into the envelope register's unsigned range. Returns the register
/// and its widened form, which the gain computer reuses rather than rebuilding.
///
/// The loop is a recurrence, so it stays combinational — there is no pipelining
/// it, which is why callers pipeline only the apply path around it.
let private envelopeFollower (peak: Expr) (attack: Expr) (releaseRate: Expr) (advance: Expr) : Expr * Expr =
    let env = reg "env" sampleWidth

    let alpha = wire "alpha" 16
    mux (greaterThan peak env) attack releaseRate ==> alpha

    // Zero-padded to a bit wider than the sample, which is what makes these
    // signed values that can never present as negative.
    let envWide = wire "env_wide" (SInt wideWidth)
    widenUnsigned wideWidth env ==> envWide
    let peakWide = wire "peak_wide" (SInt wideWidth)
    widenUnsigned wideWidth peak ==> peakWide

    let difference = wire "difference" (SInt wideWidth)
    sub peakWide envWide ==> difference
    let alphaSigned = wire "alpha_signed" (SInt 17)
    widenUnsigned 17 alpha ==> alphaSigned

    let step = wire "step" (SInt stepWidth)
    mul difference alphaSigned ==> step
    let stepQ15 = wire "step_q15" (SInt(stepWidth - 15))
    shr 15 step ==> stepQ15

    let envNext = wire "env_next" (SInt envNextWidth)
    add (signExtend envNextWidth envWide) (signExtend envNextWidth stepQ15) ==> envNext

    // Clip into the unsigned envelope register's range.
    let envFloor = lit 0UL envNextWidth
    let envCeiling = lit (gainCap - 1UL) envNextWidth
    let envClipped = wire "env_clipped" envNextWidth

    mux
            (lt envNext envFloor)
            envFloor
            (mux (greaterThan envNext envCeiling) envCeiling envNext)
    ==> envClipped

    let envValue = wire "env_value" sampleWidth
    slice (sampleWidth - 1) 0 envClipped ==> envValue
    If advance (fun () -> envValue ==> env)

    env, envWide

/// Gain computer: `gain = 1 - min((env - threshold) * ratio, 1)` in Q0.24,
/// returned sign-extended and ready for the apply multiply. `ratio` is a slope
/// of gain against excess rather than an N:1 knob, which is what keeps the
/// whole computer one multiply and a clip — no division, no log.
let private gainComputer (envWide: Expr) (threshold: Expr) (ratio: Expr) : Expr =
    let thresholdWide = wire "threshold_wide" (SInt wideWidth)
    widenUnsigned wideWidth threshold ==> thresholdWide
    let overThreshold = wire "over_threshold" (SInt wideWidth)
    sub envWide thresholdWide ==> overThreshold

    let excess = wire "excess" sampleWidth

    mux
            (lt overThreshold (lit 0UL wideWidth))
            (lit 0UL sampleWidth)
            (slice (sampleWidth - 1) 0 overThreshold)
    ==> excess

    let reductionRaw = wire "reduction_raw" gainRedWidth
    mul excess ratio ==> reductionRaw
    let reduction = wire "reduction" gainWidth

    mux
            (greaterThan reductionRaw (lit gainCap gainRedWidth))
            (lit gainCap gainWidth)
            (slice (gainWidth - 1) 0 reductionRaw)
    ==> reduction

    let gain = wire "gain" gainWidth
    sub (lit gainCap gainWidth) reduction ==> gain
    let gainSigned = wire "gain_signed" (SInt(gainWidth + 1))
    widenUnsigned (gainWidth + 1) gain ==> gainSigned
    gainSigned

/// Pipeline latency of [audioCompressor]'s gain-apply datapath. The envelope
/// feedback is a recurrence and stays combinational; only the two output
/// multiplies are registered. A parallel or bypass path must delay by this
/// much to stay aligned.
let compressorLatency = 2

/// Single-band stereo-coupled dynamic-range compressor.
///
/// Envelope detector, one envelope shared by both channels:
///
///     peak = max(|left|, |right|)
///     alpha = (peak > env) ? attack : release          (Q1.15)
///     env' = env + alpha*(peak - env)
///
/// Coupling the channels into one envelope stops a transient on one side
/// shifting the stereo image, and halves the envelope hardware.
///
/// Gain reduction, linear-domain and division-free:
///
///     excess = max(0, env - threshold)
///     gain = 2^24 - min(excess * ratio, 2^24)          (Q0.24, in [0,1])
///
/// `ratio` is a slope of gain against excess rather than a traditional N:1
/// knob — that is what makes the whole computer one multiply and a clip.
///
/// Sample path per channel: apply gain, then `makeup` (Q8.8, as the master
/// volume is). The three multiplies in series — excess*ratio, sample*gain,
/// gained*makeup — are split one per stage, which is why this stage costs
/// cycles where the gain and limiter stages do not.
let audioCompressor (name: string) =
    let wideWidth = sampleWidth + 1
    let stepWidth = wideWidth + 17
    let envNextWidth = sampleWidth + 4
    let gainRedWidth = sampleWidth + 8
    let gainWidth = sampleWidth + 1
    let gainCap = 1UL <<< sampleWidth
    let boostProductWidth = sampleWidth + 17
    let boostWidth = boostProductWidth - gainFracBits

    defineModule
        name
        (fun p ->
            {| s = stereoPorts p
               threshold = p.inPort "threshold" sampleWidth
               ratio = p.inPort "ratio" 8
               attack = p.inPort "attack" 16
               // `release` is a Verilog reserved word.
               releaseRate = p.inPort "releaseRate" 16
               makeup = p.inPort "makeup" 16 |})
        (fun m io ->
            fun (threshold: Expr) (ratio: Expr) (attack: Expr) (releaseRate: Expr) (makeup: Expr) (s: Stream<Expr * Expr>) ->
                threshold ==> io.threshold
                ratio ==> io.ratio
                attack ==> io.attack
                releaseRate ==> io.releaseRate
                makeup ==> io.makeup
                stereoSplice m io.s s)
        (fun io _ ->
            // Advance when downstream can accept; freeze whole on backpressure.
            let enable = wireBit "enable"
            io.s.outReady ==> enable
            io.s.outReady ==> io.s.inReady

            let advance = wireBit "advance"
            (io.s.inValid &&& enable) ==> advance

            // The apply path is `compressorLatency` deep, so valid rides
            // alongside. The envelope loop is a recurrence and cannot join it.
            let validPipe = List.init compressorLatency (fun i -> regBit $"cvalid{i}")

            If enable (fun () ->
                validPipe
                |> List.iteri (fun i r -> (if i = 0 then io.s.inValid else validPipe[i - 1]) ==> r))

            List.last validPipe ==> io.s.outValid

            let makeupSigned = wire "makeup_signed" (SInt 17)
            widenUnsigned 17 io.makeup ==> makeupSigned

            // Boost FIRST, then detect on the boosted signal. Applying makeup
            // after detection lets a heavily-boosted input slip past a
            // quiet-looking envelope and clip downstream — measured as a buzz
            // on hot input in the multiband build this topology came from.
            // Detecting post-boost means the compressor regulates the level it
            // actually emits, which is also what makes one global threshold
            // meaningful across bands whose makeup gains differ by ~10x.
            let boost channelName x =
                let product = wire $"{channelName}_boost_product" (SInt boostProductWidth)
                mul x makeupSigned ==> product
                let boosted = wire $"{channelName}_boosted" (SInt boostWidth)
                shr gainFracBits product ==> boosted
                boosted

            let boostedLeft = boost "left" io.s.inLeft
            let boostedRight = boost "right" io.s.inRight

            // The detector reads a registered copy, which keeps the makeup
            // multiply out of the envelope recurrence's combinational path.
            let magnitude channelName boosted =
                let held = reg $"{channelName}_boosted_held" (SInt boostWidth)
                If advance (fun () -> boosted ==> held)
                let negated = wire $"{channelName}_negated" (SInt boostWidth)
                sub (lit 0UL boostWidth) held ==> negated
                let absolute = wire $"{channelName}_absolute" boostWidth
                mux (slice (boostWidth - 1) (boostWidth - 1) held) negated held ==> absolute
                // Clamp to full scale: the boosted value is wider than a sample
                // and the envelope is a sample-sized level.
                let level = wire $"{channelName}_level" sampleWidth
                saturate sampleWidth absolute ==> level
                level

            let absLeft = magnitude "left" boostedLeft
            let absRight = magnitude "right" boostedRight

            let peak = wire "peak" sampleWidth
            mux (greaterThan absLeft absRight) absLeft absRight ==> peak

            let _, envWide = envelopeFollower peak io.attack io.releaseRate advance
            let gainSigned = gainComputer envWide io.threshold io.ratio

            // Stage 1 — latch the gain beside the boosted samples it was
            // derived from, so they stay aligned down the apply pipeline.
            let gainHeld = reg "gain_held" (SInt(gainWidth + 1))
            let leftHeld = reg "left_held" (SInt boostWidth)
            let rightHeld = reg "right_held" (SInt boostWidth)

            If enable (fun () ->
                gainSigned ==> gainHeld
                boostedLeft ==> leftHeld
                boostedRight ==> rightHeld)

            // Stage 2 — one multiply, because makeup is already folded in.
            let applyGain channelName held =
                let productWidth = boostWidth + gainWidth + 1
                let product = wire $"{channelName}_gain_product" (SInt productWidth)
                mul held gainHeld ==> product
                let scaled = wire $"{channelName}_gain_scaled" (SInt(productWidth - sampleWidth))
                shr sampleWidth product ==> scaled
                let saturated = wire $"{channelName}_gain_saturated" (SInt sampleWidth)
                saturate sampleWidth scaled ==> saturated
                let output = reg $"{channelName}_gained" (SInt sampleWidth)
                If enable (fun () -> saturated ==> output)
                output

            applyGain "left" leftHeld ==> io.s.outLeft
            applyGain "right" rightHeld ==> io.s.outRight)

// ---------------------------------------------------------------------------
// Sources and the tone-control filter.

/// Phase-accumulator width. Frequency = Fs * step / 2^tonePhaseWidth.
let tonePhaseWidth = 24

/// Phase increment for ~440 Hz at Fs ~= 48.83 kHz (the i2sMaster defaults on a
/// 100 MHz fabric clock): round(440 / 48828.125 * 2^24).
let toneStep440 = 151199UL

/// The output half of a stereo stage's ports — what a source declares.
type StereoSourcePorts =
    { outLeft: Expr
      outRight: Expr
      outValid: Expr
      outReady: Expr }

let private stereoSourcePorts (p: Ports) : StereoSourcePorts =
    { outLeft = p.outPort "out_left" sampleWidth
      outRight = p.outPort "out_right" sampleWidth
      outValid = p.outPort "out_valid" 1
      outReady = p.inPort "out_ready" 1 }

let private sourceStream (m: Builder) (sp: StereoSourcePorts) : Stream<Expr * Expr> =
    m.RegisterStreamReady sp.outReady

    { payload = (sp.outLeft, sp.outRight)
      valid = sp.outValid
      ready = sp.outReady
      layout = sampleLayout }

/// Triangle-wave tone generator — a numerically-controlled oscillator sourcing
/// a stereo stream with the same value on both channels.
///
/// The phase accumulator advances by `step` on each *accepted* sample
/// (valid and ready), so the pitch tracks the I2S frame rate rather than the
/// fabric clock — the same design at 100 MHz and 166.67 MHz plays the same
/// note. The top phase bit selects the rising or falling half of the triangle;
/// the low bits, complemented on the falling half, form the ramp, which is then
/// re-centred to a signed sample spanning +/-2^22 (about -6 dBFS) — audible
/// with headroom below full scale.
///
/// `enable` low holds the phase and emits nothing, so the DAC sees silence
/// rather than a stuck tone.
let toneGenerator (name: string) =
    defineModule
        name
        (fun p ->
            {| s = stereoSourcePorts p
               enable = p.inPort "enable" 1
               step = p.inPort "step" tonePhaseWidth |})
        (fun m io ->
            fun (enable: Expr) (step: Expr) ->
                enable ==> io.enable
                step ==> io.step
                sourceStream m io.s)
        (fun io _ ->
            let phase = reg "phase" tonePhaseWidth

            let ramp = wire "ramp" (tonePhaseWidth - 1)
            slice (tonePhaseWidth - 2) 0 phase ==> ramp

            let inverted = wire "inverted" (tonePhaseWidth - 1)
            bnot ramp ==> inverted

            // The top phase bit is the half-select: rising uses the ramp,
            // falling its complement.
            let triangle = wire "triangle" (tonePhaseWidth - 1)
            mux (slice (tonePhaseWidth - 1) (tonePhaseWidth - 1) phase) inverted ramp ==> triangle

            let sample = wire "sample" sampleWidth
            sub (widenUnsigned sampleWidth triangle) (lit (1UL <<< (sampleWidth - 2)) sampleWidth) ==> sample

            io.enable ==> io.s.outValid
            sample ==> io.s.outLeft
            sample ==> io.s.outRight

            If (io.enable &&& io.s.outReady) (fun () -> phase + io.step ==> phase))

/// Coefficient encoding: Q1.15 in 16 bits, so 32767 is the representable
/// maximum (just under +1.0).
let firCoeffWidth = 16

let firCoeffFrac = 15

let presetBypass = 0
let presetLowPass = 1
let presetHighPass = 2

let private coeffLimit = 1 <<< firCoeffFrac
let private clampCoeff v = max -coeffLimit (min (coeffLimit - 1) v)

/// Kotlin's `roundToInt` breaks ties toward positive infinity; .NET's
/// `Math.Round` is banker's rounding, which would silently disagree by an LSB
/// on exactly-half coefficients. `floor(x + 0.5)` reproduces Kotlin's rule, so
/// the two stacks design bit-identical banks.
let private roundHalfUp (x: float) = int (floor (x + 0.5))

let private designBypass taps =
    let center = (taps - 1) / 2
    List.init taps (fun n -> if n = center then coeffLimit - 1 else 0)

let private designLowPass taps (cutoff: float) (fs: float) =
    let center = float (taps - 1) / 2.0
    let twoFc = 2.0 * cutoff / fs

    let raw =
        List.init taps (fun n ->
            let k = float n - center

            let ideal =
                if k = 0.0 then twoFc
                else sin (System.Math.PI * twoFc * k) / (System.Math.PI * k)

            // Hamming window: tapers the impulse response's edges, buying
            // stopband attenuation at the cost of a wider transition band.
            let w = 0.54 - 0.46 * cos (2.0 * System.Math.PI * float n / float (taps - 1))
            ideal * w)

    let total = List.sum raw
    raw |> List.map (fun r -> clampCoeff (roundHalfUp (r / total * float coeffLimit)))

let private designHighPass taps cutoff fs =
    // Spectral inversion of the low-pass at the same cutoff: hp = delta - lp,
    // with delta at the centre tap. Same linear-phase group delay as the other
    // two banks, so all three presets agree on output latency — which is what
    // lets one valid pipeline serve whichever is selected.
    let lp = designLowPass taps cutoff fs
    let center = (taps - 1) / 2
    lp |> List.mapi (fun n v -> clampCoeff (if n = center then coeffLimit - 1 - v else -v))

/// Tone-control FIR: a stereo stage with a 2-bit `preset` selecting bypass,
/// low-pass or high-pass. One delay line per channel feeds three parallel MAC
/// banks and the preset muxes the accumulators, so switching presets costs a
/// mux rather than reloading coefficients — and every bank shares the group
/// delay, so a switch does not shift the signal in time.
///
/// The MAC trees are `adderTreePipelined`, not the combinational reduction:
/// products feeding DSPs get re-flattened into a linear DSP cascade unless
/// registers stand in the way. Latency is `ceilLog2 taps` cycles and the valid
/// bit is shifted alongside.
///
/// **Transition-width caveat:** 16 taps gives a transition of roughly
/// 3.3*Fs/N — about 10 kHz at 48 kHz — so these are coarse tone shapers, not
/// surgical filters. The low-pass audibly dulls highs and the high-pass thins
/// lows, which is what a tone control is for; for precision use a biquad
/// cascade, whose slopes are far sharper for the same hardware.
let audioFir (name: string) (taps: int) (sampleRate: float) (lpCutoff: float) (hpCutoff: float) =
    if taps < 4 || taps > 64 then failwith $"audioFir taps must be 4..64, got {taps}"

    if lpCutoff <= 0.0 || lpCutoff >= sampleRate / 2.0 then
        failwith $"audioFir lpCutoff out of range: {lpCutoff}"

    if hpCutoff <= 0.0 || hpCutoff >= sampleRate / 2.0 then
        failwith $"audioFir hpCutoff out of range: {hpCutoff}"

    let productWidth = sampleWidth + firCoeffWidth
    let accWidth = productWidth + ceilLog2 taps
    let scaledWidth = accWidth - firCoeffFrac
    let macLatency = ceilLog2 taps

    let banks =
        [ "bypass", designBypass taps
          "lowpass", designLowPass taps lpCutoff sampleRate
          "highpass", designHighPass taps hpCutoff sampleRate ]

    defineModule
        name
        (fun p ->
            {| s = stereoPorts p
               preset = p.inPort "preset" 2 |})
        (fun m io ->
            fun (preset: Expr) (s: Stream<Expr * Expr>) ->
                preset ==> io.preset
                stereoSplice m io.s s)
        (fun io _ ->
            let enable = wireBit "enable"
            io.s.outReady ==> enable
            io.s.outReady ==> io.s.inReady

            let advance = wireBit "advance"
            (io.s.inValid &&& enable) ==> advance

            let validPipe = List.init macLatency (fun i -> regBit $"v{i}")

            If enable (fun () ->
                validPipe
                |> List.iteri (fun i r -> (if i = 0 then io.s.inValid else validPipe[i - 1]) ==> r))

            List.last validPipe ==> io.s.outValid

            // The coefficient banks are named wires shared by both channels:
            // a signed multiply needs a declared signal on each side, and naming
            // a bank once means it is emitted once rather than per channel.
            let coeffBanks =
                banks
                |> List.map (fun (label, values) ->
                    label,
                    values
                    |> List.mapi (fun i c ->
                        let w = wire $"coeff_{label}_{i}" (SInt firCoeffWidth)
                        lit (uint64 c &&& 0xFFFFUL) firCoeffWidth ==> w
                        w))

            let channel channelName current =
                let line =
                    List.scan
                        (fun previous i ->
                            let d = reg $"{channelName}_d{i}" (SInt sampleWidth)
                            If advance (fun () -> previous ==> d)
                            d)
                        current
                        [ 1 .. taps - 1 ]

                let bank (label: string, coeffs: Expr list) =
                    List.zip coeffs line
                    |> List.mapi (fun i (c, tap) ->
                        let p = wire $"{channelName}_{label}_p{i}" (SInt productWidth)
                        mul c tap ==> p
                        signExtend accWidth p)
                    |> adderTreePipelined $"{channelName}_{label}" accWidth enable
                    |> fst

                let accumulators = List.map bank coeffBanks

                let selected = wire $"{channelName}_selected" (SInt accWidth)

                mux
                        (eq io.preset (lit (uint64 presetLowPass) 2))
                        accumulators[1]
                        (mux (eq io.preset (lit (uint64 presetHighPass) 2)) accumulators[2] accumulators[0])
                ==> selected

                let scaled = wire $"{channelName}_scaled" (SInt scaledWidth)
                shr firCoeffFrac selected ==> scaled
                saturate sampleWidth scaled

            channel "left" io.s.inLeft ==> io.s.outLeft
            channel "right" io.s.inRight ==> io.s.outRight)

/// The stock tone control: 16 taps, 4 kHz low-pass and 300 Hz high-pass at
/// 48 kHz.
let audioToneFilter name = audioFir name 16 48_000.0 4_000.0 300.0

// ---------------------------------------------------------------------------
// I2S. Bit-serial clocking rather than datapath: the codec's frame is a
// counter hierarchy, and rx/tx are shift registers hung off its edge ticks.

/// The input half of a stereo stage's ports — what a sink declares.
type StereoSinkPorts =
    { inLeft: Expr
      inRight: Expr
      inValid: Expr
      inReady: Expr }

let private stereoSinkPorts (p: Ports) : StereoSinkPorts =
    { inLeft = p.inPort "in_left" sampleWidth
      inRight = p.inPort "in_right" sampleWidth
      inValid = p.inPort "in_valid" 1
      inReady = p.outPort "in_ready" 1 }

let private stereoSink (m: Builder) (sp: StereoSinkPorts) (s: Stream<Expr * Expr>) =
    let left, right = s.payload
    left ==> sp.inLeft
    right ==> sp.inRight
    s.valid ==> sp.inValid
    sp.inReady ==> s.ready

/// I2S clock generator: one fabric clock in, the codec's MCLK / SCLK / LRCLK
/// out, plus the two internal edge ticks `i2sRx` and `i2sTx` hang off.
///
/// Three nested dividers. MCLK toggles every `mclkHalfDiv` fabric cycles (the
/// CS5343/CS4344 want ~256*Fs); SCLK every `sclkHalfDiv`; LRCLK every
/// `bitsPerSlot` SCLK falling edges. MCLK is divided independently of SCLK, so
/// the jitter between them is bounded by fabric-cycle quantisation — which the
/// codec tolerates.
///
/// The two ticks are one fabric cycle each and land on opposite SCLK edges:
/// `sclkRxTick` on the rising edge, where the ADC's data is stable, and
/// `sclkTxTick` on the falling edge, where the DAC latches and LRCLK turns.
/// Splitting them is what lets receive and transmit share one frame without
/// either sampling the other's transition.
///
/// Sample rate is `fabric / (4 * sclkHalfDiv * bitsPerSlot)`: at 100 MHz with
/// the stock 16 and 32 that is 48.828 kHz, inside codec tolerance. An exact
/// 48 kHz wants a 12.288 MHz MMCM clock driving this module instead.
let i2sMaster (name: string) (mclkHalfDiv: int) (sclkHalfDiv: int) (bitsPerSlot: int) =
    if mclkHalfDiv < 1 then failwith $"i2sMaster mclkHalfDiv must be >= 1, got {mclkHalfDiv}"
    if sclkHalfDiv < 1 then failwith $"i2sMaster sclkHalfDiv must be >= 1, got {sclkHalfDiv}"
    if bitsPerSlot < 1 then failwith $"i2sMaster bitsPerSlot must be >= 1, got {bitsPerSlot}"

    defineModule
        name
        (fun p ->
            {| mclk = p.outPort "mclk" 1
               sclk = p.outPort "sclk" 1
               lrclk = p.outPort "lrclk" 1
               sclkRxTick = p.outPort "sclkRxTick" 1
               sclkTxTick = p.outPort "sclkTxTick" 1 |})
        (fun m io -> fun () -> io)
        (fun io _ ->
            let mclkReg = regBit "mclk_reg"
            let sclkReg = regBit "sclk_reg"
            let lrclkReg = regBit "lrclk_reg"
            let rxTick = regBit "rx_tick"
            let txTick = regBit "tx_tick"

            mclkReg ==> io.mclk
            sclkReg ==> io.sclk
            lrclkReg ==> io.lrclk
            rxTick ==> io.sclkRxTick
            txTick ==> io.sclkTxTick

            // Three dividers, each flipping its clock on the period it counts.
            // `wrap` is the whole content of a divider, so the nesting the
            // hand-rolled version needed — one level per counter, to place the
            // reset opposite the increment — is gone.
            let mclkPeriod = counter "mclk_count" mclkHalfDiv (lit 1UL 1)
            If mclkPeriod.wrap (fun () -> bnot mclkReg ==> mclkReg)

            let sclkPeriod = counter "sclk_count" sclkHalfDiv (lit 1UL 1)
            If sclkPeriod.wrap (fun () -> bnot sclkReg ==> sclkReg)

            // The two edges of SCLK, each owning one tick. Low-about-to-go-high
            // is where the ADC's data is stable; high-about-to-go-low is where
            // the DAC latches and the frame advances.
            (sclkPeriod.wrap &&& bnot sclkReg) ==> rxTick
            (sclkPeriod.wrap &&& sclkReg) ==> txTick

            // One bit per falling edge; LRCLK turns when the slot is full.
            let slot = counter "bit_count" bitsPerSlot (sclkPeriod.wrap &&& sclkReg)
            If slot.wrap (fun () -> bnot lrclkReg ==> lrclkReg))

/// The stock clock generator: Fs ~= 48.8 kHz from a 100 MHz fabric clock.
let i2sMasterDefault name = i2sMaster name 4 16 32

/// I2S receiver: the ADC's serial line into a stereo stream.
///
/// Built for FPGA-master operation — `i2sMaster` owns the clocking and hands
/// this module `sclkTick` (one fabric cycle on each sampling edge) and the
/// current `lrclk` level. Each LRCLK half-period is one transition tick, during
/// which `sdout` carries nothing, then 24 data bits MSB-first, then arbitrary
/// zero padding. The receiver counts data ticks from the LRCLK edge and latches
/// on the 24th.
///
/// `valid` pulses for one fabric cycle when a left/right pair completes.
/// Downstream is assumed always-ready: at 48 kHz against a fabric clock three
/// orders of magnitude faster, a consumer has ~1000 cycles to take each sample.
let i2sRx (name: string) =
    defineModule
        name
        (fun p ->
            {| s = stereoSourcePorts p
               sclkTick = p.inPort "sclkTick" 1
               lrclk = p.inPort "lrclk" 1
               sdout = p.inPort "sdout" 1 |})
        (fun m io ->
            fun (sclkTick: Expr) (lrclk: Expr) (sdout: Expr) ->
                sclkTick ==> io.sclkTick
                lrclk ==> io.lrclk
                sdout ==> io.sdout
                sourceStream m io.s)
        (fun io _ ->
            let shift = reg "shift" sampleWidth
            let bitCount = reg "bit_count" 6

            let leftHold = reg "left_hold" sampleWidth
            let validReg = regBit "valid_reg"
            let leftReg = reg "left_reg" sampleWidth
            let rightReg = reg "right_reg" sampleWidth

            validReg ==> io.s.outValid
            leftReg ==> io.s.outLeft
            rightReg ==> io.s.outRight

            lit 0UL 1 ==> validReg

            let shifted = wire "shifted" sampleWidth
            cat (slice (sampleWidth - 2) 0 shift) io.sdout ==> shifted

            let lrclk = edgeDetect "lrclk" io.sclkTick io.lrclk
            let lrclkEdge = wireBit "lrclk_edge"
            lrclk.changed ==> lrclkEdge

            If io.sclkTick (fun () ->
                If lrclkEdge (fun () ->
                    // The tick on which LRCLK turns carries no data — the I2S
                    // one-cycle delay.
                    lit 0UL 6 ==> bitCount)

                Else (fun () ->
                    bitCount + lit 1UL 6 ==> bitCount

                    If (lt bitCount (lit (uint64 sampleWidth) 6)) (fun () ->
                        shifted ==> shift

                        If (eq bitCount (lit (uint64 (sampleWidth - 1)) 6)) (fun () ->
                            If (eq io.lrclk (lit 0UL 1)) (fun () -> shifted ==> leftHold)

                            Else (fun () ->
                                leftHold ==> leftReg
                                shifted ==> rightReg
                                lit 1UL 1 ==> validReg))))))

/// I2S transmitter: a stereo stream out to the DAC's serial line. The mirror
/// of `i2sRx`, and it shares the frame convention exactly — one transition
/// tick, then 24 data bits MSB-first.
///
/// A one-slot pending buffer decouples the stream handshake from the frame:
/// `ready` asserts whenever that slot is empty, and the slot commits to the
/// shift registers on entry to a new left slot. That is what lets a producer
/// hand over a sample at any point in the frame without tearing one in half.
/// After a slot's 24 ticks the shift register has zero-filled, so the padding
/// ticks emit zeros without a case for them.
let i2sTx (name: string) =
    defineModule
        name
        (fun p ->
            {| s = stereoSinkPorts p
               sclkTick = p.inPort "sclkTick" 1
               lrclk = p.inPort "lrclk" 1
               sdin = p.outPort "sdin" 1 |})
        (fun m io ->
            fun (sclkTick: Expr) (lrclk: Expr) (s: Stream<Expr * Expr>) ->
                sclkTick ==> io.sclkTick
                lrclk ==> io.lrclk
                stereoSink m io.s s
                io.sdin)
        (fun io _ ->
            let leftShift = reg "left_shift" sampleWidth
            let rightShift = reg "right_shift" sampleWidth
            let pendingLeft = reg "pending_left" sampleWidth
            let pendingRight = reg "pending_right" sampleWidth
            let pendingValid = regBit "pending_valid"

            let bitCount = reg "bit_count" 6

            // Whichever channel's slot is live drives the line from its MSB.
            mux
                    io.lrclk
                    (slice (sampleWidth - 1) (sampleWidth - 1) rightShift)
                    (slice (sampleWidth - 1) (sampleWidth - 1) leftShift)
            ==> io.sdin

            bnot pendingValid ==> io.s.inReady

            If (io.s.inValid &&& bnot pendingValid) (fun () ->
                io.s.inLeft ==> pendingLeft
                io.s.inRight ==> pendingRight
                lit 1UL 1 ==> pendingValid)

            let lrclk = edgeDetect "lrclk" io.sclkTick io.lrclk
            let lrclkEdge = wireBit "lrclk_edge"
            lrclk.changed ==> lrclkEdge

            If io.sclkTick (fun () ->
                If lrclkEdge (fun () ->
                    lit 0UL 6 ==> bitCount

                    If (eq io.lrclk (lit 0UL 1) &&& pendingValid) (fun () ->
                        pendingLeft ==> leftShift
                        pendingRight ==> rightShift
                        lit 0UL 1 ==> pendingValid))

                Else (fun () ->
                    bitCount + lit 1UL 6 ==> bitCount

                    If (lt bitCount (lit (uint64 sampleWidth) 6)) (fun () ->
                        If (eq io.lrclk (lit 0UL 1)) (fun () ->
                            cat (slice (sampleWidth - 2) 0 leftShift) (lit 0UL 1) ==> leftShift)

                        Else (fun () ->
                            cat (slice (sampleWidth - 2) 0 rightShift) (lit 0UL 1) ==> rightShift)))))

// ---------------------------------------------------------------------------
// Multiband compression. Generic DSP: an 8-band crossover feeding a compressor
// per band. Mastering, broadcast loudness and hearing-aid fitting all want the
// same machine — what differs is only where the per-band makeup gains come
// from, and those arrive as register values.

let multibandBands = 8

/// Default crossover cutoffs (Hz): geometric means of log-spaced band centres
/// from 250 Hz to 8 kHz. Seven cutoffs make eight bands.
let defaultCrossovers = [ 320.0; 525.0; 860.0; 1410.0; 2320.0; 3810.0; 6250.0 ]

/// The filter shapes the RBJ cookbook covers, which is every shape the biquad
/// stages here expose.
type EqType =
    | Peaking
    | LowShelf
    | HighShelf
    | LowPass
    | HighPass

/// Five biquad coefficients as real numbers, `a0` already normalised to 1 and
/// the feedback signs matching the hardware's subtractive convention — so a
/// design plugs straight in with no re-derivation at the boundary.
type BiquadDesign =
    { b0: float
      b1: float
      b2: float
      a1: float
      a2: float }

/// The coefficient set that passes audio through unchanged.
let identityDesign = { b0 = 1.0; b1 = 0.0; b2 = 0.0; a1 = 0.0; a2 = 0.0 }

/// Robert Bristow-Johnson's cookbook formulae. `gainDb` applies to the
/// shelving and peaking shapes and is ignored by the pass filters, which is
/// the cookbook's own convention rather than an omission here.
let rbjDesign (shape: EqType) (fc: float) (q: float) (gainDb: float) (fs: float) : BiquadDesign =
    let w0 = 2.0 * System.Math.PI * fc / fs
    let cosW0 = cos w0
    let sinW0 = sin w0
    let alpha = sinW0 / (2.0 * q)
    let a = 10.0 ** (gainDb / 40.0) // sqrt of the linear gain
    let twoSqrtAAlpha = 2.0 * sqrt a * alpha

    let normalise (b0, b1, b2, a0, a1, a2) =
        { b0 = b0 / a0
          b1 = b1 / a0
          b2 = b2 / a0
          a1 = a1 / a0
          a2 = a2 / a0 }

    match shape with
    | Peaking ->
        normalise (
            1.0 + alpha * a,
            -2.0 * cosW0,
            1.0 - alpha * a,
            1.0 + alpha / a,
            -2.0 * cosW0,
            1.0 - alpha / a
        )
    | LowShelf ->
        normalise (
            a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha),
            2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0),
            a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha),
            (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha,
            -2.0 * ((a - 1.0) + (a + 1.0) * cosW0),
            (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha
        )
    | HighShelf ->
        normalise (
            a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha),
            -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0),
            a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha),
            (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha,
            2.0 * ((a - 1.0) - (a + 1.0) * cosW0),
            (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha
        )
    | LowPass ->
        normalise ((1.0 - cosW0) / 2.0, 1.0 - cosW0, (1.0 - cosW0) / 2.0, 1.0 + alpha, -2.0 * cosW0, 1.0 - alpha)
    | HighPass ->
        normalise ((1.0 + cosW0) / 2.0, -(1.0 + cosW0), (1.0 + cosW0) / 2.0, 1.0 + alpha, -2.0 * cosW0, 1.0 - alpha)

/// Quantise a real to a two's-complement bit pattern with `fracBits`
/// fractional bits. Truncation toward zero rather than rounding, matching the
/// Kotlin original so both stacks quantise a coefficient to the same bits.
let quantiseQ (fracBits: int) (totalBits: int) (value: float) : uint64 =
    let scaled = int64 (value * float (1L <<< fracBits))
    let maxValue = (1L <<< (totalBits - 1)) - 1L
    let minValue = -(1L <<< (totalBits - 1))
    uint64 (max minValue (min maxValue scaled)) &&& ((1UL <<< totalBits) - 1UL)

/// A design in the Q2.30 form the `biquad` module's coefficient ports take.
let toQ230 (d: BiquadDesign) : uint64 list =
    [ d.b0; d.b1; d.b2; d.a1; d.a2 ]
    |> List.map (quantiseQ biquadCoeffFrac biquadCoeffWidth)

/// The crossover's low-pass, which is just the cookbook at unity gain. Kept as
/// its own name because that is what the filterbank asks for.
let lowPassCoeffsQ230 (fc: float) (q: float) (fs: float) : uint64 list =
    toQ230 (rbjDesign LowPass fc q 0.0 fs)

/// An envelope time constant in seconds as the Q1.15 coefficient the
/// compressors take: `alpha = 1 - exp(-1/(tau*Fs))`. Zero or negative means
/// instantaneous, which is the largest representable alpha rather than an
/// error — a zero attack time is a legitimate request.
let envelopeAlphaQ15 (seconds: float) (fs: float) : uint64 =
    if seconds <= 0.0 then
        0x7FFFUL
    else
        let alpha = 1.0 - exp (-1.0 / (seconds * fs))
        uint64 (max 1L (min 0x7FFFL (int64 (alpha * float (1 <<< 15)))))

/// The inverse, for host display and for checking the forward direction.
let envelopeAlphaSeconds (alpha: uint64) (fs: float) : float =
    let a = float alpha / float (1 <<< 15)
    if a >= 1.0 then 0.0 else -1.0 / (log (1.0 - a) * fs)

/// Width of one band signal: a sample plus a bit, because a band is a
/// difference of two low-pass outputs.
let bandWidth = sampleWidth + 1

/// Width of a band after its makeup boost, kept wide so the band sum can be
/// saturated once at the end rather than per band.
let gainedWidth = bandWidth + 9

/// Mono single-band compressor — the per-band unit. The same envelope detector
/// and gain computer as `audioCompressor`, in the same Q formats and with the
/// same boost-first topology, but mono and emitting a wide unsaturated value
/// for the caller to sum.
///
/// With `ratio = 0` and unity makeup it is an exact pass-through, which is what
/// lets the filterbank still reconstruct its input through eight of these.
/// `envelope` is exposed for host diagnostics.
let monoBandCompressor (name: string) =
    let boostProductWidth = bandWidth + 17
    let wideWidth = sampleWidth + 1
    let stepWidth = wideWidth + 17
    let envNextWidth = sampleWidth + 4
    let gainRedWidth = sampleWidth + 8
    let gainWidth = sampleWidth + 1
    let gainCap = 1UL <<< sampleWidth
    let applyProductWidth = gainedWidth + gainWidth + 1

    defineModule
        name
        (fun p ->
            {| band = p.inPortAs "band" (SInt bandWidth)
               advance = p.inPort "advance" 1
               enable = p.inPort "enable" 1
               threshold = p.inPort "threshold" sampleWidth
               ratio = p.inPort "ratio" 8
               attack = p.inPort "attack" 16
               releaseRate = p.inPort "releaseRate" 16
               makeup = p.inPort "makeup" 16
               gained = p.outPortAs "gained" (SInt gainedWidth)
               envelope = p.outPort "envelope" sampleWidth |})
        (fun m io ->
            fun (band: Expr) (advance: Expr) (enable: Expr) (threshold: Expr) (ratio: Expr) (attack: Expr) (releaseRate: Expr) (makeup: Expr) ->
                band ==> io.band
                advance ==> io.advance
                enable ==> io.enable
                threshold ==> io.threshold
                ratio ==> io.ratio
                attack ==> io.attack
                releaseRate ==> io.releaseRate
                makeup ==> io.makeup
                io.gained, io.envelope)
        (fun io _ ->
            let makeupSigned = wire "makeup_signed" (SInt 17)
            widenUnsigned 17 io.makeup ==> makeupSigned

            let boostProduct = wire "boost_product" (SInt boostProductWidth)
            mul io.band makeupSigned ==> boostProduct
            let boosted = wire "boosted" (SInt gainedWidth)
            shr gainFracBits boostProduct ==> boosted

            // Registered ahead of the envelope loop: the makeup multiply stays
            // out of the recurrence's combinational path.
            let detected = reg "detected" (SInt gainedWidth)
            If io.advance (fun () -> boosted ==> detected)

            let negated = wire "negated" (SInt gainedWidth)
            sub (lit 0UL gainedWidth) detected ==> negated
            let absolute = wire "absolute" gainedWidth
            mux (slice (gainedWidth - 1) (gainedWidth - 1) detected) negated detected ==> absolute
            let peak = wire "peak" sampleWidth
            saturate sampleWidth absolute ==> peak

            // --- envelope follower + gain computer, shared with audioCompressor ---
            let env, envWide = envelopeFollower peak io.attack io.releaseRate io.advance
            env ==> io.envelope
            let gainSigned = gainComputer envWide io.threshold io.ratio

            // --- apply, pipelined ---
            let boostedHeld = reg "boosted_held" (SInt gainedWidth)
            let gainHeld = reg "gain_held" (SInt(gainWidth + 1))

            If io.enable (fun () ->
                boosted ==> boostedHeld
                gainSigned ==> gainHeld)

            let applyProduct = wire "apply_product" (SInt applyProductWidth)
            mul boostedHeld gainHeld ==> applyProduct
            let applyScaled = wire "apply_scaled" (SInt(applyProductWidth - sampleWidth))
            shr sampleWidth applyProduct ==> applyScaled
            let applySaturated = wire "apply_saturated" (SInt gainedWidth)
            saturate gainedWidth applyScaled ==> applySaturated
            let gained = reg "gained_reg" (SInt gainedWidth)
            If io.enable (fun () -> applySaturated ==> gained)
            gained ==> io.gained)

/// 8-band stereo multiband compressor.
///
/// A subtractive crossover splits each ear into eight bands with seven fixed
/// low-pass biquads — band 0 is the lowest low-pass, band k the difference
/// between successive low-passes, band 7 what the last low-pass leaves. The
/// split is exact by construction: the bands sum back to the input, so unity
/// makeup and zero ratio reconstruct the signal rather than approximating it.
/// Each band then gets its own compressor, and the eight are summed and
/// saturated once.
///
/// Per-band makeup gains are the whole point of the shape: they are Q8.8
/// register values, so what the module is *for* — mastering, broadcast
/// loudness, a hearing-aid prescription — lives in whatever host writes them,
/// not in the fabric.
///
/// `threshold` / `ratio` / `attack` / `releaseRate` are shared across bands; a
/// global threshold is meaningful here only because each band compresses its
/// own post-makeup level (see `monoBandCompressor`). `envelope` reports the
/// loudest band detector across both ears, for host metering.
let multibandCompressor8 (name: string) (crossovers: float list) (q: float) (sampleRate: float) =
    if List.length crossovers <> multibandBands - 1 then
        failwith $"multibandCompressor8 needs {multibandBands - 1} crossovers, got {List.length crossovers}"

    let lowPassCount = List.length crossovers
    let coefficients = crossovers |> List.map (fun fc -> lowPassCoeffsQ230 fc q sampleRate)
    let section = biquadSection $"{name}_biquad"
    let compressor = monoBandCompressor $"{name}_band"
    let sumWidth = gainedWidth + 3

    defineModule
        name
        (fun p ->
            {| s = stereoPorts p
               threshold = p.inPort "threshold" sampleWidth
               ratio = p.inPort "ratio" 8
               attack = p.inPort "attack" 16
               releaseRate = p.inPort "releaseRate" 16
               leftGains = List.init multibandBands (fun i -> p.inPort $"leftGain{i}" 16)
               rightGains = List.init multibandBands (fun i -> p.inPort $"rightGain{i}" 16)
               envelope = p.outPort "envelope" sampleWidth |})
        (fun m io ->
            fun (threshold: Expr) (ratio: Expr) (attack: Expr) (releaseRate: Expr) (leftGains: Expr list) (rightGains: Expr list) (s: Stream<Expr * Expr>) ->
                threshold ==> io.threshold
                ratio ==> io.ratio
                attack ==> io.attack
                releaseRate ==> io.releaseRate
                List.iter2 (fun port g -> g ==> port) io.leftGains leftGains
                List.iter2 (fun port g -> g ==> port) io.rightGains rightGains
                stereoSplice m io.s s, io.envelope)
        (fun io _ ->
            let enable = wireBit "enable"
            io.s.outReady ==> enable
            io.s.outReady ==> io.s.inReady

            // An input register, because the path from an upstream buffer read
            // into the filterbank is long enough to be the critical one.
            let inLeft = reg "in_left_reg" (SInt sampleWidth)
            let inRight = reg "in_right_reg" (SInt sampleWidth)
            let inValid = regBit "in_valid_reg"

            If enable (fun () ->
                io.s.inLeft ==> inLeft
                io.s.inRight ==> inRight
                io.s.inValid ==> inValid)

            // ONE valid chain, starting at the input register, and every gate in
            // this stage derived from it. `inValid` is the valid of the beat at
            // position 0; each later position is that bit having moved once.
            //
            // Both gates below used to be derived independently of the chain
            // that carries `valid` to the output, which is how the two came
            // apart: there were two notions of "where is this beat" and nothing
            // held them together. Now there is one.

            // Position 0 — the filterbank may touch its history only when the
            // beat in the input register is real.
            let advance = wireBit "advance"
            (inValid &&& enable) ==> advance

            // Position 1 — the compressors sit one place further down, because
            // the bands come off registered low-pass outputs. The same bit,
            // moved once: the first link of the chain rather than a separately
            // derived signal that happens to resemble it.
            let validAt1 = regBit "valid_1"
            If enable (fun () -> inValid ==> validAt1)

            let advanceDelayed = wireBit "advance_delayed"
            (validAt1 &&& enable) ==> advanceDelayed

            let envelopes = ResizeArray<Expr>()

            let ear earName x (gains: Expr list) =
                let lowPass =
                    coefficients
                    |> List.mapi (fun k cs ->
                        // Annotated because BiquadDesign shares these field
                        // names — one carries reals for the host, the other
                        // nets for the fabric.
                        let coeffs: BiquadCoeffs =
                            { b0 = lit cs[0] biquadCoeffWidth
                              b1 = lit cs[1] biquadCoeffWidth
                              b2 = lit cs[2] biquadCoeffWidth
                              a1 = lit cs[3] biquadCoeffWidth
                              a2 = lit cs[4] biquadCoeffWidth }

                        let y = instanceNamed $"{earName}_lp{k}" section x advance coeffs
                        let held = reg $"{earName}_lp_held{k}" sampleWidth
                        If enable (fun () -> y ==> held)
                        held)

                let xHeld = reg $"{earName}_x_held" sampleWidth
                If enable (fun () -> x ==> xHeld)

                // The subtractive split: successive low-pass differences, with
                // the input's own residue as the top band.
                let band k =
                    let b = wire $"{earName}_band{k}" bandWidth

                    (if k = 0 then signExtend bandWidth lowPass[0]
                         elif k = lowPassCount then
                             sub (signExtend bandWidth xHeld) (signExtend bandWidth lowPass[lowPassCount - 1])
                         else
                             sub (signExtend bandWidth lowPass[k]) (signExtend bandWidth lowPass[k - 1]))
                    ==> b

                    b

                let gained =
                    List.init multibandBands (fun k ->
                        let value, envelope =
                            instanceNamed
                                $"{earName}_band{k}_comp"
                                compressor
                                (band k)
                                advanceDelayed
                                enable
                                io.threshold
                                io.ratio
                                io.attack
                                io.releaseRate
                                gains[k]

                        let held = wire $"{earName}_env{k}" sampleWidth
                        envelope ==> held
                        envelopes.Add held
                        let wide = wire $"{earName}_gained{k}" gainedWidth
                        value ==> wide
                        signExtend sumWidth wide)

                let total, treeLatency = adderTreePipelined $"{earName}_sum" sumWidth enable gained
                let sum = wire $"{earName}_sum_value" (SInt sumWidth)
                total ==> sum
                let saturated = wire $"{earName}_out" (SInt sampleWidth)
                saturate sampleWidth sum ==> saturated
                saturated, 1 + compressorLatency + treeLatency

            let outLeft, earLatency = ear "left" inLeft io.leftGains
            let outRight, _ = ear "right" inRight io.rightGains
            let latency = 1 + earLatency

            // Loudest band detector across both ears — a registered max tree,
            // because a flat reduce over sixteen wide values is a deep enough
            // cone to matter and this is only a meter.
            fst (
                reduceTreePipelined "env_max" sampleWidth enable (fun a b -> mux (greaterThan a b) a b) (List.ofSeq envelopes)
            )
            ==> io.envelope

            // `valid` rides a delay line matched to the DSP path, so it arrives
            // with the result it describes rather than ahead of it.
            //
            // There is no bypass copy alongside it any more. A raw passthrough
            // was a second data path, `latency` deep and two samples wide per
            // ear, kept so that flipping bypass would not move the signal in
            // time — and it was redundant: this chain is *measurably*
            // bit-exact with threshold at full scale, ratio zero and unity
            // gains, which are the register map's own defaults. Configuration
            // already gives a bit-perfect passthrough, so the mux bought
            // nothing and cost a path that had to be kept in step with this
            // one. See `audio: unity settings pass audio through`.
            // Positions 2..earLatency, continuing from `validAt1`. The output's
            // valid is the last link — the very same bit that gated the
            // filterbank, arriving with the result it describes.
            let validRest = List.init (earLatency - 1) (fun i -> regBit $"v{i}")

            // KNOWN DEFECT, diagnosed 2026-08-19 — see notes/SMALL_FINDINGS.md.
            // These shift once per *ready cycle*, while the DSP above advances
            // once per *accepted beat*. Those are the same event only while the
            // producer never stalls, which is the only case the suite used to
            // exercise. When they diverge the DSP result arrives under somebody
            // else's `valid` and the output is a shifted signal.
            //
            // Shifting these on `advance` instead is NOT the fix: `advance`
            // already implies a beat, so `validPipe[0]` would be a constant 1,
            // `outValid` would stick high once the pipe filled, and the tail
            // would never drain. Draining on `enable` is what flushes the last
            // `latency` samples, and that is why it was written this way. The
            // real fix is a positional pipeline the DSP shares, which is a
            // restructure rather than a gate change.
            If enable (fun () ->
                validRest
                |> List.iteri (fun i r -> (if i = 0 then validAt1 else validRest[i - 1]) ==> r))

            (if List.isEmpty validRest then validAt1 else List.last validRest) ==> io.s.outValid
            outLeft ==> io.s.outLeft
            outRight ==> io.s.outRight)

/// The stock 8-band compressor: the default crossovers, Butterworth Q, 48 kHz.
let multibandCompressor name =
    multibandCompressor8 name defaultCrossovers 0.707 48_000.0
