/// Drawing a recorded trace.
///
/// One pixel column per *cycle*, never decimated: a viewer that thinned samples
/// to fit its width would draw a signal toggling every cycle as a flat line,
/// which is worse than not drawing it. A page is therefore a fixed number of
/// cycles that you page through, the same idiom the memory window uses — and
/// anything that wants zoom, search or measurement should go out as VCD to a
/// real viewer.
module Warp11.SimView.Waveform

open System.Numerics
open Avalonia
open Avalonia.Media.Imaging
open Avalonia.Platform
open Warp11.Debug

/// Cycles per page, and the height of one signal's lane.
let pageSamples = 900
let rowHeight = 26

let private background = 0xFF101418u
let private high = 0xFF3FB618u
let private band = 0xFF6E8BA6u
let private cursorLine = 0xFFE06C3Bu

let valueAt (s: TraceSignal) i =
    if s.width > 64 then s.wideValues[i] else BigInteger s.values[i]

let private length (trace: Trace) = trace.Length

/// Pixels per cycle. A short trace would otherwise be a postage stamp — 61
/// cycles is 61 pixels — so the page widens itself to fill the panel while a
/// full page stays at one pixel per cycle. Never below 1: the cycle is the
/// smallest thing that can be drawn, because it is the smallest thing that
/// happened.
let zoomFor columns =
    if columns <= 0 then 1 else max 1 (min 16 (pageSamples / columns))

/// The trace as a bitmap: one lane per signal, `zoom` columns per cycle. A
/// one-bit signal is drawn as its level with a riser at each edge; anything
/// wider is a band that breaks where the value changes, which is the shape that
/// says "something happened here" without pretending to show the value.
let render (trace: Trace) (cursor: int) (zoom: int) : WriteableBitmap =
    let columns = max 1 (length trace)
    let rows = max 1 (List.length trace.signals)
    let width = columns * zoom
    let height = rows * rowHeight

    let bitmap =
        new WriteableBitmap(PixelSize(width, height), Vector(96.0, 96.0), PixelFormat.Bgra8888, AlphaFormat.Opaque)

    use buffer = bitmap.Lock()
    let line = Array.create width (int background)

    let flush y =
        System.Runtime.InteropServices.Marshal.Copy(
            line,
            0,
            buffer.Address + nativeint (y * buffer.RowBytes),
            width
        )

    // Drawn lane by lane so the buffer stays one row wide: for each pixel row,
    // work out which signals put ink in it.
    let signals = List.toArray trace.signals

    for y in 0 .. height - 1 do
        System.Array.Fill(line, int background)

        let row = y / rowHeight
        let withinRow = y % rowHeight
        let top = 4
        let bottom = rowHeight - 6

        if row < signals.Length then
            let s = signals[row]

            for c in 0 .. columns - 1 do
                let v = valueAt s c
                let changed = c > 0 && v <> valueAt s (c - 1)

                // The riser belongs at the leading edge of the cycle whose
                // value changed, so an edge lines up with the cycle it happened
                // in rather than the one before it.
                for p in 0 .. zoom - 1 do
                    let x = c * zoom + p

                    let ink =
                        if s.width = 1 then
                            let level = if v.IsZero then bottom else top
                            withinRow = level || (changed && p = 0 && withinRow >= top && withinRow <= bottom)
                        else
                            withinRow = top
                            || withinRow = bottom
                            || (changed && p = 0 && withinRow >= top && withinRow <= bottom)

                    if ink then
                        line[x] <- int (if s.width = 1 then high else band)

        if cursor >= 0 && cursor < columns then
            for p in 0 .. zoom - 1 do
                line[cursor * zoom + p] <- int cursorLine

        flush y

    bitmap
