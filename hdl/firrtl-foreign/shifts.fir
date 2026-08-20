; Dynamic shifts — the amount is a signal, so this is a barrel shifter rather
; than a rewiring. Written here in FIRRTL's own widths: a dshl keeps every bit
; it could produce (8 + 2^3 - 1 = 15), a dshr keeps its operand's width, and a
; dshr on a signed operand fills with the sign.
;
; The shift amount reaches 7 on an 8-bit value, so the shifted-out cases are
; covered rather than assumed, and the two right shifts part company on every
; negative input.
FIRRTL version 4.0.0
circuit Shifts :
  public module Shifts :
    input a : UInt<8>
    input s : SInt<8>
    input n : UInt<3>
    output wide : UInt<15>
    output logical : UInt<8>
    output arithmetic : SInt<8>
    output roundTrip : UInt<8>

    connect wide, dshl(a, n)
    connect logical, dshr(a, n)
    connect arithmetic, dshr(s, n)
    ; shifted up and back down: everything below the amount is gone
    connect roundTrip, bits(dshl(a, n), 7, 0)
