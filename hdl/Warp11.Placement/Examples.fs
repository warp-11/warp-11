/// The example designs, built by the same edits a canvas makes, laid out,
/// and written as files by `-- examples <dir>` — so an example a person
/// opens is exactly a design the checks prove. Open one with
/// `Warp11.Placement.Canvas -- edit <file> <recording.wav>`.
module Warp11.Placement.Examples

open Warp11
open Warp11.Fu
open Warp11.Graph
open Warp11.Edit
open Warp11.Placement.Placement

/// The rate the recordings beside `Warp11.Effects` are at.
let recordingRate = 48_828.0

let private step (change: Graph -> Result<Graph, string>) (h: History) =
    match apply change h with
    | h, None -> h
    | _, Some why -> failwith why

let private connect (a: PortRef) (b: PortRef) = addEdge (a, Out) (b, In)

/// The edge between two ports, through a connection.
let private through (connection: Connection) (a: PortRef) (b: PortRef) (g: Graph) =
    match g.edges |> List.tryFind (fun e -> e.from = a && e.``to`` = b) with
    | Some e -> setConnection e connection g
    | None -> Error $"{showPort a} → {showPort b}: no such edge"

/// Boxes in a row, left to right, in the order given.
let private inARow (names: string list) (g: Graph) : Graph =
    (g, List.indexed names) ||> List.fold (fun g (i, name) -> moveBox name (60.0 + float i * 260.0, 120.0) g)

/// A design with a stereo stream in and one out at the rate: the start of
/// every audio design here.
let private stereoDesign (name: string) (rate: float) : History = history (emptyGraph name rate stereoPins.fields)

/// A box of `unit`, under the name given.
let private place (unit: string) (name: string) (h: History) =
    let fresh = freshBoxName h.present unit
    h |> step (addBox unit (0.0, 0.0) >> Result.map fst) |> step (renameBox fresh name)

/// M1's design: a stereo stream through `gain`, volume and mute the
/// design's own controls.
let gain (rate: float) : Graph =
    { gainGraph with sampleRate = rate } |> inARow [ "input"; "gain"; "output" ]

/// The gain example's mapping: the KV260 preset, the boundary in the
/// host's memory — a WAV in, the design, a WAV out, no codec needed.
let gainMapping: Warp11.Mapping.Mapping = { board = kv260; path = viaHostMemory; plugged = [] }

/// M2's design: three `eq` sections with creation arguments — a low shelf,
/// a peaking cut, a high shelf — designed for the rate, with a register
/// stage on each edge between them: a band's last multiply does not feed
/// the next band's first in one cycle.
let threeBandEq (rate: float) : Graph =
    let band (name: string) (shape: string) (fc: string) (gain: string) (h: History) =
        h
        |> place "eq" name
        |> step (setArgument name "shape" shape)
        |> step (setArgument name "fc" fc)
        |> step (setArgument name "gain" gain)

    (stereoDesign "ThreeBandEq" rate
     |> band "low" "lowshelf" "200" "6"
     |> band "mid" "peaking" "1000" "-4"
     |> band "high" "highshelf" "5000" "3"
     |> step (connect (port "input" "in") (port "low" streamInlet))
     |> step (connect (port "low" streamOutlet) (port "mid" streamInlet))
     |> step (through Connection.Forward (port "low" streamOutlet) (port "mid" streamInlet))
     |> step (connect (port "mid" streamOutlet) (port "high" streamInlet))
     |> step (through Connection.Forward (port "mid" streamOutlet) (port "high" streamInlet))
     |> step (connect (port "high" streamOutlet) (port "output" "out")))
        .present
    |> inARow [ "input"; "low"; "mid"; "high"; "output" ]

/// The second half of M2: a gain whose volume is a number box the host can
/// change and whose mute is a toggle.
let controls (rate: float) : Graph =
    (history (gain rate)
     |> step (rename "Controls")
     |> step (removeBoundaryPort (port "input" "volume"))
     |> step (removeBoundaryPort (port "input" "mute"))
     |> step (addControlBox NumberBox (unsignedInt 16) "256" (60.0, 320.0) >> Result.map fst)
     |> step (renameBox "number" "volume")
     |> step (connect (port "volume" controlOutlet) (port "gain" "volume"))
     |> step (addControlBox NumberBox (unsignedInt 1) "0" (320.0, 320.0) >> Result.map fst)
     |> step (connect (port "toggle" controlOutlet) (port "gain" "mute")))
        .present

