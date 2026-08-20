/// The IR as low-FIRRTL text.
///
/// This exists to keep us honest rather than to be a dependency. If `firtool`
/// accepts what comes out of here and its Verilog agrees with our trace, then
/// the claim "we did not invent an IR" is a measured fact instead of an
/// intention. `firtool` is a CI tool; nothing a user does needs it, and the
/// front page's promise — .NET 10 and nothing else until you want a bitstream —
/// is untouched by this file existing.
///
/// **Where our semantics and FIRRTL's differ, and what is emitted:**
///
/// FIRRTL's `add` and `sub` *widen* — `add(UInt<8>, UInt<8>)` is `UInt<9>`, and
/// `sub` on two unsigned operands is `SInt<9>`, because it will not lose the
/// carry or the sign. Ours wrap at the wider operand, which is what a hardware
/// adder does and what every design here assumes. That is exactly FIRRTL's
/// `tail(…, 1)`, verified over all 65,536 8-bit operand pairs when this was
/// planned, so the translation is a wrapper rather than a reinterpretation:
///
///     our  add a b        →  asUInt(tail(add(a, b), 1))
///     our  sub a b        →  asUInt(tail(sub(a, b), 1))
///
/// with `asSInt` where the operands are signed. Everything else lines up
/// one-for-one, which is the point of Phase 1 having happened first.
module Warp11.Firrtl

/// What cannot be said in `.fir`, said plainly rather than emitted wrong.
///
/// A memory's initial contents are the interesting one: warp11 puts a ROM's
/// values in the Verilog as an `initial` block, which Vivado turns into BRAM
/// INIT, and FIRRTL has no portable way to express that. Rather than emit a
/// memory that reads as zeros and call the export a success, this refuses and
/// names the design.
exception Unrepresentable of what: string

let private fail what = raise (Unrepresentable what)

/// A ground type as FIRRTL writes it. Public because the export's own check
/// compares against it rather than restating the spelling.
let typeText (t: GroundType) =
    match t with
    | UInt w -> $"UInt<%d{w}>"
    | SInt w -> $"SInt<%d{w}>"

/// A literal, at its type. An `SInt` carries a bit pattern here and a *value*
/// there, so the two's-complement reading is done on the way out.
let private litText (value: uint64) (t: GroundType) =
    match t with
    | UInt w -> $"UInt<%d{w}>(%d{value})"
    | SInt w ->
        let signBit = if w >= 64 then 1UL <<< 63 else 1UL <<< (w - 1)

        let signed =
            if w >= 64 then int64 value
            elif value &&& signBit <> 0UL then int64 value - (1L <<< w)
            else int64 value

        $"SInt<%d{w}>(%d{signed})"

/// Our arithmetic at FIRRTL's: the carry FIRRTL keeps is the one we drop.
let private wrapped op (a: string) (b: string) signed =
    let back = if signed then "asSInt" else "asUInt"
    $"{back}(tail({op}({a}, {b}), 1))"

