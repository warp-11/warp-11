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

/// The design as a board top takes it.
let mandelChunks (name: string) (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : BoardTop.Design =
    let def = mandelChunksDef name width pixels maxIter fracBits threads lanes

    { name = name
      sampleRate = 0.0
      streams = 1
      inputs = [ beatPin ]
      outputs = [ pixelsPin ]
      controls = viewControls fracBits
      starting = []
      answers = pixels / pixelsPerBeat
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
let renderInSim (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) (view: uint64 * uint64 * uint64 * uint64) (cycleLimit: int) =
    let sim = Sim (mandelChunksDef "MandelChunksSim" width pixels maxIter fracBits threads lanes).def
    let chunks = chunkedWidth pixels width / pixels
    let beats = height * chunks
    let cxOrigin, cyOrigin, dx, dy = view
    sim.Poke("cxOrigin", cxOrigin)
    sim.Poke("cyOrigin", cyOrigin)
    sim.Poke("dx", dx)
    sim.Poke("dy", dy)
    sim.Poke("out1_ready", 1UL)
    let pixelsOut = ResizeArray<byte>()
    let mutable offered = 0
    let mutable cycles = 0

    while pixelsOut.Count < beats * pixels && cycles < cycleLimit do
        sim.Poke("in1_valid", (if offered < beats then 1UL else 0UL))
        sim.Poke("in1_beat", uint64 offered)
        let accepted = offered < beats && sim.Peek "in1_ready" = 1UL

        if sim.Peek "out1_valid" = 1UL then
            let beat = sim.PeekWide "out1_pixels"

            for j in 0 .. pixelsPerBeat - 1 do
                pixelsOut.Add(byte ((beat >>> (j * 8)) &&& BigInteger 255))

        sim.Tick()
        cycles <- cycles + 1
        if accepted then offered <- offered + 1

    pixelsOut.ToArray(), cycles

/// The same frame from the whole-pixel twin, pixel by pixel.
let renderTwin (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (view: uint64 * uint64 * uint64 * uint64) =
    let widthPadded = chunkedWidth pixels width
    let cxOrigin, cyOrigin, dx, dy = view

    [| for r in 0 .. height - 1 do
           for c in 0 .. widthPadded - 1 ->
               let cx = (cxOrigin + uint64 c * dx) &&& 0xFFFFFFFFUL
               let cy = (cyOrigin + uint64 r * dy) &&& 0xFFFFFFFFUL
               byte (laneTwin fracBits maxIter cx cy) |]

/// The same frame through the counted board top in the Sim: the batch
/// registers written as the host would — the count, the view, where the
/// frame goes — start pulsed, busy polled, the frame read out of the
/// behavioural DDR. What the driver does on the KV260, against the top the
/// generator builds, before any bitstream exists.
let renderThroughBoardTop (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) (view: uint64 * uint64 * uint64 * uint64) =
    let design = mandelChunks "MandelChunks" width pixels maxIter fracBits threads lanes
    let top = BoardTop.boardTop kv260 Counted design
    let batch = top.batch.Value
    let sim = Sim top.top
    // Write-only: the counted top reads nothing, so it has no read channel.
    let ddr = SimAxiWriteSlave(sim, 0x20000, dataBytes = 16, awEvery = 3, bDelay = 6)
    let axi = SimAxi.clientWith sim ddr.Cycle
    let chunks = chunkedWidth pixels width / pixels
    let beats = height * chunks
    let dst = 0x8000
    let cxOrigin, cyOrigin, dx, dy = view
    let register (name: string) = top.registers |> List.find (fun (n, _) -> n = name) |> snd

    for name, value in [ "cxOrigin", cxOrigin; "cyOrigin", cyOrigin; "dx", dx; "dy", dy ] do
        axi.write32 (register name).offset value

    axi.write32 batch.dstAddr.offset (uint64 dst)
    axi.write32 batch.frameCount.offset (uint64 beats)
    axi.write32 batch.start.offset 1UL
    let mutable spins = 0

    while axi.read32 batch.busy.offset <> 0UL && spins < 400_000 do
        ddr.Cycle()
        spins <- spins + 1

    let frame = ddr.Memory[dst .. dst + beats * pixels - 1]
    frame, spins, top
