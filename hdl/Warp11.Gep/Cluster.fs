/// The WarpCPU cluster pool, queue half (P4's summit, part (a)): a small pool
/// of breeder blocks feeding many eval-only lanes through the record router,
/// fed from a host-published pairing-entry ring in DDR and writing results
/// back to the same DDR.
///
/// One offspring's path:
///
///   a filler claims a free breeder + the next queue slot → fetches the 16-word
///   entry (one 4-beat burst) and both parent gene records → serializes them
///   into the breeder → the breeder breeds, compiles, and streams the unit
///   record out → the router binds that stream to a free lane bank → the PACK
///   FSM copies the child genome out of the DONE breeder into that lane-bank's
///   staging slot and releases the breeder (it breeds the next offspring while
///   the eval runs) → the lane evaluates and presents `(fitness, entryId)` →
///   the EMIT FSM writes the 4-beat gene record to the entry's dest slot, then
///   the `(fitness, entryId, seq)` ring record.
///
/// Both DDR writes for one offspring happen at result time, genome beats before
/// the ring beat, so when `results_done` counts a ring B-ack that offspring's
/// genome has landed too — the invariant a host `awaitResults` leans on. It
/// counts write *responses*, not beats handed to the master.
///
/// Evaluate-only entries (the skip-writeback flag) release their breeder in the
/// pack-pick cycle: nothing to stage, and emit goes straight to the ring record.
///
/// **Inline-parent queue mode** (`inlineParents`): the host marshals each work
/// item as entry + parentA + parentB records contiguous (48 words / 192 B), so
/// the fabric reads parents as the next sequential bursts of the entry's own
/// slot instead of random-accessing the population region by slot. Purely an
/// address change — the fabric sees the same parent bytes.
///
/// The auto/streaming half — the in-fabric selection FSM, the entry FIFO, the
/// fitness table and its barrier, the op-list emitter — is part (b) and is not
/// here; this module is queue mode alone.
module Warp11.Gep.Cluster

open Warp11
open Warp11.Stdlib
open Warp11.Gep.Hdl

/// Gene record stride on the wire: 16 words / 64 B / 4 beats. A pairing entry
/// is the same stride, so an inline-parent work item is three of them back to
/// back.
let gepRecordWords = 16

/// Pairing-entry flags live in word 7's high half. Bit 0: evaluate-only —
/// score the offspring and skip the genome writeback.
let gepFlagSkipWriteback = 1

/// An inline-parent work item: the entry plus both parent records is 192 B, but
/// the STRIDE is padded to 256 B so the whole item can be fetched as ONE 12-beat
/// burst. An AXI burst may not cross a 4 KB boundary, and at a 192 B stride
/// every 21st item straddles one; at 256 B — which divides 4096 — none can. The
/// padding costs 25% more queue bytes on a path whose cost is round-trip
/// latency, not bandwidth.
let gepWorkItemWords = 64
let gepWorkItemBeats = 12

/// The nine Bernoulli rates in the order a pairing entry packs them: two
/// 16-bit halves per word starting at word 3, so rate `i` is word `3 + i/2`'s
/// `i % 2` half. A stored rate is the HIGH half of the breeder's 32-bit
/// threshold — the host quantizes to 16 bits and its software oracle must draw
/// against the same quantized values. Kotlin's `GepRate.Crossover` is this
/// list's `constReplace`: both spell "cr", and it is constant replacement, not
/// recombination.
let private rateCount = 9

/// How many bits index `n` things.
/// The in-fabric generation loop. `popCapacity` (a power of two) sizes the
/// fitness table's per-region half; `opList` adds the producer half of the
/// streaming redesign, where the breed round streams its pairing entries out to
/// DDR for the host to gather inline parents instead of breeding them here.
type GepAutoShape = { popCapacity: int; opList: bool }

/// Everything that sizes the cluster. The divide is a sharing ratio here as it
/// is at the lane (`FuSharing`); `None` is a cluster with no divide arm at all.
/// `auto` is the same shape of choice one level up: `None` is a pool the host
/// drives entry by entry, and nothing of the selection machinery is elaborated.
[<NoEquality; NoComparison>]
type GepClusterShape =
    { nBreeders: int
      nLanes: int
      /// Fillers time-share one read master; more of them overlap entry and
      /// parent fetches across breeders. One collapses to a single dispatcher.
      nFillers: int
      functionSet: int[]
      terminalSet: int[]
      geneLen: int
      headLen: int
      constCount: int
      varCount: int
      capacity: int
      nThreads: int
      caseCapacity: int
      addrWidth: int
      readOutstanding: int
      writeOutstanding: int
      divide: FuSharing option
      inlineParents: bool
      auto: GepAutoShape option }

/// What the in-fabric generation loop needs from the host: the run's shape, one
/// breeding policy for every offspring, and the selection stream's seed.
type GepAutoConfig =
    { autoMode: Expr
      /// Individuals per region — a power of two, at most the shape's
      /// `popCapacity`. Two regions ping-pong, so the population region holds
      /// `2 * autoPop` gene records.
      autoPop: Expr
      autoGens: Expr
      /// The nine rates packed two 16-bit halves per word in `rateOrder`, so
      /// word `i` carries rates `2i` (low) and `2i+1` (high) — the entry
      /// record's own packing, minus the flags sharing word 7's high half.
      autoRates: Expr list
      autoSigma: Expr
      autoRange: Expr
      autoSeed: Expr list
      /// Op-list mode only: where the breed round streams its entries, whether
      /// this pass skips the score round, and whether the selection stream
      /// continues rather than reseeding.
      oplistBase: Expr option
      skipScore: Expr option
      rngContinue: Expr option }

/// The host-facing control bus: queue/population/ring geometry, the run pulse,
/// and the epoch case-load broadcast. `auto` is present exactly when the shape
/// carries the generation loop.
type GepClusterConfig =
    { startQueue: Expr
      queueBase: Expr
      popBase: Expr
      ringBase: Expr
      queueMask: Expr
      ringMask: Expr
      entriesPublished: Expr
      nCases: Expr
      ldCase: Expr
      caseAddr: Expr
      caseField: Expr
      caseData: Expr
      auto: GepAutoConfig option }

// One piece of machinery: the parameters size the datapath and the body
// elaborates it once.
/// The auto-mode generation loop. Code 2 was a pre-FIFO handshake step and
/// stayed gone, so the encoding has a hole in it — `machineCoded` keeps the
/// remaining four where they were rather than closing it.
[<RequireQualifiedAccess>]
type private Selection =
    | Idle
    /// Drawing tournaments and pushing finished entries into the FIFO.
    | Generate
    /// Waiting for the round's offspring to land before flipping the bank.
    | Barrier
    | Done

/// One filler's states. Code 0 is where a filler sits before the host starts
/// the pool — a real state, and one the hand-encoded form left unnamed because
/// a register initialised to 0 does not have to explain itself.
[<RequireQualifiedAccess>]
type private Filler =
    | Unstarted
    /// Available: bidding for the next work item.
    | Pick
    /// Fetching the work-item entry.
    | Entry
    | ParentA
    | SerializeA
    | ParentB
    | SerializeB
    /// Record complete — hand it to the breeder and go back to Pick.
    | StartBreeder

/// A breeder's occupancy, from the pool's side.
[<RequireQualifiedAccess>]
type private Occupancy =
    | Free
    /// Holding an offspring: the operator engine, compiler and serializer run.
    | Running
    /// Finished, waiting for the pack stage to drain it.
    | Waiting
    /// Being drained.
    | Packing

/// The emit stage: a finished offspring's four genome beats, then its ring
/// entry.
[<RequireQualifiedAccess>]
type private Emitter =
    | Idle
    | Genome
    | Ring

