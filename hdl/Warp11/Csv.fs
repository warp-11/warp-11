/// A table as a file, a row a beat: the columns named after a design's
/// pins, each cell a number read in its pin's format — an integer, or a
/// decimal that the format's fraction bits scale — and written back the
/// same way. What a tick of prices, a layer's activations or any general
/// data looks like on a boundary.
[<AutoOpen>]
module Warp11.Csv

open System.Numerics

type Table =
    { columns: string list
      rows: string list list }

let private split (line: string) = line.Split ',' |> Array.map (fun c -> c.Trim()) |> List.ofArray

/// A comma-separated file with a header row. Blank lines are skipped.
let readCsv (path: string) : Table =
    let lines = System.IO.File.ReadAllLines path |> Array.filter (fun l -> l.Trim() <> "")

    match List.ofArray lines with
    | [] -> failwith $"{path}: an empty file has no columns"
    | header :: rows -> { columns = split header; rows = rows |> List.map split }

let writeCsv (path: string) (t: Table) =
    System.IO.File.WriteAllLines(path, (String.concat "," t.columns) :: (t.rows |> List.map (String.concat ",")))

/// A cell as the bits of a number in `f`: an integer, or a decimal scaled by
/// the format's fraction bits, two's complement at the format's width when
/// negative. Refused, naming the cell, when it is not a number or does not
/// fit.
let parseCell (f: NumberFormat) (text: string) : Result<BigInteger, string> =
    match System.Decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
    | false, _ -> Error $"'{text}' is not a number"
    | true, value ->
        let scaled = BigInteger(System.Math.Round(value * decimal (1L <<< f.fracBits)))
        let full = BigInteger.One <<< f.totalWidth
        let limitHigh = if f.signed then (full >>> 1) - BigInteger.One else full - BigInteger.One
        let limitLow = if f.signed then -(full >>> 1) else BigInteger.Zero

        if scaled > limitHigh || scaled < limitLow then
            Error $"'{text}' does not fit {describeFormat f}"
        else
            Ok(if scaled < BigInteger.Zero then scaled + full else scaled)

/// The bits of a number in `f` as a cell: signed read as such, a fraction
/// written as a decimal.
let formatCell (f: NumberFormat) (bits: BigInteger) : string =
    let full = BigInteger.One <<< f.totalWidth
    let value = if f.signed && bits >= (full >>> 1) then bits - full else bits

    if f.fracBits = 0 then
        value.ToString()
    else
        (decimal value / decimal (1L <<< f.fracBits)).ToString(System.Globalization.CultureInfo.InvariantCulture)
