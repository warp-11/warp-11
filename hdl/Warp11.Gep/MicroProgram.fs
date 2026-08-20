/// The dataflow micro-program a gene compiles to — the form the sequential
/// hardware engine executes, one instruction per cycle.
///
/// There is one instruction per *function* node; terminals never occupy an
/// instruction. An operand is a tagged reference rather than a memory slot the
/// decoder had to preload: Var and Const read the engine's per-case register
/// files directly, Result reads the result RAM. Sourcing terminals instead of
/// staging them is what keeps the loop at one cycle per function node.
///
/// Instructions are emitted deepest level first, so a node's children always
/// have result indices before the node references them, and the root is
/// emitted last.
module Warp11.Gep.MicroProgram

open System.Collections.Generic
open Warp11.Gep.Opcodes
open Warp11.Gep.Karva
open Warp11.Gep.Chromosome

type Src =
    | Var
    | Const
    | Result

/// The packed encoding's tag — Src as its Kotlin enum ordinal.
let srcOrdinal (src: Src) : int =
    match src with
    | Var -> 0
    | Const -> 1
    | Result -> 2

type MicroInstruction =
    { op: int
      aSrc: Src
      aIdx: int
      bSrc: Src
      bIdx: int }

/// Packed instruction word the hardware program memory holds, LSB-first:
/// op[7:0], aSrc[9:8], aIdx[15:10], bSrc[17:16], bIdx[23:18].
let instrWidth = 24

let packInstruction (i: MicroInstruction) : int64 =
    (int64 i.op &&& 0xFFL)
    ||| (int64 (srcOrdinal i.aSrc) <<< 8)
    ||| (int64 i.aIdx <<< 10)
    ||| (int64 (srcOrdinal i.bSrc) <<< 16)
    ||| (int64 i.bIdx <<< 18)

/// outputSrc/outputIdx name the gene's value explicitly rather than assuming
/// it is the last instruction's result: a gene whose root is a terminal emits
/// no instructions, and its value is that terminal.
type MicroProgram =
    { instructions: MicroInstruction list
      outputSrc: Src
      outputIdx: int }

    /// Result RAM depth. One slot per instruction.
    member this.ResultSlots = this.instructions.Length

/// Each function node's child positions: contiguous in the level below at a
/// prefix-sum offset.
let private childPositions (gene: int[]) (levels: KarvaLevels) : Dictionary<int, int[]> =
    let children = Dictionary<int, int[]>()

    for level in 0 .. levels.Depth - 2 do
        let start = levels.starts[level]
        let nextStart = levels.starts[level + 1]
        let mutable cursor = 0

        for j in 0 .. levels.widths[level] - 1 do
            let pos = start + j
            let arity = arityOf gene[pos]

            if arity > 0 then
                children[pos] <- Array.init arity (fun i -> nextStart + cursor + i)
                cursor <- cursor + arity

    children

let private operand (gene: int[]) (pos: int) (resultIndex: Dictionary<int, int>) : Src * int =
    let op = gene[pos]

    if isConstant op then Const, termIndex op
    elif isTerminal op then Var, termIndex op
    else Result, resultIndex[pos]

let compileGene (gene: int[]) : MicroProgram =
    let levels = decode gene
    let children = childPositions gene levels
    let resultIndex = Dictionary<int, int>()
    let instructions = ResizeArray<MicroInstruction>()

    for level in levels.Depth - 1 .. -1 .. 0 do
        let start = levels.starts[level]

        for j in 0 .. levels.widths[level] - 1 do
            let pos = start + j
            let op = gene[pos]

            if not (isTerminal op) then
                let kids = children[pos]
                let aSrc, aIdx = operand gene kids[0] resultIndex

                let bSrc, bIdx =
                    if arityOf op >= 2 then operand gene kids[1] resultIndex else Var, 0

                resultIndex[pos] <- instructions.Count
                instructions.Add { op = op; aSrc = aSrc; aIdx = aIdx; bSrc = bSrc; bIdx = bIdx }

    // The root is position 0; after emission its operand resolves to the last
    // instruction's result, or to the terminal itself if the gene is one node.
    let outputSrc, outputIdx = operand gene 0 resultIndex

    { instructions = List.ofSeq instructions
      outputSrc = outputSrc
      outputIdx = outputIdx }

/// Shifts Result indices into the chromosome-wide result space and Const
/// indices past prior genes' banks.
let private shift (src: Src) (idx: int) (resultBase: int) (constBase: int) : Src * int =
    match src with
    | Result -> Result, idx + resultBase
    | Const -> Const, idx + constBase
    | Var -> Var, idx

