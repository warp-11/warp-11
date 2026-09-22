/// The executable: the Verilog demo dump with its living checks and the
/// differential-oracle writer, dispatched from main.
module Warp11.Designs.Main

open System.Numerics
open Warp11

let private diffDesignsAtDefault () =
    [ comparator8.def
      holdThroughReset.def
      dynamicShifts.def
      bitReductions.def
      constantDivision.def
      streamDivider.def
      maskedWrite.def
      maskedWriteWide.def
      pipelinedReadSlave.def
      deepChannelSlave.def
      twoWindowSlave.def
      carriedRead.def
      bufferedStream.def
      deepBufferedStream.def
      taggedDivide.def
      farmedDivide.def
      add3
      dot2
      dot2Auto
      dot2Ambient.def
      dot2Inline.def
      pipelinedDot.def
      gatedCounter.def
      streamPipe.def
      coordPipe.def
      boundaryWalk.def
      onCounter.def
      onPriority.def
      ifElseLadder.def
      switchRing.def
      sequencer.def
      lfsrSource.def
      oneHotScan.def
      mux1HSelect.def
      edgeDetector.def
      flowSampler.def
      dividers.def
      bitShapes.def
      loopPipeline.def
      treeSum.def
      ramTest.def
      dualRead.def
      fillingMemory.def
      priorityWrite.def
      maskedWritePriority.def
      romLookup.def
      blockRomLookup.def
      sumOverLut.def
      sumOverBlock.def
      sumOverUltra.def
      sumOverDdr.def
      oneOwnerOnePort.def
      twoOwnersOnePort.def
      twoOwnersTwoPorts.def
      sumFromReadWindow.def
      sumReportsDone.def
      sumWhollyOnChip.def
      assertedSaturate.def
      cmdProcessor.def
      unionRoundTrip.def
      forkJoin.def
      signedOps.def
      xorOps.def
      satOps.def
      escapeStep.def
      escapeStepFixed.def
      escapeStep28.def
      wideBeat.def
      widenOps.def
      dispatchRoundTrip.def
      clusteredRoundTrip.def
      twoStreamSplit.def
      twoStreamSplitReplicateJoin.def
      framePipeline.def
      (sweepPipeline 2).def
      windowSweep.def
      pixelBlur.def
      movingAverage.def
      sparseDot.def
      indirectGather.def
      indexSweep.def
      maxPoolDilate.def
      maxPool2x2.def
      maxPoolCombinational.def
      typedPipeline.def
      probedPipe.def
      axiWriteMaster.def
      axiWriteMasterSingle.def
      axiReadMaster.def
      axiReadMasterSingle.def
      axiReadMasterBurst.def
      axiPulse.def
      axiScratch.def
      neighborCount.def
      regMapScratch.def
      serialRegMap.def
      snapshotConflate.def
      snapshotDdr.def
      audioOps.def
      audioChain.def
      audioTone.def
      i2sLoopback.def
      i2sLinkPassthru.def
      i2sLinkCodec.def
      i2sLinkTone.def
      i2sLinkHalfVolume.def
      selectLadder.def
      multibandStage.def
      delayTap.def
      audioEchoStage.def ]

/// Each design with the length of testbench it needs — the default for all but
/// the one whose unit of work is a pass rather than a beat.
let private diffDesigns () =
    [ for d in diffDesignsAtDefault () -> d, diffCycles ]
    // Three stereo frames through the folded engine — a pass is ~170 cycles
    // plus the handshake — under stimulus that offers and takes at random.
    @ [ multibandStageFolded.def, 4 * 200
        // A UART frame is ten bits of 24 cycles; a few of them under random
        // pokes on `rx` is the receiver seeing every state.
        serialRegMap.def, 2_000 ]

/// `streamWindow`/`lineWindow`'s defining property, driven rather than trusted: across two
/// frames of distinct rows, the emitted windows are exactly rows r−1, r, r+1
/// widened under `Edge.Wrap` — which also proves the prologue emits nothing,
/// the counter re-arms between frames, and a consumer that stalls on a
/// pattern loses no window and duplicates none.
let private lineWindowProperty () =
    let rows = 4
    let p = 8

    // `widen` under Edge.Wrap, in software: bit 0 is column −1 (= bit P−1),
    // the top bit is column P (= bit 0).
    let widen (row: uint64) =
        ((row &&& 1UL) <<< (p + 1)) ||| (row <<< 1) ||| ((row >>> (p - 1)) &&& 1UL)

    let frame1 = [ 0xA3UL; 0x01UL; 0x52UL; 0x9CUL; 0x7FUL; 0xE8UL ]
    let frame2 = [ 0x11UL; 0xB6UL; 0x40UL; 0x05UL; 0xDDUL; 0x66UL ]

    let expected =
        [ for beats in [ frame1; frame2 ] do
              for r in 1..rows -> widen beats[r - 1], widen beats[r], widen beats[r + 1] ]

    let sim = Sim windowSweep.def
    let got = ResizeArray()
    let mutable cycle = 0

    for beats in [ frame1; frame2 ] do
        for v in beats do
            sim.Poke("in_row", v)
            sim.Poke("in_valid", 1UL)
            let mutable accepted = false

            while not accepted do
                // The consumer stalls every third cycle, so primed-and-held
                // is exercised, not just the streaming fast path.
                let ready = cycle % 3 <> 0
                sim.Poke("win_ready", (if ready then 1UL else 0UL))

                if sim.Peek "win_valid" = 1UL && ready then
                    got.Add(sim.Peek "win_row0", sim.Peek "win_row1", sim.Peek "win_row2")

                accepted <- sim.Peek "in_ready" = 1UL
                sim.Tick()
                cycle <- cycle + 1

        sim.Poke("in_valid", 0UL)

        // A gap between frames: nothing may be offered while no beat is in.
        for _ in 1..3 do
            sim.Poke("win_ready", 1UL)

            if sim.Peek "win_valid" = 1UL then
                got.Add(0UL, 0UL, 0UL)

            sim.Tick()

    List.ofSeq got = expected

/// A streaming design driven flat out — every beat offered, every output
/// taken — collecting `wanted` outputs of the named port. The simple half of
/// stream driving; `lineWindowProperty` covers the stalling half.
let private streamRun (d: ModuleDef) (inPort: string) (outPort: string) (beats: uint64 list) (wanted: int) =
    let inValid = $"{inPort.Split('_').[0]}_valid"
    let outPrefix = outPort.Split('_').[0]
    let sim = Sim d
    sim.Poke($"{outPrefix}_ready", 1UL)
    let got = ResizeArray()

    for v in beats do
        sim.Poke(inValid, 1UL)
        sim.Poke(inPort, v)

        if sim.Peek $"{outPrefix}_valid" = 1UL then
            got.Add(sim.Peek outPort)

        sim.Tick()

    sim.Poke(inValid, 0UL)

    for _ in 1..8 do
        if sim.Peek $"{outPrefix}_valid" = 1UL then
            got.Add(sim.Peek outPort)

        sim.Tick()

    got |> Seq.truncate wanted |> List.ofSeq

