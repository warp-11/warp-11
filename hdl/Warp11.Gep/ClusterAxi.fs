/// The WarpCPU cluster behind its AXI boundary — P5's seam. One register map,
/// declared once and consumed three times: `axiLiteSlaveOf` elaborates the
/// slave from it, `regMapRsLines` generates the Rust driver's offsets from it,
/// and the wrapper below wires the pool to it entry by entry. Nothing spells a
/// register name twice, so the fabric and the driver cannot disagree.
///
/// Offsets mirror the original `GepClusterLayout` so the two implementations stay
/// comparable register for register. One deliberate divergence: the original keeps
/// `parent_hits`/`parent_misses` wired to zero because a resident parent store
/// used to live there. The F# pool never had one — the streaming redesign
/// deleted the need — so those registers are absent rather than lying.
module Warp11.Gep.ClusterAxi

open Warp11
open Warp11.Gep.Hdl
open Warp11.Gep.Cluster

/// A register-map value plus the handles the wrapper needs to wire it. Built
/// once per geometry, because the case-address width and the counter blocks
/// depend on it.
type GepClusterMap =
    { /// Reads the identity at offset 0; the same word's writes are the pulses.
      id: RegEntry
      startQueue: RegEntry
      ldCase: RegEntry
      queueBase: RegEntry
      popBase: RegEntry
      ringBase: RegEntry
      queueMask: RegEntry
      ringMask: RegEntry
      entriesPublished: RegEntry
      nCases: RegEntry
      caseAddr: RegEntry
      caseField: RegEntry
      caseData: RegEntry
      running: RegEntry
      allIdle: RegEntry
      resultsDone: RegEntry
      entriesTaken: RegEntry
      cycleCount: RegEntry
      feedStallCycles: RegEntry
      breederStallCycles: RegEntry
      fillBusyCycles: RegEntry
      packBusyCycles: RegEntry
      emitBusyCycles: RegEntry
      busyBreederCycles: RegEntry
      busyLaneCycles: RegEntry
      streamsActive: RegEntry
      autoMode: RegEntry option
      autoPop: RegEntry option
      autoGens: RegEntry option
      autoRates: RegEntry list
      autoSigma: RegEntry option
      autoRange: RegEntry option
      autoSeeds: RegEntry list
      autoRound: RegEntry option
      autoDone: RegEntry option
      autoBase: RegEntry option
      bestIdx: RegEntry option
      bestFitLo: RegEntry option
      bestFitHi: RegEntry option
      oplistBase: RegEntry option
      skipScore: RegEntry option
      rngContinue: RegEntry option
      oplistDone: RegEntry option
      breederBusy: RegEntry list
      laneBusy: RegEntry list }

/// Bit positions inside the two packed status words.
let ctrlOffset = 0x00UL
/// The counter blocks are allocated consecutively, so the driver's stride
/// arithmetic — `BREEDER0_BUSY_CYCLES_OFFSET + 4 * breeder` — holds without a
/// base constant to keep in step: the generated layout carries index zero and
/// the rest follow it.
let clusterApertureAddrWidth = 10

/// Read at offset 0, where the control word's write side lives — a `roConst`
/// owns a word's read side and pulse bits own its write side, so the identity
/// costs no address space. Continues the `F5B0D00n` family: 001 is the mini
/// Mandelbrot pod, 002 the frame accelerator, 003 this.
///
/// It answers **which design**, and only that: a driver pointed at the wrong
/// bitstream or the wrong base address stops here rather than reading plausible
/// rubbish at every offset, since an unmapped read inside the aperture returns
/// zero instead of faulting.
///
/// It does **not** answer which revision of the map. An older bitstream of this
/// same design answers this constant happily and then serves every register
/// from the wrong address, because the offsets here are allocated and adding a
/// register moves everything after it. That is what `layoutHash` is for, and
/// the two are checked together.
let clusterIdMagic = 0xF5B0D003UL

