/// The engine: Conway's rule as an ordinary function over 64 rows of 64 bits
/// (bit x of rows[y] = cell (y, x), dead border, B3/S23). Three
/// implementations of the same step — the software rungs of the tutorial's
/// optimization ladder. Swapping one into SimulatedBus changes the number on
/// screen and nothing else.
module Warp11.GolView.Engine

/// The step a developer writes first: clear, allocation-heavy, obviously
/// correct. This is also the oracle the hardware chapters diff against.
let stepIdiomatic (rows: uint64[]) : uint64[] =
    let cell y x =
        if y >= 0 && y < 64 && x >= 0 && x < 64 then
            int ((rows[y] >>> x) &&& 1UL)
        else
            0

    let neighbors y x =
        List.sum
            [ for dy in -1 .. 1 do
                  for dx in -1 .. 1 do
                      if dy <> 0 || dx <> 0 then yield cell (y + dy) (x + dx) ]

    [| for y in 0..63 ->
           (0UL, [ 0..63 ])
           ||> List.fold (fun acc x ->
               let n = neighbors y x

               if n = 3 || (cell y x = 1 && n = 2) then
                   acc ||| (1UL <<< x)
               else
                   acc) |]

/// The same per-cell algorithm with the allocations squeezed out: plain
/// loops, no lists, one output array. Same answers, ~25x the speed.
let stepArrays (rows: uint64[]) : uint64[] =
    let next = Array.zeroCreate 64

    for y in 0..63 do
        let mutable acc = 0UL

        for x in 0..63 do
            let mutable n = 0

            for dy in -1 .. 1 do
                for dx in -1 .. 1 do
                    let yy, xx = y + dy, x + dx

                    if (dy <> 0 || dx <> 0) && yy >= 0 && yy < 64 && xx >= 0 && xx < 64 then
                        n <- n + int ((rows[yy] >>> xx) &&& 1UL)

            if n = 3 || (n = 2 && (rows[y] >>> x) &&& 1UL = 1UL) then
                acc <- acc ||| (1UL <<< x)

        next[y] <- acc

    next

/// A different algorithm: carry-save adders over the row bitmasks compute
/// every column's neighbor count at once, 64 cells per operation. Shifting
/// left aligns the x-1 neighbor onto column x, so the dead border falls out
/// of the shifts for free. ~2000x the idiomatic step — and the same shape
/// the fabric implements in parallel adders.
let stepBitboard (rows: uint64[]) : uint64[] =
    let inline ha a b = struct (a ^^^ b, a &&& b)

    let inline fa a b c =
        let s = a ^^^ b
        struct (s ^^^ c, (a &&& b) ||| (c &&& s))

    [| for y in 0..63 ->
           let u = if y > 0 then rows[y - 1] else 0UL
           let s = rows[y]
           let d = if y < 63 then rows[y + 1] else 0UL
           let struct (us, uc) = fa (u <<< 1) u (u >>> 1)
           let struct (ds, dc) = fa (d <<< 1) d (d >>> 1)
           let struct (ss, sc) = ha (s <<< 1) (s >>> 1)
           // Bit-planes of the neighbor count: n0 ones, n1 twos, n2/n3 fours
           // and eights.
           let struct (n0, c1) = fa us ds ss
           let struct (t, c2) = fa uc dc sc
           let struct (n1, c2b) = ha t c1
           let struct (n2, n3) = ha c2 c2b
           // N = 3, or N = 2 and already alive.
           ~~~n2 &&& ~~~n3 &&& n1 &&& (n0 ||| s) |]

let population (rows: uint64[]) : uint32 =
    rows
    |> Array.sumBy (fun r -> uint32 (System.Numerics.BitOperations.PopCount r))