/// `pixelBlur` against a software model of the same 3×3 mean over clamped
/// borders — multi-bit cells through the same window the 1-bit check walks.
let private pixelBlurAgrees () =
    let pixels = 4
    let rows = [| [| 10; 200; 30; 90 |]; [| 0; 50; 255; 20 |]; [| 70; 80; 90; 100 |]; [| 5; 15; 25; 35 |] |]
    // The loader's vertical halo, Clamp flavoured: first and last rows repeated.
    let beats = Array.concat [ [| rows[0] |]; rows; [| rows[rows.Length - 1] |] ]

    let packRow (r: int[]) =
        r |> Array.mapi (fun i v -> uint64 v <<< (i * 8)) |> Array.sum

    let expected =
        [ for r in 0 .. rows.Length - 1 ->
              packRow
                  [| for c in 0 .. pixels - 1 ->
                         let mutable sum = 0

                         for dr in 0..2 do
                             for dc in -1..1 do
                                 let cc = max 0 (min (pixels - 1) (c + dc))
                                 sum <- sum + beats[r + dr][cc]

                         (sum >>> 3) &&& 0xFF |] ]

    streamRun pixelBlur.def "in_row" "out_row" (beats |> Array.map packRow |> List.ofArray) rows.Length = expected

/// `movingAverage` against the arithmetic: unframed, so the window primes
/// once and every input from the fourth onward produces one output.
let private movingAverageAgrees () =
    let samples = [ 100UL; 200UL; 300UL; 400UL; 60000UL; 8UL; 12UL; 500UL ]

    let expected =
        samples
        |> List.windowed 4
        |> List.map (fun w -> (List.sum w) / 4UL)

    streamRun movingAverage.def "in_sample" "out_sample" samples expected.Length = expected

/// `sparseDot`: fill the activation vector, then the dot is simply there —
/// the gather is wiring, which is the claim the design exists to make.
let private sparseDotAgrees () =
    let activations = [| 9UL; 14UL; 3UL; 200UL; 77UL; 1UL; 130UL; 42UL |]
    let sim = Sim sparseDot.def

    activations
    |> Array.iteri (fun i v ->
        sim.Poke("fill_addr", uint64 i)
        sim.Poke("fill_data", v)
        sim.Poke("fill_enable", 1UL)
        sim.Tick())

    sim.Poke("fill_enable", 0UL)
    sim.Tick()

    let expected = 3UL * activations[1] + 1UL * activations[3] + 2UL * activations[4] + 5UL * activations[6]
    sim.Peek "dot" = expected

/// `indirectGather`: B[A[i]] for a handful of indices — each answer costs the
/// two-hop walk, which is dynamic-irregular access priced honestly.
let private indirectGatherAgrees () =
    let table = [| 5UL; 2UL; 7UL; 0UL; 3UL; 6UL; 1UL; 4UL |]
    let values = [| 11UL; 22UL; 33UL; 44UL; 55UL; 66UL; 77UL; 88UL |]
    let sim = Sim indirectGather.def

    for i in 0..7 do
        sim.Poke("fill_t_addr", uint64 i)
        sim.Poke("fill_t_data", table[i])
        sim.Poke("fill_v_addr", uint64 i)
        sim.Poke("fill_v_data", values[i])
        sim.Poke("fill_t_enable", 1UL)
        sim.Poke("fill_v_enable", 1UL)
        sim.Tick()

    sim.Poke("fill_t_enable", 0UL)
    sim.Poke("fill_v_enable", 0UL)

    let indices = [ 2UL; 0UL; 6UL; 5UL ]
    sim.Poke("out_ready", 1UL)
    let got = ResizeArray()

    for i in indices do
        sim.Poke("in_index", i)
        sim.Poke("in_valid", 1UL)
        let mutable accepted = false

        while not accepted do
            accepted <- sim.Peek "in_ready" = 1UL

            if sim.Peek "out_valid" = 1UL then
                got.Add(sim.Peek "out_value")

            sim.Tick()

    sim.Poke("in_valid", 0UL)

    for _ in 1..8 do
        if sim.Peek "out_valid" = 1UL then
            got.Add(sim.Peek "out_value")

        sim.Tick()

    List.ofSeq got = [ for i in indices -> values[int table[int i]] ]

/// `rangeStream`'s claim: exactly 0…n−1 per pulse, in order, nothing between
/// pulses — held under a consumer that stalls on a pattern, and repeated,
/// since a scan that runs once is not a schedule.
let private rangeStreamProperty () =
    let sim = Sim indexSweep.def
    let got = ResizeArray()
    let mutable cycle = 0

    let run () =
        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)

        for _ in 1..24 do
            let ready = cycle % 3 <> 2
            sim.Poke("out_ready", (if ready then 1UL else 0UL))

            if sim.Peek "out_valid" = 1UL && ready then
                got.Add(sim.Peek "out_index")

            sim.Tick()
            cycle <- cycle + 1

    run ()
    run ()
    List.ofSeq got = [ 0UL; 1UL; 2UL; 3UL; 4UL; 0UL; 1UL; 2UL; 3UL; 4UL ]

/// `maxPoolDilate` against a software model of iterated 5×5 max over a
/// zero-padded 8×8 byte map — `bandedWorld`'s second user, driven through the
/// same free-running contract as the Game of Life sweep: fill, hold `run`
/// until the counter reaches the target, let any pass in flight finish, and
/// step the model however many passes the counter says completed.
let private maxPoolAgrees () =
    let mapRows = 8
    let columns = 8

    let byteOf (world: uint64[]) y x =
        if y < 0 || y >= mapRows || x < 0 || x >= columns then
            0UL
        else
            (world[y] >>> (x * 8)) &&& 0xFFUL

    let dilate (world: uint64[]) =
        [| for y in 0 .. mapRows - 1 ->
               let mutable row = 0UL

               for x in 0 .. columns - 1 do
                   let mutable best = 0UL

                   for dy in -2 .. 2 do
                       for dx in -2 .. 2 do
                           best <- max best (byteOf world (y + dy) (x + dx))

                   row <- row ||| (best <<< (x * 8))

               row |]

    let rand = System.Random 29
    let start = [| for _ in 1..mapRows -> uint64 (rand.Next()) <<< 32 ||| uint64 (rand.Next()) |]

    let sim = Sim maxPoolDilate.def

    for y in 0 .. mapRows - 1 do
        sim.Poke("fill_addr", uint64 y)
        sim.Poke("fill_data", start[y])
        sim.Poke("fill_enable", 1UL)
        sim.Tick()

    sim.Poke("fill_enable", 0UL)
    sim.Poke("run", 1UL)
    let mutable cycles = 0

    while sim.Peek "generation" < 2UL && cycles < 2000 do
        sim.Tick()
        cycles <- cycles + 1

    sim.Poke("run", 0UL)

    for _ in 1..48 do
        sim.Tick()

    let completed = int (sim.Peek "generation")

    let expected =
        [ 1..completed ]
        |> List.fold (fun world _ -> dilate world) start

    let got =
        [| for y in 0 .. mapRows - 1 ->
               sim.Poke("probe_addr", uint64 y)
               sim.Tick()
               sim.Peek "probe_data" |]

    completed >= 2 && List.ofArray got = List.ofArray expected

