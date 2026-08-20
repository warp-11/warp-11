/// The problems the Kotlin engine was judged on, ported. These are what the
/// search is *for* — every rung below them (the ALU, the lane, the cluster)
/// exists to run these faster, and porting them is what lets the F# stack be
/// compared with the Kotlin one on results rather than on cycle counts.
///
/// Two families, and they answer different questions. Symbolic regression asks
/// whether the search finds a known formula; classification asks whether the
/// evaluator needs comparison opcodes at all, which is a question about the
/// instruction set rather than the GA.
module Warp11.Gep.Problems

open Warp11.Gep.Fixed
open Warp11.Gep.Opcodes
open Warp11.Gep.Rng
open Warp11.Gep.Chromosome
open Warp11.Gep.Karva
open Warp11.Gep.Fitness
open Warp11.Gep.Engine

/// A uniform draw in [0, 1) off the same xoshiro stream the rest of the engine
/// uses. Kotlin sampled with `kotlin.random`, so the drawn CASES differ between
/// the two ports — these are benchmarks, not bit-exact oracles, and the
/// comparison lives at the level of solved-or-not and R^2.
let private nextUnit (rng: GepRng) =
    float (uint32 (rng.NextWord())) / 4294967296.0

/// Fitness cases at random points of a box, rather than `sampleFunction`'s even
/// sweep of one variable — what a multi-variable target needs.
let sampleRandom (count: int) (variableCount: int) (from: float) (until: float) (rng: GepRng) (f: float[] -> float) =
    let inputs = Array.zeroCreate<int> (count * variableCount)
    let targets = Array.zeroCreate<int> count
    let point = Array.zeroCreate<float> variableCount

    for i in 0 .. count - 1 do
        for v in 0 .. variableCount - 1 do
            point[v] <- from + (until - from) * nextUnit rng
            inputs[i * variableCount + v] <- fx point[v]

        targets[i] <- fx (f point)

    fitnessCases variableCount inputs targets

// ---------------------------------------------------------------------------
// Symbolic regression

/// Ferreira's canonical benchmark.
let quarticPolynomial (x: float) = x * x * x * x + x * x * x + x * x + x

/// Two variables and three constants that are not small integers, so it cannot
/// be built from terminals and arithmetic alone. Exercises the constant bank,
/// which the quartic never touches.
let bilinearWithConstants (v: float[]) = 3.7 * v[0] * v[1] + 2.1 * v[0] - 5.3

[<NoEquality; NoComparison>]
type Benchmark =
    { name: string
      config: GepConfig
      cases: FitnessCases
      populationSize: int
      generations: int
      varNames: string list }

let symbolicBenchmarks () =
    let arithmetic = arithmeticSet

    [ { name = "quartic  x^4 + x^3 + x^2 + x"
        config = gepConfig (geneLayout 8 2) 1 4 0 arithmetic ADD
        // The quartic already reaches 30 at x=2 and Q16.16 saturates past
        // 32768, so a wider sweep would clip the targets rather than stress
        // the search.
        cases = sampleFunction 50 -2.0 2.0 quarticPolynomial
        populationSize = 200
        generations = 100
        varNames = [ "x" ] }
      { name = "bilinear 3.7xy + 2.1x - 5.3"
        config = gepConfig (geneLayout 6 2) 2 3 4 arithmetic ADD
        cases = sampleRandom 60 2 -3.0 3.0 (GepRng 11L) bilinearWithConstants
        populationSize = 200
        generations = 300
        varNames = [ "x"; "y" ] } ]

let runSymbolic (b: Benchmark) =
    let engine = GepEngine(b.config, b.cases, GepRng 42L, b.populationSize)
    let interval = max 1 (b.generations / 10)
    printfn $"=== {b.name} ==="
    printfn "gen %4d  mse %.6g" 0 engine.BestError

    engine.Run(
        b.generations,
        fun e ->
            if e.Generation % interval = 0 then
                printfn "gen %4d  mse %.6g" e.Generation e.BestError
    )

    printfn "best %.6g" engine.BestError
    printfn "%s" (formatChromosome b.config b.varNames engine.Best)
    printfn ""
    engine.BestError

