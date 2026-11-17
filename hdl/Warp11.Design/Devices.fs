/// What satisfies a graph's boundary in a given world.
///
/// A graph declares what it needs — a stream in and a stream out, of such
/// fields, and these controls. It says nothing about where the beats come
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

/// The one stream in and the one stream out a device drives. A design with
/// more on its boundary has no device yet, and says so.
let boundaryOf (g: Graph) : BoundaryStream * BoundaryStream =
    match g.inputs, g.outputs with
    | [ input ], [ output ] -> input, output
    | ins, outs -> failwith $"{g.name}: a device drives one stream in and one out, and the design has %d{ins.Length} in and %d{outs.Length} out"

/// What a stereo WAV device needs the boundary to be: two signed samples
/// each way.
let private stereo = [ "left", signedInt sampleWidth; "right", signedInt sampleWidth ]

/// How many stereo frames a beat of `fields` carries: one for a stereo
/// stream, k for k frames side by side — pairs of signed samples, whatever
/// they are called. `None` when the fields are not that.
let framesABeat (fields: Fields) : int option =
    let sample = signedInt sampleWidth

    if not fields.IsEmpty && fields.Length % 2 = 0 && fields |> List.forall (fun (_, f) -> f = sample) then
        Some(fields.Length / 2)
    else
        None