let rec private expr (e: Expr) : string =
    match e with
    | Lit (v, t) -> litText v t
    | Ref (n, _) -> n
    // FIRRTL types its arithmetic: both operands of add/sub/mul/eq/lt must
    // agree on signedness, where ours only have to agree on width (and add/sub
    // not even that — two's complement makes them sign-agnostic). Each operand
    // is therefore read at the pair's own reading on the way out.
    | Add (a, b) ->
        let t = typeOf e
        wrapped "add" (coerce t a) (coerce t b) t.Signed
    | Sub (a, b) ->
        let t = typeOf e
        wrapped "sub" (coerce t a) (coerce t b) t.Signed
    | Mul (a, b) ->
        let signed = isSigned e
        $"mul({read signed a}, {read signed b})"
    // FIRRTL wants both arms at one type, where ours takes the true arm's and
    // width-checks the rest. Same rule, said out loud.
    | Mux (c, t, f) -> $"mux({expr c}, {expr t}, {coerce (typeOf t) f})"
    // Bits are bits here too, and FIRRTL is stricter than we are about saying
    // so: `cat` requires its operands to agree on signedness and returns UInt,
    // which is also what our `typeOf` gives a `Concat`. So both sides are read
    // unsigned, exactly as the bitwise operators below. Without this, catting a
    // zero literal onto a value that had been *declared* signed emits FIRRTL
    // firtool rejects — invisible to the Verilog leg, since Verilog does not
    // care, and to our own Sim, which agrees with the Verilog.
    | Concat (hi, lo) -> $"cat({read false hi}, {read false lo})"
    | Slice (s, hi, lo) -> $"bits({expr s}, %d{hi}, %d{lo})"
    | Eq (a, b) -> $"eq({read (isSigned a) a}, {read (isSigned a) b})"
    | Lt (a, b) -> $"lt({read (isSigned a) a}, {read (isSigned a) b})"
    // Bits are bits: the result is unsigned whatever went in, so both sides are
    // read that way and the reinterpretation costs nothing.
    | And (a, b) -> $"and({read false a}, {read false b})"
    | Or (a, b) -> $"or({read false a}, {read false b})"
    | Xor (a, b) -> $"xor({read false a}, {read false b})"
    | Not v -> $"not({read false v})"
    | Shr (s, n) -> $"shr({expr s}, %d{n})"
    | Div (a, b) -> $"div({expr a}, {read (isSigned a) b})"
    | Rem (a, b) -> $"rem({expr a}, {read (isSigned a) b})"
    | Reduce (AllBits, v) -> $"andr({read false v})"
    | Reduce (AnyBit, v) -> $"orr({read false v})"
    | Reduce (Parity, v) -> $"xorr({read false v})"
    | DynamicShl (v, n) -> $"dshl({expr v}, {read false n})"
    | DynamicShr (v, n) -> $"dshr({expr v}, {read false n})"
    | Pad (s, w) -> $"pad({expr s}, %d{w})"
    | AsUInt v -> $"asUInt({expr v})"
    | AsSInt v -> $"asSInt({expr v})"
    // A mem read is a port on the memory, not an expression, so it is lifted
    // out before this ever sees one.
    | MemRead (m, _, _) -> fail $"a memory read of '{m}' reached expression emission"

/// An expression as a *particular* type. Our connects are width-checked and
/// signedness-agnostic — driving a `UInt` wire from a signed sum is ordinary
/// here, because the bits are the bits. FIRRTL types its connects, so the
/// reinterpretation that was implicit becomes an `asUInt`/`asSInt` that costs
/// nothing and says what was already true.
/// The same bits at the other reading, without touching the width.
and private read (signed: bool) (e: Expr) : string =
    let et = typeOf e

    if et.Signed = signed then expr e
    elif signed then $"asSInt({expr e})"
    else $"asUInt({expr e})"

/// An expression at a type it may have to be *narrowed* into. Only memory ports
/// need this: FIRRTL types a mem's address at exactly clog2(depth) and its data
/// at the word type, where our IR lets an address be any width and lets signed
/// bits drive a memory word. Both are the same reinterpretation the Verilog
/// index already performs silently.
and private atPortType (t: GroundType) (e: Expr) : string =
    let et = typeOf e

    if et.Width > t.Width then
        let narrowed = $"bits({expr e}, %d{t.Width - 1}, 0)"
        if t.Signed then $"asSInt({narrowed})" else narrowed
    else
        coerce t e

and private coerce (t: GroundType) (e: Expr) : string =
    let et = typeOf e

    if et.Width > t.Width then
        fail $"a %d{et.Width}-bit value drives a %d{t.Width}-bit net — checkWidths should have caught this"

    let widened = if et.Width < t.Width then $"pad({expr e}, %d{t.Width})" else expr e

    if et.Signed = t.Signed then widened
    elif t.Signed then $"asSInt({widened})"
    else $"asUInt({widened})"

