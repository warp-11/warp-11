[<AutoOpen>]
module Warp11.Dsl

/// An ordered last-connect-wins assignment scope: a later assign to the same
/// target replaces the earlier value, and emission order is first-touch order —
/// deliberately not a bare Dictionary, whose enumeration order is unspecified.
/// Last-connect-wins is the *merge* mechanism (an If branch folds into its parent
/// by re-Setting the target), not a user-facing affordance — `Assign` rejects a
/// second `==>` at one level, so it is unreachable from a design body.
type private Scope() =
    let values = System.Collections.Generic.Dictionary<string, Expr>()
    let order = ResizeArray<string>()

    member _.Set(t, v) =
        if not (values.ContainsKey t) then order.Add t
        values[t] <- v

    member _.TryGet t =
        match values.TryGetValue t with
        | true, v -> Some v
        | _ -> None

    member _.Items = [ for t in order -> t, values[t] ]

/// A state machine's elaboration-time record. `states` is what each code means —
/// the debugger's decode, and the one thing an encoded state register cannot
/// carry itself. `reached` is which states something has named as a destination,
/// so finalize can say that a state exists with no way in. `machine` in the
/// stdlib creates one; nothing else should.
type StateMachineRecord =
    { stateReg: string
      states: (uint64 * string) list
      reached: System.Collections.Generic.HashSet<uint64> }

