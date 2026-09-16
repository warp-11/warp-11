/// A palette entry is a **factory**: a unit made from creation arguments —
/// Pure Data's `[biquad~ 1000 0.7]`, the numbers typed into the box. An
/// argument configures the unit at elaboration (a filter's corner, a delay
/// line's capacity) and a change to one is a new design; a control inlet is
/// a value the module holds between beats and is wired. The line between
/// them is the library's own: `audioEcho`'s `capacity` is an argument and
/// its `delay` a port, for the reason its doc gives.
///
/// Arguments are text, so a saved design holds them as they were typed and
/// `make` needs no closure; each factory parses its own and refuses naming
/// the parameter. A filter is designed **for the design's sample rate**, which
/// `make` is handed — coefficients fix a frequency in cycles per sample.
module Warp11.Factories

open Warp11
open Warp11.Fu
open Warp11.Units
open Warp11.Pedal

type ParameterKind =
    | IntParameter
    | FloatParameter
    /// A list of numbers, comma-separated.
    | FloatsParameter
    | ChoiceParameter of string list

/// One creation argument: what it is called, what it accepts, what a fresh
/// box starts with.
type Parameter =
    { name: string
      kind: ParameterKind
      ``default``: string
      about: string }

/// A box's arguments, by parameter name, as typed.
type Arguments = Map<string, string>

type Factory =
    { name: string
      parameters: Parameter list
      /// The unit, for the design's sample rate and these arguments — every
      /// parameter present — or the first thing wrong with them.
      make: float -> Arguments -> Result<ErasedFu, string>
      /// The same unit as F# source: the typed constructor applied to the
      /// arguments as values, with the design's rate as `sampleRate`. What
      /// the export prints, so a design leaves the GUI naming its units the
      /// way a hand-written one does. `make` and `print` parse the arguments
      /// once between them, so they cannot disagree.
      print: float -> Arguments -> Result<string, string> }

/// What a fresh box of the factory holds.
let defaults (f: Factory) : Arguments =
    f.parameters |> List.map (fun p -> p.name, p.``default``) |> Map.ofList

/// The arguments a box holds, every parameter present: the factory's
/// defaults under what the box says.
let complete (f: Factory) (arguments: Arguments) : Arguments =
    (defaults f, arguments) ||> Map.fold (fun acc k v -> Map.add k v acc)

/// A unit that takes no arguments, as a factory: the typed value and the
/// F# name it goes by.
let plain (symbol: string) (unit: Fu<'a, 'r>) : Factory =
    let erased = erase unit

    { name = unit.name
      parameters = []
      make = fun _ _ -> Ok erased
      print = fun _ _ -> Ok symbol }

