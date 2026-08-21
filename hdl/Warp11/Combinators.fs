/// The small shapes that turned up in more than one place. Compiled before the
/// stream layer so that everything above the IR can reach them — which is the
/// whole point: a helper nobody downstream can call gets written again.
[<AutoOpen>]
module Warp11.Combinators

/// A signal put through `stages` registers — control that has to arrive
/// alongside a result the pipeline is still computing. The registers are named
/// `{name}_d1`..`{name}_dN`, so the delayed copy of `turn` is findable in a
/// waveform beside the thing it describes.
///
/// Unlike `delayOf`, this is inline logic rather than an instantiated module:
/// the delay of a *control* signal belongs in the module whose control it is,
/// and a per-stage `Delay1` instance would put the pipeline's shape in the
/// instance list instead.
let delayChain (name: string) (width: int) (stages: int) (source: Expr) =
    if stages < 0 then
        failwith $"delayChain '{name}' by %d{stages} stages"

    (source, [ 1..stages ])
    ||> List.fold (fun current stage ->
        let r = reg $"{name}_d%d{stage}" width
        current ==> r
        r)

/// How many cycles `memReadPort` takes to answer — a property of the memory
/// rather than of any particular read, so something that has to be *sized*
/// around a read can ask before elaborating one. The AXI-Lite channel does
/// exactly that: it has to know how long to wait before raising the registers
/// that do the waiting, and it cannot elaborate a port to find out because the
/// port needs the address the channel has not held yet.
///
/// One, for every storage there is. It stops being one the day a port is backed
/// by something slower than a block, and everything that asks will follow.
let memReadDepth (_: Mem) = 1

/// What `memReadPort` hands back: the word, how late it is, and a way to
/// carry anything else across the same distance.
type MemReadPort =
    { /// The word, `depth` cycles after the address was presented.
      data: Expr
      /// How many cycles late `data` is. Read it rather than assuming one —
      /// that is what makes this a port rather than a value.
      depth: int
      /// Carry a signal across the read so it arrives beside `data`. The
      /// registers are named by the caller, so a waveform still reads the way
      /// it did.
      through: string -> Expr -> Expr }

/// A memory read that **owns its own latency**, and carries whatever the read
/// was about through it.
///
/// `memReadNextCycle` hands back a word one cycle late and leaves the caller to
/// remember what it asked for. Every site in this tree that had to remember was
/// writing the same registers by hand — GoL's prefetch delays both the address
/// and its valid to meet the data, GEP's lane fetch delays the lane selector —
/// and each one had to know the number 1 to do it.
///
/// ```fsharp
/// let read = memReadPort loadMem prefetchAddr
/// let dataIndex = read.through "prefetch_data_index" prefetchAddr
/// let dataValid = read.through "prefetch_data_valid" prefetching
/// // read.data, dataIndex and dataValid are all about the same read
/// ```
///
/// **`through` delays by the port's depth, whatever the port's depth is.** That
/// is the whole of it: a hand-written `reg` states the latency, so it is wrong
/// the first time the read changes, and nothing anywhere would say so. Depth
/// lives in one place and every carried signal follows it — the same rule that
/// makes `reduceTreePipelined` return its depth rather than take one.
///
/// This is `withContext`'s bargain at a scale that does not want a FIFO: no
/// handshake, no backpressure, one register per carried signal, and the caller
/// names them so a waveform still reads the way it did.
let memReadPort (m: Mem) (addr: Expr) : MemReadPort =
    let data = memReadNextCycle m addr
    let depth = memReadDepth m

    { data = data
      depth = depth
      through = fun name signal -> delayChain name (width signal) depth signal }

