/// Seeing the signal: a box's outlets as a waveform, and a wire's level,
/// both read from the session's trace ring — one sample per cycle, of which
/// the beats are the cycles the source's valid was high. The debugger's
/// `Waveform` draws bits; a sample stream wants its amplitude drawn, which
/// is what this does.
module Warp11.Placement.Canvas.Scope

open System.Numerics
open Avalonia
open Avalonia.Media.Imaging
open Avalonia.Platform
open Warp11.Debug

/// A trace signal's value at a sample, as the bits.
let private at (s: TraceSignal) (i: int) : BigInteger =
    if s.width > 64 then s.wideValues[i] else BigInteger s.values[i]

/// The beats of a signal over a trace: its value, read as signed at its
/// width, at every sample where `valid` was high. Empty when either net is
/// not in the trace.
let beats (trace: Trace) (valid: string) (signal: string) : int64[] =
    match trace.signals |> List.tryFind (fun s -> s.name = valid), trace.signals |> List.tryFind (fun s -> s.name = signal) with
    // A field wider than a machine word is not a sample — a packed row of
    // pixels, a word of a table — and has no amplitude to draw.
    | Some _, Some s when s.width > 64 -> [||]
    | Some v, Some s ->
        let half = BigInteger.One <<< (s.width - 1)
        let full = BigInteger.One <<< s.width

        [| for i in 0 .. trace.Length - 1 do
               if not (at v i).IsZero then
                   let raw = at s i
                   yield int64 (if raw >= half then raw - full else raw) |]
    | _ -> [||]

/// The loudest a signal got: the largest magnitude among its beats, as a
/// fraction of full scale for its width.
let level (width: int) (values: int64[]) : float =
    if values.Length = 0 then
        0.0
    else
        let fullScale = float (1L <<< (width - 1))
        float (values |> Array.map abs |> Array.max) / fullScale

let private background = 0xFF101418
let private centre = 0xFF3A4048
let colours = [ 0xFF3FB618; 0xFF4C9BE8; 0xFFE0A030; 0xFFE06C3B ]

/// The last `width` beats of each trace, one column each, drawn as a line
/// from the previous sample to this one; full scale is the lane's height.
let render (traces: (int64[] * int) list) (width: int) (height: int) (widthBits: int) : WriteableBitmap =
    let bitmap =
        new WriteableBitmap(PixelSize(width, height), Vector(96.0, 96.0), PixelFormat.Bgra8888, AlphaFormat.Opaque)

    let pixels = Array.create (width * height) background
    let mid = height / 2

    for x in 0 .. width - 1 do
        pixels[mid * width + x] <- centre

    let fullScale = float (1L <<< (widthBits - 1))

    let yOf (v: int64) =
        let y = mid - int (float v / fullScale * float (mid - 2))
        max 0 (min (height - 1) y)

    for values, colour in traces do
        let n = min width values.Length
        let start = values.Length - n
        let mutable previous = None

        for x in 0 .. n - 1 do
            let y = yOf values[start + x]

            let y0 =
                match previous with
                | Some p -> p
                | None -> y

            for yy in (min y y0) .. (max y y0) do
                pixels[yy * width + x] <- colour

            previous <- Some y

    use buffer = bitmap.Lock()

    for y in 0 .. height - 1 do
        System.Runtime.InteropServices.Marshal.Copy(pixels, y * width, buffer.Address + nativeint (y * buffer.RowBytes), width)

    bitmap
