/// **An SDR SDRAM behind an AXI slave** — the memory chip a board without a
/// processing system carries (the Icepi Zero's 32 MiB, the DE10-Lite's 64),
/// presented to the fabric the way the KV260's PS presents its DDR: a read
/// bus and a write bus. So a region placed in it is `HostMemoryRegions` over
/// buses this module answers, and nothing above the bus learns which memory
/// it got (`notes/DEVICES.md` §13, carrier D).
///
/// One transaction at a time, row by row: activate, a column command a cycle
/// at burst length one, precharge — and an auto-refresh between transactions
/// whenever one is due. Slow next to the chip's peak and simple enough to be
/// right; the timing is the chip's, in nanoseconds, made cycles at the
/// fabric's clock here.
[<AutoOpen>]
module Warp11.Sdram

open Warp11

/// The chip's bytes.
let sdramBytes (chip: SdramChip) =
    (1 <<< (chip.bankBits + chip.rowBits + chip.columnBits)) * (chip.dataWidth / 8)

/// The chip's pins as the fabric drives them. The data bus is bidirectional
/// on the board; here it is its two halves and the enable, and the board's
/// wrapper joins them into the one pin — so the Sim, the differential and
/// any other target meet ordinary ports.
type SdramPins =
    { clk: Output
      cke: Output
      csN: Output
      rasN: Output
      casN: Output
      weN: Output
      ba: Output
      a: Output
      dqm: Output
      dqOut: Output
      dqOe: Output
      dqIn: Input }

/// Declare a chip's pins in an io factory, as `sdram_*`.
let sdramPins (p: Ports) (chip: SdramChip) : SdramPins =
    { clk = p.outPort "sdram_clk" 1
      cke = p.outPort "sdram_cke" 1
      csN = p.outPort "sdram_csn" 1
      rasN = p.outPort "sdram_rasn" 1
      casN = p.outPort "sdram_casn" 1
      weN = p.outPort "sdram_wen" 1
      ba = p.outPort "sdram_ba" chip.bankBits
      a = p.outPort "sdram_a" (max chip.rowBits (chip.columnBits + 2))
      dqm = p.outPort "sdram_dqm" (chip.dataWidth / 8)
      dqOut = p.outPort "sdram_dq_out" chip.dataWidth
      dqOe = p.outPort "sdram_dq_oe" 1
      dqIn = p.inPort "sdram_dq_in" chip.dataWidth }

/// Cycles at `fabricHz` that cover `ns`, at least one.
let private cyclesFor (fabricHz: int) (ns: float) =
    max 1 (int (ceil (ns * float fabricHz / 1e9)))

