[<AutoOpen>]
module Warp11.Layout

/// The witness that turns a typed payload into ports and back: field names and
/// widths for declaring, pack/unpack for wiring. Hand-written per shape — the
/// no-reflection answer, one line per layout via the layoutN helpers below.
type Layout<'p> =
    { fields: (string * int) list
      pack: 'p -> Expr list
      unpack: Expr list -> 'p }

/// A ready/valid stream endpoint, generic over its payload — one Expr, a tuple of
/// named fields, whatever the layout says. `payload` and `valid` are driven by the
/// producer; `ready` is a net its (single) consumer must drive. The backward wire
/// travels forward inside the value — which is what lets a stream ride ordinary
/// function application even though the handshake flows both ways. The layout
/// rides the stream from its creation site, so consumers never ask the caller
/// for the pack/unpack recipe the stream already knows.
type Stream<'p> =
    { payload: 'p
      valid: Expr
      ready: Expr
      layout: Layout<'p> }

/// A valid-only endpoint — SpinalHDL's `Flow[T]`. Payload and `valid`, and no
/// `ready`: a beat transfers on every cycle `valid` is high, and the consumer
/// cannot refuse one.
///
/// The missing wire is the whole point, and it is worth being clear that this
/// is a *narrower* type than `Stream`, not a lighter one. Backpressure is how a
/// stage says it needs time — the reason `pipe(latency)` was rejected and
/// `throttle` exists — so reach for a Flow only where the producer genuinely
/// cannot be stopped and saying otherwise would be a lie: a sampled input, a
/// free-running counter, a result the fabric emits whether or not anyone is
/// listening. Everywhere else, a Stream that stalls is the honest description.
///
/// What it buys where it does fit: the type says the link cannot be refused, so
/// a reader need not work out whether a `1 ==> ready` is load-bearing or a stub;
/// there is no backward combinational path to close timing on; and there is
/// nothing to register but the forward direction, so `flowStage` is a plain
/// register rather than a skid buffer.
type Flow<'p> =
    { payload: 'p
      valid: Expr
      layout: Layout<'p> }

/// Two payloads side by side, as one. The fields of the first followed by the
/// fields of the second, so a beat that carries an operand pair *and* whatever
/// the caller wants to keep beside it is one layout rather than a hand-written
/// shape per combination.
///
/// Field names must not collide: they become port names, and two ports called
/// `data` is a design that does not elaborate. Caught here, where the reason is
/// obvious, rather than at the declaration that trips over it.
let layoutJoin (a: Layout<'a>) (b: Layout<'b>) : Layout<'a * 'b> =
    let clash =
        List.map fst a.fields
        |> List.filter (fun n -> List.exists (fun (m, _) -> m = n) b.fields)

    if not (List.isEmpty clash) then
        failwith
            $"""layoutJoin: both sides carry a field called {String.concat ", " clash} — field names become port names, so rename one side"""

    let aCount = List.length a.fields

    { fields = a.fields @ b.fields
      pack = fun (x, y) -> a.pack x @ b.pack y
      unpack =
        fun nets ->
            if List.length nets <> aCount + List.length b.fields then
                failwith $"layoutJoin: expected %d{aCount + List.length b.fields} nets"

            a.unpack (List.truncate aCount nets), b.unpack (List.skip aCount nets) }

let layout1 (an: string, aw: int) : Layout<Expr> =
    { fields = [ (an, aw) ]
      pack = fun x -> [ x ]
      unpack =
        fun nets ->
            match nets with
            | [ x ] -> x
            | _ -> failwith $"layout1 {an}: expected 1 net" }

let layout2 (an: string, aw: int) (bn: string, bw: int) : Layout<Expr * Expr> =
    { fields = [ (an, aw); (bn, bw) ]
      pack = fun (x, y) -> [ x; y ]
      unpack =
        fun nets ->
            match nets with
            | [ x; y ] -> x, y
            | _ -> failwith $"layout2 {an},{bn}: expected 2 nets" }

let layout3 (an: string, aw: int) (bn: string, bw: int) (cn: string, cw: int) : Layout<Expr * Expr * Expr> =
    { fields = [ (an, aw); (bn, bw); (cn, cw) ]
      pack = fun (x, y, z) -> [ x; y; z ]
      unpack =
        fun nets ->
            match nets with
            | [ x; y; z ] -> x, y, z
            | _ -> failwith $"layout3 {an},{bn},{cn}: expected 3 nets" }

let layout4
    (an: string, aw: int)
    (bn: string, bw: int)
    (cn: string, cw: int)
    (dn: string, dw: int)
    : Layout<Expr * Expr * Expr * Expr> =
    { fields = [ (an, aw); (bn, bw); (cn, cw); (dn, dw) ]
      pack = fun (x, y, z, u) -> [ x; y; z; u ]
      unpack =
        fun nets ->
            match nets with
            | [ x; y; z; u ] -> x, y, z, u
            | _ -> failwith $"layout4 {an},{bn},{cn},{dn}: expected 4 nets" }

/// The transporter for a payload that has to cross as ONE flat bus — a wide
/// port, a run payload riding a dispatch tree, anything the wire cannot carry
/// as separate fields. `dematerialize` packs the typed payload down to a single
/// Expr (first field at the low bits); `materialize` reconstitutes it.
///
/// The point is that both derive from one `Layout`, so the two ends of a link
/// cannot disagree. Packing by hand with `cat` at the producer and unpacking by
/// hand with `slice` at the consumer — in two files, with offsets computed
/// independently — is the failure this removes: reorder one side and nothing
/// fails to compile.
type Transporter<'p> =
    { pattern: Layout<'p>
      width: int
      dematerialize: 'p -> Expr
      materialize: Expr -> 'p }

/// A union beat on wires: a tag plus data bits every variant shares. Which
/// variant the bits mean is a per-cycle runtime fact, so consumption is
/// conditional (see `matchUnion`), never an elaboration-time pattern match.
type UnionBeat = { tag: Expr; data: Expr }

/// A sum-type payload. Variants are ordinary Layouts, so a variant's view is
/// typed end to end — no new IR: the tag is a field, the data is a field, and
/// variant fields are slices. Unions compile away exactly as layouts do.
type Union2<'a, 'b> =
    { tagWidth: int
      dataWidth: int
      variant0: Layout<'a>
      variant1: Layout<'b> }

let private packedWidth (l: Layout<_>) = l.fields |> List.sumBy snd

let union2 (v0: Layout<'a>) (v1: Layout<'b>) : Union2<'a, 'b> =
    { tagWidth = 1
      dataWidth = max (packedWidth v0) (packedWidth v1)
      variant0 = v0
      variant1 = v1 }

/// First field at the low bits, zero-padded up to the union's data width.
let private packInto dataWidth (l: Layout<'p>) (payload: 'p) =
    let packed =
        match l.pack payload with
        | [] -> failwith "a variant needs at least one field"
        | first :: rest -> List.fold (fun acc f -> cat f acc) first rest

    let pw = packedWidth l

    if pw < dataWidth then
        cat (lit 0UL (dataWidth - pw)) packed
    else
        packed

let private variantView (l: Layout<'p>) (data: Expr) : 'p =
    (0, l.fields)
    ||> List.mapFold (fun offset (_, w) -> slice (offset + w - 1) offset data, offset + w)
    |> fst
    |> l.unpack

/// Both directions from one layout. A union variant is the same pack widened to
/// the union's data field, so `transporter` and `inject0`/`variant0` are the one
/// mechanism — this just names it for the ordinary flat-bus case.
let transporter (l: Layout<'p>) : Transporter<'p> =
    let w = packedWidth l

    { pattern = l
      width = w
      dematerialize = packInto w l
      materialize = variantView l }

let inject0 (u: Union2<'a, 'b>) (payload: 'a) : UnionBeat =
    { tag = lit 0UL u.tagWidth
      data = packInto u.dataWidth u.variant0 payload }

let inject1 (u: Union2<'a, 'b>) (payload: 'b) : UnionBeat =
    { tag = lit 1UL u.tagWidth
      data = packInto u.dataWidth u.variant1 payload }

let variant0 (u: Union2<'a, 'b>) data : 'a = variantView u.variant0 data
let variant1 (u: Union2<'a, 'b>) data : 'b = variantView u.variant1 data

/// The stream layout of a union beat, so union streams ride the generic stream
/// machinery unchanged — stages, maps, the handshake, all of it.
let unionLayout (u: Union2<'a, 'b>) : Layout<UnionBeat> =
    { fields = [ ("tag", u.tagWidth); ("data", u.dataWidth) ]
      pack = fun b -> [ b.tag; b.data ]
      unpack =
        fun nets ->
            match nets with
            | [ t; d ] -> { tag = t; data = d }
            | _ -> failwith "unionLayout: expected 2 nets" }
