# Stream pipe

A source, a transform, a sink — three lines that build nothing but wires. This
is the smallest possible use of the layer Warp 11 uses to connect almost
everything, and the page is about the two-wire contract underneath it.

## The problem it solves

Once a design has parts, the parts have to hand work to each other. And the
hard question is never the data — it is *timing*. What happens when the
producer has a value and the consumer is busy? What happens when the consumer
is free and the producer has nothing?

You could solve it per connection: count cycles, agree that stage two is always
ready three cycles after stage one, write the number in a comment. That works
until someone changes stage one, and then it fails silently and produces a
picture with a diagonal tear in it.

The alternative is to make every connection say so out loud. That is
**ready/valid**.

## The contract

Two extra wires alongside the data, pointing in opposite directions:

- **`valid`** — the producer says *I have a beat for you*.
- **`ready`** — the consumer says *I can take one*.

A beat moves on a clock edge **only when both are high**. That is the whole
protocol. Neither side may wait for the other before deciding — the producer
cannot hold `valid` low until it sees `ready` — because that deadlocks
immediately. Both just state their own situation, every cycle.

Everything else in this tier is that rule with more wires.

## What to look at

Watch `in_valid`, `in_ready`, `out_valid`, `out_ready` and both values.

- Poke `in_value = 7`, `in_valid = 1`, `out_ready = 1`. `out_value` is
  **8** immediately — no stepping. The transform is combinational.
- Now poke `out_ready = 0` and watch `in_ready`. It goes to **0 too**, in the
  same instant.

That second one is the important one. There is nothing between the two ends of
this design, so a stall at the sink is a stall at the source, right now, with no
cycle in between. Backpressure travels *backwards* through the chain as fast as
electricity, which is exactly what "this design is only wires" means.

## Reading the source

```fsharp
Stream.input "in" beatLayout |> Stream.map bump |> Stream.out "out"
```

`Stream.input` declares the ports — one per field of the layout, plus `valid`
in and `ready` out. `Stream.out` does the mirror. `|>` is F#'s pipe operator, so
the chain reads in the direction the data flows.

```fsharp
let beatLayout = layout1 ("value", 8)
```

A **layout** is the payload's shape: field names and widths. It rides along with
the stream from the moment it is created, which is why nothing downstream has to
be told what the beat looks like — and why the port names came out as
`in_value`, `in_valid`, `in_ready` without anyone writing them.

```fsharp
Stream.map bump
```

`map` is free. It transforms the payload and passes `valid` and `ready` straight
through: no register, no module, no cycle. When you want a transform that
*costs* a cycle, that is `stage`, and the vocabulary is deliberate — see
[**Stream stages**](streamStages.md).

## Why not just declare a latency

Warp 11 had a `pipe(latency)` proposal and rejected it. Declaring "this stage
takes 3 cycles" is the same unchecked assumption as the comment it replaced,
one level up: nothing verifies it, and it rots the first time the stage changes.

A stage that needs time holds `ready` low. That is not a convention — it is the
only thing it *can* do, and every consumer already handles it.

## Try this

- Poke `in_valid = 0` with `out_ready = 1` and watch `out_valid` follow.
- Set `out_ready = 0` and step. Nothing anywhere changes, because nothing can.
- Look at the signal list: six ports for what is conceptually one connection.
  That is the cost of the contract, and it is why `wormhole` exists — one call
  wires all of them.

## See also

- [**Stream stages**](streamStages.md) — what it costs to put storage in the chain, and what that
  buys.
- [**Fork and join**](streamFork.md) — one beat to two places, and back.
- [**Flow (valid only)**](flowSampler.md) — what to do when the producer genuinely cannot be told
  to wait.
