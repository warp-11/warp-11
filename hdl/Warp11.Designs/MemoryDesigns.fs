/// The memory half of the oracle catalog: everything whose subject is
/// *storage* — where a word lives, when it can be read back, which lanes a
/// write reaches, and whether the words are on this chip at all.
///
/// Split out of `Designs.fs` because storage turned out to be one question with
/// several answers rather than several topics. A design wants capacity and a
/// read; whether that is satisfied by LUTs, by a block, by a preloaded table or
/// by an AXI master onto PS DDR is a fact about the board. Keeping every answer
/// in one file is what makes the distance between them legible, and it is the
/// file `notes/DEVICES.md`'s UC2 is written against.
///
/// These are differential-oracle inputs first and demonstrations second, which
/// is why the shapes are small and the stimulus is adversarial: three-bit
/// addresses collide constantly across 50 random cycles, and enables a
/// hand-written test would drive one at a time arrive together.
[<AutoOpen>]
module Warp11.Designs.MemoryCatalog

open Warp11

// ---------------------------------------------------------------------------
// Reading: now, or next cycle
// ---------------------------------------------------------------------------

/// A 8x8 RAM with a write under On, a sync read and an async read. The oracle's
/// random 3-bit addresses collide constantly across 50 cycles, so read-first —
/// the semantics sim and silicon must agree on — is differentially exercised
/// rather than asserted.
let ramTest =
    defModule
        "RamTest"
        (fun p ->
            (p.inPort "waddr" 3,
             p.inPort "wdata" 8,
             p.inPort "wen" 1,
             p.inPort "raddr" 3,
             p.outPort "next_cycle_out" 8,
             p.outPort "this_cycle_out" 8))
        (fun (waddr, wdata, wen, raddr, nextCycleOut, thisCycleOut) ->
            let store = distributedMem "store" 3 8
            If wen (fun () -> memWrite store waddr wdata (lit 1UL 1))
            (memReadPort store raddr).data ==> nextCycleOut
            memRead store raddr ==> thisCycleOut)

/// A memory read with the caller's own values carried through it.
///
/// The tag and the request's valid go in with the address and come back
/// attached to the word — which is the whole claim, and the reason the port
/// exists rather than the caller writing a register and remembering to. Nothing
/// here names a latency; `through` delays by whatever the port's depth is.
let carriedRead =
    defModule
        "CarriedRead"
        (fun p ->
            (p.inPort "waddr" 4,
             p.inPort "wdata" 8,
             p.inPort "wen" 1,
             p.inPort "raddr" 4,
             p.inPort "tag" 8,
             p.inPort "ask" 1,
             p.outPort "data" 8,
             p.outPort "tag_out" 8,
             p.outPort "answered" 1))
        (fun (waddr, wdata, wen, raddr, tag, ask, data, tagOut, answered) ->
            let store = blockMem "store" 4 8
            If wen (fun () -> memWrite store waddr wdata (lit 1UL 1))

            let read = memReadPort store raddr
            read.data ==> data
            read.through "tag" tag ==> tagOut
            read.through "ask" ask ==> answered)

/// Two reads of one memory at two addresses, in the same cycle.
///
/// Writes fold and reads do not, and that asymmetry is deliberate. Several
/// `memWrite` calls become one priority-muxed write site because two write
/// sites stop a synthesiser inferring a block RAM. `memRead` returns an
/// expression, may be called any number of times, and nothing counts ports — a
/// second simultaneous read of a LUTRAM is a second *copy* of the array, and
/// the cost arrives as LUTs in the utilisation report rather than as an error.
/// Replication is usually what a caller wanted; the point of this design is
/// that it is a choice being made, not a free lunch.
///
/// Both storages are here because they replicate differently: LUTRAM duplicates
/// the array, a block has two hard ports and a third read would cost a copy.
/// The two addresses are independent inputs, so the oracle drives them apart
/// constantly — a port answering its neighbour's address would otherwise be
/// invisible in every cycle the two happened to agree.
let dualRead =
    defModule
        "DualRead"
        (fun p ->
            (p.inPort "waddr" 3,
             p.inPort "wdata" 8,
             p.inPort "wen" 1,
             p.inPort "addr_a" 3,
             p.inPort "addr_b" 3,
             p.outPort "now_a" 8,
             p.outPort "now_b" 8,
             p.outPort "next_a" 8,
             p.outPort "next_b" 8))
        (fun (waddr, wdata, wen, addrA, addrB, nowA, nowB, nextA, nextB) ->
            let lut = distributedMem "lut" 3 8
            If wen (fun () -> memWrite lut waddr wdata (lit 1UL 1))
            memRead lut addrA ==> nowA
            memRead lut addrB ==> nowB

            let deep = blockMem "deep" 3 8
            If wen (fun () -> memWrite deep waddr wdata (lit 1UL 1))
            (memReadPort deep addrA).data ==> nextA
            (memReadPort deep addrB).data ==> nextB)