let gepClusterMap (shape: GepClusterShape) : GepClusterMap * RegMap =
    let caseAddrW =
        let mutable w = 0
        while (1 <<< w) < shape.caseCapacity do w <- w + 1
        w

    let bW =
        if shape.nBreeders <= 1 then 1
        else 32 - System.Numerics.BitOperations.LeadingZeroCount(uint (shape.nBreeders - 1))

    if shape.nBreeders > 32 then
        failwith $"the breeder counter block holds 32, got %d{shape.nBreeders}"

    if shape.nLanes > 96 then
        failwith $"the lane counter block holds 96, got %d{shape.nLanes}"

    let opList =
        match shape.auto with
        | Some a -> a.opList
        | None -> false

    let hasAuto = shape.auto.IsSome

    // Allocated rather than numbered, in declaration order, with no offsets
    // stated anywhere. Several blocks here are conditional, so a cluster built
    // without the auto engine lays out differently from one with it — which is
    // sound because the Rust layout is generated from this same map and only
    // `clusterSiliconShape` ever reaches hardware. Nothing pins an address, and
    // nothing needs to.
    let regs, builtMap =
        buildRegMapPinned clusterApertureAddrWidth (fun r ->
            let id, startQueue, ldCase =
                r.Word(fun w ->
                    w.Const("id", clusterIdMagic), w.Pulse "startQueue", w.Pulse "ldCase")

            // Which revision of this map the fabric was built from. The word is
            // its own because a `roConst` owns a whole read side; the value is
            // computed once the map is closed, over every entry but this one.
            r.LayoutHash "layoutHash"

            let queueBase = r.RwReg("queueBase", 32, 0UL)
            let popBase = r.RwReg("popBase", 32, 0UL)
            let ringBase = r.RwReg("ringBase", 32, 0UL)
            let queueMask = r.RwReg("queueMask", 32, 0UL)
            let ringMask = r.RwReg("ringMask", 32, 0UL)
            let entriesPublished = r.RwReg("entriesPublished", 32, 0UL)
            let nCases = r.RwReg("nCases", caseAddrW + 1, 0UL)
            let caseAddr = r.RwReg("caseAddr", caseAddrW, 0UL)
            let caseField = r.RwReg("caseField", 8, 0UL)
            let caseData = r.RwReg("caseData", 32, 0UL)

            let running, allIdle =
                r.Word(fun w -> w.Field("running", 1), w.Field("allIdle", 1))

            let resultsDone = r.RoField("resultsDone", 32)
            let entriesTaken = r.RoField("entriesTaken", 32)
            let cycleCount = r.RoField("cycleCount", 32)
            let feedStallCycles = r.RoField("feedStallCycles", 32)
            let breederStallCycles = r.RoField("breederStallCycles", 32)
            let fillBusyCycles = r.RoField("fillBusyCycles", 32)
            let packBusyCycles = r.RoField("packBusyCycles", 32)
            let emitBusyCycles = r.RoField("emitBusyCycles", 32)
            let busyBreederCycles = r.RoField("busyBreederCycles", 32)
            let busyLaneCycles = r.RoField("busyLaneCycles", 32)
            let streamsActive = r.RoField("streamsActive", bW + 1)

            // The auto engine's block, absent entirely on a cluster built
            // without it — and the blocks after it simply move up.
            let autoMode = if hasAuto then Some(r.RwReg("autoMode", 1, 0UL)) else None
            let autoPop = if hasAuto then Some(r.RwReg("autoPop", 16, 0UL)) else None
            let autoGens = if hasAuto then Some(r.RwReg("autoGens", 32, 0UL)) else None

            let autoRates =
                if hasAuto then
                    [ for i in 0..4 -> r.RwReg($"autoR{i}", 32, 0UL) ]
                else
                    []

            let autoSigma = if hasAuto then Some(r.RwReg("autoSigma", 32, 0UL)) else None
            let autoRange = if hasAuto then Some(r.RwReg("autoRange", 32, 0UL)) else None

            let autoSeeds =
                if hasAuto then
                    [ for i in 0..3 -> r.RwReg($"autoS{i}", 32, 0UL) ]
                else
                    []

            let autoRound = if hasAuto then Some(r.RoField("autoRound", 32)) else None

            // Three conditional bits in one word, on two different conditions.
            // Each takes the next free bit, so a build without the auto engine
            // puts the op-list's bit at 0 rather than leaving a hole — the
            // layout follows, because it is generated from this.
            let autoDone, autoBase, oplistDone =
                r.Word(fun w ->
                    (if hasAuto then Some(w.Field("autoDone", 1)) else None),
                    (if hasAuto then Some(w.Field("autoBase", 1)) else None),
                    (if opList then Some(w.Field("oplistDone", 1)) else None))

            let bestIdx = if hasAuto then Some(r.RoField("bestIdx", 16)) else None
            let bestFitLo = if hasAuto then Some(r.RoField("bestFitLo", 32)) else None
            let bestFitHi = if hasAuto then Some(r.RoField("bestFitHi", 32)) else None

            let oplistBase = if opList then Some(r.RwReg("oplistBase", 32, 0UL)) else None
            let skipScore = if opList then Some(r.RwReg("skipScore", 1, 0UL)) else None
            let rngContinue = if opList then Some(r.RwReg("rngContinue", 1, 0UL)) else None

            let breederBusy =
                [ for b in 0 .. shape.nBreeders - 1 -> r.RoField($"breeder{b}BusyCycles", 32) ]

            let laneBusy =
                [ for l in 0 .. shape.nLanes - 1 -> r.RoField($"lane{l}BusyCycles", 32) ]

            { id = id
              startQueue = startQueue
              ldCase = ldCase
              queueBase = queueBase
              popBase = popBase
              ringBase = ringBase
              queueMask = queueMask
              ringMask = ringMask
              entriesPublished = entriesPublished
              nCases = nCases
              caseAddr = caseAddr
              caseField = caseField
              caseData = caseData
              running = running
              allIdle = allIdle
              resultsDone = resultsDone
              entriesTaken = entriesTaken
              cycleCount = cycleCount
              feedStallCycles = feedStallCycles
              breederStallCycles = breederStallCycles
              fillBusyCycles = fillBusyCycles
              packBusyCycles = packBusyCycles
              emitBusyCycles = emitBusyCycles
              busyBreederCycles = busyBreederCycles
              busyLaneCycles = busyLaneCycles
              streamsActive = streamsActive
              autoMode = autoMode
              autoPop = autoPop
              autoGens = autoGens
              autoRates = autoRates
              autoSigma = autoSigma
              autoRange = autoRange
              autoSeeds = autoSeeds
              autoRound = autoRound
              autoDone = autoDone
              autoBase = autoBase
              oplistDone = oplistDone
              bestIdx = bestIdx
              bestFitLo = bestFitLo
              bestFitHi = bestFitHi
              oplistBase = oplistBase
              skipScore = skipScore
              rngContinue = rngContinue
              breederBusy = breederBusy
              laneBusy = laneBusy })

    // The record and the map, both from the builder above — every register
    // named once, and the entries list gone along with the arithmetic.
    regs, builtMap

