module Warp11.Crossings

// ---------------------------------------------------------------------------
// The clock-domain crossing check (notes/CLOCK_DOMAINS.md increment 3).
//
// A register samples its input cone on its own domain's edge; a signal from
// another domain in that cone latches unsettled values on silicon while the
// Sim and Verilator — cycle-accurate, no timing model — pass. That is the
// silicon-only bug class, so a crossing without a CDC entry is an elaboration
// error naming the module, the register and the signal.
//
// The walk runs per module, at `Builder.Def`, and reaches across one instance
// boundary through `portDomains`: a child records which domains drive each
// output and which domains sample each input, so the parent judges the wiring
// without seeing inside. **Known limitation, accepted:** a signal routed
// through a purely combinational child — in one port, out another, sampled two
// levels up — carries no domain through the pass-through, so that crossing is
// not seen. The flat design has full visibility, and increment 4's Sim work
// adds the same check there as the backstop.

/// Every declaration or memory an expression reads, memory names included —
/// a `MemRead`'s array contributes itself (the read happens in the mem's
/// domain) and its address cone.
let private refsFolder : ExprFolder<string list> =
    { fLit = fun _ -> []
      fRef = fun (n, _) -> [ n ]
      fAdd = fun (a, b) -> a @ b
      fSub = fun (a, b) -> a @ b
      fMul = fun (a, b) -> a @ b
      fMux = fun (c, t, f) -> c @ t @ f
      fConcat = fun (a, b) -> a @ b
      fSlice = fun (s, _, _) -> s
      fEq = fun (a, b) -> a @ b
      fLt = fun (a, b) -> a @ b
      fAnd = fun (a, b) -> a @ b
      fOr = fun (a, b) -> a @ b
      fXor = fun (a, b) -> a @ b
      fNot = id
      fShr = fun (s, _) -> s
      fPad = fun (s, _) -> s
      fDynamicShl = fun (v, n) -> v @ n
      fDynamicShr = fun (v, n) -> v @ n
      fReduce = fun (_, v) -> v
      fDiv = fun (a, b) -> a @ b
      fRem = fun (a, b) -> a @ b
      fMemRead = fun (m, a, _) -> m :: a
      fAsUInt = id
      fAsSInt = id }

let private refsOf (e: Expr) = foldExpr refsFolder e |> List.distinct

/// The domains a named signal carries. Regs and mems are their declared
/// domain; a module input is universal (the parent enforces what it feeds);
/// a child-output staging net is what the child recorded; a wire resolves
/// through its one assign, memoized. An in-progress name is a combinational
/// cycle, which `checkCombinationalLoops` owns — it contributes nothing here.
let private domainResolver
    (regsAndMems: Set<string>)
    (inputs: Set<string>)
    (domainOfDecl: string -> string)
    (stagingOut: System.Collections.Generic.Dictionary<string, string list>)
    (assigns: System.Collections.Generic.IDictionary<string, Expr>)
    : string -> Set<string> =
    let memo = System.Collections.Generic.Dictionary<string, Set<string>>()
    let visiting = System.Collections.Generic.HashSet<string>()

    let rec domainsOf (n: string) : Set<string> =
        match memo.TryGetValue n with
        | true, ds -> ds
        | _ ->
            let ds =
                if regsAndMems.Contains n then
                    Set.singleton (domainOfDecl n)
                elif inputs.Contains n then
                    Set.empty
                else
                    match stagingOut.TryGetValue n with
                    | true, ds -> Set.ofList ds
                    | _ ->
                        if not (visiting.Add n) then
                            Set.empty
                        else
                            let ds =
                                match assigns.TryGetValue n with
                                | true, e -> refsOf e |> List.map domainsOf |> Set.unionMany
                                | _ -> Set.empty

                            visiting.Remove n |> ignore
                            ds

            memo[n] <- ds
            ds

    domainsOf

/// The domain each assertion is checked in — the one domain its cone lives
/// in, or the module's own where the cone is mixed or reaches nothing
/// clocked. One entry per `Assert`, in statement order; meant for the
/// flattened design, so instances are not consulted.
let internal assertDomainsOf (m: ModuleDef) : string list =
    let own = m.domain.domainName
    let declDomain = dict m.declDomains

    let domainOfDecl n =
        match declDomain.TryGetValue n with
        | true, d -> d
        | _ -> own

    let regsAndMems =
        m.decls
        |> List.choose (function
            | Reg (n, _, _)
            | Memory (n, _, _, _, _) -> Some n
            | _ -> None)
        |> Set.ofList

    let inputs =
        m.decls
        |> List.choose (function
            | Input (n, _) -> Some n
            | _ -> None)
        |> Set.ofList

    let assigns =
        m.stmts
        |> List.choose (function
            | Assign (t, v) -> Some(t, v)
            | _ -> None)
        |> dict

    let domainsOf =
        domainResolver regsAndMems inputs domainOfDecl (System.Collections.Generic.Dictionary()) assigns

    [ for stmt in m.stmts do
          match stmt with
          | Assert (cond, _) ->
              let ds = refsOf cond |> List.map domainsOf |> Set.unionMany

              yield
                  (match Set.toList ds with
                   | [ d ] -> d
                   | _ -> own)
          | _ -> () ]

