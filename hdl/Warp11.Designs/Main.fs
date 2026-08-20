/// The executable: the Verilog demo dump with its living checks and the
/// differential-oracle writer, dispatched from main.
module Warp11.Designs.Main

open System.Numerics
open Warp11

let private diffDesigns () =
    [ comparator8
      holdThroughReset
      dynamicShifts
      bitReductions
      constantDivision
      streamDivider
      maskedWrite
      maskedWriteWide
      pipelinedReadSlave
      deepChannelSlave
      twoWindowSlave
      carriedRead
      bufferedStream
      deepBufferedStream
      taggedDivide
      farmedDivide
      add3
      dot2
      dot2Auto
      dot2Ambient
      dot2Inline
      pipelinedDot
      gatedCounter
      streamPipe
      coordPipe
      onCounter
      onPriority
      sequencer
      lfsrSource
      oneHotScan
      mux1HSelect
      edgeDetector
      flowSampler
      dividers
      bitShapes
      loopPipeline
      treeSum
      ramTest
      fillingMemory
      assertedSaturate
      cmdProcessor
      unionRoundTrip
      forkJoin
      signedOps
      xorOps
      satOps
      escapeStep
      escapeStepFixed
      escapeStep28
      wideBeat
      widenOps
      dispatchRoundTrip
      clusteredRoundTrip
      twoStreamSplit
      twoStreamSplitReplicateJoin
      framePipeline
      sweepPipeline 2
      typedPipeline
      probedPipe
      axiWriteMaster
      axiWriteMasterSingle
      axiReadMaster
      axiReadMasterSingle
      axiReadMasterBurst
      axiPulse
      axiScratch
      neighborCount
      regMapScratch
      snapshotConflate
      snapshotDdr
      audioOps
      audioChain
      audioTone
      i2sLoopback
      multibandStage ]

/// Unity settings must be audibly transparent: gain at 1.0x unmuted,
/// compression with a zero slope and 1.0x makeup, and a limiter threshold at
/// full scale. The whole chain then owes back exactly what it was given,
/// delayed only by the compressor's apply pipeline.
///
/// The differential proves the Sim and the Verilog agree; it cannot notice
/// that both round a Q-format shift the wrong way, or that a saturation clips
/// at the wrong bound. This is the check that pins the arithmetic — every
/// scale factor in three stages has to be exactly right for a sample to
/// survive the trip unchanged.
let private audioUnityPassthrough () : bool =
    let sim = Sim(audioChain)
    sim.Poke("volume", gainUnity)
    sim.Poke("mute", 0UL)
    sim.Poke("threshold", 0UL)
    sim.Poke("ratio", 0UL) // zero slope — no gain reduction whatever the envelope
    sim.Poke("attack", 1UL <<< 15)
    sim.Poke("releaseRate", 1UL <<< 15)
    sim.Poke("makeup", gainUnity)
    sim.Poke("limit", (1UL <<< (sampleWidth - 1)) - 1UL) // full scale — never clamps
    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 1UL)

    // Distinct per channel: feeding both the same value would let a left/right
    // swap anywhere in three stages pass unnoticed.
    let left = [ 1000UL; 2000UL; 4095UL; 7UL; 65535UL; 300UL ]
    let right = [ 11UL; 90210UL; 64UL; 123456UL; 5UL; 8191UL ]
    let seenLeft = ResizeArray<uint64>()
    let seenRight = ResizeArray<uint64>()

    for l, r in List.zip left right do
        sim.Poke("in_left", l)
        sim.Poke("in_right", r)
        sim.Tick()
        seenLeft.Add(sim.Peek "out_left")
        seenRight.Add(sim.Peek "out_right")

    // Peek-after-Tick reads the state the edge just produced, so one of the
    // apply pipeline's two register stages is already reflected in the first
    // reading: sample k appears at observation k + 1.
    let matches (seen: ResizeArray<uint64>) (fed: uint64 list) =
        Seq.forall2 (=) seen (Seq.truncate seen.Count (0UL :: fed))

    // Muting must silence the chain — and this doubles as the negative control
    // that proves the comparison above can fail at all. Without it, a harness
    // that read the wrong port or compared empty sequences would report a
    // passthrough that never happened.
    sim.Poke("mute", 1UL)

    let muted =
        [ for l, r in List.zip left right do
              sim.Poke("in_left", l)
              sim.Poke("in_right", r)
              sim.Tick()
              yield sim.Peek "out_left"
              yield sim.Peek "out_right" ]

    matches seenLeft left
    && matches seenRight right
    && List.forall ((=) 0UL) (List.skip 4 muted)

/// The cookbook designs, checked by what the filters must *do* rather than by
/// restating their formulae — a transcription error reproduced in the check
/// would prove nothing.
///
/// Each shape is evaluated as its own transfer function at DC and at Nyquist,
/// where the answer is known without a spectrum analyser: z = 1 and z = -1 give
/// H = (b0 + b1 + b2) / (1 + a1 + a2) and (b0 - b1 + b2) / (1 - a1 + a2).
let private rbjCookbookDesigns () : bool =
    let fs = 48_000.0
    let near (tolerance: float) (a: float) (b: float) = abs (a - b) < tolerance

    let response (d: BiquadDesign) atDc =
        let sign = if atDc then 1.0 else -1.0
        (d.b0 + sign * d.b1 + d.b2) / (1.0 + sign * d.a1 + d.a2)

    let lowPass = rbjDesign LowPass 1000.0 0.707 0.0 fs
    let highPass = rbjDesign HighPass 1000.0 0.707 0.0 fs
    let lowShelf = rbjDesign LowShelf 1000.0 0.707 12.0 fs
    let highShelf = rbjDesign HighShelf 1000.0 0.707 12.0 fs
    let peakingFlat = rbjDesign Peaking 1000.0 0.707 0.0 fs

    // A low-pass passes DC and stops Nyquist; a high-pass does the reverse.
    // Get a sign wrong anywhere in either and one of these four flips.
    let gainOf = 10.0 ** (12.0 / 20.0) // +12 dB

    near 1e-9 (response lowPass true) 1.0
    && near 1e-9 (response lowPass false) 0.0
    && near 1e-9 (response highPass true) 0.0
    && near 1e-9 (response highPass false) 1.0
    // A shelf reaches its gain on its own side and unity on the other.
    && near 1e-6 (response lowShelf true) gainOf
    && near 1e-6 (response lowShelf false) 1.0
    && near 1e-6 (response highShelf true) 1.0
    && near 1e-6 (response highShelf false) gainOf
    // A peaking filter at 0 dB is the identity, exactly.
    && near 1e-12 peakingFlat.b0 1.0
    && near 1e-12 (peakingFlat.b1 - peakingFlat.a1) 0.0
    && near 1e-12 (peakingFlat.b2 - peakingFlat.a2) 0.0
    // The envelope time constant round-trips through its Q1.15 encoding.
    && near 2e-4 (envelopeAlphaSeconds (envelopeAlphaQ15 0.010 fs) fs) 0.010
    && envelopeAlphaQ15 0.0 fs = 0x7FFFUL

/// Real audio through the real design, judged the way a compressor is judged:
/// by what it does to peaks.
///
/// A WAV round trip is the oracle the other checks cannot be. The differential
/// proves Sim and Verilog agree; the property checks pin one arithmetic
/// relationship each. Neither notices that a filter rings, or that an envelope
/// never settles over a real signal — those need a signal, and a signal needs
/// somewhere to come from and go.
///
/// Two runs over the same tone pair, differing only in threshold:
///   * wide open (threshold at full scale) must pass the peaks through, which
///     says the filterbank reconstructs a *moving* signal and not just DC;
///   * clamped down must visibly reduce them, on both channels.
let private wavThroughMultiband () : bool =
    let input = toneWav 48_000 2_000 440.0 0.8

    let run threshold =
        let sim = Sim(multibandStage)
        sim.Poke("threshold", threshold)
        sim.Poke("ratio", 4UL)
        sim.Poke("attack", 1UL <<< 14)
        sim.Poke("releaseRate", 1UL <<< 12)

        for i in 0 .. multibandBands - 1 do
            sim.Poke($"lg{i}", gainUnity)
            sim.Poke($"rg{i}", gainUnity)

        runWavThroughSim sim defaultWavPorts 64 input

    let inLeft, inRight = peaks input
    let openLeft, openRight = peaks (run ((1UL <<< (sampleWidth - 1)) - 1UL))
    let clampedLeft, clampedRight = peaks (run 200_000UL)

    // Wide open the peaks come back EXACTLY — 26213 in, 26213 out on both
    // channels — so the eight-way split and reassembly are lossless on a
    // moving signal, not merely on DC. The tolerance is here for the Q2.30
    // coefficients to spend; measured, they spend none.
    let survives got want = abs (got - want) * 100 < want * 5

    // Clamped, the measured reduction is ~23%: a multiband compressor only
    // pulls down the bands the signal is actually in, so a two-tone input can
    // never lose half its peak the way a broadband compressor would. Demanding
    // more than the topology can deliver is how a correct design gets called
    // broken.
    let reduced clamped opened = clamped * 10 < opened * 9 && clamped > 0

    survives openLeft inLeft
    && survives openRight inRight
    && reduced clampedLeft openLeft
    && reduced clampedRight openRight

/// The eight bands must sum back to the signal they were split from.
///
/// This is the property the whole filterbank rests on, and it is a real claim
/// rather than a tautology: the split is *subtractive* — band 0 is the lowest
/// low-pass, band k the difference of successive low-passes, band 7 the
/// residue — so the bands telescope back to the input exactly, where a bank of
/// independent band-pass filters would only approximate it and leave audible
/// crossover ripple. At unity makeup and zero ratio every compressor is a
/// pass-through, so what comes out is the reconstruction and nothing else.
///
/// Only two counts are tolerated away from exact: the biquads are Q2.30 and
/// the band sum saturates once, so a least-significant bit or two is expected.
/// Anything more means the split is not telescoping.
let private multibandReconstructs () : bool =
    let sim = Sim(multibandStage)
    sim.Poke("threshold", 0UL)
    sim.Poke("ratio", 0UL) // zero slope — every band passes through
    sim.Poke("attack", 1UL <<< 15)
    sim.Poke("releaseRate", 1UL <<< 15)

    for i in 0 .. multibandBands - 1 do
        sim.Poke($"lg{i}", gainUnity)
        sim.Poke($"rg{i}", gainUnity)

    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 1UL)

    // DC: every band's filter settles, and the reconstruction is the input.
    let level = 200_000UL
    sim.Poke("in_left", level)
    sim.Poke("in_right", level / 2UL)

    for _ in 1..400 do
        sim.Tick()

    let asSample (v: uint64) =
        if v >= (1UL <<< (sampleWidth - 1)) then int64 v - (1L <<< sampleWidth) else int64 v

    let near target value = abs (asSample value - int64 target) <= 2L

    near level (sim.Peek "out_left")
    && near (level / 2UL) (sim.Peek "out_right")

/// The compressor must regulate the level it actually *emits*, not the one it
/// was handed. With a large makeup gain and an input already loud enough to
/// exceed the threshold once boosted, a detect-then-boost topology sees a
/// quiet-looking envelope, applies no reduction, and lets the makeup multiply
/// slam the output into the saturation rail — audible as buzz, and the reason
/// this design boosts first.
///
/// So: feed a signal that is modest on its own but hot after 8x makeup, with a
/// threshold below the boosted level and a real ratio. A correct compressor
/// pulls the output back below full scale. A detect-then-boost one pins it at
/// the rail.
let private compressorRegulatesOutput () : bool =
    let sim = Sim(audioChain)
    let fullScale = (1UL <<< (sampleWidth - 1)) - 1UL
    sim.Poke("volume", gainUnity)
    sim.Poke("mute", 0UL)
    let level = 500_000UL
    let makeup = 8UL

    // The threshold sits ABOVE the raw level and BELOW the boosted one. That
    // gap is exactly what separates the topologies: detecting on the raw
    // signal finds nothing over threshold and applies no reduction at all.
    sim.Poke("threshold", 1_000_000UL)
    sim.Poke("ratio", 1UL) // a slope, not an N:1 knob — 1 already reduces hard
    sim.Poke("attack", 1UL <<< 15)
    sim.Poke("releaseRate", 1UL <<< 15)
    sim.Poke("makeup", makeup * gainUnity)
    sim.Poke("limit", fullScale)
    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 1UL)
    sim.Poke("in_left", level)
    sim.Poke("in_right", level)

    for _ in 1..200 do
        sim.Tick()

    let settled = sim.Peek "out_left"

    // And the other side of it: at unity makeup the same input sits below the
    // threshold for real, so the compressor must leave it completely alone.
    // Without this the check would also pass for a compressor that always
    // reduces, which is not the property being claimed.
    sim.Poke("makeup", gainUnity)

    for _ in 1..200 do
        sim.Tick()

    let untouched = sim.Peek "out_left"

    // Measured: 3,284,744 against 4,000,000 unregulated — the 0.821 gain the
    // Q0.24 computer owes for this excess, not a rounding artefact.
    settled < level * makeup && settled > 0UL && untouched = level

/// The receiver against a hand-built ideal I2S frame — one transition tick
/// carrying no data, then 24 bits MSB-first, then padding — which is how the
/// Kotlin side verifies it, and the only way to judge rx without also judging
/// tx. A real codec supplies exactly this frame, so it is the spec, not a
/// convenience.
let private i2sRxDecodes () : bool =
    let sim = Sim(i2sRxStage)
    sim.Poke("out_ready", 1UL)

    let step lrclk sdout tick =
        sim.Poke("lrclk", lrclk)
        sim.Poke("sdout", sdout)
        sim.Poke("sclkTick", tick)
        sim.Tick()

    let slot lrclk (value: uint64) pad =
        step lrclk 0UL 1UL

        for i in 0 .. sampleWidth - 1 do
            step lrclk ((value >>> (sampleWidth - 1 - i)) &&& 1UL) 1UL

        for _ in 1..pad do
            step lrclk 0UL 1UL

    let left = 0x123456UL
    let right = 0xABCDEFUL

    // Warm-up so the previous-LRCLK register is 1 and the left slot opens on a
    // real edge.
    step 1UL 0UL 1UL
    slot 0UL left 2
    slot 1UL right 0

    sim.Peek "out_left" = left && sim.Peek "out_right" = right

/// The transmitter against the same frame convention, read back off the serial
/// line. Pairing this with the receiver check is what makes the two
/// independent: each is judged against the ideal frame rather than against the
/// other, so a shared misreading of the convention cannot cancel out.
let private i2sTxEmits () : bool =
    let sim = Sim(i2sTxStage)
    let left = 0x123456UL
    let right = 0xABCDEFUL

    sim.Poke("in_left", left)
    sim.Poke("in_right", right)
    sim.Poke("in_valid", 1UL)
    sim.Poke("sclkTick", 0UL)
    sim.Poke("lrclk", 0UL)
    sim.Tick()
    sim.Poke("in_valid", 0UL)

    // Warm-up at lrclk = 1 so entry to the left slot is an edge.
    sim.Poke("sclkTick", 1UL)
    sim.Poke("lrclk", 1UL)
    sim.Tick()

    // The edge tick commits the pending sample; `sdin` is combinational from
    // the shift register, so the MSB is readable before the first data tick.
    let slot lrclk =
        sim.Poke("lrclk", lrclk)
        sim.Tick()

        [ for _ in 1..sampleWidth ->
              let bit = sim.Peek "sdin"
              sim.Tick()
              bit ]
        |> List.fold (fun acc bit -> (acc <<< 1) ||| bit) 0UL

    slot 0UL = left && slot 1UL = right

