/// The WarpCPU cluster on a board — P5's seam, generated. The cluster is a
/// `Design`: its needs are the host's controls, what it reports, and four
/// regions (`notes/DEVICES.md` §13) — the queue it reads, the population it
/// reads and writes, the ring and the op-list it writes. The board top, the
/// register map and the Rust seam all come from `boardTop`, so nothing here
/// spells an offset, a base address or a mask; where the regions live is the
/// board's, and the seam says.
module Warp11.Gep.ClusterAxi

open Warp11
open Warp11.Gep.Hdl
open Warp11.Gep.Cluster

/// What the cluster asks of the world, as its body takes it.
type private ClusterIo =
    { cfg: GepClusterConfig
      memory: GepMemory
      report: string -> Expr -> unit }

/// The cluster as a design. Declaration order is register order, so the
/// controls come first, then the reports — the breeder and lane counters
/// last, each block consecutive, so `BREEDER0_BUSY_CYCLES_OFFSET + 4 * b`
/// holds without a base to keep in step.
let clusterDesign (name: string) (shape: GepClusterShape) : BoardTop.Design =
    let caseAddrW = log2Exact shape.caseCapacity

    let breederIndexWidth =
        if shape.nBreeders <= 1 then 1
        else 32 - System.Numerics.BitOperations.LeadingZeroCount(uint (shape.nBreeders - 1))

    let opList =
        match shape.auto with
        | Some a -> a.opList
        | None -> false

    let reports =
        [ yield "running", 1
          yield "allIdle", 1
          yield "resultsDone", 32
          yield "entriesTaken", 32
          yield "cycleCount", 32
          yield "feedStallCycles", 32
          yield "breederStallCycles", 32
          yield "fillBusyCycles", 32
          yield "packBusyCycles", 32
          yield "emitBusyCycles", 32
          yield "busyBreederCycles", 32
          yield "busyLaneCycles", 32
          yield "streamsActive", breederIndexWidth + 1
          if shape.auto.IsSome then
              yield "autoRound", 32
              yield "autoDone", 1
              yield "autoBase", 1
              yield "bestIdx", 16
              yield "bestFitLo", 32
              yield "bestFitHi", 32
          if opList then yield "oplistDone", 1
          for b in 0 .. shape.nBreeders - 1 -> $"breeder{b}BusyCycles", 32
          for l in 0 .. shape.nLanes - 1 -> $"lane{l}BusyCycles", 32 ]

    BoardTop.design
        name
        (fun n ->
            let startQueue = n.pulseIn "startQueue"
            let ldCase = n.pulseIn "ldCase"
            let entriesPublished = n.valueIn ("entriesPublished", unsignedInt 32)
            let nCases = n.valueIn ("nCases", unsignedInt (caseAddrW + 1))
            let caseAddr = n.valueIn ("caseAddr", unsignedInt caseAddrW)
            let caseField = n.valueIn ("caseField", unsignedInt 8)
            let caseData = n.valueIn ("caseData", unsignedInt 32)

            let auto =
                shape.auto
                |> Option.map (fun a ->
                    let mode = n.valueIn ("autoMode", unsignedInt 1)
                    let pop = n.valueIn ("autoPop", unsignedInt 16)
                    let gens = n.valueIn ("autoGens", unsignedInt 32)
                    let rates = [ for i in 0..4 -> n.valueIn ($"autoR{i}", unsignedInt 32) ]
                    let sigma = n.valueIn ("autoSigma", unsignedInt 32)
                    let range = n.valueIn ("autoRange", unsignedInt 32)
                    let seeds = [ for i in 0..3 -> n.valueIn ($"autoS{i}", unsignedInt 32) ]
                    let skipScore = if a.opList then Some(n.valueIn ("skipScore", unsignedInt 1)) else None
                    let rngContinue = if a.opList then Some(n.valueIn ("rngContinue", unsignedInt 1)) else None

                    { autoMode = mode
                      autoPop = pop
                      autoGens = gens
                      autoRates = rates
                      autoSigma = sigma
                      autoRange = range
                      autoSeed = seeds
                      skipScore = skipScore
                      rngContinue = rngContinue })

            let reporters = [ for name, w in reports -> name, n.valueOut (name, unsignedInt w) ] |> Map.ofList

            let regions =
                [ for spec in gepRegions shape ->
                      spec.name, n.region (spec.name, layoutOfList spec.fields, spec.records, spec.usage, spec.sharing) ]
                |> Map.ofList

            { cfg =
                { startQueue = startQueue
                  entriesPublished = entriesPublished
                  nCases = nCases
                  ldCase = ldCase
                  caseAddr = caseAddr
                  caseField = caseField
                  caseData = caseData
                  auto = auto }
              memory =
                { queue = regions["queue"]
                  population = regions["population"]
                  ring = regions["ring"]
                  oplist = regions |> Map.tryFind "oplist" }
              report = fun name value -> reporters[name] value })
        (fun io ->
            let pool = gepClusterPool shape "cl" io.memory io.cfg

            io.report "running" pool.running
            io.report "allIdle" pool.allIdle
            io.report "resultsDone" pool.resultsDone
            io.report "entriesTaken" pool.entriesTaken
            io.report "cycleCount" pool.cycleCount
            io.report "feedStallCycles" pool.feedStallCycles
            io.report "breederStallCycles" pool.breederStallCycles
            io.report "fillBusyCycles" pool.fillBusyCycles
            io.report "packBusyCycles" pool.packBusyCycles
            io.report "emitBusyCycles" pool.emitBusyCycles
            io.report "busyBreederCycles" pool.busyBreederCycles
            io.report "busyLaneCycles" pool.busyLaneCycles
            io.report "streamsActive" pool.streamsActive

            pool.breederBusyCycles |> List.iteri (fun b c -> io.report $"breeder{b}BusyCycles" c)
            pool.laneBusyCycles |> List.iteri (fun l c -> io.report $"lane{l}BusyCycles" c)

            match pool.auto with
            | None -> ()
            | Some a ->
                io.report "autoRound" a.round
                io.report "autoDone" a.finished
                io.report "autoBase" a.baseFlag
                io.report "bestIdx" a.bestIdx
                let bestFit = wire "best_fit_w" 64
                a.bestFit ==> bestFit
                io.report "bestFitLo" (slice 31 0 bestFit)
                io.report "bestFitHi" (slice 63 32 bestFit)

                match a.oplistDone with
                | Some value -> io.report "oplistDone" value
                | None -> ())

