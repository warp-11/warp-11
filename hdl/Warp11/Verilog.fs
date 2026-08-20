[<AutoOpen>]
module Warp11.Verilog

/// Emit `expr` zero-extended to `target` bits. Every operand is widened explicitly so
/// a sub-expression's width never depends on the context it lands in — Verilog's
/// context-determined width rules would otherwise let an inlined multiply truncate
/// when the assignment target is narrow. Module ports used to pin this for free.
/// A signed operation needs a *named* operand, because emission replicates the
/// sign bit by name. Reinterpretation is transparent to that — `asSInt` moves no
/// bits — so this sees through it to the signal underneath.
/// The sign bit of a value, as Verilog text. Reaches through reinterpretation
/// and through a narrowing shift, because neither moves the top bit.
let rec private signBitOf expr =
    match expr with
    | Ref (n, t) -> Some $"{n}[{t.Width - 1}]"
    // A literal's sign is known here, so it needs no name — and without this a
    // signed literal is the one value that cannot be padded, which is exactly
    // what a constant signed divisor asks for.
    | Lit (v, t) -> Some(if (v >>> (t.Width - 1)) &&& 1UL = 1UL then "1'b1" else "1'b0")
    | AsUInt v
    | AsSInt v -> signBitOf v
    | Shr (s, _) ->
        match s with
        | Ref (n, t) -> Some $"{n}[{t.Width - 1}]"
        | _ -> signBitOf s
    | _ -> None

let rec private namedRef expr =
    match expr with
    | Ref (n, t) -> Some(n, t.Width)
    | AsUInt v
    | AsSInt v -> namedRef v
    | _ -> None

