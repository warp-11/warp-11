# Your own modules

Everything so far went into one module. This page defines a module of your own
— a saturating accumulator — and instantiates it **twice**, which is the moment
hardware reuse stops being a function call and becomes two pieces of silicon
with one description.

## What to look at

`en` starts at 1 and the two adders start at 3 and 5, because this page set
them that way.

- Press **Step** a few times. `total_left` climbs by 3, `total_right` by 5,
  and `total_both` by 8 — three accumulators, running at once, from one
  definition.
- The signal list shows the instances as groups: `left`, `right`, and
  `satAcc8_1` — the third was made through `satAccOf`, so it named itself. Each has
  its own `r` inside: three registers exist because there are three instances.
- Poke `add_right = 200` and keep stepping. `total_right` pins at `0xFF` — the
  saturation is *inside* the module, so every instance has it. `total_both`
  pins soon after.
- `lowest` always shows the smaller of the two named totals. That is `Min8`,
  the fourth instance.

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

## The full route: `defModule`

State, or more ports than a pure function's operands, want `defModule` — a
module defined as what it actually is: a bundle of ports, and one body over
them.

```fsharp
type SatAccIo =
    { add: Input
      en: Input
      total: Output }

let satAcc =
    defModule
        "SatAcc8"
        (fun p ->
            { add = p.inPort "add" 8
              en = p.inPort "en" 1
              total = p.outPort "total" 8 })
        (fun io ->
            let r = reg "r" 8
            ...
            r ==> io.total)
```

Two arguments after the name:

- **The IO bundle** — a record you shape yourself, which is what makes the IO
  *typed*: the body and every use go through named fields, so a port renamed
  in one place is a compile error in the other, not a mis-wire. `Input` and
  `Output` are both just `Expr` — the record is saying which way each field
  points, from the module's own perspective, and the elaborator holds you to
  it: drive `io.add` from inside the body and elaboration stops with *"'add'
  is an input of 'SatAcc8' — the caller drives it, the body reads it."* The
  factory is also the *whole* interface — a port declared from inside the
  body is refused, so the bundle is a complete receipt of the boundary.
- **The body** — ordinary design code over those wires, and the *whole*
  module. `reg`, `wire`, `If`, `==>` all work, because the body elaborates
  with this module ambient, exactly as a `design` body does. Every mapping —
  ports to logic to ports — is plain code here, in one place.

There is no third piece. What a *use* looks like is decided at the use, with
the same wires.

## Instantiating

`.NewNamed` stamps out one copy and hands you the bundle again — this time
over the instance's staging wires — and you wire it where you stand:

```fsharp
let totalLeft =
    let c = satAcc.NewNamed "left"
    addLeft ==> c.add
    en ==> c.en
    c.total
```

When a module is used enough to deserve a call shape, that wiring becomes an
**ordinary function beside the module** — not framework, just F#:

```fsharp
let satAccOf (add: Expr) (en: Expr) =
    let c = satAcc.New          // auto-named: satAcc8_1, as the signal list shows
    add ==> c.add
    en ==> c.en
    c.total
```

The design's third accumulator uses it — `satAccOf (addLeft + addRight) en` —
feeding the *sum* of both adds through the same saturating hardware. One
instance per `.New`/`.NewNamed` read: bind it once and wire it once, because
one bundle wired twice drives one instance twice, which the one-driver rule
refuses.

The emitted Verilog — look at the **source** tab's Verilog, or emit it
yourself — contains **one** `module SatAcc8` definition and **three** instance
lines. That is the dedupe rule: a definition is written once however many
times it is used. `left` and `right` are named because the names carry the
page's story — the groups in the signal list, and the staging-wire rule below.

## How it actually works: one description, two worlds

| | runs | against |
|---|---|---|
| the IO bundle | at definition **and** at every instantiation | both worlds |
| the body | once, at definition | the module's own builder |
| the wiring | once per instance | your design, where it is written |

**Definition time**: `defModule` makes a fresh builder for the module, runs
your bundle function with factories that *declare real ports* on it, then runs
the body with that builder made ambient. The result is frozen into a module
definition.

**Instantiation time**: `.NewNamed "left"` declares the staging wires
(`left_add`, `left_en`, `left_total`) in *your* design, then **runs your
bundle function again** — this time the factories declare nothing and instead
hand back references to those staging wires. Because it is the same function,
the record has the same shape both times: inside the body, `io.add` was the
module's real port; in your hands, `c.add` is the wire `left_add`.

That re-run is the whole trick, and it is what makes the IO *typed*. The port
names are written once, in one function, and both worlds execute it — so the
body and every instantiation refer to the same port by construction, and
renaming one is a compile error at every stale use rather than a silent
mis-wire. It is also why one definition serves many instances: the frozen
definition is shared, and each instantiation only adds staging wires and an
instance entry — the dedupe the previous section pointed at.

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
both sides of that rule: `fnModule2`/`defModule` is how the module half is
built, and a wrapper like `satAccOf` is the same call shape either way. Whether
something *should* be a module is mostly a question of whether you want it
named in the Verilog and grouped in the debugger — the hardware is the same.

## Try this

- Poke `en = 0` and step. Both totals hold — the enable reaches both
  instances, but each instance's register is its own.
- Find `left_r`, `right_r` and `satAcc8_1_r` in the filter box: the three
  registers, living in their instances' groups — the last under the name
  `.New` chose.
- Change one instance's input and watch only its total move — nothing is
  shared except the definition.

## See also

- [**Dot product**](dotProduct.md) — the consumer side: stdlib entries that are
  secretly modules.
- [**Your own stage**](ownStage.md) — the same wrapper pattern for a module
  whose ports carry a ready/valid handshake.
- [**Counter**](counter.md) — the register and enable idiom `SatAcc8` is built
  from.
- [**Shared unit**](sharedUnit.md) — when you *want* one piece of hardware
  serving several callers, which is a design, not a default.
