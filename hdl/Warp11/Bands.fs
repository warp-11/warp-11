/// **The banded world store — the working set of the band architecture**
/// (`notes/STREAMING_ARCH.md`). A 2D world of full-width rows, striped into
/// per-band memories and double-buffered, that hands each of a row of
/// lockstep engines its slice of the world *with the vertical halo already
/// routed* — and takes the next generation's rows back.
///
/// This exists because the composition it packages, though it is nothing but
/// existing parts (`rangeStream`, `streamBroadcast`, `blockReadWindow`,
/// per-band memories, a parity bit), was measured unreadable written out
/// longhand: of the Game of Life sweep's ~120 lines, fifteen were Life and
/// the rest were this file. The doc's open question — does `band` decompose,
/// or is it a primitive? — got its answer by the writing: it decomposes, and
/// the decomposition deserves a name.
[<AutoOpen>]
module Warp11.Bands

/// The shape of a banded world: how it is striped, how far a rule reaches,
/// and what lies beyond the top and bottom edges.
type BandedWorldSpec =
    { /// Parallel band engines; the world is striped into this many band
      /// memories per buffer. A power of two, for the address split.
      engines: int
      /// Full-width rows per band. A power of two of at least two, for the
      /// truncation-wrap local addressing.
      bandRows: int
      /// Bits per row — columns × cellBits, but this store never needs to
      /// know the split.
      rowBits: int
      /// The interaction radius r: halo rows fetched from each neighbouring
      /// band per sweep. 1 for a 3-row window, 2 for a 5-row one. At most
      /// `bandRows`, so halos come only from immediate neighbours.
      haloRows: int
      /// What lies beyond the first and last rows of the world: `Wrap` (a
      /// torus) or `Zero` (dead padding). `Clamp` would need a band to
      /// re-read its own edge rows against the shared schedule, and is
      /// refused until something needs it.
      verticalEdge: Edge }

/// A constructed banded world. Reads serve the current generation, landings
/// build the next; the caller says when to `flip` (having seen `swept`), and
/// the host fills and probes by global row address.
type BandedWorld =
    { /// Engine g's read sweep: on each start pulse, `bandRows + 2·haloRows`
      /// rows in order — the neighbours' edge rows first and last.
      sweeps: Stream<Expr> list
      /// Engine g's write side: hand it the stream of next-generation rows;
      /// they land at consecutive local addresses in the non-current buffer.
      landings: (Stream<Expr> -> unit) list
      /// Every engine has landed its whole band this sweep.
      swept: Expr
      /// The current generation's row at the probe address, one cycle after
      /// the address (a synchronous read).
      probe: Expr }