/// Analyze one multi-domain module: refuse unsynchronised crossings, and
/// record its port domains for the parent's judgement. Called by
/// `Builder.Def` only when the module carries a foreign domain — a
/// single-domain module skips this entirely.
let internal analyze (m: ModuleDef) : (string * string list) list =
    let own = m.domain.domainName
    let declDomain = dict m.declDomains

    let domainOfDecl n =
        match declDomain.TryGetValue n with
        | true, d -> d
        | _ -> own

    let crossings = Set.ofList m.crossings

    let regsAndMems =
        m.decls
        |> List.choose (function
            | Reg (n, _, _)
            | Memory (n, _, _, _, _) -> Some n
            | _ -> None)
        |> Set.ofList

    let inputs =
        m.decls
        |> List.choose (function
            | Input (n, _) -> Some n
            | _ -> None)
        |> Set.ofList

    let assigns =
        m.stmts
        |> List.choose (function
            | Assign (t, v) -> Some(t, v)
            | _ -> None)
        |> dict

    // A child port's recorded domain is relative to the child: its own domain
    // name means "wherever the instance is clocked".
    let mapChildDomain (inst: Instance) (d: string) =
        if d = inst.child.domain.domainName then inst.domain else d

    // Staging nets, both directions. An output net is a source in the child's
    // recorded domains; an input net is a sink the parent must feed correctly.
    let stagingOut = System.Collections.Generic.Dictionary<string, string list>()
    let stagingIn = System.Collections.Generic.Dictionary<string, Instance * string * string list>()

    for inst in m.instances do
        let recorded = dict inst.child.portDomains

        for d in inst.child.decls do
            match d with
            | Output (n, _) ->
                let domains =
                    if List.isEmpty inst.child.foreignDomains then
                        // A single-domain child's outputs are all its own
                        // domain — it never recorded anything.
                        [ inst.domain ]
                    else
                        match recorded.TryGetValue n with
                        | true, ds -> ds |> List.map (mapChildDomain inst)
                        | _ -> []

                stagingOut[$"{inst.instName}_{n}"] <- domains
            | Input (n, _) ->
                let sampledIn =
                    if List.isEmpty inst.child.foreignDomains then
                        [ inst.domain ]
                    else
                        match recorded.TryGetValue n with
                        | true, ds -> ds |> List.map (mapChildDomain inst)
                        | _ -> []

                stagingIn[$"{inst.instName}_{n}"] <- (inst, n, sampledIn)
            | _ -> ()

    let domainsOf = domainResolver regsAndMems inputs domainOfDecl stagingOut assigns

    // Inputs found inside a sampling cone, with the domain that samples them.
    let sampledInputs = System.Collections.Generic.Dictionary<string, Set<string>>()

    let noteSampledInput n d =
        sampledInputs[n] <-
            match sampledInputs.TryGetValue n with
            | true, ds -> Set.add d ds
            | _ -> Set.singleton d

    // One cone, one sampling domain: every signal in it must carry only that
    // domain, and every input in it is recorded as sampled there.
    let requireCone (samplerDomain: string) (e: Expr) (describe: string -> string) =
        for n in refsOf e do
            if inputs.Contains n then
                noteSampledInput n samplerDomain

            for d in domainsOf n do
                if d <> samplerDomain then
                    failwith (describe n + $" — '{n}' is domain '{d}''s; cross with a CDC entry (synchronize)")

    let regNames =
        m.decls
        |> List.choose (function
            | Reg (n, _, _) -> Some n
            | _ -> None)
        |> Set.ofList

    for stmt in m.stmts do
        match stmt with
        | Assign (t, v) ->
            // A register samples its cone on its domain's edge. A marked
            // register is a CDC entry's sampling flop — the one place a
            // foreign signal may arrive.
            if regNames.Contains t && not (crossings.Contains t) then
                let dr = domainOfDecl t
                requireCone dr v (fun n -> $"'{m.name}': register '{t}' (domain '{dr}') samples '{n}'")

            // A child input is sampled inside the child, in the domains it
            // recorded; what drives the staging net must live there.
            match stagingIn.TryGetValue t with
            | true, (inst, port, sampledIn) ->
                for s in sampledIn do
                    requireCone s v (fun n ->
                        $"'{m.name}': instance '{inst.instName}' input '{port}' (sampled in domain '{s}') is driven by '{n}'")
            | _ -> ()
        | _ -> ()

    // A write happens on the mem's clock: address, data, enable and mask are
    // all sampled there.
    for stmt in m.stmts do
        match stmt with
        | MemWrite (mem, addr, data, enable, mask) ->
            let dm = domainOfDecl mem

            for e in [ yield addr; yield data; yield enable; yield! Option.toList mask ] do
                requireCone dm e (fun n -> $"'{m.name}': memory '{mem}' (domain '{dm}') is written from '{n}'")
        // An assertion's edge is undecided until the Sim schedules domains
        // (increment 4); it is sim-only either way, so it is not checked here.
        | Assert _
        | Assign _ -> ()

    [ for d in m.decls do
          match d with
          | Output (n, _) -> yield n, (domainsOf n |> Set.toList |> List.sort)
          | Input (n, _) ->
              match sampledInputs.TryGetValue n with
              | true, ds -> yield n, (ds |> Set.toList |> List.sort)
              | _ -> ()
          | _ -> () ]
