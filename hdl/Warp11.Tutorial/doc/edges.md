# Edge detect

Turning a level into an event. One register and three gates, which is the whole
of it — and the reason it is a library entry rather than something you write
inline is what the gating does.

## Level and edge

A wire is high or low. That is a **level**, and it says nothing about *when* it
became high. Most of the time what you actually want is the moment of change:

- a button that was pressed, not a button that is down
- a frame that started, not a frame that is in progress
- one increment per press, not one per cycle the press lasts

Software gets this for free — an event handler fires once. Hardware does not:
your `If (button) (fun () -> count + 1 ==> count)` counts every *cycle* the
button is held, which at 166 MHz is a few million increments per press.

## What to look at

Watch `rising`, `falling`, `changed`, `previous` and `pulses`, with
`enable = 1`.

- Poke `signal = 1`. `rising` goes high **immediately** — before you step.
- Step. `rising` drops to 0 even though `signal` is still 1, and `pulses` is
  now 1. The edge happened once.
- Poke `signal = 0`. `falling` goes high, the same way.
- Hold `signal` high and step ten times. `pulses` does not move.

That last one is the point: `pulses` counts presses, not cycles.

## Reading the source

```fsharp
let e = edgeDetect "sig" enable signal

e.rising ==> rising     // signal && !previous
e.falling ==> falling    // !signal && previous
e.changed ==> changed    // signal ^ previous
```

One register samples the signal; the three outputs compare the live signal
against that sample. `previous` is exposed too, because a design that wants the
plain delayed copy should not have to build a second one.

The register is why `rising` is a *pulse* rather than a level: on the cycle
after the change, `previous` catches up and the comparison goes quiet.

## The `enable` is the interesting parameter

```fsharp
let edgeDetect (name: string) (enable: Expr) (signal: Expr) = ...
```

`enable` gates **only the sample**. Tie it high and this is the ordinary form.
Gate it and you get edge detection *in the enabled domain* — the register only
looks at the signal on the cycles you say, so "changed" means "changed since the
last time we looked".

That is what a design straddling two rates needs. An I2S receiver sees its
frame clock turn over once per audio frame, not once per fast clock; sampling it
every cycle would report the same edge dozens of times. Gating the sample to the
bit clock makes one turn produce one edge.

This is also the cheap way to cross into a slower domain without building
anything that deserves the name "clock domain crossing" — which Warp 11 does not
have yet, and which this is not a substitute for when the domains are genuinely
asynchronous.

## Try this

- Set `enable = 0` and toggle `signal`. Nothing fires — `previous` is frozen, so
  the comparison never updates.
- Re-enable it and watch a single edge appear for whatever change accumulated.
- Watch `changed` while toggling: it is `rising | falling`, and sometimes that
  is the one you want.
- Count falling edges instead by changing the `If e.rising` to `If e.falling`.

## See also

- [**Delay chain**](delayAlign.md) — the same one-register idea, used to align rather than to
  compare.
- [**Counter**](counter.md) — what `pulses` is, and why it holds when nothing fires.
- [**Wrap counters**](wrapCounter.md) — a wrap signal is an edge somebody already computed for
  you.