/// The module under construction — the ambient thing every declaration and
/// every `==>` reaches without being passed one.
///
/// Held on a stack rather than threaded through the design, which is what
/// makes a module body ordinary F# code: `mul8 a b` is a call, not a call
/// with a context argument. Designs rarely name this type; they get it from
/// `design`, `moduleDef` or `defineModule` and never see it again.
type Builder(name: string, ?clockSpec: ClockSpec) =
    do requireNotVerilogKeyword name "a module"
    let clock = defaultArg clockSpec defaultClock
    let decls = ResizeArray<Decl>()
    let instances = ResizeArray<Instance>()
    let streamReadies = ResizeArray<string>()
    let probes = ResizeArray<string>()
    let machines = ResizeArray<StateMachineRecord>()
    let nameCounts = System.Collections.Generic.Dictionary<string, int>()
    // Raw write calls, each with its enable already ANDed with the If conditions
    // active at the call. Def merges them into one write site per mem.
    let memWrites = ResizeArray<string * Expr * Expr * Expr * Expr option>()
    // Assertions, each with the If conditions active at the call already folded
    // in as an implication.
    let asserts = ResizeArray<Expr * string>()
    // The conjunction of active If conditions — what the Rust spike's `when`
    // lacked and mem write enables need.
    let conds = System.Collections.Generic.Stack<Expr>()

    // Assignment state. Unconditional assigns live in baseScope; each If branch
    // gets its own scope and merges into its parent as a Mux when the block ends,
    // so nesting AND-folds structurally and stmts end up one assign per target.
    let baseScope = Scope()
    let scopes = System.Collections.Generic.Stack<Scope>()
    let assignCounts = System.Collections.Generic.Dictionary<string, int>()
    let declTypes = System.Collections.Generic.Dictionary<string, GroundType>()
    // How each name was declared, kept for the duplicate-declaration message:
    // what the first one was is the half that says how the collision happened.
    let declKinds = System.Collections.Generic.Dictionary<string, string>()
    let regNames = System.Collections.Generic.HashSet<string>()
    // A completed If whose Else may still arrive. Flushed (merged with no
    // else branch) by the next statement at the same level.
    let mutable pendingIf: (Expr * Scope) option = None

    member private _.NextName(childName: string) =
        let stem =
            string (System.Char.ToLowerInvariant childName[0]) + childName[1..]

        let n =
            match nameCounts.TryGetValue stem with
            | true, c -> c + 1
            | _ -> 1

        nameCounts[stem] <- n
        $"{stem}_{n}"

    /// A fresh unique name for library-internal signals (fork readies, merge
    /// state) — the combinator, not the user, owns these names.
    member this.FreshName(stem: string) = this.NextName stem

    /// One declaration per name. An instance's staging wires are named
    /// `{instance}_{port}` in the PARENT's namespace, so they share it with
    /// ordinary declarations — `Stream.out "b_low"` beside an instance named `b`
    /// is the measured case. A duplicate emitted a port redeclared as a wire and
    /// self-assigned (`assign b_low_data = b_low_data`), which elaboration, lint
    /// and synthesis all accepted.
    member private _.DeclareAs(decl, n, (t: GroundType), kind) =
        requireNotVerilogKeyword n kind

        match declKinds.TryGetValue n with
        | true, prior -> failwith $"'{n}' is declared twice in '{name}' — {prior}, then {kind}"
        | _ -> ()

        decls.Add decl
        declTypes[n] <- t
        declKinds[n] <- kind

        (match decl with
         | Reg _ -> regNames.Add n |> ignore
         | _ -> ())

        Ref(n, t)

    member private this.Declare(decl, n, (t: GroundType)) =
        this.DeclareAs(
            decl,
            n,
            t,
            match decl with
            | Input _ -> "an input port"
            | Output _ -> "an output port"
            | Wire _ -> "a wire"
            | Reg _ -> "a reg"
            | Memory _ -> "a mem"
        )

    /// Declare an input port.
    member this.Input(n, t: GroundType) = this.Declare(Input(n, t), n, t)
    /// Declare an output port.
    member this.Output(n, t: GroundType) = this.Declare(Output(n, t), n, t)
    /// Declare a wire.
    member this.Wire(n, t: GroundType) = this.Declare(Wire(n, t), n, t)
    /// Declare a register and the value it takes under reset.
    member this.Reg(n, t: GroundType, init: uint64) = this.Declare(Reg(n, t, Some init), n, t)

    /// A register with no reset: it holds its value while reset is asserted.
    /// Not the default, and should not be — a state machine that survives reset
    /// is a bug. This is for the data path, where the reset net buys nothing and
    /// costs fanout, routing and SRL inference.
    member this.RegNoReset(n, t: GroundType) = this.Declare(Reg(n, t, None), n, t)

    // A bare width still means unsigned, so every existing declaration — and the
    // `moduleDef` API's `m.Input("a", 8)` — reads exactly as it did.
    /// An input port at a bare width, which still means unsigned.
    member this.Input(n, w: int) = this.Input(n, UInt w)
    /// An output port at a bare width.
    member this.Output(n, w: int) = this.Output(n, UInt w)
    /// A wire at a bare width.
    member this.Wire(n, w: int) = this.Wire(n, UInt w)
    /// A register at a bare width.
    member this.Reg(n, w: int, init) = this.Reg(n, UInt w, init)

    /// The value `t` would currently read as: innermost If scope first, then base.
    member private _.CurrentValue t =
        let inScopes =
            scopes
            |> Seq.tryPick (fun s -> s.TryGet t)

        match inScopes with
        | Some v -> Some v
        | None -> baseScope.TryGet t

    /// The fall-through value for a branch that does not assign `t`: whatever it
    /// already was — and a reg with no prior assignment holds itself, while a wire
    /// in that position is an error (warp11's rule, verbatim).
    member private this.PriorOrHold t =
        match this.CurrentValue t with
        | Some v -> v
        | None when regNames.Contains t -> Ref(t, declTypes[t])
        | None ->
            failwith
                $"'{t}' is assigned inside If without an unconditional default — a reg holds its value there; a wire has nothing to hold"

    member private _.Set(t, v) =
        if scopes.Count > 0 then
            (scopes.Peek()).Set(t, v)
        else
            baseScope.Set(t, v)

    /// The statement after an If has arrived, so that If is else-less: merge its
    /// branch as Mux(cond, branch value, fall-through).
    member private this.FlushPending() =
        match pendingIf with
        | None -> ()
        | Some (cond, thenScope) ->
            pendingIf <- None

            for t, v in thenScope.Items do
                this.Set(t, Mux(cond, v, this.PriorOrHold t))

    /// Open a conditional scope. The branch's assignments are collected and folded
    /// into the parent as muxes when the block ends, so a register with no
    /// unconditional default holds its value and a wire without one is an error.
    member this.If(cond, thenBody: unit -> unit) =
        this.FlushPending()

        if width cond <> 1 then
            failwith "If requires a 1-bit condition"

        let scope = Scope()
        scopes.Push scope
        conds.Push cond
        thenBody ()
        this.FlushPending() // an inner If left dangling merges inside this branch
        conds.Pop() |> ignore
        scopes.Pop() |> ignore
        pendingIf <- Some(cond, scope)

    /// The other branch of the `If` immediately preceding. Anything in between
    /// seals that `If` as else-less and this then fails.
    member this.Else(elseBody: unit -> unit) =
        match pendingIf with
        | None -> failwith "Else must immediately follow its If"
        | Some (cond, thenScope) ->
            pendingIf <- None
            let elseScope = Scope()
            scopes.Push elseScope
            conds.Push(Not cond)
            elseBody ()
            this.FlushPending()
            conds.Pop() |> ignore
            scopes.Pop() |> ignore

            // Merge both branches at once — the pending merge never ran, so
            // PriorOrHold still sees the value from before the whole If block.
            let elseOnly =
                [ for t, v in elseScope.Items do
                      if (thenScope.TryGet t).IsNone then yield t, v ]

            for t, thenV in thenScope.Items do
                let elseV =
                    match elseScope.TryGet t with
                    | Some v -> v
                    | None -> this.PriorOrHold t

                this.Set(t, Mux(cond, thenV, elseV))

            for t, elseV in elseOnly do
                this.Set(t, Mux(cond, this.PriorOrHold t, elseV))

    /// One unconditional driver per signal, and one per If branch — a second `==>`
    /// at the same scope would silently discard the first (the Scope below is
    /// last-connect-wins, because that is how an If branch merges). Conditional
    /// override is unaffected: it lands in a child scope, which is exactly the
    /// `default ==> w` + `If c (other ==> w)` shape `PriorOrHold` already requires.
    member private this.RequireUndriven(n: string) =
        let here = if scopes.Count > 0 then scopes.Peek() else baseScope

        if (here.TryGet n).IsSome then
            failwith
                $"'{n}' is assigned twice at the same level in '{name}' — the second would silently replace the first; a conditional override belongs inside If"

    /// Drive a declared signal. A second drive at the same scope is refused — the
    /// bug it catches is silent through elaboration, lint and synthesis — while the
    /// scope underneath is last-connect-wins, which is how a branch merges into its
    /// parent.
    member this.Assign(target, value) =
        match target with
        | Ref (n, _) ->
            this.FlushPending()
            this.RequireUndriven n

            assignCounts[n] <-
                (match assignCounts.TryGetValue n with
                 | true, c -> c + 1
                 | _ -> 1)

            this.Set(n, value)
        | _ -> failwith "assign target must be a declared signal"

    /// Record a stream's ready net so `checkStreams` can judge at emission whether
    /// it ended up with exactly one consumer.
    member _.RegisterStreamReady(ready) =
        match ready with
        | Ref (n, _) -> streamReadies.Add n
        | _ -> failwith "a stream's ready must be a declared net"

    /// Record a stall probe, so `streamReport` can find its counters later.
    member _.RegisterProbe(name: string) = probes.Add name

    /// Record a state machine, so the debugger can show a state register by name
    /// rather than as the number it is.
    member _.RegisterStateMachine(record: StateMachineRecord) = machines.Add record

    /// Declare a memory of 2^addrWidth words. `style` decides what it becomes on
    /// silicon, and with it which reads are legal — the combinational read is only
    /// allowed on distributed storage.
    member this.Memory(n, addrWidth, memWidth, init, style) : Mem =
        this.Declare(Memory(n, addrWidth, memWidth, init, style), n, UInt memWidth) |> ignore

        { memName = n
          addrWidth = addrWidth
          memWidth = memWidth
          style = style }

    /// Record a write. Several writes to one memory fold into a single
    /// priority-muxed write site, because two write sites stop a synthesiser
    /// inferring a block RAM even when they are mutually exclusive.
    member this.Write(mem: Mem, addr, data, enable, mask: Expr option) =
        this.FlushPending() // a write is a statement: it seals a pending If

        if width enable <> 1 then
            failwith $"write enable for '{mem.memName}' must be 1 bit"

        match mask with
        | None -> ()
        | Some m ->
            let lanes = width m

            if lanes < 1 || mem.memWidth % lanes <> 0 then
                failwith
                    $"masked write on '{mem.memName}': a %d{lanes}-bit mask does not divide a %d{mem.memWidth}-bit word into equal lanes"

        let effective = conds |> Seq.fold (fun e c -> And(c, e)) enable
        memWrites.Add(mem.memName, addr, data, effective, mask)

    /// Sync read, BRAM-shaped: the address samples into a read register at the
    /// clock edge, so the value arrives a cycle later — and a same-cycle write to
    /// the same address is not seen (read-first).
    /// A claim that must hold on every edge. Inside `If` it is conditional —
    /// `!active || cond` — because a claim about a branch says nothing about
    /// the cycles that branch is not taken.
    member this.AssertThat(cond: Expr, message: string) =
        this.FlushPending() // an assertion is a statement: it seals a pending If

        if width cond <> 1 then
            failwith $"an assertion needs a 1-bit condition, got %d{width cond} bits"

        asserts.Add(conds |> Seq.fold (fun claim active -> Or(Not active, claim)) cond, message)

    /// A read that answers next cycle, as a register holding the word.
    member this.SyncRead(mem: Mem, addr) =
        let r = this.Reg(this.NextName $"{mem.memName}_rd", mem.memWidth, 0UL)
        this.Assign(r, MemRead(mem.memName, addr, mem.memWidth))
        r

    /// Instantiate a module under a given name, returning it as a function. Its
    /// ports become `{instName}_{port}` staging wires in this module, each carrying
    /// the port's own type so a signed port stays signed across the boundary.
    member this.Instance<'io, 'fn>(instName: string, tm: TypedModule<'io, 'fn>) : 'fn =
        requireNotVerilogKeyword instName "an instance"
        instances.Add { instName = instName; child = tm.def }

        let staging = $"a staging wire for instance '{instName}'"

        // The staging wire takes the port's own type, so a signed port stays
        // signed across the boundary rather than arriving as raw bits.
        for d in tm.def.decls do
            match d with
            | Input (n, t)
            | Output (n, t) ->
                let stagingNet = $"{instName}_{n}"
                this.DeclareAs(Wire(stagingNet, t), stagingNet, t, staging) |> ignore
            | _ -> ()

        // The staging net carries the port's declared type, so a signed port
        // stays signed across an instantiation.
        let net n w = Ref($"{instName}_{n}", UInt w)
        let netAs n (t: GroundType) = Ref($"{instName}_{n}", t)

        tm.apply
            this
            (tm.io
                { inPort = net
                  outPort = net
                  inPortAs = netAs
                  outPortAs = netAs })

    /// The same, with a name derived from the module's.
    member this.Instance<'io, 'fn>(tm: TypedModule<'io, 'fn>) : 'fn =
        this.Instance(this.NextName tm.def.name, tm)

    /// Finish elaboration and hand back the module. Dangling conditionals flush
    /// here, and a state machine with a state nothing transitions to fails here.
    member this.Def =
        this.FlushPending()

        // A state nothing transitions to is dead logic, and the hand-encoded
        // form cannot notice: `sFoo` is a number, and an unused number reads
        // exactly like a used one.
        for machine in machines do
            let unreachable =
                [ for code, stateName in machine.states do
                      if not (machine.reached.Contains code) then yield stateName ]

            if not (List.isEmpty unreachable) then
                failwith
                    $"""state machine '{machine.stateReg}' in '{name}' can never reach {String.concat ", " unreachable} — nothing transitions there"""

        // One write site per mem: fold this mem's write calls into a priority mux
        // (a later call wins when both fire), because two write sites kill BRAM
        // inference even when mutually exclusive.
        // A masked write slices its data and its mask per lane, and Verilog can
        // only part-select a *name*. The merged expressions are mux trees, so
        // they are given names here — two wires per masked mem, and none at all
        // for an unmasked one, which is why unmasked emission is untouched.
        let laneWires = ResizeArray<Decl * Stmt>()

        let asWire (nm: string) (e: Expr) =
            let t = typeOf e
            laneWires.Add(Wire(nm, t), Assign(nm, e))
            Ref(nm, t)

        let mergedWrites =
            [ for memName in List.distinct [ for m, _, _, _, _ in memWrites -> m ] ->
                  let ws =
                      [ for m, a, d, e, k in memWrites do
                            if m = memName then yield a, d, e, k ]

                  // Every write on one mem has to agree on the lane count, or
                  // the merged site would have no single meaning for a mask bit.
                  let laneCounts =
                      ws |> List.choose (fun (_, _, _, k) -> k) |> List.map width |> List.distinct

                  if List.length laneCounts > 1 then
                      failwith
                          $"""writes to '{memName}' disagree on lane count ({String.concat ", " [ for l in laneCounts -> string l ]}) — they merge into one write site, so one mask bit would mean different widths depending on which write won"""

                  // A whole-word write among masked ones is an all-lanes mask.
                  // With no masked write anywhere the mem keeps `None` and emits
                  // exactly what it emitted before masks existed.
                  let widen (k: Expr option) =
                      match k, laneCounts with
                      | Some m, _ -> Some m
                      | None, [ lanes ] -> Some(Lit(maskOf lanes, UInt lanes))
                      | None, _ -> None

                  let addr, data, enable, mask =
                      ws
                      |> List.map (fun (a, d, e, k) -> a, d, e, widen k)
                      |> List.reduce (fun (pa, pd, pe, pk) (a, d, e, k) ->
                          Mux(e, a, pa),
                          Mux(e, d, pd),
                          Or(e, pe),
                          match k, pk with
                          | Some m, Some pm -> Some(Mux(e, m, pm))
                          | _ -> None)

                  match mask with
                  | None -> MemWrite(memName, addr, data, enable, None)
                  | Some k ->
                      MemWrite(
                          memName,
                          addr,
                          asWire $"{memName}_wdata" data,
                          enable,
                          Some(asWire $"{memName}_wmask" k)) ]

        { name = name
          decls = List.ofSeq decls @ [ for d, _ in laneWires -> d ]
          stmts =
            [ for t, v in baseScope.Items -> Assign(t, v) ]
            @ [ for _, a in laneWires -> a ]
            @ mergedWrites
            @ [ for cond, message in asserts -> Assert(cond, message) ]
          instances = List.ofSeq instances
          clock = clock
          streamReadies =
            [ for n in streamReadies ->
                  n,
                  (match assignCounts.TryGetValue n with
                   | true, c -> c
                   | _ -> 0) ]
          probes = List.ofSeq probes
          stateMachines = [ for machine in machines -> machine.stateReg, machine.states ] }

