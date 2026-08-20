/// The SRBench layer: datasets, ground-truth problems, and R^2 — the shape a
/// published symbolic-regression benchmark takes, so this engine's quality can
/// be put beside other people's rather than only beside itself.
///
/// The accuracy metric is evaluated through the SAME Q16.16 path the hardware
/// uses, deliberately. A double-precision score would flatter the design by
/// measuring an engine nobody runs; this measures the quality the accelerator
/// actually delivers.
module Warp11.Gep.Srbench

open Warp11.Gep.Fixed
open Warp11.Gep.Opcodes
open Warp11.Gep.Rng
open Warp11.Gep.Chromosome
open Warp11.Gep.Karva
open Warp11.Gep.Fitness
open Warp11.Gep.Engine

/// Feature rows and a target column in `float` — the SRBench-shaped input,
/// whether it came from a ground-truth formula or a CSV. `toFitnessCases`
/// converts to the engine's Q16.16 form once.
type Dataset =
    { featureNames: string list
      rows: float[] list
      target: float[] }

    member this.Size = this.target.Length
    member this.FeatureCount = this.featureNames.Length

let toFitnessCases (d: Dataset) =
    let n = d.FeatureCount
    let inputs = Array.zeroCreate<int> (d.Size * n)
    let targets = Array.zeroCreate<int> d.Size

    d.rows
    |> List.iteri (fun k row ->
        for f in 0 .. n - 1 do
            inputs[k * n + f] <- fx row[f])

    d.target |> Array.iteri (fun k v -> targets[k] <- fx v)
    fitnessCases n inputs targets

/// True when every value fits Q16.16 — a problem whose values pass ±32768 needs
/// rescaling before it means anything here. Worth checking rather than
/// assuming: silent saturation looks like a hard problem.
let fitsFixedPoint (d: Dataset) =
    d.rows |> List.forall (Array.forall (fun v -> abs v < 32000.0))
    && d.target |> Array.forall (fun v -> abs v < 32000.0)

/// A known formula sampled over documented variable ranges. Success is measured
/// two ways — R^2 on held-out data, and whether the recovered expression IS the
/// formula, which is the interesting one and the one SRBench calls symbolic
/// solution rate.
[<NoEquality; NoComparison>]
type GroundTruth =
    { name: string
      vars: string list
      ranges: (float * float) list
      truth: string
      functionSet: int[]
      formula: float[] -> float }

let private nextUnit (rng: GepRng) =
    float (uint32 (rng.NextWord())) / 4294967296.0

let sample (n: int) (rng: GepRng) (p: GroundTruth) =
    let rows =
        [ for _ in 1..n ->
            Array.init p.vars.Length (fun i ->
                let lo, hi = p.ranges[i]
                lo + (hi - lo) * nextUnit rng) ]

    { featureNames = p.vars
      rows = rows
      target = rows |> List.map p.formula |> Array.ofList }

/// Coefficient of determination — SRBench's accuracy metric. 1.0 is perfect,
/// 0.0 is no better than predicting the mean.
let rSquared (config: GepConfig) (dataset: Dataset) (c: Chromosome) =
    let mean = Array.average dataset.target
    let vars = Array.zeroCreate<int> config.variableCount
    let mutable ssRes = 0.0
    let mutable ssTot = 0.0

    dataset.rows
    |> List.iteri (fun k row ->
        for f in 0 .. dataset.FeatureCount - 1 do
            if f < vars.Length then vars[f] <- fx row[f]

        let predicted = fxToDouble (evaluateChromosome config vars c)
        let actual = dataset.target[k]
        ssRes <- ssRes + (predicted - actual) * (predicted - actual)
        ssTot <- ssTot + (actual - mean) * (actual - mean))

    if ssTot = 0.0 then 0.0 else 1.0 - ssRes / ssTot