/// Looping the transmitter's line back into the receiver over the real clock
/// generator: the link is stable, and it lands **one bit position late**.
///
/// That is not a bug in either framer — both pass the ideal-frame checks above,
/// which is the convention a real codec supplies. It is a property of looping
/// them back through `i2sMaster`'s two ticks. LRCLK turns on the *falling* edge,
/// which is tx's tick: tx commits and presents its MSB there. Rx runs on the
/// *rising* edge, so the first rx tick of the new slot is the one where it sees
/// LRCLK changed — and by its own (correct) rule it treats that tick as the
/// no-data transition and discards what is on the line. Which is the MSB. The
/// receiver then takes bits 22..0 plus a trailing pad zero, giving exactly
/// `input << 1`.
///
/// So the two framers are each right against the codec and off by one against
/// each other. Nothing in the Kotlin suite could have found this: it drives rx
/// and tx separately against hand-built frames and never closes the loop, and
/// on real hardware the codec — not the other framer — defines the timing,
/// which is why the MEMS front end worked. A fabric loopback would need the
/// line delayed by one bit time; no shipping design needs one, so this is
/// recorded rather than fixed.
///
/// Asserted as the measured relationship, so the check still fails if the link
/// breaks in some *other* way.
let private i2sLoopbackRoundTrip () : bool =
    let sim = Sim(i2sLoopback)
    let left = 0xA5A5A0UL
    let right = 0x5A5A50UL
    sim.Poke("in_left", left)
    sim.Poke("in_right", right)
    sim.Poke("in_valid", 1UL)
    sim.Poke("out_ready", 1UL)

    // One LRCLK period is 2 * bitsPerSlot * 2 * sclkHalfDiv = 2048 fabric
    // cycles at the stock divisors, so a full stereo frame needs a few
    // thousand. Sample the output whenever the receiver flags one complete.
    let received = ResizeArray<uint64 * uint64>()

    for _ in 1..12000 do
        sim.Tick()

        if sim.Peek "out_valid" = 1UL then
            received.Add(sim.Peek "out_left", sim.Peek "out_right")

    // The first frame catches the transmitter mid-slot, so judge the settled
    // link rather than the first thing out of it.
    let mask = (1UL <<< sampleWidth) - 1UL
    let oneBitLate v = (v <<< 1) &&& mask

    received.Count > 1
    && List.ofSeq received |> List.last = (oneBitLate left, oneBitLate right)

/// The tone-control presets have to actually shape tone. A constant input is
/// pure DC, so the low-pass — normalised to unity gain at DC — must pass it
/// through, and the high-pass — a spectral inversion whose coefficients sum to
/// zero — must annihilate it.
///
/// This is the check the differential cannot make: agreeing Sim and Verilog
/// would happily both implement a filter designed from wrong coefficients. It
/// tests the windowed-sinc design, the Q1.15 scaling and the pipelined MAC
/// together, through the one input whose correct output is known without
/// reimplementing the filter.
let private audioFirDcResponse () : bool =
    let settle (preset: uint64) (level: uint64) =
        let sim = Sim(audioFirStage)
        sim.Poke("preset", preset)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 1UL)
        sim.Poke("in_left", level)
        sim.Poke("in_right", level)
        // Long enough to fill the 16-tap delay line and flush the MAC pipeline.
        for _ in 1..40 do
            sim.Tick()

        sim.Peek "out_left"

    let level = 100_000L

    // The port is a 24-bit two's-complement sample, so the high-pass residue
    // reads as 0xFFFFFC rather than a small number. Comparing the raw bits
    // would call a correctly annihilated DC a failure.
    let asSample (v: uint64) =
        if v >= (1UL <<< (sampleWidth - 1)) then int64 v - (1L <<< sampleWidth) else int64 v

    let near target preset =
        abs (asSample (settle (uint64 preset) (uint64 level)) - target) <= 4L

    // Bypass is a single centre tap of 32767, one LSB short of unity by
    // construction, so it reproduces the input only to within Q1.15 rounding.
    near level presetBypass
    && near level presetLowPass
    && near 0L presetHighPass

/// A module that instantiates a module: the case a shorter grouping rule would
/// get wrong. Two `Delay8`s inside one `DoubleDelay8`, so the flattened names
/// carry two instance prefixes.
let private nestedDelayOf w =
    liftUnary (
        stateModule1 $"DoubleDelay%d{w}" ("d", w) ("q", w) (fun d ->
            let stage = delayOf w
            stage (stage d))
    )

let private nestedGroups =
    design "NestedGroups" (fun () ->
        let pair = nestedDelayOf 8
        let x = input "x" 8
        let out = output "out" 8
        pair x ==> out)

/// The inventory's contract, and what the debugger's watch list rests on: every
/// name it lists is a name the Sim answers to. A name in the table the Sim
/// rejects is a crash at the click.
let private inventoryNamesPeek () =
    let peekable (d: ModuleDef) =
        let sim = Sim(d)
        let inv = Inventory.ofDesign d

        let signalsOk =
            inv.signals
            |> List.forall (fun s ->
                try
                    if s.width > 64 then
                        sim.PeekWide s.name |> ignore
                    else
                        sim.Peek s.name |> ignore

                    true
                with _ ->
                    false)

        let memsOk =
            inv.mems
            |> List.forall (fun m ->
                try
                    sim.PeekMem(m.name, (1 <<< m.addrWidth) - 1) |> ignore
                    true
                with _ ->
                    false)

        signalsOk && memsOk

    [ add3
      dot2Ambient
      pipelinedDot
      loopPipeline
      nestedGroups
      ramTest
      fillingMemory
      assertedSaturate
      cmdProcessor
      wideBeat
      framePipeline
      audioChain
      snapshotConflate ]
    |> List.forall peekable

/// A signal's group is the instance it came from, by longest match. The rule
/// earns its keep twice over: auto-generated instance names contain underscores
/// (`mul8_1`), and an instance inside an instance carries both prefixes.
let private inventoryGroups () =
    let flat = Inventory.ofDesign add3
    let nested = Inventory.ofDesign nestedGroups
    let ram = Inventory.ofDesign ramTest

    let nestedPair =
        nested.groups
        |> List.exists (fun outer ->
            outer <> ""
            && nested.groups
               |> List.exists (fun inner -> inner.Length > outer.Length && inner.StartsWith outer))

    let everySignalGrouped =
        nested.signals |> List.forall (fun s -> List.contains s.group nested.groups)

    flat.groups = [ ""; "a1_"; "a2_" ]
    && nestedPair
    && everySignalGrouped
    && (ram.mems |> List.map (fun m -> m.name, m.addrWidth, m.wordWidth)) = [ "store", 3, 8 ]

/// Breakpoint expressions against three designs, checked by what they must
/// *do*: fire on the cycle the state first satisfies them, read memories and
/// bit slices, and tell a signed compare from an unsigned one. The error cases
/// matter as much — a debugger that accepts a typo silently never fires.
let private breakpointExpressions () =
    let counterHits text expected =
        let sim = Sim(counterMutable)
        sim.Poke("enable", 1UL)

        match Breakpoint.compile sim text with
        | Error _ -> false
        | Ok bp -> Breakpoint.runUntil sim bp.isHit 40 = expected

    let comparing a b text =
        let sim = Sim(comparator8)
        sim.Poke("a", a)
        sim.Poke("b", b)

        match Breakpoint.compile sim text with
        | Error _ -> false
        | Ok bp -> bp.isHit ()

    let afterWrite text =
        let sim = Sim(ramTest)
        sim.Poke("waddr", 3UL)
        sim.Poke("wdata", 0x55UL)
        sim.Poke("wen", 1UL)
        sim.Tick()
        sim.Poke("wen", 0UL)

        match Breakpoint.compile sim text with
        | Error _ -> false
        | Ok bp -> bp.isHit ()

    let rejects text =
        match Breakpoint.compile (Sim(counterMutable)) text with
        | Error _ -> true
        | Ok _ -> false

    // A 128-bit register: the predicate routes onto the BigInteger path by the
    // same rule an assignment does, and a slice of it comes back narrow.
    let wideAfterBytes text =
        let sim = Sim(wideBeat)
        sim.Poke("shift_en", 1UL)

        for b in 1UL..3UL do
            sim.Poke("byte_in", b)
            sim.Tick()

        match Breakpoint.compile sim text with
        | Error _ -> false
        | Ok bp -> bp.isHit ()

    // Fires on the exact cycle, in every radix, and not at all when it cannot.
    counterHits "count == 10" (10, true)
    && counterHits "count == 0xa" (10, true)
    && counterHits "count == 0b1010" (10, true)
    && counterHits "count == 3 && enable == 1" (3, true)
    && counterHits "count == 3 && enable == 0" (40, false)
    && counterHits "count > 200" (40, false)
    && counterHits "count != 0 && !(count < 7)" (7, true)
    // Bit slices and shifts.
    && counterHits "count[0] == 1 && count > 4" (5, true)
    && counterHits "count[3:2] == 0b11" (12, true)
    && counterHits "count >> 1 == 5" (10, true)
    // 200 is -56 read as signed, which only `signed` can see.
    && comparing 200UL 10UL "signed(a) < 0"
    && comparing 200UL 10UL "signed(a) < signed(b)"
    && not (comparing 200UL 10UL "a < b")
    // A signed shift keeps the sign: -56 >> 2 is -14 (0xF2), where the same
    // bits shifted logically give 50. The only consumer of the arithmetic
    // shift that neither the golden diff nor the differential reaches.
    && comparing 200UL 10UL "signed(a) >> 2 == 0xf2"
    && comparing 200UL 10UL "a >> 2 == 50"
    && comparing 200UL 10UL "larger == 200 && greater == 1"
    && comparing 7UL 7UL "equal == 1 && !less && !greater"
    // A memory, by word.
    && afterWrite "store[3] == 0x55"
    && not (afterWrite "store[2] == 0x55")
    && afterWrite "store[3] + 1 == 0x56"
    // Wide, and sliced back down.
    && wideAfterBytes "beat[23:0] == 0x010203"
    && wideAfterBytes "beat == 0x010203"
    && not (wideAfterBytes "beat == 0x010204")
    && wideAfterBytes "beat[7:0] == 3 && beat[15:8] == 2"
    // And the typos.
    && rejects "count == "
    && rejects "nosuchsignal == 1"
    && rejects "count 5"
    && rejects "count[99] == 1"
    && rejects "count << r"
    && rejects ""

/// The debugger's run loop, driven the way a window drives it: post a command,
/// wait for a snapshot. Checked headless because the loop is the part that has
/// to be right — a UI on top of a broken one just makes it harder to see.
let private waitUntil (predicate: unit -> bool) =
    let deadline = System.DateTime.UtcNow.AddSeconds 5.0

    let rec go () =
        if predicate () then true
        elif System.DateTime.UtcNow > deadline then false
        else
            System.Threading.Thread.Sleep 5
            go ()

    go ()

let private debugSessionDrives () =
    use raw = new Debug.DebugSession(counterMutable)
    let session = raw :> Debug.IDebugSession

    session.Poke("enable", System.Numerics.BigInteger.One)
    session.Watch "count"
    session.Step 10

    let stepped = waitUntil (fun () -> session.Latest.cycle = 10 && not session.Latest.running)

    let watchFollows =
        session.Latest.values
        |> List.exists (fun v -> v.name = "count" && v.value = System.Numerics.BigInteger 10)

    let breaksWhereItShould =
        match session.AddBreakpoint "count == 20" with
        | Error _ -> false
        | Ok () ->
            session.Run()

            waitUntil (fun () -> session.Latest.hit = Some "count == 20")
            && session.Latest.cycle = 20
            && not session.Latest.running
            && session.Latest.breakpoints = [ { text = "count == 20"; enabled = true; hits = 1 } ]

    // A disabled breakpoint is not a breakpoint: the run must sail past 30.
    session.EnableBreakpoint("count == 20", false)
    session.Step 15
    let ranPast = waitUntil (fun () -> session.Latest.cycle = 35)

    session.Reset()
    let resets = waitUntil (fun () -> session.Latest.cycle = 0 && session.Latest.hit = None)

    stepped
    && watchFollows
    && breaksWhereItShould
    && ranPast
    && resets
    && Result.isError (session.AddBreakpoint "nosuchsignal == 1")

/// A memory window follows the design rather than being read once: the words
/// come back in every snapshot, and asking past the end pages to the tail
/// instead of failing.
let private debugSessionShowsMemory () =
    use raw = new Debug.DebugSession(ramTest)
    let session = raw :> Debug.IDebugSession

    session.ViewMemory("store", 0, 8)
    let showing = waitUntil (fun () -> session.Latest.memory.IsSome)

    let wordsAre expected =
        match session.Latest.memory with
        | Some view -> view.start = 0 && List.ofArray view.words = expected
        | None -> false

    let startsEmpty = wordsAre [ 0UL; 0UL; 0UL; 0UL; 0UL; 0UL; 0UL; 0UL ]

    // Write 0x55 to address 3 the way the design does, and watch the window
    // pick it up without being asked again.
    session.Poke("waddr", System.Numerics.BigInteger 3)
    session.Poke("wdata", System.Numerics.BigInteger 0x55)
    session.Poke("wen", System.Numerics.BigInteger.One)
    session.Step 1
    // Posted *after* the step, and it must stay after it: a step holds the
    // command queue until it completes, so this cannot reach back and disarm
    // the write it was supposed to follow. Draining the queue before stepping
    // is what silently stopped a one-edge load from ever landing.
    session.Poke("wen", System.Numerics.BigInteger.Zero)
    let landed = waitUntil (fun () -> wordsAre [ 0UL; 0UL; 0UL; 0x55UL; 0UL; 0UL; 0UL; 0UL ])

    // And exactly one write happened, not one per cycle.
    session.Step 3

    let onlyOnce =
        waitUntil (fun () -> session.Latest.cycle = 4)
        && wordsAre [ 0UL; 0UL; 0UL; 0x55UL; 0UL; 0UL; 0UL; 0UL ]

    // The mem is 8 words deep; asking for 8 from address 6 gives the last two.
    session.ViewMemory("store", 6, 8)

    let clamped =
        waitUntil (fun () ->
            match session.Latest.memory with
            | Some view -> view.start = 6 && view.words.Length = 2
            | None -> false)

    session.ClearMemoryView()
    let cleared = waitUntil (fun () -> session.Latest.memory.IsNone)

    showing && startsEmpty && landed && onlyOnce && clamped && cleared

/// The trace records per *cycle*, not per snapshot — the distinction the whole
/// feature rests on, since a run advances thousands of cycles between two
/// published snapshots. Checked by recording a counter and demanding the trace
/// be 0,1,2,3… with no gaps, and by reading the VCD back.
let private tracesEveryCycle () =
    use raw = new Debug.DebugSession(counterMutable)
    let session = raw :> Debug.IDebugSession

    session.Poke("enable", System.Numerics.BigInteger.One)
    session.Watch "count"
    let watching = waitUntil (fun () -> session.Latest.values |> List.exists (fun v -> v.name = "count"))

    let started = Result.isOk (session.StartRecording false)
    session.Step 40
    let ran = waitUntil (fun () -> session.Latest.cycle = 40 && session.Latest.recorded >= 41)

    let trace = session.Trace()

    // Sample 0 is the state before the first tick, so the counter reads
    // 0,1,2,…,40 across 41 samples with nothing skipped.
    let contiguous =
        match trace.signals with
        | [ counted ] ->
            counted.name = "count"
            && counted.values.Length >= 41
            && Seq.forall2 (=) (Seq.truncate 41 counted.values) [ for i in 0UL..40UL -> i ]
        | _ -> false

    let vcd = Vcd.render "counter" trace

    // Every cycle changes the counter, so every cycle gets a timestamp, and
    // the header has to name the one signal at its real width.
    let vcdWellFormed =
        vcd.Contains "$enddefinitions $end"
        && vcd.Contains "$var wire 8 "
        && vcd.Contains "count $end"
        && vcd.Split('\n') |> Array.filter (fun l -> l.StartsWith "#") |> Array.length >= 41

    // A ring shorter than the run keeps the *end*, which is what makes a
    // breakpoint's approach readable: record 8, run past it, keep the last 8.
    let keepsTheEnd =
        session.StopRecording()
        waitUntil (fun () -> not session.Latest.recording) |> ignore
        let trace = session.Trace()

        match trace.signals with
        | [ counted ] ->
            let held = counted.values.Length
            let last = counted.values[held - 1]
            // The final sample is the current cycle's value, and the trace's
            // own first-cycle number agrees with where it starts.
            last = uint64 session.Latest.cycle
            && trace.firstCycle + held - 1 = session.Latest.cycle
        | _ -> false

    watching && started && ran && contiguous && vcdWellFormed && keepsTheEnd