/// `maxPool2x2` against the arithmetic: one pass, an 8×8 byte map in, its
/// 2×2-stride-2 max in the 4×4 output — the CNN layer shape, exercising
/// `streamStride`'s claim (the even-indexed windows and only those) and the
/// shape-changing write side.
let private maxPool2x2Agrees () =
    let mapRows = 24
    let columns = 24
    let rand = System.Random 31
    let cells = Array.init mapRows (fun _ -> Array.init columns (fun _ -> rand.Next 256))

    let packRow (row: int[]) =
        row
        |> Array.mapi (fun x v -> System.Numerics.BigInteger v <<< (x * 8))
        |> Array.sum

    let expected =
        [ for y in 0 .. mapRows / 2 - 1 ->
              packRow
                  [| for j in 0 .. columns / 2 - 1 ->
                         [ for dy in 0..1 do
                               for dx in 0..1 -> cells[2 * y + dy][2 * j + dx] ]
                         |> List.max |] ]

    let sim = Sim maxPool2x2.def

    for y in 0 .. mapRows - 1 do
        sim.Poke("fill_addr", uint64 y)
        sim.PokeWide("fill_data", packRow cells[y])
        sim.Poke("fill_enable", 1UL)
        sim.Tick()

    sim.Poke("fill_enable", 0UL)
    sim.Poke("start", 1UL)
    sim.Tick()
    sim.Poke("start", 0UL)
    let mutable cycles = 0

    while sim.Peek "complete" = 0UL && cycles < 400 do
        sim.Tick()
        cycles <- cycles + 1

    let got =
        [ for y in 0 .. mapRows / 2 - 1 ->
              sim.Poke("probe_addr", uint64 y)
              sim.Tick()
              sim.PeekWide "probe_data" ]

    sim.Peek "complete" = 1UL && got = expected

/// `maxPoolCombinational` against the arithmetic, several random maps: pure
/// combinational, so pokes answer without a tick — which is itself part of
/// the claim being checked.
let private maxPoolCombinationalAgrees () =
    let rand = System.Random 37
    let sim = Sim maxPoolCombinational.def

    [ 1..4 ]
    |> List.forall (fun _ ->
        let cells = [| for _ in 0..63 -> uint64 (rand.Next 256) |]

        for y in 0..7 do
            for x in 0..7 do
                sim.Poke($"in_%d{y}_%d{x}", cells[y * 8 + x])

        [ for y in 0..3 do
              for x in 0..3 ->
                  let expected =
                      [ for dy in 0..1 do
                            for dx in 0..1 -> cells[(2 * y + dy) * 8 + (2 * x + dx)] ]
                      |> List.max

                  sim.Peek $"out_%d{y}_%d{x}" = expected ]
        |> List.forall id)

/// What `Machine.Switch` is for, in three claims.
///
/// The one that matters is arithmetic rather than behavioural, which is unusual
/// for a check here: `Switch` and a block of sibling `st.If` compute the same
/// function, and the difference is how much silicon they ask for. Sibling `If`s
/// merge last-connect-wins, so each one's fall-through arm is the whole
/// expression built so far; a state that transitions conditionally names that
/// fall-through twice, once in the inner `If` and once in the outer, and with no
/// sharing in `Expr` the doubling compounds to **2^n comparators for n states**.
/// So the claim is a count, not a waveform — a behavioural test passes either
/// way, which is exactly why this went unnoticed until something hit 740 KB on
/// one line.
let private switchIsLinear () =
    // The same six-state machine as `switchRing`, written the way every machine
    // in this tree is written today. Same module name, so the two are
    // comparable line for line.
    let handWritten =
        defModule
            "SwitchRing"
            (fun p -> (p.inPort "go" 1, p.inPort "halt" 1, p.outPort "phase" 3, p.outPort "ticks" 8))
            (fun (go, halt, phase, ticks) ->
            let st = machine "stage" [ Load; Warm; Run; Drain; Flush; Park ]
            let count = reg "count" 8

            st.Value ==> phase
            count ==> ticks

            st.If Load (fun () -> If go (fun () -> st.Goto Warm))
            st.If Warm (fun () -> st.Goto Run)

            st.If Run (fun () ->
                count + lit 1UL 8 ==> count
                If halt (fun () -> st.Goto Drain))

            st.If Drain (fun () -> If (eq count (lit 4UL 8)) (fun () -> st.Goto Flush))
            st.If Flush (fun () -> st.Goto Park)

            st.If Park (fun () ->
                If go (fun () ->
                    lit 0UL 8 ==> count
                    st.Goto Load)))

    // 1. Both forms compute the same machine, over every combination of the two
    //    inputs for long enough to go round the ring several times. This is the
    //    claim that passes either way — it is here to establish that what
    //    follows is a cost difference and not a behavioural one.
    let sameBehaviour =
        let a, b = Sim switchRing.def, Sim handWritten.def

        [ for cycle in 0..199 do
              let go = uint64 ((cycle / 3) % 2)
              let halt = uint64 ((cycle / 7) % 2)

              for sim in [ a; b ] do
                  sim.Poke("go", go)
                  sim.Poke("halt", halt)
                  sim.Tick()

              yield a.Peek "phase" = b.Peek "phase" && a.Peek "ticks" = b.Peek "ticks" ]
        |> List.forall id

    // 2. And the cost is not the same, in a way one size cannot show. A ring of
    //    the first n phases, every arm transitioning conditionally — the shape
    //    that makes the forms diverge — built both ways at three sizes, because
    //    the claim is about growth.
    let ring n useSwitch =
        defModule
            "Ring"
            (fun p -> (p.inPort "go" 1, p.outPort "out" (bitsToHold n)))
            (fun (go, out) ->
                let states = List.take n allPhases
                let st = machine "stage" states
                st.Value ==> out
                let arm i = fun () -> If go (fun () -> st.Goto states[(i + 1) % n])

                if useSwitch then
                    st.Switch [ for i in 0 .. n - 1 -> states[i], arm i ]
                else
                    for i in 0 .. n - 1 do
                        st.If states[i] (arm i))

    let comparators (d: TypedModule<_>) =
        System.Text.RegularExpressions.Regex.Matches(emitDesign d.def, "stage == ").Count

    // One comparator per arm out of `Switch`, at every size: the ladder names
    // each state's test once and shares one fall-through.
    let linear = [ 4; 6; 8 ] |> List.forall (fun n -> comparators (ring n true) = n)

    // Exactly 2^n - 1 out of the sibling form, because each `If`'s fall-through
    // is the whole expression so far and the nested `If` inside the arm names it
    // a second time. Eight states is 255 comparators against eight.
    let siblingFormIsExponential =
        [ 4; 6; 8 ] |> List.forall (fun n -> comparators (ring n false) = (1 <<< n) - 1)

    // 3. A state named twice is refused — only the first arm could ever run, so
    //    the second is silicon nothing reaches.
    let refusesDuplicateArm =
        try
            defModule "Dup" (fun p -> p.outPort "phase" 3) (fun out ->
                let st = machine "stage" [ Load; Warm ]
                st.Value ==> out

                st.Switch
                    [ Load, fun () -> st.Goto Warm
                      Load, fun () -> st.Goto Load ])
            |> ignore

            false
        with e ->
            e.Message.Contains "one state, one arm"

    sameBehaviour && linear && siblingFormIsExponential && refusesDuplicateArm

