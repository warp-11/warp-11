/// The living checks. `dotnet run` = self-checks (the compiler diffed against
/// the tree evaluator, breeding invariants, an engine smoke solve);
/// `dotnet run -- golden <file>` = verify the Kotlin-exported vectors.
module Warp11.Gep.Main

open Warp11
open Warp11.Gep.Fixed
open Warp11.Gep.Rng
open Warp11.Gep.Opcodes
open Warp11.Gep.Karva
open Warp11.Gep.Chromosome
open Warp11.Gep.MicroProgram
open Warp11.Gep.HwBreeding
open Warp11.Gep.Fitness
open Warp11.Gep.Engine

let private tailTerminalsOnly (config: GepConfig) (c: Chromosome) : bool =
    Seq.forall
        (fun g ->
            Seq.forall
                (fun i -> config.layout.IsHead i || isTerminal c.symbols[config.GeneStart g + i])
                (seq { 0 .. config.layout.length - 1 }))
        (seq { 0 .. config.geneCount - 1 })

/// The compiler oracle: for every random chromosome and input row, the
/// compiled micro-program and the tree evaluator must agree exactly — if they
/// ever disagree the compiler is wrong, and the hardware would inherit the
/// bug.
let private compilerDiff () : bool =
    let rng = GepRng(11L)
    let rangeFx = fx defaultConstantRange

    let configs =
        [ gepConfig (geneLayout 6 2) 2 1 0 functionSet ADD
          gepConfig (geneLayout 8 2) 3 2 4 functionSet ADD
          gepConfig (geneLayout 5 2) 1 3 2 rationalSet MUL
          // Homeotic: the ADF calls must compile to the conventional genes'
          // output operands and nothing else — same oracle, same instruction
          // format, which is the claim that ADFs need no hardware change.
          adfConfig (geneLayout 6 2) 3 4 2 functionSet
          adfConfig (geneLayout 4 2) 2 1 0 rationalSet ]

    List.forall
        (fun config ->
            Seq.forall
                (fun _ ->
                    let c = hwRandomChromosome config rangeFx rng
                    let program = compileChromosome config c

                    Seq.forall
                        (fun _ ->
                            let vars = Array.init config.variableCount (fun _ -> rng.NextBounded(8 * fxOne) - 4 * fxOne)
                            evaluateChromosome config vars c = runProgram program vars c.constants)
                        (seq { 1 .. 20 }))
                (seq { 1 .. 50 }))
        configs

/// Every offspring of every random pairing keeps the two invariants the
/// operators preserve by construction: fixed length, terminals-only tails.
let private breedingInvariants () : bool =
    let rng = GepRng(23L)
    let config = gepConfig (geneLayout 8 2) 3 2 4 functionSet ADD
    let thresholds = thresholdsFrom defaultGepParams defaultConstantRange
    let rangeFx = fx defaultConstantRange

    Seq.forall
        (fun _ ->
            let parentA = hwRandomChromosome config rangeFx rng
            let parentB = hwRandomChromosome config rangeFx rng
            let child = hwBreedOffspring parentA parentB config thresholds rng

            child.symbols.Length = config.chromosomeLength
            && child.constants.Length = config.geneCount * config.constantCount
            && tailTerminalsOnly config child)
        (seq { 1 .. 200 })

/// Every symbol legal for the gene it sits in: functions in heads only, and
/// terminals drawn from *that gene's* alphabet. Under a homeotic linkage the
/// last gene's terminals are ADF calls, so this is what proves no operator
/// smuggles a variable reference into it — a `variable 5` that means "ADF 5"
/// where only 4 ADFs exist would be an out-of-range call, and the operators
/// have to make that unreachable rather than repair it after the fact.
let private symbolsRoleLegal (config: GepConfig) (c: Chromosome) : bool =
    Seq.forall
        (fun g ->
            Seq.forall
                (fun i ->
                    let symbol = c.symbols[config.GeneStart g + i]

                    if isTerminal symbol then Array.contains symbol (config.TerminalsOf g)
                    else config.layout.IsHead i && Array.contains symbol config.functionSet)
                (seq { 0 .. config.layout.length - 1 }))
        (seq { 0 .. config.geneCount - 1 })

/// The ADF chromosome breeds through the *same* operator set as a plain one —
/// that is the whole point of making homeotic a linkage rather than a separate
/// engine — so it has to keep the same invariants plus role legality, and its
/// offspring must still compile to a program that agrees with the evaluator.
let private adfBreedingInvariants () : bool =
    let rng = GepRng(29L)
    // Four variables but two ADFs on purpose: the alphabets then genuinely
    // differ, so a conventional symbol landing in the homeotic gene names an
    // ADF that does not exist. With adfCount = variableCount the two sets
    // coincide and this check would pass without testing anything.
    let config = adfConfig (geneLayout 6 2) 4 2 2 functionSet
    let thresholds = thresholdsFrom defaultGepParams defaultConstantRange
    let rangeFx = fx defaultConstantRange

    Seq.forall
        (fun _ ->
            let parentA = hwRandomChromosome config rangeFx rng
            let parentB = hwRandomChromosome config rangeFx rng
            let child = hwBreedOffspring parentA parentB config thresholds rng
            let program = compileChromosome config child

            child.symbols.Length = config.chromosomeLength
            && child.constants.Length = config.geneCount * config.constantCount
            && symbolsRoleLegal config child
            && Seq.forall
                (fun _ ->
                    let vars = Array.init config.variableCount (fun _ -> rng.NextBounded(8 * fxOne) - 4 * fxOne)
                    evaluateChromosome config vars child = runProgram program vars child.constants)
                (seq { 1 .. 5 }))
        (seq { 1 .. 300 })

/// The engine solves a smooth 1-variable target well enough to prove the loop
/// selects: x^2 + x + 1 over [-2, 2], error must fall well under the constant
/// predictor's.
let private engineSmoke () : bool =
    let config = gepConfig (geneLayout 6 2) 1 2 2 arithmeticSet ADD
    let cases = sampleFunction 32 -2.0 2.0 (fun x -> x * x + x + 1.0)
    let engine = GepEngine(config, cases, GepRng(7L), 80)
    engine.Run(40, ignore)
    engine.BestError < 0.5

/// The same smoke through a homeotic chromosome: the engine, the fitness path
/// and the operators all have to work on an ADF encoding with nothing else
/// changed — one engine, two linkages.
let private adfEngineSmoke () : bool =
    let config = adfConfig (geneLayout 6 2) 1 2 2 arithmeticSet
    let cases = sampleFunction 32 -2.0 2.0 (fun x -> x * x + x + 1.0)
    let engine = GepEngine(config, cases, GepRng(7L), 80)
    engine.Run(40, ignore)
    engine.BestError < 0.5

/// The first hardware the oracle judges: the xoshiro128pp core, walked in the
/// Sim against GepRng word-for-word — the reset-default state (1,2,3,4)
/// first, then a SplitMix64-expanded seed loaded through the ports.
let private xoshiroVsRng () : bool =
    let sim = Sim(Hdl.xoshiroWalk)
    sim.Poke("step", 1UL)

    let walk (rng: GepRng) (count: int) =
        Seq.forall
            (fun _ ->
                let expected = uint64 (uint32 (rng.NextWord()))
                let got = sim.Peek "word"
                sim.Tick()
                got = expected)
            (seq { 1 .. count })

    let defaultStream = walk (GepRng(1, 2, 3, 4)) 64

    let seed = 42L
    let state = expandSeed seed
    sim.Poke("load", 1UL)
    sim.Poke("step", 0UL)

    for i in 0 .. 3 do
        sim.Poke($"s{i}", uint64 (uint32 state[i]))

    sim.Tick()
    sim.Poke("load", 0UL)
    sim.Poke("step", 1UL)
    let seededStream = walk (GepRng(seed)) 64
    defaultStream && seededStream

/// The reciprocal table read out of the first initialized memory, every entry
/// against `fxRecipTable` — plus Reset(), which must reload the contents (a
/// BRAM INIT comes back with the bitstream).
let private recipRomVsTable () : bool =
    let sim = Sim(Hdl.recipRomWalk)

    let readAll () =
        Seq.forall
            (fun i ->
                sim.Poke("addr", uint64 i)
                sim.Tick()
                sim.Peek "value" = uint64 (uint32 fxRecipTable[i]))
            (seq { 0 .. 511 })

    let first = readAll ()
    sim.Reset()
    first && readAll ()

/// The divide FU streamed at II=1 against `fxDivRecip` — the golden edge
/// cases plus a random sweep, one operand pair per cycle, every quotient
/// compared `gepDivLatency` cycles later.
let private divArmVsFxDivRecip () : bool =
    let rng = GepRng(777L)

    let edges =
        [ fx 1.0, fx 3.0
          fx -1.0, fx 3.0
          fx 1.0, fx -3.0
          fx -1.0, fx -3.0
          fx 355.0, fx 113.0
          0, fx 2.0
          fx 5.0, 0
          fx 5.0, fxDivEps
          fx 5.0, fxDivEps + 1
          System.Int32.MaxValue, 1
          System.Int32.MinValue, fx 2.0
          1, System.Int32.MaxValue
          fx 30000.0, fx 0.002 ]

    let cases =
        Array.ofList edges
        |> Array.append (Array.init 200 (fun _ -> rng.NextWord(), rng.NextWord()))

    let expected =
        cases |> Array.map (fun (a, b) -> uint64 (uint32 (fxDivRecip a b)))

    let sim = Sim(Hdl.gepDivRecip)
    let results = ResizeArray<uint64>()

    for i in 0 .. cases.Length + Hdl.gepDivLatency - 2 do
        if i < cases.Length then
            let a, b = cases[i]
            sim.Poke("a", uint64 (uint32 a))
            sim.Poke("b", uint64 (uint32 b))

        sim.Tick()

        if i >= Hdl.gepDivLatency - 1 then
            results.Add(sim.Peek "q")

    results.ToArray() = expected

/// Both ALU elaborations streamed at II=1 against the software `applyOp` —
/// every arm, the terminal-passthrough default, full-range Q16.16 operands
/// (so the saturating arms saturate). The div ALU's expected values run with
/// the reciprocal seam active, because `divRecipArm` IS `fxDivRecip`.
let private aluVsApplyOp () : bool =
    let rng = GepRng(4242L)

    let streamCases (design: ModuleDef) (latency: int) (cases: (int * int * int)[]) (expected: uint64[]) =
        let sim = Sim(design)
        let results = ResizeArray<uint64>()

        for i in 0 .. cases.Length + latency - 2 do
            if i < cases.Length then
                let op, a, b = cases[i]
                sim.Poke("op", uint64 op)
                sim.Poke("a", uint64 (uint32 a))
                sim.Poke("b", uint64 (uint32 b))

            sim.Tick()

            if i >= latency - 1 then
                results.Add(sim.Peek "result")

        results.ToArray() = expected

    let makeCases (ops: int[]) (count: int) =
        Array.init count (fun _ ->
            let op = ops[rng.NextBounded ops.Length]
            op, rng.NextWord(), rng.NextWord())

    let plainOps =
        Array.append functionSet [| variable 3; constant 7 |]
        |> Array.filter (fun op -> op <> DIV)

    let plainCases = makeCases plainOps 300
    let plainExpected = plainCases |> Array.map (fun (op, a, b) -> uint64 (uint32 (applyOp op a b)))

    let divOps = Array.append rationalSet [| NEG; ABS; GT; variable 0 |]
    let divCases = makeCases divOps 300

    let divExpected =
        useRecipDiv <- true

        let e =
            divCases |> Array.map (fun (op, a, b) -> uint64 (uint32 (applyOp op a b)))

        useRecipDiv <- false
        e

    streamCases Hdl.gepAluPlain (Hdl.gepAluLatency false) plainCases plainExpected
    && streamCases Hdl.gepAluDiv (Hdl.gepAluLatency true) divCases divExpected

/// The fabric compiler against `compileGene`: random genes (three geometries,
/// plus the one-node terminal gene) loaded into the walk's gene buffer, the
/// FSM run to done, the record read back via PeekMem and compared word for
/// word — header and instructions both.
let private karvaCompilerVsCompileGene () : bool =
    let rng = GepRng(31415L)
    let rangeFx = fx defaultConstantRange

    let configs =
        [ gepConfig (geneLayout 8 2) 3 1 4 functionSet ADD
          gepConfig (geneLayout 12 2) 2 1 0 comparisonSet ADD
          gepConfig (geneLayout 5 2) 1 1 2 rationalSet ADD ]

    let genes =
        [ for config in configs do
              for _ in 1 .. 10 -> gene config 0 (hwRandomChromosome config rangeFx rng)
          // The legal single-node gene: a terminal root, no instructions.
          yield Array.append [| variable 2 |] (Array.create 4 (variable 0)) ]

    let runOne (g: int[]) =
        let sim = Sim(Hdl.karvaCompilerWalk)

        for i in 0 .. g.Length - 1 do
            sim.Poke("load_en", 1UL)
            sim.Poke("load_addr", uint64 i)
            sim.Poke("load_data", uint64 g[i])
            sim.Tick()

        sim.Poke("load_en", 0UL)
        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)
        let mutable cycles = 0

        while sim.Peek "done" = 0UL && cycles < 500 do
            sim.Tick()
            cycles <- cycles + 1

        let program = compileGene g

        let header =
            uint64 program.instructions.Length
            ||| (uint64 (srcOrdinal program.outputSrc) <<< 8)
            ||| (uint64 program.outputIdx <<< 10)

        sim.Peek "done" = 1UL
        && sim.Peek "n_instr" = uint64 program.instructions.Length
        && sim.PeekMem("rec_mem", 0) = header
        && (program.instructions
            |> List.mapi (fun i ins -> sim.PeekMem("rec_mem", i + 1) = uint64 (packInstruction ins))
            |> List.forall id)

    List.forall runOne genes

