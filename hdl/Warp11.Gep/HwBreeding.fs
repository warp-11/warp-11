/// The breeding semantics the fabric operator engine implements — this file IS
/// the specification the HDL FSM mirrors, executed on GepRng's loop-free
/// primitives (threshold Bernoulli, Lemire bounded, Irwin–Hall creep). One
/// pairing entry produces **one** offspring: the child starts as parentA, a
/// shadow starts as parentB, the recombination gates run the usual pairwise
/// swaps across both, and only the child is varied and emitted (the
/// complementary child comes from a second entry with swapped parents and its
/// own seed).
///
/// Draw order is normative — the HDL consumes the identical word sequence:
///  1. one-point gate; hit → cut = bounded(len)
///  2. two-point gate; hit → bounded(len) ×2
///  3. gene-recombination gate; hit → bounded(geneCount)
///  4. mutation: per symbol, gate; hit → bounded(F+T) head / bounded(T) tail
///  5. constant replacement: per constant, gate; hit → bounded(2·rangeFx+1)
///  6. creep: per constant, gate; hit → 12 words (Irwin–Hall)
///  7. inversion gate (skipped without a draw when head < 2); hit →
///     bounded(geneCount), bounded(head−1), bounded(head−start−1)
///  8. IS gate (same head<2 skip); hit → bounded(min(max,head−1)),
///     bounded(geneCount), bounded(len−length+1), bounded(geneCount),
///     bounded(head−length)
///  9. RIS gate; hit → bounded(geneCount), bounded(head), [terminal scan —
///     no draws], then bounded(min(max, head−start)) only if a function found
/// 10. gene-transposition gate (skipped without a draw when the conventional
///     gene count is < 2); hit → bounded(conventional genes − 1)
///
/// The operators apply unchanged to a homeotic (ADF) chromosome — same flat
/// symbol array, same uniform geometry — with two role gates, both of which are
/// no-ops when every gene is conventional, so the plain word sequence and the
/// plain outcome are untouched: IS transposition applies only when its source
/// and target genes have the same role (the draws happen either way), and gene
/// transposition ranges over the conventional genes only. The HDL FSM mirrors
/// the plain path; a hardware ADF would have to adopt these two gates.
module Warp11.Gep.HwBreeding

open Warp11.Gep.Fixed
open Warp11.Gep.Rng
open Warp11.Gep.Opcodes
open Warp11.Gep.Chromosome

let defaultMaxTransposon = 3

/// Ten named probability thresholds, one per genetic operator, plus the two
/// Q16.16 policy constants. The list *is* the policy.
type GepBreedThresholds =
    { onePoint: int
      twoPoint: int
      geneRecomb: int
      mutation: int
      constReplace: int
      creep: int
      inversion: int
      isTrans: int
      risTrans: int
      geneTrans: int
      creepSigmaFx: int
      constRangeFx: int }

/// Swaps symbols over [from, until). A constant bank travels with its gene
/// only when that gene crosses over whole; for a gene split by the point, the
/// offspring keeps the bank it already had — disruption rather than
/// invalidity.
let crossoverInPlace (a: Chromosome) (b: Chromosome) (config: GepConfig) (from: int) (until: int) =
    let last = min until config.chromosomeLength

    for i in from .. last - 1 do
        let t = a.symbols[i]
        a.symbols[i] <- b.symbols[i]
        b.symbols[i] <- t

    for g in 0 .. config.geneCount - 1 do
        if config.GeneStart g >= from && config.GeneStart g + config.layout.length <= last then
            for k in 0 .. config.constantCount - 1 do
                let i = config.ConstantStart g + k
                let t = a.constants[i]
                a.constants[i] <- b.constants[i]
                b.constants[i] <- t

/// Symbol draws are per-gene because a homeotic gene's terminals are ADF calls,
/// not variables. With one alphabet for every gene — every plain config — these
/// are the original single-alphabet draws, word for word.
let private hwHeadSymbol (config: GepConfig) (gene: int) (rng: GepRng) : int =
    let terminals = config.TerminalsOf gene
    let i = rng.NextBounded(config.functionSet.Length + terminals.Length)

    if i < config.functionSet.Length then config.functionSet[i]
    else terminals[i - config.functionSet.Length]

