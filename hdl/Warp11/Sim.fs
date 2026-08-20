[<AutoOpen>]
module Warp11.Sim

open System.Numerics

let rec private refs expr =
    match expr with
    | Lit _ -> []
    | Ref (n, _) -> [ n ]
    | Add (a, b)
    | Sub (a, b)
    | Mul (a, b)
    | Concat (a, b)
    | Eq (a, b)
    | Lt (a, b)
    | And (a, b)
    | Or (a, b)
    | Xor (a, b) -> refs a @ refs b
    | Mux (c, t, f) -> refs c @ refs t @ refs f
    | Slice (s, _, _) -> refs s
    | AsUInt v
    | AsSInt v -> refs v
    | Not v -> refs v
    | Shr (s, _) -> refs s
    | Pad (s, _) -> refs s
    | Reduce (_, v) -> refs v
    | Div (a, b)
    | Rem (a, b) -> refs a @ refs b
    | DynamicShl (v, n)
    | DynamicShr (v, n) -> refs v @ refs n
    // The array itself is a source, like a reg: only the address creates a
    // combinational dependency.
    | MemRead (_, a, _) -> refs a

/// True when evaluating the expression touches a value wider than 64 bits —
/// the test that routes an assignment onto the BigInteger path. Checked at
/// every node, so a narrow result computed through a wide intermediate still
/// routes wide.
let rec private touchesWide expr =
    width expr > 64
    || match expr with
       | Lit _
       | Ref _ -> false
       | Add (a, b)
       | Sub (a, b)
       | Mul (a, b)
          | Concat (a, b)
       | Eq (a, b)
       | Lt (a, b)
          | And (a, b)
       | Or (a, b)
       | Xor (a, b) -> touchesWide a || touchesWide b
       | Mux (c, t, f) -> touchesWide c || touchesWide t || touchesWide f
       | Slice (s, _, _) -> touchesWide s
       | AsUInt v
       | AsSInt v -> touchesWide v
       | Not v -> touchesWide v
       | Shr (s, _) -> touchesWide s
       | Pad (s, _) -> touchesWide s
       | Reduce (_, v) -> touchesWide v
       | Div (a, b)
       | Rem (a, b) -> touchesWide a || touchesWide b
       | DynamicShl (v, n)
       | DynamicShr (v, n) -> touchesWide v || touchesWide n
       | MemRead (_, a, _) -> touchesWide a

let internal maskB w = (BigInteger.One <<< w) - BigInteger.One

/// A signal's storage slot and width, resolved once — see `Sim.Handle`.
[<Struct>]
type Handle = { Slot: int; Width: int }

