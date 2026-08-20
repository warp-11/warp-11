/// Golden-vector verification against the Kotlin GEP engine.
///
/// The vector file is written by the Kotlin exporter
/// (`:examples:gep:runGoldenVectors`) and carries *data, not seeds of
/// kotlin.Random*: parents, genes and inputs appear as recorded integers, so
/// the only stream ever replayed is GepRng's — the portable, normative one.
///
/// Format: line-oriented, whitespace-separated decimal integers. Each section
/// opens with a keyword line and closes with `end`; `#` lines and blanks are
/// ignored.
module Warp11.Gep.Golden

open Warp11.Gep.Rng
open Warp11.Gep.Opcodes
open Warp11.Gep.Chromosome
open Warp11.Gep.MicroProgram
open Warp11.Gep.HwBreeding
open Warp11.Gep.Fitness
open Warp11.Gep.Engine

type private Reader(lines: string[]) =
    let mutable pos = 0

    member _.AtEnd = pos >= lines.Length

    member _.Next() : string[] =
        let mutable line = [||]

        while line.Length = 0 && pos < lines.Length do
            let raw = lines[pos].Trim()
            pos <- pos + 1

            if raw.Length > 0 && not (raw.StartsWith "#") then
                line <- raw.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)

        line

let private ints (tokens: string[]) (from: int) : int[] =
    Array.sub tokens from (tokens.Length - from) |> Array.map int

/// `key <n> <v0> .. <v(n-1)>` — a counted int array.
let private counted (tokens: string[]) : int[] =
    let n = int tokens[1]
    Array.init n (fun i -> int tokens[2 + i])

let private parseConfig (tokens: string[]) : GepConfig =
    // config <head> <maxArity> <vars> <consts> <genes> <linkOp> <fnCount> <fn...>
    let functions = Array.init (int tokens[7]) (fun i -> int tokens[8 + i])
    gepConfig (geneLayout (int tokens[1]) (int tokens[2])) (int tokens[3]) (int tokens[5]) (int tokens[4]) functions (int tokens[6])

type VerifyReport =
    { passed: int
      failed: int
      failures: string list }

let verify (path: string) : VerifyReport =
    let reader = Reader(System.IO.File.ReadAllLines path)
    let mutable passed = 0
    let failures = ResizeArray<string>()

    let check (name: string) (ok: bool) =
        if ok then passed <- passed + 1 else failures.Add name

    let expectEnd (section: string) =
        let tokens = reader.Next()

        if tokens.Length = 0 || tokens[0] <> "end" then
            failwith $"malformed golden file: {section} not closed by 'end'"

    let mutable tokens = reader.Next()

    while tokens.Length > 0 do
        match tokens[0] with
        | "rng" ->
            // One stream, consumed in recorded order: expanded state, 16
            // words, 8 Bernoulli gates, 8 bounded draws, 4 creep deltas.
            let seed = int64 tokens[1]
            let rng = GepRng(seed)
            let state = reader.Next() |> counted
            check $"rng {seed} state" (rng.State() = state)
            let words = reader.Next() |> counted
            check $"rng {seed} words" (Array.init words.Length (fun _ -> rng.NextWord()) = words)
            let bern = reader.Next()
            let threshold = int bern[1]
            let expected = ints bern 2 |> Array.map (fun v -> v <> 0)
            check $"rng {seed} bernoulli" (Array.init expected.Length (fun _ -> rng.Bernoulli threshold) = expected)
            let bounded = reader.Next()
            let n = int bounded[1]
            let draws = ints bounded 2
            check $"rng {seed} bounded" (Array.init draws.Length (fun _ -> rng.NextBounded n) = draws)
            let creep = reader.Next()
            let sigmaFx = int creep[1]
            let deltas = ints creep 2
            check $"rng {seed} creep" (Array.init deltas.Length (fun _ -> rng.CreepDeltaFx sigmaFx) = deltas)
            expectEnd "rng"
        | "thresholds" ->
            // The 12 threshold words Kotlin derives from the default params —
            // exercises thresholdOf and fx against GepBreedThresholds.from.
            let values = reader.Next() |> fun t -> ints t 1
            let d = thresholdsFrom defaultGepParams defaultConstantRange

            let ours =
                [| d.onePoint; d.twoPoint; d.geneRecomb; d.mutation; d.constReplace; d.creep
                   d.inversion; d.isTrans; d.risTrans; d.geneTrans; d.creepSigmaFx; d.constRangeFx |]

            check "thresholds default" (ours = values)
            expectEnd "thresholds"
        | "divrecip" ->
            let mutable case = reader.Next()

            while case[0] <> "end" do
                // case <a> <b> <expected>
                let a = int case[1]
                let b = int case[2]
                check $"divrecip {a}/{b}" (Fixed.fxDivRecip a b = int case[3])
                case <- reader.Next()
        | "breed" ->
            let seed = int64 tokens[1]
            let config = parseConfig (reader.Next())
            let thresholdValues = reader.Next() |> fun t -> ints t 1

            let thresholds =
                { onePoint = thresholdValues[0]
                  twoPoint = thresholdValues[1]
                  geneRecomb = thresholdValues[2]
                  mutation = thresholdValues[3]
                  constReplace = thresholdValues[4]
                  creep = thresholdValues[5]
                  inversion = thresholdValues[6]
                  isTrans = thresholdValues[7]
                  risTrans = thresholdValues[8]
                  geneTrans = thresholdValues[9]
                  creepSigmaFx = thresholdValues[10]
                  constRangeFx = thresholdValues[11] }

            let parent () =
                let symbols = reader.Next() |> counted
                let constants = reader.Next() |> counted
                { symbols = symbols; constants = constants }

            let parentA = parent ()
            let parentB = parent ()
            let expected = parent ()
            let offspring = hwBreedOffspring parentA parentB config thresholds (GepRng(seed))

            check
                $"breed {seed}"
                (offspring.symbols = expected.symbols && offspring.constants = expected.constants)

            expectEnd "breed"
        | "eval" ->
            let label = tokens[1]
            let config = parseConfig (reader.Next())
            let symbols = reader.Next() |> counted
            let constants = reader.Next() |> counted
            let vars = reader.Next() |> counted
            let chromosome = { symbols = symbols; constants = constants }
            let evalResult = reader.Next() |> fun t -> int t[1]
            check $"eval {label}" (evaluateChromosome config vars chromosome = evalResult)
            let programTokens = reader.Next()
            let packedCount = int programTokens[1]
            let packed = Array.init packedCount (fun i -> int64 programTokens[2 + i])
            let output = reader.Next()
            let program = compileChromosome config chromosome

            check
                $"compile {label}"
                (List.map packInstruction program.instructions = List.ofArray packed
                 && srcOrdinal program.outputSrc = int output[1]
                 && program.outputIdx = int output[2])

            let runResult = reader.Next() |> fun t -> int t[1]
            check $"run {label}" (runProgram program vars constants = runResult)
            expectEnd "eval"
        | "sse" ->
            let results = reader.Next() |> counted
            let targets = reader.Next() |> counted
            let value = reader.Next() |> fun t -> int64 t[1]
            check "sse" (sumSquaredError results targets = value)
            expectEnd "sse"
        | other -> failwith $"unknown golden section: {other}"

        tokens <- reader.Next()

    { passed = passed
      failed = failures.Count
      failures = List.ofSeq failures }