/// The operator engine against `hwBreedOffspring` on shared seeds — the
/// campaign's centerpiece check: the FSM must consume the identical word
/// sequence, so any divergence in gate order, bound, or buffer arithmetic
/// shows up as a wrong child. Rates are set high so every operator fires
/// across the seed sweep.
let private operatorEngineVsHwBreed () : bool =
    let config = gepConfig (geneLayout 8 2) 3 1 4 functionSet ADD

    let thresholds =
        { onePoint = thresholdOf 0.5
          twoPoint = thresholdOf 0.5
          geneRecomb = thresholdOf 0.4
          mutation = thresholdOf 0.15
          constReplace = thresholdOf 0.4
          creep = thresholdOf 0.5
          inversion = thresholdOf 0.5
          isTrans = thresholdOf 0.5
          risTrans = thresholdOf 0.5
          geneTrans = 0
          creepSigmaFx = fx 0.1
          constRangeFx = fx 10.0 }

    let parentRng = GepRng(2718L)
    let rangeFx = fx defaultConstantRange

    let runOne (seed: int64) =
        let parentA = hwRandomChromosome config rangeFx parentRng
        let parentB = hwRandomChromosome config rangeFx parentRng
        let expected = hwBreedOffspring parentA parentB config thresholds (GepRng(seed))

        let sim = Sim(Hdl.operatorEngineWalk)

        let loadParent (par: uint64) (c: Chromosome) =
            sim.Poke("ld_par", par)

            for idx in 0 .. c.symbols.Length - 1 do
                sim.Poke("ld_sym", 1UL)
                sim.Poke("ld_addr", uint64 idx)
                sim.Poke("ld_sdata", uint64 c.symbols[idx])
                sim.Tick()

            sim.Poke("ld_sym", 0UL)

            for idx in 0 .. c.constants.Length - 1 do
                sim.Poke("ld_const", 1UL)
                sim.Poke("ld_addr", uint64 idx)
                sim.Poke("ld_cdata", uint64 (uint32 c.constants[idx]))
                sim.Tick()

            sim.Poke("ld_const", 0UL)

        loadParent 0UL parentA
        loadParent 1UL parentB

        sim.Poke("th_1p", uint64 (uint32 thresholds.onePoint))
        sim.Poke("th_2p", uint64 (uint32 thresholds.twoPoint))
        sim.Poke("th_gr", uint64 (uint32 thresholds.geneRecomb))
        sim.Poke("th_mut", uint64 (uint32 thresholds.mutation))
        sim.Poke("th_cr", uint64 (uint32 thresholds.constReplace))
        sim.Poke("th_creep", uint64 (uint32 thresholds.creep))
        sim.Poke("th_inv", uint64 (uint32 thresholds.inversion))
        sim.Poke("th_is", uint64 (uint32 thresholds.isTrans))
        sim.Poke("th_ris", uint64 (uint32 thresholds.risTrans))
        sim.Poke("sigma_fx", uint64 (uint32 thresholds.creepSigmaFx))
        sim.Poke("range_fx", uint64 (uint32 thresholds.constRangeFx))

        let state = expandSeed seed

        for idx in 0 .. 3 do
            sim.Poke($"s{idx}", uint64 (uint32 state[idx]))

        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)
        let mutable cycles = 0

        while sim.Peek "done" = 0UL && cycles < 2000 do
            sim.Tick()
            cycles <- cycles + 1

        let symbolsOk =
            Seq.forall
                (fun idx ->
                    sim.Poke("rd_saddr", uint64 idx)
                    sim.Peek "child_sym" = uint64 expected.symbols[idx])
                (seq { 0 .. config.chromosomeLength - 1 })

        let constantsOk =
            Seq.forall
                (fun idx ->
                    sim.Poke("rd_caddr", uint64 idx)
                    sim.Peek "child_const" = uint64 (uint32 expected.constants[idx]))
                (seq { 0 .. config.constantCount - 1 })

        sim.Peek "done" = 1UL && symbolsOk && constantsOk

    List.forall runOne [ 1L .. 25L ]

/// The unit engine against the software evaluation: four individuals (one a
/// bare terminal that emits no instructions) and 16 cases packed into real
/// fill beats, the bank committed, results collected off the handshake and
/// compared against runProgram + sumSquaredError per individual.
let private unitEngineVsEvaluate () : bool =
    let config = gepConfig (geneLayout 8 2) 3 1 4 functionSet ADD
    let rng = GepRng(6060L)
    let rangeFx = fx defaultConstantRange

    let terminalChromosome =
        { symbols = Array.append [| variable 1 |] (Array.create (config.chromosomeLength - 1) (variable 0))
          constants = Array.init config.constantCount (fun _ -> fx 1.5) }

    let individuals =
        [ hwRandomChromosome config rangeFx rng
          terminalChromosome
          hwRandomChromosome config rangeFx rng
          hwRandomChromosome config rangeFx rng ]

    let nCases = 16

    let cases =
        [ for _ in 1 .. nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(8 * fxOne) - 4 * fxOne),
            rng.NextBounded(8 * fxOne) - 4 * fxOne ]

    let expected =
        withRecipDiv (fun () ->
            [ for c in individuals ->
                let program = compileChromosome config c

                let results =
                    [| for vars, _ in cases -> runProgram program vars c.constants |]

                sumSquaredError results (cases |> List.map snd |> Array.ofList) ])

    let capacity = 32
    let indivWords = Hdl.gepUnitIndivWords capacity config.constantCount

    let sim = Sim(Hdl.unitEngineWalk)
    let bigOf (words: uint64[]) =
        Array.fold
            (fun acc (w: uint64) -> (acc <<< 32) ||| System.Numerics.BigInteger(w))
            System.Numerics.BigInteger.Zero
            (Array.rev words)

    // Case beats: beat 0 = the vars in four lanes, beat 1 = the target.
    for idx in 0 .. nCases - 1 do
        let vars, target = cases[idx]
        let varWords = Array.init 4 (fun v -> if v < vars.Length then uint64 (uint32 vars[v]) else 0UL)
        sim.Poke("fill_case_en", 1UL)
        sim.Poke("fill_case_sel", 0UL)
        sim.Poke("fill_case_addr", uint64 idx)
        sim.PokeWide("fill_beat", bigOf varWords)
        sim.Tick()
        sim.Poke("fill_case_sel", 1UL)
        sim.PokeWide("fill_beat", bigOf [| uint64 (uint32 target); 0UL; 0UL; 0UL |])
        sim.Tick()

    sim.Poke("fill_case_en", 0UL)

    // Individual records: word 0 header, 1..capacity packed instructions,
    // then the constants; 4 words per line.
    let lineBase = ref 0

    for c in individuals do
        let program = compileChromosome config c

        let words =
            [| yield
                   uint64 program.instructions.Length
                   ||| (uint64 (srcOrdinal program.outputSrc) <<< 8)
                   ||| (uint64 program.outputIdx <<< 10)
               for ins in program.instructions -> uint64 (packInstruction ins)
               yield! Array.create (capacity - program.instructions.Length) 0UL
               for k in c.constants -> uint64 (uint32 k)
               yield! Array.create (indivWords - 1 - capacity - config.constantCount) 0UL |]

        for line in 0 .. indivWords / 4 - 1 do
            sim.Poke("fill_indiv_en", 1UL)
            sim.Poke("fill_indiv_addr", uint64 (lineBase.Value + line))
            sim.PokeWide("fill_beat", bigOf (Array.sub words (line * 4) 4))
            sim.Tick()

        lineBase.Value <- lineBase.Value + indivWords / 4

    sim.Poke("fill_indiv_en", 0UL)
    sim.Poke("fill_unit_id", 77UL)
    sim.Poke("fill_commit", 1UL)
    sim.Tick()
    sim.Poke("fill_commit", 0UL)
    sim.Poke("n_cases", uint64 nCases)
    sim.Poke("m_count", uint64 individuals.Length)
    sim.Poke("res_ready", 1UL)

    let collected = ResizeArray<uint64 * uint64 * uint64>()
    let mutable cycles = 0

    while collected.Count < individuals.Length && cycles < 20000 do
        if sim.Peek "res_valid" = 1UL then
            collected.Add(sim.Peek "res_fit", sim.Peek "res_unit", sim.Peek "res_m")

        sim.Tick()
        cycles <- cycles + 1

    collected.Count = individuals.Length
    && Seq.forall2
        (fun (fit, unit, m) (idx, exp: int64) -> fit = uint64 exp && unit = 77UL && m = uint64 idx)
        collected
        (List.indexed expected)

/// The divide sharing knob, both ways round. One lane, one set of individuals
/// whose programs contain DIV, evaluated with the arm resident in the lane's
/// ALU and again with it pooled behind `gepDivPod`. Both must equal the
/// software evaluation: sharing changes WHEN a result comes back and never
/// what it is, so the two elaborations are bit-identical in their answers and
/// differ only in cycles.
let private unitEngineDivSharing () : bool =
    let config = gepConfig (geneLayout 8 2) 3 1 4 rationalSet ADD
    let rng = GepRng(4242L)

    // Hand-built rather than drawn: a random chromosome's open reading frame
    // often holds no DIV at all, and a DIV-free program lets a broken socket
    // pass. Constant 2 is zero on purpose — the protected divide's eps guard
    // has to agree between the two elaborations too.
    let head (symbols: int list) =
        { symbols =
            Array.append
                (Array.ofList symbols)
                (Array.init (config.chromosomeLength - symbols.Length) (fun i -> variable (i % config.variableCount)))
          constants = [| fx 1.5; fx -0.25; fx 0.0; fx 3.0 |] }

    let individuals =
        [ head [ DIV; DIV; ADD; DIV; variable 0; variable 1; variable 2; constant 0 ]
          head [ ADD; DIV; MUL; variable 0; DIV; variable 1; constant 1; constant 2 ]
          head [ DIV; variable 0; constant 2 ]
          head [ variable 1 ] ]

    let programs = [ for c in individuals -> compileChromosome config c ]

    // A DIV-free program would let a broken socket pass unnoticed.
    let divCount =
        programs
        |> List.sumBy (fun p -> p.instructions |> List.filter (fun i -> i.op = DIV) |> List.length)

    let nCases = 16

    let cases =
        [ for _ in 1..nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(8 * fxOne) - 4 * fxOne),
            rng.NextBounded(8 * fxOne) - 4 * fxOne ]

    // The fabric divides by reciprocal in both sharings, so the twin must too —
    // this check passed for a day without it, on operands that happened not to
    // separate the two divides.
    let expected =
        withRecipDiv (fun () ->
            [ for program, c in List.zip programs individuals ->
                let results = [| for vars, _ in cases -> runProgram program vars c.constants |]
                sumSquaredError results (cases |> List.map snd |> Array.ofList) ])

    let capacity = 32
    let indivWords = Hdl.gepUnitIndivWords capacity config.constantCount

    let bigOf (words: uint64[]) =
        Array.fold
            (fun acc (w: uint64) -> (acc <<< 32) ||| System.Numerics.BigInteger(w))
            System.Numerics.BigInteger.Zero
            (Array.rev words)

    let run (sharing: FuSharing) =
        let sim = Sim(Hdl.unitEngineDivWalk sharing)

        for idx in 0 .. nCases - 1 do
            let vars, target = cases[idx]
            let varWords = Array.init 4 (fun v -> if v < vars.Length then uint64 (uint32 vars[v]) else 0UL)
            sim.Poke("fill_case_en", 1UL)
            sim.Poke("fill_case_sel", 0UL)
            sim.Poke("fill_case_addr", uint64 idx)
            sim.PokeWide("fill_beat", bigOf varWords)
            sim.Tick()
            sim.Poke("fill_case_sel", 1UL)
            sim.PokeWide("fill_beat", bigOf [| uint64 (uint32 target); 0UL; 0UL; 0UL |])
            sim.Tick()

        sim.Poke("fill_case_en", 0UL)
        let lineBase = ref 0

        for program, c in List.zip programs individuals do
            let words =
                [| yield
                       uint64 program.instructions.Length
                       ||| (uint64 (srcOrdinal program.outputSrc) <<< 8)
                       ||| (uint64 program.outputIdx <<< 10)
                   for ins in program.instructions -> uint64 (packInstruction ins)
                   yield! Array.create (capacity - program.instructions.Length) 0UL
                   for k in c.constants -> uint64 (uint32 k)
                   yield! Array.create (indivWords - 1 - capacity - config.constantCount) 0UL |]

            for line in 0 .. indivWords / 4 - 1 do
                sim.Poke("fill_indiv_en", 1UL)
                sim.Poke("fill_indiv_addr", uint64 (lineBase.Value + line))
                sim.PokeWide("fill_beat", bigOf (Array.sub words (line * 4) 4))
                sim.Tick()

            lineBase.Value <- lineBase.Value + indivWords / 4

        sim.Poke("fill_indiv_en", 0UL)
        sim.Poke("fill_unit_id", 91UL)
        sim.Poke("fill_commit", 1UL)
        sim.Tick()
        sim.Poke("fill_commit", 0UL)
        sim.Poke("n_cases", uint64 nCases)
        sim.Poke("m_count", uint64 individuals.Length)
        sim.Poke("res_ready", 1UL)

        let collected = ResizeArray<uint64>()
        let mutable cycles = 0

        while collected.Count < individuals.Length && cycles < 40000 do
            if sim.Peek "res_valid" = 1UL then
                collected.Add(sim.Peek "res_fit")

            sim.Tick()
            cycles <- cycles + 1

        List.ofSeq collected, cycles

    let residentFits, residentCycles = run PerLane
    let pooledFits, pooledCycles = run Pooled
    let want = [ for e in expected -> uint64 e ]

    printfn
        $"      divide sharing: %d{divCount} DIV instructions, PerLane %d{residentCycles} cycles, Pooled %d{pooledCycles}"

    if residentFits <> want || pooledFits <> want then
        printfn $"      want     %A{want}"
        printfn $"      PerLane  %A{residentFits}"
        printfn $"      Pooled   %A{pooledFits}"

    divCount > 0 && residentFits = want && pooledFits = want