let gepClusterPool (shape: GepClusterShape) (prefix: string) (cfg: GepClusterConfig) =
    let nBreeders = shape.nBreeders
    let nLanes = shape.nLanes
    let nFillers = shape.nFillers
    let geneLen = shape.geneLen
    let constCount = shape.constCount
    let capacity = shape.capacity
    let addrWidth = shape.addrWidth

    if nBreeders < 1 then failwith $"need at least one breeder, got %d{nBreeders}"
    if nLanes < 1 then failwith $"need at least one lane, got %d{nLanes}"
    if nFillers < 1 then failwith $"need at least one filler, got %d{nFillers}"

    if nFillers > nBreeders then
        failwith $"more fillers (%d{nFillers}) than breeders (%d{nBreeders}) is wasteful"

    if geneLen > 33 || constCount > 4 then
        failwith "the gene record holds 33 symbols and 4 constants"

    if geneLen % 2 <> 1 then
        failwith $"a Karva gene is 2h+1 symbols long, got %d{geneLen}"

    // The generation loop and its host bus arrive together or not at all — a
    // pool cannot be told it selects for itself and then left without a seed.
    let auto =
        match shape.auto, cfg.auto with
        | Some s, Some c ->
            if s.popCapacity &&& (s.popCapacity - 1) <> 0 then
                failwith $"popCapacity must be a power of two, got %d{s.popCapacity}"

            if c.autoRates.Length <> (rateCount + 1) / 2 then
                failwith $"autoRates packs two rates per word, so it needs %d{(rateCount + 1) / 2} words"

            if c.autoSeed.Length <> 4 then
                failwith "the selection stream takes four pre-expanded seed words"

            if s.opList && (c.oplistBase.IsNone || c.skipScore.IsNone || c.rngContinue.IsNone) then
                failwith "op-list mode needs oplistBase, skipScore and rngContinue"

            Some(s, c)
        | None, None -> None
        | Some _, None -> failwith "the shape carries the generation loop but the config has no auto bus"
        | None, Some _ -> failwith "an auto bus was supplied to a pool with no generation loop"

    let opList =
        match auto with
        | Some (s, _) -> s.opList
        | None -> false

    // The breeder's line counter and the router's line index are 4 bits, so a
    // lane's individual bank is 16 lines = 64 words.
    let lineIdxW = 4
    let indivCapacity = 4 <<< lineIdxW
    let indivWords = gepUnitIndivWords capacity constCount

    if indivWords > indivCapacity then
        failwith $"the unit record (%d{indivWords} words) must fit a %d{indivCapacity}-word bank"

    let caseAddrW = log2Exact shape.caseCapacity
    let caseCountW = caseAddrW + 1
    let bW = bitsToHold nBreeders
    let lW = bitsToHold nLanes
    let fW = bitsToHold nFillers
    let slotW = lW + 1
    let nSlots = 2 * nLanes
    let threadW = log2Exact shape.nThreads
    let lastSymWord = 1 + (geneLen - 1) / 4
    // Gene-record words carrying payload; every other word emits as zero.
    let validWords = Set.ofList ([ 1..lastSymWord ] @ [ 10 .. 10 + constCount - 1 ])

    // Pack and emit states.
    let pIdle, pPack = 0UL, 1UL

    let one1 = lit 1UL 1
    let zero1 = lit 0UL 1
    let k (v: int) (w: int) = lit (uint64 v) w
    let zext (w: int) (x: Expr) = if width x >= w then x else cat (lit 0UL (w - width x)) x
    let le a b = bnot (lt b a)
    let ge a b = bnot (lt a b)

    let entDepth = 4
    let entAddrW = 2

    // ---- Config latch ----
    let queueBaseR = reg $"{prefix}_queueBase" addrWidth
    let popBaseR = reg $"{prefix}_popBase" addrWidth
    let ringBaseR = reg $"{prefix}_ringBase" addrWidth
    let queueMaskR = reg $"{prefix}_queueMask" 32
    let ringMaskR = reg $"{prefix}_ringMask" 32
    let running = regBit $"{prefix}_running"

    // ---- The generation loop (auto mode) ----
    // A host-seeded xoshiro stream drives tournament draws and per-offspring
    // breeding seeds; a DOUBLE-BUFFERED fitness table (read bank = the previous
    // generation, write bank = this one, flipped at each barrier) keeps
    // selection bit-exactly mirrorable however lane completion interleaves; the
    // elite is the deterministic argmin, lowest index on ties. Queue mode is
    // untouched — auto mode only replaces entry ACQUISITION.
    //
    // The FSM runs AHEAD of dispatch, pushing finished entries into a small FIFO
    // a filler pops in one cycle, so entry generation (~14 cycles) never
    // serializes with fetch and serialize. Draw order, and therefore what a
    // software mirror must reproduce, is unaffected.
    let autoState =
        auto
        |> Option.map (fun (ashape, ac) ->
            let fitCapW = log2Exact ashape.popCapacity
            let selSt =
                machineCoded
                    $"{prefix}_selSt"
                    [ Selection.Idle, 0UL
                      Selection.Generate, 1UL
                      Selection.Barrier, 3UL
                      Selection.Done, 4UL ]
            let aPhase = regBit $"{prefix}_aPhase" // 0 = SCORE, 1 = BREED
            let aRound = reg $"{prefix}_aRound" 32
            let aIdx = reg $"{prefix}_aIdx" 16
            let aBaseFlag = regBit $"{prefix}_aBaseFlag"
            let selK = reg $"{prefix}_selK" 4
            let drawA = reg $"{prefix}_drawA" 16
            let drawB = reg $"{prefix}_drawB" 16
            let tFitA = reg $"{prefix}_tFitA" 64
            let selPA = reg $"{prefix}_selPA" 16
            let selPB = reg $"{prefix}_selPB" 16
            let selSeed = [ for i in 0..3 -> reg $"{prefix}_selSeed%d{i}" 32 ]
            let bestFit = reg $"{prefix}_bestFit" 64
            let bestIdx = reg $"{prefix}_bestIdx" 16
            let eliteIdx = reg $"{prefix}_eliteIdx" 16
            let lastBestFit = reg $"{prefix}_lastBestFit" 64
            let lastBestIdx = reg $"{prefix}_lastBestIdx" 16
            let roundTarget = reg $"{prefix}_roundTarget" 32
            let writeBank = regBit $"{prefix}_writeBank"

            let allOnes64 = wire $"{prefix}_allOnes64" 64
            (lit 0UL 64 - lit 1UL 64) ==> allOnes64

            // Op-list mode single-buffers the table: the host serializes the
            // emit and breed passes, so nothing reads and writes it at once and
            // the barrier's `writeBank := readBank` flip is a no-op.
            let readBank = wireBit $"{prefix}_readBank"
            (if ashape.opList then zero1 else bnot writeBank) ==> readBank
            let fitTable = blockMem $"{prefix}_fitTable" (fitCapW + 1) 64

            let entParA = distributedMem $"{prefix}_entParA" entAddrW 16
            let entParB = distributedMem $"{prefix}_entParB" entAddrW 16
            let entDest = distributedMem $"{prefix}_entDest" entAddrW 16
            let entId = distributedMem $"{prefix}_entId" entAddrW 16
            let entSkip = distributedMem $"{prefix}_entSkip" entAddrW 1
            let entZero = distributedMem $"{prefix}_entZero" entAddrW 1
            let entSeed = [ for i in 0..3 -> distributedMem $"{prefix}_entSeed%d{i}" entAddrW 32 ]
            let entWrP = reg $"{prefix}_entWrP" entAddrW
            let entRdP = reg $"{prefix}_entRdP" entAddrW
            let entCnt = reg $"{prefix}_entCnt" 3
            let entPush = wireBit $"{prefix}_entPush"
            let entPop = wireBit $"{prefix}_entPop"

            let oplistBaseR =
                if ashape.opList then Some(reg $"{prefix}_oplistBase" addrWidth) else None

            let opListActiveRound =
                if ashape.opList then Some(wireBit $"{prefix}_opListActiveRound") else None

            let olPop = if ashape.opList then Some(wireBit $"{prefix}_olPop") else None
            let olEmitted = if ashape.opList then Some(reg $"{prefix}_olEmitted" 32) else None

            // The slice rule: a rate word must be a named signal before its
            // halves can be taken, and a caller may hand us any expression.
            let rateWords =
                ac.autoRates
                |> List.mapi (fun i w ->
                    let r = wire $"{prefix}_autoRate%d{i}" 32
                    w ==> r
                    r)

            let rateHalf (i: int) =
                if i % 2 = 0 then slice 15 0 rateWords[i / 2] else slice 31 16 rateWords[i / 2]

            let popMask = wire $"{prefix}_popMask" 16
            (ac.autoPop - lit 1UL 16) ==> popMask
            // Score entries and the breed round's elite draw with zero rates.
            let zeroRatesSel = wireBit $"{prefix}_zeroRatesSel"
            (bnot aPhase ||| eq aIdx (lit 0UL 16)) ==> zeroRatesSel
            let shortEntry = zeroRatesSel

            // Short entries need only four seed words; a tournament entry draws
            // four index words and four seed words with two sync-read fitness
            // lookups per parent in between. The RNG steps on exactly the draw
            // cycles, and pauses on the push cycle.
            let selStep = wireBit $"{prefix}_selStep"

            (selSt.Is Selection.Generate
                 &&& mux
                         shortEntry
                         (le selK (k 3 4))
                         (le selK (k 1 4)
                          ||| eq selK (k 4 4)
                          ||| eq selK (k 5 4)
                          ||| (ge selK (k 8 4) &&& le selK (k 11 4))))
            ==> selStep

            // `rngContinue` holds the stream across passes: seed only at
            // generation 0, so a later pass's draws continue where the previous
            // emit left off.
            let selLoad =
                match ac.rngContinue with
                | Some rc -> cfg.startQueue &&& ac.autoMode &&& bnot rc
                | None -> cfg.startQueue &&& ac.autoMode

            let selWord = wire $"{prefix}_selWord" 32

            instanceNamed $"{prefix}_selRng" (xoshiro128pp "ClusterSelXoshiro128pp") selLoad ac.autoSeed selStep
            ==> selWord

            let selDrawIdx = wire $"{prefix}_selDrawIdx" 16
            (slice 15 0 selWord &&& popMask) ==> selDrawIdx
            let tourIdx = wire $"{prefix}_tourIdx" 16
            mux (eq selK (k 1 4) ||| eq selK (k 5 4)) drawA drawB ==> tourIdx
            let fitRdAddr = wire $"{prefix}_fitRdAddr" (fitCapW + 1)
            cat readBank (slice (fitCapW - 1) 0 tourIdx) ==> fitRdAddr
            let selFitOut = (memReadPort fitTable fitRdAddr).data

            let aBase = wire $"{prefix}_aBase" 16
            mux aBaseFlag ac.autoPop (lit 0UL 16) ==> aBase
            let aOther = wire $"{prefix}_aOther" 16
            mux aBaseFlag (lit 0UL 16) ac.autoPop ==> aOther
            let autoDest = wire $"{prefix}_autoDest" 16
            mux aPhase (aOther + aIdx) (lit 0UL 16) ==> autoDest

            // All-zero state is xoshiro's one degenerate point; mirror the
            // SplitMix64 expander's guard and force s0 = 1.
            let seedZero = wireBit $"{prefix}_seedZero"

            (selSeed |> List.map (fun s -> eq s (lit 0UL 32)) |> List.reduce (&&&))
            ==> seedZero

            let selSeed0G = wire $"{prefix}_selSeed0G" 32
            mux seedZero (lit 1UL 32) selSeed[0] ==> selSeed0G

            If (selSt.Is Selection.Generate) (fun () ->
                If shortEntry (fun () ->
                    If (eq selK (k 0 4)) (fun () ->
                        If aPhase (fun () ->
                            eliteIdx ==> selPA
                            eliteIdx ==> selPB)

                        Else (fun () ->
                            aIdx ==> selPA
                            aIdx ==> selPB))

                    for i in 0..3 do
                        If (eq selK (k i 4)) (fun () -> selWord ==> selSeed[i])

                    If (le selK (k 3 4)) (fun () -> selK + lit 1UL 4 ==> selK))

                Else (fun () ->
                    If (eq selK (k 0 4)) (fun () ->
                        selDrawIdx ==> drawA
                        k 1 4 ==> selK)

                    If (eq selK (k 1 4)) (fun () ->
                        selDrawIdx ==> drawB
                        k 2 4 ==> selK)

                    If (eq selK (k 2 4)) (fun () ->
                        selFitOut ==> tFitA
                        k 3 4 ==> selK)

                    If (eq selK (k 3 4)) (fun () ->
                        mux (le tFitA selFitOut) drawA drawB ==> selPA
                        k 4 4 ==> selK)

                    If (eq selK (k 4 4)) (fun () ->
                        selDrawIdx ==> drawA
                        k 5 4 ==> selK)

                    If (eq selK (k 5 4)) (fun () ->
                        selDrawIdx ==> drawB
                        k 6 4 ==> selK)

                    If (eq selK (k 6 4)) (fun () ->
                        selFitOut ==> tFitA
                        k 7 4 ==> selK)

                    If (eq selK (k 7 4)) (fun () ->
                        mux (le tFitA selFitOut) drawA drawB ==> selPB
                        k 8 4 ==> selK)

                    for i in 0..3 do
                        If (eq selK (k (8 + i) 4)) (fun () -> selWord ==> selSeed[i])

                    If (ge selK (k 8 4) &&& le selK (k 11 4)) (fun () -> selK + lit 1UL 4 ==> selK)))

            // The push step holds — RNG paused — until the FIFO has room, then
            // the entry lands and aIdx advances (or the round enters its barrier).
            let atPush = wireBit $"{prefix}_atPush"

            (selSt.Is Selection.Generate
                 &&& mux shortEntry (eq selK (k 4 4)) (eq selK (k 12 4)))
            ==> atPush

            (atPush &&& bnot (eq entCnt (k entDepth 3))) ==> entPush

            If entPush (fun () ->
                memWrite entParA entWrP (aBase + selPA) one1
                memWrite entParB entWrP (aBase + selPB) one1
                memWrite entDest entWrP autoDest one1
                memWrite entId entWrP aIdx one1
                memWrite entSkip entWrP (bnot aPhase) one1
                memWrite entZero entWrP zeroRatesSel one1
                memWrite entSeed[0] entWrP selSeed0G one1

                for i in 1..3 do
                    memWrite entSeed[i] entWrP selSeed[i] one1

                entWrP + lit 1UL entAddrW ==> entWrP
                k 0 4 ==> selK

                If (eq aIdx (ac.autoPop - lit 1UL 16)) (fun () -> selSt.Goto Selection.Barrier)
                Else (fun () -> aIdx + lit 1UL 16 ==> aIdx))

            // Push and pop may coincide; start overrides both.
            (entCnt + zext 3 entPush - zext 3 entPop) ==> entCnt

            {| cfg = ac
               fitCapW = fitCapW
               fitTable = fitTable
               writeBank = writeBank
               readBank = readBank
               allOnes64 = allOnes64
               selSt = selSt
               selK = selK
               aPhase = aPhase
               aRound = aRound
               aIdx = aIdx
               aBaseFlag = aBaseFlag
               bestFit = bestFit
               bestIdx = bestIdx
               eliteIdx = eliteIdx
               lastBestFit = lastBestFit
               lastBestIdx = lastBestIdx
               roundTarget = roundTarget
               selSeed = selSeed
               entParA = entParA
               entParB = entParB
               entDest = entDest
               entId = entId
               entSkip = entSkip
               entZero = entZero
               entSeed = entSeed
               entWrP = entWrP
               entRdP = entRdP
               entCnt = entCnt
               entPush = entPush
               entPop = entPop
               rateHalf = rateHalf
               rateWords = rateWords
               oplistBaseR = oplistBaseR
               opListActiveRound = opListActiveRound
               olPop = olPop
               olEmitted = olEmitted |})

    // ---- Per-breeder entry registers ----
    let brSt =
        [ for b in 0 .. nBreeders - 1 ->
              machine $"{prefix}_brSt%d{b}" [ Occupancy.Free; Occupancy.Running; Occupancy.Waiting; Occupancy.Packing ] ]
    let destB = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_destB%d{b}" 32 ]
    let entryIdB = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_entryIdB%d{b}" 32 ]
    let flagsB = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_flagsB%d{b}" 16 ]
    let sigmaB = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_sigmaB%d{b}" 32 ]
    let rangeB = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_rangeB%d{b}" 32 ]

    let seedB =
        [ for b in 0 .. nBreeders - 1 -> [ for i in 0..3 -> reg $"{prefix}_seedB%d{b}_%d{i}" 32 ] ]

    let ratesB =
        [ for b in 0 .. nBreeders - 1 -> [ for i in 0 .. rateCount - 1 -> reg $"{prefix}_rateB%d{b}_%d{i}" 16 ] ]

    // ---- Pack FSM state, declared here because the breeders read the genome
    // back through `wbK` ----
    let pstate = regInit $"{prefix}_pstate" 1 pIdle
    let pSel = reg $"{prefix}_pSel" bW
    let pSlot = reg $"{prefix}_pSlot" slotW
    let wbK = reg $"{prefix}_wbK" 6
    let wordAcc = reg $"{prefix}_wordAcc" 32

    // ---- Filler state. `fn` drops the suffix at one filler, so the single-
    // filler elaboration reads as the plain dispatcher it is. ----
    let fn (stem: string) (f: int) =
        if nFillers = 1 then $"{prefix}_{stem}" else $"{prefix}_{stem}%d{f}"

    let dstate =
        [ for f in 0 .. nFillers - 1 ->
              machine
                  (fn "dstate" f)
                  [ Filler.Unstarted
                    Filler.Pick
                    Filler.Entry
                    Filler.ParentA
                    Filler.SerializeA
                    Filler.ParentB
                    Filler.SerializeB
                    Filler.StartBreeder ] ]
    // fillSel[b]: breeder b is mid-fill — set at claim, cleared at Filler.StartBreeder, so a
    // free-breeder pick cannot re-grab it before its state flips to RUN.
    let fillSel = [ for b in 0 .. nBreeders - 1 -> regBit $"{prefix}_fillSel%d{b}" ]
    let fillBr = [ for f in 0 .. nFillers - 1 -> reg (fn "fillBr" f) bW ]
    let slotPtr = [ for f in 0 .. nFillers - 1 -> reg (fn "slotPtr" f) 32 ]
    // The shared CLAIM pointer: with several fills in flight a completion
    // pointer could not address them, so `entries_taken` reports claimed.
    let entryPtr = reg $"{prefix}_entryPtr" 32
    // The parent SLOT indices, read from the entry (queue mode) or popped from
    // the pre-generation FIFO (auto mode). An inline-parent pool with no
    // generation loop never reads them — parents ride the entry's own burst —
    // so it does not declare them; auto mode always fetches parents by slot, so
    // the moment the loop is present they are back.
    let needsParentSlots = not shape.inlineParents || auto.IsSome

    let parReg =
        [ for f in 0 .. nFillers - 1 ->
              (if needsParentSlots then
                   Some(reg (fn "parAReg" f) 32, reg (fn "parBReg" f) 32)
               else
                   None) ]
    let reqSent = [ for f in 0 .. nFillers - 1 -> regBit (fn "reqSent" f) ]
    // Wide enough for the 12-beat single burst, not just a 4-beat record.
    let beatCtr = [ for f in 0 .. nFillers - 1 -> reg (fn "beatCtr" f) 4 ]
    let serCtr = [ for f in 0 .. nFillers - 1 -> reg (fn "serCtr" f) 6 ]
    // The read master's owner grant: one filler holds it from its burst issue
    // through the 4th beat.
    let busyOwnerValid = regBit $"{prefix}_busyOwnerValid"
    let busyOwnerId = reg $"{prefix}_busyOwnerId" fW
    let lastRd = reg $"{prefix}_lastRd" fW

    // ---- Per-filler fetch addressing ----
    let entrySlot =
        [ for f in 0 .. nFillers - 1 ->
              let w = wire (fn "entrySlot" f) 32
              (slotPtr[f] &&& queueMaskR) ==> w
              w ]

    let entryAddr =
        [ for f in 0 .. nFillers - 1 ->
              let w = wire (fn "entryAddr" f) addrWidth

              if shape.inlineParents then
                  // The stride is a padded 256 B, so the address is a shift and
                  // the 12-beat burst never crosses a 4 KB boundary.
                  (queueBaseR + cat (slice (addrWidth - 9) 0 entrySlot[f]) (lit 0UL 8)) ==> w
              else
                  (queueBaseR + cat (slice (addrWidth - 7) 0 entrySlot[f]) (lit 0UL 6)) ==> w

              w ]

    let parentAddr (f: int) (second: bool) =
        let w = wire (fn (if second then "parBAddr" else "parAAddr") f) addrWidth
        let inlineOff = (if second then 2 else 1) * gepRecordWords * 4

        // Auto mode always fetches the parent from its population slot; only
        // queue mode can have it inline, because only queue mode has a host
        // marshalling the work item.
        match parReg[f] with
        | Some (parA, parB) ->
            let bySlot = popBaseR + cat (slice (addrWidth - 7) 0 (if second then parB else parA)) (lit 0UL 6)

            if shape.inlineParents then
                match auto with
                | Some (_, ac) -> mux ac.autoMode bySlot (entryAddr[f] + k inlineOff addrWidth) ==> w
                | None -> (entryAddr[f] + k inlineOff addrWidth) ==> w
            else
                bySlot ==> w
        | None -> (entryAddr[f] + k inlineOff addrWidth) ==> w

        w

    let parAAddr = [ for f in 0 .. nFillers - 1 -> parentAddr f false ]
    let parBAddr = [ for f in 0 .. nFillers - 1 -> parentAddr f true ]

    let inD (f: int) (state: Filler) = dstate[f].Is state

    // Every filler's machine shares one encoding, so a code can be read off any
    // of them — which is what lets a muxed copy of "whichever filler won the
    // grant" be compared against a state.
    let holdsD (value: Expr) (state: Filler) = dstate[0].Holds value state

    // ONE burst per work item instead of three. The entry and both parent
    // records are contiguous, so a single 12-beat read replaces three 4-beat
    // ones and pays the DDR round trip once rather than three times — the
    // binding cost on this path (measured: 116 -> the rDelay-16 figure).
    // Auto mode still fetches parents by population slot, so it keeps the
    // three-burst path; the mode is a runtime decision there.
    let singleBurst = wireBit $"{prefix}_singleBurst"

    (if not shape.inlineParents then
             zero1
         else
             match auto with
             | Some (_, ac) -> bnot ac.autoMode
             | None -> one1)
    ==> singleBurst

    let lastBeat = wire $"{prefix}_lastBeat" 4
    mux singleBurst (k (gepWorkItemBeats - 1) 4) (k 3 4) ==> lastBeat

    // Auto mode takes the entry from the FIFO in one cycle, so Filler.Entry issues no
    // DDR request there. Parent states always fetch.
    let fetching =
        [ for f in 0 .. nFillers - 1 ->
              let w = wireBit (fn "fetching" f)

              let entryFetch =
                  match autoState with
                  | Some a -> inD f Filler.Entry &&& bnot a.cfg.autoMode
                  | None -> inD f Filler.Entry

              (entryFetch ||| inD f Filler.ParentA ||| inD f Filler.ParentB) ==> w
              w ]

    let reqPending =
        [ for f in 0 .. nFillers - 1 ->
              let w = wireBit (fn "reqPending" f)
              (fetching[f] &&& bnot reqSent[f]) ==> w
              w ]

    let reqAddr =
        [ for f in 0 .. nFillers - 1 ->
              let w = wire (fn "reqAddr" f) addrWidth
              mux (inD f Filler.Entry) entryAddr[f] (mux (inD f Filler.ParentA) parAAddr[f] parBAddr[f]) ==> w
              w ]

    // ---- The wormhole over the shared read master: a held burst keeps its
    // owner, otherwise the lowest fetcher at or after lastRd+1 wins this cycle
    // (combinational, so one filler issues with no added latency). ----
    let rrBase = wire $"{prefix}_rrBase" fW

    (if nFillers = 1 then
             lit 0UL fW
         else
             mux (eq lastRd (k (nFillers - 1) fW)) (lit 0UL fW) (lastRd + lit 1UL fW))
    ==> rrBase

    let freshValid, freshId = roundRobinPick fetching rrBase fW
    let grantValid = wireBit $"{prefix}_grantValid"
    mux busyOwnerValid one1 freshValid ==> grantValid
    let grantId = wire $"{prefix}_grantId" fW
    mux busyOwnerValid busyOwnerId freshId ==> grantId

    let byId (sel: Expr) (per: int -> Expr) =
        selectIndexed sel [ for f in 0 .. nFillers - 1 -> per f ]

    let ownReqPending = wireBit $"{prefix}_ownReqPending"
    byId grantId (fun f -> reqPending[f]) ==> ownReqPending
    let ownReqAddr = wire $"{prefix}_ownReqAddr" addrWidth
    byId grantId (fun f -> reqAddr[f]) ==> ownReqAddr
    let reqValid = wireBit $"{prefix}_reqValid"
    (grantValid &&& ownReqPending) ==> reqValid
    let reqReady = wireBit $"{prefix}_reqReady"
    registerStreamReady reqReady

    let resp =
        axiMasterReaderBurst
            addrWidth
            128
            shape.readOutstanding
            16
            { payload = (ownReqAddr, mux singleBurst (k (gepWorkItemBeats - 1) 8) (k 3 8))
              valid = reqValid
              ready = reqReady
              layout = layout2 ("addr", addrWidth) ("len", 8) }

    let respFlow = streamToFlow resp
    let respFire = wireBit $"{prefix}_respFire"
    respFlow.valid ==> respFire
    let beatIn = wire $"{prefix}_beatIn" 128
    fst respFlow.payload ==> beatIn

    let reqFire = wireBit $"{prefix}_reqFire"
    (reqValid &&& reqReady) ==> reqFire

    for f in 0 .. nFillers - 1 do
        If (reqFire &&& eq grantId (k f fW)) (fun () -> one1 ==> reqSent[f])

    If reqFire (fun () ->
        one1 ==> busyOwnerValid
        grantId ==> busyOwnerId)

    // Beats belong to the granted owner; `cur*` project its state.
    let curDstate = wire $"{prefix}_curDstate" 3
    byId grantId (fun f -> dstate[f].Value) ==> curDstate
    let curBeat = wire $"{prefix}_curBeat" 4
    byId grantId (fun f -> beatCtr[f]) ==> curBeat
    let curBr = wire $"{prefix}_curBr" bW
    byId grantId (fun f -> fillBr[f]) ==> curBr

    /// Per breeder, whichever filler owns it — the serializer signals reach the
    /// breeder through this, lowest filler first.
    let ownerMux (b: int) (per: int -> Expr) =
        if nFillers = 1 then
            per 0
        else
            List.fold
                (fun acc f -> mux (eq fillBr[f] (k b bW)) (per f) acc)
                (per (nFillers - 1))
                [ nFillers - 2 .. -1 .. 0 ]

    // ---- Entry landing: the 4 beats into the owner's breeder registers ----
    for b in 0 .. nBreeders - 1 do
        If (holdsD curDstate Filler.Entry &&& respFire &&& eq curBr (k b bW)) (fun () ->
            If (eq curBeat (k 0 4)) (fun () ->
                slice 95 64 beatIn ==> destB[b]
                slice 111 96 beatIn ==> ratesB[b][0]
                slice 127 112 beatIn ==> ratesB[b][1])

            If (eq curBeat (k 1 4)) (fun () ->
                // Word 4 onward: two rate halves per word, then flags.
                for i in 2 .. rateCount - 1 do
                    slice ((i - 2) * 16 + 15) ((i - 2) * 16) beatIn ==> ratesB[b][i]

                slice 127 112 beatIn ==> flagsB[b])

            If (eq curBeat (k 2 4)) (fun () ->
                slice 31 0 beatIn ==> sigmaB[b]
                slice 63 32 beatIn ==> rangeB[b]
                slice 95 64 beatIn ==> entryIdB[b])

            If (eq curBeat (k 3 4)) (fun () ->
                for i in 0..3 do
                    slice (32 * i + 31) (32 * i) beatIn ==> seedB[b][i]))

    for f in 0 .. nFillers - 1 do
        match parReg[f] with
        | Some (parA, parB) ->
            If (inD f Filler.Entry &&& respFire &&& eq grantId (k f fW) &&& eq beatCtr[f] (k 0 4)) (fun () ->
                slice 31 0 beatIn ==> parA
                slice 63 32 beatIn ==> parB)
        | None -> ()

    // ---- Auto mode: the "entry" pops from the pre-generation FIFO in one
    // cycle. At most one filler is in Filler.Entry-auto per cycle (a claim is one per
    // cycle and Filler.Entry-auto lasts one), so the single pop stays coherent. ----
    let autoTake =
        autoState
        |> Option.map (fun a ->
            let anyTakeE, takeFE =
                priorityPick
                    [ for f in 0 .. nFillers - 1 -> inD f Filler.Entry &&& a.cfg.autoMode ]
                    [ [ for f in 0 .. nFillers - 1 -> k f fW ] ]

            let take = wireBit $"{prefix}_autoTake"
            anyTakeE ==> take
            let autoF = wire $"{prefix}_autoF" fW
            takeFE[0] ==> autoF
            let autoBr = wire $"{prefix}_autoBr" bW
            byId autoF (fun f -> fillBr[f]) ==> autoBr

            // The op-list writer drains the FIFO in the breed round, the
            // dispatcher in the score round — mutually exclusive, because the
            // op-list round gates the dispatcher off — so the FIFO advances
            // exactly once per entry either way.
            (match a.olPop with
                 | Some p -> take ||| p
                 | None -> take)
            ==> a.entPop

            If take (fun () ->
                for b in 0 .. nBreeders - 1 do
                    If (eq autoBr (k b bW)) (fun () ->
                        zext 32 (memRead a.entDest a.entRdP) ==> destB[b]
                        zext 32 (memRead a.entId a.entRdP) ==> entryIdB[b]

                        mux
                                (memRead a.entSkip a.entRdP)
                                (k gepFlagSkipWriteback 16)
                                (lit 0UL 16)
                        ==> flagsB[b]

                        a.cfg.autoSigma ==> sigmaB[b]
                        a.cfg.autoRange ==> rangeB[b]

                        for i in 0 .. rateCount - 1 do
                            mux (memRead a.entZero a.entRdP) (lit 0UL 16) (a.rateHalf i)
                            ==> ratesB[b][i]

                        for i in 0..3 do
                            memRead a.entSeed[i] a.entRdP ==> seedB[b][i])

                a.entRdP + lit 1UL entAddrW ==> a.entRdP)

            for f in 0 .. nFillers - 1 do
                If (take &&& eq autoF (k f fW)) (fun () ->
                    match parReg[f] with
                    | Some (parA, parB) ->
                        zext 32 (memRead a.entParA a.entRdP) ==> parA
                        zext 32 (memRead a.entParB a.entRdP) ==> parB
                    | None -> failwith "auto mode fetches parents by slot — the slot registers must exist"

                    dstate[f].Goto Filler.ParentA
                    k 0 4 ==> beatCtr[f]
                    zero1 ==> reqSent[f]) // start the DDR fetch straight away

            take)

    // ---- Parent staging: 16 words landed 4-per-beat, drained by the
    // serializer. One 4 x u32 bank per filler; only the granted owner writes. ----
    // Eight entries, not four: the single burst delivers BOTH parents before
    // either is serialized, so A lands in 0-3 and B in 4-7. The three-burst path
    // uses the same halves, which is what lets the serializer stay mode-blind.
    let stg =
        [ for f in 0 .. nFillers - 1 -> [ for j in 0..3 -> distributedMem (fn $"stg%d{j}" f) 3 32 ] ]

    for f in 0 .. nFillers - 1 do
        let landing = wireBit (fn "parLanding" f)

        (respFire
             &&& eq grantId (k f fW)
             &&& (inD f Filler.ParentA
                  ||| inD f Filler.ParentB
                  ||| (singleBurst &&& inD f Filler.Entry &&& bnot (lt beatCtr[f] (k 4 4)))))
        ==> landing

        // Single burst: beats 4..11 are the two records back to back, so the
        // slot is beat - 4. Three bursts: parent A's four beats then parent B's.
        let parBeat = wire (fn "parBeat" f) 4
        (beatCtr[f] - k 4 4) ==> parBeat
        let stgIdx = wire (fn "stgIdx" f) 3

        mux singleBurst (slice 2 0 parBeat) (cat (inD f Filler.ParentB) (slice 1 0 beatCtr[f]))
        ==> stgIdx

        for j in 0..3 do
            memWrite (stg[f][j]) stgIdx (slice (32 * j + 31) (32 * j) beatIn) landing

    // ---- Serializer: symbol i is record byte 4+i, constant c is word 10+c.
    // One per filler, all running in parallel into their own breeders. ----
    let symPhase = [ for f in 0 .. nFillers - 1 -> wireBit (fn "symPhase" f) ]
    let constIdx = [ for f in 0 .. nFillers - 1 -> wire (fn "constIdx" f) 6 ]
    let serByte = [ for f in 0 .. nFillers - 1 -> wire (fn "serByte" f) 8 ]
    let serWord = [ for f in 0 .. nFillers - 1 -> wire (fn "serWord" f) 32 ]
    let serializing = [ for f in 0 .. nFillers - 1 -> wireBit (fn "serializing" f) ]

    for f in 0 .. nFillers - 1 do
        lt serCtr[f] (k geneLen 6) ==> symPhase[f]
        (serCtr[f] - k geneLen 6) ==> constIdx[f]
        let constOff = wire (fn "constOff" f) 6
        (cat (lit 0UL 2) (cat (slice 1 0 constIdx[f]) (lit 0UL 2)) + k 40 6) ==> constOff
        let bytePos = wire (fn "bytePos" f) 6
        mux symPhase[f] (serCtr[f] + k 4 6) constOff ==> bytePos
        let beatSel = wire (fn "beatSel" f) 3
        cat (inD f Filler.SerializeB) (slice 5 4 bytePos) ==> beatSel
        let wordSel = wire (fn "wordSel" f) 2
        slice 3 2 bytePos ==> wordSel
        let byteSel = wire (fn "byteSel" f) 2
        slice 1 0 bytePos ==> byteSel
        selectIndexed wordSel [ for j in 0..3 -> memRead (stg[f][j]) beatSel ] ==> serWord[f]
        selectIndexed byteSel [ for j in 0..3 -> slice (8 * j + 7) (8 * j) (serWord[f]) ] ==> serByte[f]
        (inD f Filler.SerializeA ||| inD f Filler.SerializeB) ==> serializing[f]

    // ---- Breeders. `rel` is declared first: the pack FSM that drives it reads
    // the breeders' child ports, so the two would otherwise be a cycle. ----
    let relW = [ for b in 0 .. nBreeders - 1 -> wireBit $"{prefix}_rel%d{b}" ]

    let breeders =
        [ for b in 0 .. nBreeders - 1 ->
              let obSer = ownerMux b (fun f -> serializing[f])
              let obSym = ownerMux b (fun f -> symPhase[f])
              let obDst = ownerMux b (fun f -> dstate[f].Value)

              let load =
                  { ldSym = obSer &&& fillSel[b] &&& obSym
                    ldPar = holdsD obDst Filler.SerializeB
                    ldAddr = mux obSym (ownerMux b (fun f -> serCtr[f])) (ownerMux b (fun f -> constIdx[f]))
                    ldSdata = ownerMux b (fun f -> serByte[f])
                    ldConst = obSer &&& fillSel[b] &&& bnot obSym
                    ldCdata = ownerMux b (fun f -> serWord[f]) }

              let hi (i: int) = cat (ratesB[b][i]) (lit 0UL 16)

              let rates =
                  { mutation = hi 0
                    constReplace = hi 1
                    creep = hi 2
                    inversion = hi 3
                    isTrans = hi 4
                    risTrans = hi 5
                    onePoint = hi 6
                    twoPoint = hi 7
                    geneRecomb = hi 8
                    sigmaFx = sigmaB[b]
                    rangeFx = rangeB[b] }

              gepBreederBlock
                  shape.functionSet
                  shape.terminalSet
                  geneLen
                  shape.headLen
                  constCount
                  capacity
                  $"{prefix}_br%d{b}"
                  (holdsD obDst Filler.StartBreeder &&& fillSel[b])
                  relW[b]
                  seedB[b]
                  rates
                  load
                  wbK
                  (wbK - k geneLen 6) ]

    // A breeder whose record has fully streamed waits for the pack FSM.
    for b in 0 .. nBreeders - 1 do
        If (brSt[b].Is Occupancy.Running &&& breeders[b].finished) (fun () -> brSt[b].Goto Occupancy.Waiting)

    // ---- Lanes. The fill bus is declared first: the router needs each lane's
    // `can_fill` and each lane needs the router's fill bus. ----
    let laneFillW =
        [ for l in 0 .. nLanes - 1 ->
              { beat = wire $"{prefix}_l%d{l}_fillBeat" 128
                indivEn = wireBit $"{prefix}_l%d{l}_fillEn"
                indivAddr = wire $"{prefix}_l%d{l}_fillAddr" lineIdxW
                commit = wireBit $"{prefix}_l%d{l}_fillCommit"
                unitId = wire $"{prefix}_l%d{l}_fillUnit" 32 } ]

    let laneParts =
        [ for l in 0 .. nLanes - 1 ->
              // Registered-broadcast leaves: the epoch case bus and n_cases
              // reach each lane through its own register stage.
              let ldCaseR = regBit $"{prefix}_l%d{l}_ldCase"
              cfg.ldCase ==> ldCaseR
              let caseAddrR = reg $"{prefix}_l%d{l}_caseAddr" caseAddrW
              cfg.caseAddr ==> caseAddrR
              let caseFieldR = reg $"{prefix}_l%d{l}_caseField" 8
              cfg.caseField ==> caseFieldR
              let caseDataR = reg $"{prefix}_l%d{l}_caseData" 32
              cfg.caseData ==> caseDataR
              let nCasesR = reg $"{prefix}_l%d{l}_nCases" caseCountW
              cfg.nCases ==> nCasesR

              let laneDiv =
                  match shape.divide with
                  | None -> NoDiv
                  | Some PerLane -> ResidentDiv
                  | Some Pooled ->
                      // The pod's writeback is what the lane consumes and what
                      // the lane's issues produce, so one end is declared here
                      // and driven once the pod exists.
                      let wbTag = wire $"{prefix}_l%d{l}_wbTag" threadW
                      let wbQ = wire $"{prefix}_l%d{l}_wbQ" 32
                      let wbValid = wireBit $"{prefix}_l%d{l}_wbValid"
                      let wbReady = wireBit $"{prefix}_l%d{l}_wbReady"
                      registerStreamReady wbReady

                      PodDiv
                          { payload = { tag = wbTag; fields = [ wbQ ] }
                            valid = wbValid
                            ready = wbReady
                            layout = fuLayout threadW gepDivWritebackPorts }

              let engine =
                  gepUnitEngine
                      capacity
                      constCount
                      shape.varCount
                      shape.nThreads
                      shape.caseCapacity
                      indivCapacity
                      laneDiv
                      $"{prefix}_lane%d{l}"
                      laneFillW[l]
                      (ResidentCases(ldCaseR, caseAddrR, caseFieldR, caseDataR))
                      nCasesR
                      (k 1 8) // single-record units: res_unit is the entry id

              engine, laneDiv ]

    let lanes = [ for engine, _ in laneParts -> engine ]

    match shape.divide with
    | Some Pooled ->
        let podOut = gepDivPod $"{prefix}_divpod" [ for e in lanes -> e.divIssue.Value ]

        List.iter2
            (fun (_, d) out ->
                match d with
                | PodDiv wb -> streamExport wb.payload wb.valid wb.ready out
                | NoDiv
                | ResidentDiv -> ())
            laneParts
            podOut
    | Some PerLane
    | None -> ()

    // ---- Router ----
    let router =
        gepRecordRouter
            nLanes
            lineIdxW
            $"{prefix}_rr"
            [ for b in breeders -> b.rec_ ]
            entryIdB
            [ for e in lanes -> e.canFill ]

    for l in 0 .. nLanes - 1 do
        router.laneFills[l].beat ==> laneFillW[l].beat
        router.laneFills[l].indivEn ==> laneFillW[l].indivEn
        router.laneFills[l].indivAddr ==> laneFillW[l].indivAddr
        router.laneFills[l].commit ==> laneFillW[l].commit
        router.laneFills[l].unitId ==> laneFillW[l].unitId

    // ---- Lane-bank mirrors: which bank the next fill on lane l lands in, and
    // which bank the next result comes from. Deliberately not reset by start —
    // they shadow engine state that start does not touch either. ----
    let fillBankM = [ for l in 0 .. nLanes - 1 -> regBit $"{prefix}_fillBankM%d{l}" ]
    let resBankM = [ for l in 0 .. nLanes - 1 -> regBit $"{prefix}_resBankM%d{l}" ]

    let fillBankNext =
        [ for l in 0 .. nLanes - 1 ->
              let w = wireBit $"{prefix}_fillBankNext%d{l}"
              mux laneFillW[l].commit (bnot fillBankM[l]) fillBankM[l] ==> w
              w ]

    for l in 0 .. nLanes - 1 do
        fillBankNext[l] ==> fillBankM[l]

    // Grant tap: remember which (lane, bank) breeder b's record streams into.
    // fillBankNext, not the mirror, so a grant coinciding with the previous
    // stream's commit pulse sees the post-flip bank.
    let slotB = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_slotB%d{b}" slotW ]
    let grantBank = wireBit $"{prefix}_grantBank"
    selectIndexed router.grantL fillBankNext ==> grantBank

    If router.grantFire (fun () ->
        for b in 0 .. nBreeders - 1 do
            If (eq router.grantB (k b bW)) (fun () -> cat router.grantL grantBank ==> slotB[b]))

    // ---- Claim: one filler per cycle leaves Filler.Pick with a free breeder and the
    // next queue slot. A breeder is free when idle AND unclaimed, so two fillers
    // cannot grab the same one. ----
    let freeBits =
        [ for b in 0 .. nBreeders - 1 ->
              let w = wireBit $"{prefix}_brFree%d{b}"
              (brSt[b].Is Occupancy.Free &&& bnot fillSel[b]) ==> w
              w ]

    let anyFreeE, freePick = priorityPick freeBits [ [ for b in 0 .. nBreeders - 1 -> k b bW ] ]
    let anyFree = wireBit $"{prefix}_anyFree"
    anyFreeE ==> anyFree
    let pickB = wire $"{prefix}_pickB" bW
    freePick[0] ==> pickB

    let feedStallCyc = reg $"{prefix}_feedStallCyc" 32
    let breederStallCyc = reg $"{prefix}_breederStallCyc" 32
    let fillBusyCyc = reg $"{prefix}_fillBusyCyc" 32

    // Signed-difference work test: a restart rewinds entries_published.
    let avail = wire $"{prefix}_avail" 32
    (cfg.entriesPublished - entryPtr) ==> avail
    let workPending = wireBit $"{prefix}_workPending"
    (bnot (eq avail (lit 0UL 32)) &&& bnot (slice 31 31 avail)) ==> workPending
    let goCond = wireBit $"{prefix}_goCond"

    match autoState with
    | None -> workPending ==> goCond
    | Some a ->
        // The FIFO pop lags the claim by a cycle (claim -> Filler.Entry -> pop), so a
        // naive `entCnt != 0` lets a second filler claim against a count that
        // has not been decremented yet and later pop an empty FIFO. Gate on
        // entries not already committed to a pending pop instead. At one filler
        // the sole filler is never in Filler.Entry while it is in Filler.Pick, so this
        // reduces to `entCnt != 0`.
        let pendingPops = wire $"{prefix}_pendingPops" 6
        countWhere 6 id [ for f in 0 .. nFillers - 1 -> inD f Filler.Entry ] ==> pendingPops
        let entAvail = wireBit $"{prefix}_entAvail"
        lt pendingPops (zext 6 a.entCnt) ==> entAvail

        // In op-list mode the dispatcher stays idle through the breed round —
        // the op-list writer drains the FIFO instead of breeding.
        let autoGo =
            match a.opListActiveRound with
            | Some active -> entAvail &&& bnot active
            | None -> entAvail

        mux a.cfg.autoMode autoGo workPending ==> goCond

    let pickAnyE, pickFE =
        priorityPick [ for f in 0 .. nFillers - 1 -> inD f Filler.Pick ] [ [ for f in 0 .. nFillers - 1 -> k f fW ] ]

    let anyPick = wireBit $"{prefix}_anyPick"
    pickAnyE ==> anyPick
    let claimFiller = wire $"{prefix}_claimFiller" fW
    pickFE[0] ==> claimFiller

    If (anyPick &&& bnot goCond) (fun () ->
        If running (fun () -> feedStallCyc + lit 1UL 32 ==> feedStallCyc))

    If (anyPick &&& goCond &&& bnot anyFree) (fun () ->
        breederStallCyc + lit 1UL 32 ==> breederStallCyc)

    let claimFire = wireBit $"{prefix}_claimFire"
    (anyPick &&& goCond &&& anyFree) ==> claimFire

    If claimFire (fun () ->
        for f in 0 .. nFillers - 1 do
            If (eq claimFiller (k f fW)) (fun () ->
                dstate[f].Goto Filler.Entry
                pickB ==> fillBr[f]
                entryPtr ==> slotPtr[f]
                zero1 ==> reqSent[f]
                k 0 4 ==> beatCtr[f])

        for b in 0 .. nBreeders - 1 do
            If (eq pickB (k b bW)) (fun () -> one1 ==> fillSel[b])

        entryPtr + lit 1UL 32 ==> entryPtr)

    // Beat advance for the granted owner; the burst's last beat transitions it
    // and releases the read master back to the round-robin. "Last" is the 12th
    // beat for a single-burst work item and the 4th for a record fetch.
    If (respFire &&& busyOwnerValid) (fun () ->
        fillBusyCyc + lit 1UL 32 ==> fillBusyCyc

        for f in 0 .. nFillers - 1 do
            If (eq grantId (k f fW)) (fun () ->
                If (eq beatCtr[f] lastBeat) (fun () ->
                    k 0 4 ==> beatCtr[f]
                    zero1 ==> reqSent[f]
                    zero1 ==> busyOwnerValid
                    k f fW ==> lastRd

                    If (inD f Filler.Entry) (fun () ->
                        // Single burst: both parents are already staged, so go
                        // straight to serializing. Otherwise fetch parent A.
                        If singleBurst (fun () ->
                            dstate[f].Goto Filler.SerializeA
                            k 0 6 ==> serCtr[f])

                        Else (fun () -> dstate[f].Goto Filler.ParentA))

                    If (inD f Filler.ParentA) (fun () ->
                        dstate[f].Goto Filler.SerializeA
                        k 0 6 ==> serCtr[f])

                    If (inD f Filler.ParentB) (fun () ->
                        dstate[f].Goto Filler.SerializeB
                        k 0 6 ==> serCtr[f]))

                Else (fun () -> beatCtr[f] + lit 1UL 4 ==> beatCtr[f])))

    for f in 0 .. nFillers - 1 do
        If serializing[f] (fun () ->
            fillBusyCyc + lit 1UL 32 ==> fillBusyCyc

            If (eq serCtr[f] (k (geneLen + constCount - 1) 6)) (fun () ->
                k 0 6 ==> serCtr[f]
                // Single burst: parent B is already staged, so serialize it
                // straight away. Otherwise Filler.SerializeA -> Filler.ParentB starts its fetch.
                If (inD f Filler.SerializeA) (fun () ->
                    If singleBurst (fun () -> dstate[f].Goto Filler.SerializeB)

                    Else (fun () ->
                        dstate[f].Goto Filler.ParentB
                        k 0 4 ==> beatCtr[f]
                        zero1 ==> reqSent[f]))

                Else (fun () -> dstate[f].Goto Filler.StartBreeder))

            Else (fun () -> serCtr[f] + lit 1UL 6 ==> serCtr[f]))

        If (inD f Filler.StartBreeder) (fun () ->
            dstate[f].Goto Filler.Pick

            for b in 0 .. nBreeders - 1 do
                If (eq fillBr[f] (k b bW)) (fun () ->
                    brSt[b].Goto Occupancy.Running
                    zero1 ==> fillSel[b]))

    // ---- Per-slot state: dest / skip / valid for each lane-bank in flight ----
    let slotValid = [ for s in 0 .. nSlots - 1 -> regBit $"{prefix}_slotValid%d{s}" ]
    let destS = [ for s in 0 .. nSlots - 1 -> reg $"{prefix}_destS%d{s}" 32 ]
    let skipS = [ for s in 0 .. nSlots - 1 -> regBit $"{prefix}_skipS%d{s}" ]

    // ---- Pack FSM: drain DONE breeders into staging, then release them ----
    let anyWbE, wbFields =
        priorityPick
            [ for b in 0 .. nBreeders - 1 -> brSt[b].Is Occupancy.Waiting ]
            [ [ for b in 0 .. nBreeders - 1 -> k b bW ]
              [ for b in 0 .. nBreeders - 1 -> slice 0 0 flagsB[b] ]
              [ for b in 0 .. nBreeders - 1 -> slotB[b] ]
              [ for b in 0 .. nBreeders - 1 -> destB[b] ] ]

    let anyWb = wireBit $"{prefix}_anyWb"
    anyWbE ==> anyWb
    let wbPickB = wire $"{prefix}_wbPickB" bW
    wbFields[0] ==> wbPickB
    let wbSkip = wireBit $"{prefix}_wbSkip"
    wbFields[1] ==> wbSkip
    let wbSlot = wire $"{prefix}_wbSlot" slotW
    wbFields[2] ==> wbSlot
    let wbDest = wire $"{prefix}_wbDest" 32
    wbFields[3] ==> wbDest

    let packBusyCyc = reg $"{prefix}_packBusyCyc" 32
    If pstate (fun () -> packBusyCyc + lit 1UL 32 ==> packBusyCyc)

    let packPickFire = wireBit $"{prefix}_packPickFire"
    (bnot pstate &&& anyWb) ==> packPickFire

    If packPickFire (fun () ->
        for s in 0 .. nSlots - 1 do
            If (eq wbSlot (k s slotW)) (fun () ->
                wbDest ==> destS[s]
                wbSkip ==> skipS[s]
                If wbSkip (fun () -> one1 ==> slotValid[s]))

        If wbSkip (fun () ->
            // Evaluate-only: nothing to stage, so release the breeder now.
            for b in 0 .. nBreeders - 1 do
                If (eq wbPickB (k b bW)) (fun () -> brSt[b].Goto Occupancy.Free))

        Else (fun () ->
            wbPickB ==> pSel
            wbSlot ==> pSlot
            k 0 6 ==> wbK
            lit pPack 1 ==> pstate

            for b in 0 .. nBreeders - 1 do
                If (eq wbPickB (k b bW)) (fun () -> brSt[b].Goto Occupancy.Packing)))

    let childSym = wire $"{prefix}_childSym" 8
    selectIndexed pSel [ for b in breeders -> b.childSym ] ==> childSym
    let childConst = wire $"{prefix}_childConst" 32
    selectIndexed pSel [ for b in breeders -> b.childConst ] ==> childConst

    // Re-pack the child into a 16-word gene record inside this offspring's
    // staging slot: symbols land little-endian 4 per word, and the final
    // partial word is realigned (2h+1 is odd — 1 or 3 leftover bytes).
    let stgMem = distributedMem $"{prefix}_stgMem" (slotW + 4) 32
    let packWord = wire $"{prefix}_packWord" 32
    cat childSym (slice 31 8 wordAcc) ==> packWord
    let packIdx = wire $"{prefix}_packIdx" 4
    (slice 5 2 wbK + k 1 4) ==> packIdx
    let wbCIdx = wire $"{prefix}_wbCIdx" 6
    (wbK - k geneLen 6) ==> wbCIdx
    let wbConstWr = wire $"{prefix}_wbConstWr" 4
    (cat (lit 0UL 2) (slice 1 0 wbCIdx) + k 10 4) ==> wbConstWr
    let packDone = wireBit $"{prefix}_packDone"
    (pstate &&& eq wbK (k (geneLen + constCount - 1) 6)) ==> packDone

    If pstate (fun () ->
        If (lt wbK (k geneLen 6)) (fun () ->
            packWord ==> wordAcc

            If (eq (slice 1 0 wbK) (k 3 2)) (fun () -> memWrite stgMem (cat pSlot packIdx) packWord one1)

            If (eq wbK (k (geneLen - 1) 6)) (fun () ->
                if geneLen % 4 = 1 then
                    memWrite stgMem (cat pSlot (k lastSymWord 4)) (zext 32 childSym) one1
                else
                    // Three leftover symbols: [s2 s1 s0 x] realigns to [0 s2 s1 s0].
                    memWrite stgMem (cat pSlot (k lastSymWord 4)) (zext 32 (slice 31 8 packWord)) one1))

        Else (fun () -> memWrite stgMem (cat pSlot wbConstWr) childConst one1)

        If packDone (fun () ->
            lit pIdle 1 ==> pstate

            for s in 0 .. nSlots - 1 do
                If (eq pSlot (k s slotW)) (fun () -> one1 ==> slotValid[s])

            for b in 0 .. nBreeders - 1 do
                If (eq pSel (k b bW)) (fun () -> brSt[b].Goto Occupancy.Free))

        Else (fun () -> wbK + lit 1UL 6 ==> wbK))

    for b in 0 .. nBreeders - 1 do
        ((packPickFire &&& wbSkip &&& eq wbPickB (k b bW))
             ||| (packDone &&& eq pSel (k b bW)))
        ==> relW[b]

    // ---- Emit FSM: take lane results, write the genome beats then the ring
    // record ----
    let estate = machine $"{prefix}_estate" [ Emitter.Idle; Emitter.Genome; Emitter.Ring ]
    let eSlot = reg $"{prefix}_eSlot" slotW
    let eSkip = regBit $"{prefix}_eSkip"
    let eDest = reg $"{prefix}_eDest" 32
    let eFit = reg $"{prefix}_eFit" 64
    let eUnit = reg $"{prefix}_eUnit" 32
    let wbBeat = reg $"{prefix}_wbBeat" 2
    let resultSeq = reg $"{prefix}_resultSeq" 32

    let resSlotAt =
        [ for l in 0 .. nLanes - 1 ->
              let w = wire $"{prefix}_resSlotAt%d{l}" slotW
              cat (k l lW) resBankM[l] ==> w
              w ]

    let slotMux (l: int) (regs: Expr list) = mux resBankM[l] regs[2 * l + 1] regs[2 * l]

    let anyResE, resFields =
        priorityPick
            [ for l in 0 .. nLanes - 1 -> lanes[l].res.valid &&& slotMux l slotValid ]
            [ [ for l in 0 .. nLanes - 1 -> k l lW ]
              [ for l in 0 .. nLanes - 1 -> resSlotAt[l] ]
              [ for l in 0 .. nLanes - 1 -> slotMux l skipS ]
              [ for l in 0 .. nLanes - 1 -> slotMux l destS ]
              [ for l in 0 .. nLanes - 1 -> let fit, _, _ = lanes[l].res.payload in fit ]
              [ for l in 0 .. nLanes - 1 -> let _, unit, _ = lanes[l].res.payload in unit ] ]

    let anyRes = wireBit $"{prefix}_anyRes"
    anyResE ==> anyRes
    let resPickL = wire $"{prefix}_resPickL" lW
    resFields[0] ==> resPickL
    let resTakeFire = wireBit $"{prefix}_resTakeFire"
    (estate.Is Emitter.Idle &&& anyRes) ==> resTakeFire

    for l in 0 .. nLanes - 1 do
        (resTakeFire &&& eq resPickL (k l lW)) ==> lanes[l].res.ready

    If resTakeFire (fun () ->
        resFields[1] ==> eSlot
        resFields[2] ==> eSkip
        resFields[3] ==> eDest
        resFields[4] ==> eFit
        resFields[5] ==> eUnit
        k 0 2 ==> wbBeat
        mux resFields[2] (estate.Code Emitter.Ring) (estate.Code Emitter.Genome) ==> estate.Value

        for l in 0 .. nLanes - 1 do
            If (eq resPickL (k l lW)) (fun () -> bnot resBankM[l] ==> resBankM[l])

        for s in 0 .. nSlots - 1 do
            If (eq resFields[1] (k s slotW)) (fun () -> zero1 ==> slotValid[s]))

    let emitBusyCyc = reg $"{prefix}_emitBusyCyc" 32

    If (bnot (estate.Is Emitter.Idle)) (fun () -> emitBusyCyc + lit 1UL 32 ==> emitBusyCyc)

    // Emit: 4 beats from the staging slot to the dest population slot, then one
    // ring record. Non-payload gene-record words emit zero.
    let emitWords =
        [ for j in 0..3 ->
              let addr = wire $"{prefix}_emitA%d{j}" (slotW + 4)
              cat eSlot (cat wbBeat (k j 2)) ==> addr

              let value =
                  List.fold
                      (fun acc bt ->
                          if Set.contains (4 * bt + j) validWords then
                              acc
                          else
                              mux (eq wbBeat (k bt 2)) (lit 0UL 32) acc)
                      (memRead stgMem addr)
                      [ 0..3 ]

              let w = wire $"{prefix}_emitW%d{j}" 32
              value ==> w
              w ]

    let genomeBeat = wire $"{prefix}_genomeBeat" 128
    catAll [ emitWords[3]; emitWords[2]; emitWords[1]; emitWords[0] ] ==> genomeBeat
    let ringSlot = wire $"{prefix}_ringSlot" 32
    (resultSeq &&& ringMaskR) ==> ringSlot
    let ringAddr = wire $"{prefix}_ringAddr" addrWidth
    (ringBaseR + cat (slice (addrWidth - 5) 0 ringSlot) (lit 0UL 4)) ==> ringAddr
    let destByte = wire $"{prefix}_destByte" addrWidth
    (popBaseR + cat (slice (addrWidth - 7) 0 eDest) (lit 0UL 6)) ==> destByte
    let emitAddr = wire $"{prefix}_emitAddr" addrWidth
    (destByte + zext addrWidth (cat wbBeat (lit 0UL 4))) ==> emitAddr
    let ringBeat = wire $"{prefix}_ringBeat" 128
    cat (cat resultSeq eUnit) eFit ==> ringBeat

    // ---- Write master ----
    let wrReady = wireBit $"{prefix}_wrReady"
    registerStreamReady wrReady

    let emitValid = wireBit $"{prefix}_emitValid"
    (estate.Is Emitter.Genome ||| estate.Is Emitter.Ring) ==> emitValid
    let emitBeatAddr = wire $"{prefix}_emitBeatAddr" addrWidth
    mux (estate.Is Emitter.Genome) emitAddr ringAddr ==> emitBeatAddr
    let emitBeatData = wire $"{prefix}_emitBeatData" 128
    mux (estate.Is Emitter.Genome) genomeBeat ringBeat ==> emitBeatData
    let wrFire = wireBit $"{prefix}_wrFire"
    (emitValid &&& wrReady) ==> wrFire

    // ---- Op-list emitter: the producer half of the streaming redesign ----
    // In op-list mode the breed round does NOT breed internally — the selection
    // FSM's entries stream to a DDR ring in the same 16-word record the queue
    // consumes, for the host to gather inline parents. The emit FSM is idle then
    // (the dispatcher is gated off and the score round's results have all
    // drained past the barrier), so this is a phase-gated mux on the write
    // master, not an arbiter.
    let olEmit =
        match autoState with
        | Some a when opList ->
            let olIdle, olWrite = 0UL, 1UL
            let olState = regInit $"{prefix}_olState" 1 olIdle
            let olBeat = reg $"{prefix}_olBeat" 2
            (a.cfg.autoMode &&& a.aPhase) ==> a.opListActiveRound.Value

            // The FIFO head as 16 words. It already holds the guarded seed, so
            // there is no re-guard here.
            let olZero = wireBit $"{prefix}_olZero"
            memRead a.entZero a.entRdP ==> olZero
            let olFlags = wire $"{prefix}_olFlags" 16

            mux (memRead a.entSkip a.entRdP) (k gepFlagSkipWriteback 16) (lit 0UL 16)
            ==> olFlags

            let olWord (i: int) (value: Expr) =
                let w = wire $"{prefix}_olW%d{i}" 32
                value ==> w
                w

            let olW =
                [ yield olWord 0 (zext 32 (memRead a.entParA a.entRdP))
                  yield olWord 1 (zext 32 (memRead a.entParB a.entRdP))
                  yield olWord 2 (zext 32 (memRead a.entDest a.entRdP))
                  for i in 0..3 -> olWord (3 + i) (mux olZero (lit 0UL 32) a.rateWords[i])
                  // Word 7 is flags over the geneRecomb half.
                  yield olWord 7 (cat olFlags (mux olZero (lit 0UL 16) (slice 15 0 a.rateWords[4])))
                  yield olWord 8 a.cfg.autoSigma
                  yield olWord 9 a.cfg.autoRange
                  yield olWord 10 (zext 32 (memRead a.entId a.entRdP))
                  yield olWord 11 (lit 0UL 32)
                  for i in 0..3 -> olWord (12 + i) (memRead a.entSeed[i] a.entRdP) ]

            let beatOf (b: int) =
                catAll [ olW[4 * b + 3]; olW[4 * b + 2]; olW[4 * b + 1]; olW[4 * b] ]

            let olBeatData = wire $"{prefix}_olBeatData" 128

            List.fold
                    (fun acc b -> mux (eq olBeat (k b 2)) (beatOf b) acc)
                    (beatOf 3)
                    [ 0..2 ]
            ==> olBeatData

            let entIdX = wire $"{prefix}_olEntryId" addrWidth
            zext addrWidth (memRead a.entId a.entRdP) ==> entIdX
            let olEntryBase = wire $"{prefix}_olEntryBase" addrWidth
            (a.oplistBaseR.Value + cat (slice (addrWidth - 7) 0 entIdX) (lit 0UL 6)) ==> olEntryBase
            let olAddr = wire $"{prefix}_olAddr" addrWidth
            (olEntryBase + zext addrWidth (cat olBeat (lit 0UL 4))) ==> olAddr
            let olActive = wireBit $"{prefix}_olActive"
            eq olState (lit olWrite 1) ==> olActive
            let olFire = wireBit $"{prefix}_olFire"
            (olActive &&& wrReady) ==> olFire
            (olFire &&& eq olBeat (k 3 2)) ==> a.olPop.Value

            If (bnot olState &&& a.opListActiveRound.Value &&& bnot (eq a.entCnt (k 0 3))) (fun () ->
                lit olWrite 1 ==> olState
                k 0 2 ==> olBeat)

            If olFire (fun () ->
                If (eq olBeat (k 3 2)) (fun () ->
                    lit olIdle 1 ==> olState
                    a.olEmitted.Value + lit 1UL 32 ==> a.olEmitted.Value
                    a.entRdP + lit 1UL entAddrW ==> a.entRdP) // pop the FIFO

                Else (fun () -> olBeat + lit 1UL 2 ==> olBeat))

            Some
                {| active = olActive
                   addr = olAddr
                   data = olBeatData
                   fire = olFire |}
        | _ -> None

    let beatValid = wireBit $"{prefix}_beatValid"
    let beatAddr = wire $"{prefix}_beatAddr" addrWidth
    let beatData = wire $"{prefix}_beatData" 128

    match olEmit with
    | Some ol ->
        (emitValid ||| ol.active) ==> beatValid
        mux ol.active ol.addr emitBeatAddr ==> beatAddr
        mux ol.active ol.data emitBeatData ==> beatData
    | None ->
        emitValid ==> beatValid
        emitBeatAddr ==> beatAddr
        emitBeatData ==> beatData

    let writer =
        axiMasterWriterTracked
            addrWidth
            128
            shape.writeOutstanding
            { payload = (beatAddr, beatData, lit 0xFFFFUL 16)
              valid = beatValid
              ready = wrReady
              layout = axiWriteBeatLayout addrWidth 128 }

    If (estate.Is Emitter.Genome &&& wrFire) (fun () ->
        If (eq wbBeat (k 3 2)) (fun () -> estate.Goto Emitter.Ring)
        Else (fun () -> wbBeat + lit 1UL 2 ==> wbBeat))

    If (estate.Is Emitter.Ring &&& wrFire) (fun () ->
        estate.Goto Emitter.Idle
        resultSeq + lit 1UL 32 ==> resultSeq)

    // ---- In-order B-ack bookkeeping: one FIFO bit per accepted write, and a
    // popped ring bit means that offspring's genome writes all landed too. This
    // counts write RESPONSES, not beats handed to the master. ----
    let isRing = distributedMem $"{prefix}_isRing" 4 1
    let ringWrPtr = reg $"{prefix}_ringWrPtr" 4
    let ringRdPtr = reg $"{prefix}_ringRdPtr" 4
    let resultsDone = reg $"{prefix}_resultsDone" 32

    // Op-list writes ride the same bookkeeping as any non-ring write (isRing
    // bit 0): their B-acks must advance ringRdPtr so all_idle and the pointer
    // comparison stay honest, but they must not bump resultsDone.
    let wrAccept =
        match olEmit with
        | Some ol -> wrFire ||| ol.fire
        | None -> wrFire

    If wrAccept (fun () ->
        memWrite isRing ringWrPtr (estate.Is Emitter.Ring) one1
        ringWrPtr + lit 1UL 4 ==> ringWrPtr)

    If writer.bAck (fun () ->
        ringRdPtr + lit 1UL 4 ==> ringRdPtr
        If (memRead isRing ringRdPtr) (fun () -> resultsDone + lit 1UL 32 ==> resultsDone))

    // ---- Auto mode: the fitness table writeback, the argmin, the barrier ----
    match autoState with
    | None -> ()
    | Some a ->
        let entryIdLow = wire $"{prefix}_entryIdLow" 16
        slice 15 0 eUnit ==> entryIdLow
        // In op-list mode this fires in the BREED pass too (which the host runs
        // as queue mode), and that is what lets the next emit pass select
        // without the host ever loading a table: the breed pass writes each
        // offspring's fitness at its entry-id slot and the emit pass reads it.
        // One write port either way, so the table stays BRAM-shaped.
        let evalFitWr = wireBit $"{prefix}_evalFitWr"

        (wrFire
             &&& estate.Is Emitter.Ring
             &&& (if opList then one1 else a.cfg.autoMode))
        ==> evalFitWr

        If evalFitWr (fun () ->
            memWrite a.fitTable (cat a.writeBank (slice (a.fitCapW - 1) 0 eUnit)) eFit one1

            If (lt eFit a.bestFit ||| (eq eFit a.bestFit &&& lt entryIdLow a.bestIdx)) (fun () ->
                eFit ==> a.bestFit
                entryIdLow ==> a.bestIdx))

        If (a.selSt.Is Selection.Barrier &&& eq resultsDone a.roundTarget) (fun () ->
            a.bestFit ==> a.lastBestFit
            a.bestIdx ==> a.lastBestIdx
            a.bestIdx ==> a.eliteIdx
            a.allOnes64 ==> a.bestFit
            lit 0UL 16 ==> a.bestIdx
            a.roundTarget + zext 32 a.cfg.autoPop ==> a.roundTarget
            a.readBank ==> a.writeBank

            If (bnot a.aPhase) (fun () ->
                one1 ==> a.aPhase
                lit 1UL 32 ==> a.aRound
                lit 0UL 16 ==> a.aIdx
                a.selSt.Goto Selection.Generate)

            Else (fun () ->
                bnot a.aBaseFlag ==> a.aBaseFlag

                If (eq a.aRound a.cfg.autoGens) (fun () -> a.selSt.Goto Selection.Done)

                Else (fun () ->
                    a.aRound + lit 1UL 32 ==> a.aRound
                    lit 0UL 16 ==> a.aIdx
                    a.selSt.Goto Selection.Generate)))

        // Single-shot: the fabric emits ONE generation's op-list and hands off
        // to the host. The breed round's resultsDone never advances (nothing
        // breeds), so its barrier is "every entry written" instead. The score
        // round's barrier above is unchanged and still flips to the breed phase.
        match a.olEmitted with
        | Some emitted ->
            If (a.selSt.Is Selection.Barrier
                &&& a.aPhase
                &&& eq emitted (zext 32 a.cfg.autoPop)) (fun () -> a.selSt.Goto Selection.Done)
        | None -> ()

    // ---- Counters and status ----
    let cyc = reg $"{prefix}_cyc" 32
    cyc + lit 1UL 32 ==> cyc

    let busyBreederCyc = reg $"{prefix}_busyBreederCyc" 32

    If running (fun () ->
        busyBreederCyc
        + countWhere 32 id [ for b in 0 .. nBreeders - 1 -> bnot (brSt[b].Is Occupancy.Free) ]
        ==> busyBreederCyc)

    let busyLaneCyc = reg $"{prefix}_busyLaneCyc" 32

    If running (fun () ->
        busyLaneCyc + countWhere 32 id [ for e in lanes -> bnot e.idle ]
        ==> busyLaneCyc)

    let brCyc = [ for b in 0 .. nBreeders - 1 -> reg $"{prefix}_brCyc%d{b}" 32 ]

    for b in 0 .. nBreeders - 1 do
        If (running &&& bnot (brSt[b].Is Occupancy.Free)) (fun () -> brCyc[b] + lit 1UL 32 ==> brCyc[b])

    let laneCyc = [ for l in 0 .. nLanes - 1 -> reg $"{prefix}_laneCyc%d{l}" 32 ]

    for l in 0 .. nLanes - 1 do
        If (running &&& bnot lanes[l].idle) (fun () -> laneCyc[l] + lit 1UL 32 ==> laneCyc[l])

    let allIdle = wireBit $"{prefix}_allIdle"

    (List.reduce (&&&) [ for f in 0 .. nFillers - 1 -> inD f Filler.Pick ]
         &&& bnot busyOwnerValid
         &&& bnot goCond
         &&& List.reduce (&&&) freeBits
         &&& bnot pstate
         &&& estate.Is Emitter.Idle
         &&& List.reduce (&&&) [ for e in lanes -> e.idle ]
         &&& bnot (List.reduce (|||) slotValid)
         &&& eq ringWrPtr ringRdPtr)
    ==> allIdle

    // ---- Start: latch config, reset every pointer, free every breeder. The
    // host must quiesce (all_idle) before re-starting. The lane-bank mirrors
    // survive on purpose — they shadow engine state start does not touch. ----
    If cfg.startQueue (fun () ->
        one1 ==> running
        cfg.queueBase ==> queueBaseR
        cfg.popBase ==> popBaseR
        cfg.ringBase ==> ringBaseR
        cfg.queueMask ==> queueMaskR
        cfg.ringMask ==> ringMaskR
        lit pIdle 1 ==> pstate
        estate.Goto Emitter.Idle
        lit 0UL 32 ==> entryPtr
        lit 0UL 32 ==> resultSeq
        lit 0UL 32 ==> resultsDone
        k 0 4 ==> ringWrPtr
        k 0 4 ==> ringRdPtr
        zero1 ==> busyOwnerValid
        lit 0UL fW ==> busyOwnerId
        lit 0UL fW ==> lastRd

        for f in 0 .. nFillers - 1 do
            dstate[f].Goto Filler.Pick
            zero1 ==> reqSent[f]
            k 0 4 ==> beatCtr[f]
            k 0 6 ==> serCtr[f]
            lit 0UL bW ==> fillBr[f]
            lit 0UL 32 ==> slotPtr[f]

        for b in 0 .. nBreeders - 1 do
            brSt[b].Goto Occupancy.Free
            zero1 ==> fillSel[b]
            lit 0UL 32 ==> brCyc[b]

        for l in 0 .. nLanes - 1 do
            lit 0UL 32 ==> laneCyc[l]

        lit 0UL 32 ==> feedStallCyc
        lit 0UL 32 ==> breederStallCyc
        lit 0UL 32 ==> fillBusyCyc
        lit 0UL 32 ==> packBusyCyc
        lit 0UL 32 ==> emitBusyCyc
        lit 0UL 32 ==> busyBreederCyc
        lit 0UL 32 ==> busyLaneCyc
        lit 0UL 32 ==> cyc

        match autoState with
        | None -> ()
        | Some a ->
            mux a.cfg.autoMode (a.selSt.Code Selection.Generate) (a.selSt.Code Selection.Idle) ==> a.selSt.Value

            // `skipScore` starts the emit pass already in the breed round: the
            // table and the argmin are there from the previous breed pass, so
            // PRESERVE them and take the elite from bestIdx. Every other start —
            // generation 0's emit, and every breed pass — resets for a fresh
            // argmin.
            let preserveBest =
                match a.cfg.skipScore with
                | Some s -> s &&& a.cfg.autoMode
                | None -> zero1

            (match a.cfg.skipScore with
                 | Some s -> s
                 | None -> zero1)
            ==> a.aPhase

            lit 0UL 32 ==> a.aRound
            lit 0UL 16 ==> a.aIdx
            k 0 4 ==> a.selK
            k 0 entAddrW ==> a.entWrP
            k 0 entAddrW ==> a.entRdP
            k 0 3 ==> a.entCnt
            zero1 ==> a.aBaseFlag
            zero1 ==> a.writeBank
            mux preserveBest a.bestFit a.allOnes64 ==> a.bestFit
            mux preserveBest a.bestIdx (lit 0UL 16) ==> a.bestIdx
            mux preserveBest a.bestIdx (lit 0UL 16) ==> a.eliteIdx
            lit 0UL 64 ==> a.lastBestFit
            lit 0UL 16 ==> a.lastBestIdx
            zext 32 a.cfg.autoPop ==> a.roundTarget

            match a.oplistBaseR, a.cfg.oplistBase with
            | Some r, Some v -> v ==> r
            | _ -> ()

            match a.olEmitted with
            | Some e -> lit 0UL 32 ==> e
            | None -> ())

    {| running = running
       allIdle = allIdle
       resultsDone = resultsDone
       entriesTaken = entryPtr
       cycleCount = cyc
       feedStallCycles = feedStallCyc
       breederStallCycles = breederStallCyc
       fillBusyCycles = fillBusyCyc
       packBusyCycles = packBusyCyc
       emitBusyCycles = emitBusyCyc
       busyBreederCycles = busyBreederCyc
       busyLaneCycles = busyLaneCyc
       breederBusyCycles = brCyc
       laneBusyCycles = laneCyc
       streamsActive = router.streamsActive
       auto =
        autoState
        |> Option.map (fun a ->
            {| round = a.aRound
               finished = a.selSt.Is Selection.Done
               baseFlag = a.aBaseFlag
               // The fused loop reports the last COMPLETED round's best, latched
               // at the barrier. Op-list mode has no barrier on its final
               // (breed) pass, so it reports the live argmin that pass tracked.
               bestIdx = (if opList then a.bestIdx else a.lastBestIdx)
               bestFit = (if opList then a.bestFit else a.lastBestFit)
               // Emitted AND committed: every entry accepted and every beat
               // B-acked, so the host may read the ring the instant it asserts.
               oplistDone =
                a.olEmitted
                |> Option.map (fun e -> eq e (zext 32 a.cfg.autoPop) &&& eq ringWrPtr ringRdPtr) |}) |}

