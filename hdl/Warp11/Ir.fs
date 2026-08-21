[<AutoOpen>]
module Warp11.Ir

/// What a bag of bits is: a width, and whether the top bit is a sign.
///
/// FIRRTL's ground types, and the reason the IR has them: signedness on the
/// *operation* meant `mul` and `mulS` were two nodes saying one thing about
/// their operands, and every caller had to remember which. Here the operands
/// say it, once, where they are declared.
type GroundType =
    | UInt of width: int
    | SInt of width: int

    /// How many bits.
    member t.Width =
        match t with
        | UInt w
        | SInt w -> w

    /// Whether the top bit is a sign — what `mul`, `lt` and the shifts read.
    member t.Signed =
        match t with
        | SInt _ -> true
        | UInt _ -> false

/// The same width, the other reading. Zero hardware — this is `asUInt`/`asSInt`
/// at the type level.
let asUnsignedType (t: GroundType) = UInt t.Width
/// The other direction, and the one `mul` and `lt` read.
let asSignedType (t: GroundType) = SInt t.Width

/// Which way a whole-value fold goes. `AllBits` is true only for all-ones,
/// `AnyBit` false only for zero, and `Parity` is the XOR of every bit.
type Reduction =
    | AllBits
    | AnyBit
    | Parity

/// Every value a design can compute. Width-only bit vectors with a reading
/// attached — `GroundType` is the whole of the type system down here.
///
/// Meaning lives a layer up: `Number` knows about fraction bits, `Union2`
/// knows about variants, and neither is visible to the emitter or the
/// simulator. What the IR knows is how wide a value is and whether its top bit
/// is a sign, which is exactly what it takes to emit correct Verilog.
type Expr =
    | Lit of value: uint64 * ty: GroundType
    | Ref of name: string * ty: GroundType
    | Add of lhs: Expr * rhs: Expr
    | Sub of lhs: Expr * rhs: Expr
    | Mul of lhs: Expr * rhs: Expr
    | Mux of cond: Expr * ifTrue: Expr * ifFalse: Expr
    | Concat of hi: Expr * lo: Expr
    | Slice of source: Expr * hi: int * lo: int
    | Eq of lhs: Expr * rhs: Expr
    | Lt of lhs: Expr * rhs: Expr
    | And of lhs: Expr * rhs: Expr
    | Or of lhs: Expr * rhs: Expr
    | Xor of lhs: Expr * rhs: Expr
    | Not of value: Expr
    /// Narrowing right shift: drop the low `shift` bits. Arithmetic exactly
    /// when the source is signed — the top bits survive, so the sign does too,
    /// which is the whole reason this is not a `Slice`.
    | Shr of source: Expr * shift: int
    /// Widen to `width`, replicating the sign bit or padding zeros — whichever
    /// the source says. FIRRTL's `pad`, and it absorbs the old `SignExtend`.
    | Pad of source: Expr * width: int
    /// Shifts by an amount that is itself a signal — a barrel shifter, where
    /// `Shr` is a part-select. FIRRTL's `dshl`/`dshr`, including their widths:
    /// a left shift keeps every bit it could ever produce, so it widens by
    /// `2^amountWidth - 1`, and a right shift keeps its operand's width.
    | DynamicShl of value: Expr * amount: Expr
    | DynamicShr of value: Expr * amount: Expr
    /// A whole value folded down to one bit. FIRRTL's `andr`/`orr`/`xorr`, and
    /// Verilog's `&x`/`|x`/`^x` — one node rather than three, because the only
    /// thing that differs is which way the fold goes.
    | Reduce of kind: Reduction * value: Expr
    /// Truncating division and its remainder. Combinational, as FIRRTL and
    /// Verilog both have them — the *authoring* surface only offers a constant
    /// divisor, because that is where the cost cliff is, but the IR mirrors
    /// FIRRTL so a foreign design reads whatever it says.
    | Div of dividend: Expr * divisor: Expr
    | Rem of dividend: Expr * divisor: Expr
    | MemRead of mem: string * addr: Expr * width: int
    /// Reinterpretation: the same bits read the other way. No hardware, and the
    /// only way a value changes signedness.
    | AsUInt of value: Expr
    | AsSInt of value: Expr

    /// The bitwise trio only. Their result lands in `UInt` whatever the operands
    /// said — bits are bits, FIRRTL's rule — so they need nothing that has to be
    /// written after `typeOf`. The arithmetic three are augmented further down,
    /// where `agreeOnSign` exists to route them through.
    static member (&&&)(a: Expr, b: Expr) = And(a, b)
    static member (|||)(a: Expr, b: Expr) = Or(a, b)
    static member (^^^)(a: Expr, b: Expr) = Xor(a, b)

