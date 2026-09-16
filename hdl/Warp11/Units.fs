/// Units that cost cycles, for the trial. A sequential unit presents a
/// stream — the only way anything here may say it takes time — and takes an
/// instance name, because there will be copies.
module Warp11.Units

open Warp11
open Warp11.Fu

/// Multiply by shift-and-add: `w` cycles a product, one product at a time,
/// no DSP block. The classic bit-serial form — the accumulator starts as
/// `{0, multiplier}`, and each step adds the multiplicand into its top half
/// if the low bit is set, then shifts the whole thing right by one. After
/// `w` steps the accumulator is the product.
///
/// The shape is the three-state worker every one-beat stage here has: a
/// beat accepted, work done, the result offered until taken — and the offer
/// taken this cycle admits the next beat this cycle.
let shiftAddMultiplier (name: string) (w: int) : Stream<Expr * Expr> -> Stream<Expr> =
    fun (s: Stream<Expr * Expr>) ->
        let st = machine $"{name}_state" [ Accepting; Working; Offering ]
        let ready = wireBit $"{name}_ready"
        registerStreamReady ready

        let taken = st.Is Offering &&& ready
        (st.Is Accepting ||| taken) ==> s.ready
        let accept = s.valid &&& s.ready

        let acc = reg $"{name}_acc" (2 * w)
        let mcand = reg $"{name}_mcand" w
        let stepBits = ceilLog2 w
        let step = reg $"{name}_step" stepBits

        let a, b = s.payload
        let hi = slice (2 * w - 1) w acc
        let lo = slice (w - 1) 0 acc
        let lowBit = slice 0 0 acc
        // One bit wider than `hi`, so the carry out of the add survives the shift.
        let hiNext = wire $"{name}_hi_next" (w + 1)
        mux lowBit (add (pad (w + 1) hi) (pad (w + 1) mcand)) (pad (w + 1) hi) ==> hiNext
        // `{hiNext, lo}` is 2w+1 bits; keeping [2w:1] is the shift. Declared,
        // because a slice takes a named signal.
        let shifted = wire $"{name}_shifted" (2 * w + 1)
        cat hiNext lo ==> shifted

        ifElse
            [ (accept,
               fun () ->
                   cat (lit 0UL w) b ==> acc
                   a ==> mcand
                   lit 0UL stepBits ==> step
                   st.Goto Working)
              (st.Is Working,
               fun () ->
                   slice (2 * w) 1 shifted ==> acc
                   step + lit 1UL stepBits ==> step
                   If (eq step (lit (uint64 (w - 1)) stepBits)) (fun () -> st.Goto Offering))
              (taken, fun () -> st.Goto Accepting) ]

        { payload = acc
          valid = st.Is Offering
          ready = ready
          layout = layout1 ("product", 2 * w) }

/// `audioGain` as a box: a stereo stream through it, `volume` and `mute` held
/// beside it. The library's instance-as-a-function is the law verbatim; this
/// line is the whole cost of making a module a box.
let gainModule: Fu<Expr * Expr, Expr * Expr> =
    let stereo = pins2 ("left", signedInt sampleWidth) ("right", signedInt sampleWidth)

    moduleUnit "gain" stereo stereo [ "volume", unsignedInt 16; "mute", unsignedInt 1 ] (fun instance controls s ->
        match controls with
        | [ volume; mute ] -> audioGain "AudioGain" instance volume mute s
        | _ -> failwith "gain: volume and mute")

// ---------------------------------------------------------------------------
// Three small units the first designs were placed with, and the palette
// still offers: a 16-bit multiply and a 32-bit add that cost nothing to
// copy, and the shift-and-add multiplier that costs sixteen cycles a
// product.

let multiply16 =
    fu
        "mul16"
        (pins2 ("a", unsignedInt 16) ("b", unsignedInt 16))
        (pins1 ("product", Warp11.Number.productFormat (unsignedInt 16) (unsignedInt 16)))
        (fun (a, b) -> mul a b)

let add32 = fu "add32" (pins2 ("x", unsignedInt 32) ("y", unsignedInt 32)) (pins1 ("sum", unsignedInt 33)) (fun (x, y) -> add (pad 33 x) (pad 33 y))

let shiftAddMultiply16 =
    fuSequential "smul16" (pins2 ("a", unsignedInt 16) ("b", unsignedInt 16)) (pins1 ("product", unsignedInt 32)) (fun instance -> shiftAddMultiplier instance 16)

