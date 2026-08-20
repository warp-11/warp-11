# Delay chain

Data going through three cycles of arithmetic, and a tag that has to travel the
same distance to still be describing it. The page is about the least glamorous
bug in pipelined hardware and the one-line fix for it.

## The problem

You pipeline something. The data now takes three cycles to cross your design —
fine, that is what pipelining is. But the *control* that went in alongside it
did not: the "this is the last pixel of the row" bit, the "this beat is valid",
the destination address. Those arrived on cycle 0 and the data they describe
arrives on cycle 3.

Nothing warns you. The widths are right, the design elaborates, the simulator is
happy. You get a picture with the wrong pixel marked, or a write to the wrong
address, or a row that ends one beat early — a bug that looks like an arithmetic
error and is not.

## What to look at

Watch `out`, `aligned_tag` and `raw_tag`.

- Poke `data = 10`, `tag = 1`, press **Step** once.
- Now poke `tag = 0` and `data = 20`. `raw_tag` goes to 0 **immediately** — it
  is the input, straight through.
- Step twice. `aligned_tag` is **still 1**, because it is still carrying the
  value that belongs with the data now emerging.
- Step once more and it goes to 0, in the same cycle its data arrives.

`raw_tag` is the bug and `aligned_tag` is the fix, side by side.

## Reading the source

```fsharp
delayChain "data" 8 3 (data + lit 1UL 8) ==> out
delayChain "tag" 1 3 tag ==> aligned
```

`delayChain name width stages source` is `stages` registers in a row. That is
all it is — three lines of `let r = reg …` folded into one call.

The reason it is worth a library entry is not the code, it is the **name at the
call site**. The registers come out called `tag_d1`, `tag_d2`, `tag_d3`, so when
you are staring at a waveform trying to work out where an off-by-one came from,
the signals say what they are and how far along they got. A hand-rolled version
tends to produce `r1`, `r2`, `r3`, or worse, three registers with unrelated
names in three different places.

**This shape reached four independent copies in this codebase before it was
lifted** — each written by someone who knew about the others. That is the
canonical example of the second-user rule failing quietly, and why the repo now
has a script that counts duplicated shapes.

## Inline, not a module

`delayChain` builds registers directly in the module that calls it, where
`delayOf` (from [**Dot product**](dotProduct.md)'s family) instantiates a `Delay8` submodule.
That is deliberate: the delay of a *control* signal belongs to the module whose
control it is. Making each stage an instance would put the pipeline's internal
shape into the instance list, which is noise for anyone reading the hierarchy.

Both exist because both are sometimes right. The rule from **Dot product**
applies — it is a definition-site choice.

## Counting the stages

The number 3 here has to match the pipeline it is shadowing, and nothing checks
that. That is the honest limitation: `delayChain` makes the alignment *visible*
and *named*, not automatic.

Where the alignment genuinely must not drift, the answer is not a bigger delay
helper — it is to put the control **in the payload**, so it cannot separate from
its data at all. That is what the stream layer does, and why every beat in
[**Farm**](streamFarm.md) carries its own `id`.

## Try this

- Set `tag = 1` for exactly one step and watch the 1 walk through
  `tag_d1`, `tag_d2`, `tag_d3` in the signal list.
- Change one `delayChain` to 2 stages and watch the tag arrive a cycle early.
- Open the **waveform** tab, record `raw_tag`, `aligned_tag` and `out`, and the
  three-cycle offset is a picture rather than an argument.

## See also

- [**Stream stages**](streamStages.md) — the same three cycles, with the control carried in the
  beat so it cannot drift.
- [**Edge detect**](edges.md) — the other thing a single delayed copy is for.
- [**Wrap counters**](wrapCounter.md) — control signals worth aligning to something.
