# Fork and join

One beat in, two branches, one beat out of each — then merged back to a single
stream. Fan-out and fan-in, which between them cover most of what a real design
does with the handshake.

## What to look at

Poke `in_value = 10`, `in_valid = 1`, `out_ready = 1` and step a few times.
Values come out alternating between **11** (the incrementing branch) and
**20** (the doubling branch): each input beat produces two output beats.

Then set `out_ready = 0`, step, and watch `in_ready`. The source stops.

## Broadcast: everyone, in lockstep

```fsharp
match streamBroadcast 2 source with
| [ a; b ] -> ...
```

`streamBroadcast n` copies every beat to all `n` branches. The rule that matters
is the handshake it builds: **a beat fires only when every branch can take it.**
The source's `ready` is the AND of the branches' readies.

So the slowest branch sets the pace for all of them. If one branch stalls, the
source stalls, and the other branch sits idle even though it was free. That is
not a flaw — it is what "every consumer sees every beat" costs, and if you did
not want lockstep you wanted a [**Farm**](streamFarm.md).

## Merge: arbitration, not ordering

```fsharp
Stream.merge [ incremented; doubled ]
```

`merge` takes N streams and produces one. Internally it is a tree of two-input
merges, each picking between its inputs when both offer — the one not served
last wins, so neither starves.

**What it does not give you is order.** Beats leave in whatever order they
arrive, and when the branches have different latencies that is not the order
they went in. This design keeps both branches at one stage deep so the
alternation is tidy; make them unequal and it stops being.

That is the rule the library states as *the pixel-beat rule*: **a beat that can
be reordered must carry its own identity.** Do not infer position from arrival
order. The [**Farm**](streamFarm.md) page shows what happens when you forget.

## Why the stages are there

```fsharp
let incremented = stage (Stream.map bump a)
let doubled = stage (Stream.map (fun v -> v + v) b)
```

Each branch has a register stage before the merge. Without one, the merge's
arbitration decision would feed combinationally back into the broadcast's ready
calculation, and the elaborator rejects the loop — correctly, because that is a
circuit whose value depends on itself.

The general shape: **a place where paths split and rejoin wants storage in
between.** One stage is enough.

## Try this

- Change one branch to `Stream.stages 3` and watch the output stop alternating.
- Watch `fork_ready_1` and `fork_ready_2` in the signal list — the two branch
  readies the source ANDs together.
- Set `out_ready = 0` and confirm both branches freeze together.
- In the source, make one branch's transform `id` and see that a branch doing
  nothing still costs its beat.

## See also

- [**Farm**](streamFarm.md) — the other fan-out: each beat to *one* worker, not all of them.
- [**Stream stages**](streamStages.md) — why the register between split and join is not optional.
- [**Stall probes**](streamProbes.md) — which branch is actually the slow one.
