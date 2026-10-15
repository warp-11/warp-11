/// PDM microphones: a one-bit stream per clock edge, decimated to samples.
///
/// A digital MEMS microphone is a sigma-delta modulator on a diaphragm: it
/// takes a bit clock in the low megahertz and answers one bit per period whose
/// *density* is the sound pressure. Two share a data line by each driving one
/// half of the clock period — the one whose select pin is low drives after the
/// rising edge, the other after the falling edge — and go high-impedance for
/// the other half. What the fabric owes them is the clock and a decimator: a
/// filter that turns `decimation` bits into one sample at the clock rate
/// divided by that.
///
/// The decimator here is Hogenauer's CIC — `order` integrators at the bit rate,
/// then `order` combs at the sample rate — because it is the one filter that
/// decimates a bit stream with no multiplier at all: adds, subtracts and
/// registers. Its magnitude response is sinc^order, which droops toward the
/// band edge (−1.6 dB at 8 kHz for order 4 by 64 at 48.8 kHz); `cicDroopDb`
/// tabulates that so whoever sets gains downstream can fold the correction in
/// rather than paying for a compensation filter's latency. What it does *not*
/// remove is the modulator's shaped noise just under the output Nyquist, which
/// sinc^4 only attenuates by 16 dB there; a design that cares follows the link
/// with `biquadCascade (butterworthLowPass 4 fc fs)`.
///
/// This is the second peripheral link after I2S, and the same shape: `pdmPins`
/// in the io factory, `pdmLink` in the body, a `Stream` out. The edge each
/// microphone is sampled on is settled inside the link, for the reason the I2S
/// ticks never reach a caller — sampling a microphone on the edge it starts
/// driving on elaborates cleanly and is wrong on silicon.
[<AutoOpen>]
module Warp11.Pdm

open System

/// A PDM bus as a design presents it: the clock the design makes, and the data
/// line every microphone on the bus shares.
type PdmPins =
    { clockOut: Output
      dataIn: Input }

/// Declare a bus's pins under `prefix`: `{prefix}_clk` out, `{prefix}_data` in.
/// Call from a module's io factory.
let pdmPinsNamed (p: Ports) (prefix: string) : PdmPins =
    { clockOut = p.outPort $"{prefix}_clk" 1
      dataIn = p.inPort $"{prefix}_data" 1 }

/// The one bus most designs have: `pdm_clk` and `pdm_data`.
let pdmPins (p: Ports) : PdmPins = pdmPinsNamed p "pdm"

// ---------------------------------------------------------------------------
// The CIC decimator, one channel.

/// The decimator's ports. Both ticks are one fabric cycle each: `bitTick` on
/// the edge this channel's bit is sampled on, `outTick` once per `decimation`
/// of them. The link makes both; the ports are public for a design with a
/// bit stream from somewhere else.
type CicPorts =
    { /// The data line, read on `bitTick`.
      data: Input
      /// One cycle per bit.
      bitTick: Input
      /// One cycle per sample. The combs run and the sample updates on it.
      outTick: Input
      /// The sample, held until the next `outTick`.
      sample: Output }

/// Width of the integrators and combs: `order · ⌈log2 decimation⌉ + 1` bits,
/// Hogenauer's bound for a one-bit input. Every stage is this wide and wraps,
/// and the wrapping is correct: two's complement arithmetic makes the comb's
/// difference exact as long as the register can hold the output's range, which
/// this width is chosen to.
let cicAccumulatorWidth (order: int) (decimation: int) : int = order * ceilLog2 decimation + 1

/// How far the centred comb output is shifted right to land in a sample. The
/// comb's range is `0 .. decimation^order`; centred, its magnitude is bounded
/// by `2^(accumulatorWidth − 2)`, and a sample's by `2^(sampleWidth − 1)`.
/// Negative when the accumulator is the narrower — a low order or a small
/// decimation — in which case the value is shifted left instead.
let private cicShift (order: int) (decimation: int) (sampleWidth: int) : int =
    cicAccumulatorWidth order decimation - 1 - sampleWidth

