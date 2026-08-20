/// The problem definition and scoring.
module Warp11.Gep.Fitness

open Warp11.Gep.Fixed
open Warp11.Gep.Karva
open Warp11.Gep.Chromosome

/// Fixed input/target rows a candidate is scored against. Inputs are flat with
/// a stride of variableCount, which is how they sit in BRAM — a row per cycle
/// streaming past a configured evaluator.
type FitnessCases =
    { variableCount: int
      inputs: int[]
      targets: int[] }

    member this.Size = this.targets.Length

    member this.LoadInputs(index: int, into: int[]) =
        System.Array.Copy(this.inputs, index * this.variableCount, into, 0, this.variableCount)

let fitnessCases (variableCount: int) (inputs: int[]) (targets: int[]) : FitnessCases =
    if inputs.Length <> targets.Length * variableCount then
        failwith $"expected {targets.Length * variableCount} inputs for {targets.Length} cases, got {inputs.Length}"
    // fx() clamps silently, so a problem whose values exceed Q16.16 would be
    // quietly rewritten into a different, flat-topped one — nothing downstream
    // can detect that, so it has to fail here.
    if Array.exists isSaturated targets then
        failwith "target values exceed Q16.16 (+/-32768); rescale or normalise the problem"

    if Array.exists isSaturated inputs then
        failwith "input values exceed Q16.16 (+/-32768); rescale or normalise the problem"

    { variableCount = variableCount; inputs = inputs; targets = targets }

/// saturatedCases is how many cases produced a clipped result — one sticky bit
/// per evaluation in hardware. Only clipped *outputs* are counted, so a zero
/// count is evidence rather than proof.
type ScoreReport =
    { meanSquaredError: float
      saturatedCases: int }

/// Decoding happens once per individual and evaluation once per case — the
/// same split the hardware makes, where decode configures the evaluator
/// lattice and cases then stream through it.
let forEachCase (config: GepConfig) (cases: FitnessCases) (action: int -> int -> unit) (c: Chromosome) =
    let genes = Array.init config.geneCount (fun g -> gene config g c)
    let levels = Array.map decode genes
    let banks = Array.init config.geneCount (fun g -> constantsOf config g c)
    let vars = Array.zeroCreate config.variableCount
    // Under a homeotic linkage this holds the ADF results, which the last gene
    // then reads as its variables.
    let adf = Array.zeroCreate config.AdfCount

    for k in 0 .. cases.Size - 1 do
        cases.LoadInputs(k, vars)
        let mutable acc = 0

        for g in 0 .. config.AdfCount - 1 do
            let value = evaluateLevels genes[g] levels[g] vars banks[g]
            adf[g] <- value

            acc <-
                match config.linkage with
                | Homeotic -> acc
                | LinkOp op -> if g = 0 then value else linkValues op acc value

        let output =
            match config.linkage with
            | LinkOp _ -> acc
            | Homeotic ->
                let hom = config.AdfCount
                evaluateLevels genes[hom] levels[hom] adf banks[hom]

        action k output

/// Scores a candidate over every case; lower error is better. Error
/// accumulates in a float because squared errors overflow Q16.16 long before
/// they stop being informative.
let score (config: GepConfig) (cases: FitnessCases) (c: Chromosome) : ScoreReport =
    let mutable total = 0.0
    let mutable saturated = 0

    c
    |> forEachCase config cases (fun k output ->
        if isSaturated output then saturated <- saturated + 1
        let error = fxToDouble output - fxToDouble cases.targets[k]
        total <- total + error * error)

    { meanSquaredError = total / float cases.Size
      saturatedCases = saturated }

let meanSquaredError (config: GepConfig) (cases: FitnessCases) (c: Chromosome) : float =
    (score config cases c).meanSquaredError

/// Midpoint of the 0/1 class encoding.
let decisionThreshold : int = fxOne / 2

let private margin = 0.5

/// Hinge-style margin loss: penalises a case only until it sits `margin` clear
/// of the decision threshold on the correct side. This is the fitness to
/// select on for classification — MSE against 0/1 targets punishes an
/// expression for the *magnitude* of its output.
let marginLoss (config: GepConfig) (cases: FitnessCases) (c: Chromosome) : float =
    let threshold = fxToDouble decisionThreshold
    let mutable total = 0.0

    c
    |> forEachCase config cases (fun k output ->
        let distance = fxToDouble output - threshold
        let signed = if cases.targets[k] > decisionThreshold then distance else -distance
        let slack = margin - signed
        if slack > 0.0 then total <- total + slack)

    total / float cases.Size

/// Fraction of cases misclassified against 0/1 targets. Report this, but do
/// not select on it — it is piecewise constant and offers evolution no
/// gradient.
let classificationError (config: GepConfig) (cases: FitnessCases) (threshold: int) (c: Chromosome) : float =
    let mutable wrong = 0

    c
    |> forEachCase config cases (fun k output ->
        let predicted = if output > threshold then 1 else 0
        let actual = if cases.targets[k] > threshold then 1 else 0
        if predicted <> actual then wrong <- wrong + 1)

    float wrong / float cases.Size

/// The exact fixed-point sum-of-squared-error the hardware engine accumulates:
/// per case, err = saturating(result - target), then err*err in Q16.16, summed
/// as a 64-bit integer. Diverges from meanSquaredError's float math on purpose
/// — this one is the bit-exact spec the engine's fitness output is diff-tested
/// against, not the value the software GA selects on.
let sumSquaredError (results: int[]) (targets: int[]) : int64 =
    let mutable acc = 0L

    for i in 0 .. results.Length - 1 do
        let err = fxSub results[i] targets[i]
        acc <- acc + (int64 (fxMul err err) &&& 0xFFFFFFFFL)

    acc

/// Samples count evenly spaced points of a one-variable function.
let sampleFunction (count: int) (from: float) (until: float) (f: float -> float) : FitnessCases =
    if count < 2 then failwith $"need at least two samples: {count}"
    let inputs = Array.zeroCreate count
    let targets = Array.zeroCreate count

    for i in 0 .. count - 1 do
        let x = from + (until - from) * float i / float (count - 1)
        inputs[i] <- fx x
        targets[i] <- fx (f x)

    fitnessCases 1 inputs targets
