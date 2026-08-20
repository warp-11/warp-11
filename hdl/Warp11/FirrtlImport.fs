/// Low-FIRRTL text back into the IR.
///
/// The export exists to prove our IR is FIRRTL-expressible. This exists to prove
/// the other direction — that the IR is not a private dialect that only happens
/// to write out — and, further out, that the Sim can run FIRRTL nobody here
/// wrote. The two together are what make "we did not invent an IR" a claim about
/// the IR rather than about the emitter.
///
/// **Scope: low FIRRTL.** Bundles, vectors, `when` and `invalid` belong to the
/// high dialect and would need a lowering pass, which is a different project;
/// they are refused by name rather than half-supported. That is the same
/// discipline the export applies to a preloaded ROM.
///
/// **Where import is not export backwards.** FIRRTL's `add` is *wider* than
/// ours: `add(UInt<8>, UInt<8>)` is `UInt<9>` where our `Add` is `max(wa, wb)`.
/// Export bridges that with `tail(…, 1)`. Coming the other way there are two
/// cases, and both are handled:
///
///   - the shape our own export produces, `asUInt(tail(add(a, b), 1))`, is
///     recognised and folded straight back to `Add(a, b)`;
///   - a bare foreign `add(a, b)` becomes our `Add` over operands padded to
///     `w + 1`, where the wrap can never fire, so the widening is preserved.
module Warp11.FirrtlImport

open System

/// A construct this reader does not accept, named rather than guessed at.
exception Unsupported of what: string

let private fail what = raise (Unsupported what)

// ---------------------------------------------------------------- structure

/// FIRRTL scopes by indentation, so the first pass is a tree of lines rather
/// than a token soup: it makes a module's body, and a memory's fields, obvious.
type private Line =
    { indent: int
      text: string
      children: Line list }

let private structure (source: string) : Line list =
    let raw =
        source.Replace("\r\n", "\n").Split '\n'
        |> Array.toList
        |> List.map (fun l ->
            // `;` starts a comment, as in the spec.
            let cut = l.IndexOf ';'
            if cut >= 0 then l.Substring(0, cut) else l)
        |> List.map (fun l ->
            // `@[file line:col]` is source-location debug info, which
            // `circt-translate --export-firrtl` attaches to every line. It says
            // nothing about the circuit.
            let cut = l.IndexOf "@["
            if cut >= 0 then l.Substring(0, cut) else l)
        |> List.filter (fun l -> l.Trim() <> "")
        |> List.map (fun l -> l.Length - l.TrimStart().Length, l.Trim())

    let rec build (lines: (int * string) list) =
        match lines with
        | [] -> [], []
        | (indent, text) :: rest ->
            let deeper = rest |> List.takeWhile (fun (i, _) -> i > indent)
            let after = rest |> List.skip (List.length deeper)
            let children, _ = build deeper
            let siblings, _ = build after
            { indent = indent; text = text; children = children } :: siblings, []

    build raw |> fst

// ------------------------------------------------------------------- tokens

let private isIdentChar c = Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '$'

let private tokenize (s: string) =
    let out = ResizeArray<string>()
    let mutable i = 0

    while i < s.Length do
        let c = s[i]

        if Char.IsWhiteSpace c then
            i <- i + 1
        elif c = '"' then
            let start = i
            i <- i + 1

            while i < s.Length && (s[i] <> '"' || s[i - 1] = '\\') do
                i <- i + 1

            i <- i + 1
            out.Add(s.Substring(start, i - start))
        elif isIdentChar c || (c = '-' && i + 1 < s.Length && Char.IsDigit s[i + 1]) then
            let start = i
            if c = '-' then i <- i + 1

            while i < s.Length && isIdentChar s[i] do
                i <- i + 1

            out.Add(s.Substring(start, i - start))
        else
            out.Add(string c)
            i <- i + 1

    List.ofSeq out

// ------------------------------------------------------------------- types

let private parseType (tokens: string list) : GroundType * string list =
    match tokens with
    | kind :: "<" :: width :: ">" :: rest ->
        let w = Int32.Parse width

        match kind with
        | "UInt" -> UInt w, rest
        | "SInt" -> SInt w, rest
        | other -> fail $"'{other}<{w}>' is not a ground type this reader accepts"
    | "Clock" :: rest -> UInt 1, rest
    | "Reset" :: rest
    | "AsyncReset" :: rest -> fail "AsyncReset — warp11's registers reset synchronously"
    | "UInt" :: _
    | "SInt" :: _ -> fail "an unwidthed UInt/SInt — this reader needs low FIRRTL, where every width is known"
    | "{" :: _ -> fail "a bundle type — high FIRRTL, which needs a lowering pass this reader does not have"
    | t :: _ -> fail $"unexpected type '{t}'"
    | [] -> fail "expected a type"

// -------------------------------------------------------------- expressions