let rec emitAt target expr =
    match expr with
    | Lit (v, t) -> $"%d{max target t.Width}'d%d{v}"
    | _ ->
        let w = width expr

        let core =
            match expr with
            | Lit _ -> failwith "unreachable"
            | Ref (n, _) -> n
            | Add (a, b) -> $"({emitAt w a} + {emitAt w b})"
            | Sub (a, b) -> $"({emitAt w a} - {emitAt w b})"
            // Both operands sign-extended to the full product width by explicit
            // replication, then multiplied as bit patterns: the low wa+wb bits of
            // that product are the exact signed product, and every width is
            // self-determined — no `$signed`, whose meaning would depend on the
            // context this expression is inlined into. Which multiply this is now
            // comes from the operands' type rather than from a second node.
            | Mul (a, b) when isSigned a ->
                let sext operand =
                    match namedRef operand with
                    | Some (n, sourceWidth) ->
                        "{{" + string (w - sourceWidth) + "{" + n + "[" + string (sourceWidth - 1) + "]}}, " + n + "}"
                    | None -> failwith "emit: signed multiply of a computed value — assign it to a wire first"

                $"({sext a} * {sext b})"
            | Mul (a, b) -> $"({emitAt w a} * {emitAt w b})"
            | Mux (c, t, f) -> $"({emitAt 1 c} ? {emitAt w t} : {emitAt w f})"
            | Concat (hi, lo) -> $"{{{emitAt (width hi) hi}, {emitAt (width lo) lo}}}"
            | Slice (source, hi, lo) when (namedRef source).IsSome ->
                $"{(namedRef source).Value |> fst}[{hi}:{lo}]"
            | Slice _ -> failwith "emit: slice of a computed value — assign it to a wire first"
            | Eq (a, b) ->
                let common = max (width a) (width b)
                $"({emitAt common a} == {emitAt common b})"
            // Signed compare as unsigned compare with both sign bits flipped —
            // adds 2^(w-1) to each side, mapping the signed range onto the
            // unsigned one order-preservingly. Needs no named operand, so unlike
            // a signed multiply this accepts computed values.
            | Lt (a, b) when isSigned a ->
                let cw = width a
                let flip = 1UL <<< (cw - 1)
                $"(({emitAt cw a} ^ %d{cw}'d%d{flip}) < ({emitAt cw b} ^ %d{cw}'d%d{flip}))"
            | Lt (a, b) ->
                let common = max (width a) (width b)
                $"({emitAt common a} < {emitAt common b})"
            // Dropping the low bits is a part-select, whatever the reading —
            // the sign, if there is one, rides on the top bit that survives.
            | Shr (source, sh) ->
                match namedRef source with
                | Some (n, sw) -> n + "[" + string (sw - 1) + ":" + string sh + "]"
                | None -> failwith "emit: shr of a computed value — assign it to a wire first"
            // A pad to the width the value already has is the value: without
            // this the concatenation below asks for zero bits of zero.
            // A barrel shifter. The left shift widens first — through `Pad`, so
            // a signed operand extends by its sign rather than by zeros, which
            // is also why a signed one needs a name.
            // Verilog's own reduction operators, which is what these are.
            // Signed division needs `$signed` — one of the two places it
            // appears. There is no bit trick for it the way the sign-flip
            // serves a signed compare, and the operands are named, so the
            // meaning does not depend on where this is inlined.
            //
            // Both operands extend to the *quotient's* width, which is one more
            // than the dividend's because MIN / -1 overflows. Verilog would do
            // that extension itself — `/` sizes its operands from context — but
            // an implicit width is what a width warning exists to catch, and
            // Verilator is right to object. Writing it out costs no gates.
            | Div (a, b) when isSigned a -> $"($signed({emitAt w (Pad(a, w))}) / $signed({emitAt w (Pad(b, w))}))"
            | Div (a, b) -> $"({emitAt w a} / {emitAt (width a) b})"
            | Rem (a, b) when isSigned a -> $"($signed({emitAt (width a) a}) %% $signed({emitAt (width a) b}))"
            | Rem (a, b) -> $"({emitAt (width a) a} %% {emitAt (width a) b})"
            | Reduce (AllBits, v) -> $"(&{emitAt (width v) v})"
            | Reduce (AnyBit, v) -> $"(|{emitAt (width v) v})"
            | Reduce (Parity, v) -> $"(^{emitAt (width v) v})"
            | DynamicShl (v, n) -> $"({emitAt w (Pad(v, w))} << {emitAt (width n) n})"
            | DynamicShr (v, n) when isSigned v ->
                // The other one. A variable-distance sign fill
                // has no width-explicit form — the constant case replicates a
                // named bit, and there is no such trick when the distance is a
                // signal. The operand is named and the width is the operand's,
                // so the meaning does not depend on where this is inlined.
                match namedRef v with
                | Some (name, _) -> $"($signed({name}) >>> {emitAt (width n) n})"
                | None -> failwith "emit: signed dynamic shift of a computed value — assign it to a wire first"
            | DynamicShr (v, n) -> $"({emitAt w v} >> {emitAt (width n) n})"
            | Pad (source, _) when w = width source -> emitAt w source
            | Pad (source, _) when not (isSigned source) -> $"{{{w - width source}'d0, {emitAt (width source) source}}}"
            | Pad (source, _) ->
                // Sign extension replicates a named bit, and `signBitOf` reaches
                // through a shift to find it — which is what makes `sra` a `pad`
                // of a `shr` and not a node of its own.
                match signBitOf source with
                | Some bit -> "{{" + string (w - width source) + "{" + bit + "}}, " + emitAt (width source) source + "}"
                | None -> failwith "emit: sign extension of a computed value — assign it to a wire first"
            // Reinterpretation moves no bits.
            | AsUInt v
            | AsSInt v -> emitAt target v
            | And (a, b) -> $"({emitAt w a} & {emitAt w b})"
            | Or (a, b) -> $"({emitAt w a} | {emitAt w b})"
            | Xor (a, b) -> $"({emitAt w a} ^ {emitAt w b})"
            | Not v -> $"(~{emitAt w v})"
            | MemRead (m, a, _) -> $"{m}[{emitAt (width a) a}]"

        if w >= target then
            core
        else
            $"{{%d{target - w}'d0, {core}}}"

let emit expr = emitAt (width expr) expr

let private nameAndWidth decl =
    match decl with
    | Input (n, t)
    | Output (n, t)
    | Wire (n, t) -> n, t.Width
    | Reg (n, t, _) -> n, t.Width
    | Memory(n, _, w, _, _) -> n, w