/// Whether an expression reaches a particular memory read.
let rec private usesRead (mem: string) (addr: Expr) (e: Expr) =
    match e with
    | MemRead (m2, a2, _) -> (m2 = mem && a2 = addr) || usesRead mem addr a2
    | Add (a, b)
    | Sub (a, b)
    | Mul (a, b)
    | Eq (a, b)
    | Lt (a, b)
    | And (a, b)
    | Or (a, b)
    | Xor (a, b)
    | Concat (a, b) -> usesRead mem addr a || usesRead mem addr b
    | Mux (c, t, f) -> usesRead mem addr c || usesRead mem addr t || usesRead mem addr f
    | Not v
    | AsUInt v
    | AsSInt v
    | Shr (v, _)
    | Pad (v, _)
    | Slice (v, _, _) -> usesRead mem addr v
    | Reduce (_, v) -> usesRead mem addr v
    | Div (a, b)
    | Rem (a, b) -> usesRead mem addr a || usesRead mem addr b
    | DynamicShl (v, n)
    | DynamicShr (v, n) -> usesRead mem addr v || usesRead mem addr n
    | Lit _
    | Ref _ -> false

/// Every distinct (mem, address) read in a module, in order — each becomes one
/// reader port, since FIRRTL memories are ported rather than indexed.
let private readsOf (m: ModuleDef) =
    let rec walk e =
        seq {
            match e with
            | MemRead (mem, a, _) ->
                yield mem, a
                yield! walk a
            | Add (a, b)
            | Sub (a, b)
            | Mul (a, b)
            | Eq (a, b)
            | Lt (a, b)
            | And (a, b)
            | Or (a, b)
            | Xor (a, b)
            | Concat (a, b) ->
                yield! walk a
                yield! walk b
            | Mux (c, t, f) ->
                yield! walk c
                yield! walk t
                yield! walk f
            | Not v
            | AsUInt v
            | AsSInt v
            | Shr (v, _)
            | Pad (v, _)
            | Slice (v, _, _) -> yield! walk v
            | Reduce (_, v) -> yield! walk v
            | Div (a, b)
            | Rem (a, b) ->
                yield! walk a
                yield! walk b
            | DynamicShl (v, n)
            | DynamicShr (v, n) ->
                yield! walk v
                yield! walk n
            | Lit _
            | Ref _ -> ()
        }

    [ for s in m.stmts do
        match s with
        | Assign (_, v) -> yield! walk v
        | MemWrite (_, a, d, e, k) ->
            yield! walk a
            yield! walk d
            yield! walk e

            match k with
            | Some mk -> yield! walk mk
            | None -> ()
        | Assert (c, _) -> yield! walk c ]
    |> List.distinct