/// Select from a list by index — a mux network over live values, which is what
/// a row of per-slot registers wants (a mem would add a read cycle and a write
/// port). Index 0 is the fall-through arm, so an out-of-range selector reads as
/// element 0 rather than as nothing.
///
/// The selector's own width sizes the comparison literals; a list shorter than
/// the selector's range simply leaves the high codes on element 0.
let selectIndexed (sel: Expr) (values: Expr list) =
    if List.isEmpty values then
        failwith "selectIndexed needs at least one value"

    values
    |> List.indexed
    |> List.tail
    |> List.fold (fun acc (i, v) -> mux (eq sel (lit (uint64 i) (width sel))) v acc) (List.head values)

/// The lowest set bit, as a one-hot vector — a priority scan, which is what a
/// row of requesters wants when exactly one may win and index order decides.
/// `[0; 1; 1; 0]` gives `[0; 1; 0; 0]`; all-zero gives all-zero.
///
/// A linear fold, so depth grows with the list. That is right for the handful
/// of slots this shape is used on; `priorityPick` is the balanced-tree form for
/// when the fold becomes the timing path, and it also carries fields along.
let oneHotLowest (bits: Expr list) : Expr list =
    (lit 1UL 1, bits)
    ||> List.mapFold (fun noneLower b -> noneLower &&& b, noneLower &&& bnot b)
    |> fst

/// What `edgeDetect` hands back: the sample, and the three comparisons
/// against it.
type EdgeDetect =
    { /// The sampled copy — a register, updated only while `enable` is high.
      previous: Expr
      /// The signal differs from its sample, either direction.
      changed: Expr
      /// Low to high.
      rising: Expr
      /// High to low.
      falling: Expr }

/// Edge detection on a one-bit signal: `previous` samples it whenever `enable`
/// is high, and the three outputs compare the live signal against that sample.
/// With `enable` tied high this is the plain form; gated, it detects edges *in
/// the enabled domain*, which is what a design sampling on a slower tick wants
/// — an I2S receiver sees LRCLK turn once per frame, not once per fast clock.
///
/// `previous` is a reg and `enable` gates only the sample, so a design that
/// wants the raw delayed copy can read it.
let edgeDetect (name: string) (enable: Expr) (signal: Expr) : EdgeDetect =
    if width signal <> 1 then
        failwith $"edgeDetect '{name}' needs a 1-bit signal, got %d{width signal} bits"

    let previous = regBit $"{name}_previous"
    If enable (fun () -> signal ==> previous)

    { previous = previous
      changed = signal ^^^ previous
      rising = signal &&& bnot previous
      falling = bnot signal &&& previous }

/// A maximal-length Galois LFSR: `width` bits shifting one place per `step`,
/// visiting every non-zero state exactly once before repeating — 2^width − 1
/// states. The cheapest pseudo-random source there is (a shift and a masked
/// xor, no multiply, no carry chain), which is why it is the field's default
/// for test stimulus, dithering and traffic generation.
///
/// It is *not* a substitute for `xoshiro128pp` where statistical quality
/// matters: consecutive states share all but one bit, so the low bits are
/// strongly correlated. Use it to stir something, not to sample a distribution.
///
/// Galois form: the state shifts toward the LSB, and the bit shifted out xors
/// a tap mask back in — one xor gate per tap, all in parallel, unlike the
/// Fibonacci form's xor chain into the top bit.
///
/// The polynomials are the standard maximal-length ones; `lfsrPeriodOk` in the
/// design catalog walks the full period of every width offered here and
/// asserts it visits 2^width − 1 distinct states, so a wrong tap mask is a
/// failing check rather than a subtly short sequence.
let lfsrTaps =
    dict [ 2, 0x3UL
           3, 0x5UL
           4, 0x9UL
           5, 0x12UL
           6, 0x21UL
           7, 0x41UL
           8, 0x8EUL
           9, 0x108UL
           10, 0x204UL
           11, 0x402UL
           12, 0x829UL
           13, 0x100DUL
           14, 0x2015UL
           15, 0x4001UL
           16, 0x8016UL
           17, 0x10004UL
           18, 0x20013UL
           19, 0x40013UL
           20, 0x80004UL
           22, 0x200001UL
           24, 0x80000DUL ]

