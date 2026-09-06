/// A prototype of `notes/DEVICES.md` §10b–§10d, built as new designs rather
/// than as a change to anything: a bus that is a **value**, carries its own
/// **name**, and is **claimed** by whoever drives it, with the arbiter falling
/// out of how many clients a mapping puts on a port.
///
/// **Nothing here is a library change**, deliberately. The stdlib's AXI masters
/// declare `m_axi_*` themselves, so they cannot be handed a bus, and giving
/// them that shape is the refactor this file exists to justify first. The two
/// masters below are therefore minimal ones of their own — single-outstanding,
/// about thirty lines each — written against a bus instead of against the
/// module they happen to be elaborated into. They are not better than
/// `axiMasterWriter`; they are the same idea with the ports taken away.
///
/// What the three designs demonstrate, in order:
///
/// | design | clients | ports | what appears |
/// |---|---|---|---|
/// | `oneOwnerOnePort` | 1 | 1 | nothing — the client is wired straight to the bus |
/// | `twoOwnersOnePort` | 2 | 1 | a round-robin merge, because two clients share |
/// | `twoOwnersTwoPorts` | 2 | 2 | nothing again, on two independently named buses |
///
/// The kernel is identical in all three and never learns which it got.
[<AutoOpen>]
module Warp11.Designs.BusCatalog

open Warp11

// ---------------------------------------------------------------------------
// Level one: the bus — now `Warp11.Stdlib`'s
// ---------------------------------------------------------------------------
//
// `AxiReadBus`, `AxiWriteBus`, `axiReadBusNamed`, `axiWriteBusNamed` and the
// bus-taking masters were prototyped here and have moved into the library,
// which is where they belonged: a bus is not a property of the design catalog.
// What is left in this file is what uses them — the window constructors over a
// bus region, and the designs that demonstrate the topologies.
//
// The move needed one change to the masters. Their internals were unprefixed,
// so two of them in one module collided on `aw_pending` even with two
// differently named buses — which made `twoOwnersTwoPorts` inexpressible with
// the library's masters and was the only reason this file had its own.

// ---------------------------------------------------------------------------
// The client: it sees two windows and nothing else
// ---------------------------------------------------------------------------

/// The two completion outputs `sumClient` drives — `{name}_handed_over` (the
/// wrong answer, brought out on purpose) and `{name}_done` — declared where the
/// boundary is; `sumClient` in the body is what drives them.
let private sumClientPorts (p: Ports) (name: string) =
    (p.outPort $"{name}_handed_over" 1, p.outPort $"{name}_done" 1)

/// Walk `count` words of `source`, keep a running sum, write each partial sum
/// to the matching index of `sink`.
///
/// **Every parameter is a window, a scalar or the boundary pair.** No bus, no
/// address, no stride, no base — so the same client runs against LUTs, against
/// a block, or against a region of a port on the far side of the chip, and
/// cannot tell.
///
/// It also raises `{name}_done` when its results are **in memory** — not when
/// it handed the last one over. That is one `&&&` against `sink.idle`, and it
/// is the same line in every mapping: against an array `idle` is the constant
/// one and the term folds away, against a region of a port it waits for the
/// write responses to come back. A client that had counted its own writes
/// instead would be right on chip and wrong over a bus, and nothing about the
/// emitted Verilog would say which (`notes/DEVICES.md` §10f).
///
/// `name` prefixes every signal it owns, which is what lets a design hold two.
let sumClient
    (name: string)
    (count: int)
    (run: Expr)
    (source: ReadWindow)
    (sink: WriteWindow)
    (handedOverPort, donePort)
    =
    if count > source.words then
        failwith $"sumClient '{name}': asked for %d{count} words of a %d{source.words}-word source window"

    if count > sink.words then
        failwith $"sumClient '{name}': asked to write %d{count} words to a %d{sink.words}-word sink window"

    if source.wordWidth <> sink.wordWidth then
        failwith
            $"sumClient '{name}': a %d{source.wordWidth}-bit source and a %d{sink.wordWidth}-bit sink"

    let wordWidth = source.wordWidth
    let indexWidth = indexWidthFor count

    // Ask for index 0, 1, ... while `run` holds.
    let index = reg $"{name}_index" indexWidth
    let asking = wireBit $"{name}_asking"
    registerStreamReady asking

    let more = wireBit $"{name}_more"
    (run &&& lt index (lit (uint64 count) indexWidth)) ==> more
    If (more &&& asking) (fun () -> index + lit 1UL indexWidth ==> index)

    let words =
        source.read
            { payload = index
              valid = more
              ready = asking
              layout = layout1 ("index", indexWidth) }

    // Results come back in order, so a second counter is all the bookkeeping
    // the write side needs — no tag, no shadow queue.
    let outIndex = reg $"{name}_out_index" indexWidth
    let writing = wireBit $"{name}_writing"
    registerStreamReady writing
    writing ==> words.ready

    let acc = reg $"{name}_acc" wordWidth
    let total = wire $"{name}_total" wordWidth
    (acc + words.payload) ==> total

    If (words.valid &&& writing) (fun () ->
        total ==> acc
        outIndex + lit 1UL indexWidth ==> outIndex)

    sink.write
        { payload = outIndex, total
          valid = words.valid
          ready = writing
          layout = layout2 ("index", indexWidth) ("word", wordWidth) }

    // `outIndex` reaching `count` says the last word was *accepted*; `idle`
    // says it landed. Both, or the answer is a promise rather than a fact.
    //
    // `handed_over` is the wrong answer, brought out as a port on purpose: a
    // check that only ever watched the right one could not tell whether the
    // `idle` term was doing anything.
    let allAccepted = wireBit $"{name}_all_accepted"
    eq outIndex (lit (uint64 count) indexWidth) ==> allAccepted
    allAccepted ==> handedOverPort
    (allAccepted &&& sink.idle) ==> donePort