/// What each name in scope is, so a reference can be typed. Memory port fields
/// (`mem.r0.data`) resolve through `memWord`.
type private Scope =
    { types: Collections.Generic.IDictionary<string, GroundType>
      /// An instance's port field spelled as our IR spells it: `adder.z` is
      /// the staging wire `adder_z`, because our IR has no dotted references.
      rename: Collections.Generic.IDictionary<string, string> }

let private literalValue (t: GroundType) (text: string) =
    let raw =
        if text.StartsWith "\"" then
            let body = text.Trim('"')

            if body.StartsWith "h" then
                Convert.ToUInt64(body.Substring 1, 16) |> int64
            elif body.StartsWith "b" then
                Convert.ToUInt64(body.Substring 1, 2) |> int64
            else
                Int64.Parse body
        else
            Int64.Parse text

    // An SInt literal carries a *value* in the text and a bit pattern in the IR.
    match t with
    | SInt w when raw < 0L -> uint64 (raw + (1L <<< w)) &&& maskOf w
    | _ -> uint64 raw &&& maskOf t.Width

let rec private parseExpr (scope: Scope) (tokens: string list) : Expr * string list =
    match tokens with
    | ("UInt" | "SInt") :: "<" :: _ :: ">" :: _ ->
        let t, rest = parseType tokens

        match rest with
        | "(" :: value :: ")" :: after -> Lit(literalValue t value, t), after
        | _ -> fail $"expected a literal value after {t}"

    | op :: "(" :: rest when isPrimOp op ->
        let args, after = parseArgs scope rest []
        applyPrim scope op args, after

    // A primop's constant argument is a bare integer, with no type beside it.
    | number :: rest when number.Length > 0 && (Char.IsDigit number[0] || number[0] = '-') ->
        let v = Int64.Parse number
        let w = max 1 (64 - Numerics.BitOperations.LeadingZeroCount(uint64 (abs v)))
        Lit(uint64 v &&& maskOf w, UInt w), rest

    | name :: rest when isIdentChar name[0] ->
        let name =
            match scope.rename.TryGetValue name with
            | true, r -> r
            | _ -> name

        match scope.types.TryGetValue name with
        | true, t -> Ref(name, t), rest
        | _ -> fail $"reference to '{name}', which nothing in this module declares"

    | t :: _ -> fail $"unexpected token '{t}' where an expression was expected"
    | [] -> fail "expected an expression"

and private parseArgs scope tokens acc =
    match tokens with
    | ")" :: rest -> List.rev acc, rest
    | "," :: rest -> parseArgs scope rest acc
    | _ ->
        let e, rest = parseExpr scope tokens
        parseArgs scope rest (e :: acc)

/// Every primitive the spec defines, so an unsupported one is refused by name
/// rather than mistaken for a reference to an undeclared signal.
and private isPrimOp op =
    List.contains
        op
        [ "add"; "sub"; "mul"; "div"; "rem"; "mux"; "cat"; "bits"; "eq"; "neq"; "lt"; "leq"
          "gt"; "geq"; "and"; "or"; "xor"; "not"; "shr"; "shl"; "dshr"; "dshl"; "pad"
          "asUInt"; "asSInt"; "asClock"; "asAsyncReset"; "tail"; "head"; "cvt"; "neg"
          "andr"; "orr"; "xorr" ]

