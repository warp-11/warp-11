/// What satisfies a graph's boundary in a given world.
///
/// A graph declares what it needs — so many stream pins in and out, of such
/// formats, and these controls. It says nothing about where the beats come
/// from. A *mapping* says that, per world: in the simulator a WAV file plays
/// into the input box and what leaves the output box is a WAV file; on a
/// board the same boundary would be I2S pins. The graph is the same data in
/// both, which is the point of keeping the two apart.
///
/// Only the simulator's mapping exists yet.
module Warp11.Devices

open Warp11
open Warp11.Fu
open System.Numerics
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate

/// A stream driven from a list of beats, each a value per field, and what
/// comes back collected the same way — the device form any table of beats
/// takes: an image's rows, a CSV's rows. Always ready: a file never pushes
/// back.
type BeatStreamDevice(sim: Sim, input: StreamPins, output: StreamPins, beats: BigInteger list list, wanted: int) =
    let heard = ResizeArray<BigInteger list>()
    let mutable offered = 0
    let mutable accepted = false
    let mutable arrival: BigInteger list option = None

    member _.FramesOffered = offered
    member _.FramesHeard = heard.Count
    member _.Remaining = beats.Length - offered
    member _.Wanted = wanted
    member _.Heard: BigInteger list list = List.ofSeq heard

    interface ISimDevice with
        member _.Drive() =
            let more = offered < beats.Length
            sim.Poke(input.valid, (if more then 1UL else 0UL))

            if more then
                List.iter2 (fun name value -> sim.PokeWide(name, value)) input.fields beats[offered]

            sim.Poke(output.ready, 1UL)
            accepted <- more && sim.Peek input.ready = 1UL

            arrival <-
                if sim.Peek output.valid = 1UL then
                    Some(output.fields |> List.map sim.PeekWide)
                else
                    None

        member _.Sample() =
            if accepted then
                offered <- offered + 1

            arrival |> Option.iter heard.Add
            accepted <- false
            arrival <- None

/// Drive one beat device until it has heard what it wanted, or nothing has
/// moved for `idleLimit` cycles.
let private drive (sim: Sim) (devices: BeatStreamDevice list) (idleLimit: int) =
    let driven = devices |> List.map (fun d -> d :> ISimDevice)
    let heardSoFar () = devices |> List.sumBy (fun d -> d.FramesHeard)
    let mutable idle = 0
    let mutable lastHeard = 0

    while devices |> List.exists (fun d -> d.FramesHeard < d.Wanted) && idle < idleLimit do
        driven |> List.iter (fun d -> d.Drive())
        sim.Tick()
        driven |> List.iter (fun d -> d.Sample())

        if heardSoFar () = lastHeard then
            idle <- idle + 1
        else
            idle <- 0
            lastHeard <- heardSoFar ()

/// The simulator's answer to a stereo boundary: a recording in, control
/// values held for the run, and where to put what came out.
type SimMapping =
    { source: WavData
      controls: (string * uint64) list
      /// Written when the run ends, if given; the result comes back either way.
      outputPath: string option }

/// What a stereo WAV device needs the boundary to be: one stream, two signed
/// sample pins each way.
let private stereo = [ "left", signedInt sampleWidth; "right", signedInt sampleWidth ]

let private describePins (pins: (string * NumberFormat) list) =
    pins |> List.map (fun (n, f) -> $"{n}: {describeFormat f}") |> String.concat ", "