let private declaredWidth m target =
    m.decls
    |> List.tryPick (fun d ->
        let n, w = nameAndWidth d
        if n = target then Some w else None)

let private memShape m memName =
    m.decls
    |> List.tryPick (function
        | Memory(n, aw, w, _, _) when n = memName -> Some(aw, w)
        | _ -> None)

let checkWidths m =
    [ for stmt in m.stmts do
          match stmt with
          | Assign (t, v) ->
              match declaredWidth m t with
              | Some dw when dw <> width v ->
                  yield $"{m.name}.{t}: declared %d{dw} bits, driven %d{width v}"
              | _ -> ()
          | MemWrite (mem, addr, data, enable, _) ->
              match memShape m mem with
              | Some (aw, w) ->
                  if width addr <> aw then
                      yield $"{m.name}.{mem}: %d{aw}-bit address written with %d{width addr} bits"

                  if width data <> w then
                      yield $"{m.name}.{mem}: %d{w}-bit data written with %d{width data} bits"

                  if width enable <> 1 then
                      yield $"{m.name}.{mem}: write enable is %d{width enable} bits"
              | None -> yield $"{m.name}: write to undeclared mem '{mem}'"
          | Assert (cond, message) ->
              if width cond <> 1 then
                  yield $"{m.name}: assertion '{message}' is %d{width cond} bits, not 1" ]

let internal range w = if w = 1 then "" else $"[%d{w - 1}:0] "

/// Verilog puts one statement on one line and Verilator's preprocessor refuses a
/// line carrying more than 40,000 tokens, so a deep `If` fold can produce an
/// `assign` no tool will parse — the GEP operator engine's 39-state next-state
/// expression reached ~740 KB on a single line, which hid every design
/// containing it from the differential.
///
/// The emitter therefore splits an oversized expression across generated wires.
/// This is purely an emission concern: the IR is unchanged, the Sim never sees
/// it, and every design under the budget emits byte-for-byte as before — which
/// is what keeps the golden-Verilog diff a usable check.
///
/// The budget counts IR nodes rather than tokens because a node's emitted token
/// count is bounded by a small constant (a sign-extension replication is the
/// widest, ~10), so nodes are the stable proxy and 1,500 of them leaves a wide
/// margin under the real limit.
let internal splitBudget = 1500

/// An expression's children and how to put it back together — the one place
/// that enumerates the IR structurally, so `splitExpr` below stays shape-blind.
let private parts expr : Expr list * (Expr list -> Expr) =
    match expr with
    | Lit _
    | Ref _ -> [], (fun _ -> expr)
    | Add (a, b) -> [ a; b ], (fun c -> Add(c[0], c[1]))
    | Sub (a, b) -> [ a; b ], (fun c -> Sub(c[0], c[1]))
    | Mul (a, b) -> [ a; b ], (fun c -> Mul(c[0], c[1]))
    | Concat (a, b) -> [ a; b ], (fun c -> Concat(c[0], c[1]))
    | Eq (a, b) -> [ a; b ], (fun c -> Eq(c[0], c[1]))
    | Lt (a, b) -> [ a; b ], (fun c -> Lt(c[0], c[1]))
    | AsUInt v -> [ v ], (fun c -> AsUInt c[0])
    | AsSInt v -> [ v ], (fun c -> AsSInt c[0])
    | And (a, b) -> [ a; b ], (fun c -> And(c[0], c[1]))
    | Or (a, b) -> [ a; b ], (fun c -> Or(c[0], c[1]))
    | Xor (a, b) -> [ a; b ], (fun c -> Xor(c[0], c[1]))
    | Mux (c0, t, f) -> [ c0; t; f ], (fun c -> Mux(c[0], c[1], c[2]))
    | Not v -> [ v ], (fun c -> Not c[0])
    | Slice (s, hi, lo) -> [ s ], (fun c -> Slice(c[0], hi, lo))
    | Shr (s, sh) -> [ s ], (fun c -> Shr(c[0], sh))
    | Pad (s, w) -> [ s ], (fun c -> Pad(c[0], w))
    | Reduce (kind, v) -> [ v ], (fun c -> Reduce(kind, c[0]))
    | Div (a, b) -> [ a; b ], (fun c -> Div(c[0], c[1]))
    | Rem (a, b) -> [ a; b ], (fun c -> Rem(c[0], c[1]))
    | DynamicShl (v, n) -> [ v; n ], (fun c -> DynamicShl(c[0], c[1]))
    | DynamicShr (v, n) -> [ v; n ], (fun c -> DynamicShr(c[0], c[1]))
    | MemRead (m, a, w) -> [ a ], (fun c -> MemRead(m, c[0], w))