/// The breeder block end to end: parents in, the 128-bit record lines out
/// under intermittent backpressure, reassembled and compared against
/// hwBreedOffspring + compileChromosome — header, instructions and child
/// constants — then the child read back through the DONE ports and the
/// release handshake exercised.
let private breederBlockVsOracle () : bool =
    let config = gepConfig (geneLayout 8 2) 3 1 4 functionSet ADD
    let capacity = 32
    let indivWords = Hdl.gepUnitIndivWords capacity config.constantCount

    let thresholds =
        { onePoint = thresholdOf 0.5
          twoPoint = thresholdOf 0.5
          geneRecomb = thresholdOf 0.4
          mutation = thresholdOf 0.15
          constReplace = thresholdOf 0.4
          creep = thresholdOf 0.5
          inversion = thresholdOf 0.5
          isTrans = thresholdOf 0.5
          risTrans = thresholdOf 0.5
          geneTrans = 0
          creepSigmaFx = fx 0.1
          constRangeFx = fx 10.0 }

    let parentRng = GepRng(9090L)
    let rangeFx = fx defaultConstantRange

    let runOne (seed: int64) =
        let parentA = hwRandomChromosome config rangeFx parentRng
        let parentB = hwRandomChromosome config rangeFx parentRng
        let child = hwBreedOffspring parentA parentB config thresholds (GepRng(seed))
        let program = compileChromosome config child

        let sim = Sim(Hdl.breederBlockWalk)

        let loadParent (par: uint64) (c: Chromosome) =
            sim.Poke("ld_par", par)

            for idx in 0 .. c.symbols.Length - 1 do
                sim.Poke("ld_sym", 1UL)
                sim.Poke("ld_addr", uint64 idx)
                sim.Poke("ld_sdata", uint64 c.symbols[idx])
                sim.Tick()

            sim.Poke("ld_sym", 0UL)

            for idx in 0 .. c.constants.Length - 1 do
                sim.Poke("ld_const", 1UL)
                sim.Poke("ld_addr", uint64 idx)
                sim.Poke("ld_cdata", uint64 (uint32 c.constants[idx]))
                sim.Tick()

            sim.Poke("ld_const", 0UL)

        loadParent 0UL parentA
        loadParent 1UL parentB

        for name, v in
            [ "th_1p", thresholds.onePoint; "th_2p", thresholds.twoPoint
              "th_gr", thresholds.geneRecomb; "th_mut", thresholds.mutation
              "th_cr", thresholds.constReplace; "th_creep", thresholds.creep
              "th_inv", thresholds.inversion; "th_is", thresholds.isTrans
              "th_ris", thresholds.risTrans; "sigma_fx", thresholds.creepSigmaFx
              "range_fx", thresholds.constRangeFx ] do
            sim.Poke(name, uint64 (uint32 v))

        let state = expandSeed seed

        for idx in 0 .. 3 do
            sim.Poke($"s{idx}", uint64 (uint32 state[idx]))

        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)

        // Collect lines under intermittent backpressure (ready 2 of 3 cycles).
        let lines = ResizeArray<System.Numerics.BigInteger * uint64 * uint64>()
        let mutable cycles = 0

        while sim.Peek "done" = 0UL && cycles < 4000 do
            sim.Poke("rec_ready", (if cycles % 3 = 0 then 0UL else 1UL))

            if sim.Peek "rec_valid" = 1UL && sim.Peek "rec_ready" = 1UL then
                lines.Add(sim.PeekWide "rec_line", sim.Peek "rec_line_idx", sim.Peek "rec_last")

            sim.Tick()
            cycles <- cycles + 1

        let words =
            [| for lineValue, _, _ in lines do
                   for lane in 0 .. 3 ->
                       uint64 ((lineValue >>> (lane * 32)) &&& System.Numerics.BigInteger 0xFFFFFFFFL) |]

        let header =
            uint64 program.instructions.Length
            ||| (uint64 (srcOrdinal program.outputSrc) <<< 8)
            ||| (uint64 program.outputIdx <<< 10)

        let instrsOk =
            program.instructions
            |> List.mapi (fun idx ins -> words[idx + 1] = uint64 (packInstruction ins))
            |> List.forall id

        let constsOk =
            child.constants
            |> Array.mapi (fun idx k -> words[capacity + 1 + idx] = uint64 (uint32 k))
            |> Array.forall id

        let childOk =
            Seq.forall
                (fun idx ->
                    sim.Poke("rd_saddr", uint64 idx)
                    sim.Peek "child_sym" = uint64 child.symbols[idx])
                (seq { 0 .. config.chromosomeLength - 1 })

        let lastFlagsOk =
            lines |> Seq.mapi (fun idx (_, li, la) -> li = uint64 idx && la = (if idx = lines.Count - 1 then 1UL else 0UL)) |> Seq.forall id

        sim.Poke("rel", 1UL)
        sim.Tick()
        let released = sim.Peek "busy" = 0UL && sim.Peek "done" = 0UL

        lines.Count = indivWords / 4
        && words[0] = header
        && instrsOk
        && constsOk
        && childOk
        && lastFlagsOk
        && released

    List.forall runOne [ 101L .. 110L ]

/// The record router's choreography: two breeders presenting simultaneously
/// bind lowest-index-first to the two lanes, lines land registered on the
/// bound lane at their addresses, commits pulse with the held entry ids, and
/// a released lane accepts the next record. Ready must be low before binding
/// (it is a registered binding bit).
let private recordRouterChoreography () : bool =
    let sim = Sim(Hdl.recordRouterWalk)
    let big (v: int) = System.Numerics.BigInteger(v)

    let mkRecord (baseV: int) (n: int) =
        [ for k in 0 .. n - 1 -> big (baseV + k), uint64 k, (if k = n - 1 then 1UL else 0UL) ]

    // Per breeder: the records to stream, each with its entry id, and a gap
    // of idle cycles after each record (the pool's DONE-hold, which keeps the
    // entry id stable through the commit).
    let driver (name: string) (records: (uint64 * (System.Numerics.BigInteger * uint64 * uint64) list) list) =
        let mutable queue = records
        let mutable gap = 0

        {| Present =
            fun () ->
                match queue with
                | (entry, (line, idx, last) :: _) :: _ when gap = 0 ->
                    sim.Poke($"{name}_entry_id", entry)
                    sim.Poke($"{name}_rec_valid", 1UL)
                    sim.PokeWide($"{name}_rec_line", line)
                    sim.Poke($"{name}_rec_line_idx", idx)
                    sim.Poke($"{name}_rec_last", last)
                | _ -> sim.Poke($"{name}_rec_valid", 0UL)
           Consume =
            fun () ->
                (match queue with
                 | (entry, _ :: restLines) :: restRecords when
                     gap = 0
                     && sim.Peek $"{name}_rec_valid" = 1UL
                     && sim.Peek $"{name}_rec_ready" = 1UL
                     ->
                     if List.isEmpty restLines then
                         gap <- 6
                         queue <- restRecords
                     else
                         queue <- (entry, restLines) :: restRecords
                 | _ -> ())

                if gap > 0 then gap <- gap - 1 |}

    let b0 = driver "b0" [ 111UL, mkRecord 0x1000 3; 333UL, mkRecord 0x3000 2 ]
    let b1 = driver "b1" [ 222UL, mkRecord 0x2000 2 ]

    sim.Poke("l0_can_fill", 1UL)
    sim.Poke("l1_can_fill", 1UL)

    let lane0Log = ResizeArray<System.Numerics.BigInteger * uint64>()
    let lane1Log = ResizeArray<System.Numerics.BigInteger * uint64>()
    let commits = ResizeArray<int * uint64>()
    let readyBeforeBind = sim.Peek "b0_rec_ready" = 0UL && sim.Peek "b1_rec_ready" = 0UL

    for _ in 1 .. 80 do
        b0.Present()
        b1.Present()
        b0.Consume()
        b1.Consume()
        sim.Tick()

        if sim.Peek "l0_fill_indiv_en" = 1UL then
            lane0Log.Add(sim.PeekWide "l0_fill_beat", sim.Peek "l0_fill_indiv_addr")

        if sim.Peek "l1_fill_indiv_en" = 1UL then
            lane1Log.Add(sim.PeekWide "l1_fill_beat", sim.Peek "l1_fill_indiv_addr")

        if sim.Peek "l0_fill_commit" = 1UL then
            commits.Add(0, sim.Peek "l0_fill_unit_id")

        if sim.Peek "l1_fill_commit" = 1UL then
            commits.Add(1, sim.Peek "l1_fill_unit_id")

    let expectedLane0 =
        [ for k in 0 .. 2 -> big (0x1000 + k), uint64 k ] @ [ for k in 0 .. 1 -> big (0x3000 + k), uint64 k ]

    let expectedLane1 = [ for k in 0 .. 1 -> big (0x2000 + k), uint64 k ]

    readyBeforeBind
    && List.ofSeq lane0Log = expectedLane0
    && List.ofSeq lane1Log = expectedLane1
    && List.ofSeq commits = [ 0, 111UL; 1, 222UL; 0, 333UL ]

/// A genome as its 16-word DDR gene record: symbols packed four per word from
/// word 1, constants from word 10 — the format the pool's pack FSM writes and
/// the host stages parents in.
let private geneRecordWords (c: Chromosome) =
    let words = Array.zeroCreate<uint32> Cluster.gepRecordWords

    c.symbols
    |> Array.iteri (fun i s -> words[1 + i / 4] <- words[1 + i / 4] ||| (uint32 (s &&& 0xFF) <<< (8 * (i % 4))))

    c.constants |> Array.iteri (fun idx v -> words[10 + idx] <- uint32 v)
    words

/// A pairing entry carries only the top 16 bits of each Bernoulli gate, so an
/// oracle has to draw against the same quantized values to stay bit-exact.
let private quantizeRates (t: GepBreedThresholds) =
    let q v = v &&& ~~~0xFFFF

    { t with
        onePoint = q t.onePoint
        twoPoint = q t.twoPoint
        geneRecomb = q t.geneRecomb
        mutation = q t.mutation
        constReplace = q t.constReplace
        creep = q t.creep
        inversion = q t.inversion
        isTrans = q t.isTrans
        risTrans = q t.risTrans }

/// The nine gates off, the two policy spans untouched — what a zero-rate entry
/// breeds with, so the offspring is a copy of parent A.
let private silenceRates (t: GepBreedThresholds) =
    { t with
        onePoint = 0
        twoPoint = 0
        geneRecomb = 0
        mutation = 0
        constReplace = 0
        creep = 0
        inversion = 0
        isTrans = 0
        risTrans = 0 }

/// A pairing entry as its 16-word DDR record — the one layout the fabric's
/// filler reads, the fabric's op-list emitter writes, and the host marshaller
/// produces. Rates travel as each gate's top 16 bits, two per word from word 3.
let private entryWordsOf
    (parentA: int)
    (parentB: int)
    (dest: int)
    (t: GepBreedThresholds)
    (entryId: int)
    (skip: bool)
    (seed: int64)
    =
    let high (v: int) = int ((uint32 v) >>> 16)
    let flags = if skip then Cluster.gepFlagSkipWriteback else 0

    [| yield uint32 parentA
       yield uint32 parentB
       yield uint32 dest
       yield uint32 (high t.mutation ||| (high t.constReplace <<< 16))
       yield uint32 (high t.creep ||| (high t.inversion <<< 16))
       yield uint32 (high t.isTrans ||| (high t.risTrans <<< 16))
       yield uint32 (high t.onePoint ||| (high t.twoPoint <<< 16))
       yield uint32 (high t.geneRecomb ||| (flags <<< 16))
       yield uint32 t.creepSigmaFx
       yield uint32 t.constRangeFx
       yield uint32 entryId
       yield 0u
       for w in expandSeed seed -> uint32 w |]

/// The fitness a lane will report for this genome. Under `withRecipDiv`,
/// because the fabric's divide is the reciprocal one and this exists only to
/// predict hardware.
let private fitnessOf (config: GepConfig) (cases: (int[] * int) list) (c: Chromosome) =
    withRecipDiv (fun () ->
        let program = compileChromosome config c
        let results = [| for vars, _ in cases -> runProgram program vars c.constants |]
        sumSquaredError results (cases |> List.map snd |> Array.ofList))

