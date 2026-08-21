/// Numbers: how to read a bag of bits as one.
///
/// Three attributes, none of which vary together — a width, a count of fraction
/// bits, and whether the top bit is a sign. An integer is the `fracBits = 0`
/// case, fixed-point the general one, and signed and unsigned are a flag rather
/// than two type systems.
///
/// Signedness is not this layer's to enforce: the IR's ground types carry it, so
/// `signed` here *types the bits* and multiply and compare are then just multiply
/// and compare. What remains is the part the IR has no opinion about — where the
/// binary point sits — which is why `renormTo`, `saturateTo` and `resize` are
/// still the interesting functions.
///
/// Not auto-opened: `input` and `wire` here are deliberately the DSL's names,
/// because a design working in numbers wants to declare them the way it always
/// did. `open Warp11.Number` in the files that work in numbers, and the two
/// vocabularies stay apart everywhere else.
module Warp11.Number

/// The description. Everything here is checked at elaboration, which is this
/// system's build step — the same treatment widths have always had.
type NumberFormat =
    { totalWidth: int
      fracBits: int
      signed: bool }

/// A value and how to read it.
type Number =
    { bits: Expr
      format: NumberFormat }

let private describe (f: NumberFormat) =
    let sign = if f.signed then "signed" else "unsigned"
    $"%d{f.totalWidth}w/%d{f.fracBits}f/{sign}"

let private sameFormat op (a: NumberFormat) (b: NumberFormat) =
    if a <> b then
        failwith $"{op}: operand formats disagree — {describe a} vs {describe b}"

/// The product's format: widths add (the full product, exact) and fraction bits
/// add. Signedness is not a thing that can be averaged, so a mixed product is an
/// error rather than a guess.
let productFormat (a: NumberFormat) (b: NumberFormat) =
    if a.signed <> b.signed then
        failwith "(*): one operand is signed and the other is not — say which the product is"

    { totalWidth = a.totalWidth + b.totalWidth
      fracBits = a.fracBits + b.fracBits
      signed = a.signed }

/// Arithmetic on numbers. Every operation checks the operands' formats agree
/// and hands back the format the result actually has — which for `*` is wider
/// than either operand, because the full product is exact and narrowing it is
/// a decision `renormTo` or `saturateTo` makes deliberately.
type Number with

    /// Add is add. Two's complement makes the gates identical either way, so
    /// signedness never enters here — this is a lift over the IR, not a
    /// duplicate of it.
    static member (+)(a: Number, b: Number) : Number =
        sameFormat "(+)" a.format b.format
        { a with bits = a.bits + b.bits }

    /// Subtract, on the same argument as add.
    static member (-)(a: Number, b: Number) : Number =
        sameFormat "(-)" a.format b.format
        { a with bits = a.bits - b.bits }

    /// Multiply is multiply — the operands are typed, so which extension the
    /// product needs is settled a level down.
    static member ( * )(a: Number, b: Number) : Number =
        { bits = mul a.bits b.bits
          format = productFormat a.format b.format }

/// The ground type a format's bits are read at. The one place the two systems
/// touch: everything above is fraction bits, everything below is UInt/SInt.
let groundType (fmt: NumberFormat) =
    if fmt.signed then SInt fmt.totalWidth else UInt fmt.totalWidth

/// Type a raw Expr at a format — the boundary constructor, for bits arriving
/// from ports, mems or streams, none of which carry an interpretation.
let ofBits (fmt: NumberFormat) (e: Expr) : Number =
    if width e <> fmt.totalWidth then
        failwith $"Number.ofBits: %d{width e}-bit value typed at a %d{fmt.totalWidth}-bit format"

    let bits =
        if isSigned e = fmt.signed then e
        elif fmt.signed then asSInt e
        else asUInt e

    { bits = bits; format = fmt }

/// A real number as bits at a format: round, range-check, mask to width. The
/// range checked is the format's own — which is the point of carrying
/// signedness, since `12.0` fits an unsigned Q4.4 and not a signed one.
let constant (fmt: NumberFormat) (value: float) : Number =
    let scaled = System.Math.Round(value * float (1L <<< fmt.fracBits))

    let low, high =
        if fmt.signed then
            -(2.0 ** float (fmt.totalWidth - 1)), 2.0 ** float (fmt.totalWidth - 1) - 1.0
        else
            0.0, 2.0 ** float fmt.totalWidth - 1.0

    if scaled < low || scaled > high then
        failwith $"Number.constant: %g{value} does not fit %d{fmt.totalWidth}w/%d{fmt.fracBits}f (scaled %g{scaled})"

    ofBits fmt (lit (uint64 (int64 scaled) &&& maskOf fmt.totalWidth) fmt.totalWidth)

/// Ordering, likewise: the operands say how they are to be read.
let lessThan (a: Number) (b: Number) : Expr =
    sameFormat "lessThan" a.format b.format
    lt a.bits b.bits

