/// The comms seam: everything the view knows about where frames come from.
/// Two implementations — the local software twin (the GUI's bring-up rig,
/// zero network) and the Zenoh bus (the board daemon). Which one a window
/// gets is a composition-root choice, invisible in the view — the call-site
/// invariance rule, applied to the wire.
module Warp11.MandelView.Bus

/// A rectangle of the complex plane in the fabric's own Q4.28 bit patterns —
/// origin and per-pixel step, the four registers `mandelFrameAxi` latches on
/// start. The view thinks in floats and converts here, so the wire carries
/// exactly what the hardware consumes and nothing re-derives it.
type MandelView =
    { cxOrigin: uint32
      cyOrigin: uint32
      dx: uint32
      dy: uint32 }

/// One frame as published on `warp11/mandel/frame`. `view` is the view this
/// frame ANSWERS, not necessarily the last one requested: the daemon
/// coalesces, so a drag that outran the fabric gets one reply for several
/// puts.
type MandelFrame =
    { view: MandelView
      /// Fabric cycles for the render. 0 when the producer has no fabric —
      /// the software twin does not invent one.
      cycles: uint32
      width: int
      height: int
      maxIter: int
      /// Escape counts, row-major, one byte per pixel, already cropped of
      /// the fabric's row padding.
      pixels: byte[] }

type IMandelBus =
    inherit System.IDisposable
    /// Fires on whatever thread the transport owns — the view marshals.
    [<CLIEvent>]
    abstract FrameReceived: IEvent<MandelFrame>
    abstract Render: view: MandelView -> unit
    /// The fabric clock, for turning cycles into milliseconds. 0.0 when the
    /// producer is software, and the view then reports the round trip alone
    /// rather than dressing a twin up as an accelerator.
    abstract FabricHz: float
    /// What is behind the bus, for the status line. The demo's numbers mean
    /// nothing without it.
    abstract Describe: string

// ---- Q4.28, the format the fabric latches ----

[<Literal>]
let FracBits = 28

let private scale = float (1 <<< FracBits)

/// Q4.28 is signed in 32 bits: four integer bits, so the representable range
/// is [-8, 8) — comfortably the whole set, and the reason the view clamps
/// rather than letting a pan wander off the format.
let toQ (v: float) : uint32 = uint32 (int32 (v * scale))

let ofQ (bits: uint32) : float = float (int32 bits) / scale

// ---- the wire, as `runtime/mandel-daemon` defines it ----

let private headerWords = 8
let private headerBytes = headerWords * 4

let decodeFrame (payload: byte[]) : MandelFrame option =
    if payload.Length < headerBytes then
        None
    else
        let word i = System.BitConverter.ToUInt32(payload, i * 4)
        let width = int (word 5)
        let height = int (word 6)

        if width <= 0 || height <= 0 || payload.Length <> headerBytes + width * height then
            None
        else
            Some
                { view =
                    { cxOrigin = word 0
                      cyOrigin = word 1
                      dx = word 2
                      dy = word 3 }
                  cycles = word 4
                  width = width
                  height = height
                  maxIter = int (word 7)
                  pixels = payload[headerBytes..] }

let encodeView (view: MandelView) : byte[] =
    [| for word in [ view.cxOrigin; view.cyOrigin; view.dx; view.dy ] do
           yield! System.BitConverter.GetBytes word |]
