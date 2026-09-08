/// Something attached to a running design's pins.
///
/// **Two halves rather than one `Tick`, because the device does not own the
/// clock.** A test loop, the debugger's run loop and `SimAxi`'s `advance` all
/// want to be the thing that calls `sim.Tick()`; a device that ticked for them
/// could not be embedded in any of them. So a device says what to put on the
/// design's inputs before the cycle, and what to read off its outputs after,
/// and whoever owns the loop puts the tick between.
///
/// The ordering is the whole contract: `Drive` writes, the cycle happens,
/// `Sample` reads. A device that read in `Drive` would be reading last cycle's
/// values, which is the bug this shape exists to make unwriteable.
[<AutoOpen>]
module Warp11.SimDevice

type ISimDevice =
    /// Before the tick: put this cycle's values on the design's inputs.
    abstract Drive: unit -> unit
    /// After the tick: read the design's outputs, and work out what the next
    /// `Drive` should put on the wire.
    abstract Sample: unit -> unit
