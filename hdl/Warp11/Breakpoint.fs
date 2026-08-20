/// Breakpoint expressions: text in, a predicate over a running design out.
///
/// The IR is already the expression language — `count == 0x40 && !valid` is
/// `And(Eq(…), Not(…))` — so this parses to `Warp11.Ir.Expr` and hands the
/// result to `Sim.CompilePredicate`, which compiles it exactly like one of the
/// design's own assignments. Testing a breakpoint therefore costs one thunk
/// call per cycle, not a walk.
///
/// It lives in the library rather than in the debugger because "run until this
/// holds" is worth having headless — and because that is what lets it be
/// checked without clicking anything.
///
/// The grammar, loosest binding first:
///
///     ||  &&  == != < <= > >=  |  ^  &  << >>  + -  *  unary ! ~ -
///     name            a signal, by its flattened name
///     name[i]         bit i of a signal, or word i of a memory
///     name[hi:lo]     a slice of a signal
///     signed(e)       read e's bits as two's complement — this is what turns
///                     `<` into a signed compare and `>>` into an arithmetic
///                     shift, and it is the only place signedness enters, per
///                     the IR's own doctrine that operations carry it
///     0x1f  0b1010  42  -1
module Warp11.Breakpoint

open System.Numerics

exception private ParseError of string

let private fail message = raise (ParseError message)

// ---------------------------------------------------------------------------
// Tokens

type private Token =
    | Number of BigInteger
    | Name of string
    | Symbol of string
    | End

/// Two-character symbols first, so `<=` is never read as `<` then `=`.
let private symbols =
    [ "&&"; "||"; "=="; "!="; "<="; ">="; "<<"; ">>"
      "("; ")"; "["; "]"; ":"; "!"; "~"; "-"; "+"; "*"; "&"; "|"; "^"; "<"; ">" ]

let private readNumber (text: string) start =
    let digits isDigit from =
        let rec scan i = if i < text.Length && isDigit text[i] then scan (i + 1) else i
        scan from

    let isHex c = System.Char.IsAsciiHexDigit c
    let isBin c = c = '0' || c = '1'
    let isDec c = System.Char.IsAsciiDigit c

    let parse (radix: int) (body: string) =
        if body = "" then fail "a number needs digits after its prefix"

        body
        |> Seq.fold
            (fun acc c ->
                let d = System.Convert.ToInt32(string c, radix)
                acc * BigInteger(radix) + BigInteger(d))
            BigInteger.Zero

    if start + 1 < text.Length && text[start] = '0' && (text[start + 1] = 'x' || text[start + 1] = 'X') then
        let stop = digits isHex (start + 2)
        parse 16 (text.Substring(start + 2, stop - start - 2)), stop
    elif start + 1 < text.Length && text[start] = '0' && (text[start + 1] = 'b' || text[start + 1] = 'B') then
        let stop = digits isBin (start + 2)
        parse 2 (text.Substring(start + 2, stop - start - 2)), stop
    else
        let stop = digits isDec start
        parse 10 (text.Substring(start, stop - start)), stop

let private tokenize (text: string) =
    let isNameStart c = System.Char.IsLetter c || c = '_'
    let isNameChar c = System.Char.IsLetterOrDigit c || c = '_'

    let rec go i acc =
        if i >= text.Length then
            List.rev (End :: acc)
        elif System.Char.IsWhiteSpace text[i] then
            go (i + 1) acc
        elif System.Char.IsAsciiDigit text[i] then
            let value, stop = readNumber text i
            go stop (Number value :: acc)
        elif isNameStart text[i] then
            let rec scan j = if j < text.Length && isNameChar text[j] then scan (j + 1) else j
            let stop = scan i
            go stop (Name(text.Substring(i, stop - i)) :: acc)
        else
            match symbols |> List.tryFind (fun s -> i + s.Length <= text.Length && text.Substring(i, s.Length) = s) with
            | Some s -> go (i + s.Length) (Symbol s :: acc)
            | None -> fail $"unexpected character '{text[i]}'"

    go 0 [] |> Array.ofList

// ---------------------------------------------------------------------------
// Terms
//
// A parsed subexpression, plus the two things the IR itself does not carry:
// whether its bits are to be read as signed, and — for a bare number — the
// value it still holds while it waits to learn its width.

type private Term =
    { expr: Expr
      signed: bool
      /// Set only for a literal that has not been given a width yet. It adopts
      /// the width of whatever it is combined with, which is the DSL's own
      /// "a neighbour supplies the type" rule, at the keyboard.
      pending: BigInteger option }

let private ofExpr signed expr =
    { expr = expr
      signed = signed
      pending = None }

let private materialize (value: BigInteger) w =
    // `Lit` carries a uint64, so a literal tops out at 64 bits however wide the
    // signal beside it is. Say that, rather than claiming a 128-bit signal has
    // no room for it.
    let span = BigInteger.One <<< min w 64
    let fitted = if value.Sign < 0 then value + span else value

    if w > 64 && value.Sign < 0 then
        fail $"a negative literal cannot be written wider than 64 bits (beside a %d{w}-bit value)"

    if fitted.Sign < 0 || fitted >= span then
        if w > 64 then
            fail $"a literal is limited to 64 bits, and %A{value} needs more"
        else
            fail $"the literal %A{value} does not fit %d{w} bits"

    Lit(uint64 fitted, UInt w)

