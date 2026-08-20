/// The tutorial, as something you can run.
///
///     dotnet run -c Release                    # open the tutorial debugger
///     dotnet run -c Release -- "RAM"           # open it on one design
///     dotnet run -c Release -- check           # the living checks
///     dotnet run -c Release -- diff <dir>      # write the Verilog testbenches
///
/// The checks are here rather than in a project of their own because they are
/// the same claim from the other side: a page that teaches something the
/// silicon does not do would be worse than no page, so every design the
/// tutorial ships goes through `Warp11.Diff` exactly as the oracle catalog's
/// do. `run_differential.sh` compiles what `diff` writes.
module Warp11.Tutorial.App.Main

open Warp11
open Warp11.Catalog
open Warp11.Tutorial

let private designs =
    Registry.catalog.entries |> List.map (fun e -> e.build ())

/// Every entry elaborates, simulates a cycle, and names itself consistently.
let private entriesLoad () =
    let loads (e: Entry) =
        try
            let d = e.build ()
            let sim = Sim d
            sim.Tick()
            let inventory = Inventory.ofDesign d

            not (e.label.Trim() = "")
            && inventory.topName = d.name
            && not (List.isEmpty inventory.signals)
        with _ ->
            false

    Registry.catalog.entries |> List.forall loads

/// Every entry has a page, and the source pane can find its design.
///
/// The tutorial is the one catalog where a missing page is a failure rather
/// than a gap: an entry nobody has written about is not a tutorial entry, it is
/// a design that wandered in.
let private pagesExist () =
    let complete (e: Entry) =
        match Registry.catalog.doc e.binding, Registry.catalog.source e.binding with
        | Some page, Some source ->
            page.Trim() <> ""
            && page.Contains "## "
            && source.Contains e.binding
        | _ -> false

    Registry.catalog.entries |> List.forall complete

/// Every signal an entry asks to have watched is a signal its design has.
///
/// The session ignores a name it cannot find, which is the right behaviour at
/// run time and the reason this check exists: rename a register and the page
/// keeps telling the reader to watch something that quietly never appears.
let private watchedSignalsExist () =
    let present (e: Entry) =
        let inventory = Inventory.ofDesign (e.build ())
        let names = inventory.signals |> List.map (fun s -> s.name) |> Set.ofList

        let inputs =
            inventory.signals
            |> List.filter (fun s -> s.kind = Warp11.Inventory.SignalKind.Input)
            |> List.map (fun s -> s.name)
            |> Set.ofList

        // A poke of a non-input would be silently ignored by the session, so
        // the check holds pokes to the stronger claim: the name exists AND is
        // an input the page's reader could have typed into themselves.
        let badPokes =
            e.pokes |> List.map fst |> List.filter (inputs.Contains >> not)

        match e.watch |> List.filter (names.Contains >> not), badPokes with
        | [], [] -> true
        | missing, badPokes ->
            for m in missing do
                printfn "  %s watches %s, which it does not have" e.label m

            for p in badPokes do
                printfn "  %s pokes %s, which is not an input it has" e.label p

            false

    Registry.catalog.entries |> List.forall present

/// Labels are what the picker shows and what the command line matches, so two
/// entries sharing one is a design nobody can open.
let private labelsAreUnique () =
    let labels = Registry.catalog.entries |> List.map (fun e -> e.label)
    List.length (List.distinct labels) = List.length labels

/// Every page a "see also" points at is a page that exists.
///
/// The set is meant to read as a path rather than a reference, which only works
/// while the links land. They rot silently: a page moved into a later tier, or
/// an entry renamed, leaves prose confidently recommending something the reader
/// cannot find, and nothing else in the build notices.
let private crossLinksResolve () =
    let labels = Registry.catalog.entries |> List.map (fun e -> e.label) |> Set.ofList

    let referenced (page: string) =
        let after = page.Split("## See also")

        if after.Length < 2 then
            []
        else
            after.[1].Split "**"
            // Split on the marker: every odd-indexed piece is bold text.
            |> Array.mapi (fun i part -> i, part)
            |> Array.filter (fun (i, _) -> i % 2 = 1)
            |> Array.map snd
            |> Array.toList

    Registry.catalog.entries
    |> List.forall (fun e ->
        match Registry.catalog.doc e.binding with
        | None -> false
        | Some page ->
            match referenced page |> List.filter (fun l -> not (labels.Contains l)) with
            | [] -> true
            | dangling ->
                printfn "  %s links to %s" e.label (String.concat ", " dangling)
                false)

