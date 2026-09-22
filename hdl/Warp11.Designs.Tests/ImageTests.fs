module Warp11.Designs.Tests.ImageTests

open System.Numerics
open Expecto
open Warp11
open Warp11.Designs

let private runFlatStream label (design: ModuleDef) (inputPort: string) (outputPort: string) beats expectedCount =
    let inputPrefix = inputPort.Split('_').[0]
    let outputPrefix = outputPort.Split('_').[0]
    let sim = Sim design
    sim.Poke($"{outputPrefix}_ready", 1UL)
    let output = ResizeArray<uint64>()

    for index, beat in List.indexed beats do
        sim.Poke($"{inputPrefix}_valid", 1UL)
        sim.Poke(inputPort, beat)
        Expect.equal (sim.Peek $"{inputPrefix}_ready") 1UL $"{label} did not accept flat-out input {index}"

        if sim.Peek $"{outputPrefix}_valid" = 1UL then
            output.Add(sim.Peek outputPort)

        sim.Tick()

    sim.Poke($"{inputPrefix}_valid", 0UL)
    let mutable cycles = 0

    while output.Count < expectedCount && cycles < 64 do
        if sim.Peek $"{outputPrefix}_valid" = 1UL then
            output.Add(sim.Peek outputPort)

        sim.Tick()
        cycles <- cycles + 1

    Expect.equal output.Count expectedCount $"{label} produced {output.Count}/{expectedCount} expected outputs"
    List.ofSeq output

let private packRow (row: int[]) =
    row |> Array.mapi (fun index value -> uint64 value <<< (index * 8)) |> Array.sum

