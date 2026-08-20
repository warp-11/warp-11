# LFSR

A pseudo-random source made of a shift and an xor. It is the cheapest generator
in hardware by a wide margin, it has a property worth understanding, and it is
not a random-number generator.

## What to look at

Poke `step = 1` and press **Run** briefly, watching `value`. It scatters — no
pattern you can see, and it never sits still.

Then pause and step one cycle at a time, watching in **bin**. Consecutive
values share every bit but one, shifted along. That is the tell, and it is the
whole reason this is not a substitute for a real generator.

Watch `low_bit` while running: it is the bit about to be fed back, and it looks
random on its own.

## What it is

A **linear-feedback shift register**: the state shifts one place per step, and
the bit that falls off the end is xored back into a fixed set of positions.

```fsharp
let state = lfsr "state" 8 0xACUL step
```

Eight bits, a seed of `0xAC`, advancing whenever `step` is high. In hardware
that is one register, a shift (which is *free* — it is just wiring), and a
handful of xor gates. No adder, no carry chain, no multiplier. It fits in the
gaps of a design that is already full.

That cheapness is why it is everywhere: test stimulus, dithering, traffic
generation, spreading memory accesses around, breaking ties in an arbiter.

## The property that matters

An 8-bit LFSR with the right taps visits **every non-zero state exactly once**
before repeating — 2⁸ − 1 = 255 states. That is called *maximal length*, and it
is what "the right taps" means.

The measured behaviour of this design: **255 steps to return to the seed, 255
distinct states, and zero is never among them.**

Zero is excluded for a structural reason worth knowing: the all-zero state
shifts to the all-zero state forever. **Zero is a fixed point the sequence
cannot leave**, which is why `lfsr` refuses a zero seed at elaboration rather
than letting you find out later.

## Why the check walks the whole period

This is the reason this page exists as much as the LFSR is.

**A wrong tap mask still produces a plausible-looking stream.** It scatters, it
does not obviously repeat, it passes any eyeball test — and it might have a
period of 30 instead of 255, which will quietly ruin whatever you were using it
for. You cannot see the difference by looking.

So the living check does not sample it. It walks the full period, counts the
distinct states, and asserts there are exactly 2⁸ − 1 of them. That is the
difference between testing a *golden vector* and testing the *defining
property*, and this is the clearest case of it in the whole library.

## When not to use it

Consecutive states share all but one bit. The low bits are strongly correlated,
and anything that samples them as if they were independent draws will be wrong
in ways that are hard to see.

- **Stirring something** — dithering, tie-breaking, filling a memory with
  not-all-zeros: perfect.
- **Sampling a distribution** — a Monte Carlo simulation, a genetic algorithm's
  mutation decisions: use `xoshiro128pp`, which is in the stdlib for exactly
  this reason and costs rather more.

The GEP accelerator uses xoshiro, not this, and that was a deliberate choice.

## Try this

- Step one cycle at a time in **bin** and watch the shift.
- Set `step = 0` and confirm the state freezes — the sequence advances on your
  say-so, not the clock's.
- Press **Reset** and confirm it returns to `0xAC`, the seed.
- Add a breakpoint `state == 0xAC` and **Run** — it stops after exactly 255
  steps.

## See also

- [**Bit shapes**](bitShapes.md) — the shifting and slicing this is built from.
- [**Assertions**](assertions.md) — the other way to state a property the design must keep.
- [**Arbiter (one-hot)**](arbiter.md) — one place a cheap random bit is genuinely useful.