/// The top-level synthesis unit: the AXI-Lite control slave, the pool, and the
/// pool's read and write master channels combined into the one `m_axi` AXI4
/// interface an HP/HPC port takes. The io factory declares all three bundles;
/// the pool ties and drives its two master channels through the port records
/// it is handed.
let gepClusterAxi (topName: string) (shape: GepClusterShape) =
    let m, mMap = gepClusterMap shape

    defModuleClocked
        axiClock
        topName
        (fun p ->
            (axiLiteSlavePorts p mMap.apertureAddrWidth,
             axiReadBusPorts p "m_axi" shape.addrWidth 128,
             axiWriteBusPorts p "m_axi" shape.addrWidth 128))
        (fun (slavePorts, readBusPorts, writeBusPorts) ->
        let regs = regMapSlave slavePorts mMap

        let autoCfg =
            shape.auto
            |> Option.map (fun _ ->
                { autoMode = regs.value m.autoMode.Value
                  autoPop = regs.value m.autoPop.Value
                  autoGens = regs.value m.autoGens.Value
                  autoRates = [ for r in m.autoRates -> regs.value r ]
                  autoSigma = regs.value m.autoSigma.Value
                  autoRange = regs.value m.autoRange.Value
                  autoSeed = [ for s in m.autoSeeds -> regs.value s ]
                  oplistBase = m.oplistBase |> Option.map regs.value
                  skipScore = m.skipScore |> Option.map regs.value
                  rngContinue = m.rngContinue |> Option.map regs.value })

        let pool =
            gepClusterPool
                shape
                "cl"
                readBusPorts
                writeBusPorts
                { startQueue = regs.pulse m.startQueue
                  queueBase = regs.value m.queueBase
                  popBase = regs.value m.popBase
                  ringBase = regs.value m.ringBase
                  queueMask = regs.value m.queueMask
                  ringMask = regs.value m.ringMask
                  entriesPublished = regs.value m.entriesPublished
                  nCases = regs.value m.nCases
                  ldCase = regs.pulse m.ldCase
                  caseAddr = regs.value m.caseAddr
                  caseField = regs.value m.caseField
                  caseData = regs.value m.caseData
                  auto = autoCfg }

        regs.drive m.running pool.running
        regs.drive m.allIdle pool.allIdle
        regs.drive m.resultsDone pool.resultsDone
        regs.drive m.entriesTaken pool.entriesTaken
        regs.drive m.cycleCount pool.cycleCount
        regs.drive m.feedStallCycles pool.feedStallCycles
        regs.drive m.breederStallCycles pool.breederStallCycles
        regs.drive m.fillBusyCycles pool.fillBusyCycles
        regs.drive m.packBusyCycles pool.packBusyCycles
        regs.drive m.emitBusyCycles pool.emitBusyCycles
        regs.drive m.busyBreederCycles pool.busyBreederCycles
        regs.drive m.busyLaneCycles pool.busyLaneCycles
        regs.drive m.streamsActive pool.streamsActive

        List.iter2 (fun e c -> regs.drive e c) m.breederBusy pool.breederBusyCycles
        List.iter2 (fun e c -> regs.drive e c) m.laneBusy pool.laneBusyCycles

        match pool.auto with
        | None -> ()
        | Some a ->
            regs.drive m.autoRound.Value a.round
            regs.drive m.autoDone.Value a.finished
            regs.drive m.autoBase.Value a.baseFlag
            regs.drive m.bestIdx.Value a.bestIdx
            let bestFit = wire "best_fit_w" 64
            a.bestFit ==> bestFit
            regs.drive m.bestFitLo.Value (slice 31 0 bestFit)
            regs.drive m.bestFitHi.Value (slice 63 32 bestFit)

            match m.oplistDone, a.oplistDone with
            | Some entry, Some value -> regs.drive entry value
            | _ -> ())

