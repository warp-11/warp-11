/// The GoL executable: living checks against the twin, the differential
/// writer, and the Sim-performance probe the direction's open qualifier
/// asked for at this design (`perf`).
module Warp11.GoL.Main

open Warp11
open Warp11.GoL.Core
open Warp11.GoL.Twin
open Warp11.GoL.Wrapper

let private diffDesigns () =
    [ golHarness 8 8
      golLiveHarness 8 8
      golHarnessUnrolled 2 8 8
      golAxiScaled
      golAxiScaledX2 ]

/// Drive the harness and the twin through the same generations and demand
/// row-for-row, population-for-population equality every cycle. An unrolled
/// harness advances `gensPerTick` twin steps per Sim tick.
let private runAgainstTwin
    (sim: Sim)
    (gridWidth: int)
    (gridHeight: int)
    (gensPerTick: int)
    (start: uint64[])
    (generations: int)
    =
    for y in 0 .. gridHeight - 1 do
        sim.Poke($"load_row_%d{y}", start[y])

    sim.Poke("load_enable", 1UL)
    sim.Poke("tick_enable", 0UL)
    sim.Tick()
    sim.Poke("load_enable", 0UL)
    sim.Poke("tick_enable", 1UL)

    let readRows () =
        [| for y in 0 .. gridHeight - 1 -> sim.Peek $"row_%d{y}" |]

    let loaded = readRows () = start

    let generationsOk =
        (start, [ 1 .. generations / gensPerTick ])
        ||> List.fold (fun expected _ ->
            sim.Tick()

            let next =
                (expected, [ 1 .. gensPerTick ])
                ||> List.fold (fun g _ -> step gridWidth gridHeight g)

            if readRows () <> next then
                failwith "grid diverged from the twin"

            if sim.Peek "population" <> uint64 (population next) then
                failwith "population diverged from the twin"

            next)
        |> ignore

        true

    loaded && generationsOk

let private demoChecks () =
    for d in [ golHarness 8 8 ] do
        match checkWidths d with
        | [] -> printfn $"{d.name}: widths ok"
        | problems -> problems |> List.iter (printfn "%s")

    // The glider — the canonical Life litmus: it must translate one cell
    // diagonally every 4 generations until the dead border eats it, and the
    // twin must agree the whole way.
    let glider =
        [| 0b010UL; 0b100UL; 0b111UL; 0UL; 0UL; 0UL; 0UL; 0UL |]

    let gliderOk = runAgainstTwin (Sim(golHarness 8 8)) 8 8 1 glider 16
    printfn $"glider vs twin (16 gens):     %b{gliderOk}"

    let gliderX2Ok = runAgainstTwin (Sim(golHarnessUnrolled 2 8 8)) 8 8 2 glider 16
    printfn $"glider vs twin, 2 gens/tick:  %b{gliderX2Ok}"

    // Random soups: dense chaos exercises every birth/survive/die case at
    // every border. Deterministic seed, three soups, 30 generations each.
    let rand = System.Random 11

    let soupsOk =
        [ 1..3 ]
        |> List.forall (fun _ ->
            let soup = [| for _ in 0..7 -> uint64 (rand.Next(0, 256)) |]
            runAgainstTwin (Sim(golHarness 8 8)) 8 8 1 soup 30)

    printfn $"random soups vs twin (3x30):  %b{soupsOk}"

    // A load must win over a same-cycle tick — the wrapper's pacing FSM may
    // be mid-burst when the host reloads.
    let loadWinsOk =
        let sim = Sim(golHarness 8 8)
        let blinker = [| 0UL; 0b111UL; 0UL; 0UL; 0UL; 0UL; 0UL; 0UL |]

        for y in 0..7 do
            sim.Poke($"load_row_%d{y}", blinker[y])

        sim.Poke("load_enable", 1UL)
        sim.Poke("tick_enable", 1UL)
        sim.Tick()
        [| for y in 0..7 -> sim.Peek $"row_%d{y}" |] = blinker

    printfn $"load beats tick:              %b{loadWinsOk}"
    0