/// Does the mapping fit what the graph declares? Said before anything runs,
/// naming the side that does not.
let private check (g: Graph) (m: SimMapping) =
    if g.streams < 1 then
        failwith $"{g.name}: a design has at least one stream"

    // A WAV's header holds a whole number of hertz; the design's rate need not
    // be one, so the two agree when they round to the same.
    if round g.sampleRate <> float m.source.sampleRate then
        failwith $"{g.name}: the design is made for %g{g.sampleRate} Hz and the recording is %d{m.source.sampleRate} Hz"

    for side, pins in [ "input", g.inputs; "output", g.outputs ] do
        if pins <> stereo then
            failwith $"{g.name}: a WAV device needs the {side} box to be [{describePins stereo}], and it is [{describePins pins}]"

    let ports = controlPorts g

    for name, _ in m.controls do
        if not (ports |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: no control called '{name}' — it has [{describePins ports}]"

    // The design's own controls come from outside, so the mapping must say;
    // a number box and an unwired inlet hold their own values.
    for name, _ in g.controls do
        if not (m.controls |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: the mapping gives no value for control '{name}'"

/// The stream pins of the design's `i`th stream, in and out.
let streamPinsOf (g: Graph) (i: int) : StreamPins * StreamPins =
    streamPins $"in%d{i}" (layoutOfList g.inputs), streamPins $"out%d{i}" (layoutOfList g.outputs)

/// Play the mapping's recording through the graph's design in the simulator
/// — into every stream the design has, the same recording each — and return
/// what each output box heard, stream by stream. `idleLimit` bounds the wait
/// for a beat that never comes, so a design that deadlocks fails rather than
/// hangs.
let runInSimAll (idleLimit: int) (g: Graph) (m: SimMapping) : WavData list =
    check g m
    let design = elaborate g
    let sim = Sim design.def

    for name, value in startingValues g @ m.controls do
        sim.Poke(name, value)

    let devices =
        [ for i in 1 .. g.streams ->
              let input, output = streamPinsOf g i
              WavStreamDevice(sim, input, output, m.source) ]

    let driven = devices |> List.map (fun d -> d :> ISimDevice)
    let heardSoFar () = devices |> List.sumBy (fun d -> d.FramesHeard)
    let mutable idle = 0
    let mutable lastHeard = 0

    while devices |> List.exists (fun d -> d.FramesHeard < m.source.FrameCount) && idle < idleLimit do
        driven |> List.iter (fun d -> d.Drive())
        sim.Tick()
        driven |> List.iter (fun d -> d.Sample())

        if heardSoFar () = lastHeard then
            idle <- idle + 1
        else
            idle <- 0
            lastHeard <- heardSoFar ()

    let heard = devices |> List.map (fun d -> d.Output)
    m.outputPath |> Option.iter (fun path -> writeWavFile path heard.Head)
    heard

/// `runInSimAll`, the first stream's hearing.
let runInSim (idleLimit: int) (g: Graph) (m: SimMapping) : WavData = (runInSimAll idleLimit g m).Head

// ---------------------------------------------------------------------------
// An image on the boundary: rows in, rows out.

/// What satisfies a row boundary in the simulator: a grey image, a row a
/// beat, and where to put what came out. A stencil's halo is the unit's own
/// business, so a frame on the boundary is exactly the image's rows.
type ImageMapping =
    { image: Grey
      outputPath: string option }

/// The row pin an image of `width` pixels needs.
let rowPin (width: int) : string * NumberFormat = "row", unsignedInt (width * 8)

let private checkImage (g: Graph) (m: ImageMapping) =
    if g.streams < 1 then
        failwith $"{g.name}: a design has at least one stream"

    for side, pins in [ "input", g.inputs; "output", g.outputs ] do
        if pins <> [ rowPin m.image.width ] then
            failwith $"{g.name}: an image %d{m.image.width} pixels wide needs the {side} box to be [{describePins [ rowPin m.image.width ]}], and it is [{describePins pins}]"

    for name, _ in g.controls do
        failwith $"{g.name}: the image mapping gives no value for control '{name}'"

/// The image's rows as beats.
let imageBeats (m: ImageMapping) : BigInteger list list =
    [ for r in 0 .. m.image.height - 1 -> [ rowBits (m.image.Row r) ] ]

/// Play the image through the design's rows, every stream the same, and
/// return what the first output box drew: as many rows as went in.
let runImageInSim (idleLimit: int) (g: Graph) (m: ImageMapping) : Grey =
    checkImage g m
    let sim = Sim (elaborate g).def

    for name, value in startingValues g do
        sim.Poke(name, value)

    let beats = imageBeats m

    let devices =
        [ for i in 1 .. g.streams ->
              let input, output = streamPinsOf g i
              BeatStreamDevice(sim, input, output, beats, m.image.height) ]

    drive sim devices idleLimit
    let heard = devices.Head.Heard |> List.truncate m.image.height |> List.map List.head
    let out = ofRows m.image.width heard
    m.outputPath |> Option.iter (fun path -> writePgm path out)
    out

// ---------------------------------------------------------------------------
// A table on the boundary: a row a beat, columns by pin name.

type CsvMapping =
    { table: Table
      controls: (string * uint64) list
      outputPath: string option }

let private checkCsv (g: Graph) (m: CsvMapping) =
    if g.streams < 1 then
        failwith $"{g.name}: a design has at least one stream"

    let columns = String.concat ", " m.table.columns

    for name, _ in g.inputs do
        if not (List.contains name m.table.columns) then
            failwith $"{g.name}: the table has no column '{name}' for the input box's pin — its columns are [{columns}]"

    let ports = controlPorts g

    for name, _ in m.controls do
        if not (ports |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: no control called '{name}' — it has [{describePins ports}]"

    for name, _ in g.controls do
        if not (m.controls |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: the mapping gives no value for control '{name}'"

/// The table's rows as beats: each input pin's column, read in the pin's
/// format. Refused naming the row, the column and the cell.
let csvBeats (g: Graph) (t: Table) : BigInteger list list =
    [ for r, row in List.indexed t.rows ->
          [ for name, f in g.inputs ->
                let at = List.findIndex ((=) name) t.columns

                if at >= row.Length then
                    failwith $"row %d{r + 1}: no cell for '{name}'"

                match parseCell f row[at] with
                | Ok bits -> bits
                | Error why -> failwith $"row %d{r + 1}, '{name}': {why}" ] ]

/// Play the table through the design, a row a beat, and return the table
/// the output box wrote: the output pins as columns, one row a beat heard.
let runCsvInSim (idleLimit: int) (g: Graph) (m: CsvMapping) : Table =
    checkCsv g m
    let sim = Sim (elaborate g).def

    for name, value in startingValues g @ m.controls do
        sim.Poke(name, value)

    let beats = csvBeats g m.table

    let devices =
        [ for i in 1 .. g.streams ->
              let input, output = streamPinsOf g i
              BeatStreamDevice(sim, input, output, beats, beats.Length) ]

    drive sim devices idleLimit

    let out =
        { columns = g.outputs |> List.map fst
          rows = devices.Head.Heard |> List.map (fun beat -> List.map2 (fun (_, f) bits -> formatCell f bits) g.outputs beat) }

    m.outputPath |> Option.iter (fun path -> writeCsv path out)
    out
