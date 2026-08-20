/// Symbol encoding. Arity lives in the top two bits so that the decoder reads
/// it as a bit-slice instead of a table lookup, and the head/tail invariant
/// ("tail holds terminals only") collapses to a two-bit compare.
///
///   [7:6] arity class   00 terminal / 01 unary / 10 binary / 11 ternary
///   [5:0] selector      terminal: [5] 0=variable 1=constant, [4:0] index
///                       function: which function within the arity class
///
/// The class value *is* the arity, so arityOf needs no mapping.
module Warp11.Gep.Opcodes

let arityShift = 6

let classTerminal = 0
let classUnary = 1
let classBinary = 2
let classTernary = 3

/// Bit 5 of a terminal selects the constant bank over the variable bank.
let termConstBit = 0x20
let termIndexMask = 0x1F

/// Terminals and constants: 32 of each per gene.
let maxVariables = 32
let maxConstants = 32

let arityOf (op: int) : int = (op >>> arityShift) &&& 0x3

let isTerminal (op: int) : bool = arityOf op = classTerminal

let isConstant (op: int) : bool = isTerminal op && (op &&& termConstBit) <> 0

let termIndex (op: int) : int = op &&& termIndexMask

let variable (index: int) : int =
    if index < 0 || index >= maxVariables then
        failwith $"variable index out of range: {index}"

    index

let constant (index: int) : int =
    if index < 0 || index >= maxConstants then
        failwith $"constant index out of range: {index}"

    termConstBit ||| index

let private unary = classUnary <<< arityShift
let private binary = classBinary <<< arityShift

let NEG = unary ||| 0
let ABS = unary ||| 1

let ADD = binary ||| 0
let SUB = binary ||| 1
let MUL = binary ||| 2
let MIN = binary ||| 3
let MAX = binary ||| 4

/// Comparisons yield 1.0 or 0.0 rather than a distinct boolean type, which
/// keeps the evaluator single-typed: AND is MUL, OR is MAX, NOT is 1 - x, and
/// IF(c,t,f) is c*t + (1-c)*f — no ternary opcode needed.
let GT = binary ||| 5
let LT = binary ||| 6
let DIV = binary ||| 7

/// Every function symbol the base set defines. Division lives in its own
/// opt-in set rather than widening every existing run.
let functionSet = [| NEG; ABS; ADD; SUB; MUL; MIN; MAX; GT; LT |]

/// Arithmetic only — the subset a smooth regression target needs.
let arithmeticSet = [| ADD; SUB; MUL |]

/// Arithmetic plus protected division.
let rationalSet = [| ADD; SUB; MUL; DIV |]

/// Arithmetic plus thresholds, for rule-shaped targets.
let comparisonSet = [| ADD; SUB; MUL; GT; LT |]

let opName (op: int) : string =
    if op = NEG then "neg"
    elif op = ABS then "abs"
    elif op = ADD then "+"
    elif op = SUB then "-"
    elif op = MUL then "*"
    elif op = DIV then "/"
    elif op = MIN then "min"
    elif op = MAX then "max"
    elif op = GT then ">"
    elif op = LT then "<"
    else $"op:0x%02x{op}"
