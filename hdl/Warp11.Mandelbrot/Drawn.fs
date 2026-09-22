/// The drawn frame held to the typed one: the check that the four boxes on
/// the canvas and `Lane.mandelChunksDef` are the same design. It lives here
/// rather than with the placement's checks because the units it names are
/// this project's — which is the whole point of a project bringing its own.
module Warp11.Mandelbrot.Drawn

open Warp11
open Warp11.Graph
open Warp11.Elaborate
open Warp11.Mandelbrot

// UD22 — The Mandelbrot frame, drawn. The four boxes — a count in, `coords`,
// `mandelChunk` spent three times, pixels out — elaborate to the bytes of the
// typed `mandelChunksDef` assembled with the same placement; the frame the
// drawn design renders in the Sim from a count is the whole-pixel twin's,
// pixel for pixel; the export names the units and the counted path; and
// the counted KV260 top takes the drawn design as it takes the typed one.

// CHECK
let mandelbrotDrawn () : bool =
    let g = Example.mandelbrot 32 32 48 28 8 3
    let typed = Lane.mandelChunksDef "Mandelbrot" 32 32 48 28 8 3
    let sameBytes = emitDesign (elaborate g).def = emitDesign typed.def

    let toQ (v: float) = uint64 (int64 (v * 268435456.0)) &&& 0xFFFFFFFFUL
    let view = [ "cxOrigin", toQ -2.0; "cyOrigin", toQ 0.5; "dx", toQ 0.25; "dy", toQ -0.25 ]

    let frame =
        Warp11.Devices.runFrameInSim 20_000 g { width = 32; height = 4; controls = view; outputPath = None }

    let twin =
        [| for r in 0 .. 3 do
               for c in 0 .. 31 ->
                   let cx = (toQ -2.0 + uint64 c * toQ 0.25) &&& 0xFFFFFFFFUL
                   let cy = (toQ 0.5 + uint64 r * toQ -0.25) &&& 0xFFFFFFFFUL
                   byte (Lane.laneTwin 28 48 cx cy) |]

    let exported =
        match Warp11.Export.exportWith (Some Example.mandelbrotMapping) { g with mapping = Some "mandelbrot.kv260.json" } with
        | Ok text -> text
        | Error why -> failwith why

    let top = Warp11.Elaborate.boardTopOf kv260 Counted g

    sameBytes
    && frame.width = 32
    && frame.height = 4
    && frame.pixels = twin
    && exported.Contains "mandelChunk 32 48 28 8"
    && exported.Contains "coords 32 32 28"
    && exported.Contains "copies 3"
    && exported.Contains "let path = Counted"
    && top.name = "MandelbrotBatch"
    && top.batch.IsSome
