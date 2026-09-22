/// The frame as the canvas draws it, as a design file: four boxes built by
/// the same edits a canvas makes. `Warp11.Mandelbrot -- example <dir>` writes
/// it, and `Warp11.Placement`'s own examples no longer know about it — a
/// project's example lives with the project, as its units do.
module Warp11.Mandelbrot.Example

open Warp11
open Warp11.Graph
open Warp11.Edit
open Warp11.Mandelbrot

let private step (change: Graph -> Result<Graph, string>) (h: History) =
    match apply change h with
    | h, None -> h
    | _, Some why -> failwith why

let private wire (a: PinRef) (b: PinRef) = addWire (a, Out) (b, In)

/// Boxes in a row, left to right.
let private inARow (names: string list) (g: Graph) : Graph =
    (g, List.indexed names) ||> List.fold (fun g (i, name) -> moveBox name (60.0 + float i * 260.0, 120.0) g)

/// The Mandelbrot frame as the canvas draws it: a count in, `coords` minting
/// each chunk's view from the origin and steps on the input box,
/// `mandelChunk` spent `lanes` times, sixteen pixels a beat out. Opened with
/// `frame:<width>x<height>`, drawn a frame at a time; on the KV260 it is
/// the counted path.
let mandelbrot (width: int) (pixels: int) (maxIter: int) (fracBits: int) (threads: int) (lanes: int) : Graph =
    let view = Lane.viewFormat fracBits

    (history (emptyGraph "Mandelbrot" defaultSampleRate)
     |> step (addInputPin Lane.beatPin)
     |> step (addControl ("cxOrigin", view))
     |> step (addControl ("cyOrigin", view))
     |> step (addControl ("dx", view))
     |> step (addControl ("dy", view))
     |> step (addOutputPin Lane.pixelsPin)
     |> step (addBox "coords" (0.0, 0.0) >> Result.map fst)
     |> step (setArgument "coords" "width" (string width))
     |> step (setArgument "coords" "pixels" (string pixels))
     |> step (setArgument "coords" "fracBits" (string fracBits))
     |> step (addBox "mandelChunk" (0.0, 0.0) >> Result.map fst)
     |> step (setArgument "mandelChunk" "pixels" (string pixels))
     |> step (setArgument "mandelChunk" "maxIter" (string maxIter))
     |> step (setArgument "mandelChunk" "fracBits" (string fracBits))
     |> step (setArgument "mandelChunk" "threads" (string threads))
     |> step (setCopies "mandelChunk" lanes)
     |> step (wire (pin "input" "beat") (pin "coords" "beat"))
     |> step (wire (pin "input" "cxOrigin") (pin "coords" "cxOrigin"))
     |> step (wire (pin "input" "cyOrigin") (pin "coords" "cyOrigin"))
     |> step (wire (pin "input" "dx") (pin "coords" "dx"))
     |> step (wire (pin "input" "dy") (pin "coords" "dy"))
     |> step (wire (pin "coords" "cx0") (pin "mandelChunk" "cx0"))
     |> step (wire (pin "coords" "cy") (pin "mandelChunk" "cy"))
     |> step (wire (pin "coords" "dx") (pin "mandelChunk" "dx"))
     |> step (wire (pin "mandelChunk" "pixels") (pin "output" "pixels")))
        .present
    |> inARow [ "input"; "coords"; "mandelChunk"; "output" ]

/// The Mandelbrot example's mapping: the KV260 on the counted path, at the
/// clock the frame design proved — 166.67 MHz fills the part's DSPs at 104
/// lanes with timing to spare.
let mandelbrotMapping: Warp11.Mapping.Mapping = { board = kv260At 166_666_672; path = Counted }

/// The silicon config, but four lanes: a frame in the Sim in minutes rather
/// than hours. Copies is the number to change for the board.
let design: Graph = { mandelbrot 1400 128 256 28 8 4 with mapping = Some "mandelbrot.kv260.json" }

/// Write the design and its mapping into `dir`.
let write (dir: string) =
    System.IO.Directory.CreateDirectory dir |> ignore
    Warp11.DesignFile.save (System.IO.Path.Combine(dir, "mandelbrot.json")) design
    Warp11.Mapping.save (System.IO.Path.Combine(dir, "mandelbrot.kv260.json")) mandelbrotMapping
    printfn $"""{System.IO.Path.Combine(dir, "mandelbrot.json")}"""
