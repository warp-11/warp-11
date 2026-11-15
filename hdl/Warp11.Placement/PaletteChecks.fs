/// The palette: its units are the typed ones, a box is a factory and its
/// creation arguments, the pedal units do what they say through a drawn
/// design, and a unit written in the GUI travels with the design.
module Warp11.Placement.PaletteChecks

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Pedal
open Warp11.Factories
open Warp11.Graph
open Warp11.Edit
open Warp11.Elaborate
open Warp11.Devices
open Warp11.Placement.Placement
open Warp11.Placement.ChainChecks

// ---------------------------------------------------------------------------
// UD3 — The palette cannot drift from the units.
//
// `erase` is the only way an entry is made, and it reads the typed unit's
// layouts. So the format a GUI shows for a box is the format the typed API
// elaborates — by construction, and checked here once. A unit with sockets
// says them, and what serves them is in the palette beside it.

// CHECK
let paletteIsTheUnits () : bool =
    let unit (name: string) =
        let factory = (units ())[name]

        match factory.make defaultSampleRate (defaults factory) with
        | Ok u -> u
        | Error why -> failwith why

    let sockets (name: string) =
        let factory = (units ())[name]

        match factory.served with
        | Some served -> [ for socket in served.sockets -> socket.name, socket.count, socket.needs.name ]
        | None -> []

    (unit "mul16").operands.fields = multiply16.operands.fields
    && (unit "mul16").results.fields = multiply16.results.fields
    && (unit "add32").results.fields = add32.results.fields
    && (unit "gain").controls = gainModule.controls
    && (match (unit "smul16").law, (unit "gain").law with
        | Sequential _, Sequential _ -> true
        | _ -> false)
    && sockets "gain" = [ "multiply", 2, gainMultiplier.name ]
    && sockets "eq" = [ "multiply", 10, eqMultiplier.name ]
    && sockets "mul16" = []
    && (unit gainMultiplier.name).operands.fields = gainOperands.fields
    && (unit eqMultiplier.name).results.fields = biquadProduct.fields

// ---------------------------------------------------------------------------
// UD8 — A box is a factory and its creation arguments. The arguments reach
// the hardware: a low-pass at 100 Hz silences a 5 kHz tone, and the bands
// as drawn pass it. And what a factory refuses, it refuses naming the
// parameter: a corner above half the rate, a shape that is not one, a
// capacity that is not a power of two, an argument the unit does not have —
// at the box, at the file, and when the design's rate moves under a box.
// (The drawn bands against the typed ones at the bytes is CD2.)

// CHECK
let argumentsMakeTheUnit () : bool =
    let rate = 48_000.0
    // The bands as the edits build them: every argument present, as a
    // file reads them back.
    let g = Examples.threeBandEq rate
    let step (change: Graph -> Result<Graph, string>) (g: Graph) = change g |> Result.defaultWith failwith

    let reopened =
        match DesignFile.parse (DesignFile.write g) with
        | Ok g -> g
        | Error why -> failwith why

    // A 5 kHz tone through a 100 Hz low-pass is silenced; through the bands it is not.
    let tone = toneWav (int rate) 200 5000.0 0.25
    let peak (w: WavData) = w.samples |> Array.map (fun s -> abs (int s)) |> Array.max
    let heard (g: Graph) = runInSim 10_000 g { source = tone; controls = []; outputPath = None }
    let lowPassed = heard (g |> step (setArgument "low" "shape" "lowpass") |> step (setArgument "low" "fc" "100"))

    let refuses (change: Graph -> Result<Graph, string>) (names: string list) =
        match change g with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    let fileRefuses (edit: string -> string) (names: string list) =
        match DesignFile.parse (edit (DesignFile.write g)) with
        | Ok _ -> false
        | Error why -> names |> List.forall (fun n -> why.Contains n)

    let claims =
        [ "reopens", lazy (reopened = g)
          "the bands pass 5 kHz", lazy (peak (heard g) > peak tone / 2)
          "a 100 Hz low-pass silences it", lazy (peak lowPassed < peak tone / 20)
          // a corner above half the rate
          "fc above half the rate", lazy (refuses (setArgument "mid" "fc" "30000") [ "mid"; "fc"; "30000" ])
          // not a number
          "q not a number", lazy (refuses (setArgument "mid" "q" "loud") [ "mid"; "q"; "loud" ])
          // not a shape
          "not a shape", lazy (refuses (setArgument "mid" "shape" "notch") [ "mid"; "shape"; "notch"; "peaking" ])
          // not a parameter
          "not a parameter", lazy (refuses (setArgument "mid" "fq" "1") [ "mid"; "fq"; "shape, fc, q, gain" ])
          // the rate moved under the high band
          "the rate moved", lazy (refuses (setSampleRate 8_000.0) [ "high"; "fc"; "5000" ])
          // an echo whose line is not a power of two
          "echo capacity",
          lazy (refuses (fun g -> addBox "echo" (0.0, 0.0) g |> Result.bind (fun (g, n) -> setArgument n "capacity" "1000" g)) [ "echo"; "capacity"; "1000" ])
          // a file with an argument the unit does not have, and one the factory refuses
          "file: unknown argument", lazy (fileRefuses (fun t -> t.Replace("\"fc\": \"1000\"", "\"corner\": \"1000\"")) [ "mid"; "corner" ])
          "file: refused argument", lazy (fileRefuses (fun t -> t.Replace("\"fc\": \"1000\"", "\"fc\": \"90000\"")) [ "mid"; "fc"; "90000" ])
          // a mapping at another rate
          "a recording at another rate",
          lazy
              (try
                  runInSim 100 g { source = toneWav 44_100 10 440.0 0.25; controls = []; outputPath = None } |> ignore
                  false
               with e ->
                   e.Message.Contains "48000" && e.Message.Contains "44100") ]

    for name, claim in claims do
        if not claim.Value then
            printfn $"      not so: {name}"

    claims |> List.forall (fun (_, claim) -> claim.Value)

