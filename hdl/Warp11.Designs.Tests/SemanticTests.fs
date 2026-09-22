module Warp11.Designs.Tests.SemanticTests

open Expecto
open Warp11
open Warp11.Designs

type private Probe =
    | First
    | Second
    | Never

let private expectThrowsContaining fragment action =
    Expect.throwsC action (fun ex ->
        Expect.stringContains ex.Message fragment "The rejection should identify the invalid state-machine structure")

let tests =
    testList
        "Control semantics"
        [ testCase "ifElse uses first-match ordering over the full byte domain" <| fun _ ->
              let sim = Sim ifElseLadder.def

              for value in 0UL..255UL do
                  sim.Poke("x", value)
                  sim.Tick()

                  let expectedBand, expectedHeld =
                      if value < 4UL then 1UL, 11UL
                      elif value < 8UL then 2UL, 0UL
                      elif value < 16UL then 3UL, 33UL
                      else 4UL, 0UL

                  Expect.equal (sim.Peek "band") expectedBand $"Wrong first-match band for x={value}"
                  Expect.equal (sim.Peek "held") expectedHeld $"Wrong fall-through value for x={value}"

          testCase "ifElse validates and preserves degenerate forms" <| fun _ ->
              let ladderWithoutElse =
                  defModule "Degenerate" (fun ports -> ports.inPort "c" 1, ports.outPort "out" 8) (fun (condition, output) ->
                      lit 0UL 8 ==> output
                      ifElse [ condition, fun () -> lit 5UL 8 ==> output ])

              let plainIf =
                  defModule "Degenerate" (fun ports -> ports.inPort "c" 1, ports.outPort "out" 8) (fun (condition, output) ->
                      lit 0UL 8 ==> output
                      If condition (fun () -> lit 5UL 8 ==> output))

              Expect.equal (emitDesign ladderWithoutElse.def) (emitDesign plainIf.def) "An else-less ladder should emit like If"

              let otherwiseOnly =
                  defModule "Degenerate" (fun ports -> ports.outPort "out" 8) (fun output ->
                      ifElse [ otherwise, fun () -> lit 7UL 8 ==> output ])

              let plainBody =
                  defModule "Degenerate" (fun ports -> ports.outPort "out" 8) (fun output -> lit 7UL 8 ==> output)

              Expect.equal (emitDesign otherwiseOnly.def) (emitDesign plainBody.def) "An otherwise-only ladder should emit its body"
              Expect.throws (fun () -> moduleDef "EmptyLadder" (fun _ -> ifElse []) |> ignore) "An empty ladder should be rejected"

              Expect.throws
                  (fun () ->
                      moduleDef "UnreachableArm" (fun builder ->
                          let condition = builder.Input("c", 1)
                          let output = builder.Output("out", 8)
                          lit 0UL 8 ==> output
                          ifElse
                              [ otherwise, fun () -> lit 1UL 8 ==> output
                                condition, fun () -> lit 2UL 8 ==> output ])
                      |> ignore)
                  "An arm following otherwise should be rejected"

          testCase "machine metadata decodes and stalled states hold" <| fun _ ->
              let inventory = Inventory.ofDesign sequencer.def

              let states =
                  Expect.wantSome
                      (inventory.stateMachines |> Map.tryFind "stage")
                      "The sequencer should expose its state decode"

              Expect.sequenceEqual
                  (Map.toList states)
                  [ 0UL, "Idle"; 1UL, "Fetch"; 2UL, "Decode"; 3UL, "Execute"; 4UL, "Writeback"; 5UL, "Done" ]
                  "State names should retain declaration order and encoding"

              let sim = Sim sequencer.def
              let stateName () = states |> Map.find (sim.Peek "stage")
              sim.Poke("stall", 1UL)
              sim.Poke("start", 1UL)
              sim.Tick()
              sim.Poke("start", 0UL)
              let observed = [ for _ in 1..4 -> sim.Tick(); stateName () ]
              Expect.sequenceEqual observed [ "Decode"; "Execute"; "Execute"; "Execute" ] "Execute should hold while stalled"
              sim.Poke("stall", 0UL)
              sim.Tick()
              Expect.equal (stateName ()) "Writeback" "The machine should advance when stall clears"

          testCase "machine rejects unreachable and unknown states" <| fun _ ->
              expectThrowsContaining "can never reach Never" (fun () ->
                  defModule "Unreachable" (fun ports -> ports.inPort "go" 1) (fun go ->
                      let state = machine "st" [ First; Second; Never ]
                      state.If First (fun () -> If go (fun () -> state.Goto Second))
                      state.If Second (fun () -> state.Goto First))
                  |> ignore)

              expectThrowsContaining "is not a state of 'st'" (fun () ->
                  moduleDef "Unknown" (fun _ ->
                      let state = machine "st" [ First; Second ]
                      state.If First (fun () -> state.Goto Never))
                  |> ignore) ]
