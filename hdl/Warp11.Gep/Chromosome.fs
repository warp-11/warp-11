/// Gene geometry, configuration and the chromosome itself.
///
/// The tail is sized so that even a head of nothing but maximum arity
/// functions has enough terminals to close every branch, which is what makes
/// every syntactically possible gene a valid expression.
module Warp11.Gep.Chromosome

open Warp11.Gep.Fixed
open Warp11.Gep.Opcodes
open Warp11.Gep.Karva

type GeneLayout =
    { headLength: int
      maxArity: int
      tailLength: int
      length: int }

    member this.IsHead(offsetInGene: int) = offsetInGene < this.headLength

let geneLayout (headLength: int) (maxArity: int) : GeneLayout =
    if headLength < 1 then failwith $"head length must be positive: {headLength}"
    if maxArity < 1 then failwith $"max arity must be positive: {maxArity}"

    { headLength = headLength
      maxArity = maxArity
      tailLength = headLength * (maxArity - 1) + 1
      length = headLength + headLength * (maxArity - 1) + 1 }

/// How a chromosome's genes combine into one value.
type Linkage =
    /// Every gene folded with one fixed operator — plain multigenic GEP.
    | LinkOp of int
    /// The last gene is homeotic (Ferreira's cellular system): itself an
    /// ordinary Karva expression, but its terminals are calls to the
    /// conventional genes ahead of it, which are then automatically defined
    /// functions. It replaces the fixed linking operator with an evolved,
    /// reusable combination — so a shared factor over a sum of sub-results can
    /// be expressed once instead of rediscovered in every gene.
    | Homeotic

/// Constants are a per-gene bank indexed directly by terminal opcode rather
/// than Ferreira's Dc domain. Per-gene rather than per-chromosome so that gene
/// recombination and gene transposition carry a gene's constants with it.
///
/// A homeotic chromosome is *this same record*, not a parallel type: same flat
/// symbol array, same uniform gene geometry, same constant banks. Only the
/// terminal alphabet of the last gene and the way genes combine differ, which
/// is why every genetic operator applies to ADF chromosomes unchanged.
type GepConfig =
    { layout: GeneLayout
      variableCount: int
      geneCount: int
      constantCount: int
      functionSet: int[]
      linkage: Linkage
      chromosomeLength: int
      terminalSet: int[]
      /// The homeotic gene's alphabet: one reference per conventional gene
      /// (encoded as `variable g` — an ADF call reads its gene's output the way
      /// a conventional terminal reads a variable) followed by its own
      /// constants. Empty unless linkage is Homeotic.
      homeoticTerminals: int[] }

    member this.GeneStart(gene: int) = gene * this.layout.length
    member this.ConstantStart(gene: int) = gene * this.constantCount

    /// Conventional genes — every gene in plain mode, all but the last under a
    /// homeotic linkage.
    member this.AdfCount =
        match this.linkage with
        | LinkOp _ -> this.geneCount
        | Homeotic -> this.geneCount - 1

    member this.IsHomeoticGene(gene: int) =
        match this.linkage with
        | LinkOp _ -> false
        | Homeotic -> gene = this.geneCount - 1

    /// The alphabet a symbol in this gene may be drawn from. Roles are a
    /// property of the position, so mutation and generation stay legal by
    /// construction rather than by repair.
    member this.TerminalsOf(gene: int) =
        if this.IsHomeoticGene gene then this.homeoticTerminals else this.terminalSet

    member this.GeneOf(position: int) = position / this.layout.length

let private conventionalTerminals (variableCount: int) (constantCount: int) =
    Array.init (variableCount + constantCount) (fun i ->
        if i < variableCount then variable i else constant (i - variableCount))

let private checkGeometry (layout: GeneLayout) (variableCount: int) (constantCount: int) (functionSet: int[]) =
    if Array.isEmpty functionSet then failwith "empty function set"
    if variableCount + constantCount < 1 then failwith "no terminals available"
    if variableCount > maxVariables then failwith $"too many variables: {variableCount}"
    if constantCount > maxConstants then failwith $"too many constants: {constantCount}"
    let widest = functionSet |> Array.map arityOf |> Array.max

    if widest > layout.maxArity then
        failwith $"function set needs arity {widest} but layout allows {layout.maxArity}"