let private widthOfTerm (t: Term) =
    match t.pending with
    | Some _ -> 64
    | None -> width t.expr

/// The two operands of a binary operator, with a bare literal taking the other
/// side's width. Two literals fall back to 64 bits.
let private operands (a: Term) (b: Term) =
    match a.pending, b.pending with
    | Some va, Some vb -> materialize va 64, materialize vb 64
    | Some va, None -> materialize va (width b.expr), b.expr
    | None, Some vb -> a.expr, materialize vb (width a.expr)
    | None, None -> a.expr, b.expr

/// Anything, as a one-bit truth value.
let private truth (e: Expr) =
    if width e = 1 then e else Not(Eq(e, Lit(0UL, UInt(width e))))

// ---------------------------------------------------------------------------
// The parser. Position is threaded rather than held in a cursor, so every
// production is an ordinary function of where it starts.

type private Scope =
    { widthOf: string -> int option
      memOf: string -> (int * int) option }

let rec private parseOr scope (ts: Token[]) pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol "||" ->
            let right, next = parseAnd scope ts (pos + 1)
            let a, b = operands left right
            more (ofExpr false (Or(truth a, truth b))) next
        | _ -> left, pos

    let first, next = parseAnd scope ts pos
    more first next

and private parseAnd scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol "&&" ->
            let right, next = parseCompare scope ts (pos + 1)
            let a, b = operands left right
            more (ofExpr false (And(truth a, truth b))) next
        | _ -> left, pos

    let first, next = parseCompare scope ts pos
    more first next

and private parseCompare scope ts pos =
    let left, next = parseBitOr scope ts pos

    match ts[next] with
    | Symbol (("==" | "!=" | "<" | "<=" | ">" | ">=") as op) ->
        let right, after = parseBitOr scope ts (next + 1)
        let a, b = operands left right
        let signed = left.signed || right.signed

        if signed && width a <> width b then
            fail
                $"a signed compare needs equal widths, got %d{width a} and %d{width b} — slice or extend one side first"

        let less x y = if signed then Lt(AsSInt x, AsSInt y) else Lt(x, y)

        let result =
            match op with
            | "==" -> Eq(a, b)
            | "!=" -> Not(Eq(a, b))
            | "<" -> less a b
            | ">" -> less b a
            | "<=" -> Not(less b a)
            | _ -> Not(less a b)

        ofExpr false result, after
    | _ -> left, next

and private parseBitOr scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol "|" ->
            let right, next = parseBitXor scope ts (pos + 1)
            let a, b = operands left right
            more { ofExpr (left.signed || right.signed) (Or(a, b)) with pending = None } next
        | _ -> left, pos

    let first, next = parseBitXor scope ts pos
    more first next

and private parseBitXor scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol "^" ->
            let right, next = parseBitAnd scope ts (pos + 1)
            let a, b = operands left right
            more (ofExpr (left.signed || right.signed) (Xor(a, b))) next
        | _ -> left, pos

    let first, next = parseBitAnd scope ts pos
    more first next

and private parseBitAnd scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol "&" ->
            let right, next = parseShift scope ts (pos + 1)
            let a, b = operands left right
            more (ofExpr (left.signed || right.signed) (And(a, b))) next
        | _ -> left, pos

    let first, next = parseShift scope ts pos
    more first next

and private parseShift scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol (("<<" | ">>") as op) ->
            // The IR shifts by a constant — that is what a shift is in hardware
            // unless a barrel is built for it.
            let amount, next =
                match ts[pos + 1] with
                | Number n when n.Sign >= 0 && n <= BigInteger 4096 -> int n, pos + 2
                | _ -> fail $"'{op}' needs a constant shift amount"

            let source = left.expr
            let w = width source

            let shifted =
                if op = "<<" then
                    if amount = 0 then source else Concat(source, Lit(0UL, UInt amount))
                elif amount = 0 then
                    source
                elif amount >= w then
                    fail $"'>>' by %d{amount} on %d{w} bits — the shift must be under the width"
                elif left.signed then
                    Pad(Shr(AsSInt source, amount), w)
                else
                    Slice(source, w - 1, amount)

            more (ofExpr left.signed shifted) next
        | _ -> left, pos

    let first, next = parseSum scope ts pos
    more first next

and private parseSum scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol (("+" | "-") as op) ->
            let right, next = parseProduct scope ts (pos + 1)
            let a, b = operands left right
            let signed = left.signed || right.signed
            more (ofExpr signed (if op = "+" then Add(a, b) else Sub(a, b))) next
        | _ -> left, pos

    let first, next = parseProduct scope ts pos
    more first next