/// Rewrite `expr` so that it — and every wire `fresh` names along the way — is
/// at most `budget` nodes. Children are shrunk first, then the largest ones are
/// lifted out until the node itself fits, so hoists land at the widest cut
/// available rather than wherever the walk happened to be.
///
/// The named-operand rule survives for free: `slice`/`sra`/`signExtend`/`mulS`
/// already require declared signals, so their children are `Ref`s of size one
/// and are never the largest child of anything.
let private splitExpr (budget: int) (fresh: unit -> string) (expr: Expr) : Expr * (string * Expr) list =
    let hoists = ResizeArray<string * Expr>()

    let hoist (e: Expr) =
        let n = fresh ()
        hoists.Add(n, e)
        Ref(n, typeOf e)

    let rec go expr : Expr * int =
        match expr with
        | Lit _
        | Ref _ -> expr, 1
        | _ ->
            let children, rebuild = parts expr

            let rec shrink (cs: (Expr * int) list) =
                let total = 1 + List.sumBy snd cs

                if total <= budget then
                    cs
                else
                    let largest = cs |> List.map snd |> List.max

                    if largest <= 1 then
                        cs // nothing left to lift — a node with only Refs always fits
                    else
                        let idx = cs |> List.findIndex (fun (_, s) -> s = largest)
                        shrink (cs |> List.mapi (fun i c -> if i = idx then hoist (fst c), 1 else c))

            let final = shrink (List.map go children)
            rebuild (List.map fst final), 1 + List.sumBy snd final

    let root, _ = go expr
    root, List.ofSeq hoists

/// A module needs the clock pair iff it holds registers anywhere in its hierarchy —
/// warp11's transitive clk/rst threading, in miniature.
/// A module needs the clock pair if it has state — or an assertion, which is
/// checked on the edge. Checking combinationally instead would avoid the ports,
/// and would also fire on values a settling design passes through on its way to
/// being right, so a claim is edge-triggered like everything else. The
/// consequence is worth knowing: adding an assertion to a purely combinational
/// module adds `clk`/`rst` to its boundary.
let rec needsClk m =
    m.decls
    |> List.exists (function
        | Reg _
        | Memory _ -> true
        | _ -> false)
    || m.stmts
       |> List.exists (function
           | Assert _ -> true
           | _ -> false)
    || m.instances |> List.exists (fun i -> needsClk i.child)

