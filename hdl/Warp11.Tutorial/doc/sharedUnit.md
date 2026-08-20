# Shared unit

Two clients sharing one multiplier. Neither knows the other exists: each hands
in a tagged request and gets a tagged answer back. Everything between —
arbitration, keeping track of whose result is emerging, routing it home — is
`warpFu`, and the multiplier itself has never heard of any of it.

## What to look at

Poke both clients at once: `c0_valid = 1`, `c0_tag = 5`, `c0_a = 3`,
`c0_b = 4`; `c1_valid = 1`, `c1_tag = 9`, `c1_a = 6`, `c1_b = 7`. Set both
`w0_ready` and `w1_ready` to 1, and step.

- `c0_ready` and `c1_ready` alternate. Only one client is served per cycle,
  because the unit accepts one operand pair per cycle.
- After two cycles the answers start coming back: `w0` carries tag 5 and
  product 12, `w1` carries tag 9 and product 42, on alternating cycles.
- Look closely at the cycle where `w0_valid` is 0: `w0_product` still reads 42,
  the *other* client's answer.

That last one is worth a second. The result **broadcasts to both writeback
ports** and only `valid` differs. A demux in hardware is not a switch that
moves data down one path — the data goes everywhere and one consumer is told to
look. Wires are free; steering them is not.

## What each side sees

```fsharp
let issue = fuLayout 4 [ "a", 8; "b", 8 ]
let clients = [ for i in 0..1 -> Stream.input $"c{i}" issue ]
```

A client's view is a stream of `{ tag; fields }`. It offers operands with a
`tag`, and later receives a beat with that same tag and the results where the
operands were. The tag is **opaque** — `warpFu` never looks inside it. In a real
design it is a thread number, so the answer knows which of a [**Barrel lane**](barrelLane.md)'s
slots to land in.

The unit's view is smaller still:

```fsharp
let stages = 2

let multiply operands =
    match operands with
    | [ a; b ] -> [ delayChain "mul" 16 stages (mul a b) ], stages
```

Two expressions in, one out — plus **the depth it took to produce them**. It is
applied verbatim to whoever won this cycle. Any fixed-latency, one-per-cycle core
drops in here unchanged; the whole sharing apparatus is wrapper.

That trailing `stages` is the part worth pausing on. `warpFu` needs the depth, to
know when a result will emerge and which tag it belongs to — but it is **not
told** the depth as a parameter. The core *reports* it, and the number is written
once, at the one place that actually knows.

The rule behind that: **a module may not ask its caller for a latency it does not
own.** A declared latency is unchecked by construction — nothing verifies that
the `2` you passed matches the pipe you built — and it rots the first time the
core changes. So anything that costs cycles either returns its depth like this,
or presents a `Stream` so the number never escapes at all.

## This is deliberately not request/response

Nothing correlates a reply to a call, and nothing waits. The two directions are
independent streams through a fixed-latency pipe, and the tag is payload that
happens to be useful for routing.

That is what leaves writeback order and latency free. A client that *waited*
for its answer would need somewhere to park, a timeout, and a story about
ordering. A client that just receives tagged beats needs none of it, which is
why this shape and not the one that looks more like a function call.

## When to share, and when not to

This is the part that is easy to get backwards, and there is a measurement.

Sharing looks like the frugal choice, so the instinct is to pool anything
expensive. On the GEP cluster the divider is 697 LUTs — 5.6% of the design
across all eight lanes. Pooling it **cost 44–118% of throughput and saved
nothing on the resource that was actually binding.**

The rule that came out of that:

> Pool by cost **×** rarity. A unit worth pooling has to be both big *and*
> rarely used. Cheap ones get their own copy, always.

`FuSharing` names the two choices — `PerLane` and `Pooled` — so a design says
which ratio it wants and the machinery follows. In a barrel datapath `PerLane`
is as cheap as fusing the arm straight into the pipeline, because that is
exactly what it elaborates to: no arbiter, no routing, the result lands in a
statically known slot.

And at one client, the pod *is* the unit. No arbiter, no grant register, no
demux — `ready` ties high, and only the tag delay line survives. A design
parameterized by client count does not sprout a pointless one-way arbiter in
its Verilog when that count is 1.

## The one contract

There is no buffer on the writeback side. A presented result is assumed to be
accepted, so a client must not have more requests outstanding than it can take
answers for. The returned streams therefore *declare* a `ready` that this
wrapper never reads.

That is the honest cost of keeping the wrapper thin. A client that cannot honour
it wants per-client skid buffers — still a change to the wrapper, not to the
unit.

## Try this

- Drop `c1_valid` to 0. `c0_ready` goes high every cycle: with one client there
  is nothing to arbitrate.
- Set both clients' operands to the same values but keep the tags different.
  The products are identical and the tags still come back to the right places —
  the tag is the only thing distinguishing them.
- Hold `w0_ready` low. Nothing stops: the wrapper does not read it. That is the
  contract above, seen from outside.
- In the source, change `stages` from 2 to 3 and re-run. Everything still works,
  and *that is the exercise* — the depth reaches `warpFu` from the core rather
  than from the call site, so there is no second copy of the number to forget.
  Try to write the bug instead: there is nowhere to put a `3` that disagrees.

## See also

- [**Barrel lane**](barrelLane.md) — where the tags come from, and the case where sharing is
  usually the wrong answer.
- [**Arbiter (one-hot)**](arbiter.md) — the pick that decides a cycle's winner.
- [**Farm**](streamFarm.md) — the other way to spread work across units, when the units are
  identical and the beats are interchangeable.
- [**Delay chain**](delayAlign.md) — the tag line that carries routing alongside the pipeline.
