# Streams and `wormhole` — the connection layer

Reference for the ready/valid stream layer in `Warp11/Streams.fs`. **This is the
preferred way to wire anything stream-shaped**, and most designs in this repo
are built out of it rather than out of raw signals.

A `Stream<'p>` is a payload plus `valid` and `ready`. A beat transfers on the
cycle both are high — the standard AXI-style handshake — and that single rule
is what makes stages composable: a stage that needs time holds `ready` low, and
everything upstream of it stalls correctly without knowing why.

## The shape of it

```fsharp
defModule
    "Brighten"
    (fun p ->
        (streamInputPorts p "px" pixelLayout,          // the boundary, declared
         streamOutputPorts p "out" pixelLayout))
    (fun (pxPorts, outPorts) ->
        streamSource pxPorts                           // the boundary, as a stream
        |> streamMap (fun (col, value) -> col, value + lit 1UL 8)
        |> streamStageFor pixelLayout                  // one registered pipeline slot
        |> streamProbe "after_stage"                   // stall telemetry, no behaviour
        |> streamSink outPorts)
```

Payloads are **named fields from a `Layout`**, not bit positions, so a stage
reads `beat.cx` rather than `beat[47:16]`. Both ends of a link are built from
the same `Layout` value, which is what makes it impossible for them to disagree
about the encoding.

## The boundary — five helpers, and which one you want

A stream at a module boundary is three or more ports: a field per layout entry,
a `valid`, and a `ready` going the other way. Declaring those by hand and
wiring them one `==>` at a time is where the mistakes live — the wires are the
same type, they sit next to each other, and a swapped pair elaborates cleanly.
So the boundary is declared as a **group**, and the body reaches it through
whichever of these matches what it is doing:

| you are… | use |
|---|---|
| declaring the ports | `streamInputPorts p "px" layout` / `streamOutputPorts p "out" layout` |
| reading your own consumed boundary as a stream | `streamSource pxPorts` |
| landing a stream you *received* on your own produced boundary | `streamSink outPorts s` |
| landing a stream your own logic *built* there | `streamDrive outPorts valid payload` |
| driving an instance's consumed boundary | `streamToInputPorts inPorts s` |
| reading an instance's produced boundary | `streamOfOutputPorts outPorts` |

The split between `streamSink` and `streamDrive` is the one worth knowing.
`streamSink` hands the consumer's `ready` **back up the chain** to whatever
produced the stream, which is exactly right when the stream came from a stage.
A stream a body makes out of its own logic has no such wire — its `ready` *is*
the boundary's, read directly to gate whatever produces the beat — and handing
the port back to itself is an elaboration error, correctly, since a module may
not drive its own input. So:

```fsharp
// received from a stage: streamSink
streamSource pxPorts |> compress |> streamSink outPorts

// built here: read `ready`, drive the other two thirds
let taking = outPorts.ready &&& holding
streamDrive outPorts holding (addrOf slot, dataOf slot)
```

The last two rows are the same boundary seen from **outside**, where every
direction is flipped: an instance's `StreamInputPorts` are wires the parent
drives. They exist so a parent does not spell the handshake out in four `==>`
in an order it has to get right, which is the loose-port threading the port
group removes, reappearing one level up.

## `wormhole` — one call connects, whatever the topology

`wormhole` is the connect operation. Its whole point is that **the topology is
decided by a number, not by which function you call**:

```fsharp
// one producer to one consumer
source |> wormhole sink

// one producer fanned out to N workers, and gathered back
source |> wormholeOut Broadcast n (fun i s -> worker i s) |> wormholeIn stages sink
```