let tests =
    testList
        "Images and schedules"
        [ testCase "line windows wrap columns, survive stalls, and re-arm" <| fun _ ->
              let rows = 4
              let pixels = 8

              let widen row =
                  ((row &&& 1UL) <<< (pixels + 1))
                  ||| (row <<< 1)
                  ||| ((row >>> (pixels - 1)) &&& 1UL)

              let frames =
                  [ [ 0xA3UL; 0x01UL; 0x52UL; 0x9CUL; 0x7FUL; 0xE8UL ]
                    [ 0x11UL; 0xB6UL; 0x40UL; 0x05UL; 0xDDUL; 0x66UL ] ]

              let expected =
                  [ for frame in frames do
                        for row in 1..rows -> widen frame[row - 1], widen frame[row], widen frame[row + 1] ]

              let sim = Sim windowSweep.def
              let actual = ResizeArray<uint64 * uint64 * uint64>()
              let mutable cycle = 0

              for frameIndex, frame in List.indexed frames do
                  for beatIndex, beat in List.indexed frame do
                      sim.Poke("in_row", beat)
                      sim.Poke("in_valid", 1UL)
                      let mutable accepted = false
                      let mutable waitCycles = 0

                      while not accepted && waitCycles < 64 do
                          let ready = cycle % 3 <> 0
                          sim.Poke("win_ready", if ready then 1UL else 0UL)

                          if sim.Peek "win_valid" = 1UL && ready then
                              actual.Add(sim.Peek "win_row0", sim.Peek "win_row1", sim.Peek "win_row2")

                          accepted <- sim.Peek "in_ready" = 1UL
                          sim.Tick()
                          cycle <- cycle + 1
                          waitCycles <- waitCycles + 1

                      Expect.isTrue accepted $"Frame {frameIndex}, beat {beatIndex} was not accepted"

                  sim.Poke("in_valid", 0UL)

                  for _ in 1..3 do
                      sim.Poke("win_ready", 1UL)
                      Expect.equal (sim.Peek "win_valid") 0UL $"Frame {frameIndex} produced output in the inter-frame gap"
                      sim.Tick()

              Expect.sequenceEqual actual expected "Each output should contain the wrapped previous/current/next rows"

          testCase "pixel blur agrees with a clamped software model" <| fun _ ->
              let pixels = 4

              let rows =
                  [| [| 10; 200; 30; 90 |]
                     [| 0; 50; 255; 20 |]
                     [| 70; 80; 90; 100 |]
                     [| 5; 15; 25; 35 |] |]

              let beats = Array.concat [ [| rows[0] |]; rows; [| rows[rows.Length - 1] |] ]

              let expected =
                  [ for row in 0 .. rows.Length - 1 ->
                        packRow
                            [| for column in 0 .. pixels - 1 ->
                                   let mutable sum = 0

                                   for deltaRow in 0..2 do
                                       for deltaColumn in -1..1 do
                                           let sourceColumn = max 0 (min (pixels - 1) (column + deltaColumn))
                                           sum <- sum + beats[row + deltaRow][sourceColumn]

                                   (sum >>> 3) &&& 0xFF |] ]

              let actual =
                  runFlatStream "pixel blur" pixelBlur.def "in_row" "out_row" (beats |> Array.map packRow |> List.ofArray) rows.Length

              Expect.sequenceEqual actual expected "Blurred rows should agree with the 3x3 clamped model"

          testCase "moving average agrees with four-sample windows" <| fun _ ->
              let samples = [ 100UL; 200UL; 300UL; 400UL; 60000UL; 8UL; 12UL; 500UL ]
              let expected = samples |> List.windowed 4 |> List.map (fun window -> List.sum window / 4UL)
              let actual = runFlatStream "moving average" movingAverage.def "in_sample" "out_sample" samples expected.Length
              Expect.sequenceEqual actual expected "Every primed sample should equal the four-tap average"

          testCase "sparse dot agrees with fixed software taps" <| fun _ ->
              let activations = [| 9UL; 14UL; 3UL; 200UL; 77UL; 1UL; 130UL; 42UL |]
              let sim = Sim sparseDot.def

              for index, value in Array.indexed activations do
                  sim.Poke("fill_addr", uint64 index)
                  sim.Poke("fill_data", value)
                  sim.Poke("fill_enable", 1UL)
                  sim.Tick()

              sim.Poke("fill_enable", 0UL)
              sim.Tick()

              let expected = 3UL * activations[1] + activations[3] + 2UL * activations[4] + 5UL * activations[6]
              Expect.equal (sim.Peek "dot") expected "The wired sparse taps should compute the expected weighted sum"

          testCase "indirect gather returns B[A[i]]" <| fun _ ->
              let table = [| 5UL; 2UL; 7UL; 0UL; 3UL; 6UL; 1UL; 4UL |]
              let values = [| 11UL; 22UL; 33UL; 44UL; 55UL; 66UL; 77UL; 88UL |]
              let sim = Sim indirectGather.def

              for index in 0..7 do
                  sim.Poke("fill_t_addr", uint64 index)
                  sim.Poke("fill_t_data", table[index])
                  sim.Poke("fill_v_addr", uint64 index)
                  sim.Poke("fill_v_data", values[index])
                  sim.Poke("fill_t_enable", 1UL)
                  sim.Poke("fill_v_enable", 1UL)
                  sim.Tick()

              sim.Poke("fill_t_enable", 0UL)
              sim.Poke("fill_v_enable", 0UL)
              sim.Poke("out_ready", 1UL)
              let indices = [ 2UL; 0UL; 6UL; 5UL ]
              let actual = ResizeArray<uint64>()

              for index in indices do
                  sim.Poke("in_index", index)
                  sim.Poke("in_valid", 1UL)
                  let mutable accepted = false
                  let mutable waitCycles = 0

                  while not accepted && waitCycles < 64 do
                      accepted <- sim.Peek "in_ready" = 1UL
                      if sim.Peek "out_valid" = 1UL then actual.Add(sim.Peek "out_value")
                      sim.Tick()
                      waitCycles <- waitCycles + 1

                  Expect.isTrue accepted $"Gather request for index {index} was not accepted"

              sim.Poke("in_valid", 0UL)
              let mutable drainCycles = 0

              while actual.Count < indices.Length && drainCycles < 64 do
                  if sim.Peek "out_valid" = 1UL then actual.Add(sim.Peek "out_value")
                  sim.Tick()
                  drainCycles <- drainCycles + 1

              let expected = [ for index in indices -> values[int table[int index]] ]
              Expect.sequenceEqual actual expected "Each result should follow both synchronous memory hops"

          testCase "range stream restarts and advances only on transfer" <| fun _ ->
              let sim = Sim indexSweep.def
              let actual = ResizeArray<uint64>()
              let mutable cycle = 0

              let run () =
                  sim.Poke("start", 1UL)
                  sim.Tick()
                  sim.Poke("start", 0UL)

                  for _ in 1..24 do
                      let ready = cycle % 3 <> 2
                      sim.Poke("out_ready", if ready then 1UL else 0UL)

                      if sim.Peek "out_valid" = 1UL && ready then
                          actual.Add(sim.Peek "out_index")

                      sim.Tick()
                      cycle <- cycle + 1

              run ()
              run ()
              Expect.sequenceEqual actual [ 0UL; 1UL; 2UL; 3UL; 4UL; 0UL; 1UL; 2UL; 3UL; 4UL ] "Each start should emit exactly one complete range"

          testCase "iterative 5x5 max pool agrees with zero-padded dilation" <| fun _ ->
              let rowCount = 8
              let columnCount = 8

              let byteAt (world: uint64[]) row column =
                  if row < 0 || row >= rowCount || column < 0 || column >= columnCount then
                      0UL
                  else
                      (world[row] >>> (column * 8)) &&& 0xFFUL

              let dilate (world: uint64[]) =
                  [| for row in 0 .. rowCount - 1 ->
                         let mutable packed = 0UL

                         for column in 0 .. columnCount - 1 do
                             let mutable best = 0UL

                             for deltaRow in -2..2 do
                                 for deltaColumn in -2..2 do
                                     best <- max best (byteAt world (row + deltaRow) (column + deltaColumn))

                             packed <- packed ||| (best <<< (column * 8))

                         packed |]

              let random = System.Random 29
              let initial = [| for _ in 1..rowCount -> uint64 (random.Next()) <<< 32 ||| uint64 (random.Next()) |]
              let sim = Sim maxPoolDilate.def

              for row in 0 .. rowCount - 1 do
                  sim.Poke("fill_addr", uint64 row)
                  sim.Poke("fill_data", initial[row])
                  sim.Poke("fill_enable", 1UL)
                  sim.Tick()

              sim.Poke("fill_enable", 0UL)
              sim.Poke("run", 1UL)
              let mutable cycles = 0

              while sim.Peek "generation" < 2UL && cycles < 2000 do
                  sim.Tick()
                  cycles <- cycles + 1

              Expect.isLessThan cycles 2000 "Pooling did not complete two generations within the cycle limit"
              sim.Poke("run", 0UL)

              for _ in 1..48 do
                  sim.Tick()

              let completed = int (sim.Peek "generation")
              Expect.isGreaterThanOrEqual completed 2 "At least two dilation passes should complete"

              let expected =
                  [ 1..completed ]
                  |> List.fold (fun world _ -> dilate world) initial

              for row in 0 .. rowCount - 1 do
                  sim.Poke("probe_addr", uint64 row)
                  sim.Tick()
                  Expect.equal (sim.Peek "probe_data") expected[row] $"Dilation output differs at row {row} after {completed} passes"

          testCase "wide one-shot 2x2 pool agrees with the software model" <| fun _ ->
              let rowCount = 24
              let columnCount = 24
              let random = System.Random 31
              let cells = Array.init rowCount (fun _ -> Array.init columnCount (fun _ -> random.Next 256))

              let packWideRow (row: int[]) =
                  row
                  |> Array.mapi (fun column value -> BigInteger value <<< (column * 8))
                  |> Array.sum

              let expected =
                  [ for row in 0 .. rowCount / 2 - 1 ->
                        packWideRow
                            [| for column in 0 .. columnCount / 2 - 1 ->
                                   [ for deltaRow in 0..1 do
                                         for deltaColumn in 0..1 -> cells[2 * row + deltaRow][2 * column + deltaColumn] ]
                                   |> List.max |] ]

              let sim = Sim maxPool2x2.def

              for row in 0 .. rowCount - 1 do
                  sim.Poke("fill_addr", uint64 row)
                  sim.PokeWide("fill_data", packWideRow cells[row])
                  sim.Poke("fill_enable", 1UL)
                  sim.Tick()

              sim.Poke("fill_enable", 0UL)
              sim.Poke("start", 1UL)
              sim.Tick()
              sim.Poke("start", 0UL)
              let mutable cycles = 0

              while sim.Peek "complete" = 0UL && cycles < 400 do
                  sim.Tick()
                  cycles <- cycles + 1

              Expect.equal (sim.Peek "complete") 1UL "One-shot pooling did not complete within 400 cycles"

              for row in 0 .. rowCount / 2 - 1 do
                  sim.Poke("probe_addr", uint64 row)
                  sim.Tick()
                  Expect.equal (sim.PeekWide "probe_data") expected[row] $"Wide pooled output differs at row {row}"

          testCase "combinational 2x2 pool agrees without a clock tick" <| fun _ ->
              let random = System.Random 37
              let sim = Sim maxPoolCombinational.def

              for caseIndex in 1..4 do
                  let cells = [| for _ in 0..63 -> uint64 (random.Next 256) |]

                  for row in 0..7 do
                      for column in 0..7 do
                          sim.Poke($"in_{row}_{column}", cells[row * 8 + column])

                  for row in 0..3 do
                      for column in 0..3 do
                          let expected =
                              [ for deltaRow in 0..1 do
                                    for deltaColumn in 0..1 -> cells[(2 * row + deltaRow) * 8 + (2 * column + deltaColumn)] ]
                              |> List.max

                          Expect.equal
                              (sim.Peek $"out_{row}_{column}")
                              expected
                              $"Combinational pool case {caseIndex} differs at ({row}, {column})" ]