/// What an expression *is*. Arithmetic keeps its operands' reading; bit
/// manipulation lands in `UInt`, because bits are bits and a slice has no sign
/// until someone says so. Both rules are FIRRTL's.
let rec typeOf expr =
    match expr with
    | Lit (_, t)
    | Ref (_, t) -> t
    | Add (a, b)
    | Sub (a, b) ->
        let w = max (typeOf a).Width (typeOf b).Width
        if (typeOf a).Signed then SInt w else UInt w
    | Mul (a, b) ->
        let w = (typeOf a).Width + (typeOf b).Width
        if (typeOf a).Signed then SInt w else UInt w
    | Mux (_, t, _) -> typeOf t
    | Shr (s, n) ->
        let t = typeOf s
        let w = max (t.Width - n) 1
        if t.Signed then SInt w else UInt w
    | Pad (s, w) ->
        let t = typeOf s
        let w' = max t.Width w
        if t.Signed then SInt w' else UInt w'
    | DynamicShl (v, n) ->
        let t = typeOf v
        let w = t.Width + (1 <<< (typeOf n).Width) - 1
        if t.Signed then SInt w else UInt w
    | DynamicShr (v, _) -> typeOf v
    | Reduce _ -> UInt 1
    // FIRRTL's widths, checked against firtool rather than remembered. A
    // quotient cannot exceed its dividend, and a remainder cannot reach its
    // divisor — except that a *signed* quotient gains a bit, because
    // MIN / -1 overflows: -128 / -1 is +128, which needs nine.
    | Div (a, _) ->
        let t = typeOf a
        if t.Signed then SInt(t.Width + 1) else UInt t.Width
    | Rem (a, b) ->
        let t = typeOf a
        let w = min t.Width (typeOf b).Width
        if t.Signed then SInt w else UInt w
    | AsUInt v -> asUnsignedType (typeOf v)
    | AsSInt v -> asSignedType (typeOf v)
    | Concat (hi, lo) -> UInt((typeOf hi).Width + (typeOf lo).Width)
    | Slice (_, hi, lo) -> UInt(hi - lo + 1)
    | Eq _
    | Lt _ -> UInt 1
    | And (a, b)
    | Or (a, b)
    | Xor (a, b) -> UInt(max (typeOf a).Width (typeOf b).Width)
    | Not v -> UInt (typeOf v).Width
    | MemRead (_, _, w) -> UInt w

/// How many bits, whatever the reading. Widths live in the values, which is
/// why so little of the DSL takes a width parameter.
let width (expr: Expr) = (typeOf expr).Width

/// Is this value read as signed?
let isSigned (expr: Expr) = (typeOf expr).Signed

let internal maskOf w =
    if w >= 64 then System.UInt64.MaxValue else (1UL <<< w) - 1UL

/// A constant at a stated width, read as unsigned.
let lit value width = Lit(value, UInt width)
/// A reference to a name at a stated width. Declaring the thing is someone
/// else's job — this only refers to it.
let signal name width = Ref(name, UInt width)

/// The same, read as signed. The bits are identical; only the reading differs.
let litS value width = Lit(value, SInt width)
/// A reference read as signed.
let signalS name width = Ref(name, SInt width)

