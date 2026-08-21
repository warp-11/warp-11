/// `==>` again, extended to accept a `Number`.
///
/// The connect operator dispatches on what is being connected — an `Expr` passes
/// through, a bare number takes the target's width — and that dispatch is
/// statically resolved, so the set of things it accepts is fixed where it is
/// written. `Widen` lives in `Ir.fs`, which cannot see `Number`: the layer is
/// above it, and F# does not consider extension members when resolving a
/// statically resolved constraint (measured — it reports the *original*
/// overloads and ignores the extension entirely).
///
/// So the dispatch is restated here, over the three things that can drive a net,
/// and this module is auto-opened so that it shadows the two-case version for
/// everything downstream. The duplication is four lines and buys back the
/// `.bits` at every connection — which was 14 of the 18 escapes in the first
/// ported designs.
[<AutoOpen>]
module Warp11.NumberOperators

open Warp11.Number

/// The three things that can drive a net, resolved statically. Public because
/// `==>` is `inline` and an inline body has to reach whatever it calls.
type Driver =
    | Driver

    static member ($)(Driver, value: Expr) = fun (_: int) -> value
    static member ($)(Driver, value: uint64) = fun (w: int) -> litAt w value

    static member ($)(Driver, value: Number) =
        fun (w: int) ->
            if value.format.totalWidth <> w then
                failwith
                    $"(==>): a %d{value.format.totalWidth}-bit number cannot drive a %d{w}-bit signal — resize it first"

            value.bits

/// Connect: the value, then the signal it drives. The value may be a signal, a
/// bare number that takes the target's width, or a `Number`, which brings its
/// own width and is checked against the target's.
let inline (==>) value (target: Expr) =
    Dsl.connect target ((Driver $ value) (width target))
