/// The units a guitar pedal wants that the audio library does not have: a
/// mixer, a waveshaper, a tremolo, an all-pass section. Each is a stereo
/// stream through a law that declares its own wires under the instance's
/// name, so a design may spend copies of it; the stage machinery around a
/// unit is the placement's business, as it is for `gainModule`.
///
/// The beat's fields are bits: a field that came from the design's own
/// input is width-only, one that came from a module reads signed. Every unit
/// here reads its operands as samples first, so it does not care which.
///
/// A comb is `echo` — a feedback comb over a delay line is what an echo is —
/// and a splitter is two wires from one outlet, which the graph allows.
module Warp11.Pedal

open Warp11
open Warp11.Fu

let private stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

/// A beat field read as a sample: a declared signed wire, whichever way the
/// field arrived, so `pad` and `saturate` have a named signed signal.
let private sample (instance: string) (name: string) (v: Expr) : Expr =
    let x = wire $"{instance}_{name}" (SInt sampleWidth)
    asSInt v ==> x
    x

/// A Q8.8 gain control read as a signed multiplicand, zero-padded so it can
/// never present as negative — `audioGain`'s move.
let private gainSigned (instance: string) (name: string) (g: Expr) : Expr =
    let x = wire $"{instance}_{name}" (SInt 17)
    cat (lit 0UL 1) g ==> x
    x

/// `x * gain >> 8`, saturated back to a sample.
let private scaled (instance: string) (name: string) (x: Expr) (gain: Expr) : Expr =
    let product = wire $"{instance}_{name}_product" (SInt(sampleWidth + 17))
    mul x gain ==> product
    let shifted = wire $"{instance}_{name}_shifted" (SInt(sampleWidth + 17 - gainFracBits))
    shr gainFracBits product ==> shifted
    let out = wire $"{instance}_{name}" (SInt sampleWidth)
    saturate sampleWidth shifted ==> out
    out

let private saturatedSum (instance: string) (name: string) (a: Expr) (b: Expr) : Expr =
    let w = sampleWidth + 1
    let sum = wire $"{instance}_{name}_sum" (SInt w)
    add (pad w a) (pad w b) ==> sum
    let out = wire $"{instance}_{name}" (SInt sampleWidth)
    saturate sampleWidth sum ==> out
    out

let private saturatedDifference (instance: string) (name: string) (a: Expr) (b: Expr) : Expr =
    let w = sampleWidth + 1
    let difference = wire $"{instance}_{name}_difference" (SInt w)
    sub (pad w a) (pad w b) ==> difference
    let out = wire $"{instance}_{name}" (SInt sampleWidth)
    saturate sampleWidth difference ==> out
    out