/// A primitive at our IR's semantics. The interesting ones are `add`/`sub`,
/// where FIRRTL keeps a bit we drop.
and private applyPrim scope op (args: Expr list) : Expr =
    let arg n = List.item n args

    let intOf (e: Expr) =
        match e with
        | Lit (v, _) -> int v
        | _ -> fail $"'{op}' needs a constant argument"

    match op, List.length args with
    // Widening: pad both operands to w+1 first, where our wraparound can never
    // fire, so FIRRTL's extra bit survives.
    | "add", 2 ->
        let w = max (width (arg 0)) (width (arg 1)) + 1
        Add(widen w (arg 0), widen w (arg 1))
    | "sub", 2 ->
        // Widens like `add` and keeps its operands' reading — `sub` on two
        // `UInt` is `UInt<w+1>`, not signed. Worth stating because the obvious
        // guess is the other one, and a round-trip against our own emitter
        // cannot tell the difference: it was firtool refusing a file that did.
        let w = max (width (arg 0)) (width (arg 1)) + 1
        Sub(widen w (arg 0), widen w (arg 1))
    | "mul", 2 -> Mul(arg 0, arg 1)
    | "mux", 3 -> Mux(arg 0, arg 1, arg 2)
    | "cat", 2 -> Concat(arg 0, arg 1)
    | "bits", 3 -> Slice(arg 0, intOf (arg 1), intOf (arg 2))
    | "eq", 2 -> Eq(arg 0, arg 1)
    | "neq", 2 -> Not(Eq(arg 0, arg 1))
    | "lt", 2 -> Lt(arg 0, arg 1)
    | "gt", 2 -> Lt(arg 1, arg 0)
    | "leq", 2 -> Not(Lt(arg 1, arg 0))
    | "geq", 2 -> Not(Lt(arg 0, arg 1))
    | "and", 2 -> And(arg 0, arg 1)
    | "or", 2 -> Or(arg 0, arg 1)
    | "xor", 2 -> Xor(arg 0, arg 1)
    | "not", 1 -> Not(arg 0)
    | "shr", 2 -> Shr(arg 0, intOf (arg 1))
    | "shl", 2 -> Concat(arg 0, Lit(0UL, UInt(intOf (arg 1))))
    // FIRRTL's pad to a width the value already has is the identity; building
    // the node anyway would ask the emitter for a zero-width concatenation.
    | "pad", 2 ->
        let w = intOf (arg 1)
        if w <= width (arg 0) then arg 0 else Pad(arg 0, w)
    | "asUInt", 1 -> AsUInt(arg 0)
    | "asSInt", 1 -> AsSInt(arg 0)
    | "tail", 2 ->
        let n = intOf (arg 1)
        let w = width (arg 0)
        Slice(arg 0, w - n - 1, 0)
    | "head", 2 ->
        let n = intOf (arg 1)
        let w = width (arg 0)
        Slice(arg 0, w - 1, w - n)
    | "neg", 1 ->
        // Always SInt<w+1>: zero minus the operand widened its own way.
        let w = width (arg 0) + 1
        Sub(Lit(0UL, SInt w), widen w (arg 0))
    | "cvt", 1 -> if isSigned (arg 0) then arg 0 else AsSInt(Pad(arg 0, width (arg 0) + 1))
    | "andr", 1 -> Reduce(AllBits, arg 0)
    | "orr", 1 -> Reduce(AnyBit, arg 0)
    | "xorr", 1 -> Reduce(Parity, arg 0)
    | "div", 2 -> Div(arg 0, arg 1)
    | "rem", 2 -> Rem(arg 0, arg 1)
    | "dshl", 2 -> DynamicShl(arg 0, arg 1)
    | "dshr", 2 -> DynamicShr(arg 0, arg 1)
    | "asClock", 1 -> fail "'asClock' — warp11 models one clock per module, named by its ClockSpec"
    | "asAsyncReset", 1 -> fail "'asAsyncReset' — warp11's registers reset synchronously"
    | _ -> fail $"'{op}' with %d{List.length args} argument(s) is not something this reader accepts"

and private widen w (e: Expr) = if width e >= w then e else Pad(e, w)

/// Undo the export's own wrapping, so a round trip lands on the node it started
/// from rather than on a slice of a wider one. Every rewrite here is a shape
/// this file's sibling emitter produces; a foreign file simply will not match.
let rec private canonical (e: Expr) : Expr =
    match e with
    | AsUInt inner
    | AsSInt inner ->
        // `asUInt(tail(add(pad a, pad b), 1))` is our wraparound add on the way
        // out; this is it coming home. The tail's width says what the operands
        // were before the widening, which is what makes the match exact rather
        // than a guess.
        let rewrap (folded: Expr) =
            if isSigned folded = isSigned e then folded
            elif isSigned e then AsSInt folded
            else AsUInt folded

        match canonical inner with
        | Slice (Add (a, b), hi, 0) ->
            match unwiden (hi + 1) a, unwiden (hi + 1) b with
            | Some x, Some y -> rewrap (Add(x, y))
            | _ -> if isSigned e then AsSInt(canonical inner) else AsUInt(canonical inner)
        | Slice (Sub (a, b), hi, 0) ->
            match unwiden (hi + 1) a, unwiden (hi + 1) b with
            | Some x, Some y -> rewrap (Sub(x, y))
            | _ -> if isSigned e then AsSInt(canonical inner) else AsUInt(canonical inner)
        | c -> if isSigned e then AsSInt c else AsUInt c
    | Add (a, b) -> Add(canonical a, canonical b)
    | Sub (a, b) -> Sub(canonical a, canonical b)
    | Mul (a, b) -> Mul(canonical a, canonical b)
    | Eq (a, b) -> Eq(canonical a, canonical b)
    | Lt (a, b) -> Lt(canonical a, canonical b)
    | And (a, b) -> And(canonical a, canonical b)
    | Or (a, b) -> Or(canonical a, canonical b)
    | Xor (a, b) -> Xor(canonical a, canonical b)
    | Concat (a, b) -> Concat(canonical a, canonical b)
    | Mux (c, t, f) -> Mux(canonical c, canonical t, canonical f)
    | Not v -> Not(canonical v)
    | Shr (v, n) -> Shr(canonical v, n)
    | Pad (v, w) -> Pad(canonical v, w)
    | Reduce (kind, v) -> Reduce(kind, canonical v)
    | Div (a, b) -> Div(canonical a, canonical b)
    | Rem (a, b) -> Rem(canonical a, canonical b)
    | DynamicShl (v, n) -> DynamicShl(canonical v, canonical n)
    | DynamicShr (v, n) -> DynamicShr(canonical v, canonical n)
    | Slice (v, hi, lo) -> Slice(canonical v, hi, lo)
    | MemRead (m, a, w) -> MemRead(m, canonical a, w)
    | Lit _
    | Ref _ -> e