let private hwTailSymbol (config: GepConfig) (gene: int) (rng: GepRng) : int =
    let terminals = config.TerminalsOf gene
    terminals[rng.NextBounded terminals.Length]

/// Uniform Q16.16 in [−rangeFx, +rangeFx] — one word through Lemire over the
/// span.
let private hwRandomConstantFx (rangeFx: int) (rng: GepRng) : int =
    rng.NextBounded(2 * rangeFx + 1) - rangeFx

let private hwMutate (c: Chromosome) (config: GepConfig) (thresholds: GepBreedThresholds) (rng: GepRng) =
    for i in 0 .. c.symbols.Length - 1 do
        if rng.Bernoulli thresholds.mutation then
            let offset = i % config.layout.length
            let gene = config.GeneOf i

            c.symbols[i] <-
                if config.layout.IsHead offset then hwHeadSymbol config gene rng
                else hwTailSymbol config gene rng

let private hwReplaceConstants (c: Chromosome) (thresholds: GepBreedThresholds) (rng: GepRng) =
    for k in 0 .. c.constants.Length - 1 do
        if rng.Bernoulli thresholds.constReplace then
            c.constants[k] <- hwRandomConstantFx thresholds.constRangeFx rng

let private hwCreepConstants (c: Chromosome) (thresholds: GepBreedThresholds) (rng: GepRng) =
    for k in 0 .. c.constants.Length - 1 do
        if rng.Bernoulli thresholds.creep then
            c.constants[k] <- fxAdd c.constants[k] (rng.CreepDeltaFx thresholds.creepSigmaFx)

let private hwInvert (c: Chromosome) (config: GepConfig) (thresholds: GepBreedThresholds) (rng: GepRng) =
    let head = config.layout.headLength

    if head >= 2 && rng.Bernoulli thresholds.inversion then
        let baseIdx = config.GeneStart(rng.NextBounded config.geneCount)
        let start = rng.NextBounded(head - 1)
        let last = start + 1 + rng.NextBounded(head - start - 1)
        let mutable lo = start
        let mutable hi = last

        while lo < hi do
            let t = c.symbols[baseIdx + lo]
            c.symbols[baseIdx + lo] <- c.symbols[baseIdx + hi]
            c.symbols[baseIdx + hi] <- t
            lo <- lo + 1
            hi <- hi - 1

let private hwTransposeIS (c: Chromosome) (config: GepConfig) (thresholds: GepBreedThresholds) (rng: GepRng) =
    let head = config.layout.headLength

    if head >= 2 && rng.Bernoulli thresholds.isTrans then
        let length = 1 + rng.NextBounded(min defaultMaxTransposon (head - 1))
        let sourceGene = rng.NextBounded config.geneCount

        let source =
            config.GeneStart sourceGene
            + rng.NextBounded(config.layout.length - length + 1)

        let targetGene = rng.NextBounded config.geneCount
        let baseIdx = config.GeneStart targetGene
        let target = 1 + rng.NextBounded(head - length)

        // The only operator that copies symbols from one gene into another, so
        // the only one that could reinterpret them under a different alphabet:
        // a conventional gene's `variable 5` means ADF 5 inside a homeotic
        // gene, which may not exist. Roles must match — checked after the draws
        // rather than before, so the word sequence is the same either way and
        // every plain config breeds exactly as before.
        if config.IsHomeoticGene sourceGene = config.IsHomeoticGene targetGene then
            let run = Array.init length (fun i -> c.symbols[source + i])

            for i in head - 1 .. -1 .. target + length do
                c.symbols[baseIdx + i] <- c.symbols[baseIdx + i - length]

            for i in 0 .. length - 1 do
                c.symbols[baseIdx + target + i] <- run[i]