/// A two-phase simulator: combinational assigns run in dependency order,
/// register next-values are computed from the settled state and committed
/// together. Narrow signals (≤64 bits) run on uint64; any assignment touching a
/// wider value runs on a parallel BigInteger path (the full-scale pod's 128-bit
/// egress beats), so wide designs pay only where they are wide. Mems stay
/// narrow — no design needs a wide word.
///
/// The design is compiled once at construction: every signal becomes an index
/// into a flat array and every expression becomes a thunk with its widths and
/// masks already folded in. It is still a plain cycle-based interpreter — the
/// reference half of the differential oracle — but a fast enough one to sit
/// under an interactive debugger.
type Sim(design: ModuleDef, ?checkAsserts: bool) =
    let m = flatten design

    // Off unless asked. An assertion costs what its expression costs, on every
    // tick — small against a real design's settle and dominant against a toy —
    // so the choice is made at construction and "off" means the claims are
    // never compiled into the program at all, not that they are skipped.
    let checking = defaultArg checkAsserts false

    let regInits =
        dict
            [ for d in m.decls do
                  match d with
                  // Only the ones reset asserts on. A register with no reset
                  // holds through it, which is the whole point of asking for one.
                  | Reg (n, _, Some init) -> yield n, init
                  | _ -> () ]

    let memShapes =
        dict
            [ for d in m.decls do
                  match d with
                  | Memory(n, aw, w, _, _) -> yield n, (aw, w)
                  | _ -> () ]

    let isReg n = regInits.ContainsKey n

    let combAssigns =
        [ for stmt in m.stmts do
              match stmt with
              | Assign (t, v) when not (isReg t) -> yield t, v
              | Assign _
              | MemWrite _
              | Assert _ -> () ]

    let regAssigns =
        [ for stmt in m.stmts do
              match stmt with
              | Assign (t, v) when isReg t -> yield t, v, touchesWide v
              | Assign _
              | MemWrite _
              | Assert _ -> () ]

    let assertions =
        [ for stmt in m.stmts do
              match stmt with
              | Assert (cond, message) when checking -> yield cond, message
              | Assign _
              | MemWrite _
              | Assert _ -> () ]

    let memWrites =
        [ for stmt in m.stmts do
              match stmt with
              | MemWrite (mem, a, d, e, k) ->
                  let maskTouchesWide = k |> Option.map touchesWide |> Option.defaultValue false

                  yield
                      mem, a, d, e, k, (touchesWide a || touchesWide d || touchesWide e || maskTouchesWide)
              | Assign _
              | Assert _ -> () ]

    // Depth-first topological order: an assign runs after every combinational
    // assign it reads. Registers and inputs are sources; a cycle is an error.
    let ordered =
        let byTarget = dict combAssigns
        let visitState = System.Collections.Generic.Dictionary<string, bool>()
        let order = ResizeArray()

        let rec visit t =
            match visitState.TryGetValue t with
            | true, false -> failwith $"combinational loop through '{t}'"
            | true, true -> ()
            | _ ->
                visitState[t] <- false

                for d in refs byTarget[t] do
                    if byTarget.ContainsKey d then visit d

                order.Add(t, byTarget[t])
                visitState[t] <- true

        for t, _ in combAssigns do
            visit t

        [ for t, v in order -> t, v, touchesWide v ]

    // ---- storage -----------------------------------------------------------
    // Every declared signal gets an integer slot. Narrow values live in a
    // uint64[] and wide ones in a BigInteger[] over the same index space, so a
    // compiled read is an array index and never a string hash.
    let slots = System.Collections.Generic.Dictionary<string, int>()
    let slotWidths = ResizeArray<int>()

    do
        for d in m.decls do
            match d with
            | Input (n, t)
            | Output (n, t)
            | Wire (n, t)
            | Reg (n, t, _) ->
                if not (slots.ContainsKey n) then
                    slots[n] <- slotWidths.Count
                    slotWidths.Add t.Width
            | Memory _ -> ()

    let narrow = Array.zeroCreate<uint64> slotWidths.Count
    let wideVals = Array.create slotWidths.Count BigInteger.Zero

    // Copy propagation. An assign that is exactly another signal at the same
    // width makes its target an *alias* — the two share a slot, so the assign
    // itself disappears and a peek of either name still reads the right value.
    // Flattening turns every instance port into a staging wire of this shape,
    // which is why it is worth a pass: on the 104-lane pod it is most of the
    // program. `ordered` is topological, so a source is canonical before
    // anything aliases to it.
    let canonical = Array.init slotWidths.Count id

    let rec root i =
        if canonical[i] = i then
            i
        else
            let r = root canonical[i]
            canonical[i] <- r
            r

    let aliasedTargets = System.Collections.Generic.HashSet<string>()

    do
        for t, v, _ in ordered do
            match v with
            | Ref (s, _) when
                slots.ContainsKey t
                && slots.ContainsKey s
                && slotWidths[slots[t]] = slotWidths[slots[s]]
                ->
                canonical[slots[t]] <- root slots[s]
                aliasedTargets.Add t |> ignore
            | _ -> ()

    let slotFor name =
        match slots.TryGetValue name with
        | true, i -> root i
        | _ -> failwith $"unknown signal '{name}' in {m.name}"

    let memArrays =
        let arrays = System.Collections.Generic.Dictionary<string, uint64[]>()

        for d in m.decls do
            match d with
            | Memory(n, aw, w, init, _) when w <= 64 ->
                let arr = Array.zeroCreate<uint64> (1 <<< aw)

                match init with
                | Some contents -> System.Array.Copy(contents, arr, contents.Length)
                | None -> ()

                arrays[n] <- arr
            | _ -> ()

        arrays

    // Wide memories live in a BigInteger store, mirroring the narrow/wide dual
    // the *signals* have had all along — memories just never got theirs until a
    // 128-bit lane-masked table needed one. Contents (`init`) stay narrow-only:
    // the IR carries them as uint64 words, which cannot fill a wider one.
    let wideMemArrays =
        let arrays = System.Collections.Generic.Dictionary<string, BigInteger[]>()

        for d in m.decls do
            match d with
            | Memory(n, aw, w, init, _) when w > 64 ->
                if Option.isSome init then
                    failwith $"'{n}' is a %d{w}-bit initialized memory — init words are uint64, so contents stop at 64 bits"

                arrays[n] <- Array.create (1 <<< aw) BigInteger.Zero
            | _ -> ()

        arrays

    let isWideMem (mem: string) = wideMemArrays.ContainsKey mem

    // ---- the compiler ------------------------------------------------------
    // Each expression is walked ONCE, here, and becomes a thunk closed over the
    // slot arrays with its widths, masks and indices already resolved. The
    // evaluator this replaced called `width` — itself a recursive walk of the
    // subtree — at every arithmetic node on every evaluation, which made
    // evaluation superlinear in expression size and hit the deep combinational
    // cones (a reduce tree, a barrel lane) hardest.

    /// Sign-extension of a w-bit pattern to 64 bits, masks folded in.
    let sextAt w : uint64 -> uint64 =
        if w >= 64 then
            id
        else
            let signBit = 1UL <<< (w - 1)
            let high = ~~~(maskOf w)
            fun v -> if v &&& signBit <> 0UL then v ||| high else v

    /// The same, on the wide path: the w-bit pattern read as a signed value.
    let sextAtB w : BigInteger -> BigInteger =
        let full = BigInteger.One <<< w

        fun v ->
            if (v >>> (w - 1)) &&& BigInteger.One = BigInteger.One then
                v - full
            else
                v

    let rec compileNarrow expr : unit -> uint64 =
        match expr with
        | Lit (v, _) -> fun () -> v
        | Ref (n, _) ->
            let i = slotFor n
            fun () -> narrow[i]
        | MemRead (mem, a, _) ->
            if isWideMem mem then
                // Unreachable by construction: a wide read's width routes the
                // whole expression to the wide path. Said in words anyway,
                // because the alternative is a KeyNotFoundException.
                failwith $"narrow compile reached wide mem '{mem}'"

            let arr = memArrays[mem]
            let aw, _ = memShapes[mem]
            let addrMask = maskOf aw
            let ca = compileNarrow a
            fun () -> arr[int (ca () &&& addrMask)]
        | Add (a, b) ->
            let mask = maskOf (max (width a) (width b))
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> (ca () + cb ()) &&& mask
        | Sub (a, b) ->
            let mask = maskOf (max (width a) (width b))
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> (ca () - cb ()) &&& mask
        | Mul (a, b) when isSigned a ->
            let sa, sb = sextAt (width a), sextAt (width b)
            let mask = maskOf (width a + width b)
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> (sa (ca ()) * sb (cb ())) &&& mask
        | Mul (a, b) ->
            let mask = maskOf (width a + width b)
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> (ca () * cb ()) &&& mask
        | Mux (c, t, f) ->
            let cc, ct, cf = compileNarrow c, compileNarrow t, compileNarrow f
            fun () -> if cc () <> 0UL then ct () else cf ()
        | Concat (hi, lo) ->
            let shift = width lo
            let chi, clo = compileNarrow hi, compileNarrow lo
            fun () -> (chi () <<< shift) ||| clo ()
        | Slice (s, hi, lo) ->
            let mask = maskOf (hi - lo + 1)
            let cs = compileNarrow s
            fun () -> (cs () >>> lo) &&& mask
        | Eq (a, b) ->
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> if ca () = cb () then 1UL else 0UL
        | Lt (a, b) when isSigned a ->
            let s = sextAt (width a)
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> if int64 (s (ca ())) < int64 (s (cb ())) then 1UL else 0UL
        | Lt (a, b) ->
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> if ca () < cb () then 1UL else 0UL
        // Reinterpretation moves no bits, so it compiles to its operand.
        | AsUInt v
        | AsSInt v -> compileNarrow v
        | And (a, b) ->
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> ca () &&& cb ()
        | Or (a, b) ->
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> ca () ||| cb ()
        | Xor (a, b) ->
            let ca, cb = compileNarrow a, compileNarrow b
            fun () -> ca () ^^^ cb ()
        | Not v ->
            let mask = maskOf (width v)
            let cv = compileNarrow v
            fun () -> ~~~(cv ()) &&& mask
        // A narrowing shift is a part-select of the surviving high bits, and
        // that is true whichever way the value reads — the sign rides along.
        | Shr (s, n) ->
            let mask = maskOf (width expr)
            let cs = compileNarrow s
            fun () -> (cs () >>> n) &&& mask
        // Truncating toward zero, which is what Verilog and FIRRTL both mean.
        // Division by zero is undefined in FIRRTL and X in Verilog; zero is what
        // Verilator produces, and agreeing with the tool the differential checks
        // against is worth more here than agreeing with a silence.
        | Div (a, b) when isSigned a ->
            let wa = width a
            let sx = sextAt wa
            let mask = maskOf (width expr)
            let ca, cb = compileNarrow a, compileNarrow b

            fun () ->
                let d = int64 (sx (cb ()))
                if d = 0L then 0UL else uint64 (int64 (sx (ca ())) / d) &&& mask
        | Div (a, b) ->
            let mask = maskOf (width expr)
            let ca, cb = compileNarrow a, compileNarrow b

            fun () ->
                let d = cb ()
                if d = 0UL then 0UL else (ca () / d) &&& mask
        | Rem (a, b) when isSigned a ->
            let wa = width a
            let sx = sextAt wa
            let mask = maskOf (width expr)
            let ca, cb = compileNarrow a, compileNarrow b

            fun () ->
                let d = int64 (sx (cb ()))
                if d = 0L then 0UL else uint64 (int64 (sx (ca ())) % d) &&& mask
        | Rem (a, b) ->
            let mask = maskOf (width expr)
            let ca, cb = compileNarrow a, compileNarrow b

            fun () ->
                let d = cb ()
                if d = 0UL then 0UL else (ca () % d) &&& mask
        | Reduce (kind, v) ->
            let mask = maskOf (width v)
            let cv = compileNarrow v

            match kind with
            | AllBits -> fun () -> if cv () = mask then 1UL else 0UL
            | AnyBit -> fun () -> if cv () <> 0UL then 1UL else 0UL
            | Parity -> fun () -> uint64 (System.Numerics.BitOperations.PopCount(cv ()) &&& 1)
        // A shift by more than the width is not undefined here the way F#'s
        // `<<<` is — it is zero, or all sign bits for an arithmetic shift, which
        // is what the hardware does and what Verilog says.
        | DynamicShl (v, n) ->
            let mask = maskOf (width expr)
            let w = width expr
            let cv, cn = compileNarrow v, compileNarrow n

            fun () ->
                let k = int (cn ())
                if k >= w then 0UL else (cv () <<< k) &&& mask
        | DynamicShr (v, n) when isSigned v ->
            let w = width v
            let sx = sextAt w
            let mask = maskOf w
            let cv, cn = compileNarrow v, compileNarrow n

            fun () ->
                let k = int (cn ())
                let signed = int64 (sx (cv ()))
                let shifted = if k >= 64 then (if signed < 0L then -1L else 0L) else signed >>> k
                uint64 shifted &&& mask
        | DynamicShr (v, n) ->
            let mask = maskOf (width v)
            let cv, cn = compileNarrow v, compileNarrow n

            fun () ->
                let k = int (cn ())
                if k >= 64 then 0UL else (cv () >>> k) &&& mask
        | Pad (s, tw) when isSigned s ->
            let sx = sextAt (width s)
            let mask = maskOf tw
            let cs = compileNarrow s
            fun () -> sx (cs ()) &&& mask
        | Pad (s, tw) ->
            let mask = maskOf tw
            let cs = compileNarrow s
            fun () -> cs () &&& mask

    // A narrow signal read on the wide path lifts; masking on store keeps every
    // held value non-negative, so BigInteger comparisons are the unsigned ones.
    and compileWide expr : unit -> BigInteger =
        match expr with
        | Lit (v, _) ->
            let b = BigInteger v
            fun () -> b
        | Ref (n, _) ->
            let i = slotFor n
            if slotWidths[i] > 64 then fun () -> wideVals[i] else fun () -> BigInteger narrow[i]
        | MemRead (mem, a, _) ->
            let aw, _ = memShapes[mem]
            let addrMask = maskB aw
            let ca = compileWide a

            if isWideMem mem then
                let arr = wideMemArrays[mem]
                fun () -> arr[int (ca () &&& addrMask)]
            else
                let arr = memArrays[mem]
                fun () -> BigInteger(arr[int (ca () &&& addrMask)])
        | Add (a, b) ->
            let mask = maskB (max (width a) (width b))
            let ca, cb = compileWide a, compileWide b
            fun () -> (ca () + cb ()) &&& mask
        | Sub (a, b) ->
            let mask = maskB (max (width a) (width b))
            let ca, cb = compileWide a, compileWide b
            fun () -> (ca () - cb ()) &&& mask
        | Mul (a, b) when isSigned a ->
            let sa, sb = sextAtB (width a), sextAtB (width b)
            let mask = maskB (width a + width b)
            let ca, cb = compileWide a, compileWide b
            fun () -> (sa (ca ()) * sb (cb ())) &&& mask
        | Mul (a, b) ->
            let mask = maskB (width a + width b)
            let ca, cb = compileWide a, compileWide b
            fun () -> (ca () * cb ()) &&& mask
        | Mux (c, t, f) ->
            let cc, ct, cf = compileWide c, compileWide t, compileWide f
            fun () -> if cc () <> BigInteger.Zero then ct () else cf ()
        | Concat (hi, lo) ->
            let shift = width lo
            let chi, clo = compileWide hi, compileWide lo
            fun () -> (chi () <<< shift) ||| clo ()
        | Slice (s, hi, lo) ->
            let mask = maskB (hi - lo + 1)
            let cs = compileWide s
            fun () -> (cs () >>> lo) &&& mask
        | Eq (a, b) ->
            let ca, cb = compileWide a, compileWide b
            fun () -> if ca () = cb () then BigInteger.One else BigInteger.Zero
        | Lt (a, b) when isSigned a ->
            let s = sextAtB (width a)
            let ca, cb = compileWide a, compileWide b
            fun () -> if s (ca ()) < s (cb ()) then BigInteger.One else BigInteger.Zero
        | Lt (a, b) ->
            let ca, cb = compileWide a, compileWide b
            fun () -> if ca () < cb () then BigInteger.One else BigInteger.Zero
        | AsUInt v
        | AsSInt v -> compileWide v
        | And (a, b) ->
            let ca, cb = compileWide a, compileWide b
            fun () -> ca () &&& cb ()
        | Or (a, b) ->
            let ca, cb = compileWide a, compileWide b
            fun () -> ca () ||| cb ()
        | Xor (a, b) ->
            let ca, cb = compileWide a, compileWide b
            fun () -> ca () ^^^ cb ()
        | Not v ->
            // masked complement; BigInteger has no ~~~
            let mask = maskB (width v)
            let cv = compileWide v
            fun () -> cv () ^^^ mask
        | Shr (s, n) ->
            let mask = maskB (width expr)
            let cs = compileWide s
            fun () -> (cs () >>> n) &&& mask
        | Div (a, b) when isSigned a ->
            let sx = sextAtB (width a)
            let mask = maskB (width expr)
            let ca, cb = compileWide a, compileWide b

            fun () ->
                let d = sx (cb ())
                if d.IsZero then BigInteger.Zero else (sx (ca ()) / d) &&& mask
        | Div (a, b) ->
            let mask = maskB (width expr)
            let ca, cb = compileWide a, compileWide b

            fun () ->
                let d = cb ()
                if d.IsZero then BigInteger.Zero else (ca () / d) &&& mask
        | Rem (a, b) when isSigned a ->
            let sx = sextAtB (width a)
            let mask = maskB (width expr)
            let ca, cb = compileWide a, compileWide b

            fun () ->
                let d = sx (cb ())
                if d.IsZero then BigInteger.Zero else (sx (ca ()) % d) &&& mask
        | Rem (a, b) ->
            let mask = maskB (width expr)
            let ca, cb = compileWide a, compileWide b

            fun () ->
                let d = cb ()
                if d.IsZero then BigInteger.Zero else (ca () % d) &&& mask
        | Reduce (kind, v) ->
            let mask = maskB (width v)
            let cv = compileWide v

            match kind with
            | AllBits -> fun () -> if cv () = mask then BigInteger.One else BigInteger.Zero
            | AnyBit -> fun () -> if cv () <> BigInteger.Zero then BigInteger.One else BigInteger.Zero
            | Parity ->
                // Bit by bit, since BigInteger has no popcount.
                let w = width v

                fun () ->
                    let x = cv ()
                    let mutable acc = 0

                    for i in 0 .. w - 1 do
                        if (x >>> i) &&& BigInteger.One = BigInteger.One then acc <- acc ^^^ 1

                    BigInteger acc
        | DynamicShl (v, n) ->
            let mask = maskB (width expr)
            let w = width expr
            let cv, cn = compileWide v, compileWide n

            fun () ->
                let k = int (cn ())
                if k >= w then BigInteger.Zero else (cv () <<< k) &&& mask
        | DynamicShr (v, n) when isSigned v ->
            let w = width v
            let sx = sextAtB w
            let mask = maskB w
            let cv, cn = compileWide v, compileWide n

            fun () ->
                let k = int (cn ())
                let signed = sx (cv ())
                let shifted = if k >= w then (if signed.Sign < 0 then BigInteger.MinusOne else BigInteger.Zero) else signed >>> k
                shifted &&& mask
        | DynamicShr (v, n) ->
            let mask = maskB (width v)
            let w = width v
            let cv, cn = compileWide v, compileWide n

            fun () ->
                let k = int (cn ())
                if k >= w then BigInteger.Zero else (cv () >>> k) &&& mask
        | Pad (s, tw) when isSigned s ->
            let sx = sextAtB (width s)
            let mask = maskB tw
            let cs = compileWide s
            fun () -> sx (cs ()) &&& mask
        | Pad (s, tw) ->
            let mask = maskB tw
            let cs = compileWide s
            fun () -> cs () &&& mask

    /// Land a wide-path result at its target, masked, routed by the target's own
    /// width — a narrow target computed through a wide intermediate lands as
    /// uint64.
    let storeWide target (c: unit -> BigInteger) : unit -> unit =
        let i = slotFor target
        let w = slotWidths[i]
        let mask = maskB w

        if w > 64 then
            fun () -> wideVals[i] <- c () &&& mask
        else
            fun () -> narrow[i] <- uint64 (c () &&& mask)

    /// An assignment compiled to a thunk that evaluates and lands its value.
    let compileAssign target expr onWidePath : unit -> unit =
        if onWidePath then
            storeWide target (compileWide expr)
        else
            let i = slotFor target
            let mask = maskOf slotWidths[i]
            let c = compileNarrow expr
            fun () -> narrow[i] <- c () &&& mask

    // ---- the program -------------------------------------------------------
    let settleOps =
        [| for t, v, onWidePath in ordered do
               if not (aliasedTargets.Contains t) then
                   yield compileAssign t v onWidePath |]

    // Register next-values land in pre-allocated buffers and commit together
    // after every one of them has been evaluated, so a tick allocates nothing.
    let regNarrow = [| for t, v, w in regAssigns do if not w then yield t, v |]
    let regNarrowSlots = regNarrow |> Array.map (fst >> slotFor)
    let regNarrowNext = Array.zeroCreate<uint64> regNarrow.Length

    let regNarrowOps =
        regNarrow
        |> Array.mapi (fun k (_, v) ->
            let mask = maskOf slotWidths[regNarrowSlots[k]]
            let c = compileNarrow v
            fun () -> regNarrowNext[k] <- c () &&& mask)

    let regWide = [| for t, v, w in regAssigns do if w then yield t, v |]
    let regWideNext = Array.create regWide.Length BigInteger.Zero

    let regWideOps =
        regWide
        |> Array.mapi (fun k (_, v) ->
            let c = compileWide v
            fun () -> regWideNext[k] <- c ())

    let regWideCommit =
        regWide
        |> Array.mapi (fun k (t, _) ->
            let i = slotFor t
            let mask = maskB slotWidths[i]

            if slotWidths[i] > 64 then
                fun () -> wideVals[i] <- regWideNext[k] &&& mask
            else
                fun () -> narrow[i] <- uint64 (regWideNext[k] &&& mask))

    // Mem writes evaluate against the same pre-edge state and land after the
    // registers, which is what makes a same-cycle read of a written address
    // read-first.
    let writeTargets =
        [| for mem, _, _, _, _, _ in memWrites -> (if isWideMem mem then null else memArrays[mem]) |]

    let writeTargetsWide =
        [| for mem, _, _, _, _, _ in memWrites -> (if isWideMem mem then wideMemArrays[mem] else null) |]

    let writeAddr = Array.zeroCreate<int> memWrites.Length
    let writeData = Array.zeroCreate<uint64> memWrites.Length
    let writeDataWide = Array.create memWrites.Length BigInteger.Zero
    let writeEnabled = Array.zeroCreate<bool> memWrites.Length
    // Which bits of the word this write reaches. All of them unless the write
    // carries a lane mask, and then it is rebuilt each cycle from the mask's
    // value — the same word-shaped result the Verilog reaches lane by lane.
    let writeKeep = Array.create memWrites.Length System.UInt64.MaxValue
    let writeKeepWide = Array.create memWrites.Length BigInteger.MinusOne

    let memWriteOps =
        memWrites
        |> List.mapi (fun k (mem, a, d, e, maskExpr, onWidePath) ->
            let aw, w = memShapes[mem]

            // One bit per lane expanded into a word-shaped keep pattern: lane i
            // set means its `laneWidth` bits survive.
            let keepOf =
                match maskExpr with
                | None -> fun () -> System.UInt64.MaxValue
                | Some mk ->
                    let lanes = width mk
                    let laneWidth = w / lanes
                    let laneBits = maskOf laneWidth
                    let cm = compileNarrow mk

                    fun () ->
                        let bits = cm ()
                        let mutable keep = 0UL

                        for i in 0 .. lanes - 1 do
                            if (bits >>> i) &&& 1UL = 1UL then
                                keep <- keep ||| (laneBits <<< (i * laneWidth))

                        keep

            if isWideMem mem then
                // A wide mem's data is wide by definition, so the whole write
                // compiles on the wide path; the mask itself stays narrow (one
                // bit per lane) and only its *expansion* is wide.
                let keepOfWide =
                    match maskExpr with
                    | None -> fun () -> BigInteger.MinusOne
                    | Some mk ->
                        let lanes = width mk
                        let laneWidth = w / lanes
                        let laneBits = maskB laneWidth
                        let cm = compileNarrow mk

                        fun () ->
                            let bits = cm ()
                            let mutable keep = BigInteger.Zero

                            for i in 0 .. lanes - 1 do
                                if (bits >>> i) &&& 1UL = 1UL then
                                    keep <- keep ||| (laneBits <<< (i * laneWidth))

                            keep

                let ca, cd, ce = compileWide a, compileWide d, compileWide e
                let addrMask, dataMask = maskB aw, maskB w

                fun () ->
                    if ce () <> BigInteger.Zero then
                        writeEnabled[k] <- true
                        writeAddr[k] <- int (ca () &&& addrMask)
                        writeDataWide[k] <- cd () &&& dataMask
                        writeKeepWide[k] <- keepOfWide ()
                    else
                        writeEnabled[k] <- false
            elif onWidePath then
                let ca, cd, ce = compileWide a, compileWide d, compileWide e
                let addrMask, dataMask = maskB aw, maskB w

                fun () ->
                    if ce () <> BigInteger.Zero then
                        writeEnabled[k] <- true
                        writeAddr[k] <- int (ca () &&& addrMask)
                        writeData[k] <- uint64 (cd () &&& dataMask)
                        writeKeep[k] <- keepOf ()
                    else
                        writeEnabled[k] <- false
            else
                let ca, cd, ce = compileNarrow a, compileNarrow d, compileNarrow e
                let addrMask, dataMask = maskOf aw, maskOf w

                fun () ->
                    if ce () <> 0UL then
                        writeEnabled[k] <- true
                        writeAddr[k] <- int (ca () &&& addrMask)
                        writeData[k] <- cd () &&& dataMask
                        writeKeep[k] <- keepOf ()
                    else
                        writeEnabled[k] <- false)
        |> Array.ofList

    let settle () =
        for k in 0 .. settleOps.Length - 1 do
            settleOps[k] ()

    // Claims compiled like any other expression — an assertion is a breakpoint
    // the design carries with it.
    let assertOps =
        [| for cond, message in assertions ->
               (if touchesWide cond then
                    let c = compileWide cond
                    fun () -> c () <> BigInteger.Zero
                 else
                    let c = compileNarrow cond
                    fun () -> c () <> 0UL),
               message |]

    let violations = ResizeArray<string * int>()
    let mutable ticks = 0

    /// Checked after the edge, against settled state — the same instant the
    /// emitted `always @(posedge clk)` block checks it. A claim is recorded
    /// once per cycle it fails; a harness decides whether that is fatal.
    let checkClaims () =
        for holds, message in assertOps do
            if not (holds ()) then violations.Add(message, ticks)

    /// Poking marks the combinational state stale rather than re-settling on the
    /// spot: a harness that pokes five pins before a tick then settles once, not
    /// five times. Every read path settles first, so this is invisible.
    let mutable stale = false

    let ensureSettled () =
        if stale then
            settle ()
            stale <- false

    do
        for d in m.decls do
            match d with
            | Reg (n, t, init) ->
                // At construction an unreset register starts at zero, which is
                // what Verilator does with an uninitialised reg and so keeps the
                // differential comparing like with like. FIRRTL calls it
                // undefined; agreeing with the tool we check against is worth
                // more here than agreeing with the spec's silence.
                let start = Option.defaultValue 0UL init
                let i = slotFor n
                if t.Width > 64 then wideVals[i] <- BigInteger start else narrow[i] <- start
            | _ -> ()

        settle ()

    member _.Poke(name, value: uint64) =
        let i = slotFor name

        if slotWidths[i] > 64 then
            failwith $"'{name}' is %d{slotWidths[i]} bits — poke it with PokeWide"

        narrow[i] <- value &&& maskOf slotWidths[i]
        stale <- true

    member _.PokeWide(name, value: BigInteger) =
        let i = slotFor name
        let mask = maskB slotWidths[i]

        if slotWidths[i] > 64 then
            wideVals[i] <- value &&& mask
        else
            narrow[i] <- uint64 (value &&& mask)

        stale <- true

    member _.Peek(name: string) =
        let i = slotFor name

        if slotWidths[i] > 64 then
            failwith $"'{name}' is %d{slotWidths[i]} bits — peek it with PeekWide"

        ensureSettled ()
        narrow[i]

    member _.PeekWide(name: string) : BigInteger =
        let i = slotFor name
        ensureSettled ()
        if slotWidths[i] > 64 then wideVals[i] else BigInteger narrow[i]

    /// The host backdoor into a mem, by flattened name — warp11's peekMem.
    member _.PeekMem(name: string, index: int) =
        if isWideMem name then
            failwith $"'{name}' words are wider than 64 bits — PeekMemWide"

        memArrays[name][index]

    member _.PeekMemWide(name: string, index: int) : BigInteger =
        if isWideMem name then wideMemArrays[name][index] else BigInteger(memArrays[name][index])

    /// A signal's storage slot, resolved once. `PeekAt` on a handle skips the
    /// name lookup, which is what a watch table reading the same signals every
    /// cycle wants.
    member _.Handle(name: string) : Handle =
        let i = slotFor name
        { Slot = i; Width = slotWidths[i] }

    member _.PeekAt(h: Handle) : uint64 =
        ensureSettled ()
        narrow[h.Slot]

    member _.PeekWideAt(h: Handle) : BigInteger =
        ensureSettled ()
        if h.Width > 64 then wideVals[h.Slot] else BigInteger narrow[h.Slot]

    member _.PokeAt(h: Handle, value: uint64) =
        narrow[h.Slot] <- value &&& maskOf h.Width
        stale <- true

    /// The width of a declared signal, and the (addrWidth, width) of a mem —
    /// what an expression layer needs to type its operands.
    member _.TryWidth(name: string) =
        match slots.TryGetValue name with
        | true, i -> Some slotWidths[i]
        | _ -> None

    member _.TryMemShape(name: string) =
        match memShapes.TryGetValue name with
        | true, shape -> Some shape
        | _ -> None

    /// Compile an expression over this design's signals into a predicate — the
    /// seam a breakpoint binds to, so testing one costs a thunk call per cycle
    /// rather than a walk. Routed onto the wide path by the same rule as an
    /// assignment.
    member _.CompilePredicate(expr: Expr) : unit -> bool =
        if touchesWide expr then
            let c = compileWide expr

            fun () ->
                ensureSettled ()
                c () <> BigInteger.Zero
        else
            let c = compileNarrow expr

            fun () ->
                ensureSettled ()
                c () <> 0UL

    member _.Reset() =
        for KeyValue (n, init) in regInits do
            let i = slotFor n
            if slotWidths[i] > 64 then wideVals[i] <- BigInteger init else narrow[i] <- init

        // Initialized mems reload — reset models reconfiguration, and a BRAM
        // INIT comes back with the bitstream. Uninitialized mems keep their
        // contents, mirroring BRAM through an ordinary reset.
        for d in m.decls do
            match d with
            | Memory(n, _, _, Some contents, _) ->
                let arr = memArrays[n]
                System.Array.Clear arr
                System.Array.Copy(contents, arr, contents.Length)
            | _ -> ()

        settle ()
        stale <- false

    member _.Tick() =
        // Reg next-values and write operands both evaluate against the pre-edge
        // state, then regs commit, then writes land — so a sync read of an
        // address written this cycle captures the OLD value: read-first, matching
        // the emitted always block.
        ensureSettled ()

        for k in 0 .. regNarrowOps.Length - 1 do
            regNarrowOps[k] ()

        for k in 0 .. regWideOps.Length - 1 do
            regWideOps[k] ()

        for k in 0 .. memWriteOps.Length - 1 do
            memWriteOps[k] ()

        for k in 0 .. regNarrowSlots.Length - 1 do
            narrow[regNarrowSlots[k]] <- regNarrowNext[k]

        for k in 0 .. regWideCommit.Length - 1 do
            regWideCommit[k] ()

        for k in 0 .. writeEnabled.Length - 1 do
            if writeEnabled[k] then
                if isNull (box writeTargetsWide[k]) then
                    let keep = writeKeep[k]

                    if keep = System.UInt64.MaxValue then
                        writeTargets[k][writeAddr[k]] <- writeData[k]
                    else
                        let old = writeTargets[k][writeAddr[k]]
                        writeTargets[k][writeAddr[k]] <- (writeData[k] &&& keep) ||| (old &&& ~~~keep)
                else
                    let arr = writeTargetsWide[k]
                    let keep = writeKeepWide[k]

                    if keep = BigInteger.MinusOne then
                        arr[writeAddr[k]] <- writeDataWide[k]
                    else
                        let old = arr[writeAddr[k]]
                        arr[writeAddr[k]] <- (writeDataWide[k] &&& keep) ||| (old &&& (BigInteger.MinusOne ^^^ keep))

        settle ()
        ticks <- ticks + 1

        if assertOps.Length > 0 then checkClaims ()

    /// Every assertion failure so far, as (message, cycle). Empty is the design
    /// keeping its promises — or a Sim built without `checkAsserts`.
    member _.Violations = List.ofSeq violations

    /// The count and the newest entry, without building a list — what a run
    /// loop asks after every tick.
    member _.ViolationCount = violations.Count

    member _.LastViolation =
        if violations.Count = 0 then
            None
        else
            Some violations[violations.Count - 1]

    member _.ClearViolations() = violations.Clear()