// ---------------------------------------------------------------------------
// UD9 — The pedal units, each on its defining property, through a drawn
// design and the simulator's mapping. A mixer fed the same tone twice at
// unity doubles it; a waveshaper at unity drive lifts a quarter-scale tone
// by the cubic's 1.5 − 0.5x², and at sixteen times drive squares it to full
// scale; a tremolo at full depth swings a tone between its level and
// silence; an all-pass is flat, so a tone leaves at the level it entered.
//
// The mixer takes four fields in one beat, so the tone is broadcast to four
// fields boxes, each picking a channel under the name the mixer calls it,
// and joined: what the field model did by wiring one pin to two.

// CHECK
let pedalUnitsDoWhatTheySay () : bool =
    let rate = int defaultSampleRate

    let chain (name: string) (unit: string) (controls: (string * NumberFormat) list) : Graph =
        { emptyGraph name defaultSampleRate stereoPins.fields with
            controls = controls
            boxes = [ box "u" unit [] ]
            edges =
                [ wire (port "input" "in") (port "u" streamInlet) ]
                @ [ for n, _ in controls -> wire (port "input" n) (port "u" n) ]
                @ [ wire (port "u" streamOutlet) (port "output" "out") ] }

    let mixTwice =
        let picks = [ "a_left", "left"; "a_right", "right"; "b_left", "left"; "b_right", "right" ]
        let controls = [ "a_gain", unsignedInt 16; "b_gain", unsignedInt 16 ]

        { emptyGraph "MixTwice" defaultSampleRate stereoPins.fields with
            controls = controls
            boxes =
                [ for name, field in picks -> box name fieldsBox [ "pick", $"{name} = {field}" ] ]
                @ [ box "four" joinBox [ "inputs", "4" ]; box "u" "mixer" [] ]
            edges =
                [ for name, _ in picks -> wire (port "input" "in") (port name streamInlet) ]
                @ [ for i, (name, _) in List.indexed picks -> wire (port name streamOutlet) (port "four" $"in%d{i}") ]
                @ [ wire (port "four" streamOutlet) (port "u" streamInlet) ]
                @ [ for n, _ in controls -> wire (port "input" n) (port "u" n) ]
                @ [ wire (port "u" streamOutlet) (port "output" "out") ] }

    let tone = toneWav rate 4000 1000.0 0.25
    let peak (samples: int16[]) = samples |> Array.map (fun s -> abs (int s)) |> Array.max
    let inPeak = peak tone.samples

    let heard (g: Graph) (values: (string * uint64) list) =
        (runInSim 100_000 g { source = tone; controls = values; outputPath = None }).samples

    // The same tone into both inputs, both gains unity: twice the level.
    let mixed = heard mixTwice [ "a_gain", gainUnity; "b_gain", gainUnity ]

    let shaper = chain "Shape" "waveshaper" [ "drive", unsignedInt 16 ]
    let shapedClean = heard shaper [ "drive", gainUnity ]
    let shapedHard = heard shaper [ "drive", 16UL * gainUnity ]
    // 1.5x − 0.5x³ at x = 0.25 is 0.3672, i.e. 1.469 times the input.
    let cubic = 1.5 * 0.25 - 0.5 * 0.25 ** 3.0

    // Full depth, one sweep over the 4000-frame tone: the loudest tenth is the
    // tone and the quietest is near silence.
    let trem =
        heard
            (chain "Trem" "tremolo" [ "rate", unsignedInt tremoloPhaseWidth; "depth", unsignedInt 16 ])
            [ "rate", uint64 ((1 <<< tremoloPhaseWidth) / 4000); "depth", gainUnity ]

    let tenth (samples: int16[]) (i: int) =
        let n = samples.Length / 10
        peak samples[i * n .. (i + 1) * n - 1]

    let tremTenths = [ for i in 0..9 -> tenth trem i ]

    // An all-pass at 0.7 over a 100-frame tap: once settled, the tone is at
    // its own level, within a few percent.
    let passed = heard (chain "AllPass" "allpass" [ "delay", unsignedInt 12; "gain", unsignedInt 16 ]) [ "delay", 100UL; "gain", 179UL ]
    let settled = tenth passed 9

    peak mixed = 2 * inPeak
    && abs (float (peak shapedClean) - cubic * 4.0 * float inPeak) < 0.02 * float inPeak
    && peak shapedHard > 32_000
    && List.max tremTenths > inPeak * 9 / 10
    && List.min tremTenths < inPeak / 5
    && abs (settled - inPeak) < inPeak / 20