/// The silicon first-light vector's data, rebuilt deterministically. One
/// definition serves the file the board consumes and any check that needs to
/// reproduce exactly what silicon ran — which is how a board mismatch gets
/// chased off-silicon instead of over ssh.
let private boardVectorDataCases (nCases: int) (entries: int) =
    let shape = ClusterAxi.clusterSiliconShape
    // Derived from the shape, never re-typed: the fabric's symbol ROMs and this
    // oracle must be built from ONE function set.
    let config = ClusterAxi.clusterSiliconConfig

    if config.functionSet <> shape.functionSet || config.terminalSet <> shape.terminalSet then
        failwith "the board vector's config and the elaborated shape disagree about the symbol sets"

    let popSlots = 64
    let rng = GepRng(2_026_0810L)
    let rangeFx = fx defaultConstantRange
    let population = [| for _ in 1..popSlots -> hwRandomChromosome config rangeFx rng |]

    let cases =
        [ for _ in 1..nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(6 * fxOne) - 3 * fxOne),
            rng.NextBounded(12 * fxOne) - 6 * fxOne ]

    let thresholds =
        quantizeRates
            { onePoint = thresholdOf 0.5
              twoPoint = thresholdOf 0.4
              geneRecomb = thresholdOf 0.2
              mutation = thresholdOf 0.25
              constReplace = thresholdOf 0.3
              creep = thresholdOf 0.4
              inversion = thresholdOf 0.3
              isTrans = thresholdOf 0.3
              risTrans = thresholdOf 0.3
              geneTrans = 0
              creepSigmaFx = fx 0.1
              constRangeFx = fx 10.0 }

    let pairings =
        [ for e in 0 .. entries - 1 ->
            let parentA = (e * 7) % popSlots
            let parentB = (e * 11 + 3) % popSlots
            let seed = 900_000L + 4_099L * int64 e

            {| entryId = e
               parentA = parentA
               parentB = parentB
               dest = popSlots + e
               child = hwBreedOffspring population[parentA] population[parentB] config thresholds (GepRng seed)
               words = entryWordsOf parentA parentB (popSlots + e) thresholds e false seed |} ]

    {| config = config
       cases = cases
       population = population
       pairings = pairings
       popSlots = popSlots
       nCases = nCases |}

/// The deployed case count: a multiple of the lane's 16 threads, and what the
/// board vector uses.
let private boardVectorData (entries: int) = boardVectorDataCases 32 entries

/// The elaboration gates a cluster cannot pass by accident: `emitDesign` runs
/// the width, reserved-name and one-consumer-per-stream checks over every
/// module in the hierarchy, so a mis-sized net or a doubly-driven ready fails
/// here rather than at Vivado. Walked over every arrangement the knobs reach,
/// including the pooled divide — a shared pod across the lanes — which the
/// queue-mode oracle run does not exercise.
let private clusterElaborations () : bool =
    [ Cluster.clusterPoolWalk 1 false
      Cluster.clusterPoolWalk 2 false
      Cluster.clusterPoolWalk 1 true
      Cluster.clusterPoolWalk 2 true
      Cluster.clusterPoolDivWalk PerLane
      Cluster.clusterPoolDivWalk Pooled
      Cluster.clusterAutoWalk false
      Cluster.clusterAutoWalk true ]
    |> List.forall (fun d -> emitDesign d |> String.length > 0)

/// The WarpCPU cluster in queue mode, end to end against the software chain.
/// A population and a host-published pairing ring sit in one behavioral DDR
/// serving both master channels; the fabric claims entries, breeds, compiles,
/// evaluates, and writes each offspring's genome and `(fitness, entryId, seq)`
/// ring record back to the same memory. Two waves through a 4-slot queue ring
/// and a 4-slot result ring, so both wrap, and mixed rate profiles including
/// evaluate-only (skip-writeback) entries whose dest slot must stay untouched.
///
/// `inlineParents` is the decisive variant: the population's parent slots hold
/// GARBAGE and the real parents live only inside the marshaled work items, so a
/// bit-exact fitness can only mean the fabric bred from the inline copies.
let private clusterQueueVsOracle (nFillers: int) (inlineParents: bool) : bool =
    let config = Cluster.clusterConfig
    let recordWords = Cluster.gepRecordWords
    let recordBytes = recordWords * 4
    let itemBytes = if inlineParents then Cluster.gepWorkItemWords * 4 else recordBytes
    let nCases = 16
    let waves = 2
    let queueSlots = 4
    let ringSlots = 4
    let entries = waves * queueSlots
    let popSlots = 10
    let destBase = 16
    let queueOff = 0
    let popOff = 4096
    let ringOff = 12288

    let rng = GepRng(7171L)
    let rangeFx = fx defaultConstantRange
    let population = [| for _ in 1..popSlots -> hwRandomChromosome config rangeFx rng |]

    let cases =
        [ for _ in 1..nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(6 * fxOne) - 3 * fxOne),
            rng.NextBounded(12 * fxOne) - 6 * fxOne ]

    let profile (rates: float list) =
        match rates with
        | [ p1; p2; gr; mu; cr; cp; inv; is; ris ] ->
            quantizeRates
                { onePoint = thresholdOf p1
                  twoPoint = thresholdOf p2
                  geneRecomb = thresholdOf gr
                  mutation = thresholdOf mu
                  constReplace = thresholdOf cr
                  creep = thresholdOf cp
                  inversion = thresholdOf inv
                  isTrans = thresholdOf is
                  risTrans = thresholdOf ris
                  geneTrans = 0
                  creepSigmaFx = fx 0.1
                  constRangeFx = fx 10.0 }
        | _ -> failwith "a profile is nine rates"

    let profiles =
        [ profile [ 0.4; 0.3; 0.0; 0.1; 0.2; 0.3; 0.2; 0.2; 0.2 ]
          profile [ 0.95; 0.95; 0.9; 0.5; 0.7; 0.9; 0.95; 0.95; 0.95 ]
          profile [ 0.0; 0.0; 0.0; 0.0; 0.0; 0.0; 0.0; 0.0; 0.0 ] ]

    let pairings =
        [ for e in 0 .. entries - 1 ->
            let thresholds = profiles[e % profiles.Length]
            let parentA = (e * 7) % popSlots
            let parentB = (e * 3 + 1) % popSlots
            let seed = 5_000L + 991L * int64 e

            {| entryId = 0xD000 + e
               parentA = parentA
               parentB = parentB
               dest = destBase + e
               thresholds = thresholds
               seed = seed
               // Some zero-rate entries are evaluate-only.
               skip = e % profiles.Length = 2
               child = hwBreedOffspring population[parentA] population[parentB] config thresholds (GepRng seed) |} ]

    let sim = Sim(Cluster.clusterPoolWalk nFillers inlineParents)
    let ddr = SimAxiDdr(sim, 16384)

    let writeWords (byteAddr: int) (words: uint32[]) =
        words |> Array.iteri (fun i w -> ddr.WriteWord(byteAddr + i * 4, w))

    // Parent slots: the real records, or garbage when the work items carry the
    // parents inline (then a correct fitness can only have come from inline).
    for s in 0 .. popSlots - 1 do
        writeWords
            (popOff + s * recordBytes)
            (if inlineParents then
                 Array.init recordWords (fun i -> 0xDEAD0000u ||| uint32 i)
             else
                 geneRecordWords population[s])

    // Sentinel the dest slots, so a writeback — or its absence — is visible.
    let sentinel = Array.init recordWords (fun i -> 0x5EAD0000u ||| uint32 i)

    for p in pairings do
        writeWords (popOff + p.dest * recordBytes) sentinel

    sim.Poke("queue_base", uint64 queueOff)
    sim.Poke("pop_base", uint64 popOff)
    sim.Poke("ring_base", uint64 ringOff)
    sim.Poke("queue_mask", uint64 (queueSlots - 1))
    sim.Poke("ring_mask", uint64 (ringSlots - 1))
    sim.Poke("n_cases", uint64 nCases)
    sim.Poke("start_queue", 1UL)
    ddr.Cycle()
    sim.Poke("start_queue", 0UL)
    ddr.Cycle()

    // Epoch case broadcast: one field per cycle over the shared load bus.
    for idx in 0 .. nCases - 1 do
        let vars, target = cases[idx]

        for f in 0 .. config.variableCount - 1 do
            sim.Poke("ld_case", 1UL)
            sim.Poke("case_addr", uint64 idx)
            sim.Poke("case_field", uint64 f)
            sim.Poke("case_data", uint64 (uint32 vars[f]))
            ddr.Cycle()

        sim.Poke("case_field", uint64 config.variableCount)
        sim.Poke("case_data", uint64 (uint32 target))
        ddr.Cycle()

    sim.Poke("ld_case", 0UL)
    ddr.Cycle()
    ddr.Cycle() // let the per-lane broadcast registers drain

    let mutable cycles = 0

    let awaitResults (target: int) =
        while sim.Peek "results_done" <> uint64 target && cycles < 200_000 do
            ddr.Cycle()
            cycles <- cycles + 1

        sim.Peek "results_done" = uint64 target

    let harvest () =
        [ for r in 0 .. queueSlots - 1 ->
            let b = ringOff + r * 16

            (uint64 (ddr.ReadWord(b + 4)) <<< 32) ||| uint64 (ddr.ReadWord b),
            int (ddr.ReadWord(b + 8)) ]

    let results = ResizeArray<uint64 * int>()

    let ranWaves =
        [ for w in 0 .. waves - 1 ->
            let wave = pairings |> List.skip (w * queueSlots) |> List.take queueSlots

            wave
            |> List.iteri (fun slot p ->
                writeWords (queueOff + slot * itemBytes) (entryWordsOf p.parentA p.parentB p.dest p.thresholds p.entryId p.skip p.seed)

                if inlineParents then
                    writeWords (queueOff + slot * itemBytes + recordBytes) (geneRecordWords population[p.parentA])
                    writeWords (queueOff + slot * itemBytes + 2 * recordBytes) (geneRecordWords population[p.parentB]))

            sim.Poke("entries_published", uint64 ((w + 1) * queueSlots))
            let ok = awaitResults ((w + 1) * queueSlots)
            results.AddRange(harvest ())
            ok ]

    let byEntry = results |> Seq.map (fun (fit, entryId) -> entryId, fit) |> Map.ofSeq

    let fitnessOk =
        pairings
        |> List.forall (fun p ->
            match Map.tryFind p.entryId byEntry with
            | Some fit -> fit = uint64 (fitnessOf config cases p.child)
            | None -> false)

    let writebackOk =
        pairings
        |> List.forall (fun p ->
            let slot = [| for i in 0 .. recordWords - 1 -> ddr.ReadWord(popOff + p.dest * recordBytes + i * 4) |]
            slot = (if p.skip then sentinel else geneRecordWords p.child))

    let quiesced = sim.Peek "all_idle" = 1UL
    let distinct = byEntry.Count = entries

    printfn
        $"      cluster %d{nFillers}f inlineParents=%b{inlineParents}: %d{entries} offspring in %d{cycles} cycles"

    if not (fitnessOk && writebackOk && distinct && quiesced) then
        printfn
            $"      fitness=%b{fitnessOk} writeback=%b{writebackOk} distinct=%b{distinct} idle=%b{quiesced}"

    List.forall id ranWaves && distinct && fitnessOk && writebackOk && quiesced

/// The software mirror of the fabric's generation loop, and the reason auto
/// mode is checkable at all: the same xoshiro draws in the same order (four
/// index draws then four seed words for a tournament entry, four seed words for
/// a short one), the same tournament comparison over the same fitness banking,
/// the same elitism at index 0 and the same region ping-pong.
///
/// `singleBank` is op-list mode's table: the host serializes the emit and breed
/// passes there, so both hit bank 0 and the barrier's flip is a no-op.
///
/// Returns one entry list per round — round 0 is the score round, then `gens`
/// breed rounds — plus the final region flag and the last barrier's argmin.
let private mirrorGenerationLoop
    (config: GepConfig)
    (cases: (int[] * int) list)
    (thresholds: GepBreedThresholds)
    (silent: GepBreedThresholds)
    (popSize: int)
    (gens: int)
    (initialPop: Chromosome[])
    (selSeedWords: int[])
    (singleBank: bool)
    =
    let pop = Array.copy initialPop
    let sel = GepRng(selSeedWords[0], selSeedWords[1], selSeedWords[2], selSeedWords[3])
    let bank = Array.init 2 (fun _ -> Array.zeroCreate<uint64> popSize)
    let mutable writeBank = 0
    let mutable baseFlag = 0
    let mutable elite = 0
    let mutable lastBest = (0UL, 0)
    let rounds = ResizeArray<_>()

    for round in 0 .. gens do
        let phaseScore = round = 0
        let readBank = if singleBank then 0 else 1 - writeBank
        let aBase = baseFlag * popSize
        let aOther = (1 - baseFlag) * popSize

        let entries =
            [ for idx in 0 .. popSize - 1 ->
                let short = phaseScore || idx = 0

                let parentA, parentB =
                    if short then
                        let p = if phaseScore then idx else elite
                        p, p
                    else
                        let draw () = sel.NextWord() &&& (popSize - 1)
                        let d0, d1 = draw (), draw ()
                        let pa = if bank[readBank][d0] <= bank[readBank][d1] then d0 else d1
                        let d2, d3 = draw (), draw ()
                        let pb = if bank[readBank][d2] <= bank[readBank][d3] then d2 else d3
                        pa, pb

                let seeds = Array.init 4 (fun _ -> sel.NextWord())
                // xoshiro's one degenerate point, guarded on the way into the FIFO.
                if Array.forall (fun w -> w = 0) seeds then seeds[0] <- 1

                let child =
                    hwBreedOffspring
                        pop[aBase + parentA]
                        pop[aBase + parentB]
                        config
                        (if short then silent else thresholds)
                        (GepRng(seeds[0], seeds[1], seeds[2], seeds[3]))

                {| id = idx
                   parentA = aBase + parentA
                   parentB = aBase + parentB
                   dest = (if phaseScore then 0 else aOther + idx)
                   skip = phaseScore
                   zeroRates = short
                   seeds = seeds
                   child = child
                   fit = uint64 (fitnessOf config cases child) |} ]

        rounds.Add entries

        for e in entries do
            bank[writeBank][e.id] <- e.fit
            if not e.skip then pop[e.dest] <- e.child

        // The barrier: the elite is the deterministic argmin, lowest id on ties.
        let bestIdx =
            [ 0 .. popSize - 1 ]
            |> List.minBy (fun i -> bank[writeBank][i], i)

        lastBest <- (bank[writeBank][bestIdx], bestIdx)
        elite <- bestIdx
        writeBank <- readBank
        if not phaseScore then baseFlag <- 1 - baseFlag

    {| rounds = List.ofSeq rounds
       baseFlag = baseFlag
       lastBest = lastBest |}

