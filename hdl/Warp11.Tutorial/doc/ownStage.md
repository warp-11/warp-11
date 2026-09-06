# Your own stage

Every stream so far arrived from the design boundary or came out of a library
call. This page builds a stage out of a **module of your own** — one whose
ports carry the ready/valid handshake themselves — and the small wrapper that
lets it drop into a chain as if it were any library stage. From the chain's
side, it is one.

The module is a worker that *grinds*: it accepts a beat, works on it for three
cycles, then offers the bumped result. While it grinds it cannot take another
beat, and its `in_ready` output is how it says so.

## What to look at

Poke `in_value = 5`, `in_valid = 1`, `out_ready = 1` (the tutorial preloads
these), then **Step** and count:

- steps 1–4 — `in_ready` is **low**: the worker took the beat on the way into
  step 1 and is grinding; nothing you poke gets in
- step 5 — `out_valid` goes 1 and `out_value` is **6**
- step 6 — `in_ready` is high again, and the next beat goes in

One beat every six cycles — the three grind cycles plus one each to accept,
finish and offer. A worker like this has a **throughput of 1/(cycles+3)**,
which is precisely the kind of stage worth replicating; that thread is picked
up under *Try this*.

Notice `out_value` already reads 6 during the grind. The register it lands on
is visible the whole time — but an answer is not an answer until `valid` says
so, the same lesson [**Shared unit**](sharedUnit.md) makes with its demux.

Now stall the sink: poke `out_ready = 0` and keep stepping. The worker finishes
its grind, raises `out_valid`, and then **holds** — valid stays up, the value
does not move, `in_ready` stays low — for as long as you leave the sink
stalled. Release `out_ready` and the beat leaves on the next step. Nothing was
lost, because both sides of the module speak the handshake.

## A module that owns its flow control

In [**Stream stages**](streamStages.md) the backpressure came from library
stages; here it comes from *inside your module*. That is the shape every real
unit in this repo has — a divider that is busy for `width` cycles, a barrel
lane, an audio filter: anything that costs time says so through `ready`,
because a latency number that travels between modules rots the first time the
module changes. The handshake is the module's boundary, not something the
caller wires up around it.

## Reading the source

Three pieces. First, the IO — a record of two `StreamPorts` groups:

```fsharp
type SlowWorkerIo =
    { input: StreamPorts
      output: StreamPorts }

let slowWorker cycles =
    defModule
        $"SlowWorker%d{cycles}"
        (fun p ->
            { input = streamInPorts p "in" 8
              output = streamOutPorts p "out" 8 })
        (fun io ->
            streamOfPorts beatLayout io.input
            |> streamIterate beatLayout (grind cycles)
            |> streamToPorts io.output)
```

`streamInPorts` declares `in_data`/`in_valid` arriving and `in_ready` leaving;
`streamOutPorts` is the mirror. Which side of the handshake each wire is on is
the *helper's* decision, made once — the record itself is direction-blind,
which is what lets the same bundle read back over an instance's staging wires.

Inside the body, `streamOfPorts` turns the input port group into a real
`Stream`, and `streamToPorts` lands one on the output group — so between those
two lines the module speaks the same stream API a design body does. The
grinding itself is `streamIterate` running a small `Iteration` record; the
source pane has its story (a tail-recursive function taken apart so each
recursive call costs a cycle), and it is deliberately not the point of this
page.

Second, the wrapper — the whole trick, three lines:

```fsharp
let slowWorkerOf cycles instName (s: Stream<Expr>) : Stream<Expr> =
    let c = (slowWorker cycles).NewNamed instName
    streamToPorts c.input s
    streamOfPorts beatLayout c.output
```

`.NewNamed` hands back the same `SlowWorkerIo` bundle the definition saw, over
the instance's staging wires. Then the *same two helpers* the body used run
again, pointed the other way: `streamToPorts` feeds the instance's input side
from the caller's stream, `streamOfPorts` reads its output side back as one.
`streamOfPorts` also registers the returned `ready` net, so the one-consumer
check applies to your module's stream exactly as it does to a library stage's.

Wiring it backwards is caught, not trusted: every staging wire knows which
side of the child it is, so swapping `c.input` and `c.output` fails at the
first misdirected `==>` with the port and instance named, and a child input
you forget to wire at all fails when the enclosing module finalizes.

Third, the design — which is now one line per hop:

```fsharp
streamSource inPorts
|> slowWorkerOf 3 "worker"
|> streamSink outPorts
```

## Why a wrapper and not something built in

A module is a bundle of ports and one body — nothing else. Anything shaped
like a call is an ordinary function written beside the module, and this page's
wrapper is the stream-flavoured one: the same pattern gives the audio filters
and the Mandelbrot lanes their `Stream -> Stream` shape. Because it is just a
function, partial application composes for free — `slowWorkerOf 3` is
`string -> Stream -> Stream`, which is exactly what
`Stream.specOf "worker" (slowWorkerOf 3)` wants when this stage joins a
pipeline written as data.

## Try this

- Stall the sink before the grind finishes (poke `out_ready = 0` on step 2),
  step past the offer, and confirm nothing is lost when you release it.
- Drop `in_valid` to 0 and step: `in_ready` sits high — an idle worker is just
  waiting, and the handshake says that too.
- In the source, change `slowWorkerOf 3 "worker"` to chain two —
  `|> slowWorkerOf 3 "left" |> slowWorkerOf 3 "right"` — and watch the signal
  list grow a second instance of the same definition.
- Replace the farm's plain stages in [**Pipeline as data**](streamPipeline.md)
  with `Stream.specOf "worker" (slowWorkerOf 3) |> Stream.lanes 3` — a
  1/(cycles+3) worker is the shape whose farm width is worth sweeping.

## See also

- [**Stream stages**](streamStages.md) — the library stage this page hand-builds an alternative to.
- [**Your own modules**](ownModules.md) — `defModule` and the wrapper-beside-the-module pattern, without streams.
- [**Farm**](streamFarm.md) — replicating a slow worker, which is where its throughput number starts to matter.
- [**Pipeline as data**](streamPipeline.md) — `Stream.specOf`, the door a wrapped module walks through to join a pipeline.