/// A 256-word memory that fills itself: one word per cycle while `run` is high,
/// each holding 3× its own address plus one, so a wrong word is obvious by
/// inspection. The catalog's other memories are eight words deep, which is
/// small enough to read at a glance and therefore no test of anything that has
/// to *page* through a memory.
let fillingMemory =
    defModule
        "FillingMemory"
        (fun p -> (p.inPort "run" 1, p.outPort "addr" 8, p.outPort "word" 16))
        (fun (run, addr, word) ->
            let store = distributedMem "store" 8 16
            let ptr = reg "ptr" 8
            let wide = wire "wide" 16
            let value = wire "value" 16

            cat (lit 0UL 8) ptr ==> wide
            wide + wide + wide + lit 1UL 16 ==> value

            If run (fun () ->
                memWrite store ptr value (lit 1UL 1)
                ptr + lit 1UL 8 ==> ptr)

            ptr ==> addr
            (memReadPort store ptr).data ==> word)

// ---------------------------------------------------------------------------
// Writing: several sites, one write port
// ---------------------------------------------------------------------------

/// Three write sites on one memory, deliberately firing together.
///
/// Several `memWrite` calls on one mem **fold to a single priority-muxed write
/// site**: the address, the data and the enable each become a mux chain, so the
/// emitted Verilog holds exactly one `store[...] <=` however many places wrote.
/// That is what makes Vivado's "two write sites kill block-RAM inference"
/// gotcha a property of the DSL here rather than a discipline everyone has to
/// remember.
///
/// **The last site in the source wins.** All three enables are ports, so the
/// oracle drives every combination including all three at once, which is the
/// case a fold that reduced from the wrong end would get exactly backwards.
/// Each site writes a value naming itself and a different address, so the word
/// read back says which one won rather than merely being plausible.
let priorityWrite =
    defModule
        "PriorityWrite"
        (fun p ->
            (p.inPort "addr" 3,
             p.inPort "raddr" 3,
             p.inPort "low_enable" 1,
             p.inPort "mid_enable" 1,
             p.inPort "high_enable" 1,
             p.outPort "word" 8))
        (fun (addr, raddr, lowEnable, midEnable, highEnable, word) ->
            let store = distributedMem "store" 3 8

            memWrite store addr (lit 0x11UL 8) lowEnable
            memWrite store (addr + lit 1UL 3) (lit 0x22UL 8) midEnable
            memWrite store (addr + lit 2UL 3) (lit 0x33UL 8) highEnable

            memRead store raddr ==> word)

/// A byte-enabled memory: the write reaches only the lanes its strobe selects.
///
/// Four 8-bit lanes in a 32-bit word — AXI's `wstrb`, and the shape a
/// synthesiser turns into one block RAM with four write-enables rather than
/// read-modify-write logic. Each lane carries a different byte of `wdata` so a
/// strobe that reached the wrong lane is visible, and the word is read back
/// whole: what the check is really asserting is that the lanes the strobe left
/// alone still hold what a *previous* write put there.
let maskedWrite =
    defModule
        "MaskedWrite"
        (fun p ->
            (p.inPort "waddr" 3,
             p.inPort "wdata" 32,
             p.inPort "wstrb" 4,
             p.inPort "wen" 1,
             p.inPort "raddr" 3,
             p.outPort "rdata" 32))
        (fun (waddr, wdata, wstrb, wen, raddr, rdata) ->
            let store = blockMem "store" 3 32
            memWriteMasked store waddr wdata wen wstrb

            (memReadPort store raddr).data ==> rdata)