/// Assertions, checked by what they must *do*: stay quiet while the design
/// keeps its promise, fire on the cycle it stops, cost nothing when the Sim was
/// not asked to check them, and mean nothing about a branch that was not taken.
let private assertionsHold () =
    let run checking pokes cycles =
        let sim = Sim(assertedCounter, checkAsserts = checking)

        for name, value in pokes do
            sim.Poke(name, value)

        for _ in 1..cycles do
            sim.Tick()

        sim.Violations

    // 400 enabled cycles saturate at the ceiling and stay there: neither claim
    // is broken, so a correct design is silent.
    let quiet = run true [ "enable", 1UL ] 400 = []

    // Both controls at once breaks the conditional claim, on every cycle it
    // holds, and breaks nothing else.
    let conditionalFires =
        match run true [ "enable", 1UL; "hold", 1UL ] 3 with
        | [ (a, 1); (b, 2); (c, 3) ] -> [ a; b; c ] |> List.forall (fun m -> m.Contains "asserted together")
        | _ -> false

    // Driven to the ceiling and then wrapped: the saturation claim fires on the
    // cycle the counter comes back as zero, and only then.
    let saturationFires =
        let sim = Sim(assertedCounter, checkAsserts = true)
        sim.Poke("hold", 1UL)
        sim.Tick()
        sim.Poke("hold", 0UL)
        sim.Poke("wrap", 1UL)
        sim.Tick()

        match sim.Violations with
        | [ (message, cycle) ] -> message.Contains "wrapped past" && cycle = 2
        | _ -> false

    // The same run with checking off records nothing — the claims are not in
    // the program at all rather than being evaluated and ignored.
    let silentWhenOff =
        let sim = Sim assertedCounter
        sim.Poke("hold", 1UL)
        sim.Tick()
        sim.Poke("hold", 0UL)
        sim.Poke("wrap", 1UL)
        sim.Tick()
        sim.Violations = []

    // The implication: with `enable` low, the conditional claim says nothing
    // even though `hold` is high, which would break it inside the branch.
    let quietWhenNotTaken = run true [ "hold", 1UL ] 20 = []

    quiet
    && conditionalFires
    && saturationFires
    && silentWhenOff
    && quietWhenNotTaken

/// The four utility entries, each against the property that defines it rather
/// than against a golden vector — a wrong LFSR tap mask still produces a
/// plausible-looking stream, and only the period says otherwise.
let private utilityPrimitives () =
    // Every polynomial offered, walked for its whole period. A maximal-length
    // LFSR visits all 2^w − 1 non-zero states before repeating, so returning to
    // the seed EARLY is the failure, and returning to it exactly on time with
    // every state seen once is the proof. The largest offered (24 bits, 16.7M
    // steps) runs in well under a second in software.
    let periodOk width =
        let states = 1UL <<< width
        let seed = 1UL
        let mutable state = seed
        let mutable steps = 0UL
        let mutable repeated = false

        while not repeated && steps < states do
            state <- lfsrNext width state
            steps <- steps + 1UL
            repeated <- state = seed

        // Zero would be a trap state, and a short cycle means wrong taps.
        state <> 0UL && steps = states - 1UL

    let everyPolynomialMaximal =
        lfsrTaps.Keys |> Seq.toList |> List.forall periodOk

    // And the hardware is that function: same seed, same stream, and `step` low
    // holds the state rather than advancing it.
    let hardwareMatchesReference =
        let sim = Sim lfsrSource
        let mutable expected = 1UL
        sim.Poke("step", 0UL)
        sim.Tick()
        let held = sim.Peek "state" = 1UL
        sim.Poke("step", 1UL)

        let walked =
            [ for _ in 1..600 do
                  sim.Tick()
                  expected <- lfsrNext 9 expected
                  yield sim.Peek "state" = expected ]
            |> List.forall id

        held && walked

    // A one-hot scan is defined by what it does to every input pattern, so
    // check all of them rather than a few: exactly the lowest set bit survives,
    // and all-zero stays all-zero.
    let oneHotLowestPicks =
        let n = 4
        let sim = Sim oneHotScan

        [ for pattern in 0 .. (1 <<< n) - 1 ->
              for i in 0 .. n - 1 do
                  sim.Poke($"request{i}", uint64 ((pattern >>> i) &&& 1))

              sim.Tick()

              let out = [ for i in 0 .. n - 1 -> sim.Peek $"grant{i}" ]
              let lowest = [ 0 .. n - 1 ] |> List.tryFind (fun i -> (pattern >>> i) &&& 1 = 1)

              out = [ for i in 0 .. n - 1 -> if Some i = lowest then 1UL else 0UL ] ]
        |> List.forall id

    // One-hot select, including the no-select case the callers rely on.
    let mux1HSelects =
        let sim = Sim mux1HSelect

        for i in 0..3 do
            sim.Poke($"value{i}", uint64 (0x10 * (i + 1)))

        // The arbiter feeds the select, so asking from `i` upward makes `i` the
        // winner — the pairing the toy design exists to show.
        let pick i =
            for k in 0..3 do
                sim.Poke($"request{k}", if k >= i then 1UL else 0UL)

            sim.Tick()
            sim.Peek "winner"

        let selected = [ 0..3 ] |> List.forall (fun i -> pick i = uint64 (0x10 * (i + 1)))

        for k in 0..3 do
            sim.Poke($"request{k}", 0UL)

        sim.Tick()
        selected && sim.Peek "winner" = 0UL

    // Edge detection is about the enable as much as the signal: a change that
    // happens while `enable` is low is seen when it next goes high, not missed.
    let edgesDetected =
        let sim = Sim edgeDetector

        // Read the outputs BEFORE the clock edge that samples: an edge is
        // asserted during the cycle it happens, and `previous` catches up at
        // that cycle's edge. Peeking after the tick reads the cycle after,
        // which is precisely when the edge is over.
        let cycle (enable: uint64) (signal: uint64) =
            sim.Poke("enable", enable)
            sim.Poke("signal", signal)
            let seen = sim.Peek "rising", sim.Peek "falling", sim.Peek "changed"
            sim.Tick()
            seen

        // Enabled: a 0→1 turn is a rising edge for exactly one cycle.
        let quiet = cycle 1UL 0UL = (0UL, 0UL, 0UL)
        let rose = cycle 1UL 1UL = (1UL, 0UL, 1UL)
        let steady = cycle 1UL 1UL = (0UL, 0UL, 0UL)
        let fell = cycle 1UL 0UL = (0UL, 1UL, 1UL)

        // Disabled: `enable` gates the *sample*, so an edge that happens while
        // it is low stays pending instead of being missed — the property an
        // I2S framer depends on, seeing LRCLK turn on its own tick rather than
        // on whatever the fast clock was doing.
        let pending = cycle 0UL 1UL = (1UL, 0UL, 1UL)
        let stillPending = cycle 0UL 1UL = (1UL, 0UL, 1UL)
        let taken = cycle 1UL 1UL = (1UL, 0UL, 1UL)
        let consumed = cycle 1UL 1UL = (0UL, 0UL, 0UL)

        quiet
        && rose
        && steady
        && fell
        && pending
        && stillPending
        && taken
        && consumed

    // A flow's defining property is the one a stream does not have: a beat is
    // lost when the consumer is not there, and the count of lost beats is
    // exactly the cycles `valid && !ready`.
    let flowLosesWhatItMust =
        let sim = Sim flowSampler

        let cycle (sample: uint64) (ready: uint64) =
            sim.Poke("sample", sample)
            sim.Poke("out_ready", ready)
            sim.Tick()

        // Stated as relationships rather than totals: a count read through a
        // register is a cycle behind the overflow that fed it, and encoding
        // that skew in the expected numbers tests the arithmetic rather than
        // the property.

        // Producing into a consumer that refuses: the count climbs, one per
        // beat, and it is the only thing that does.
        cycle 1UL 0UL
        let idle = sim.Peek "dropped"

        for _ in 1..10 do
            cycle 1UL 0UL

        let refused = sim.Peek "dropped"
        let climbs = refused = idle + 10UL

        // Consumer ready: the loss stops dead and beats come out instead.
        for _ in 1..10 do
            cycle 1UL 1UL

        let taken = sim.Peek "dropped"
        let stopsWhenTaken = taken = refused
        let flowing = sim.Peek "out_valid" = 1UL

        // Producer idle: the beat already in `flowStage` drains — dropped once
        // more, since the consumer is refusing again — and then there is
        // nothing to lose. That the drain is exactly one beat is also what
        // pins the stage's one cycle of latency.
        cycle 0UL 0UL
        cycle 0UL 0UL
        let drainedOne = sim.Peek "dropped" = taken + 1UL

        cycle 0UL 0UL
        let quietWhenIdle = sim.Peek "dropped" = taken + 1UL && sim.Peek "out_valid" = 0UL

        climbs && stopsWhenTaken && flowing && drainedOne && quietWhenIdle

    // A counter's whole risk is an off-by-one, so this checks the sequence and
    // the wrap position rather than sampling a value: over three full periods,
    // the count must visit 0..n-1 in order and `wrap` must be high on exactly
    // the cycles where it reads n-1, three times and no more.
    let countersWrapWhereTheySay =
        let sim = Sim dividers
        let period = 6

        // Read before the tick: `wrap` is combinational from the count and the
        // enable, and asserted during the cycle the rollover happens.
        let cycle (enable: uint64) (last: uint64) =
            sim.Poke("enable", enable)
            sim.Poke("last", last)
            let seen = sim.Peek "count", sim.Peek "wrap", sim.Peek "window_count", sim.Peek "window_wrap"
            sim.Tick()
            seen

        let observed = [ for _ in 1 .. period * 3 -> cycle 1UL 3UL ]

        let counted =
            observed
            |> List.mapi (fun i (count, _, _, _) -> count = uint64 (i % period))
            |> List.forall id

        let wrapped =
            observed
            |> List.mapi (fun i (_, wrap, _, _) -> wrap = (if i % period = period - 1 then 1UL else 0UL))
            |> List.forall id

        let wrapCount = observed |> List.sumBy (fun (_, wrap, _, _) -> int wrap)

        // The runtime-bounded one wraps at `last` INCLUSIVE, so a bound of 3 is
        // a period of four.
        let windowed =
            observed
            |> List.mapi (fun i (_, _, wc, ww) -> wc = uint64 (i % 4) && ww = (if i % 4 = 3 then 1UL else 0UL))
            |> List.forall id

        // Enable low holds everything, wrap included, however long it sits.
        let held = cycle 0UL 3UL
        let stillHeld = cycle 0UL 3UL
        let holds = held = stillHeld && (let _, w, _, _ = held in w = 0UL)

        // The divider itself: one flip per period, so half the frequency.
        let flips =
            let flipping = Sim dividers
            flipping.Poke("enable", 1UL)
            flipping.Poke("last", 3UL)
            let before = flipping.Peek "divided"

            for _ in 1..period do
                flipping.Tick()

            let after = flipping.Peek "divided"

            for _ in 1..period do
                flipping.Tick()

            after <> before && flipping.Peek "divided" = before

        counted && wrapped && wrapCount = 3 && windowed && holds && flips

    // The bit shapes, against their definitions rather than against a vector —
    // exhaustively over 4-bit inputs, which is 256 cases and costs nothing.
    let bitShapesAreTheirDefinitions =
        let sim = Sim bitShapes

        // `&&&` and `|||` are the Expr operators here, so the host-side bit
        // arithmetic goes through division instead.
        let bitsOf (w: int) (v: uint64) = [ for i in 0 .. w - 1 -> (v >>> i) % 2UL ]

        [ for a in 0UL..15UL do
              for b in 0UL..15UL do
                  sim.Poke("a", a)
                  sim.Poke("b", b)
                  sim.Poke("flag", a % 2UL)
                  sim.Poke("index", a % 4UL)
                  sim.Tick()

                  // catAll puts the first element at the top.
                  let joined = sim.Peek "joined" = (a <<< 4) + b
                  let mask = sim.Peek "mask" = (if a % 2UL = 1UL then 15UL else 0UL)

                  let flipped =
                      sim.Peek "flipped" = (bitsOf 4 a |> List.mapi (fun i bit -> bit <<< (3 - i)) |> List.sum)

                  let ones = sim.Peek "ones" = (bitsOf 4 a |> List.sum)

                  // Exactly the indexed position is hot, and nothing else.
                  let hot = [ for i in 0..3 -> sim.Peek $"hot{i}" ]
                  let oneHot = hot = [ for i in 0..3 -> if uint64 i = a % 4UL then 1UL else 0UL ]

                  // And the round trip is the identity.
                  let recovered = sim.Peek "recovered" = (a % 4UL)

                  yield joined && mask && flipped && ones && oneHot && recovered ]
        |> List.forall id

    everyPolynomialMaximal
    && hardwareMatchesReference
    && oneHotLowestPicks
    && mux1HSelects
    && edgesDetected
    && flowLosesWhatItMust
    && countersWrapWhereTheySay
    && bitShapesAreTheirDefinitions

/// Flattening must not merge two signals into one name.
///
/// `flatten` prefixes a child's internals with the instance name, so a
/// grandchild's `sig` inside instance `gc` becomes `gc_sig`. If the parent
/// already declares `gc_sig`, two different signals land on one name and the
/// Sim answers for whichever it evaluated — silently, and with the Verilog
/// still correct, because the emitter preserves hierarchy and never flattens.
///
/// This is checked by building the collision rather than by asserting the
/// absence of one: a check that only ran clean designs would pass just as well
/// with the guard removed. Both halves are here — the collision is refused,
/// and the same shape with the instance renamed is accepted and computes the
/// right answer.
let private flattenRefusesNameCollisions () =
    let bare name =
        { name = name
          decls = []
          stmts = []
          instances = []
          clock = defaultClock
          streamReadies = []
          probes = []
          stateMachines = [] }

    // Grandchild: one internal wire, `sig`.
    let grandchild =
        { bare "CollideGrandChild" with
            decls = [ Input("i", UInt 8); Output("o", UInt 8); Wire("sig", UInt 8) ]
            stmts =
                [ Assign("sig", Add(Ref("i", UInt 8), Lit(1UL, UInt 8)))
                  Assign("o", Ref("sig", UInt 8)) ] }

    /// The parent, with its instance named by the caller. At "gc" its own
    /// `gc_sig` collides with the grandchild's flattened `sig`; at "child"
    /// nothing does, and the arithmetic is identical either way.
    let parent instanceName =
        { bare "CollideParent" with
            decls =
                [ Input("i", UInt 8)
                  Output("o", UInt 8)
                  Wire("gc_sig", UInt 8)
                  Wire($"{instanceName}_i", UInt 8)
                  Wire($"{instanceName}_o", UInt 8) ]
            stmts =
                [ Assign("gc_sig", Add(Ref("i", UInt 8), Lit(100UL, UInt 8)))
                  Assign($"{instanceName}_i", Ref("i", UInt 8))
                  Assign("o", Add(Ref($"{instanceName}_o", UInt 8), Ref("gc_sig", UInt 8))) ]
            instances = [ { instName = instanceName; child = grandchild } ] }

    let refused =
        try
            flatten (parent "gc") |> ignore
            false
        with e -> e.Message.Contains "gc_sig" && e.Message.Contains "collides"

    // Renamed, the same design is legal — and 5 must give (5+1) + (5+100).
    let accepted =
        let sim = Sim(parent "child")
        sim.Poke("i", 5UL)
        sim.Tick()
        sim.Peek "o" = 111UL

    refused && accepted

