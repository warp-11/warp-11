/// The GoL executable: living checks against the twin, the differential
/// writer, and the Sim-performance probe the direction's open qualifier
/// asked for at this design (`perf`).
module Warp11.GoL.Main

open Warp11
open Warp11.GoL.Board
open Warp11.GoL.Core
open Warp11.GoL.Twin
open Warp11.GoL

let private diffDesigns () =
    [ (golHarness 8 8).def
      (golLiveHarness 8 8).def
      (BoardTop.boardTopFor World.mapping World.design).top
      (BoardTop.boardTop icepi viaLink (World.lifeAt 8 8)).top ]

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
    for d in [ (golHarness 8 8).def ] do
        match checkWidths d with
        | [] -> printfn $"{d.name}: widths ok"
        | problems -> problems |> List.iter (printfn "%s")

    // The glider — the canonical Life litmus: it must translate one cell
    // diagonally every 4 generations until the dead border eats it, and the
    // twin must agree the whole way.
    let glider =
        [| 0b010UL; 0b100UL; 0b111UL; 0UL; 0UL; 0UL; 0UL; 0UL |]

    let gliderOk = runAgainstTwin (Sim (golHarness 8 8).def) 8 8 1 glider 16
    printfn $"glider vs twin (16 gens):     %b{gliderOk}"

    // Random soups: dense chaos exercises every birth/survive/die case at
    // every border. Deterministic seed, three soups, 30 generations each.
    let rand = System.Random 11

    let soupsOk =
        [ 1..3 ]
        |> List.forall (fun _ ->
            let soup = [| for _ in 0..7 -> uint64 (rand.Next(0, 256)) |]
            runAgainstTwin (Sim (golHarness 8 8).def) 8 8 1 soup 30)

    printfn $"random soups vs twin (3x30):  %b{soupsOk}"

    // The world as a design on a board with one wire: the glider seeded over
    // the Icepi's link a row a frame, run for a while at the fabric's pace,
    // stopped, and the world pulled whole — the twin's world at the
    // generation the fabric reports.
    let linkOk =
        let top = BoardTop.boardTop icepi viaLink (World.lifeAt 8 8)
        let sim = Sim top.top
        let cyclesPerBit = uartCyclesPerBit icepi.fabricHz 1_000_000
        let link, uart = serialClientForMap top.map sim (uartSimPins "host") cyclesPerBit
        let device = uart :> ISimDevice
        let offset name = (top.registers |> List.find (fun (n, _) -> n = name) |> snd).offset

        let drain cycles =
            for _ in 1 .. cycles do
                device.Drive()
                sim.Tick()
                device.Sample()

        link.write32 (offset "seedLinkArm") 1UL

        for row in glider do
            uart.Send(SerialFrame.play row)

        drain (8 * 6 * 10 * cyclesPerBit * 2)
        // A free-running burst, stopped from the host.
        link.write32 (offset "intervalCycles") 3UL
        link.write32 (offset "tickCount") 0UL
        link.write32 (offset "tick") 1UL
        drain 5_000
        let runningOk = link.read32 (offset "ticking") = 1UL
        link.write32 (offset "stop") 1UL
        let generation = int (link.read32 (offset "generation"))
        // Then a counted one: exactly seven more, and the burst ends itself.
        link.write32 (offset "tickCount") 7UL
        link.write32 (offset "tick") 1UL
        drain 200
        let afterBurst = int (link.read32 (offset "generation"))
        let burstOk = afterBurst = generation + 7 && link.read32 (offset "ticking") = 0UL
        let generation = afterBurst
        uart.TakeReceived() |> ignore
        link.write32 (offset "worldLinkPull") 1UL
        drain (8 * 7 * 10 * cyclesPerBit * 2)
        let world, _ = SerialFrame.streamedWords (uart.TakeReceived())
        let expected = [ 1 .. generation ] |> List.fold (fun g _ -> step 8 8 g) glider |> List.ofArray
        let populationOk = link.read32 (offset "population") = uint64 (population (Array.ofList expected))
        printfn $"life over the link ({top.name}): %d{generation} generations, world %b{world = expected}, population %b{populationOk}, ticking while free %b{runningOk}, counted burst of 7 %b{burstOk}"
        world = expected && populationOk && generation > 0 && runningOk && burstOk

    printfn $"life over the link vs twin:   %b{linkOk}"

    // A load must win over a same-cycle tick — the wrapper's pacing FSM may
    // be mid-burst when the host reloads.
    let loadWinsOk =
        let sim = Sim (golHarness 8 8).def
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
        let d = (golHarness gridWidth gridHeight).def
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

[<EntryPoint>]
let main argv =
    match argv with
    | [| "diff"; outDir |] ->
        writeDiff (diffDesigns ()) outDir
        0
    | [| "perf" |] -> perfProbe ()
    | [| "bench" |] -> benchTwin ()
    // The world for the project's board — `Board.board`, one line to
    // change: `life out/` writes the directory its toolchain builds from.
    // The world's side is `World.side`, a design parameter stated in its
    // own line. On a board with one wire the world comes out as snapshots
    // over it; on the KV260 it is the host-memory top, the triple buffer
    // in DDR that `gol-daemon` serves. `mapping` prints the mapping the
    // world gets.
    | [| "mapping" |] ->
        printfn "%s" (Warp11.Mapping.write World.mapping)
        0
    | [| "life"; dir |] ->
        let out = Warp11.Build.write dir (BoardTop.boardTopFor World.mapping World.design)

        for file in out.files do
            printfn $"wrote {file}"

        printfn $"build with: {out.run}"
        0
    | [| "emit"; gridWidth; gridHeight; path |] ->
        System.IO.File.WriteAllText(path, emitDesign (golHarness (int gridWidth) (int gridHeight)).def + "\n")
        printfn $"wrote {path}"
        0
    | _ -> demoChecks ()
