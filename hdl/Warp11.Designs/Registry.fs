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

let catalog =
    embedded
        (System.Reflection.Assembly.GetExecutingAssembly())
        "Designs.fs"
        [ entry "Counter" (nameof onCounter) (fun () -> onCounter)
          entry "Counter (explicit builder)" (nameof counterMutable) (fun () -> counterMutable)
          entry "Comparator" (nameof comparator8) (fun () -> comparator8)
          entry "Add3" (nameof add3) (fun () -> add3)
          entry "Dot product" (nameof dot2Ambient) (fun () -> dot2Ambient)
          entry "Dot product, pipelined" (nameof pipelinedDot) (fun () -> pipelinedDot)
          entry "Delay chain (4 deep)" (nameof loopPipeline) (fun () -> loopPipeline)
          entry "Gated counter" (nameof gatedCounter) (fun () -> gatedCounter)
          entry "Hold through reset" (nameof holdThroughReset) (fun () -> holdThroughReset)
          entry "Dynamic shifts" (nameof dynamicShifts) (fun () -> dynamicShifts)
          entry "Bit reductions" (nameof bitReductions) (fun () -> bitReductions)
          entry "Constant division" (nameof constantDivision) (fun () -> constantDivision)
          entry "Stream divider" (nameof streamDivider) (fun () -> streamDivider)
          entry "Masked write (wide)" (nameof maskedWriteWide) (fun () -> maskedWriteWide)
          entry "Masked write" (nameof maskedWrite) (fun () -> maskedWrite)
          entry "Pipelined read channel" (nameof pipelinedReadSlave) (fun () -> pipelinedReadSlave)
          entry "Deep read channel" (nameof deepChannelSlave) (fun () -> deepChannelSlave)
          entry "Two-window slave" (nameof twoWindowSlave) (fun () -> twoWindowSlave)
          entry "Carried read" (nameof carriedRead) (fun () -> carriedRead)
          entry "Buffered stream" (nameof bufferedStream) (fun () -> bufferedStream)
          entry "Buffered stream (block RAM)" (nameof deepBufferedStream) (fun () -> deepBufferedStream)
          entry "Tagged divide" (nameof taggedDivide) (fun () -> taggedDivide)
          entry "Farmed divide" (nameof farmedDivide) (fun () -> farmedDivide)
          entry "Priority mux" (nameof onPriority) (fun () -> onPriority)
          entry "Sequencer (state machine)" (nameof sequencer) (fun () -> sequencer)
          entry "LFSR source" (nameof lfsrSource) (fun () -> lfsrSource)
          entry "Priority scan (one-hot)" (nameof oneHotScan) (fun () -> oneHotScan)
          entry "One-hot mux" (nameof mux1HSelect) (fun () -> mux1HSelect)
          entry "Edge detector" (nameof edgeDetector) (fun () -> edgeDetector)
          entry "Flow sampler (valid-only)" (nameof flowSampler) (fun () -> flowSampler)
          entry "Clock dividers (counter)" (nameof dividers) (fun () -> dividers)
          entry "Bit shapes" (nameof bitShapes) (fun () -> bitShapes)
          entry "Adder tree (8 inputs)" (nameof treeSum) (fun () -> treeSum)
          entry "RAM, sync and async read" (nameof ramTest) (fun () -> ramTest)
          entry "Filling memory (256 words)" (nameof fillingMemory) (fun () -> fillingMemory)
          entry "Command processor (union + mem)" (nameof cmdProcessor) (fun () -> cmdProcessor)
          entry "Stream pipe" (nameof streamPipe) (fun () -> streamPipe)
          entry "Fork and join" (nameof forkJoin) (fun () -> forkJoin)
          entry "Signed operations" (nameof signedOps) (fun () -> signedOps)
          entry "Neighborhood count" (nameof neighborCount) (fun () -> neighborCount)
          entry "AXI-Lite scratch registers" (nameof regMapScratch) (fun () -> regMapScratch)
          entry "Frame pipeline" (nameof framePipeline) (fun () -> framePipeline)
          entry "Sweep pipeline (4 workers)" (nameof sweepPipeline) (fun () -> sweepPipeline 4) ]

let designs = catalog.entries

/// The command-line escape hatch: open straight to one design by label.
let tryFind (label: string) =
    designs
    |> List.tryFind (fun e -> System.String.Equals(e.label, label, System.StringComparison.OrdinalIgnoreCase))
    |> Option.map (fun e -> e.build)