/// The named measurement: elaboration, Sim construction and ticks/second at
/// growing grids — GoL is the trigger case for porting the flat-program Sim,
/// so the number gets recorded, not guessed.
let private perfProbe () =
    for gridWidth, gridHeight, ticks in [ 16, 16, 500; 32, 32, 200; 64, 64, 50 ] do
        let clock = System.Diagnostics.Stopwatch.StartNew()
        let d = golHarness gridWidth gridHeight
        let elaborateMs = clock.Elapsed.TotalMilliseconds

        clock.Restart()
        let sim = Sim(d)
        let constructMs = clock.Elapsed.TotalMilliseconds

        let rand = System.Random 7

        for y in 0 .. gridHeight - 1 do
            sim.Poke($"load_row_%d{y}", uint64 (rand.NextInt64()) &&& ((1UL <<< gridWidth) - 1UL))

        sim.Poke("load_enable", 1UL)
        sim.Tick()
        sim.Poke("load_enable", 0UL)
        sim.Poke("tick_enable", 1UL)

        clock.Restart()

        for _ in 1..ticks do
            sim.Tick()

        let tickMs = clock.Elapsed.TotalMilliseconds
        let perSecond = float ticks / (tickMs / 1000.0)

        printfn
            $"%d{gridWidth}x%d{gridHeight}: elaborate %6.0f{elaborateMs} ms   construct %6.0f{constructMs} ms   %d{ticks} ticks in %6.0f{tickMs} ms = %5.0f{perSecond} cyc/s"

    0

/// The tier-1 measurement for the optimization narrative: generations per
/// second of the software rule flat-out at the silicon config, no Sim in the
/// loop — the idiomatic twin and an imperative array port of the same
/// per-cell algorithm, differentially checked before it is timed.
let private benchTwin () =
    let width, height = 64, 64
    let rand = System.Random 11
    let soup = [| for _ in 1..height -> uint64 (rand.NextInt64()) |]

    let stepArrays (rows: uint64[]) =
        let next = Array.zeroCreate height

        for y in 0 .. height - 1 do
            let mutable acc = 0UL

            for x in 0 .. width - 1 do
                let mutable n = 0

                for dy in -1 .. 1 do
                    for dx in -1 .. 1 do
                        let yy, xx = y + dy, x + dx

                        if (dy <> 0 || dx <> 0) && yy >= 0 && yy < height && xx >= 0 && xx < width then
                            n <- n + int ((rows[yy] >>> xx) &&& 1UL)

                if n = 3 || (n = 2 && (rows[y] >>> x) &&& 1UL = 1UL) then
                    acc <- acc ||| (1UL <<< x)

            next[y] <- acc

        next

    // Carry-save adders over the row bitmasks: every column's neighbor count
    // computed bit-parallel, the dead border falling out of the shifts free.
    let stepBitboard (rows: uint64[]) =
        let inline ha a b = struct (a ^^^ b, a &&& b)

        let inline fa a b c =
            let s = a ^^^ b
            struct (s ^^^ c, (a &&& b) ||| (c &&& s))

        [| for y in 0 .. height - 1 ->
               let u = if y > 0 then rows[y - 1] else 0UL
               let s = rows[y]
               let d = if y < height - 1 then rows[y + 1] else 0UL
               let struct (us, uc) = fa (u <<< 1) u (u >>> 1)
               let struct (ds, dc) = fa (d <<< 1) d (d >>> 1)
               let struct (ss, sc) = ha (s <<< 1) (s >>> 1)
               let struct (n0, c1) = fa us ds ss
               let struct (t, c2) = fa uc dc sc
               let struct (n1, c2b) = ha t c1
               let struct (n2, n3) = ha c2 c2b
               // N = 3, or N = 2 and already alive.
               ~~~n2 &&& ~~~n3 &&& n1 &&& (n0 ||| s) |]

    let portsOk =
        (soup, [ 1..32 ])
        ||> List.fold (fun g _ ->
            let expected = step width height g

            if stepArrays g <> expected then
                failwith "array port diverged from the twin"

            if stepBitboard g <> expected then
                failwith "bitboard port diverged from the twin"

            expected)
        |> ignore

        true

    printfn $"ports vs twin (32 gens):      %b{portsOk}"

    let measure name (stepOnce: uint64[] -> uint64[]) =
        let mutable rows = soup

        for _ in 1..20 do
            rows <- stepOnce rows // warm the JIT before the clock starts

        let clock = System.Diagnostics.Stopwatch.StartNew()
        let mutable gens = 0

        while clock.ElapsedMilliseconds < 2000L do
            for _ in 1..64 do
                rows <- stepOnce rows

            gens <- gens + 64

        let perSecond = float gens / clock.Elapsed.TotalSeconds
        printfn $"%s{name}: %7d{gens} gens in %.1f{clock.Elapsed.TotalSeconds} s = %9.0f{perSecond} gens/s   (population %d{population rows})"

    measure "twin (idiomatic F#)   " (step width height)
    measure "arrays (imperative F#)" stepArrays
    measure "bitboard (F#)         " stepBitboard
    0

