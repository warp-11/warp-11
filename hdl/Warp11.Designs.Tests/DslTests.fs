module Warp11.Designs.Tests.DslTests

open Expecto
open Warp11
open Warp11.Designs

let private expectThrowsContaining fragment action =
    Expect.throwsC action (fun ex ->
        Expect.stringContains ex.Message fragment "The rejection should identify its cause")

let private expectElaborates design message =
    try
        emitDesign design |> ignore
    with ex ->
        failtestf "%s: %s" message ex.Message

let private gateChild =
    defModule "GateChild" (fun ports -> ports.inPort "x" 8, ports.outPort "y" 8) (fun (input, output) -> input ==> output)

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
                    expectThrowsContaining "assigned twice" (fun () -> doubleAssign () |> ignore)

                testCase "combinational loops are rejected but registered feedback is accepted" <| fun _ ->
                    expectThrowsContaining "combinational loop" (fun () ->
                        defModule "Loop" (fun ports -> ports.outPort "out" 1) (fun output ->
                            let oscillator = wireBit "osc"
                            bnot oscillator ==> oscillator
                            oscillator ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                    let feedback =
                        defModule "Feedback" (fun ports -> ports.outPort "out" 8) (fun output ->
                            let state = reg "state" 8
                            let middle = wire "middle" 8
                            state + lit 1UL 8 ==> state
                            state ==> middle
                            middle ==> output)

                    expectElaborates feedback.def "Registered feedback should elaborate"

                testCase "outputs require a driver but conditional overrides are accepted" <| fun _ ->
                    expectThrowsContaining "never driven" (fun () ->
                        defModule
                            "Undriven"
                            (fun ports ->
                                let input = ports.inPort "a" 8
                                ports.outPort "dangling" 8 |> ignore
                                input, ports.outPort "b" 8)
                            (fun (input, output) -> input ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                    let overrideDesign =
                        defModule "Override" (fun ports -> ports.inPort "condition" 1, ports.inPort "a" 8, ports.outPort "b" 8) (fun (condition, input, output) ->
                            lit 0UL 8 ==> output
                            If condition (fun () -> input ==> output))

                    expectElaborates overrideDesign.def "A default plus conditional override should elaborate"

                testCase "ROM writes are rejected but RAM writes are accepted" <| fun _ ->
                    expectThrowsContaining "is a rom" (fun () ->
                        defModule "RomWrite" (fun ports -> ports.inPort "addr" 2, ports.outPort "out" 8) (fun (address, output) ->
                            let memory = blockRom "memory" 8 [| 1UL; 2UL; 3UL; 4UL |]
                            memWrite memory address (lit 9UL 8) (lit 1UL 1)
                            (memReadPort memory address).data ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                    let ramWrite =
                        defModule "RamWrite" (fun ports -> ports.inPort "addr" 2, ports.outPort "out" 8) (fun (address, output) ->
                            let memory = blockMem "memory" 2 8
                            memWrite memory address (lit 9UL 8) (lit 1UL 1)
                            (memReadPort memory address).data ==> output)

                    expectElaborates ramWrite.def "Writable RAM should elaborate"

                testCase "UltraRAM cannot be read combinationally" <| fun _ ->
                    expectThrowsContaining "is an ultraMem" (fun () ->
                        defModule "AsyncUltra" (fun ports -> ports.inPort "addr" 2, ports.outPort "out" 8) (fun (address, output) ->
                            let memory = ultraMem "memory" 2 8
                            memRead memory address ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                testCase "port direction rejects own-input and child-output drives" <| fun _ ->
                    expectThrowsContaining "is an input of" (fun () ->
                        defModule "OwnInput" (fun ports -> ports.inPort "a" 8, ports.outPort "b" 8) (fun (input, output) ->
                            lit 0UL 8 ==> input
                            input ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                    expectThrowsContaining "read it, don't drive it" (fun () ->
                        defModule "ChildOutput" (fun ports -> ports.inPort "a" 8, ports.outPort "out" 8) (fun (input, output) ->
                            let childInput, childOutput = gateChild.NewNamed "child"
                            input ==> childInput
                            lit 0UL 8 ==> childOutput
                            childOutput ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                testCase "child inputs must be wired and fully wired children elaborate" <| fun _ ->
                    expectThrowsContaining "child inputs were never driven" (fun () ->
                        defModule "UnwiredChild" (fun ports -> ports.outPort "out" 8) (fun output ->
                            let _childInput, childOutput = gateChild.NewNamed "child"
                            childOutput ==> output)
                        |> fun design -> emitDesign design.def
                        |> ignore)

                    let wired =
                        defModule "WiredChild" (fun ports -> ports.inPort "a" 8, ports.outPort "out" 8) (fun (input, output) ->
                            let childInput, childOutput = gateChild.NewNamed "child"
                            input ==> childInput
                            childOutput ==> output)

                    expectElaborates wired.def "A fully wired child should elaborate"

                testCase "stream input ready may be driven by the module body" <| fun _ ->
                    let streamWire =
                        defModule
                            "StreamWire"
                            (fun ports -> streamInputPorts ports "in" (layout1 ("value", 8)), streamOutputPorts ports "out" (layout1 ("value", 8)))
                            (fun (input, output) -> streamSource input |> streamSink output)

                    expectElaborates streamWire.def "Driving a stream input's ready net should elaborate"

                testCase "ports can only be declared in the IO factory" <| fun _ ->
                    expectThrowsContaining "ports are declared in the io factory" (fun () ->
                        defModule
                            "BodyPort"
                            (fun ports -> ports, ports.outPort "out" 8)
                            (fun (stashed, output) -> stashed.inPort "a" 8 ==> output)
                        |> ignore)

                testCase "reserved words cannot be signal names" <| fun _ ->
                    expectThrowsContaining "reserved" (fun () -> moduleDef "KeywordCheck" (fun _ -> wireBit "matches" |> ignore) |> ignore)

                testCase "signed multiplication requires signal operands with matching readings" <| fun _ ->
                    expectThrowsContaining "signal" (fun () -> mul (litS 3UL 8) (litS 5UL 8) |> ignore)
                    expectThrowsContaining "signed" (fun () -> mul (signalS "x" 8) (signal "y" 8) |> ignore)

                testCase "binary and mux operands require matching signedness" <| fun _ ->
                    expectThrowsContaining "signed" (fun () -> lt (signalS "x" 8) (signal "y" 8) |> ignore)
                    expectThrowsContaining "signed" (fun () -> add (signalS "x" 8) (signal "y" 8) |> ignore)
                    expectThrowsContaining "signed" (fun () -> mux (signal "c" 1) (signalS "x" 8) (signal "y" 8) |> ignore)

                testCase "shift, extension, and comparison widths are validated" <| fun _ ->
                    expectThrowsContaining "shift must be" (fun () -> sra 8 (signal "x" 8) |> ignore)
                    expectThrowsContaining "narrows" (fun () -> signExtend 4 (signal "x" 8) |> ignore)
                    expectThrowsContaining "width" (fun () -> lt (signalS "x" 8) (signalS "y" 16) |> ignore)

                testCase "Number boundaries and renormalization are validated" <| fun _ ->
                    expectThrowsContaining "fraction" (fun () -> Number.renormTo Number.q9_7 (Number.ofBits Number.q4_4 (signal "x" 8)) |> ignore)
                    expectThrowsContaining "32-bit format" (fun () -> Number.ofBits Number.q4_28 (signal "x" 8) |> ignore)
                    expectThrowsContaining "does not fit" (fun () -> Number.constant Number.q4_4 9.0 |> ignore) ] ]