/// The controller: the chip on `pins`, answering `readBus` and `writeBus` as
/// an AXI slave whose beats are two chip words wide. A burst is in bytes
/// from zero and must stay inside the chip; a row boundary inside one is
/// crossed by closing the row and opening the next. Writes are answered on B
/// once their last word is in the chip and the write recovery has run.
let sdramController (fabricHz: int) (chip: SdramChip) (pins: SdramPins) (readBus: AxiReadBus) (writeBus: AxiWriteBus) =
    let named (what: string) = $"sdram_{what}"
    let beatWidth = width readBus.rdata

    if beatWidth <> 2 * chip.dataWidth || width writeBus.wdata <> beatWidth then
        failwith $"the SDRAM's buses are two chip words a beat, %d{2 * chip.dataWidth} bits, not %d{beatWidth}"

    let cycles = cyclesFor fabricHz
    let tRcd = cycles chip.tRcdNs
    let tRp = cycles chip.tRpNs
    let tRfc = cycles chip.tRfcNs
    let tWr = cycles chip.tWrNs + 1
    let tRefi = int (chip.tRefiNs * float fabricHz / 1e9)
    let initCycles = int (chip.initUs * float fabricHz / 1e6)
    let cl = chip.casLatency
    let addrPins = width pins.a
    let wordAddrBits = chip.bankBits + chip.rowBits + chip.columnBits
    let waitWidth = ceilLog2 (max initCycles tRefi + 2)

    // ---- the command bus, registered so the chip sees clean edges --------
    let command = reg (named "command") 4 // cs ras cas we, active low
    let nop = lit 0b0111UL 4
    let ba = reg (named "ba_q") chip.bankBits
    let a = reg (named "a_q") addrPins
    let dqOut = reg (named "dq_out_q") chip.dataWidth
    let dqOe = regBit (named "dq_oe_q")
    nop ==> command
    lit 0UL 1 ==> dqOe

    lit 1UL 1 ==> pins.cke
    lit 0UL 1 ==> pins.clk // the board's wrapper drives the chip's clock from the fabric's
    slice 3 3 command ==> pins.csN
    slice 2 2 command ==> pins.rasN
    slice 1 1 command ==> pins.casN
    slice 0 0 command ==> pins.weN
    ba ==> pins.ba
    a ==> pins.a
    lit 0UL (chip.dataWidth / 8) ==> pins.dqm
    dqOut ==> pins.dqOut
    dqOe ==> pins.dqOe

    let issue (bits: uint64) = lit bits 4 ==> command
    let activateCmd, readCmd, writeCmd, prechargeCmd, refreshCmd, modeCmd = 0b0011UL, 0b0101UL, 0b0100UL, 0b0010UL, 0b0001UL, 0b0000UL

    // ---- the transaction held: which bus, where, how many words ----------
    let wait = reg (named "wait") waitWidth
    let waiting = wireBit (named "waiting")
    bnot (eq wait (lit 0UL waitWidth)) ==> waiting
    If waiting (fun () -> wait - lit 1UL waitWidth ==> wait)
    let after (n: int) = lit (uint64 (max 0 (n - 1))) waitWidth ==> wait

    let isWrite = regBit (named "is_write")
    let word = reg (named "word") wordAddrBits // the chip word being reached
    let left = reg (named "left") 10 // chip words still to command
    let column = wire (named "column") chip.columnBits
    slice (chip.columnBits - 1) 0 word ==> column
    let row = wire (named "row") chip.rowBits
    slice (wordAddrBits - 1) (chip.columnBits + chip.bankBits) word ==> row
    let bank = wire (named "bank") chip.bankBits
    slice (chip.columnBits + chip.bankBits - 1) chip.columnBits word ==> bank
    let rowEnds = wireBit (named "row_ends")
    eq column (lit ((1UL <<< chip.columnBits) - 1UL) chip.columnBits) ==> rowEnds

    // ---- refresh: owed every tRefi, paid between transactions -------------
    let sinceRefresh = reg (named "since_refresh") waitWidth
    let refreshOwed = reg (named "refresh_owed") 4
    let refreshTick = wireBit (named "refresh_tick")
    eq sinceRefresh (lit (uint64 (tRefi - 1)) waitWidth) ==> refreshTick
    let refreshPaid = wireBit (named "refresh_paid")

    ifElse [ (refreshTick, fun () -> lit 0UL waitWidth ==> sinceRefresh)
             (otherwise, fun () -> sinceRefresh + lit 1UL waitWidth ==> sinceRefresh) ]

    // Owed only once the chip is up: the power-up sequence refreshes it.
    let up = regBit (named "up")
    refreshOwed + pad 4 (refreshTick &&& up) - pad 4 refreshPaid ==> refreshOwed

    // ---- the read data: CL cycles behind its command, two words a beat -----
    // A read column is commanded only when the beat it completes has room,
    // so nothing the chip answers is ever dropped.
    let landing = [ for k in 0 .. cl -> regBit (named $"landing_%d{k}") ]
    let landingLast = [ for k in 0 .. cl -> regBit (named $"landing_last_%d{k}") ]
    let readIssued = wireBit (named "read_issued")
    let readIssuedLast = wireBit (named "read_issued_last")
    readIssued ==> landing[0]
    readIssuedLast ==> landingLast[0]

    for k in 1 .. cl do
        landing[k - 1] ==> landing[k]
        landingLast[k - 1] ==> landingLast[k]

    let lowWord = reg (named "low_word") chip.dataWidth
    let haveLow = regBit (named "have_low")
    let beatLayout = layout2 ("data", beatWidth) ("last", 1)
    let beatInReady = wireBit (named "beat_in_ready")
    registerStreamReady beatInReady
    let arrived = wireBit (named "arrived")
    landing[cl] ==> arrived

    If arrived (fun () ->
        ifElse [ (haveLow, fun () -> lit 0UL 1 ==> haveLow)
                 (otherwise, fun () ->
                     pins.dqIn ==> lowWord
                     lit 1UL 1 ==> haveLow) ])

    let beats =
        ({ payload = cat pins.dqIn lowWord, landingLast[cl]
           valid = arrived &&& haveLow
           ready = beatInReady
           layout = beatLayout }
        : Stream<Expr * Expr>)
        |> streamFifo (named "read_beats") 16

    // Room for every word in flight: the FIFO's free beats against the words
    // commanded and not yet landed.
    let inFlight = reg (named "in_flight") 6
    let held = reg (named "held") 6
    let beatFire = wireBit (named "beat_fire")
    let beatOut = wireBit (named "beat_out")
    (arrived &&& haveLow) ==> beatFire
    inFlight + pad 6 readIssued - pad 6 arrived ==> inFlight
    held + pad 6 beatFire - pad 6 beatOut ==> held
    let room = wireBit (named "room")
    let flightRounded = wire (named "flight_rounded") 6
    inFlight + lit 2UL 6 ==> flightRounded
    let beatsCommitted = wire (named "beats_committed") 7
    (pad 7 held + pad 7 (slice 5 1 flightRounded)) ==> beatsCommitted
    lt beatsCommitted (lit 14UL 7) ==> room

    let data, last = beats.payload
    data ==> readBus.rdata
    last ==> readBus.rlast
    beats.valid ==> readBus.rvalid
    readBus.rready ==> beats.ready
    (beats.valid &&& readBus.rready) ==> beatOut

    // ---- the machine ---------------------------------------------------------
    let initWait, initRefresh, initMode, idle, activate, columns, closing, responding =
        0UL, 2UL, 3UL, 4UL, 6UL, 7UL, 8UL, 9UL

    let state = regInit (named "state") 4 initWait
    let isIn (s: uint64) = eq state (lit s 4)
    let goto (s: uint64) = lit s 4 ==> state
    let initRefreshes = reg (named "init_refreshes") 2
    let highHalf = regBit (named "high_half")

    // Requests: AR and AW, one at a time, taking turns when both wait — a
    // steady stream of either would otherwise starve the other, and a reader
    // waiting on a write it feeds would never be answered.
    let takeRead = wireBit (named "take_read")
    let takeWrite = wireBit (named "take_write")
    let idleNow = wireBit (named "idle_now")
    let lastWasRead = regBit (named "last_was_read")
    (isIn idle &&& bnot waiting &&& eq refreshOwed (lit 0UL 4)) ==> idleNow
    (idleNow &&& readBus.arvalid &&& (bnot writeBus.awvalid ||| bnot lastWasRead)) ==> takeRead
    (idleNow &&& writeBus.awvalid &&& (bnot readBus.arvalid ||| lastWasRead)) ==> takeWrite
    If takeRead (fun () -> lit 1UL 1 ==> lastWasRead)
    If takeWrite (fun () -> lit 0UL 1 ==> lastWasRead)
    takeRead ==> readBus.arready
    takeWrite ==> writeBus.awready

    // The chip word a bus address names: bytes over the chip's two.
    let wordOf (name: string) (byteAddr: Expr) =
        let w = wire (named name) (width byteAddr)
        byteAddr ==> w
        let word = wire (named $"{name}_word") wordAddrBits
        slice wordAddrBits 1 w ==> word
        word

    let readWord = wordOf "ar_addr" readBus.araddr
    let writeWord = wordOf "aw_addr" writeBus.awaddr

    // The W beat is taken on its high word; its low word goes out first.
    let wFire = wireBit (named "w_fire")
    let bValid = regBit (named "b_valid")
    bValid ==> writeBus.bvalid

    let commanding = wireBit (named "commanding")
    (isIn columns &&& bnot waiting) ==> commanding
    let readCanGo = wireBit (named "read_can_go")
    (commanding &&& bnot isWrite &&& room) ==> readCanGo
    let writeCanGo = wireBit (named "write_can_go")
    (commanding &&& isWrite &&& writeBus.wvalid) ==> writeCanGo
    (writeCanGo &&& highHalf) ==> wFire
    wFire ==> writeBus.wready
    readCanGo ==> readIssued
    (readCanGo &&& eq left (lit 1UL 10)) ==> readIssuedLast
    let columnGoes = wireBit (named "column_goes")
    (readCanGo ||| writeCanGo) ==> columnGoes
    lit 0UL 1 ==> refreshPaid

    let rowAddress = pad addrPins row
    let columnAddress = pad addrPins column

    ifElse
        [ (isIn initWait &&& bnot waiting,
           fun () ->
               // The power-up wait, counted once from reset.
               ifElse [ (eq initRefreshes (lit 0UL 2), fun () ->
                            after initCycles
                            lit 1UL 2 ==> initRefreshes)
                        (otherwise, fun () ->
                            issue prechargeCmd
                            lit (1UL <<< 10) addrPins ==> a // all banks
                            lit 0UL 2 ==> initRefreshes
                            after tRp
                            goto initRefresh) ])
          (isIn initRefresh &&& bnot waiting,
           fun () ->
               issue refreshCmd
               after tRfc
               ifElse [ (eq initRefreshes (lit 1UL 2), fun () -> goto initMode)
                        (otherwise, fun () -> lit 1UL 2 ==> initRefreshes) ])
          (isIn initMode &&& bnot waiting,
           fun () ->
               // Burst length one, sequential, CAS latency `cl`, writes as reads.
               issue modeCmd
               lit 0UL chip.bankBits ==> ba
               lit (uint64 (cl <<< 4)) addrPins ==> a
               after chip.tMrdCycles
               lit 1UL 1 ==> up
               goto idle)
          (isIn idle &&& bnot waiting &&& bnot (eq refreshOwed (lit 0UL 4)),
           fun () ->
               issue refreshCmd
               lit 1UL 1 ==> refreshPaid
               after tRfc)
          (takeRead,
           fun () ->
               lit 0UL 1 ==> isWrite
               readWord ==> word
               (cat (lit 0UL 1) (cat readBus.arlen (lit 0UL 1)) + lit 2UL 10) ==> left
               goto activate)
          (takeWrite,
           fun () ->
               lit 1UL 1 ==> isWrite
               writeWord ==> word
               (cat (lit 0UL 1) (cat writeBus.awlen (lit 0UL 1)) + lit 2UL 10) ==> left
               lit 0UL 1 ==> highHalf
               goto activate)
          (isIn activate &&& bnot waiting,
           fun () ->
               issue activateCmd
               bank ==> ba
               rowAddress ==> a
               after tRcd
               goto columns)
          (columnGoes,
           fun () ->
               ifElse [ (isWrite, fun () -> issue writeCmd)
                        (otherwise, fun () -> issue readCmd) ]

               bank ==> ba
               columnAddress ==> a
               mux highHalf (slice (2 * chip.dataWidth - 1) chip.dataWidth writeBus.wdata) (slice (chip.dataWidth - 1) 0 writeBus.wdata) ==> dqOut
               isWrite ==> dqOe
               bnot highHalf ==> highHalf
               word + lit 1UL wordAddrBits ==> word
               left - lit 1UL 10 ==> left

               // The last word, or the row's last column: close the row. A
               // burst that goes on reopens on the next row.
               If (eq left (lit 1UL 10) ||| rowEnds) (fun () ->
                   // Write recovery before the precharge, and a read's
                   // last word out of the chip before its row closes.
                   after (max tWr (cl + 1))
                   goto closing))
          (isIn closing &&& bnot waiting,
           fun () ->
               issue prechargeCmd
               lit (1UL <<< 10) addrPins ==> a
               after tRp

               ifElse [ (bnot (eq left (lit 0UL 10)), fun () -> goto activate)
                        (isWrite, fun () -> goto responding)
                        (otherwise, fun () -> goto idle) ])
          (isIn responding &&& bnot waiting &&& bnot bValid,
           fun () ->
               lit 1UL 1 ==> bValid
               goto idle) ]

    If (bValid &&& writeBus.bready) (fun () -> lit 0UL 1 ==> bValid)