/// The silicon rehearsal: every host action a real five-channel AXI-Lite
/// transaction, the frame read back from the fake DDR — the same sequence
/// the Rust driver runs on the board. Parameterized so it runs both at the
/// scaled config (fast, the inner loop) and at the full 64×64 silicon config
/// (32 beats per frame — the multi-beat write path the scaled config's two
/// beats cannot exercise; the +16-row DDR rotation hid exactly there).
let private axiRehearsalAt
    design
    (gensPerCycle: int)
    (gridWidth: int)
    (gridHeight: int)
    (awEvery: int)
    (wEvery: int)
    (bDelay: int)
    (jitter: int option)
    =
    let m = golMap gridWidth gridHeight
    let sim = Sim(design)
    let fbBase = 0x80
    let slotStride = 1 <<< golSlotShift gridWidth gridHeight

    let ddr =
        SimAxiWriteSlave(
            sim,
            fbBase + 3 * slotStride,
            dataBytes = 16,
            awEvery = awEvery,
            wEvery = wEvery,
            bDelay = bDelay,
            ?jitter = jitter
        )
    let cycle () = ddr.Cycle()

    // The handshakes live in `SimAxi` — six copies of them used to live
    // in the projects, this one included.
    let axi = SimAxi.clientWith sim (cycle)
    let read32, write32 = axi.read32, axi.write32

    let idOk = read32 m.id.offset = golIdMagic
    printfn $"ID reads back:                %b{idOk}"

    write32 m.fbBaseAddr.offset (uint64 fbBase)

    // The soup goes in through the load window, low word of each row first.
    let rand = System.Random 5

    let soup =
        [| for _ in 1..gridHeight ->
               if gridWidth = 64 then
                   uint64 (rand.NextInt64())
               else
                   uint64 (rand.NextInt64()) &&& ((1UL <<< gridWidth) - 1UL) |]

    for y in 0 .. gridHeight - 1 do
        for k in 0 .. m.wordsPerRow - 1 do
            write32
                (m.loadRow.offset + uint64 ((y * m.wordsPerRow + k) * 4))
                ((soup[y] >>> (32 * k)) &&& 0xFFFFFFFFUL)

    write32 0x000UL 1UL // load pulse

    for _ in 1 .. m.windowWords + 6 do
        cycle () // the prefetch walk

    let status = read32 m.busy.offset
    let loadOk = status = (uint64 (population soup) <<< 1) // busy 0, population packed above it
    printfn $"load via window + prefetch:   %b{loadOk}"

    // A 5-generation burst at 3 cycles per generation, polled to completion.
    write32 m.tickCount.offset 5UL
    write32 m.intervalCycles.offset 3UL
    write32 0x000UL 2UL // tick pulse

    let mutable guard = 0

    while read32 m.busy.offset &&& 1UL = 1UL && guard < 100 do
        guard <- guard + 1

    // Five fires; the grid advances gensPerCycle generations per fire and
    // the generation register counts true generations.
    let after5 =
        (soup, [ 1 .. 5 * gensPerCycle ])
        ||> List.fold (fun g _ -> step gridWidth gridHeight g)

    let burstOk =
        read32 m.generation.offset = uint64 (5 * gensPerCycle)
        && read32 m.busy.offset = (uint64 (population after5) <<< 1)

    printfn $"burst of 5 (interval 3):      %b{burstOk}"

    // Capture, read the slot straight out of DDR, compare against the twin.
    // Conflate grants the latest COMPLETED frame, which can lag the grid by
    // one frame latency — a driver that wants the settled post-burst state
    // idles one frame period after busy clears before capturing (the live
    // UI streams continuously and never cares). The idle scales with the
    // frame period: beats × pacing, plus latch/drain/publish slack.
    let beatCount = gridWidth * gridHeight / 128

    for _ in 1 .. 3 * beatCount * (max awEvery wEvery + bDelay) + 200 do
        cycle ()

    write32 m.snapCapture.offset 1UL
    let mutable snapGuard = 0

    while read32 m.snapReady.offset &&& 1UL = 0UL && snapGuard < 3000 do
        snapGuard <- snapGuard + 1

    if read32 m.snapReady.offset &&& 1UL = 0UL then
        for signal in
            [ "snap_copying"
              "snap_index"
              "conflate_draining"
              "conflate_write_idx"
              "conflate_done_idx"
              "conflate_read_idx"
              "conflate_capture_queued"
              "enq_ptr"
              "aw_ptr"
              "w_ptr"
              "b_ptr"
              "writer_idle"
              "writer_armed" ] do
            printfn $"  {signal} = %d{sim.Peek signal}"

        failwith "snapshot capture never granted — frame period exceeds the poll budget"

    let snapStatus = read32 m.snapReady.offset
    let slot = int (snapStatus >>> 16) &&& 3
    let overrunClear = (snapStatus >>> 8) &&& 0xFFUL = 0UL
    let rowsPerBeat = 128 / gridWidth

    let frameRows =
        [| for y in 0 .. gridHeight - 1 ->
               let beat = y / rowsPerBeat
               let byteBase = fbBase + slot * slotStride + beat * 16 + (y % rowsPerBeat) * (gridWidth / 8)

               (0UL, [ 0 .. gridWidth / 8 - 1 ])
               ||> List.fold (fun acc k -> acc ||| (uint64 ddr.Memory[byteBase + k] <<< (8 * k))) |]

    let frameOk = frameRows = after5 && overrunClear
    printfn $"DDR frame matches the twin:   %b{frameOk}"

    if not frameOk then
        // Which generation (and at which row offset) does the frame hold?
        let twins =
            (soup, [ 1 .. 5 * gensPerCycle ])
            ||> List.scan (fun g _ -> step gridWidth gridHeight g)

        printfn $"  granted slot %d{slot}; per-slot content:"

        for s in 0..2 do
            let rowsAt =
                [| for y in 0 .. gridHeight - 1 ->
                       let beat = y / rowsPerBeat
                       let byteBase = fbBase + s * slotStride + beat * 16 + (y % rowsPerBeat) * (gridWidth / 8)

                       (0UL, [ 0 .. gridWidth / 8 - 1 ])
                       ||> List.fold (fun acc k -> acc ||| (uint64 ddr.Memory[byteBase + k] <<< (8 * k))) |]

            let zero = rowsAt |> Array.filter ((=) 0UL) |> Array.length

            let genHits =
                [ for g, twin in List.indexed twins ->
                      g, (Array.indexed rowsAt |> Array.filter (fun (y, r) -> r = twin[y]) |> Array.length) ]
                |> List.filter (fun (_, hits) -> hits > gridHeight / 4)
                |> List.map (fun (g, hits) -> $"gen%d{g}:%d{hits}")
                |> String.concat " "

            printfn $"    slot %d{s}: %d{zero} zero rows; strong matches: {genHits}"

    // Both interrupt bits should be pending (burst done + snapshot granted);
    // w1c clears them and drops the irq line.
    let irqPending = read32 m.burstIrq.offset = 3UL && sim.Peek "irq" = 1UL
    write32 m.burstIrq.offset 3UL
    let irqCleared = read32 m.burstIrq.offset = 0UL && sim.Peek "irq" = 0UL
    printfn $"w1c irq set then cleared:     %b{irqPending && irqCleared}"

    write32 m.snapRelease.offset 1UL
    cycle ()
    let releaseOk = read32 m.snapReady.offset &&& 1UL = 0UL

    write32 0x000UL 4UL // reset pulse
    cycle ()
    cycle ()
    let resetOk = read32 m.busy.offset = 0UL && read32 m.generation.offset = 0UL
    printfn $"release + reset:              %b{releaseOk && resetOk}"
    0