/// The operand of a widened add/sub, back at the width it had before the
/// widening — `Some x` only when this really is `x` padded by one bit, so a
/// genuine `pad` in the source is left alone.
and private unwiden (w: int) (e: Expr) =
    match e with
    | Pad (inner, pw) when pw = w + 1 && width inner = w -> Some inner
    // `sub` reads unsigned operands as signed, because it keeps the borrow.
    | AsSInt (Pad (inner, pw))
    | AsUInt (Pad (inner, pw)) when pw = w + 1 && width inner = w -> Some inner
    | _ -> None

// ------------------------------------------------------------------ modules

/// A memory as `.fir` describes it: ports are fields, so the reader and writer
/// connects have to be gathered before any expression mentioning them can be
/// understood.
type private MemInfo =
    { name: string
      addrWidth: int
      wordWidth: int
      /// port name -> the address it is driven with, once known
      readAddr: Collections.Generic.Dictionary<string, Expr>
      writeAddr: Collections.Generic.Dictionary<string, Expr>
      writeData: Collections.Generic.Dictionary<string, Expr>
      writeEnable: Collections.Generic.Dictionary<string, Expr> }

let private bitsToHold (n: int) =
    let mutable w = 0
    while (1 <<< w) < n do w <- w + 1
    w

/// Replace every reference to a memory's read-port data with the read itself.
/// Two passes are needed rather than one because an address can be any
/// expression, including another read.
let rec private resolveReads (mems: Map<string, MemInfo>) (e: Expr) : Expr =
    let go = resolveReads mems

    match e with
    | Ref (name, t) when name.EndsWith ".data" ->
        let parts = name.Split '.'

        match Map.tryFind parts[0] mems with
        | Some info when info.readAddr.ContainsKey parts[1] ->
            MemRead(info.name, go info.readAddr[parts[1]], info.wordWidth)
        | _ -> Ref(name, t)
    | Add (a, b) -> Add(go a, go b)
    | Sub (a, b) -> Sub(go a, go b)
    | Mul (a, b) -> Mul(go a, go b)
    | Eq (a, b) -> Eq(go a, go b)
    | Lt (a, b) -> Lt(go a, go b)
    | And (a, b) -> And(go a, go b)
    | Or (a, b) -> Or(go a, go b)
    | Xor (a, b) -> Xor(go a, go b)
    | Concat (a, b) -> Concat(go a, go b)
    | Mux (c, t, f) -> Mux(go c, go t, go f)
    | Not v -> Not(go v)
    | Shr (v, n) -> Shr(go v, n)
    | Pad (v, w) -> Pad(go v, w)
    | Reduce (kind, v) -> Reduce(kind, go v)
    | Div (a, b) -> Div(go a, go b)
    | Rem (a, b) -> Rem(go a, go b)
    | DynamicShl (v, n) -> DynamicShl(go v, go n)
    | DynamicShr (v, n) -> DynamicShr(go v, go n)
    | Slice (v, hi, lo) -> Slice(go v, hi, lo)
    | AsUInt v -> AsUInt(go v)
    | AsSInt v -> AsSInt(go v)
    | MemRead (m, a, w) -> MemRead(m, go a, w)
    | Lit _
    | Ref _ -> e

/// warp11's named-operand rule, satisfied on the reader's behalf.
///
/// Verilog has no part-select of an expression, so `slice`, `shr`, a signed
/// `pad` and a signed multiply all take a declared signal — a rule a warp11
/// author obeys by writing a wire. FIRRTL has no such rule: `bits(dshl(a, n),
/// 7, 0)` is ordinary, and firtool hoists it to a `_GEN` on its way out. So does
/// this, which is what makes a foreign file readable rather than only a file we
/// wrote.
let private hoistNamedOperands (fresh: Expr -> Expr) (e: Expr) : Expr =
    // `Pad` wants a *sign bit* it can name, not a named operand, and emission
    // reaches one through a narrowing shift — the top bit survives, so
    // `pad(shr(x, 3), 8)` is already fine. Hoisting it anyway would put a wire
    // where our own emitter never had one, which a round trip notices.
    let rec signReachable e =
        match e with
        | Ref _ -> true
        | AsUInt v
        | AsSInt v
        | Shr (v, _) -> signReachable v
        | _ -> false

    let rec go e =
        let named (x: Expr) =
            let x = go x
            if isNamed x then x else fresh x

        match e with
        | Slice (v, hi, lo) -> Slice(named v, hi, lo)
        | Shr (v, n) -> Shr(named v, n)
        | Pad (v, w) when isSigned v && not (signReachable v) -> Pad(named v, w)
        | Mul (a, b) when isSigned a || isSigned b -> Mul(named a, named b)
        | DynamicShr (v, n) when isSigned v -> DynamicShr(named v, go n)
        | Add (a, b) -> Add(go a, go b)
        | Sub (a, b) -> Sub(go a, go b)
        | Mul (a, b) -> Mul(go a, go b)
        | Eq (a, b) -> Eq(go a, go b)
        | Lt (a, b) -> Lt(go a, go b)
        | And (a, b) -> And(go a, go b)
        | Or (a, b) -> Or(go a, go b)
        | Xor (a, b) -> Xor(go a, go b)
        | Concat (a, b) -> Concat(go a, go b)
        | Mux (c, t, f) -> Mux(go c, go t, go f)
        | Not v -> Not(go v)
        | Pad (v, w) -> Pad(go v, w)
        | AsUInt v -> AsUInt(go v)
        | AsSInt v -> AsSInt(go v)
        | Reduce (kind, v) -> Reduce(kind, go v)
        | Div (a, b) -> Div(go a, go b)
        | Rem (a, b) -> Rem(go a, go b)
        | DynamicShl (v, n) -> DynamicShl(go v, go n)
        | DynamicShr (v, n) -> DynamicShr(go v, go n)
        | MemRead (m, a, w) -> MemRead(m, go a, w)
        | Lit _
        | Ref _ -> e

    go e

