; A memory with two readers on one array, which warp11's own emitter never
; produces in this shape — it names readers by the address expression, so two
; different addresses is the case worth pushing on. Write and read collide on
; purpose, so read-under-write semantics show up in the trace.
FIRRTL version 4.0.0
circuit Memory :
  public module Memory :
    input clock : Clock
    input reset : UInt<1>
    input waddr : UInt<3>
    input wdata : UInt<8>
    input wen : UInt<1>
    input raddr : UInt<3>
    output out : UInt<8>
    output alsoOut : UInt<8>

    mem store :
      data-type => UInt<8>
      depth => 8
      read-latency => 0
      write-latency => 1
      reader => r0
      reader => r1
      writer => w
      read-under-write => old

    connect store.r0.addr, raddr
    connect store.r0.en, UInt<1>(1)
    connect store.r0.clk, clock
    connect store.r1.addr, waddr
    connect store.r1.en, UInt<1>(1)
    connect store.r1.clk, clock
    connect store.w.addr, waddr
    connect store.w.en, wen
    connect store.w.clk, clock
    connect store.w.data, wdata
    connect store.w.mask, UInt<1>(1)
    connect out, store.r0.data
    connect alsoOut, store.r1.data