/// The whole generation loop in fabric, against a software mirror of it.
///
/// The fabric runs its own selection: a score round evaluates every individual
/// of region A, then `gens` breed rounds draw parents by binary tournament over
/// the double-buffered fitness table, copy the elite through at index 0, and
/// write each offspring into the OTHER region — after which the regions swap.
/// The mirror reproduces the xoshiro stream word for word (four index draws and
/// four seed words per tournament entry, four seed words for a short one),
/// breeds with `hwBreedOffspring`, and scores with the software chain, so any
/// divergence in draw order, tournament comparison, elitism, banking or the
/// region ping-pong shows up as a wrong genome or a wrong fitness.
///
/// Region B is staged with GARBAGE: round 1 must read only region A, and the
/// fabric would have to be reading the wrong region for that garbage to matter.
let private clusterAutoVsMirror () : bool =
    let config = Cluster.clusterConfig
    let popSize = 8
    let gens = 2
    let nCases = 16
    let recordWords = Cluster.gepRecordWords
    let recordBytes = recordWords * 4
    let popOff = 0
    let ringOff = 2048
    let ringSlots = 32
    let rounds = 1 + gens // the score round, then `gens` breed rounds

    let rng = GepRng(31337L)
    let rangeFx = fx defaultConstantRange

    let thresholds =
        quantizeRates
            { onePoint = thresholdOf 0.6
              twoPoint = thresholdOf 0.4
              geneRecomb = thresholdOf 0.0
              mutation = thresholdOf 0.2
              constReplace = thresholdOf 0.3
              creep = thresholdOf 0.4
              inversion = thresholdOf 0.3
              isTrans = thresholdOf 0.3
              risTrans = thresholdOf 0.3
              geneTrans = 0
              creepSigmaFx = fx 0.1
              constRangeFx = fx 10.0 }

    let silent = silenceRates thresholds

    let cases =
        [ for _ in 1..nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(6 * fxOne) - 3 * fxOne),
            rng.NextBounded(12 * fxOne) - 6 * fxOne ]

    // ---- The mirror ----
    // Region A starts as a drawn population; region B is never read before the
    // first breed round overwrites it, which is what staging garbage there
    // proves.
    let initialPop = Array.init (2 * popSize) (fun _ -> hwRandomChromosome config rangeFx rng)
    let selSeedWords = expandSeed 987_654L

    let mirror =
        mirrorGenerationLoop config cases thresholds silent popSize gens initialPop selSeedWords false

    let expected = mirror.rounds
    let baseFlag = mirror.baseFlag
    let lastBest = mirror.lastBest

    // ---- The fabric ----
    let sim = Sim(Cluster.clusterAutoWalk false)
    let ddr = SimAxiDdr(sim, 4096)

    let writeWords (byteAddr: int) (words: uint32[]) =
        words |> Array.iteri (fun i w -> ddr.WriteWord(byteAddr + i * 4, w))

    for s in 0 .. 2 * popSize - 1 do
        writeWords
            (popOff + s * recordBytes)
            (if s < popSize then
                 geneRecordWords initialPop[s]
             else
                 Array.init recordWords (fun i -> 0xDEAD0000u ||| uint32 i))

    sim.Poke("queue_base", 0UL)
    sim.Poke("pop_base", uint64 popOff)
    sim.Poke("ring_base", uint64 ringOff)
    sim.Poke("queue_mask", 0UL)
    sim.Poke("ring_mask", uint64 (ringSlots - 1))
    sim.Poke("n_cases", uint64 nCases)
    sim.Poke("auto_mode", 1UL)
    sim.Poke("auto_pop", uint64 popSize)
    sim.Poke("auto_gens", uint64 gens)
    sim.Poke("auto_sigma", uint64 (uint32 thresholds.creepSigmaFx))
    sim.Poke("auto_range", uint64 (uint32 thresholds.constRangeFx))

    let high (v: int) = int ((uint32 v) >>> 16)

    // The nine rates, two 16-bit halves per word in the entry's own order.
    for w, (lo, hi) in
        List.indexed
            [ thresholds.mutation, thresholds.constReplace
              thresholds.creep, thresholds.inversion
              thresholds.isTrans, thresholds.risTrans
              thresholds.onePoint, thresholds.twoPoint
              thresholds.geneRecomb, 0 ] do
        sim.Poke($"auto_r{w}", uint64 (uint32 (high lo ||| (high hi <<< 16))))

    for i in 0..3 do
        sim.Poke($"auto_s{i}", uint64 (uint32 selSeedWords[i]))

    // Cases first: auto mode starts evaluating the moment it is started, so a
    // load afterwards would race the score round.
    for idx in 0 .. nCases - 1 do
        let vars, target = cases[idx]

        for f in 0 .. config.variableCount - 1 do
            sim.Poke("ld_case", 1UL)
            sim.Poke("case_addr", uint64 idx)
            sim.Poke("case_field", uint64 f)
            sim.Poke("case_data", uint64 (uint32 vars[f]))
            ddr.Cycle()

        sim.Poke("case_field", uint64 config.variableCount)
        sim.Poke("case_data", uint64 (uint32 target))
        ddr.Cycle()

    sim.Poke("ld_case", 0UL)
    ddr.Cycle()
    ddr.Cycle()

    sim.Poke("start_queue", 1UL)
    ddr.Cycle()
    sim.Poke("start_queue", 0UL)

    let mutable cycles = 0

    while sim.Peek "auto_done" <> 1UL && cycles < 400_000 do
        ddr.Cycle()
        cycles <- cycles + 1

    let finished = sim.Peek "auto_done" = 1UL

    // ---- Compare ----
    let ringRecords =
        [ for r in 0 .. rounds * popSize - 1 ->
            let b = ringOff + r * 16

            (uint64 (ddr.ReadWord(b + 4)) <<< 32) ||| uint64 (ddr.ReadWord b),
            int (ddr.ReadWord(b + 8)) ]

    // Round boundaries are clean (a barrier separates them); within a round the
    // lanes finish in whatever order they finish, so map by entry id.
    let fitnessOk =
        [ 0 .. rounds - 1 ]
        |> List.forall (fun round ->
            let seen =
                ringRecords
                |> List.skip (round * popSize)
                |> List.take popSize
                |> List.map (fun (f, id) -> id, f)
                |> Map.ofList

            seen.Count = popSize
            && expected[round]
               |> List.forall (fun e -> Map.tryFind e.id seen = Some e.fit))

    let slotOk (slot: int) (c: Chromosome) =
        let got = [| for i in 0 .. recordWords - 1 -> ddr.ReadWord(popOff + slot * recordBytes + i * 4) |]
        got = geneRecordWords c

    // Every genome written by the last two rounds is still in DDR: the final
    // breed round's children in one region, the previous round's in the other.
    let writebackOk =
        [ 0 .. popSize - 1 ]
        |> List.forall (fun idx ->
            let last = expected[rounds - 1][idx]
            let prev = expected[rounds - 2][idx]
            slotOk last.dest last.child && slotOk prev.dest prev.child)

    let bestFit, bestIdx = lastBest

    let bestOk =
        sim.Peek "best_idx" = uint64 bestIdx
        && ((sim.Peek "best_fit_hi" <<< 32) ||| sim.Peek "best_fit_lo") = bestFit

    let stateOk =
        sim.Peek "auto_round" = uint64 gens
        && sim.Peek "auto_base" = uint64 baseFlag
        && sim.Peek "all_idle" = 1UL

    printfn
        $"      cluster auto: %d{rounds} rounds x %d{popSize} in %d{cycles} cycles, best %d{bestIdx} @ %d{bestFit}"

    if not (finished && fitnessOk && writebackOk && bestOk && stateOk) then
        printfn
            $"      done=%b{finished} fitness=%b{fitnessOk} writeback=%b{writebackOk} best=%b{bestOk} state=%b{stateOk}"

    finished && fitnessOk && writebackOk && bestOk && stateOk