/// The seam: the silicon config's Verilog plus the generated Rust layout —
/// offsets and bit positions from the same map the slave elaborates.
let private writeHardware (repoRoot: string) =
    let gridWidth, gridHeight = 64, 64
    let buildDir = System.IO.Path.Combine(repoRoot, "hardware", "build")
    let runtimeSrc = System.IO.Path.Combine(repoRoot, "runtime", "core", "src")
    System.IO.Directory.CreateDirectory buildDir |> ignore
    let m = golMap gridWidth gridHeight

    let layout =
        [ "//! Register map for the `GolAxi` AXI-Lite slave (Game of Life 64x64,"
          "//! DDR triple-buffer snapshot at FB_BASE_ADDR's value)."
          "//! Generated by `dotnet run -- hardware <repo-root>` in hdl/Warp11.GoL."
          "//! Do not edit by hand — changes will be overwritten on next emit."
          "" ]
        @ regMapRsLines m.map
        @ [ ""
            $"pub const GENS_PER_CYCLE: usize = %d{golGensPerCycle};"
            $"pub const GRID_WIDTH: usize = %d{gridWidth};"
            $"pub const GRID_HEIGHT: usize = %d{gridHeight};"
            $"pub const ROWS_PER_BEAT: usize = %d{128 / gridWidth};"
            $"pub const BEAT_COUNT: usize = %d{golBeatCount gridWidth gridHeight};"
            $"pub const FRAME_BYTES: usize = %d{golBeatCount gridWidth gridHeight * 16};"
            $"pub const SLOT_STRIDE_BYTES: usize = %d{1 <<< golSlotShift gridWidth gridHeight};"
            "pub const SLOT_COUNT: usize = 3;"
            "" ]

    let verilogPath = System.IO.Path.Combine(buildDir, "GolAxi.v")
    let layoutPath = System.IO.Path.Combine(runtimeSrc, "gol_layout.rs")
    System.IO.File.WriteAllText(verilogPath, emitDesign golAxiFull.Value + "\n")
    System.IO.File.WriteAllText(layoutPath, String.concat "\n" layout)
    printfn $"wrote {verilogPath}"
    printfn $"wrote {layoutPath}"
    0