/// One module. `known` carries the modules already read, so an `inst` can be
/// linked to its child — `.fir` lists children before parents, as our own
/// emitter does.
let private readModule (known: Map<string, ModuleDef>) (header: string) (body: Line list) : ModuleDef =
    let name =
        match tokenize header with
        | "public" :: "module" :: n :: _
        | "module" :: n :: _ -> n
        | "extmodule" :: n :: _ -> fail $"extmodule '{n}' — this reader has no body to import"
        | _ -> fail $"unrecognised module header '{header}'"

    let types = Collections.Generic.Dictionary<string, GroundType>()
    let rename = Collections.Generic.Dictionary<string, string>()
    let decls = ResizeArray<Decl>()
    let instances = ResizeArray<Instance>()
    let mems = Collections.Generic.Dictionary<string, MemInfo>()
    let connects = ResizeArray<string * string list>()
    let asserts = ResizeArray<string list>()
    let regInits = Collections.Generic.Dictionary<string, uint64>()
    let nodeOf = Collections.Generic.Dictionary<string, string list>()

    let mutable clockPort = None
    let mutable resetPort = None
    let mutable resetNode = None
    // Our emitter writes the clock pair as the first two ports. A module whose
    // only sequential content is an instance has no `regreset` to name the
    // reset, so the port right after the clock is the fallback.
    let mutable resetCandidate = None

    // --- pass 1: declarations, so every name has a type before any expression
    //     is parsed. FIRRTL is order-insensitive, so this is not a restriction.
    for line in body do
        match tokenize line.text with
        | "input" :: n :: ":" :: rest ->
            if List.head rest = "Clock" then
                clockPort <- Some n
                // Still in scope: a memory port's `clk` connect names it.
                types[n] <- UInt 1
            else
                let t, _ = parseType rest
                types[n] <- t

                if clockPort.IsSome && resetCandidate.IsNone && t = UInt 1 then
                    resetCandidate <- Some n

                decls.Add(Input(n, t))
        | "output" :: n :: ":" :: rest ->
            let t, _ = parseType rest
            types[n] <- t
            decls.Add(Output(n, t))
        | "wire" :: n :: ":" :: rest ->
            let t, _ = parseType rest
            types[n] <- t
            decls.Add(Wire(n, t))
        | "regreset" :: n :: ":" :: rest ->
            let t, after = parseType rest
            types[n] <- t

            // `, clock, reset, init` — the reset tells us which port it is.
            match after with
            | "," :: _clk :: "," :: rstName :: "," :: initTokens ->
                resetNode <- Some rstName
                regInits[n] <- 0UL
                decls.Add(Reg(n, t, Some 0UL))
                nodeOf["$init$" + n] <- initTokens
            | "," :: _clk :: "," :: kind :: _ when isPrimOp kind ->
                fail
                    $"register '{n}' resets on `{kind}(…)` rather than a plain signal — warp11's registers reset synchronously off the module's own reset"
            | _ -> fail $"malformed regreset '{n}'"
        // FIRRTL's plain `reg` holds its value through reset, which is what
        // `regNoReset` is.
        | "reg" :: n :: ":" :: rest ->
            let t, _ = parseType rest
            types[n] <- t
            decls.Add(Reg(n, t, None))
        | "mem" :: n :: ":" :: _ ->
            // Matched on the raw text, not on tokens: `data-type` is one
            // FIRRTL keyword and three tokens, since `-` also starts a
            // negative literal.
            let field (key: string) =
                line.children
                |> List.tryPick (fun c ->
                    let prefix = key + " =>"

                    if c.text.StartsWith prefix then
                        Some(tokenize (c.text.Substring prefix.Length))
                    else
                        None)

            let dataType =
                match field "data-type" with
                | Some t when t |> List.exists (fun w -> w.Contains "[") ->
                    // A vector data-type is how a per-lane write mask is spelled
                    // in FIRRTL, and putting one back together is not a parse —
                    // the writer connected each lane separately, so the word and
                    // the mask would have to be *inferred* from a set of connects
                    // that need not be adjacent or complete. Refused by name
                    // rather than half-read, because a write reconstructed wrong
                    // is circuit behaviour changed silently.
                    fail
                        $"""memory '{n}' has a vector data-type ({String.concat " " t}) — a lane-masked memory. The export writes these; the reader does not put them back together"""
                | Some t -> fst (parseType t)
                | None -> fail $"memory '{n}' has no data-type"

            let depth =
                match field "depth" with
                | Some [ d ] -> Int32.Parse d
                | _ -> fail $"memory '{n}' has no depth"

            match field "read-latency" with
            | Some [ "0" ] -> ()
            | _ -> fail $"memory '{n}' is not read-latency 0 — warp11 mems read combinationally"

            match field "write-latency" with
            | Some [ "1" ] -> ()
            | _ -> fail $"memory '{n}' is not write-latency 1 — warp11 mems write on the edge"

            if line.children |> List.exists (fun c -> c.text.StartsWith "readwriter =>") then
                fail
                    $"memory '{n}' has a readwriter port — warp11 mems have separate read and write sites, which is what keeps BRAM inference a property of the DSL"

            let info =
                { name = n
                  addrWidth = bitsToHold depth
                  wordWidth = dataType.Width
                  readAddr = Collections.Generic.Dictionary()
                  writeAddr = Collections.Generic.Dictionary()
                  writeData = Collections.Generic.Dictionary()
                  writeEnable = Collections.Generic.Dictionary() }

            mems[n] <- info
            // FIRRTL says nothing about storage style, and its memories read
            // synchronously, so the tool is left to choose.
            decls.Add(Decl.Memory(n, info.addrWidth, dataType.Width, None, Unspecified))

            // Port fields, so an expression mentioning one can be typed.
            for c in line.children do
                match tokenize (c.text.Replace("=>", " => ")) with
                | "reader" :: "=" :: ">" :: [ p ] ->
                    types[$"{n}.{p}.data"] <- UInt dataType.Width
                    types[$"{n}.{p}.addr"] <- UInt info.addrWidth
                    types[$"{n}.{p}.en"] <- UInt 1
                | "writer" :: "=" :: ">" :: [ p ] ->
                    types[$"{n}.{p}.data"] <- UInt dataType.Width
                    types[$"{n}.{p}.addr"] <- UInt info.addrWidth
                    types[$"{n}.{p}.en"] <- UInt 1
                    types[$"{n}.{p}.mask"] <- UInt 1
                | _ -> ()
        | "inst" :: i :: "of" :: [ childName ] ->
            match Map.tryFind childName known with
            | None -> fail $"instance '{i}' is of '{childName}', which this circuit has not defined yet"
            | Some child ->
                instances.Add { instName = i; child = child }

                // Our IR connects an instance by name: the parent declares a
                // wire `{inst}_{port}`, and that *is* the port. A file we wrote
                // already has those wires, in the order the parent made them,
                // so re-declaring would put them back in the wrong place; a
                // foreign file has never heard of them, so they are added.
                for d in child.decls do
                    match d with
                    | Input (pn, t)
                    | Output (pn, t) ->
                        let staging = $"{i}_{pn}"
                        rename[$"{i}.{pn}"] <- staging

                        if not (types.ContainsKey staging) then
                            types[staging] <- t
                            decls.Add(Wire(staging, t))

                        types[staging] <- t
                    | _ -> ()
        | "node" :: n :: "=" :: rest -> nodeOf[n] <- rest
        | "connect" :: rest ->
            let target = List.head rest
            let value = List.tail rest |> List.skipWhile (fun t -> t = ",")
            connects.Add(target, value)
        | "assert" :: "(" :: rest -> asserts.Add rest
        // `invalidate` says a net has no defined value yet. Our IR has no such
        // state and the one-driver rule would object to the net being left
        // undriven, so this is a no-op — which is sound, because anything the
        // file *does* drive it with wins, and anything it does not is caught by
        // `checkWidths` downstream.
        | "invalidate" :: _ -> ()
        | "skip" :: _ -> ()
        | "when" :: _
        | "else" :: _ ->
            fail "a `when` block — high FIRRTL. Lower it first: `firtool x.fir --ir-fir | circt-translate --export-firrtl`"
        | "define" :: _
        | "propassign" :: _ -> fail "a probe or property, which warp11's IR has no equivalent for"
        | "printf" :: _
        | "fprintf" :: _ -> fail "`printf` — warp11's Sim has no print statement"
        | "stop" :: _ -> fail "`stop` — warp11's Sim runs for the cycles it is asked for"
        | "assume" :: _
        | "cover" :: _ -> fail "`assume`/`cover` — warp11 carries only `assert`"
        | "attach" :: _ -> fail "`attach` — analog nets, which warp11's IR has no type for"
        | "layerblock" :: _
        | "layer" :: _ -> fail "a layer block, which warp11's IR has no equivalent for"
        | "connect" :: _ -> ()
        | [] -> ()
        // Anything left is a statement this reader has never heard of. Ignoring
        // it would drop circuit behaviour silently, which is the one outcome
        // worth refusing over.
        | other -> fail $"""unrecognised statement '{String.concat " " other}'"""

    // The active-low arrangement our emitter writes: `node rst_pos = not(port)`.
    // Recognising it recovers the ClockSpec instead of leaving a stray wire.
    let mutable activeLow = false

    match resetNode with
    | Some rn ->
        // The internal reset is referenced by the write-enable gating, so it
        // has to be in scope even though it never reaches our IR.
        types[rn] <- UInt 1

        match nodeOf.TryGetValue rn with
        | true, [ "not"; "("; port; ")" ] ->
            resetPort <- Some port
            activeLow <- true
        | _ -> resetPort <- Some rn
    | None -> resetPort <- resetCandidate

    let scope = { types = types; rename = rename }

    // Wires the reader had to invent, in the order it invented them.
    let hoisted = ResizeArray<string * Expr>()

    // The declarations list is already materialised by this point, so a hoisted
    // wire is recorded here and appended at the end rather than added in place.
    let fresh (e: Expr) =
        let name = $"_hoist_%d{hoisted.Count}"
        let t = typeOf e
        types[name] <- t
        hoisted.Add(name, e)
        Ref(name, t)

    let satisfyNaming = hoistNamedOperands fresh

    // Nodes that are not the reset become ordinary wires.
    for KeyValue (n, tokens) in nodeOf do
        if not (n.StartsWith "$init$") && Some n <> resetNode then
            let e, _ = parseExpr scope tokens
            types[n] <- typeOf e
            decls.Add(Wire(n, typeOf e))

    // --- pass 2: values.
    let parsed = [ for target, tokens in connects -> target, fst (parseExpr scope tokens) ]

    // The clock pair is not in our decls — `emitVerilog` puts it on the
    // boundary itself, from the ClockSpec — so the ports we recognised as the
    // clock and the reset are dropped here rather than declared twice.
    let decls =
        decls
        |> Seq.filter (fun d ->
            match d with
            | Input (n, _) -> Some n <> resetPort && Some n <> clockPort
            | _ -> true)

    // Register reset values, which rode along on the `regreset` line.
    let decls =
        decls
        |> Seq.map (fun d ->
            match d with
            | Reg (n, t, _) ->
                match nodeOf.TryGetValue("$init$" + n) with
                | true, tokens ->
                    match fst (parseExpr scope tokens) with
                    | Lit (v, _) -> Reg(n, t, Some v)
                    | _ -> fail $"register '{n}' resets to something that is not a literal"
                | _ -> d
            | _ -> d)
        |> List.ofSeq

    // Memory port connects are structure, not statements: gather them.
    for target, value in parsed do
        let parts = target.Split '.'

        if parts.Length = 3 && mems.ContainsKey parts[0] then
            let info = mems[parts[0]]

            match parts[2] with
            | "addr" ->
                if info.writeData.ContainsKey parts[1] || types.ContainsKey $"{parts[0]}.{parts[1]}.mask" then
                    info.writeAddr[parts[1]] <- value
                else
                    info.readAddr[parts[1]] <- value
            | "data" -> info.writeData[parts[1]] <- value
            | "en" ->
                if types.ContainsKey $"{parts[0]}.{parts[1]}.mask" then
                    info.writeEnable[parts[1]] <- value
                else
                    ()
            | _ -> ()

    // A write port's address landed in readAddr if its `addr` connect came
    // before anything identified it as a writer; move it across.
    for KeyValue (_, info) in mems do
        for p in List.ofSeq info.writeData.Keys do
            if info.readAddr.ContainsKey p then
                info.writeAddr[p] <- info.readAddr[p]
                info.readAddr.Remove p |> ignore

    let memMap = mems |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    let instInputs =
        set
            [ for inst in instances do
                for d in inst.child.decls do
                    match d with
                    | Input (pn, _) -> yield $"{inst.instName}.{pn}"
                    | _ -> () ]

    // An instance's clock pair is wired by our emitter from the ClockSpec, so
    // these connects carry no information our IR keeps.
    let instClockPorts =
        set
            [ for inst in instances do
                yield $"{inst.instName}.{inst.child.clock.clockPort}"
                yield $"{inst.instName}.{inst.child.clock.resetPort}" ]

    let instOutputStaging =
        set
            [ for inst in instances do
                for d in inst.child.decls do
                    match d with
                    | Output (pn, _) -> yield $"{inst.instName}_{pn}"
                    | _ -> () ]

    let internalReset =
        match resetNode with
        | Some rn -> rn
        | None -> "rst"

    /// The export gates every write enable with `not(reset)`, because our
    /// Verilog puts sequential work inside `if (rst) … else …`. Strip it back
    /// off, or a round trip would gate it twice.
    let ungate (e: Expr) =
        match e with
        | And (inner, Not (Ref (r, _))) when r = internalReset -> inner
        | _ -> e

    let statements =
        [ for target, value in parsed do
              let parts = target.Split '.'
              let resolved = satisfyNaming (canonical (resolveReads memMap value))

              if parts.Length > 1 && mems.ContainsKey parts[0] then
                  // A memory's port connects are structure rather than
                  // statements — except that the write itself is a statement,
                  // and where it sits among the others matters. Our export
                  // writes `data` last of the trio, so that is the point the
                  // whole write belongs at.
                  if parts.Length = 3 && parts[2] = "data" && types.ContainsKey $"{parts[0]}.{parts[1]}.mask" then
                      let info = mems[parts[0]]
                      let addr = satisfyNaming (canonical (resolveReads memMap info.writeAddr[parts[1]]))
                      let data = satisfyNaming (canonical (resolveReads memMap info.writeData[parts[1]]))

                      let enable =
                          match info.writeEnable.TryGetValue parts[1] with
                          | true, e -> satisfyNaming (ungate (canonical (resolveReads memMap e)))
                          | _ -> Lit(1UL, UInt 1)

                      yield MemWrite(parts[0], addr, data, enable, None)
              elif instClockPorts.Contains target then
                  ()
              elif instOutputStaging.Contains target then
                  () // an instance's output reaches its staging wire implicitly here
              elif instInputs.Contains target then
                  // In our IR the staging wire *is* the instance's input — the
                  // connection is by name, not by statement. So `connect
                  // inst.port, inst_port` carries nothing and is dropped. A
                  // foreign file that drives the port with something else keeps
                  // its driver, landing on the staging wire.
                  let staging = target.Replace(".", "_")

                  match resolved with
                  | Ref (n, _) when n = staging -> ()
                  | _ -> yield Assign(staging, resolved)
              elif target.Contains "." then
                  fail $"connect to '{target}', which is not a name this reader can place"
              else
                  yield Assign(target, resolved)

          for tokens in asserts do
              // assert(clk, pred, en, "message")
              let pred, rest = parseExpr scope (List.tail (List.skipWhile (fun t -> t <> ",") tokens))
              let message = rest |> List.tryFind (fun t -> t.StartsWith "\"") |> Option.defaultValue "\"\""
              yield Assert(canonical (resolveReads memMap pred), message.Trim('"')) ]

    // Nodes are wires with a driver.
    let nodeStatements =
        [ for KeyValue (n, tokens) in nodeOf do
              if not (n.StartsWith "$init$") && Some n <> resetNode then
                  yield Assign(n, satisfyNaming (canonical (resolveReads memMap (fst (parseExpr scope tokens))))) ]

    // Force both lists before reading `hoisted`, since building them is what
    // fills it.
    let bodyStatements = nodeStatements @ statements
    let hoistStatements = [ for name, e in hoisted -> Assign(name, e) ]

    { name = name
      decls = decls @ [ for hoistName, e in hoisted -> Wire(hoistName, typeOf e) ]
      // Hoisted drivers first: they are combinational, so position does not
      // matter to the emitter, and reading them at the top is kinder.
      stmts = hoistStatements @ bodyStatements
      instances = List.ofSeq instances
      clock =
        { clockPort = Option.defaultValue "clk" clockPort
          resetPort = Option.defaultValue "rst" resetPort
          resetActiveLow = activeLow }
      streamReadies = []
      probes = []
      stateMachines = [] }