/// The masked write at a width no uint64 can hold: a 128-bit word in four
/// 32-bit lanes.
///
/// Same contract as `maskedWrite`, and deliberately the same shape — what this
/// design exists to exercise is the simulator's *wide* memory store (BigInteger
/// words, BigInteger keep masks), which the 64-bit toy structurally cannot
/// reach. It is the shape GEP's merged tables take, proven here first.
let maskedWriteWide =
    defModule
        "MaskedWriteWide"
        (fun p ->
            (p.inPort "waddr" 3,
             p.inPort "wdata" 128,
             p.inPort "wstrb" 4,
             p.inPort "wen" 1,
             p.inPort "raddr" 3,
             p.outPort "rdata" 128))
        (fun (waddr, wdata, wstrb, wen, raddr, rdata) ->
            let store = blockMem "store" 3 128
            memWriteMasked store waddr wdata wen wstrb

            (memReadPort store raddr).data ==> rdata)

/// Two masked writes at one address with complementary masks, firing together.
///
/// **Masks do not merge across write sites.** The priority pick happens first
/// and the winner's mask applies second, so two writes covering opposite halves
/// of a word do *not* both land — the later one wins outright and the other
/// half keeps whatever a previous cycle put there. That is the only sane
/// reading once the two sites may also disagree about the address, and it is
/// the trap in the shape GEP's case-table fill takes: two masked writes, one
/// filling the variable lanes and one dropping a word into the target lane,
/// which are safe only because `sel` makes them mutually exclusive.
///
/// Here they are deliberately *not* exclusive. `low_enable` and `high_enable`
/// are independent ports, so the both-high case — the one where the intuitive
/// reading and the real one differ — arrives roughly a quarter of the time
/// under the oracle's stimulus.
let maskedWritePriority =
    defModule
        "MaskedWritePriority"
        (fun p ->
            (p.inPort "addr" 3,
             p.inPort "raddr" 3,
             p.inPort "low_data" 32,
             p.inPort "high_data" 32,
             p.inPort "low_enable" 1,
             p.inPort "high_enable" 1,
             p.outPort "rdata" 32))
        (fun (addr, raddr, lowData, highData, lowEnable, highEnable, rdata) ->
            let store = blockMem "store" 3 32

            memWriteMasked store addr lowData lowEnable (lit 0x3UL 4)
            memWriteMasked store addr highData highEnable (lit 0xCUL 4)

            (memReadPort store raddr).data ==> rdata)

// ---------------------------------------------------------------------------
// Contents that arrive with the bitstream
// ---------------------------------------------------------------------------

/// A preloaded table in LUTs, read combinationally — the small-lookup shape.
///
/// The contents are part of the declaration, so nothing writes this table and
/// nothing has to load it: `distributedRom` is `distributedMem`'s rule with
/// `rom`'s contents, which means the combinational read is legal here for
/// exactly the reason it is legal there. The values are the squares, chosen
/// because a table read at the wrong index still returns a *plausible* number —
/// the check walks every address rather than sampling one.
///
/// Address width is derived from the array, not declared: eight values give a
/// 3-bit port. A table whose length is not a power of two rounds up, and the
/// addresses past the end read as zero.
let romLookup =
    defModule
        "RomLookup"
        (fun p -> (p.inPort "index" 3, p.outPort "square" 16))
        (fun (index, square) ->
            let squares = distributedRom "squares" 16 [| 0UL; 1UL; 4UL; 9UL; 16UL; 25UL; 36UL; 49UL |]
            memRead squares index ==> square)

/// The same table in block RAM, which changes what a read costs.
///
/// `blockRom` puts the contents in a BRAM INIT, and a block cannot read
/// combinationally — so `memRead` is refused here and `memReadPort` is the only
/// way in, exactly as on a `blockMem`. Sixteen entries rather than eight because
/// a block is the wrong home for a table small enough to sit in LUTs, and the
/// pair reads better when the deeper one is visibly the deeper one.
///
/// Worth knowing before reaching for this on silicon: **a sync-read ROM feeding
/// a DSP multiply silently demotes to LUTROM**, because Vivado absorbs the
/// output register as the DSP's input register. Re-register after the read when
/// the value goes into a multiplier.
let blockRomLookup =
    defModule
        "BlockRomLookup"
        (fun p -> (p.inPort "index" 4, p.outPort "prime" 16))
        (fun (index, prime) ->
            let primes =
                blockRom
                    "primes"
                    16
                    [| 2UL; 3UL; 5UL; 7UL; 11UL; 13UL; 17UL; 19UL; 23UL; 29UL; 31UL; 37UL; 41UL; 43UL; 47UL; 53UL |]

            (memReadPort primes index).data ==> prime)

// ---------------------------------------------------------------------------
// Storage a stream hides
// ---------------------------------------------------------------------------