/// A zero-latency stereo stage: the law gets the instance, the controls,
/// the beat's operands and `accepted` — high when this beat moves — and
/// hands back the two samples out. The handshake passes straight through.
let private stereoUnit
    (name: string)
    (operands: Layout<'a>)
    (controls: (string * NumberFormat) list)
    (law: string -> Expr list -> 'a -> Expr -> Expr * Expr)
    : Fu<'a, Expr * Expr> =
    { name = name
      operands = operands
      results = stereo
      controls = controls
      copies = 1
      law =
        Sequential(fun instance controls s ->
            let accepted = wire $"{instance}_accepted" 1
            (s.valid &&& s.ready) ==> accepted

            // A new stream rather than `{ s with … }`: the payload changes
            // type, and a copy keeps the record's.
            { payload = law instance controls s.payload accepted
              valid = s.valid
              ready = s.ready
              layout = stereo }) }

/// Two stereo inputs summed, each through its own Q8.8 gain — a wet/dry
/// mix, or two voices. Saturating.
let mixer: Fu<Expr * Expr * Expr * Expr, Expr * Expr> =
    stereoUnit
        "mixer"
        (pins4 ("a_left", signedInt sampleWidth) ("a_right", signedInt sampleWidth) ("b_left", signedInt sampleWidth) ("b_right", signedInt sampleWidth))
        [ "a_gain", unsignedInt 16; "b_gain", unsignedInt 16 ]
        (fun instance controls (aL, aR, bL, bR) _ ->
            match controls with
            | [ aGain; bGain ] ->
                let ga = gainSigned instance "a_gain" aGain
                let gb = gainSigned instance "b_gain" bGain

                let channel (name: string) (a: Expr) (b: Expr) =
                    saturatedSum
                        instance
                        name
                        (scaled instance $"a_{name}_scaled" (sample instance $"a_{name}" a) ga)
                        (scaled instance $"b_{name}_scaled" (sample instance $"b_{name}" b) gb)

                channel "left" aL bL, channel "right" aR bR
            | _ -> failwith "mixer: two gains")

/// Drive into a cubic soft clipper: `y = 1.5 x − 0.5 x³` for the driven sample
/// at full scale = 1, which is 1 with zero slope at ±1 — the classic shape.
/// `drive` is Q8.8; past unity the driven sample saturates first, so more
/// drive is more square.
let waveshaper: Fu<Expr * Expr, Expr * Expr> =
    stereoUnit
        "waveshaper"
        stereo
        [ "drive", unsignedInt 16 ]
        (fun instance controls (l, r) _ ->
            match controls with
            | [ drive ] ->
                let g = gainSigned instance "drive" drive
                let fraction = sampleWidth - 1

                let channel (name: string) (x: Expr) =
                    let y = scaled instance $"{name}_driven" (sample instance $"{name}_in" x) g

                    let square = wire $"{instance}_{name}_square" (SInt(2 * sampleWidth))
                    mul y y ==> square
                    let y2 = wire $"{instance}_{name}_y2" (SInt(2 * sampleWidth - fraction))
                    shr fraction square ==> y2

                    let cube = wire $"{instance}_{name}_cube" (SInt(2 * sampleWidth - fraction + sampleWidth))
                    mul y2 y ==> cube
                    let y3 = wire $"{instance}_{name}_y3" (SInt(2 * sampleWidth - fraction + sampleWidth - fraction))
                    shr fraction cube ==> y3

                    // 3y − y³, then halve: (3y − y³) / 2 = 1.5y − 0.5y³. Three
                    // times is y plus y shifted up, both as declared signals.
                    let w = sampleWidth + 3
                    let doubled = wire $"{instance}_{name}_doubled" (SInt(sampleWidth + 1))
                    cat y (lit 0UL 1) ==> doubled
                    let tripled = wire $"{instance}_{name}_tripled" (SInt w)
                    add (pad w y) (pad w doubled) ==> tripled
                    let shaped = wire $"{instance}_{name}_shaped" (SInt(w + 1))
                    sub (pad (w + 1) tripled) (pad (w + 1) y3) ==> shaped
                    let halved = wire $"{instance}_{name}_halved" (SInt w)
                    shr 1 shaped ==> halved
                    let out = wire $"{instance}_{name}" (SInt sampleWidth)
                    saturate sampleWidth halved ==> out
                    out

                channel "left" l, channel "right" r
            | _ -> failwith "waveshaper: drive")

/// Phase-accumulator width, as the tone generator's: a beat advances the
/// phase by `rate`, so the sweep is `Fs · rate / 2^24` hertz.
let tremoloPhaseWidth = 24

/// Amplitude modulation by a triangle: the gain swings from unity down by
/// `depth` (Q8.8, 256 is all the way to silence) and back, once per
/// `2^24 / rate` beats. The phase advances on accepted beats, so it tracks
/// the sample rate rather than the clock.
let tremolo: Fu<Expr * Expr, Expr * Expr> =
    stereoUnit
        "tremolo"
        stereo
        [ "rate", unsignedInt tremoloPhaseWidth; "depth", unsignedInt 16 ]
        (fun instance controls (l, r) accepted ->
            match controls with
            | [ rate; depth ] ->
                let phase = reg $"{instance}_phase" tremoloPhaseWidth
                let rampWidth = tremoloPhaseWidth - 1
                let ramp = wire $"{instance}_ramp" rampWidth
                slice (rampWidth - 1) 0 phase ==> ramp
                let inverted = wire $"{instance}_inverted" rampWidth
                bnot ramp ==> inverted
                let triangle = wire $"{instance}_triangle" rampWidth
                mux (slice (rampWidth) (rampWidth) phase) inverted ramp ==> triangle

                // swing = depth · triangle / 2^23, in Q8.8: 0 at the trough, `depth` at the peak.
                let swingProduct = wire $"{instance}_swing_product" (16 + rampWidth)
                mul depth triangle ==> swingProduct
                let swing = wire $"{instance}_swing" 16
                slice (16 + rampWidth - 1) rampWidth swingProduct ==> swing

                // gain = unity − swing, floored at silence for a depth past unity.
                let gain = wire $"{instance}_gain" 16
                let unity = lit gainUnity 16
                mux (lt unity swing) (lit 0UL 16) (sub unity swing) ==> gain
                let g = gainSigned instance "gain_signed" gain

                If accepted (fun () -> phase + rate ==> phase)

                scaled instance "left" (sample instance "left_in" l) g, scaled instance "right" (sample instance "right_in" r) g
            | _ -> failwith "tremolo: rate and depth")

/// Schroeder's all-pass section over a delay line, the building block of a
/// reverb: flat in magnitude, so a tone comes out at the level it went in,
/// and every frequency at a different delay.
///
///     v = x + g · v[n − D]
///     y = v[n − D] − g · v
///
/// `capacity` is the line's length, a power of two; `delay` is the tap and
/// `gain` is `g`, Q8.8 — 179 is about 0.7. The two channels share one line
/// as a packed frame, as the echo does.
let allpass (capacity: int) : Fu<Expr * Expr, Expr * Expr> =
    stereoUnit
        "allpass"
        stereo
        [ "delay", unsignedInt (log2Exact capacity); "gain", unsignedInt 16 ]
        (fun instance controls (l, r) accepted ->
            match controls with
            | [ delay; gain ] ->
                let g = gainSigned instance "gain_signed" gain
                let written = wire $"{instance}_written" sampleBits

                // The line advances on the beats this stage passes, never on
                // bare cycles: a delay in clocks would change with a stall.
                let delayed = delayBuffer $"{instance}_line" capacity accepted delay written

                let channel (name: string) (x: Expr) (delayedBits: Expr) =
                    let vd = sample instance $"{name}_delayed" delayedBits
                    let v = saturatedSum instance $"{name}_v" (sample instance $"{name}_in" x) (scaled instance $"{name}_feedback" vd g)
                    let y = saturatedDifference instance name vd (scaled instance $"{name}_feedforward" v g)
                    v, y

                let vL, yL = channel "left" l (sampleLeft delayed)
                let vR, yR = channel "right" r (sampleRight delayed)
                cat vL vR ==> written
                yL, yR
            | _ -> failwith "allpass: delay and gain")