/// The op-list emitter: the producer half of the streaming redesign. The score
/// round runs in fabric exactly as it does in the fused loop, and then the breed
/// round does NOT breed — it streams its pairing entries to a DDR ring in the
/// same 16-word record queue mode consumes, for the host to gather inline
/// parents against. What that pins is the whole selection stack (draws,
/// tournaments against the score round's own fitnesses, elitism, the guarded
/// seed) plus the entry format shared with the queue half.
///
/// The table is single-banked here, which is the point of `opList`: the host
/// serializes the emit and breed passes, so both hit bank 0.
let private clusterOpListVsMirror () : bool =
    let config = Cluster.clusterConfig
    let popSize = 8
    let nCases = 16
    let recordWords = Cluster.gepRecordWords
    let recordBytes = recordWords * 4
    let popOff = 0
    let ringOff = 2048
    let oplistOff = 2560
    let ringSlots = 32

    let rng = GepRng(20_260_810L)
    let rangeFx = fx defaultConstantRange

    let thresholds =
        quantizeRates
            { onePoint = thresholdOf 0.5
              twoPoint = thresholdOf 0.35
              geneRecomb = thresholdOf 0.25
              mutation = thresholdOf 0.2
              constReplace = thresholdOf 0.3
              creep = thresholdOf 0.4
              inversion = thresholdOf 0.3
              isTrans = thresholdOf 0.3
              risTrans = thresholdOf 0.3
              geneTrans = 0
              creepSigmaFx = fx 0.1
              constRangeFx = fx 10.0 }

    let silent = silenceRates thresholds

    let cases =
        [ for _ in 1..nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(6 * fxOne) - 3 * fxOne),
            rng.NextBounded(12 * fxOne) - 6 * fxOne ]

    let initialPop = Array.init (2 * popSize) (fun _ -> hwRandomChromosome config rangeFx rng)
    let selSeedWords = expandSeed 4_711L

    // One breed round is all a single-shot emit produces.
    let mirror =
        mirrorGenerationLoop config cases thresholds silent popSize 1 initialPop selSeedWords true

    let high (v: int) = int ((uint32 v) >>> 16)

    // The five packed rate words the host programs, which are also words 3..7
    // of every emitted entry.
    let rateWords =
        [| for lo, hi in
               [ thresholds.mutation, thresholds.constReplace
                 thresholds.creep, thresholds.inversion
                 thresholds.isTrans, thresholds.risTrans
                 thresholds.onePoint, thresholds.twoPoint
                 thresholds.geneRecomb, 0 ] -> uint32 (high lo ||| (high hi <<< 16)) |]

    let expectedEntry (e: {| id: int
                             parentA: int
                             parentB: int
                             dest: int
                             skip: bool
                             zeroRates: bool
                             seeds: int[]
                             child: Chromosome
                             fit: uint64 |}) =
        let flags = if e.skip then Cluster.gepFlagSkipWriteback else 0
        let rate i = if e.zeroRates then 0u else rateWords[i]

        [| yield uint32 e.parentA
           yield uint32 e.parentB
           yield uint32 e.dest
           for i in 0..3 -> rate i
           yield (uint32 flags <<< 16) ||| (rate 4 &&& 0xFFFFu)
           yield uint32 thresholds.creepSigmaFx
           yield uint32 thresholds.constRangeFx
           yield uint32 e.id
           yield 0u
           for w in e.seeds -> uint32 w |]

    let sim = Sim(Cluster.clusterAutoWalk true)
    let ddr = SimAxiDdr(sim, 4096)

    let writeWords (byteAddr: int) (words: uint32[]) =
        words |> Array.iteri (fun i w -> ddr.WriteWord(byteAddr + i * 4, w))

    for s in 0 .. 2 * popSize - 1 do
        writeWords
            (popOff + s * recordBytes)
            (if s < popSize then
                 geneRecordWords initialPop[s]
             else
                 Array.init recordWords (fun i -> 0xDEAD0000u ||| uint32 i))

    sim.Poke("queue_base", 0UL)
    sim.Poke("pop_base", uint64 popOff)
    sim.Poke("ring_base", uint64 ringOff)
    sim.Poke("oplist_base", uint64 oplistOff)
    sim.Poke("queue_mask", 0UL)
    sim.Poke("ring_mask", uint64 (ringSlots - 1))
    sim.Poke("n_cases", uint64 nCases)
    sim.Poke("auto_mode", 1UL)
    sim.Poke("auto_pop", uint64 popSize)
    sim.Poke("auto_gens", 1UL)
    sim.Poke("auto_sigma", uint64 (uint32 thresholds.creepSigmaFx))
    sim.Poke("auto_range", uint64 (uint32 thresholds.constRangeFx))
    sim.Poke("skip_score", 0UL)
    sim.Poke("rng_continue", 0UL)
    rateWords |> Array.iteri (fun i w -> sim.Poke($"auto_r{i}", uint64 w))

    for i in 0..3 do
        sim.Poke($"auto_s{i}", uint64 (uint32 selSeedWords[i]))

    for idx in 0 .. nCases - 1 do
        let vars, target = cases[idx]

        for f in 0 .. config.variableCount - 1 do
            sim.Poke("ld_case", 1UL)
            sim.Poke("case_addr", uint64 idx)
            sim.Poke("case_field", uint64 f)
            sim.Poke("case_data", uint64 (uint32 vars[f]))
            ddr.Cycle()

        sim.Poke("case_field", uint64 config.variableCount)
        sim.Poke("case_data", uint64 (uint32 target))
        ddr.Cycle()

    sim.Poke("ld_case", 0UL)
    ddr.Cycle()
    ddr.Cycle()

    sim.Poke("start_queue", 1UL)
    ddr.Cycle()
    sim.Poke("start_queue", 0UL)

    let mutable cycles = 0

    while sim.Peek "oplist_done" <> 1UL && cycles < 400_000 do
        ddr.Cycle()
        cycles <- cycles + 1

    let emitted = sim.Peek "oplist_done" = 1UL

    // The score round's fitnesses still go to the ring; the breed round's
    // entries go to the op-list ring instead of breeding.
    let scoreOk =
        let seen =
            [ for r in 0 .. popSize - 1 ->
                int (ddr.ReadWord(ringOff + r * 16 + 8)),
                (uint64 (ddr.ReadWord(ringOff + r * 16 + 4)) <<< 32)
                ||| uint64 (ddr.ReadWord(ringOff + r * 16)) ]
            |> Map.ofList

        seen.Count = popSize
        && mirror.rounds[0] |> List.forall (fun e -> Map.tryFind e.id seen = Some e.fit)

    let entriesOk =
        mirror.rounds[1]
        |> List.forall (fun e ->
            let got =
                [| for w in 0 .. recordWords - 1 -> ddr.ReadWord(oplistOff + e.id * recordBytes + w * 4) |]

            got = expectedEntry e)

    // Nothing bred, so the population must be exactly as staged.
    let untouched =
        [ 0 .. popSize - 1 ]
        |> List.forall (fun s ->
            [| for i in 0 .. recordWords - 1 -> ddr.ReadWord(popOff + s * recordBytes + i * 4) |] = geneRecordWords initialPop[s])

    let finished = sim.Peek "auto_done" = 1UL && sim.Peek "all_idle" = 1UL

    printfn $"      cluster op-list: %d{popSize} entries emitted in %d{cycles} cycles"

    if not (emitted && scoreOk && entriesOk && untouched && finished) then
        printfn
            $"      emitted=%b{emitted} score=%b{scoreOk} entries=%b{entriesOk} untouched=%b{untouched} done=%b{finished}"

    emitted && scoreOk && entriesOk && untouched && finished

/// The seam: the same pool, the same offspring, driven the way silicon drives
/// it — every control word through the AXI-Lite register map and every status
/// word read back out of it, with nothing poked at a bare port. What this
/// checks is the wiring the bare-port oracle cannot see: that each register
/// reaches the config bit it claims to, that the pulses pulse, and that the
/// telemetry a driver polls means what the map says.
///
/// The map is read here, never re-typed: offsets come from the same
/// `gepClusterMap` value the slave is elaborated from, so a moved register
/// moves the check with it.
let private clusterAxiSeamParts (jitter: int option) : bool * bool =
    let config = Cluster.clusterConfig
    let shape = Cluster.clusterShape 1 false
    let m = ClusterAxi.gepClusterMap shape
    let recordWords = Cluster.gepRecordWords
    let recordBytes = recordWords * 4
    let nCases = 16
    let entries = 4
    let popSlots = 6
    let destBase = 8
    let queueOff = 0
    let popOff = 2048
    let ringOff = 6144

    let rng = GepRng(5150L)
    let rangeFx = fx defaultConstantRange
    let population = [| for _ in 1..popSlots -> hwRandomChromosome config rangeFx rng |]

    let cases =
        [ for _ in 1..nCases ->
            Array.init config.variableCount (fun _ -> rng.NextBounded(6 * fxOne) - 3 * fxOne),
            rng.NextBounded(12 * fxOne) - 6 * fxOne ]

    let thresholds =
        quantizeRates
            { onePoint = thresholdOf 0.5
              twoPoint = thresholdOf 0.4
              geneRecomb = thresholdOf 0.2
              mutation = thresholdOf 0.25
              constReplace = thresholdOf 0.3
              creep = thresholdOf 0.4
              inversion = thresholdOf 0.3
              isTrans = thresholdOf 0.3
              risTrans = thresholdOf 0.3
              geneTrans = 0
              creepSigmaFx = fx 0.1
              constRangeFx = fx 10.0 }

    let pairings =
        [ for e in 0 .. entries - 1 ->
            let parentA = (e * 3) % popSlots
            let parentB = (e * 5 + 1) % popSlots
            let seed = 7_000L + 313L * int64 e
            let skip = e = 2 // one evaluate-only entry, so the skip path is wired too

            {| entryId = 0xAB00 + e
               parentA = parentA
               parentB = parentB
               dest = destBase + e
               skip = skip
               child = hwBreedOffspring population[parentA] population[parentB] config thresholds (GepRng seed)
               words = entryWordsOf parentA parentB (destBase + e) thresholds (0xAB00 + e) skip seed |} ]

    let sim = Sim(ClusterAxi.clusterAxiWalk)
    let ddr = SimAxiDdr(sim, 8192, ?jitter = jitter)

    let writeWords (byteAddr: int) (words: uint32[]) =
        words |> Array.iteri (fun i w -> ddr.WriteWord(byteAddr + i * 4, w))

    for s in 0 .. popSlots - 1 do
        writeWords (popOff + s * recordBytes) (geneRecordWords population[s])

    let sentinel = Array.init recordWords (fun i -> 0x5EAD0000u ||| uint32 i)

    for p in pairings do
        writeWords (popOff + p.dest * recordBytes) sentinel
        writeWords (queueOff + (p.entryId % entries) * recordBytes) p.words

    // ---- the AXI-Lite master, one transaction at a time, servicing the DDR
    // on every cycle so the fabric keeps running underneath it ----
    let mutable protocolOk = true

    // The handshakes live in `SimAxi` — six copies of them used to live
    // in the projects, this one included.
    let axi = SimAxi.clientWith sim (ddr.Cycle)
    let read32, write32 = axi.read32, axi.write32

    let field (e: RegEntry) =
        match e.kind with
        | RoField (shift, w) -> (read32 e.offset >>> shift) &&& ((1UL <<< w) - 1UL)
        | _ -> failwith $"'{e.name}' is not a read-only field"

    write32 m.queueBase.offset (uint64 queueOff)
    write32 m.popBase.offset (uint64 popOff)
    write32 m.ringBase.offset (uint64 ringOff)
    write32 m.queueMask.offset (uint64 (entries - 1))
    write32 m.ringMask.offset (uint64 (entries - 1))
    write32 m.nCases.offset (uint64 nCases)
    write32 ClusterAxi.ctrlOffset 1UL // the start pulse, bit 0 of the control word

    for idx in 0 .. nCases - 1 do
        let vars, target = cases[idx]
        write32 m.caseAddr.offset (uint64 idx)

        for f in 0 .. config.variableCount - 1 do
            write32 m.caseField.offset (uint64 f)
            write32 m.caseData.offset (uint64 (uint32 vars[f]))
            write32 ClusterAxi.ctrlOffset 2UL // the ldCase pulse, bit 1

        write32 m.caseField.offset (uint64 config.variableCount)
        write32 m.caseData.offset (uint64 (uint32 target))
        write32 ClusterAxi.ctrlOffset 2UL

    // The per-lane broadcast registers are one cycle behind the bus.
    ddr.Cycle()
    ddr.Cycle()

    let runningAfterStart = field m.running = 1UL
    write32 m.entriesPublished.offset (uint64 entries)

    let mutable cycles = 0

    while field m.resultsDone <> uint64 entries && cycles < 200_000 do
        ddr.Cycle()
        cycles <- cycles + 1

    let harvested = field m.resultsDone = uint64 entries

    // Quiesce before reading the ring: all_idle is what a driver waits on.
    let mutable settle = 0

    while field m.allIdle <> 1UL && settle < 1000 do
        ddr.Cycle()
        settle <- settle + 1

    let byEntry =
        [ for r in 0 .. entries - 1 ->
            int (ddr.ReadWord(ringOff + r * 16 + 8)),
            (uint64 (ddr.ReadWord(ringOff + r * 16 + 4)) <<< 32)
            ||| uint64 (ddr.ReadWord(ringOff + r * 16)) ]
        |> Map.ofList

    let fitnessOk =
        byEntry.Count = entries
        && pairings
           |> List.forall (fun p -> Map.tryFind p.entryId byEntry = Some(uint64 (fitnessOf config cases p.child)))

    let writebackOk =
        pairings
        |> List.forall (fun p ->
            let got = [| for i in 0 .. recordWords - 1 -> ddr.ReadWord(popOff + p.dest * recordBytes + i * 4) |]
            got = (if p.skip then sentinel else geneRecordWords p.child))

    // The telemetry a driver polls has to mean what the map says it means.
    let telemetryOk =
        field m.entriesTaken = uint64 entries
        && field m.allIdle = 1UL
        && field m.streamsActive = 0UL
        && field m.cycleCount > 0UL
        && field m.busyBreederCycles > 0UL
        && field m.busyLaneCycles > 0UL
        && (m.breederBusy |> List.forall (fun e -> field e > 0UL))
        && (m.laneBusy |> List.forall (fun e -> field e > 0UL))

    printfn $"      cluster seam: %d{entries} offspring through the register map in %d{cycles} cycles"

    // Occupancy is deliberately NOT part of `resultsOk`. "every lane was busy
    // at least once" is a statement about scheduling, and with four offspring
    // the lanes are starved by construction (see `boardvector`) — shift the
    // memory timing and a lane that happened to get work now does not. That is
    // the test being brittle, not the design being wrong, and conflating the
    // two would make a timing sweep useless.
    let resultsOk = protocolOk && runningAfterStart && harvested && fitnessOk && writebackOk

    if not (resultsOk && telemetryOk) then
        printfn
            $"      protocol=%b{protocolOk} running=%b{runningAfterStart} harvested=%b{harvested} fitness=%b{fitnessOk} writeback=%b{writebackOk} telemetry=%b{telemetryOk}"

    resultsOk, telemetryOk