/// The check geometry behind the register map: the same two-breeder,
/// two-lane pool the bare-port oracle runs, now driven the way silicon is.
let clusterAxiWalk =
    gepClusterAxi "GepClusterAxiWalk" (clusterShape 1 false)

/// The silicon shape — the measured pick, ported: 4 breeders x 8
/// DIV-resident 16-thread lanes, the warped dispatcher at two fillers, and the
/// host-marshaled streaming loop (inline parents + the op-list emitter). That
/// shape is balanced and it FITS: 95,266 LUT / 81.3% at synth on the KV260,
/// where 6x12 is 131% and does not. The breeders, not the dividers, are the LUT
/// hog — which is why the divide stays `PerLane` (the pod cannot relieve the
/// binding resource, and pooling it cost 44-118% throughput).
///
/// The superset function set (comparison plus DIV) covers Coulomb and the
/// comparison benchmarks from one bitstream.
let clusterSiliconShape =
    { clusterShape 2 true with
        nBreeders = 4
        nLanes = 8
        functionSet = Array.append Opcodes.comparisonSet [| Opcodes.DIV |]
        nThreads = 16
        caseCapacity = 256
        divide = Some PerLane
        auto = Some { popCapacity = 1024; opList = true } }

/// The silicon shape as a software config, so a host-side oracle draws from the
/// SAME symbol sets the fabric's ROMs are built from. Deriving it rather than
/// re-typing it is not tidiness: the silicon shape widens the function set with
/// DIV, and a board vector built against the narrower `clusterConfig` breeds
/// different children the moment a draw lands on a function symbol — measured
/// as 53/64 fitnesses and 37/64 genomes on the first silicon run, deterministic
/// and therefore not a fabric fault at all.
let clusterSiliconConfig =
    Chromosome.gepConfig
        (Chromosome.geneLayout clusterSiliconShape.headLen 2)
        clusterSiliconShape.varCount
        1
        clusterSiliconShape.constCount
        clusterSiliconShape.functionSet
        Opcodes.ADD

/// Lazy, so only the seam emit pays for elaborating 4 breeders and 8 lanes.
let clusterAxiSilicon = lazy (gepClusterAxi "GepClusterAxi" clusterSiliconShape)