// ---------------------------------------------------------------------------
// Three topologies. The client is the same line in all of them.
// ---------------------------------------------------------------------------

let private busCount = 16
let private busIndexWidth = 5
let private busWordWidth = 32

/// The fill ports a check stages a client's source array through — declared
/// where the boundary is; `sourceFor` in the body puts the array behind them.
let private sourceForPorts (p: Ports) (name: string) =
    (p.inPort $"{name}_fill_addr" busIndexWidth,
     p.inPort $"{name}_fill_data" busWordWidth,
     p.inPort $"{name}_fill_enable" 1)

/// A client's own source array, filled through the `sourceForPorts` trio.
///
/// `distributedMem` because `lutReadWindow` reads combinationally and the DSL
/// refuses that on a block. Nothing about the bus depends on this: a client's
/// source and its sink are independent choices, which is the point of there
/// being two windows rather than one memory.
let private sourceFor (name: string) (fillAddr, fillData, fillEnable) =
    let m = distributedMem $"{name}_src" busIndexWidth busWordWidth

    memWrite m fillAddr fillData fillEnable

    lutReadWindow m

/// **One client, one window, one port — and no arbiter.**
let oneOwnerOnePort =
    defModule
        "OneOwnerOnePort"
        (fun p ->
            (axiWriteBusPorts p "m_axi" 32 busWordWidth,
             p.inPort "run" 1,
             sourceForPorts p "a",
             sumClientPorts p "a"))
        (fun (busPorts, run, aSrc, aClient) ->
            let bus = axiWriteBusOf busPorts

            let wa =
                writeWindowOn bus 1 "a" (lit 0x100UL 32) busCount

            sumClient "a" busCount run (sourceFor "a" aSrc) wa aClient)

/// **Two clients, two windows, one port.** The client lines are unchanged; the
/// body opened a second window and a round-robin merge appeared behind it.
let twoOwnersOnePort =
    defModule
        "TwoOwnersOnePort"
        (fun p ->
            (axiWriteBusPorts p "m_axi" 32 busWordWidth,
             p.inPort "run" 1,
             sourceForPorts p "a",
             sumClientPorts p "a",
             sourceForPorts p "b",
             sumClientPorts p "b"))
        (fun (busPorts, run, aSrc, aClient, bSrc, bClient) ->
            let bus = axiWriteBusOf busPorts

            let wa, wb =
                defineWriteWindows bus 1 (fun windows ->
                    createWriteWindow windows "a" (lit 0x100UL 32) busCount,
                    createWriteWindow windows "b" (lit 0x200UL 32) busCount)

            sumClient "a" busCount run (sourceFor "a" aSrc) wa aClient
            sumClient "b" busCount run (sourceFor "b" bSrc) wb bClient)

/// **Two clients, two windows, two ports.** Same two client lines again, each
/// window now on a bus of its own — `m_axi_hp0` and `m_axi_hp1`, two of the
/// independent paths into DDR this repo has never used two of at once.
let twoOwnersTwoPorts =
    defModule
        "TwoOwnersTwoPorts"
        (fun p ->
            (axiWriteBusPorts p "m_axi_hp0" 32 busWordWidth,
             axiWriteBusPorts p "m_axi_hp1" 32 busWordWidth,
             p.inPort "run" 1,
             sourceForPorts p "a",
             sumClientPorts p "a",
             sourceForPorts p "b",
             sumClientPorts p "b"))
        (fun (busPortsA, busPortsB, run, aSrc, aClient, bSrc, bClient) ->
            let busA = axiWriteBusOf busPortsA
            let busB = axiWriteBusOf busPortsB

            let wa =
                writeWindowOn busA 1 "a" (lit 0x100UL 32) busCount

            let wb =
                writeWindowOn busB 1 "b" (lit 0x100UL 32) busCount

            sumClient "a" busCount run (sourceFor "a" aSrc) wa aClient
            sumClient "b" busCount run (sourceFor "b" bSrc) wb bClient)

