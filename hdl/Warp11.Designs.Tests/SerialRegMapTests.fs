module Warp11.Designs.Tests.SerialRegMapTests

open Expecto
open Warp11
open Warp11.Designs

let private openLink () =
    let sim = Sim serialRegMap.def
    let cyclesPerBit = uartCyclesPerBit serialFabricHz serialBaud
    let link, uart = serialClient sim (uartSimPins "host") cyclesPerBit
    sim, cyclesPerBit, link, uart

/// The streaming variant's link, plus the sim so a check can read how many
/// words the fabric believes it has handed over.
let private openStreamLink () =
    let sim = Sim serialRegMapStream.def
    let cyclesPerBit = uartCyclesPerBit serialFabricHz serialBaud
    let link, uart = serialClient sim (uartSimPins "host") cyclesPerBit
    sim, cyclesPerBit, link, uart

let tests =
    testList
        "Serial register map"
        [ testCase "streamed frames come out, and register traffic still answers" <| fun _ ->
              let sim, _, link, uart = openStreamLink ()

              // The register map still works with a stream competing for the
              // wire — this is the property that matters, because a recorder
              // that made `wdrc show` stop answering would be unusable.
              Expect.equal (link.read32 serialRegs.id.offset) 0x5E71A1UL "the ID should still cross the UART"
              link.write32 serialRegs.control.offset 0xBEEFUL
              Expect.equal (link.read32 serialRegs.control.offset) 0xBEEFUL "a write should still read back"
              Expect.equal (sim.Peek "control_out") 0xBEEFUL "the write should still reach the design"

              // ...and the fabric has been handing words over all along.
              Expect.isGreaterThan (sim.Peek "sent") 0UL "no streamed word was ever taken"

          testCase "a streamed frame is tagged apart from a reply" <| fun _ ->
              let sim, cyclesPerBit, _, uart = openStreamLink ()
              let device = uart :> ISimDevice

              // Let the link run with nothing asked of it: every frame out is
              // a streamed one.
              for _ in 1 .. cyclesPerBit * 200 do
                  device.Drive()
                  sim.Tick()
                  device.Sample()

              let bytes = uart.TakeReceived()
              Expect.isGreaterThan bytes.Length 6 "the fabric sent nothing unasked"

              let sync = List.findIndex (fun b -> b = byte serialSync) bytes
              let frame = bytes[sync .. sync + 6]
              Expect.equal frame[0] (byte serialSync) "a frame starts with the sync byte"
              Expect.equal frame[1] (byte serialStreamStatus) "an unasked frame is tagged as a streamed word"

              let payloadXor = frame[2] ^^^ frame[3] ^^^ frame[4] ^^^ frame[5]
              Expect.equal frame[6] (frame[1] ^^^ payloadXor) "the streamed frame's checksum should cover status and data"

          testCase "UART link reads, writes, pulses, and live fields" <| fun _ ->
              let sim, _, link, _ = openLink ()
              Expect.equal (link.read32 serialRegs.id.offset) 0x5E71A1UL "The constant ID should cross the UART"
              Expect.equal (link.read32 serialRegs.control.offset) 0x100UL "The control register should expose its reset value"
              Expect.equal (sim.Peek "control_out") 0x100UL "The design should see the reset value"

              link.write32 serialRegs.control.offset 0xBEEFUL
              Expect.equal (link.read32 serialRegs.control.offset) 0xBEEFUL "A UART write should read back"
              Expect.equal (sim.Peek "control_out") 0xBEEFUL "A UART write should reach the design"

              link.write32 serialRegs.bump.offset 1UL
              link.write32 serialRegs.bump.offset 1UL
              Expect.equal (link.read32 serialRegs.count.offset) 2UL "Two pulse writes should increment twice"
              let first = link.read32 serialRegs.ticks.offset
              let second = link.read32 serialRegs.ticks.offset
              Expect.isGreaterThan second first "The live tick field should advance between UART transactions"

          testCase "UART link refuses a bad checksum without writing" <| fun _ ->
              let sim, cyclesPerBit, link, uart = openLink ()
              link.write32 serialRegs.control.offset 0xBEEFUL
              let request = SerialFrame.write (int (serialRegs.control.offset >>> 2)) 0x1234UL
              let corrupted = List.take (List.length request - 1) request @ [ List.last request ^^^ 0x01uy ]
              uart.TakeReceived() |> ignore
              uart.Send corrupted
              let device = uart :> ISimDevice
              let budget = (List.length corrupted + 3) * 10 * cyclesPerBit + 64 * cyclesPerBit

              for _ in 1..budget do
                  device.Drive()
                  sim.Tick()
                  device.Sample()

              match SerialFrame.parse false (uart.TakeReceived()) with
              | Error reason -> Expect.stringContains reason "refused" "The reply should explicitly refuse the corrupt frame"
              | Ok reply -> failtestf "Corrupt request unexpectedly succeeded: %A" reply

              Expect.equal (sim.Peek "control_out") 0xBEEFUL "The corrupt write must leave the register untouched"

          testCase "preloaded writable window reloads on reset" <| fun _ ->
              let sim, _, link, _ = openLink ()
              let wordAt index = serialRegs.preset.offset + uint64 (4 * index)
              let expected = serialTableInit @ List.replicate 4 0UL

              for index in 0..7 do
                  Expect.equal (link.read32 (wordAt index)) expected[index] $"Preloaded word {index} should boot with its declared value"

              link.write32 serialRegs.control.offset 1UL
              Expect.equal (link.read32 serialRegs.entry.offset) 20UL "The design-side read should see preloaded word one"
              link.write32 (wordAt 1) 77UL
              Expect.equal (link.read32 (wordAt 1)) 77UL "The host should read back its window write"
              Expect.equal (link.read32 serialRegs.entry.offset) 77UL "The design-side read should follow the host write"
              Expect.equal (link.read32 (wordAt 0)) 10UL "The previous word should remain unchanged"
              Expect.equal (link.read32 (wordAt 2)) 30UL "The next word should remain unchanged"

              sim.Reset()
              Expect.equal (link.read32 (wordAt 1)) 20UL "Reset should reload the declared window contents" ]
