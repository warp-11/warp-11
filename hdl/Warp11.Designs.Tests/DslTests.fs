module Warp11.Designs.Tests.DslTests

open Expecto
open Warp11
open Warp11.Designs

let private expectThrowsContaining fragment action =
    Expect.throwsC action (fun ex ->
        Expect.stringContains ex.Message fragment "The rejection should identify its cause")

let tests =
    testList
        "DSL semantics"
        [ testCase "register without reset retains its value" <| fun _ ->
              let sim = Sim holdThroughReset.def
              sim.Poke("value", 42UL)
              sim.Tick()

              Expect.equal (sim.Peek "held_out") 42UL "Both registers should take the input"
              Expect.equal (sim.Peek "cleared_out") 42UL "Both registers should take the input"

              sim.Reset()

              Expect.equal (sim.Peek "held_out") 42UL "regNoReset should retain its value"
              Expect.equal (sim.Peek "cleared_out") 3UL "A resettable register should return to its initial value"

              let verilog = emitDesign holdThroughReset.def
              let start = verilog.IndexOf "if (rst)"
              let stop = verilog.IndexOf("end else", start)
              let resetBranch = verilog.Substring(start, stop - start)
              Expect.stringContains resetBranch "cleared <=" "The reset branch should assign the resettable register"
              Expect.isFalse (resetBranch.Contains "held <=") "The reset branch should not assign regNoReset"

          testCase "dynamic shifts agree for every amount" <| fun _ ->
              let sim = Sim dynamicShifts.def

              for value in [ 1UL; 0xA5UL; 0xFFUL ] do
                  for amount in 0UL..7UL do
                      sim.Poke("value", value)
                      sim.Poke("amount", amount)
                      sim.Poke("signed_value", value)
                      sim.Tick()

                      let label = $"value=0x{value:X2}, amount={amount}"
                      Expect.equal (sim.Peek "shifted_left") (value <<< int amount) $"Left shift differs for {label}"
                      Expect.equal (sim.Peek "shifted_right") (value >>> int amount) $"Logical shift differs for {label}"

                      let arithmetic = uint64 ((int64 (sbyte value)) >>> int amount) &&& 0xFFUL
                      Expect.equal (sim.Peek "shifted_arith") arithmetic $"Arithmetic shift differs for {label}"
                      Expect.equal (sim.Peek "shifted_fixed") (value <<< 3) $"Constant shift differs for {label}"

          testCase "reductions agree for every byte" <| fun _ ->
              let sim = Sim bitReductions.def

              for value in 0UL..255UL do
                  sim.Poke("value", value)
                  sim.Tick()

                  let bits = [ for i in 0..7 -> (value >>> i) &&& 1UL ]
                  let any = if List.exists ((=) 1UL) bits then 1UL else 0UL
                  let all = if List.forall ((=) 1UL) bits then 1UL else 0UL
                  let odd = List.sum bits % 2UL

                  Expect.equal (sim.Peek "any") any $"OR reduction differs for 0x{value:X2}"
                  Expect.equal (sim.Peek "all") all $"AND reduction differs for 0x{value:X2}"
                  Expect.equal (sim.Peek "odd") odd $"XOR reduction differs for 0x{value:X2}"

          testCase "constant division agrees for every byte" <| fun _ ->
              let sim = Sim constantDivision.def

              for value in 0UL..255UL do
                  sim.Poke("value", value)
                  sim.Poke("signed_value", value)
                  sim.Tick()

                  let signed = int64 (sbyte value)
                  Expect.equal (sim.Peek "tenths") (value / 10UL) $"Quotient differs for {value} / 10"
                  Expect.equal (sim.Peek "units") (value % 10UL) $"Remainder differs for {value} / 10"
                  Expect.equal (sim.Peek "eighths") (value / 8UL) $"Quotient differs for {value} / 8"
                  Expect.equal (sim.Peek "thirds") (uint64 (signed / 3L) &&& 0x1FFUL) $"Signed quotient differs for {signed} / 3"

          testCase "narrowing operations preserve signedness" <| fun _ ->
              let signed = signalS "s" 16
              let unsigned = signal "u" 16

              Expect.isTrue (isSigned (saturate 8 signed)) "Signed saturation should stay signed"
              Expect.isFalse (isSigned (saturate 8 unsigned)) "Unsigned saturation should stay unsigned"
              Expect.isTrue (isSigned (shr 3 signed)) "Signed narrowing shift should stay signed"
              Expect.isFalse (isSigned (shr 3 unsigned)) "Unsigned narrowing shift should stay unsigned"
              Expect.isTrue (isSigned (pad 24 signed)) "Signed padding should stay signed"
              Expect.isFalse (isSigned (pad 24 unsigned)) "Unsigned padding should stay unsigned"

          testCase "literals borrow the other operand's signedness" <| fun _ ->
              let signed = signalS "d" 8
              let unsigned = signal "u" 8

              Expect.isTrue (isSigned (sub (lit 0UL 8) signed)) "Subtraction should borrow signedness"
              Expect.isTrue (isSigned (add (lit 1UL 8) signed)) "Addition should borrow signedness"
              Expect.isTrue (isSigned (mux (signal "c" 1) (lit 0UL 8) signed)) "Mux should borrow signedness"
              Expect.isFalse (isSigned (sub (lit 0UL 8) unsigned)) "Unsigned subtraction should remain unsigned"
              Expect.isFalse (isSigned (mux (signal "c" 1) (lit 0UL 8) unsigned)) "Unsigned mux should remain unsigned"

          testCase "memory reads enforce storage timing" <| fun _ ->
              expectThrowsContaining "block RAM cannot read combinationally" (fun () ->
                  (defModule "AsyncOnBlock" (fun p -> (p.inPort "addr" 3, p.outPort "out" 8)) (fun (addr, out) ->
                      let memory = blockMem "blocked" 3 8
                      memRead memory addr ==> out))
                      .def
                  |> emitDesign
                  |> ignore)

              let distributed =
                  defModule "AsyncOnDistributed" (fun p -> (p.inPort "addr" 3, p.outPort "out" 8)) (fun (addr, out) ->
                      let memory = distributedMem "lutram" 3 8
                      memRead memory addr ==> out)

              Expect.stringContains
                  (emitDesign distributed.def)
                  "(* ram_style = \"distributed\" *) reg [7:0] lutram"
                  "Distributed memory should carry its synthesis attribute"

              let synchronous =
                  defModule "SyncOnBlock" (fun p -> (p.inPort "addr" 3, p.outPort "out" 8)) (fun (addr, out) ->
                      let memory = blockMem "blocked" 3 8
                      (memReadPort memory addr).data ==> out)

              Expect.stringContains (emitDesign synchronous.def) "(* ram_style = \"block\" *)" "A synchronous block read should elaborate"

          testList
              "emission and elaboration guards"
              [ testCase "module names cannot collide" <| fun _ ->
                    expectThrowsContaining "Mul8" (fun () -> emitDesign nameCollision.def |> ignore)

                testCase "assignment widths must agree" <| fun _ ->
                    expectThrowsContaining "declared 8 bits, driven 16" (fun () -> emitDesign widthViolation.def |> ignore)

                testCase "declarations cannot collide" <| fun _ ->
                    expectThrowsContaining "declared twice" (fun () -> declCollision () |> ignore)

                testCase "streams require a consumer" <| fun _ ->
                    expectThrowsContaining "consumer" (fun () -> emitDesign danglingStream.def |> ignore)

                testCase "register windows cannot overlap" <| fun _ ->
                    expectThrowsContaining "overlap" (fun () -> onOverlappingWindows () |> ignore)

                testCase "registers cannot sit inside windows" <| fun _ ->
                    expectThrowsContaining "inside" (fun () -> onRegisterInsideWindow () |> ignore)

                testCase "conditionally assigned wires need defaults" <| fun _ ->
                    expectThrowsContaining "default" (fun () -> onBadWire () |> ignore)

                testCase "signals have one driver" <| fun _ ->
                    expectThrowsContaining "assigned twice" (fun () -> doubleAssign () |> ignore) ] ]
