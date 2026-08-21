[<AutoOpen>]
module Warp11.Diff

open System.Numerics

// ---------------------------------------------------------------------------
// The differential oracle: seeded random stimulus through the Sim, then a
// generated self-checking Verilog testbench asserting the identical trace under
// Verilator. Any divergence between the two implementations fails the run.

let private stableHash (s: string) =
    s |> Seq.fold (fun acc c -> acc * 31 + int c) 17

/// Hex digits of a masked (non-negative) value, no leading zeros — Verilog
/// sized-hex-literal payload.
let private hexDigits (v: BigInteger) =
    let s = v.ToString("x").TrimStart('0')
    if s = "" then "0" else s

/// A self-checking Verilog testbench for a design: seeded random stimulus, and
/// an assertion per cycle that the emitted Verilog produces exactly the trace
/// the Sim did.
///
/// This is the differential oracle's generator. What it verifies is the
/// *toolchain* — a divergence is a simulator bug or an emitter bug, and the
/// design is the test input.
let diffTb (design: ModuleDef) (cycles: int) =
    let rand = System.Random(stableHash design.name)
    let sim = Sim(design)
    let clocked = needsClk design

    let inputs =
        [ for d in design.decls do
              match d with
              | Input (n, t) -> yield n, t.Width
              | _ -> () ]

    let outputs =
        [ for d in design.decls do
              match d with
              | Output (n, t) -> yield n, t.Width
              | _ -> () ]

    // Ports of 64 bits and up draw from NextBytes (NextInt64's bound cannot
    // express 2^64) and travel as hex; narrower ports keep the original
    // single-call draw, so an existing design's seeded sequence — and its
    // golden testbench — is unchanged by the wide path existing.
    let randomValue w =
        if w < 64 then
            BigInteger(uint64 (rand.NextInt64(0L, int64 (1UL <<< w))))
        else
            let nBytes = (w + 7) / 8
            let bytes = Array.zeroCreate<byte> (nBytes + 1) // trailing 0 keeps it non-negative
            rand.NextBytes(System.Span(bytes, 0, nBytes))
            BigInteger(bytes) &&& maskB w

    let trace =
        [ for _ in 1..cycles ->
              let pokes = [ for n, w in inputs -> n, w, randomValue w ]

              for n, w, v in pokes do
                  if w < 64 then sim.Poke(n, uint64 v) else sim.PokeWide(n, v)

              if clocked then sim.Tick()

              pokes,
              [ for n, w in outputs -> n, w, (if w > 64 then sim.PeekWide n else BigInteger(sim.Peek n)) ] ]

    let tbName = design.name + "_diff_tb"
    let clk = design.clock.clockPort
    let rstPort = design.clock.resetPort
    let rstAssert = if design.clock.resetActiveLow then "0" else "1"
    let rstDeassert = if design.clock.resetActiveLow then "1" else "0"

    let declLines =
        [ if clocked then yield $"    reg {clk} = 0, {rstPort} = {rstAssert};"
          for n, w in inputs -> $"    reg {range w}{n} = 0;"
          for n, w in outputs -> $"    wire {range w}{n};" ]

    let conns =
        [ if clocked then
              yield $".{clk}({clk})"
              yield $".{rstPort}({rstPort})"
          for n, _ in inputs @ outputs -> $".{n}({n})" ]

    let step =
        if clocked then
            [ $"        #1 {clk} = 1;"; $"        #1 {clk} = 0;" ]
        else
            [ "        #1;" ]

    let body =
        [ if clocked then
              yield! step
              yield $"        {rstPort} = {rstDeassert};"
          for cycle, (pokes, peeks) in List.indexed trace do
              for n, w, v in pokes do
                  if w < 64 then
                      yield $"        {n} = {v};"
                  else
                      yield $"        {n} = %d{w}'h{hexDigits v};"

              yield! step

              for n, w, v in peeks do
                  if w < 64 then
                      yield
                          $"        if ({n} !== %d{w}'d{v}) $fatal(1, \"cycle %d{cycle} {n}: expected {v} got %%0d\", {n});"
                  else
                      yield
                          $"        if ({n} !== %d{w}'h{hexDigits v}) $fatal(1, \"cycle %d{cycle} {n}: expected %d{w}'h{hexDigits v} got %%h\", {n});" ]

    String.concat
        "\n"
        [ yield $"module {tbName};"
          yield! declLines
          let connList = String.concat ", " conns
          yield $"    {design.name} dut ({connList});"
          yield "    initial begin"
          yield! body
          yield $"        $display(\"DIFF PASS {design.name}\");"
          yield "        $finish;"
          yield "    end"
          yield "endmodule" ]

/// `writeDiff designs outDir` writes modules.v plus one self-checking testbench
/// per design; run_differential.sh drives Verilator over the result.
let writeDiff (designs: ModuleDef list) (outDir: string) =
    System.IO.Directory.CreateDirectory outDir |> ignore

    for d in designs do
        emitDesign d |> ignore // the elaboration checks gate the oracle too

    let moduleText =
        designs
        |> List.collect allModules
        |> List.distinctBy (fun c -> c.name)
        |> List.map emitVerilog
        |> String.concat "\n\n"

    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "modules.v"), moduleText + "\n")

    for d in designs do
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"{d.name}_diff_tb.v"), diffTb d 50 + "\n")

    // The third leg's input. A design firtool can compile gets a `.fir` beside
    // its testbench, and the runner verilates *that* Verilog against the same
    // trace — two strangers agreeing is a much stronger claim than one tool
    // agreeing with itself. What cannot be said in FIRRTL (a preloaded ROM) is
    // named here rather than skipped quietly.
    let unrepresentable =
        [ for d in designs do
            try
                let text = Firrtl.emitFirrtl d
                System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"{d.name}.fir"), text + "\n")
            with Firrtl.Unrepresentable why ->
                yield d.name, why ]

    printfn $"wrote modules.v + %d{List.length designs} testbenches to {outDir}"
    designs |> List.iter (fun d -> printfn $"  {d.name}")

    if not (List.isEmpty unrepresentable) then
        printfn $"  no .fir for %d{List.length unrepresentable} design(s):"

        for name, why in unrepresentable do
            printfn $"    {name}: {why}"
