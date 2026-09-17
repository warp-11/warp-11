/// The frame as the canvas draws it: the raster as a stream of chunk beats,
/// `coords` minting each chunk's view and `mandel16` spent `lanes` times
/// over them — the typed twin of the four-box design, assembled with the
/// same placement the canvas uses, so the two meet at the bytes. The
/// boundary is the batch contract's: the host asks for `height × chunks`
/// beats and reads the frame back in raster order, no addresses on the
/// beats and no window.
module Warp11.Mandelbrot.Chunked

open System.Numerics
open Warp11
open Warp11.Fu
open Warp11.Mandel

/// The design's pins, as the boundary sees them.
let beatPin = "beat", unsignedInt 32
let pixelsPin = "pixels", pixelsFormat

let viewControls (fracBits: int) =
    let view = viewFormat fracBits
    [ "cxOrigin", view; "cyOrigin", view; "dx", view; "dy", view ]

/// The design: beats in, chunk views, the farmed lane, pixels out.
let mandelChunksDef (name: string) (width: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) =
    let coords = coords width fracBits
    let lane = copies lanes (mandel16 maxIter fracBits threads)
    let view = viewFormat fracBits

    defModule
        name
        (fun p ->
            streamInputPorts p "in1" (pins1 beatPin),
            streamOutputPorts p "out1" (pins1 pixelsPin),
            p.inPort "cxOrigin" 32,
            p.inPort "cyOrigin" 32,
            p.inPort "dx" 32,
            p.inPort "dy" 32)
        (fun (in1, out1, cxOrigin, cyOrigin, dx, dy) ->
            [ streamSource in1 ]
            |> fuStagesWith [ cxOrigin; cyOrigin; dx; dy ] coords "coords" (pins3 ("cx0", view) ("cy", view) ("dx", view)) id (fun r _ -> r)
            |> fuStagesWith [] lane "mandel" (pins1 pixelsPin) id (fun r _ -> r)
            |> List.iter2 streamSink [ out1 ])

/// The design as a board top takes it.
let mandelChunks (name: string) (width: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : BoardTop.Design =
    let def = mandelChunksDef name width maxIter fracBits threads lanes

    { name = name
      sampleRate = 0.0
      streams = 1
      inputs = [ beatPin ]
      outputs = [ pixelsPin ]
      controls = viewControls fracBits
      starting = []
      rig =
        fun instance ->
            let in1, out1, cxOrigin, cyOrigin, dx, dy = def.NewNamed instance

            { through =
                fun s ->
                    s
                    |> streamMapTo (pins1 beatPin) List.head
                    |> streamThroughInstance in1 out1
                    |> streamMapTo (layoutOfList [ pixelsPin ]) List.singleton
              ports = [ "cxOrigin", cxOrigin; "cyOrigin", cyOrigin; "dx", dx; "dy", dy ] } }

/// A frame through the design in the Sim: beats `0 .. height × chunks - 1`
/// offered as fast as they are taken, the pixels collected in the order
/// they leave — the raster, if the farm keeps order — as one byte a pixel.
let renderInSim (width: int) (height: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) (view: uint64 * uint64 * uint64 * uint64) (cycleLimit: int) =
    let sim = Sim (mandelChunksDef "MandelChunksSim" width maxIter fracBits threads lanes).def
    let chunks = paddedWidth width / pixelsPerBeat
    let beats = height * chunks
    let cxOrigin, cyOrigin, dx, dy = view
    sim.Poke("cxOrigin", cxOrigin)
    sim.Poke("cyOrigin", cyOrigin)
    sim.Poke("dx", dx)
    sim.Poke("dy", dy)
    sim.Poke("out1_ready", 1UL)
    let pixels = ResizeArray<byte>()
    let mutable offered = 0
    let mutable cycles = 0

    while pixels.Count < beats * pixelsPerBeat && cycles < cycleLimit do
        sim.Poke("in1_valid", (if offered < beats then 1UL else 0UL))
        sim.Poke("in1_beat", uint64 offered)
        let accepted = offered < beats && sim.Peek "in1_ready" = 1UL

        if sim.Peek "out1_valid" = 1UL then
            let beat = sim.PeekWide "out1_pixels"

            for j in 0 .. pixelsPerBeat - 1 do
                pixels.Add(byte ((beat >>> (j * 8)) &&& BigInteger 255))

        sim.Tick()
        cycles <- cycles + 1
        if accepted then offered <- offered + 1

    pixels.ToArray(), cycles

/// The same frame from the whole-pixel twin, pixel by pixel.
let renderTwin (width: int) (height: int) (maxIter: int) (fracBits: int) (view: uint64 * uint64 * uint64 * uint64) =
    let widthPadded = paddedWidth width
    let cxOrigin, cyOrigin, dx, dy = view

    [| for r in 0 .. height - 1 do
           for c in 0 .. widthPadded - 1 ->
               let cx = (cxOrigin + uint64 c * dx) &&& 0xFFFFFFFFUL
               let cy = (cyOrigin + uint64 r * dy) &&& 0xFFFFFFFFUL
               byte (laneTwin fracBits maxIter cx cy) |]
