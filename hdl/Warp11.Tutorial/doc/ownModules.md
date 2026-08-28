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
  `satAcc8_1` — the third was made with `.New`, so it named itself. Each has
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

## The typed route: `fnModule`

State, or ports the body should read as a typed value, want `fnModule`:

```fsharp
let satAcc =
    fnModule
        "SatAcc8"
        (fun p ->
            let add = p.inPort "add" 8
            let en = p.inPort "en" 1
            (add, en), (add, en), p.outPort "total" 8)
        (fun (addPort, enPort) total ->
            fun (add: Expr) (en: Expr) ->
                add ==> addPort
                en ==> enPort
                total)
        (fun (add, en) ->
            let r = reg "r" 8
            ...
            r)
```

Three arguments after the name, each doing one job:

- **The ports** — declared once, and stated from both sides in the same place:
  the raw ports a caller's arguments land on, the *view* the body reads, and
  the output port. Here the view is just the raw ports again, because nothing
  is packed — a module whose neighbourhood arrives as one bus would build its
  view by slicing that bus, right here beside the declaration, and the body
  would read fields instead of bit ranges.
- **The apply function** — what a *use* of this module looks like. It is
  handed the raw ports and the output, and writes the call shape itself —
  `satAcc` chooses two curried parameters — landing the arguments with `==>`
  and handing back the output. This is where "instances are functions" comes
  from: you are writing the function that each instantiation becomes.
- **The body** — ordinary design code. `reg`, `wire`, `If`, `==>` all work,
  because the body elaborates with this module ambient, exactly as a `design`
  body does. It reads the view and **returns** the expression that drives the
  output; the wiring of that return value onto `total` happens for you. The
  body never touches a port.

Richer boundaries — a stream in, side outputs beside the result — use
`viewModule` and, underneath everything, `defineModule`; the shape is the
same, the view just gets richer.

## Instantiating

```fsharp
let totalLeft = instanceNamed "left" satAcc addLeft en
let totalRight = instanceNamed "right" satAcc addRight en
```

Each `instanceNamed` stamps out one copy and wires it. When the instance's
name is not part of the story, `.New` is the shorter form — the design's third
accumulator uses it:

```fsharp
let totalBoth = satAcc.New (addLeft + addRight) en
```

Reading `.New` stamps a fresh, auto-named instance — `satAcc8_1`, as the
signal list shows — and hands back the module's function, here
`Expr -> Expr -> Expr`, so applying it wires your arguments and gives you the
output. An argument is any expression: this one feeds the *sum* of both adds
through the same saturating hardware. One instance per read: bind it once and
apply it once, because `let f = satAcc.New` applied twice would drive *one*
instance twice, which the one-driver rule refuses.

The emitted Verilog — look at the **source** tab's Verilog, or emit it
yourself — contains **one** `module SatAcc8` definition and **three** instance
lines. That is the dedupe rule: a definition is written once however many
times it is used. `left` and `right` are named because the names carry the
page's story — the groups in the signal list, and the staging-wire rule below.

## How it actually works: one description, two worlds

The arguments are not ceremony — they are the definition sorted by *when each
part has to execute*.

| | runs | against |
|---|---|---|
| ports | at definition **and** at every instantiation | both worlds |
| body | once, at definition | the module's own builder |
| apply | once per instance | the caller's builder |

**Definition time**: `fnModule` makes a fresh builder for the module, runs
your ports function with factories that *declare real ports* on it, then runs
the body — with that builder made ambient, which is why `reg`, `If` and `==>`
work in a body exactly as they do in a `design` — and wires what the body
returns onto the output port. The result is frozen into a module definition.

**Instantiation time**: `instanceNamed "left" satAcc ...` declares the staging
wires (`left_add`, `left_en`, `left_total`) in *your* design, then **runs your
ports function again** — this time the factories declare nothing and instead
hand back references to those staging wires. Because it is the same function,
the result has the same shape both times: inside the body, the view read the
module's real ports; inside apply, `addPort` is the wire
`left_add`. Then apply runs and wires your expressions across.

That re-run is the whole trick, and it is what makes the IO *typed*. The port
names are written once, in one function, and both worlds execute it — so the
body and every instantiation refer to the same port by construction, and
renaming one is a compile error at every stale use rather than a silent
mis-wire. It is also why ports cannot simply be declared inside apply: it runs
only at instantiation, and by then the module definition —
which emission and the staging wires are both built from — has long since been
frozen.

Two smaller things this explains:

- **Why `==>` works in apply too.** An instantiation happens while
  *your* design is elaborating, so the ambient builder there is the caller's —
  the same operator serves both worlds, each time landing in the module that is
  actually being built. There is no other way to drive a signal: the primitive
  underneath is internal to the library, and `==>` is the language.
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
both sides of that rule: `fnModule2`/`fnModule` is how the module half is
built, and the function you get back is the same shape either way. Whether
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
- [**Counter**](counter.md) — the register and enable idiom `SatAcc8` is built
  from.
- [**Shared unit**](sharedUnit.md) — when you *want* one piece of hardware
  serving several callers, which is a design, not a default.