// ---------------------------------------------------------------------------
// Image units: a row of 8-bit pixels is one beat, and a stencil reads a
// window of rows. `lineWindow` leaves the vertical halo to the loader — a
// frame is `rows + 2` beats, the first and last rows repeated for a clamp —
// and a unit is one beat in, one beat out, so the unit supplies the halo
// itself and a frame on its boundary is exactly its rows.

/// The clamp halo, supplied inside the unit: the first row of every frame
/// goes out twice before the frame, the last twice after it, so `rows` beats
/// in become `rows + 2` beats for a 3-row window and the window's `rows`
/// results are one per row that came in. A branching machine, not a count:
/// a row is held one cycle to be sent again.
let clampHalo (name: string) (rows: int) (s: Stream<Expr>) : Stream<Expr> =
    let w = width s.payload
    let countBits = bitsToHold rows
    let count = reg $"{name}_count" countBits
    let held = reg $"{name}_held" w
    let copying = regBit $"{name}_copying"
    let haloSent = regBit $"{name}_halo_sent"
    let ready = wireBit $"{name}_ready"
    registerStreamReady ready

    let atFirst = eq count (lit 0UL countBits) &&& bnot haloSent
    let atLast = eq count (lit (uint64 (rows - 1)) countBits)

    // The source is taken when its row goes out as itself: not while a copy
    // goes out, and not while the first row goes out as the halo before it.
    (ready &&& bnot copying &&& bnot atFirst) ==> s.ready

    let outValid = copying ||| s.valid
    let fired = outValid &&& ready

    ifElse
        [ (fired &&& copying,
           fun () ->
               lit 0UL 1 ==> copying
               lit 0UL countBits ==> count
               lit 0UL 1 ==> haloSent)
          (fired &&& atFirst, fun () -> lit 1UL 1 ==> haloSent)
          (fired &&& atLast,
           fun () ->
               s.payload ==> held
               lit 1UL 1 ==> copying)
          (fired, fun () -> count + lit 1UL countBits ==> count) ]

    // A declared row out: the window slices its columns, and a slice takes
    // a named signal.
    let row = wire $"{name}_row" w
    mux copying held s.payload ==> row

    { payload = row
      valid = outValid
      ready = ready
      layout = s.layout }

/// A 3×3 box blur over rows of `columns` pixels, borders replicated: each
/// output pixel the mean of its neighbourhood, `sum >>> 3` standing in for
/// /9 as blurs on silicon do. `Warp11.Designs`' `pixelBlur` for any width,
/// and any frame height, as a module of its own so a design may place it
/// more than once.
let pixelBlurDef (name: string) (columns: int) (rows: int) : TypedModule<StreamInputPorts<Expr> * StreamOutputPorts<Expr>> =
    let rowLayout = layout1 ("row", columns * 8)

    defModule
        name
        (fun p -> streamInputPorts p "in" rowLayout, streamOutputPorts p "out" rowLayout)
        (fun (inPorts, outPorts) ->
            let blurRow (win: Expr list) =
                match win with
                | [ above; centre; below ] ->
                    catAll
                        [ for c in columns - 1 .. -1 .. 0 ->
                              let cells =
                                  [ for row in [ above; centre; below ] do
                                        for k in 0..2 -> slice ((c + k) * 8 + 7) ((c + k) * 8) row ]

                              let sum = wire $"blur_sum%d{c}" 12
                              (cells |> List.map (pad 12) |> List.reduce (+)) ==> sum
                              slice 10 3 sum ]
                | _ -> failwith "pixelBlur: a 3-row window"

            streamSource inPorts
            |> clampHalo "halo" rows
            |> lineWindow
                { rows = 3
                  edgeColumns = 1
                  cellBits = 8
                  edge = Edge.Clamp }
                rows
            |> Stream.mapTo rowLayout blurRow
            |> streamSink outPorts)

/// The blur as a unit: a row in, a row out, the halo its own.
let blurUnit (columns: int) (rows: int) : Fu<Expr, Expr> =
    let rowPins = pins1 ("row", unsignedInt (columns * 8))

    moduleUnit "blur" rowPins rowPins [] (fun instance _ s ->
        let inPorts, outPorts = (pixelBlurDef "PixelBlur" columns rows).NewNamed instance
        streamThroughInstance inPorts outPorts s)