/// The DDR side of an AXI4 write master at the design boundary — the Sim's
/// fake DDR. Always-ready AW and W channels, responses issued in order, and
/// strobed bytes landing in a backing array that a host-side check (or a
/// full-frame render) reads back. Drive it one `Cycle()` per clock: it samples
/// the master's pre-edge outputs, answers the slave-side pins, and ticks the
/// Sim. AW and W arrive on independent queues and pair in order, so a master
/// that runs its channels apart is served correctly.
type SimAxiWriteSlave
    (sim: Sim,
     memBytes: int,
     ?prefix: string,
     ?dataBytes: int,
     ?awEvery: int,
     ?wEvery: int,
     ?bDelay: int,
     ?memory: byte[],
     ?jitter: int) =
    let p = defaultArg prefix "m_axi"
    let lanes = defaultArg dataBytes 16
    // Channel pacing: accept AW every awEvery-th cycle, W every wEvery-th,
    // respond B bDelay cycles after the write pairs. The defaults (1, 1, 0)
    // are the old always-ready slave; anything else skews the channels
    // independently, the way a real interconnect does — the always-ready
    // slave structurally cannot exercise the master's stall paths.
    let awN = max 1 (defaultArg awEvery 1)
    let wN = max 1 (defaultArg wEvery 1)
    let bD = max 0 (defaultArg bDelay 0)
    // Seeded, so a failure is reproducible from the seed alone.
    let rng = jitter |> Option.map System.Random
    let mutable awStall = 0
    let mutable wStall = 0
    // A shared array makes this the write half of one DDR (see `SimAxiDdr`);
    // on its own it owns `memBytes` of its own.
    let memory = defaultArg memory (Array.zeroCreate<byte> memBytes)
    let pendingAddr = System.Collections.Generic.Queue<uint64>()
    let pendingData = System.Collections.Generic.Queue<BigInteger * uint64>()
    let bDue = System.Collections.Generic.Queue<int>()
    let mutable cycleCount = 0
    let mutable awReadyNow = true
    let mutable wReadyNow = true

    do
        sim.Poke($"{p}_awready", 1UL)
        sim.Poke($"{p}_wready", 1UL)

    member _.Memory = memory

    /// Everything this slave does before the clock edge: capture AW/W, pair
    /// them into memory, and present B. Split out of `Cycle` so a shared-memory
    /// read/write pair can each take their phase and then tick ONCE.
    member _.Capture() =
        if awReadyNow && sim.Peek $"{p}_awvalid" = 1UL then
            pendingAddr.Enqueue(sim.Peek $"{p}_awaddr")

        if wReadyNow && sim.Peek $"{p}_wvalid" = 1UL then
            let data =
                if lanes > 8 then sim.PeekWide $"{p}_wdata" else BigInteger(sim.Peek $"{p}_wdata")

            pendingData.Enqueue(data, sim.Peek $"{p}_wstrb")

        while pendingAddr.Count > 0 && pendingData.Count > 0 do
            let addr = int (pendingAddr.Dequeue())
            let data, strb = pendingData.Dequeue()

            for lane in 0 .. lanes - 1 do
                if (strb >>> lane) &&& 1UL = 1UL then
                    memory[addr + lane] <- byte ((data >>> (lane * 8)) &&& BigInteger(255))

            bDue.Enqueue(cycleCount + bD)

        // The B the upcoming tick sees; if the design's bready is high it is
        // consumed — decided before the poke, dequeued after, so a due
        // response is never eaten unseen.
        let bShown = bDue.Count > 0 && bDue.Peek() <= cycleCount

        if bShown && sim.Peek $"{p}_bready" = 1UL then
            bDue.Dequeue() |> ignore

        sim.Poke($"{p}_bvalid", (if bShown then 1UL else 0UL))

    /// Readies for the NEXT tick — poked after the edge, so the values the
    /// design acts on always equal the values this harness captured with.
    member _.Pace() =
        cycleCount <- cycleCount + 1
        // Accept, then stall 0-3 cycles — independently per channel, because
        // AW and W are independent and a master must not assume they move
        // together.
        let pace (stall: int) (fixedN: int) =
            match rng with
            | Some r -> (if stall > 0 then stall - 1, false else r.Next(0, 4), true)
            | None -> stall, cycleCount % fixedN = 0

        let aw, awR = pace awStall awN
        let w, wR = pace wStall wN
        awStall <- aw
        wStall <- w
        awReadyNow <- awR
        wReadyNow <- wR
        sim.Poke($"{p}_awready", (if awReadyNow then 1UL else 0UL))
        sim.Poke($"{p}_wready", (if wReadyNow then 1UL else 0UL))

    member this.Cycle() =
        this.Capture()
        sim.Tick()
        this.Pace()