/// A stream FIFO between a producer and a consumer.
///
/// The whole of what it buys is decoupling: a burst is absorbed, and a consumer
/// that pauses stops the producer only once the buffer is full. First-word
/// fall-through, so `payload` and `valid` arrive together as the contract
/// requires.
let bufferedStream =
    defModule
        "BufferedStream"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_data" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (inData, inValid, inReady, outData, outValid, outReady) ->
            let src =
                { payload = inData
                  valid = inValid
                  ready = inReady
                  layout = layout1 ("data", 8) }

            let out = streamFifo "fifo" 8 src

            out.payload ==> outData
            out.valid ==> outValid
            outReady ==> out.ready)

/// The same FIFO, deep enough that its words live in a block rather than in
/// LUTs — and that is the only thing that is different about it.
///
/// The source is character for character `bufferedStream` with one number
/// changed. Above the crossover the head becomes a synchronous read behind a
/// two-slot skid, which is a different circuit answering to the same `Stream`:
/// same capacity, same beat per cycle, same order. The pair exists so that
/// claim is a measurement rather than a design note — the check runs one model
/// against both.
let deepBufferedStream =
    defModule
        "DeepBufferedStream"
        (fun p ->
            (p.inPort "in_data" 8,
             p.inPort "in_valid" 1,
             p.outPort "in_ready" 1,
             p.outPort "out_data" 8,
             p.outPort "out_valid" 1,
             p.inPort "out_ready" 1))
        (fun (inData, inValid, inReady, outData, outValid, outReady) ->
            let src =
                { payload = inData
                  valid = inValid
                  ready = inReady
                  layout = layout1 ("data", 8) }

            let out = streamFifo "fifo" 128 src

            out.payload ==> outData
            out.valid ==> outValid
            outReady ==> out.ready)

// ---------------------------------------------------------------------------
// Memory that is not on this chip
// ---------------------------------------------------------------------------
//
// The other end of every design above. A block RAM read is a value one cycle
// later; a PS DDR read is a request that leaves the chip, may have several
// siblings in flight, and comes back after a number nobody can name. The two
// cannot present the same interface if that interface is a delay line — which
// is why everything here is a `Stream`, and why the masters take addresses in
// and hand words out rather than being read like an array.
//
// That is also the whole of UC2 in `notes/DEVICES.md`: a design honest about
// backpressure stays *correct* whichever of these satisfies it, and only its
// throughput moves. What these designs prove is the half that can be measured
// here — that the ring, the pointers and the protocol state survive a slave
// whose timing is adversarial. The mapping that would let one design reach
// either end is not built yet.

/// The AXI4 read master's ring path at ports: request addresses in, read data
/// out, `m_axi_ar*`/`m_axi_r*` at the boundary. The rehearsal drives it
/// against `SimAxiReadSlave` across the pacing matrix.
let axiReadMaster =
    defModule
        "AxiReadMaster"
        (fun p ->
            (streamInputPorts p "req" (layout1 ("addr", 32)),
             axiReadBusPorts p "m_axi" 32 32,
             streamOutputPorts p "resp" (layout1 ("data", 32))))
        (fun (req, busPorts, resp) ->
            streamSource req
            |> axiMasterReaderOn (axiReadBusOf busPorts) 8
            |> streamSink resp)

/// The single-outstanding degenerate read path — pending flags, no ring.
let axiReadMasterSingle =
    defModule
        "AxiReadMasterSingle"
        (fun p ->
            (streamInputPorts p "req" (layout1 ("addr", 32)),
             axiReadBusPorts p "m_axi" 32 32,
             streamOutputPorts p "resp" (layout1 ("data", 32))))
        (fun (req, busPorts, resp) ->
            streamSource req
            |> axiMasterReaderOn (axiReadBusOf busPorts) 1
            |> streamSink resp)

/// The burst read master: (addr, len) descriptors in, (data, last) beats out,
/// streaming R passthrough — GEP's host-marshaled streaming shape.
let axiReadMasterBurst =
    defModule
        "AxiReadMasterBurst"
        (fun p ->
            (streamInputPorts p "req" (layout2 ("addr", 32) ("len", 8)),
             axiReadBusPorts p "m_axi" 32 32,
             streamOutputPorts p "resp" (layout2 ("data", 32) ("last", 1))))
        (fun (req, busPorts, resp) ->
            streamSource req
            |> axiMasterReaderBurstOn (axiReadBusOf busPorts) 4 16
            |> streamSink resp)

