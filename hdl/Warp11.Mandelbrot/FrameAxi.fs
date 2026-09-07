/// The full-scale accelerator behind its AXI boundary: the frame pod, the
/// control slave (register map mirroring the original `MandelbrotRegs`), the
/// frame-cycle counter, and the 128-bit AXI master into PS DDR. The same
/// elaboration serves the scaled oracle config and the 104-lane silicon
/// config — only the parameters differ.
module Warp11.Mandelbrot.FrameAxi

open Warp11
open Warp11.Mandelbrot.LanePod
open Warp11.Mandelbrot.FramePod

/// The register map, one definition consumed twice: the slave elaboration below
/// and the generated Rust layout. Written with the builder, so no offset
/// appears here — declaration order is the layout, and the addresses it
/// produces are the ones the hand-written map had.
///
/// 0x000 reads as the ID and writes as the start pulse: a constant owns a
/// word's read side and a pulse bit owns its write side, so both live at one
/// word.
let internal frameIdMagic = 0xF5B0D002UL // "F# pod v2" — the frame successor to the mini pod's ...001
let internal frameApertureAddrWidth = 12

type FrameRegs =
    { id: RegEntry
      start: RegEntry
      busy: RegEntry
      doneSticky: RegEntry
      cxOrigin: RegEntry
      cyOrigin: RegEntry
      dx: RegEntry
      dy: RegEntry
      cycles: RegEntry
      fbBaseAddr: RegEntry }

let frameRegs, frameMap =
    buildRegMapPinned frameApertureAddrWidth (fun r ->
        let id, start = r.Word(fun w -> w.Const("id", frameIdMagic), w.Pulse "start")

        let regs =
            { id = id
              start = start
              busy = r.RoField("busy", 1)
              doneSticky = r.RoField("done", 1)
              cxOrigin = r.RwReg("cxOrigin", 32, 0UL)
              cyOrigin = r.RwReg("cyOrigin", 32, 0UL)
              dx = r.RwReg("dx", 32, 0UL)
              dy = r.RwReg("dy", 32, 0UL)
              cycles = r.RoField("frameCycles", 32)
              fbBaseAddr = r.RwReg("fbBaseAddr", 32, 0UL) }

        // Which revision of this map the fabric was built from. Allocated
        // AFTER every existing register, so the migration keeps the addresses
        // the hand-written map had and only adds a word at the end — the
        // identity says which design, this says whether the host's offsets
        // still mean what they meant.
        r.LayoutHash "layoutHash"
        regs)