/// M4's design: the three-band EQ imported and placed twice in series — a
/// design as a box, to double-click into — with `ready` registered between
/// the two, so it does not ripple back through six bands.
let twice (rate: float) : Graph =
    (stereoDesign "Twice" rate
     |> step (importDesign (threeBandEq rate))
     |> step (addBox "ThreeBandEq" (0.0, 0.0) >> Result.map fst)
     |> step (addBox "ThreeBandEq" (0.0, 0.0) >> Result.map fst)
     |> step (connect (port "input" "in") (port "ThreeBandEq" "in"))
     |> step (connect (port "ThreeBandEq" "out") (port "ThreeBandEq2" "in"))
     |> step (through Connection.Backward (port "ThreeBandEq" "out") (port "ThreeBandEq2" "in"))
     |> step (connect (port "ThreeBandEq2" "out") (port "output" "out")))
        .present
    |> inARow [ "input"; "ThreeBandEq"; "ThreeBandEq2"; "output" ]

/// The three-band EQ with one multiplier a band: each band's ten products
/// take turns on a multiplier box of its own, drawn as an edge from the
/// band's socket port to the multiplier. `ready` is registered between the
/// bands, since a band served by a shared multiply passes it straight
/// through. Thirty multiplies become three.
let threeBandEqShared (rate: float) : Graph =
    let bands = [ "low"; "mid"; "high" ]
    let multiplier = Warp11.Audio.eqMultiplier.name

    let served (band: string) (h: History) =
        h
        |> place multiplier $"multiply_{band}"
        |> step (connect (port band "multiply") (port $"multiply_{band}" servicePort))

    let chain =
        (history (threeBandEq rate)
         |> step (rename "ThreeBandEqShared")
         |> step (through Connection.Backward (port "low" streamOutlet) (port "mid" streamInlet))
         |> step (through Connection.Backward (port "mid" streamOutlet) (port "high" streamInlet)))

    ((chain, bands) ||> List.fold (fun h band -> served band h)).present
    |> fun g ->
        (g, List.indexed bands)
        ||> List.fold (fun g (i, band) -> moveBox $"multiply_{band}" (320.0 + float i * 260.0, 320.0) g)

/// The shared three-band EQ placed twice in series, `ready` registered
/// between the two: six multipliers where the drawing in place has sixty.
let twiceShared (rate: float) : Graph =
    (stereoDesign "TwiceShared" rate
     |> step (importDesign (threeBandEqShared rate))
     |> step (addBox "ThreeBandEqShared" (0.0, 0.0) >> Result.map fst)
     |> step (addBox "ThreeBandEqShared" (0.0, 0.0) >> Result.map fst)
     |> step (connect (port "input" "in") (port "ThreeBandEqShared" "in"))
     |> step (connect (port "ThreeBandEqShared" "out") (port "ThreeBandEqShared2" "in"))
     |> step (through Connection.Backward (port "ThreeBandEqShared" "out") (port "ThreeBandEqShared2" "in"))
     |> step (connect (port "ThreeBandEqShared2" "out") (port "output" "out")))
        .present
    |> inARow [ "input"; "ThreeBandEqShared"; "ThreeBandEqShared2"; "output" ]

/// M6's image design: rows of `columns` pixels through a 3×3 blur. Opened
/// with a PGM of that width, the mapping supplies the halo rows.
let imageBlur (columns: int) (rows: int) : Graph =
    (history (emptyGraph "ImageBlur" defaultSampleRate [ Warp11.Devices.rowPin columns ])
     |> step (addBox "blur" (0.0, 0.0) >> Result.map fst)
     |> step (setArgument "blur" "columns" (string columns))
     |> step (setArgument "blur" "rows" (string rows))
     |> step (connect (port "input" "in") (port "blur" streamInlet))
     |> step (connect (port "blur" streamOutlet) (port "output" "out")))
        .present
    |> inARow [ "input"; "blur"; "output" ]

/// M6's table design: two integer columns in, their sum out — a CSV a row
/// a beat.
let adder: Graph =
    (history (emptyGraph "Adder" defaultSampleRate [ "x", unsignedInt 32; "y", unsignedInt 32 ])
     |> step (setFields (port "output" "out") [ "sum", unsignedInt 33 ])
     |> step (addBox "add32" (0.0, 0.0) >> Result.map fst)
     |> step (connect (port "input" "in") (port "add32" streamInlet))
     |> step (connect (port "add32" streamOutlet) (port "output" "out")))
        .present
    |> inARow [ "input"; "add32"; "output" ]

