[<AutoOpen>]
/// Driving an AXI-Lite slave that lives inside the Sim — the *host's* side of
/// the seam `RegMap` generates, written in the language the design was.
///
/// This exists because six projects had hand-written copies of the same five
/// channel handshake: `Warp11.Gep`, `Warp11.GoL`, `Warp11.Designs`,
/// `Warp11.Tutorial.App`, and both of `Warp11.Mandelbrot`'s hosts. The parts
/// that actually differed were which `Sim` and how a cycle passes; everything
/// else was the AXI-Lite specification, copied.
///
/// Every step asserts the handshake it expects, which is the point: a protocol
/// mistake fails here, loudly, in a simulator you can step — rather than
/// hanging on silicon where there is nothing to look at.
module Warp11.SimAxi

/// A host's view of a slave: the two operations a register window has.
///
/// Deliberately not an interface over the `Sim` itself. What a driver needs is
/// exactly a 32-bit read and a 32-bit write at an offset, which is also what
/// the Rust `RegisterWindow` offers, so the two sides of the bridge describe
/// the same thing in their own languages.
type AxiLiteClient =
    { read32: uint64 -> uint64
      write32: uint64 -> uint64 -> unit }

/// Drive a design's `s_axi_*` pins through real handshakes.
///
/// `advance` is how one cycle passes. It is a parameter rather than
/// `sim.Tick` because a design whose *master* moves while the host talks to its
/// slave needs those cycles serviced too — Game of Life and the Mandelbrot
/// frame pod both pass a fake-DDR harness's `Cycle` here, and get the AXI-Lite
/// conversation and the DDR traffic interleaved the way silicon would.
let clientWith (sim: Sim) (advance: unit -> unit) : AxiLiteClient =
    let require signal expected =
        let actual = sim.Peek signal

        if actual <> expected then
            failwith $"AXI-Lite protocol: {signal} = %d{actual}, expected %d{expected}"

    let read32 (offset: uint64) =
        sim.Poke("s_axi_araddr", offset)
        sim.Poke("s_axi_arvalid", 1UL)
        sim.Poke("s_axi_rready", 1UL)
        require "s_axi_arready" 1UL
        advance ()
        sim.Poke("s_axi_arvalid", 0UL)
        require "s_axi_rvalid" 1UL
        let value = sim.Peek "s_axi_rdata"
        advance ()
        require "s_axi_rvalid" 0UL
        sim.Poke("s_axi_rready", 0UL)
        value

    let write32 (offset: uint64) (value: uint64) =
        sim.Poke("s_axi_awaddr", offset)
        sim.Poke("s_axi_awvalid", 1UL)
        sim.Poke("s_axi_wdata", value)
        sim.Poke("s_axi_wvalid", 1UL)
        sim.Poke("s_axi_bready", 1UL)
        require "s_axi_awready" 1UL
        require "s_axi_wready" 1UL
        advance ()
        sim.Poke("s_axi_awvalid", 0UL)
        sim.Poke("s_axi_wvalid", 0UL)
        require "s_axi_bvalid" 1UL
        advance ()
        require "s_axi_bvalid" 0UL
        sim.Poke("s_axi_bready", 0UL)

    { read32 = read32; write32 = write32 }

/// The common case: cycles pass by ticking the Sim and nothing else needs
/// servicing.
let client (sim: Sim) : AxiLiteClient = clientWith sim sim.Tick

/// The F# half of the Rust `FsSimWindow` bridge, so the same driver that will
/// mmap `/dev/mem` on a board runs against the elaborated design first.
///
/// Reads `R <hexoffset>` and `W <hexoffset> <hexvalue>` on stdin and answers
/// with the value read, or `OK`. The ready marker goes out first because
/// `dotnet run`'s own build chatter may precede us on stdout, and the Rust side
/// discards lines until it sees it.
///
/// A project exposes this as one more argv case:
///
///     | [| "simserve" |] -> SimAxi.serve (SimAxi.client (Sim myDesign))
///
/// and `FsSimWindow::spawn` on the Rust side does the rest.
let serveWith (marker: string) (client: AxiLiteClient) =
    let out = System.Console.Out
    out.WriteLine marker
    out.Flush()

    let mutable line = System.Console.In.ReadLine()

    while line <> null do
        (match line.Split(' ') with
         | [| "R"; offset |] -> out.WriteLine(sprintf "%08x" (client.read32 (System.Convert.ToUInt64(offset, 16))))
         | [| "W"; offset; value |] ->
             client.write32 (System.Convert.ToUInt64(offset, 16)) (System.Convert.ToUInt64(value, 16))
             out.WriteLine "OK"
         | _ -> out.WriteLine "ERR")

        out.Flush()
        line <- System.Console.In.ReadLine()

/// `serveWith` at the marker `FsSimWindow::spawn` waits for.
let serve (client: AxiLiteClient) = serveWith "SIMSERVE" client