let private hwTransposeRIS (c: Chromosome) (config: GepConfig) (thresholds: GepBreedThresholds) (rng: GepRng) =
    if rng.Bernoulli thresholds.risTrans then
        let head = config.layout.headLength
        let baseIdx = config.GeneStart(rng.NextBounded config.geneCount)
        let mutable start = rng.NextBounded head

        while start < head && isTerminal c.symbols[baseIdx + start] do
            start <- start + 1

        if start < head then
            let length = 1 + rng.NextBounded(min defaultMaxTransposon (head - start))
            let run = Array.init length (fun i -> c.symbols[baseIdx + start + i])

            for i in head - 1 .. -1 .. length do
                c.symbols[baseIdx + i] <- c.symbols[baseIdx + i - length]

            for i in 0 .. length - 1 do
                c.symbols[baseIdx + i] <- run[i]

/// Rotates a gene to the front. Only conventional genes take part: a homeotic
/// gene rotated into slot 0 would be read under the conventional alphabet, and
/// a conventional gene rotated into the last slot under the homeotic one. In
/// plain mode every gene is conventional, so this is the original operator.
let private hwTransposeGene (c: Chromosome) (config: GepConfig) (thresholds: GepBreedThresholds) (rng: GepRng) =
    if config.AdfCount >= 2 && rng.Bernoulli thresholds.geneTrans then
        let geneLength = config.layout.length
        let g = 1 + rng.NextBounded(config.AdfCount - 1)
        let moved = Array.sub c.symbols (config.GeneStart g) geneLength
        System.Array.Copy(c.symbols, 0, c.symbols, geneLength, config.GeneStart g)
        System.Array.Copy(moved, 0, c.symbols, 0, geneLength)

        if config.constantCount > 0 then
            let bank = Array.sub c.constants (config.ConstantStart g) config.constantCount
            System.Array.Copy(c.constants, 0, c.constants, config.constantCount, config.ConstantStart g)
            System.Array.Copy(bank, 0, c.constants, 0, config.constantCount)

/// Breed one offspring from a pairing (parentA, parentB, thresholds, stream).
let hwBreedOffspring (parentA: Chromosome) (parentB: Chromosome) (config: GepConfig) (thresholds: GepBreedThresholds) (rng: GepRng) : Chromosome =
    let child = parentA.Copy()
    let shadow = parentB.Copy()

    if rng.Bernoulli thresholds.onePoint then
        crossoverInPlace child shadow config (rng.NextBounded config.chromosomeLength) config.chromosomeLength

    if rng.Bernoulli thresholds.twoPoint then
        let a = rng.NextBounded config.chromosomeLength
        let b = rng.NextBounded config.chromosomeLength
        crossoverInPlace child shadow config (min a b) (max a b + 1)

    if rng.Bernoulli thresholds.geneRecomb then
        let start = config.GeneStart(rng.NextBounded config.geneCount)
        crossoverInPlace child shadow config start (start + config.layout.length)

    hwMutate child config thresholds rng
    hwReplaceConstants child thresholds rng
    hwCreepConstants child thresholds rng
    hwInvert child config thresholds rng
    hwTransposeIS child config thresholds rng
    hwTransposeRIS child config thresholds rng
    hwTransposeGene child config thresholds rng
    child

/// A random chromosome over the same GepRng stream the breeding gates use —
/// head symbols from functions+terminals, tail from terminals, constants
/// uniform in [−rangeFx, +rangeFx]. (The Kotlin side generates initial
/// populations from kotlin.Random; the F# oracle draws everything from the
/// one hardware-mirrorable stream instead.)
let hwRandomChromosome (config: GepConfig) (rangeFx: int) (rng: GepRng) : Chromosome =
    let symbols = Array.zeroCreate config.chromosomeLength

    for g in 0 .. config.geneCount - 1 do
        let baseIdx = config.GeneStart g

        for i in 0 .. config.layout.length - 1 do
            symbols[baseIdx + i] <-
                if config.layout.IsHead i then hwHeadSymbol config g rng
                else hwTailSymbol config g rng

    let constants =
        Array.init (config.geneCount * config.constantCount) (fun _ -> hwRandomConstantFx rangeFx rng)

    { symbols = symbols; constants = constants }
