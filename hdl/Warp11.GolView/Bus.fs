/// The comms seam: everything the view knows about where frames come from.
/// Two implementations — the local simulated bus (the software twin on a
/// timer; zero network, the GUI's bring-up rig) and the Zenoh bus (the
/// board daemon). Which one a window gets is a composition-root choice,
/// invisible in the view — the call-site invariance rule, applied to the
/// wire.
module Warp11.GolView.Bus

/// One frame as published on `warp11/gol/frame`: 64 rows, bit x of row y =
/// cell (y, x), plus the counters the fabric keeps.
type GolFrame =
    { generation: uint32
      population: uint32
      rows: uint64[] }

type IGolBus =
    inherit System.IDisposable
    /// Fires on whatever thread the transport owns — the view marshals.
    [<CLIEvent>]
    abstract FrameReceived: IEvent<GolFrame>
    abstract Load: rows: uint64[] -> unit
    abstract Run: gensPerSec: uint32 -> unit
    abstract Stop: unit -> unit
    abstract Reset: unit -> unit

/// The frame wire format, shared by both buses: header then rows, all LE.
let decodeFrame (payload: byte[]) : GolFrame option =
    if payload.Length <> 8 + 512 then
        None
    else
        Some
            { generation = System.BitConverter.ToUInt32(payload, 0)
              population = System.BitConverter.ToUInt32(payload, 4)
              rows = [| for y in 0..63 -> System.BitConverter.ToUInt64(payload, 8 + y * 8) |] }

let encodeRows (rows: uint64[]) : byte[] =
    [| for row in rows do
           yield! System.BitConverter.GetBytes row |]