/// The Rust half of the seam: the map's offsets, plus the geometry a driver
/// needs to lay out DDR and size its transfers.
let clusterLayoutRs (shape: GepClusterShape) =
    let _, mMap = gepClusterMap shape
    let indivWords = gepUnitIndivWords shape.capacity shape.constCount

    let autoCapacity =
        match shape.auto with
        | Some a -> a.popCapacity
        | None -> 0

    [ "//! Register map for the `GepClusterAxi` AXI-Lite slave — the WarpCPU"
      "//! genetic-programming cluster. Population, pairing entries, the op-list"
      "//! and the result ring all live in PS DDR; only control words cross"
      "//! AXI-Lite, and the fitness table never leaves the fabric."
      "//! Generated by `dotnet run -- hardware <repo-root>` in hdl/Warp11.Gep."
      "//! Do not edit by hand — changes will be overwritten on next emit."
      "" ]
    @ regMapRsLines mMap
    @ [ ""
        $"pub const N_BREEDERS: usize = %d{shape.nBreeders};"
        $"pub const N_LANES: usize = %d{shape.nLanes};"
        $"pub const N_FILLERS: usize = %d{shape.nFillers};"
        $"pub const N_THREADS: usize = %d{shape.nThreads};"
        $"pub const GENE_LEN: usize = %d{shape.geneLen};"
        $"pub const HEAD_LEN: usize = %d{shape.headLen};"
        $"pub const CONST_COUNT: usize = %d{shape.constCount};"
        $"pub const VAR_COUNT: usize = %d{shape.varCount};"
        $"pub const CAPACITY: usize = %d{shape.capacity};"
        $"pub const CASE_CAPACITY: usize = %d{shape.caseCapacity};"
        $"pub const AUTO_POP_CAPACITY: usize = %d{autoCapacity};"
        $"pub const INLINE_PARENTS: bool = %b{shape.inlineParents};"
        ""
        "/// A gene record, a pairing entry and an op-list entry are all this many"
        "/// 32-bit words — 64 B, four 128-bit beats."
        $"pub const RECORD_WORDS: usize = %d{gepRecordWords};"
        $"pub const RECORD_BYTES: usize = %d{gepRecordWords * 4};"
        "/// An inline-parent work item is the entry plus both parent records —"
        "/// 192 B of payload on a 256 B STRIDE. The padding is what lets the"
        "/// fabric fetch the whole item as one 12-beat burst: an AXI burst may"
        "/// not cross a 4 KB boundary, and 256 divides 4096 where 192 does not."
        $"pub const WORK_ITEM_WORDS: usize = %d{gepWorkItemWords};"
        $"pub const WORK_ITEM_BYTES: usize = %d{gepWorkItemWords * 4};"
        $"pub const WORK_ITEM_PAYLOAD_WORDS: usize = %d{3 * gepRecordWords};"
        $"pub const WORK_ITEM_BEATS: usize = %d{gepWorkItemBeats};"
        "/// One fitness-ring record: [fit_lo, fit_hi, entry_id, seq]."
        "pub const RESULT_WORDS: usize = 4;"
        "pub const RESULT_BYTES: usize = 16;"
        "/// Words per packed individual as a lane consumes it (header, padded"
        "/// program, constants), 16 B-aligned."
        $"pub const INDIV_WORDS: usize = %d{indivWords};"
        ""
        "/// Pairing-entry flags, word 7's high half."
        $"pub const FLAG_SKIP_WRITEBACK: u32 = %d{gepFlagSkipWriteback};"
        ""
        "/// Word indices inside a pairing entry — the one layout the fabric's"
        "/// filler, the fabric's op-list emitter and the host marshaller share."
        "pub const ENTRY_PARENT_A: usize = 0;"
        "pub const ENTRY_PARENT_B: usize = 1;"
        "pub const ENTRY_DEST: usize = 2;"
        "pub const ENTRY_RATE0: usize = 3; // ..RATE0+3; word 7 is flags<<16 | geneRecomb"
        "pub const ENTRY_FLAGS_WORD: usize = 7;"
        "pub const ENTRY_SIGMA: usize = 8;"
        "pub const ENTRY_RANGE: usize = 9;"
        "pub const ENTRY_ID: usize = 10;"
        "pub const ENTRY_SEED0: usize = 12; // ..SEED0+3"
        ""
        "/// Word indices inside a gene record."
        "pub const RECORD_SYMBOLS: usize = 1; // 4 symbols per word"
        "pub const RECORD_CONSTANTS: usize = 10;"
        "" ]
