module Warp11.Designs.Tests.EmitterTests

open System.Text.RegularExpressions
open Expecto
open Warp11
open Warp11.Designs

let private laneMasked (design: ModuleDef) =
    design.stmts
    |> List.exists (function
        | MemWrite (_, _, _, _, Some _) -> true
        | _ -> false)

let private exportableDesigns () =
    Registry.designs
    |> List.map (fun entry -> entry.build ())
    |> List.filter (fun design ->
        not (laneMasked design)
        && allModules design
           |> List.forall (fun child ->
               child.decls
               |> List.forall (function
                   | Memory (_, _, _, Some _, _) -> false
                   | _ -> true)))

let private withoutRamStyle verilog =
    Regex.Replace(verilog, """\(\* ram_style = "[a-z]*" \*\) """, "")

let private firrtlHeader =
    """FIRRTL version 4.0.0
circuit T :
  public module T :
    input clock : Clock
    input reset : UInt<1>
    input a : UInt<8>
    input b : UInt<8>
    output o : UInt<8>
"""

let private expectUnsupported body expected =
    Expect.throwsC
        (fun () -> FirrtlImport.importFirrtl (firrtlHeader + body) |> ignore)
        (fun ex ->
            Expect.isTrue
                (ex :? FirrtlImport.Unsupported)
                $"The FIRRTL reader should reject {expected} as unsupported"
            Expect.stringContains ex.Message expected $"The rejection should identify {expected}")

let private bare name =
    { name = name
      decls = []
      stmts = []
      instances = []
      domain = defaultDomain
      foreignDomains = []
      declDomains = []
      crossings = []
      portDomains = []
      streamReadies = []
      probes = []
      stateMachines = [] }

let private collisionParent instanceName =
    let grandchild =
        { bare "CollideGrandChild" with
            decls = [ Input ("i", UInt 8); Output ("o", UInt 8); Wire ("sig", UInt 8) ]
            stmts =
                [ Assign ("sig", Add (Ref ("i", UInt 8), Lit (1UL, UInt 8)))
                  Assign ("o", Ref ("sig", UInt 8)) ] }

    { bare "CollideParent" with
        decls =
            [ Input ("i", UInt 8)
              Output ("o", UInt 8)
              Wire ("gc_sig", UInt 8)
              Wire ($"{instanceName}_i", UInt 8)
              Wire ($"{instanceName}_o", UInt 8) ]
        stmts =
            [ Assign ("gc_sig", Add (Ref ("i", UInt 8), Lit (100UL, UInt 8)))
              Assign ($"{instanceName}_i", Ref ("i", UInt 8))
              Assign ("o", Add (Ref ($"{instanceName}_o", UInt 8), Ref ("gc_sig", UInt 8))) ]
        instances =
            [ { instName = instanceName
                child = grandchild
                domain = defaultDomain.domainName } ] }

let private structuralLines (verilog: string) =
    verilog.Split '\n'
    |> Array.filter (fun line -> not (line.StartsWith "module " || line.Contains "assign escape"))
    |> Array.toList

let private fixedLayerTest =
    testCase "Fixed arithmetic layer emits the raw design structure" <| fun _ ->
        Expect.equal
            (emitDesign escapeStepFixed.def |> structuralLines)
            (emitDesign escapeStep.def |> structuralLines)
            "The Fixed layer should compile away except for its signed escape comparison"

let private multiplierMemoizationTest =
    testCase "mulOf memoizes multipliers by width" <| fun _ ->
        Expect.isTrue (System.Object.ReferenceEquals(mulOf 8, mulOf 8)) "Repeated requests should return the same multiplier module"

let private flattenCollisionTest =
    testCase "flatten rejects colliding hierarchical names" <| fun _ ->
        Expect.throwsC
            (fun () -> flatten (collisionParent "gc") |> ignore)
            (fun ex ->
                Expect.stringContains ex.Message "gc_sig" "The collision should identify the flattened name"
                Expect.stringContains ex.Message "collides" "The collision should be explicit")

        let sim = Sim (collisionParent "child")
        sim.Poke("i", 5UL)
        sim.Tick()
        Expect.equal (sim.Peek "o") 111UL "Renaming the instance should preserve both independent signals"