/// A design that lies, so the check below can prove the machinery works.
///
/// It is not in the catalog and never will be: the shipped assertion design's
/// claim is true for every input, which is what a good assertion is, and a
/// claim that cannot fail cannot demonstrate failing. This one can.
let private brokenClaim =
    design "BrokenClaim" (fun () ->
        let x = input "x" 8
        let y = output "y" 8
        assertThat (eq x (lit 0UL 8)) "x must be zero"
        x ==> y)

/// The claims the pages make, checked against the designs that make them.
/// Each of these is a sentence someone reads and believes.
let private pagesTellTheTruth () =
    // Counter: holds when neither input is high, clears regardless of enable.
    let counterHolds =
        let sim = Sim counter
        sim.Poke("enable", 1UL)
        for _ in 1..5 do sim.Tick()
        let climbed = sim.Peek "count" = 5UL

        sim.Poke("enable", 0UL)
        for _ in 1..5 do sim.Tick()
        let held = sim.Peek "count" = 5UL

        sim.Poke("enable", 1UL)
        sim.Poke("clear", 1UL)
        sim.Tick()
        climbed && held && sim.Peek "count" = 0UL

    // Priority mux: sel1 outranks sel0, which is the page's whole claim.
    let sel1Wins =
        let sim = Sim priorityMux
        sim.Poke("a", 0x11UL)
        sim.Poke("b", 0x22UL)
        sim.Poke("c", 0x33UL)
        sim.Poke("sel0", 1UL)
        sim.Poke("sel1", 1UL)
        sim.Peek "out" = 0x33UL

    // Signed operations: the same bits, two answers.
    let signednessDiffers =
        let sim = Sim signedOps
        sim.Poke("a", 0xFFUL)
        sim.Poke("b", 0x01UL)
        sim.Peek "below" = 0UL && sim.Peek "below_signed" = 1UL

    // RAM: the combinational read is the ported one, a cycle early.
    // Your own modules: two instances accumulate independently from one
    // definition, saturation lives inside the module so both have it, and
    // `lowest` is the Min8 instance doing its one job.
    let instancesAreIndependent =
        let sim = Sim ownModules
        sim.Poke("add_left", 3UL)
        sim.Poke("add_right", 5UL)
        sim.Poke("en", 1UL)

        for _ in 1..4 do
            sim.Tick()

        let climbedApart =
            sim.Peek "total_left" = 12UL
            && sim.Peek "total_right" = 20UL
            && sim.Peek "lowest" = 12UL

        sim.Poke("add_right", 200UL)

        for _ in 1..3 do
            sim.Tick()

        let saturated = sim.Peek "total_right" = 0xFFUL && sim.Peek "total_left" = 21UL

        sim.Poke("en", 0UL)
        sim.Tick()
        sim.Tick()
        let held = sim.Peek "total_left" = 21UL && sim.Peek "total_right" = 0xFFUL

        climbedApart && saturated && held

    let thisCycleLeadsNextByOne =
        let sim = Sim ram
        sim.Poke("waddr", 3UL)
        sim.Poke("wdata", 0xAAUL)
        sim.Poke("wen", 1UL)
        sim.Tick()
        sim.Poke("wen", 0UL)
        sim.Poke("raddr", 3UL)
        let thisCycleNow = sim.Peek "this_cycle_out" = 0xAAUL
        let nextCycleNotYet = sim.Peek "next_cycle_out" <> 0xAAUL
        sim.Tick()
        thisCycleNow && nextCycleNotYet && sim.Peek "next_cycle_out" = 0xAAUL

    // Bit shapes: the one-hot round trip is the identity, every index.
    let oneHotRoundTrips =
        let sim = Sim bitShapes

        [ 0UL..3UL ]
        |> List.forall (fun i ->
            sim.Poke("index", i)
            sim.Peek "recovered" = i)

    // Sequencer: stall holds Execute, and a full run retires four passes.
    let stallHolds =
        let sim = Sim sequencer
        sim.Poke("stall", 1UL)
        sim.Poke("start", 1UL)
        sim.Tick()
        sim.Poke("start", 0UL)
        for _ in 1..3 do sim.Tick()
        let stuck = sim.Peek "stage"
        for _ in 1..10 do sim.Tick()
        let stillStuck = sim.Peek "stage" = stuck

        sim.Poke("stall", 0UL)
        for _ in 1..40 do sim.Tick()
        stillStuck && sim.Peek "finished" = 1UL && sim.Peek "retired" = 4UL

    // Fixed-point: 1.5 * 2.0 is 3.0, and reinterpreting moves no bits.
    let fixedArithmetic =
        let sim = Sim fixedPoint
        sim.Poke("a", 0x18UL) // 1.5 in Q4.4
        sim.Poke("b", 0x20UL) // 2.0 in Q4.4
        // 3.0 in Q4.4 is 48. `doubled` reads a as Q5.3 — same bits, twice the
        // value — so the bits must be unchanged.
        sim.Peek "product" = 48UL
        && sim.Peek "doubled" = 0x18UL
        && sim.Peek "below" = 1UL

    // ROM: the table is there before the first cycle, and the padding reads zero.
    let romIsPreloaded =
        let sim = Sim romTable

        let squaresRight =
            [ 0UL..7UL ]
            |> List.forall (fun i ->
                sim.Poke("index", i)
                sim.Peek "square" = i * i)

        sim.Poke("index", 4UL)
        let inRange = sim.Peek "prime" = 11UL
        sim.Poke("index", 6UL)
        squaresRight && inRange && sim.Peek "prime" = 0UL

    // Assertions: the shipped claim survives every reachable state, and the
    // machinery does fire when a claim is false.
    let claimsHold =
        let sim = Sim(assertions, checkAsserts = true)
        sim.Poke("step", 1UL)
        for _ in 1..50 do sim.Tick()
        let held = sim.ViolationCount = 0 && sim.Peek "phase" <= 4UL

        let liar = Sim(brokenClaim, checkAsserts = true)
        liar.Poke("x", 1UL)
        liar.Tick()
        held && liar.ViolationCount > 0

    // The pipe is wires: the transform is combinational and backpressure
    // passes straight back through it.
    let pipeIsFree =
        let sim = Sim streamPipe
        sim.Poke("in_valid", 1UL)
        sim.Poke("in_value", 7UL)
        sim.Poke("out_ready", 1UL)
        let forwarded = sim.Peek "out_value" = 8UL && sim.Peek "out_valid" = 1UL
        sim.Poke("out_ready", 0UL)
        forwarded && sim.Peek "in_ready" = 0UL

    // Three stages are three cycles.
    let stagesCostCycles =
        let sim = Sim streamStages
        sim.Poke("in_valid", 1UL)
        sim.Poke("in_value", 5UL)
        sim.Poke("out_ready", 1UL)

        let arrivals =
            [ for _ in 1..4 do
                  sim.Tick()
                  yield sim.Peek "out_valid" ]

        arrivals = [ 0UL; 0UL; 1UL; 1UL ] && sim.Peek "out_value" = 8UL

    // A farm returns everything it was given, and not in that order. The value
    // records which lane a beat took: lane i adds i+1.
    let farmReorders =
        let sim = Sim streamFarm
        sim.Poke("in_valid", 1UL)
        sim.Poke("out_ready", 0UL)
        let issued = ResizeArray<uint64>()
        let mutable next = 1UL

        for _ in 1..8 do
            sim.Poke("in_id", next)
            sim.Poke("in_value", next * 10UL)

            if sim.Peek "in_ready" = 1UL then
                issued.Add next
                next <- next + 1UL

            sim.Tick()

        sim.Poke("in_valid", 0UL)
        sim.Poke("out_ready", 1UL)
        let returned = ResizeArray<uint64>()

        for _ in 1..20 do
            // A beat is consumed *by* the tick, so it is read before it.
            if sim.Peek "out_valid" = 1UL then returned.Add(sim.Peek "out_id")
            sim.Tick()

        issued.Count > 3
        && Set.ofSeq issued = Set.ofSeq returned
        && List.ofSeq issued <> List.ofSeq returned

    // Probes count the two ways a link wastes a cycle, and nothing else.
    let probesCount =
        let blocked = Sim streamProbes
        blocked.Poke("in_valid", 1UL)
        blocked.Poke("out_ready", 0UL)
        for _ in 1..20 do blocked.Tick()

        let starved = Sim streamProbes
        starved.Poke("in_valid", 0UL)
        starved.Poke("out_ready", 1UL)
        for _ in 1..20 do starved.Tick()

        blocked.Peek "intake_blocked" > 0UL
        && blocked.Peek "intake_starved" = 0UL
        && starved.Peek "intake_starved" > 0UL
        && starved.Peek "intake_blocked" = 0UL

    // A flow cannot be told to wait, so a consumer that is not there loses
    // beats — and the design counts exactly how many.
    let flowLosesBeats =
        let sim = Sim flowSampler
        sim.Poke("sample", 1UL)
        sim.Poke("out_ready", 0UL)
        for _ in 1..10 do sim.Tick()
        let lost = sim.Peek "dropped_count"

        let kept = Sim flowSampler
        kept.Poke("sample", 1UL)
        kept.Poke("out_ready", 1UL)
        for _ in 1..10 do kept.Tick()

        lost > 0UL && kept.Peek "dropped_count" = 0UL

    // The LFSR's defining property, walked rather than sampled: a wrong tap
    // mask still produces a plausible-looking stream, and only the full period
    // says otherwise.
    let lfsrIsMaximalLength =
        let sim = Sim noise
        sim.Poke("step", 1UL)
        let seed = sim.Peek "value"
        let seen = System.Collections.Generic.HashSet<uint64>()
        seen.Add seed |> ignore
        let mutable steps = 0
        let mutable home = false

        while not home && steps < 1000 do
            sim.Tick()
            steps <- steps + 1
            let v = sim.Peek "value"
            if v = seed then home <- true else seen.Add v |> ignore

        // 2^8 - 1 states, every one of them, and never zero.
        steps = 255 && seen.Count = 255 && not (seen.Contains 0UL)

    // Both trees compute the same sum; the pipelined one takes its depth in
    // cycles to say so, and reports that depth rather than being told it.
    let treesAgree =
        let sim = Sim adderTree
        sim.Poke("enable", 1UL)
        for i in 0..7 do sim.Poke($"x{i}", uint64 (i + 1))

        let combinational = sim.Peek "flat" = 36UL
        let depth = sim.Peek "depth"
        for _ in 1..3 do sim.Tick()
        combinational && depth = 3UL && sim.Peek "pipelined" = 36UL

    // A wrap is a signal, so it can drive the next counter up.
    let countersCascade =
        let sim = Sim wrapCounter
        sim.Poke("enable", 1UL)
        sim.Poke("last", 2UL)
        for _ in 1..5 do sim.Tick()
        // Five ticks of a period-5 counter is one full lap: column back to 0,
        // row advanced once.
        sim.Peek "column" = 0UL && sim.Peek "row" = 1UL

    // An edge is a level compared against its own past.
    let edgesFire =
        let sim = Sim edges
        sim.Poke("enable", 1UL)
        sim.Poke("signal", 0UL)
        sim.Tick()
        sim.Poke("signal", 1UL)
        let rose = sim.Peek "rising" = 1UL && sim.Peek "falling" = 0UL
        sim.Tick()
        let settled = sim.Peek "rising" = 0UL && sim.Peek "pulses" = 1UL
        sim.Poke("signal", 0UL)
        rose && settled && sim.Peek "falling" = 1UL

    // A tag has to travel as far as the data it describes.
    let delaysAlign =
        let sim = Sim delayAlign
        sim.Poke("data", 10UL)
        sim.Poke("tag", 1UL)
        sim.Tick()
        sim.Poke("tag", 0UL)
        sim.Poke("data", 20UL)
        // The tag went low immediately; its delayed copy has not heard yet.
        let immediate = sim.Peek "raw_tag" = 0UL
        for _ in 1..2 do sim.Tick()
        let stillHigh = sim.Peek "aligned_tag" = 1UL
        sim.Tick()
        immediate && stillHigh && sim.Peek "aligned_tag" = 0UL

    // One-hot grant, and a select with no comparator in it.
    let arbiterGrantsOne =
        let sim = Sim arbiter
        for i in 0..3 do
            sim.Poke($"req{i}", 1UL)
            sim.Poke($"value{i}", uint64 (0x10 * (i + 1)))

        let grants = [ for i in 0..3 -> sim.Peek $"grant{i}" ]
        let lowestWins = grants = [ 1UL; 0UL; 0UL; 0UL ] && sim.Peek "served" = 0x10UL

        sim.Poke("req0", 0UL)
        sim.Poke("req1", 0UL)
        let shifted = sim.Peek "served" = 0x30UL

        for i in 0..3 do sim.Poke($"req{i}", 0UL)
        lowestWins && shifted && sim.Peek "any" = 0UL && sim.Peek "served" = 0UL

    // A barrel is a schedule: a thread's slot comes round once every `threads`
    // cycles, and its writeback lands inside that gap.
    let barrelInterleaves =
        let sim = Sim barrelLane
        sim.Poke("x", 1UL)

        let trace =
            [ for _ in 1..16 do
                  sim.Tick()
                  yield sim.Peek "thread0" ]

        // Thread 0 advances on one cycle in four and holds through the other
        // three — the interleave, read straight off one thread's total.
        let advanced =
            trace |> List.pairwise |> List.mapi (fun i (a, b) -> i, a <> b) |> List.filter snd |> List.map fst

        let everyFourth = advanced |> List.pairwise |> List.forall (fun (a, b) -> b - a = 4)

        // Forty-two cycles is ten turns each, and thread t adds t+1 per turn.
        let totals = Sim barrelLane
        totals.Poke("x", 1UL)
        for _ in 1..42 do totals.Tick()

        everyFourth
        && List.length advanced >= 3
        && [ for t in 0..3 -> totals.Peek $"thread{t}" ] = [ 10UL; 20UL; 30UL; 40UL ]

    // The PRNG's defining property: it is *the* xoshiro128++ stream, not a
    // stream that looks like one. Any tap, rotate or ordering slip still
    // produces plausible noise, so the check walks it against the reference.
    let prngMatchesReference =
        let rotl (x: uint32) k = (x <<< k) ||| (x >>> (32 - k))
        let s = [| 1u; 2u; 3u; 4u |] // the state the core resets to

        let nextWord () =
            let word = rotl (s[0] + s[3]) 7 + s[0]
            let t = s[1] <<< 9
            s[2] <- s[2] ^^^ s[0]
            s[3] <- s[3] ^^^ s[1]
            s[1] <- s[1] ^^^ s[2]
            s[0] <- s[0] ^^^ s[3]
            s[2] <- s[2] ^^^ t
            s[3] <- rotl s[3] 11
            uint64 word

        let sim = Sim prng
        sim.Poke("step", 1UL)

        [ 1..64 ]
        |> List.forall (fun _ ->
            let agreed = sim.Peek "value" = nextWord ()
            sim.Tick()
            agreed)

    // A FIR's impulse response *is* its coefficient list — the one measurement
    // that pins taps, order and delay-line depth at once.
    let firRespondsWithItsCoefficients =
        let sim = Sim firFilter
        sim.Poke("sample", 1UL)

        let response =
            [ yield sim.Peek "smoothed"
              sim.Tick()
              sim.Poke("sample", 0UL)

              for _ in 1..4 do
                  yield sim.Peek "smoothed"
                  sim.Tick() ]

        response = [ 1UL; 2UL; 2UL; 1UL; 0UL ]

    // The three edge policies, each on a grid that only it reads as non-empty.
    let edgePoliciesDiffer =
        let sim = Sim lifeCell
        let poke cell = for y in 0..2 do for x in 0..2 do sim.Poke($"g{y}{x}", cell y x)

        poke (fun _ _ -> 1UL)
        // A full grid: eight live neighbors, so the center dies of crowding.
        let full = sim.Peek "live" = 8UL && sim.Peek "next" = 0UL && sim.Peek "orthogonal" = 4UL

        // The opposite corner is a neighbor only under wrap.
        poke (fun y x -> if (y, x) = (2, 2) then 1UL else 0UL)
        let wrapsRound = sim.Peek "corner_wrap" = 1UL && sim.Peek "corner_zero" = 0UL && sim.Peek "corner_clamp" = 0UL

        // Clamp folds three off-grid neighbors onto the cell itself, so a lone
        // corner counts as its own neighbor three times over.
        poke (fun y x -> if (y, x) = (0, 0) then 1UL else 0UL)
        let clampsOntoItself =
            sim.Peek "corner_clamp" = 3UL && sim.Peek "corner_zero" = 0UL && sim.Peek "corner_wrap" = 0UL

        // Exactly three neighbors is a birth.
        poke (fun y x -> if (y, x) = (1, 1) then 0UL elif y = 0 && x < 3 then 1UL else 0UL)
        full && wrapsRound && clampsOntoItself && sim.Peek "live" = 3UL && sim.Peek "next" = 1UL

    // Sharing is only safe if an answer comes back to the client that asked,
    // carrying the tag it asked with — and if neither client starves.
    let sharedUnitRoutesByTag =
        let sim = Sim sharedUnit
        let operands = [ 0, (5UL, 3UL, 4UL); 1, (9UL, 6UL, 7UL) ]

        for client, (tag, a, b) in operands do
            sim.Poke($"c{client}_valid", 1UL)
            sim.Poke($"c{client}_tag", tag)
            sim.Poke($"c{client}_a", a)
            sim.Poke($"c{client}_b", b)
            sim.Poke($"w{client}_ready", 1UL)

        let landed =
            [ for _ in 1..12 do
                  for client, _ in operands do
                      if sim.Peek $"w{client}_valid" = 1UL then
                          yield client, sim.Peek $"w{client}_tag", sim.Peek $"w{client}_product"

                  sim.Tick() ]

        let correct =
            landed
            |> List.forall (fun (client, tag, product) ->
                match List.tryFind (fun (c, _) -> c = client) operands with
                | Some (_, (expectedTag, a, b)) -> tag = expectedTag && product = a * b
                | None -> false)

        // Both clients were served, and no beat landed on the wrong one.
        correct
        && landed |> List.map (fun (c, _, _) -> c) |> List.distinct |> List.length = 2

    // The register map from the host's side: a write handshake, a readback,
    // and a constant the driver uses to know which bitstream it is talking to.
    let registerMapAnswers =
        let sim = Sim registerMap

        // The handshakes live in `SimAxi`, which asserts every step rather
        // than polling for it — a slave that stalls fails here loudly.
        let axi = SimAxi.client sim
        let read, write = axi.read32, axi.write32

        let idle = sim.Peek "running" = 0UL && sim.Peek "elapsed" = 0UL
        write 0x0UL 1UL
        let started = sim.Peek "running" = 1UL
        let identity = read 0x4UL = 0xA57AUL
        let readback = read 0x0UL = 1UL
        let elapsed = read 0x8UL

        idle && started && identity && readback && elapsed > 0UL

    // The arm gate, which is a hardware-safety property before it is a
    // correctness one: with no base address the master must not issue at all.
    let masterStaysDisarmed =
        let sim = Sim ddrMaster
        sim.Poke("m_axi_awready", 1UL)
        sim.Poke("m_axi_wready", 1UL)
        sim.Poke("m_axi_bvalid", 1UL)

        let issuedWhileDisarmed =
            [ for _ in 1..20 do
                  yield sim.Peek "m_axi_awvalid"
                  sim.Tick() ]

        sim.Poke("base_addr", 0x40000000UL)

        let addresses =
            [ for _ in 1..20 do
                  if sim.Peek "m_axi_awvalid" = 1UL then yield sim.Peek "m_axi_awaddr"
                  sim.Tick() ]

        List.forall ((=) 0UL) issuedWhileDisarmed
        && sim.Peek "words_written" > 0UL
        // Consecutive 32-bit words from the base the host supplied.
        && List.truncate 3 addresses = [ 0x40000000UL; 0x40000004UL; 0x40000008UL ]

    counterHolds
    && barrelInterleaves
    && prngMatchesReference
    && firRespondsWithItsCoefficients
    && edgePoliciesDiffer
    && sharedUnitRoutesByTag
    && registerMapAnswers
    && masterStaysDisarmed
    && lfsrIsMaximalLength
    && treesAgree
    && countersCascade
    && edgesFire
    && delaysAlign
    && arbiterGrantsOne
    && pipeIsFree
    && stagesCostCycles
    && farmReorders
    && probesCount
    && flowLosesBeats
    && fixedArithmetic
    && romIsPreloaded
    && claimsHold
    && sel1Wins
    && signednessDiffers
    && thisCycleLeadsNextByOne
    && instancesAreIndependent
    && oneHotRoundTrips
    && stallHolds

let private report name ok =
    printfn "%-34s%b" (name + ":") ok
    ok

let private runChecks () =
        let results =
            [ report "entries all load" (entriesLoad ())
              report "every entry has a page" (pagesExist ())
              report "labels are unique" (labelsAreUnique ())
              report "watched signals exist" (watchedSignalsExist ())
              report "cross-links resolve" (crossLinksResolve ())
              report "the pages tell the truth" (pagesTellTheTruth ()) ]

        printfn ""
        printfn "%d designs, %d pages" (List.length designs) (List.length Registry.catalog.entries)
        if List.forall id results then 0 else 1

[<EntryPoint>]
let main argv =
    match argv with
    | [| "check" |] -> runChecks ()
    | [| "diff"; outDir |] ->
        writeDiff designs outDir
        0
    | _ ->
        // Anything else is a design label, or nothing at all. The window's own
        // exit code is the process's.
        Warp11.SimView.Desktop.run (Debugger.source (Array.tryHead argv)) Debugger.panels