/// A bare number at a width it was not told, but can work out: the width of
/// whatever it is written beside.
///
/// The fit check is the reason this is a function and not a constructor. A
/// literal too big for the signal it is being connected to is a mistake, and
/// keeping its low bits would hide the mistake rather than report it — the same
/// rule `rom` already applies to its contents.
let litAt (w: int) (value: uint64) =
    if value > maskOf w then
        failwith $"literal %d{value} does not fit %d{w} bits"

    Lit(value, UInt w)

/// What a declaration is being given: a bare width, or a type.
///
/// `input "a" 8` means `UInt 8` — which is what every declaration in the
/// codebase meant before there was a choice — and `input "a" (SInt 8)` says the
/// other thing. Resolved statically, the same way the connect operator decides
/// what it is connecting.
type AsType =
    | AsType

    static member ($)(AsType, w: int) = UInt w
    static member ($)(AsType, t: GroundType) = t

/// How a bare number reaches an operator that wanted a signal.
///
/// Overloads on a type rather than a plain function, because the choice has to
/// be made from the *type* of what was written — an `Expr` passes through, a
/// number is given a width. That is what lets `0UL ==> r` and `someSignal ==> r`
/// be the same operator. Public because the operators using it are `inline`,
/// and an inline body has to be reachable from wherever it is called.
type Widen =
    | Widen

    static member ($)(Widen, value: Expr) = fun (_: int) -> value
    static member ($)(Widen, value: uint64) = fun (w: int) -> litAt w value

/// Does this expression reach a declared signal? True through `asUInt` and
/// `asSInt`, because a reinterpretation moves no bits — which is what lets
/// `slice` take one.
let rec isNamed expr =
    match expr with
    | Ref _ -> true
    | AsUInt v
    | AsSInt v -> isNamed v
    | _ -> false

/// A literal borrows its neighbour's *reading* as well as its width — the
/// codebase's scalar rule, one attribute wider. Two named signals that disagree
/// is an error: which of them is wrong is the author's to say.
let agreeOnSign (a: Expr) (b: Expr) =
    if isSigned a = isSigned b then
        a, b
    else
        match a, b with
        | Lit _, _ -> (if isSigned b then AsSInt a else AsUInt a), b
        | _, Lit _ -> a, (if isSigned a then AsSInt b else AsUInt b)
        | _ ->
            failwith
                $"one operand is signed and the other is not (%A{typeOf a} vs %A{typeOf b}) — asSInt or asUInt one of them"

/// Add at the wider operand's width. Two's complement makes the *gates*
/// sign-agnostic, so there is no signed variant — but the *result's reading* is
/// taken from the operands, so they still have to agree on one. Without that a
/// signed value added to an unsigned literal came out labelled unsigned, and the
/// mislabel was silent until something downstream that cares — a compare, a
/// shift, a pad — read it the wrong way.
let add a b =
    let a, b = agreeOnSign a b
    Add(a, b)

/// Wraparound subtract, at `add`'s rule and for `add`'s reason. Signedness still
/// enters the IR only where the bit patterns diverge — multiply, ordering
/// compares, right shift — and this is about which of those the result reaches.
let sub a b =
    let a, b = agreeOnSign a b
    Sub(a, b)

/// Multiply. Signed when its operands are — and both must agree, because
/// emission extends each side by the *pair's* reading: a signed operand against
/// an unsigned one would zero-extend both and quietly compute the wrong product.
/// A signed multiply also needs declared signals, since sign extension
/// replicates a named bit.
let mul a b =
    let a, b = agreeOnSign a b

    if isSigned a && not (isNamed a && isNamed b) then
        failwith "a signed multiply needs declared signals — sign extension replicates a named bit; assign computed values to wires first"

    Mul(a, b)

/// Select, at `add`'s rule: the mux itself is sign-agnostic — it moves bits, it
/// does not read them — but `typeOf` takes the result's reading from the *true*
/// branch, so branches that disagree make the answer depend on which one was
/// written first. They agree here instead.
let mux cond ifTrue ifFalse =
    let ifTrue, ifFalse = agreeOnSign ifTrue ifFalse
    Mux(cond, ifTrue, ifFalse)

/// Concatenate, `hi` above `lo`, widths adding. The result is unsigned: a
/// joined bit pattern has no sign to inherit.
let cat hi lo = Concat(hi, lo)

