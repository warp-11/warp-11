/// The tutorial catalog: what the debugger lists when it is opened as a
/// tutorial, in the order someone meeting warp11 should read them.
///
/// Order is the curriculum. `Counter` first because it is the smallest thing
/// with a register in it; `Comparator` second because it takes the register
/// away again and leaves combinational logic on its own; `Sequencer` last
/// because everything before it is a piece of what it is made of.
module Warp11.Tutorial.Registry

open Warp11.Catalog

let catalog =
    embedded
        (System.Reflection.Assembly.GetExecutingAssembly())
        "Designs.fs"
        [ entry "Counter" (nameof counter) (fun () -> counter)
          |> watching [ "r" ]
          |> poking [ "enable", 1UL ]
          entry "Comparator" (nameof comparator) (fun () -> comparator)
          entry "Priority mux" (nameof priorityMux) (fun () -> priorityMux)
          entry "Dot product" (nameof dotProduct) (fun () -> dotProduct)
          entry "Your own modules" (nameof ownModules) (fun () -> ownModules)
          |> watching [ "left_r"; "right_r" ]
          |> poking [ "add_left", 3UL; "add_right", 5UL; "en", 1UL ]
          entry "Bit shapes" (nameof bitShapes) (fun () -> bitShapes)
          entry "Signed operations" (nameof signedOps) (fun () -> signedOps)
          entry "Fixed-point" (nameof fixedPoint) (fun () -> fixedPoint)
          entry "RAM" (nameof ram) (fun () -> ram)
          entry "ROM" (nameof romTable) (fun () -> romTable)
          entry "Assertions" (nameof assertions) (fun () -> assertions) |> watching [ "r" ]
          entry "Sequencer" (nameof sequencer) (fun () -> sequencer)

          // Tier 1 — the combinators.
          entry "Delay chain" (nameof delayAlign) (fun () -> delayAlign)
          entry "Edge detect" (nameof edges) (fun () -> edges)
          entry "LFSR" (nameof noise) (fun () -> noise)
          entry "Arbiter (one-hot)" (nameof arbiter) (fun () -> arbiter)
          entry "Adder tree" (nameof adderTree) (fun () -> adderTree)
          entry "Wrap counters" (nameof wrapCounter) (fun () -> wrapCounter)

          // Tier 2 — the ready/valid layer, in the order it builds up.
          entry "Stream pipe" (nameof streamPipe) (fun () -> streamPipe)
          entry "Stream stages" (nameof streamStages) (fun () -> streamStages)
          entry "Buffering" (nameof streamBuffer) (fun () -> streamBuffer)
          entry "Fork and join" (nameof streamFork) (fun () -> streamFork)
          entry "Farm" (nameof streamFarm) (fun () -> streamFarm)
          entry "Carrying context" (nameof streamContext) (fun () -> streamContext)
          entry "Stall probes" (nameof streamProbes) (fun () -> streamProbes)
          entry "Pipeline as data" (nameof streamPipeline) (fun () -> streamPipeline)
          entry "Flow (valid only)" (nameof flowSampler) (fun () -> flowSampler)

          // Tier 3 — the substrates, and the constraints that come with them.
          entry "Barrel lane" (nameof barrelLane) (fun () -> barrelLane) |> watching [ "turn" ]
          entry "PRNG" (nameof prng) (fun () -> prng)
          entry "FIR filter" (nameof firFilter) (fun () -> firFilter)
          entry "Neighborhood" (nameof lifeCell) (fun () -> lifeCell)
          entry "Shared unit" (nameof sharedUnit) (fun () -> sharedUnit)
          entry "Register map" (nameof registerMap) (fun () -> registerMap)
          entry "DDR master" (nameof ddrMaster) (fun () -> ddrMaster) ]