let mandelFrameAxi
    (topName: string)
    (width: int)
    (height: int)
    (maxIter: int)
    (fracBits: int)
    (nThreads: int)
    (numLanes: int)
    =
    defModuleClocked
        axiClock
        topName
        (fun p -> axiLiteSlavePorts p frameApertureAddrWidth, axiWriteBusPorts p "m_axi" 32 128)
        (fun (slavePorts, writeBusPorts) ->
        let addrWidth = lanePodAddrWidth width height

        // Status sources feed the slave through wires/regs declared first —
        // the slave's read mux needs them before the core exists.
        let busyW = wireBit "busy_w"
        let doneSticky = regBit "done_sticky" // latched frameDone, cleared on start (poll-friendly)
        let cycles = reg "cycles" 32 // cleared on start, counts while busy, freezes at done

        // Keyed by entry rather than by position: the list slave handed back
        // two lists that had to be destructured in the right order, with a
        // `failwith` guarding the shape. That guard is gone because the shape
        // cannot be wrong.
        let regs = regMapSlave slavePorts frameMap

        regs.drive frameRegs.busy busyW
        regs.drive frameRegs.doneSticky doneSticky
        regs.drive frameRegs.cycles cycles

        let startPulse = regs.pulse frameRegs.start
        let cxOrigin = regs.value frameRegs.cxOrigin
        let cyOrigin = regs.value frameRegs.cyOrigin
        let dx = regs.value frameRegs.dx
        let dy = regs.value frameRegs.dy
        let fbBaseAddr = regs.value frameRegs.fbBaseAddr

        // The framebuffer as a window. The design hands over beat indices and
        // the pixels in them; where index zero lives, how far apart beats are
        // and which lanes a write covers are the window's business — which is
        // why `fbBaseAddr` reaches it as a parameter and appears nowhere below.
        //
        // The index is the beat's byte offset with its low four bits dropped,
        // because a 128-bit beat is sixteen bytes and the window shifts them
        // straight back. Free in hardware, and it keeps the offset arithmetic
        // out of the design. Indices need not be sequential: beats leave the
        // lanes in arbitration order and carry their own coordinates.
        //
        // Opened here rather than beside the write below because `frame.idle`
        // is what the done register is gated on, and that is stated before the
        // pipeline exists.
        let frame =
            writeWindowOn (axiWriteBusOf writeBusPorts) 16 "fb" fbBaseAddr (1 <<< (addrWidth - 4))

        let piped =
            frameCmdStream startPulse cxOrigin cyOrigin dx dy
            |> mandelFramePipeline width height maxIter fracBits nThreads numLanes

        let beats, busy, frameDone =
            mandelFrameGatherer width height "gather" startPulse piped

        busy ==> busyW

        // `frameDone` is the last beat *leaving the gatherer* — one stage
        // upstream of the master, so it fires with up to sixteen writes still
        // in flight, roughly 256 bytes of the frame's tail not yet in DDR. The
        // host-visible done is that event held until the framebuffer says every
        // word landed (`notes/DEVICES.md` §10f).
        //
        // It has never manifested: polling this register is an AXI-Lite read
        // through the PS against a drain of a few hundred nanoseconds, a race
        // that always loses. A race that always loses is still a race.
        let allGathered = regBit "all_gathered"

        ifElse [
            (startPulse, fun () -> lit 0UL 1 ==> allGathered)
            (otherwise, fun () -> If frameDone (fun () -> lit 1UL 1 ==> allGathered)) ]

        ifElse [
            (startPulse, fun () -> lit 0UL 1 ==> doneSticky)
            (otherwise, fun () -> If (allGathered &&& frame.idle) (fun () -> lit 1UL 1 ==> doneSticky)) ]

        // `cycles` stays on `busy` — the compute time, which is the number the
        // frame budget is written in. The drain is a handful of cycles on top
        // and belongs to whoever measures egress, not to this register.
        ifElse [
            (startPulse, fun () -> lit 0UL 32 ==> cycles)
            (otherwise, fun () -> If busy (fun () -> cycles + lit 1UL 32 ==> cycles)) ]

        beats
        |> streamProbe "egress"
        |> streamMapTo
            (layout2 ("index", addrWidth - 4) ("word", 128))
            (fun (addr, beat) -> slice (addrWidth - 1) 4 addr, beat)
        |> frame.write)

/// The oracle/rehearsal config — the same architecture the scaled render
/// proved, now behind the real register map.
let mandelFrameAxiScaled = mandelFrameAxi "MandelFrameAxiScaled" 64 48 48 28 8 4

/// The silicon config: 1400×800 / 256 / 104 lanes / 8 threads — 100% DSP
/// (104 lanes × 3 mults × 4 DSP48 = 1,248). One definition feeds the
/// elaboration and the generated Rust layout.
let frameFullWidth = 1400
let frameFullHeight = 800
let frameFullMaxIter = 256
let frameFullFracBits = 28
let frameFullThreads = 8
let frameFullLanes = 104

/// Lazy, so only the seam emit pays the elaboration.
let mandelFrameAxiFull =
    lazy (mandelFrameAxi "MandelFrameAxi" frameFullWidth frameFullHeight frameFullMaxIter frameFullFracBits frameFullThreads frameFullLanes)
