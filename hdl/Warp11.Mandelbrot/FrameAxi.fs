/// The full-scale accelerator behind its AXI boundary: the frame pod, the
/// control slave (register map mirroring Kotlin's `MandelbrotRegs`), the
/// frame-cycle counter, and the 128-bit AXI master into PS DDR. The same
/// elaboration serves the scaled oracle config and the 104-lane silicon
/// config — only the parameters differ.
module Warp11.Mandelbrot.FrameAxi

open Warp11
open Warp11.Mandelbrot.LanePod
open Warp11.Mandelbrot.FramePod

/// The register map, one definition consumed twice: the slave elaboration
/// below and the generated Rust layout (the seam). Offsets mirror the Kotlin
/// map; 0x000 reads as the ID and writes as the start pulse (a pulse register
/// never joins the read mux, so both live at one word).
let internal frameIdMagic = 0xF5B0D002UL // "F# pod v2" — the frame successor to the mini pod's ...001
let internal frameStartOffset = 0x000UL
let internal frameBusyOffset = 0x004UL
let internal frameDoneOffset = 0x008UL
let internal frameCxOffset = 0x00CUL
let internal frameCyOffset = 0x010UL
let internal frameDxOffset = 0x014UL
let internal frameDyOffset = 0x018UL
let internal frameCyclesOffset = 0x01CUL
let internal frameFbBaseOffset = 0x020UL
let internal frameApertureAddrWidth = 12

let mandelFrameAxi
    (topName: string)
    (width: int)
    (height: int)
    (maxIter: int)
    (fracBits: int)
    (nThreads: int)
    (numLanes: int)
    =
    designClocked axiClock topName (fun () ->
        let addrWidth = lanePodAddrWidth width height

        // Status sources feed the slave through wires/regs declared first —
        // the slave's read mux needs them before the core exists.
        let busyW = wireBit "busy_w"
        let doneSticky = regBit "done_sticky" // latched frameDone, cleared on start (poll-friendly)
        let cycles = reg "cycles" 32 // cleared on start, counts while busy, freezes at done

        let pulses, viewRegs =
            axiLiteSlaveFull
                frameApertureAddrWidth
                [ "start", frameStartOffset ]
                [ "cxOrigin", frameCxOffset, 32
                  "cyOrigin", frameCyOffset, 32
                  "dx", frameDxOffset, 32
                  "dy", frameDyOffset, 32
                  "fbBaseAddr", frameFbBaseOffset, 32 ]
                [ frameStartOffset, lit frameIdMagic 32 // 0x000 reads as ID
                  frameBusyOffset, busyW
                  frameDoneOffset, doneSticky
                  frameCyclesOffset, cycles ]
                []

        let startPulse, cxOrigin, cyOrigin, dx, dy, fbBaseAddr =
            match pulses, viewRegs with
            | [ p ], [ cx; cy; dx; dy; fb ] -> p, cx, cy, dx, dy, fb
            | _ -> failwith "unexpected slave register shape"

        let piped =
            frameCmdStream startPulse cxOrigin cyOrigin dx dy
            |> mandelFramePipeline width height maxIter fracBits nThreads numLanes

        let beats, busy, frameDone =
            instanceNamed "gather" (mandelFrameGatherer width height) startPulse piped

        busy ==> busyW

        If startPulse (fun () -> lit 0UL 1 ==> doneSticky)
        Else (fun () -> If frameDone (fun () -> lit 1UL 1 ==> doneSticky))
        If startPulse (fun () -> lit 0UL 32 ==> cycles)
        Else (fun () -> If busy (fun () -> cycles + lit 1UL 32 ==> cycles))

        beats
        |> streamProbe "egress"
        |> streamMapTo (axiWriteBeatLayout 32 128) (fun (addr, beat) -> (fbBaseAddr + cat (lit 0UL (32 - addrWidth)) addr, beat, lit 0xFFFFUL 16))
        |> axiMasterWriter 32 128 16)

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