/// **A window eight writes deep, so "handed over" and "in memory" come apart.**
///
/// The same `sumClient` line again; what differs is `maxOutstanding`. At one
/// outstanding write the client cannot hand over a second word until the first
/// has been acknowledged, so the two answers are never more than a few cycles
/// apart and a check watching them proves little. At eight, the last word is
/// accepted with up to seven still in flight — which is the situation a real
/// accelerator is always in, and the one `idle` exists for.
let sumReportsDone =
    defModule
        "SumReportsDone"
        (fun p ->
            (axiWriteBusPorts p "m_axi" 32 busWordWidth,
             p.inPort "run" 1,
             sourceForPorts p "a",
             sumClientPorts p "a"))
        (fun (busPorts, run, aSrc, aClient) ->
            let bus = axiWriteBusOf busPorts

            let wa =
                writeWindowOn bus 8 "a" (lit 0x100UL 32) busCount

            sumClient "a" busCount run (sourceFor "a" aSrc) wa aClient)

/// Entirely on chip: the same client with an array for a sink instead of a
/// window onto a port. No bus in the design at all, and the client is the same
/// line it is above.
let sumWhollyOnChip =
    defModule
        "SumWhollyOnChip"
        (fun p ->
            (p.inPort "probe_addr" busIndexWidth,
             p.outPort "probe_data" busWordWidth,
             p.inPort "run" 1,
             sourceForPorts p "a",
             sumClientPorts p "a"))
        (fun (probe, probeData, run, aSrc, aClient) ->
            let results = distributedMem "dst" busIndexWidth busWordWidth
            memRead results probe ==> probeData

            sumClient "a" busCount run (sourceFor "a" aSrc) (memWriteWindow results) aClient)

/// Two masters on one bus with no arbiter between them. A function, not a
/// value: elaboration refuses, and refuses *naming the bus*.
///
/// Reaching this takes going around the window layer to the master directly,
/// which is itself the finding — `defineWriteWindows` cannot produce it,
/// because a bus with two windows builds one master over a merge.
let onBusWithTwoOwners () =
    defModule
        "OnBusWithTwoOwners"
        (fun p ->
            let beatsPorts name =
                (p.inPort $"{name}_addr" 32, p.inPort $"{name}_data" busWordWidth, p.inPort $"{name}_valid" 1)

            (axiWriteBusPorts p "m_axi" 32 busWordWidth, beatsPorts "a", beatsPorts "b"))
        (fun (busPorts, aPorts, bPorts) ->
            let bus = axiWriteBusOf busPorts

            let beats name (addr, data, valid) =
                { payload = addr, data, lit 0xFUL 4
                  valid = valid
                  ready =
                    (let r = wireBit $"{name}_ready" in
                     registerStreamReady r
                     r)
                  layout = axiWriteBeatLayout 32 busWordWidth }

            axiMasterWriterOn bus 1 (beats "a" aPorts)
            axiMasterWriterOn bus 1 (beats "b" bPorts))

/// A window opened and never written to. A function, not a value: the merge
/// would otherwise take a floating input, and nothing about the emitted Verilog
/// would say so — `checkStreams` is what turns it into an error.
let onWindowNeverWritten () =
    defModule
        "OnWindowNeverWritten"
        (fun p ->
            (axiWriteBusPorts p "m_axi" 32 busWordWidth,
             p.inPort "run" 1,
             sourceForPorts p "a",
             sumClientPorts p "a"))
        (fun (busPorts, run, aSrc, aClient) ->
            let bus = axiWriteBusOf busPorts

            let wa, _unused =
                defineWriteWindows bus 1 (fun windows ->
                    createWriteWindow windows "a" (lit 0x100UL 32) busCount,
                    createWriteWindow windows "b" (lit 0x200UL 32) busCount)

            sumClient "a" busCount run (sourceFor "a" aSrc) wa aClient)

/// A read window put to work, so the read half is exercised: the same client,
/// its source now a region of a port rather than an array.
let sumFromReadWindow =
    defModule
        "SumFromReadWindow"
        (fun p ->
            (axiReadBusPorts p "m_axi_hp0" 32 busWordWidth,
             axiWriteBusPorts p "m_axi_hp1" 32 busWordWidth,
             p.inPort "run" 1,
             sumClientPorts p "a"))
        (fun (readBusPorts, writeBusPorts, run, aClient) ->
            let readBus = axiReadBusOf readBusPorts
            let writeBus = axiWriteBusOf writeBusPorts

            let wa =
                writeWindowOn writeBus 1 "a" (lit 0x1000UL 32) busCount

            sumClient "a" busCount run (readWindowOn readBus 1 (lit 0x0UL 32) busCount) wa aClient)