/// A `.fir` circuit as the design its main module describes.
let importFirrtl (source: string) : ModuleDef =
    // `circt-translate --export-firrtl` writes a placeholder rather than
    // failing when it meets an expression it cannot put back into `.fir` text —
    // indexing a vector by a signal is the one that turns up, since lowering
    // makes it a `multibit_mux`. Saying so beats "reference to
    // '<unsupported-expr-multibit_mux>'".
    if source.Contains "<unsupported-expr-" then
        let kind =
            let at = source.IndexOf "<unsupported-expr-"
            let rest = source.Substring(at + "<unsupported-expr-".Length)
            rest.Substring(0, max 0 (rest.IndexOf '>'))

        fail
            $"this file contains '{kind}', which circt-translate could not export back to .fir — it wrote a placeholder instead of the expression. Indexing a vector by a signal is the usual cause"

    let top = structure source

    let circuit =
        top
        |> List.tryFind (fun l -> l.text.StartsWith "circuit ")
        |> Option.defaultWith (fun () -> fail "no `circuit` line — is this a .fir file?")

    let circuitName =
        match tokenize circuit.text with
        | "circuit" :: n :: _ -> n
        | _ -> fail $"unrecognised circuit line '{circuit.text}'"

    // Modules may be children of the circuit line or siblings of it, depending
    // on how the file was written; take both.
    let moduleLines =
        (circuit.children @ top)
        |> List.filter (fun l -> l.text.StartsWith "module " || l.text.StartsWith "public module ")
        |> List.distinctBy (fun l -> l.text)

    let mutable known = Map.empty
    let mutable last = None

    for l in moduleLines do
        let m = readModule known l.text l.children
        known <- Map.add m.name m known
        last <- Some m

    match Map.tryFind circuitName known with
    | Some m -> m
    | None ->
        match last with
        | Some m -> m
        | None -> fail $"circuit '{circuitName}' declares no modules"
