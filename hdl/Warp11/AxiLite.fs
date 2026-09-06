[<AutoOpen>]
module Warp11.AxiLite

/// The AXI-Lite slave, in the two shapes the codebase asks for it: driven by a
/// declarative `RegMap` (`axiLiteSlaveOf`, next door) or by plain lists
/// (`axiLiteSlave`, below). They differ in what the words *mean* and agree on
/// every bit of the protocol, so the protocol lives here once.
///
/// Before 2026-08-18 they agreed by having been written twice, and `census.fsx`
/// had been reporting it: the sixteen ports, the write handshake and the read
/// handshake were duplicated between `RegMap.fs` and `Stdlib.fs`, down to the
/// register names. Two copies of a bus protocol drift in exactly one direction —
/// one of them gets a fix.

/// Widen a read value to the 32-bit bus. Anything wider is a mistake the bus
/// cannot carry, so it is refused rather than truncated.
let internal zeroExtend32 (e: Expr) =
    if width e = 32 then e
    elif width e < 32 then cat (lit 0UL (32 - width e)) e
    else failwith $"axiLiteSlave: a %d{width e}-bit read value — the bus is 32"

/// The raw `s_axi_*` ports and the finished write channel, as the two read
/// channels above see them. Private: a slave reaches this through
/// `axiLiteChannel` or `axiLiteChannelPipelined`, never directly.
type private AxiLitePorts =
    { /// Width of a word index — the address width less the two byte bits.
      wordWidth: int
      /// The write data bus.
      wdata: Expr
      /// High on the cycle a write is accepted.
      writeFire: Expr
      /// The write address as a word index.
      awWord: Expr
      /// The raw read address, still in bytes.
      araddr: Expr
      /// The host is presenting a read address.
      arvalid: Expr
      /// The slave accepts it. Driven by whichever read channel is in use.
      arready: Expr
      /// The read data bus, for the read channel to drive.
      rdata: Expr
      /// The read response code.
      rresp: Expr
      /// The slave is presenting read data.
      rvalid: Expr
      /// The host takes it.
      rready: Expr }

/// The raw seventeen `s_axi_*` wires of an AXI-Lite slave boundary, as an io
/// factory declares them — no logic, no interpretation. `axiLiteChannelOn`
/// (or the slaves over it) is the body half that makes them a bus.
type AxiLiteSlavePorts =
    { /// The aperture's address width in bytes — carried so the body halves
      /// need not be told twice.
      addrWidth: int
      awaddr: Input
      awvalid: Input
      awready: Output
      wdata: Input
      wvalid: Input
      wready: Output
      bresp: Output
      bvalid: Output
      bready: Input
      araddr: Input
      arvalid: Input
      arready: Output
      rdata: Output
      rresp: Output
      rvalid: Output
      rready: Input }

/// Declare the slave boundary in an io factory. `s_axi_wstrb` is declared and
/// deliberately unread — the slave trusts whole-word writes.
let axiLiteSlavePorts (p: Ports) (addrWidth: int) : AxiLiteSlavePorts =
    let awaddr = p.inPort "s_axi_awaddr" addrWidth
    let awvalid = p.inPort "s_axi_awvalid" 1
    let awready = p.outPort "s_axi_awready" 1
    let wdata = p.inPort "s_axi_wdata" 32
    p.inPort "s_axi_wstrb" 4 |> ignore
    let wvalid = p.inPort "s_axi_wvalid" 1
    let wready = p.outPort "s_axi_wready" 1
    let bresp = p.outPort "s_axi_bresp" 2
    let bvalid = p.outPort "s_axi_bvalid" 1
    let bready = p.inPort "s_axi_bready" 1
    let araddr = p.inPort "s_axi_araddr" addrWidth
    let arvalid = p.inPort "s_axi_arvalid" 1
    let arready = p.outPort "s_axi_arready" 1
    let rdata = p.outPort "s_axi_rdata" 32
    let rresp = p.outPort "s_axi_rresp" 2
    let rvalid = p.outPort "s_axi_rvalid" 1
    let rready = p.inPort "s_axi_rready" 1

    { addrWidth = addrWidth
      awaddr = awaddr
      awvalid = awvalid
      awready = awready
      wdata = wdata
      wvalid = wvalid
      wready = wready
      bresp = bresp
      bvalid = bvalid
      bready = bready
      araddr = araddr
      arvalid = arvalid
      arready = arready
      rdata = rdata
      rresp = rresp
      rvalid = rvalid
      rready = rready }

