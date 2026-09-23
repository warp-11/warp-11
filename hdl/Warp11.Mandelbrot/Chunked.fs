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
open Warp11.Mandelbrot.Lane

/// The design as a board top takes it.
let mandelChunks (name: string) (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : BoardTop.Design =
    let def = mandelChunksDef name width pixels maxIter fracBits threads lanes

    { name = name
      sampleRate = 0.0
      streams = 1
      needs =
        [ yield BoardTop.streamIn "beats" [ beatPin ]
          yield BoardTop.streamOut "pixels" [ pixelsPin ]
          for name, format in viewControls fracBits -> BoardTop.valueIn name format ]
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
              ports = [ "cxOrigin", cxOrigin; "cyOrigin", cyOrigin; "dx", dx; "dy", dy ]
              readbacks = [] } }

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
    let top = BoardTop.boardTop kv260 viaCount design
    let batch = top.batch.Value
    let sim = Sim top.top
    // Write-only: the counted top reads nothing, so it has no read channel.
    let chunks = chunkedWidth pixels width / pixels
    let beats = height * chunks
    let dst = 0x8000
    // The behavioural DDR is sized from the frame rather than fixed, so a
    // full-size render does not run off the end of it.
    let ddr = SimAxiWriteSlave(sim, dst + beats * pixels + 0x1000, dataBytes = 16, awEvery = 3, bDelay = 6)
    let axi = SimAxi.clientWith sim ddr.Cycle
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

/// The scattered design as a board top takes it: the same boundary as
/// `mandelChunks` with one field added — the beat's destination — which is
/// what lets everything under it run in no order.
let mandelScatter (name: string) (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : BoardTop.Design =
    let def = mandelScatterDef name width pixels maxIter fracBits threads lanes

    { name = name
      sampleRate = 0.0
      streams = 1
      needs =
        [ yield BoardTop.streamIn "beats" [ beatPin ]
          // The destination rides at the head of the output row: the scatter
          // sink reads it there and the rest is the payload.
          yield BoardTop.streamOut "pixels" [ indexPin; pixelsPin ]
          for name, format in viewControls fracBits -> BoardTop.valueIn name format ]
      answers = pixels / pixelsPerBeat
      rig =
        fun instance ->
            let in1, out1, cxOrigin, cyOrigin, dx, dy = def.NewNamed instance

            { through =
                fun s ->
                    s
                    |> streamMapTo (pins1 beatPin) List.head
                    |> streamThroughInstance in1 out1
                    |> streamMapTo (layoutOfList [ indexPin; pixelsPin ]) (fun (index, px) -> [ index; px ])
              ports = [ "cxOrigin", cxOrigin; "cyOrigin", cyOrigin; "dx", dx; "dy", dy ]
              readbacks = [] } }

/// A frame through the **scattered** counted top in the Sim, driven exactly
/// as `renderThroughBoardTop` drives the ordered one — same registers, same
/// behavioural DDR, same paced write slave — so the two are comparable at the
/// cycle.
let renderThroughScatterTop (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) (view: uint64 * uint64 * uint64 * uint64) =
    let design = mandelScatter "MandelScatter" width pixels maxIter fracBits threads lanes
    let top = BoardTop.boardTop kv260 viaScatter design
    let batch = top.batch.Value
    let sim = Sim top.top
    let chunks = chunkedWidth pixels width / pixels
    let beats = height * chunks
    let dst = 0x8000
    // The behavioural DDR is sized from the frame rather than fixed, so a
    // full-size render does not run off the end of it.
    let ddr = SimAxiWriteSlave(sim, dst + beats * pixels + 0x1000, dataBytes = 16, awEvery = 3, bDelay = 6)
    let axi = SimAxi.clientWith sim ddr.Cycle
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

/// The example's silicon shape, said once. The frame is the README's; the
/// chunk is the width the farm was measured at (128 px is −6% cycles against
/// the row design, 16 px is +39%); the clock is the one the frame design
/// proved at 104 lanes with timing to spare. `lanes` is the only dial.
let frameWidth = 1400

let chunkPixels = 128

let maxIterations = 256

let viewFracBits = 28

let laneThreads = 8

let fabricHz = 166_666_672

/// Every lane the part's 1,248 DSPs hold, at twelve DSPs a lane.
let siliconLanes = 104

/// The example on the KV260 as the directory its toolchain builds from,
/// straight from the typed design: the counted path, so the host asks for a
/// count of beats and the frame lands in its memory. There is no design file
/// in the way — this is the whole path from the code to the bitstream.
let buildDirectory (dir: string) (lanes: int) =
    mandelChunks "Mandelbrot" frameWidth chunkPixels maxIterations viewFracBits laneThreads lanes
    |> BoardTop.boardTop (kv260At fabricHz) viaCount
    |> Warp11.Build.write dir

/// The same, on the **scatter** path: every beat carrying where it goes, so
/// the lanes run in no order. Same board, same registers, same driver — the
/// frame comes back the same way, because where a beat lands is the design's
/// business rather than the host's.
/// Named apart from the counted design on purpose: the two then package as
/// separate apps, so a board can hold both and the A/B is a `loadapp` rather
/// than a rebuild.
let buildScatterDirectory (dir: string) (lanes: int) =
    mandelScatter "MandelbrotScatter" frameWidth chunkPixels maxIterations viewFracBits laneThreads lanes
    |> BoardTop.boardTop (kv260At fabricHz) viaScatter
    |> Warp11.Build.write dir
