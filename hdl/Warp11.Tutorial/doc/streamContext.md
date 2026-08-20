# Carrying context

Three dividers in a farm, and every quotient still knows which request it
belonged to — without the divider ever hearing about a request id.

```fsharp
Stream.farmWith "dv" 3 2 operands results identity
    (fun i -> divider $"dv{i}" 8 >> Stream.stages (i * 8) id)
    src
```

## The problem this exists for

Look at [**Farm**](streamFarm.md) first. Its beats carry an `id`, and the payload was *widened by
hand* to make room for one:

```fsharp
let tagged = layout2 ("id", 8) ("value", 8)
```

That works because the worker there is a transform we wrote, so it was free to
carry a field it never reads. A divider is not: it takes two operands and
returns a quotient, and widening it to carry someone's pixel coordinate would be
a strange thing to ask of a divider.

The usual alternative is worse — keep a queue beside the farm and pair answers
with questions by position. That is exactly what breaks here.

## What to look at

- Poke `in_dividend = 101`, `in_divisor = 7`, `out_ready = 1`.
- Poke `in_valid = 1` and step, changing `in_id` each time it is accepted
  (1, 2, 3 …).
- Watch `out_id` rather than the quotients.

Measured, issuing ids 1 to 9:

```
issued    1  2  3  4  5  6  7  8  9
returned  1  2  4  3  5  7  6  8  9
```

**Beat 4 came back before beat 3, and 7 before 6.** The lanes have deliberately
unequal depth, so a request dispatched later to a fast lane overtakes one sent
earlier to a slow one. A queue beside the farm would have handed answer 4 to
question 3, and every answer after that to the wrong question.

The id is what makes that harmless. `out_quotient` is always the answer to the
question `out_id` names.

## Nobody wrote a tag

The word "tag" is worth being careful with, because there isn't one here.

A **tag** is a value that travels *through* a unit so a result can be routed
back to whichever client issued it — that is `warpFu`, and [**Shared unit**](sharedUnit.md) is
the page for it. This is not that. Here the id never enters the divider at all:
it goes into a FIFO beside the lane it was dispatched to, and comes back out
paired with that lane's answer.

The farm can do this because it owns both the dispatch *and* the merge, so it
knows which lane produced each beat. `farmWith` is `farm` of `withContext` and
nothing else.

For a single unit rather than a farm, `withContext` is the same idea with one
FIFO:

```fsharp
withContext "dv" 4 operands results identity (divider "dv" 8) requests
```

## The depth is a throughput knob

`farmWith "dv" 3 2 …` — that `2` is the per-lane context depth, and getting it
wrong cannot produce a wrong answer.

The context FIFO is pushed and popped in lockstep with the lane's own accept and
emit, so it holds exactly the contexts of the beats in flight. Too shallow means
the source is held off sooner; it can never pair the wrong context with a
result. Each lane needs room only for the beats *it* has in flight, so three
one-at-a-time workers want 2, not 6.

## Try this

- Poke `out_ready = 0` for a while, then release it. The ids still come back
  matched — backpressure changes the timing and nothing else.
- Issue the same `in_id` twice and watch it come back twice. Nothing here is
  checking that ids are unique; that is the caller's business, and what the
  field means is entirely up to you.
- Look for `dv_0_context_store` in the signal list. That is one lane's context
  FIFO — eight bits wide, because that is what an id costs.

## See also

- [**Farm**](streamFarm.md) — the replication this is built on, and where the ids came from.
- [**Buffering**](streamBuffer.md) — the FIFO doing the holding here, on its own.
- [**Shared unit**](sharedUnit.md) — the case that genuinely needs a tag, because results must
  find their way back to independent clients.