/// The one-outstanding write channel over a declared boundary, shared between
/// the classic channel and the pipelined one — the body half of
/// `axiLiteSlavePorts`.
let private axiLiteWriteChannelOn (ports: AxiLiteSlavePorts) : AxiLitePorts =
    let addrWidth = ports.addrWidth
    let wordWidth = addrWidth - 2

    // write channel — aw and w accepted together, one response outstanding
    let bvalidR = regBit "bvalidR"
    let writeFire = wireBit "write_fire"
    (ports.awvalid &&& ports.wvalid &&& bnot bvalidR) ==> writeFire
    writeFire ==> ports.awready
    writeFire ==> ports.wready
    If writeFire (fun () -> lit 1UL 1 ==> bvalidR)
    If (bvalidR &&& ports.bready) (fun () -> lit 0UL 1 ==> bvalidR)
    bvalidR ==> ports.bvalid
    lit 0UL 2 ==> ports.bresp

    let awWord = wire "aw_word" wordWidth
    slice (addrWidth - 1) 2 ports.awaddr ==> awWord

    { wordWidth = wordWidth
      wdata = ports.wdata
      writeFire = writeFire
      awWord = awWord
      araddr = ports.araddr
      arvalid = ports.arvalid
      arready = ports.arready
      rdata = ports.rdata
      rresp = ports.rresp
      rvalid = ports.rvalid
      rready = ports.rready }

/// What `beginRead` hands back at the moment a read address is accepted.
type ReadAccept =
    { /// The accepted address as a word index — live on the accept cycle and
      /// held after, so a source reading a memory keeps presenting it.
      word: Expr
      /// High from the accept until the host takes RDATA, which is exactly
      /// the span a source must serve the held word for. Composition rather
      /// than declaration: a slave with no use for it emits what it always
      /// did.
      inFlight: Expr }

/// The one-outstanding AXI-Lite channel, as the slave above it sees it.
type AxiLiteChannel =
    { /// Width of a word index — the address width less the two byte bits.
      wordWidth: int
      /// The write data bus.
      wdata: Expr
      /// High on the cycle a write is accepted.
      writeFire: Expr
      /// The write address as a word index.
      awWord: Expr
      /// Raise the read channel. Called once, and after the slave has
      /// declared its own registers — declaration order is emission order,
      /// so raising it earlier would move every register in every register
      /// map to the far side of it.
      beginRead: unit -> ReadAccept
      /// How many cycles after the AR accept RDATA is sampled. Every read
      /// source has to be ready by then, and `requireSourceFits` is how a
      /// source says whether it is — the number is stated once, by the caller
      /// that knows the sources, instead of being a coincidence between this
      /// file and whatever they happen to cost.
      answersAfter: int
      /// The read data bus, for the slave to drive.
      rdata: Expr }