/// A module definition together with the two things that make it callable: how
/// to view its ports as a typed value, and how to wire an instance up as a
/// function of that view. `defineModule` builds one; `instance` and the `lift`
/// family turn one into something a call site can apply.
and TypedModule<'io, 'fn> =
    { def: ModuleDef
      io: Ports -> 'io
      apply: Builder -> 'io -> 'fn }

/// The module currently being elaborated. A stack, so a design may define another
/// inside itself. This is the price of `mul8 a b` being a plain call: the builder
/// has to be reachable without being passed.
let private elaborating = System.Collections.Generic.Stack<Builder>()

let internal current () =
    if elaborating.Count = 0 then
        failwith "no module is being elaborated — this belongs inside a design { }"

    elaborating.Peek()

/// Elaborate a module from a body that takes the builder explicitly. The
/// untyped seam — no port view, no call shape — used where something wants a
/// `ModuleDef` and nothing intends to instantiate it as a function.
let moduleDef name (body: Builder -> unit) =
    let b = Builder(name)
    elaborating.Push b

    try
        body b
    finally
        elaborating.Pop() |> ignore

    b.Def

/// Define a module once, with a typed view of its ports and a call shape.
///
/// `io` declares the ports and packages them however the definition wants to
/// see them; `apply` says what instantiating one *is* at a call site — usually
/// "drive these inputs, hand back that output"; `body` is the module's
/// contents, elaborated with the module ambient so it reads like any other
/// design code.
///
/// The result is re-runnable: `io` runs again per instance, so a call site
/// gets fresh port references rather than a shared record.
let defineModule name (io: Ports -> 'io) (apply: Builder -> 'io -> 'fn) (body: 'io -> Builder -> unit) =
    let b = Builder(name)

    let ioValue =
        io
            { inPort = fun n w -> b.Input(n, w)
              outPort = fun n w -> b.Output(n, w)
              inPortAs = fun n t -> b.Input(n, t)
              outPortAs = fun n t -> b.Output(n, t) }

    // The body elaborates with this module ambient, so a definition body is the
    // same ordinary code a design body is.
    elaborating.Push b

    try
        body ioValue b
    finally
        elaborating.Pop() |> ignore

    { def = b.Def
      io = io
      apply = apply }