let gepConfig (layout: GeneLayout) (variableCount: int) (geneCount: int) (constantCount: int) (functionSet: int[]) (linkOp: int) : GepConfig =
    if geneCount < 1 then failwith $"gene count must be positive: {geneCount}"
    checkGeometry layout variableCount constantCount functionSet

    { layout = layout
      variableCount = variableCount
      geneCount = geneCount
      constantCount = constantCount
      functionSet = functionSet
      linkage = LinkOp linkOp
      chromosomeLength = layout.length * geneCount
      terminalSet = conventionalTerminals variableCount constantCount
      homeoticTerminals = [||] }

/// adfCount conventional genes plus one homeotic gene, all of one geometry.
/// Uniform geometry is what keeps positional recombination role-preserving: a
/// position is head/tail and conventional/homeotic identically in both parents,
/// so crossover can never reinterpret a symbol under a different alphabet.
let adfConfig (layout: GeneLayout) (variableCount: int) (adfCount: int) (constantCount: int) (functionSet: int[]) : GepConfig =
    if adfCount < 1 then failwith $"ADF count must be positive: {adfCount}"
    if adfCount > maxVariables then failwith $"too many ADFs: {adfCount}"
    checkGeometry layout variableCount constantCount functionSet

    { layout = layout
      variableCount = variableCount
      geneCount = adfCount + 1
      constantCount = constantCount
      functionSet = functionSet
      linkage = Homeotic
      chromosomeLength = layout.length * (adfCount + 1)
      terminalSet = conventionalTerminals variableCount constantCount
      homeoticTerminals = conventionalTerminals adfCount constantCount }

/// A candidate solution: geneCount fixed-length genes concatenated, plus one
/// constant bank per gene. Flat arrays because that is the shape the hardware
/// reads out of BRAM.
type Chromosome =
    { symbols: int[]
      constants: int[] }

    member this.Copy() =
        { symbols = Array.copy this.symbols
          constants = Array.copy this.constants }

let gene (config: GepConfig) (index: int) (c: Chromosome) : int[] =
    Array.sub c.symbols (config.GeneStart index) config.layout.length

let constantsOf (config: GepConfig) (index: int) (c: Chromosome) : int[] =
    Array.sub c.constants (config.ConstantStart index) config.constantCount

/// Magnitude bound for generated constants — a mutation policy, not genome
/// geometry.
let defaultConstantRange = 10.0

let linkValues (op: int) (a: int) (b: int) : int =
    if op = ADD then fxAdd a b
    elif op = SUB then fxSub a b
    elif op = MUL then fxMul a b
    elif op = MIN then (if a <= b then a else b)
    elif op = MAX then (if a >= b then a else b)
    else failwith $"unsupported linking function: 0x%02x{op}"

/// The conventional genes' values, in gene order — the ADF results a homeotic
/// gene reads as its variables.
let adfValues (config: GepConfig) (vars: int[]) (c: Chromosome) : int[] =
    Array.init config.AdfCount (fun g -> evaluate (gene config g c) vars (constantsOf config g c))

/// Evaluates every gene and combines the results. Under a link operator that
/// is a fold — in hardware, geneCount evaluators in parallel feeding a reduce
/// tree; here sequential, which agrees because the linking ops are
/// associative. Under a homeotic linkage the conventional genes are evaluated
/// first and the homeotic gene is then evaluated with those results standing
/// in as its variables.
let evaluateChromosome (config: GepConfig) (vars: int[]) (c: Chromosome) : int =
    match config.linkage with
    | LinkOp op ->
        let mutable acc = 0

        for g in 0 .. config.geneCount - 1 do
            let value = evaluate (gene config g c) vars (constantsOf config g c)
            acc <- if g = 0 then value else linkValues op acc value

        acc
    | Homeotic ->
        let hom = config.AdfCount
        evaluate (gene config hom c) (adfValues config vars c) (constantsOf config hom c)

let formatChromosome (config: GepConfig) (varNames: string list) (c: Chromosome) : string =
    let formatGene g names =
        format (gene config g c) (constantsOf config g c) names

    match config.linkage with
    | LinkOp op ->
        [ for g in 0 .. config.geneCount - 1 -> formatGene g varNames ]
        |> String.concat $" {opName op} "
    | Homeotic ->
        // The ADFs are substituted into the homeotic expression rather than
        // named, so the printed formula is the whole recovered function.
        let adfNames = [ for g in 0 .. config.AdfCount - 1 -> $"({formatGene g varNames})" ]
        formatGene config.AdfCount adfNames
