module Warp11.Designs.Tests.RegMapTests

open Expecto
open Warp11
open Warp11.Designs

let private layoutHash (map: RegMap) =
    map.entries
    |> List.pick (fun entry ->
        match entry.name, entry.kind with
        | "layout", RoConst value -> Some value
        | _ -> None)

let tests =
    testList
        "Register maps"
        [ testCase "builder matches the hand-written layout" <| fun _ ->
              let expected =
                  [ roConst "id" 0x000UL 0xF5C0FFEEUL
                    pulseBit "bump" 0x000UL 0
                    pulseBit "clear" 0x000UL 1
                    rwReg "threshold" 0x004UL 16 0UL
                    roField "count" 0x008UL 0 8
                    roField "high" 0x008UL 8 1
                    w1cBit "wrapIrq" 0x00CUL 0
                    roField "patLow" 0x010UL 0 8
                    rwArray "pattern" 0x040UL 16 Distributed None
                    roArray "trace" 0x080UL 16 Distributed ]

              Expect.sequenceEqual scratchMap.entries expected "The allocated map should preserve the manually computed layout"

          testCase "builder derives the smallest valid aperture" <| fun _ ->
              let _, tiny = buildRegMap (fun builder -> builder.RwReg("a", 8, 0UL), builder.RoField("b", 8))
              let _, wide = buildRegMap (fun builder -> [ for index in 0..7 -> builder.RwReg($"r{index}", 32, 0UL) ])
              Expect.equal tiny.apertureAddrWidth 4 "A tiny map should retain the 16-byte minimum aperture"
              Expect.equal wide.apertureAddrWidth 5 "Eight words should require a five-bit address"

          testCase "At controls the next allocated offset" <| fun _ ->
              let registers, _ =
                  buildRegMap (fun builder ->
                      let first = builder.RwReg("first", 32, 0UL)
                      builder.At 0x40UL
                      first, builder.RwReg("second", 32, 0UL))

              Expect.equal (fst registers).offset 0x00UL "The first register should begin at zero"
              Expect.equal (snd registers).offset 0x40UL "At should seek before allocating the second register"

          testCase "layout hash follows layout rather than declaration order" <| fun _ ->
              let _, inOrder =
                  buildRegMapPinned 5 (fun builder ->
                      builder.LayoutHash "layout"
                      builder.RwReg("a", 8, 0UL) |> ignore
                      builder.RwReg("b", 16, 0UL) |> ignore)

              let _, reordered =
                  buildRegMapPinned 5 (fun builder ->
                      builder.At 0x08UL
                      builder.RwReg("b", 16, 0UL) |> ignore
                      builder.At 0x00UL
                      builder.LayoutHash "layout"
                      builder.RwReg("a", 8, 0UL) |> ignore)

              let _, widened =
                  buildRegMapPinned 5 (fun builder ->
                      builder.LayoutHash "layout"
                      builder.RwReg("a", 8, 0UL) |> ignore
                      builder.RwReg("b", 17, 0UL) |> ignore)

              let _, renamed =
                  buildRegMapPinned 5 (fun builder ->
                      builder.LayoutHash "layout"
                      builder.RwReg("a", 8, 0UL) |> ignore
                      builder.RwReg("c", 16, 0UL) |> ignore)

              let _, added =
                  buildRegMapPinned 5 (fun builder ->
                      builder.LayoutHash "layout"
                      builder.RwReg("a", 8, 0UL) |> ignore
                      builder.RwReg("b", 16, 0UL) |> ignore
                      builder.RwReg("d", 8, 0UL) |> ignore)

              let expected = layoutHash inOrder
              Expect.equal (layoutHash reordered) expected "Equivalent layouts should have the same hash"
              Expect.notEqual (layoutHash widened) expected "Changing a width should change the hash"
              Expect.notEqual (layoutHash renamed) expected "Changing a name should change the hash"
              Expect.notEqual (layoutHash added) expected "Adding an entry should change the hash"
              Expect.isLessThanOrEqual expected 0xFFFFUL "The generated seam stores the hash in 16 bits"

          testCase "AXI register map implements fields, pulses, windows, and W1C" <| fun _ ->
              let sim = Sim regMapScratch.def
              let axi = SimAxi.client sim

              Expect.equal (axi.read32 0x000UL) 0xF5C0FFEEUL "The ID overlay should be readable"
              axi.write32 0x004UL 3UL
              Expect.equal (axi.read32 0x004UL) 3UL "The threshold register should retain writes"

              axi.write32 0x000UL 1UL
              axi.write32 0x000UL 1UL
              Expect.equal (axi.read32 0x008UL) 2UL "The bump pulse should increment count"

              axi.write32 0x000UL 1UL
              axi.write32 0x000UL 1UL
              Expect.equal (axi.read32 0x008UL) 0x104UL "Packed read-only fields should share a word"

              axi.write32 0x040UL 0xABUL
              axi.write32 0x000UL 2UL
              Expect.equal (axi.read32 0x010UL) 0xABUL "The fabric should read the host-written window"

              for _ in 1..256 do
                  axi.write32 0x000UL 1UL

              Expect.equal (axi.read32 0x00CUL) 1UL "Counter wrap should set the interrupt status"
              Expect.equal (sim.Peek "irq") 1UL "Counter wrap should assert IRQ"
              axi.write32 0x00CUL 1UL
              Expect.equal (axi.read32 0x00CUL) 0UL "Writing one should clear W1C status"
              Expect.equal (sim.Peek "irq") 0UL "Clearing status should deassert IRQ"

          testCase "arbitrated host window reads preserve design access" <| fun _ ->
              let sim = Sim regMapScratch.def
              let axi = SimAxi.client sim

              for index in 0..15 do
                  axi.write32 (0x040UL + uint64 (index * 4)) (0xA100UL + uint64 index)

              for index in 0..15 do
                  let address = 0x040UL + uint64 (index * 4)
                  Expect.equal (axi.read32 address) (0xA100UL + uint64 index) $"Window word {index} should read back"

              axi.write32 0x044UL 0xBEEFUL
              Expect.equal (axi.read32 0x044UL) 0xBEEFUL "A later write should replace the selected word"
              Expect.equal (axi.read32 0x010UL) 0x00UL "The design-side port should return to pattern[0]"
              axi.write32 0x040UL 0xA1FFUL
              Expect.equal (axi.read32 0x010UL) 0xFFUL "The design-side port should observe subsequent writes"

          testCase "host reads design-written window words" <| fun _ ->
              let sim = Sim regMapScratch.def
              let axi = SimAxi.client sim

              for _ in 1..5 do
                  axi.write32 0x000UL 1UL

              for index in 0..4 do
                  let address = 0x080UL + uint64 (index * 4)
                  Expect.equal (axi.read32 address) (0xC500UL + uint64 index) $"Trace word {index} should contain its marked value"

              Expect.equal (axi.read32 (0x080UL + 10UL * 4UL)) 0UL "An untouched trace word should remain zero" ]
