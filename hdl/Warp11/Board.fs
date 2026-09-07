/// What a design is being built *for*: the clock it will run at, and how a
/// host reaches it.
///
/// This exists because two kinds of number were previously written down in
/// several places at once and could disagree. The fabric clock frequency
/// decides every derived rate — an I2S sample rate, a baud rate, a timer — and
/// a design moved to a board with a different clock keeps its old divisors and
/// runs at the wrong rate with nothing complaining. The host binding — where
/// the register aperture sits, and on which bus — is restated today in the
/// block design's Tcl and in the device tree, neither of which is generated.
///
/// **A `Board` is passed, not ambient.** SpinalHDL puts frequency on the clock
/// domain and reads it from an implicit `ClockDomain.current`, which is the most
/// developed answer in the field and is deliberately not what this is: an
/// ambient stack is a second piece of hidden elaboration state beside the
/// builder's, and the number of things that actually need a frequency is small
/// enough that handing it over is no burden. If that stops being true, this is
/// the type an ambient answer would carry.
///
/// **What it deliberately does not hold.** The register map's aperture is a
/// property of the *design*, not the board — the same map generates the same
/// Rust layout on either bus, which is what lets a driver survive the move. A
/// build-script generator computes the address range it needs from the design's
/// `RegMap` and takes only the base from here. See `notes/DEVICES.md`, which is
/// where the general per-board mapping is designed; this type is the part of it
/// that two callers already need, built now rather than in anticipation.
[<AutoOpen>]
module Warp11.Boards

/// How the host reaches the design's registers.
///
/// A union rather than an optional base address because a generator has to
/// handle both, and "no base address means SPI" is a convention a reader would
/// have to be told. The two cases are the two real targets: the KV260's
/// memory-mapped AXI-Lite slave, and a part with no AXI at all.
///
/// The cases are `AxiLiteAt`/`SpiBus` rather than `AxiLite`/`Spi` because
/// `Warp11.AxiLite` is an `AutoOpen` module: a bare `AxiLite` case would be in
/// scope everywhere the module name is, which is a collision waiting to
/// confuse rather than one the compiler catches.
type HostBus =
    /// A memory-mapped AXI-Lite aperture at this base. The *size* of the
    /// aperture is not here: it comes from the design's register map.
    | AxiLiteAt of baseAddr: uint64
    /// Registers over SPI, for parts with no bus — the seam
    /// `notes/DEVICES.md` plans as the third consumer of one `RegMap`.
    | SpiBus

/// One target: what to call it, how fast its fabric clock runs, and how a host
/// talks to it.
type Board =
    { /// The board's name, as the generated scaffolding will refer to it.
      name: string
      /// The fabric clock, in hertz. Every derived rate divides this.
      fabricHz: int
      /// How the host reaches the registers.
      hostBus: HostBus }

/// The KV260, as every Warp 11 app on it is configured: a 100 MHz PL clock and
/// the AXI-Lite aperture at 0xB0000000, which is where every Warp 11 slave app
/// on this board lives.
let kv260 =
    { name = "kv260"
      fabricHz = 100_000_000
      hostBus = AxiLiteAt 0xB0000000UL }

/// The iCEBreaker's 12 MHz crystal. No AXI on this part — which is what makes
/// it the useful second board: it is the one that catches an assumption about
/// the KV260 baked into something generic.
let iceBreaker =
    { name = "icebreaker"
      fabricHz = 12_000_000
      hostBus = SpiBus }
