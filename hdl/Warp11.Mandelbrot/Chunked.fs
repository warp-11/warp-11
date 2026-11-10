/// The frame: the raster as a stream of chunk beats, `coords` minting each
/// chunk's view and the addressed lane pod spent `lanes` times over them in
/// no order, every beat of pixels carrying the place it belongs. The
/// boundary is the batch contract's counted path: the host asks for
/// `height × chunks` beats and each lands at its place — in the host's
/// memory by address, or placed by the host from the index on a link.
module Warp11.Mandelbrot.Chunked

open System.Numerics
open Warp11
open Warp11.Fu
open Warp11.Mandelbrot.Lane

/// What a host driving the frame has to agree with the fabric about: how
/// wide a row is, how wide a chunk, where an escape count saturates, and the
/// fraction bits of the view it writes. Stated where the fabric is built
/// from them, and read from the seam.
let private frameFacts (width: int) (pixels: int) (maxIter: int) (fracBits: int) =
    [ BoardTop.fact "frameWidth" (uint64 width)
      BoardTop.fact "chunkPixels" (uint64 pixels)
      BoardTop.fact "maxIterations" (uint64 maxIter)
      BoardTop.fact "fractionBits" (uint64 fracBits) ]

/// The same frame from the whole-pixel twin, pixel by pixel.
let renderTwin (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (view: uint64 * uint64 * uint64 * uint64) =
    let widthPadded = chunkedWidth pixels width
    let cxOrigin, cyOrigin, dx, dy = view

    [| for r in 0 .. height - 1 do
           for c in 0 .. widthPadded - 1 ->
               let cx = (cxOrigin + uint64 c * dx) &&& 0xFFFFFFFFUL
               let cy = (cyOrigin + uint64 r * dy) &&& 0xFFFFFFFFUL
               byte (laneTwin fracBits maxIter cx cy) |]

/// The design as a board top takes it: the beats in, and each beat of
/// pixels out at the head of its row the place it belongs, which is what
/// lets everything under it run in no order.
let mandelScatterOn (storage: RamStyle) (name: string) (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : BoardTop.Design =
    let def = mandelScatterDefOn storage name width pixels maxIter fracBits threads lanes

    let needs =
        [ yield BoardTop.streamIn "beats" [ beatPin ]
          // The destination rides at the head of the output row: the scatter
          // sink reads it there and the rest is the payload.
          yield BoardTop.streamOut "pixels" [ indexPin; pixelsPin ]
          for name, format in viewControls fracBits -> BoardTop.valueIn name format
          yield! frameFacts width pixels maxIter fracBits ]

    { name = name
      sampleRate = 0.0
      streams = 1
      needs = needs
      answers = pixels / pixelsPerBeat
      atClocking = None
      body =
        BoardTop.rigged needs (fun instance ->
            let in1, out1, cxOrigin, cyOrigin, dx, dy = def.NewNamed instance

            { through =
                fun s ->
                    s
                    |> streamMapTo (pins1 beatPin) List.head
                    |> streamThroughInstance in1 out1
                    |> streamMapTo (layoutOfList [ indexPin; pixelsPin ]) (fun (index, px) -> [ index; px ])
              ports = [ "cxOrigin", cxOrigin; "cyOrigin", cyOrigin; "dx", dx; "dy", dy ]
              readbacks = [] }) }

/// The scattered design with LUT-RAM register files.
let mandelScatter (name: string) (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : BoardTop.Design =
    mandelScatterOn Distributed name width pixels maxIter fracBits threads lanes

/// A frame through the counted top on the KV260 in the Sim: the batch
/// registers written as the host would — the count, the view, where the
/// frame goes — start pulsed, busy polled, the frame read out of a
/// behavioural DDR with a paced write slave. What the driver does on the
/// board, against the top the generator builds, before any bitstream exists.
let renderThroughBoardTop (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) (view: uint64 * uint64 * uint64 * uint64) =
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

    // The fabric's own count, as the board reports it — not the polls,
    // each of which is an AXI read that advances the Sim several cycles.
    let cycles = int (axi.read32 batch.cycles.offset)
    let frame = ddr.Memory[dst .. dst + beats * pixels - 1]
    frame, cycles, top

/// The frame over a board's host link, in the Sim: the count written over
/// the UART, each beat back as a frame of its index then its pixels, and
/// placed by the index rather than by when it came — what `warp11_batch
/// --serial frame:…` does on the Icepi.
let renderThroughLink (width: int) (height: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) (view: uint64 * uint64 * uint64 * uint64) =
    let design = mandelScatter "MandelScatter" width pixels maxIter fracBits threads lanes
    let top = BoardTop.boardTop icepi viaCountLink design
    let sim = Sim top.top
    let cyclesPerBit = uartCyclesPerBit icepi.fabricHz 1_000_000
    let link, uart = serialClientForMap top.map sim (uartSimPins "host") cyclesPerBit
    let device = uart :> ISimDevice
    let offset name = (top.registers |> List.find (fun (n, _) -> n = name) |> snd).offset
    let chunks = chunkedWidth pixels width / pixels
    let count = height * chunks
    let beats = count * design.answers
    // A beat on the link: the index word, then the pixels low word first.
    let wordsPerBeat = 1 + beatBits / 32
    let words = beats * wordsPerBeat
    let cxOrigin, cyOrigin, dx, dy = view

    for name, value in [ "cxOrigin", cxOrigin; "cyOrigin", cyOrigin; "dx", dx; "dy", dy ] do
        link.write32 (offset name) value

    link.write32 (offset "pixelsLinkArm") 1UL
    link.write32 (offset "beatsCountTotal") (uint64 count)
    link.write32 (offset "beatsCountStart") 1UL
    let budget = words * 7 * 10 * cyclesPerBit * 2 + 200_000
    let mutable cycles = 0
    let mutable received = []

    while received.Length < words && cycles < budget do
        device.Drive()
        sim.Tick()
        device.Sample()
        cycles <- cycles + 1

        if cycles % 1000 = 0 then
            received <- fst (SerialFrame.streamedWords uart.Received)

    received <- fst (SerialFrame.streamedWords uart.Received)
    let busy = link.read32 (offset "beatsCountBusy")
    let frame = Array.zeroCreate<byte> (beats * pixelsPerBeat)

    for beat in List.chunkBySize wordsPerBeat received do
        match beat with
        | index :: pixelWords when pixelWords.Length = wordsPerBeat - 1 ->
            pixelWords
            |> List.iteri (fun k w ->
                for j in 0..3 do
                    frame[int index * pixelsPerBeat + 4 * k + j] <- byte ((w >>> (8 * j)) &&& 255UL))
        | _ -> ()

    frame, cycles, busy, top

/// The example's silicon shape, said once. The frame is the README's; the
/// chunk is the width the farm was measured at (128 px; 16 px is +39%
/// against the row design, the barrel draining its tail every chunk); the
/// clock is the one the frame design proved at 104 lanes with timing to
/// spare. `lanes` is the only dial. On silicon the ordered farm costs +41.5%
/// against the row design at 256 iterations and the scattered one +1.2% —
/// the README's table.
let frameWidth = 1400

let chunkPixels = 128

let maxIterations = 256

let viewFracBits = 28

let laneThreads = 8

let fabricHz = 166_666_672

/// The frame on a part: the lanes given, and the register files in the
/// storage the part has — LUT RAM where it has it, block RAM where it does
/// not — the lane reading them the same way. The storage is the part's
/// business; the count is the design's.
let mandelFrameFor (lanes: int) (name: string) (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) : BoardTop.Design =
    BoardTop.sized name (fun fit ->
        let storage = if fit.part.facts.lutRam then Distributed else Block
        mandelScatterOn storage name width pixels maxIter fracBits threads lanes)

/// The board the frame is built for: the KV260 at the lanes' clock. Moving
/// the frame to another board is this line.
let board = kv260At fabricHz

/// Mandelbrot's mapping: the frame counted in, each beat out carrying
/// where it goes — written there in the host's memory, or sent as a frame
/// on its link for the host to place — as the board has.
let mapping: Warp11.Mapping.Mapping =
    { board = board; path = viaScatterHost board; plugged = [] }

/// The lanes the frame is built with: a design parameter, stated here and
/// nothing to do with the board. 104 is every lane the KV260's 1 248 DSPs
/// hold at twelve a lane; the Icepi holds 2, the DE10-Lite 10 (measured
/// 2026-11-08, 39 093 of 49 760 logic elements). The board's gate says
/// whether what is stated fits. `bitstream <dir> <lanes>` overrides it for
/// a sweep.
let lanes = 104

/// The example as the directory its board's toolchain builds from, straight
/// from the typed design, at `lanes` unless the line gives a count. There is
/// no design file in the way — this is the whole path from the code to the
/// bitstream.
let buildDirectory (dir: string) (lanesGiven: int option) =
    mandelFrameFor (defaultArg lanesGiven lanes) "Mandelbrot" frameWidth chunkPixels maxIterations viewFracBits laneThreads
    |> BoardTop.boardTopFor mapping
    |> Warp11.Build.write dir