let emitVerilog m =
    let isReg n =
        m.decls
        |> List.exists (function
            | Reg (rn, _, _) -> rn = n
            | _ -> false)

    let clk = m.clock.clockPort
    // The reset every internal consumer sees is active-high; an active-low
    // port derives it as a wire, warp11's `${resetPort}_pos` arrangement.
    let rstInternal =
        if m.clock.resetActiveLow then m.clock.resetPort + "_pos" else m.clock.resetPort

    let ports =
        [ if needsClk m then
              yield $"input {clk}"
              yield $"input {m.clock.resetPort}"
          for d in m.decls do
              match d with
              | Input (n, t) -> yield $"input {range t.Width}{n}"
              | Output (n, t) -> yield $"output {range t.Width}{n}"
              | _ -> () ]

    // Split oversized expressions onto generated wires (see `splitBudget`). The
    // generated names carry the statement's own stem so the emitted Verilog
    // still reads, and a counter plus a collision check keeps them unique.
    let taken =
        System.Collections.Generic.HashSet<string>([ for d in m.decls -> fst (nameAndWidth d) ])

    let splits = ResizeArray<string * Expr>()
    let mutable splitIndex = 0

    let split (stem: string) (e: Expr) =
        let root, hoists =
            splitExpr
                splitBudget
                (fun () ->
                    let mutable n = $"{stem}__s%d{splitIndex}"

                    while not (taken.Add n) do
                        splitIndex <- splitIndex + 1
                        n <- $"{stem}__s%d{splitIndex}"

                    splitIndex <- splitIndex + 1
                    n)
                e

        splits.AddRange hoists
        root

    let stmts =
        [ for stmt in m.stmts ->
              match stmt with
              | Assign (t, v) -> Assign(t, split t v)
              | MemWrite (mem, addr, data, enable, mask) ->
                  MemWrite(
                      mem,
                      split mem addr,
                      split mem data,
                      split mem enable,
                      Option.map (split mem) mask)
              | Assert (cond, message) -> Assert(split "assert" cond, message) ]

    let bodyDecls =
        [ if needsClk m && m.clock.resetActiveLow then
              yield $"    wire {rstInternal} = ~{m.clock.resetPort};"
          for d in m.decls do
              match d with
              | Wire (n, t) -> yield $"    wire {range t.Width}{n};"
              | Reg (n, t, _) -> yield $"    reg {range t.Width}{n};"
              // The attribute is the whole point of `RamStyle`: it takes the
              // storage decision away from the synthesiser, so an asynchronous
              // read means on silicon what it means here.
              | Memory(n, aw, w, _, style) ->
                  let attribute =
                      match style with
                      | Unspecified -> ""
                      | Distributed -> "(* ram_style = \"distributed\" *) "
                      | Block -> "(* ram_style = \"block\" *) "

                  yield $"    {attribute}reg {range w}{n} [0:%d{(1 <<< aw) - 1}];"
              | _ -> ()
          for n, e in splits do
              yield $"    wire {range (width e)}{n};" ]

    // Initialized mems: an `initial` block Vivado turns into BRAM INIT. Every
    // entry is written explicitly — zeros included — so the emitted Verilog
    // matches the Sim's zero default instead of leaving the tail X, which is
    // what makes the two agree from t=0.
    let memInits =
        [ for d in m.decls do
              match d with
              | Memory(n, aw, w, Some contents, _) ->
                  yield "    initial begin"

                  for i in 0 .. (1 <<< aw) - 1 do
                      let v = if i < contents.Length then contents[i] else 0UL
                      yield $"        {n}[%d{i}] = %d{w}'d%d{v};"

                  yield "    end"
              | _ -> () ]

    let combinational =
        [ for n, e in splits do
              yield $"    assign {n} = {emit e};"
          for stmt in stmts do
              match stmt with
              | Assign (t, v) when not (isReg t) -> yield $"    assign {t} = {emit v};"
              | Assign _
              | MemWrite _
              | Assert _ -> () ]

    let resets =
        [ for d in m.decls do
              match d with
              // A register with no reset contributes no line here, which is
              // exactly how it comes to hold its value through reset — and how
              // synthesis sees a flop with no reset at all.
              | Reg (n, t, Some init) -> yield $"            {n} <= %d{t.Width}'d%d{init};"
              | _ -> () ]

    // The mem array carries no reset, mirroring BRAM; read-port regs are ordinary
    // regs and do reset.
    let sequential =
        [ for stmt in stmts do
              match stmt with
              | Assign (t, v) when isReg t -> yield $"            {t} <= {emit v};"
              | Assign _ -> ()
              | MemWrite (mem, addr, data, enable, None) ->
                  yield $"            if ({emit enable}) {mem}[{emit addr}] <= {emit data};"
              // A masked write is the byte-enable template: one guarded
              // assignment per lane, nested inside the write enable. That
              // nesting is not decoration — it is the shape a synthesiser
              // recognises as a block RAM's per-lane write ports, and a flat
              // `if (en && mask[i])` per lane is not.
              | MemWrite (mem, addr, data, enable, Some mask) ->
                  let lanes = width mask
                  let laneWidth = width data / lanes
                  yield $"            if ({emit enable}) begin"

                  for i in 0 .. lanes - 1 do
                      let hi = (i + 1) * laneWidth - 1
                      let lo = i * laneWidth

                      yield
                          $"                if ({emit mask}[%d{i}]) {mem}[{emit addr}][%d{hi}:%d{lo}] <= {emit data}[%d{hi}:%d{lo}];"

                  yield "            end"
              // Assertions are not synthesizable and land in their own
              // translate_off block below, not in the design's always block.
              | Assert _ -> () ]

    let always =
        if List.isEmpty sequential && List.isEmpty resets then
            []
        elif List.isEmpty resets
             && not (stmts |> List.exists (function MemWrite _ -> true | _ -> false)) then
            // Nothing to reset, so no reset branch — and then the module's flops
            // genuinely have no reset, which is the point of asking for one.
            // Only reachable when every register here is `regNoReset` and there
            // are no memory writes, since a write is gated on reset by living
            // in the else branch.
            [ yield $"    always @(posedge {clk}) begin"
              yield! (sequential |> List.map (fun l -> l.Substring 4))
              yield "    end" ]
        else
            [ yield $"    always @(posedge {clk}) begin"
              yield $"        if ({rstInternal}) begin"
              yield! resets
              yield "        end else begin"
              yield! sequential
              yield "        end"
              yield "    end" ]

    // Assertions live in their own always block inside a translate_off region:
    // synthesis skips the region, simulation does not. Held off during reset,
    // where a design has not yet promised anything.
    let assertions =
        [ for stmt in stmts do
              match stmt with
              | Assert (cond, message) ->
                  let escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"")
                  yield $"            if (!({emit cond})) $fatal(1, \"assertion failed: {escaped}\");"
              | Assign _
              | MemWrite _ -> () ]

    let assertBlock =
        if List.isEmpty assertions then
            []
        else
            [ yield "// synthesis translate_off"
              yield $"    always @(posedge {clk}) begin"
              yield $"        if (!{rstInternal}) begin"
              yield! assertions
              yield "        end"
              yield "    end"
              yield "// synthesis translate_on" ]

    let instantiations =
        [ for inst in m.instances do
              let conns =
                  [ if needsClk inst.child then
                        yield $".{inst.child.clock.clockPort}({clk})"
                        // The parent's internal reset is active-high; invert it
                        // when the child's port is itself active-low.
                        let childRst =
                            if inst.child.clock.resetActiveLow then $"~{rstInternal}" else rstInternal

                        yield $".{inst.child.clock.resetPort}({childRst})"
                    for d in inst.child.decls do
                        match d with
                        | Input (n, _)
                        | Output (n, _) -> yield $".{n}({inst.instName}_{n})"
                        | _ -> () ]

              let connList = String.concat ", " conns
              yield $"    {inst.child.name} {inst.instName} ({connList});" ]

    let portList = String.concat ", " ports

    String.concat
        "\n"
        [ yield $"module {m.name} ({portList});"
          yield! bodyDecls
          yield! memInits
          yield! instantiations
          yield! combinational
          yield! always
          yield! assertBlock
          yield "endmodule" ]