/// The gain from a full-scale bit density to a full-scale sample. Exactly 1
/// when `decimation` is a power of two; otherwise below it, by the ratio of
/// `decimation^order / 2` to the power of two above it, because the shift is
/// by whole bits. A check states its expectation through this rather than
/// assuming unity.
let cicGain (order: int) (decimation: int) (sampleWidth: int) : float =
    let fullScale = (float decimation ** float order) / 2.0
    fullScale / (2.0 ** float (cicShift order decimation sampleWidth)) / (2.0 ** float (sampleWidth - 1))

/// The CIC's magnitude response in dB at `hz`, for samples at `sampleRate`:
/// `(sin(π f/fs) / (R sin(π f/(R fs))))^order`, which is 0 dB at DC and droops
/// toward the band edge. Tabulated so a design can correct it where it sets
/// gains anyway — a fit's curve, a band's makeup — instead of filtering it out
/// at the cost of a FIR's group delay.
let cicDroopDb (order: int) (decimation: int) (sampleRate: float) (hz: float) : float =
    if hz = 0.0 then
        0.0
    else
        let theta = Math.PI * hz / (float decimation * sampleRate)
        let h = sin (float decimation * theta) / (float decimation * sin theta)
        20.0 * log10 (abs h ** float order)

/// A CIC decimator: `order` integrators advancing on `bitTick`, `order` combs
/// advancing on `outTick`, the result centred, scaled to `sampleWidth` bits
/// and clamped — the clamp only ever acts on a bit stream no modulator
/// produces, since a stable one never reaches full density.
///
/// The input is read as `0`/`1`, so the comb's output sits at half scale for
/// silence; subtracting `decimation^order / 2` centres it. Reading the bit as
/// ±1 instead would double the input's width and buy nothing.
///
/// Each integrator adds the *registered* value of the one before it, so the
/// chain is a pipeline rather than a ripple of adders — one sample of extra
/// delay per stage, and the longest path one adder deep at any order.
let cicDecimatorDef (name: string) (order: int) (decimation: int) (sampleWidth: int) : TypedModule<CicPorts> =
    if order < 1 then failwith $"cicDecimator '{name}': order must be >= 1, got {order}"
    if decimation < 2 then failwith $"cicDecimator '{name}': decimation must be >= 2, got {decimation}"
    if sampleWidth < 2 then failwith $"cicDecimator '{name}': sampleWidth must be >= 2, got {sampleWidth}"

    let accWidth = cicAccumulatorWidth order decimation
    let shift = cicShift order decimation sampleWidth
    let offset = (pown (uint64 decimation) order) / 2UL

    defModule
        name
        (fun p ->
            { data = p.inPort "data" 1
              bitTick = p.inPort "bitTick" 1
              outTick = p.inPort "outTick" 1
              sample = p.outPortAs "sample" (SInt sampleWidth) })
        (fun io ->
            let integrators = [ for k in 0 .. order - 1 -> reg $"int{k}" accWidth ]

            // Stage k adds what stage k−1 held at the start of the cycle; the
            // first adds the bit.
            let integratorInputs = pad accWidth io.data :: List.take (order - 1) integrators

            If io.bitTick (fun () ->
                for integrator, input in List.zip integrators integratorInputs do
                    integrator + input ==> integrator)

            // Each comb subtracts its previous input from its current one, the
            // chain reading the last integrator. Combinational, so the sample
            // register one `outTick` after the last bit holds the whole result.
            let combPrevious = [ for k in 0 .. order - 1 -> reg $"comb{k}_prev" accWidth ]

            let combStages =
                List.fold
                    (fun (x, stages) k ->
                        let y = wire $"comb{k}" accWidth
                        sub x combPrevious[k] ==> y
                        (y, (x, y) :: stages))
                    (List.last integrators, [])
                    [ 0 .. order - 1 ]
                |> snd
                |> List.rev

            let combOut = combStages |> List.last |> snd

            let centred = wire "centred" (SInt accWidth)
            asSInt (sub combOut (lit offset accWidth)) ==> centred

            let sample =
                if shift > 0 then
                    let scaled = wire "scaled" (SInt accWidth)
                    sra shift centred ==> scaled
                    saturate sampleWidth scaled
                elif shift < 0 then
                    let scaled = wire "scaled" (SInt(accWidth - shift))
                    asSInt (cat centred (lit 0UL (-shift))) ==> scaled
                    saturate sampleWidth scaled
                else
                    saturate sampleWidth centred

            let sampleReg = reg "sample_reg" (SInt sampleWidth)
            sampleReg ==> io.sample

            If io.outTick (fun () ->
                for (x, _), previous in List.zip combStages combPrevious do
                    x ==> previous

                sample ==> sampleReg))