/// Does the mapping fit what the graph declares? Said before anything runs,
/// naming the side that does not.
let private check (g: Graph) (m: SimMapping) =
    // A WAV's header holds a whole number of hertz; the design's rate need not
    // be one, so the two agree when they round to the same.
    if round g.sampleRate <> float m.source.sampleRate then
        failwith $"{g.name}: the design is made for %g{g.sampleRate} Hz and the recording is %d{m.source.sampleRate} Hz"

    let input, output = boundaryOf g

    for stream in [ input; output ] do
        if (framesABeat stream.fields).IsNone then
            failwith $"{g.name}: a WAV device needs '{stream.name}' to be [{describePins stereo}], or a whole number of such frames side by side, and it is [{describePins stream.fields}]"

    if framesABeat input.fields <> framesABeat output.fields then
        failwith $"{g.name}: a WAV device needs as many frames a beat out of '{output.name}' as into '{input.name}'"

    let ports = controlPorts g

    for name, _ in m.controls do
        if not (ports |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: no control called '{name}' — it has [{describePins ports}]"

    // The design's own controls come from outside, so the mapping must say;
    // a number box holds its own value.
    for name, _ in g.controls do
        if not (m.controls |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: the mapping gives no value for control '{name}'"

/// The pins of the design's stream in and its stream out.
let streamPinsOf (g: Graph) : StreamPins * StreamPins =
    let input, output = boundaryOf g
    streamPins input.name (layoutOfList input.fields), streamPins output.name (layoutOfList output.fields)

/// Play the mapping's recording through the graph's design in the simulator
/// and return what came out. `idleLimit` bounds the wait for a beat that
/// never comes, so a design that deadlocks fails rather than hangs.
let runInSim (idleLimit: int) (g: Graph) (m: SimMapping) : WavData =
    check g m
    let design = elaborate g
    let sim = Sim design.def

    for name, value in startingValues g @ m.controls do
        sim.Poke(name, value)

    let input, output = streamPinsOf g
    let frames = framesABeat (fst (boundaryOf g)).fields |> Option.get

    let heard =
        if frames = 1 then
            let device = WavStreamDevice(sim, input, output, m.source)
            let driven = device :> ISimDevice
            let mutable idle = 0
            let mutable lastHeard = 0

            while device.FramesHeard < m.source.FrameCount && idle < idleLimit do
                driven.Drive()
                sim.Tick()
                driven.Sample()

                if device.FramesHeard = lastHeard then
                    idle <- idle + 1
                else
                    idle <- 0
                    lastHeard <- device.FramesHeard

            device.Output
        else
            // Several frames a beat: the recording's frames taken `frames`
            // at a time, the last beat filled out with silence, and what
            // comes back cut to the recording's length.
            let sampleAt (i: int) = if i < m.source.samples.Length then m.source.samples[i] else 0s
            let beatCount = (m.source.FrameCount + frames - 1) / frames

            let beats =
                [ for b in 0 .. beatCount - 1 ->
                      [ for lane in 0 .. 2 * frames - 1 -> BigInteger(toSampleBits (sampleAt (b * 2 * frames + lane))) ] ]

            let device = BeatStreamDevice(sim, input, output, beats, beatCount)
            drive sim [ device ] idleLimit

            let samples =
                [| for beat in device.Heard do
                       for lane in beat -> fromSampleBits (uint64 lane) |]

            { m.source with samples = Array.truncate m.source.samples.Length samples }

    m.outputPath |> Option.iter (fun path -> writeWavFile path heard)
    heard

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
    let input, output = boundaryOf g

    for stream in [ input; output ] do
        if stream.fields <> [ rowPin m.image.width ] then
            failwith $"{g.name}: an image %d{m.image.width} pixels wide needs '{stream.name}' to be [{describePins [ rowPin m.image.width ]}], and it is [{describePins stream.fields}]"

    for name, _ in g.controls do
        failwith $"{g.name}: the image mapping gives no value for control '{name}'"

/// The image's rows as beats.
let imageBeats (m: ImageMapping) : BigInteger list list =
    [ for r in 0 .. m.image.height - 1 -> [ rowBits (m.image.Row r) ] ]

/// Play the image through the design's rows and return what it drew: as
/// many rows as went in.
let runImageInSim (idleLimit: int) (g: Graph) (m: ImageMapping) : Grey =
    checkImage g m
    let sim = Sim (elaborate g).def

    for name, value in startingValues g do
        sim.Poke(name, value)

    let beats = imageBeats m

    let input, output = streamPinsOf g
    let devices = [ BeatStreamDevice(sim, input, output, beats, m.image.height) ]

    drive sim devices idleLimit
    let heard = devices.Head.Heard |> List.truncate m.image.height |> List.map List.head
    let out = ofRows m.image.width heard
    m.outputPath |> Option.iter (fun path -> writePgm path out)
    out

// ---------------------------------------------------------------------------
// A frame the design draws: a count in, chunks of pixels out.

/// What satisfies a generator's boundary in the simulator: nothing but a
/// count of beats, the index on the input box's one field, and the output
/// box's one field read as pixels — a byte each, as many a beat as the
/// field holds — assembled into a frame `width` pixels wide. The board's
/// `viaCount` path, in the simulator.
type FrameMapping =
    { width: int
      height: int
      controls: (string * uint64) list
      outputPath: string option }

/// Pixels a beat of `pins` carries: the one output field, a byte a pixel.
let pixelsPerBeatOf (pins: (string * NumberFormat) list) : int =
    match pins with
    | [ _, f ] when f.totalWidth % 8 = 0 -> f.totalWidth / 8
    | _ -> failwith $"a frame needs the output box to be one field of whole bytes, and it is [{describePins pins}]"

/// Beats a frame of `m` takes from a design with `outputs`: rows of chunks.
let frameBeats (m: FrameMapping) (outputs: (string * NumberFormat) list) : int =
    let perBeat = pixelsPerBeatOf outputs
    m.height * ((m.width + perBeat - 1) / perBeat)

let private checkFrame (g: Graph) (m: FrameMapping) =
    let input, output = boundaryOf g

    match input.fields with
    | [ _ ] -> ()
    | pins -> failwith $"{g.name}: a counted input is one field, the beat index, and '{input.name}' is [{describePins pins}]"

    pixelsPerBeatOf output.fields |> ignore
    let ports = controlPorts g

    for name, _ in m.controls do
        if not (ports |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: no control called '{name}' — it has [{describePins ports}]"

    for name, _ in g.controls do
        if not (m.controls |> List.exists (fun (n, _) -> n = name)) then
            failwith $"{g.name}: the mapping gives no value for control '{name}'"

/// The count as beats: the index on the one field.
let countBeats (n: int) : BigInteger list list = [ for k in 0 .. n - 1 -> [ BigInteger k ] ]

/// Draw the frame: the count into the design, the chunks out, the image.
let runFrameInSim (idleLimit: int) (g: Graph) (m: FrameMapping) : Grey =
    checkFrame g m
    let sim = Sim (elaborate g).def

    for name, value in startingValues g @ m.controls do
        sim.Poke(name, value)

    let _, drawn = boundaryOf g
    let beats = frameBeats m drawn.fields

    let input, output = streamPinsOf g
    let devices = [ BeatStreamDevice(sim, input, output, countBeats beats, beats) ]

    drive sim devices idleLimit
    let heard = devices.Head.Heard |> List.truncate beats |> List.map List.head
    let out = ofChunks m.width (pixelsPerBeatOf drawn.fields) heard
    m.outputPath |> Option.iter (fun path -> writePgm path out)
    out

// ---------------------------------------------------------------------------
// A table on the boundary: a row a beat, columns by pin name.

type CsvMapping =
    { table: Table
      controls: (string * uint64) list
      outputPath: string option }

let private checkCsv (g: Graph) (m: CsvMapping) =
    let columns = String.concat ", " m.table.columns

    for name, _ in (fst (boundaryOf g)).fields do
        if not (List.contains name m.table.columns) then
            failwith $"{g.name}: the table has no column '{name}' for the input stream's field — its columns are [{columns}]"

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
          [ for name, f in (fst (boundaryOf g)).fields ->
                let at = List.findIndex ((=) name) t.columns

                if at >= row.Length then
                    failwith $"row %d{r + 1}: no cell for '{name}'"

                match parseCell f row[at] with
                | Ok bits -> bits
                | Error why -> failwith $"row %d{r + 1}, '{name}': {why}" ] ]

/// Play the table through the design, a row a beat, and return the table
/// the output stream wrote: its fields as columns, one row a beat heard.
let runCsvInSim (idleLimit: int) (g: Graph) (m: CsvMapping) : Table =
    checkCsv g m
    let sim = Sim (elaborate g).def

    for name, value in startingValues g @ m.controls do
        sim.Poke(name, value)

    let beats = csvBeats g m.table

    let input, output = streamPinsOf g
    let devices = [ BeatStreamDevice(sim, input, output, beats, beats.Length) ]

    drive sim devices idleLimit
    let _, written = boundaryOf g

    let out =
        { columns = written.fields |> List.map fst
          rows = devices.Head.Heard |> List.map (fun beat -> List.map2 (fun (_, f) bits -> formatCell f bits) written.fields beat) }

    m.outputPath |> Option.iter (fun path -> writeCsv path out)
    out
