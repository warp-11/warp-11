/// The oracle catalog as something a debugger can be opened on.
///
/// Curated rather than complete: the catalog next door wants coverage of every
/// IR node, and this wants something to look at — registers to watch, instances
/// to group under, a memory to window, a handshake that stalls, and one design
/// big enough that the filter box is the only way to find anything.
///
/// It carries no pages. Teaching is `Warp11.Tutorial`'s job, and these designs
/// are shaped by what the differential oracle needs rather than by what reads
/// well. The `source` pane still works, which is all this list ever wanted.
module Warp11.Designs.Registry

open Warp11.Catalog
open Warp11.Designs.Catalog
open Warp11.Designs.MemoryCatalog
open Warp11.Designs.BusCatalog

let catalog =
    embeddedFrom
        (System.Reflection.Assembly.GetExecutingAssembly())
        [ "MemoryDesigns.fs"; "BusDesigns.fs"; "Designs.fs" ]
        [ entry "Counter" (nameof onCounter) (fun () -> onCounter.def)
          entry "Counter (explicit builder)" (nameof counterMutable) (fun () -> counterMutable)
          entry "Comparator" (nameof comparator8) (fun () -> comparator8.def)
          entry "Add3" (nameof add3) (fun () -> add3)
          entry "Dot product" (nameof dot2Ambient) (fun () -> dot2Ambient.def)
          entry "Dot product, pipelined" (nameof pipelinedDot) (fun () -> pipelinedDot.def)
          entry "Delay chain (4 deep)" (nameof loopPipeline) (fun () -> loopPipeline.def)
          entry "Gated counter" (nameof gatedCounter) (fun () -> gatedCounter.def)
          entry "Hold through reset" (nameof holdThroughReset) (fun () -> holdThroughReset.def)
          entry "Dynamic shifts" (nameof dynamicShifts) (fun () -> dynamicShifts.def)
          entry "Bit reductions" (nameof bitReductions) (fun () -> bitReductions.def)
          entry "Constant division" (nameof constantDivision) (fun () -> constantDivision.def)
          entry "Stream divider" (nameof streamDivider) (fun () -> streamDivider.def)
          entry "Masked write (wide)" (nameof maskedWriteWide) (fun () -> maskedWriteWide.def)
          entry "Masked write" (nameof maskedWrite) (fun () -> maskedWrite.def)
          entry "Pipelined read channel" (nameof pipelinedReadSlave) (fun () -> pipelinedReadSlave.def)
          entry "Deep read channel" (nameof deepChannelSlave) (fun () -> deepChannelSlave.def)
          entry "Two-window slave" (nameof twoWindowSlave) (fun () -> twoWindowSlave.def)
          entry "Carried read" (nameof carriedRead) (fun () -> carriedRead.def)
          entry "Boundary walk (stream ports)" (nameof boundaryWalk) (fun () -> boundaryWalk.def)
          entry "Buffered stream" (nameof bufferedStream) (fun () -> bufferedStream.def)
          entry "Buffered stream (block RAM)" (nameof deepBufferedStream) (fun () -> deepBufferedStream.def)
          entry "Tagged divide" (nameof taggedDivide) (fun () -> taggedDivide.def)
          entry "Farmed divide" (nameof farmedDivide) (fun () -> farmedDivide.def)
          entry "Priority mux" (nameof onPriority) (fun () -> onPriority.def)
          entry "Priority ladder (ifElse)" (nameof ifElseLadder) (fun () -> ifElseLadder.def)
          entry "State ladder (Switch)" (nameof switchRing) (fun () -> switchRing.def)
          entry "Sequencer (state machine)" (nameof sequencer) (fun () -> sequencer.def)
          entry "LFSR source" (nameof lfsrSource) (fun () -> lfsrSource.def)
          entry "Priority scan (one-hot)" (nameof oneHotScan) (fun () -> oneHotScan.def)
          entry "One-hot mux" (nameof mux1HSelect) (fun () -> mux1HSelect.def)
          entry "Edge detector" (nameof edgeDetector) (fun () -> edgeDetector.def)
          entry "Flow sampler (valid-only)" (nameof flowSampler) (fun () -> flowSampler.def)
          entry "Clock dividers (counter)" (nameof dividers) (fun () -> dividers.def)
          entry "Bit shapes" (nameof bitShapes) (fun () -> bitShapes.def)
          entry "Adder tree (8 inputs)" (nameof treeSum) (fun () -> treeSum.def)
          entry "RAM, sync and async read" (nameof ramTest) (fun () -> ramTest.def)
          entry "Two read ports" (nameof dualRead) (fun () -> dualRead.def)
          entry "Filling memory (256 words)" (nameof fillingMemory) (fun () -> fillingMemory.def)
          entry "Priority write (three sites)" (nameof priorityWrite) (fun () -> priorityWrite.def)
          entry "Masked write (two sites)" (nameof maskedWritePriority) (fun () -> maskedWritePriority.def)
          entry "ROM lookup (LUTs)" (nameof romLookup) (fun () -> romLookup.def)
          entry "ROM lookup (block RAM)" (nameof blockRomLookup) (fun () -> blockRomLookup.def)
          entry "Running sum over LUTs" (nameof sumOverLut) (fun () -> sumOverLut.def)
          entry "Running sum over block RAM" (nameof sumOverBlock) (fun () -> sumOverBlock.def)
          entry "Running sum over UltraRAM" (nameof sumOverUltra) (fun () -> sumOverUltra.def)
          entry "Running sum over DDR" (nameof sumOverDdr) (fun () -> sumOverDdr.def)
          entry "Bus: one owner, one port" (nameof oneOwnerOnePort) (fun () -> oneOwnerOnePort.def)
          entry "Bus: two owners, one port" (nameof twoOwnersOnePort) (fun () -> twoOwnersOnePort.def)
          entry "Bus: two owners, two ports" (nameof twoOwnersTwoPorts) (fun () -> twoOwnersTwoPorts.def)
          entry "Bus: sum from a read window" (nameof sumFromReadWindow) (fun () -> sumFromReadWindow.def)
          entry "Window: done means landed" (nameof sumReportsDone) (fun () -> sumReportsDone.def)
          entry "Window: wholly on chip" (nameof sumWhollyOnChip) (fun () -> sumWhollyOnChip.def)
          entry "Command processor (union + mem)" (nameof cmdProcessor) (fun () -> cmdProcessor.def)
          entry "Stream pipe" (nameof streamPipe) (fun () -> streamPipe.def)
          entry "Fork and join" (nameof forkJoin) (fun () -> forkJoin.def)
          entry "Signed operations" (nameof signedOps) (fun () -> signedOps.def)
          entry "Neighborhood count" (nameof neighborCount) (fun () -> neighborCount.def)
          entry "AXI-Lite scratch registers" (nameof regMapScratch) (fun () -> regMapScratch.def)
          entry "Frame pipeline" (nameof framePipeline) (fun () -> framePipeline.def)
          entry "Sweep pipeline (4 workers)" (nameof sweepPipeline) (fun () -> (sweepPipeline 4).def)
          entry "Line window (3-row stencil feed)" (nameof windowSweep) (fun () -> windowSweep.def)
          entry "Pixel blur (3x3 over 8-bit cells)" (nameof pixelBlur) (fun () -> pixelBlur.def)
          entry "Moving average (unframed taps)" (nameof movingAverage) (fun () -> movingAverage.def)
          entry "Sparse dot (static irregular)" (nameof sparseDot) (fun () -> sparseDot.def)
          entry "Indirect gather (dynamic irregular)" (nameof indirectGather) (fun () -> indirectGather.def)
          entry "Index sweep (rangeStream)" (nameof indexSweep) (fun () -> indexSweep.def)
          entry "Max-pool dilation (banded world)" (nameof maxPoolDilate) (fun () -> maxPoolDilate.def)
          entry "Max pool 2x2, stride 2 (CNN layer)" (nameof maxPool2x2) (fun () -> maxPool2x2.def)
          entry "I2S link (MEMS pins)" (nameof i2sLinkPassthru) (fun () -> i2sLinkPassthru.def)
          entry "I2S link (codec pins)" (nameof i2sLinkCodec) (fun () -> i2sLinkCodec.def)
          entry "I2S link (transmit only)" (nameof i2sLinkTone) (fun () -> i2sLinkTone.def)
          entry "I2S link + half volume" (nameof i2sLinkHalfVolume) (fun () -> i2sLinkHalfVolume.def)
          entry "Select ladder (first match wins)" (nameof selectLadder) (fun () -> selectLadder.def)
          entry "Max pool, combinational" (nameof maxPoolCombinational) (fun () -> maxPoolCombinational.def)
          entry "Delay tap (accepted beats)" (nameof delayTap) (fun () -> delayTap.def)
          |> poking [ "enable", 1UL; "tap", 8UL ]
          entry "Echo (memory delay line)" (nameof audioEchoStage) (fun () -> audioEchoStage.def)
          |> poking [ "delay", 64UL; "feedback", 128UL; "in_valid", 1UL; "out_ready", 1UL ] ]

let designs = catalog.entries

/// The command-line escape hatch: open straight to one design by label.
let tryFind (label: string) =
    designs
    |> List.tryFind (fun e -> System.String.Equals(e.label, label, System.StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun e -> e.build)
