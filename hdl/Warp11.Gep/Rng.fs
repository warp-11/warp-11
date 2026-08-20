/// Host-side mirror of the fabric PRNG + the breeding engine's derivation
/// primitives. Word generator: xoshiro128++ (Blackman/Vigna), bit-identical to
/// the `xoshiro128pp` HDL module — same state, same stream, word for word —
/// which is what lets the fabric operator engine be diffed against the
/// operators exactly.
///
/// Derivations are chosen for loop-free, division-free hardware:
///  - Bernoulli: one word, one compare against a `p·2³²` threshold.
///  - NextBounded: Lemire multiply-shift `(word·n) >>> 32` — bias ≤ n/2³².
///  - CreepDeltaFx: the integer Irwin–Hall deviate (12 words).
///
/// Seeding: SplitMix64 expands a 64-bit seed into the 4×32 state (two outputs,
/// lo/hi words) — done host-side so the fabric core needs no 64×64 multiplies.
module Warp11.Gep.Rng

open Warp11.Gep.Fixed

let private rotl (x: int) (k: int) : int =
    (x <<< k) ||| int (uint32 x >>> (32 - k))

/// SplitMix64: two outputs, split lo/hi, give the 4×32 xoshiro state.
/// Writes s0..s3 at out[offset..offset+3].
let expandSeedInto (seed: int64) (out: int[]) (offset: int) =
    let mutable x = uint64 seed

    for k in 0 .. 1 do
        x <- x + 0x9E3779B97F4A7C15UL
        let mutable z = x
        z <- (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
        z <- (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
        z <- z ^^^ (z >>> 31)
        out[offset + 2 * k] <- int (uint32 z)
        out[offset + 2 * k + 1] <- int (uint32 (z >>> 32))

    if List.forall (fun i -> out[offset + i] = 0) [ 0 .. 3 ] then
        out[offset] <- 1 // the one degenerate point

let expandSeed (seed: int64) : int[] =
    let out = Array.zeroCreate 4
    expandSeedInto seed out 0
    out

/// Bernoulli threshold encoding: `round(p·2³²)` as a u32 held in an int
/// (p = 1.0 is unreachable by design).
let thresholdOf (p: float) : int =
    if p < 0.0 || p > 1.0 then
        failwith $"probability out of range: {p}"

    min (int64 (floor (p * 4294967296.0 + 0.5))) 0xFFFFFFFFL |> uint32 |> int

type GepRng private (s: int[]) =

    new(s0: int, s1: int, s2: int, s3: int) =
        if s0 = 0 && s1 = 0 && s2 = 0 && s3 = 0 then
            failwith "all-zero xoshiro state is degenerate"

        GepRng([| s0; s1; s2; s3 |])

    new(seed: int64) = GepRng(expandSeed seed)

    /// The state words as the pairing entry carries them (s0..s3).
    member _.State() : int[] = Array.copy s

    member _.NextWord() : int =
        let result = rotl (s[0] + s[3]) 7 + s[0]
        let t = s[1] <<< 9
        s[2] <- s[2] ^^^ s[0]
        s[3] <- s[3] ^^^ s[1]
        s[1] <- s[1] ^^^ s[2]
        s[0] <- s[0] ^^^ s[3]
        s[2] <- s[2] ^^^ t
        s[3] <- rotl s[3] 11
        result

    /// True with probability `threshold / 2³²`. One word, one unsigned compare.
    member this.Bernoulli(threshold: int) : bool =
        uint32 (this.NextWord()) < uint32 threshold

    /// Uniform in [0, n) via Lemire multiply-shift — one word, one multiply.
    member this.NextBounded(n: int) : int =
        int ((uint64 (uint32 (this.NextWord())) * uint64 (uint32 n)) >>> 32)

    /// The integer Irwin–Hall creep deviate over this stream: sum of 12
    /// uniform 32-bit words recentred, scaled by sigma in Q16.16.
    member this.CreepDeltaFx(sigmaFx: int) : int =
        let mutable sum = 0L

        for _ in 1 .. 12 do
            sum <- sum + (int64 (this.NextWord()) &&& 0xFFFFFFFFL)

        sum <- sum - (6L <<< 32)
        fxSat ((sum * int64 sigmaFx) >>> 32)
