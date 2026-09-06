/// The tutorial catalog: what the debugger lists when it is opened as a
/// tutorial, in the order someone meeting warp11 should read them.
///
/// Order is the curriculum. `Counter` first because it is the smallest thing
/// with a register in it; `Comparator` second because it takes the register
/// away again and leaves combinational logic on its own; `FSM` last
/// because everything before it is a piece of what it is made of.
module Warp11.Tutorial.Registry

open Warp11.Catalog

let catalog =
    embedded
        (System.Reflection.Assembly.GetExecutingAssembly())
        "Designs.fs"
        [ entry "Counter" (nameof counter) (fun () -> counter.def)
          |> watching [ "r" ]
          |> poking [ "enable", 1UL ]
          entry "Comparator" (nameof comparator) (fun () -> comparator.def)
          entry "Priority mux" (nameof priorityMux) (fun () -> priorityMux.def)
          entry "Dot product" (nameof dotProduct) (fun () -> dotProduct.def)
          entry "Your own modules" (nameof ownModules) (fun () -> ownModules.def)
          |> watching [ "left_r"; "right_r"; "satAcc8_1_r" ]
          |> poking [ "add_left", 3UL; "add_right", 5UL; "en", 1UL ]
          entry "Bit shapes" (nameof bitShapes) (fun () -> bitShapes.def)
          entry "Signed operations" (nameof signedOps) (fun () -> signedOps.def)
          entry "Fixed-point" (nameof fixedPoint) (fun () -> fixedPoint.def)
          entry "RAM" (nameof ram) (fun () -> ram.def)
          entry "ROM" (nameof romTable) (fun () -> romTable.def)
          entry "Assertions" (nameof assertions) (fun () -> assertions.def) |> watching [ "r" ]
          entry "FSM" (nameof fsm) (fun () -> fsm.def)

          // Tier 1 — the combinators.
          entry "Delay chain" (nameof delayAlign) (fun () -> delayAlign.def)
          entry "Edge detect" (nameof edges) (fun () -> edges.def)
          entry "LFSR" (nameof noise) (fun () -> noise.def)
          entry "Arbiter (one-hot)" (nameof arbiter) (fun () -> arbiter.def)
          entry "Adder tree" (nameof adderTree) (fun () -> adderTree.def)
          entry "Wrap counters" (nameof wrapCounter) (fun () -> wrapCounter.def)

          // Tier 2 — the ready/valid layer, in the order it builds up.
          entry "Stream pipe" (nameof streamPipe) (fun () -> streamPipe.def)
          entry "Stream stages" (nameof streamStages) (fun () -> streamStages.def)
          entry "Your own stage" (nameof ownStage) (fun () -> ownStage.def)
          |> poking [ "in_value", 5UL; "in_valid", 1UL; "out_ready", 1UL ]
          entry "Buffering" (nameof streamBuffer) (fun () -> streamBuffer.def)
          entry "Fork and join" (nameof streamFork) (fun () -> streamFork.def)
          entry "Farm" (nameof streamFarm) (fun () -> streamFarm.def)
          entry "Carrying context" (nameof streamContext) (fun () -> streamContext.def)
          entry "Stall probes" (nameof streamProbes) (fun () -> streamProbes.def)
          entry "Pipeline as data" (nameof streamPipeline) (fun () -> streamPipeline.def)
          entry "Flow (valid only)" (nameof flowSampler) (fun () -> flowSampler.def)

          // Tier 3 — the substrates, and the constraints that come with them.
          entry "Barrel lane" (nameof barrelLane) (fun () -> barrelLane.def) |> watching [ "turn" ]
          entry "PRNG" (nameof prng) (fun () -> prng.def)
          entry "FIR filter" (nameof firFilter) (fun () -> firFilter.def)
          entry "Neighborhood" (nameof lifeCell) (fun () -> lifeCell.def)
          entry "Shared unit" (nameof sharedUnit) (fun () -> sharedUnit.def)
          entry "Register map" (nameof registerMap) (fun () -> registerMap.def)
          entry "DDR master" (nameof ddrMaster) (fun () -> ddrMaster.def) ]