/// The AXI master's pointer ring under the oracle: 128-bit beats, 4 slots.
/// The testbench's random awready/wready/bvalid stand in for the interconnect,
/// so protocol-state equivalence is checked under adversarial slave timing —
/// including illegal timing (spurious bvalid), where both implementations must
/// still agree state-for-state.
let axiWriteMaster =
    defModule
        "AxiWriteMaster"
        (fun p ->
            (streamInputPorts p "in" (axiWriteBeatLayout 32 128),
             axiWriteBusPorts p "m_axi" 32 128))
        (fun (inPorts, busPorts) ->
            streamSource inPorts
            |> axiMasterWriterOn (axiWriteBusOf busPorts) 4)

/// The single-outstanding degenerate path — pending flags, no ring.
let axiWriteMasterSingle =
    defModule
        "AxiWriteMasterSingle"
        (fun p ->
            (streamInputPorts p "in" (axiWriteBeatLayout 16 32),
             axiWriteBusPorts p "m_axi" 16 32))
        (fun (inPorts, busPorts) ->
            streamSource inPorts
            |> axiMasterWriterOn (axiWriteBusOf busPorts) 1)

// ---------------------------------------------------------------------------
// One kernel, three storages — and the kernel does not change
// ---------------------------------------------------------------------------
//
// `notes/DEVICES.md`'s UC2 at the smallest scale that demonstrates it: a module
// that reads a memory, computes over what it read, and writes the results
// somewhere. Which memory, and which somewhere, are decided *outside* it.
//
// The mechanism is `ReadWindow` and `WriteWindow` from `Windows.fs`. The kernel
// takes one of each and never names a storage, never declares a port for one,
// and cannot tell LUTs from a block from memory on the other side of the chip.
// Everything it can see is a `Stream`, which is §3's argument: a design honest
// about backpressure stays correct under any latency, and only throughput
// moves.

/// **The kernel.** Walks `count` words of `source`, keeps a running sum, and
/// writes each partial sum to the matching index of `sink`.
///
/// The sum is genuinely stateful, so this is not a map that would work by
/// accident: `acc` advances only on a beat that actually fires, which is what
/// makes the answer independent of how long either end takes to respond.
///
/// Results arrive in the order they were asked for, so the write side needs one
/// more counter and no tag — the shadow queue `CLAUDE.md` warns about never
/// appears.
///
/// `run` arrives as a value rather than a port. The module declaring its own
/// control input would work in all three designs here, because in all three it
/// happens to be the top; the rule that a bus is declared at the design's top
/// level is worth nothing if the module next door quietly declares one anyway.
let runningSumOver (count: int) (run: Expr) (source: ReadWindow) (sink: WriteWindow) =
    if count > source.words then
        failwith $"runningSumOver: asked for %d{count} words of a %d{source.words}-word source"

    if count > sink.words then
        failwith $"runningSumOver: asked to write %d{count} words to a %d{sink.words}-word sink"

    if source.wordWidth <> sink.wordWidth then
        failwith $"runningSumOver: a %d{source.wordWidth}-bit source and a %d{sink.wordWidth}-bit sink"

    let wordWidth = source.wordWidth
    let indexWidth = indexWidthFor count

    let index = reg "req_index" indexWidth
    let asking = wireBit "req_ready"
    registerStreamReady asking

    let more = wireBit "req_more"
    (run &&& lt index (lit (uint64 count) indexWidth)) ==> more
    If (more &&& asking) (fun () -> index + lit 1UL indexWidth ==> index)

    let words =
        source.read
            { payload = index
              valid = more
              ready = asking
              layout = layout1 ("index", indexWidth) }

    let outIndex = reg "sum_index" indexWidth
    let writing = wireBit "sum_ready"
    registerStreamReady writing
    writing ==> words.ready

    let acc = reg "acc" wordWidth
    let total = wire "sum_total" wordWidth
    (acc + words.payload) ==> total

    If (words.valid &&& writing) (fun () ->
        total ==> acc
        outIndex + lit 1UL indexWidth ==> outIndex)

    sink.write
        { payload = outIndex, total
          valid = words.valid
          ready = writing
          layout = layout2 ("index", indexWidth) ("word", wordWidth) }

// ---- the windows a mapping chooses between ---------------------------------
//
// All of them are `Warp11.Windows` now — `lutReadWindow`, `blockReadWindow` and
// `memWriteWindow` for storage on this chip, `readWindowOn` and
// `defineWriteWindows`/`createWriteWindow` for a region of a bus. This file
// used to carry its own DDR pair; they were the library's two with a hardcoded
// four-byte stride, and they are gone.

