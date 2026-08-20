/// Karva (K-expression) decoding and evaluation.
///
/// Karva is breadth-first, so a node's children are *not* adjacent to it and a
/// reverse-order stack machine pairs the wrong operands. Evaluation instead
/// runs bottom-up level by level, with each level's functions consuming the
/// level below left to right — a FIFO, not a stack.
///
/// This mirrors the intended hardware exactly: decode is the per-individual
/// configuration pass, evaluate is the per-fitness-case pipeline.
module Warp11.Gep.Karva

open Warp11.Gep.Fixed
open Warp11.Gep.Opcodes

/// Level boundaries of the coding region, outermost (root) first.
type KarvaLevels =
    { starts: int[]
      widths: int[] }

    member this.Depth = this.starts.Length

    /// Symbols reached from the root; the rest of the gene is non-coding.
    member this.CodingLength =
        if this.starts.Length = 0 then 0
        else Array.last this.starts + Array.last this.widths

/// Walks levels from the root until one contributes no children. A valid gene
/// (head symbols anything, tail symbols terminals only) always terminates
/// before running off the end; the bounds check catches hand-built genes that
/// don't.
let decode (gene: int[]) : KarvaLevels =
    if gene.Length = 0 then failwith "empty gene"
    let starts = ResizeArray<int>()
    let widths = ResizeArray<int>()
    let mutable start = 0
    let mutable width = 1

    while width > 0 do
        if start + width > gene.Length then
            failwith $"malformed gene: level at {start} needs {width} symbols, gene has {gene.Length}"

        starts.Add start
        widths.Add width
        let mutable children = 0

        for i in start .. start + width - 1 do
            children <- children + arityOf gene[i]

        start <- start + width
        width <- children

    { starts = starts.ToArray(); widths = widths.ToArray() }

let private applyCompare (op: int) (a: int) (b: int) : int =
    if op = MIN then (if a <= b then a else b)
    elif op = MAX then (if a >= b then a else b)
    elif op = GT then (if a > b then fxOne else 0)
    elif op = LT then (if a < b then fxOne else 0)
    else a

/// The single source of ALU truth: the tree evaluator, the micro-program
/// interpreter, and the HDL ALU all realise exactly this. `b` is ignored for
/// unary ops. An opcode outside the function set passes `a` through, which is
/// what a terminal node needs.
let applyOp (op: int) (a: int) (b: int) : int =
    if op = NEG then fxNeg a
    elif op = ABS then fxAbs a
    elif op = ADD then fxAdd a b
    elif op = SUB then fxSub a b
    elif op = MUL then fxMul a b
    elif op = DIV then fxDivActive a b
    else applyCompare op a b

let private terminalValue (op: int) (vars: int[]) (constants: int[]) : int =
    if isConstant op then constants[termIndex op] else vars[termIndex op]

let evaluateLevels (gene: int[]) (levels: KarvaLevels) (vars: int[]) (constants: int[]) : int =
    let mutable below = Array.empty<int>

    for level in levels.Depth - 1 .. -1 .. 0 do
        let start = levels.starts[level]
        let width = levels.widths[level]
        let out = Array.zeroCreate width
        let mutable cursor = 0

        for j in 0 .. width - 1 do
            let op = gene[start + j]

            out[j] <-
                match arityOf op with
                | 0 -> terminalValue op vars constants
                | 1 ->
                    let a = below[cursor]
                    cursor <- cursor + 1
                    applyOp op a 0
                | 2 ->
                    let a = below[cursor]
                    let b = below[cursor + 1]
                    cursor <- cursor + 2
                    applyOp op a b
                | _ -> failwith $"no ternary functions defined: 0x%02x{op}"

        below <- out

    below[0]

let evaluate (gene: int[]) (vars: int[]) (constants: int[]) : int =
    evaluateLevels gene (decode gene) vars constants

let defaultVarNames : string list = List.init maxVariables (fun i -> $"x{i}")

let private render (op: int) (operands: string list) (constants: int[]) (varNames: string list) : string =
    match arityOf op with
    | 0 ->
        if isConstant op then string (fxToDouble constants[termIndex op])
        else varNames[termIndex op]
    | 1 -> $"{opName op}({operands[0]})"
    | 2 ->
        if op = MIN || op = MAX then $"{opName op}({operands[0]}, {operands[1]})"
        else $"({operands[0]} {opName op} {operands[1]})"
    | _ -> failwith $"no ternary functions defined: 0x%02x{op}"

/// Renders the coding region as an infix expression. Interpretability is the
/// main reason to prefer evolved formulas over evolved weights, so this is a
/// first-class output, not a debug aid.
let formatLevels (gene: int[]) (levels: KarvaLevels) (constants: int[]) (varNames: string list) : string =
    let mutable below = Array.empty<string>

    for level in levels.Depth - 1 .. -1 .. 0 do
        let start = levels.starts[level]
        let width = levels.widths[level]
        let out = Array.create width ""
        let mutable cursor = 0

        for j in 0 .. width - 1 do
            let op = gene[start + j]
            let arity = arityOf op
            out[j] <- render op (List.init arity (fun i -> below[cursor + i])) constants varNames
            cursor <- cursor + arity

        below <- out

    below[0]

let format (gene: int[]) (constants: int[]) (varNames: string list) : string =
    formatLevels gene (decode gene) constants varNames