/// The behavioral DDR for the AXI *read* master — `SimAxiWriteSlave`'s
/// mirror. AR accepted every `arEvery`-th cycle; a burst's first beat becomes
/// available `rDelay` cycles after its AR; INCR addressing, RLAST on the
/// final beat; a presented beat holds until the master takes it
/// (rvalid && rready), so backpressure is honored.
///
/// The cycle is two-phase because a burst master's resp side is a
/// combinational passthrough of the R channel: `BeginCycle()` presents this
/// cycle's R beat (pokes rvalid/rdata/rlast and settles), the harness then
/// peeks whatever design outputs it wants to record, and `FinishCycle()`
/// makes the consume/accept decisions against the settled state, ticks, and
/// paces ARREADY for the next cycle. `Cycle()` is the two glued together for
/// harnesses that don't observe mid-cycle.
type SimAxiReadSlave
    (sim: Sim,
     memBytes: int,
     ?prefix: string,
     ?dataBytes: int,
     ?arEvery: int,
     ?rDelay: int,
     ?memory: byte[],
     ?jitter: int) =
    let p = defaultArg prefix "m_axi"
    let lanes = defaultArg dataBytes 4
    let arN = max 1 (defaultArg arEvery 1)
    let rD = max 0 (defaultArg rDelay 0)
    // Seeded so a failure is reproducible: the seed is the whole state.
    let rng = jitter |> Option.map System.Random
    let mutable arStall = 0

    /// A read latency drawn per burst rather than fixed. Real memory does not
    /// answer at a constant offset, and a design that only ever meets one is a
    /// design whose correctness nobody has tested.
    let drawDelay () =
        match rng with
        | Some r -> r.Next(0, 4)
        | None -> rD
    let memory = defaultArg memory (Array.zeroCreate<byte> memBytes)
    // Pending bursts in AR order: address of the next beat, beats left, and
    // the cycle the first beat becomes available.
    let bursts = System.Collections.Generic.Queue<uint64 * int * int>()
    let mutable cycleCount = 0
    let mutable arReadyNow = true
    let mutable presented = false

    do
        sim.Poke($"{p}_arready", 1UL)
        sim.Poke($"{p}_rvalid", 0UL)
        sim.Poke($"{p}_rresp", 0UL)

    member _.Memory = memory

    member _.BeginCycle() =
        presented <- bursts.Count > 0 && (let (_, _, due) = bursts.Peek() in due <= cycleCount)

        if presented then
            let addr, beatsLeft, _ = bursts.Peek()
            let mutable value = BigInteger.Zero

            for lane in lanes - 1 .. -1 .. 0 do
                value <- (value <<< 8) ||| BigInteger(int memory[int addr + lane])

            if lanes > 8 then
                sim.PokeWide($"{p}_rdata", value)
            else
                sim.Poke($"{p}_rdata", uint64 value)

            sim.Poke($"{p}_rlast", (if beatsLeft = 1 then 1UL else 0UL))

        sim.Poke($"{p}_rvalid", (if presented then 1UL else 0UL))

    /// The pre-edge half of `FinishCycle`: consume this cycle's R beat if the
    /// master took it, and capture a new AR. Split out for the same reason as
    /// the write slave's `Capture`.
    member _.Consume() =
        if presented && sim.Peek $"{p}_rready" = 1UL then
            let addr, beatsLeft, due = bursts.Dequeue()

            if beatsLeft > 1 then
                // Re-present the rest of the burst at the head — R beats of
                // one transaction stay contiguous and in order.
                let rest = [ uint64 (int addr + lanes), beatsLeft - 1, due ] @ List.ofSeq bursts
                bursts.Clear()
                for b in rest do bursts.Enqueue b

        if arReadyNow && sim.Peek $"{p}_arvalid" = 1UL then
            let beats = int (sim.Peek $"{p}_arlen") + 1
            bursts.Enqueue(sim.Peek $"{p}_araddr", beats, cycleCount + drawDelay ())

    member _.Pace() =
        cycleCount <- cycleCount + 1

        arReadyNow <-
            match rng with
            | Some r ->
                // Accept, then stall 0-3 cycles, then accept again.
                if arStall > 0 then
                    arStall <- arStall - 1
                    false
                else
                    arStall <- r.Next(0, 4)
                    true
            | None -> cycleCount % arN = 0

        sim.Poke($"{p}_arready", (if arReadyNow then 1UL else 0UL))

    member this.FinishCycle() =
        this.Consume()
        sim.Tick()
        this.Pace()

    member this.Cycle() =
        this.BeginCycle()
        this.FinishCycle()