/// The check that closed the silicon fitness tail, and the reason it stays.
///
/// The board run came back with every genome bit-exact but ~5% of fitnesses off
/// by 26-142 parts in 10^9. It reproduced HERE, in the Sim, to the exact same
/// three individuals and the exact same deltas — which is itself the useful
/// half: sim and silicon agreed perfectly, including on the bug, so nothing
/// about the board was ever suspect.
///
/// The cause was not the fabric and not saturation (the first hypothesis, and
/// wrong — one failing individual had zero clipped terms). Splitting the batch
/// by opcode was decisive: **every failing individual had exactly one DIV and
/// every passing one had none.** `Fixed.useRecipDiv` is a global mutable seam,
/// default FALSE, and only `aluVsApplyOp` had ever set it — so every other
/// software twin was predicting a machine that divides exactly, while the
/// fabric always divides by reciprocal. `withRecipDiv` now scopes it, and the
/// twins that judge hardware all run inside it.
///
/// This check keeps the door shut by using the exact children silicon
/// evaluated, at the exact cases. It reports clipped terms too, so a green run
/// cannot be mistaken for coverage it does not have.
let private laneDivFitnessVsEvaluate () : bool =
    let vector = boardVectorData 64
    let config = vector.config
    let cases = vector.cases
    let nCases = vector.nCases
    let capacity = 32
    let indivWords = Hdl.gepUnitIndivWords capacity config.constantCount
    // The lane bank holds 512 words, so twelve records fit; take the eight
    // largest-fitness children, which is where the failures were.
    let batch =
        vector.pairings
        |> List.sortByDescending (fun p -> fitnessOf config cases p.child)
        |> List.truncate 8

    // How many of this individual's per-case squared errors clip at the 32-bit
    // ceiling — the coverage number, so a green run cannot be a vacuous one.
    let saturatingTerms (c: Chromosome) =
        withRecipDiv (fun () ->
            let program = compileChromosome config c

            cases
            |> List.filter (fun (vars, target) ->
                let err = fxSub (runProgram program vars c.constants) target
                fxMul err err = System.Int32.MaxValue)
            |> List.length)

    let bigOf (words: uint64[]) =
        Array.fold
            (fun acc (w: uint64) -> (acc <<< 32) ||| System.Numerics.BigInteger(w))
            System.Numerics.BigInteger.Zero
            (Array.rev words)

    let sim = Sim(Hdl.unitEngineDivWalk PerLane)

    for idx in 0 .. nCases - 1 do
        let vars, target = cases[idx]
        let varWords = Array.init 4 (fun v -> if v < vars.Length then uint64 (uint32 vars[v]) else 0UL)
        sim.Poke("fill_case_en", 1UL)
        sim.Poke("fill_case_sel", 0UL)
        sim.Poke("fill_case_addr", uint64 idx)
        sim.PokeWide("fill_beat", bigOf varWords)
        sim.Tick()
        sim.Poke("fill_case_sel", 1UL)
        sim.PokeWide("fill_beat", bigOf [| uint64 (uint32 target); 0UL; 0UL; 0UL |])
        sim.Tick()

    sim.Poke("fill_case_en", 0UL)
    let lineBase = ref 0

    for p in batch do
        let program = compileChromosome config p.child

        let words =
            [| yield
                   uint64 program.instructions.Length
                   ||| (uint64 (srcOrdinal program.outputSrc) <<< 8)
                   ||| (uint64 program.outputIdx <<< 10)
               for ins in program.instructions -> uint64 (packInstruction ins)
               yield! Array.create (capacity - program.instructions.Length) 0UL
               for k in p.child.constants -> uint64 (uint32 k)
               yield! Array.create (indivWords - 1 - capacity - config.constantCount) 0UL |]

        for line in 0 .. indivWords / 4 - 1 do
            sim.Poke("fill_indiv_en", 1UL)
            sim.Poke("fill_indiv_addr", uint64 (lineBase.Value + line))
            sim.PokeWide("fill_beat", bigOf (Array.sub words (line * 4) 4))
            sim.Tick()

        lineBase.Value <- lineBase.Value + indivWords / 4

    sim.Poke("fill_indiv_en", 0UL)
    sim.Poke("fill_unit_id", 5UL)
    sim.Poke("fill_commit", 1UL)
    sim.Tick()
    sim.Poke("fill_commit", 0UL)
    sim.Poke("n_cases", uint64 nCases)
    sim.Poke("m_count", uint64 batch.Length)
    sim.Poke("res_ready", 1UL)

    let collected = ResizeArray<uint64>()
    let mutable cycles = 0

    while collected.Count < batch.Length && cycles < 200_000 do
        if sim.Peek "res_valid" = 1UL then
            collected.Add(sim.Peek "res_fit")

        sim.Tick()
        cycles <- cycles + 1

    let got = List.ofSeq collected
    let want = [ for p in batch -> uint64 (fitnessOf config cases p.child) ]
    let clipped = batch |> List.sumBy (fun p -> saturatingTerms p.child)

    printfn
        $"      lane saturation: %d{batch.Length} individuals, %d{clipped}/%d{batch.Length * nCases} case terms clip at 2^31-1"

    if got <> want then
        List.zip3 batch got want
        |> List.iter (fun (p, g, w) ->
            if g <> w then
                let program = compileChromosome config p.child
                let opCount op = program.instructions |> List.filter (fun i -> i.op = op) |> List.length

                printfn
                    $"      entry %d{p.entryId}: delta %d{int64 g - int64 w}, clipped %d{saturatingTerms p.child}, ops div %d{opCount DIV} gt %d{opCount GT} lt %d{opCount LT} sub %d{opCount SUB} mul %d{opCount MUL} add %d{opCount ADD}")

        printfn "      passing individuals for contrast:"

        List.zip3 batch got want
        |> List.iter (fun (p, g, w) ->
            if g = w then
                let program = compileChromosome config p.child
                let opCount op = program.instructions |> List.filter (fun i -> i.op = op) |> List.length

                printfn
                    $"      entry %d{p.entryId}: OK, clipped %d{saturatingTerms p.child}, ops div %d{opCount DIV} gt %d{opCount GT} lt %d{opCount LT} sub %d{opCount SUB} mul %d{opCount MUL} add %d{opCount ADD}")

    got.Length = batch.Length && got = want

/// One point of the feeder/lane sweep: build the cluster at this mix, run a
/// batch of offspring through a behavioral DDR at a given read latency, and
/// report throughput and where the time went.
///
/// Queue mode only — the question is about the feed path, and the generation
/// loop would only add elaboration the sweep never exercises.
///
/// **`rDelay` is the whole experiment.** With a zero-latency DDR every mix
/// looks alike, because the thing that actually serializes the pool on silicon
/// is that a filler HOLDS the shared read master across a whole burst: one DDR
/// round trip in flight at a time, three per offspring. A model without read
/// latency cannot see that and would recommend the wrong shape.
let private runClusterMix (sharing: FuSharing) (nBreeders: int) (nLanes: int) (nFillers: int) (rDelay: int) (nCases: int) (entries: int) =
    let vector = boardVectorDataCases nCases entries
    let config = vector.config
    let recordBytes = Cluster.gepRecordWords * 4
    let itemBytes = Cluster.gepWorkItemWords * 4
    // The ring masks are power-of-two wraps, so the batch must be one.
    if entries &&& (entries - 1) <> 0 then
        failwith $"the sweep batch must be a power of two, got %d{entries}"

    let queueOff = 0
    let popOff = 0x4000
    let ringOff = 0xC000

    let shape =
        { ClusterAxi.clusterSiliconShape with
            nBreeders = nBreeders
            nLanes = nLanes
            nFillers = nFillers
            divide = Some sharing
            auto = None }

    let tag =
        match sharing with
        | PerLane -> "PerLane"
        | Pooled -> "Pooled"

    let design =
        Cluster.clusterPoolDesign $"GepClusterMix%d{nBreeders}b%d{nLanes}l%d{nFillers}f{tag}" shape

    // Through the width/name/stream gates before simulating: a mis-sized net
    // shows up here as a message, and in the Sim as an out-of-range DDR write
    // twenty minutes later.
    emitDesign design |> ignore
    let sim = Sim(design)
    let ddr = SimAxiDdr(sim, 0x10000, rDelay = rDelay)

    let writeWords (byteAddr: int) (words: uint32[]) =
        words |> Array.iteri (fun i w -> ddr.WriteWord(byteAddr + i * 4, w))

    for s in 0 .. vector.popSlots - 1 do
        writeWords (popOff + s * recordBytes) (geneRecordWords vector.population[s])

    vector.pairings
    |> List.iteri (fun e p ->
        writeWords (queueOff + e * itemBytes) p.words
        writeWords (queueOff + e * itemBytes + recordBytes) (geneRecordWords vector.population[p.parentA])
        writeWords (queueOff + e * itemBytes + 2 * recordBytes) (geneRecordWords vector.population[p.parentB]))

    sim.Poke("queue_base", uint64 queueOff)
    sim.Poke("pop_base", uint64 popOff)
    sim.Poke("ring_base", uint64 ringOff)
    sim.Poke("queue_mask", uint64 (entries - 1))
    sim.Poke("ring_mask", uint64 (entries - 1))
    sim.Poke("n_cases", uint64 vector.nCases)
    sim.Poke("start_queue", 1UL)
    ddr.Cycle()
    sim.Poke("start_queue", 0UL)
    ddr.Cycle()

    // Staged the way the *driver* stages them, not the way that is convenient
    // here: `case_addr` written once per case, then per field a field write, a
    // data write, and a one-cycle `ld_case` pulse with idle cycles between —
    // which is what an AXI-Lite host actually produces. Holding `ld_case` high
    // and changing the address every cycle is a different waveform, and a
    // difference between the harness and the driver is a difference the board
    // gets to discover on its own.
    for idx in 0 .. vector.nCases - 1 do
        let vars, target = vector.cases[idx]
        sim.Poke("case_addr", uint64 idx)
        ddr.Cycle()

        let fields =
            [ for f in 0 .. config.variableCount - 1 -> uint64 (uint32 vars[f]) ]
            @ [ uint64 (uint32 target) ]

        fields
        |> List.iteri (fun f value ->
            sim.Poke("case_field", uint64 f)
            ddr.Cycle()
            sim.Poke("case_data", value)
            ddr.Cycle()
            sim.Poke("ld_case", 1UL)
            ddr.Cycle()
            sim.Poke("ld_case", 0UL)
            ddr.Cycle())

    ddr.Cycle()
    ddr.Cycle()

    let startCycle = sim.Peek "cycle_count"
    sim.Poke("entries_published", uint64 entries)
    let mutable spun = 0

    while sim.Peek "results_done" <> uint64 entries && spun < 4_000_000 do
        ddr.Cycle()
        spun <- spun + 1

    let cycles = int (sim.Peek "cycle_count" - startCycle)

    // A configuration that is fast because it is broken must not win.
    let ring =
        [ for r in 0 .. entries - 1 ->
            int (ddr.ReadWord(ringOff + r * 16 + 8)),
            (uint64 (ddr.ReadWord(ringOff + r * 16 + 4)) <<< 32)
            ||| uint64 (ddr.ReadWord(ringOff + r * 16)) ]
        |> Map.ofList

    let exact =
        vector.pairings
        |> List.filter (fun p -> Map.tryFind p.entryId ring = Some(uint64 (fitnessOf config vector.cases p.child)))
        |> List.length

    {| breeders = nBreeders
       lanes = nLanes
       fillers = nFillers
       rDelay = rDelay
       nCases = nCases
       sharing = tag
       cycles = cycles
       perOffspring = float cycles / float entries
       exact = exact
       entries = entries
       fillBusy = float (sim.Peek "fill_busy_cycles") / float cycles
       breederBusy = float (sim.Peek "busy_breeder_cycles") / float cycles / float nBreeders
       laneBusy = float (sim.Peek "busy_lane_cycles") / float cycles / float nLanes
       // Per-lane, not just the mean: the board reports these and the mean can
       // hide a lane that never ran at all.
       perLane =
           [ for i in 0 .. nLanes - 1 -> 100.0 * float (sim.Peek $"lane{i}_busy_cycles") / float cycles ]
       feedStall = float (sim.Peek "feed_stall_cycles") / float cycles |}

let private printMixHeader () =
    printfn "  div       b  l  f  rDly  cases    cycles   cyc/off   fill%%  breeder%%  lane%%  exact"

let private printMixRow (r: {| breeders: int
                               lanes: int
                               fillers: int
                               rDelay: int
                               nCases: int
                               sharing: string
                               cycles: int
                               perOffspring: float
                               exact: int
                               entries: int
                               fillBusy: float
                               breederBusy: float
                               laneBusy: float
                               perLane: float list
                               feedStall: float |}) =
    printfn
        "  %-8s  %d  %d  %d  %4d  %5d  %8d  %7.1f  %6.0f  %8.0f  %5.0f  %d/%d"
        r.sharing r.breeders r.lanes r.fillers r.rDelay r.nCases r.cycles r.perOffspring
        (100.0 * r.fillBusy) (100.0 * r.breederBusy) (100.0 * r.laneBusy)
        r.exact r.entries

    let lanes = r.perLane |> List.mapi (fun i p -> $"l%d{i} %.0f{p}%%") |> String.concat " "
    printfn $"      {lanes}"

/// What `debug` will open, by label. The same principle the debugger's own
/// registry keeps — a design is here because someone put it here — on this side
/// of the dependency, where the state machines worth watching live.
let private debuggable =
    [ "operator-engine", fun () -> Hdl.operatorEngineWalk
      "karva", fun () -> Hdl.karvaCompilerWalk
      "breeder", fun () -> Hdl.breederBlockWalk
      "unit-engine", fun () -> Hdl.unitEngineWalk
      "router", fun () -> Hdl.recordRouterWalk
      "cluster", fun () -> Cluster.clusterPoolWalk 2 true
      "cluster-auto", fun () -> Cluster.clusterAutoWalk false ]

