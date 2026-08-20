/// The software frame: `laneTwin` — the same whole-pixel twin every
/// Mandelbrot oracle in this repo judges the fabric against — walked over a
/// view. Not an approximation of the accelerator, the *same arithmetic*, so
/// the bring-up rig and the board produce identical pixels for identical
/// views and a difference on screen is a real difference.
module Warp11.MandelView.Twin

open System.Threading.Tasks
open Warp11.Mandelbrot.Lane
open Warp11.MandelView.Bus

/// Rows in parallel: 1.12M pixels at up to 256 iterations is a second of
/// single-threaded work and a fraction of one across cores. Each row is
/// independent — no accumulator, no shared state — so this needs no
/// coordination beyond the loop itself.
let frame (view: MandelView) (width: int) (height: int) (maxIter: int) : byte[] =
    let pixels = Array.zeroCreate<byte> (width * height)

    Parallel.For(
        0,
        height,
        fun py ->
            // Wrapping u32 adds, exactly as the fabric's row generator does
            // them — F#'s uint32 arithmetic is unchecked, which is the
            // semantics wanted here rather than an oversight.
            let cy = view.cyOrigin + uint32 py * view.dy
            let rowBase = py * width

            for px in 0 .. width - 1 do
                let cx = view.cxOrigin + uint32 px * view.dx
                pixels[rowBase + px] <- byte (laneTwin FracBits maxIter (uint64 cx) (uint64 cy))
    )
    |> ignore

    pixels