/// A module that is a pure function of one input. The output width is measured
/// by running `f` on a reference — widths live in the values, so the definition
/// does not have to be told.
let fnModule1 name (an, aw) outName (f: Expr -> Expr) =
    let outWidth = width (f (Ref(an, UInt aw)))

    defineModule
        name
        (fun p -> (p.inPort an aw, p.outPort outName outWidth))
        (fun m (pa, po) x ->
            m.Assign(pa, x)
            po)
        (fun (ia, o) m -> m.Assign(o, f ia))

/// The same for two inputs.
let fnModule2 name (an, aw) (bn, bw) outName (f: Expr -> Expr -> Expr) =
    let outWidth = width (f (Ref(an, UInt aw)) (Ref(bn, UInt bw)))

    defineModule
        name
        (fun p -> (p.inPort an aw, p.inPort bn bw, p.outPort outName outWidth))
        (fun m (pa, pb, po) x y ->
            m.Assign(pa, x)
            m.Assign(pb, y)
            po)
        (fun (ia, ib, o) m -> m.Assign(o, f ia ib))

/// The same for three.
let fnModule3 name (an, aw) (bn, bw) (cn, cw) outName (f: Expr -> Expr -> Expr -> Expr) =
    let outWidth = width (f (Ref(an, UInt aw)) (Ref(bn, UInt bw)) (Ref(cn, UInt cw)))

    defineModule
        name
        (fun p -> (p.inPort an aw, p.inPort bn bw, p.inPort cn cw, p.outPort outName outWidth))
        (fun m (pa, pb, pc, po) x y z ->
            m.Assign(pa, x)
            m.Assign(pb, y)
            m.Assign(pc, z)
            po)
        (fun (ia, ib, ic, o) m -> m.Assign(o, f ia ib ic))