/// What the cluster is built for: the KV260 at the clock its overlay pins,
/// its regions in the host's memory spread over the PS ports.
let clusterBoard = kv260At 99_999_001

/// The cluster's own constants, after the register map's and the regions':
/// the shape the fabric was built at. Where each record and each field sits
/// is the regions' — `QUEUE_PARENT_A_OFFSET`, `RING_STRIDE_BYTES` — and the
/// marshaller reads it there.
let clusterSeamExtras (shape: GepClusterShape) =
    let indivWords = gepUnitIndivWords shape.capacity shape.constCount

    let autoCapacity =
        match shape.auto with
        | Some a -> a.popCapacity
        | None -> 0

    [ $"pub const N_BREEDERS: usize = %d{shape.nBreeders};"
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
      "/// Words per packed individual as a lane consumes it (header, padded"
      "/// program, constants), 16 B-aligned."
      $"pub const INDIV_WORDS: usize = %d{indivWords};"
      "/// Pairing-entry flags, the high half of `recombFlags`."
      $"pub const FLAG_SKIP_WRITEBACK: u32 = %d{gepFlagSkipWriteback};" ]

/// The cluster on its board: the top, the map and the seam from one call.
let clusterTopOf (name: string) (shape: GepClusterShape) : BoardTop.BoardTop =
    let t = BoardTop.boardTop clusterBoard viaHostMemory (clusterDesign name shape)
    { t with seamExtras = t.seamExtras @ [ "" ] @ clusterSeamExtras shape }

/// The check geometry behind the register map: the same two-breeder,
/// two-lane pool the bare-port oracle runs, now driven the way silicon is.
let clusterAxiWalk = lazy (clusterTopOf "GepClusterWalk" (clusterShape 1 false))

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
        // A queue and a ring the size of the largest population, the
        // population both ping-pong halves of it.
        queueRecords = 1024
        popRecords = 2048
        ringRecords = 1024
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
let clusterAxiSilicon = lazy (clusterTopOf "GepCluster" clusterSiliconShape)

/// The committed seam's text, from the same top the build directory is
/// written from.
let clusterLayoutRs () = BoardTop.seamLines clusterAxiSilicon.Value

/// The smallest GEP there is: one breeder, one lane, the streaming loop's
/// op-list emitter — what `notes/DEVICES.md` §13 rung 4 measured as fitting
/// the Icepi's ECP5-25F by count (89% of the cells, 24 of 28 multipliers).
/// Its regions are the host's, kept over the link, so their sizes cost the
/// fabric nothing.
let clusterIcepiShape =
    { clusterShape 1 true with
        nBreeders = 1
        nLanes = 1
        queueRecords = 64
        popRecords = 128
        ringRecords = 64
        auto = Some { popCapacity = 64; opList = true } }

/// It on the Icepi, its host and its regions on the one UART.
let clusterOnIcepi =
    lazy
        (let t = BoardTop.boardTop icepi viaLink (clusterDesign "GepCluster" clusterIcepiShape)
         { t with seamExtras = t.seamExtras @ [ "" ] @ clusterSeamExtras clusterIcepiShape })