let private shifted (resultBase: int) (constBase: int) (i: MicroInstruction) : MicroInstruction =
    let aSrc, aIdx = shift i.aSrc i.aIdx resultBase constBase
    let bSrc, bIdx = shift i.bSrc i.bIdx resultBase constBase
    { i with aSrc = aSrc; aIdx = aIdx; bSrc = bSrc; bIdx = bIdx }

/// A homeotic operand. Its Var(i) is a *call to ADF i*, so it resolves to
/// whatever operand that conventional gene's output already is — no instruction
/// and no result slot of its own. Const and Result shift like any other gene's.
let private remapHomeotic (adfOutput: (Src * int)[]) (resultBase: int) (constBase: int) (src: Src) (idx: int) : Src * int =
    match src with
    | Var ->
        if idx >= adfOutput.Length then
            failwith $"homeotic gene calls ADF {idx}, chromosome has {adfOutput.Length}"

        adfOutput[idx]
    | Const -> Const, idx + constBase
    | Result -> Result, idx + resultBase

/// Compiles a whole chromosome into one flat program — how genes are folded
/// through a single engine. Each gene's program is concatenated with its
/// result and constant indices shifted into a chromosome-wide space, and the
/// genes are then combined.
///
/// Under a link operator the combination is an accumulator: a link is an
/// ordinary binary-op instruction, so the engine runs the folded program with
/// no changes, and with one gene the result equals compileGene. Under a
/// homeotic linkage the last gene compiles the same way as any other, except
/// its ADF calls are rewritten to the conventional genes' output operands —
/// which turns an evolved combination into ordinary instructions over result
/// slots. Either way the output is one micro-program in the existing
/// instruction format: ADFs are a compiler change, not a hardware one.
let compileChromosome (config: GepConfig) (c: Chromosome) : MicroProgram =
    let instructions = ResizeArray<MicroInstruction>()
    let adfOutput = ResizeArray<Src * int>()
    let mutable acc = None
    let mutable constBase = 0

    for g in 0 .. config.AdfCount - 1 do
        let geneProgram = compileGene (gene config g c)
        let baseIdx = instructions.Count

        for i in geneProgram.instructions do
            instructions.Add(shifted baseIdx constBase i)

        let geneOut = shift geneProgram.outputSrc geneProgram.outputIdx baseIdx constBase
        adfOutput.Add geneOut

        acc <-
            match config.linkage, acc with
            | Homeotic, _ -> None
            | LinkOp _, None -> Some geneOut
            | LinkOp op, Some (accSrc, accIdx) ->
                let outSrc, outIdx = geneOut

                instructions.Add { op = op; aSrc = accSrc; aIdx = accIdx; bSrc = outSrc; bIdx = outIdx }
                Some(Result, instructions.Count - 1)

        constBase <- constBase + config.constantCount

    let outputSrc, outputIdx =
        match config.linkage with
        | LinkOp _ -> Option.get acc
        | Homeotic ->
            let homProgram = compileGene (gene config config.AdfCount c)
            let baseIdx = instructions.Count
            let remap = remapHomeotic (adfOutput.ToArray()) baseIdx constBase

            for i in homProgram.instructions do
                let aSrc, aIdx = remap i.aSrc i.aIdx
                let bSrc, bIdx = remap i.bSrc i.bIdx
                instructions.Add { i with aSrc = aSrc; aIdx = aIdx; bSrc = bSrc; bIdx = bIdx }

            remap homProgram.outputSrc homProgram.outputIdx

    { instructions = List.ofSeq instructions
      outputSrc = outputSrc
      outputIdx = outputIdx }

let private read (src: Src) (idx: int) (vars: int[]) (constants: int[]) (results: int[]) : int =
    match src with
    | Var -> vars[idx]
    | Const -> constants[idx]
    | Result -> results[idx]

/// Runs a compiled program the way the hardware will: terminals sourced from
/// the var/const register files, intermediates from the result RAM, one op per
/// step. Exists to be diff-tested against evaluate — if the two ever disagree
/// the compiler is wrong, and the hardware would inherit the bug.
let runProgram (program: MicroProgram) (vars: int[]) (constants: int[]) : int =
    let results = Array.zeroCreate program.ResultSlots
    let mutable i = 0

    for ins in program.instructions do
        let a = read ins.aSrc ins.aIdx vars constants results
        let b = read ins.bSrc ins.bIdx vars constants results
        results[i] <- applyOp ins.op a b
        i <- i + 1

    read program.outputSrc program.outputIdx vars constants results