/// A factory over a typed constructor: `parse` reads the arguments once,
/// `build` makes the unit from what it read, `show` prints the same call.
let private factory (name: string) (parameters: Parameter list) (parse: float -> Arguments -> Result<'p, string>) (build: float -> 'p -> Fu<'a, 'r>) (show: 'p -> string) : Factory =
    { name = name
      parameters = parameters
      make = fun rate args -> parse rate args |> Result.map (build rate >> erase)
      print = fun rate args -> parse rate args |> Result.map show }

/// A float as F# source, always with a point so it reads as a float.
let showFloat (x: float) : string =
    let text = x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
    if text.Contains '.' || text.Contains 'E' || text.Contains 'e' then text else text + ".0"

// ---------------------------------------------------------------------------
// Parsing an argument, refusing with the parameter's name.

let private invariant = System.Globalization.CultureInfo.InvariantCulture

let private intArg (name: string) (args: Arguments) : Result<int, string> =
    match System.Int32.TryParse(Map.find name args, System.Globalization.NumberStyles.Integer, invariant) with
    | true, v -> Ok v
    | _ -> Error $"{name}: '{Map.find name args}' is not a whole number"

let private floatArg (name: string) (args: Arguments) : Result<float, string> =
    match System.Double.TryParse(Map.find name args, System.Globalization.NumberStyles.Float, invariant) with
    | true, v -> Ok v
    | _ -> Error $"{name}: '{Map.find name args}' is not a number"

let private floatsArg (name: string) (args: Arguments) : Result<float list, string> =
    let text = Map.find name args

    text.Split([| ','; ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
    |> List.ofArray
    |> List.fold
        (fun acc part ->
            match acc, System.Double.TryParse(part, System.Globalization.NumberStyles.Float, invariant) with
            | Ok xs, (true, v) -> Ok(v :: xs)
            | Ok _, _ -> Error $"{name}: '{part}' is not a number"
            | Error e, _ -> Error e)
        (Ok [])
    |> Result.map List.rev

let private choiceArg (name: string) (choices: string list) (args: Arguments) : Result<string, string> =
    let text = Map.find name args

    if List.contains text choices then
        Ok text
    else
        let listed = String.concat ", " choices
        Error $"{name}: '{text}' is not one of {listed}"

let private positive (name: string) (v: float) : Result<float, string> =
    if v > 0.0 then Ok v else Error $"{name}: must be above zero, not {v}"

let private belowNyquist (name: string) (rate: float) (v: float) : Result<float, string> =
    if v > 0.0 && v < rate / 2.0 then
        Ok v
    else
        Error $"{name}: %g{v} Hz is not between 0 and half the sample rate, %g{rate / 2.0}"

// ---------------------------------------------------------------------------
// The audio units: a stereo stream through each, controls beside it. One
// line each, as `gainModule` is — the library's instance-as-a-function is
// the law verbatim.

let private stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

let private literals (width: int) (values: uint64 list) = values |> List.map (fun v -> lit v width)

/// A biquad section per channel with its coefficients designed for `rate`
/// from the shape, corner, Q and gain — no coefficient inlets, only the
/// numbers a person thinks in. The typed unit behind the `eq` box.
let eqSection (shape: EqType) (fc: float) (q: float) (gainDb: float) (rate: float) : Fu<Expr * Expr, Expr * Expr> =
    let coefficients = toQ230 (rbjDesign shape fc q gainDb rate)
    moduleUnit "eq" stereo stereo [] (fun instance _ s -> audioEqBand "AudioEqBand" instance (literals biquadCoeffWidth coefficients) s)

let limiterUnit: Fu<Expr * Expr, Expr * Expr> =
    moduleUnit "limiter" stereo stereo [ "threshold", signedInt sampleWidth ] (fun instance controls s ->
        match controls with
        | [ threshold ] -> audioLimiter "AudioLimiter" instance threshold s
        | _ -> failwith "limiter: threshold")

let echoUnit (capacity: int) : Fu<Expr * Expr, Expr * Expr> =
    moduleUnit "echo" stereo stereo [ "delay", unsignedInt (log2Exact capacity); "feedback", unsignedInt 16 ] (fun instance controls s ->
        match controls with
        | [ delay; feedback ] -> audioEcho "AudioEcho" capacity instance delay feedback s
        | _ -> failwith "echo: delay and feedback")

let compressorUnit: Fu<Expr * Expr, Expr * Expr> =
    moduleUnit
        "compressor"
        stereo
        stereo
        [ "threshold", unsignedInt sampleWidth; "ratio", unsignedInt 8; "attack", unsignedInt 16; "releaseRate", unsignedInt 16; "makeup", unsignedInt 16 ]
        (fun instance controls s ->
            match controls with
            | [ threshold; ratio; attack; releaseRate; makeup ] ->
                audioCompressor
                    "AudioCompressor"
                    instance
                    { threshold = threshold
                      ratio = ratio
                      attack = attack
                      releaseRate = releaseRate
                      makeup = makeup }
                    s
            | _ -> failwith "compressor: five controls")

let firUnit (taps: int) (lowPass: float) (highPass: float) (rate: float) : Fu<Expr * Expr, Expr * Expr> =
    moduleUnit "fir" stereo stereo [ "preset", unsignedInt 2 ] (fun instance controls s ->
        match controls with
        | [ preset ] -> audioFir "AudioFir" taps { sampleRate = rate; lowPass = lowPass; highPass = highPass } instance preset s
        | _ -> failwith "fir: preset")

let multibandUnit (crossovers: float list) (rate: float) : Fu<Expr * Expr, Expr * Expr> =
    let gains prefix = [ for i in 0 .. multibandBands - 1 -> $"{prefix}%d{i}", unsignedInt 16 ]

    moduleUnit
        "multiband"
        stereo
        stereo
        ([ "threshold", unsignedInt sampleWidth; "ratio", unsignedInt 8; "attack", unsignedInt 16; "releaseRate", unsignedInt 16 ] @ gains "lg" @ gains "rg")
        (fun instance controls s ->
            match controls with
            | threshold :: ratio :: attack :: releaseRate :: gains when gains.Length = 2 * multibandBands ->
                let leftGains, rightGains = List.splitAt multibandBands gains

                multibandCompressor8
                    "MultibandCompressor"
                    crossovers
                    rate
                    instance
                    { threshold = threshold
                      ratio = ratio
                      attack = attack
                      releaseRate = releaseRate
                      leftGains = leftGains
                      rightGains = rightGains }
                    s
                |> fst
            | _ -> failwith "multiband: four controls and the gains")

/// A biquad section per channel, its coefficients designed here from the
/// shape, corner, Q and gain, for the design's rate — so the box has no
/// coefficient inlets, only the numbers a person thinks in.
let eqShapes = [ "peaking"; "lowshelf"; "highshelf"; "lowpass"; "highpass" ]

let private eqShape (text: string) : EqType =
    match text with
    | "peaking" -> Peaking
    | "lowshelf" -> LowShelf
    | "highshelf" -> HighShelf
    | "lowpass" -> LowPass
    | _ -> HighPass

let private eqShapeSymbol (text: string) : string =
    match eqShape text with
    | Peaking -> "Peaking"
    | LowShelf -> "LowShelf"
    | HighShelf -> "HighShelf"
    | LowPass -> "LowPass"
    | HighPass -> "HighPass"

let eq: Factory =
    factory
        "eq"
        [ { name = "shape"; kind = ChoiceParameter eqShapes; ``default`` = "peaking"; about = "the cookbook response" }
          { name = "fc"; kind = FloatParameter; ``default`` = "1000"; about = "corner or centre, in hertz" }
          { name = "q"; kind = FloatParameter; ``default`` = "0.707"; about = "quality factor" }
          { name = "gain"; kind = FloatParameter; ``default`` = "0"; about = "decibels, for the peaking and shelving shapes" } ]
        (fun rate args ->
            match choiceArg "shape" eqShapes args, floatArg "fc" args |> Result.bind (belowNyquist "fc" rate), floatArg "q" args |> Result.bind (positive "q"), floatArg "gain" args with
            | Ok shape, Ok fc, Ok q, Ok gain -> Ok(shape, fc, q, gain)
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e)
        (fun rate (shape, fc, q, gain) -> eqSection (eqShape shape) fc q gain rate)
        (fun (shape, fc, q, gain) -> $"eqSection {eqShapeSymbol shape} {showFloat fc} {showFloat q} {showFloat gain} sampleRate")

let limiter: Factory = plain "limiterUnit" limiterUnit

let echo: Factory =
    factory
        "echo"
        [ { name = "capacity"; kind = IntParameter; ``default`` = "16384"; about = "the delay line's length in frames, a power of two" } ]
        (fun _ args ->
            intArg "capacity" args
            |> Result.bind (fun capacity ->
                if isPowerOfTwo capacity && capacity >= delayBufferMinimum then
                    Ok capacity
                else
                    Error $"capacity: %d{capacity} is not a power of two of at least %d{delayBufferMinimum}"))
        (fun _ capacity -> echoUnit capacity)
        (fun capacity -> $"echoUnit %d{capacity}")

let compressor: Factory = plain "compressorUnit" compressorUnit

let fir: Factory =
    factory
        "fir"
        [ { name = "taps"; kind = IntParameter; ``default`` = "16"; about = "filter length" }
          { name = "lowPass"; kind = FloatParameter; ``default`` = "4000"; about = "the low-pass bank's corner, in hertz" }
          { name = "highPass"; kind = FloatParameter; ``default`` = "300"; about = "the high-pass bank's corner, in hertz" } ]
        (fun rate args ->
            match intArg "taps" args, floatArg "lowPass" args |> Result.bind (belowNyquist "lowPass" rate), floatArg "highPass" args |> Result.bind (belowNyquist "highPass" rate) with
            | Ok taps, _, _ when taps < 2 -> Error $"taps: at least 2, not %d{taps}"
            | Ok taps, Ok lowPass, Ok highPass -> Ok(taps, lowPass, highPass)
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e)
        (fun rate (taps, lowPass, highPass) -> firUnit taps lowPass highPass rate)
        (fun (taps, lowPass, highPass) -> $"firUnit %d{taps} {showFloat lowPass} {showFloat highPass} sampleRate")

let multiband: Factory =
    factory
        "multiband"
        [ { name = "crossovers"
            kind = FloatsParameter
            ``default`` = defaultCrossovers |> List.map (fun x -> x.ToString(invariant)) |> String.concat ", "
            about = $"%d{multibandBands - 1} crossover frequencies in hertz, rising" } ]
        (fun rate args ->
            floatsArg "crossovers" args
            |> Result.bind (fun crossovers ->
                if crossovers.Length <> multibandBands - 1 then
                    Error $"crossovers: %d{multibandBands} bands take %d{multibandBands - 1} crossovers, not %d{crossovers.Length}"
                elif crossovers <> List.sort crossovers then
                    Error "crossovers: must rise"
                else
                    match crossovers |> List.map (belowNyquist "crossovers" rate) |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                    | Some e -> Error e
                    | None -> Ok crossovers))
        (fun rate crossovers -> multibandUnit crossovers rate)
        (fun crossovers ->
            let listed = crossovers |> List.map showFloat |> String.concat "; "
            $"multibandUnit [ {listed} ] sampleRate")

let allpassSection: Factory =
    factory
        "allpass"
        [ { name = "capacity"; kind = IntParameter; ``default`` = "4096"; about = "the delay line's length in frames, a power of two" } ]
        (fun _ args ->
            intArg "capacity" args
            |> Result.bind (fun capacity ->
                if isPowerOfTwo capacity && capacity >= delayBufferMinimum then
                    Ok capacity
                else
                    Error $"capacity: %d{capacity} is not a power of two of at least %d{delayBufferMinimum}"))
        (fun _ capacity -> allpass capacity)
        (fun capacity -> $"allpass %d{capacity}")

/// Every unit the GUI may offer. `erase` is the only way a unit gets in, so
/// a palette cannot disagree with the unit the typed API elaborates.
let palette: Map<string, Factory> =
    [ plain "multiply16" multiply16
      plain "add32" add32
      plain "shiftAddMultiply16" shiftAddMultiply16
      plain "gainModule" gainModule
      eq
      limiter
      echo
      compressor
      fir
      multiband
      plain "mixer" mixer
      plain "waveshaper" waveshaper
      plain "tremolo" tremolo
      allpassSection ]
    |> List.map (fun f -> f.name, f)
    |> Map.ofList
