/// The software twin: the same B3/S23 rule with the same dead border, over
/// rows as bit masks (bit x of rows[y] = cell (y, x)) — the representation
/// the Sim's row ports peek straight into.
module Warp11.GoL.Twin

let step (gridWidth: int) (gridHeight: int) (rows: uint64[]) : uint64[] =
    let cell y x =
        if y >= 0 && y < gridHeight && x >= 0 && x < gridWidth then
            int ((rows[y] >>> x) &&& 1UL)
        else
            0

    let neighbors y x =
        List.sum
            [ for dy in -1 .. 1 do
                  for dx in -1 .. 1 do
                      if dy <> 0 || dx <> 0 then yield cell (y + dy) (x + dx) ]

    [| for y in 0 .. gridHeight - 1 ->
           (0UL, [ 0 .. gridWidth - 1 ])
           ||> List.fold (fun acc x ->
               let n = neighbors y x

               if n = 3 || (cell y x = 1 && n = 2) then
                   acc ||| (1UL <<< x)
               else
                   acc) |]

let population (rows: uint64[]) =
    rows |> Array.sumBy System.Numerics.BitOperations.PopCount
