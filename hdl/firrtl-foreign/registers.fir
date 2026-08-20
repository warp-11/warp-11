; Registers as FIRRTL declares them, with the reset value written inline on the
; `regreset` rather than reconstructed from an `always` block. Also `node`,
; which warp11's IR has no equivalent for and turns into a wire.
FIRRTL version 4.0.0
circuit Registers :
  public module Registers :
    input clock : Clock
    input reset : UInt<1>
    input step : UInt<1>
    input load : UInt<8>
    output count : UInt<8>
    output doubled : UInt<9>
    output seen : UInt<1>

    regreset counter : UInt<8>, clock, reset, UInt<8>(7)
    regreset flag : UInt<1>, clock, reset, UInt<1>(0)

    node next = mux(step, add(counter, UInt<8>(1)), pad(load, 8))
    connect counter, bits(next, 7, 0)
    connect flag, or(flag, step)
    connect count, counter
    connect doubled, add(counter, counter)
    connect seen, flag