[<EntryPoint>]
let main argv =
    match argv with
    | [| "diff"; outDir |] ->
        writeDiff (diffDesigns ()) outDir
        0
    | [| "perf" |] -> perfProbe ()
    | [| "bench" |] -> benchTwin ()
    | [| "axi" |] -> axiRehearsalAt golAxiScaled 1 16 16 1 1 0 None
    // The same rehearsal with the DDR answering after a random 0-3 cycle delay
    // and stalling AW and W independently. A design whose frame depends on when
    // memory answered passes the always-ready model and corrupts on a board.
    | [| "axi-jitter" |] ->
        [ 1..6 ]
        |> List.map (fun seed -> axiRehearsalAt golAxiScaled 1 16 16 1 1 0 (Some seed) = 0)
        |> List.forall id
        |> fun ok ->
            printfn $"axi rehearsal under random memory timing (6 seeds): %b{ok}"
            if ok then 0 else 1
    | [| "axi-x2" |] -> axiRehearsalAt golAxiScaledX2 2 16 16 1 1 0 None
    | [| "axi-full"; awEvery; wEvery; bDelay |] ->
        axiRehearsalAt golAxiFull.Value golGensPerCycle 64 64 (int awEvery) (int wEvery) (int bDelay) None
    | [| "axi-full" |] ->
        // The pacing matrix: always-ready, then each channel throttled
        // against the other, then both with a lagging B — the skews a real
        // interconnect applies and the always-ready slave cannot.
        [ 1, 1, 0; 2, 1, 0; 1, 2, 0; 3, 1, 6; 1, 3, 6 ]
        |> List.map (fun (awEvery, wEvery, bDelay) ->
            printfn $"--- pacing aw/%d{awEvery} w/%d{wEvery} b+%d{bDelay} ---"
            axiRehearsalAt golAxiFull.Value golGensPerCycle 64 64 awEvery wEvery bDelay None)
        |> List.max
    | [| "hardware"; repoRoot |] -> writeHardware repoRoot
    | [| "emit"; gridWidth; gridHeight; path |] ->
        System.IO.File.WriteAllText(path, emitDesign (golHarness (int gridWidth) (int gridHeight)) + "\n")
        printfn $"wrote {path}"
        0
    | [| "emit-probe"; gensPerCycle; gridWidth; gridHeight; path |] ->
        System.IO.File.WriteAllText(
            path,
            emitDesign (golProbe (int gensPerCycle) (int gridWidth) (int gridHeight)) + "\n"
        )

        printfn $"wrote {path}"
        0
    | _ -> demoChecks ()