let allModules m =
    let rec collect md =
        [ for i in md.instances do
              yield! collect i.child
              yield i.child ]

    collect m @ [ m ]

/// One name, one module. `List.distinct` is structural, so a module reached by two
/// paths collapses and only a genuine disagreement survives to be counted.
let checkNames m =
    allModules m
    |> List.distinct
    |> List.countBy (fun c -> c.name)
    |> List.filter (fun (_, n) -> n > 1)
    |> List.map (fun (name, n) -> $"{name}: %d{n} different modules share this name")

/// A stream has exactly one consumer: every registered ready net must be driven
/// exactly once. Zero means a created stream was never consumed (an undriven port
/// in the emitted Verilog); two means two consumers fight over one producer.
let checkStreams m =
    [ for md in allModules m |> List.distinct do
          for readyNet, drivers in md.streamReadies do
              if drivers <> 1 then
                  yield
                      $"{md.name}: stream ready '{readyNet}' driven %d{drivers} times (a stream has exactly one consumer)" ]

let emitDesign m =
    let widthProblems =
        [ for md in allModules m |> List.distinct do
              yield! checkWidths md ]

    match widthProblems @ checkNames m @ checkStreams m with
    | [] -> ()
    | problems -> failwith (String.concat "; " problems)

    allModules m
    |> List.distinctBy (fun c -> c.name)
    |> List.map emitVerilog
    |> String.concat "\n\n"