/// What `ifElse` is for, in four claims.
///
/// The first is the one no golden vector can defend. `IfElseLadder`'s
/// conditions nest rather than partition — every x below 4 satisfies all three
/// — so a ladder folded the other way round emits a mux tree that reads
/// perfectly well and is wrong for every input under 16. Disjoint conditions
/// could not tell the two apart. So the ordering is walked over the whole input
/// domain rather than sampled.
///
/// The rest are the contract around it: a target the winning arm skips holds
/// what it had, the ladder emits what the hand-nested form emits character for
/// character, and the two degenerate ladders mean what the shapes they replace
/// mean.
let private ifElseLadders () =
    // 1. First match wins, and 2. a target the winning arm does not drive falls
    //    through. Both come off the same walk because both are properties of
    //    the same fold: `held` is driven by arms one and three, so band two and
    //    the else arm are the cases that have to fall back to the unconditional
    //    default rather than to the arm above them.
    let walked =
        let sim = Sim ifElseLadder.def

        [ for x in 0UL..255UL do
              sim.Poke("x", x)
              sim.Tick()

              let band, held =
                  if x < 4UL then 1UL, 11UL
                  elif x < 8UL then 2UL, 0UL
                  elif x < 16UL then 3UL, 33UL
                  else 4UL, 0UL

              yield sim.Peek "band" = band && sim.Peek "held" = held ]
        |> List.forall id

    // 3. The ladder *is* the nested form rather than a second implementation of
    //    it. Same module name, so a difference in the emitted text is a
    //    difference in the logic. This is what made rewriting 108 call sites as
    //    `ifElse` a safe edit; keeping it as a check is what keeps it safe as
    //    the fold changes underneath.
    let handNested =
        moduleDef "IfElseLadder" (fun m ->
            let x = m.Input("x", 8)
            let band = m.Output("band", 8)
            let held = m.Output("held", 8)

            lit 0UL 8 ==> band
            lit 0UL 8 ==> held

            let innermost () =
                m
                    .If(
                        lt x (lit 16UL 8),
                        fun () ->
                            lit 3UL 8 ==> band
                            lit 33UL 8 ==> held
                    )
                    .Else(fun () -> lit 4UL 8 ==> band)

            let middle () =
                m.If(lt x (lit 8UL 8), fun () -> lit 2UL 8 ==> band).Else(innermost)

            m
                .If(
                    lt x (lit 4UL 8),
                    fun () ->
                        lit 1UL 8 ==> band
                        lit 11UL 8 ==> held
                )
                .Else(middle))

    let sameVerilog = emitDesign ifElseLadder.def = emitDesign handNested

    // 4. The ladder's edges. `otherwise` is a condition rather than a limb of
    //    the syntax, so the shapes around it are what a fold gets wrong: an
    //    absent else, an else that is the only arm, an empty ladder, and an
    //    `otherwise` that something was written below.
    let refuses (body: Builder -> unit) =
        try
            moduleDef "Refused" body |> ignore
            false
        with _ ->
            true

    // No `otherwise` is the else-less ladder — and it emits what an else-less
    // `If` emits, which is what retired the empty function this used to need.
    let elseLess =
        let viaLadder =
            defModule "Degenerate" (fun p -> (p.inPort "c" 1, p.outPort "out" 8)) (fun (c, out) ->
                lit 0UL 8 ==> out
                ifElse [ (c, fun () -> lit 5UL 8 ==> out) ])

        let written =
            defModule "Degenerate" (fun p -> (p.inPort "c" 1, p.outPort "out" 8)) (fun (c, out) ->
                lit 0UL 8 ==> out
                If c (fun () -> lit 5UL 8 ==> out))

        emitDesign viaLadder.def = emitDesign written.def

    // An `otherwise` alone is its body, run where the ladder stood — no mux on
    // a constant selector, which is what emitting the arm would have left.
    let otherwiseOnly =
        let viaLadder =
            defModule "Degenerate" (fun p -> p.outPort "out" 8) (fun out ->
                ifElse [ (otherwise, fun () -> lit 7UL 8 ==> out) ])

        let written =
            defModule "Degenerate" (fun p -> p.outPort "out" 8) (fun out -> lit 7UL 8 ==> out)

        emitDesign viaLadder.def = emitDesign written.def

    // An empty ladder drives nothing, which is a bug rather than an identity.
    let refusesEmpty = refuses (fun _ -> ifElse [])

    // And an arm below `otherwise` can never run, so it is refused rather than
    // elaborated into silicon nothing reaches.
    let refusesUnreachableArm =
        refuses (fun m ->
            let c = m.Input("c", 1)
            let out = m.Output("out", 8)
            lit 0UL 8 ==> out

            ifElse [ (otherwise, fun () -> lit 1UL 8 ==> out)
                     (c, fun () -> lit 2UL 8 ==> out) ])

    walked
    && sameVerilog
    && elseLess
    && otherwiseOnly
    && refusesEmpty
    && refusesUnreachableArm

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
        defModule
            "Sequencer"
            (fun p ->
                (p.inPort "start" 1,
                 p.inPort "stall" 1,
                 p.outPort "busy" 1,
                 p.outPort "finished" 1,
                 p.outPort "retired" 8))
            (fun (start, stall, busy, finished, retired) ->
            let sIdle, sFetch, sDecode, sExecute, sWriteback, sDone = 0UL, 1UL, 2UL, 3UL, 4UL, 5UL
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

            ifElse
                [ (inState sIdle, begin')
                  (inState sDone, begin')
                  (inState sFetch, fun () -> lit sDecode 3 ==> stage)
                  (inState sDecode, fun () -> lit sExecute 3 ==> stage)
                  (inState sExecute, fun () -> If (bnot stall) (fun () -> lit sWriteback 3 ==> stage))
                  (inState sWriteback,
                   fun () ->
                       count + lit 1UL 8 ==> count

                       ifElse [
                           (eq count (lit 3UL 8), fun () -> lit sDone 3 ==> stage)
                           (otherwise, fun () -> lit sFetch 3 ==> stage) ]) ])

    let sameVerilog = emitDesign sequencer.def = emitDesign handEncoded.def

    // The decode reaches the debugger under the flattened name the Sim peeks by.
    let decoded =
        match (Inventory.ofDesign sequencer.def).stateMachines.TryFind "stage" with
        | Some states ->
            states
            |> Map.toList
            |> (=) [ 0UL, "Idle"; 1UL, "Fetch"; 2UL, "Decode"; 3UL, "Execute"; 4UL, "Writeback"; 5UL, "Done" ]
        | None -> false

    // And it means what it says: the state the design is in is the state the
    // decode names, held at EXECUTE for as long as the stall lasts.
    let walked =
        let sim = Sim sequencer.def
        let stateNow () = (Inventory.ofDesign sequencer.def).stateMachines["stage"] |> Map.find (sim.Peek "stage")
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
            defModule "Unreachable" (fun p -> p.inPort "go" 1) (fun go ->
                let m = machine "st" [ First; Second; Never ]
                m.If First (fun () -> If go (fun () -> m.Goto Second))
                m.If Second (fun () -> m.Goto First)))
        |> Option.exists (fun message -> message.Contains "can never reach Never")

    // A state the machine was not given is not a state, however well it types.
    let unknownRefused =
        refuses (fun () ->
            moduleDef "Unknown" (fun _ ->
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

/// `regNoReset`'s defining property, which is about *reset* and nothing else:
/// the two registers take the same value from the same input on the same edge,
/// and diverge only when reset is asserted.
///
/// Checked against the Sim rather than by reading the Verilog, because the
/// Verilog is the easy half — a missing line in the reset branch is visible on
/// inspection, but that the simulator agrees is what the differential then
/// carries onto silicon.
let private holdsThroughReset () =
    let sim = Sim holdThroughReset.def

    sim.Poke("value", 42UL)
    sim.Tick()

    let bothTook = sim.Peek "held_out" = 42UL && sim.Peek "cleared_out" = 42UL

    sim.Reset()

    // The whole difference, in one line each.
    let heldSurvived = sim.Peek "held_out" = 42UL
    let clearedWentBack = sim.Peek "cleared_out" = 3UL

    // And the emission says the same thing: one register is named in the reset
    // branch and the other is not.
    let verilog = emitDesign holdThroughReset.def
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
    let sim = Sim dynamicShifts.def

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
    let sim = Sim bitReductions.def

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
    let sim = Sim constantDivision.def

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
    let sim = Sim maskedWrite.def
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
    let sim = Sim maskedWriteWide.def
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

/// A memory's write sites fold, and **the last one in the source wins**.
///
/// The structural half is checked first and is the reason the rule exists: the
/// emitted Verilog holds exactly one `store[...] <=` however many places wrote,
/// which is what keeps a synthesiser inferring a block RAM. A design with three
/// visible write sites that emitted three would still simulate correctly here
/// and fail on silicon, so the count is asserted rather than assumed.
///
/// The behavioural half drives all three enables independently, so every
/// combination arrives — including all three at once, the case a fold reducing
/// from the wrong end would get exactly backwards, and the case a design that
/// merged them by hand would have had to reason about.
let private lastWriteSiteWins () =
    let onlyOneWriteSite =
        let verilog = emitDesign priorityWrite.def
        let sites = verilog.Split("store[").Length - 1
        // One in the declaration's read path and one in the always block would
        // be two; the read is `memRead`, which emits `store[raddr]` — so the
        // write site is the other one.
        sites = 2

    let sim = Sim priorityWrite.def
    let rng = System.Random 3131
    let model = Array.zeroCreate<uint64> 8

    let mutable ok = true
    let mutable allThree = 0

    for _ in 1..4000 do
        let addr = rng.Next 8
        let raddr = rng.Next 8
        let low = rng.Next 2 = 1
        let mid = rng.Next 2 = 1
        let high = rng.Next 2 = 1

        sim.Poke("addr", uint64 addr)
        sim.Poke("raddr", uint64 raddr)
        sim.Poke("low_enable", (if low then 1UL else 0UL))
        sim.Poke("mid_enable", (if mid then 1UL else 0UL))
        sim.Poke("high_enable", (if high then 1UL else 0UL))

        // The read is combinational, so it answers about the words standing
        // before this cycle's write.
        if sim.Peek "word" <> model[raddr] then ok <- false

        // Last site first in the model, because last site wins.
        if high then model[(addr + 2) % 8] <- 0x33UL
        elif mid then model[(addr + 1) % 8] <- 0x22UL
        elif low then model[addr] <- 0x11UL

        if low && mid && high then allThree <- allThree + 1

        sim.Tick()

    // A fold from the wrong end passes every cycle where at most one enable is
    // high, so the check is only worth anything if the overlap actually happened.
    onlyOneWriteSite && ok && allThree > 300

/// **Masks do not merge across write sites**: the priority pick happens first,
/// and only the winner's mask applies.
///
/// The two writes cover opposite halves of the word, which is the arrangement
/// that makes the wrong answer look right — merge them and both halves land,
/// and a design filling a word from two places would appear to work. It does
/// not: with both enables high the high write wins outright, its two lanes
/// land, and the low two keep whatever a previous cycle left there.
///
/// The model states that directly rather than deriving it, so a change to the
/// fold's direction fails here instead of quietly redefining the rule.
let private masksDoNotMergeAcrossSites () =
    let sim = Sim maskedWritePriority.def
    let rng = System.Random 7272
    let model = Array.zeroCreate<uint64> 8

    let mutable ok = true
    let mutable checks = 0
    let mutable bothFired = 0
    let mutable pending = -1L

    for _ in 1..6000 do
        let addr = rng.Next 8
        let raddr = rng.Next 8
        let lowData = uint64 (rng.Next()) &&& 0xFFFFFFFFUL
        let highData = uint64 (rng.Next()) &&& 0xFFFFFFFFUL
        let low = rng.Next 2 = 1
        let high = rng.Next 2 = 1

        sim.Poke("addr", uint64 addr)
        sim.Poke("raddr", uint64 raddr)
        sim.Poke("low_data", lowData)
        sim.Poke("high_data", highData)
        sim.Poke("low_enable", (if low then 1UL else 0UL))
        sim.Poke("high_enable", (if high then 1UL else 0UL))

        // Read-first, like every synchronous read here: the expectation is
        // captured now and compared next cycle.
        if pending >= 0L then
            checks <- checks + 1
            if sim.Peek "rdata" <> uint64 pending then ok <- false

        pending <- int64 model[raddr]

        // One winner, one mask. The high write is later in the source.
        let apply data lanes =
            let keep = List.fold (fun acc lane -> acc ||| (0xFFUL <<< (lane * 8))) 0UL lanes
            model[addr] <- (data &&& keep) ||| (model[addr] &&& ~~~keep)

        if high then apply highData [ 2; 3 ]
        elif low then apply lowData [ 0; 1 ]

        if low && high then bothFired <- bothFired + 1

        sim.Tick()

    ok && checks > 5000 && bothFired > 1000

/// A ROM holds what it was declared with, at **every** address.
///
/// Walked rather than sampled, and that is the point: a table read at the wrong
/// index still returns a plausible number, and a spot check of two entries
/// passes against an off-by-one. Both storages are walked, because they reach
/// the word by different routes — LUTs combinationally, a block through a read
/// register a cycle later — and only the second can be a cycle out.
let private romsHoldTheirContents () =
    let squares = [| 0UL; 1UL; 4UL; 9UL; 16UL; 25UL; 36UL; 49UL |]

    let primes =
        [| 2UL; 3UL; 5UL; 7UL; 11UL; 13UL; 17UL; 19UL; 23UL; 29UL; 31UL; 37UL; 41UL; 43UL; 47UL; 53UL |]

    let lutWalks =
        let sim = Sim romLookup.def

        squares
        |> Array.indexed
        |> Array.forall (fun (i, expected) ->
            sim.Poke("index", uint64 i)
            sim.Tick()
            sim.Peek "square" = expected)

    let blockWalks =
        let sim = Sim blockRomLookup.def
        let mutable ok = true

        // A cycle behind, so the address goes in and the answer is read after
        // the edge — the whole difference between the two designs.
        for i in 0 .. primes.Length - 1 do
            sim.Poke("index", uint64 i)
            sim.Tick()
            if sim.Peek "prime" <> primes[i] then ok <- false

        ok

    // And the contents reach the Verilog, which is the half a simulator cannot
    // speak for: an initial block Vivado turns into a BRAM INIT.
    let contentsAreEmitted =
        let verilog = emitDesign blockRomLookup.def
        verilog.Contains "primes[15] = 16'd53" && verilog.Contains "(* ram_style = \"block\" *)"

    lutWalks && blockWalks && contentsAreEmitted

/// **Writes fold and reads do not.** Two reads of one memory at two addresses
/// are two ports, each answering its own address.
///
/// The behavioural claim is the easy half and the structural one is why this
/// design exists: `memRead` counts nothing, so a second simultaneous read is a
/// second copy of the array in LUTs, and the cost shows up in the utilisation
/// report rather than as an error. Nothing here can measure LUTs, so what is
/// asserted instead is that both reads survived to the Verilog — an emitter
/// that folded them would produce a design that answers one address twice, and
/// the random stimulus drives the two apart in seven cycles out of eight.
let private readsDoNotFold () =
    let sim = Sim dualRead.def
    let rng = System.Random 4242
    let model = Array.zeroCreate<uint64> 8

    let mutable ok = true
    let mutable apart = 0
    let mutable pending = None

    for _ in 1..4000 do
        let waddr = rng.Next 8
        let wdata = uint64 (rng.Next 256)
        let wen = rng.Next 4 <> 0
        let addrA = rng.Next 8
        let addrB = rng.Next 8

        sim.Poke("waddr", uint64 waddr)
        sim.Poke("wdata", wdata)
        sim.Poke("wen", (if wen then 1UL else 0UL))
        sim.Poke("addr_a", uint64 addrA)
        sim.Poke("addr_b", uint64 addrB)

        // The LUTRAM pair answers now; the block pair answers about the
        // addresses that were presented last cycle.
        if sim.Peek "now_a" <> model[addrA] then ok <- false
        if sim.Peek "now_b" <> model[addrB] then ok <- false

        match pending with
        | Some (a, b) ->
            if sim.Peek "next_a" <> a then ok <- false
            if sim.Peek "next_b" <> b then ok <- false
        | None -> ()

        pending <- Some(model[addrA], model[addrB])

        if addrA <> addrB then apart <- apart + 1
        if wen then model[waddr] <- wdata

        sim.Tick()

    ok && apart > 3000

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
    let sim = Sim twoWindowSlave.def

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
    let sim = Sim carriedRead.def
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

/// The boundary helpers' defining property: **every beat is delivered exactly
/// once, in order, whatever the consumer does with `ready`.**
///
/// A property rather than a captured trace, and the distinction matters here
/// more than usual: the sequence 1, 2, 3 … is what a correct handshake
/// produces, and *every* way of getting the handshake wrong shows up in it. A
/// `ready` read a cycle late repeats a value; a `valid` driven from the wrong
/// side skips one; a source that advances its counter on offer rather than on
/// transfer drops beats exactly when the consumer stalls — which is why the
/// consumer here stalls at random rather than taking everything offered.
///
/// `boundaryWalk` puts `streamDrive` (the source builds its own stream),
/// `streamToInputPorts` and `streamOfOutputPorts` (the parent drives one
/// instance's boundary from the other's) all in one path, so a mistake in any
/// of the three breaks this sequence.
let private boundaryDelivers () =
    let sim = Sim boundaryWalk.def
    let rng = System.Random 20260908
    let seen = ResizeArray<uint64>()

    for _ in 1..2000 do
        let take = rng.Next 3 <> 0
        sim.Poke("out_ready", (if take then 1UL else 0UL))

        if take && sim.Peek "out_valid" = 1UL then
            seen.Add(sim.Peek "out_data")

        sim.Tick()

    // `satIncLogic` saturates at 0xFF, so the walk's counter wrapping is not
    // part of the claim: check the run up to the first saturated beat, which is
    // long enough to cover many stalls.
    let run = seen |> Seq.takeWhile (fun v -> v < 0xFFUL) |> List.ofSeq

    List.length run > 60
    && run = [ for i in 1 .. List.length run -> uint64 i ]

/// Both storages, one model. The LUTRAM form and the block form are different
/// circuits, and this is the assertion that a caller cannot tell.
let private fifoBuffers () =
    fifoModel bufferedStream.def 8 && fifoModel deepBufferedStream.def 128

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

    capacityOf bufferedStream.def = 8
    && capacityOf deepBufferedStream.def = 128
    && sustainedRate bufferedStream.def = 1000
    && sustainedRate deepBufferedStream.def = 1000

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
    let sim = Sim taggedDivide.def
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
    let sim = Sim farmedDivide.def
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
            (defModule "AsyncOnBlock" (fun p -> (p.inPort "addr" 3, p.outPort "out" 8)) (fun (addr, out) ->
                let m = blockMem "blocked" 3 8
                memRead m addr ==> out))
                .def
            |> emitDesign
            |> ignore

            false
        with ex ->
            ex.Message.Contains "block RAM cannot read combinationally"

    // And the attribute reaches the Verilog, which is the half that does the
    // work — the error only stops the wrong design being written.
    let attributeIsEmitted =
        let d =
            defModule "AsyncOnDistributed" (fun p -> (p.inPort "addr" 3, p.outPort "out" 8)) (fun (addr, out) ->
                let m = distributedMem "lutram" 3 8
                memRead m addr ==> out)

        (emitDesign d.def).Contains "(* ram_style = \"distributed\" *) reg [7:0] lutram"

    // A sync read is legal on any of them: block RAM's whole point.
    let syncIsAlwaysFine =
        let d =
            defModule "SyncOnBlock" (fun p -> (p.inPort "addr" 3, p.outPort "out" 8)) (fun (addr, out) ->
                let m = blockMem "blocked" 3 8
                (memReadPort m addr).data ==> out)

        (emitDesign d.def).Contains "(* ram_style = \"block\" *)"

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

    [ streamAgreesUnderStalls true "divider" streamDivider.def ignore [ "dividend"; "divisor" ] [ "quotient"; "remainder" ] pairs
      streamAgreesUnderStalls true "divider+context" taggedDivide.def ignore [ "dividend"; "divisor"; "tag" ] [ "quotient"; "remainder"; "tag_out" ] tagged
      streamAgreesUnderStalls false "divider farm" farmedDivide.def ignore [ "dividend"; "divisor"; "tag" ] [ "quotient"; "remainder"; "tag_out" ] tagged ]
    |> List.forall id

let private mainDemo () =
    for d in
        [ add3
          dot2
          dot2Auto
          dot2Ambient.def
          dot2Inline.def
          pipelinedDot.def
          gatedCounter.def
          streamPipe.def
          coordPipe.def
          onCounter.def
          onPriority.def
          loopPipeline.def
          treeSum.def
          ramTest.def
          cmdProcessor.def
          unionRoundTrip.def
          forkJoin.def
          signedOps.def
          xorOps.def
          satOps.def
          escapeStep.def
          escapeStepFixed.def
          escapeStep28.def
          wideBeat.def
          widenOps.def
          dispatchRoundTrip.def
          clusteredRoundTrip.def
          twoStreamSplit.def
          twoStreamSplitReplicateJoin.def
          framePipeline.def
          (sweepPipeline 2).def
          windowSweep.def
          pixelBlur.def
          movingAverage.def
          sparseDot.def
          indirectGather.def
          indexSweep.def
          maxPoolDilate.def
          maxPool2x2.def
          maxPoolCombinational.def
          typedPipeline.def
          probedPipe.def
          axiWriteMaster.def
          axiWriteMasterSingle.def
          axiReadMaster.def
          axiReadMasterSingle.def
          axiReadMasterBurst.def
          axiPulse.def
          axiScratch.def
          neighborCount.def
          regMapScratch.def
          snapshotConflate.def
          snapshotDdr.def
          audioOps.def
          audioChain.def
          audioTone.def
          i2sLoopback.def
          multibandStage.def ] do
        printfn "%s" (emitDesign d)
        printfn ""

    try
        emitDesign nameCollision.def |> ignore
        printfn "name collision:               NOT detected — emitDesign is broken"
    with ex ->
        printfn $"emitDesign refused:           {ex.Message}"

    try
        emitDesign widthViolation.def |> ignore
        printfn "width violation:              NOT detected — emitDesign is broken"
    with ex ->
        printfn $"emitDesign refused:           {ex.Message}"

    try
        declCollision () |> ignore
        printfn "declaration collision:        NOT detected — the declaration check is broken"
    with ex ->
        printfn $"elaboration refused:          {ex.Message}"

    try
        emitDesign danglingStream.def |> ignore
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


    for m in
        [ add3
          dot2
          dot2Auto
          dot2Ambient.def
          dot2Inline.def
          pipelinedDot.def
          gatedCounter.def
          streamPipe.def
          coordPipe.def
          onCounter.def
          onPriority.def
          loopPipeline.def
          treeSum.def
          ramTest.def
          cmdProcessor.def
          unionRoundTrip.def
          forkJoin.def
          signedOps.def
          xorOps.def
          satOps.def
          escapeStep.def
          escapeStepFixed.def
          escapeStep28.def
          wideBeat.def
          widenOps.def
          dispatchRoundTrip.def
          clusteredRoundTrip.def
          twoStreamSplit.def
          twoStreamSplitReplicateJoin.def
          framePipeline.def
          (sweepPipeline 2).def
          windowSweep.def
          pixelBlur.def
          movingAverage.def
          sparseDot.def
          indirectGather.def
          indexSweep.def
          maxPoolDilate.def
          maxPool2x2.def
          maxPoolCombinational.def
          typedPipeline.def
          probedPipe.def
          axiWriteMaster.def
          axiWriteMasterSingle.def
          axiReadMaster.def
          axiReadMasterSingle.def
          axiReadMasterBurst.def
          axiPulse.def
          axiScratch.def
          neighborCount.def
          regMapScratch.def
          snapshotConflate.def
          snapshotDdr.def ] do
        match checkWidths m with
        | [] -> printfn $"{m.name}: widths ok"
        | problems -> problems |> List.iter (printfn "%s")

    // The telemetry workflow: a stalled ProbedPipe read by streamReport —
    // learning where a design stalls costs a tick and a peek, not a build.
    let reportSim = Sim(probedPipe.def)
    reportSim.Poke("in_valid", 1UL)
    reportSim.Poke("out_ready", 0UL)

    for _ in 1..10 do
        reportSim.Tick()

    for name, blocked, starved in streamReport reportSim.Peek probedPipe.def do
        printfn $"stream '{name}':             blocked %d{blocked} starved %d{starved}"

    // The builder's defining property: a map built by allocating offsets is
    // the same map as one built by writing them. Entry for entry — name,
    // offset and kind — against the hand-written `ScratchMap` this design used
    // to carry, which survives here as the reference precisely so the claim
    // has something to be true against.
    //
    // A golden vector would not do: it would pass a builder that allocated
    // *consistently wrong*, since the emitted Verilog would still be
    // self-consistent. What has to hold is agreement with the offsets a person
    // computed by hand, including the two windows' alignment.
    let builderMatchesByHand =
        let byHand =
            [ roConst "id" 0x000UL 0xF5C0FFEEUL
              pulseBit "bump" 0x000UL 0
              pulseBit "clear" 0x000UL 1
              rwReg "threshold" 0x004UL 16 0UL
              roField "count" 0x008UL 0 8
              roField "high" 0x008UL 8 1
              w1cBit "wrapIrq" 0x00CUL 0
              roField "patLow" 0x010UL 0 8
              rwArray "pattern" 0x040UL 16 Distributed None
              roArray "trace" 0x080UL 16 Distributed ]

        let allocated = scratchMap.entries

        let same =
            List.length byHand = List.length allocated
            && List.forall2 (fun (a: RegEntry) (b: RegEntry) -> a = b) byHand allocated

        if not same then
            let show (es: RegEntry list) =
                es |> List.map (fun e -> $"{e.name}@0x%03x{e.offset}") |> String.concat " "

            printfn $"      by hand:   {show byHand}"
            printfn $"      allocated: {show allocated}"

        same

    // The aperture the builder derives when it is not told one: the smallest
    // power of two that holds the high-water mark, floored at 16 bytes.
    let builderDerivesAperture =
        let _, tiny = buildRegMap (fun r -> r.RwReg("a", 8, 0UL), r.RoField("b", 8))
        let _, wide = buildRegMap (fun r -> [ for i in 0..7 -> r.RwReg($"r{i}", 32, 0UL) ])
        tiny.apertureAddrWidth = 4 && wide.apertureAddrWidth = 5

    // The layout fingerprint's defining property: it tracks the LAYOUT, not the
    // source text. Two maps with the same addresses hash the same however they
    // were written; any change to an address, a width, a name or a default
    // changes it. That is what makes it usable as a build stamp — a driver
    // comparing it is asking "were we made from the same map", and a reordering
    // that moved nothing must not raise a false alarm.
    let fingerprintTracksLayout =
        let hashOf (m: RegMap) =
            m.entries
            |> List.pick (fun e ->
                match e.name, e.kind with
                | "layout", RoConst v -> Some v
                | _ -> None)

        // Same addresses, written in a different order — `At` earning its keep
        // in the one place it is used, which is a test that needs to defeat
        // declaration order on purpose.
        let _, inOrder =
            buildRegMapPinned 5 (fun r ->
                r.LayoutHash "layout"
                r.RwReg("a", 8, 0UL) |> ignore
                r.RwReg("b", 16, 0UL) |> ignore)

        let _, reordered =
            buildRegMapPinned 5 (fun r ->
                r.At 0x08UL
                r.RwReg("b", 16, 0UL) |> ignore
                r.At 0x00UL
                r.LayoutHash "layout"
                r.RwReg("a", 8, 0UL) |> ignore)

        let _, widened =
            buildRegMapPinned 5 (fun r ->
                r.LayoutHash "layout"
                r.RwReg("a", 8, 0UL) |> ignore
                r.RwReg("b", 17, 0UL) |> ignore)

        let _, renamed =
            buildRegMapPinned 5 (fun r ->
                r.LayoutHash "layout"
                r.RwReg("a", 8, 0UL) |> ignore
                r.RwReg("c", 16, 0UL) |> ignore)

        let _, added =
            buildRegMapPinned 5 (fun r ->
                r.LayoutHash "layout"
                r.RwReg("a", 8, 0UL) |> ignore
                r.RwReg("b", 16, 0UL) |> ignore
                r.RwReg("d", 8, 0UL) |> ignore)

        let base_ = hashOf inOrder

        hashOf reordered = base_
        && hashOf widened <> base_
        && hashOf renamed <> base_
        && hashOf added <> base_
        && base_ <= 0xFFFFUL

    // `At` is unused by every map in this repository, so its behaviour is
    // asserted here rather than by a caller: it seeks, and what follows is
    // allocated from where it seeks to.
    let builderAtSeeks =
        let regs, _ = buildRegMap (fun r ->
            let first = r.RwReg("first", 32, 0UL)
            r.At 0x40UL
            first, r.RwReg("second", 32, 0UL))

        (fst regs).offset = 0x00UL && (snd regs).offset = 0x40UL

    // The declarative reg map, driven by real five-channel transactions: the
    // ID overlay, a pulse pair, packed ro fields against an rw threshold, the
    // w1c + irq path through a genuine 8-bit wrap, and a window word written
    // by the host and sync-read back out by the fabric.
    let regMapOk =
        let sim = Sim(regMapScratch.def)

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

    printfn $"reg map builder = by hand:    %b{builderMatchesByHand}"
    printfn $"reg map builder derives width:%b{builderDerivesAperture}"
    printfn $"reg map builder At seeks:     %b{builderAtSeeks}"
    printfn $"layout hash tracks layout:    %b{fingerprintTracksLayout}"
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
        let sim = Sim(regMapScratch.def)
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
        let sim = Sim(regMapScratch.def)
        let axi = SimAxi.client sim
        let read32, write32 = axi.read32, axi.write32

        for _ in 1..5 do
            write32 0x000UL 1UL // bump: writes 0xC500 | count at trace[count]

        let marked =
            [ 0..4 ] |> List.forall (fun i -> read32 (0x080UL + uint64 (i * 4)) = 0xC500UL + uint64 i)

        let untouched = read32 (0x080UL + 10UL * 4UL) = 0UL
        marked && untouched

    printfn $"design-written window reads:  %b{designWindowOk}"
    printfn $"registry entries all load:    %b{registryLoads ()}"
    printfn $"reg holds through reset:      %b{holdsThroughReset ()}"
    printfn $"dynamic shifts shift:         %b{dynamicShiftsShift ()}"
    printfn $"bit reductions reduce:        %b{reductionsReduce ()}"
    printfn $"constant division divides:    %b{divisionDivides ()}"
    printfn $"dividers ignore stalls:       %b{dividersAreStallIndependent ()}"
    printfn $"fifo buffers in order:        %b{fifoBuffers ()}"
    printfn $"boundary delivers in order:   %b{boundaryDelivers ()}"
    printfn $"fifo storage is invisible:    %b{fifoStorageIsInvisible ()}"
    printfn $"carried read pairs up:        %b{carriedReadPairsUp ()}"
    printfn $"windows answer their range:   %b{windowsAnswerTheirOwnRange ()}"
    printfn $"strobe spares other lanes:    %b{strobeSparesUntouchedLanes ()}"
    printfn $"wide strobe spares lanes:     %b{strobeSparesWideLanes ()}"
    printfn $"last write site wins:         %b{lastWriteSiteWins ()}"
    printfn $"masks do not merge:           %b{masksDoNotMergeAcrossSites ()}"
    printfn $"roms hold their contents:     %b{romsHoldTheirContents ()}"
    printfn $"reads do not fold:            %b{readsDoNotFold ()}"
    printfn $"context rides along:          %b{contextRidesAlong ()}"
    printfn $"farm carries context:         %b{farmCarriesContext ()}"
    printfn $"ram style rule holds:         %b{ramStyleRule ()}"
    printfn $"a literal borrows the sign:   %b{literalBorrowsTheSign ()}"
    printfn $"saturate keeps the reading:   %b{saturateKeepsTheReading ()}"
    printfn $"ifElse ladders, four claims:  %b{ifElseLadders ()}"
    printfn $"Switch is linear, not 2^n:   %b{switchIsLinear ()}"
    printfn $"line window property:         %b{lineWindowProperty ()}"
    printfn $"pixel blur (8-bit cells):     %b{pixelBlurAgrees ()}"
    printfn $"moving average (unframed):    %b{movingAverageAgrees ()}"
    printfn $"sparse dot (static gather):   %b{sparseDotAgrees ()}"
    printfn $"indirect gather (dynamic):    %b{indirectGatherAgrees ()}"
    printfn $"range stream schedule:        %b{rangeStreamProperty ()}"
    printfn $"max-pool dilation (banded):   %b{maxPoolAgrees ()}"
    printfn $"max pool 2x2 stride 2:        %b{maxPool2x2Agrees ()}"
    printfn $"max pool combinational:       %b{maxPoolCombinationalAgrees ()}"
    printfn $"state machines, four claims:  %b{stateMachines ()}"
    // The design-space sweep: the same pipeline at each worker count, driven
    // flat out, judged by throughput and the probes. This is the whole
    // methodology at toy scale — the optimum is read off the table, not
    // guessed: beats stop rising where the expander becomes the wall.
    printfn ""

    for nWorkers in [ 1; 2; 4; 8 ] do
        let d = (sweepPipeline nWorkers).def
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

        let sim = Sim(multibandStage.def)
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
        writeDiffWith (diffDesigns ()) outDir
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
