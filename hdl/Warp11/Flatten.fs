[<AutoOpen>]
module Warp11.Flatten

// ---------------------------------------------------------------------------
// Simulation. Flatten the hierarchy, then evaluate the flat design directly —
// warp11's sim architecture in miniature, with none of its performance work.

let rec private renameRefs prefix expr =
    let r = renameRefs prefix

    match expr with
    | Lit _ -> expr
    | Ref (n, t) -> Ref($"{prefix}_{n}", t)
    | Add (a, b) -> Add(r a, r b)
    | Sub (a, b) -> Sub(r a, r b)
    | Mul (a, b) -> Mul(r a, r b)
    | Mux (c, t, f) -> Mux(r c, r t, r f)
    | Concat (hi, lo) -> Concat(r hi, r lo)
    | Slice (s, hi, lo) -> Slice(r s, hi, lo)
    | Eq (a, b) -> Eq(r a, r b)
    | Lt (a, b) -> Lt(r a, r b)
    | AsUInt v -> AsUInt(r v)
    | AsSInt v -> AsSInt(r v)
    | And (a, b) -> And(r a, r b)
    | Or (a, b) -> Or(r a, r b)
    | Xor (a, b) -> Xor(r a, r b)
    | Not v -> Not(r v)
    | Shr (s, n) -> Shr(r s, n)
    | Pad (s, w) -> Pad(r s, w)
    | Reduce (kind, v) -> Reduce(kind, r v)
    | Div (a, b) -> Div(r a, r b)
    | Rem (a, b) -> Rem(r a, r b)
    | DynamicShl (v, n) -> DynamicShl(r v, r n)
    | DynamicShr (v, n) -> DynamicShr(r v, r n)
    | MemRead (m, a, w) -> MemRead($"{prefix}_{m}", r a, w)

/// Inline every instance, prefixing child-internal names with the instance name.
/// For the simulator only. The parent's `instName_port` wires already exist
/// (Instance created them), so only child internals move; child port
/// declarations vanish into those wires.
let rec private flattenUnchecked (m: ModuleDef) : ModuleDef =
    let inlined =
        [ for inst in m.instances do
              let child = flattenUnchecked inst.child
              let prefix = inst.instName

              let decls =
                  [ for d in child.decls do
                        match d with
                        | Input _
                        | Output _ -> ()
                        | Wire (n, w) -> yield Wire($"{prefix}_{n}", w)
                        | Reg (n, w, init) -> yield Reg($"{prefix}_{n}", w, init)
                        | Memory(n, aw, w, init, style) -> yield Memory($"{prefix}_{n}", aw, w, init, style) ]

              let stmts =
                  [ for stmt in child.stmts ->
                        match stmt with
                        | Assign (t, v) -> Assign($"{prefix}_{t}", renameRefs prefix v)
                        | MemWrite (mem, a, d, e, k) ->
                            MemWrite(
                                $"{prefix}_{mem}",
                                renameRefs prefix a,
                                renameRefs prefix d,
                                renameRefs prefix e,
                                Option.map (renameRefs prefix) k)
                        | Assert (cond, message) ->
                            // The message keeps the instance path, or a claim
                            // that fires in one of 104 lanes says nothing about
                            // which.
                            Assert(renameRefs prefix cond, $"{prefix}: {message}") ]

              yield decls, stmts ]

    { m with
        decls = m.decls @ List.collect fst inlined
        stmts = m.stmts @ List.collect snd inlined
        instances = [] }

let private declaredName =
    function
    | Input (n, _)
    | Output (n, _)
    | Wire (n, _)
    | Reg (n, _, _)
    | Memory(n, _, _, _, _) -> n

/// Flatten, and refuse a design whose signals do not survive it distinctly.
///
/// `renameRefs` prefixes unconditionally, so a grandchild's `sig` inside
/// instance `gc` lands on `gc_sig` — and a parent that already declares a wire
/// named `gc_sig` collapses two different signals onto one name. The Sim then
/// evaluates one of them twice and silently answers with the wrong value; the
/// emitter preserves hierarchy and never flattens, so the Verilog stays
/// correct and only the simulated design is wrong.
///
/// **This is checked here rather than at the declaration**, which is where the
/// one-declaration rule lives, because there is no declaration site at which
/// it is wrong: `gc_sig` in the parent and `sig` in the grandchild are both
/// legal in their own scopes, and only flattening brings them into one. This
/// is the earliest point at which the collision exists.
///
/// Realistic rather than contrived: `rd`/`wr` are the accepted AXI short
/// forms, so an instance `rd` with an internal `data`, beside a parent wire
/// `rd_data`, is an ordinary naming accident.
let flatten (m: ModuleDef) : ModuleDef =
    let flat = flattenUnchecked m

    let collisions =
        flat.decls
        |> List.countBy declaredName
        |> List.filter (fun (_, n) -> n > 1)

    match collisions with
    | [] -> flat
    | _ ->
        let detail =
            collisions
            |> List.map (fun (name, n) -> $"'{name}' x%d{n}")
            |> String.concat ", "

        failwith
            $"flatten '{m.name}': %s{detail} — an instance's flattened child signal \
              collides with a name already declared beside it. Rename either the \
              instance or the signal; unchanged, the simulator would evaluate one \
              and silently answer for the other."