let private designWith (b: Builder) (body: unit -> unit) =
    elaborating.Push b

    try
        body ()
    finally
        elaborating.Pop() |> ignore

    b.Def

/// Elaborate a top-level design. The body is ordinary code with the module
/// ambient, and the result is what the emitter, the simulator and the debugger
/// all take.
let design name (body: unit -> unit) = designWith (Builder(name)) body

/// A design with named clock/reset ports — `designClocked axiClock` is how an
/// AXI wrapper gets `s_axi_aclk`/`s_axi_aresetn` on its boundary.
let designClocked spec name (body: unit -> unit) = designWith (Builder(name, spec)) body

// The public seams, taking a type. Public because the declaration functions
// below are `inline` — an inline body has to reach what it calls — while the
// ambient builder itself stays internal.
/// Declare an input port at a ground type. `input` is the spelling to reach
/// for; this is the seam under it.
let declareInput name (t: GroundType) = (current ()).Input(name, t)
/// Declare an output port at a ground type.
let declareOutput name (t: GroundType) = (current ()).Output(name, t)
/// Declare a wire at a ground type.
let declareWire name (t: GroundType) = (current ()).Wire(name, t)
/// Declare a register at a ground type, with the value it takes under reset.
let declareReg name (t: GroundType) init = (current ()).Reg(name, t, init)
/// Declare a register that reset does not reach.
let declareRegNoReset name (t: GroundType) = (current ()).RegNoReset(name, t)