// ---------------------------------------------------------------------------
// Classification
//
// Does the evaluator need comparison opcodes, or is thresholding a real-valued
// expression enough? Two boundaries answer it. A circle is smooth and
// polynomial, so arithmetic alone should suffice; a box is a conjunction of
// thresholds — the shape a trading rule takes — and is where comparisons should
// earn their place if they are going to. Running both function sets against
// both targets isolates the operators from the problem.

/// Class 1 inside the unit circle: a smooth, polynomial boundary.
let insideCircle (v: float[]) = if v[0] * v[0] + v[1] * v[1] < 1.0 then 1.0 else 0.0

/// Class 1 inside an axis-aligned box: a conjunction of thresholds.
let insideBox (v: float[]) = if v[0] > 0.5 && v[1] < -0.2 then 1.0 else 0.0

let classificationSamples = 300
let classificationGenerations = 300

let private classificationCases (target: float[] -> float) =
    sampleRandom classificationSamples 2 -2.0 2.0 (GepRng 17L) target

let runClassificationSeed (seed: int64) (target: float[] -> float) (functionSet: int[]) =
    let config = gepConfig (geneLayout 8 2) 2 3 4 functionSet ADD
    let cases = classificationCases target

    let engine =
        GepEngine(config, cases, GepRng seed, 200, defaultGepParams, marginLoss)

    engine.Run(classificationGenerations, ignore)
    classificationError config cases decisionThreshold engine.Best, formatChromosome config [ "x"; "y" ] engine.Best

let runClassification (target: float[] -> float) (functionSet: int[]) (label: string) =
    let config = gepConfig (geneLayout 8 2) 2 3 4 functionSet ADD
    let cases = classificationCases target
    // Selection runs on margin loss: a misclassification count is too flat to
    // search on, and MSE against 0/1 targets penalises magnitude rather than
    // separation. Accuracy is the REPORTED metric, never the objective.
    let engine =
        GepEngine(config, cases, GepRng 42L, 200, defaultGepParams, marginLoss)

    engine.Run(classificationGenerations, ignore)
    let errorRate = classificationError config cases decisionThreshold engine.Best
    printfn "  %-12s err %.4f   %s" label errorRate (formatChromosome config [ "x"; "y" ] engine.Best)
    errorRate

/// One boundary against both function sets, under the majority-vote baseline.
/// The baseline is not decoration: without it an error rate is uninterpretable —
/// a run that found nothing at all scores whatever the class balance is, and
/// reads like a result.
let reportClassification (name: string) (target: float[] -> float) =
    let cases = classificationCases target
    let positives = cases.targets |> Array.filter (fun t -> t > 0) |> Array.length
    let baseline = float (min positives (classificationSamples - positives)) / float classificationSamples

    printfn
        "=== %s (class 1 in %.1f%% of cases, majority-vote err %.4f) ==="
        name
        (100.0 * float positives / float classificationSamples)
        baseline

    let arithmetic = runClassification target arithmeticSet "arithmetic"
    let comparison = runClassification target comparisonSet "with > <"
    printfn ""
    baseline, arithmetic, comparison

/// The same question asked properly. A single seed cannot separate "the
/// operators help" from "that seed got lucky" — and on the circle it visibly
/// does not: the two ports disagree on that cell while agreeing on the other
/// three, purely because their samplers differ. So run every cell over several
/// seeds and report the spread. This is the one place the F# port improves on
/// the Kotlin original rather than mirroring it.
let reportClassificationSeeds (seeds: int64 list) (name: string) (target: float[] -> float) =
    let cases = classificationCases target
    let positives = cases.targets |> Array.filter (fun t -> t > 0) |> Array.length
    let baseline = float (min positives (classificationSamples - positives)) / float classificationSamples

    printfn
        "=== %s (class 1 in %.1f%%, majority-vote err %.4f, %d seeds) ==="
        name
        (100.0 * float positives / float classificationSamples)
        baseline
        seeds.Length

    for fset, label in [ arithmeticSet, "arithmetic"; comparisonSet, "with > <" ] do
        let errors = [ for s in seeds -> fst (runClassificationSeed s target fset) ]
        let beat = errors |> List.filter (fun e -> e < baseline - 1e-9) |> List.length

        printfn
            "  %-12s best %.4f  median %.4f  worst %.4f   beat the baseline %d/%d"
            label
            (List.min errors)
            (List.sort errors |> List.item (errors.Length / 2))
            (List.max errors)
            beat
            seeds.Length

    printfn ""