/// Kotlin's rule, verbatim: slice takes a declared signal, not an arbitrary
/// expression — Verilog has no part-select of a computed value, so wrap it in a
/// wire first. Enforced here and again at emission.
/// A signal read the other way is still that signal, so `asSInt w` slices — the
/// bits do not move, and emission reaches the name underneath.
let slice hi lo source =
    if isNamed source then
        Slice(source, hi, lo)
    else
        failwith "slice needs a declared signal — assign the computed value to a wire first"
/// Equality. The right operand may be a bare number, which takes the left's
/// width — comparing a state register against a code is the commonest literal
/// in the codebase, and the width was never the interesting part of it.
let inline eq (a: Expr) b = Eq(a, (Widen $ b) (width a))

/// Less-than, signed exactly when its operands are. Unsigned, differing widths
/// zero-extend to the wider, like Eq. Signed, they must already match:
/// sign-extending the narrower operand changes its bits, and that is the
/// caller's decision, not something a compare should make quietly.
let inline lt (a: Expr) b =
    let a, b = agreeOnSign a ((Widen $ b) (width a))

    if isSigned a && width a <> width b then
        failwith
            $"a signed compare needs equal widths, got %d{width a} and %d{width b} — sign-extend the narrower operand first"

    Lt(a, b)

/// Bitwise complement, width-preserving.
let bnot value = Not value

/// Read these bits the other way. No hardware — the bits do not move, only what
/// an operation will make of them. This is how a value that was declared one way
/// gets used the other, and it is the only way.
let asSInt (value: Expr) = AsSInt value
/// And back the other way.
let asUInt (value: Expr) = AsUInt value

/// The same operators, with a bare number on one side.
///
/// An augmentation rather than members on the type, because the width a literal
/// borrows comes from `width`, which cannot be written before the type it
/// matches on. The rule is the codebase's existing one, made to apply where it
/// reads best: **when a neighbour supplies the width, the scalar borrows it;
/// otherwise the literal names its own.** `lit` is untouched and every existing
/// call site still says exactly what it said.
///
/// The arithmetic three on two expressions live here rather than on the type,
/// for the same reason: they route through `add`/`sub`/`mul`, which need
/// `agreeOnSign`, which needs `typeOf`. Writing `a + b` and writing `add a b`
/// were never meant to mean different things, and before this they did — the
/// operator built the node directly and skipped the agreement.
type Expr with
    static member (+)(a: Expr, b: Expr) = add a b
    static member (-)(a: Expr, b: Expr) = sub a b
    static member ( * )(a: Expr, b: Expr) = mul a b

    static member (+)(a: Expr, b: uint64) = add a (litAt (width a) b)
    static member (+)(a: uint64, b: Expr) = add (litAt (width b) a) b
    static member (-)(a: Expr, b: uint64) = sub a (litAt (width a) b)
    static member (-)(a: uint64, b: Expr) = sub (litAt (width b) a) b
    static member ( * )(a: Expr, b: uint64) = mul a (litAt (width a) b)
    static member ( * )(a: uint64, b: Expr) = mul (litAt (width b) a) b
    static member (&&&)(a: Expr, b: uint64) = And(a, litAt (width a) b)
    static member (&&&)(a: uint64, b: Expr) = And(litAt (width b) a, b)
    static member (|||)(a: Expr, b: uint64) = Or(a, litAt (width a) b)
    static member (|||)(a: uint64, b: Expr) = Or(litAt (width b) a, b)
    static member (^^^)(a: Expr, b: uint64) = Xor(a, litAt (width a) b)
    static member (^^^)(a: uint64, b: Expr) = Xor(litAt (width b) a, b)

/// Arithmetic shift right by a constant, width-preserving: the sign bit fills
/// from the top. No longer a node — it is the narrowing shift padded back to
/// where it started, which is what FIRRTL says it is. The slice rule applies:
/// emission replicates the operand's sign bit and part-selects the rest, both of
/// which need a name.
let sra n source =
    if not (isNamed source) then
        failwith "sra needs a declared signal — assign the computed value to a wire first"

    let w = width source

    if n = 0 then source
    elif n > 0 && n < w then Pad(Shr(AsSInt source, n), w)
    else failwith $"sra by %d{n} on %d{w} bits — the shift must be in 0..%d{w - 1}"

