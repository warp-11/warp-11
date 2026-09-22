/// This project's units, offered to a design drawn on the canvas. A head
/// registers them before it opens anything; `Warp11.Design` ships only the
/// general units, so a project's own live with the project.
module Warp11.Mandelbrot.Units

open Warp11
open Warp11.Factories
open Warp11.Mandelbrot

let private chunkPixels =
    { name = "pixels"; kind = IntParameter; ``default`` = "64"; about = "pixels a beat: a power of two, whole beats of 16" }

let private chunkPixelsOk (v: int) =
    if v >= 16 && v % 16 = 0 && (v &&& (v - 1)) = 0 then Ok v else Error $"pixels is a power of two from 16 up, not %d{v}"

let private here = [ "Warp11.Mandelbrot.Lane" ]

let mandelChunk: Factory =
    { factory
        "mandelChunk"
        [ chunkPixels
          { name = "maxIter"; kind = IntParameter; ``default`` = "256"; about = "iterations before a point is called inside" }
          { name = "fracBits"; kind = IntParameter; ``default`` = "28"; about = "fraction bits of the 32-bit view numbers" }
          { name = "threads"; kind = IntParameter; ``default`` = "8"; about = "pixels in flight through the cone; more than its 4 stages" } ]
        (fun _ args ->
            match intArg "pixels" args |> Result.bind chunkPixelsOk, intArg "maxIter" args |> Result.bind (atLeast "maxIter" 2), intArg "fracBits" args, intArg "threads" args |> Result.bind (atLeast "threads" (Lane.mandelStepLatency + 1)) with
            | Ok pixels, Ok maxIter, Ok fracBits, Ok threads when fracBits >= 1 && fracBits <= 30 -> Ok(pixels, maxIter, fracBits, threads)
            | Ok _, Ok _, Ok fracBits, Ok _ -> Error $"fracBits is between 1 and 30, not %d{fracBits}"
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e)
        (fun _ (pixels, maxIter, fracBits, threads) -> Lane.mandelChunk pixels maxIter fracBits threads)
        (fun (pixels, maxIter, fracBits, threads) -> $"mandelChunk %d{pixels} %d{maxIter} %d{fracBits} %d{threads}") with
        opens = here }

let coords: Factory =
    { factory
        "coords"
        [ { name = "width"; kind = IntParameter; ``default`` = "1400"; about = "pixels in a row of the frame" }
          chunkPixels
          { name = "fracBits"; kind = IntParameter; ``default`` = "28"; about = "fraction bits of the 32-bit view numbers" } ]
        (fun _ args ->
            match intArg "width" args |> Result.bind (atLeast "width" 1), intArg "pixels" args |> Result.bind chunkPixelsOk, intArg "fracBits" args with
            | Ok width, Ok pixels, Ok fracBits when fracBits >= 1 && fracBits <= 30 -> Ok(width, pixels, fracBits)
            | Ok _, Ok _, Ok fracBits -> Error $"fracBits is between 1 and 30, not %d{fracBits}"
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e)
        (fun _ (width, pixels, fracBits) -> Lane.coords width pixels fracBits)
        (fun (width, pixels, fracBits) -> $"coords %d{width} %d{pixels} %d{fracBits}") with
        opens = here }

/// Every unit this project offers.
let all: Factory list = [ mandelChunk; coords ]

/// Offer them from here on. A head calls this once, before it loads a design
/// that names them.
let register () =
    match Factories.register all with
    | Ok() -> ()
    | Error why -> failwith why
