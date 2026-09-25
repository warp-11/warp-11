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
/// the high bits, matching the original encoding byte for byte so a host that
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

/// Fraction bits in a biquad coefficient.
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

/// The direct-form-I biquad's ports. Coefficients arrive as signals rather
/// than as elaboration-time constants, so one instance serves any response the
/// host cares to write.
type BiquadPorts =
    { /// The input sample.
      x: Input
      /// High for one cycle per sample. The section advances on it, so the
      /// filter's rate is the caller's to set.
      advance: Input
      /// Feed-forward coefficient on the current sample.
      b0: Input
      /// Feed-forward coefficient on the previous sample.
      b1: Input
      /// Feed-forward coefficient on the sample before that.
      b2: Input
      /// Feedback coefficient on the previous output. Subtracted — the sign
      /// convention is the hardware's, and `BiquadDesign` already matches it.
      a1: Input
      /// Feedback coefficient on the output before that, same convention.
      a2: Input
      /// The filtered sample.
      y: Output }

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
let biquadDef (name: string) (sampleWidth: int) (coeffWidth: int) (coeffFrac: int) : TypedModule<BiquadPorts> =
    if sampleWidth < 4 || sampleWidth > 32 then
        failwith $"biquad sampleWidth must be 4..32, got {sampleWidth}"

    if coeffWidth < 16 || coeffWidth > 32 then
        failwith $"biquad coeffWidth must be 16..32, got {coeffWidth}"

    if coeffFrac < 1 || coeffFrac >= coeffWidth then
        failwith $"biquad coeffFrac must be 1..{coeffWidth - 1}, got {coeffFrac}"

    let productWidth = sampleWidth + coeffWidth
    let accWidth = productWidth + 3
    let scaledWidth = accWidth - coeffFrac

    defModule
        name
        (fun p ->
            { x = p.inPortAs "x" (SInt sampleWidth)
              advance = p.inPort "advance" 1
              b0 = p.inPortAs "b0" (SInt coeffWidth)
              b1 = p.inPortAs "b1" (SInt coeffWidth)
              b2 = p.inPortAs "b2" (SInt coeffWidth)
              a1 = p.inPortAs "a1" (SInt coeffWidth)
              a2 = p.inPortAs "a2" (SInt coeffWidth)
              y = p.outPortAs "y" (SInt sampleWidth) })
        (fun io ->
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

/// One section under `instName`, called as a function: wire the sample, the
/// advance pulse and the coefficients, read the filtered sample back.
let biquad (name: string) (sampleWidth: int) (coeffWidth: int) (coeffFrac: int) instName =
    let io = (biquadDef name sampleWidth coeffWidth coeffFrac).NewNamed instName

    fun (x: Expr) (advance: Expr) (c: BiquadCoeffs) ->
        x ==> io.x
        advance ==> io.advance
        c.b0 ==> io.b0
        c.b1 ==> io.b1
        c.b2 ==> io.b2
        c.a1 ==> io.a1
        c.a2 ==> io.a2
        io.y

/// The default section's definition: a 24-bit sample and Q2.30 coefficients.
let biquadSectionDef name = biquadDef name sampleWidth biquadCoeffWidth biquadCoeffFrac

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
    { inLeft: Input
      inRight: Input
      inValid: Input
      inReady: Output
      outLeft: Output
      outRight: Output
      outValid: Output
      outReady: Input }

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
let private stereoSplice (sp: StereoPorts) (s: Stream<Expr * Expr>) : Stream<Expr * Expr> =
    let left, right = s.payload
    left ==> sp.inLeft
    right ==> sp.inRight
    s.valid ==> sp.inValid
    sp.inReady ==> s.ready
    registerStreamReady sp.outReady

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

/// The gain register value that passes a sample through unchanged.
let gainUnity = 1UL <<< gainFracBits

// ---------------------------------------------------------------------------
// The gain table: a compression law as a table of dB gains, indexed by the
// envelope's logarithm.
//
// **Why a table and not the formula.** The four-region curve a prescription is
// written in (expansion, linear, compression, limiting) is affine in dB in
// every region, and evaluating it in fabric costs two or three multiplies a
// band-sample plus a log whose piecewise-linear error lands straight on the
// curve's slope. A table bakes the exact log into its entries, costs one
// multiply-add, and expresses *any* dB curve rather than one family of them.
//
// **Why dB and not linear gain.** Every later term — a noise-reduction
// decision, a volume control, a feedback suppressor — is then an *add* on one
// adder rather than a multiply, the interpolation of a curve that is
// piecewise-linear in dB is exact inside a region, and the safety ceiling is
// one saturate on the sum, in one place, across every source of gain.
// (`~/projects/fsharp/HearingAid/docs/DSP_ROADMAP.md` § The gain table.)

/// Width of a gain in the log domain: Q5.11 signed, which reaches ±96 dB in
/// steps of 0.003 dB. Wide enough that no prescription and no sum of terms over
/// one comes near the ends, and narrow enough that two of them fit a table word.
let gainLogWidth = 16

/// Fraction bits in a `gainLogWidth` gain.
let gainLogFracBits = 11

/// **A gain travels as the base-two logarithm of itself, and every number a
/// person reads is in decibels.** The two differ by a constant, so the choice
/// decides one thing only: whether the exponential that turns a gain back into
/// a multiplier gets its integer part for free. In log2 the integer part *is*
/// the shift and the fraction *is* the table index; in decibels both need a
/// multiply by 1/6.0206 first, once per band and ear.
///
/// So the stored unit is log2 and the spoken unit is decibels: `gainTableWords`
/// takes a curve in dB, the host converts what it writes, and `gainLogOfDb` /
/// `gainDbOfLog` are the only place the constant appears.
let gainLogPerDb = 1.0 / (20.0 * log10 2.0)

/// Decibels as the log2 gain the fabric carries.
let gainLogOfDb (db: float) = db * gainLogPerDb

/// A log2 gain back in decibels, for anything a person reads.
let gainDbOfLog (gain: float) = gain / gainLogPerDb

/// How finely one octave of envelope is divided. Two steps is 3.01 dB an
/// entry, and a prescription is tabulated to within 0.6 dB on that grid —
/// three quarters of it at a knee, where the law's slope changes inside one
/// entry, and the rest the bow below.
///
/// **The interpolation walks the envelope, not the decibel.** The fraction
/// bits it uses are the mantissa's, so the chord is straight in amplitude
/// while the law is straight in dB, and across 3.01 dB the two bow apart by
/// up to 0.19 dB. Walking the decibel instead would need the logarithm this
/// table exists to avoid, and the bow is a quarter of what a knee already
/// costs — so it is paid rather than removed. Four steps an octave would
/// halve the knee and near enough erase the bow, at two words a band.
let gainTableStepsPerOctave = 2

/// Entries the table uses: one per step of every octave the envelope spans.
let gainTableEntries = sampleWidth * gainTableStepsPerOctave

/// Fraction bits the lookup interpolates on, and the width of the multiply's
/// unsigned operand.
let gainTableFracBits = 8

/// Bits of the address the step within an octave takes.
let gainTableStepBits = log2Exact gainTableStepsPerOctave

/// Bits of the address the octave takes.
let gainTableOctaveBits = bitsToHold sampleWidth

/// Bits the address takes: the octave with the step under it.
let gainTableAddrBits = gainTableOctaveBits + gainTableStepBits

/// Words the table occupies. This is the address's own range rather than the
/// entry count rounded up, which is why the fabric's address is the octave
/// concatenated with the step and needs no compare: a table sized to
/// `gainTableEntries` would leave the top octaves addressing past its end.
/// The words between the last entry and the end are unreachable.
let gainTableSize = 1 <<< gainTableAddrBits

/// The envelope level entry `e` stands for, as a fraction of full scale.
/// Entry 0 is the smallest envelope there is, and is what `env = 0` reads.
let gainTableLevel (e: int) =
    let octave = e / gainTableStepsPerOctave
    let step = e % gainTableStepsPerOctave
    let mantissa = 1.0 + float step / float gainTableStepsPerOctave
    mantissa * (2.0 ** float octave) / (2.0 ** float sampleWidth)

/// Width of a table word: the entry's gain with the step to the next above it.
let gainTableWordWidth = 2 * gainLogWidth

/// A dB gain curve, tabulated for the fabric.
///
/// `curve` takes the envelope's level in **dBFS** — 0 at full scale, negative
/// below — and returns the gain to apply there, in **dB**. Anything in dB
/// SPL is the caller's own offset folded into the curve, so the stdlib holds
/// no opinion about microphones.
///
/// A word is the entry's gain in the low sixteen bits and the signed step to
/// the next entry in the high sixteen, both Q8.8, so the fabric's lookup is
/// one multiply-add and the gain is continuous — no zipper across an entry
/// boundary.
let gainTableWords (curve: float -> float) : uint64[] =
    let one = float (1 <<< gainLogFracBits)
    let mostNegative = -(float (1 <<< (gainLogWidth - 1))) / one
    let mostPositive = float ((1 <<< (gainLogWidth - 1)) - 1) / one

    let toGainLog (db: float) =
        uint64 (int16 (round (max mostNegative (min mostPositive (gainLogOfDb db)) * one)))
        &&& ((1UL <<< gainLogWidth) - 1UL)

    // One point past the last entry, so every entry has a step to the next
    // and the top of the range interpolates like the rest of it. Without it
    // the last entry is flat across its 3 dB while the limiting region is
    // still falling, which costs over two decibels exactly where the ceiling
    // matters most.
    let gains = [| for e in 0..gainTableEntries -> curve (20.0 * log10 (gainTableLevel e)) |]

    [| for e in 0 .. gainTableSize - 1 ->
           if e >= gainTableEntries then
               0UL
           else
               (toGainLog (gains[e + 1] - gains[e]) <<< gainLogWidth) ||| toGainLog gains[e] |]

/// What the fabric's lookup computes for an envelope, from the same words —
/// the model its own path is held to, and the way a host predicts what the
/// band will do without running it. In log2, like the words; `gainDbOfLog` puts
/// it back in the unit the curve was written in.
let gainTableLookup (words: uint64[]) (env: int) : float =
    let mantissaBits = gainTableStepBits + gainTableFracBits
    let low bits x = x &&& ((1 <<< bits) - 1)
    let signedGainLog raw = float (int16 (uint16 (low gainLogWidth raw))) / float (1 <<< gainLogFracBits)

    if env <= 0 then
        signedGainLog (words[0] |> int)
    else
        // The leading one names the octave, and the bits under it place the
        // envelope within it — the same split the fabric's priority encoder and
        // mantissa select make, written the same way round so the two can be
        // read against each other.
        let octave =
            let rec find b = if b < 0 || (env >>> b) &&& 1 = 1 then b else find (b - 1)
            find (sampleWidth - 1)

        let mantissa =
            if octave >= mantissaBits then
                low mantissaBits (env >>> (octave - mantissaBits))
            else
                low mantissaBits (env <<< (mantissaBits - octave))

        let entry = octave * gainTableStepsPerOctave + (mantissa >>> gainTableFracBits)
        let fraction = low gainTableFracBits mantissa

        // In integers and with the same truncating shift the fabric uses, so
        // this predicts the gain rather than approximating it — the two agree to
        // the last of the sixteen bits, which is what lets a check hold one to
        // the other instead of to a tolerance.
        let word = int words[entry]
        let here = int (int16 (uint16 (low gainLogWidth word)))
        let step = int (int16 (uint16 (low gainLogWidth (word >>> gainLogWidth))))
        // The shift divides out the *fraction's* scale, not the gain's. They
        // were the same number while a gain was Q8.8 decibels, which is exactly
        // the kind of coincidence that hides a wrong constant.
        float (here + ((step * fraction) >>> gainTableFracBits)) / float (1 <<< gainLogFracBits)

/// Width of the interpolation's product.
let gainTableProductWidth = gainLogWidth + gainTableFracBits + 1

/// Where an envelope reads in the table, and how far past that entry it falls.
type GainTableIndex =
    { /// The word to read — the octave with the step under it.
      address: Expr
      /// How far along the entry the envelope sits, unsigned Q0.`gainTableFracBits`.
      fraction: Expr }

/// Split an envelope into the table address and the fraction past it.
///
/// The address is the position of the envelope's leading one with the mantissa
/// bits under it — which is the exponent and mantissa of a float, and is why
/// the logarithm the curve is written in costs a priority encoder rather than
/// an approximation whose error lands on the curve's slope.
///
/// `env` must be a declared unsigned signal, since the mantissa is read out of
/// it by slicing. A zero envelope reads entry zero with no interpolation, which
/// is the floor the curve's first entry states.
let gainTableIndex (name: string) (env: Expr) : GainTableIndex =
    if width env <> sampleWidth then
        failwith $"gainTableIndex '{name}' expects a %d{sampleWidth}-bit envelope, got %d{width env} bits"

    let mantissaBits = gainTableStepBits + gainTableFracBits

    // The octave is the highest set bit. Listing the bits most-significant
    // first turns `priorityPick`'s lowest-index-first tree into a
    // highest-bit-first one at log depth.
    let bits = [ for b in sampleWidth - 1 .. -1 .. 0 -> slice b b env ]
    let indices = [ [ for b in sampleWidth - 1 .. -1 .. 0 -> lit (uint64 b) gainTableOctaveBits ] ]
    let _, picked = priorityPick bits indices

    let octave = wire $"{name}_octave" gainTableOctaveBits
    picked[0] ==> octave

    // The mantissa bits below that leading one. A dynamic shift would widen to
    // every position the amount could reach; selecting among static slices is
    // the same mux tree without the width.
    let mantissa = wire $"{name}_mantissa" mantissaBits

    selectIndexed
        octave
        [ for o in 0 .. sampleWidth - 1 ->
              if o = 0 then lit 0UL mantissaBits
              elif o < mantissaBits then catAll [ slice (o - 1) 0 env; lit 0UL (mantissaBits - o) ]
              else slice (o - 1) (o - mantissaBits) env ]
    ==> mantissa

    let address = wire $"{name}_address" gainTableAddrBits
    catAll [ octave; slice (mantissaBits - 1) gainTableFracBits mantissa ] ==> address

    let fraction = wire $"{name}_fraction" gainTableFracBits
    slice (gainTableFracBits - 1) 0 mantissa ==> fraction

    { address = address; fraction = fraction }

/// The entry's own gain, out of a table word.
let private gainTableHere (name: string) (word: Expr) : Expr =
    let here = wire $"{name}_here" (SInt gainLogWidth)
    asSInt (slice (gainLogWidth - 1) 0 word) ==> here
    here

/// The interpolation's two multiply operands — the first half of
/// `gainTableGain`. The addend is `gainTableHere`, separately, because a stage
/// that puts a shared multiplier between the halves computes it on the far side,
/// out of a word it carried through.
let private gainTableOperands (name: string) (word: Expr) (fraction: Expr) : Expr * Expr =
    if width word <> gainTableWordWidth then
        failwith $"gainTableOperands '{name}' expects a %d{gainTableWordWidth}-bit word, got %d{width word} bits"

    let step = wire $"{name}_step" (SInt gainLogWidth)
    asSInt (slice (gainTableWordWidth - 1) gainLogWidth word) ==> step

    let along = wire $"{name}_along" (SInt(gainTableFracBits + 1))
    widenUnsigned (gainTableFracBits + 1) fraction ==> along

    step, along

/// The gain from the interpolation product — the second half of `gainTableGain`.
/// `product` must be a declared signal, since the shift names it.
let private gainTableFromProduct (name: string) (product: Expr) (here: Expr) : Expr =
    let shifted = wire $"{name}_shifted" (SInt(width product))
    sra gainTableFracBits product ==> shifted

    let scaled = wire $"{name}_scaled" (SInt gainLogWidth)
    asSInt (slice (gainLogWidth - 1) 0 shifted) ==> scaled

    let gain = wire $"{name}_gain" (SInt gainLogWidth)
    add here scaled ==> gain
    gain

/// The gain a table word means at a fraction past its entry: the entry's gain
/// plus the stored step scaled by the fraction, in Q8.8 decibels.
///
/// One multiply and one add, and the sum cannot overflow — it lies between the
/// entry's gain and the next entry's, both of which the word already holds.
///
/// `multiply` is whichever multiplier the caller owns, as in `envelopeStep`: a
/// spatial engine hands it `mul` and a folded one hands it a slot on a shared
/// unit, so whether the table gets a block of its own is decided here and is
/// invisible to every caller. The halves are `gainTableOperands` and
/// `gainTableFromProduct`, for an engine that puts a pipeline between them.
let gainTableGain (multiply: Expr -> Expr -> Expr) (name: string) (word: Expr) (fraction: Expr) : Expr =
    let step, along = gainTableOperands name word fraction

    let product = wire $"{name}_scaled_wide" (SInt gainTableProductWidth)
    multiply step along ==> product

    gainTableFromProduct name product (gainTableHere name word)

// ---------------------------------------------------------------------------
// The exponential: a log2 gain back into a multiplier.
//
// `2^(n + f)` is `2^f` shifted by `n`, and a log2 gain hands both over for free
// — the integer part *is* the shift and the fraction *is* the index. So the only
// arithmetic here is one octave of `2^f`, tabulated, and the shift, which is the
// one place a variable shifter appears in the audio path.

/// How finely the mantissa is tabulated — sixteen points across one octave. A
/// chord across a sixteenth of an octave of `2^f` is wrong by `(h·ln2)²/8`,
/// which is 0.002 dB, so the exponential is nowhere near what limits the curve.
let gainExpBits = 4

/// Points in the mantissa table.
let gainExpEntries = 1 <<< gainExpBits

/// Fraction bits left inside one of those points.
let gainExpFracBits = gainLogFracBits - gainExpBits

/// Width of the mantissa: Q1.15 across [1, 2).
let gainExpMantissaBits = 16

/// Width of a mantissa word: the point, and the step to the next above it.
let gainExpWordWidth = 2 * gainExpMantissaBits

/// Bits the exponent takes.
let gainExpExponentBits = gainLogWidth - gainLogFracBits

/// Octaves of gain the apply delivers, either side of unity — ±48 dB.
///
/// **This number sizes the variable shifter, which is the one expensive thing
/// in the audio path**, so it is bounded on purpose rather than left at what the
/// gain format could hold. Two things follow from it and each is worth about as
/// much: the shift is never less than `gainExpMantissaBits - gainApplyOctaves`,
/// so that much of it is constant and therefore free wiring; and what is left
/// spans `2 * gainApplyOctaves` positions rather than the format's 32, which is
/// one barrel stage fewer.
///
/// The gain *format* stays wider, because a sum of terms — a volume control, a
/// noise-reduction decision — needs headroom above what any one of them asks
/// for. It is the apply that clamps, which is where the shifter is.
let gainApplyOctaves = 8

/// The part of the shift that is the same for every gain, and so costs nothing.
let private gainApplyShiftFloor = gainExpMantissaBits - gainApplyOctaves

/// Bits the varying part of the shift takes.
let private gainApplyShiftBits = log2Exact (2 * gainApplyOctaves)

/// `2^f` across one octave, as a point and a step per word — the same word shape
/// the gain table uses, interpolated the same way.
let gainExpWords: uint64[] =
    let one = float (1 <<< (gainExpMantissaBits - 1))
    let at e = int (round (2.0 ** (float e / float gainExpEntries) * one))

    [| for e in 0 .. gainExpEntries - 1 ->
           (uint64 (at (e + 1) - at e) <<< gainExpMantissaBits) ||| uint64 (at e) |]

/// The mantissa the fabric computes for a log2 gain's fraction — `2^f` in Q1.15,
/// as an integer, so the host predicts the fabric rather than approximating it.
let gainExpMantissaOfLog (gainLog: int) : int =
    let fraction = gainLog &&& ((1 <<< gainLogFracBits) - 1)
    let word = int gainExpWords[fraction >>> gainExpFracBits]
    let within = fraction &&& ((1 <<< gainExpFracBits) - 1)
    let here = word &&& ((1 <<< gainExpMantissaBits) - 1)
    let step = word >>> gainExpMantissaBits
    here + ((step * within) >>> gainExpFracBits)

/// What the fabric's apply produces for a log2 gain and a sample — the model the
/// apply path is held to. `gainLog` is the signed Q5.11 value, `sample` a signed
/// sample; the shift and the clamp are the fabric's, floor and two's complement.
let gainApplyToSample (gainLog: int) (sample: int) : int =
    let exponent =
        gainLog >>> gainLogFracBits
        |> max -gainApplyOctaves
        |> min (gainApplyOctaves - 1)
    // In 64 bits: a full-scale sample times a mantissa is 2^38, and the fabric's
    // product wire is wider than an int.
    let product = int64 sample * int64 (gainExpMantissaOfLog gainLog)
    let scaled = product >>> (gainExpMantissaBits - 1 - exponent)
    let ceiling = int64 ((1 <<< (sampleWidth - 1)) - 1)
    int (max (-ceiling - 1L) (min ceiling scaled))

/// The mantissa point an exponent's fraction lands on.
///
/// The table is a select over literals rather than a `rom`, because at sixteen
/// words it is the mux tree a synthesiser builds from one anyway, and because it
/// exports as FIRRTL where a preloaded memory does not.
let gainExpHere (name: string) (gainLog: Expr) : Expr =
    let entry = wire $"{name}_base_entry" gainExpBits
    slice (gainLogFracBits - 1) gainExpFracBits gainLog ==> entry

    let word = wire $"{name}_base_word" gainExpWordWidth
    selectIndexed entry [ for w in gainExpWords -> lit w gainExpWordWidth ] ==> word

    let here = wire $"{name}_base_here" (gainExpMantissaBits + 1)
    widenUnsigned (gainExpMantissaBits + 1) (slice (gainExpMantissaBits - 1) 0 word) ==> here
    here

/// The exponential's two multiply operands. The addend is `gainExpHere`,
/// separately, for the same reason the table's is.
let gainExpOperands (name: string) (gainLog: Expr) : Expr * Expr =
    let entry = wire $"{name}_exp_entry" gainExpBits
    slice (gainLogFracBits - 1) gainExpFracBits gainLog ==> entry

    let within = wire $"{name}_exp_within" gainExpFracBits
    slice (gainExpFracBits - 1) 0 gainLog ==> within

    let word = wire $"{name}_exp_word" gainExpWordWidth
    selectIndexed entry [ for w in gainExpWords -> lit w gainExpWordWidth ] ==> word

    let step = wire $"{name}_exp_step" (SInt(gainExpMantissaBits + 1))
    widenUnsigned (gainExpMantissaBits + 1) (slice (gainExpWordWidth - 1) gainExpMantissaBits word) ==> step

    let along = wire $"{name}_exp_along" (SInt(gainExpFracBits + 1))
    widenUnsigned (gainExpFracBits + 1) within ==> along

    step, along

/// Width of the exponential's product.
let gainExpProductWidth = gainExpMantissaBits + gainExpFracBits + 2

/// The mantissa from that product — the second half. `2^f` in Q1.15, one bit
/// wider than the format so the top of the octave has somewhere to sit.
let gainExpFromProduct (name: string) (product: Expr) (here: Expr) : Expr =
    let shifted = wire $"{name}_exp_shifted" (SInt(width product))
    sra gainExpFracBits product ==> shifted

    let scaled = wire $"{name}_exp_scaled" (gainExpMantissaBits + 1)
    asUInt (slice gainExpMantissaBits 0 shifted) ==> scaled

    let mantissa = wire $"{name}_exp_mantissa" (gainExpMantissaBits + 1)
    add here scaled ==> mantissa
    mantissa

/// The apply's operands: the value, and the mantissa as something to multiply
/// it by.
let gainApplyOperands (name: string) (value: Expr) (mantissa: Expr) : Expr * Expr =
    let mantissaSigned = wire $"{name}_mantissa_signed" (SInt(gainExpMantissaBits + 2))
    widenUnsigned (gainExpMantissaBits + 2) mantissa ==> mantissaSigned
    value, mantissaSigned

/// The scaled value from the apply's product: shifted by the gain's integer
/// part, then saturated.
///
/// **The shift is the only variable shifter in the audio path**, and it is what
/// a gain spanning octaves costs wherever it is kept. A folded engine has one of
/// them however many bands it serves, which is the shape that makes it cheap: on
/// an iCE40 UP5K the exponential, this shift and the saturate are 362 LUT4
/// together.
let gainApplyFromProduct (name: string) (gainLog: Expr) (product: Expr) (toWidth: int) : Expr =
    let exponentWide = wire $"{name}_exponent_wide" (SInt gainExpExponentBits)
    asSInt (slice (gainLogWidth - 1) gainLogFracBits gainLog) ==> exponentWide

    // Clamped to the octaves the apply delivers, which is what bounds the
    // shift. A signed saturate to `gainApplyShiftBits` is exactly the range.
    let exponent = wire $"{name}_exponent" (SInt gainApplyShiftBits)
    saturate gainApplyShiftBits exponentWide ==> exponent

    let exponentRoom = wire $"{name}_exponent_room" (SInt(gainApplyShiftBits + 1))
    pad (gainApplyShiftBits + 1) exponent ==> exponentRoom

    let shiftWide = wire $"{name}_shift_wide" (SInt(gainApplyShiftBits + 1))
    sub (lit (uint64 (gainApplyOctaves - 1)) (gainApplyShiftBits + 1)) exponentRoom ==> shiftWide

    let shift = wire $"{name}_shift" gainApplyShiftBits
    asUInt (slice (gainApplyShiftBits - 1) 0 shiftWide) ==> shift

    // The constant part first, which is wiring, and it narrows what the barrel
    // has to move by exactly as much.
    let narrowed = wire $"{name}_narrowed" (SInt(width product - gainApplyShiftFloor))
    slice (width product - 1) gainApplyShiftFloor product ==> narrowed

    let shifted = wire $"{name}_shifted" (SInt(width narrowed))
    shrBy shift narrowed ==> shifted

    let scaled = wire $"{name}_scaled" (SInt toWidth)
    saturate toWidth shifted ==> scaled
    scaled

/// Apply a log2 gain to a sample: the fraction becomes a mantissa, the integer
/// part becomes the shift, and the result saturates at the value's own width.
///
/// `multiply` is the caller's, as everywhere else in here: two of them, the
/// mantissa's interpolation and the apply, so a folded engine spends two slots
/// on its shared unit and a spatial one two blocks of its own. The halves are
/// `gainExpOperands` / `gainExpFromProduct` and `gainApplyOperands` /
/// `gainApplyFromProduct`, for an engine that puts a pipeline between them.
/// `toWidth` is the width to saturate into, which is the caller's business and
/// not the value's: a band arrives at its own width and leaves wide enough that
/// a sum of bands can saturate once at the end. **Do not pre-widen the value to
/// get there** — the product, and so the shifter, is as wide as what goes in,
/// and nine bits of sign extension is nine more bits through four barrel stages.
let gainApply (multiply: Expr -> Expr -> Expr) (name: string) (toWidth: int) (gainLog: Expr) (value: Expr) : Expr =
    if width gainLog <> gainLogWidth then
        failwith $"gainApply '{name}' expects a %d{gainLogWidth}-bit log gain, got %d{width gainLog} bits"

    let step, along = gainExpOperands name gainLog

    let expProduct = wire $"{name}_exp_product" (SInt gainExpProductWidth)
    multiply step along ==> expProduct

    let mantissa = gainExpFromProduct name expProduct (gainExpHere name gainLog)
    let a, b = gainApplyOperands name value mantissa

    let product = wire $"{name}_product" (SInt(width a + width b))
    multiply a b ==> product

    gainApplyFromProduct name gainLog product toWidth


/// The volume stage's ports.
type AudioGainPorts =
    { /// The stereo stream through the stage.
      s: StereoPorts
      /// Gain in Q8.8: `gainUnity` passes through, 512 doubles, 128 halves,
      /// 0 is silence.
      volume: Input
      /// High forces the output to zero, whatever `volume` says.
      mute: Input }

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
let audioGainDef (name: string) : TypedModule<AudioGainPorts> =
    let productWidth = sampleWidth + 17
    let scaledWidth = productWidth - gainFracBits

    defModule
        name
        (fun p ->
            { s = stereoPorts p
              volume = p.inPort "volume" 16
              mute = p.inPort "mute" 1 })
        (fun io ->
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

/// One instance under `instName`, called as a function: wire the volume and
/// mute controls, splice the stream through.
let audioGain (name: string) instName =
    let io = (audioGainDef name).NewNamed instName

    fun (volume: Expr) (mute: Expr) (s: Stream<Expr * Expr>) ->
        volume ==> io.volume
        mute ==> io.mute
        stereoSplice io.s s

/// The single-band equalizer's ports.
type AudioEqBandPorts =
    { /// The stereo stream through the band.
      s: StereoPorts
      /// The five biquad coefficients in `b0, b1, b2, a1, a2` order, each
      /// Q2.30 — `biquadUnity` is +1.0. `biquadDesign` produces them.
      coefficients: Input list }

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
let audioEqBandDef (name: string) : TypedModule<AudioEqBandPorts> =
    defModule
        name
        (fun p ->
            { s = stereoPorts p
              coefficients = List.init 5 (fun i -> p.inPort $"c{i}" biquadCoeffWidth) })
        (fun io ->
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

            section "left" io.s.inLeft advance coefficients ==> io.s.outLeft
            section "right" io.s.inRight advance coefficients ==> io.s.outRight)

/// One instance under `instName`, called as a function: wire the five
/// coefficients, splice the stream through.
let audioEqBand (name: string) instName =
    let io = (audioEqBandDef name).NewNamed instName

    fun (coefficients: Expr list) (s: Stream<Expr * Expr>) ->
        List.iter2 (fun port c -> c ==> port) io.coefficients coefficients
        stereoSplice io.s s

/// The hard limiter's ports.
type AudioLimiterPorts =
    { /// The stereo stream through the limiter.
      s: StereoPorts
      /// The magnitude no sample may exceed, at sample scale. Signed,
      /// because it is compared against samples that are.
      threshold: Input }

/// Hard brick-wall stereo limiter — the chain's final safety stage.
///
///     out = clamp(in, -threshold, +threshold)
///
/// Combinational, no envelope, no smoothing. It adds harmonic distortion when
/// it engages, which is the correct behaviour here: audible limiter distortion
/// means the stage is doing the job it exists for. `threshold` is a positive
/// sample value; a host writing a negative one gets a nonsensical limit pair,
/// so the register is treated as effectively unsigned.
let audioLimiterDef (name: string) : TypedModule<AudioLimiterPorts> =
    defModule
        name
        (fun p ->
            { s = stereoPorts p
              threshold = p.inPortAs "threshold" (SInt sampleWidth) })
        (fun io ->
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

/// One instance under `instName`, called as a function: wire the threshold,
/// splice the stream through.
let audioLimiter (name: string) instName =
    let io = (audioLimiterDef name).NewNamed instName

    fun (threshold: Expr) (s: Stream<Expr * Expr>) ->
        threshold ==> io.threshold
        stereoSplice io.s s

/// The ports of a stereo echo: the stream, how far back to read, and how much
/// of what it reads comes back round.
type AudioEchoPorts =
    { /// The stereo stream through the stage.
      s: StereoPorts
      /// How far back the echo is read, in samples. At `stockSampleRate` a
      /// delay of 16,000 is about a third of a second.
      delay: Input
      /// How much of the echo is fed back in, Q8.8 as every gain in this file
      /// is: 0 is dry, `gainUnity / 2` halves each repeat, `gainUnity` would
      /// never decay at all.
      feedback: Input }

/// Stereo echo over a memory-backed delay line.
///
///     written = saturate24(in + (echo * feedback) >> 8)
///     out     = written
///
/// What leaves is also what goes back into the line, so every pass round is
/// multiplied by `feedback` again and the repeats decay geometrically. With
/// `feedback = 0` the stage is an exact pass-through — the reset value every
/// control in this file takes, so a bitstream that has heard from no host still
/// passes audio.
///
/// **`capacity` is a resource and `delay` is a setting**, which is why one is
/// an argument and the other a port. The line is a block RAM and a design
/// cannot grow one while it runs; the tap is arithmetic, so a host moves the
/// echo time without a rebuild. `delayBuffer` is where that split is made.
///
/// The two channels share one line as a single packed frame — one memory rather
/// than two, and a pair that cannot drift apart, which is what `sampleLayout`
/// already means by a frame.
let audioEchoDef (name: string) (capacity: int) : TypedModule<AudioEchoPorts> =
    let wetWidth = sampleWidth + 17 - gainFracBits
    let sumWidth = wetWidth + 1

    defModule
        name
        (fun p ->
            { s = stereoPorts p
              delay = p.inPort "delay" (log2Exact capacity)
              feedback = p.inPort "feedback" 16 })
        (fun io ->
            spliceHandshake io.s

            // The line advances on beats the stage accepted, never on bare
            // cycles: an echo measured in clocks would change pitch the first
            // time something downstream stalled.
            let fired = wireBit "fired"
            (io.s.inValid &&& io.s.outReady) ==> fired

            // Declared before the line reads it and driven after: the loop from
            // `written` back to itself runs through the memory's registered
            // read, so it is a recurrence rather than a combinational cycle.
            let written = wire "written" sampleBits
            let echoed = delayBuffer "line" capacity fired io.delay written

            let feedbackSigned = wire "feedback_signed" (SInt 17)
            widenUnsigned 17 io.feedback ==> feedbackSigned

            let channel channelName (dry: Expr) (wetBits: Expr) =
                let wet = wire $"{channelName}_wet" (SInt sampleWidth)
                asSInt wetBits ==> wet

                let product = wire $"{channelName}_product" (SInt(sampleWidth + 17))
                mul wet feedbackSigned ==> product

                let scaled = wire $"{channelName}_scaled" (SInt wetWidth)
                shr gainFracBits product ==> scaled

                let sum = wire $"{channelName}_sum" (SInt sumWidth)
                add (pad sumWidth dry) (pad sumWidth scaled) ==> sum

                let saturated = wire $"{channelName}_saturated" (SInt sampleWidth)
                saturate sampleWidth sum ==> saturated
                saturated

            let left = channel "left" io.s.inLeft (sampleLeft echoed)
            let right = channel "right" io.s.inRight (sampleRight echoed)

            cat left right ==> written
            left ==> io.s.outLeft
            right ==> io.s.outRight)

/// One instance under `instName`, called as a function: wire the two controls,
/// splice the stream through.
let audioEcho (name: string) (capacity: int) instName =
    let io = (audioEchoDef name capacity).NewNamed instName

    fun (delay: Expr) (feedback: Expr) (s: Stream<Expr * Expr>) ->
        delay ==> io.delay
        feedback ==> io.feedback
        stereoSplice io.s s

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

/// The envelope step's multiply operands, from the current envelope and this
/// sample's peak: the difference and the chosen coefficient, plus the widened
/// envelope the second half needs back. The first half of `envelopeStep`,
/// split so a pipelined engine can compute it a stage ahead of the multiply.
let private envelopeOperands (env: Expr) (peak: Expr) (attack: Expr) (releaseRate: Expr) : Expr * Expr * Expr =
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

    difference, alphaSigned, envWide

/// The next envelope from the step product and the widened envelope it was
/// stepped from — the second half of `envelopeStep`. `step` must be a declared
/// `SInt stepWidth` signal, the multiply's own product width.
let private envelopeFromStep (step: Expr) (envWide: Expr) : Expr =
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
    envValue

/// One step of the envelope recurrence, as arithmetic on a value: the next
/// envelope from the current one and this sample's peak, and the current one
/// widened for the gain computer. `multiply` is whichever multiplier the caller
/// owns — see `envelopeFollower`. The two halves are `envelopeOperands` and
/// `envelopeFromStep`, for an engine that puts a pipeline between them.
let private envelopeStep (multiply: Expr -> Expr -> Expr) (env: Expr) (peak: Expr) (attack: Expr) (releaseRate: Expr) : Expr * Expr =
    let difference, alphaSigned, envWide = envelopeOperands env peak attack releaseRate

    let step = wire "step" (SInt stepWidth)
    multiply difference alphaSigned ==> step

    envelopeFromStep step envWide, envWide

/// Envelope follower: `env += alpha*(peak - env)` with `alpha` the attack
/// coefficient while the signal is rising and the release one while it falls,
/// clipped into the envelope register's unsigned range. Returns the register
/// and its widened form, which the gain computer reuses rather than rebuilding.
///
/// The loop is a recurrence, so it stays combinational — there is no pipelining
/// it, which is why callers pipeline only the apply path around it.
///
/// **The arithmetic is `envelopeStep`; this is the register around it.** The
/// folded engine keeps its envelopes in memory and runs the same step on a
/// fetched value, so the step is written once and takes the multiplier as an
/// argument — a spatial engine hands it `mul`, a folded one hands it a slot on
/// a shared multiplier. One law, two storages.
let private envelopeFollower (peak: Expr) (attack: Expr) (releaseRate: Expr) (advance: Expr) : Expr * Expr =
    let env = reg "env" sampleWidth
    let envValue, envWide = envelopeStep mul env peak attack releaseRate
    If advance (fun () -> envValue ==> env)

    env, envWide

/// How far the envelope is over the threshold, clipped at zero — the gain
/// computer's multiply operand, and the first half of `gainComputer`.
let private gainExcess (envWide: Expr) (threshold: Expr) : Expr =
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

    excess

/// The gain from the reduction product, clipped and sign-extended for the apply
/// multiply — the second half of `gainComputer`. `reductionRaw` must be a
/// declared `gainRedWidth` signal.
let private gainFromReduction (reductionRaw: Expr) : Expr =
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

/// Gain computer: `gain = 1 - min((env - threshold) * ratio, 1)` in Q0.24,
/// returned sign-extended and ready for the apply multiply. `ratio` is a slope
/// of gain against excess rather than an N:1 knob, which is what keeps the
/// whole computer one multiply and a clip — no division, no log. The halves
/// are `gainExcess` and `gainFromReduction`.
let private gainComputer (multiply: Expr -> Expr -> Expr) (envWide: Expr) (threshold: Expr) (ratio: Expr) : Expr =
    let excess = gainExcess envWide threshold

    let reductionRaw = wire "reduction_raw" gainRedWidth
    multiply excess ratio ==> reductionRaw

    gainFromReduction reductionRaw

/// Pipeline latency of [audioCompressor]'s gain-apply datapath. The envelope
/// feedback is a recurrence and stays combinational; only the two output
/// multiplies are registered. A parallel or bypass path must delay by this
/// much to stay aligned.
let compressorLatency = 2

/// The stereo compressor's ports. Every parameter is a signal, so a host can
/// retune the dynamics without a rebuild.
type AudioCompressorPorts =
    { /// The stereo stream through the compressor.
      s: StereoPorts
      /// The level above which gain reduction begins, at sample scale.
      threshold: Input
      /// Compression ratio. Above the threshold, this much input change
      /// produces one unit of output change.
      ratio: Input
      /// How fast the envelope rises toward a louder signal.
      attack: Input
      /// How fast it falls back toward a quieter one.
      releaseRate: Input
      /// Gain applied after compression, in the same Q8.8 as `audioGain` —
      /// which is what makes the boost-then-compress order preservable.
      makeup: Input }

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
let audioCompressorDef (name: string) : TypedModule<AudioCompressorPorts> =
    let gainWidth = sampleWidth + 1
    let boostProductWidth = sampleWidth + 17
    let boostWidth = boostProductWidth - gainFracBits

    defModule
        name
        (fun p ->
            { s = stereoPorts p
              threshold = p.inPort "threshold" sampleWidth
              ratio = p.inPort "ratio" 8
              attack = p.inPort "attack" 16
              // `release` is a Verilog reserved word.
              releaseRate = p.inPort "releaseRate" 16
              makeup = p.inPort "makeup" 16 })
        (fun io ->
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
            let gainSigned = gainComputer mul envWide io.threshold io.ratio

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

/// What a compressor is set to. Five values, four of them unsigned and two of
/// them the same width, handed over positionally: swapping attack and release
/// elaborates, passes every width check, and produces a compressor that pumps
/// instead of one that breathes. Named fields make the swap unwriteable, and a
/// host-facing register map fills the record in one place (`effectsSettings` in
/// the audio example) so the map itself stops at the top of the design.
type CompressorSettings =
    { threshold: Expr
      ratio: Expr
      attack: Expr
      releaseRate: Expr
      makeup: Expr }

/// The five controls as a module's own ports — the io-factory half of
/// `CompressorSettings`, so a design declares them and hands them on as one
/// value instead of threading five same-typed inputs through its body.
///
/// The declaration order reproduces what the hand-written designs had, so
/// converting one is byte-identical in the emitted Verilog and the host names
/// its registers exactly as before.
let compressorSettingsPorts (p: Ports) : CompressorSettings =
    { threshold = p.inPort "threshold" sampleWidth
      ratio = p.inPort "ratio" 8
      attack = p.inPort "attack" 16
      releaseRate = p.inPort "releaseRate" 16
      makeup = p.inPort "makeup" 16 }

/// One instance under `instName`, called as a function: wire the dynamics
/// controls, splice the stream through.
let audioCompressor (name: string) instName =
    let io = (audioCompressorDef name).NewNamed instName

    fun (settings: CompressorSettings) (s: Stream<Expr * Expr>) ->
        settings.threshold ==> io.threshold
        settings.ratio ==> io.ratio
        settings.attack ==> io.attack
        settings.releaseRate ==> io.releaseRate
        settings.makeup ==> io.makeup
        stereoSplice io.s s

// ---------------------------------------------------------------------------
// Sources and the tone-control filter.

/// Phase-accumulator width. Frequency = Fs * step / 2^tonePhaseWidth.
let tonePhaseWidth = 24

/// The phase increment that plays `hz` at a link running at `fs`.
///
/// **Derived rather than written down**, because the step and the frame rate
/// are the same fact twice: a constant picked for one Fs plays a different note
/// the moment a design moves to a board that frames at another, and a tone
/// generator that is 4% flat is not a failure anyone notices on a bench — it is
/// still a sound. `sampleRateOf` supplies the `fs` a design actually achieves,
/// so neither number is restated.
let toneStepFor (fs: float) (hz: float) : uint64 =
    uint64 (round (hz / fs * float (1 <<< tonePhaseWidth)))

/// The output half of a stereo stage's ports — what a source declares.
type StereoSourcePorts =
    { outLeft: Output
      outRight: Output
      outValid: Output
      outReady: Input }

let private stereoSourcePorts (p: Ports) : StereoSourcePorts =
    { outLeft = p.outPort "out_left" sampleWidth
      outRight = p.outPort "out_right" sampleWidth
      outValid = p.outPort "out_valid" 1
      outReady = p.inPort "out_ready" 1 }

let private sourceStream (sp: StereoSourcePorts) : Stream<Expr * Expr> =
    registerStreamReady sp.outReady

    { payload = (sp.outLeft, sp.outRight)
      valid = sp.outValid
      ready = sp.outReady
      layout = sampleLayout }

/// The tone source's ports.
type ToneGeneratorPorts =
    { /// The stereo stream out. A source, so there is nothing coming in.
      s: StereoSourcePorts
      /// High while the tone runs. Low holds the phase where it was.
      enable: Input
      /// Phase increment per sample — the frequency, as the numerically
      /// controlled oscillator sees it.
      step: Input }

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
let toneGeneratorDef (name: string) : TypedModule<ToneGeneratorPorts> =
    defModule
        name
        (fun p ->
            { s = stereoSourcePorts p
              enable = p.inPort "enable" 1
              step = p.inPort "step" tonePhaseWidth })
        (fun io ->
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

/// One instance under `instName`, called as a function: wire the enable and
/// step controls, hand back the stereo stream out.
let toneGenerator (name: string) instName =
    let io = (toneGeneratorDef name).NewNamed instName

    fun (enable: Expr) (step: Expr) ->
        enable ==> io.enable
        step ==> io.step
        sourceStream io.s

/// Coefficient encoding: Q1.15 in 16 bits, so 32767 is the representable
/// maximum (just under +1.0).
let firCoeffWidth = 16

/// Fraction bits in a FIR coefficient.
let firCoeffFrac = 15

/// The filter presets a design can select at run time, as register values.
/// `presetBypass` passes the input through.
let presetBypass = 0
/// A low-pass response.
let presetLowPass = 1
/// A high-pass response.
let presetHighPass = 2

let private coeffLimit = 1 <<< firCoeffFrac
let private clampCoeff v = max -coeffLimit (min (coeffLimit - 1) v)

/// Java's `roundToInt` breaks ties toward positive infinity; .NET's
/// `Math.Round` is banker's rounding, which would silently disagree by an LSB
/// on exactly-half coefficients. `floor(x + 0.5)` reproduces the same rule, so
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

/// The FIR stage's ports.
type AudioFirPorts =
    { /// The stereo stream through the filter.
      s: StereoPorts
      /// Which response to apply: `presetBypass`, `presetLowPass` or
      /// `presetHighPass`. Selected at run time, so all three sets of taps
      /// are elaborated and one is chosen.
      preset: Input }

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
/// A tone control's three frequencies. All three are floats in hertz and they
/// arrived positionally, so `sampleRate lpCutoff hpCutoff` could be given in
/// any order and still build a filter — a wrong one, whose only symptom is
/// that it sounds wrong. Named fields also put the rate beside the cutoffs it
/// is relative to, which is the relationship a coefficient set actually fixes:
/// designing at one rate and running at another moves every cutoff by the
/// ratio between them.
type FirBands =
    { /// The rate the coefficients are designed for, in hertz.
      sampleRate: float
      /// Corner of the low-pass bank.
      lowPass: float
      /// Corner of the high-pass bank.
      highPass: float }

let audioFirDef (name: string) (taps: int) (bands: FirBands) : TypedModule<AudioFirPorts> =
    let sampleRate, lpCutoff, hpCutoff = bands.sampleRate, bands.lowPass, bands.highPass

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

    defModule
        name
        (fun p ->
            { s = stereoPorts p
              preset = p.inPort "preset" 2 })
        (fun io ->
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

/// One instance under `instName`, called as a function: wire the preset
/// select, splice the stream through.
let audioFir (name: string) (taps: int) (bands: FirBands) instName =
    let io = (audioFirDef name taps bands).NewNamed instName

    fun (preset: Expr) (s: Stream<Expr * Expr>) ->
        preset ==> io.preset
        stereoSplice io.s s

/// The stock tone control: 16 taps, 4 kHz low-pass and 300 Hz high-pass.
///
/// **The rate is the caller's**, for the reason `multibandCompressor` states:
/// a filter's coefficients fix a frequency in cycles per *sample*, so a
/// stdlib entry that assumed 48 000 would be 1.7 % wrong on every board design
/// in this repository, which runs at `stockSampleRate`.
let audioToneFilter name (sampleRate: float) =
    audioFir
        name
        16
        { sampleRate = sampleRate
          lowPass = 4_000.0
          highPass = 300.0 }

// ---------------------------------------------------------------------------
// I2S. Bit-serial clocking rather than datapath: the codec's frame is a
// counter hierarchy, and rx/tx are shift registers hung off its edge ticks.

/// The input half of a stereo stage's ports — what a sink declares.
type StereoSinkPorts =
    { inLeft: Input
      inRight: Input
      inValid: Input
      inReady: Output }

let private stereoSinkPorts (p: Ports) : StereoSinkPorts =
    { inLeft = p.inPort "in_left" sampleWidth
      inRight = p.inPort "in_right" sampleWidth
      inValid = p.inPort "in_valid" 1
      inReady = p.outPort "in_ready" 1 }

let private stereoSink (sp: StereoSinkPorts) (s: Stream<Expr * Expr>) =
    let left, right = s.payload
    left ==> sp.inLeft
    right ==> sp.inRight
    s.valid ==> sp.inValid
    sp.inReady ==> s.ready

/// The clock generator's outputs — the one place in this file where a module
/// hands its caller the ports themselves, because clocking is what it *is*.
type I2sMasterPorts =
    { /// Master clock to the codec.
      mclk: Output
      /// Serial bit clock.
      sclk: Output
      /// Left/right word clock. High and low halves are the two channels.
      lrclk: Output
      /// One fabric cycle on each edge the receiver should sample on.
      sclkRxTick: Output
      /// One fabric cycle on each edge the transmitter should drive on.
      sclkTxTick: Output }

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
let i2sMaster (name: string) (mclkHalfDiv: int) (sclkHalfDiv: int) (bitsPerSlot: int) : TypedModule<I2sMasterPorts> =
    if mclkHalfDiv < 1 then failwith $"i2sMaster mclkHalfDiv must be >= 1, got {mclkHalfDiv}"
    if sclkHalfDiv < 1 then failwith $"i2sMaster sclkHalfDiv must be >= 1, got {sclkHalfDiv}"
    if bitsPerSlot < 1 then failwith $"i2sMaster bitsPerSlot must be >= 1, got {bitsPerSlot}"

    defModule
        name
        (fun p ->
            { mclk = p.outPort "mclk" 1
              sclk = p.outPort "sclk" 1
              lrclk = p.outPort "lrclk" 1
              sclkRxTick = p.outPort "sclkRxTick" 1
              sclkTxTick = p.outPort "sclkTxTick" 1 })
        (fun io ->
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
/// The stock divisors, named once so everything derived from them derives from
/// the same numbers: `i2sMasterDefault` builds the generator out of these, and
/// `stockSampleRate` below is what they produce. Written down twice, the rate
/// and the generator could disagree — which is the defect this whole family of
/// helpers exists to make unwriteable.
let stockMclkHalfDiv = 4
let stockSclkHalfDiv = 16

/// Bit-clock periods each channel occupies in the stock frame. The sample is
/// narrower; the rest of the slot is zero padding.
let stockBitsPerSlot = 32

let i2sMasterDefault name =
    i2sMaster name stockMclkHalfDiv stockSclkHalfDiv stockBitsPerSlot

/// How far the achieved sample rate may sit from the requested one before
/// `i2sMasterHz` refuses to build. One percent: converters tolerate several,
/// and a rate that is out by more than this is a divisor that was never going
/// to work rather than a rounding artifact.
let i2sRateTolerance = 0.01

/// The nominal MCLK a converter expects, as a multiple of the sample rate.
/// 256x is what the CS5343/CS4344 want and what the stock divisors produce. A
/// shared-bus pinout declares no MCLK pin at all, so the number does not reach
/// anything there.
let private i2sMclkRatio = 256

/// The clock generator asked for a **sample rate** rather than for divisors.
///
/// `i2sMaster` takes the three dividers directly, which is honest and is what
/// the emitted hardware is, but it means every caller does the same arithmetic
/// by hand and the fabric clock frequency appears nowhere — so a design moved
/// to a board with a different clock keeps its old divisors and silently runs
/// at the wrong rate. Naming the rate makes that a build failure instead.
///
/// The divisors are chosen by rounding, then the rate they actually produce is
/// checked back against the request. **A rate outside `i2sRateTolerance` is an
/// error, not a warning** — 100 MHz cannot make exactly 48 kHz, and the gap
/// between "cannot, by 0.0003%" and "cannot, by 1.7%" is the whole question.
///
/// The achieved rate is deliberately not returned. A caller that has to thread
/// it onward is a caller re-deriving the frame timing, which is the
/// transmitter's and receiver's business; `sampleRateOf` computes it for a
/// check or a comment without a value crossing a module boundary.
let i2sMasterHz (fabricHz: int) (targetFs: int) (bitsPerSlot: int) (name: string) : TypedModule<I2sMasterPorts> =
    if fabricHz < 1 then failwith $"i2sMasterHz '{name}': fabricHz must be >= 1, got {fabricHz}"
    if targetFs < 1 then failwith $"i2sMasterHz '{name}': targetFs must be >= 1, got {targetFs}"
    if bitsPerSlot < 1 then failwith $"i2sMasterHz '{name}': bitsPerSlot must be >= 1, got {bitsPerSlot}"

    let divide (a: int) (b: float) = max 1 (int (round (float a / b)))

    let sclkHalfDiv = divide fabricHz (4.0 * float targetFs * float bitsPerSlot)
    let mclkHalfDiv = divide fabricHz (2.0 * float i2sMclkRatio * float targetFs)

    let achieved = float fabricHz / (4.0 * float sclkHalfDiv * float bitsPerSlot)
    let error = abs (achieved - float targetFs) / float targetFs

    if error > i2sRateTolerance then
        failwith (
            $"i2sMasterHz '{name}': %d{fabricHz} Hz cannot make %d{targetFs} Hz at %d{bitsPerSlot} bits per slot — "
            + $"the nearest divisor (%d{sclkHalfDiv}) gives %.3f{achieved} Hz, off by %.2f{error * 100.0}%%. "
            + $"Either pick a rate this clock divides into, or drive the design from a clock that divides into this one.")

    i2sMaster name mclkHalfDiv sclkHalfDiv bitsPerSlot

/// The sample rate a given set of divisors produces, for a check or a comment.
/// The same arithmetic `i2sMasterHz` verifies against, exposed so a test can
/// state the rate it expects rather than restating the formula.
let sampleRateOf (fabricHz: int) (sclkHalfDiv: int) (bitsPerSlot: int) : float =
    float fabricHz / (4.0 * float sclkHalfDiv * float bitsPerSlot)

/// The rate `i2sMasterDefault`'s divisors produce on the KV260's fabric clock,
/// which is what every board design in this repository runs at: 48 828.125 Hz.
///
/// Derived rather than written down, so it cannot drift from the divisors it
/// comes from or from the board it comes from — and stated once, so the
/// designs that need a rate do not each restate it. A design on any other
/// clock passes its own.
let stockSampleRate =
    sampleRateOf kv260.fabricHz stockSclkHalfDiv stockBitsPerSlot

/// Phase increment for 440 Hz at the stock rate — the tone every KV260 audio
/// app plays.
///
/// **Declared beside the rate rather than beside `toneStepFor`**, which is the
/// whole reason it is a derivation: the step depends on the frame rate, the
/// frame rate depends on the divisors and the board, and putting the three in
/// one place is what stops a divisor changing while the step stays put. A
/// design on another clock calls `toneStepFor` with its own.
///
/// **It had already drifted.** The constant written here was 151,199, under a
/// comment quoting this very formula at 48 828.125 Hz — which gives 151,183.
/// 151,199 is what 48 823 Hz gives, a frame rate this repository has not run at
/// for some time. The tone was 440.05 Hz rather than 440, which is inaudible
/// and beside the point: a number that has quietly stopped agreeing with its own
/// stated derivation is the failure mode, and the size of the error is luck.
let toneStep440 = toneStepFor stockSampleRate 440.0

/// The I2S receiver's ports. Clocking arrives from `i2sMaster` rather than
/// being recovered, which is what FPGA-master operation means.
type I2sRxPorts =
    { /// The stereo stream out.
      s: StereoSourcePorts
      /// One fabric cycle on each edge to sample `sdout` on.
      sclkTick: Input
      /// The current word-clock level — which channel is on the wire.
      lrclk: Input
      /// Serial data in from the converter.
      sdout: Input }

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
let i2sRxDef (name: string) : TypedModule<I2sRxPorts> =
    defModule
        name
        (fun p ->
            { s = stereoSourcePorts p
              sclkTick = p.inPort "sclkTick" 1
              lrclk = p.inPort "lrclk" 1
              sdout = p.inPort "sdout" 1 })
        (fun io ->
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
                ifElse [
                    (lrclkEdge, fun () ->
                        // The tick on which LRCLK turns carries no data — the I2S
                        // one-cycle delay.
                        lit 0UL 6 ==> bitCount)
                    (otherwise, fun () ->
                    bitCount + lit 1UL 6 ==> bitCount

                    ifElse [
                        (lt bitCount (lit (uint64 sampleWidth) 6), fun () ->
                            shifted ==> shift

                            ifElse [
                                (eq bitCount (lit (uint64 (sampleWidth - 1)) 6), fun () ->
                                    ifElse [
                                        (eq io.lrclk (lit 0UL 1), fun () -> shifted ==> leftHold)
                                        (otherwise, fun () ->
                                        leftHold ==> leftReg
                                        shifted ==> rightReg
                                        lit 1UL 1 ==> validReg) ])
                            ])
                    ]) ]))

/// One receiver under `instName`, called as a function: wire the clocking and
/// the serial line, hand back the stereo stream out.
let i2sRx (name: string) instName =
    let io = (i2sRxDef name).NewNamed instName

    fun (sclkTick: Expr) (lrclk: Expr) (sdout: Expr) ->
        sclkTick ==> io.sclkTick
        lrclk ==> io.lrclk
        sdout ==> io.sdout
        sourceStream io.s

/// The I2S transmitter's ports, mirroring the receiver's.
type I2sTxPorts =
    { /// The stereo stream in.
      s: StereoSinkPorts
      /// One fabric cycle on each edge to drive `sdin` on.
      sclkTick: Input
      /// The current word-clock level.
      lrclk: Input
      /// Serial data out to the converter.
      sdin: Output }

/// I2S transmitter: a stereo stream out to the DAC's serial line. The mirror
/// of `i2sRx`, and it shares the frame convention exactly — one transition
/// tick, then 24 data bits MSB-first.
///
/// A one-slot pending buffer decouples the stream handshake from the frame:
/// `ready` asserts whenever that slot is empty, and the slot commits to the
/// shift registers on entry to a new left slot. That is what lets a producer
/// hand over a sample at any point in the frame without tearing one in half.
///
/// **The shift registers carry a leading zero** — 25 bits for a 24-bit
/// sample — so the transition tick drives nothing and the MSB lands one bit
/// clock after the LRCLK edge, where I2S puts it. Without that bit (until
/// 2026-09-14) the MSB rode the edge itself, which is left-justified framing:
/// an I2S-mode converter read every word shifted left by one — a clean x2
/// below half scale and a wrap above it — and nothing in simulation could see
/// it, because the codec model's receive side had been written to the same
/// timing. After the 25 ticks the register has zero-filled, so the padding
/// ticks emit zeros without a case for them.
/// A transmitter that sends the top `sentBits` of each sample.
///
/// The stream's samples stay `sampleWidth` wide — what changes is how many of
/// their bits reach the wire, taken from the **top**, which is what a
/// narrower receiver reads anyway. `sentBits = sampleWidth` is the whole
/// sample and emits exactly what this module always emitted.
///
/// It exists because a slot cannot always be as wide as the sample: a
/// receiver's bit clock has a ceiling, and at a fixed frame rate the only
/// thing left to give is depth. A 24-bit sample in a 16-bit slot at 46 875 Hz
/// is 1.5 MHz of bit clock where the full sample would be 2.25 — and the
/// eight bits it drops are below the noise floor of any microphone that fed
/// it.
let i2sTxDefAt (sentBits: int) (name: string) : TypedModule<I2sTxPorts> =
    if sentBits < 1 || sentBits > sampleWidth then
        failwith $"i2sTx '{name}': sends %d{sentBits} bits of a %d{sampleWidth}-bit sample"

    defModule
        name
        (fun p ->
            { s = stereoSinkPorts p
              sclkTick = p.inPort "sclkTick" 1
              lrclk = p.inPort "lrclk" 1
              sdin = p.outPort "sdin" 1 })
        (fun io ->
            // The top of a sample, where the whole sample is the whole thing —
            // spelled that way so the unnarrowed case emits what it always did.
            let sent (e: Expr) =
                if sentBits = sampleWidth then
                    e
                else
                    slice (sampleWidth - 1) (sampleWidth - sentBits) e

            // The leading zero is the transition bit.
            let shiftWidth = sentBits + 1
            let leftShift = reg "left_shift" shiftWidth
            let rightShift = reg "right_shift" shiftWidth
            let pendingLeft = reg "pending_left" sampleWidth
            let pendingRight = reg "pending_right" sampleWidth
            let pendingValid = regBit "pending_valid"

            let bitCount = reg "bit_count" 6

            // Whichever channel's slot is live drives the line from its top bit.
            mux
                    io.lrclk
                    (slice (shiftWidth - 1) (shiftWidth - 1) rightShift)
                    (slice (shiftWidth - 1) (shiftWidth - 1) leftShift)
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
                ifElse [
                    (lrclkEdge, fun () ->
                        lit 0UL 6 ==> bitCount

                        If (eq io.lrclk (lit 0UL 1) &&& pendingValid) (fun () ->
                            cat (lit 0UL 1) (sent pendingLeft) ==> leftShift
                            cat (lit 0UL 1) (sent pendingRight) ==> rightShift
                            lit 0UL 1 ==> pendingValid))
                    (otherwise, fun () ->
                    bitCount + lit 1UL 6 ==> bitCount

                    If (lt bitCount (lit (uint64 shiftWidth) 6)) (fun () ->
                        ifElse [
                            (eq io.lrclk (lit 0UL 1), fun () ->
                                cat (slice (shiftWidth - 2) 0 leftShift) (lit 0UL 1) ==> leftShift)
                            (otherwise, fun () ->
                            cat (slice (shiftWidth - 2) 0 rightShift) (lit 0UL 1) ==> rightShift) ])) ]))

/// The transmitter at the design's own sample width — every bit of it.
let i2sTxDef (name: string) : TypedModule<I2sTxPorts> = i2sTxDefAt sampleWidth name

/// One transmitter under `instName`, sending the top `sentBits` of each
/// sample: wire the clocking, sink the stream, hand back the serial line out.
let i2sTxAt (sentBits: int) (name: string) instName =
    let io = (i2sTxDefAt sentBits name).NewNamed instName

    fun (sclkTick: Expr) (lrclk: Expr) (s: Stream<Expr * Expr>) ->
        sclkTick ==> io.sclkTick
        lrclk ==> io.lrclk
        stereoSink io.s s
        io.sdin

/// One transmitter under `instName`, called as a function: wire the clocking,
/// sink the stream, hand back the serial line out.
let i2sTx (name: string) instName =
    let io = (i2sTxDef name).NewNamed instName

    fun (sclkTick: Expr) (lrclk: Expr) (s: Stream<Expr * Expr>) ->
        sclkTick ==> io.sclkTick
        lrclk ==> io.lrclk
        stereoSink io.s s
        io.sdin

// ---------------------------------------------------------------------------
// The link: the whole I2S front end as one thing, so a design that wants audio
// in and audio out never touches a clock generator, a tick or a pin.
//
// This is the `axiLiteSlavePorts` / `regMapSlave` split applied to the codec:
// `i2sPins` declares the boundary in the io factory (where it must, since the
// boundary seals when that factory returns), `i2sLink` builds the machinery in
// the body and hands back streams. Everything the caller can get wrong by hand
// — which tick reaches which framer, which clock pin goes undriven, whether
// receive and transmit share one generator — is settled inside.

/// Which physical pin set a board presents. The two shapes are not a
/// preference: both appear in this repository with constraint files that bind
/// exactly these names, and a design must declare exactly what its `.xdc`
/// binds or it will not build for the board.
type I2sPinout =
    /// One clock bus shared by every chip on it, and no MCLK: `bclk`, `ws`,
    /// `sd_in`, `sd_out`. The shape a board takes when every device on the bus
    /// derives its timing from the bit and word clocks — a source that wants no
    /// master clock, and a sink that makes its own.
    | SharedBus
    /// Separate converters on separate connector rows, each with its own clock
    /// trio — the Pmod I2S2 shape: `mclk`/`sclk`/`lrclk` and `sdin` for the
    /// DAC, `mclk2`/`sclk2`/`lrclk2` and `sdout` for the ADC.
    | SeparateCodecs

/// The clock pins a pinout presents, as lists because *how many* is the thing
/// that varies: a shared bus has one of each and no MCLK at all, separate
/// converters have two of each. A list also makes "this board has no MCLK" an
/// empty list rather than a special case someone has to remember.
type I2sClockPins =
    { mclkPins: Output list
      sclkPins: Output list
      lrclkPins: Output list }

/// The pins of a transmit-only link — a design with a DAC and nothing to
/// listen to. `audioToneAxi` is the shape: four pins, and its `.xdc` binds
/// four.
type I2sTxPins =
    { txClocks: I2sClockPins
      dataOut: Output }

/// The pins of a duplex link: the same clocks, plus a line each way.
type I2sPins =
    { clocks: I2sClockPins
      txDataOut: Output
      dataIn: Input }

/// Declare a duplex link's pins. Call from a module's io factory.
///
/// **The declaration order is deliberate and is not cosmetic.** It reproduces
/// the order the hand-wired designs this replaces declare their ports in, so
/// converting one of them to this function is a byte-identical change to the
/// emitted Verilog rather than a header reshuffle that has to be read to be
/// dismissed. That is why the two pinouts spell their declarations out
/// separately instead of sharing a helper: the interleaving of the data pin
/// with the clock pins is the part that has to match.
let i2sPins (p: Ports) (pinout: I2sPinout) : I2sPins =
    match pinout with
    | SharedBus ->
        let sdIn = p.inPort "sd_in" 1
        let bclk = p.outPort "bclk" 1
        let ws = p.outPort "ws" 1
        let sdOut = p.outPort "sd_out" 1

        { clocks =
            { mclkPins = []
              sclkPins = [ bclk ]
              lrclkPins = [ ws ] }
          txDataOut = sdOut
          dataIn = sdIn }
    | SeparateCodecs ->
        let sdout = p.inPort "sdout" 1
        let mclk = p.outPort "mclk" 1
        let sclk = p.outPort "sclk" 1
        let lrclk = p.outPort "lrclk" 1
        let sdin = p.outPort "sdin" 1
        // The ADC is a second chip on a second connector row and needs the same
        // three clocks on its own pins. One generator drives both: a second
        // would drift against the first.
        let mclk2 = p.outPort "mclk2" 1
        let sclk2 = p.outPort "sclk2" 1
        let lrclk2 = p.outPort "lrclk2" 1

        { clocks =
            { mclkPins = [ mclk; mclk2 ]
              sclkPins = [ sclk; sclk2 ]
              lrclkPins = [ lrclk; lrclk2 ] }
          txDataOut = sdin
          dataIn = sdout }

/// Declare a transmit-only link's pins. Call from a module's io factory.
/// A transmit-only link's pins **under a prefix**, so a design can declare
/// more than one. An empty prefix is the bare names every design used before
/// there was a second port, and emits exactly those.
///
/// A second transmitter is not an exotic case: a recorder is one — the same
/// audio leaving on its own three wires, at its own slot width, to something
/// that is not the converter.
let i2sTxPinsNamed (p: Ports) (prefix: string) (pinout: I2sPinout) : I2sTxPins =
    let named (n: string) = if prefix = "" then n else $"{prefix}_{n}"

    match pinout with
    | SharedBus ->
        let bclk = p.outPort (named "bclk") 1
        let ws = p.outPort (named "ws") 1
        let sdOut = p.outPort (named "sd_out") 1

        { txClocks =
            { mclkPins = []
              sclkPins = [ bclk ]
              lrclkPins = [ ws ] }
          dataOut = sdOut }
    | SeparateCodecs ->
        let mclk = p.outPort (named "mclk") 1
        let sclk = p.outPort (named "sclk") 1
        let lrclk = p.outPort (named "lrclk") 1
        let sdin = p.outPort (named "sdin") 1

        { txClocks =
            { mclkPins = [ mclk ]
              sclkPins = [ sclk ]
              lrclkPins = [ lrclk ] }
          dataOut = sdin }

let i2sTxPins (p: Ports) (pinout: I2sPinout) : I2sTxPins =
    match pinout with
    | SharedBus ->
        let bclk = p.outPort "bclk" 1
        let ws = p.outPort "ws" 1
        let sdOut = p.outPort "sd_out" 1

        { txClocks =
            { mclkPins = []
              sclkPins = [ bclk ]
              lrclkPins = [ ws ] }
          dataOut = sdOut }
    | SeparateCodecs ->
        let mclk = p.outPort "mclk" 1
        let sclk = p.outPort "sclk" 1
        let lrclk = p.outPort "lrclk" 1
        let sdin = p.outPort "sdin" 1

        { txClocks =
            { mclkPins = [ mclk ]
              sclkPins = [ sclk ]
              lrclkPins = [ lrclk ] }
          dataOut = sdin }

/// A duplex link, as the body sees it: samples arriving, and somewhere to put
/// samples going out.
type I2sLink =
    { /// The stereo stream off the converter. `valid` pulses once per pair.
      input: Stream<Expr * Expr>
      /// Hand it the stream to transmit. Call exactly once — the data pin takes
      /// one driver, and never calling it leaves the pin undriven, which fails
      /// at emission.
      send: Stream<Expr * Expr> -> unit }

/// A transmit-only link.
type I2sTxLink =
    { sendOnly: Stream<Expr * Expr> -> unit }

let private driveClocks (pins: I2sClockPins) (m: I2sMasterPorts) =
    for pin in pins.mclkPins do
        m.mclk ==> pin

    for pin in pins.sclkPins do
        m.sclk ==> pin

    for pin in pins.lrclkPins do
        m.lrclk ==> pin

/// The whole front end: one clock generator at the board's rate, a receiver on
/// the sampling edge, a transmitter on the driving edge, every clock pin
/// driven.
///
/// **The two edge ticks never reach the caller**, which is the point. Wiring
/// `sclkTxTick` to the receiver is a bug that passes elaboration, passes every
/// stream check, and cannot be caught in simulation at all — it moves the
/// receiver from the edge where the line is stable to the edge where it
/// changes, and a zero-delay model has no opinion about that. Here it is not
/// expressible.
///
/// `prefix` names the instances (`{prefix}_clocks`, `{prefix}_rx`,
/// `{prefix}_tx`), so a design may hold more than one link.
///
/// **`fabricHz` is a frequency, not a `Board`.** A `Board` is a thing an
/// application holds; threading it through the hardware constructors would put
/// a record about *targets* into the signature of everything that divides a
/// clock, and a stage that needs a number should ask for the number. Callers
/// that have one write `kv260.fabricHz`, which reads as what it is.
let private checkSlotFor (name: string) (sentBits: int) (bitsPerSlot: int) =
    if bitsPerSlot < sentBits then
        failwith
            ($"i2sLink '{name}': bitsPerSlot is %d{bitsPerSlot} and the link sends %d{sentBits} bits — "
             + "a slot cannot be narrower than what goes in it.")

let private checkSlot (name: string) (bitsPerSlot: int) =
    if bitsPerSlot < sampleWidth then
        failwith
            ($"i2sLink '{name}': bitsPerSlot is %d{bitsPerSlot}, narrower than the %d{sampleWidth}-bit sample. "
             + "These are different numbers: the slot is how many bit-clock periods each channel occupies, "
             + "and the sample is how many of them carry data — the rest are zero padding. 32 is the usual slot.")

let i2sLink (prefix: string) (pins: I2sPins) (fabricHz: int) (targetFs: int) (bitsPerSlot: int) : I2sLink =
    checkSlot prefix bitsPerSlot
    let clocks = instanceNamed $"{prefix}_clocks" (i2sMasterHz fabricHz targetFs bitsPerSlot "I2sMaster")
    driveClocks pins.clocks clocks

    { input = i2sRx "I2sRx" $"{prefix}_rx" clocks.sclkRxTick clocks.lrclk pins.dataIn
      send = fun s -> i2sTx "I2sTx" $"{prefix}_tx" clocks.sclkTxTick clocks.lrclk s ==> pins.txDataOut }

/// The transmit half alone, for a design with nothing to listen to.
let i2sTxLink (prefix: string) (pins: I2sTxPins) (fabricHz: int) (targetFs: int) (bitsPerSlot: int) : I2sTxLink =
    checkSlot prefix bitsPerSlot
    let clocks = instanceNamed $"{prefix}_clocks" (i2sMasterHz fabricHz targetFs bitsPerSlot "I2sMaster")
    driveClocks pins.txClocks clocks

    { sendOnly = fun s -> i2sTx "I2sTx" $"{prefix}_tx" clocks.sclkTxTick clocks.lrclk s ==> pins.dataOut }

/// A transmit-only link that sends **`sentBits` of each sample** into slots
/// `bitsPerSlot` wide, at its own frame rate.
///
/// The duplex `i2sLink` deliberately refuses a slot narrower than the sample,
/// because there that is a confusion between two different numbers. Here it
/// is a choice: the sample is narrowed on purpose, to fit a receiver whose
/// bit clock will not go faster.
let i2sTxLinkAt
    (sentBits: int)
    (prefix: string)
    (pins: I2sTxPins)
    (fabricHz: int)
    (targetFs: int)
    (bitsPerSlot: int)
    : I2sTxLink =
    checkSlotFor prefix sentBits bitsPerSlot

    // The master's *module* name carries its slot width, because its contents
    // depend on it: a design with two links at different rates would otherwise
    // declare two different modules called `I2sMaster`, which the elaborator
    // refuses — correctly, and only once both exist.
    let clocks =
        instanceNamed $"{prefix}_clocks" (i2sMasterHz fabricHz targetFs bitsPerSlot $"I2sMaster%d{bitsPerSlot}")
    driveClocks pins.txClocks clocks

    { sendOnly = fun s -> i2sTxAt sentBits "I2sTxNarrow" $"{prefix}_tx" clocks.sclkTxTick clocks.lrclk s ==> pins.dataOut }

// ---------------------------------------------------------------------------
// Multiband compression. Generic DSP: an 8-band crossover feeding a compressor
// per band. Mastering, broadcast loudness and any per-band level prescription
// all want the same machine — what differs is only where the per-band makeup
// gains come from, and those arrive as register values.

/// How many bands the multiband compressor has. Eight, and stated here rather
/// than repeated: the crossover list, the register map and the per-band
/// compressors all size themselves from it.
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
/// original implementation so both stacks quantise a coefficient to the same bits.
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

/// One band's compressor with its law in a host-written table: detect on the raw
/// band, index the table with the envelope, and apply in the log domain what it
/// says. Returns the gained band and the envelope.
///
/// **Detection is on the raw band, not on a boosted copy.** The table returns
/// *total* gain, so there is no makeup multiply to come first — which is what
/// retires the boost-then-detect topology and its one global threshold, and it
/// makes `envelope` the band's own input level in a fixed calibration, which is
/// what a fitting meter wants to read.
///
/// `table` is how the caller's memory answers an address. It is a function
/// rather than a port because the memory belongs to whoever owns the register
/// map, and a `defModule` boundary cannot carry a read: the law is inline logic
/// for that reason, which is a definition-site choice no caller can see.
///
/// Four multiplies a band and a sample, the same four the formula cost: the
/// envelope step, the table's interpolation, the exponential's, and the apply.
let bandGainTable
    (multiply: Expr -> Expr -> Expr)
    (name: string)
    (toWidth: int)
    (table: Expr -> Expr)
    (attack: Expr)
    (releaseRate: Expr)
    (advance: Expr)
    (band: Expr)
    : Expr * Expr =
    let w = width band

    let negated = wire $"{name}_negated" (SInt w)
    sub (lit 0UL w) band ==> negated
    let absolute = wire $"{name}_absolute" w
    mux (slice (w - 1) (w - 1) band) negated band ==> absolute
    let peak = wire $"{name}_peak" sampleWidth
    saturate sampleWidth absolute ==> peak

    let env, _ = envelopeFollower peak attack releaseRate advance

    let index = gainTableIndex name env
    let word = wire $"{name}_word" gainTableWordWidth
    table index.address ==> word

    let gainLog = gainTableGain multiply $"{name}_curve" word index.fraction
    gainApply multiply $"{name}_apply" toWidth gainLog band, env

/// One band's compressor ports. The multiband stage instantiates one of these
/// per band and sums what comes back.
type MonoBandCompressorPorts =
    { /// This band's sample, signed and wider than a full-range sample —
      /// a crossover output can exceed the input it came from.
      band: Input
      /// High for one cycle per sample.
      advance: Input
      /// Low passes the band through with makeup gain and no compression.
      enable: Input
      /// The level above which gain reduction begins, at sample scale.
      threshold: Input
      /// Compression ratio above the threshold.
      ratio: Input
      /// How fast the envelope rises toward a louder signal.
      attack: Input
      /// How fast it falls back toward a quieter one.
      releaseRate: Input
      /// Per-band gain in Q8.8. This is the field a fitting prescription
      /// arrives in — the fabric knows nothing about where the number came
      /// from.
      makeup: Input
      /// The band after compression and makeup.
      gained: Output
      /// The detector's current level, for a host measuring what the band is
      /// actually doing.
      envelope: Output }

/// Mono single-band compressor — the per-band unit. The same envelope detector
/// and gain computer as `audioCompressor`, in the same Q formats and with the
/// same boost-first topology, but mono and emitting a wide unsaturated value
/// for the caller to sum.
///
/// With `ratio = 0` and unity makeup it is an exact pass-through, which is what
/// lets the filterbank still reconstruct its input through eight of these.
/// `envelope` is exposed for host diagnostics.
let monoBandCompressorDef (name: string) : TypedModule<MonoBandCompressorPorts> =
    let boostProductWidth = bandWidth + 17
    let gainWidth = sampleWidth + 1
    let applyProductWidth = gainedWidth + gainWidth + 1

    defModule
        name
        (fun p ->
            { band = p.inPortAs "band" (SInt bandWidth)
              advance = p.inPort "advance" 1
              enable = p.inPort "enable" 1
              threshold = p.inPort "threshold" sampleWidth
              ratio = p.inPort "ratio" 8
              attack = p.inPort "attack" 16
              releaseRate = p.inPort "releaseRate" 16
              makeup = p.inPort "makeup" 16
              gained = p.outPortAs "gained" (SInt gainedWidth)
              envelope = p.outPort "envelope" sampleWidth })
        (fun io ->
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
            let gainSigned = gainComputer mul envWide io.threshold io.ratio

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

/// One band unit under `instName`, called as a function: wire the band and its
/// controls, read the gained value and the envelope back.
let monoBandCompressor (name: string) instName =
    let io = (monoBandCompressorDef name).NewNamed instName

    fun (band: Expr) (advance: Expr) (enable: Expr) (threshold: Expr) (ratio: Expr) (attack: Expr) (releaseRate: Expr) (makeup: Expr) ->
        band ==> io.band
        advance ==> io.advance
        enable ==> io.enable
        threshold ==> io.threshold
        ratio ==> io.ratio
        attack ==> io.attack
        releaseRate ==> io.releaseRate
        makeup ==> io.makeup
        io.gained, io.envelope

/// One section of the crossover: a biquad that reads one node of the graph and
/// writes another. Node 0 is the ear's sample; every other node is an earlier
/// section's output.
type CrossoverSection =
    { input: int
      output: int
      /// Q2.30 `[b0; b1; b2; a1; a2]`, a high-pass's negation already folded in.
      coefficients: uint64 list
      /// What it is, for a reader: `lp 320`, `hp 320`, `ap 525`.
      role: string }

/// The crossover as a graph of sections, in an order every engine can run it
/// in: a section appears after every section it reads.
type CrossoverTree =
    { sections: CrossoverSection list
      /// The nodes that are the bands, low band first.
      bands: int list
      /// How many nodes there are, the sample included.
      nodes: int
      /// Sections on the path from the sample to any band — the same for
      /// every band, which is what keeps them time-aligned in a pipeline.
      depth: int }

/// The Linkwitz-Riley quality: two cascaded first-order sections, so a
/// low-pass and a high-pass at the same cutoff sum to an all-pass rather than
/// to a peak or a notch. It is the design's, not a caller's.
let linkwitzRileyQ = 0.5

/// A first-order all-pass at `fc`, as a section: what an LR2 low-pass and
/// high-pass at `fc` sum to, and therefore what the *other* branch of a split
/// has to pass through to stay in phase with this one.
let private allPassDesign (fc: float) (fs: float) : BiquadDesign =
    let t = tan (System.Math.PI * fc / fs)
    let c = (1.0 - t) / (1.0 + t)
    // (−c + z⁻¹) / (1 − c·z⁻¹): y = −c·x + x1 + c·y1
    { b0 = -c; b1 = 1.0; b2 = 0.0; a1 = -c; a2 = 0.0 }

/// The crossover: a balanced tree of second-order Linkwitz-Riley splits, each
/// branch passed through the all-passes of the splits in the *other* branch.
///
/// **Why a tree, and why the all-passes.** A subtractive split — band k as the
/// difference of two low-passes — reconstructs exactly and isolates terribly:
/// two low-passes with different cutoffs are not in phase below either, and
/// the difference of two near-unit vectors 12° apart is 20% of the signal,
/// so a 440 Hz tone came out of the 1.4 kHz band at a third of its level
/// (measured 2026-09-13). An LR2 low-pass and high-pass at one cutoff sum to
/// a first-order all-pass, so a split loses nothing; the tree then keeps the
/// two halves of every split in phase by sending each through the all-passes
/// of the other half's splits. The bands sum to an all-pass — flat in
/// magnitude, not bit-exact — and a tone leaks into a band two away at
/// −15 dB rather than −5.
///
/// For eight bands: seven splits, ten all-passes, and every band's path
/// exactly seven sections long. Sections are listed breadth-first, so a
/// section's input was produced as many beats before it as the tree allows.
let crossoverTree (crossovers: float list) (sampleRate: float) : CrossoverTree =
    let fc = List.toArray crossovers
    let mutable nextNode = 1
    let fresh () = let n = nextNode in nextNode <- nextNode + 1; n
    let quantise (d: BiquadDesign) = toQ230 d

    let lowPass k = quantise (rbjDesign LowPass fc[k] linkwitzRileyQ 0.0 sampleRate)

    let highPass k =
        let d = rbjDesign HighPass fc[k] linkwitzRileyQ 0.0 sampleRate
        quantise { d with b0 = -d.b0; b1 = -d.b1; b2 = -d.b2 }

    let allPass k = quantise (allPassDesign fc[k] sampleRate)

    // (level, section) pairs; `lo..hi` are the crossovers still to split on.
    let rec build (node: int) (level: int) (lo: int) (hi: int) : (int * CrossoverSection) list * (int * int) list =
        if lo > hi then
            [], [ node, level ]
        else
            let mid = (lo + hi) / 2
            let low = fresh ()
            let high = fresh ()

            let splits =
                [ level, { input = node; output = low; coefficients = lowPass mid; role = $"lp %.0f{fc[mid]}" }
                  level, { input = node; output = high; coefficients = highPass mid; role = $"hp %.0f{fc[mid]}" } ]

            let compensate (from: int) (through: int list) =
                ((from, level + 1, []), through)
                ||> List.fold (fun (node, level, acc) k ->
                    let out = fresh ()
                    out, level + 1, acc @ [ level, { input = node; output = out; coefficients = allPass k; role = $"ap %.0f{fc[k]}" } ])

            let lowNode, lowLevel, lowComp = compensate low [ mid + 1 .. hi ]
            let highNode, highLevel, highComp = compensate high [ lo .. mid - 1 ]
            let lowSections, lowLeaves = build lowNode lowLevel lo (mid - 1)
            let highSections, highLeaves = build highNode highLevel (mid + 1) hi
            splits @ lowComp @ highComp @ lowSections @ highSections, lowLeaves @ highLeaves

    let sections, leaves = build 0 0 0 (fc.Length - 1)
    let depths = leaves |> List.map snd |> List.distinct

    if List.length depths <> 1 then
        failwith $"crossover tree: bands at different depths %A{depths} — a balanced count of bands is required"

    { sections = sections |> List.sortBy fst |> List.map snd
      bands = leaves |> List.map fst
      nodes = nextNode
      depth = List.head depths }

/// The eight-band compressor's ports. One crossover feeding one compressor per
/// band, with independent left and right makeup gains.
type MultibandCompressorPorts =
    { /// The stereo stream through the whole bank.
      s: StereoPorts
      /// The level above which gain reduction begins, shared by every band.
      threshold: Input
      /// Compression ratio, shared by every band.
      ratio: Input
      /// Envelope attack rate, shared by every band.
      attack: Input
      /// Envelope release rate, shared by every band.
      releaseRate: Input
      /// Per-band makeup gain for the left channel, Q8.8, low band first.
      leftGains: Input list
      /// The same for the right channel. Separate because the two ears are
      /// not the same ear.
      rightGains: Input list
      /// The summed output's envelope, for a host measuring the result.
      envelope: Output }

/// 8-band stereo multiband compressor.
///
/// Each ear goes through the crossover tree — seven Linkwitz-Riley splits and
/// their all-pass compensation, one section per pipeline level, so every band
/// arrives at the same position — then each band gets its own compressor,
/// and the eight are summed and saturated once. The bands sum to an all-pass:
/// unity makeup and zero ratio return the signal at its level, phase-shifted,
/// not bit for bit.
///
/// Per-band makeup gains are the whole point of the shape: they are Q8.8
/// register values, so what the module is *for* — mastering, broadcast
/// loudness, any per-band prescription — lives in whatever host writes them,
/// not in the fabric.
///
/// `threshold` / `ratio` / `attack` / `releaseRate` are shared across bands; a
/// global threshold is meaningful here only because each band compresses its
/// own post-makeup level (see `monoBandCompressor`). `envelope` reports the
/// loudest band detector across both ears, for host metering.
let multibandCompressor8Def (name: string) (crossovers: float list) (sampleRate: float) : TypedModule<MultibandCompressorPorts> =
    if List.length crossovers <> multibandBands - 1 then
        failwith $"multibandCompressor8 needs {multibandBands - 1} crossovers, got {List.length crossovers}"

    let tree = crossoverTree crossovers sampleRate
    let section = biquadSection $"{name}_biquad"
    let compressor = monoBandCompressor $"{name}_band"
    let sumWidth = gainedWidth + 3

    defModule
        name
        (fun p ->
            { s = stereoPorts p
              threshold = p.inPort "threshold" sampleWidth
              ratio = p.inPort "ratio" 8
              attack = p.inPort "attack" 16
              releaseRate = p.inPort "releaseRate" 16
              leftGains = List.init multibandBands (fun i -> p.inPort $"leftGain{i}" 16)
              rightGains = List.init multibandBands (fun i -> p.inPort $"rightGain{i}" 16)
              envelope = p.outPort "envelope" sampleWidth })
        (fun io ->
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

            // Positions 0..depth: the beat in the input register is at 0, and
            // each level of the crossover tree moves it one on. A section at
            // level l may touch its history only when the beat at l is real;
            // the compressors sit at `depth`, where every band arrives at once.
            let validAt =
                (inValid, [ 1 .. tree.depth ])
                ||> List.scan (fun previous l ->
                    let r = regBit $"valid_%d{l}"
                    If enable (fun () -> previous ==> r)
                    r)

            let advanceAt =
                validAt
                |> List.mapi (fun l v ->
                    let a = wireBit $"advance_%d{l}"
                    (v &&& enable) ==> a
                    a)

            let envelopes = ResizeArray<Expr>()

            let ear earName x (gains: Expr list) =
                // The tree, one register after every section: node 0 is the
                // input register, and a section at level l reads a node that
                // was registered at level l − 1.
                let nodes = Array.create tree.nodes x
                let levelOf = Array.create tree.nodes 0

                for i, sec in List.indexed tree.sections do
                    let cs = sec.coefficients

                    // Annotated because BiquadDesign shares these field
                    // names — one carries reals for the host, the other
                    // nets for the fabric.
                    let coeffs: BiquadCoeffs =
                        { b0 = lit cs[0] biquadCoeffWidth
                          b1 = lit cs[1] biquadCoeffWidth
                          b2 = lit cs[2] biquadCoeffWidth
                          a1 = lit cs[3] biquadCoeffWidth
                          a2 = lit cs[4] biquadCoeffWidth }

                    let level = levelOf[sec.input]
                    let y = section $"{earName}_s{i}" nodes[sec.input] advanceAt[level] coeffs
                    let held = reg $"{earName}_node{sec.output}" sampleWidth
                    If enable (fun () -> y ==> held)
                    nodes[sec.output] <- held
                    levelOf[sec.output] <- level + 1

                let band k =
                    let b = wire $"{earName}_band{k}" bandWidth
                    signExtend bandWidth nodes[tree.bands[k]] ==> b
                    b

                let gained =
                    List.init multibandBands (fun k ->
                        let value, envelope =
                            compressor
                                $"{earName}_band{k}_comp"
                                (band k)
                                advanceAt[tree.depth]
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
                saturated, tree.depth + compressorLatency + treeLatency

            let outLeft, earLatency = ear "left" inLeft io.leftGains
            let outRight, _ = ear "right" inRight io.rightGains

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
            // There is no bypass copy alongside it. A raw passthrough would be
            // a second data path, `latency` deep and two samples wide per ear,
            // kept so that flipping bypass would not move the signal in time.
            // Unity settings pass the signal at its level — as an all-pass
            // since the crossover became a tree, bit-exact before — which is
            // what a bypass is for. See `audio: unity settings pass audio
            // through`.
            // Positions depth+1..earLatency, continuing from the tree's last
            // level. The output's valid is the last link — the very same bit
            // that gated the filterbank, arriving with the result it describes.
            let validAtDepth = List.last validAt
            let validRest = List.init (earLatency - tree.depth) (fun i -> regBit $"v{i}")

            // These shift on `enable`, with the data pipeline they describe:
            // the tree's node registers and the adder tree all move on the
            // same bit, so a bubble travels through as `valid` low and the
            // tail drains. Only the *recurrences* — the filterbank history and
            // the compressor envelopes — gate on `advance`, because state may
            // move only on a real beat.
            If enable (fun () ->
                validRest
                |> List.iteri (fun i r -> (if i = 0 then validAtDepth else validRest[i - 1]) ==> r))

            (if List.isEmpty validRest then validAtDepth else List.last validRest) ==> io.s.outValid
            outLeft ==> io.s.outLeft
            outRight ==> io.s.outRight)

/// One bank under `instName`, called as a function: wire the shared dynamics
/// and the per-band gains, splice the stream through, and hand the metering
/// envelope back beside it.
/// What a multiband compressor is set to: one dynamics setting shared by every
/// band, and a per-band makeup gain for each channel.
///
/// Six values that used to be six positional arguments — four adjacent `Expr`s
/// and then **two adjacent `Expr list`s**, where swapping the last pair swaps
/// the channels and leaves a design that still compresses, still passes every
/// width check, and puts the left ear's band gains on the right ear.
type MultibandSettings =
    { threshold: Expr
      ratio: Expr
      attack: Expr
      releaseRate: Expr
      leftGains: Expr list
      rightGains: Expr list }

/// The settings as a module's own ports, in the order the designs that
/// declared them by hand used — so converting one is byte-identical and a host
/// finds `lg0`..`rg7` exactly where it did.
let multibandSettingsPorts (p: Ports) : MultibandSettings =
    { threshold = p.inPort "threshold" sampleWidth
      ratio = p.inPort "ratio" 8
      attack = p.inPort "attack" 16
      releaseRate = p.inPort "releaseRate" 16
      leftGains = List.init multibandBands (fun i -> p.inPort $"lg{i}" 16)
      rightGains = List.init multibandBands (fun i -> p.inPort $"rg{i}" 16) }

/// Width of a makeup gain: Q8.8, unity at `gainUnity`.
let makeupWidth = 16

/// The folded bank's window onto its makeup gains: a read port it asks by
/// word — `ear * 8 + band`, left ear first, low band first — and that may be
/// borrowed for a cycle (`hostTurn`) while a host reads the table back. A
/// register map's `RwArray` hands one out as `slave.readArray entry`; a design
/// with no host builds one with `multibandFoldedSettingsPorts`. Sixteen words
/// in a memory rather than sixteen registers because a table costs a block
/// where a register file costs the fabric — on an iCE40 the difference is the
/// fit — and because everything the fitting side will add to a band later is
/// another word.
type GainTable = Expr -> HostArrayPort

/// The folded bank's settings: the spatial engine's law, and the makeup gains
/// as a table rather than sixteen wires — the one place the two engines'
/// surfaces differ, since a bank that runs its bands one at a time reads one
/// gain at a time, and one that runs them at once needs them all.
type MultibandFoldedSettings =
    { attack: Expr
      releaseRate: Expr
      /// The per-band gain curves, as one table the bank reads by
      /// `ear ++ band ++ entry`. **This replaces the threshold and the ratio as
      /// well as the makeup gains** — the curve says what the total gain is at
      /// every level, so there is nothing left for a knob to mean.
      curve: GainTable }

/// The bank's side of its gain table: a request stream of word indices out, a
/// flow of curve words back.
type CurveLookupPorts =
    { request: StreamOutputPorts<Expr>
      answer: FlowInputPorts<Expr> }

/// Bits the curve's index takes: the ear, the band, and where in that band's
/// curve the envelope reads.
let curveIndexBits = 1 + ceilLog2 multibandBands + gainTableAddrBits

let private curveLookupPorts (p: Ports) : CurveLookupPorts =
    { request = streamOutputPorts p "curve_request" (layout1 ("index", curveIndexBits))
      answer = flowInputPorts p "curve_answer" (layout1 ("word", gainTableWordWidth)) }

/// The folded bank's ports: the stereo stream, the detector's two coefficients,
/// and the curve lookup — which is all the law there is, since the curve carries
/// what a threshold and a ratio used to say.
type MultibandFoldedPorts =
    { s: StereoPorts
      attack: Input
      releaseRate: Input
      /// The bank's side of its gain table.
      curve: CurveLookupPorts
      envelope: Output }

/// The folded settings as a module's own ports, for a design with no host: the
/// detector's two, and three that load the curve — `curve_index`, `curve_word`,
/// `curve_write` — the way a host loads the window.
type MultibandFoldedSettingsPorts =
    { attack: Input
      releaseRate: Input
      curveIndex: Input
      curveWord: Input
      curveWrite: Input }

let multibandFoldedSettingsPorts (p: Ports) : MultibandFoldedSettingsPorts =
    { attack = p.inPort "attack" 16
      releaseRate = p.inPort "releaseRate" 16
      curveIndex = p.inPort "curve_index" curveIndexBits
      curveWord = p.inPort "curve_word" gainTableWordWidth
      curveWrite = p.inPort "curve_write" 1 }

/// The body half of `multibandFoldedSettingsPorts`: the curve itself, in block
/// storage, written from the load ports.
///
/// **A zeroed table is unity gain**, which is what makes the boot state
/// transparent for nothing: a word of zero is a gain of zero log2, and two to the
/// zero is one. The old makeup table had to be preloaded with `gainUnity` to say
/// the same thing.
let multibandFoldedSettingsOf (ports: MultibandFoldedSettingsPorts) : MultibandFoldedSettings =
    let store =
        preloadedBlockMem
            "curve"
            curveIndexBits
            gainTableWordWidth
            (Array.zeroCreate (1 <<< curveIndexBits))

    memWrite store ports.curveIndex ports.curveWord ports.curveWrite

    { attack = ports.attack
      releaseRate = ports.releaseRate
      curve = fun at -> { read = memReadPort store at; hostTurn = lit 0UL 1 } }

/// Wire one bank's settings and splice the stream through it — the call shape
/// both engines share, so a caller cannot tell which it has.
let private multibandInstance (io: MultibandCompressorPorts) =
    fun (settings: MultibandSettings) (s: Stream<Expr * Expr>) ->
        settings.threshold ==> io.threshold
        settings.ratio ==> io.ratio
        settings.attack ==> io.attack
        settings.releaseRate ==> io.releaseRate
        List.iter2 (fun port g -> g ==> port) io.leftGains settings.leftGains
        List.iter2 (fun port g -> g ==> port) io.rightGains settings.rightGains
        stereoSplice io.s s, io.envelope

let multibandCompressor8 (name: string) (crossovers: float list) (sampleRate: float) instName =
    multibandInstance ((multibandCompressor8Def name crossovers sampleRate).NewNamed instName)

/// The stock 8-band compressor: the default crossovers, at **the rate the
/// design actually runs**.
///
/// The sample rate is an argument rather than a constant because a filter's
/// coefficients fix a frequency in cycles per *sample*, so designing at one
/// rate and running at another moves every crossover by the ratio between them
/// — coefficients designed for 320 Hz at 48 000 are the same coefficients as
/// 325.5 Hz at 48 828.125, the way a tape played fast is sharp.
///
/// This used to be `48_000.0`, which was wrong by +1.73 % on the one board it
/// ran on. Substituting that board's 48 828.125 would have been a different
/// bug: this is generic stdlib, and a rate is a fact about a target, so it is
/// asked for rather than assumed. `sampleRateOf` computes it from the divisors
/// if the caller has those instead.
///
/// **The error it replaced was nearly harmless, and the reason is worth
/// keeping**: every crossover came from the same constant, so all seven moved
/// by the same ratio, and a subtractive crossover's reconstruction depends on
/// the filters' relationship to each other rather than their absolute
/// placement. A uniformly wrong rate costs almost nothing; a *mixed* rate —
/// one filter redesigned and the rest left alone — puts a real dip at that
/// crossover. If this ever gains a per-band override, that is the trap.
let multibandCompressor name (sampleRate: float) =
    multibandCompressor8 name defaultCrossovers sampleRate

// ---------------------------------------------------------------------------
// The folded multiband compressor: the same DSP, as the spatial engine's own
// operations chained one after another over one shared multiplier.
//
// `multibandCompressor8` holds every multiply in hardware at once — 588 DSP
// blocks on the KV260, and 32 on a part that has 8. Here every operation that
// multiplies is a stage holding one beat, and the multiplier is shared behind
// them through `warpFu`. The bits are the spatial engine's by construction —
// every multiply issued at the widths the spatial engine uses, each stage
// rescaling its product exactly as the spatial one did, the biquad's five
// products summed serially in the reference's own accumulator — and by
// measurement: the equivalence is a living check, not an argument.

// ---------------------------------------------------------------------------
// The shared multiplier, and the three kinds of one-beat stage the folded
// engine is assembled from.

/// The multiplier's operand widths: the widest pair any operation presents.
let private podAWidth = gainedWidth
let private podBWidth = biquadCoeffWidth
let private podProductWidth = podAWidth + podBWidth

/// One client's end of the shared multiplier: the client drives `a`, `b` and
/// `issue`; the pod answers `grant` the cycle it takes them, and `landed` with
/// the `product` some cycles later. The product's low bits are the client's
/// own product exactly, because a product of its operands fits their widths.
type private PodClient =
    { a: Expr
      b: Expr
      /// Rides with the operands and comes back with the product, untouched.
      tag: Expr
      issue: Expr
      grant: Expr
      product: Expr
      tagBack: Expr
      landed: Expr }

let private podTagWidth = 2

/// One multiplier for every operation that needs one. Each stage asks for a
/// client; `Finish` puts them all behind `warpFu` — a round-robin arbiter, the
/// registered multiply as the core, results routed back to their issuer. No
/// client holds more than it can take back: a stage keeps one beat in flight,
/// so a result always has somewhere to land.
type private SharedMultiplier(name: string) =
    let clients = ResizeArray<PodClient>()

    member _.Client(client: string) : PodClient =
        let c =
            { a = wire $"{client}_pod_a" (SInt podAWidth)
              b = wire $"{client}_pod_b" (SInt podBWidth)
              tag = wire $"{client}_pod_tag" podTagWidth
              issue = wireBit $"{client}_pod_issue"
              grant = wireBit $"{client}_pod_grant"
              product = wire $"{client}_pod_product" (SInt podProductWidth)
              tagBack = wire $"{client}_pod_tag_back" podTagWidth
              landed = wireBit $"{client}_pod_landed" }

        clients.Add c
        c

    member _.Finish() =
        let issueLayout = fuLayout podTagWidth [ "a", podAWidth; "b", podBWidth ]

        let issues =
            [ for c in clients ->
                  ({ payload = { tag = c.tag; fields = [ c.a; c.b ] }
                     valid = c.issue
                     ready = c.grant
                     layout = issueLayout }
                   : Stream<FuBeat>) ]

        // Operands in registers, the product in a register — both of which the
        // DSP block absorbs. The depth is the construction's, reported to
        // warpFu rather than declared to it.
        let core (operands: Expr list) =
            match operands with
            | [ a; b ] ->
                let heldA = reg $"{name}_a" (SInt podAWidth)
                a ==> heldA
                let heldB = reg $"{name}_b" (SInt podBWidth)
                b ==> heldB
                let product = reg $"{name}_product_held" (SInt podProductWidth)
                mul heldA heldB ==> product
                [ product ], 2
            | _ -> failwith $"'{name}' multiplies two operands"

        // The registered arbiter: the pick over five clients was 25 ns of an
        // iCE40 cycle before the operand mux even started. Lowest client
        // first, and the crossover asks for its client first: its taps are the
        // pass's critical path and issue back to back, while a compressor
        // step can wait the few cycles a section takes.
        let results = warpFuPriority name [ "product", podProductWidth ] core issues

        for c, r in Seq.zip clients results do
            r.payload.fields.Head ==> c.product
            r.payload.tag ==> c.tagBack
            r.valid ==> c.landed
            lit 1UL 1 ==> r.ready

/// A layout's `unpack` hands a field back in its reading (`asSInt` over the
/// net); to drive the net itself, take the reading off again.
let rec private bare (e: Expr) =
    match e with
    | AsSInt v
    | AsUInt v -> bare v
    | v -> v

/// `streamFsm` with one more transition: a stage whose offer is taken this
/// cycle accepts the next beat this cycle too, straight back into `Working`,
/// rather than spending a cycle in `Accepting` first. A chain of one-beat
/// stages then moves a beat per stage-cycle instead of per stage-cycle plus
/// one, which across the crossover's forty-eight sections a sample is what
/// keeps the pass inside the frame.
let private workerFsm (name: string) (input: Stream<'p>) (outputLayout: Layout<'q>) : Machine<StreamWorkerState> * Stream<'q> =
    let st = machine $"{name}_state" [ Accepting; Working; Offering ]
    let ready = wireBit $"{name}_ready"
    registerStreamReady ready

    let taken = st.Is Offering &&& ready
    (st.Is Accepting ||| taken) ==> input.ready

    ifElse
        [ (input.valid &&& input.ready, fun () -> st.Goto Working)
          (taken, fun () -> st.Goto Accepting) ]

    let payloadWires = [ for n, f in outputLayout.fields -> wire $"{name}_out_{n}" f.totalWidth ]

    st,
    { payload = outputLayout.unpack payloadWires
      valid = st.Is Offering
      ready = ready
      layout = outputLayout }

/// Two beats of buffering whose `ready` is a function of its own state alone.
/// With the skid, a stage's `ready` reaches back through the stage behind it,
/// and through the one behind that: a chain of one-beat stages is one long
/// combinational path from the last stage's consumer to the first stage's
/// producer — fourteen stages and 46 ns here, measured. One of these every
/// few stages ends the chain, at the cost of two copies of the beat in
/// registers rather than LUTRAM, which an iCE40 does not have.
let private skidBuffer (name: string) (layout: Layout<'p>) (s: Stream<'p>) : Stream<'p> =
    let ready = wireBit $"{name}_ready"
    registerStreamReady ready

    let head = [ for n, f in layout.fields -> reg $"{name}_head_{n}" f.totalWidth ]
    let tail = [ for n, f in layout.fields -> reg $"{name}_tail_{n}" f.totalWidth ]
    let headValid = regBit $"{name}_head_valid"
    let tailValid = regBit $"{name}_tail_valid"

    bnot tailValid ==> s.ready
    let push = s.valid &&& s.ready
    let pop = headValid &&& ready
    let incoming = layout.pack s.payload
    let load regs values = List.iter2 (fun r v -> v ==> r) regs values

    ifElse
        [ (pop &&& tailValid,
           fun () ->
               load head tail
               lit 1UL 1 ==> headValid
               If push (fun () -> load tail incoming)
               push ==> tailValid)
          (pop,
           fun () ->
               If push (fun () -> load head incoming)
               push ==> headValid)
          (push &&& headValid,
           fun () ->
               load tail incoming
               lit 1UL 1 ==> tailValid)
          (push,
           fun () ->
               load head incoming
               lit 1UL 1 ==> headValid) ]

    ({ payload = layout.unpack head
       valid = headValid
       ready = ready
       layout = layout }
     : Stream<'p>)

/// Hold the beat a stage accepts, in registers named for the stage.
let private holdBeat (name: string) (layout: Layout<'p>) (accept: Expr) (s: Stream<'p>) : 'p =
    let held = [ for n, f in layout.fields -> reg $"{name}_{n}" f.totalWidth ]
    If accept (fun () -> List.iter2 (fun r v -> v ==> r) held (layout.pack s.payload))
    layout.unpack held

/// Drive a stage's output payload from what it holds. The value is a function
/// of the stage's own registers — the beat it holds and what its work put in
/// registers — so it is stable for as long as the stage offers it, and the
/// stage keeps one copy of the beat rather than two.
let private offer (layout: Layout<'q>) (out: Stream<'q>) (value: 'q) =
    List.iter2 (fun v r -> r ==> bare v) (layout.pack out.payload) (layout.pack value)

/// A stage that reads words from one store for each beat: the addresses the
/// beat names, one a cycle, and the beat handed on with the words attached.
/// A stage that reads words for each beat — one a cycle — **each from its own
/// store**. One store for every address is the common case and has its own
/// entry below; several exist because two fields of one band's state live in
/// separate memories, so that the stages writing them cannot collide on a
/// single write port.
let private readStageFrom
    (name: string)
    (inLayout: Layout<'p>)
    (outLayout: Layout<'q>)
    (sources: 'p -> (Mem * Expr) list)
    (attach: 'p -> Expr list -> 'q)
    (s: Stream<'p>)
    : Stream<'q> =
    let st, out = workerFsm name s outLayout
    let accept = s.valid &&& s.ready
    let beat = holdBeat name inLayout accept s
    let pairs = sources beat
    let count = List.length pairs

    let issuing = regBit $"{name}_issuing"
    If accept (fun () -> lit 1UL 1 ==> issuing)
    let which = counter $"{name}_which" count (st.Is Working &&& issuing)
    If which.wrap (fun () -> lit 0UL 1 ==> issuing)

    // Each word lands its port's depth after its address, with its index. A
    // port per store: the reads are free to happen together, and the cycle
    // `which` is on picks whose word lands.
    let reads = [ for store, addr in pairs -> memReadPort store addr ]
    let first = List.head reads
    let arrived = first.through $"{name}_arrived" (st.Is Working &&& issuing)
    let arrivedIndex = first.through $"{name}_arrived_index" which.count

    // Each word at **its own store's** width. Sizing them all from the first
    // store's was right only while every store happened to be the same width,
    // and wrong silently: a narrower store's word drove a wider register and the
    // mismatch surfaced at emission, a long way from here.
    let words = [ for i, r in List.indexed reads -> reg $"{name}_word%d{i}" (width r.data) ]

    If arrived (fun () ->
        ifElse
            [ for i, (w, r) in List.indexed (List.zip words reads) ->
                  (eq arrivedIndex (lit (uint64 i) (width arrivedIndex)), fun () -> r.data ==> w) ])

    // Offer from the edge the last word lands on: the word register and the
    // state move together.
    If (st.Is Working &&& arrived &&& eq arrivedIndex (lit (uint64 (count - 1)) (width arrivedIndex))) (fun () ->
        st.Goto Offering)

    offer outLayout out (attach beat words)
    out

/// The common case: every word read from the same store.
let private readStage
    (name: string)
    (store: Mem)
    (inLayout: Layout<'p>)
    (outLayout: Layout<'q>)
    (addresses: 'p -> Expr list)
    (attach: 'p -> Expr list -> 'q)
    (s: Stream<'p>)
    : Stream<'q> =
    readStageFrom name inLayout outLayout (fun b -> [ for a in addresses b -> store, a ]) attach s

/// A stage that writes words to one store for each beat — one a cycle, from
/// the beat — and hands the beat on unchanged.
let private writeStage (name: string) (store: Mem) (layout: Layout<'p>) (writes: 'p -> (Expr * Expr) list) (s: Stream<'p>) : Stream<'p> =
    let st, out = workerFsm name s layout
    let accept = s.valid &&& s.ready
    let beat = holdBeat name layout accept s
    let pending = writes beat

    let writing = regBit $"{name}_writing"
    If accept (fun () -> lit 1UL 1 ==> writing)
    let which = counter $"{name}_which" (List.length pending) (st.Is Working &&& writing)

    memWrite
        store
        (selectIndexed which.count (List.map fst pending))
        (selectIndexed which.count (List.map snd pending))
        (st.Is Working &&& writing)

    If which.wrap (fun () ->
        lit 0UL 1 ==> writing
        st.Goto Offering)

    offer layout out beat
    out

/// A write that costs no beat: the word goes to the store as the beat
/// transfers, and the stream is handed on as it was.
let private writeThrough (store: Mem) (write: 'p -> Expr * Expr) (s: Stream<'p>) : Stream<'p> =
    let addr, data = write s.payload
    memWrite store addr data (s.valid &&& s.ready)
    s

/// A stage whose work is one multiply on the shared unit: the operands from
/// the beat (declared signals, any width up to the pod's), and the beat
/// handed on as `finish product beat` when the product lands. `productWidth`
/// is how much of the product the finish uses — the low bits — and is what
/// the stage keeps: the pod's product is the widest pair's, and a register
/// as wide as that at every stage was a hundred flops on a part that had
/// none to spare.
let private podStage
    (name: string)
    (pod: SharedMultiplier)
    (inLayout: Layout<'p>)
    (outLayout: Layout<'q>)
    (productWidth: int)
    (operands: 'p -> Expr * Expr)
    (finish: Expr -> 'p -> 'q)
    (s: Stream<'p>)
    : Stream<'q> =
    let st, out = workerFsm name s outLayout
    let accept = s.valid &&& s.ready
    let beat = holdBeat name inLayout accept s
    let client = pod.Client name

    let a, b = operands beat
    signExtend podAWidth a ==> client.a
    signExtend podBWidth b ==> client.b
    lit 0UL podTagWidth ==> client.tag

    // The request is a register, not a decode of the state: it heads the
    // arbiter's path into the multiplier, which is the engine's longest.
    let issued = regBit $"{name}_issued"
    If accept (fun () -> lit 0UL 1 ==> issued)
    let request = regBit $"{name}_request"
    (accept ||| (st.Is Working &&& bnot issued &&& bnot client.grant)) ==> request
    request ==> client.issue
    If (client.issue &&& client.grant) (fun () -> lit 1UL 1 ==> issued)

    // The product is held here — the pod's own register is every client's —
    // and the output is finished from it and the held beat.
    let product = reg $"{name}_product_held" (SInt productWidth)

    If (st.Is Working &&& client.landed) (fun () ->
        slice (productWidth - 1) 0 client.product ==> product
        st.Goto Offering)

    offer outLayout out (finish product beat)
    out

// ---------------------------------------------------------------------------
// The beats between the stages. Each carries what the next stage needs and
// no more; a stage's registers are its beat's fields.

/// One crossover section of one ear: which section, which node it reads and
/// which it writes, whether its output is a band and which, the sample's
/// parity (for the node scoreboard), and — once `readNode` has run — the
/// value it filters, in `x`. Before that `x` carries the ear's sample, which
/// is what a section reading node 0 filters.
type private Section =
    { ear: Expr
      section: Expr
      input: Expr
      output: Expr
      leaf: Expr
      band: Expr
      /// Whether the section is first order — an all-pass — with three
      /// non-zero taps rather than five.
      short: Expr
      parity: Expr
      x: Expr }

/// A section filtered: its output.
type private SectionDone = { section: Section; y: Expr }

/// One band of one ear, from the crossover.
type private Band = { ear: Expr; band: Expr; value: Expr }

/// A band with its compressor's state read: the envelope, and last sample's
/// boosted value — the detector's input.
type private BandState =
    { ear: Expr
      band: Expr
      value: Expr
      env: Expr
      detected: Expr }

/// A band with its detector's peak: the magnitude of last sample's band, at
/// sample scale. **Last sample's band, not a boosted copy of it** — the table
/// returns total gain, so there is no makeup multiply for the detector to sit
/// behind, and `env` is the band's own input level in a fixed calibration.
type private Detected =
    { ear: Expr
      band: Expr
      env: Expr
      value: Expr
      peak: Expr }

/// A band whose envelope has stepped — `env` is still the value before the
/// step, which is what the gain reads.
type private Stepped =
    { ear: Expr
      band: Expr
      env: Expr
      value: Expr
      envNext: Expr }

/// A band with its curve's word fetched, and how far past that entry its
/// envelope fell.
type private Curved =
    { ear: Expr
      band: Expr
      value: Expr
      word: Expr
      fraction: Expr
      envNext: Expr }

/// A band with its gain interpolated, in the log domain.
type private Gain =
    { ear: Expr
      band: Expr
      value: Expr
      gainLog: Expr
      envNext: Expr }

/// A band with its gain's mantissa — the exponential done, the shift still to
/// come, which is what the apply's own multiply is followed by.
type private Mantissa =
    { ear: Expr
      band: Expr
      value: Expr
      gainLog: Expr
      mantissa: Expr
      envNext: Expr }

/// A band compressed, ready to sum.
type private Gained =
    { ear: Expr
      band: Expr
      gained: Expr
      envNext: Expr }

/// A section's history: x and y, two slots each.
let private historyTaps = 4
let private biquadIssues = 5
let private slotBits = ceilLog2 multibandBands
let private historyTapBits = ceilLog2 historyTaps
let private stateFieldBits = 1

/// The tree's dimensions, from the tree.
type private TreeShape =
    { tree: CrossoverTree
      sectionBits: int
      nodeBits: int }

let private treeShape (tree: CrossoverTree) : TreeShape =
    { tree = tree
      sectionBits = ceilLog2 (List.length tree.sections)
      nodeBits = ceilLog2 tree.nodes }

let private sectionFields (t: TreeShape) =
    [ "ear", 1
      "section", t.sectionBits
      "input", t.nodeBits
      "output", t.nodeBits
      "leaf", 1
      "band", slotBits
      "short", 1
      "parity", 1
      "x", sampleWidth ]

let private packSection (s: Section) = [ s.ear; s.section; s.input; s.output; s.leaf; s.band; s.short; s.parity; s.x ]

let private unpackSection (nets: Expr list) : Section * Expr list =
    match nets with
    | ear :: section :: input :: output :: leaf :: band :: short :: parity :: x :: rest ->
        { ear = ear
          section = section
          input = input
          output = output
          leaf = leaf
          band = band
          short = short
          parity = parity
          x = asSInt x },
        rest
    | _ -> failwith "section: wrong arity"

let private sectionLayout (t: TreeShape) : Layout<Section> =
    { fields = fieldsOfWidths (sectionFields t)
      pack = packSection
      unpack = unpackSection >> fst }

let private sectionDoneLayout (t: TreeShape) : Layout<SectionDone> =
    { fields = fieldsOfWidths (sectionFields t @ [ "y", sampleWidth ])
      pack = fun s -> packSection s.section @ [ s.y ]
      unpack =
        fun nets ->
            match unpackSection nets with
            | section, [ y ] -> { section = section; y = asSInt y }
            | _ -> failwith "section done: wrong arity" }

let private bandLayout: Layout<Band> =
    { fields = fieldsOfWidths [ "ear", 1; "band", slotBits; "value", bandWidth ]
      pack = fun b -> [ b.ear; b.band; b.value ]
      unpack =
        function
        | [ ear; band; value ] -> { ear = ear; band = band; value = asSInt value }
        | _ -> failwith "band: wrong arity" }

let private bandStateLayout: Layout<BandState> =
    { fields = fieldsOfWidths [ "ear", 1; "band", slotBits; "value", bandWidth; "env", sampleWidth; "detected", bandWidth ]
      pack = fun b -> [ b.ear; b.band; b.value; b.env; b.detected ]
      unpack =
        function
        | [ ear; band; value; env; detected ] ->
            { ear = ear
              band = band
              value = asSInt value
              env = env
              detected = asSInt detected }
        | _ -> failwith "band state: wrong arity" }

let private detectedLayout: Layout<Detected> =
    { fields = fieldsOfWidths [ "ear", 1; "band", slotBits; "env", sampleWidth; "value", bandWidth; "peak", sampleWidth ]
      pack = fun b -> [ b.ear; b.band; b.env; b.value; b.peak ]
      unpack =
        function
        | [ ear; band; env; value; peak ] ->
            { ear = ear
              band = band
              env = env
              value = asSInt value
              peak = peak }
        | _ -> failwith "detected: wrong arity" }

let private steppedLayout: Layout<Stepped> =
    { fields = fieldsOfWidths [ "ear", 1; "band", slotBits; "env", sampleWidth; "value", bandWidth; "env_next", sampleWidth ]
      pack = fun b -> [ b.ear; b.band; b.env; b.value; b.envNext ]
      unpack =
        function
        | [ ear; band; env; value; envNext ] ->
            { ear = ear
              band = band
              env = env
              value = asSInt value
              envNext = envNext }
        | _ -> failwith "stepped: wrong arity" }

let private curvedLayout: Layout<Curved> =
    { fields =
        fieldsOfWidths
            [ "ear", 1
              "band", slotBits
              "value", bandWidth
              "word", gainTableWordWidth
              "fraction", gainTableFracBits
              "env_next", sampleWidth ]
      pack = fun b -> [ b.ear; b.band; b.value; b.word; b.fraction; b.envNext ]
      unpack =
        function
        | [ ear; band; value; word; fraction; envNext ] ->
            { ear = ear
              band = band
              value = asSInt value
              word = word
              fraction = fraction
              envNext = envNext }
        | _ -> failwith "curved: wrong arity" }

let private gainLayout: Layout<Gain> =
    { fields = fieldsOfWidths [ "ear", 1; "band", slotBits; "value", bandWidth; "gain_log", gainLogWidth; "env_next", sampleWidth ]
      pack = fun b -> [ b.ear; b.band; b.value; b.gainLog; b.envNext ]
      unpack =
        function
        | [ ear; band; value; gainLog; envNext ] ->
            { ear = ear
              band = band
              value = asSInt value
              gainLog = asSInt gainLog
              envNext = envNext }
        | _ -> failwith "gain: wrong arity" }

let private mantissaLayout: Layout<Mantissa> =
    { fields =
        fieldsOfWidths
            [ "ear", 1
              "band", slotBits
              "value", bandWidth
              "gain_log", gainLogWidth
              "mantissa", gainExpMantissaBits + 1
              "env_next", sampleWidth ]
      pack = fun b -> [ b.ear; b.band; b.value; b.gainLog; b.mantissa; b.envNext ]
      unpack =
        function
        | [ ear; band; value; gainLog; mantissa; envNext ] ->
            { ear = ear
              band = band
              value = asSInt value
              gainLog = asSInt gainLog
              mantissa = mantissa
              envNext = envNext }
        | _ -> failwith "mantissa: wrong arity" }

let private gainedLayout: Layout<Gained> =
    { fields = fieldsOfWidths [ "ear", 1; "band", slotBits; "gained", gainedWidth; "env_next", sampleWidth ]
      pack = fun b -> [ b.ear; b.band; b.gained; b.envNext ]
      unpack =
        function
        | [ ear; band; gained; envNext ] -> { ear = ear; band = band; gained = asSInt gained; envNext = envNext }
        | _ -> failwith "gained: wrong arity" }

// ---------------------------------------------------------------------------
// The stores.

/// What persists between samples — each section's history (x1, x2, y1, y2),
/// each band's envelope and last boosted value, the coefficients — and what
/// lives for one sample: the tree's node values, and per node the parity of
/// the sample that last wrote it, which is how a section knows the node it
/// reads is this sample's. All in block memory — iCE40 has no LUTRAM —
/// packed by entry, because its blocks are width-bound (256×16: a memory 16
/// deep and 24 wide costs two).
type private FoldStores =
    { history: Mem
      coefficients: Mem
      /// A band's envelope and last sample's band value, in **separate**
      /// memories rather than two fields of one.
      ///
      /// They are written by two different stages, and two writes to one mem
      /// fold to a single priority-muxed port — so a cycle where both fired
      /// would silently drop one of them. Separate memories give each stage
      /// its own port and make the collision impossible to express, where
      /// before it was merely prevented by a buffer nobody could explain
      /// (2026-09-23).
      envelopes: Mem
      detecteds: Mem
      nodes: Mem
      /// One register per ear, a bit per node.
      written: Expr list }

let private envelopeField = 0UL
let private detectedField = 1UL

let private foldStores (t: TreeShape) : FoldStores =
    let coeffSlots = 1 <<< ceilLog2 biquadIssues

    { history = blockMem "history" (1 + t.sectionBits + historyTapBits) sampleWidth
      coefficients =
        blockRom
            "coefficients"
            biquadCoeffWidth
            [| for sec in t.tree.sections do
                   yield! sec.coefficients
                   yield! Array.zeroCreate (coeffSlots - biquadIssues) |]
      envelopes = blockMem "band_envelope" (1 + slotBits) gainedWidth
      detecteds = blockMem "band_detected" (1 + slotBits) bandWidth
      nodes = blockMem "nodes" (1 + t.nodeBits) sampleWidth
      written = [ for ear in 0..1 -> reg $"written_%d{ear}" (1 <<< t.nodeBits) ] }

/// A section's history is two slots each of its input and its output, the
/// slot a sample writes being the sample's parity: this sample's x goes where
/// the sample before last's was, so x1 sits at the other parity and x2 at
/// this one, and a sample writes two words rather than shifting four. `kind`
/// is 0 for x, 1 for y; `slot` the parity.
let private historyAddrOf (ear: Expr) (section: Expr) (kind: Expr) (slot: Expr) =
    cat ear (cat section (cat kind slot))
let private coefficientAddr (s: Section) (tap: int) = cat s.section (lit (uint64 tap) (ceilLog2 (1 <<< ceilLog2 biquadIssues)))
let private stateAddr (ear: Expr) (band: Expr) = cat ear band

// ---------------------------------------------------------------------------
// The stages, in the order the body chains them.

/// Accept a stereo beat and emit its sections: the tree's sections in their
/// order, each for the left ear and then the right, with the ear's sample and
/// the sample's parity riding on each. The next beat is accepted once the
/// last section has gone, so a section is never in the chain twice — which
/// keeps its history read and its history write in order without an argument
/// about depth. The program is a ROM: one word per section, saying which
/// node it reads, which it writes, and whether that is a band.
let private sections (t: TreeShape) (s: Stream<Expr * Expr>) : Stream<Section> =
    let busy = regBit "sections_busy"
    bnot busy ==> s.ready
    let accept = s.valid &&& s.ready

    let left, right = s.payload
    let sampleLeft = reg "sample_left" (SInt sampleWidth)
    let sampleRight = reg "sample_right" (SInt sampleWidth)
    let parity = regBit "sample_parity"

    let ready = wireBit "sections_taken"
    registerStreamReady ready
    let transfer = busy &&& ready
    let ear = counter "section_ear" 2 transfer
    let section = counter "section" (List.length t.tree.sections) ear.wrap

    If accept (fun () ->
        left ==> sampleLeft
        right ==> sampleRight
        bnot parity ==> parity
        lit 1UL 1 ==> busy)

    If section.wrap (fun () -> lit 0UL 1 ==> busy)

    // The program word: input node, output node, leaf, band.
    let bandOf =
        t.tree.bands |> List.mapi (fun k node -> node, k) |> Map.ofList

    let wordWidth = t.nodeBits + t.nodeBits + 1 + slotBits + 1

    let words =
        [| for sec in t.tree.sections ->
               let leaf, band =
                   match Map.tryFind sec.output bandOf with
                   | Some k -> 1UL, uint64 k
                   | None -> 0UL, 0UL

               // First order when b2 and a2 are zero: three taps to issue.
               let short = if sec.coefficients[2] = 0UL && sec.coefficients[4] = 0UL then 1UL else 0UL

               ((((uint64 sec.input <<< t.nodeBits ||| uint64 sec.output) <<< 1 ||| leaf) <<< slotBits ||| band) <<< 1) ||| short |]

    let program = blockRom "crossover" wordWidth words
    // The word for the section the counter names arrives the port's depth
    // after the counter moves; until then what the port shows is the last
    // section's, and the beat is not offered.
    let read = memReadPort program section.count
    let word = wire "crossover_word" wordWidth
    read.data ==> word
    let stale = read.through "crossover_stale" ear.wrap

    let inputAt = wordWidth - t.nodeBits
    let outputAt = inputAt - t.nodeBits
    let leafAt = outputAt - 1
    let bandAt = leafAt - slotBits

    let input = wire "section_input" t.nodeBits
    slice (wordWidth - 1) inputAt word ==> input
    let output = wire "section_output" t.nodeBits
    slice (inputAt - 1) outputAt word ==> output
    let leaf = wire "section_leaf" 1
    slice leafAt leafAt word ==> leaf
    let band = wire "section_band" slotBits
    slice (bandAt + slotBits - 1) bandAt word ==> band
    let short = wire "section_short" 1
    slice 0 0 word ==> short

    ({ payload =
        { ear = ear.count
          section = section.count
          input = input
          output = output
          leaf = leaf
          band = band
          short = short
          parity = parity
          x = mux ear.count sampleRight sampleLeft }
       valid = busy &&& bnot stale
       ready = ready
       layout = sectionLayout t }
     : Stream<Section>)

/// Fetch the value a section filters: the ear's sample for node 0, otherwise
/// the node an earlier section of this sample wrote — and not before it has:
/// the stage waits while the node's parity is not this sample's. That wait is
/// the only ordering in the crossover, and it is exact, so nothing here needs
/// to know how far apart a producer and its consumer are.
let private readNode (t: TreeShape) (stores: FoldStores) (s: Stream<Section>) : Stream<Section> =
    let layout = sectionLayout t
    let st, out = workerFsm "node" s layout
    let accept = s.valid &&& s.ready
    let beat = holdBeat "node" layout accept s

    let fromSample = eq beat.input (lit 0UL t.nodeBits)
    let writtenBits = wire "written_bits" (1 <<< t.nodeBits)
    mux beat.ear stores.written[1] stores.written[0] ==> writtenBits
    let writtenShifted = wire "written_shifted" (1 <<< t.nodeBits)
    shrBy beat.input writtenBits ==> writtenShifted
    let writtenParity = wire "written_parity" 1
    slice 0 0 writtenShifted ==> writtenParity
    let available = fromSample ||| eq writtenParity beat.parity

    // The read is issued once, on the first cycle the node is available.
    let issued = regBit "node_issued"
    If accept (fun () -> lit 0UL 1 ==> issued)
    let issue = st.Is Working &&& available &&& bnot issued
    If issue (fun () -> lit 1UL 1 ==> issued)
    let read = memReadPort stores.nodes (cat beat.ear beat.input)
    let arrived = read.through "node_arrived" issue
    let word = reg "node_word" (SInt sampleWidth)
    If arrived (fun () -> read.data ==> word)
    If (st.Is Working &&& arrived) (fun () -> st.Goto Offering)

    offer layout out { beat with x = mux fromSample beat.x word }
    out

/// Issue a section's five multiplies to the shared unit, back to back as the
/// grants come — b0·x, b1·x1, b2·x2, a1·y1, a2·y2 — and hand the beat on the
/// moment the last is granted, so the next section issues while this one's
/// products are still landing in `collectTaps`.
///
/// The coefficients come from the ROM as they are needed rather than riding
/// in the beat, and the history words from their store the same way: two
/// ports each, one at the tap the counter names and one at the tap after it,
/// so the right word is present whether or not the last cycle was a grant.
/// In `Accepting` the ports already look at the offered beat's section, so
/// the first tap needs no wait.
let private issueTaps (t: TreeShape) (stores: FoldStores) (client: PodClient) (s: Stream<Section>) : Stream<Section> =
    let layout = sectionLayout t
    let st, out = workerFsm "issue" s layout
    let accept = s.valid &&& s.ready
    let beat = holdBeat "issue" layout accept s

    // A second-order section issues slots 0..4 (b0, b1, b2, a1, a2); a
    // first-order one issues 0, 1, 3 — its b2 and a2 are zero, and a product
    // of zero need not be made. The tap counter counts issues; the slot is
    // what the counter means for this section.
    let granted = client.issue &&& client.grant
    let taps = wire "issue_taps" 3
    mux beat.short (lit 2UL 3) (lit 4UL 3) ==> taps
    let tap = counterTo "issue_tap" taps granted
    let last = eq tap.count taps
    let slotBits = ceilLog2 (1 <<< ceilLog2 biquadIssues)
    let slotOf (count: Expr) (short: Expr) =
        mux (short &&& eq count (lit 2UL 3)) (lit 3UL slotBits) (pad slotBits count)
    let slot = wire "issue_slot" slotBits
    slotOf tap.count beat.short ==> slot
    let subtracts = lt (lit 2UL slotBits) slot

    let issuedAll = regBit "issue_all"
    If accept (fun () -> lit 0UL 1 ==> issuedAll)
    If (granted &&& last) (fun () -> lit 1UL 1 ==> issuedAll)
    let request = regBit "issue_request"
    (accept ||| (st.Is Working &&& bnot issuedAll &&& bnot (granted &&& last))) ==> request
    request ==> client.issue

    // The section the stores are asked about: the offered beat's whenever
    // this stage could accept it — which, with the skid, includes `Offering`.
    let ear = wire "issue_store_ear" 1
    mux (st.Is Working) beat.ear s.payload.ear ==> ear
    let section = wire "issue_rom_section" t.sectionBits
    mux (st.Is Working) beat.section s.payload.section ==> section
    let short = wire "issue_rom_short" 1
    mux (st.Is Working) beat.short s.payload.short ==> short
    let parity = wire "issue_store_parity" 1
    mux (st.Is Working) beat.parity s.payload.parity ==> parity
    let thisTap = memReadPort stores.coefficients (cat section slot)
    let nextCount = wire "issue_next_count" 3
    tap.count + lit 1UL 3 ==> nextCount
    let nextSlot = wire "issue_next_slot" slotBits
    slotOf nextCount short ==> nextSlot
    let nextTap = memReadPort stores.coefficients (cat section nextSlot)
    let justGranted = regBit "issue_just_granted"
    granted ==> justGranted
    let coefficient = wire "issue_coefficient" (SInt biquadCoeffWidth)
    mux justGranted nextTap.data thisTap.data ==> coefficient

    // The word a slot multiplies is the history tap before it — x1, x2, y1,
    // y2 at slots 1..4; slot 0 is the section's input, from the beat. One
    // back is the other parity's slot, two back this parity's.
    let historyBefore (name: string) (at: Expr) =
        let before = wire $"issue_{name}_before" slotBits
        sub at (lit 1UL slotBits) ==> before
        let kind = wire $"issue_{name}_kind" 1
        slice 1 1 before ==> kind
        let twoBack = wire $"issue_{name}_two_back" 1
        slice 0 0 before ==> twoBack
        memReadPort stores.history (historyAddrOf ear section kind (bnot (twoBack ^^^ parity)))

    let thisWord = historyBefore "this" slot
    let nextWord = historyBefore "next" nextSlot
    let history = wire "issue_history" (SInt sampleWidth)
    mux justGranted nextWord.data thisWord.data ==> history
    let operand = wire "issue_operand" (SInt sampleWidth)
    mux (eq slot (lit 0UL slotBits)) beat.x history ==> operand
    signExtend podAWidth operand ==> client.a
    signExtend podBWidth coefficient ==> client.b
    // The product comes back knowing whether it subtracts and whether it is
    // the section's last, so the collector counts nothing.
    cat last subtracts ==> client.tag

    // Offer from the last grant's edge.
    If (st.Is Working &&& granted &&& last) (fun () -> st.Goto Offering)
    offer layout out beat
    out

/// Sum a section's five products as they land — in `biquadDef`'s own
/// accumulator width, the first tap loading and the feedback taps
/// subtracting, so the total is the tree's total — rescale, and offer the
/// section with its output.
///
/// The products land whether or not this stage holds their section yet:
/// `issueTaps` hands the beat on at the last grant, two cycles before the
/// last product, and may have to wait for this stage to take it. So the sum
/// lives beside the beat rather than in it, and a finished result waits in
/// `pending` for its beat to arrive. Nothing can overrun it: the issuer does
/// not start the next section until this stage has taken this one, and by
/// then `pending` has been consumed.
let private collectTaps (t: TreeShape) (client: PodClient) (s: Stream<Section>) : Stream<SectionDone> =
    let st, out = workerFsm "collect" s (sectionDoneLayout t)
    let accept = s.valid &&& s.ready
    let beat = holdBeat "collect" (sectionLayout t) accept s

    let productWidth = sampleWidth + biquadCoeffWidth
    let accWidth = productWidth + 3
    let product = wire "collect_product" (SInt productWidth)
    slice (productWidth - 1) 0 client.product ==> product
    let addend = signExtend accWidth product
    let acc = reg "collect_acc" (SInt accWidth)
    let total = wire "collect_total" (SInt accWidth)

    // Each product says whether it subtracts and whether it is the last of
    // its section; the first after a last loads the accumulator.
    let lastTap = wire "collect_last" 1
    slice 1 1 client.tagBack ==> lastTap
    let subtracts = wire "collect_subtracts" 1
    slice 0 0 client.tagBack ==> subtracts
    let first = regInit "collect_first" 1 1UL
    If client.landed (fun () -> lastTap ==> first)

    mux first addend (mux subtracts (sub acc addend) (add acc addend)) ==> total
    If client.landed (fun () -> total ==> acc)

    // Rescaled from the register the beat after the last product lands: the
    // accumulate and the saturate are each a long carry chain.
    let complete = regBit "collect_complete"
    (client.landed &&& lastTap) ==> complete
    let rescaled = wire "collect_rescaled" (SInt(accWidth - biquadCoeffFrac))
    shr biquadCoeffFrac acc ==> rescaled
    let y = wire "collect_y" (SInt sampleWidth)
    saturate sampleWidth rescaled ==> y

    let pending = reg "collect_pending" (SInt sampleWidth)
    let pendingValid = regBit "collect_pending_valid"
    If complete (fun () ->
        y ==> pending
        lit 1UL 1 ==> pendingValid)

    let yHeld = reg "collect_y_held" (SInt sampleWidth)

    If (st.Is Working &&& pendingValid) (fun () ->
        pending ==> yHeld
        lit 0UL 1 ==> pendingValid
        st.Goto Offering)

    offer
        (sectionDoneLayout t)
        out
        { section = beat; y = yHeld }

    out

/// Keep a section's input and output for the next two samples: into this
/// parity's slots, which held the sample before last's.
let private writeHistory (t: TreeShape) (stores: FoldStores) =
    writeStage "history_write" stores.history (sectionDoneLayout t) (fun d ->
        [ historyAddrOf d.section.ear d.section.section (lit 0UL 1) d.section.parity, d.section.x
          historyAddrOf d.section.ear d.section.section (lit 1UL 1) d.section.parity, d.y ])

/// Keep a section's output for the sections that read it, and mark the node
/// as this sample's. Costs no beat.
let private writeNode (t: TreeShape) (stores: FoldStores) (s: Stream<SectionDone>) : Stream<SectionDone> =
    let d = s.payload
    let transfer = s.valid &&& s.ready
    memWrite stores.nodes (cat d.section.ear d.section.output) d.y transfer

    for ear, written in List.indexed stores.written do
        let mine = transfer &&& eq d.section.ear (lit (uint64 ear) 1)
        let shifted = wire $"written_shift_%d{ear}" (width (shlBy d.section.output (lit 1UL 1)))
        shlBy d.section.output (lit 1UL 1) ==> shifted
        let bit = wire $"written_bit_%d{ear}" (1 <<< t.nodeBits)
        slice ((1 <<< t.nodeBits) - 1) 0 shifted ==> bit

        // Set or clear the one bit the section wrote, to the sample's parity.
        If mine (fun () -> mux d.section.parity (written ||| bit) (written &&& bnot bit) ==> written)

    s

/// The bands are the tree's leaves: a section whose output is a band offers
/// its value as that band, and any other section's beat ends here. Costs no
/// beat.
let private bands (s: Stream<SectionDone>) : Stream<Band> =
    let d = s.payload
    let ready = wireBit "bands_taken"
    registerStreamReady ready
    // A non-leaf beat is taken the cycle it is offered; a leaf waits for the
    // band's consumer.
    (ready ||| bnot d.section.leaf) ==> s.ready

    ({ payload =
        { ear = d.section.ear
          band = d.section.band
          value = signExtend bandWidth d.y }
       valid = s.valid &&& d.section.leaf
       ready = ready
       layout = bandLayout }
     : Stream<Band>)

/// Read a band's state: its envelope, and last sample's boosted value — the
/// detector's input — two words from the state store.
let private readState (stores: FoldStores) =
    readStageFrom
        "state_read"
        bandLayout
        bandStateLayout
        (fun b -> [ stores.envelopes, stateAddr b.ear b.band; stores.detecteds, stateAddr b.ear b.band ])
        (fun b words ->
            { ear = b.ear
              band = b.band
              value = b.value
              env = slice (sampleWidth - 1) 0 words[0]
              detected = asSInt words[1] })

/// Keep this sample's band as next sample's detector input.
let private writeDetected (stores: FoldStores) =
    writeThrough stores.detecteds (fun (b: BandState) -> stateAddr b.ear b.band, b.value)

/// The detector: the magnitude of last sample's band, clipped to sample scale.
/// Costs no beat; its chain — negate, pick, saturate — lands in the next stage's
/// registers, a stage boundary ahead of the envelope's own compare and subtract,
/// which it would otherwise share a cycle with.
let private detect (s: Stream<BandState>) : Stream<Detected> =
    let b = s.payload
    let negated = wire "negated" (SInt bandWidth)
    sub (lit 0UL bandWidth) b.detected ==> negated
    let absolute = wire "absolute" bandWidth
    mux (slice (bandWidth - 1) (bandWidth - 1) b.detected) negated b.detected ==> absolute
    let peak = wire "peak" sampleWidth
    saturate sampleWidth absolute ==> peak

    streamMapTo detectedLayout (fun (b: BandState) -> { ear = b.ear; band = b.band; env = b.env; value = b.value; peak = peak }) s

/// The envelope step, off the detector's peak — the one law, in its two
/// halves around the shared multiplier.
let private envelope (pod: SharedMultiplier) (io: MultibandFoldedPorts) =
    podStage
        "envelope"
        pod
        detectedLayout
        steppedLayout
        stepWidth
        (fun b ->
            let difference, alphaSigned, _ = envelopeOperands b.env b.peak io.attack io.releaseRate
            difference, alphaSigned)
        (fun step b ->
            let envWide = wire "env_wide_landed" (SInt wideWidth)
            widenUnsigned wideWidth b.env ==> envWide

            { ear = b.ear
              band = b.band
              env = b.env
              value = b.value
              envNext = envelopeFromStep step envWide })

/// Keep the stepped envelope for the next sample.
let private writeEnvelope (stores: FoldStores) =
    writeThrough stores.envelopes (fun (b: Stepped) -> stateAddr b.ear b.band, widenUnsigned gainedWidth b.envNext)

/// Fetch this band's curve word, keyed on the envelope before this sample's
/// step — one request out, held until the table takes it, since a host reading
/// the table back borrows its port for a cycle. The fraction past the entry is a
/// slice of the same held envelope, so it rides out with the word rather than
/// being recomputed a stage later off a value that has moved.
let private fetchCurve (io: MultibandFoldedPorts) (s: Stream<Stepped>) : Stream<Curved> =
    let st, out = workerFsm "curve" s curvedLayout
    let accept = s.valid &&& s.ready
    let beat = holdBeat "curve" steppedLayout accept s

    let asked = regBit "curve_asked"
    If accept (fun () -> lit 0UL 1 ==> asked)

    let index = gainTableIndex "curve" beat.env

    let wanted = wire "curve_index" (1 + slotBits + gainTableAddrBits)
    catAll [ beat.ear; beat.band; index.address ] ==> wanted
    let taken = wireBit "curve_request_taken"
    registerStreamReady taken

    let request: Stream<Expr> =
        { payload = wanted
          valid = st.Is Working &&& bnot asked
          ready = taken
          layout = io.curve.request.layout }

    streamSink io.curve.request request
    If (request.valid &&& request.ready) (fun () -> lit 1UL 1 ==> asked)

    let answer = flowSource io.curve.answer
    let landing = st.Is Working &&& asked &&& answer.valid
    let word = reg "curve_word" gainTableWordWidth

    If landing (fun () ->
        answer.payload ==> word
        st.Goto Offering)

    offer
        curvedLayout
        out
        { ear = beat.ear
          band = beat.band
          value = beat.value
          word = word
          fraction = index.fraction
          envNext = beat.envNext }

    out

/// The gain the curve's word means at that fraction — the table's two halves
/// around the shared multiplier, in place of the formula's.
let private interpolate (pod: SharedMultiplier) =
    podStage
        "interp"
        pod
        curvedLayout
        gainLayout
        gainTableProductWidth
        (fun b -> gainTableOperands "interp" b.word b.fraction)
        (fun product b ->
            { ear = b.ear
              band = b.band
              value = b.value
              gainLog = gainTableFromProduct "interp" product (gainTableHere "interp" b.word)
              envNext = b.envNext })

/// The exponential's mantissa, off the gain's fraction — the other half of what
/// used to be one multiply, because a log gain has to come back before it can
/// scale anything.
let private expand (pod: SharedMultiplier) =
    podStage
        "expand"
        pod
        gainLayout
        mantissaLayout
        gainExpProductWidth
        (fun b -> gainExpOperands "expand" b.gainLog)
        (fun product b ->
            { ear = b.ear
              band = b.band
              value = b.value
              gainLog = b.gainLog
              mantissa = gainExpFromProduct "expand" product (gainExpHere "expand" b.gainLog)
              envNext = b.envNext })

/// Apply the gain to the band: its mantissa on the shared multiplier, then the
/// gain's integer part as the shift. The band goes in at **its own width** and
/// comes out wide enough for the sum to saturate once at the end — widening it
/// first would put nine more bits through every barrel stage.
let private applyLog (pod: SharedMultiplier) =
    let applyProductWidth = bandWidth + gainExpMantissaBits + 2

    podStage
        "apply"
        pod
        mantissaLayout
        gainedLayout
        applyProductWidth
        (fun b -> gainApplyOperands "apply" b.value b.mantissa)
        (fun applyProduct b ->
            { ear = b.ear
              band = b.band
              gained = gainApplyFromProduct "apply" b.gainLog applyProduct gainedWidth
              envNext = b.envNext })

/// Sum the eight bands of each ear, saturate once, and offer the stereo beat
/// when the right ear's last band has landed. The meter is the loudest
/// envelope across the sixteen.
let private sumBands (s: Stream<Gained>) : Stream<Expr * Expr> * Expr =
    let offering = regBit "sum_offering"
    bnot offering ==> s.ready
    let transfer = s.valid &&& s.ready

    // The beat lands in registers first: the apply's saturate into the sum's
    // adder and its own saturate is more than a cycle, as the spatial engine's
    // `gained` register already says. The sum runs a beat behind the transfer;
    // the beat after a sample's last is sixteen away, so `offering` is set
    // long before it could matter.
    let arrived = regBit "sum_arrived"
    transfer ==> arrived
    let b = holdBeat "sum" gainedLayout transfer s
    let first = eq b.band (lit 0UL slotBits)
    let last = eq b.band (lit (uint64 (multibandBands - 1)) slotBits)

    // One accumulator per ear: the crossover runs the ears interleaved, so
    // the bands arrive left, right, left, right.
    let sumWidth = gainedWidth + 3
    let sums = [ for ear in 0..1 -> reg $"ear_sum_%d{ear}" (SInt sumWidth) ]
    let sum = wire "ear_sum" (SInt sumWidth)
    mux b.ear sums[1] sums[0] ==> sum
    let total = wire "sum_total" (SInt sumWidth)
    mux first (signExtend sumWidth b.gained) (add sum (signExtend sumWidth b.gained)) ==> total
    If (arrived &&& bnot b.ear) (fun () -> total ==> sums[0])
    If (arrived &&& b.ear) (fun () -> total ==> sums[1])

    // Saturated from the accumulator the cycle after an ear's last band
    // lands — the add and the saturate are each a long carry chain, and
    // together they were the engine's critical path. The other ear's band
    // is what lands next, so the register is still this ear's total.
    let landed = regBit "sum_landed"
    (arrived &&& last) ==> landed
    let landedEar = regBit "sum_landed_ear"
    If (arrived &&& last) (fun () -> b.ear ==> landedEar)
    let landedSum = wire "sum_landed_total" (SInt sumWidth)
    mux landedEar sums[1] sums[0] ==> landedSum
    let earOut = wire "ear_out" (SInt sampleWidth)
    saturate sampleWidth landedSum ==> earOut
    let outLeft = reg "out_left_reg" (SInt sampleWidth)
    let outRight = reg "out_right_reg" (SInt sampleWidth)
    If (landed &&& bnot landedEar) (fun () -> earOut ==> outLeft)
    If (landed &&& landedEar) (fun () -> earOut ==> outRight)

    let envMax = reg "env_max" sampleWidth
    let envelope = reg "envelope_reg" sampleWidth
    If arrived (fun () -> mux (lt envMax b.envNext) b.envNext envMax ==> envMax)

    If (arrived &&& last &&& b.ear) (fun () ->
        mux (lt envMax b.envNext) b.envNext envMax ==> envelope
        lit 0UL sampleWidth ==> envMax)

    If (landed &&& landedEar) (fun () -> lit 1UL 1 ==> offering)

    let ready = wireBit "sum_taken"
    registerStreamReady ready
    If (offering &&& ready) (fun () -> lit 0UL 1 ==> offering)

    ({ payload = (outLeft, outRight)
       valid = offering
       ready = ready
       layout = sampleLayout }
     : Stream<Expr * Expr>),
    envelope

/// The module's stereo ports as the stream they carry, from inside the body.
let private streamOfStereo (sp: StereoPorts) : Stream<Expr * Expr> =
    ({ payload = (sp.inLeft, sp.inRight)
       valid = sp.inValid
       ready = sp.inReady
       layout = sampleLayout }
     : Stream<Expr * Expr>)

/// Drive the module's stereo output ports from a stream, from inside the body.
let private streamToStereo (sp: StereoPorts) (s: Stream<Expr * Expr>) =
    let left, right = s.payload
    left ==> sp.outLeft
    right ==> sp.outRight
    s.valid ==> sp.outValid
    sp.outReady ==> s.ready

/// The 8-band stereo multiband compressor on one multiplier — the same ports,
/// the same settings and the same samples as `multibandCompressor8Def`, as
/// the spatial engine's own operations chained one after another, each a
/// stage that holds one beat: the crossover a section at a time, then each
/// band through its compressor's steps, then the sum. What is shared is the
/// multiplier behind them, through `warpFu`. No stage tells another how long
/// it takes, and nothing here is scheduled: a beat moves when the next stage
/// can take it. The state lives in memory, so it does not reset with the
/// registers.
let multibandCompressor8FoldedDef (name: string) (crossovers: float list) (sampleRate: float) : TypedModule<MultibandFoldedPorts> =
    if List.length crossovers <> multibandBands - 1 then
        failwith $"multibandCompressor8Folded needs {multibandBands - 1} crossovers, got {List.length crossovers}"

    let shape = treeShape (crossoverTree crossovers sampleRate)

    defModule
        name
        (fun p ->
            { s = stereoPorts p
              attack = p.inPort "attack" 16
              releaseRate = p.inPort "releaseRate" 16
              curve = curveLookupPorts p
              envelope = p.outPort "envelope" sampleWidth })
        (fun io ->
            let stores = foldStores shape
            let pod = SharedMultiplier "pod"
            let biquad = pod.Client "biquad"

            let out, envelope =
                streamOfStereo io.s
                |> sections shape
                |> readNode shape stores
                |> skidBuffer "node_skid" (sectionLayout shape)
                |> issueTaps shape stores biquad
                |> collectTaps shape biquad
                |> writeHistory shape stores
                |> writeNode shape stores
                |> bands
                |> skidBuffer "band_skid" bandLayout
                |> readState stores
                |> writeDetected stores
                |> detect
                |> envelope pod io
                |> writeEnvelope stores
                |> fetchCurve io
                |> interpolate pod
                |> expand pod
                |> applyLog pod
                |> sumBands

            envelope ==> io.envelope
            out |> streamToStereo io.s
            pod.Finish())

/// Wire one folded bank's settings and splice the stream through it. The
/// makeup table is the caller's: the bank's request meets the table's port
/// here — refused for the cycle a host readback has borrowed it, answered a
/// port-depth later otherwise — and neither side learns how long the other
/// takes.
let private multibandFoldedInstance (io: MultibandFoldedPorts) (instName: string) =
    fun (settings: MultibandFoldedSettings) (s: Stream<Expr * Expr>) ->
        settings.attack ==> io.attack
        settings.releaseRate ==> io.releaseRate

        let port = settings.curve (List.head io.curve.request.targets)
        let accepted = io.curve.request.valid &&& bnot port.hostTurn
        bnot port.hostTurn ==> io.curve.request.ready
        port.read.through $"{instName}_curve_landed" accepted ==> io.curve.answer.valid

        let word = wire $"{instName}_curve_read" (width port.read.data)
        port.read.data ==> word

        (if width word > gainTableWordWidth then slice (gainTableWordWidth - 1) 0 word else word)
        ==> io.curve.answer.payload

        stereoSplice io.s s, io.envelope

/// One folded bank under `instName`, with `multibandCompressor8`'s call shape
/// over the folded settings.
let multibandCompressor8Folded (name: string) (crossovers: float list) (sampleRate: float) instName =
    multibandFoldedInstance ((multibandCompressor8FoldedDef name crossovers sampleRate).NewNamed instName) instName

/// The stock 8-band compressor on one multiplier: `multibandCompressor`'s
/// call shape, over `MultibandFoldedSettings` — the same law, with the makeup
/// gains in a table the bank reads rather than sixteen wires it is handed.
let multibandCompressorFolded name (sampleRate: float) =
    multibandCompressor8Folded name defaultCrossovers sampleRate