/// A behavioural SDR SDRAM on a design's `sdram_*` pins, for the Sim: it
/// decodes the commands the controller registers, keeps the chip's words,
/// answers a read `casLatency` cycles on, and **refuses** what a real chip
/// would silently get wrong — a command before the power-up wait, a column
/// command to a closed bank or before tRCD, an activate to an open bank or
/// before tRP, anything inside tRFC, a refresh interval past the eight a
/// chip may postpone. A refused command is a violation, listed rather than
/// thrown, so a check can say what went wrong.
type SimSdram(sim: Sim, chip: SdramChip, fabricHz: int) =
    let cycles (ns: float) = max 1 (int (ceil (ns * float fabricHz / 1e9)))
    let words = System.Collections.Generic.Dictionary<int, uint64>()
    let banks = 1 <<< chip.bankBits
    let openRow: int option[] = Array.create banks None
    let activatedAt = Array.create banks -1000
    let prechargedAt = Array.create banks -1000
    let reads = System.Collections.Generic.Queue<int * int>() // (cycle due, chip word)
    let violations = ResizeArray<string>()
    let mutable cycle = 0
    let mutable lastRefresh = 0
    let mutable refreshUntil = 0
    let mutable modeSet = false
    let mutable casLatency = 0
    let mutable dqIn = 0UL
    let initCycles = int (chip.initUs * float fabricHz / 1e6)
    let tRefi = int (chip.tRefiNs * float fabricHz / 1e9)
    let wordOf bank row column = (((row <<< chip.bankBits) ||| bank) <<< chip.columnBits) ||| column
    let refuse (why: string) = violations.Add $"cycle %d{cycle}: {why}"

    member _.Violations = List.ofSeq violations
    member _.Word(index: int) = match words.TryGetValue index with | true, v -> v | _ -> 0UL
    member _.SetWord(index: int, value: uint64) = words[index] <- value

    interface ISimDevice with
        member _.Drive() = sim.Poke("sdram_dq_in", dqIn)

        member _.Sample() =
            let csN = sim.Peek "sdram_csn"
            let command = (sim.Peek "sdram_rasn" <<< 2) ||| (sim.Peek "sdram_casn" <<< 1) ||| sim.Peek "sdram_wen"
            let bank = int (sim.Peek "sdram_ba")
            let a = int (sim.Peek "sdram_a")
            let column = a &&& ((1 <<< chip.columnBits) - 1)

            if csN = 0UL && command <> 0b111UL then
                if cycle < initCycles then refuse "a command inside the power-up wait"
                if cycle < refreshUntil then refuse "a command inside tRFC"

                match command with
                | 0b011UL -> // activate
                    if openRow[bank].IsSome then refuse $"activate to bank %d{bank} with a row open"
                    if cycle - prechargedAt[bank] < cycles chip.tRpNs then refuse $"activate to bank %d{bank} inside tRP"
                    openRow[bank] <- Some a
                    activatedAt[bank] <- cycle
                | 0b101UL
                | 0b100UL -> // read, write
                    if not modeSet then refuse "a column command before the mode register is set"

                    match openRow[bank] with
                    | None -> refuse $"a column command to bank %d{bank} with no row open"
                    | Some row ->
                        if cycle - activatedAt[bank] < cycles chip.tRcdNs then refuse $"a column command to bank %d{bank} inside tRCD"

                        if command = 0b101UL then
                            reads.Enqueue(cycle + casLatency, wordOf bank row column)
                        else
                            if sim.Peek "sdram_dq_oe" <> 1UL then refuse "a write with the data bus not driven"
                            words[wordOf bank row column] <- sim.Peek "sdram_dq_out"
                | 0b010UL -> // precharge
                    let all = (a >>> 10) &&& 1 = 1

                    for b in 0 .. banks - 1 do
                        if all || b = bank then
                            openRow[b] <- None
                            prechargedAt[b] <- cycle
                | 0b001UL -> // refresh
                    if openRow |> Array.exists Option.isSome then refuse "a refresh with a row open"
                    if modeSet && cycle - lastRefresh > 9 * tRefi then refuse $"a refresh %d{cycle - lastRefresh} cycles after the last, past nine intervals"
                    lastRefresh <- cycle
                    refreshUntil <- cycle + cycles chip.tRfcNs
                | _ -> // mode register
                    modeSet <- true
                    casLatency <- (a >>> 4) &&& 7

            if modeSet && cycle - lastRefresh > 9 * tRefi then
                refuse "no refresh for nine intervals"
                lastRefresh <- cycle

            // What the chip drives next: a read latched with the pins of
            // cycle `s` puts its word on the bus for cycle `s + casLatency`,
            // which the controller samples at that cycle's closing edge — the
            // second edge after the chip took the command, at latency two.
            dqIn <- 0UL

            while reads.Count > 0 && fst (reads.Peek()) <= cycle do
                let _, w = reads.Dequeue()
                dqIn <- (match words.TryGetValue w with | true, v -> v | _ -> 0UL)

            cycle <- cycle + 1