[<EntryPoint>]
let main argv =
    match argv with
    | [| "debug"; label |] ->
        match debuggable |> List.tryFind (fst >> (=) label) with
        | Some (_, build) -> Warp11.SimView.Desktop.debug $"GEP — {label}" (build ())
        | None ->
            printfn "unknown design '%s'" label
            printfn "try: %s" (String.concat ", " (List.map fst debuggable))
            1
    | [| "diff"; outDir |] ->
        writeDiff
            [ Hdl.xoshiroWalk
              Hdl.recipRomWalk
              Hdl.gepDivRecip
              Hdl.gepAluPlain
              Hdl.gepAluDiv
              Hdl.karvaCompilerWalk
              Hdl.operatorEngineWalk
              Hdl.unitEngineWalk
              Hdl.unitEngineDivWalk PerLane
              Hdl.unitEngineDivWalk Pooled
              Hdl.breederBlockWalk
              Hdl.recordRouterWalk ]
            outDir
        0
    | [| "emit-cluster"; dir |] ->
        // The cluster's RTL, one file per arrangement, for a Verilator lint
        // pass — the design is too large to carry in the differential's
        // testbench loop, where every design is also compiled and run.
        System.IO.Directory.CreateDirectory dir |> ignore

        for name, design in
            [ "queue", Cluster.clusterPoolWalk 1 false
              "inline", Cluster.clusterPoolWalk 2 true
              "divpooled", Cluster.clusterPoolDivWalk Pooled
              "auto", Cluster.clusterAutoWalk false
              "oplist", Cluster.clusterAutoWalk true ] do
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name + ".v"), emitDesign design + "\n")
            printfn $"{design.name} -> {name}.v"

        0
    | [| "hardware"; repoRoot |] ->
        // The seam: the silicon config's Verilog where Vivado sources it, and
        // the generated Rust layout where the driver owns it. Both come off the
        // one register map the slave is elaborated from.
        let buildDir = System.IO.Path.Combine(repoRoot, "hardware", "build")
        let runtimeSrc = System.IO.Path.Combine(repoRoot, "runtime", "core", "src")
        System.IO.Directory.CreateDirectory buildDir |> ignore
        let verilogPath = System.IO.Path.Combine(buildDir, "GepClusterAxi.v")
        let layoutPath = System.IO.Path.Combine(runtimeSrc, "gep_layout.rs")
        let started = System.Diagnostics.Stopwatch.StartNew()
        let verilog = emitDesign ClusterAxi.clusterAxiSilicon.Value
        System.IO.File.WriteAllText(verilogPath, verilog + "\n")

        System.IO.File.WriteAllText(
            layoutPath,
            String.concat "\n" (ClusterAxi.clusterLayoutRs ClusterAxi.clusterSiliconShape)
        )

        printfn $"wrote {verilogPath} ({verilog.Length / 1024} KiB, {started.ElapsedMilliseconds} ms)"
        printfn $"wrote {layoutPath}"
        0
    | [| "boardvector"; dir |]
    | [| "boardvector"; dir; _ |] ->
        // The silicon first-light vector: a population, a set of fitness cases,
        // host-marshaled inline-parent work items, and the answers. The board
        // CLI stages the first three into DDR and checks the fabric against the
        // fourth, so silicon is judged by the same oracle the Sim is.
        System.IO.Directory.CreateDirectory dir |> ignore
        let shape = ClusterAxi.clusterSiliconShape
        // Derived from the shape, never re-typed: the fabric's symbol ROMs and
        // this oracle must be built from ONE function set.
        let config = ClusterAxi.clusterSiliconConfig

        if config.functionSet <> shape.functionSet || config.terminalSet <> shape.terminalSet then
            failwith "the board vector's config and the elaborated shape disagree about the symbol sets"

        // Entry count is an argument: 64 starves eight lanes (measured — the
        // feed stalls for half the run), so a throughput number needs a batch
        // big enough to keep them fed.
        let entries =
            match argv with
            | [| _; _; n |] -> int n
            | _ -> 64

        let popSlots = 64
        let nCases = 32
        let rng = GepRng(2_026_0810L)
        let rangeFx = fx defaultConstantRange
        let population = [| for _ in 1..popSlots -> hwRandomChromosome config rangeFx rng |]

        let cases =
            [ for _ in 1..nCases ->
                Array.init config.variableCount (fun _ -> rng.NextBounded(6 * fxOne) - 3 * fxOne),
                rng.NextBounded(12 * fxOne) - 6 * fxOne ]

        let thresholds =
            quantizeRates
                { onePoint = thresholdOf 0.5
                  twoPoint = thresholdOf 0.4
                  geneRecomb = thresholdOf 0.2
                  mutation = thresholdOf 0.25
                  constReplace = thresholdOf 0.3
                  creep = thresholdOf 0.4
                  inversion = thresholdOf 0.3
                  isTrans = thresholdOf 0.3
                  risTrans = thresholdOf 0.3
                  geneTrans = 0
                  creepSigmaFx = fx 0.1
                  constRangeFx = fx 10.0 }

        let pairings =
            [ for e in 0 .. entries - 1 ->
                let parentA = (e * 7) % popSlots
                let parentB = (e * 11 + 3) % popSlots
                let seed = 900_000L + 4_099L * int64 e

                {| entryId = e
                   parentA = parentA
                   parentB = parentB
                   dest = popSlots + e
                   child = hwBreedOffspring population[parentA] population[parentB] config thresholds (GepRng seed)
                   words = entryWordsOf parentA parentB (popSlots + e) thresholds e false seed |} ]

        let le32 (words: uint32 seq) =
            [| for w in words do
                   yield byte w
                   yield byte (w >>> 8)
                   yield byte (w >>> 16)
                   yield byte (w >>> 24) |]

        let write name (bytes: byte[]) =
            let path = System.IO.Path.Combine(dir, name)
            System.IO.File.WriteAllBytes(path, bytes)
            printfn $"wrote {path} ({bytes.Length} bytes)"

        // The work-item stream exactly as the host marshaller produces it:
        // entry, parent A's record, parent B's record, then padding to the
        // 256 B stride the single-burst fetch needs.
        write
            "workitems.bin"
            (le32
                [ for p in pairings do
                      yield! p.words
                      yield! geneRecordWords population[p.parentA]
                      yield! geneRecordWords population[p.parentB]
                      yield! Array.zeroCreate<uint32> (Cluster.gepWorkItemWords - 3 * Cluster.gepRecordWords) ])

        write "population.bin" (le32 [ for c in population do yield! geneRecordWords c ])

        write
            "cases.bin"
            (le32
                [ for vars, target in cases do
                      for v in vars -> uint32 v
                      yield uint32 target ])

        write "expected_genomes.bin" (le32 [ for p in pairings do yield! geneRecordWords p.child ])

        write
            "expected_fitness.bin"
            (le32
                [ for p in pairings do
                      let f = uint64 (fitnessOf config cases p.child)
                      yield uint32 f
                      yield uint32 (f >>> 32) ])

        let manifest =
            [ $"entries {entries}"
              $"pop_slots {popSlots}"
              $"n_cases {nCases}"
              $"var_count {config.variableCount}"
              $"record_words {Cluster.gepRecordWords}"
              $"work_item_words {Cluster.gepWorkItemWords}"
              $"work_item_payload_words {3 * Cluster.gepRecordWords}"
              $"dest_base {popSlots}"
              $"n_breeders {shape.nBreeders}"
              $"n_lanes {shape.nLanes}"
              "" ]

        let manifestPath = System.IO.Path.Combine(dir, "manifest.txt")
        System.IO.File.WriteAllText(manifestPath, String.concat "\n" manifest)
        printfn $"wrote {manifestPath}"
        0
    | [| "sweep"; spec |] ->
        // "4x8x2@16,4x8x1@16" — breeders x lanes x fillers @ DDR read latency.
        printMixHeader ()

        for point in spec.Split(',') do
            let mix, delay, cases =
                match point.Split('@') with
                | [| m; rest |] ->
                    match rest.Split('/') with
                    | [| d; c |] -> m, int d, int c
                    | [| d |] -> m, int d, 32
                    | _ -> failwith $"cannot parse '{point}'"
                | [| m |] -> m, 0, 32
                | _ -> failwith $"cannot parse '{point}'"

            let mix, sharing =
                if mix.EndsWith "P" then mix.TrimEnd 'P', Pooled else mix, PerLane

            match mix.Split('x') with
            | [| b; l; f |] ->
                let started = System.Diagnostics.Stopwatch.StartNew()
                let r = runClusterMix sharing (int b) (int l) (int f) delay cases 64
                printMixRow r
                eprintfn $"    ({started.Elapsed.TotalSeconds:F0} s of wall clock)"
            | _ -> failwith $"cannot parse mix '{mix}'"

        0
    | [| "emit-divcompare"; dir |] ->
        // The silicon shape twice, differing ONLY in how the divide is shared —
        // so an area probe attributes the difference to that and nothing else.
        System.IO.Directory.CreateDirectory dir |> ignore

        for sharing, name in [ PerLane, "perlane"; Pooled, "pooled" ] do
            let shape = { ClusterAxi.clusterSiliconShape with divide = Some sharing }
            let design = ClusterAxi.gepClusterAxi $"GepClusterAxi{name}" shape
            let path = System.IO.Path.Combine(dir, name + ".v")
            System.IO.File.WriteAllText(path, emitDesign design + "\n")
            printfn $"wrote {path}"

        0
    | [| "problems" |] ->
        // The search's own benchmarks: symbolic regression, then the
        // classification pair that asks whether comparison opcodes earn their
        // place. Both function sets against both boundaries isolates the
        // operators from the problem.
        Problems.symbolicBenchmarks () |> List.iter (Problems.runSymbolic >> ignore)
        let seeds = [ 42L; 43L; 44L; 45L; 46L; 47L; 48L ]
        Problems.reportClassificationSeeds seeds "circle  x^2 + y^2 < 1" Problems.insideCircle
        Problems.reportClassificationSeeds seeds "box     x > 0.5 and y < -0.2" Problems.insideBox
        0
    | [| "srbench" |] -> Srbench.runStarterSet Srbench.defaultRunParams |> ignore; 0
    | [| "srbench-seeds"; name; n |] ->
        // The README's claims are per-configuration and restart-sensitive, so
        // ask the question the way it was answered: a seed distribution at the
        // recorded config, not one run at the default one.
        let seeds = [ for i in 0 .. int n - 1 -> 1000L + 7L * int64 i ]

        let problems =
            Srbench.feynmanStarter |> List.filter (fun p -> p.name.StartsWith name)

        if problems.IsEmpty then
            eprintfn $"no problem matching '{name}'"
            2
        else
            for p in problems do
                Srbench.runSeeds p Srbench.coulombRunParams seeds |> ignore

            0
    | [| "srbench-adf"; name; n; adfCount |] ->
        // The encoding question asked the only way it can be answered: both
        // arms, same seeds, same budget, distributions side by side.
        let seeds = [ for i in 0 .. int n - 1 -> 1000L + 7L * int64 i ]

        let problems =
            Srbench.feynmanStarter |> List.filter (fun p -> p.name.StartsWith name)

        if problems.IsEmpty then
            eprintfn $"no problem matching '{name}'"
            2
        else
            for p in problems do
                Srbench.runEncodingPair p Srbench.coulombRunParams (int adfCount) seeds |> ignore

            0
    | [| "srbench"; gens |] ->
        Srbench.runStarterSet { Srbench.defaultRunParams with generations = int gens } |> ignore
        0
    | [| "golden"; path |] ->
        let report = Golden.verify path
        printfn $"golden vectors: {report.passed} passed, {report.failed} failed"

        for name in report.failures do
            printfn $"  FAIL {name}"

        if report.failed = 0 then 0 else 1
    | [||] ->
        let checks =
            [ "compiler vs tree evaluator (5 configs x 50 x 20, 2 homeotic)", compilerDiff
              "breeding invariants (200 pairings)", breedingInvariants
              "ADF breeding: role legality + compiler (300 pairings)", adfBreedingInvariants
              "engine smoke (x^2+x+1)", engineSmoke
              "ADF engine smoke (x^2+x+1, homeotic)", adfEngineSmoke
              "xoshiro hardware vs GepRng (2 x 64 words)", xoshiroVsRng
              "recip ROM vs fxRecipTable (512 + reset reload)", recipRomVsTable
              "div arm vs fxDivRecip (13 edges + 200 random, II=1)", divArmVsFxDivRecip
              "ALU vs applyOp (2 elaborations x 300, II=1)", aluVsApplyOp
              "karva compiler vs compileGene (31 genes)", karvaCompilerVsCompileGene
              "operator engine vs hwBreedOffspring (25 seeds)", operatorEngineVsHwBreed
              "unit engine vs evaluate (4 indivs x 16 cases)", unitEngineVsEvaluate
              "divide sharing: resident vs pooled, both vs evaluate", unitEngineDivSharing
              "breeder block vs oracle (10 seeds, backpressured)", breederBlockVsOracle
              "record router choreography (2x2, rebind)", recordRouterChoreography
              "cluster elaborations (8 arrangements, emit-gated)", clusterElaborations
              "cluster queue mode vs oracle (8 offspring, ring wrap)", (fun () -> clusterQueueVsOracle 1 false)
              "cluster inline parents, 2 fillers (garbage population)", (fun () -> clusterQueueVsOracle 2 true)
              "cluster auto mode vs the generation-loop mirror", clusterAutoVsMirror
              "cluster op-list emitter vs the mirror (entries + score round)", clusterOpListVsMirror
              "cluster seam: the same run through the AXI-Lite register map",
              (fun () -> let r, t = clusterAxiSeamParts None in r && t)
              // The same run again, with the DDR model answering after a
              // random 0-3 cycle delay and stalling its channels independently.
              // A correct design is indifferent to when memory answers; one
              // that is not passes the always-ready model and fails on a board,
              // which is the failure this suite exists to prevent.
              "cluster seam: indifferent to memory timing",
              (fun () -> [ 1..6 ] |> List.forall (fun seed -> fst (clusterAxiSeamParts (Some seed))))
              "lane fitness on DIV-bearing programs (the silicon tail)", laneDivFitnessVsEvaluate ]

        let mutable ok = true

        for name, check in checks do
            let result = check ()
            printfn $"{name}: {result}"
            ok <- ok && result

        if ok then 0 else 1
    | _ ->
        eprintfn "usage: dotnet run [-- golden <vectors-file>]"
        2