/// Multiply and accumulate, `a * b + c`: three streams on the boundary,
/// joined into the beats the multiplier and the adder take. What the adder
/// needs beside the product is joined to it; nothing is carried past a box.
let mac: Graph =
    let u16, u32 = unsignedInt 16, unsignedInt 32

    (history { emptyGraph "Mac" defaultSampleRate [ "a", u16 ] with inputs = []; outputs = [] }
     |> step (addInputStream "a" [ "a", u16 ])
     |> step (addInputStream "b" [ "b", u16 ])
     |> step (addInputStream "c" [ "c", u32 ])
     |> step (addOutputStream "out" [ "sum", unsignedInt 33 ])
     |> place joinBox "factors"
     |> place "mul16" "product"
     |> place joinBox "terms"
     |> place "add32" "sum"
     |> step (connect (port "input" "a") (port "factors" "in0"))
     |> step (connect (port "input" "b") (port "factors" "in1"))
     |> step (connect (port "factors" streamOutlet) (port "product" streamInlet))
     |> step (connect (port "product" streamOutlet) (port "terms" "in0"))
     |> step (connect (port "input" "c") (port "terms" "in1"))
     |> step (connect (port "terms" streamOutlet) (port "sum" streamInlet))
     |> step (connect (port "sum" streamOutlet) (port "output" "out")))
        .present
    |> inARow [ "input"; "factors"; "product"; "terms"; "sum"; "output" ]

/// A 64×64 test image: a diagonal gradient with a bright square, so a blur
/// has edges to soften.
let gradient: Grey =
    let side = 64

    { width = side
      height = side
      pixels =
        [| for r in 0 .. side - 1 do
               for c in 0 .. side - 1 ->
                   if r >= 20 && r < 44 && c >= 20 && c < 44 then 255uy else byte ((r + c) * 2 % 256) |] }

/// A few rows for the adder.
let numbers: Table =
    { columns = [ "x"; "y" ]
      rows = [ for i in 1..8 -> [ string (i * 100); string (i * i) ] ] }

/// Every example, with the file it is written to.
let all (rate: float) : (string * Graph) list =
    // The gain example names its default mapping, the file beside it; the
    // designs built from it do not inherit that.
    [ "gain.json", { gain rate with mapping = Some "gain.kv260.json" }
      "three-band-eq.json", threeBandEq rate
      "controls.json", controls rate
      "twice.json", twice rate
      "three-band-eq-shared.json", threeBandEqShared rate
      "twice-shared.json", twiceShared rate
      "blur.json", imageBlur gradient.width gradient.height
      "adder.json", adder
      "mac.json", mac ]

/// Every example's mapping files, one per board that holds it: rows
/// through the KV260's memory, and over the link on the three boards with
/// one wire. Which boards, measured through the open flow 2026-11-07 (the
/// gate's numbers, cells used of the part's): the Go Board holds nothing
/// with a multiplier in it — the gain alone is 2 096 of 1 280 — so gain,
/// controls (a gain with settings), the EQ, twice and the blur are not
/// written for it; the iCEBreaker holds the gain (its 5 280 cells) but not
/// the three-band EQ (5 489), twice (8 825) or the blur (11 364, and 32 of
/// its 30 block RAMs). The blur's row is one 512-bit field — four beats of
/// the KV260's memory, sixteen words of a link — and both carry it. The
/// gain example's `gain.kv260.json` is the one its design file names.
let mappings: (string * Warp11.Mapping.Mapping) list =
    let on (board: Board) (path: DataPath) : Warp11.Mapping.Mapping = { board = board; path = path; plugged = [] }
    let link = [ "icebreaker", on (iceBreakerAt 24_000_000) viaLink; "icepi", on icepi viaLink; "goboard", on goBoard viaLink; "de10lite", on de10Lite viaLink ]
    let memory = [ "kv260", gainMapping ]

    let holds (example: string) (boards: string list) =
        [ for name, m in memory @ link do
              if List.contains name boards then
                  yield $"{example}.{name}.json", m ]

    holds "gain" [ "kv260"; "icebreaker"; "icepi"; "de10lite" ]
    @ holds "three-band-eq" [ "kv260"; "icepi"; "de10lite" ]
    @ holds "controls" [ "kv260"; "icebreaker"; "icepi"; "de10lite" ]
    @ holds "twice" [ "kv260"; "icepi"; "de10lite" ]
    @ holds "three-band-eq-shared" [ "kv260"; "icepi" ]
    @ holds "twice-shared" [ "kv260"; "icepi" ]
    @ holds "blur" [ "kv260"; "icepi"; "de10lite" ]
    @ holds "adder" [ "kv260"; "icebreaker"; "icepi"; "goboard"; "de10lite" ]

/// Write every example into `dir`, its mapping files, and the image and
/// table they open with.
let write (dir: string) =
    System.IO.Directory.CreateDirectory dir |> ignore

    for file, g in all recordingRate do
        Warp11.DesignFile.save (System.IO.Path.Combine(dir, file)) g

    for file, m in mappings do
        Warp11.Mapping.save (System.IO.Path.Combine(dir, file)) m

    writePgm (System.IO.Path.Combine(dir, "gradient.pgm")) gradient
    writeCsv (System.IO.Path.Combine(dir, "numbers.csv")) numbers
