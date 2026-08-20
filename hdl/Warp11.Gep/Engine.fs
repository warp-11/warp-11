/// Generational loop with elitism and tournament selection.
///
/// Tournament rather than roulette because it needs only comparators — no
/// cumulative sums, no division — which is what makes it the selection scheme
/// worth putting in hardware.
///
/// Unlike the Kotlin engine (which drives a separate software operator set on
/// kotlin.Random), this engine draws *everything* from one GepRng stream and
/// breeds through hwBreedOffspring — one pairing entry, one offspring, exactly
/// the fabric's model. There is a single operator implementation to keep
/// correct, and an engine trajectory is reproducible from one 64-bit seed.
module Warp11.Gep.Engine

open Warp11.Gep.Fixed
open Warp11.Gep.Rng
open Warp11.Gep.Chromosome
open Warp11.Gep.HwBreeding
open Warp11.Gep.Fitness

/// Per-symbol probability for mutation; whole-operator probability for the
/// rest. That asymmetry is Ferreira's: mutation is meant to land roughly two
/// point changes per chromosome, so its rate scales with chromosome length.
type OperatorRates =
    { mutation: float
      inversion: float
      isTransposition: float
      risTransposition: float
      geneTransposition: float }

let defaultOperatorRates =
    { mutation = 0.05
      inversion = 0.1
      isTransposition = 0.1
      risTransposition = 0.1
      geneTransposition = 0.1 }

type RecombinationRates =
    { onePoint: float
      twoPoint: float
      gene: float }

let defaultRecombinationRates = { onePoint = 0.3; twoPoint = 0.3; gene = 0.1 }

/// replacement explores the constant range, creep refines within it. Both are
/// needed: replacement alone never converges on a precise coefficient, and
/// creep alone cannot escape the neighbourhood of its starting draw.
type ConstantRates =
    { replacement: float
      creep: float
      creepSigma: float }

let defaultConstantRates = { replacement = 0.05; creep = 0.3; creepSigma = 0.1 }

type GepParams =
    { operators: OperatorRates
      recombination: RecombinationRates
      constants: ConstantRates
      tournamentSize: int
      elitism: int }

let defaultGepParams =
    { operators = defaultOperatorRates
      recombination = defaultRecombinationRates
      constants = defaultConstantRates
      tournamentSize = 3
      elitism = 1 }

let thresholdsFrom (parameters: GepParams) (constantRange: float) : GepBreedThresholds =
    { onePoint = thresholdOf parameters.recombination.onePoint
      twoPoint = thresholdOf parameters.recombination.twoPoint
      geneRecomb = thresholdOf parameters.recombination.gene
      mutation = thresholdOf parameters.operators.mutation
      constReplace = thresholdOf parameters.constants.replacement
      creep = thresholdOf parameters.constants.creep
      inversion = thresholdOf parameters.operators.inversion
      isTrans = thresholdOf parameters.operators.isTransposition
      risTrans = thresholdOf parameters.operators.risTransposition
      geneTrans = thresholdOf parameters.operators.geneTransposition
      creepSigmaFx = fx parameters.constants.creepSigma
      constRangeFx = fx constantRange }

type GepEngine(config: GepConfig, cases: FitnessCases, rng: GepRng, populationSize: int, parameters: GepParams, fitness: GepConfig -> FitnessCases -> Chromosome -> float) =

    let thresholds = thresholdsFrom parameters defaultConstantRange
    let rangeFx = fx defaultConstantRange

    do
        if cases.variableCount <> config.variableCount then
            failwith $"cases supply {cases.variableCount} variables, config expects {config.variableCount}"

        if parameters.elitism < 0 || parameters.elitism >= populationSize then
            failwith $"elitism out of range: {parameters.elitism}"

    let mutable population =
        List.init populationSize (fun _ -> hwRandomChromosome config rangeFx rng)

    let mutable errors : float[] =
        population |> List.map (fitness config cases) |> Array.ofList

    let mutable generation = 0

    new(config, cases, rng, populationSize) =
        GepEngine(config, cases, rng, populationSize, defaultGepParams, meanSquaredError)

    member _.Population = population
    member _.Errors = errors
    member _.Generation = generation

    member _.BestIndex =
        [ 0 .. errors.Length - 1 ] |> List.minBy (fun i -> errors[i])

    member this.Best = population[this.BestIndex]
    member this.BestError = errors[this.BestIndex]

    member private _.Select() : Chromosome =
        let mutable winner = rng.NextBounded population.Length

        for _ in 2 .. parameters.tournamentSize do
            let challenger = rng.NextBounded population.Length
            if errors[challenger] < errors[winner] then winner <- challenger

        population[winner]

    member this.Step() =
        let next = ResizeArray<Chromosome>(population.Length)

        if parameters.elitism = 1 then
            next.Add population[this.BestIndex]
        elif parameters.elitism > 1 then
            let ranked = [ 0 .. errors.Length - 1 ] |> List.sortBy (fun i -> errors[i])

            for i in 0 .. parameters.elitism - 1 do
                next.Add population[ranked[i]]

        // One pairing entry, one offspring — the fabric's breeding model.
        while next.Count < population.Length do
            let parentA = this.Select()
            let parentB = this.Select()
            next.Add(hwBreedOffspring parentA parentB config thresholds rng)

        population <- List.ofSeq next
        errors <- population |> List.map (fitness config cases) |> Array.ofList
        generation <- generation + 1

    member this.Run(generations: int, onGeneration: GepEngine -> unit) =
        for _ in 1 .. generations do
            this.Step()
            onGeneration this