/// The same comparison with the operands the other way round.
let greaterThan (a: Number) (b: Number) : Expr = lessThan b a

/// Equality needs no variant: the bits are equal or they are not.
let equalTo (a: Number) (b: Number) : Expr =
    sameFormat "equalTo" a.format b.format
    eq a.bits b.bits

/// Shifting right. Not about signedness any more — `shr` reads that from the
/// value — but about *width*: the signed form keeps it (`sra`), the unsigned one
/// narrows, and that difference is a real choice about what the caller wants
/// back, so it stays here.
let shiftRight (by: int) (x: Number) : Number =
    if x.format.signed then
        { x with bits = sra by x.bits }
    else
        { bits = shr by x.bits
          format = { x.format with totalWidth = x.format.totalWidth - by } }

/// Clamping to a narrower width, at the range the value's own type describes.
let saturateTo (toWidth: int) (x: Number) : Number =
    { bits = saturate toWidth x.bits
      format = { x.format with totalWidth = toWidth } }

/// Widening. Was the one choice that was not even a function, just an idiom —
/// sign bit replicated or zeros padded, and which is right is a property of the
/// value. Now that the value says, it is `pad`.
let resize (toWidth: int) (x: Number) : Number =
    if toWidth < x.format.totalWidth then
        failwith $"Number.resize to %d{toWidth} narrows a %d{x.format.totalWidth}-bit value — saturateTo or renormTo instead"

    { bits = pad toWidth x.bits
      format = { x.format with totalWidth = toWidth } }

/// Drop fraction bits and narrow, in one part-select of the source — the
/// renormalization after a multiply. The target format names what the slice
/// means, and truncates toward negative infinity. Slice rule: the source must be
/// a declared signal (`Number.wire` it first).
let renormTo (target: NumberFormat) (x: Number) : Number =
    let dropped = x.format.fracBits - target.fracBits

    if dropped < 0 then
        failwith $"renormTo: cannot invent fraction bits (%d{x.format.fracBits}f -> %d{target.fracBits}f)"

    let hi = dropped + target.totalWidth - 1

    if hi >= x.format.totalWidth then
        failwith
            $"renormTo: target %d{target.totalWidth}w/%d{target.fracBits}f needs bit %d{hi} of a %d{x.format.totalWidth}-bit source"

    ofBits target (slice hi dropped x.bits)

/// The same bits under a different format of the same width — zero hardware.
/// Claiming k fewer fraction bits multiplies the represented value by 2^k, which
/// is how a doubling costs nothing.
let reinterpret (target: NumberFormat) (x: Number) : Number =
    if x.format.totalWidth <> target.totalWidth then
        failwith
            $"reinterpret: widths must match (%d{x.format.totalWidth} vs %d{target.totalWidth}) — reinterpretation moves no bits"

    ofBits target x.bits

/// A design input at a format. The port is declared signed or not, so the
/// interpretation survives into the IR rather than being reapplied at each use.
let input name (fmt: NumberFormat) : Number = ofBits fmt (Dsl.input name (groundType fmt))

/// Land a value in a named wire, keeping its format. This is how a computed
/// value becomes multiplicable and renormalizable — `*` and `renormTo` need
/// declared signals underneath (the slice rule).
let wire name (x: Number) : Number =
    let w = Dsl.wire name (groundType x.format)
    x.bits ==> w
    { x with bits = w }

/// Format witnesses. One line each, binding the three numbers together — the
/// trust point, and everything downstream of it is checked.
let signedInt w : NumberFormat = { totalWidth = w; fracBits = 0; signed = true }
/// An unsigned integer of `w` bits.
let unsignedInt w : NumberFormat = { totalWidth = w; fracBits = 0; signed = false }

/// A signed fixed-point format: `totalWidth` bits, `fracBits` of them below the
/// binary point.
let signedFixed totalWidth fracBits : NumberFormat =
    { totalWidth = totalWidth; fracBits = fracBits; signed = true }

/// The unsigned fixed-point format.
let unsignedFixed totalWidth fracBits : NumberFormat =
    { totalWidth = totalWidth; fracBits = fracBits; signed = false }

/// The formats this codebase actually uses, named the way the literature names
/// them. `q4_4` is 8 bits with 4 below the point — the audio gain format.
let q4_4 = signedFixed 8 4
/// 16 bits, 7 below the point — an audio sample.
let q9_7 = signedFixed 16 7
/// 32 bits, 28 below the point — Mandelbrot's coordinate format.
let q4_28 = signedFixed 32 28
/// 64 bits, 55 below the point.
let q9_55 = signedFixed 64 55
/// What a `q4_4` product *is*, rather than what a caller wishes it were. Stated
/// as the product so the two cannot disagree.
let q8_8 = productFormat q4_4 q4_4
/// Likewise for `q4_28`.
let q8_56 = productFormat q4_28 q4_28
