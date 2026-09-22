module Warp11.Designs.Tests.StructuralTests

open System.Text.RegularExpressions
open Expecto
open Warp11
open Warp11.Designs

let private handNestedIfElse =
    moduleDef "IfElseLadder" (fun builder ->
        let x = builder.Input("x", 8)
        let band = builder.Output("band", 8)
        let held = builder.Output("held", 8)
        lit 0UL 8 ==> band
        lit 0UL 8 ==> held

        let innermost () =
            builder
                .If(
                    lt x (lit 16UL 8),
                    fun () ->
                        lit 3UL 8 ==> band
                        lit 33UL 8 ==> held
                )
                .Else(fun () -> lit 4UL 8 ==> band)

        let middle () =
            builder.If(lt x (lit 8UL 8), fun () -> lit 2UL 8 ==> band).Else(innermost)

        builder
            .If(
                lt x (lit 4UL 8),
                fun () ->
                    lit 1UL 8 ==> band
                    lit 11UL 8 ==> held
            )
            .Else(middle))

let private handEncodedSequencer =
    defModule
        "Sequencer"
        (fun ports ->
            (ports.inPort "start" 1,
             ports.inPort "stall" 1,
             ports.outPort "busy" 1,
             ports.outPort "finished" 1,
             ports.outPort "retired" 8))
        (fun (start, stall, busy, finished, retired) ->
            let idle, fetch, decode, execute, writeback, done_ = 0UL, 1UL, 2UL, 3UL, 4UL, 5UL
            let stage = regInit "stage" 3 idle
            let inState state = eq stage (lit state 3)
            let count = reg "count" 8
            bnot (inState idle ||| inState done_) ==> busy
            inState done_ ==> finished
            count ==> retired

            let begin' () =
                If start (fun () ->
                    lit 0UL 8 ==> count
                    lit fetch 3 ==> stage)

            ifElse
                [ inState idle, begin'
                  inState done_, begin'
                  inState fetch, fun () -> lit decode 3 ==> stage
                  inState decode, fun () -> lit execute 3 ==> stage
                  inState execute, fun () -> If (bnot stall) (fun () -> lit writeback 3 ==> stage)
                  inState writeback,
                  fun () ->
                      count + lit 1UL 8 ==> count

                      ifElse
                          [ eq count (lit 3UL 8), fun () -> lit done_ 3 ==> stage
                            otherwise, fun () -> lit fetch 3 ==> stage ] ])

let private siblingSwitchRing =
    defModule
        "SwitchRing"
        (fun ports -> ports.inPort "go" 1, ports.inPort "halt" 1, ports.outPort "phase" 3, ports.outPort "ticks" 8)
        (fun (go, halt, phase, ticks) ->
            let state = machine "stage" [ Load; Warm; Run; Drain; Flush; Park ]
            let count = reg "count" 8
            state.Value ==> phase
            count ==> ticks
            state.If Load (fun () -> If go (fun () -> state.Goto Warm))
            state.If Warm (fun () -> state.Goto Run)

            state.If Run (fun () ->
                count + lit 1UL 8 ==> count
                If halt (fun () -> state.Goto Drain))

            state.If Drain (fun () -> If (eq count (lit 4UL 8)) (fun () -> state.Goto Flush))
            state.If Flush (fun () -> state.Goto Park)

            state.If Park (fun () ->
                If go (fun () ->
                    lit 0UL 8 ==> count
                    state.Goto Load)))

let private ring size useSwitch =
    defModule
        "Ring"
        (fun ports -> ports.inPort "go" 1, ports.outPort "out" (bitsToHold size))
        (fun (go, output) ->
            let states = List.take size allPhases
            let state = machine "stage" states
            state.Value ==> output
            let arm index () = If go (fun () -> state.Goto states[(index + 1) % size])

            if useSwitch then
                state.Switch [ for index in 0 .. size - 1 -> states[index], arm index ]
            else
                for index in 0 .. size - 1 do
                    state.If states[index] (arm index))

let private comparatorCount design =
    Regex.Matches(emitDesign design, "stage == ").Count

let tests =
    testList
        "Structural equivalence"
        [ testCase "ifElse emits the hand-nested mux structure" <| fun _ ->
              Expect.equal (emitDesign ifElseLadder.def) (emitDesign handNestedIfElse) "ifElse should remain structural sugar"

          testCase "machine emits the hand-encoded sequencer structure" <| fun _ ->
              Expect.equal (emitDesign sequencer.def) (emitDesign handEncodedSequencer.def) "machine should preserve the established encoding"

          testCase "Machine.Switch preserves sibling-If behavior" <| fun _ ->
              let switched = Sim switchRing.def
              let sibling = Sim siblingSwitchRing.def

              for cycle in 0..199 do
                  let go = uint64 ((cycle / 3) % 2)
                  let halt = uint64 ((cycle / 7) % 2)

                  for sim in [ switched; sibling ] do
                      sim.Poke("go", go)
                      sim.Poke("halt", halt)
                      sim.Tick()

                  Expect.equal (switched.Peek "phase") (sibling.Peek "phase") $"Phase differs at cycle {cycle}"
                  Expect.equal (switched.Peek "ticks") (sibling.Peek "ticks") $"Counter differs at cycle {cycle}"

          testCase "Machine.Switch comparator growth is linear" <| fun _ ->
              for size in [ 4; 6; 8 ] do
                  Expect.equal (comparatorCount (ring size true).def) size $"Switch should emit one comparator per state at size {size}"
                  Expect.equal
                      (comparatorCount (ring size false).def)
                      ((1 <<< size) - 1)
                      $"Sibling If fixture should continue exercising exponential duplication at size {size}"

          testCase "Machine.Switch rejects duplicate state arms" <| fun _ ->
              Expect.throwsC
                  (fun () ->
                      defModule "Dup" (fun ports -> ports.outPort "phase" 3) (fun output ->
                          let state = machine "stage" [ Load; Warm ]
                          state.Value ==> output
                          state.Switch
                              [ Load, fun () -> state.Goto Warm
                                Load, fun () -> state.Goto Load ])
                      |> ignore)
                  (fun ex -> Expect.stringContains ex.Message "one state, one arm" "Duplicate arms should be diagnosed") ]