/// Widen, the way the value itself says: sign bit replicated for a signed one,
/// zeros for an unsigned one. FIRRTL's `pad`, and the fifth of `Number`'s
/// choices stops being a choice. Extending to the operand's own width is the
/// identity; narrowing is refused — that decision is the caller's.
let pad targetWidth source =
    let w = width source

    if targetWidth = w then source
    elif targetWidth < w then
        failwith $"pad to %d{targetWidth} bits narrows a %d{w}-bit value — slice or saturate instead"
    elif isSigned source && not (isNamed source) then
        failwith "padding a signed value needs a declared signal — sign extension replicates a named bit; assign computed values to wires first"
    else
        Pad(source, targetWidth)

/// Sign-extend regardless of how the source was declared — `pad` for a value
/// whose bits are two's complement but whose declaration has not said so. The
/// slice rule applies, since emission replicates the named sign bit.
let signExtend targetWidth source =
    if not (isNamed source) then
        failwith "signExtend needs a declared signal — assign the computed value to a wire first"

    let w = width source

    if targetWidth = w then source
    elif targetWidth > w then Pad(AsSInt source, targetWidth)
    else failwith $"signExtend to %d{targetWidth} bits narrows a %d{w}-bit signal — slice instead"

/// The widest a dynamic left shift could ever need. FIRRTL's rule, and a
/// footgun in it: the width follows from the *amount's* width, so a 32-bit shift
/// amount asks for a 4-gigabit value. Refused rather than attempted.
let private dynamicShlWidth (value: Expr) (amount: Expr) =
    let amountWidth = (typeOf amount).Width

    if amountWidth > 12 then
        failwith
            $"a dynamic left shift by a %d{amountWidth}-bit amount would produce a %d{amountWidth}-bit-addressable value — slice the amount to the range it can actually take"

    width value + (1 <<< amountWidth) - 1

/// Shift left by a signal. Widens the way FIRRTL says, keeping every bit the
/// shift could produce, so nothing is lost and the caller narrows on purpose.
let shlBy (amount: Expr) (value: Expr) =
    dynamicShlWidth value amount |> ignore
    DynamicShl(value, amount)

/// Shift right by a signal, at the operand's own width. Arithmetic exactly when
/// the operand is signed — and a signed one needs a declared signal, since the
/// emitted form names it.
let shrBy (amount: Expr) (value: Expr) =
    if isSigned value && not (isNamed value) then
        failwith
            "a signed dynamic right shift needs a declared signal — assign the computed value to a wire first"

    DynamicShr(value, amount)

/// Divide by a *constant*, and only by a constant.
///
/// The divisor is an `int` rather than an `Expr`, so dividing by a signal is not
/// something this surface can express — which is deliberate, and is where the
/// cost is. `x / 8` is a shift and `x / 10` is a multiply by a reciprocal, both
/// of which synthesis does for free; `a / b` on two signals is thirty levels of
/// logic that looks identical at the call site and first shows up as a timing
/// failure. Reach for a pipelined divider when the divisor really varies.
///
/// Zero is refused here, where FIRRTL leaves division by zero undefined.
let divideBy (k: int) (value: Expr) =
    if k = 0 then failwith "divideBy 0 — the divisor must be non-zero"

    let t = typeOf value

    if k < 0 && not t.Signed then
        failwith $"divideBy %d{k} on an unsigned value — read it as signed first, or divide by a positive constant"

    Div(value, Lit(uint64 k &&& maskOf t.Width, if t.Signed then SInt t.Width else UInt t.Width))