// ---- the three mappings ----------------------------------------------------

let private sumCount = 64
let private sumIndexWidth = 8
let private sumWordWidth = 32

/// Source and results both in LUTs. `fill_*` loads the source and `probe_addr`
/// reads the results back — the host's half of a mapping that keeps everything
/// on chip.
let sumOverLut =
    defModule
        "SumOverLut"
        (fun p ->
            (p.inPort "fill_addr" sumIndexWidth,
             p.inPort "fill_data" sumWordWidth,
             p.inPort "fill_enable" 1,
             p.inPort "probe_addr" sumIndexWidth,
             p.outPort "probe_data" sumWordWidth,
             p.inPort "run" 1))
        (fun (fillAddr, fillData, fillEnable, probeAddr, probeData, run) ->
            let source = distributedMem "src" sumIndexWidth sumWordWidth
            let results = distributedMem "dst" sumIndexWidth sumWordWidth

            memWrite source fillAddr fillData fillEnable

            memRead results probeAddr ==> probeData

            runningSumOver sumCount run (lutReadWindow source) (memWriteWindow results))

/// The same kernel over blocks. Character for character the same call; the
/// source now answers a cycle late behind a skid, and the results array has to
/// be read back synchronously because a block cannot do otherwise.
let sumOverBlock =
    defModule
        "SumOverBlock"
        (fun p ->
            (p.inPort "fill_addr" sumIndexWidth,
             p.inPort "fill_data" sumWordWidth,
             p.inPort "fill_enable" 1,
             p.inPort "probe_addr" sumIndexWidth,
             p.outPort "probe_data" sumWordWidth,
             p.inPort "run" 1))
        (fun (fillAddr, fillData, fillEnable, probeAddr, probeData, run) ->
            let source = blockMem "src" sumIndexWidth sumWordWidth
            let results = blockMem "dst" sumIndexWidth sumWordWidth

            memWrite source fillAddr fillData fillEnable

            (memReadPort results probeAddr).data ==> probeData

            runningSumOver sumCount run (blockReadWindow "src_port" source) (memWriteWindow results))

/// The same kernel over UltraRAM. Structurally `sumOverBlock` with the storage
/// word swapped: URAM reads synchronously exactly as a block does, so the same
/// skidded read port serves both — what differs is only which primitive the
/// attribute pins, and that 18.4 of the KV260's 24.2 Mb are reachable through
/// this one and not the others.
let sumOverUltra =
    defModule
        "SumOverUltra"
        (fun p ->
            (p.inPort "fill_addr" sumIndexWidth,
             p.inPort "fill_data" sumWordWidth,
             p.inPort "fill_enable" 1,
             p.inPort "probe_addr" sumIndexWidth,
             p.outPort "probe_data" sumWordWidth,
             p.inPort "run" 1))
        (fun (fillAddr, fillData, fillEnable, probeAddr, probeData, run) ->
            let source = ultraMem "src" sumIndexWidth sumWordWidth
            let results = ultraMem "dst" sumIndexWidth sumWordWidth

            memWrite source fillAddr fillData fillEnable

            (memReadPort results probeAddr).data ==> probeData

            runningSumOver sumCount run (blockReadWindow "src_port" source) (memWriteWindow results))

/// The same kernel again, with neither end on this chip. No `fill_*` and no
/// `probe_addr`: the host stages the source and reads the results in DDR
/// directly, which is the part of a mapping that stops being fabric at all.
let sumOverDdr =
    defModule
        "SumOverDdr"
        (fun p ->
            // Both halves of one conventional `m_axi`. They could as easily be
            // `m_axi_hp0` and `m_axi_hp1` — two of the independent paths the part
            // offers (§10d) — which is the thing declaring the boundary here buys
            // and the master conjuring it could not express.
            (p.inPort "run" 1,
             axiReadBusPorts p "m_axi" 32 sumWordWidth,
             axiWriteBusPorts p "m_axi" 32 sumWordWidth))
        (fun (run, readBusPorts, writeBusPorts) ->
            let readBus = axiReadBusOf readBusPorts
            let writeBus = axiWriteBusOf writeBusPorts

            runningSumOver
                sumCount
                run
                (readWindowOn readBus 4 (lit 0x0000UL 32) sumCount)
                (writeWindowOn writeBus 4 "dst" (lit 0x1000UL 32) sumCount))