/// One decimator under `instName`, called as a function: the bit, its tick and
/// the sample tick in, the sample out.
let cicDecimator (name: string) (order: int) (decimation: int) (sampleWidth: int) instName =
    let io = (cicDecimatorDef name order decimation sampleWidth).NewNamed instName

    fun (data: Expr) (bitTick: Expr) (outTick: Expr) ->
        data ==> io.data
        bitTick ==> io.bitTick
        outTick ==> io.outTick
        io.sample

// ---------------------------------------------------------------------------
// The link.

/// The CIC order the link builds: one more than a second-order modulator, which
/// is what a MEMS microphone is, so the integrators' slope exceeds the noise
/// shaping's and the aliased noise falls with the signal rather than above it.
let pdmCicOrder = 4

/// How far the achieved bit clock may sit from the requested one, by the same
/// reasoning as `i2sRateTolerance`: a microphone's clock window is tens of
/// percent wide, and a divisor further off than this was never going to land.
let pdmClockTolerance = 0.01

/// The half-period divisor `pdmLink` derives: the clock toggles every this
/// many fabric cycles.
let pdmClockHalfDiv (fabricHz: int) (targetClockHz: int) : int =
    max 1 (int (round (float fabricHz / (2.0 * float targetClockHz))))

/// The sample rate a link produces, for a check or a comment: the fabric clock
/// over twice the half divisor, over the decimation.
let pdmSampleRateOf (fabricHz: int) (halfDiv: int) (decimation: int) : float =
    float fabricHz / (2.0 * float halfDiv * float decimation)

/// A bus, as the body sees it: the stereo stream off the two microphones.
type PdmLink =
    { /// The pair, left the microphone whose select pin is low. `valid` pulses
      /// once per sample; downstream is assumed always-ready, as the I2S
      /// receiver assumes it, since a consumer has `decimation` bit periods to
      /// take each one.
      input: Stream<Expr * Expr> }