/// Declare a port, wire or register. The second argument is a width — meaning
/// unsigned, as it always did — or a type:
///
///     let count = input "count" 8            // UInt 8
///     let sample = input "sample" (SInt 16)  // signed, and `mul` knows it
let inline input name spec = declareInput name (AsType $ spec)
/// An output port, at a width or a type.
let inline output name spec = declareOutput name (AsType $ spec)
/// A wire, at a width or a type.
let inline wire name spec = declareWire name (AsType $ spec)

/// A register that resets to zero — which measured as 98.6% of every register
/// in the tree, so zero is what `reg` means and the init argument moved to
/// `regInit`. The change is loud where it bites: an old `reg name w 0UL` is an
/// `Expr` applied to a `uint64`, a type error rather than a reinterpretation.
let inline reg name spec = declareReg name (AsType $ spec) 0UL

/// The 1.4%: a register that resets to a stated value.
let inline regInit name spec init = declareReg name (AsType $ spec) init

/// The control-bit declarations: a third of every signal in the tree is one
/// bit wide, and these say so in the name rather than in a trailing `1` —
/// which also keeps "forgot the width" a compile error on the sized forms
/// instead of a silent one-bit default.
let inputBit name = declareInput name (UInt 1)
/// A one-bit output port.
let outputBit name = declareOutput name (UInt 1)
/// A one-bit wire.
let wireBit name = declareWire name (UInt 1)
/// A one-bit register, resetting to zero.
let regBit name = declareReg name (UInt 1) 0UL

/// A register that holds its value through reset. Same shape as `reg` without
/// the initial value, because there is no reset for it to take.
///
/// Reach for it in a data path — a pipeline stage, a delay line, a register
/// file — where nothing downstream cares what the value was before the first
/// valid beat arrives. A reset net that reaches every flop costs fanout and
/// routing, and it stops Vivado inferring an SRL for a delay chain. Control
/// state is the opposite case and should keep its reset: a state machine that
/// survives reset is a bug.
let inline regNoReset name spec = declareRegNoReset name (AsType $ spec)
/// Connect, when both sides are already signals. Public because `==>` is
/// `inline` and an inline body has to reach whatever it calls; the ambient
/// builder itself stays internal.
let connect (target: Expr) (value: Expr) = (current ()).Assign(target, value)

/// Connect: the value, then the signal it drives.
///
/// Source-first because that is the direction the data actually goes, and
/// because a destination-first operator would *look* like assignment — which
/// this is not. It is a permanent connection, the way solder is, and it is worth
/// the glyph saying so.
///
/// It is also the only direction that composes with a pipeline. F# puts `|>`
/// and `==>` at one precedence level, left-associative, so
///
///     word |> decode ==> out
///
/// needs no parentheses, while a destination-first operator parses as
/// `(out <== word) |> decode` and does not type-check at all.
///
/// The value may be a bare number, which takes the target's width — the target
/// is the neighbour supplying it, and there is never a second opinion about how
/// wide a connection is.
let inline (==>) value (target: Expr) = connect target ((Widen $ value) (width target))

/// A claim about the design, checked every cycle by a Sim built with
/// `checkAsserts = true` and by the emitted Verilog under a simulator that
/// honours assertions. `assert` is an F# keyword, hence the name.
///
/// Written inside `If`, the claim is conditional on that branch being taken.
let assertThat cond message = (current ()).AssertThat(cond, message)

/// Conditional assignment scope, folding to Mux trees when the block ends.
/// `If` and `Else` are two sequential statements rather than one construct.
///
/// Inside it a reg with no unconditional default holds its value; a wire there
/// is an error. The scope underneath is last-connect-wins — that is how a
/// branch merges into its parent — while a second `==>` at one level is not.
let If cond (body: unit -> unit) = (current ()).If(cond, body)

/// Must immediately follow its `If` — any intervening statement seals that If as
/// else-less, and this then fails at elaboration.
let Else (body: unit -> unit) = (current ()).Else(body)

/// Register a stream's ready net from a producer elaborated outside the
/// library assembly — the same checkStreams bookkeeping the stdlib's own
/// sources do (a stream has exactly one consumer; the raw drive count is
/// judged at emission).
let registerStreamReady (ready: Expr) = (current ()).RegisterStreamReady ready

