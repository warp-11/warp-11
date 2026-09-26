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

/// Ports of 64 bits and up draw from NextBytes (NextInt64's bound cannot
/// express 2^64) and travel as hex; narrower ports keep the original
/// single-call draw, so an existing design's seeded sequence — and its golden
/// testbench — is unchanged by the wide path existing.
let private randomValueWith (rand: System.Random) w =
    if w < 64 then
        BigInteger(uint64 (rand.NextInt64(0L, int64 (1UL <<< w))))
    else
        let nBytes = (w + 7) / 8
        let bytes = Array.zeroCreate<byte> (nBytes + 1) // trailing 0 keeps it non-negative
        rand.NextBytes(System.Span(bytes, 0, nBytes))
        BigInteger(bytes) &&& maskB w

let private diffTbSingle (design: ModuleDef) (cycles: int) =
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

    let randomValue w = randomValueWith rand w

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

/// Deterministic periods for a multi-domain testbench: the default clock at 10
/// time units, foreign domains at 6, 14, 22, … in declaration order — even, so
/// both phases are whole units, and none the default's own period, so the
/// phases drift and the interleavings get exercised.
let private foreignPeriodFor k = 6 + 8 * k

/// The multi-domain testbench: every clock driven at its computed edge times,
/// inputs poked once per default cycle at the start of the window (before any
/// edge inside it, which is when the Sim's poke lands), and every output
/// asserted one unit after every edge instant the Sim's scheduler processed —
/// the per-edge comparison, per domain. Coincident edges are safe on both
/// sides: nonblocking assignment gives Verilog exactly the Sim's merged-edge
/// rule, every next-value from the pre-edge state.
let private diffTbTimed (design: ModuleDef) (cycles: int) =
    let rand = System.Random(stableHash design.name)
    let defaultPeriod = 10

    let periods =
        [ for k, fd in List.indexed design.foreignDomains -> fd, foreignPeriodFor k ]

    let sim =
        Sim(design, domainPeriods = [ for fd, p in periods -> fd.domainName, p ])

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

    sim.CaptureWaves [ for n, _ in outputs -> n ]

    let pokesAt =
        [ for cycle in 0 .. cycles - 1 ->
              let pokes = [ for n, w in inputs -> n, w, randomValueWith rand w ]

              for n, w, v in pokes do
                  if w < 64 then sim.Poke(n, uint64 v) else sim.PokeWide(n, v)

              sim.Tick()
              int64 (cycle * defaultPeriod) + 1L, pokes ]

    // The scheduler's own instants, outputs settled — the trace the testbench
    // asserts. The t=0 snapshot is CaptureWaves' initial sample, not an edge.
    let samples = sim.Waves.samples |> List.filter (fun (t, _) -> t > 0L)

    let endTime = int64 (cycles * defaultPeriod)
    let clocksAll = (design.clock, defaultPeriod) :: [ for fd, p in periods -> fd.clock, p ]

    // (time, kind, lines): kind 0 asserts, 1 pokes, 2 clock toggles — at one
    // instant the asserts read the pre-poke state, exactly as the Sim's edge
    // sample precedes the next cycle's pokes; rising edges sit on even times
    // and everything else on odd, so no assert ever shares an instant with an
    // edge that could move what it reads.
    let events = ResizeArray<int64 * int * string list>()

    for spec, p in clocksAll do
        let mutable t = int64 p

        while t <= endTime do
            events.Add(t, 2, [ $"{spec.clockPort} = 1;" ])

            if t + int64 (p / 2) <= endTime then
                events.Add(t + int64 (p / 2), 2, [ $"{spec.clockPort} = 0;" ])

            t <- t + int64 p

    for t, pokes in pokesAt do
        events.Add(
            t,
            1,
            [ for n, w, v in pokes ->
                  if w < 64 then $"{n} = {v};" else $"{n} = %d{w}'h{hexDigits v};" ]
        )

    for t, values in samples do
        events.Add(
            t + 1L,
            0,
            [ for i, (n, w) in List.indexed outputs ->
                  let v = values[i]

                  if w < 64 then
                      $"if ({n} !== %d{w}'d{v}) $fatal(1, \"t %d{t} {n}: expected {v} got %%0d\", {n});"
                  else
                      $"if ({n} !== %d{w}'h{hexDigits v}) $fatal(1, \"t %d{t} {n}: expected %d{w}'h{hexDigits v} got %%h\", {n});" ]
        )

    let ordered = events |> Seq.sortBy (fun (t, kind, _) -> t, kind) |> List.ofSeq

    let rstAssertOf (spec: ClockSpec) = if spec.resetActiveLow then "0" else "1"
    let rstDeassertOf (spec: ClockSpec) = if spec.resetActiveLow then "1" else "0"
    let tbName = design.name + "_diff_tb"

    let declLines =
        [ for spec, _ in clocksAll do
              yield $"    reg {spec.clockPort} = 0, {spec.resetPort} = {rstAssertOf spec};"
          for n, w in inputs -> $"    reg {range w}{n} = 0;"
          for n, w in outputs -> $"    wire {range w}{n};" ]

    let conns =
        [ for spec, _ in clocksAll do
              yield $".{spec.clockPort}({spec.clockPort})"
              yield $".{spec.resetPort}({spec.resetPort})"
          for n, _ in inputs @ outputs -> $".{n}({n})" ]

    let body =
        [ // One edge of every clock under reset, so each domain's registers
          // take their inits before the timeline starts — then everything
          // released, all clocks low, time zero.
          for spec, _ in clocksAll do
              yield $"        #1 {spec.clockPort} = 1;"
              yield $"        #1 {spec.clockPort} = 0;"
          for spec, _ in clocksAll do
              yield $"        {spec.resetPort} = {rstDeassertOf spec};"
          let mutable prev = 0L

          for t, _, lines in ordered do
              if t > prev then
                  yield $"        #%d{t - prev};"
                  prev <- t

              yield! [ for line in lines -> "        " + line ] ]

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

