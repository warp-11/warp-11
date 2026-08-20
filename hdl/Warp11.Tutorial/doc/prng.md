# PRNG

xoshiro128++ in fabric: 128 bits of state, one 32-bit word per `step`, and no
multiplier anywhere. The [**LFSR**](noise.md) page ended by saying it was not a
random-number generator. This is.

## What to look at

Poke `step = 1` and step.

- `value` produces a new 32-bit word every cycle. Unlike the LFSR, consecutive
  words share nothing visible — no shifted-along bit pattern.
- `roll` is the low three bits, which is how you take a bounded draw from one.
- `drawn` counts the words that have been taken.

The core resets to state 1, 2, 3, 4, so the sequence is the same every run —
which is the point. A design you cannot re-run identically is a design you
cannot debug.

## Read, then step

```fsharp
let word = instanceNamed "rng" (xoshiro128pp "Xoshiro128pp") load seed step
```

`word` is combinational from the *current* state: the value is already there
before you advance anything. So a consumer reads `value` and pulses `step` in
the same cycle it uses the word. That is one cycle per draw with no latency to
schedule around, which is why this shape rather than a "request a number, wait"
one.

## Why not just an LFSR

An LFSR is a shift register — each state shares all but one bit with the last.
That is fine for a test pattern and disqualifying for anything that samples,
selects or mutates, because successive draws are almost the same number.

xoshiro128++ costs shifts, xors, rotates and **two adds**. That is still tiny —
tens of LUTs — and it is the reason this is the default rather than a Mersenne
Twister or anything with a multiply in its update: multipliers on an FPGA are
DSP blocks, there are 1,248 of them on this board, and a design that spends
them on random numbers has fewer left for arithmetic that matters.

## Seeding, and the one forbidden state

```fsharp
load = 1, seed0..seed3 = <the state>
```

`load` replaces all four words in one cycle and outranks `step`. Real designs
seed from the host: a driver writes a 64-bit seed into registers, and the
*host* expands it to 128 bits with SplitMix64 before sending it. Doing the
expansion off-chip is deliberate — SplitMix64 is built on 64×64 multiplies, and
paying eight DSPs to initialize a generator that needs none is a bad trade.

The all-zero state is the lattice's one fixed point: from zero the update
produces zero forever. Nothing in the hardware forbids loading it, so **the
loader must not send it**. The reset state 1, 2, 3, 4 is non-zero for that
reason.

## How this design is judged

The tutorial's check walks 64 words against a software xoshiro128++ written out
longhand and compares them one at a time.

That is deliberate, and it is the same argument the LFSR page makes. A
generator with a rotate off by one bit still produces output that passes every
eyeball test — it *looks* random, because it very nearly is. The only check
worth writing is against the reference stream, word for word. Getting that
agreement also means the host can predict exactly what the fabric will draw,
which is what makes a hardware run reproducible against a software twin.

## Try this

- Poke `load = 1` with `seed0..seed3` set to something, then `load = 0`,
  `step = 1`. The stream jumps to a different place in the same sequence.
- Load all four seed words to 0. The generator sticks at zero forever, exactly
  as the note above says. This is the one input that breaks it.
- Watch `roll` over a few dozen steps and count how often each of the eight
  values comes up. Then do the same on the [**LFSR**](noise.md) design's `low_bit` and see
  the difference between a good generator and a shift register.

## See also

- [**LFSR**](noise.md) — the cheap one, and when it is enough.
- [**ROM**](romTable.md) — the other way to get numbers into fabric, when they need to be
  specific rather than unpredictable.
- [**Barrel lane**](barrelLane.md) — GEP's engine, which draws one of these per thread per
  turn.