/// The remainder of the same division, at the same constant-only rule.
let remainderBy (k: int) (value: Expr) =
    if k = 0 then failwith "remainderBy 0 — the divisor must be non-zero"

    let t = typeOf value

    if k < 0 && not t.Signed then
        failwith $"remainderBy %d{k} on an unsigned value — read it as signed first, or use a positive constant"

    Rem(value, Lit(uint64 k &&& maskOf t.Width, if t.Signed then SInt t.Width else UInt t.Width))

/// True only when every bit of `value` is set.
let allBitsSet value = Reduce(AllBits, value)

/// True when any bit of `value` is set — the `!= 0` that designs otherwise
/// write by hand, said in one gate rather than a comparator.
let anyBitSet value = Reduce(AnyBit, value)

/// The XOR of every bit: 1 when an odd number of them are set.
let parity value = Reduce(Parity, value)

/// How a shift amount is written: a number the elaborator knows, or a signal it
/// does not. The call is the same either way — `shl 4 a` is a rewiring and
/// `shl n a` is a barrel shifter, and which one you get follows from what you
/// wrote rather than from remembering a second function's name.
type ShiftAmount =
    | ShiftAmount

    static member ($)(ShiftAmount, n: int) = Choice1Of2 n
    static member ($)(ShiftAmount, n: Expr) = Choice2Of2 n

/// Left shift. By a constant it is a rewiring — `by` zero bits appended at the
/// LSB end, pure sugar over cat, result width w + by, and any expression works.
/// By a *signal* it is a barrel shifter, widened the way FIRRTL widens it so no
/// bit the shift could produce is lost.
///
/// One call shape for both, because which one you get follows from what you
/// wrote rather than from remembering a second name.
let inline shl by source =
    match ShiftAmount $ by with
    | Choice1Of2 k ->
        if k < 0 then failwith $"shl by %d{k} — the shift must be >= 0"
        elif k = 0 then source
        else Concat(source, Lit(0UL, UInt k))
    | Choice2Of2 amount -> shlBy amount source

/// Right shift by a constant — narrowing: the high w − by bits. Arithmetic
/// exactly when the value is signed, since the sign bit is among the ones that
/// survive; the width-preserving form is `sra`. The slice rule applies:
/// declared signals only.
let inline shr by source =
    match ShiftAmount $ by with
    | Choice1Of2 k ->
        if not (isNamed source) then
            failwith "shr needs a declared signal — assign the computed value to a wire first"

        let w = width source

        if k = 0 then source
        elif k > 0 && k < w then Shr(source, k)
        else failwith $"shr by %d{k} on %d{w} bits — the shift must be in 0..%d{w - 1}"
    // By a signal the width cannot narrow, since the amount is not known here:
    // the result keeps the operand's width, as FIRRTL's `dshr` does.
    | Choice2Of2 amount -> shrBy amount source

/// Saturating narrow to `toWidth` bits, clamping at the range the *value's own
/// type* describes: all-ones for an unsigned one, the two's-complement range
/// [−2^(t−1), 2^(t−1)−1] for a signed one — the fxSat shape the GEP ALU keeps
/// total. Sugar over lt/mux/slice, so the slice rule applies, which also puts
/// the compared value on a named wire where the hardware cost lives. No-op at
/// the operand's own width.
///
/// This was `saturate`/`saturateS` until the narrowing shift learned to stay
/// signed; picking the wrong one of that pair was silent.
let saturate toWidth source =
    if not (isNamed source) then
        failwith "saturate needs a declared signal — assign the computed value to a wire first"
    else
        let w = width source

        if toWidth = w then
            source
        elif toWidth < 1 || toWidth >= w then
            failwith $"saturate to %d{toWidth} bits from %d{w} — the target must be in 1..%d{w}"
        elif isSigned source then
            let maxS = maskOf (toWidth - 1)
            let minSAtW = maskOf w &&& ~~~maxS

            // The comparisons read both sides as signed, and so does the result.
            // A clamp to [-2^(t-1), 2^(t-1)-1] has just *established* that the
            // value is signed, so handing back a UInt would discard exactly what
            // it proved — and silently, since the bits are identical either way.
            // `shr` and `pad` keep the reading for the same reason; this was the
            // last of the three that dispatched on the source's sign without
            // carrying it through.
            Mux(
                Lt(Lit(maxS, SInt w), source),
                Lit(maxS, SInt toWidth),
                Mux(
                    Lt(source, Lit(minSAtW, SInt w)),
                    Lit(1UL <<< (toWidth - 1), SInt toWidth),
                    AsSInt(Slice(source, toWidth - 1, 0))
                )
            )
        else
            let maxT = maskOf toWidth
            Mux(Lt(Lit(maxT, UInt w), source), Lit(maxT, UInt toWidth), Slice(source, toWidth - 1, 0))

