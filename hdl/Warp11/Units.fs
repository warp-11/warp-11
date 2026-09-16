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

