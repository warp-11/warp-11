; Two modules and an instance, with the child's ports driven directly from the
; parent's expressions rather than through the staging wires warp11's own
; elaboration creates. The connects are also written in reverse dependency
; order — a wire read above its own connect — because FIRRTL's connects are
; dataflow, not sequential, and a reader that assumed otherwise would be wrong.
FIRRTL version 4.0.0
circuit Hierarchy :
  module Inner :
    input x : UInt<8>
    input y : UInt<8>
    output z : UInt<9>

    connect z, add(x, y)

  public module Hierarchy :
    input a : UInt<8>
    input b : UInt<8>
    output total : UInt<9>
    output echo : UInt<8>

    wire scaled : UInt<8>

    inst adder of Inner
    connect total, adder.z
    connect adder.x, scaled
    connect adder.y, b
    connect echo, scaled
    connect scaled, and(a, UInt<8>(254))