and private parseProduct scope ts pos =
    let rec more (left: Term) pos =
        match ts[pos] with
        | Symbol "*" ->
            let right, next = parseUnary scope ts (pos + 1)
            let a, b = operands left right
            let signed = left.signed || right.signed
            // Constructed rather than built through `mulS`, whose declared-signal
            // rule exists so that emission can replicate a named sign bit. A
            // predicate is never emitted; it only ever runs in the Sim.
            more (ofExpr signed (if signed then Mul(AsSInt a, AsSInt b) else Mul(a, b))) next
        | _ -> left, pos

    let first, next = parseUnary scope ts pos
    more first next

and private parseUnary scope ts pos =
    match ts[pos] with
    | Symbol "!" ->
        let inner, next = parseUnary scope ts (pos + 1)
        let e = inner.expr
        ofExpr false (Eq(e, Lit(0UL, UInt(width e)))), next
    | Symbol "~" ->
        let inner, next = parseUnary scope ts (pos + 1)
        ofExpr inner.signed (Not(inner.expr)), next
    | Symbol "-" ->
        let inner, next = parseUnary scope ts (pos + 1)

        match inner.pending with
        | Some v -> { inner with pending = Some(-v) }, next
        | None ->
            let w = width inner.expr
            ofExpr true (Sub(Lit(0UL, UInt w), inner.expr)), next
    | _ -> parsePrimary scope ts pos

and private parsePrimary scope ts pos =
    match ts[pos] with
    | Number v ->
        { expr = Lit(0UL, UInt 1)
          signed = false
          pending = Some v },
        pos + 1
    | Symbol "(" ->
        let inner, next = parseOr scope ts (pos + 1)
        expect ts next ")"
        inner, next + 1
    | Name "signed" when ts[pos + 1] = Symbol "(" ->
        let inner, next = parseOr scope ts (pos + 2)
        expect ts next ")"

        let e =
            match inner.pending with
            | Some v -> materialize v 64
            | None -> inner.expr

        ofExpr true e, next + 1
    | Name n -> parseName scope ts pos n
    | Symbol s -> fail $"'{s}' cannot start an expression"
    | End -> fail "the expression ends early"

and private parseName scope ts pos name =
    match scope.memOf name, scope.widthOf name with
    | Some (addrWidth, wordWidth), _ ->
        if ts[pos + 1] <> Symbol "[" then
            fail $"'{name}' is a memory — index it, as {name}[0]"

        let index, next = parseOr scope ts (pos + 2)
        expect ts next "]"

        let addr =
            match index.pending with
            | Some v -> materialize v addrWidth
            | None -> index.expr

        ofExpr false (MemRead(name, addr, wordWidth)), next + 1
    | None, Some w ->
        let signal = Ref(name, UInt w)

        if ts[pos + 1] = Symbol "[" then
            let hi, next = constantIndex ts (pos + 2)

            let lo, after =
                if ts[next] = Symbol ":" then constantIndex ts (next + 1) else hi, next

            expect ts after "]"

            if hi >= w || lo > hi then
                fail $"[%d{hi}:%d{lo}] is not inside '{name}', which is %d{w} bits"

            ofExpr false (Slice(signal, hi, lo)), after + 1
        else
            ofExpr false signal, pos + 1
    | None, None -> fail $"no signal or memory named '{name}'"

and private constantIndex (ts: Token[]) pos =
    match ts[pos] with
    | Number n when n.Sign >= 0 && n < BigInteger 1_000_000 -> int n, pos + 1
    | _ -> fail "a bit index must be a constant"

and private expect (ts: Token[]) pos symbol =
    if ts[pos] <> Symbol symbol then
        fail $"expected '{symbol}'"

// ---------------------------------------------------------------------------
// The surface

/// Parse against an explicit scope — the form the living checks use.
let parseWith widthOf memOf (text: string) : Result<Expr, string> =
    try
        if System.String.IsNullOrWhiteSpace text then
            Error "the expression is empty"
        else
            let ts = tokenize text
            let term, next = parseOr { widthOf = widthOf; memOf = memOf } ts 0

            if ts[next] <> End then
                Error $"unexpected trailing input at token %d{next + 1}"
            else
                match term.pending with
                | Some v -> Ok(materialize v 64)
                | None -> Ok term.expr
    with
    | ParseError message -> Error message
    | :? System.IndexOutOfRangeException -> Error "the expression ends early"

/// Parse against a design's own signals and memories.
let parse (sim: Sim) text =
    parseWith (fun n -> sim.TryWidth n) (fun n -> sim.TryMemShape n) text

type Breakpoint =
    { text: string
      expr: Expr
      /// True when the design's current state satisfies the expression.
      isHit: unit -> bool }

let compile (sim: Sim) (text: string) : Result<Breakpoint, string> =
    parse sim text
    |> Result.map (fun e ->
        { text = text
          expr = e
          isHit = sim.CompilePredicate e })

/// Tick until a predicate holds or `maxCycles` have passed, whichever comes
/// first. The predicate is tested *after* each tick, so a run always advances —
/// otherwise a breakpoint on the state you are already sitting in could never
/// be stepped past. Returns the cycles run and whether it fired.
let runUntil (sim: Sim) (isHit: unit -> bool) maxCycles =
    let rec go cycles =
        if cycles >= maxCycles then
            cycles, false
        else
            sim.Tick()

            if isHit () then cycles + 1, true else go (cycles + 1)

    go 0