/// A starter set of SRBench ground-truth (Feynman) equations chosen to fit the
/// current hardware: at most four variables, ranges that stay inside Q16.16, and
/// each declaring the function set it needs. The products need only +,-,*; the
/// ratio problems opt into `rationalSet`, which is what makes them a test of
/// protected division rather than of the search. Trig and exponential problems
/// wait on a transcendental function-set extension — see BACKLOG.
let feynmanStarter : GroundTruth list =
    let unit3 n = List.replicate n (1.0, 5.0)

    [ { name = "I.14.3  m*g*z"
        vars = [ "m"; "g"; "z" ]
        ranges = unit3 3
        truth = "m*g*z"
        functionSet = arithmeticSet
        formula = fun v -> v[0] * v[1] * v[2] }
      { name = "I.12.1  mu*Nn"
        vars = [ "mu"; "Nn" ]
        ranges = unit3 2
        truth = "mu*Nn"
        functionSet = arithmeticSet
        formula = fun v -> v[0] * v[1] }
      { name = "I.13.4  0.5*m*(u^2+v^2+w^2)"
        vars = [ "m"; "u"; "v"; "w" ]
        ranges = unit3 4
        truth = "0.5*m*(u^2+v^2+w^2)"
        functionSet = arithmeticSet
        formula = fun v -> 0.5 * v[0] * (v[1] * v[1] + v[2] * v[2] + v[3] * v[3]) }
      { name = "I.39.1  1.5*pr*V"
        vars = [ "pr"; "V" ]
        ranges = unit3 2
        truth = "1.5*pr*V"
        functionSet = arithmeticSet
        formula = fun v -> 1.5 * v[0] * v[1] }
      { name = "I.25.13  q/C"
        vars = [ "q"; "C" ]
        ranges = unit3 2
        truth = "q/C"
        functionSet = rationalSet
        formula = fun v -> v[0] / v[1] }
      // Coulomb, normalised (the 1/4*pi*eps constant is far outside Q16.16, so
      // it folds to 1): an inverse-square law needing division over a product.
      // This is the one the cluster solved exactly on silicon.
      { name = "I.12.2  q1*q2/r^2"
        vars = [ "q1"; "q2"; "r" ]
        ranges = unit3 3
        truth = "q1*q2/r^2"
        functionSet = rationalSet
        formula = fun v -> v[0] * v[1] / (v[2] * v[2]) } ]

/// How a candidate is encoded. Plain is four genes folded with a fixed ADD;
/// Adf n is n conventional genes plus a homeotic gene that evolves how to
/// combine them. Comparable by construction — same problem, same budget, same
/// operators, one field apart — which is what makes an ADF result evidence
/// rather than an anecdote.
type Encoding =
    | Plain
    | Adf of adfCount: int

let encodingName (e: Encoding) =
    match e with
    | Plain -> "plain"
    | Adf n -> $"adf{n}"

type RunParams =
    { populationSize: int
      generations: int
      trainSize: int
      testSize: int
      seed: int64
      encoding: Encoding
      /// Stop as soon as the fit is exact on train, checked every this many
      /// generations. Makes a reported time a TIME-TO-SOLUTION rather than a
      /// fixed budget, which is the only way a speed comparison across
      /// substrates means anything.
      earlyStopEvery: int }

let defaultRunParams =
    { populationSize = 1000
      generations = 500
      trainSize = 200
      testSize = 200
      seed = 42L
      encoding = Plain
      earlyStopEvery = 0 }

/// The configuration the Kotlin README records for the Coulomb solve: a small
/// population over few cases, restarted. Restart-sensitivity is the point —
/// about half of seeds find it — so a single run proves nothing either way.
let coulombRunParams =
    { populationSize = 256
      generations = 500
      trainSize = 32
      testSize = 200
      seed = 42L
      encoding = Plain
      earlyStopEvery = 25 }

type SrbenchResult =
    { name: string
      testR2: float
      expression: string
      generations: int
      millis: int64 }

/// Fit on a train sample, report R^2 on a SEPARATE test sample so the number is
/// honest rather than overfit, plus the recovered expression and wall time. The
/// GA here is the one the hardware pool accelerates: this measures solution
/// quality, the board measures speed.
let runProblem (p: GroundTruth) (params: RunParams) =
    let rng = GepRng params.seed
    let train = sample params.trainSize rng p
    let test = sample params.testSize rng p

    // Same geometry either way, so the ADF arm differs by exactly one gene (the
    // homeotic one) rather than by a whole re-tuned configuration.
    let config =
        match params.encoding with
        | Plain -> gepConfig (geneLayout 8 2) p.vars.Length 4 4 p.functionSet ADD
        | Adf n -> adfConfig (geneLayout 8 2) p.vars.Length n 4 p.functionSet

    let cases = toFitnessCases train
    let started = System.Diagnostics.Stopwatch.StartNew()

    let engine =
        GepEngine(config, cases, rng, params.populationSize, defaultGepParams, meanSquaredError)

    let mutable ran = 0

    if params.earlyStopEvery <= 0 then
        engine.Run(params.generations, ignore)
        ran <- params.generations
    else
        // Exact on train is the stop condition, checked on a stride so the
        // check itself does not dominate a cheap generation.
        let mutable go = true

        while go && ran < params.generations do
            let chunk = min params.earlyStopEvery (params.generations - ran)
            engine.Run(chunk, ignore)
            ran <- ran + chunk
            if rSquared config train engine.Best > 0.9999 then go <- false

    let millis = started.ElapsedMilliseconds

    { name = p.name
      testR2 = rSquared config test engine.Best
      expression = formatChromosome config p.vars engine.Best
      generations = ran
      millis = millis }