/// The sixteen `s_axi_*` ports and both one-outstanding handshakes.
///
/// `answersAfter` is how many cycles the read side waits between accepting an
/// address and sampling RDATA. It is the slowest read source's depth, and the
/// caller works it out — the channel cannot, because a source needs the held
/// address that the channel has not raised yet.
///
/// The read channel is behind `beginRead` rather than raised with the rest, and
/// that is structural rather than tidiness: **declaration order is emission
/// order**, and whichever slave is calling this declares its own registers
/// between the two channels. Raising the read channel here would move every
/// register in every register map to the far side of it. `beginRead` returns
/// the read word — live on the accept cycle, held after — and is called once.
///
/// One-outstanding is the scheme both slaves already had. It is also the thing
/// a later pass replaces: RDATA is a 0-cycle mux over registers *and* a 1-cycle
/// window read, correct only because RVALID happens to rise exactly one cycle
/// after the AR accept. Nothing states that alignment and nothing checks it.
let axiLiteChannelOn (ports: AxiLiteSlavePorts) (answersAfter: int) : AxiLiteChannel =
    let addrWidth = ports.addrWidth
    let io = axiLiteWriteChannelOn ports
    let wordWidth = io.wordWidth
    let wdata = io.wdata
    let writeFire = io.writeFire
    let awWord = io.awWord
    let araddr = io.araddr
    let arvalid = io.arvalid
    let arready = io.arready
    let rdata = io.rdata
    let rresp = io.rresp
    let rvalid = io.rvalid
    let rready = io.rready

    // read channel — RVALID rises `answersAfter` cycles after AR accept; the
    // read word holds in a reg while RVALID waits, so a source that reads a
    // memory keeps presenting the same address
    let beginRead () =
        let rvalidR = regBit "rvalidR"
        let readFire = wireBit "read_fire"

        if answersAfter = 1 then
            // No gap to guard. The answer lands the cycle after the accept,
            // which is the cycle RVALID rises, so `rvalidR` is already the
            // busy flag and a separate one would be it, renamed.
            (arvalid &&& bnot rvalidR) ==> readFire
            readFire ==> arready
            If readFire (fun () -> lit 1UL 1 ==> rvalidR)
        else
            // A gap. Between the accept and the answer RVALID is still low, so
            // `rvalidR` alone would wave a second AR straight in on top of the
            // first — and there is one held address and one RDATA, so the
            // second would overwrite the first's address and the host would be
            // answered twice with the same word.
            let busy = regBit "read_busy"
            (arvalid &&& bnot rvalidR &&& bnot busy) ==> readFire
            readFire ==> arready
            If readFire (fun () -> lit 1UL 1 ==> busy)

            // The accept, walked forward to the cycle its answer is good.
            let answerDue = delayChain "read_answer" 1 (answersAfter - 1) readFire

            If answerDue (fun () ->
                lit 0UL 1 ==> busy
                lit 1UL 1 ==> rvalidR)

        If (rvalidR &&& rready) (fun () -> lit 0UL 1 ==> rvalidR)
        rvalidR ==> rvalid
        lit 0UL 2 ==> rresp

        let heldWord = reg "ar_word_held" wordWidth
        If readFire (fun () -> slice (addrWidth - 1) 2 araddr ==> heldWord)
        let readWord = wire "ar_word" wordWidth
        mux readFire (slice (addrWidth - 1) 2 araddr) heldWord ==> readWord

        // `inFlight` is composition, not declaration — a slave with no use for
        // it emits exactly what it always did. It is what an arbitrated read
        // source keys on: high from the accept until the host takes RDATA,
        // which is precisely the span the source must serve the held word for.
        { word = readWord
          inFlight = readFire ||| rvalidR }

    { wordWidth = wordWidth
      wdata = wdata
      writeFire = writeFire
      awWord = awWord
      beginRead = beginRead
      answersAfter = answersAfter
      rdata = rdata }

/// The several-in-flight channel. The write half is the classic one's; the
/// read half is a different contract, which is why this is a different type
/// rather than the same one with three fields sometimes meaningless.
/// The ambient form of [axiLiteChannelOn], for `design` bodies: declares the
/// boundary at the ambient builder, then runs the same channel. Dies with
/// `design`.
let axiLiteChannel (addrWidth: int) (answersAfter: int) : AxiLiteChannel =
    axiLiteChannelOn (axiLiteSlavePorts (ambientPorts ()) addrWidth) answersAfter

type AxiLiteChannelPipelined =
    { /// Width of a word index.
      wordWidth: int
      /// The write data bus.
      wdata: Expr
      /// High on the cycle a write is accepted.
      writeFire: Expr
      /// The write address as a word index.
      awWord: Expr
      /// The accepted read address, presented for exactly the accept cycle.
      word: Expr
      /// High on that accept cycle.
      present: Expr
      /// Where the slave puts the response for `word`, exactly
      /// `answersAfter` cycles later.
      answer: Expr }