/// **Retired.** A memory must say where it lives.
///
/// Refusing the asynchronous read was not enough. The storage class is not just
/// about read latency: it decides how the synthesiser builds the thing, and a
/// tool that is free to change its mind will. GEP's case store was 32 narrow
/// per-lane arrays the tool put in LUTs; a refactor collapsed them into one
/// wider array and Vivado silently moved it to a true dual-port block RAM. The
/// simulator, Verilator and firtool all still agreed with each other — they
/// model the Verilog, and the Verilog did not change meaning. The silicon did.
///
/// So the choice belongs to the design: `distributedMem` for LUTRAM (register
/// files, small tables, FIFO storage — and the only kind an asynchronous read
/// is legal on) or `blockMem` for BRAM (frame buffers, anything deep). Both
/// emit a `ram_style` attribute that takes the decision away from the tool, so
/// a memory means on silicon what it means here, and a refactor that changes
/// its shape cannot quietly change what it is built from.
///
/// The IR keeps `Unspecified` because FIRRTL has no `ram_style` and an imported
/// memory has to land somewhere; nothing in the authoring surface produces one.
[<System.Obsolete("A memory must declare its storage. Use `distributedMem` (LUTRAM: register files, small tables, and the only kind `memRead` is legal on) or `blockMem` (BRAM: deep arrays, `memReadPort` only). Leaving the choice to the synthesiser means the design's behaviour depends on a decision nobody made, and the tool changes its mind when the array's shape changes.", true)>]
let mem name addrWidth width =
    (current ()).Memory(name, addrWidth, width, None, Unspecified)

/// A memory built from LUTs, and **the only kind an asynchronous read is
/// allowed on**.
///
/// Block RAM cannot read combinationally, so `memRead` on anything the
/// tool decides to put in BRAM silently gains a cycle on silicon while passing
/// this repo's Sim and Verilator both. Saying `distributedMem` emits
/// `ram_style = "distributed"`, which takes that decision away from the tool —
/// the read then means what the model says, and the cost shows up as LUTs in
/// the synthesis report rather than as a wrong picture on the board.
///
/// It is a real cost: every bit is a LUT rather than a slice of a block, so
/// this is for register files, small tables and FIFO storage. A frame buffer
/// wants `blockMem` and a pipelined consumer.
let distributedMem name addrWidth width =
    (current ()).Memory(name, addrWidth, width, None, Distributed)

/// A memory built from block RAM: `ram_style = "block"`, and synchronous reads
/// only. `memRead` on one is an elaboration error rather than a surprise
/// on the board.
let blockMem name addrWidth width =
    (current ()).Memory(name, addrWidth, width, None, Block)

/// A read-only memory: contents fixed at elaboration, emitted as a Verilog
/// `initial` block (Vivado turns it into a BRAM INIT). Depth is the smallest
/// power of two covering the values; the remainder reads zero. The Sim loads
/// the contents at construction and `Reset()` reloads them, modeling
/// reconfiguration. Nothing stops a design writing it — a preloaded RAM is
/// the same declaration.
let private romOf name width (values: uint64[]) style =
    if Array.isEmpty values then failwith $"rom '{name}' needs at least one value"

    for i in 0 .. values.Length - 1 do
        if values[i] > maskOf width then
            failwith $"rom '{name}'[%d{i}] = %d{values[i]} does not fit %d{width} bits"

    let mutable addrWidth = 0

    while (1 <<< addrWidth) < values.Length do
        addrWidth <- addrWidth + 1

    (current ()).Memory(name, addrWidth, width, Some(Array.copy values), style)

/// **Retired**, for the same reason `mem` was: a memory must say where it
/// lives. A ROM is not exempt — the storage class decides what the array is
/// built from, the tool changes its mind when the array's shape or its readers
/// change, and this repo has a silicon trap of its own here: *a sync-read ROM
/// feeding a DSP multiply silently demotes to LUTROM*, because the output
/// register is absorbed as the DSP's input register.
[<System.Obsolete("A ROM must declare its storage. Use `distributedRom` (LUTROM: small tables, and the only kind a combinational read is legal on) or `blockRom` (BRAM INIT: deep tables, `memReadPort` only). Leaving the choice to the synthesiser means the design's behaviour depends on a decision nobody made.", true)>]
let rom name width (values: uint64[]) = romOf name width values Unspecified

/// A read-only memory in LUTs, for the small tables that want a combinational
/// read — `distributedMem`'s rule with `rom`'s contents.
let distributedRom name width (values: uint64[]) = romOf name width values Distributed

/// A read-only memory in block RAM: the contents become a BRAM INIT and the
/// read is synchronous only, `blockMem`'s rule with `rom`'s contents. For
/// tables too deep to spend LUTs on.
let blockRom name width (values: uint64[]) = romOf name width values Block

/// Write under the enclosing If conditions ANDed into `enable`. Multiple writes
/// to one mem merge into a single priority-muxed write site at finalize.
let memWrite m addr data enable =
    (current ()).Write(m, addr, data, enable, None)