/// One problem over many seeds. The README's Coulomb row says the solve is
/// restart-sensitive — about half of seeds find it — so "did we solve it" is a
/// question about a seed DISTRIBUTION, and a single run answers neither way.
let runSeeds (p: GroundTruth) (params: RunParams) (seeds: int64 list) =
    let results = [ for s in seeds -> runProblem p { params with seed = s } ]
    // Three bars, and conflating them is how a benchmark claim goes wrong.
    // 0.999 is SRBench's accuracy criterion. 0.9999 is the Kotlin README's
    // OPERATIONAL one — the hardware early-stops there and the row is then
    // recorded as solved. 1.0-to-7-decimals is stricter than anything either
    // project used, and is reported because the difference is real: a run can
    // recover the exact STRUCTURE and still carry a constant that is only
    // approximately right (I.39.1 lands on 1.4993 where the fabric landed on
    // 1.49985, and neither is 1.5).
    let accurate = results |> List.filter (fun r -> r.testR2 > 0.999)
    let solved = results |> List.filter (fun r -> r.testR2 >= 0.9999)
    let exact = results |> List.filter (fun r -> r.testR2 > 0.9999999)
    let r2s = results |> List.map (fun r -> r.testR2)

    printfn
        "%-26s %-6s R2>0.999 %3d/%d   solved(>=0.9999) %3d/%d   1.0-to-7dp %3d/%d   best %.7f   median %.4f"
        p.name
        (encodingName params.encoding)
        accurate.Length
        seeds.Length
        solved.Length
        seeds.Length
        exact.Length
        seeds.Length
        (List.max r2s)
        (List.sort r2s |> List.item (results.Length / 2))

    match accurate with
    | [] -> ()
    | _ ->
        let best = results |> List.maxBy (fun r -> r.testR2)
        let fastest = accurate |> List.minBy (fun r -> r.generations)
        printfn "    best  R2 %.7f at gen %d: %s" best.testR2 best.generations best.expression

        if fastest.generations <> best.generations then
            printfn "    first at gen %d in %d ms" fastest.generations fastest.millis

    results

/// Plain against ADF on the same problem, the same seeds and the same budget.
/// Reported as two distributions rather than two numbers because both arms are
/// restart-sensitive: a single pair of runs cannot tell an encoding difference
/// from a lucky seed, and that is exactly the mistake this harness exists to
/// avoid.
let runEncodingPair (p: GroundTruth) (params: RunParams) (adfCount: int) (seeds: int64 list) =
    let plain = runSeeds p { params with encoding = Plain } seeds
    let adf = runSeeds p { params with encoding = Adf adfCount } seeds
    let bestOf (rs: SrbenchResult list) = rs |> List.map (fun r -> r.testR2) |> List.max
    let solvedOf (rs: SrbenchResult list) = rs |> List.filter (fun r -> r.testR2 >= 0.9999) |> List.length
    let millisOf (rs: SrbenchResult list) = rs |> List.sumBy (fun r -> r.millis)

    printfn
        "    -> solved %d/%d plain vs %d/%d adf%d   best %.7f vs %.7f   %d ms vs %d ms"
        (solvedOf plain)
        seeds.Length
        (solvedOf adf)
        seeds.Length
        adfCount
        (bestOf plain)
        (bestOf adf)
        (millisOf plain)
        (millisOf adf)

    plain, adf

let runStarterSet (params: RunParams) =
    printfn "SRBench ground-truth (Feynman starter set) — software GA, quality baseline"
    printfn "%s" (String.replicate 78 "=")
    let mutable solved = 0

    for p in feynmanStarter do
        let r = runProblem p params
        let hit = r.testR2 > 0.999
        if hit then solved <- solved + 1
        printfn "%-30s  R2=%7.4f  %5dms  %s" p.name r.testR2 r.millis (if hit then "SOLVED" else "")
        printfn "    truth: %s" p.truth
        printfn "    found: %s" r.expression

    printfn "%s" (String.replicate 78 "=")
    printfn "solved (R2 > 0.999): %d / %d" solved feynmanStarter.Length
    solved
