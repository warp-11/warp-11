/// The GoL accelerator behind its AXI boundary: the declarative register map
/// (the first design on `RegMap.fs`), the burst/interval pacing FSM ported
/// from Kotlin's `GoLAxiWrapper`, the load-window prefetch, and the conflate
/// snapshot path into PS DDR through the 128-bit write master. One
/// parameterized elaboration serves the scaled rehearsal config and the
/// 64×64 silicon config.
///
/// Grid constraints: 128 % gridWidth = 0 (rows pack whole into 128-bit DDR
/// beats — the KV260 HP port drops sub-128-bit writes, so beats are always
/// full and 16-byte aligned) and gridHeight divisible by the rows-per-beat
/// that implies.
module Warp11.GoL.Wrapper

open Warp11
open Warp11.GoL.Core

let golIdMagic = 0xF5601001UL // "F5 GoL v1"

/// Index width for 0..n-1.
let private indexBits (n: int) =
    let mutable w = 1

    while (1 <<< w) < n do
        w <- w + 1

    w

/// The register map, one value per entry so the wrapper and the seam name
/// each register exactly once. Offsets mirror Kotlin's GoLRegs where the
/// feature survives; the snapshot windows are gone (the frame lives in DDR)
/// and `fbBaseAddr`/`snapSlot` are new — the DDR-side handshake.
type GolMap =
    { id: RegEntry
      load: RegEntry
      tick: RegEntry
      reset: RegEntry
      stop: RegEntry
      busy: RegEntry
      population: RegEntry
      generation: RegEntry
      tickCount: RegEntry
      burstIrq: RegEntry
      snapIrq: RegEntry
      snapCapture: RegEntry
      snapRelease: RegEntry
      snapReady: RegEntry
      snapOverrun: RegEntry
      snapSlot: RegEntry
      intervalCycles: RegEntry
      fbBaseAddr: RegEntry
      loadRow: RegEntry
      windowWords: int
      wordsPerRow: int
      map: RegMap }

let golMap (gridWidth: int) (gridHeight: int) : GolMap =
    let wordsPerRow = (gridWidth + 31) / 32
    let windowWords = gridHeight * wordsPerRow

    if windowWords &&& (windowWords - 1) <> 0 then
        failwith $"golMap: the load window wants a power-of-two word count, got %d{windowWords}"

    // Aligned to its own size, and clear of the register words in every config.
    let windowWordOffset = max 64 windowWords

    let id = roConst "id" 0x000UL golIdMagic
    let load = pulseBit "load" 0x000UL 0
    let tick = pulseBit "tick" 0x000UL 1
    let reset = pulseBit "reset" 0x000UL 2
    let stop = pulseBit "stop" 0x000UL 3
    let busy = roField "busy" 0x004UL 0 1
    let population = roField "population" 0x004UL 1 (bitsNeeded (gridWidth * gridHeight))
    let generation = roField "generation" 0x008UL 0 32
    let tickCount = rwReg "tickCount" 0x00CUL 32 1UL
    let burstIrq = w1cBit "burstIrq" 0x010UL 0
    let snapIrq = w1cBit "snapIrq" 0x010UL 1
    let snapCapture = pulseBit "snapCapture" 0x014UL 0
    let snapRelease = pulseBit "snapRelease" 0x018UL 0
    let snapReady = roField "snapReady" 0x01CUL 0 1
    let snapOverrun = roField "snapOverrun" 0x01CUL 8 8
    let snapSlot = roField "snapSlot" 0x01CUL 16 2
    let intervalCycles = rwReg "intervalCycles" 0x020UL 32 1UL
    let fbBaseAddr = rwReg "fbBaseAddr" 0x024UL 32 0UL
    let loadRow = rwWindow "loadRow" (uint64 (windowWordOffset * 4)) windowWords

    { id = id
      load = load
      tick = tick
      reset = reset
      stop = stop
      busy = busy
      population = population
      generation = generation
      tickCount = tickCount
      burstIrq = burstIrq
      snapIrq = snapIrq
      snapCapture = snapCapture
      snapRelease = snapRelease
      snapReady = snapReady
      snapOverrun = snapOverrun
      snapSlot = snapSlot
      intervalCycles = intervalCycles
      fbBaseAddr = fbBaseAddr
      loadRow = loadRow
      windowWords = windowWords
      wordsPerRow = wordsPerRow
      map =
        { apertureAddrWidth = 10
          entries =
            [ id
              load
              tick
              reset
              stop
              busy
              population
              generation
              tickCount
              burstIrq
              snapIrq
              snapCapture
              snapRelease
              snapReady
              snapOverrun
              snapSlot
              intervalCycles
              fbBaseAddr
              loadRow ] } }