/// A declaration carries the value's *type*, not just its width — which is how
/// signedness survives past the module that declared it. An instance's staging
/// wires are built from the child's decls, and the debugger's signal table reads
/// them, so a width-only decl was where the reading got lost.
/// What the storage should be built from, and therefore which reads are legal.
///
/// **This exists because the tool otherwise decides and can decide differently
/// from what the design assumed.** Block RAM physically cannot read
/// combinationally, so a synthesiser handed an asynchronous read of an array it
/// chose to put in BRAM inserts a register — and that design passes this repo's
/// Sim *and* Verilator, then corrupts on the board. It is the top entry in
/// CLAUDE.md's hardware gotchas, and it has bitten the Mandelbrot coalescer and
/// bounded `streamFifo`'s depth.
///
/// So the choice is stated rather than inferred: `Distributed` is LUTRAM and is
/// the only style an asynchronous read is allowed on, `Block` is BRAM and takes
/// sync reads only, and `Unspecified` leaves it to the tool — which is safe
/// precisely because an async read is refused there too.
type RamStyle =
    | Unspecified
    | Distributed
    | Block

/// What a module declares: its ports, its state, its memories. Statements
/// drive these names, and nothing else introduces one.
type Decl =
    | Input of name: string * ty: GroundType
    | Output of name: string * ty: GroundType
    | Wire of name: string * ty: GroundType
    /// A register. `init` is the value it takes while reset is asserted — and
    /// `None` means it takes none: it holds through reset, which is FIRRTL's
    /// plain `reg` and, on an FPGA, the shape you want for a datapath. A reset
    /// net reaching every flop costs fanout and routing, and it blocks SRL
    /// inference for a delay chain; Xilinx's own advice is to reset control
    /// registers and leave the data path alone.
    | Reg of name: string * ty: GroundType * init: uint64 option
    /// Depth is 2^addrWidth, so an address can never leave the array. `init`
    /// fixes the contents at elaboration (a ROM / preloaded BRAM): emission
    /// writes a Verilog `initial` block (→ BRAM INIT), the Sim loads it at
    /// construction, and `Reset()` reloads it — modeling reconfiguration.
    /// Entries past the end of `init` are zero, explicitly, in both worlds.
    | Memory of name: string * addrWidth: int * width: int * init: uint64[] option * style: RamStyle

/// The name and type of a declaration. `Memory` is not one of these — its words
/// are addressed, not driven, and it carries its own two widths.
let declOf decl =
    match decl with
    | Input (n, t)
    | Output (n, t)
    | Wire (n, t) -> Some(n, t)
    | Reg (n, t, _) -> Some(n, t)
    | Memory _ -> None

/// What a module does. `If` is not here — a conditional is folded into its
/// parent scope during elaboration, so by the time a design is a `ModuleDef`
/// every target has exactly one assign and every memory one write site.
type Stmt =
    | Assign of target: string * value: Expr
    /// One write site per mem in the final stmts — Def merges multiple write
    /// calls into a priority mux, because two write sites kill BRAM inference
    /// (Synth 8-3391) even when mutually exclusive.
    /// `mask` selects which lanes of the word the write reaches: one bit per
    /// lane, lanes dividing the word evenly. `None` writes the whole word and
    /// emits what it always did. A 32-bit memory with a 4-bit mask has byte
    /// lanes — AXI's `wstrb`, and the shape a synthesiser infers as a
    /// byte-enabled block RAM rather than as read-modify-write logic.
    | MemWrite of mem: string * addr: Expr * data: Expr * enable: Expr * mask: Expr option
    /// A claim about the design that must hold on every edge. Written inside
    /// `If`, the enclosing conditions are already folded in as an implication,
    /// so the claim says nothing about cycles where its branch is not taken.
    ///
    /// Not synthesizable and not meant to be: emission wraps it in a
    /// translate_off region, the Sim compiles it like any other expression, and
    /// both are opt-in. See `Sim(design, checkAsserts = true)`.
    | Assert of cond: Expr * message: string