/// The check geometry as a software config too, so the elaborated shape and the
/// oracle cannot drift: 4 variables, 4 constants, head 8 (a 17-symbol gene),
/// the comparison function set.
let clusterConfig =
    Chromosome.gepConfig (Chromosome.geneLayout 8 2) 4 1 4 Opcodes.comparisonSet Opcodes.ADD

/// The deployed check geometry: two breeders, two lanes, 32 program slots, 8
/// threads, 64 resident cases. `nFillers` and `inlineParents` are the two
/// arrangements the check walks.
let clusterShape (nFillers: int) (inlineParents: bool) =
    { nBreeders = 2
      nLanes = 2
      nFillers = nFillers
      functionSet = clusterConfig.functionSet
      terminalSet = clusterConfig.terminalSet
      geneLen = clusterConfig.layout.length
      headLen = clusterConfig.layout.headLength
      constCount = clusterConfig.constantCount
      varCount = clusterConfig.variableCount
      capacity = 32
      nThreads = 8
      caseCapacity = 64
      addrWidth = 32
      readOutstanding = 4
      writeOutstanding = 8
      divide = None
      inlineParents = inlineParents
      auto = None }

/// The cluster at ports: the host control bus in, the AXI4 master pair at the
/// boundary (the read and write halves of one DDR), telemetry out. The auto
/// block's ports exist exactly when the shape carries the generation loop —
/// absent rather than tied off, so reaching for one is a type error and not a
/// silently dead wire.
let clusterPoolDesign (name: string) (shape: GepClusterShape) =
    let caseAddrW = log2Exact shape.caseCapacity

    design name (fun () ->
        // Declaration order is emitted port order: the queue block, the
        // case-load bus, then the auto block.
        let startQueue = inputBit "start_queue"
        let queueBase = input "queue_base" 32
        let popBase = input "pop_base" 32
        let ringBase = input "ring_base" 32
        let queueMask = input "queue_mask" 32
        let ringMask = input "ring_mask" 32
        let entriesPublished = input "entries_published" 32
        let nCases = input "n_cases" (caseAddrW + 1)
        let ldCase = inputBit "ld_case"
        let caseAddr = input "case_addr" caseAddrW
        let caseField = input "case_field" 8
        let caseData = input "case_data" 32

        let autoCfg =
            shape.auto
            |> Option.map (fun ashape ->
                { autoMode = inputBit "auto_mode"
                  autoPop = input "auto_pop" 16
                  autoGens = input "auto_gens" 32
                  autoRates = [ for i in 0 .. (rateCount + 1) / 2 - 1 -> input $"auto_r{i}" 32 ]
                  autoSigma = input "auto_sigma" 32
                  autoRange = input "auto_range" 32
                  autoSeed = [ for i in 0..3 -> input $"auto_s{i}" 32 ]
                  oplistBase = (if ashape.opList then Some(input "oplist_base" 32) else None)
                  skipScore = (if ashape.opList then Some(inputBit "skip_score") else None)
                  rngContinue = (if ashape.opList then Some(inputBit "rng_continue") else None) })

        let pool =
            gepClusterPool
                shape
                "cl"
                { startQueue = startQueue
                  queueBase = queueBase
                  popBase = popBase
                  ringBase = ringBase
                  queueMask = queueMask
                  ringMask = ringMask
                  entriesPublished = entriesPublished
                  nCases = nCases
                  ldCase = ldCase
                  caseAddr = caseAddr
                  caseField = caseField
                  caseData = caseData
                  auto = autoCfg }

        pool.running ==> outputBit "running"
        pool.allIdle ==> outputBit "all_idle"

        pool.auto
        |> Option.iter (fun a ->
            a.round ==> output "auto_round" 32
            a.finished ==> outputBit "auto_done"
            a.baseFlag ==> outputBit "auto_base"
            a.bestIdx ==> output "best_idx" 16
            slice 31 0 a.bestFit ==> output "best_fit_lo" 32
            slice 63 32 a.bestFit ==> output "best_fit_hi" 32
            a.oplistDone |> Option.iter (fun d -> d ==> outputBit "oplist_done"))
        pool.resultsDone ==> output "results_done" 32
        pool.entriesTaken ==> output "entries_taken" 32
        pool.cycleCount ==> output "cycle_count" 32
        pool.feedStallCycles ==> output "feed_stall_cycles" 32
        pool.breederStallCycles ==> output "breeder_stall_cycles" 32
        pool.fillBusyCycles ==> output "fill_busy_cycles" 32
        pool.packBusyCycles ==> output "pack_busy_cycles" 32
        pool.emitBusyCycles ==> output "emit_busy_cycles" 32
        pool.busyBreederCycles ==> output "busy_breeder_cycles" 32
        pool.busyLaneCycles ==> output "busy_lane_cycles" 32
        pool.streamsActive ==> output "streams_active" (width pool.streamsActive)

        pool.breederBusyCycles
        |> List.iteri (fun b c -> c ==> output $"breeder{b}_busy_cycles" 32)

        pool.laneBusyCycles
        |> List.iteri (fun l c -> c ==> output $"lane{l}_busy_cycles" 32))