/// States for the two negative checks below. A machine's states are ordinary
/// values, which is what lets `Never` be refused twice over: once for having no
/// way in, once for not belonging to the machine at all.
type private Probe =
    | First
    | Second
    | Never

/// What `machine` is for, in four claims.
///
/// The first is the one that decides whether converting a design is safe at all:
/// the primitive emits what the hand-encoded form emits, character for
/// character. The rest are what the hand-encoded form cannot do — carry the
/// meaning of a code to the debugger, refuse a state nothing transitions to, and
/// refuse a state that is not one of the machine's.
let private stateMachines () =
    // The same six states, written the way every sequencer in this codebase
    // writes them today. Same module name, so a difference in the emitted text
    // is a difference in the logic.
    let handEncoded =
        design "Sequencer" (fun () ->
            let sIdle, sFetch, sDecode, sExecute, sWriteback, sDone = 0UL, 1UL, 2UL, 3UL, 4UL, 5UL
            let start = inputBit "start"
            let stall = inputBit "stall"
            let busy = outputBit "busy"
            let finished = outputBit "finished"
            let retired = output "retired" 8

            let stage = regInit "stage" 3 sIdle
            let inState s = eq stage (lit s 3)
            let count = reg "count" 8

            bnot (inState sIdle ||| inState sDone) ==> busy
            inState sDone ==> finished
            count ==> retired

            let begin' () =
                If start (fun () ->
                    lit 0UL 8 ==> count
                    lit sFetch 3 ==> stage)

            If (inState sIdle) begin'
            If (inState sDone) begin'
            If (inState sFetch) (fun () -> lit sDecode 3 ==> stage)
            If (inState sDecode) (fun () -> lit sExecute 3 ==> stage)
            If (inState sExecute) (fun () -> If (bnot stall) (fun () -> lit sWriteback 3 ==> stage))

            If (inState sWriteback) (fun () ->
                count + lit 1UL 8 ==> count

                If (eq count (lit 3UL 8)) (fun () -> lit sDone 3 ==> stage)
                Else (fun () -> lit sFetch 3 ==> stage)))

    let sameVerilog = emitDesign sequencer = emitDesign handEncoded

    // The decode reaches the debugger under the flattened name the Sim peeks by.
    let decoded =
        match (Inventory.ofDesign sequencer).stateMachines.TryFind "stage" with
        | Some states ->
            states
            |> Map.toList
            |> (=) [ 0UL, "Idle"; 1UL, "Fetch"; 2UL, "Decode"; 3UL, "Execute"; 4UL, "Writeback"; 5UL, "Done" ]
        | None -> false

    // And it means what it says: the state the design is in is the state the
    // decode names, held at EXECUTE for as long as the stall lasts.
    let walked =
        let sim = Sim sequencer
        let stateNow () = (Inventory.ofDesign sequencer).stateMachines["stage"] |> Map.find (sim.Peek "stage")
        sim.Poke("stall", 1UL)
        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)
        let after = [ for _ in 1..4 -> sim.Tick(); stateNow () ]
        sim.Poke("stall", 0UL)
        sim.Tick()

        after = [ "Decode"; "Execute"; "Execute"; "Execute" ]
        && stateNow () = "Writeback"

    let refuses build =
        try
            build () |> ignore
            None
        with ex ->
            Some ex.Message

    // A state with no way in is dead logic, and a number cannot say so.
    let unreachableRefused =
        refuses (fun () ->
            design "Unreachable" (fun () ->
                let go = inputBit "go"
                let m = machine "st" [ First; Second; Never ]
                m.If First (fun () -> If go (fun () -> m.Goto Second))
                m.If Second (fun () -> m.Goto First)))
        |> Option.exists (fun message -> message.Contains "can never reach Never")

    // A state the machine was not given is not a state, however well it types.
    let unknownRefused =
        refuses (fun () ->
            design "Unknown" (fun () ->
                let m = machine "st" [ First; Second ]
                m.If First (fun () -> m.Goto Never)))
        |> Option.exists (fun message -> message.Contains "is not a state of 'st'")

    sameVerilog && decoded && walked && unreachableRefused && unknownRefused

/// The registry is what the debugger's picker shows, so a bad entry is a crash
/// at selection time rather than at build time. Every one elaborates, builds a
/// Sim, ticks, and has something to watch — and no two share a label.
let private registryLoads () =
    let loads (e: Warp11.Catalog.Entry) =
        try
            let d = e.build ()
            let sim = Sim(d)
            sim.Tick()
            let inv = Inventory.ofDesign d
            not (e.label.Trim() = "") && inv.topName = d.name && not (List.isEmpty inv.signals)
        with _ ->
            false

    // Every entry's `binding` must name something the slicer can find in the
    // catalog. `nameof` proves the identifier exists; this proves it is a
    // *top-level* binding, which is the shape the source pane slices on and the
    // only thing `nameof` cannot tell us.
    let slices (e: Warp11.Catalog.Entry) =
        match Registry.catalog.source e.binding with
        | Some text -> text.Contains e.binding && text.Trim() <> ""
        | None -> false

    let labels = Registry.designs |> List.map (fun e -> e.label)

    List.forall loads Registry.designs
    && List.forall slices Registry.designs
    && List.length (List.distinct labels) = List.length labels

/// The FIRRTL export, checked on the property that matters before `firtool`
/// exists to check it for us: the text is *closed*. Every name it mentions is
/// one it declares, every module the design reaches appears exactly once, and
/// every port lines up with the decl it came from.
///
/// That is weaker than "firtool accepts it" and stronger than "it did not
/// throw" — a dropped declaration, a misnamed instance field or a module lost to
/// the dedupe all fail here, and those are the bugs an emitter actually has.
let private firrtlIsClosed () =
    let closed (d: ModuleDef) =
        let text = Firrtl.emitFirrtl d
        let lines = text.Split '\n' |> Array.map (fun l -> l.Trim())

        let modules = allModules d |> List.distinctBy (fun c -> c.name)

        // One module line per distinct module, and the circuit named for the top.
        // The top is `public module` — FIRRTL 4.0 removed private main modules —
        // so both spellings count.
        let moduleLines =
            lines |> Array.filter (fun l -> l.StartsWith "module " || l.StartsWith "public module ")

        let namesMatch =
            Array.length moduleLines = List.length modules
            && text.Contains $"circuit {d.name} :"

        // Every port of every module, present and typed as declared.
        let portsMatch =
            modules
            |> List.forall (fun md ->
                md.decls
                |> List.forall (function
                    | Input (n, t) -> text.Contains $"input {n} : {Firrtl.typeText t}"
                    | Output (n, t) -> text.Contains $"output {n} : {Firrtl.typeText t}"
                    | _ -> true))

        // Every connect target is a name this circuit declares, or a field of an
        // instance or memory port (which the dot makes obvious).
        let declared =
            set
                [ for md in modules do
                    for decl in md.decls do
                        match declOf decl with
                        | Some (n, _) -> yield n
                        | None -> ()

                    for decl in md.decls do
                        match decl with
                        | Memory(n, _, _, _, _) -> yield n
                        | _ -> () ]

        let targetsDeclared =
            lines
            |> Array.filter (fun l -> l.StartsWith "connect ")
            |> Array.forall (fun l ->
                let target = l.Substring("connect ".Length).Split(',').[0].Trim()
                target.Contains "." || declared.Contains target)

        namesMatch && portsMatch && targetsDeclared

    // A ROM's contents cannot be said in `.fir`, so the export refuses by name
    // rather than emitting a memory that reads as zeros. That refusal is part of
    // the contract and is checked like anything else.
    let refusesRomInit =
        let preloaded =
            design "FirrtlRomRefusal" (fun () ->
                let addr = input "addr" 2
                let out = output "out" 8
                let lookup = distributedRom "lookup" 8 [| 1UL; 2UL; 3UL; 4UL |]
                memRead lookup addr ==> out)

        try
            Firrtl.emitFirrtl preloaded |> ignore
            false
        with Firrtl.Unrepresentable message ->
            message.Contains "initial contents"

    // A lane-masked memory exports as a FIRRTL vector, which `firtool` compiles
    // and the differential's third leg checks — but the *reader* refuses one, so
    // it cannot round-trip. Named here rather than quietly filtered, the way a
    // ROM's contents are.
    let laneMasked (d: ModuleDef) =
        d.stmts |> List.exists (function MemWrite (_, _, _, _, Some _) -> true | _ -> false)

    let exportable =
        Registry.designs
        |> List.map (fun e -> e.build ())
        |> List.filter (fun d ->
            not (laneMasked d)
            && d.decls
               |> List.forall (function
                   | Memory(_, _, _, Some _, _) -> false
                   | _ -> true))

    List.forall closed exportable && refusesRomInit && not (List.isEmpty exportable)

/// The import's property: a design that goes out as `.fir` and comes back
/// through the reader emits the *identical* Verilog.
///
/// That is a strong check and a narrow one, and both halves are worth saying.
/// Strong, because it is byte-identical against the emitter this repo already
/// trusts, over every construct the catalogs use — a dropped statement, a
/// mis-scoped name, a memory write landing in the wrong place all fail it, and
/// each of those was a real bug while this was written. Narrow, because it only
/// proves the reader and the writer agree: a construct misunderstood in the same
/// way twice would round-trip happily. Reading FIRRTL nobody here wrote is what
/// closes that gap, and is the next piece rather than this one.
let private firrtlRoundTrips () =
    // A lane-masked memory exports as a FIRRTL vector — `firtool` compiles it and
    // the differential's third leg checks it — but the reader refuses one, so it
    // cannot come back. Excluded by name, the way a ROM's contents are.
    let laneMasked (d: ModuleDef) =
        d.stmts |> List.exists (function MemWrite (_, _, _, _, Some _) -> true | _ -> false)

    let exportable =
        Registry.designs
        |> List.map (fun e -> e.build ())
        |> List.filter (fun d ->
            not (laneMasked d)
            && d.decls
               |> List.forall (function
                   | Memory(_, _, _, Some _, _) -> false
                   | _ -> true))

    // `ram_style` is the one thing a round trip loses, and it is worth being
    // exact about what that means. FIRRTL has no notion of storage style — it
    // describes a circuit, not how a synthesiser should build one — so an
    // imported memory comes back `Unspecified`. Nothing about *behaviour*
    // changes, which is why the comparison strips the attribute rather than the
    // export refusing (as it does for a ROM's contents, where behaviour would
    // change). What is lost is a directive to Vivado, and `hdl/README.md` says
    // so beside the rest of the subset.
    let withoutRamStyle (verilog: string) =
        System.Text.RegularExpressions.Regex.Replace(verilog, """\(\* ram_style = "[a-z]*" \*\) """, "")

    let roundTrips (d: ModuleDef) =
        try
            let back = withoutRamStyle (emitDesign (FirrtlImport.importFirrtl (Firrtl.emitFirrtl d)))
            back = withoutRamStyle (emitDesign d)
        with _ ->
            false

    // What the reader does *not* accept, checked rather than described. Every
    // one of these is a line `hdl/README.md` claims is refused; a silent
    // acceptance would mean a construct read as something it is not, and an
    // unrecognised statement dropped is circuit behaviour dropped.
    let refuses (body: string) (expected: string) =
        let header =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input clock : Clock
    input reset : UInt<1>
    input a : UInt<8>
    input b : UInt<8>
    output o : UInt<8>
"""

        let text = header + body

        try
            FirrtlImport.importFirrtl text |> ignore
            false
        with FirrtlImport.Unsupported message ->
            message.Contains expected

    let refusesWhatItSaysItDoes =
        [ "    when a :\n      connect o, a\n", "when"
          "    connect o, asClock(a)\n", "asClock"
          "    printf(clock, UInt<1>(1), \"hi\")\n    connect o, a\n", "printf"
          "    stop(clock, UInt<1>(1), 0)\n    connect o, a\n", "stop"
          "    attach(a, b)\n    connect o, a\n", "attach"
          "    wire w : { x : UInt<8> }\n    connect o, a\n", "bundle"
          // The catch-all. Dropping a statement nobody recognised is the one
          // failure that changes a design without saying so.
          "    frobnicate o, a\n", "unrecognised statement" ]
        |> List.forall (fun (body, expected) -> refuses body expected)

    // The counterparts: constructs that used to be refused and now read as what
    // they mean. A refusal check that outlives the refusal passes forever while
    // testing nothing, so each retirement moves to this side of the ledger.
    let readsUnresetRegisters =
        let text =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input clock : Clock
    input reset : UInt<1>
    input a : UInt<8>
    output o : UInt<8>
    reg r : UInt<8>, clock
    connect r, a
    connect o, r
"""

        try
            let d = FirrtlImport.importFirrtl text

            d.decls
            |> List.exists (function
                | Reg (_, _, None) -> true
                | _ -> false)
        with _ ->
            false

    let readsDynamicShifts =
        let text =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input a : UInt<8>
    input n : UInt<3>
    output o : UInt<15>
    connect o, dshl(a, n)
"""

        try
            let d = FirrtlImport.importFirrtl text
            // 8 + 2^3 - 1 = 15: FIRRTL's width, kept rather than guessed at.
            emitDesign d |> ignore
            true
        with _ ->
            false

    let readsReductions =
        let text =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input a : UInt<8>
    output o : UInt<1>
    connect o, orr(a)
"""

        try
            emitDesign (FirrtlImport.importFirrtl text) |> ignore
            true
        with _ ->
            false

    // Division by a *signal* is the case the authoring surface will not express
    // and the reader must, since a foreign design is entitled to say it.
    let readsVariableDivision =
        let text =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input a : UInt<8>
    input b : UInt<8>
    output o : UInt<8>
    connect o, div(a, b)
