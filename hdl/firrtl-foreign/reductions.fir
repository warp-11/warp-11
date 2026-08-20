; Whole-value folds — FIRRTL's andr/orr/xorr, which warp11 spells anyBitSet,
; allBitsSet and parity. All three return one bit whatever they were handed, so
; the interesting cases are the wide operand, the signed one (where the sign bit
; is just another bit to fold), and a fold over something computed rather than
; over a port.
FIRRTL version 4.0.0
circuit Reductions :
  public module Reductions :
    input a : UInt<8>
    input b : UInt<8>
    input s : SInt<8>
    output anyA : UInt<1>
    output allA : UInt<1>
    output oddA : UInt<1>
    output oddSigned : UInt<1>
    output anyDiff : UInt<1>

    connect anyA, orr(a)
    connect allA, andr(a)
    connect oddA, xorr(a)
    connect oddSigned, xorr(s)
    ; a fold over a computed value: nonzero exactly when the two differ
    connect anyDiff, orr(xor(a, b))