/// Write only the lanes the mask selects: one bit per lane, lanes dividing the
/// word evenly. A 32-bit memory with a 4-bit mask has byte lanes, which is AXI's
/// `wstrb` and the shape a synthesiser infers as a **byte-enabled block RAM**
/// rather than as read-modify-write logic around a whole-word write.
///
/// The alternative is banking the word into one array per lane by hand, which is
/// what this codebase did before. At wide lanes the two are the same silicon — a
/// 128-bit block is four blocks either way — but at narrow ones they are not: a
/// 32-bit array with four byte-enables is one primitive, and four 8-bit arrays
/// are four of them at a width that wastes most of each.
///
/// **Masks do not merge across write sites.** Several writes to one mem still
/// fold to a single priority-muxed site, and the winning write's mask is the one
/// that applies — the pick happens first, the mask second. Two writes firing
/// together with complementary masks do not both land, which is the only sane
/// reading when they may also disagree about the address.
let memWriteMasked m addr data enable mask =
    (current ()).Write(m, addr, data, enable, Some mask)

/// Read a word: it is there this cycle, and it behaves like every other value
/// an expression is built from.
///
/// **Only legal on a `distributedMem`**, and that is the whole point of the
/// distinction. Block RAM physically cannot read combinationally, so a
/// synthesiser that put the array in a block inserts a register and the design
/// gains a cycle it does not know about — passing this repo's Sim, passing
/// Verilator, and corrupting on the board. That was the top entry in
/// CLAUDE.md's hardware gotchas and a real Mandelbrot bug; it is now an
/// elaboration error instead.
///
/// This was `memReadAsync` until 2026-08-18, which named it for the hardware
/// term — an asynchronous read port is what distributed RAM has — and thereby
/// mislabelled it for everyone actually writing F#, where `Async` is the type
/// for a computation that finishes *later*. It is the only read that finishes
/// now. The plain name belongs to it.
let memRead (m: Mem) addr =
    match m.style with
    | Distributed -> MemRead(m.memName, addr, m.memWidth)
    | Block ->
        failwith
            $"memRead on '{m.memName}', which is a blockMem — block RAM cannot read combinationally, so this would gain a cycle on silicon that no check here would catch. Use memReadPort and pipeline the consumer, or declare it distributedMem if it is small enough to live in LUTs"
    | Unspecified ->
        failwith
            $"memRead on '{m.memName}', whose storage the synthesiser chooses — and if it chooses block RAM this read gains a cycle on silicon that no check here would catch. Declare it distributedMem to mean it, or use memReadPort and pipeline the consumer"

/// The raw one-cycle read: a word that arrives **next** cycle, read-first
/// against a same-cycle write. Legal on any storage, because every storage can
/// do it.
///
/// **Not on the surface** — `memReadPort` is. This was the unqualified `memRead`
/// until 2026-08-18, when it handed back an `Expr` that looked like every other
/// value and was a cycle late, with nothing at the call site saying so. Naming
/// it after its latency was the first half of the fix; the second half is that
/// callers now reach a *port* which owns that latency and can carry their own
/// values across it, rather than a bare value they have to remember is late.
///
/// It stays internal because `memReadPort` is built out of it and nothing else
/// should be.
let internal memReadNextCycle m addr = (current ()).SyncRead(m, addr)

/// Turn a module into a function that creates a fresh instance on every call.
let liftUnary (tm: TypedModule<'io, Expr -> Expr>) = fun x -> (current ()).Instance(tm) x

/// The same for a module of two operands.
let liftBinary (tm: TypedModule<'io, Expr -> Expr -> Expr>) =
    fun x y -> (current ()).Instance(tm) x y

/// A module whose body is ordinary ambient code — regs, wires, `==>` — rather than a
/// pure function of its inputs. `build` runs inside the module's own context and
/// returns the expression that drives the output. Output width is declared, not
/// inferred: running the body to measure it would declare its registers.
let stateModule1 name (an, aw) (outName, ow) (build: Expr -> Expr) =
    defineModule
        name
        (fun p -> (p.inPort an aw, p.outPort outName ow))
        (fun m (pa, po) x ->
            m.Assign(pa, x)
            po)
        (fun (ia, o) m -> m.Assign(o, build ia))

/// The same for a module that transforms a stream — a fresh instance per call,
/// so a stage used twice in a chain is two instances of one definition.
let liftStream (tm: TypedModule<'io, Stream<'p> -> Stream<'p>>) =
    fun s -> (current ()).Instance(tm) s

/// A named instance of any typed module, in ambient context — for designs that
/// name their instances (the pod's lanes), where the name is created, not
/// looked up.
let instanceNamed (name: string) (tm: TypedModule<'io, 'fn>) : 'fn = (current ()).Instance(name, tm)