/// The module's own text. Ports first, then declarations, then the statements —
/// FIRRTL is order-insensitive within a module, so this follows the Verilog
/// emitter's shape to keep the two diffable by eye.
/// `isPublic` marks the circuit's main module. FIRRTL 4.0 removed private main
/// modules — a circuit's entry point has to say its ports are the boundary, and
/// firtool refuses the file otherwise.
let emitModule (isPublic: bool) (m: ModuleDef) =
    let indent = "  "
    let body = indent + indent

    /// What a name was declared as — the type every connect to it has to match.
    let declaredType =
        dict
            [ for d in m.decls do
                match declOf d with
                | Some (n, t) -> yield n, t
                | None -> () ]

    let clk = m.clock.clockPort

    // An active-low reset port derives its active-high form as a node, the same
    // arrangement the Verilog emitter uses under a different name.
    let rstInternal =
        if m.clock.resetActiveLow then m.clock.resetPort + "_pos" else m.clock.resetPort

    let clocked = needsClk m

    let reads = readsOf m

    // A FIRRTL memory's ports are *fields* of it: the mem declares `reader => r0`
    // and everything reaches it as `store.r0.addr`. Getting that wrong is what
    // firtool means by "use of unknown declaration".
    let readerPort index = $"r%d{index}"

    // Which reader port serves each (mem, address) pair. Numbered *within* each
    // memory, because that is the scope the port name lives in — a global
    // counter would declare `r0` and reach for `r2`.
    let readerOf =
        reads
        |> List.groupBy fst
        |> List.collect (fun (mem, rs) ->
            rs |> List.mapi (fun i (_, a) -> (mem, a), mem + "." + readerPort i))
        |> dict

    let memNames =
        [ for d in m.decls do
            match d with
            | Memory(n, _, _, _, _) -> yield n
            | _ -> () ]

    // A lane-masked memory becomes a *vector* in FIRRTL, because that is the
    // only place FIRRTL puts a per-lane mask: a write port's mask mirrors its
    // data type, so `UInt<32>` can only carry one mask bit. The vector is the
    // encoding, not a different circuit — `data-type => UInt<8>[4]` addressed as
    // one word is the same array a 32-bit memory with four byte-enables is.
    let laneMasked =
        dict [ for st in m.stmts do
                   match st with
                   | MemWrite (mem, _, _, _, Some k) -> yield mem, width k
                   | _ -> () ]

    // A read is replaced by its port's data field before expressions are
    // emitted, which is what keeps `expr` free of memory knowledge.
    let rec resolve e =
        match e with
        | MemRead (mem, a, _) ->
            let port = readerOf[(mem, a)]
            let w = width e

            match laneMasked.TryGetValue mem with
            | true, lanes ->
                // The word, put back together out of its lanes: lane 0 is the
                // low bits, matching how the writer takes it apart.
                let laneWidth = w / lanes

                [ 0 .. lanes - 1 ]
                |> List.map (fun i -> Ref($"{port}.data[%d{i}]", UInt laneWidth))
                |> List.reduce (fun low high -> Concat(high, low))
            | _ -> Ref($"{port}.data", UInt w)
        | Add (a, b) -> Add(resolve a, resolve b)
        | Sub (a, b) -> Sub(resolve a, resolve b)
        | Mul (a, b) -> Mul(resolve a, resolve b)
        | Eq (a, b) -> Eq(resolve a, resolve b)
        | Lt (a, b) -> Lt(resolve a, resolve b)
        | And (a, b) -> And(resolve a, resolve b)
        | Or (a, b) -> Or(resolve a, resolve b)
        | Xor (a, b) -> Xor(resolve a, resolve b)
        | Concat (a, b) -> Concat(resolve a, resolve b)
        | Mux (c, t, f) -> Mux(resolve c, resolve t, resolve f)
        | Not v -> Not(resolve v)
        | AsUInt v -> AsUInt(resolve v)
        | AsSInt v -> AsSInt(resolve v)
        | Shr (v, n) -> Shr(resolve v, n)
        | Pad (v, w) -> Pad(resolve v, w)
        | Reduce (kind, v) -> Reduce(kind, resolve v)
        | Div (a, b) -> Div(resolve a, resolve b)
        | Rem (a, b) -> Rem(resolve a, resolve b)
        | DynamicShl (v, n) -> DynamicShl(resolve v, resolve n)
        | DynamicShr (v, n) -> DynamicShr(resolve v, resolve n)
        | Slice (v, hi, lo) -> Slice(resolve v, hi, lo)
        | Lit _
        | Ref _ -> e

    let ports =
        [ if clocked then
              yield $"{body}input {clk} : Clock"
              yield $"{body}input {m.clock.resetPort} : UInt<1>"
          for d in m.decls do
              match d with
              | Input (n, t) -> yield $"{body}input {n} : {typeText t}"
              | Output (n, t) -> yield $"{body}output {n} : {typeText t}"
              | _ -> () ]

    let declarations =
        [ if m.clock.resetActiveLow then
              yield $"{body}node {rstInternal} = not({m.clock.resetPort})"

          for d in m.decls do
              match d with
              | Wire (n, t) -> yield $"{body}wire {n} : {typeText t}"
              | Reg (n, t, Some init) ->
                  yield $"{body}regreset {n} : {typeText t}, {clk}, {rstInternal}, {litText init t}"
              // FIRRTL's plain `reg` is exactly ours-without-a-reset.
              | Reg (n, t, None) -> yield $"{body}reg {n} : {typeText t}, {clk}"
              | Memory(n, aw, w, init, _) ->
                  if init.IsSome then
                      fail
                          $"memory '{n}' in '{m.name}' has initial contents, which FIRRTL has no portable way to express"

                  let readers = [ for (mem, a) in reads do if mem = n then yield a ]

                  yield $"{body}mem {n} :"
                  match laneMasked.TryGetValue n with
                  | true, lanes -> yield $"{body}  data-type => UInt<%d{w / lanes}>[%d{lanes}]"
                  | _ -> yield $"{body}  data-type => UInt<%d{w}>"
                  yield $"{body}  depth => %d{1 <<< aw}"
                  yield $"{body}  read-latency => 0"
                  yield $"{body}  write-latency => 1"

                  for i, _ in List.indexed readers do
                      yield $"{body}  reader => {readerPort i}"

                  if m.stmts |> List.exists (function MemWrite (mn, _, _, _, _) -> mn = n | _ -> false) then
                      yield $"{body}  writer => w"

                  // `old`, not `undefined`. warp11's mems read the stored value
                  // when a write hits the same address in the same cycle — the
                  // Verilog is a continuous index of the array and the write is
                  // non-blocking, so the read cannot see it. `undefined` lets
                  // firtool forward the write instead, and it does: MandelPod's
                  // barrel register file came out a cycle early and only the
                  // third leg noticed.
                  yield $"{body}  read-under-write => old"
              | Input _
              | Output _ -> () ]

    // A memory port is wired, not indexed: address, enable and clock each get a
    // connect, which is the shape low-FIRRTL insists on.
    // A memory's shape, for typing its ports: FIRRTL gives addr exactly
    // clog2(depth) bits and data the word type, both unsigned.
    let memShape =
        dict
            [ for d in m.decls do
                match d with
                | Memory(n, aw, w, _, _) -> yield n, (UInt aw, UInt w)
                | _ -> () ]

    /// The reads a statement's expressions reach for.
    let readsIn stmt =
        let exprs =
            match stmt with
            | Assign (_, v) -> [ v ]
            | MemWrite (_, a, d, e, k) -> [ a; d; e ] @ Option.toList k
            | Assert (c, _) -> [ c ]

        reads |> List.filter (fun (mem, addr) -> exprs |> List.exists (usesRead mem addr))

    // An instance's ports are reached as fields, and the staging wires the
    // parent declared for them are what connect to those fields.
    let instances =
        [ for inst in m.instances do
              yield $"{body}inst {inst.instName} of {inst.child.name}"

              if needsClk inst.child then
                  yield $"{body}connect {inst.instName}.{inst.child.clock.clockPort}, {clk}"

                  let childReset =
                      if inst.child.clock.resetActiveLow then $"not({rstInternal})" else rstInternal

                  yield $"{body}connect {inst.instName}.{inst.child.clock.resetPort}, {childReset}"

              for d in inst.child.decls do
                  let staging n = inst.instName + "_" + n
                  let field n = inst.instName + "." + n

                  match d with
                  | Input (n, t) -> yield $"{body}connect {field n}, {coerce t (Ref(staging n, t))}"
                  | Output (n, t) -> yield $"{body}connect {staging n}, {coerce t (Ref(field n, t))}"
                  | _ -> () ]

    // Memory reader ports. Order is not a concern — a FIRRTL connect is a
    // dataflow edge like a Verilog `assign`, not a sequential assignment, so a
    // wire read above its own connect still reads the value it will hold. That
    // is worth writing down because it is the opposite of what "last connect
    // wins" sounds like, and a scheduler built on the other reading was written
    // and thrown away here once.
    let readerPorts =
        [ for (mem, addr) in reads do
              let r = readerOf[(mem, addr)]
              let addrType, _ = memShape[mem]
              yield $"{body}connect {r}.addr, {atPortType addrType (resolve addr)}"
              yield $"{body}connect {r}.en, UInt<1>(1)"
              yield $"{body}connect {r}.clk, {clk}" ]

    let connects =
        [ for st in m.stmts do
              match st with
              | Assign (t, v) ->
                  match declaredType.TryGetValue t with
                  | true, ty -> yield $"{body}connect {t}, {coerce ty (resolve v)}"
                  | _ -> fail $"'{t}' is driven in '{m.name}' but never declared"
              | MemWrite (mem, addr, data, enable, maskExpr) ->
                  let addrType, dataType = memShape[mem]
                  yield $"{body}connect {mem}.w.addr, {atPortType addrType (resolve addr)}"
                  // Held off during reset. Our Verilog puts every sequential
                  // action inside `always @(posedge clk) if (rst) … else …`, so
                  // a memory write cannot happen on a reset edge. A FIRRTL write
                  // port has no reset of its own, so the gate is explicit here —
                  // without it a register file takes a write during reset and
                  // runs a cycle ahead ever after.
                  let gated = $"and({atPortType (UInt 1) (resolve enable)}, not({rstInternal}))"
                  yield $"{body}connect {mem}.w.en, {gated}"
                  yield $"{body}connect {mem}.w.clk, {clk}"
                  match maskExpr with
                  | None ->
                      yield $"{body}connect {mem}.w.data, {atPortType dataType (resolve data)}"
                      yield $"{body}connect {mem}.w.mask, UInt<1>(1)"
                  | Some mk ->
                      // Lane by lane, in both directions: the word is taken
                      // apart into the vector's elements and the mask's bits
                      // land on the elements' mask fields one for one.
                      let lanes = width mk
                      let laneWidth = dataType.Width / lanes
                      let d = resolve data
                      let k = resolve mk

                      for i in 0 .. lanes - 1 do
                          let hi = (i + 1) * laneWidth - 1
                          let lo = i * laneWidth

                          yield
                              $"{body}connect {mem}.w.data[%d{i}], {atPortType (UInt laneWidth) (Slice(d, hi, lo))}"

                          yield $"{body}connect {mem}.w.mask[%d{i}], {atPortType (UInt 1) (Slice(k, i, i))}"
              | Assert (c, message) ->
                  let escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"")

                  yield
                      $"{body}assert({clk}, {expr (resolve c)}, not({rstInternal}), \"{escaped}\")" ]

    let unusedReg =
        // A register nothing drives holds its reset value forever, which is
        // legal here and in Verilog alike; FIRRTL wants every net driven, so an
        // undriven one connects to itself.
        [ for d in m.decls do
            match d with
            | Reg (n, _, _) when not (m.stmts |> List.exists (function Assign (t, _) -> t = n | _ -> false)) ->
                yield $"{body}connect {n}, {n}"
            | _ -> () ]

    let keyword = if isPublic then "public module" else "module"

    [ yield $"{indent}{keyword} {m.name} :"
      yield! ports
      yield! declarations
      yield! instances
      yield! readerPorts
      yield! connects
      yield! unusedReg ]
    |> String.concat "\n"

/// The whole design as one `.fir` circuit: every module it reaches, deduplicated
/// by name exactly as the Verilog emitter does, with the top module last.
///
/// The version line matters — the textual format changed `x <= y` to
/// `connect x, y` at 3.0, and a reader picks its parser from this line.
let emitFirrtl (m: ModuleDef) =
    let modules =
        allModules m
        |> List.distinctBy (fun c -> c.name)
        |> List.map (fun c -> emitModule (c.name = m.name) c)

    [ "FIRRTL version 4.0.0"
      $"circuit {m.name} :"
      yield! modules ]
    |> String.concat "\n\n"
