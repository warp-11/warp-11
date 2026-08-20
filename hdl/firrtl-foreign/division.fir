; Division and remainder, including by a *signal* — which warp11's own surface
; refuses on purpose (a variable divisor is thirty levels of logic that looks
; free at the call site) but which a foreign design is entitled to say, and
; which the reader must therefore take.
;
; Division by zero is the interesting case here. FIRRTL leaves it undefined and
; Verilog makes it X; warp11's Sim answers zero, and the random stimulus reaches
; a zero divisor constantly, so this file is what holds the two to the same
; answer rather than a comment claiming they agree.
FIRRTL version 4.0.0
circuit Division :
  public module Division :
    input a : UInt<8>
    input b : UInt<8>
    input x : SInt<8>
    input y : SInt<8>
    output q : UInt<8>
    output r : UInt<8>
    output qs : SInt<9>
    output rs : SInt<8>
    output byTen : UInt<8>

    connect q, div(a, b)
    connect r, rem(a, b)
    connect qs, div(x, y)
    connect rs, rem(x, y)
    connect byTen, div(a, UInt<8>(10))