/// Beats per snapshot frame and the DDR slot shift: a slot is the frame
/// rounded to its own power-of-two stride, so slot addressing is a shift.
let golBeatCount (gridWidth: int) (gridHeight: int) = gridWidth * gridHeight / 128
let golSlotShift (gridWidth: int) (gridHeight: int) = indexBits (golBeatCount gridWidth gridHeight) + 4

/// `gensPerCycle` unrolls the grid (the act-5 lever): every fire of the
/// pacing FSM advances that many generations, so `tickCount` counts fires
/// while the `generation` register keeps counting true generations. The
/// interval still paces fires — a host asking for N generations/second
/// divides by gensPerCycle (the seam carries the constant).
let golAxi (topName: string) (gensPerCycle: int) (gridWidth: int) (gridHeight: int) =
    if 128 % gridWidth <> 0 then
        failwith $"golAxi: 128 %% gridWidth must be 0, got %d{gridWidth}"

    let rowsPerBeat = 128 / gridWidth

    if gridHeight % rowsPerBeat <> 0 then
        failwith $"golAxi: gridHeight %d{gridHeight} not divisible by rows-per-beat %d{rowsPerBeat}"

    let m = golMap gridWidth gridHeight
    let beatCount = golBeatCount gridWidth gridHeight
    let beatIndexBits = indexBits beatCount
    let slotShift = golSlotShift gridWidth gridHeight

    designClocked axiClock topName (fun () ->
        let regs = axiLiteSlaveOf m.map

        let loadPulse = regs.pulse m.load
        let tickPulse = regs.pulse m.tick
        let resetPulse = regs.pulse m.reset
        let stopPulse = regs.pulse m.stop
        let tickCount = regs.value m.tickCount
        let intervalCycles = regs.value m.intervalCycles
        let fbBaseAddr = regs.value m.fbBaseAddr

        // ---- the pacing FSM: Kotlin's burst/interval logic, line for line.
        // tickCount = 0 means continuous (until stop); intervalCycles paces
        // generations inside a burst; stop wins over everything.
        let tickRemaining = reg "tick_remaining" 32
        let busyReg = regBit "busy_reg"
        let continuousReg = regBit "continuous_reg"
        let intervalCount = reg "interval_count" 32
        let generationReg = reg "generation_reg" 32
        let burstDoneQ = regBit "burst_done_q"

        let intervalSeed = wire "interval_seed" 32
        mux (eq intervalCycles (lit 0UL 32)) (lit 0UL 32) (intervalCycles - lit 1UL 32) ==> intervalSeed

        let startBurst = wireBit "start_burst"
        (tickPulse &&& bnot busyReg &&& bnot stopPulse) ==> startBurst
        let intervalLast = wireBit "interval_last"
        eq intervalCount (lit 0UL 32) ==> intervalLast
        let firePulse = wireBit "fire_pulse"
        (busyReg &&& intervalLast &&& bnot stopPulse) ==> firePulse

        If stopPulse (fun () ->
            lit 0UL 1 ==> busyReg
            lit 0UL 1 ==> continuousReg
            lit 0UL 32 ==> tickRemaining
            lit 0UL 32 ==> intervalCount
            busyReg ==> burstDoneQ)

        Else (fun () ->
            If startBurst (fun () ->
                lit 1UL 1 ==> busyReg
                eq tickCount (lit 0UL 32) ==> continuousReg
                tickCount ==> tickRemaining
                intervalSeed ==> intervalCount
                lit 0UL 1 ==> burstDoneQ)

            Else (fun () ->
                If busyReg (fun () ->
                    If firePulse (fun () ->
                        intervalSeed ==> intervalCount

                        If continuousReg (fun () -> lit 0UL 1 ==> burstDoneQ)

                        Else (fun () ->
                            If (eq tickRemaining (lit 1UL 32)) (fun () ->
                                lit 0UL 32 ==> tickRemaining
                                lit 0UL 1 ==> busyReg
                                lit 1UL 1 ==> burstDoneQ)

                            Else (fun () ->
                                tickRemaining - lit 1UL 32 ==> tickRemaining
                                lit 0UL 1 ==> burstDoneQ)))

                    Else (fun () ->
                        intervalCount - lit 1UL 32 ==> intervalCount
                        lit 0UL 1 ==> burstDoneQ))

                Else (fun () -> lit 0UL 1 ==> burstDoneQ)))

        // The reset pulse decodes combinationally off the AXI write channel
        // and fans out to every cell enable — registered here so the fanout
        // starts at a local register (166.67 MHz failed at WNS -0.093 with
        // the smartconnect leg on the path). Reset lands one cycle after the
        // write, which no driver can observe.
        let resetPulseQ = regBit "reset_pulse_q"
        resetPulse ==> resetPulseQ

        If resetPulseQ (fun () -> lit 0UL 32 ==> generationReg)
        Else (fun () ->
            If firePulse (fun () -> generationReg + lit (uint64 gensPerCycle) 32 ==> generationReg))

        // ---- the load prefetch: the host fills the window (one/two 32-bit
        // words per row, low word first), pulses `load`, and the FSM walks the
        // window's sync read port into per-row staging registers, then hands
        // the whole surface to the core in one loadEnable pulse.
        let prefetchTotal = m.windowWords
        let prefetchAddrWidth = indexBits prefetchTotal
        let prefetching = regBit "prefetching"
        let prefetchAddr = reg "prefetch_addr" prefetchAddrWidth
        let loadToCore = regBit "load_to_core"

        // The address and the valid have to arrive with the word, not with the
        // request. The port owns how far that is, so neither of these states it.
        // The port is the window's — shared with the host's readback — so a
        // cycle can be stolen mid-prefetch: `hostTurn` says so, the beat is not
        // marked valid, and the address holds to retry the same word.
        let read = regs.window m.loadRow prefetchAddr
        let readData = read.data
        let dataIndex = read.through "prefetch_addr" prefetchAddr
        let dataValid = read.through "prefetching" (prefetching &&& bnot read.hostTurn)

        let staging =
            [ for y in 0 .. gridHeight - 1 ->
                  [ for k in 0 .. m.wordsPerRow - 1 -> reg $"load_stage_%d{y}_%d{k}" 32 ] ]

        If dataValid (fun () ->
            for y in 0 .. gridHeight - 1 do
                for k in 0 .. m.wordsPerRow - 1 do
                    let i = y * m.wordsPerRow + k

                    If (eq dataIndex (lit (uint64 i) prefetchAddrWidth)) (fun () ->
                        readData ==> List.item k (List.item y staging)))

        let lastFetch = wireBit "last_fetch"
        (dataValid &&& eq dataIndex (lit (uint64 (prefetchTotal - 1)) prefetchAddrWidth)) ==> lastFetch

        If loadPulse (fun () ->
            lit 1UL 1 ==> prefetching
            lit 0UL prefetchAddrWidth ==> prefetchAddr)

        Else (fun () ->
            If (prefetching &&& bnot read.hostTurn) (fun () ->
                If (eq prefetchAddr (lit (uint64 (prefetchTotal - 1)) prefetchAddrWidth)) (fun () ->
                    lit 0UL 1 ==> prefetching)

                Else (fun () -> prefetchAddr + lit 1UL prefetchAddrWidth ==> prefetchAddr)))

        lastFetch ==> loadToCore

        // ---- the core: staging rows (zeroed by reset) in, packed rows out.
        let loadRows =
            [ for y in 0 .. gridHeight - 1 ->
                  let words = List.item y staging

                  let assembled =
                      if m.wordsPerRow = 2 then
                          let rowCat = wire $"load_row_cat_%d{y}" 64
                          cat (List.item 1 words) (List.item 0 words) ==> rowCat
                          rowCat
                      else
                          List.head words

                  let row = wire $"load_row_%d{y}" gridWidth

                  let value =
                      if width assembled = gridWidth then
                          assembled
                      else
                          slice (gridWidth - 1) 0 assembled

                  mux resetPulseQ (lit 0UL gridWidth) value ==> row
                  row ]

        let coreLoad = wireBit "core_load_enable"
        (loadToCore ||| resetPulseQ) ==> coreLoad
        let coreTick = wireBit "core_tick_enable"
        firePulse ==> coreTick

        let rows, _ =
            gameOfLifeGridUnrolled gensPerCycle gridWidth gridHeight coreLoad coreTick loadRows

        // ---- the snapshot path: rows paired into 128-bit beats, conflated
        // across three DDR slots, written by the master whose drained level
        // gates slot publication (P1's operator, in anger).
        let beatRows =
            rows
            |> List.chunkBySize rowsPerBeat
            |> List.map (fun chunk ->
                match chunk with
                | low :: rest -> List.fold (fun acc r -> cat r acc) low rest
                | [] -> failwith "unreachable: rowsPerBeat >= 1")

        let writerIdle = wireBit "writer_idle_w"

        let beats, snapStatus =
            snapshotSource "snap" beatRows
            |> streamConflate3 "conflate" (regs.pulse m.snapCapture) (regs.pulse m.snapRelease) writerIdle

        // Nothing enters the write path until the host has set a base
        // address. From bitstream load until the driver arrives the
        // interconnect is still leaving reset, and transactions issued into
        // that window are partially dropped — AW/W pairing then skews
        // permanently (the per-boot +8/+9-beat DDR frame rotation: silicon
        // only, invisible to every cycle-accurate harness). Arming also
        // stops the unarmed writer from hammering physical 0x0, fbBaseAddr's
        // reset value.
        let armed = wireBit "writer_armed"
        bnot (eq fbBaseAddr (lit 0UL 32)) ==> armed

        (beats
             |> streamMapTo (axiWriteBeatLayout 32 128) (fun (slot, index, data) ->
                 let offset = cat (cat slot index) (lit 0UL 4)
                 fbBaseAddr + cat (lit 0UL (32 - 2 - beatIndexBits - 4)) offset, data, lit 0xFFFFUL 16)
             |> fun s ->
                 let armedReady = wireBit "armed_ready"
                 armedReady &&& armed ==> s.ready

                 axiMasterWriterWithIdle
                     32
                     128
                     16
                     { s with
                         valid = s.valid &&& armed
                         ready = armedReady })
        ==> writerIdle

        // ---- status and interrupts.
        // The population count is pipelined twice. A flat popcount straight
        // off width*height cell registers into the read mux was the design's
        // worst path at 166.67 MHz (WNS -0.79); registering it left the tree
        // itself route-bound (WNS -0.105, 75% route — it gathers the whole
        // grid in one cycle). So stage one counts each row where it lives,
        // stage two sums the row counts. Population reads two cycles stale,
        // which no consumer notices.
        let populationWidth = bitsNeeded (gridWidth * gridHeight)
        let rowCountWidth = bitsNeeded gridWidth

        let rowCounts =
            [ for y, row in List.indexed rows ->
                  let named = wire $"pop_row_%d{y}" gridWidth
                  row ==> named
                  let rowCount = reg $"pop_row_count_%d{y}" rowCountWidth

                  countWhere rowCountWidth id [ for x in 0 .. gridWidth - 1 -> slice x x named ]
                  ==> rowCount

                  rowCount ]

        let populationReg = reg "population_reg" populationWidth

        reduceTree (+) [ for c in rowCounts -> cat (lit 0UL (populationWidth - rowCountWidth)) c ]
        ==> populationReg

        regs.drive m.busy busyReg
        regs.drive m.population populationReg
        regs.drive m.generation generationReg
        regs.drive m.snapReady snapStatus.ready
        regs.drive m.snapOverrun snapStatus.overrun
        regs.drive m.snapSlot snapStatus.readSlot
        regs.setBit m.burstIrq burstDoneQ
        regs.setBit m.snapIrq snapStatus.irq

        let irqOut = outputBit "irq"
        regs.irq ==> irqOut)

/// The rehearsal config: every mechanism live at a size the Sim walks in
/// seconds — 16×16 packs 8 rows per beat, 2 beats per frame.
let golAxiScaled = golAxi "GolAxiScaled" 1 16 16

/// The scaled config unrolled — the differential's coverage of the k > 1
/// wrapper path (generation-by-k accounting, the composed grid in the
/// snapshot stream).
let golAxiScaledX2 = golAxi "GolAxiScaledX2" 2 16 16

/// Generations per clock in the silicon config: measured OOC, k=3 closes
/// the 6 ns budget at 4.877 ns with 47k grid LUTs (40%) — k=4 does not
/// (7.464 ns, 98k LUTs). The seam carries this so drivers convert between
/// generations and fires.
let golGensPerCycle = 3

/// The silicon config. Lazy, so only the seam emit pays the elaboration.
let golAxiFull = lazy (golAxi "GolAxi" golGensPerCycle 64 64)