/// A read bus between a master and a slave in the same module: wires, not
/// ports.
let axiReadBusBetween (prefix: string) (addrWidth: int) (dataWidth: int) : AxiReadBus =
    { prefix = prefix
      araddr = wire $"{prefix}_araddr" addrWidth
      arlen = wire $"{prefix}_arlen" 8
      arvalid = wireBit $"{prefix}_arvalid"
      arready = wireBit $"{prefix}_arready"
      rdata = wire $"{prefix}_rdata" dataWidth
      rlast = wireBit $"{prefix}_rlast"
      rvalid = wireBit $"{prefix}_rvalid"
      rready = wireBit $"{prefix}_rready"
      claimed = ref false }

/// The write bus the same way.
let axiWriteBusBetween (prefix: string) (addrWidth: int) (dataWidth: int) : AxiWriteBus =
    { prefix = prefix
      awaddr = wire $"{prefix}_awaddr" addrWidth
      awlen = wire $"{prefix}_awlen" 8
      awvalid = wireBit $"{prefix}_awvalid"
      awready = wireBit $"{prefix}_awready"
      wdata = wire $"{prefix}_wdata" dataWidth
      wstrb = wire $"{prefix}_wstrb" (dataWidth / 8)
      wlast = wireBit $"{prefix}_wlast"
      wvalid = wireBit $"{prefix}_wvalid"
      wready = wireBit $"{prefix}_wready"
      bvalid = wireBit $"{prefix}_bvalid"
      bready = wireBit $"{prefix}_bready"
      claimed = ref false }

