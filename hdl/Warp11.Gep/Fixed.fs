/// Q16.16 fixed point held in an int32 — the F# oracle of the Kotlin
/// `Fixed.kt`, golden-vector-checked against it.
///
/// Every operation saturates rather than wrapping or throwing: GEP needs total
/// functions, and a runaway subexpression must not be able to poison an
/// evaluation with a wrapped sign. Saturation is also what the HDL ALU does, so
/// the two implementations agree bit-for-bit under differential test.
module Warp11.Gep.Fixed

let fxFracBits = 16
let fxOne : int = 1 <<< fxFracBits

let internal fxSat (v: int64) : int =
    if v > int64 System.Int32.MaxValue then System.Int32.MaxValue
    elif v < int64 System.Int32.MinValue then System.Int32.MinValue
    else int v

/// Rounds half up (floor(x + 0.5)), matching Kotlin's Math.round — .NET's
/// Math.Round is banker's rounding and would diverge on exact halves.
let fx (value: float) : int = fxSat (int64 (floor (value * float fxOne + 0.5)))

let fxToDouble (value: int) : float = float value / float fxOne

let fxAdd (a: int) (b: int) : int = fxSat (int64 a + int64 b)

let fxSub (a: int) (b: int) : int = fxSat (int64 a - int64 b)

/// The shift is arithmetic, so the product truncates toward negative infinity
/// rather than toward zero. The HDL must use an arithmetic shift for the same
/// reason — rounding differences here would show up as diff-test failures only
/// on negative operands.
let fxMul (a: int) (b: int) : int = fxSat ((int64 a * int64 b) >>> fxFracBits)

let fxNeg (a: int) : int = fxSat (int64 -(int64 a))

let fxAbs (a: int) : int = if a < 0 then fxNeg a else a

/// gplearn's protected-division guard band: a denominator with |b| <= 0.001
/// yields 1.0.
let fxDivEps : int = fx 0.001

/// Protected division. A near-zero divisor yields 1.0 so the operator is total.
/// Truncation is toward zero (integer division), unlike fxMul's arithmetic
/// shift toward negative infinity; each only has to agree with its own HDL arm.
let fxDiv (a: int) (b: int) : int =
    if fxAbs b <= fxDivEps then fxOne
    else fxSat ((int64 a <<< fxFracBits) / int64 b)

/// The reciprocal table for fxDivRecip: 512 entries indexed by the 9 mantissa
/// bits below the leading 1 of the normalized divisor. Entry idx holds
/// round(2^62 / mMid) for the bucket midpoint mMid — a u31 value, one BRAM18
/// in fabric.
let fxRecipTable : int[] =
    Array.init 512 (fun idx ->
        let mMid = (1L <<< 31) ||| (int64 idx <<< 22) ||| (1L <<< 21)
        int (((1L <<< 62) + mMid / 2L) / mMid))

/// Hardware-shaped protected division: normalize the divisor with a
/// leading-zero count, look up an approximate reciprocal, refine with one
/// Newton-Raphson step, multiply by the dividend, and shift back. Same guard
/// band as fxDiv; magnitudes truncate toward zero; result is approximate
/// (~2^-20 relative). This function is the normative spec the fabric arm
/// diffs against.
let fxDivRecip (a: int) (b: int) : int =
    if fxAbs b <= fxDivEps then fxOne
    else
        let negative = (a ^^^ b) < 0
        let ua = if a < 0 then -(int64 a) else int64 a // <= 2^31
        let ub = if b < 0 then -(int64 b) else int64 b // in (fxDivEps, 2^31]

        if ua = 0L then 0
        else
            // Normalize: m = ub <<< n with the leading 1 at bit 31.
            let n = System.Numerics.BitOperations.LeadingZeroCount(uint32 ub)
            let m = ub <<< n
            // r ~= 2^62 / m (u31): table seed, then one NR step.
            let r0 = int64 fxRecipTable[int ((m >>> 22) &&& 0x1FFL)]
            let d = (1L <<< 62) - m * r0 // residual, |d| ~ 2^53
            let r = r0 + ((r0 * (d >>> 22)) >>> 40)
            // a/b in Q16.16 = (ua * r) >>> (62 - 16 - n); n <= 25 given the
            // guard, so the shift is always a right shift.
            let qMag = (ua * r) >>> (46 - n)
            fxSat (if negative then -qMag else qMag)

/// The DIV opcode's active implementation — the A/B seam for the
/// reciprocal-divide gate. False = exact fxDiv; true = the hardware-shaped
/// fxDivRecip.
let mutable useRecipDiv = false

let fxDivActive (a: int) (b: int) : int =
    if useRecipDiv then fxDivRecip a b else fxDiv a b

/// Run `body` with the reciprocal divide active, then restore the flag.
///
/// **Any software twin judging hardware belongs in here.** The fabric's DIV arm
/// IS `fxDivRecip` — there is no build in which it is not — so a comparison run
/// under the default exact divide is comparing against a machine that does not
/// exist. The two agree on most operands, which is what makes the mistake
/// expensive: it passes until it doesn't. Measured 2026-08-10, on silicon and
/// then reproduced in the Sim — every offspring whose program contained a DIV
/// came back with a fitness 26-142 parts in 10^9 off, and every DIV-free one was
/// exact. `unitEngineDivSharing` had been passing for a day with DIV-bearing
/// programs whose operands happened not to separate the two divides.
let withRecipDiv (body: unit -> 'a) : 'a =
    let saved = useRecipDiv
    useRecipDiv <- true

    try
        body ()
    finally
        useRecipDiv <- saved

/// A clipped value. The saturation bounds are ±32768 in Q16.16, which no
/// meaningful result lands on exactly, so equality is a sound clipping test.
let isSaturated (value: int) : bool =
    value = System.Int32.MaxValue || value = System.Int32.MinValue