// ---------------------------------------------------------------------------
// UD16 — A unit written in the GUI travels with the design. Its source is
// in the file; a head with a compiler opens the file and has the unit; a
// head without one refuses, naming the unit. The compiler here is a stand-in
// that knows one unit — the compiler service is the desktop canvas's, and
// its verb `unit file.fs` is the check of that — and the export prints the
// definition above the design, which is compiled and compared.

/// A unit as a person would write it in the GUI: the source, and the value
/// the source defines, made here without a compiler so the library's own
/// checks can hold a written unit.
let private swapSource =
    String.concat
        "\n"
        [ "let swap ="
          "    fu \"swap\" (pins2 (\"left\", signedInt 24) (\"right\", signedInt 24)) (pins2 (\"left\", signedInt 24) (\"right\", signedInt 24))"
          "        (fun (l, r) -> r, l)" ]

let private swap =
    fu "swap" (pins2 ("left", signedInt 24) ("right", signedInt 24)) (pins2 ("left", signedInt 24) ("right", signedInt 24)) (fun (l, r) -> r, l)

/// The gain design with the channels swapped by a written unit before the
/// gain: what a session with `swap` compiled into its palette holds.
let private writtenSwap () : Graph =
    addSessionUnit (written "swap" (erase swap) swapSource) |> ignore
    let step (change: Graph -> Result<Graph, string>) (g: Graph) = change g |> Result.defaultWith failwith

    { gainGraph with name = "SwappedGain" }
    |> step (defineUnit "swap" swapSource)
    |> step (removeEdge (wire (port "input" "in") (port "gain" streamInlet)) >> Ok)
    |> step (addBox "swap" (0.0, 0.0) >> Result.map fst)
    |> step (addEdge (port "input" "in", Out) (port "swap" streamInlet, In))
    |> step (addEdge (port "swap" streamOutlet, Out) (port "gain" streamInlet, In))
    |> fun g -> { g with positions = Map.empty }

// CHECK
let writtenUnitTravels () : bool =
    let g = writtenSwap ()
    let text = DesignFile.write g

    // Without a compiler and without the unit registered: refused. The
    // scope is what takes `swap` back out — `writtenSwap` put it in.
    compileUnit <- None

    let refused =
        Factories.only [] (fun () ->
            match DesignFile.parse text with
            | Error why -> why.Contains "swap" && why.Contains "compiler"
            | Ok _ -> false)

    // With a stand-in compiler: the unit comes back and the design reopens.
    compileUnit <-
        Some(fun name source ->
            if name = "swap" && source = swapSource then
                Ok(written "swap" (erase swap) swapSource)
            else
                Error $"the stand-in compiler knows only swap, not {name}")

    let reopened =
        match DesignFile.parse text with
        | Ok g2 -> g2 = g && (units ()).ContainsKey "swap"
        | Error _ -> false

    compileUnit <- None

    // The exported source carries the definition, once, above the design,
    // and compiles to what the drawing elaborates to.
    let exported =
        match Warp11.Export.export g with
        | Ok source -> source
        | Error why -> failwith why

    let compiled = Warp11.Placement.ExportChecks.compileExports [ g, exported ]
    let tone = toneWav (int defaultSampleRate) 100 440.0 0.25
    let heard = runInSim 10_000 g { source = tone; controls = [ "volume", gainUnity; "mute", 0UL ]; outputPath = None }
    let swappedTone = [| for i in 0 .. tone.samples.Length / 2 - 1 do yield tone.samples[2 * i + 1]; yield tone.samples[2 * i] |]

    refused
    && reopened
    && (exported.Split("let swap =").Length = 2)
    && exported.IndexOf "let swap =" < exported.IndexOf "let swappedGain ="
    && compiled = [ emitDesign (elaborate g).def ]
    && heard.samples = swappedTone