/// How a module names its clock pair — warp11's knobs, ported: an AXI-style
/// wrapper is `s_axi_aclk` / active-low `s_axi_aresetn`, everything else is
/// `clk`/`rst`. Active-low derives an internal active-high wire at emission;
/// the Sim never models the pins, so only the emitter and testbench care.
type ClockSpec =
    { clockPort: string
      resetPort: string
      resetActiveLow: bool }

/// `clk` with an active-high `rst`, which is what a design gets unless it
/// says otherwise.
let defaultClock =
    { clockPort = "clk"
      resetPort = "rst"
      resetActiveLow = false }

/// The AXI spelling. A slave wrapper's clock pair has to be named this for a
/// block design to connect it without a rename.
let axiClock =
    { clockPort = "s_axi_aclk"
      resetPort = "s_axi_aresetn"
      resetActiveLow = true }

/// One elaborated module — the output of the builder and the input to
/// everything else. The emitter, the simulator, the FIRRTL export and the
/// debugger all read this and only this.
///
/// The last three fields are elaboration-time knowledge that the flattened
/// statements no longer carry: they exist because the information is
/// unrecoverable once a design is bits and names.
type ModuleDef =
    { name: string
      decls: Decl list
      stmts: Stmt list
      instances: Instance list
      clock: ClockSpec
      /// Ready nets of streams created in this module, with how many times each
      /// was driven — a stream has exactly one consumer, and after If-folding the
      /// final stmts hold one assign per target, so the raw drive count is
      /// recorded here at elaboration for checkStreams to judge.
      streamReadies: (string * int) list
      /// Telemetry probes planted in this module (`streamProbe`) — the name
      /// stems of the `_blocked`/`_starved` counter pairs, recorded so
      /// `streamReport` can walk a design for them.
      probes: string list
      /// State machines declared in this module (`machine`): the encoded state
      /// register's name, and what each of its codes means. Elaboration is the
      /// only place that knows — the register itself carries a number — so the
      /// meaning is recorded here and the debugger shows `assemble` where the
      /// signal table would otherwise show 1.
      stateMachines: (string * (uint64 * string) list) list }

/// A child module and the name it stands under. Its ports become
/// `{instName}_{port}` in the *parent's* namespace, which is why an instance
/// name collides with an ordinary declaration.
and Instance = { instName: string; child: ModuleDef }

/// How a module names its ports. Four fields where two would do, because the
/// width-only spelling is what almost every port wants and `UInt` at 170 sites
/// would be noise: `inPort` is `inPortAs … (UInt w)`, and a port whose type is
/// more than a width says so.
///
/// This has to travel in the record rather than reach for the ambient builder,
/// because `Instance` re-runs the same function with these rebound to staging
/// nets — the factory declares nothing at a use site.
type Ports =
    { inPort: string -> int -> Expr
      outPort: string -> int -> Expr
      inPortAs: string -> GroundType -> Expr
      outPortAs: string -> GroundType -> Expr }

/// A mem handle: enough to address it and type its reads. The array itself lives
/// in the module's decls.
type Mem =
    { memName: string
      addrWidth: int
      memWidth: int
      style: RamStyle }

/// The w-bit pattern reinterpreted as a signed value in 64 bits — the reference
/// semantics for MulS, LtS and Sra, mirroring the emitter's replication. Public
/// because a bit-exact software twin (Mandelbrot.fs) is these semantics too.
let signExtend64 w (v: uint64) =
    if w < 64 && v &&& (1UL <<< (w - 1)) <> 0UL then
        v ||| ~~~(maskOf w)
    else
        v