/// One behavioral DDR behind BOTH master channels: the read and write slaves
/// over a single backing array, phased so the pair advances the design by one
/// tick rather than two. What a design that reads work items and writes its
/// results back to the same region needs in order to be observed at all — the
/// two slaves used side by side would tick twice per cycle and each see half
/// of memory.
type SimAxiDdr
    (sim: Sim,
     memBytes: int,
     ?prefix: string,
     ?arEvery: int,
     ?rDelay: int,
     ?awEvery: int,
     ?wEvery: int,
     ?bDelay: int,
     ?jitter: int) =
    let memory = Array.zeroCreate<byte> memBytes
    let p = defaultArg prefix "m_axi"

    let rd =
        SimAxiReadSlave(
            sim,
            memBytes,
            prefix = p,
            dataBytes = 16,
            arEvery = defaultArg arEvery 1,
            rDelay = defaultArg rDelay 0,
            memory = memory,
            ?jitter = jitter
        )

    let wr =
        SimAxiWriteSlave(
            sim,
            memBytes,
            prefix = p,
            dataBytes = 16,
            awEvery = defaultArg awEvery 1,
            wEvery = defaultArg wEvery 1,
            bDelay = defaultArg bDelay 0,
            memory = memory,
            // A different stream from the read side's, so the two channels are
            // not accidentally correlated by sharing one generator.
            ?jitter = (jitter |> Option.map (fun j -> j * 2 + 1))
        )

    member _.Memory = memory

    /// Present R, capture AW/W and AR, then one clock edge, then re-pace both
    /// sets of readies.
    member _.Cycle() =
        rd.BeginCycle()
        wr.Capture()
        rd.Consume()
        sim.Tick()
        rd.Pace()
        wr.Pace()

    /// Little-endian word access into the backing store, for staging work
    /// items and reading results back.
    member _.ReadWord(byteAddr: int) =
        (uint32 memory[byteAddr])
        ||| (uint32 memory[byteAddr + 1] <<< 8)
        ||| (uint32 memory[byteAddr + 2] <<< 16)
        ||| (uint32 memory[byteAddr + 3] <<< 24)

    member _.WriteWord(byteAddr: int, value: uint32) =
        memory[byteAddr] <- byte value
        memory[byteAddr + 1] <- byte (value >>> 8)
        memory[byteAddr + 2] <- byte (value >>> 16)
        memory[byteAddr + 3] <- byte (value >>> 24)
