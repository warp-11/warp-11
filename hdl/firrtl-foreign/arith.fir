; Arithmetic as FIRRTL itself defines it, in shapes warp11's emitter never
; writes: a bare widening `add` with no `tail` around it, `pad`, `shl`, `neg`,
; `cvt`, the comparison operators we express as `not(lt(..))`, and literals in
; hex. Every width here is FIRRTL's own inference rule rather than ours, which
; is the point — if the reader has those rules wrong, the trace diverges.
FIRRTL version 4.0.0
circuit Arith :
  public module Arith :
    input a : UInt<8>
    input b : UInt<8>
    output sum : UInt<9>
    output diff : UInt<9>
    output prod : UInt<16>
    output padded : UInt<12>
    output shifted : UInt<11>
    output negated : SInt<9>
    output converted : SInt<9>
    output ge : UInt<1>
    output ne : UInt<1>
    output masked : UInt<8>

    connect sum, add(a, b)
    connect diff, sub(a, b)
    connect prod, mul(a, b)
    connect padded, pad(a, 12)
    connect shifted, shl(a, 3)
    connect negated, neg(a)
    connect converted, cvt(a)
    connect ge, geq(a, b)
    connect ne, neq(a, b)
    connect masked, and(a, UInt<8>(240))