/// The whole front end: the bit clock at the board's rate, two decimators on
/// the shared line, the pair as a stream.
///
/// **Each microphone is sampled on the edge opposite the one it drives on** —
/// left on the falling edge, right on the rising — which is where the line has
/// settled and is still driven. That choice is made here and not exposed: the
/// decimator ports take any tick, so a design assembling them by hand can hand
/// a microphone the edge it is still switching on, and nothing short of the
/// bench says so.
///
/// `fabricHz` is a frequency, not a `Board`, for the reason `i2sLink` gives.
/// The fabric must run at four times the bit clock or more: the sample point
/// is a half period after the microphone starts driving, and at one fabric
/// cycle a half period that is the cycle it starts.
let pdmLink (prefix: string) (pins: PdmPins) (fabricHz: int) (targetClockHz: int) (decimation: int) : PdmLink =
    if fabricHz < 1 then failwith $"pdmLink '{prefix}': fabricHz must be >= 1, got {fabricHz}"
    if targetClockHz < 1 then failwith $"pdmLink '{prefix}': targetClockHz must be >= 1, got {targetClockHz}"
    if decimation < 2 then failwith $"pdmLink '{prefix}': decimation must be >= 2, got {decimation}"

    let halfDiv = pdmClockHalfDiv fabricHz targetClockHz

    if halfDiv < 2 then
        failwith (
            $"pdmLink '{prefix}': %d{fabricHz} Hz is under four times the %d{targetClockHz} Hz bit clock — "
            + "a microphone is sampled half a period after the edge it drives on, and that half period "
            + "must be at least two fabric cycles.")

    let achieved = float fabricHz / (2.0 * float halfDiv)
    let error = abs (achieved - float targetClockHz) / float targetClockHz

    if error > pdmClockTolerance then
        failwith (
            $"pdmLink '{prefix}': %d{fabricHz} Hz cannot make a %d{targetClockHz} Hz bit clock — "
            + $"the nearest divisor (%d{halfDiv}) gives %.1f{achieved} Hz, off by %.2f{error * 100.0}%%.")

    let clk = regBit $"{prefix}_clk_reg"
    let half = counter $"{prefix}_half" halfDiv (lit 1UL 1)
    If half.wrap (fun () -> bnot clk ==> clk)
    clk ==> pins.clockOut

    let rising = wireBit $"{prefix}_rising"
    (half.wrap &&& bnot clk) ==> rising
    let falling = wireBit $"{prefix}_falling"
    (half.wrap &&& clk) ==> falling

    // One frame counter for both channels, so their combs run on the same
    // cycle and the pair leaves together; the right channel's bits sit half a
    // period earlier, which is a phase offset and not a sample.
    let frame = counter $"{prefix}_frame" decimation falling
    let outTick = wireBit $"{prefix}_out_tick"
    frame.wrap ==> outTick

    let left = cicDecimator "CicDecimator" pdmCicOrder decimation sampleWidth $"{prefix}_cic_l"
    let right = cicDecimator "CicDecimator" pdmCicOrder decimation sampleWidth $"{prefix}_cic_r"

    let leftSample = left pins.dataIn falling outTick
    let rightSample = right pins.dataIn rising outTick

    // The samples register on `outTick`; the beat is valid the cycle after.
    let valid = regBit $"{prefix}_valid"
    outTick ==> valid

    let ready = wireBit $"{prefix}_ready"
    registerStreamReady ready

    { input =
        { payload = (leftSample, rightSample)
          valid = valid
          ready = ready
          layout = sampleLayout } }

// ---------------------------------------------------------------------------
// A cascade of biquads as a stereo stream stage: what follows a decimator when
// the shaped noise above the band has to go.

/// `designs` as sections in order, one chain per channel, advancing on each
/// beat that transfers. The coefficients are elaboration-time constants; a
/// section that the host should retune is `biquadSection` wired by hand.
///
/// Each section is five signed multiplies, so four of them a channel on a part
/// with few DSP blocks is a budget question — the folded engines in `Audio.fs`
/// exist because it was.
let biquadCascade (name: string) (prefix: string) (designs: BiquadDesign list) (s: Stream<Expr * Expr>) : Stream<Expr * Expr> =
    let advance = wireBit $"{prefix}_advance"
    (s.valid &&& s.ready) ==> advance

    let coefficientsOf (d: BiquadDesign) : BiquadCoeffs =
        match toQ230 d with
        | [ b0; b1; b2; a1; a2 ] ->
            { b0 = lit b0 biquadCoeffWidth
              b1 = lit b1 biquadCoeffWidth
              b2 = lit b2 biquadCoeffWidth
              a1 = lit a1 biquadCoeffWidth
              a2 = lit a2 biquadCoeffWidth }
        | _ -> failwith "toQ230 gives five coefficients"

    let chain (side: string) (x: Expr) =
        designs
        |> List.indexed
        |> List.fold (fun x (k, d) -> biquadSection name $"{prefix}_{side}{k}" x advance (coefficientsOf d)) x

    let left, right = s.payload
    { s with payload = (chain "l" left, chain "r" right) }
