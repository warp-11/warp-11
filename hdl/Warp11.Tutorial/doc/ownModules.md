# Your own modules

Everything so far went into one module. This page defines a module of your own
— a saturating accumulator — and instantiates it **twice**, which is the moment
hardware reuse stops being a function call and becomes two pieces of silicon
with one description.

## What to look at

`en` starts at 1 and the two adders start at 3 and 5, because this page set
them that way.

- Press **Step** a few times. `total_left` climbs by 3, `total_right` by 5 —
  two accumulators, running at once, from one definition.
- The signal list shows the instances as groups: `left` and `right`, each with
  its own `r` inside. Two registers exist because there are two instances.
- Poke `add_right = 200` and keep stepping. `total_right` pins at `0xFF` — the
  saturation is *inside* the module, so both instances have it.
- `lowest` always shows the smaller total. That is `Min8`, the third instance.

## The light route: a pure function as a module

```fsharp
let minOf8 =
    fnModule2 "Min8" ("a", 8) ("b", 8) "m" (fun a b -> mux (lt a b) a b)
    |> liftBinary
```

`fnModule2` takes a name, two named input ports, an output name, and an
ordinary function from expressions to an expression. `liftBinary` turns the
result back into something you call like a function — `minOf8 x y` — except
each call plants a `Min8` instance. If your module is a pure combinational
function, this is the whole story.

## The full route: `defineModule`

State, several ports, or ports that are not just "some inputs and one output"
want the full form:

```fsharp
let satAcc =
    defineModule
        "SatAcc8"
        (fun p ->
            {| add = p.inPort "add" 8
               en = p.inPort "en" 1
               total = p.outPort "total" 8 |})
        (fun m io ->
            fun (add: Expr) (en: Expr) ->
                m.Assign(io.add, add)
                m.Assign(io.en, en)
                io.total)
        (fun io _ ->
            let r = reg "r" 8
            ...
            r ==> io.total)
```

Three arguments, each doing one job:

- **The ports**, as a record you shape yourself — which is what makes the IO
  *typed*: the body and the caller both go through named fields, so a port
  renamed in one place is a compile error in the other, not a mis-wire.
- **The apply function** — what a *use* of this module looks like. It takes the
  caller's expressions, assigns them to the input ports, and returns the output.
  This is where "instances are functions" comes from: you are writing the
  function that each instantiation becomes.
- **The body** — ordinary design code. `reg`, `wire`, `If`, `==>` all work,
  because the body elaborates with this module ambient, exactly as a `design`
  body does.

## Instantiating

```fsharp
let totalLeft = instanceNamed "left" satAcc addLeft en
let totalRight = instanceNamed "right" satAcc addRight en
```

Each `instanceNamed` stamps out one copy and wires it. The emitted Verilog —
look at the **source** tab's Verilog, or emit it yourself — contains **one**
`module SatAcc8` definition and **two** instance lines. That is the dedupe rule:
a definition is written once however many times it is used.

## How it actually works: one description, two worlds

The three arguments are not ceremony — they are the definition sorted by *when
each part has to execute*.

| | runs | against |
|---|---|---|
| ports | at definition **and** at every instantiation | both worlds |
| body | once, at definition | the module's own builder |
| apply | once per instance | the caller's builder |

**Definition time**: `defineModule` makes a fresh builder for the module, runs
your ports function with factories that *declare real ports* on it, then runs
the body with that builder made ambient — which is why `reg`, `If` and `==>`
work in a body exactly as they do in a `design`. The result is frozen into a
module definition.

**Instantiation time**: `instanceNamed "left" satAcc ...` declares the staging
wires (`left_add`, `left_en`, `left_total`) in *your* design, then **runs your
ports function again** — this time the factories declare nothing and instead
hand back references to those staging wires. Because it is the same function,
the record has the same shape both times: inside the body, `io.add` was the
module's real port; inside apply, it is the wire `left_add`. Then apply runs
and wires your expressions across.

That re-run is the whole trick, and it is what makes the IO *typed*. The port
names are written once, in one function, and both worlds execute it — so the
body and every instantiation refer to the same port by construction, and
renaming one is a compile error at every stale use rather than a silent
mis-wire. It is also why ports cannot simply be declared inside apply: apply
runs only at instantiation, and by then the module definition — which emission
and the staging wires are both built from — has long since been frozen.

Three smaller things this explains:

- **Why apply writes `m.Assign(io.add, add)` instead of `add ==> io.add`.**
  The ambient operators serve *bodies*, where the module's builder has been
  made ambient. Apply runs in the caller's world holding an explicit builder
  parameter, and machinery uses what it is handed rather than reaching for the
  ambient stack. (Today the two would land in the same place — the discipline
  is the point.)
- **Why the body's second parameter is usually `_`.** The body *is* handed its
  builder, but with the ambient surface available it rarely needs it — the
  discard says "unused on purpose" where a named parameter would look
  forgotten.
- **Why one definition serves many instances.** The frozen definition is
  shared; each instantiation only adds staging wires and an instance entry.
  That is the dedupe the previous section pointed at in the emitted Verilog.

## The rule that will bite you exactly once

An instance's ports are reached through staging wires named
`{instance}_{port}`, **in your namespace**. The instance called `left` with a
port called `total` owns the name `left_total` — which is why this design's
outputs are `total_left` and `total_right`, not `left_total` and `right_total`.
Get this wrong and elaboration refuses with a name collision; the rule exists
because the alternative was a silent mis-wire, and it is the same
one-declaration-per-name rule the rest of the language runs on.

## Inline or instantiated — still invisible

[**Dot product**](dotProduct.md) showed that a library entry can be inline
logic or a submodule without callers telling the difference. Now you have seen
both sides of that rule: `fnModule2`/`defineModule` is how the module half is
built, and the function you get back is the same shape either way. Whether
something *should* be a module is mostly a question of whether you want it
named in the Verilog and grouped in the debugger — the hardware is the same.

## Try this

- Poke `en = 0` and step. Both totals hold — the enable reaches both
  instances, but each instance's register is its own.
- Find `left_r` and `right_r` in the filter box: the two registers, living in
  their instances' groups.
- Change one instance's input and watch only its total move — nothing is
  shared except the definition.

## See also

- [**Dot product**](dotProduct.md) — the consumer side: stdlib entries that are
  secretly modules.
- [**Counter**](counter.md) — the register and enable idiom `SatAcc8` is built
  from.
- [**Shared unit**](sharedUnit.md) — when you *want* one piece of hardware
  serving several callers, which is a design, not a default.