/// The read channel with several transactions in flight — the AXI-Lite channel
/// AXI itself always permitted, opt-in because nothing on this board's host
/// side pipelines uncached reads today. Where it earns its keep: a slow source
/// (`answersAfter` large) stops costing `answersAfter` cycles *per read* and
/// costs it once, with a read completing every cycle behind it.
///
/// The shape of the problem: the classic channel is correct because the held
/// read word stands still until RDATA is sampled, so every source — the
/// register mux, a window's read port — answers about the same address at its
/// own depth. Pipelining is precisely giving that property up, so each accept
/// instead sends a token down a `delayChain`; when a token lands, whatever the
/// slave wired to `answer` is captured into a response FIFO, and RVALID is the
/// FIFO's valid. AXI-Lite has no IDs, so responses are in order by
/// construction — the FIFO *is* the ordering.
///
/// **Credits, not hope**: an AR is accepted only while fewer than
/// `maxOutstanding` transactions are in flight (accepted, response not yet
/// taken), and the FIFO is at least that deep, so a token can never land on a
/// full queue. The assertion says so anyway, because the invariant is load-
/// bearing and invariants rot.
///
/// The slave's contract: `word` is the accepted address, presented for exactly
/// the accept cycle (`present` high); `answer` must carry the response for
/// that address exactly `answersAfter` cycles later. A memory read port fed
/// `word` does this naturally at depth 1; a combinational register mux over
/// `word` needs one capture register to arrive at the same time.
let axiLiteChannelPipelinedOn
    (ports: AxiLiteSlavePorts)
    (answersAfter: int)
    (maxOutstanding: int)
    : AxiLiteChannelPipelined =
    if maxOutstanding < 2 then
        failwith $"axiLiteChannelPipelined with %d{maxOutstanding} outstanding — one outstanding is axiLiteChannel"

    if answersAfter < 1 then
        failwith $"axiLiteChannelPipelined: answersAfter must be >= 1, got %d{answersAfter}"

    let addrWidth = ports.addrWidth
    let io = axiLiteWriteChannelOn ports
    let wordWidth = io.wordWidth

    // In flight = accepted and the response not yet taken by the host.
    let countWidth = ceilLog2 (maxOutstanding + 1)
    let inFlight = reg "read_in_flight" countWidth

    let arready = wireBit "read_credit"
    lt inFlight (lit (uint64 maxOutstanding) countWidth) ==> arready
    arready ==> io.arready

    let accept = wireBit "read_accept"
    (io.arvalid &&& arready) ==> accept

    let responded = wireBit "read_responded"
    (io.rvalid &&& io.rready) ==> responded

    ifElse [
        (accept &&& bnot responded, fun () -> inFlight + lit 1UL countWidth ==> inFlight)
        (otherwise, fun () -> If (responded &&& bnot accept) (fun () -> inFlight - lit 1UL countWidth ==> inFlight)) ]

    let word = wire "read_word" wordWidth
    slice (addrWidth - 1) 2 io.araddr ==> word

    // Each accept walks to its answer time; several tokens ride the chain at
    // once, which is the whole difference from the busy flag.
    let answerDue = delayChain "read_token" 1 answersAfter accept

    let answer = wire "read_answer_data" 32
    let answerReady = wireBit "read_answer_ready"

    let responses =
        streamFifo
            "read_resp"
            (1 <<< ceilLog2 maxOutstanding)
            { payload = answer
              valid = answerDue
              ready = answerReady
              layout = layout1 ("data", 32) }

    // Credits make this unfailable; the claim is cheap and the failure mode —
    // a response silently dropped at a full queue — is the expensive kind.
    assertThat (bnot (answerDue &&& bnot answerReady)) "pipelined read response queue overflowed"

    responses.payload ==> io.rdata
    responses.valid ==> io.rvalid
    io.rready ==> responses.ready
    lit 0UL 2 ==> io.rresp

    { wordWidth = wordWidth
      wdata = io.wdata
      writeFire = io.writeFire
      awWord = io.awWord
      word = word
      present = accept
      answer = answer }

/// The ambient form of [axiLiteChannelPipelinedOn], for `design` bodies. Dies
/// with `design`.
let axiLiteChannelPipelined (addrWidth: int) (answersAfter: int) (maxOutstanding: int) : AxiLiteChannelPipelined =
    axiLiteChannelPipelinedOn (axiLiteSlavePorts (ambientPorts ()) addrWidth) answersAfter maxOutstanding