/// Build the store. `start` launches one sweep (all engines, one shared
/// schedule); `flip` — the caller's condition, usually `swept` under its
/// control machine — swaps the buffers; `fillEnable` should already be gated
/// on the caller's idle state, because a fill during a sweep corrupts the
/// generation being read.
///
/// **The mechanism, in one paragraph.** Every band memory in both buffers is
/// read at the same local address every beat — `(i − haloRows) mod bandRows`
/// — so in lockstep each memory serves at most one consumer per cycle and
/// the halo costs no extra ports: on the first `haloRows` beats a band's word
/// is its *own* last rows, which are exactly what the engine below wants, and
/// symmetrically at the end. Each band's word stream is therefore broadcast
/// three ways — to the engine below (leg 0), its own (leg 1), the engine
/// above (leg 2) — and each engine muxes among its three sources by a beat
/// counter, and between the two buffers by the parity bit. Writes go to the
/// other buffer, because the wrap rows are re-read at the far end of a sweep
/// after their next-generation values exist: in-place is wrong by
/// construction, which is why the double buffer is not optional.
let bandedWorld
    (spec: BandedWorldSpec)
    (start: Expr)
    (flip: Expr)
    (fillAddr: Expr)
    (fillData: Expr)
    (fillEnable: Expr)
    (probeAddr: Expr)
    : BandedWorld =
    let engines = spec.engines
    let bandRows = spec.bandRows
    let halo = spec.haloRows

    let localWidth = bitsToHold bandRows

    if bandRows < 2 || (1 <<< localWidth) <> bandRows then
        failwith $"bandedWorld: bands are a power of two of at least two rows, got %d{bandRows}"

    let gridRows = engines * bandRows
    let addrWidth = bitsToHold gridRows
    let bandWidth = addrWidth - localWidth

    if engines < 1 || (1 <<< bandWidth) <> engines then
        failwith $"bandedWorld: %d{engines} engines must be a power of two, for the address split"

    if halo < 1 || halo > bandRows then
        failwith $"bandedWorld: a halo of %d{halo} rows over %d{bandRows}-row bands — the radius must be 1..bandRows"

    match spec.verticalEdge with
    | Edge.Wrap
    | Edge.Zero -> ()
    | Edge.Clamp -> failwith "bandedWorld: Clamp vertical edges are not supported (see the spec's doc)"

    let beats = bandRows + 2 * halo
    let indexWidth = bitsToHold beats

    let bufferA = [ for b in 0 .. engines - 1 -> blockMem $"worldA%d{b}" localWidth spec.rowBits ]
    let bufferB = [ for b in 0 .. engines - 1 -> blockMem $"worldB%d{b}" localWidth spec.rowBits ]

    // Which buffer holds the current generation. Reads, fill and probe go
    // there; landings go to the other; `flip` swaps.
    let parity = regBit "world_parity"
    If flip (fun () -> bnot parity ==> parity)

    // ---- host side: fill and probe by global row --------------------------
    let fillLocal =
        if engines = 1 then fillAddr else slice (localWidth - 1) 0 fillAddr

    for b in 0 .. engines - 1 do
        let inBand =
            if engines = 1 then
                fillEnable
            else
                fillEnable
                &&& eq (slice (addrWidth - 1) localWidth fillAddr) (lit (uint64 b) bandWidth)

        memWrite bufferA[b] fillLocal fillData (inBand &&& bnot parity)
        memWrite bufferB[b] fillLocal fillData (inBand &&& parity)

    let probeLocal =
        if engines = 1 then probeAddr else slice (localWidth - 1) 0 probeAddr

    let probeWordOf label buffer =
        let words = [ for band in buffer -> (memReadPort band probeLocal).data ]

        if engines = 1 then
            words[0]
        else
            // The word answers a cycle after its address, so the band select
            // is registered to arrive with it.
            let bandHeld = reg $"probe_band_%s{label}" bandWidth
            slice (addrWidth - 1) localWidth probeAddr ==> bandHeld
            selectIndexed bandHeld words

    let probe = mux parity (probeWordOf "b" bufferB) (probeWordOf "a" bufferA)

    // ---- the shared schedule ---------------------------------------------
    // Beat i reads local row (i − halo) mod bandRows in EVERY band of both
    // buffers, which is what makes the halo free — see the doc above.
    let requestLegs =
        rangeStream start beats
        |> Stream.map (fun i ->
            let shifted = wire "band_shifted" indexWidth
            i + lit (uint64 (bandRows - halo)) indexWidth ==> shifted
            let localAddr = wire "band_addr" localWidth
            slice (localWidth - 1) 0 shifted ==> localAddr
            localAddr)
        |> streamBroadcast (2 * engines)

    // Leg k of band b, by convention: 0 = to engine b+1, 1 = to engine b,
    // 2 = to engine b−1.
    let fabricOf (label: string) (buffer: Mem list) (legs: Stream<Expr> list) =
        [ for b in 0 .. engines - 1 ->
              streamBroadcast 3 ((blockReadWindow $"%s{label}%d{b}_port" buffer[b]).read legs[b]) ]

    let wordsA = fabricOf "worldA" bufferA requestLegs[.. engines - 1]
    let wordsB = fabricOf "worldB" bufferB requestLegs[engines ..]

    // ---- per-engine: sources, phase mux, write side -----------------------
    let built =
        [ for g in 0 .. engines - 1 ->
              let sourcesOf (words: Stream<Expr> list list) =
                  words[(g - 1 + engines) % engines][0], words[g][1], words[(g + 1) % engines][2]

              let aboveA, ownA, belowA = sourcesOf wordsA
              let aboveB, ownB, belowB = sourcesOf wordsB

              let ready = wireBit $"engine%d{g}_ready"
              registerStreamReady ready

              for leg in [ aboveA; ownA; belowA; aboveB; ownB; belowB ] do
                  ready ==> leg.ready

              let valid = ownA.valid &&& ownB.valid

              // Which beat of the sweep this is, so the halo beats take the
              // neighbours' words — or the edge policy's zeros at the world's
              // rim, where "the neighbour" is on the far side of the torus
              // that Zero says is not there.
              let beat = reg $"engine%d{g}_beat" indexWidth

              If (valid &&& ready) (fun () ->
                  ifElse
                      [ eq beat (lit (uint64 (beats - 1)) indexWidth), fun () -> lit 0UL indexWidth ==> beat
                        otherwise, fun () -> beat + lit 1UL indexWidth ==> beat ])

              If start (fun () -> lit 0UL indexWidth ==> beat)

              let dead = lit 0UL spec.rowBits

              let phaseMux (above: Stream<Expr>) (own: Stream<Expr>) (below: Stream<Expr>) =
                  let abovePayload =
                      match spec.verticalEdge with
                      | Edge.Zero when g = 0 -> dead
                      | _ -> above.payload

                  let belowPayload =
                      match spec.verticalEdge with
                      | Edge.Zero when g = engines - 1 -> dead
                      | _ -> below.payload

                  mux
                      (lt beat (lit (uint64 halo) indexWidth))
                      abovePayload
                      (mux (lt beat (lit (uint64 (bandRows + halo)) indexWidth)) own.payload belowPayload)

              let row = wire $"engine%d{g}_row" spec.rowBits
              mux parity (phaseMux aboveB ownB belowB) (phaseMux aboveA ownA belowA) ==> row

              let sweep: Stream<Expr> =
                  { payload = row
                    valid = valid
                    ready = ready
                    layout = layout1 ("row", spec.rowBits) }

              let written = reg $"engine%d{g}_written" indexWidth
              If start (fun () -> lit 0UL indexWidth ==> written)

              let landing (s: Stream<Expr>) =
                  lit 1UL 1 ==> s.ready
                  let writeAddr = slice (localWidth - 1) 0 written
                  memWrite bufferA[g] writeAddr s.payload (s.valid &&& parity)
                  memWrite bufferB[g] writeAddr s.payload (s.valid &&& bnot parity)
                  If s.valid (fun () -> written + lit 1UL indexWidth ==> written)

              sweep, landing, eq written (lit (uint64 bandRows) indexWidth) ]

    { sweeps = [ for sweep, _, _ in built -> sweep ]
      landings = [ for _, landing, _ in built -> landing ]
      swept = built |> List.map (fun (_, _, complete) -> complete) |> List.reduce (&&&)
      probe = probe }