"""

        try
            emitDesign (FirrtlImport.importFirrtl text) |> ignore
            true
        with _ ->
            false

    let refusesHighFirrtl =
        refusesWhatItSaysItDoes
        && readsUnresetRegisters
        && readsDynamicShifts
        && readsReductions
        && readsVariableDivision

    List.forall roundTrips exportable && refusesHighFirrtl && not (List.isEmpty exportable)

/// `regNoReset`'s defining property, which is about *reset* and nothing else:
/// the two registers take the same value from the same input on the same edge,
/// and diverge only when reset is asserted.
///
/// Checked against the Sim rather than by reading the Verilog, because the
/// Verilog is the easy half — a missing line in the reset branch is visible on
/// inspection, but that the simulator agrees is what the differential then
/// carries onto silicon.
let private holdsThroughReset () =
    let sim = Sim holdThroughReset

    sim.Poke("value", 42UL)
    sim.Tick()

    let bothTook = sim.Peek "held_out" = 42UL && sim.Peek "cleared_out" = 42UL

    sim.Reset()

    // The whole difference, in one line each.
    let heldSurvived = sim.Peek "held_out" = 42UL
    let clearedWentBack = sim.Peek "cleared_out" = 3UL

    // And the emission says the same thing: one register is named in the reset
    // branch and the other is not.
    let verilog = emitDesign holdThroughReset
    let start = verilog.IndexOf "if (rst)"
    let stop = verilog.IndexOf("end else", start)
    let resetBranch = verilog.Substring(start, stop - start)
    let saysSo = resetBranch.Contains "cleared <=" && not (resetBranch.Contains "held <=")

    bothTook && heldSurvived && clearedWentBack && saysSo

/// A dynamic shift's defining property: the amount is a *signal*, so one piece
/// of hardware does every shift, and the arithmetic one fills with the sign
/// where the logical one fills with zeros.
///
/// Walked over every amount the input can take rather than spot-checked, and
/// against shifts computed here rather than against a recorded vector — a
/// barrel shifter wired to the wrong bit still produces plausible numbers, and
/// only the full sweep says otherwise.
let private dynamicShiftsShift () =
    let sim = Sim dynamicShifts

    [ for value in [ 1UL; 0xA5UL; 0xFFUL ] do
        for amount in 0UL..7UL do
            sim.Poke("value", value)
            sim.Poke("amount", amount)
            sim.Poke("signed_value", value)
            sim.Tick()

            // Left shift keeps every bit: 8 + 2^3 - 1 = 15 wide, so nothing wraps.
            let left = sim.Peek "shifted_left" = (value <<< int amount)
            let right = sim.Peek "shifted_right" = (value >>> int amount)

            // Arithmetic: the sign fills in from the top. 0xA5 and 0xFF are
            // negative as 8-bit values, so this is where the two shifts part.
            let expected = uint64 ((int64 (sbyte value)) >>> int amount) &&& 0xFFUL
            let arith = sim.Peek "shifted_arith" = expected

            // And the constant form is still a rewiring.
            let fixedShift = sim.Peek "shifted_fixed" = (value <<< 3)

            yield left && right && arith && fixedShift ]
    |> List.forall id

/// The reductions, over **every value an 8-bit input can take** rather than a
/// handful. That matters more here than it looks: `anyBitSet` and `allBitsSet`
/// differ from each other on 254 of 256 inputs and agree on the two that a spot
/// check is most likely to try, and a parity tree missing one bit is right half
/// the time.
let private reductionsReduce () =
    let sim = Sim bitReductions

    [ for value in 0UL..255UL do
        sim.Poke("value", value)
        sim.Tick()

        let bits = [ for i in 0..7 -> (value >>> i) &&& 1UL ]

        yield
            sim.Peek "any" = (if List.exists ((=) 1UL) bits then 1UL else 0UL)
            && sim.Peek "all" = (if List.forall ((=) 1UL) bits then 1UL else 0UL)
            && sim.Peek "odd" = (List.sum bits % 2UL) ]
    |> List.forall id

/// Division's defining property, over every value an 8-bit input can take:
/// quotient and remainder agree with the host language's, and the signed one
/// truncates toward zero rather than toward negative infinity — which is where
/// a divider written by hand usually goes wrong, since a shift does the other
/// thing.
///
/// The rule that a divisor must be constant is not checked here because it
/// cannot be broken: `divideBy` takes an `int`, so a signal divisor does not
/// compile.
let private divisionDivides () =
    let sim = Sim constantDivision

    [ for value in 0UL..255UL do
        sim.Poke("value", value)
        sim.Poke("signed_value", value)
        sim.Tick()

        let signed = int64 (sbyte value)

        yield
            sim.Peek "tenths" = value / 10UL
            && sim.Peek "units" = value % 10UL
            && sim.Peek "eighths" = value / 8UL
            // Nine bits, and toward zero: -7 / 3 is -2, not -3.
            && sim.Peek "thirds" = (uint64 (signed / 3L) &&& 0x1FFUL) ]
    |> List.forall id

/// The divider's two properties, and the second is the interesting one.
///
/// It divides — checked against the host language over every dividend and a
/// sweep of divisors, because a divider wired to the wrong bit still produces
/// plausible numbers for small inputs.
///
/// And **the handshake is load-bearing, not decoration**: `ready` is low for
/// every cycle it is working, and a result waits rather than evaporating when
/// the consumer is not taking. Those two are what let a caller use this without
/// knowing how long it takes, which is the entire reason it is a stream stage
/// and not a fixed-latency core.
let private dividerDivides () =
    let sim = Sim streamDivider

    let divide (a: uint64) (b: uint64) =
        sim.Poke("dividend", a)
        sim.Poke("divisor", b)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 1UL)

        while sim.Peek "in_ready" <> 1UL do
            sim.Tick()

        sim.Tick()
        sim.Poke("in_valid", 0UL)

        // Working: it must refuse more work while it has some.
        let mutable refusedWhileBusy = true

        while sim.Peek "out_valid" <> 1UL do
            if sim.Peek "in_ready" = 1UL then refusedWhileBusy <- false
            sim.Tick()

        let q, r = sim.Peek "quotient", sim.Peek "remainder"
        sim.Tick()
        q, r, refusedWhileBusy

    let arithmetic =
        [ for a in 0UL..255UL do
            for b in [ 1UL; 2UL; 3UL; 7UL; 16UL; 100UL; 255UL ] do
                let q, r, refused = divide a b
                yield q = a / b && r = a % b && refused ]
        |> List.forall id

    // A result waits for its consumer. Without this the stage would be a
    // latency in disguise, and a slow consumer would silently lose beats.
    let resultWaits =
        sim.Poke("dividend", 100UL)
        sim.Poke("divisor", 7UL)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 0UL)

        while sim.Peek "in_ready" <> 1UL do
            sim.Tick()

        sim.Tick()
        sim.Poke("in_valid", 0UL)

        while sim.Peek "out_valid" <> 1UL do
            sim.Tick()

        // Twenty cycles of a consumer that is not ready, and the answer is
        // still there and still 14.
        [ for _ in 1..20 do
            sim.Tick()
            yield sim.Peek "out_valid" = 1UL && sim.Peek "quotient" = 14UL ]
        |> List.forall id

    // Division by zero saturates rather than trapping: every trial subtraction
    // succeeds, so the quotient is all ones.
    let byZero =
        let q, _, _ = divide 42UL 0UL
        q = 255UL

    arithmetic && resultWaits && byZero

/// The FIFO against a software queue, under stimulus that offers and takes at
/// random — which is the only way to reach the states that matter. A FIFO is
/// easy to get right when it is never full and never empty; every real bug
/// lives at a boundary, and a fixed script visits those by luck.
///
/// Three claims, and the third is the one a hand-written test usually forgets:
/// beats come out in the order they went in, none is lost or duplicated, and the
/// occupancy never exceeds the depth that was asked for.
let private fifoModel design depth =
    let sim = Sim design
    let rng = System.Random 4242
    let model = System.Collections.Generic.Queue<uint64>()

    let mutable next = 1UL
    let mutable ok = true

    for _ in 1..20000 do
        let offer = rng.Next 2 = 0
        let take = rng.Next 2 = 0

        sim.Poke("in_data", next)
        sim.Poke("in_valid", (if offer then 1UL else 0UL))
        sim.Poke("out_ready", (if take then 1UL else 0UL))

        let accepted = offer && sim.Peek "in_ready" = 1UL

        if take && sim.Peek "out_valid" = 1UL then
            if model.Count = 0 then ok <- false
            elif sim.Peek "out_data" <> model.Dequeue() then ok <- false

        if accepted then
            model.Enqueue next
            next <- (next % 250UL) + 1UL

        if model.Count > depth then ok <- false

        sim.Tick()

    ok

/// A lane strobe's defining property: **the lanes it leaves alone keep what was
/// already there.**
///
/// The model is a plain array of words and a per-lane merge, and the stimulus is
/// random addresses, random data and random strobes — including 0 (a write that
/// writes nothing) and 15 (a whole word). What makes it discriminating is that
/// every address is written repeatedly with different strobes, so a lane that
/// was wrongly overwritten holds a value no correct run would produce, and a
/// strobe applied to the wrong lane swaps two bytes that the whole-word read
/// puts side by side.
let private strobeSparesUntouchedLanes () =
    let sim = Sim maskedWrite
    let rng = System.Random 606
    let model = Array.zeroCreate<uint64> 8

    let mutable ok = true
    let mutable checks = 0

    // Read-first, and the check depends on it: the word the read register will
    // present is the one standing *before* this cycle's write lands, so the
    // expectation is captured now and compared next cycle.
    let mutable pending = -1L

    for _ in 1..6000 do
        let waddr = rng.Next 8
        let wdata = uint64 (rng.Next()) &&& 0xFFFFFFFFUL
        let strb = rng.Next 16
        let wen = rng.Next 4 <> 0
        let raddr = rng.Next 8

        sim.Poke("waddr", uint64 waddr)
        sim.Poke("wdata", wdata)
        sim.Poke("wstrb", uint64 strb)
        sim.Poke("wen", (if wen then 1UL else 0UL))
        sim.Poke("raddr", uint64 raddr)

        if pending >= 0L then
            checks <- checks + 1
            if sim.Peek "rdata" <> uint64 pending then ok <- false

        pending <- int64 model[raddr]

        if wen then
            let mutable word = model[waddr]

            for lane in 0..3 do
                if (strb >>> lane) &&& 1 = 1 then
                    let keep = 0xFFUL <<< (lane * 8)
                    word <- (wdata &&& keep) ||| (word &&& ~~~keep)

            model[waddr] <- word

        sim.Tick()

    ok && checks > 5000

/// The same strobe property at 128 bits, where the simulator's uint64 store
/// cannot represent a word and the BigInteger path is the one under test.
///
/// The model mirrors the hardware exactly as `strobeSparesUntouchedLanes` does,
/// with every value a BigInteger. Each lane gets a distinct byte pattern so a
/// lane landing in the wrong 32-bit slot of the wide word is visible, not just
/// a lane that failed to land.
let private strobeSparesWideLanes () =
    let sim = Sim maskedWriteWide
    let rng = System.Random 128128
    let model = Array.create 8 BigInteger.Zero
    let laneMask = (BigInteger.One <<< 32) - BigInteger.One

    let mutable ok = true
    let mutable checks = 0
    let mutable pending = None

    for _ in 1..4000 do
        let waddr = rng.Next 8
        // Four distinct 32-bit lane values assembled into one 128-bit word.
        let wdata =
            [ 0..3 ]
            |> List.fold (fun acc lane -> acc ||| (BigInteger(uint64 (rng.Next()) &&& 0xFFFFFFFFUL) <<< (lane * 32))) BigInteger.Zero

        let strb = rng.Next 16
        let wen = rng.Next 4 <> 0
        let raddr = rng.Next 8

        sim.Poke("waddr", uint64 waddr)
        sim.PokeWide("wdata", wdata)
        sim.Poke("wstrb", uint64 strb)
        sim.Poke("wen", (if wen then 1UL else 0UL))
        sim.Poke("raddr", uint64 raddr)

        match pending with
        | Some expected ->
            checks <- checks + 1
            if sim.PeekWide "rdata" <> expected then ok <- false
        | None -> ()

        pending <- Some model[raddr]

        if wen then
            let mutable word = model[waddr]

            for lane in 0..3 do
                if (strb >>> lane) &&& 1 = 1 then
                    let keep = laneMask <<< (lane * 32)
                    word <- (wdata &&& keep) ||| (word &&& (BigInteger.MinusOne ^^^ keep))

            model[waddr] <- word

        sim.Tick()

    ok && checks > 3500

/// The pipelined channel's three claims, driven by hand because `SimAxi.client`
/// is deliberately one-at-a-time and one-at-a-time is what this channel is not.
///
/// **In order and correct** under back-to-back accepts — eight reads over both
/// sources, every response the right word for its own address, which is what
/// the capture register exists for: without it, a source answers about the
/// address that replaced the one it was asked. **A response per cycle behind
/// the first** — the eight complete in ~11 cycles where one-outstanding needs
/// two-plus per read. **Credits cap the pipeline** — with the host not taking
/// responses, exactly `maxOutstanding` accepts happen and ARREADY holds off
/// the fifth; releasing the host drains all four, still in order.
let private pipelinedChannelPipelines () =
    let sim = Sim pipelinedReadSlave
    let axi = SimAxi.client sim

    axi.write32 0x00UL 0xAAAA5555UL // scratch, word 0

    for i in 0..7 do
        axi.write32 (0x20UL + uint64 (i * 4)) (0xB000UL + uint64 i) // table, words 8..15

    let expected (w: int) =
        if w >= 8 then 0xB000UL + uint64 (w - 8)
        elif w = 0 then 0xAAAA5555UL
        else 0x11C0DEUL

    // Burst 1: eight back-to-back reads, host always ready.
    let addrs = [| 0; 8; 1; 9; 2; 10; 15; 0 |]
    let got = ResizeArray<uint64>()
    let mutable issued = 0
    let mutable cycles = 0

    sim.Poke("s_axi_rready", 1UL)
    sim.Poke("s_axi_arvalid", 1UL)
    sim.Poke("s_axi_araddr", uint64 (addrs[0] * 4))

    while got.Count < 8 && cycles < 40 do
        let accepted = issued < 8 && sim.Peek "s_axi_arready" = 1UL

        if sim.Peek "s_axi_rvalid" = 1UL then
            got.Add(sim.Peek "s_axi_rdata")

        sim.Tick()

        if accepted then
            issued <- issued + 1

            if issued < 8 then
                sim.Poke("s_axi_araddr", uint64 (addrs[issued] * 4))
            else
                sim.Poke("s_axi_arvalid", 0UL)

        cycles <- cycles + 1

    let ordered = got.Count = 8 && Seq.forall2 (fun g a -> g = expected a) got addrs
    let pipelined = cycles <= 13 // one-outstanding needs 2+ per read: >= 16

    // Burst 2: host takes nothing; the credit ceiling is the whole claim.
    sim.Poke("s_axi_rready", 0UL)
    sim.Poke("s_axi_arvalid", 1UL)
    let mutable accepts = 0

    for i in 0..7 do
        sim.Poke("s_axi_araddr", uint64 ((8 + min i 7) * 4))
        if sim.Peek "s_axi_arready" = 1UL then accepts <- accepts + 1
        sim.Tick()

    let capped = accepts = 4 && sim.Peek "s_axi_arready" = 0UL

    // Release: the four queued responses drain, still in order.
    sim.Poke("s_axi_arvalid", 0UL)
    sim.Poke("s_axi_rready", 1UL)
    let drained = ResizeArray<uint64>()

    for _ in 1..10 do
        if sim.Peek "s_axi_rvalid" = 1UL then drained.Add(sim.Peek "s_axi_rdata")
        sim.Tick()

    let inOrder =
        drained.Count = 4 && Seq.forall2 (fun g i -> g = 0xB000UL + uint64 i) drained [ 0; 1; 2; 3 ]

    ordered && pipelined && capped && inOrder

/// The busy flag's defining property: **while a read is in flight, the channel
/// will not take another address.**
///
/// Driven by hand rather than through `SimAxi.client`, because the client asserts
/// ARREADY is high immediately — which is the thing under test here, and it is
/// deliberately low for two of the three cycles.
///
/// Two claims. RVALID rises exactly `answersAfter` cycles after the accept and
/// not before, and ARREADY is low for the whole gap. Drop the flag and the
/// second claim fails on the first cycle of the gap, while the first still
/// passes — so a check that only read values back would call a broken channel
/// working.
let private busyFlagHoldsArOff () =
    let sim = Sim deepChannelSlave
    let answersAfter = 3

    // A word to read back, so the check is about a real answer and not only
    // about handshake timing.
    sim.Poke("s_axi_awaddr", 0UL)
    sim.Poke("s_axi_awvalid", 1UL)
    sim.Poke("s_axi_wdata", 0x5A5A1234UL)
    sim.Poke("s_axi_wvalid", 1UL)
    sim.Poke("s_axi_bready", 1UL)
    sim.Tick()
    sim.Poke("s_axi_awvalid", 0UL)
    sim.Poke("s_axi_wvalid", 0UL)
    sim.Tick()

    // Hold ARVALID high throughout: a channel that forgot to say busy would
    // accept again immediately, which is precisely the bug.
    sim.Poke("s_axi_araddr", 0UL)
    sim.Poke("s_axi_arvalid", 1UL)
    sim.Poke("s_axi_rready", 1UL)

    let acceptedAtOnce = sim.Peek "s_axi_arready" = 1UL
    sim.Tick()

    let mutable heldOff = true
    let mutable roseEarly = false

    // The gap: `answersAfter - 1` cycles in which the answer is not ready.
    for _ in 1 .. answersAfter - 1 do
        if sim.Peek "s_axi_arready" <> 0UL then heldOff <- false
        if sim.Peek "s_axi_rvalid" <> 0UL then roseEarly <- true
        sim.Tick()

    let answered = sim.Peek "s_axi_rvalid" = 1UL && sim.Peek "s_axi_rdata" = 0x5A5A1234UL

    acceptedAtOnce && heldOff && not roseEarly && answered

/// Two windows and a register bank on one AR channel: **each read lands in the
/// source that owns its address**, and the host is told nothing about how many
/// sources there are.
///
/// The two windows hold values derived differently — `even` from its index
/// alone, `odd` with a high nibble and a low bit set — so a read answered by the
/// neighbouring window is wrong in a way this sees. A check that only compared
/// against "some plausible word" would pass a slave whose windows were swapped,
/// or one where the later window shadowed the earlier one entirely.
let private windowsAnswerTheirOwnRange () =
    let sim = Sim twoWindowSlave

    // Let the writer fill both arrays; after four ticks every slot holds its
    // own value and keeps being rewritten with it.
    for _ in 1..8 do
        sim.Tick()

    let axi = SimAxi.client sim

    axi.write32 0x00UL 0xDEADBEEFUL
    let scratchHolds = axi.read32 0x00UL = 0xDEADBEEFUL
    let constHolds = axi.read32 0x04UL = 0x11FA57UL

    let evenRight =
        [ 0..3 ] |> List.forall (fun i -> axi.read32 (0x10UL + uint64 (i * 4)) = uint64 (i * 4))

    let oddRight =
        [ 0..3 ]
        |> List.forall (fun i -> axi.read32 (0x20UL + uint64 (i * 4)) = uint64 (0xA0 + i * 4 + 1))

    scratchHolds && constHolds && evenRight && oddRight

/// `memReadPort`'s defining property: **what comes back describes the request
/// that went in**, and no part of the design states how long that took.
///
/// The check is a software model of the memory plus a queue of in-flight
/// requests, and it never mentions a latency either — it pairs each answered
/// beat with the oldest request still outstanding and asserts the word and the
/// tag agree with *that* request. A port that delayed the tag by two and the
/// word by one would pass any check that only compared data against the
/// address, and fail this one on the first beat.
///
/// Writes run concurrently with reads, so the model has to be updated in the
/// same order the hardware sees them — which is what makes read-first vs
/// write-first observable rather than theoretical.
let private carriedReadPairsUp () =
    let sim = Sim carriedRead
    let rng = System.Random 20250818
    let store = Array.zeroCreate<uint64> 16
    let inFlight = System.Collections.Generic.Queue<uint64 * uint64>()

    let mutable ok = true
    let mutable answered = 0

    for _ in 1..8000 do
        let waddr = uint64 (rng.Next 16)
        let wdata = uint64 (rng.Next 256)
        let wen = rng.Next 3 = 0
        let raddr = uint64 (rng.Next 16)
        let tag = uint64 (rng.Next 256)
        let ask = rng.Next 2 = 0

        sim.Poke("waddr", waddr)
        sim.Poke("wdata", wdata)
        sim.Poke("wen", (if wen then 1UL else 0UL))
        sim.Poke("raddr", raddr)
        sim.Poke("tag", tag)
        sim.Poke("ask", (if ask then 1UL else 0UL))

        // An answer this cycle belongs to the oldest request still owed one.
        if sim.Peek "answered" = 1UL then
            answered <- answered + 1

            match inFlight.TryDequeue() with
            | true, (expectWord, expectTag) ->
                if sim.Peek "data" <> expectWord then ok <- false
                if sim.Peek "tag_out" <> expectTag then ok <- false
            | _ -> ok <- false

        // Read-first: the word this request will be answered with is the one
        // in the array now, before this cycle's write lands.
        if ask then inFlight.Enqueue(store[int raddr], tag)
        if wen then store[int waddr] <- wdata

        sim.Tick()

    ok && answered > 3000

/// Both storages, one model. The LUTRAM form and the block form are different
/// circuits, and this is the assertion that a caller cannot tell.
let private fifoBuffers () =
    fifoModel bufferedStream 8 && fifoModel deepBufferedStream 128

/// The two claims a model check does not make, and the two a storage swap would
/// break quietly.
///
/// **Capacity is exactly `depth`.** Hold the consumer off and count what the
/// FIFO takes before it stops. The block form keeps two beats in its skid and
/// one more in flight from the array, none of which the pointers know about —
/// counted wrong, it would advertise 8 and hold 11.
///
/// **Throughput is a beat per cycle.** With both sides always willing, a full
/// FIFO must move one beat every cycle. This is the claim a one-slot output
/// register fails: a synchronous read cannot be issued until the slot it fills
/// is free, so the reads land every other cycle and the deep FIFO runs at half
/// the shallow one's rate — correct, in order, and silently half the speed.
let private fifoStorageIsInvisible () =
    let capacityOf design =
        let sim = Sim design
        sim.Poke("in_data", 7UL)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 0UL)

        let mutable taken = 0

        // Long enough for either form to fill and refuse; the block form needs
        // a few cycles of slack for its own read latency before it settles.
        for _ in 1..600 do
            if sim.Peek "in_ready" = 1UL then taken <- taken + 1
            sim.Tick()

        taken

    let sustainedRate design =
        let sim = Sim design
        sim.Poke("in_data", 9UL)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 1UL)

        // Prime past the fill and the first read's latency, then measure.
        for _ in 1..64 do
            sim.Tick()

        let mutable moved = 0

        for _ in 1..1000 do
            if sim.Peek "out_valid" = 1UL then moved <- moved + 1
            sim.Tick()

        moved

    capacityOf bufferedStream = 8
    && capacityOf deepBufferedStream = 128
    && sustainedRate bufferedStream = 1000
    && sustainedRate deepBufferedStream = 1000

/// `withContext`'s defining property: **the caller's data comes back attached to
/// its own result**, across a stage that takes eight cycles and has never heard
/// of it, under a consumer that stalls at random.
///
/// The tag is checked rather than just the arithmetic, and that is the point —
/// a divider that computed perfectly while returning someone else's tag would
/// pass an arithmetic check and be useless. Random backpressure is what makes
/// the queue depths vary; with a consumer that always takes, the FIFO holds one
/// beat and the bug hides.
let private contextRidesAlong () =
    let sim = Sim taggedDivide
    let rng = System.Random 99
    let pending = System.Collections.Generic.Queue<uint64 * uint64 * uint64>()

    let mutable tag = 1UL
    let mutable completed = 0
    let mutable ok = true

    for _ in 1..6000 do
        let a = uint64 (rng.Next 256)
        let b = uint64 (rng.Next(1, 256))

        sim.Poke("dividend", a)
        sim.Poke("divisor", b)
        sim.Poke("tag", tag)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", (if rng.Next 3 = 0 then 0UL else 1UL))

        if sim.Peek "in_ready" = 1UL then
            pending.Enqueue(a / b, a % b, tag)
            tag <- (tag % 200UL) + 1UL

        if sim.Peek "out_valid" = 1UL && sim.Peek "out_ready" = 1UL then
            if pending.Count = 0 then
                ok <- false
            else
                let q, r, t = pending.Dequeue()

                if sim.Peek "quotient" <> q || sim.Peek "remainder" <> r || sim.Peek "tag_out" <> t then
                    ok <- false

                completed <- completed + 1

        sim.Tick()

    ok && completed > 100

/// The farm carrying context across lanes that finish out of order.
///
/// Results are matched **by tag rather than by position**, which is the only
/// thing that works here and is the whole claim: a farm returns beats in
/// completion order, so a shadow queue beside it would pair the wrong answer
/// with the wrong request.
///
/// The check also counts how often a result really does arrive out of issue
/// order, and asserts that it is common — because with equal-latency workers it
/// is *rare* (measured: 3 times in 3,000), and a test that never reaches the
/// case it exists for is a test that passes for the wrong reason. The design
/// gives lane `i` `i` extra stages for exactly this.
let private farmCarriesContext () =
    let sim = Sim farmedDivide
    let rng = System.Random 7
    let expected = System.Collections.Generic.Dictionary<uint64, uint64 * uint64>()

    let mutable tag = 1UL
    let mutable completed = 0
    let mutable reordered = 0
    let mutable lastTag = 0UL
    let mutable ok = true

    for _ in 1..8000 do
        let a = uint64 (rng.Next 256)
        let b = uint64 (rng.Next(1, 256))

        sim.Poke("dividend", a)
        sim.Poke("divisor", b)
        sim.Poke("tag", tag)
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", (if rng.Next 4 = 0 then 0UL else 1UL))

        if sim.Peek "in_ready" = 1UL && not (expected.ContainsKey tag) then
            expected[tag] <- (a / b, a % b)
            tag <- (tag % 200UL) + 1UL

        if sim.Peek "out_valid" = 1UL && sim.Peek "out_ready" = 1UL then
            let t = sim.Peek "tag_out"

            match expected.TryGetValue t with
            | true, (q, r) ->
                if sim.Peek "quotient" <> q || sim.Peek "remainder" <> r then ok <- false
                expected.Remove t |> ignore
            | _ -> ok <- false

            // Successor modulo the wrap point; anything else really is a beat
            // that overtook another.
            if lastTag <> 0UL && t <> (lastTag % 200UL) + 1UL then
                reordered <- reordered + 1

            lastTag <- t
            completed <- completed + 1

        sim.Tick()

    ok && completed > 100 && reordered > 100

/// The storage-style rule: **an asynchronous read is only legal on a memory
/// that says it is LUTRAM**, and the emitted Verilog says so to the tool.
///
/// This is the repo's oldest silicon trap turned into an elaboration error.
/// Block RAM cannot read combinationally, so a synthesiser that puts an
/// async-read array in a block inserts a register — and the design passes this
/// Sim, passes Verilator, and corrupts on the board. Nothing here could catch
/// it, which is exactly why the choice is stated rather than inferred.
/// The other half of the agreement rule, and the half that changed behavior: a
/// literal borrows its neighbour's *reading*, not just its width.
///
/// The property, not a vector: what makes this worth checking is that the bits
/// are identical either way, so nothing downstream fails visibly when it is
/// wrong — the value is simply labelled unsigned and the next operation that
/// consults the label (a compare, a pad, a shift) quietly reads it that way.
/// `0 - x` on a signed `x` is the shape that used to come out unsigned.
/// A saturating narrow keeps its operand's reading — the third of the three
/// that dispatch on the source's sign, after `shr` and `pad`.
///
/// The property rather than a vector, and the reason is the same as always: the
/// clamp emits identical bits either way, so a regression here is invisible
/// until something downstream consults the label. A signed clamp has just
/// established the value lies in [−2^(t−1), 2^(t−1)−1]; returning `UInt` would
/// throw exactly that away. The bits are checked next door by `satOps` through
/// both differential legs; what is checked here is the *type*.
let private saturateKeepsTheReading () =
    let signed = signalS "s" 16
    let unsigned = signal "u" 16

    isSigned (saturate 8 signed)
    && not (isSigned (saturate 8 unsigned))
    // Its two siblings, so the three stay stated together and a future change
    // to one of them trips this rather than drifting quietly apart.
    && isSigned (shr 3 signed)
    && isSigned (pad 24 signed)
    && not (isSigned (shr 3 unsigned))
    && not (isSigned (pad 24 unsigned))
    // A no-op saturate is the operand itself, so it cannot invent a reading.
    && isSigned (saturate 16 signed)
    && not (isSigned (saturate 16 unsigned))

let private literalBorrowsTheSign () =
    let signed = signalS "d" 8
    let unsigned = signal "u" 8

    // Each pairs a literal with a signed neighbour; each used to take the
    // literal's unsigned label, from the left operand or the true branch.
    isSigned (sub (lit 0UL 8) signed)
    && isSigned (add (lit 1UL 8) signed)
    && isSigned (mux (signal "c" 1) (lit 0UL 8) signed)
    // And the reading still follows the neighbour rather than being assumed:
    // the same shapes around an unsigned neighbour stay unsigned.
    && not (isSigned (sub (lit 0UL 8) unsigned))
    && not (isSigned (mux (signal "c" 1) (lit 0UL 8) unsigned))

let private ramStyleRule () =
    // The refusal happens where the read is *written* — inside the design body —
    // so elaboration is what throws, not `emitDesign`. Building the design has to
    // be inside the try or the check that proves the rule takes the suite down
    // with it.
    // The `mem` that left this to the synthesiser is gone: it is a compile
    // error now, not an elaboration one, so there is no design to build here
    // and nothing left to check at run time. What remains testable is the rule
    // that still has two sides — block refuses the asynchronous read, and
    // distributed emits the attribute that makes it legal.
    let refusesAsyncOnBlock =
        try
            design "AsyncOnBlock" (fun () ->
                let addr = input "addr" 3
                let out = output "out" 8
                let m = blockMem "blocked" 3 8
                memRead m addr ==> out)
            |> emitDesign
            |> ignore

            false
        with ex ->
            ex.Message.Contains "block RAM cannot read combinationally"

    // And the attribute reaches the Verilog, which is the half that does the
    // work — the error only stops the wrong design being written.
    let attributeIsEmitted =
        let d =
            design "AsyncOnDistributed" (fun () ->
                let addr = input "addr" 3
                let out = output "out" 8
                let m = distributedMem "lutram" 3 8
                memRead m addr ==> out)

        (emitDesign d).Contains "(* ram_style = \"distributed\" *) reg [7:0] lutram"

    // A sync read is legal on any of them: block RAM's whole point.
    let syncIsAlwaysFine =
        let d =
            design "SyncOnBlock" (fun () ->
                let addr = input "addr" 3
                let out = output "out" 8
                let m = blockMem "blocked" 3 8
                (memReadPort m addr).data ==> out)

        (emitDesign d).Contains "(* ram_style = \"block\" *)"

    refusesAsyncOnBlock && attributeIsEmitted && syncIsAlwaysFine

// ---------------------------------------------------------------------------
// Stall-independence, for anything stream-shaped.
//
// The property: stalling a stage changes *when* its beats move, never *what*
// they are. A stage that fails it passes an always-ready harness and misbehaves
// the moment real memory or a real consumer makes it wait — which is how
// `multibandCompressor8` carried a signal shift that no simulation showed until
// the memory model was paced. See notes/SMALL_FINDINGS.md.
//
// Generic over the payload: hand it the input and output field names and it
// drives the two wires itself.

let private runStreamStalled
    (d: ModuleDef)
    (setup: Sim -> unit)
    (inFields: string list)
    (outFields: string list)
    (beats: uint64 list list)
    (seed: int option)
    =
    let sim = Sim d
    setup sim
    let rng = seed |> Option.map System.Random
    let out = ResizeArray<uint64 list>()
    let mutable fed = 0
    let mutable guard = 0

    while out.Count < beats.Length && guard < 400_000 do
        let offer = fed < beats.Length && (match rng with Some r -> r.Next(0, 4) > 0 | None -> true)
        let take = match rng with Some r -> r.Next(0, 4) > 0 | None -> true

        sim.Poke("in_valid", (if offer then 1UL else 0UL))
        sim.Poke("out_ready", (if take then 1UL else 0UL))

        if offer then
            List.iter2 (fun f v -> sim.Poke(f, v)) inFields beats[fed]

        if take && sim.Peek "out_valid" = 1UL then
            out.Add(outFields |> List.map sim.Peek)

        let accepted = offer && sim.Peek "in_ready" = 1UL
        sim.Tick()

        if accepted then
            fed <- fed + 1

        guard <- guard + 1

    List.ofSeq out

/// Every stall pattern must agree with the unstalled run.
///
/// `ordered` says which agreement is owed. A pipeline owes the same *sequence*.
/// A farm explicitly does not: it dispatches lowest-ready and its results leave
/// in completion order, so stalls legitimately reorder them — what it owes is
/// the same *set*, with every result still carrying its own tag. Testing a farm
/// for sequence equality would report a defect that is the documented design.
let private streamAgreesUnderStalls (ordered: bool) (label: string) d setup inFields outFields (beats: uint64 list list) =
    let canon (r: uint64 list list) = if ordered then r else List.sort r
    let baseline = runStreamStalled d setup inFields outFields beats None

    let ok =
        baseline.Length = beats.Length
        && ([ 1..6 ]
            |> List.forall (fun seed ->
                canon (runStreamStalled d setup inFields outFields beats (Some seed)) = canon baseline))

    let note = if ordered then "" else "  (as a set — a farm may reorder)"
    printfn $"      stall-independent: %-16s{label} %b{ok}{note}"
    ok

/// The divider family: the unit itself, the same unit carrying a caller's tag
/// through `withContext`, and a farm of four. All three are the shape the audio
/// defect lived in — a multi-cycle unit behind a two-wire interface.
let private dividersAreStallIndependent () =
    let rand = System.Random 11
    let pairs = [ for _ in 1..24 -> [ uint64 (rand.Next(1, 256)); uint64 (rand.Next(1, 256)) ] ]
    let tagged = pairs |> List.mapi (fun i b -> b @ [ uint64 (i % 16) ])

    [ streamAgreesUnderStalls true "divider" streamDivider ignore [ "dividend"; "divisor" ] [ "quotient"; "remainder" ] pairs
      streamAgreesUnderStalls true "divider+context" taggedDivide ignore [ "dividend"; "divisor"; "tag" ] [ "quotient"; "remainder"; "tag_out" ] tagged
      streamAgreesUnderStalls false "divider farm" farmedDivide ignore [ "dividend"; "divisor"; "tag" ] [ "quotient"; "remainder"; "tag_out" ] tagged ]
    |> List.forall id

let private mainDemo () =
    for d in
        [ add3
          dot2
          dot2Auto
          dot2Ambient
          dot2Inline
          pipelinedDot
          gatedCounter
          streamPipe
          coordPipe
          onCounter
          onPriority
          loopPipeline
          treeSum
          ramTest
          cmdProcessor
          unionRoundTrip
          forkJoin
          signedOps
          xorOps
          satOps
          escapeStep
          escapeStepFixed
          escapeStep28
          wideBeat
          widenOps
          dispatchRoundTrip
          clusteredRoundTrip
          twoStreamSplit
          twoStreamSplitReplicateJoin
          framePipeline
          sweepPipeline 2
          typedPipeline
          probedPipe
          axiWriteMaster
          axiWriteMasterSingle
          axiReadMaster
          axiReadMasterSingle
          axiReadMasterBurst
          axiPulse
          axiScratch
          neighborCount
          regMapScratch
          snapshotConflate
          snapshotDdr
          audioOps
          audioChain
          audioTone
          i2sLoopback
          multibandStage ] do
        printfn "%s" (emitDesign d)
        printfn ""

    printfn $"mulOf 8 memoized:             %b{System.Object.ReferenceEquals(mulOf 8, mulOf 8)}"
    printfn $"audio chain unity passthrough:%b{audioUnityPassthrough ()}"
    printfn $"audio FIR preset DC response: %b{audioFirDcResponse ()}"
    printfn $"compressor regulates output:  %b{compressorRegulatesOutput ()}"
    printfn $"8-band split reconstructs:    %b{multibandReconstructs ()}"
    printfn $"WAV through the multiband:    %b{wavThroughMultiband ()}"
    printfn $"RBJ cookbook designs:         %b{rbjCookbookDesigns ()}"
    printfn $"I2S rx decodes ideal frame:   %b{i2sRxDecodes ()}"
    printfn $"I2S tx emits ideal frame:     %b{i2sTxEmits ()}"
    printfn $"I2S loopback round trip:      %b{i2sLoopbackRoundTrip ()}"

    // The Fixed layer compiles away: every line except the module header and the
    // escape compare (Number.lessThan is signed; the hand-written design chose the unsigned
    // trick) must be byte-identical between the raw and typed designs.
    let minusEscape (v: string) =
        [ for line in v.Split('\n') do
              if not (line.StartsWith "module " || line.Contains "assign escape") then
                  yield line ]

    printfn $"Fixed layer compiles away:    %b{minusEscape (emitVerilog escapeStep) = minusEscape (emitVerilog escapeStepFixed)}"

    // saturate/saturateS/shl/shr against their software meanings, on the
    // boundary patterns (clamp points, sign flips) plus a spread of ordinary
    // values.
    let satOk =
        let sim = Sim(satOps)

        let clampS (v: uint64) =
            let signed = if v >= 128UL then int64 v - 256L else int64 v
            uint64 (max -8L (min 7L signed)) &&& 0xFUL

        Seq.forall
            (fun (a, b) ->
                sim.Poke("a", a)
                sim.Poke("b", b)
                sim.Tick()

                sim.Peek "narrow_u" = min a 15UL
                && sim.Peek "narrow_s" = clampS a
                && sim.Peek "sum_u" = min (a + b) 255UL
                && sim.Peek "shifted" = a * 16UL
                && sim.Peek "high" = a / 8UL)
            (Seq.allPairs [ 0UL; 7UL; 8UL; 15UL; 16UL; 127UL; 128UL; 200UL; 255UL ] [ 0UL; 1UL; 100UL; 255UL ])

    printfn $"saturate/shift semantics:     %b{satOk}"

    // The read-master rehearsal: every path (ring, single, burst) against the
    // paced behavioral DDR across a pacing matrix — an always-ready slave
    // structurally cannot exercise the stall paths, and a stalling consumer
    // (respEvery > 1) exercises backpressure on the resp side.
    let axiReadOk =
        let fill (memory: byte[]) =
            for i in 0 .. memory.Length - 1 do
                memory[i] <- byte ((i * 37 + 11) % 256)

        let word (memory: byte[]) (addr: int) =
            uint64 memory[addr]
            ||| (uint64 memory[addr + 1] <<< 8)
            ||| (uint64 memory[addr + 2] <<< 16)
            ||| (uint64 memory[addr + 3] <<< 24)

        let runSingleWord (d: ModuleDef) (arEvery: int, rDelay: int, respEvery: int) =
            let sim = Sim(d)
            let slave = SimAxiReadSlave(sim, 4096, dataBytes = 4, arEvery = arEvery, rDelay = rDelay)
            fill slave.Memory
            let addrs = [| for i in 0 .. 23 -> (i * 164) % 4092 &&& ~~~3 |]
            let results = ResizeArray<uint64>()
            let mutable next = 0
            let mutable cycles = 0

            while results.Count < addrs.Length && cycles < 3000 do
                sim.Poke("resp_ready", (if cycles % respEvery = 0 then 1UL else 0UL))

                if next < addrs.Length then
                    sim.Poke("req_valid", 1UL)
                    sim.Poke("req_addr", uint64 addrs[next])
                else
                    sim.Poke("req_valid", 0UL)

                slave.BeginCycle()
                let accepted = next < addrs.Length && sim.Peek "req_ready" = 1UL

                if sim.Peek "resp_valid" = 1UL && sim.Peek "resp_ready" = 1UL then
                    results.Add(sim.Peek "resp_data")

                slave.FinishCycle()
                if accepted then next <- next + 1
                cycles <- cycles + 1

            results.Count = addrs.Length
            && Seq.forall2 (fun r (a: int) -> r = word slave.Memory a) results addrs

        let runBurst (arEvery: int, rDelay: int, respEvery: int) =
            let sim = Sim(axiReadMasterBurst)
            let slave = SimAxiReadSlave(sim, 4096, dataBytes = 4, arEvery = arEvery, rDelay = rDelay)
            fill slave.Memory
            let bursts = [| 0, 16; 512, 1; 1024, 8; 2048, 4; 64, 16; 3000, 2 |]

            let expected =
                [ for addr, beats in bursts do
                      for k in 0 .. beats - 1 ->
                          word slave.Memory (addr + 4 * k), (if k = beats - 1 then 1UL else 0UL) ]

            let results = ResizeArray<uint64 * uint64>()
            let mutable next = 0
            let mutable cycles = 0

            while results.Count < expected.Length && cycles < 3000 do
                sim.Poke("resp_ready", (if cycles % respEvery = 0 then 1UL else 0UL))

                if next < bursts.Length then
                    let addr, beats = bursts[next]
                    sim.Poke("req_valid", 1UL)
                    sim.Poke("req_addr", uint64 addr)
                    sim.Poke("req_len", uint64 (beats - 1))
                else
                    sim.Poke("req_valid", 0UL)

                slave.BeginCycle()
                let accepted = next < bursts.Length && sim.Peek "req_ready" = 1UL

                if sim.Peek "resp_valid" = 1UL && sim.Peek "resp_ready" = 1UL then
                    results.Add(sim.Peek "resp_data", sim.Peek "resp_last")

                slave.FinishCycle()
                if accepted then next <- next + 1
                cycles <- cycles + 1

            List.ofSeq results = expected

        let matrix = [ 1, 0, 1; 3, 2, 1; 2, 5, 2; 7, 3, 3 ]

        List.forall
            (fun pacing ->
                runSingleWord axiReadMaster pacing
                && runSingleWord axiReadMasterSingle pacing
                && runBurst pacing)
            matrix

    printfn $"axi read rehearsal (3 paths x 4 pacings): %b{axiReadOk}"

    try
        emitDesign nameCollision |> ignore
        printfn "name collision:               NOT detected — emitDesign is broken"
    with ex ->
        printfn $"emitDesign refused:           {ex.Message}"

    try
        emitDesign widthViolation |> ignore
        printfn "width violation:              NOT detected — emitDesign is broken"
    with ex ->
        printfn $"emitDesign refused:           {ex.Message}"

    try
        declCollision () |> ignore
        printfn "declaration collision:        NOT detected — the declaration check is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    try
        emitDesign danglingStream |> ignore
        printfn "dangling stream:              NOT detected — checkStreams is broken"
    with ex ->
        printfn $"emitDesign refused:           {ex.Message}"

    try
        onOverlappingWindows () |> ignore
        printfn "overlapping windows:          NOT detected — the aperture check is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    try
        onRegisterInsideWindow () |> ignore
        printfn "register under a window:      NOT detected — the aperture check is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    try
        onBadWire () |> ignore
        printfn "wire without default:         NOT detected — If folding is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    try
        doubleAssign () |> ignore
        printfn "double assign:                NOT detected — the one-driver rule is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    // The transporter round trip: dematerialize four ragged-width fields onto
    // one bus, materialize them back, assert every field survives untouched.
    let transporterOk =
        let sim = Sim(transporterRoundTrip)
        let vectors = [ 0UL, 0UL, 0UL, 0UL; 31UL, 0xFFFFFFFFUL, 127UL, 0xFFFFFFFFUL; 21UL, 0xDEADBEEFUL, 85UL, 0xCAFEF00DUL ]

        vectors
        |> List.forall (fun (a, b, c, d) ->
            sim.Poke("a", a)
            sim.Poke("b", b)
            sim.Poke("c", c)
            sim.Poke("d", d)
            sim.Tick()

            sim.Peek "outA" = a
            && sim.Peek "outB" = b
            && sim.Peek "outC" = c
            && sim.Peek "outD" = d)

    printfn $"transporter round trip:       %b{transporterOk}"

    // The override idiom the one-driver rule must NOT break: an unconditional
    // default plus a conditional override, which lands in a child scope.
    let overrideOk =
        try
            design "OverrideIdiom" (fun () ->
                let enable = inputBit "enable"
                let out = output "out" 8
                lit 0UL 8 ==> out
                If enable (fun () -> lit 1UL 8 ==> out))
            |> ignore

            true
        with _ ->
            false

    printfn $"default + If override still ok: %b{overrideOk}"

    try
        design "KeywordCheck" (fun () -> wireBit "matches" |> ignore) |> ignore
        printfn "reserved-word name:           NOT detected — the keyword check is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    try
        mul (litS 3UL 8) (litS 5UL 8) |> ignore
        printfn "signed mul of a computed val: NOT detected — the operand rule is broken"
    with ex ->
        printfn $"signed mul refused:           {ex.Message}"

    // The one that was unreachable while `mulS` existed: it reinterpreted both
    // operands, so a mismatched pair could not be built. A plain `mul` can be
    // handed one, and extending each side by the pair's reading would zero-extend
    // a signed operand — a wrong product, silent through lint and synthesis.
    try
        mul (signalS "x" 8) (signal "y" 8) |> ignore
        printfn "mul of mixed signedness:      NOT detected — the agreement rule is broken"
    with ex ->
        printfn $"mixed-sign mul refused:       {ex.Message}"

    try
        lt (signalS "x" 8) (signal "y" 8) |> ignore
        printfn "lt of mixed signedness:       NOT detected — the agreement rule is broken"
    with ex ->
        printfn $"mixed-sign lt refused:        {ex.Message}"

    // Add, subtract and select are sign-agnostic at the *gates* — two's
    // complement, and a mux only moves bits — but each takes the result's
    // reading from one side (the left operand, the true branch), so operands
    // that disagree make the answer depend on which was written first.
    try
        add (signalS "x" 8) (signal "y" 8) |> ignore
        printfn "add of mixed signedness:      NOT detected — the agreement rule is broken"
    with ex ->
        printfn $"mixed-sign add refused:       {ex.Message}"

    try
        mux (signal "c" 1) (signalS "x" 8) (signal "y" 8) |> ignore
        printfn "mux of mixed signedness:      NOT detected — the agreement rule is broken"
    with ex ->
        printfn $"mixed-sign mux refused:       {ex.Message}"

    try
        sra 8 (signal "x" 8) |> ignore
        printfn "sra past the width:           NOT detected — the range check is broken"
    with ex ->
        printfn $"sra refused:                  {ex.Message}"

    try
        signExtend 4 (signal "x" 8) |> ignore
        printfn "signExtend that narrows:      NOT detected — the width rule is broken"
    with ex ->
        printfn $"signExtend refused:           {ex.Message}"

    try
        lt (signalS "x" 8) (signalS "y" 16) |> ignore
        printfn "signed lt on mixed widths:    NOT detected — the width rule is broken"
    with ex ->
        printfn $"signed lt refused:            {ex.Message}"

    try
        Number.renormTo Number.q9_7 (Number.ofBits Number.q4_4 (signal "x" 8)) |> ignore
        printfn "renormTo inventing fraction:  NOT detected — the renorm check is broken"
    with ex ->
        printfn $"renormTo refused:             {ex.Message}"

    try
        Number.ofBits Number.q4_28 (signal "x" 8) |> ignore
        printfn "ofBits on the wrong width:    NOT detected — the boundary check is broken"
    with ex ->
        printfn $"ofBits refused:               {ex.Message}"

    try
        Number.constant Number.q4_4 9.0 |> ignore
        printfn "constant out of range:        NOT detected — the fit check is broken"
    with ex ->
        printfn $"constant refused:             {ex.Message}"

    for m in
        [ add3
          dot2
          dot2Auto
          dot2Ambient
          dot2Inline
          pipelinedDot
          gatedCounter
          streamPipe
          coordPipe
          onCounter
          onPriority
          loopPipeline
          treeSum
          ramTest
          cmdProcessor
          unionRoundTrip
          forkJoin
          signedOps
          xorOps
          satOps
          escapeStep
          escapeStepFixed
          escapeStep28
          wideBeat
          widenOps
          dispatchRoundTrip
          clusteredRoundTrip
          twoStreamSplit
          twoStreamSplitReplicateJoin
          framePipeline
          sweepPipeline 2
          typedPipeline
          probedPipe
          axiWriteMaster
          axiWriteMasterSingle
          axiReadMaster
          axiReadMasterSingle
          axiReadMasterBurst
          axiPulse
          axiScratch
          neighborCount
          regMapScratch
          snapshotConflate
          snapshotDdr ] do
        match checkWidths m with
        | [] -> printfn $"{m.name}: widths ok"
        | problems -> problems |> List.iter (printfn "%s")

    // The telemetry workflow: a stalled ProbedPipe read by streamReport —
    // learning where a design stalls costs a tick and a peek, not a build.
    let reportSim = Sim(probedPipe)
    reportSim.Poke("in_valid", 1UL)
    reportSim.Poke("out_ready", 0UL)

    for _ in 1..10 do
        reportSim.Tick()

    for name, blocked, starved in streamReport reportSim.Peek probedPipe do
        printfn $"stream '{name}':             blocked %d{blocked} starved %d{starved}"

    // The master against the fake DDR: four distinctly-patterned 128-bit beats
    // land at their byte addresses — the path a full-frame render reads back.
    let axiSim = Sim(axiWriteMaster)
    let ddr = SimAxiWriteSlave(axiSim, 256)
    let mutable sent = 0

    for _ in 1..24 do
        if sent < 4 then
            axiSim.Poke("in_valid", 1UL)
            axiSim.Poke("in_addr", uint64 (sent * 16))
            axiSim.PokeWide("in_data", (BigInteger(0xA0 + sent) <<< 120) ||| BigInteger(0x10 + sent))
            axiSim.Poke("in_strb", 0xFFFFUL)
        else
            axiSim.Poke("in_valid", 0UL)

        let accepted = sent < 4 && axiSim.Peek "in_ready" = 1UL
        ddr.Cycle()
        if accepted then sent <- sent + 1

    let ddrOk =
        [ 0..3 ]
        |> List.forall (fun k -> ddr.Memory[k * 16] = byte (0x10 + k) && ddr.Memory[k * 16 + 15] = byte (0xA0 + k))

    printfn $"AXI master vs the DDR model:  %b{ddrOk}"

    // The neighborhood gather on a known checkerboard-ish grid — one value per
    // stencil/edge policy, computed by hand in the comment of each expectation.
    let neighborOk =
        let sim = Sim(neighborCount)
        let pattern = [ [ 1; 0; 1 ]; [ 0; 1; 0 ]; [ 1; 0; 1 ] ]

        for y in 0..2 do
            for x in 0..2 do
                sim.Poke($"g{y}{x}", uint64 (pattern |> List.item y |> List.item x))

        sim.Tick()

        sim.Peek "moore" = 4UL // center: the four corners
        && sim.Peek "corner" = 1UL // (0,0): only (1,1) lives
        && sim.Peek "vonNeumann" = 0UL // center: all four edges dead
        && sim.Peek "wrapped" = 4UL // (0,0) toroidal: (2,2),(2,0),(0,2),(1,1)
        && sim.Peek "clamped" = 4UL // (2,2) clamped: (1,1) + the center thrice

    printfn $"neighborhood policies:        %b{neighborOk}"

    // The declarative reg map, driven by real five-channel transactions: the
    // ID overlay, a pulse pair, packed ro fields against an rw threshold, the
    // w1c + irq path through a genuine 8-bit wrap, and a window word written
    // by the host and sync-read back out by the fabric.
    let regMapOk =
        let sim = Sim(regMapScratch)

        // The handshakes live in `SimAxi`, asserted step by step.
        let axi = SimAxi.client sim
        let read32, write32 = axi.read32, axi.write32

        let idOk = read32 0x000UL = 0xF5C0FFEEUL
        write32 0x004UL 3UL
        let thresholdOk = read32 0x004UL = 3UL
        write32 0x000UL 1UL // bump
        write32 0x000UL 1UL
        let lowOk = read32 0x008UL = 2UL // count 2, high clear
        write32 0x000UL 1UL
        write32 0x000UL 1UL
        let highOk = read32 0x008UL = 0x104UL // count 4, high set
        write32 0x040UL 0xABUL // pattern[0]
        write32 0x000UL 2UL // clear -> count 0, so patLow reads pattern[0]
        let windowOk = read32 0x010UL = 0xABUL

        for _ in 1..256 do
            write32 0x000UL 1UL // wrap the counter: 255 -> 0 sets wrapIrq

        let irqSet = read32 0x00CUL = 1UL && sim.Peek "irq" = 1UL
        write32 0x00CUL 1UL // w1c
        let irqClear = read32 0x00CUL = 0UL && sim.Peek "irq" = 0UL

        idOk && thresholdOk && lowOk && highOk && windowOk && irqSet && irqClear

    printfn $"declarative reg map:          %b{regMapOk}"

    // The arbitrated window readback: the host reads back what it wrote,
    // through the same single read port the design is using every cycle.
    //
    // Three claims. Every word comes back as written — including a second
    // write over the first, so the read is genuinely from the array and not a
    // shadow register of the last write. The design's own consumption of the
    // window survives the steal: patLow, derived from the design-side port,
    // still reads correctly immediately after a host readback. And the port
    // returns to the design when the host is idle — patLow tracks a changed
    // word without another host read of the window in between.
    let windowReadbackOk =
        let sim = Sim(regMapScratch)
        let axi = SimAxi.client sim
        let read32, write32 = axi.read32, axi.write32

        for i in 0..15 do
            write32 (0x040UL + uint64 (i * 4)) (0xA100UL + uint64 i)

        let allBack =
            [ 0..15 ] |> List.forall (fun i -> read32 (0x040UL + uint64 (i * 4)) = 0xA100UL + uint64 i)

        write32 0x044UL 0xBEEFUL // overwrite pattern[1]
        let overwritten = read32 0x044UL = 0xBEEFUL

        // count is 0, so the design side reads pattern[0]; its low byte lands
        // in patLow. Read patLow immediately after hammering the window with
        // readbacks — the steal must not have wedged the design's port.
        let designSide = read32 0x010UL = 0x00UL // pattern[0] = 0xA100, low byte 0x00
        write32 0x040UL 0xA1FFUL
        let designTracks = read32 0x010UL = 0xFFUL

        allBack && overwritten && designSide && designTracks

    printfn $"window readback (arbitrated): %b{windowReadbackOk}"

    // The mirror direction: a window the design writes and the host reads.
    // Five bumps leave five marked words; the host reads each back, plus one
    // address nothing wrote, which must be the array's zero and not a copy of
    // a neighbour. No arbitration exists on this path — the claim is that the
    // design's write port and the host's read port address the same words.
    let designWindowOk =
        let sim = Sim(regMapScratch)
        let axi = SimAxi.client sim
        let read32, write32 = axi.read32, axi.write32

        for _ in 1..5 do
            write32 0x000UL 1UL // bump: writes 0xC500 | count at trace[count]

        let marked =
            [ 0..4 ] |> List.forall (fun i -> read32 (0x080UL + uint64 (i * 4)) = 0xC500UL + uint64 i)

        let untouched = read32 (0x080UL + 10UL * 4UL) = 0UL
        marked && untouched

    printfn $"design-written window reads:  %b{designWindowOk}"

    // The snapshot path end to end: animated rows conflated into rotating DDR
    // slots through the write master, the host capturing over the fake DDR.
    // A frame is coherent iff its rows share one latch instant T (row i reads
    // i*16 + T), which is exactly what the one-cycle shadow latch promises.
    let snapshotOk =
        let sim = Sim(snapshotDdr)
        let ddr = SimAxiWriteSlave(sim, 64, dataBytes = 4)

        let captureFrame () =
            sim.Poke("snap_capture", 1UL)
            ddr.Cycle()
            sim.Poke("snap_capture", 0UL)
            let mutable guard = 0

            while sim.Peek "host_ready" = 0UL && guard < 300 do
                ddr.Cycle()
                guard <- guard + 1

            let slot = int (sim.Peek "host_slot")
            [ for i in 0..3 -> ddr.Memory[slot * 16 + i * 4] ]

        let coherentAt (rows: byte list) =
            let t = rows[0]

            if rows |> List.mapi (fun i v -> v = t + byte (i * 16)) |> List.forall id then
                Some t
            else
                None

        for _ in 1..40 do
            ddr.Cycle()

        let first = captureFrame ()
        sim.Poke("snap_capture", 1UL) // capture while holding: must count, not corrupt
        ddr.Cycle()
        sim.Poke("snap_capture", 0UL)
        let overrunCounted = sim.Peek "host_overrun" = 1UL
        sim.Poke("snap_release", 1UL)
        ddr.Cycle()
        sim.Poke("snap_release", 0UL)

        for _ in 1..25 do
            ddr.Cycle()

        let second = captureFrame ()
        let overrunCleared = sim.Peek "host_overrun" = 0UL

        match coherentAt first, coherentAt second with
        | Some t1, Some t2 -> t1 <> t2 && overrunCounted && overrunCleared
        | _ -> false

    printfn $"conflate snapshot via DDR:    %b{snapshotOk}"
    printfn $"inventory names all peek:     %b{inventoryNamesPeek ()}"
    printfn $"inventory groups by instance: %b{inventoryGroups ()}"
    printfn $"registry entries all load:    %b{registryLoads ()}"
    printfn $"breakpoint expressions:       %b{breakpointExpressions ()}"
    printfn $"FIRRTL export is closed:      %b{firrtlIsClosed ()}"
    printfn $"FIRRTL round-trips:           %b{firrtlRoundTrips ()}"
    printfn $"reg holds through reset:      %b{holdsThroughReset ()}"
    printfn $"dynamic shifts shift:         %b{dynamicShiftsShift ()}"
    printfn $"bit reductions reduce:        %b{reductionsReduce ()}"
    printfn $"constant division divides:    %b{divisionDivides ()}"
    printfn $"stream divider divides:       %b{dividerDivides ()}"
    printfn $"dividers ignore stalls:       %b{dividersAreStallIndependent ()}"
    printfn $"fifo buffers in order:        %b{fifoBuffers ()}"
    printfn $"fifo storage is invisible:    %b{fifoStorageIsInvisible ()}"
    printfn $"carried read pairs up:        %b{carriedReadPairsUp ()}"
    printfn $"windows answer their range:   %b{windowsAnswerTheirOwnRange ()}"
    printfn $"busy flag holds AR off:       %b{busyFlagHoldsArOff ()}"
    printfn $"pipelined channel pipelines:  %b{pipelinedChannelPipelines ()}"
    printfn $"strobe spares other lanes:    %b{strobeSparesUntouchedLanes ()}"
    printfn $"wide strobe spares lanes:     %b{strobeSparesWideLanes ()}"
    printfn $"context rides along:          %b{contextRidesAlong ()}"
    printfn $"farm carries context:         %b{farmCarriesContext ()}"
    printfn $"ram style rule holds:         %b{ramStyleRule ()}"
    printfn $"a literal borrows the sign:   %b{literalBorrowsTheSign ()}"
    printfn $"saturate keeps the reading:   %b{saturateKeepsTheReading ()}"
    printfn $"debug session drives a run:   %b{debugSessionDrives ()}"
    printfn $"debug session windows a mem:  %b{debugSessionShowsMemory ()}"
    printfn $"trace records every cycle:    %b{tracesEveryCycle ()}"
    printfn $"assertions hold and can fail: %b{assertionsHold ()}"
    printfn $"state machines, four claims:  %b{stateMachines ()}"
    printfn $"utility primitives:           %b{utilityPrimitives ()}"
    printfn $"flatten refuses collisions:   %b{flattenRefusesNameCollisions ()}"

    // The design-space sweep: the same pipeline at each worker count, driven
    // flat out, judged by throughput and the probes. This is the whole
    // methodology at toy scale — the optimum is read off the table, not
    // guessed: beats stop rising where the expander becomes the wall.
    printfn ""

    for nWorkers in [ 1; 2; 4; 8 ] do
        let d = sweepPipeline nWorkers
        let sim = Sim(d)
        sim.Poke("cmd_valid", 1UL)
        sim.Poke("cmd_data", 1UL)

        for _ in 1..400 do
            sim.Tick()

        let stalls =
            streamReport sim.Peek d
            |> List.map (fun (name, blocked, starved) -> $"{name} blocked %4d{blocked} starved %4d{starved}")
            |> String.concat "   "

        let beats = sim.Peek "beat_count"
        printfn $"workers %d{nWorkers}: beats %3d{beats}   {stalls}"

    0

[<EntryPoint>]
let main argv =
    match argv with
    | [| "wav"; inPath; outPath |] ->
        // Real audio through the elaborated design, end to end. The same
        // simulator the checks use, driven by a file instead of a poke loop —
        // which is the point of the harness: the thing you can listen to is
        // produced by the thing that becomes the bitstream.
        let input = readWavFile inPath
        printfn $"in:  {input.FrameCount} frames, {input.sampleRate} Hz, {input.channels} ch"

        let sim = Sim(multibandStage)
        sim.Poke("threshold", 200_000UL)
        sim.Poke("ratio", 4UL)
        sim.Poke("attack", 1UL <<< 14)
        sim.Poke("releaseRate", 1UL <<< 12)

        for i in 0 .. multibandBands - 1 do
            sim.Poke($"lg{i}", gainUnity)
            sim.Poke($"rg{i}", gainUnity)

        let output = runWavThroughSim sim defaultWavPorts 64 input
        writeWavFile outPath output
        let inL, inR = peaks input
        let outL, outR = peaks output
        printfn $"out: {output.FrameCount} frames, peaks {inL}/{inR} -> {outL}/{outR}"
        printfn $"wrote {outPath}"
        0
    | [| "diff"; outDir |] ->
        writeDiff (diffDesigns ()) outDir
        0
    // FIRRTL nobody here wrote, read by our reader and simulated. The testbench
    // asserts our Sim's trace; the runner then verilates *firtool's* Verilog
    // from the same source text against it. That is the only arrangement where
    // a construct we misread cannot pass — a round trip against our own emitter
    // would agree with itself.
    | [| "firrtl-foreign"; inDir; outDir |] ->
        System.IO.Directory.CreateDirectory outDir |> ignore

        let files = System.IO.Directory.GetFiles(inDir, "*.fir") |> Array.sort

        for file in files do
            let design = FirrtlImport.importFirrtl (System.IO.File.ReadAllText file)
            // The elaboration checks gate this the way they gate everything.
            emitDesign design |> ignore
            let tb = Warp11.Diff.diffTb design 50
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"{design.name}_diff_tb.v"), tb + "\n")
            System.IO.File.Copy(file, System.IO.Path.Combine(outDir, $"{design.name}.fir"), true)
            printfn $"  read {System.IO.Path.GetFileName file} as {design.name}"

        printfn $"wrote %d{Array.length files} foreign testbenches to {outDir}"
        0
    // Simulate a `.fir` — the standalone-tool path, at its smallest. Low FIRRTL
    // only; `hdl/README.md` says how to lower a high-FIRRTL design into that
    // subset first.
    | [| "firrtl-sim"; file; cycles |]
    | [| "firrtl-sim"; file; cycles; _ |] ->
        let design = FirrtlImport.importFirrtl (System.IO.File.ReadAllText file)
        let n = int cycles
        let sim = Sim design
        let inventory = Inventory.ofDesign design

        printfn $"{design.name}: %d{List.length inventory.signals} signals, %d{List.length inventory.mems} memories"

        // Seeded, so a run is repeatable and two people see the same waveform.
        let rand = System.Random 12345

        let flat = Flatten.flatten design

        let inputs =
            [ for d in flat.decls do
                  match d with
                  | Input (portName, t) -> yield portName, t.Width
                  | _ -> () ]

        let recorded =
            [ for s in inventory.signals ->
                s.name, s.width, ResizeArray<BigInteger>() ]

        for _ in 1..n do
            for name, w in inputs do
                if w <= 64 then
                    let mask = if w >= 64 then System.UInt64.MaxValue else (1UL <<< w) - 1UL
                    sim.Poke(name, uint64 (rand.NextInt64()) &&& mask)

            sim.Tick()

            for name, w, values in recorded do
                values.Add(if w > 64 then sim.PeekWide name else BigInteger(sim.Peek name))

        let trace: Debug.Trace =
            { firstCycle = 0
              signals =
                [ for name, w, values in recorded ->
                    { name = name
                      width = w
                      values = [| for v in values -> if w > 64 then 0UL else uint64 v |]
                      wideValues = if w > 64 then Array.ofSeq values else [||] } ] }

        let vcdPath =
            match argv with
            | [| _; _; _; out |] -> out
            | _ -> System.IO.Path.ChangeExtension(file, ".vcd")

        System.IO.File.WriteAllText(vcdPath, Vcd.render design.name trace)
        printfn $"ran %d{n} cycles under seeded stimulus; wrote {vcdPath}"
        0
    | _ -> mainDemo ()
