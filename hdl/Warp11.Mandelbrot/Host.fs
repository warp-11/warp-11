module Warp11.Mandelbrot.Host

open Warp11
open Warp11.Mandelbrot.Pod

/// `dotnet run -- simserve`: the F# half of the FsSimWindow bridge. Reads
/// `R <hexoff>` / `W <hexoff> <hexval>` lines on stdin, drives the wrapper's
/// s_axi pins through real five-channel handshakes in the Sim, and answers
/// with the value read / `OK`. The Rust `FsSimWindow` speaks the other end, so
/// the same driver that will mmap /dev/mem on the board runs against the
/// elaborated design first — the two-backends property, across the language
/// seam. Every handshake step is asserted, so a protocol bug is a failure
/// here, not a hang on silicon. The pod runs only while transactions tick it;
/// it ignores the bus, so its cycle count to done is the same as standalone.
/// `dotnet run -- simserve`: the F# half of the FsSimWindow bridge. The
/// handshakes, the assertions and the line protocol all live in `SimAxi` now —
/// what is left here is which design the bridge is talking to. The pod runs
/// only while transactions tick it; it ignores the bus, so its cycle count to
/// done is the same as standalone.
let simserve () =
    SimAxi.serve (SimAxi.client (Sim mandelPodAxi))

let private litValue expr =
    match expr with
    | Lit (v, _) -> v
    | _ -> failwith "not a literal"

/// The software twin: the lane's arithmetic replayed in host integers, exact to
/// the bit — every truncation, wrap and compare the same. GEP's pattern: the
/// fabric is right when it matches this, per pixel, with no tolerance.
let private mandelTwin () =
    let mask32 = 0xFFFFFFFFUL
    let stepBits = litValue (Number.constant Number.q4_28 mandelStep).bits
    let xMinBits = litValue (Number.constant Number.q4_28 mandelXMin).bits
    let yMinBits = litValue (Number.constant Number.q4_28 mandelYMin).bits
    let threshold = int64 (litValue (Number.constant Number.q8_56 4.0).bits)

    [| for py in 0 .. mandelHeight - 1 do
           for px in 0 .. mandelWidth - 1 do
               let cx = ((uint64 px * stepBits &&& mask32) + xMinBits) &&& mask32
               let cy = ((uint64 py * stepBits &&& mask32) + yMinBits) &&& mask32

               let rec iterate zx zy iter =
                   let zxs = signExtend64 32 zx
                   let zys = signExtend64 32 zy
                   let zx2 = zxs * zxs
                   let zy2 = zys * zys

                   if threshold < int64 (zx2 + zy2) || iter = int mandelMaxIter then
                       iter
                   else
                       let zxNext = (((zx2 - zy2) >>> 28 &&& mask32) + cx) &&& mask32
                       let zyNext = ((zxs * zys >>> 27 &&& mask32) + cy) &&& mask32
                       iterate zxNext zyNext (iter + 1)

               yield iterate 0UL 0UL 0 |]

/// `dotnet run -- mandel <out.ppm>`: tick the pod to `done`, read the
/// framebuffer through PeekMem, assert bit-exactness against the twin, write
/// the PPM (interior black, escapes shaded by iteration) and print an ASCII
/// preview. This is the Mandelbrot path's acceptance artifact end to end.
let runMandel (outPath: string) =
    let sim = Sim(mandelPod)
    let mutable cycles = 0

    while sim.Peek "done" <> 1UL && cycles < 200000 do
        sim.Tick()
        cycles <- cycles + 1

    if sim.Peek "done" <> 1UL then
        failwith "MandelPod did not reach done within 200000 cycles"

    let fb = [| for i in 0 .. mandelWidth * mandelHeight - 1 -> sim.PeekMem("fb", i) |]
    let twin = mandelTwin ()

    let mismatches =
        [ for i in 0 .. fb.Length - 1 do
              if fb[i] <> uint64 twin[i] then yield i ]

    let shade (v: uint64) = if v >= mandelMaxIter then 0UL else v

    let rows =
        [ for py in 0 .. mandelHeight - 1 ->
              String.concat " " [ for px in 0 .. mandelWidth - 1 -> string (shade fb[py * mandelWidth + px]) ] ]

    System.IO.File.WriteAllText(
        outPath,
        $"P2\n%d{mandelWidth} %d{mandelHeight}\n%d{mandelMaxIter}\n"
        + String.concat "\n" rows
        + "\n"
    )

    let ramp = " .:-=+*#%@"

    for charRow in 0 .. mandelHeight / 2 - 1 do
        let line =
            System.String(
                [| for px in 0 .. mandelWidth - 1 ->
                       let v = fb[charRow * 2 * mandelWidth + px]

                       if v >= mandelMaxIter then '@'
                       else ramp[min 8 (int v * 9 / int mandelMaxIter)] |]
            )

        printfn "%s" line

    printfn $"rendered %d{mandelWidth}x%d{mandelHeight} @ max %d{mandelMaxIter} iterations in %d{cycles} cycles -> {outPath}"
    printfn $"twin mismatches:              %d{List.length mismatches} of %d{fb.Length} pixels"

    if not (List.isEmpty mismatches) then
        failwith "the fabric and the software twin disagree"