/// A self-checking Verilog testbench for a design: seeded random stimulus, and
/// an assertion that the emitted Verilog produces exactly the trace the Sim
/// did — per cycle on one clock, per edge instant when the design declares
/// more than one domain.
///
/// This is the differential oracle's generator. What it verifies is the
/// *toolchain* — a divergence is a simulator bug or an emitter bug, and the
/// design is the test input.
let diffTb (design: ModuleDef) (cycles: int) =
    if List.isEmpty design.foreignDomains then
        diffTbSingle design cycles
    else
        diffTbTimed design cycles

/// How many cycles of seeded stimulus a testbench asserts unless a design says
/// otherwise. Enough for a beat-a-cycle design to move real data through every
/// construct it uses, which is what the oracle checks — the toolchain, not the
/// design.
let diffCycles = 50

/// `writeDiffWith designs outDir` writes modules.v plus one self-checking
/// testbench per design, each `cycles` long; run_differential.sh drives
/// Verilator over the result.
///
/// The length is per design because a design whose one unit of work is a long
/// pass — an engine folded onto one multiplier spends hundreds of cycles a
/// sample — would otherwise be asserted over a window that never reaches the
/// half of it the emitter has not been checked on. The default is
/// `diffCycles`; `writeDiff` is this with every design at the default.
/// The design with its own assertions removed, hierarchy-deep. The oracle
/// verifies the *toolchain*, and a design's claim about itself is not part of
/// that: the Sim side never checks them here (`checkAsserts` is off), while
/// Verilator turns a translate_off `$fatal` into an abort — so the priority
/// toys, whose whole point is two write sites firing together, killed the
/// run the first time random stimulus made them coincide. Stripping keeps
/// the three legs symmetric; the claims themselves are the living checks'
/// and the debugger's to enforce, where a design runs as itself.
let private stripAsserts (d: ModuleDef) =
    let rec strip (m: ModuleDef) =
        { m with
            stmts =
                m.stmts
                |> List.filter (function
                    | Assert _ -> false
                    | _ -> true)
            instances = [ for i in m.instances -> { i with child = strip i.child } ] }

    strip d

let writeDiffWith (designs: (ModuleDef * int) list) (outDir: string) =
    let designs = [ for d, cycles in designs -> stripAsserts d, cycles ]
    let designs, cycles = List.unzip designs |> fun (ds, cs) -> ds, Map.ofList (List.zip (List.map (fun (d: ModuleDef) -> d.name) ds) cs)
    System.IO.Directory.CreateDirectory outDir |> ignore

    for d in designs do
        emitDesign d |> ignore // the elaboration checks gate the oracle too

    let moduleText =
        designs
        |> List.collect allModules
        |> List.distinctBy (fun c -> c.name)
        |> List.map (emitVerilogFor Xilinx)
        |> String.concat "\n\n"

    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "modules.v"), moduleText + "\n")

    for d in designs do
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"{d.name}_diff_tb.v"), diffTb d cycles[d.name] + "\n")

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

/// Every design at `diffCycles`.
let writeDiff (designs: ModuleDef list) (outDir: string) =
    writeDiffWith [ for d in designs -> d, diffCycles ] outDir
