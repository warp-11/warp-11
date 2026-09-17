/// A greyscale image as a file — PGM, the portable graymap, binary `P5`
/// with one byte a pixel — and as row beats: a row is one beat, pixel `c`
/// at bits `8c+7 … 8c`, which is how `lineWindow` and the stencil rules
/// read a row. Small enough to read and write here; a PNG needs a decoder
/// and is a mapping's business in a head that has one.
[<AutoOpen>]
module Warp11.Pgm

open System.Numerics

/// A greyscale image, row-major, one byte a pixel.
type Grey =
    { width: int
      height: int
      pixels: byte[] }

    member g.Row(r: int) : byte[] = g.pixels[r * g.width .. (r + 1) * g.width - 1]

/// A row as a beat: pixel `c` at bits `8c+7 … 8c`.
let rowBits (row: byte[]) : BigInteger =
    (BigInteger.Zero, Array.indexed row) ||> Array.fold (fun acc (c, v) -> acc ||| (BigInteger(int v) <<< (8 * c)))

/// A beat as a row of `width` pixels.
let rowOfBits (width: int) (bits: BigInteger) : byte[] =
    [| for c in 0 .. width - 1 -> byte ((bits >>> (8 * c)) &&& BigInteger 255) |]

/// A grey image from rows of beats.
let ofRows (width: int) (rows: BigInteger list) : Grey =
    { width = width
      height = rows.Length
      pixels = rows |> List.map (rowOfBits width) |> Array.concat }

/// A grey image from beats each carrying `pixelsPerBeat` pixels in raster
/// order, a row every `ceil(width / pixelsPerBeat)` beats and cropped to
/// `width` — how a frame drawn a chunk at a time comes back.
let ofChunks (width: int) (pixelsPerBeat: int) (beats: BigInteger list) : Grey =
    let chunksPerRow = (width + pixelsPerBeat - 1) / pixelsPerBeat
    let height = beats.Length / chunksPerRow
    let padded = beats |> List.map (rowOfBits pixelsPerBeat) |> Array.concat

    { width = width
      height = height
      pixels = [| for r in 0 .. height - 1 do yield! padded[r * chunksPerRow * pixelsPerBeat .. r * chunksPerRow * pixelsPerBeat + width - 1] |] }

let writePgm (path: string) (g: Grey) =
    use file = System.IO.File.Create path
    let header = System.Text.Encoding.ASCII.GetBytes $"P5\n%d{g.width} %d{g.height}\n255\n"
    file.Write(header, 0, header.Length)
    file.Write(g.pixels, 0, g.pixels.Length)

/// A `P5` file, comments allowed in the header, one byte a pixel.
let readPgm (path: string) : Grey =
    let bytes = System.IO.File.ReadAllBytes path
    let mutable at = 0

    let token () =
        // Whitespace and `#` comments, then a run of non-whitespace.
        let mutable skipping = true

        while skipping && at < bytes.Length do
            if bytes[at] = byte '#' then
                while at < bytes.Length && bytes[at] <> byte '\n' do
                    at <- at + 1
            elif System.Char.IsWhiteSpace(char bytes[at]) then
                at <- at + 1
            else
                skipping <- false

        let start = at

        while at < bytes.Length && not (System.Char.IsWhiteSpace(char bytes[at])) do
            at <- at + 1

        System.Text.Encoding.ASCII.GetString(bytes, start, at - start)

    let magic = token ()

    if magic <> "P5" then
        failwith $"{path}: not a binary PGM (P5), it starts {magic}"

    let width = int (token ())
    let height = int (token ())
    let maxValue = int (token ())

    if maxValue <> 255 then
        failwith $"{path}: one byte a pixel, so a maximum of 255, not %d{maxValue}"

    // One whitespace byte after the header, then the pixels.
    at <- at + 1

    if bytes.Length - at < width * height then
        failwith $"{path}: %d{width}×%d{height} pixels announced, %d{bytes.Length - at} bytes present"

    { width = width
      height = height
      pixels = bytes[at .. at + width * height - 1] }