/// Regions in an SDRAM on `pins`: placed in the chip from address zero, on
/// one read and one write master the controller answers. Call `Finish`
/// once the design has taken its ports, as with the host's memory.
let sdramRegions (fabricHz: int) (chip: SdramChip) (pins: SdramPins) (specs: RegionSpec list) : HostMemoryRegions =
    let beat = 2 * chip.dataWidth
    let places = placeRegions beat (sdramBytes chip) specs
    let readBus = axiReadBusBetween "sdram_rd" 32 beat
    let writeBus = axiWriteBusBetween "sdram_wr" 32 beat
    sdramController fabricHz chip pins readBus writeBus

    let readers = places |> List.filter (fun p -> regionReads p.spec.usage)
    let writers = places |> List.filter (fun p -> regionWrites p.spec.usage)

    // A direction nothing uses has no master: its side of the bus is tied
    // quiet, so the controller sees no requests on it.
    if readers.IsEmpty then
        lit 0UL 32 ==> readBus.araddr
        lit 0UL 8 ==> readBus.arlen
        lit 0UL 1 ==> readBus.arvalid
        lit 1UL 1 ==> readBus.rready

    if writers.IsEmpty then
        lit 0UL 32 ==> writeBus.awaddr
        lit 0UL 8 ==> writeBus.awlen
        lit 0UL 1 ==> writeBus.awvalid
        lit 0UL beat ==> writeBus.wdata
        lit 0UL (beat / 8) ==> writeBus.wstrb
        lit 0UL 1 ==> writeBus.wlast
        lit 0UL 1 ==> writeBus.wvalid
        lit 1UL 1 ==> writeBus.bready

    boardMemoryRegions
        [ if not readers.IsEmpty then yield readBus, readers ]
        [ if not writers.IsEmpty then yield writeBus, writers ]
        regionReadOutstanding
        4