/// One Galois step in software — the reference the hardware is checked against,
/// and what a host-side model uses to predict the sequence.
let lfsrNext (width: int) (state: uint64) =
    let taps =
        match lfsrTaps.TryGetValue width with
        | true, t -> t
        | _ -> failwith $"no maximal-length polynomial recorded for a %d{width}-bit LFSR"

    let shifted = state >>> 1

    if state &&& 1UL = 1UL then
        shifted ^^^ taps
    else
        shifted

/// The LFSR as hardware: returns its state, which advances on `step`. The seed
/// must be non-zero — the all-zero state is the one the sequence cannot leave.
let lfsr (name: string) (width: int) (seed: uint64) (step: Expr) =
    if not (lfsrTaps.ContainsKey width) then
        failwith $"no maximal-length polynomial recorded for a %d{width}-bit LFSR"

    if seed = 0UL || seed >= (1UL <<< width) then
        failwith $"lfsr '{name}' needs a non-zero seed inside %d{width} bits, got %d{seed}"

    let state = regInit name width seed
    let taps = lfsrTaps[width]
    let shifted = wire $"{name}_shifted" width
    cat (lit 0UL 1) (slice (width - 1) 1 state) ==> shifted

    If step (fun () -> mux (slice 0 0 state) (shifted ^^^ lit taps width) shifted ==> state)

    state

// ---------------------------------------------------------------------------
// Width arithmetic, at elaboration. Host-side integers, no hardware: these
// decide how wide a register has to be, and every one of them was written out
// by hand in four or five places before landing here.

/// Bits needed to represent `0 .. n-1` — the width of a counter of `n` things,
/// an index into `n` slots, an address into `n` words. Zero for n <= 1, which is
/// honest but rarely what a declaration wants; `bitsToHold` is the floored form.
let ceilLog2 n =
    let rec go v bits = if v >= n then bits else go (v <<< 1) (bits + 1)
    go 1 0

/// `ceilLog2` with a floor of one bit, because a one-element thing still needs a
/// wire to address it and a zero-width signal is not representable. This is what
/// four separate `addrBits` and one `bitsFor` were each computing.
let bitsToHold n = max 1 (ceilLog2 n)

/// Log base two of an exact power of two, refusing anything else. The refusal is
/// the point: a design that indexes by concatenation rather than by multiply
/// only works on powers of two, and the failure should be at elaboration rather
/// than in a waveform.
let log2Exact n =
    if n <= 0 || n &&& (n - 1) <> 0 then
        failwith $"a power of two is required here, got %d{n}"

    ceilLog2 n

/// The predicate behind `log2Exact`, for a caller that wants to choose rather
/// than to fail.
let isPowerOfTwo n = n > 0 && n &&& (n - 1) = 0

// ---------------------------------------------------------------------------
// Bit shapes.

/// Concatenate a list, first element at the most significant end — Chisel's
/// `Cat`. Folds left, so `catAll [a; b; c]` is `cat (cat a b) c` and matches
/// what the hand-written nests already emitted.
let catAll (values: Expr list) =
    match values with
    | [] -> failwith "catAll of nothing"
    | _ -> List.reduce cat values

/// `n` copies of a value, side by side — Chisel's `Fill`. Width is
/// `n * width value`, so filling a one-bit signal gives an `n`-bit mask of it,
/// which is the usual reason to reach for this.
let fill n (value: Expr) =
    if n < 1 then failwith $"fill needs at least one copy, got %d{n}"
    catAll (List.replicate n value)

/// Bits in the opposite order, same width. The slice rule applies — reversing
/// means part-selecting each bit, and Verilog has no part-select of a computed
/// value — so this takes a declared signal.
let reverse (value: Expr) =
    match value with
    | Ref (_, t) -> catAll [ for i in 0 .. t.Width - 1 -> slice i i value ]
    | _ -> failwith "reverse needs a declared signal — assign the computed value to a wire first"