/// A read source has to have its answer by the time the channel samples RDATA.
///
/// This is the alignment the slave used to make by accident. RDATA is a 0-cycle
/// mux over registers *and* a memory read that costs a cycle, and it came out
/// right only because RVALID happened to rise exactly one cycle after the AR
/// accept — stated nowhere, checked by nothing. Point a window at a source that
/// costs two and every register map in the tree keeps elaborating, keeps
/// passing the differential, and returns the previous word on silicon.
///
/// It is a *check* rather than a restructure on purpose. Holding the request
/// and reading the registers combinationally at answer time is cheaper than
/// delaying every register to meet the memory, so the slave's mixture is the
/// efficient arrangement — what it was missing was anything that noticed when
/// the mixture stopped adding up.
let internal requireSourceFits (channelAnswersAfter: int) (sourceName: string) (sourceDepth: int) =
    if sourceDepth > channelAnswersAfter then
        failwith
            $"axiLiteSlave: the read channel samples RDATA %d{channelAnswersAfter} cycle(s) after the AR accept, and '{sourceName}' needs %d{sourceDepth} — RVALID would rise before the word arrived and the host would read whatever the previous transaction left. The channel can wait longer (it holds AR off while a read is in flight), so this means the depth was not folded into what the channel was built with"

/// An AXI-Lite slave, elaborated inline in the current design: declares the
/// s_axi_* ports and implements one-outstanding-transaction write and read
/// channels, OKAY-only — the same scheme as the Rust spike's scratch slave
/// (combinational accept gated on the response reg, mux-folded set/clear).
///
/// `writeRegs` are (name, byteOffset, width) registers this slave owns,
/// returned as Refs and readable back at their own offsets. `readValues` map
/// word-aligned byte offsets to values (≤32 bits, zero-padded on the bus).
/// `memWindows` map aligned byte ranges onto mems, **as many as the aperture
/// has room for**. They are sources answering one AR channel alongside the
/// registers, so what decides the channel is the slowest of them: RVALID rises
/// `answersAfter` cycles after the AR accept, and `requireSourceFits` refuses a
/// source that needs longer.
///
/// A combinational source stays correct at any depth without alignment, which is
/// why the registers need none: `readWord` is *held* while RVALID waits, so a
/// mux over it reads the same word on every cycle of the wait, and so does a
/// memory whose address has stopped moving. That is the property the whole
/// arrangement rests on and it was never written down.
///
/// Caveats, both deliberate at run-once scope (the run-once scope, notes/FINDINGS.md): RDATA stability
/// while RVALID waits relies on the read sources being stable (true after
/// `done`); wstrb is accepted and ignored — full-word writes only. Protocol
/// behavior against a real master is the FsSimWindow bridge's job.
let axiLiteSlaveFullOn
    (ports: AxiLiteSlavePorts)
    (pulseRegs: (string * uint64) list)
    (writeRegs: (string * uint64 * int) list)
    (readValues: (uint64 * Expr) list)
    (memWindows: (uint64 * Mem) list)
    : Expr list * Expr list =
    let addrWidth = ports.addrWidth
    let wordWidth = addrWidth - 2

    let wordOf off =
        if off % 4UL <> 0UL then
            failwith $"axiLiteSlave: offset 0x%x{off} is not word-aligned"

        let word = off >>> 2

        if word >= (1UL <<< wordWidth) then
            failwith $"axiLiteSlave: offset 0x%x{off} is outside the %d{addrWidth}-bit aperture"

        word

    // Ranges are checked against each other before anything elaborates. One
    // window could only collide with a register; several can also swallow each
    // other, and a swallowed window is silent — the fold below just picks one,
    // and the host reads plausible words from the wrong memory.
    let windowRanges =
        [ for base_, (m: Mem) in memWindows -> m.memName, wordOf base_, 1UL <<< m.addrWidth ]

    for i in 0 .. windowRanges.Length - 1 do
        let nameA, baseA, sizeA = windowRanges[i]

        for j in i + 1 .. windowRanges.Length - 1 do
            let nameB, baseB, sizeB = windowRanges[j]

            if baseA < baseB + sizeB && baseB < baseA + sizeA then
                failwith
                    $"axiLiteSlave: windows '{nameA}' and '{nameB}' overlap — '{nameA}' covers words %d{baseA}..%d{baseA + sizeA - 1UL} and '{nameB}' covers %d{baseB}..%d{baseB + sizeB - 1UL}. One of them would answer and the other would be unreachable"

    let namedOffsets =
        [ for n, off, _ in writeRegs -> n, off ] @ [ for n, off in pulseRegs -> n, off ]

    for regName, off in namedOffsets do
        let w = wordOf off

        for winName, baseW, sizeW in windowRanges do
            if w >= baseW && w < baseW + sizeW then
                failwith
                    $"axiLiteSlave: register '{regName}' at 0x%x{off} lands inside window '{winName}' — the window answers reads there, so the register would be write-only and silently so"

    // The channel waits for the slowest source. Nothing here is deeper than a
    // block-RAM read today, so this is 1 and the read side emits what it always
    // did; the fold is what makes a slower window a configuration rather than a
    // rewrite.
    let answersAfter =
        memWindows |> List.fold (fun deepest (_, m: Mem) -> max deepest (memReadDepth m)) 1

    let ch = axiLiteChannelOn ports answersAfter
    let wdata = ch.wdata
    let writeFire = ch.writeFire
    let awWord = ch.awWord

    let regRefs =
        [ for name, off, w in writeRegs ->
              let r = reg name w
              If (writeFire &&& eq awWord (lit (wordOf off) wordWidth)) (fun () -> slice (w - 1) 0 wdata ==> r)
              r ]

    // w1p: a write of 1 to bit 0 pulses the wire for exactly the accept cycle;
    // reads at the offset return 0 (nothing joins the read mux).
    // w1pBit — how `start` reaches the frame pod.
    let pulseRefs =
        [ for name, off in pulseRegs ->
              let p = wire name 1
              (writeFire &&& eq awWord (lit (wordOf off) wordWidth) &&& slice 0 0 wdata) ==> p
              p ]

    let readWord = (ch.beginRead ()).word

    let regData =
        List.zip (List.map (fun (_, off, _) -> off) writeRegs) regRefs @ readValues
        |> List.fold (fun acc (off, value) -> mux (eq readWord (lit (wordOf off) wordWidth)) (zeroExtend32 value) acc) (lit 0UL 32)

    // Every window answers the same AR channel, each over its own range. A
    // window later in the list wins where two overlap — which they may not, so
    // that is a statement about the fold rather than a rule anyone can reach.
    let readData =
        memWindows
        |> List.fold
            (fun below (base_, m: Mem) ->
                let baseWord = wordOf base_
                let size = 1UL <<< m.addrWidth

                if baseWord % size <> 0UL then
                    failwith $"axiLiteSlave: window '{m.memName}' at 0x%x{base_} is not aligned to its %d{int size}-word size"

                let memIndex = wire $"{m.memName}_idx" m.addrWidth
                slice (m.addrWidth - 1) 0 readWord ==> memIndex

                let aboveBase = bnot (lt readWord (lit baseWord wordWidth))

                let inWindow =
                    if baseWord + size >= (1UL <<< wordWidth) then
                        aboveBase // the window ends the aperture — no upper compare exists
                    else
                        aboveBase &&& lt readWord (lit (baseWord + size) wordWidth)

                let read = memReadPort m memIndex
                requireSourceFits ch.answersAfter $"the window '{m.memName}' at 0x%x{base_}" read.depth

                mux inWindow (zeroExtend32 read.data) below)
            regData

    readData ==> ch.rdata
    pulseRefs, regRefs

/// The common form — write registers, read values, an optional mem window, no
/// pulse registers. Emission is byte-identical to the pre-`w1p` slave.
let axiLiteSlaveOn
    (ports: AxiLiteSlavePorts)
    (writeRegs: (string * uint64 * int) list)
    (readValues: (uint64 * Expr) list)
    (memWindows: (uint64 * Mem) list)
    : Expr list =
    axiLiteSlaveFullOn ports [] writeRegs readValues memWindows |> snd

/// The ambient forms, for `design` bodies. They die with `design`.
let axiLiteSlaveFull addrWidth pulseRegs writeRegs readValues memWindows =
    axiLiteSlaveFullOn (axiLiteSlavePorts (ambientPorts ()) addrWidth) pulseRegs writeRegs readValues memWindows

let axiLiteSlave addrWidth writeRegs readValues memWindows =
    axiLiteSlaveOn (axiLiteSlavePorts (ambientPorts ()) addrWidth) writeRegs readValues memWindows