/// The two arrangements the queue-mode check walks.
let clusterPoolWalk (nFillers: int) (inlineParents: bool) =
    clusterPoolDesign
        ((if inlineParents then "GepClusterInline" else "GepClusterQueue") + $"%d{nFillers}fWalk")
        (clusterShape nFillers inlineParents)

/// The cluster with its divide pooled across the lanes — `warpFu` around one
/// reciprocal-divide core rather than an arm per lane. Sharing changes when a
/// result comes back and never what it is (measured at the lane), so this
/// arrangement is here to prove the pod composes at cluster scale, not to
/// re-check the arithmetic.
let clusterPoolDivWalk (sharing: FuSharing) =
    let name =
        match sharing with
        | PerLane -> "GepClusterDivPerLaneWalk"
        | Pooled -> "GepClusterDivPooledWalk"

    clusterPoolDesign
        name
        { clusterShape 1 false with
            functionSet = Opcodes.rationalSet
            nThreads = 16
            divide = Some sharing }

/// Individuals per region in the check's auto runs — the fitness table's
/// per-region half, and the ceiling on `auto_pop`.
let clusterAutoCapacity = 16

/// The cluster running its own generation loop: two population regions
/// ping-ponging, tournament selection over the double-buffered fitness table,
/// elitism at index 0, and a region barrier per round. `opList` swaps the breed
/// round's internal breeding for the DDR entry stream the host gathers against.
/// The streaming redesign's two halves ship together, so the op-list shape is
/// also the inline-parent one: the fabric emits the op list, and the host hands
/// the gathered work items straight back through queue mode.
let clusterAutoWalk (opList: bool) =
    clusterPoolDesign
        (if opList then "GepClusterOpListWalk" else "GepClusterAutoWalk")
        { clusterShape 1 opList with
            auto =
                Some
                    { popCapacity = clusterAutoCapacity
                      opList = opList } }
