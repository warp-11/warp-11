# Flow (valid only)

A producer that cannot be told to wait, meeting a consumer that can stall. The
type exists to make that situation honest rather than to solve it — because it
cannot be solved, only paid for.

## When ready/valid is a lie

The handshake in [**Stream pipe**](streamPipe.md) assumes the producer can be held back. Plenty
of things cannot be:

- an ADC sampling at a fixed rate
- an I2S microphone shifting in bits on its own clock
- a free-running counter, a video source, anything with a real clock behind it

Wiring one of those to a `ready` line does not make it stop; it just means the
`ready` wire is decorative. `Flow<'p>` is the same beat with **valid but no
ready** — the forward half only.

Using it says: this producer will not wait, and anyone downstream had better be
able to keep up.

## What to look at

Watch `out_valid`, `out_value` and `dropped_count`.

- Poke `sample = 1`, `out_ready = 1`, and **Run**. Beats flow, `dropped_count`
  stays at 0.
- Press **Reset**, poke `sample = 1`, `out_ready = 0`, and **Run**.
  `dropped_count` climbs, one per cycle.

Those are beats that no longer exist. Not delayed — gone.

## Reading the source

```fsharp
let sampled =
    { payload = ticks
      valid = sample
      layout = beatLayout }
    |> flowStage "staged"
```

A flow is built by hand here because its producer is a plain counter. `flowStage`
registers it — and registering a flow is **one register per field plus one for
valid**, nothing else. Compare [**Stream stages**](streamStages.md), where a stage needs a skid
buffer to survive a stall. A flow has no stall to survive, so this is what the
shape actually costs.

```fsharp
let stream, overflowed = flowToStream sampled
```

This is the interesting line. Giving a flow a `ready` it never had is the moment
data can be lost, and `flowToStream` returns **exactly which cycles it was lost
on** — `overflowed` is high whenever a beat arrived and the consumer was not
ready.

It is returned rather than swallowed on purpose. This is the one place a design
silently loses data, so the loss is a value you have to do something with:

```fsharp
let dropped = reg "dropped" 8
If overflowed (fun () -> dropped + lit 1UL 8 ==> dropped)
```

Counting it is the least you can do. The alternatives are asserting it never
happens (see [**Assertions**](assertions.md)), or putting a FIFO in front deep enough that the
assertion is provably true. What you may not do is ignore it — and the API is
shaped so that ignoring it takes deliberate effort.

## The other direction

`streamToFlow` goes the other way: drive `ready` high forever and keep the
forward half. That emits exactly the `lit 1UL 1 ==> s.ready` people were
already writing by hand — but now the *type* says the sink cannot refuse,
instead of leaving a reader to work out whether that tie-high was load-bearing.

## When Flow is an excuse

Reaching for `Flow` because backpressure is inconvenient is how you get a design
that drops beats under load and looks fine in every test. The honest test: **is
there something physical that will not wait?** If yes, `Flow` is the truth. If
the producer is your own logic that could perfectly well hold a beat, use a
stream and let it.

## Try this

- Stall `out_ready` for a few cycles, release it, and note that `dropped_count`
  never goes back down. Nothing recovers a dropped beat.
- Poke `sample = 0` and confirm nothing is dropped — no beat, no loss.
- Add an assertion that `dropped_count` stays zero, then stall the sink and
  watch the run stop on the exact cycle.
- Compare `staged_valid` with `out_valid` to see the one cycle `flowStage` costs.

## See also

- [**Stream pipe**](streamPipe.md) — the two-way contract this one deliberately gives up.
- [**Stream stages**](streamStages.md) — the skid buffer a stream stage needs and a flow does not.
- [**Assertions**](assertions.md) — how to say "this must never drop a beat" and be told when it
  does.