**It scales to one.** At `n = 1` no arbiter, no grant logic and no selection
nets are emitted — the client's `ready` is a bare `1'd1`. That is verified in
the emitted Verilog, not assumed, and it is why a design can be written for N
workers and deployed at one without carrying dead logic.

Fan-out and fan-in are **clustered** above `fanFlatMax` (16, the measured
threshold): a flat tree up to that width, a two-level tree beyond it, so no
single net drives or collects all N endpoints. At 104 lanes that distinction is
the difference between meeting timing and not.

## Pipelines as data

A pipeline is a list of stages, and one call builds it:

```fsharp
let stages =
    [ Stream.spec "scale" scaleModule |> Stream.lanes 4
      Stream.spec "clamp" clampModule
      Stream.specFromFunction saturateStage |> Stream.probed "sat" ]

source |> Stream.pipeline stages |> streamSink outPorts
```

The pipeline vocabulary lives in the nested `Stream` module, so it is spelled
`Stream.spec` where the operators above are bare — `Stream.pipeline`,
`Stream.farm` and the rest read the same way at every call site in the repo.

`Stream.spec` names a module, `Stream.specFromFunction` takes a plain function,
and **a call site cannot tell which it got** — that is the call-site invariance
rule, and it means a stage can switch between inline logic and its own module
without touching the pipeline that composes it. `Stream.lanes n` replicates a
stage across n workers; `Stream.probed` attaches telemetry.
`Stream.pipeline2`/`Stream.pipeline3` are the heterogeneous forms, for when the
payload type changes between stages.

`Stream.farm n worker` is the direct form: replicate a worker n ways, dispatch
and merge around it.

## Buffering

`streamFifo "name" depth s` absorbs a burst and propagates a pause only once
full. First-word fall-through, so `payload` and `valid` arrive together as the
contract requires; depth must be a power of two and at least 2 — for a single
beat of slack, a stage is already the buffer.

**Its storage is not part of its contract.** Up to `streamFifoDistributedMax`
(64) the words live in LUTs and the head is a combinational read; at or above it
they live in a block and the head is a synchronous read behind a two-slot skid.
Both hold exactly `depth` beats, both sustain a beat per cycle, and both present
the same `Stream` — so changing a depth from 8 to 8192 changes where the bits
sit and nothing a caller can name.

## Telemetry

`streamProbe` inserts stall counters and changes nothing about behaviour.
`streamReport` walks a design's probes and prints where the stalls were, which
is how the Mandelbrot pod's feed path was found to be the bottleneck rather
than its lanes. Probes cost registers, so they are opt-in per link.

## Flow — valid only, no backpressure

`Flow<'p>` is a payload and `valid` with no `ready`: a beat transfers on every
cycle `valid` is high and the consumer cannot refuse.

```fsharp
let stream, overflowed = flowToStream someFlow
```

**`flowToStream` returns `overflowed` rather than swallowing it.** That term is
high exactly on the cycles a beat was dropped, and converting a Flow to a
Stream is the one place a design silently loses data — so the loss is a value
the caller has to spend: count it, assert it never fires, or buffer ahead of it.

Flow is a *narrower* type, not a lighter one. Backpressure is how a stage says
it needs time, so a Flow is right only where the producer genuinely cannot be
stopped and a `ready` would be a lie — a free-running sampler, an AXI channel
that is already committed.

## Conflate — keep-latest into DDR

`snapshotSource` latches a wide state coherently in one cycle from a
free-running design, and `streamConflate3` writes it into a triple-buffered DDR
region: three slots, keep-latest, with an overrun count and an IRQ pulse. The
host reads whichever slot was most recently published.

The gate that matters: publication is held behind `writerIdle`, so no in-flight
AXI write can race a host read. Game of Life streams 167M generations/s into
DDR through this and the host sees coherent frames at 30 Hz.

## What is not built

- `constIn` (a one-entry latching stream for run constants), `streamThrottle`
  (a latency-independence test tool), and a general `buffer(n)`. The Mandelbrot
  row coalescer is a `buffer(16)` in all but name and is the prototype a general
  one would come from.
- `UsageData` and an elaboration-time area gate — every operator declaring its
  LUT/DSP cost so a configuration over budget is refused before it is built.

The design target for the whole layer is Akka Streams' operator vocabulary and
blueprint semantics, with StreamIt as the optimizer brief.
