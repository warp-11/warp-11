/// What a design holds, as a table: every signal a flattened design declares,
/// with the instance it came from. The Sim flattens the same way, so a name
/// here is exactly a name `Sim.Peek` accepts — which is what lets the debugger
/// offer a filterable list of things to watch.
///
/// Not auto-opened: `SignalKind`'s cases would shadow `Decl`'s.
module Warp11.Inventory

/// What a signal is. `Decl` without the type and without the memory case,
/// because a table wants to sort by this and nothing else about it.
[<RequireQualifiedAccess>]
type SignalKind =
    | Input
    | Output
    | Wire
    | Reg

/// One signal, under the name the Sim knows it by.
type SignalEntry =
    { name: string
      width: int
      /// Whether the declaration said these bits are two's complement. The
      /// debugger has no other way to know — the value itself is just bits.
      signed: bool
      kind: SignalKind
      /// The instance prefix this signal came from — "lane3_", or "pod_lane3_"
      /// nested. Empty for the top module's own signals.
      group: string }

/// One memory, likewise. Kept apart from the signals because a memory is
/// addressed rather than watched, and the debugger shows it differently.
type MemEntry =
    { name: string
      addrWidth: int
      wordWidth: int
      group: string }

/// The whole table for one design.
type ModuleInventory =
    { topName: string
      signals: SignalEntry list
      mems: MemEntry list
      /// Every group present, shortest first — the table's section order.
      groups: string list
      /// Every state machine in the design, keyed by its register's *flattened*
      /// name, so a value read from the Sim can be shown as the state it means.
      stateMachines: Map<string, Map<uint64, string>> }

/// Every instance prefix a flattened name can carry: an instance `b` inside
/// instance `a` contributes both "a_" and "a_b_", because that is how
/// `flatten` builds the names.
let rec private instancePrefixes outer (m: ModuleDef) =
    [ for inst in m.instances do
          let prefix = outer + inst.instName + "_"
          yield prefix
          yield! instancePrefixes prefix inst.child ]

/// Build the inventory for a design. Flattens it the way the Sim does, so
/// every name here is one `Sim.Peek` accepts.
let ofDesign (design: ModuleDef) : ModuleInventory =
    let flat = flatten design
    let prefixes = System.Collections.Generic.HashSet<string>(instancePrefixes "" design)

    // Longest match wins, so a signal whose own name contains an underscore is
    // never mistaken for a shallower instance's. Only the name's own underscore
    // positions can start a match, so this is a handful of hash lookups rather
    // than a scan of every prefix in the design.
    let groupOf (name: string) =
        [ for i in 0 .. name.Length - 1 do
              if name[i] = '_' then yield name.Substring(0, i + 1) ]
        |> List.rev
        |> List.tryFind prefixes.Contains
        |> Option.defaultValue ""

    let signals =
        [ for d in flat.decls do
              match d with
              | Input (n, t) ->
                  yield
                      { name = n
                        width = t.Width
                        signed = t.Signed
                        kind = SignalKind.Input
                        group = groupOf n }
              | Output (n, t) ->
                  yield
                      { name = n
                        width = t.Width
                        signed = t.Signed
                        kind = SignalKind.Output
                        group = groupOf n }
              | Wire (n, t) ->
                  yield
                      { name = n
                        width = t.Width
                        signed = t.Signed
                        kind = SignalKind.Wire
                        group = groupOf n }
              | Reg (n, t, _) ->
                  yield
                      { name = n
                        width = t.Width
                        signed = t.Signed
                        kind = SignalKind.Reg
                        group = groupOf n }
              | Memory _ -> () ]

    let mems =
        [ for d in flat.decls do
              match d with
              | Memory(n, aw, w, _, _) ->
                  yield
                      { name = n
                        addrWidth = aw
                        wordWidth = w
                        group = groupOf n }
              | _ -> () ]

    // Walked with the instance path rather than taken from `flat`, because
    // flattening carries decls and stmts across but not what they mean.
    let rec stateMachinesOf prefix (m: ModuleDef) =
        [ for stateReg, states in m.stateMachines -> prefix + stateReg, Map.ofList states
          for inst in m.instances do
              yield! stateMachinesOf (prefix + inst.instName + "_") inst.child ]

    { topName = design.name
      signals = signals
      mems = mems
      stateMachines = Map.ofList (stateMachinesOf "" design)
      groups =
        (signals |> List.map (fun s -> s.group))
        @ (mems |> List.map (fun m -> m.group))
        |> List.distinct
        |> List.sort }
