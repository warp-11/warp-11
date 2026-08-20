/// A recorded trace as VCD — the format every waveform viewer reads.
///
/// This is the cheapest thing the trace ring buys. The debugger's own waveform
/// is for the glance; GTKWave and Surfer do zoom, search, cursors and
/// measurement far better than anything worth writing here, and they take VCD.
///
/// Only changes are emitted, which is what the format is for: a signal that
/// holds for a thousand cycles costs one line.
module Warp11.Vcd

open System.Numerics
open System.Text
open Warp11.Debug

/// VCD identifiers are short printable-ASCII codes. `!` (33) through `~` (126)
/// gives 94 single-character ids, then two characters, and so on — enough for
/// any design that fits the trace budget.
let private identifier index =
    let alphabet = 94
    let rec build i (acc: string) =
        let next = acc + string (char (33 + i % alphabet))
        if i < alphabet then next else build (i / alphabet - 1) next

    build index ""

/// A value as VCD writes it: `0`/`1` for one bit, `b<bits> ` for a vector,
/// most significant bit first with leading zeroes trimmed (the format's own
/// convention — a reader zero-extends).
let private valueText (width: int) (v: BigInteger) =
    if width = 1 then
        if v.IsZero then "0" else "1"
    else
        let bits =
            [| for i in width - 1 .. -1 .. 0 -> if (v >>> i) &&& BigInteger.One = BigInteger.One then '1' else '0' |]
            |> System.String

        "b" + (bits.TrimStart '0' |> fun s -> if s = "" then "0" else s) + " "

/// Render a trace as a VCD file. `topName` names the scope so a viewer shows
/// the signals under the design they came from.
let render (topName: string) (trace: Trace) : string =
    let out = StringBuilder()
    let signals = List.toArray trace.signals
    let ids = Array.init signals.Length identifier

    let sampleAt (s: TraceSignal) i =
        if s.width > 64 then s.wideValues[i] else BigInteger s.values[i]

    out
        .AppendLine("$version warp11 $end")
        .AppendLine("$timescale 1ns $end")
        .AppendLine($"$scope module {topName} $end")
    |> ignore

    for i in 0 .. signals.Length - 1 do
        out.AppendLine($"$var wire %d{signals[i].width} {ids[i]} {signals[i].name} $end") |> ignore

    out.AppendLine("$upscope $end").AppendLine("$enddefinitions $end") |> ignore

    // One cycle is one time unit. The first sample is written in full — a
    // viewer needs an initial value for every signal — and after that only
    // what changed, which is the whole reason the format is small.
    for step in 0 .. trace.Length - 1 do
        let changes =
            [ for i in 0 .. signals.Length - 1 do
                  let now = sampleAt signals[i] step

                  if step = 0 || now <> sampleAt signals[i] (step - 1) then
                      yield valueText signals[i].width now + ids[i] ]

        if not (List.isEmpty changes) then
            out.AppendLine($"#%d{trace.firstCycle + step}") |> ignore

            for change in changes do
                out.AppendLine change |> ignore

    out.ToString()