let private supportedFirrtlTest =
    testCase "FIRRTL reader accepts its supported formerly-high constructs" <| fun _ ->
        let unresetRegister =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input clock : Clock
    input reset : UInt<1>
    input a : UInt<8>
    output o : UInt<8>
    reg r : UInt<8>, clock
    connect r, a
    connect o, r
"""

        let registerDesign = FirrtlImport.importFirrtl unresetRegister
        Expect.contains registerDesign.decls (Reg ("r", UInt 8, None)) "An unreset register should retain no reset value"

        let dynamicShift =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input a : UInt<8>
    input n : UInt<3>
    output o : UInt<15>
    connect o, dshl(a, n)
"""

        let reduction =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input a : UInt<8>
    output o : UInt<1>
    connect o, orr(a)
"""

        let variableDivision =
            """FIRRTL version 4.0.0
circuit T :
  public module T :
    input a : UInt<8>
    input b : UInt<8>
    output o : UInt<8>
    connect o, div(a, b)
"""

        [ dynamicShift; reduction; variableDivision ]
        |> List.iter (fun text -> FirrtlImport.importFirrtl text |> emitDesign |> ignore)

// --- Clock domains (notes/CLOCK_DOMAINS.md increment 2) -------------------

let private audio = clockDomain "audio"

let private twoDomainToy () =
    defModule
        "TwoDomainToy"
        (fun p -> p.inPort "step" 8, p.outPort "count" 8, p.outPort "acount" 8)
        (fun (step, count, acount) ->
            let c = declareReg "c" (UInt 8) 0UL
            c + lit 1UL 8 ==> c
            c ==> count

            withDomain audio (fun () ->
                let a = declareReg "a" (UInt 8) 0UL
                a + step ==> a
                a ==> acount))

let private withDomainTest =
    testCase "withDomain emits a hand-written two-clock module" <| fun _ ->
        let expected =
            String.concat
                "\n"
                [ "module TwoDomainToy (input clk, input rst, input audio_clk, input audio_rst, input [7:0] step, output [7:0] count, output [7:0] acount);"
                  "    reg [7:0] c;"
                  "    reg [7:0] a;"
                  "    assign count = c;"
                  "    assign acount = a;"
                  "    always @(posedge clk) begin"
                  "        if (rst) begin"
                  "            c <= 8'd0;"
                  "        end else begin"
                  "            c <= (c + 8'd1);"
                  "        end"
                  "    end"
                  "    always @(posedge audio_clk) begin"
                  "        if (audio_rst) begin"
                  "            a <= 8'd0;"
                  "        end else begin"
                  "            a <= (a + step);"
                  "        end"
                  "    end"
                  "endmodule" ]

        Expect.equal (emitDesign (twoDomainToy ()).def) expected "The audio register should live in its own always block"

let private pinnedModuleTest =
    testCase "a module declared in a domain keeps its clock wherever instantiated" <| fun _ ->
        let audioTick =
            defModuleIn audio "AudioTick" (fun p -> p.outPort "tick" 8) (fun tick ->
                let t = declareReg "t" (UInt 8) 0UL
                t + lit 1UL 8 ==> t
                t ==> tick)

        let parent =
            defModule "PinnedParent" (fun p -> p.outPort "o" 8) (fun o ->
                let tick = instanceNamed "atick" audioTick
                tick ==> o)

        let verilog = emitDesign parent.def
        Expect.stringContains verilog "module AudioTick (input audio_clk, input audio_rst," "The pinned module's own pair should be its domain's spelling"

        Expect.stringContains
            verilog
            "AudioTick atick (.audio_clk(audio_clk), .audio_rst(audio_rst)"
            "The parent should wire the pinned instance from its conjured audio pair"

let private ambientInstanceTest =
    testCase "the ambient domain clocks an instance created inside withDomain" <| fun _ ->
        let plainTick =
            defModule "PlainTick" (fun p -> p.outPort "tick" 8) (fun tick ->
                let t = declareReg "t" (UInt 8) 0UL
                t + lit 1UL 8 ==> t
                t ==> tick)

        let parent =
            defModule "AmbientParent" (fun p -> p.outPort "o" 8) (fun o ->
                withDomain audio (fun () ->
                    let tick = instanceNamed "atick" plainTick
                    tick ==> o))

        Expect.stringContains
            (emitDesign parent.def)
            "PlainTick atick (.clk(audio_clk), .rst(audio_rst)"
            "A default-domain child created inside the block should be clocked by it — same as inline logic"

let private domainRefusalsTest =
    testCase "the Sim, the FIRRTL export and the default name refuse domains by name" <| fun _ ->
        Expect.throwsC
            (fun () -> Sim((twoDomainToy ()).def) |> ignore)
            (fun ex -> Expect.stringContains ex.Message "one clock today" "The Sim should refuse a two-domain design until increment 4")

        Expect.throwsC
            (fun () -> Firrtl.emitFirrtl (twoDomainToy ()).def |> ignore)
            (fun ex -> Expect.stringContains ex.Message "audio" "The FIRRTL export should name the domain it cannot express yet")

        Expect.throwsC
            (fun () -> clockDomain "default" |> ignore)
            (fun ex -> Expect.stringContains ex.Message "already in" "The default domain is not declarable")

let private crossingRefusedTest =
    testCase "an unsynchronised crossing is an elaboration error naming the register and the signal" <| fun _ ->
        Expect.throwsC
            (fun () ->
                defModule "BadCross" (fun p -> p.outPort "o" 1) (fun o ->
                    let flag = declareReg "flag" (UInt 1) 0UL
                    Not flag ==> flag

                    withDomain audio (fun () ->
                        let t = declareReg "t" (UInt 1) 0UL
                        t ^^^ flag ==> t
                        t ==> o))
                |> ignore)
            (fun ex ->
                Expect.stringContains ex.Message "register 't'" "The error should name the sampling register"
                Expect.stringContains ex.Message "'flag'" "The error should name the crossing signal"
                Expect.stringContains ex.Message "synchronize" "The error should point at the CDC entry")

let private synchronizeTest =
    testCase "synchronize is the accepted crossing, and emits the two flops in the target domain" <| fun _ ->
        let good =
            defModule "GoodCross" (fun p -> p.outPort "o" 1) (fun o ->
                let flag = declareReg "flag" (UInt 1) 0UL
                Not flag ==> flag

                withDomain audio (fun () ->
                    let t = declareReg "t" (UInt 1) 0UL
                    t ^^^ synchronize audio flag ==> t
                    t ==> o))

        let verilog = emitDesign good.def
        Expect.stringContains verilog "sync_meta_1 <= flag;" "The first flop should sample the foreign signal"
        Expect.stringContains verilog "sync_out_1 <= sync_meta_1;" "The second flop should sample the first"

        let audioBlock = verilog.Substring(verilog.IndexOf "always @(posedge audio_clk)")
        Expect.stringContains audioBlock "sync_meta_1 <= flag;" "Both flops should live in the audio always block"

let private crossingFeedTest =
    testCase "feeding a pinned instance's input from another domain is refused at the wiring" <| fun _ ->
        let audioSink =
            defModuleIn audio "AudioSink" (fun p -> p.inPort "d" 1, p.outPort "q" 1) (fun (d, q) ->
                let r = declareReg "r" (UInt 1) 0UL
                d ==> r
                r ==> q)

        Expect.throwsC
            (fun () ->
                defModule "BadFeed" (fun p -> p.outPort "o" 1) (fun o ->
                    let flag = declareReg "flag" (UInt 1) 0UL
                    Not flag ==> flag
                    let d, q = instanceNamed "sink" audioSink
                    flag ==> d
                    q ==> o)
                |> ignore)
            (fun ex ->
                Expect.stringContains ex.Message "instance 'sink' input 'd'" "The error should name the instance and port"
                Expect.stringContains ex.Message "'flag'" "The error should name the wrongly-clocked driver")

let private synchronizeWidthTest =
    testCase "synchronize refuses a multi-bit signal" <| fun _ ->
        Expect.throwsC
            (fun () ->
                defModule "WideCross" (fun p -> p.outPort "o" 8) (fun o ->
                    let c = declareReg "c" (UInt 8) 0UL
                    c + lit 1UL 8 ==> c

                    withDomain audio (fun () ->
                        let t = declareReg "t" (UInt 8) 0UL
                        synchronize audio c ==> t
                        t ==> o))
                |> ignore)
            (fun ex -> Expect.stringContains ex.Message "Gray counter" "The refusal should point at the multi-bit entries")

let tests =
    testList
        "FIRRTL and emitter"
        [ testCase "FIRRTL export is closed over catalog designs" <| fun _ ->
              let designs = exportableDesigns ()
              Expect.isNonEmpty designs "The closure check should cover exportable catalog designs"

              for design in designs do
                  let text = Firrtl.emitFirrtl design
                  let lines = text.Split '\n' |> Array.map (fun line -> line.Trim())
                  let modules = allModules design |> List.distinctBy (fun child -> child.name)

                  let moduleLines =
                      lines
                      |> Array.filter (fun line -> line.StartsWith "module " || line.StartsWith "public module ")

                  Expect.equal moduleLines.Length modules.Length $"{design.name} should emit every module exactly once"
                  Expect.stringContains text $"circuit {design.name} :" $"{design.name} should name its top-level circuit"

                  for child in modules do
                      for declaration in child.decls do
                          match declaration with
                          | Input (name, signalType) ->
                              Expect.stringContains text $"input {name} : {Firrtl.typeText signalType}" $"{child.name}.{name} should retain its input type"
                          | Output (name, signalType) ->
                              Expect.stringContains text $"output {name} : {Firrtl.typeText signalType}" $"{child.name}.{name} should retain its output type"
                          | _ -> ()

                  let declared =
                      set
                          [ for child in modules do
                                for declaration in child.decls do
                                    match declOf declaration with
                                    | Some (name, _) -> yield name
                                    | None -> ()

                                for declaration in child.decls do
                                    match declaration with
                                    | Memory (name, _, _, _, _) -> yield name
                                    | _ -> () ]

                  for line in lines |> Array.filter (fun line -> line.StartsWith "connect ") do
                      let target = line.Substring("connect ".Length).Split(',').[0].Trim()
                      Expect.isTrue (target.Contains "." || declared.Contains target) $"{design.name} connect target {target} should be declared"
              ()

          testCase "FIRRTL export refuses initialized memories" <| fun _ ->
              let preloaded =
                  (defModule "FirrtlRomRefusal" (fun ports -> ports.inPort "addr" 2, ports.outPort "out" 8) (fun (address, output) ->
                      let lookup = distributedRom "lookup" 8 [| 1UL; 2UL; 3UL; 4UL |]
                      memRead lookup address ==> output)).def

              Expect.throwsC
                  (fun () -> Firrtl.emitFirrtl preloaded |> ignore)
                  (fun ex ->
                      Expect.isTrue (ex :? Firrtl.Unrepresentable) "Initialized memory should be unrepresentable in FIRRTL"
                      Expect.stringContains ex.Message "initial contents" "The refusal should identify the lost contents")

          testCase "FIRRTL catalog exports round-trip to identical Verilog" <| fun _ ->
              let designs = exportableDesigns ()
              Expect.isNonEmpty designs "The round-trip should cover exportable catalog designs"

              for design in designs do
                  let original = emitDesign design |> withoutRamStyle
                  let imported = Firrtl.emitFirrtl design |> FirrtlImport.importFirrtl |> emitDesign |> withoutRamStyle
                  Expect.equal imported original $"{design.name} should survive a FIRRTL round-trip"
              ()

          testCase "FIRRTL reader explicitly refuses unsupported constructs" <| fun _ ->
              [ "    when a :\n      connect o, a\n", "when"
                "    connect o, asClock(a)\n", "asClock"
                "    printf(clock, UInt<1>(1), \"hi\")\n    connect o, a\n", "printf"
                "    stop(clock, UInt<1>(1), 0)\n    connect o, a\n", "stop"
                "    attach(a, b)\n    connect o, a\n", "attach"
                "    wire w : { x : UInt<8> }\n    connect o, a\n", "bundle"
                "    frobnicate o, a\n", "unrecognised statement" ]
              |> List.iter (fun (body, expected) -> expectUnsupported body expected)
              ()

          supportedFirrtlTest
          flattenCollisionTest
          fixedLayerTest
          multiplierMemoizationTest
          withDomainTest
          pinnedModuleTest
          ambientInstanceTest
          domainRefusalsTest
          crossingRefusedTest
          synchronizeTest
          crossingFeedTest
          synchronizeWidthTest ]
