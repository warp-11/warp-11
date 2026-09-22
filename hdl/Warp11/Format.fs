/// How a bag of bits is read as one number: a width, a count of fraction
/// bits, and whether the top bit is a sign. An integer is the `fracBits = 0`
/// case, fixed-point the general one, and signed and unsigned are a flag
/// rather than two type systems. Everything here is checked at elaboration.
///
/// Ahead of `Layout.fs` because a layout's fields carry their format — a
/// design's pins say how they are read, not only how wide they are. The
/// arithmetic over numbers is `Number.fs`, later, where the DSL's ports are.
[<AutoOpen>]
module Warp11.Format

type NumberFormat =
    { totalWidth: int
      fracBits: int
      signed: bool }

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

/// `24w/0f/signed` — the format as a message names it.
let describeFormat (f: NumberFormat) =
    let sign = if f.signed then "signed" else "unsigned"
    $"%d{f.totalWidth}w/%d{f.fracBits}f/{sign}"

/// A boundary's pins as a person reads them — what a refusal names when a
/// design's boundary is not what a device or a board needs.
let describePins (pins: (string * NumberFormat) list) =
    pins |> List.map (fun (n, f) -> $"{n}: {describeFormat f}") |> String.concat ", "
