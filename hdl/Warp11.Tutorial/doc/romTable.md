# ROM

Two lookup tables whose contents are decided when the design is elaborated and
arrive already in the chip. The page is about the oldest trick in hardware:
**if you cannot compute it fast, look it up.**

## What to look at

Step `index` from 0 to 7 and watch both outputs. No stepping, no writing, no
initialization sequence — the answers are there from the first cycle.

- `square` reads 0, 1, 4, 9, 16, 25, 36, 49.
- `prime` reads 2, 3, 5, 7, 11 — and then **0, 0, 0**.

Press **Reset** and look again. Still there. That is the difference from
[**RAM**](ram.md): a `mem` starts as whatever the silicon happened to contain, and a `rom`
starts as what you said.

## Reading the source

```fsharp
let squares = distributedRom "squares" 8 [| 0UL; 1UL; 4UL; 9UL; 16UL; 25UL; 36UL; 49UL |]
memRead squares index ==> square
```

A name, a word width, and the contents — an ordinary F# array, evaluated at
elaboration time. It could have been a `List.map` or a loop or anything else F#
can compute, because none of it exists at run time:

`distributedRom` rather than `rom` for the same reason [**RAM**](ram.md)'s memory says
`distributedMem`: the read here is combinational, and a combinational read is
only legal on storage declared to be built from LUTs. `rom` exists too and leaves
the choice to the synthesiser — use it with `memRead` when the table is large
enough to want a block RAM and you can afford the cycle.

```fsharp
let squares = distributedRom "squares" 8 [| for i in 0UL..7UL -> i * i |]
```

is the same ROM. That is the general shape from [**Bit shapes**](bitShapes.md) again — F# is the
metaprogram — and it is how real tables get built: sine values, reciprocal
estimates, gamma curves, CRC constants. You write the *derivation*, and the
bitstream carries the *answer*.

## Why a table is a good idea

A ROM read is one cycle — combinational here, so not even that. Anything you can
tabulate becomes a constant-time operation with no arithmetic at all.

This matters more in hardware than in software, because in hardware the
alternative is *area*. A divider costs about 700 LUTs. A sine calculation costs
a pipeline. A 256-entry table costs one small memory block, of which the board
has hundreds, and they are otherwise sitting idle. Trading memory for logic is
usually the right trade, and the GEP accelerator's reciprocal estimate does
exactly this.

The limit is that tables grow exponentially with input width. Eight bits in is
256 entries; sixteen bits in is 65,536. The usual answer is a smaller table plus
interpolation.

## Depth is a power of two

```fsharp
let primes = distributedRom "primes" 8 [| 2UL; 3UL; 5UL; 7UL; 11UL |]
```

Five values, but memories are addressed by a fixed number of bits, so the depth
rounds up to eight and the three unnamed addresses read zero. There is no
bounds check and no error — an address you did not fill is simply zero, which is
why `prime` goes quiet after index 4.

That is a place a bug hides. If those three addresses should be unreachable,
say so with an assertion (see [**Assertions**](assertions.md)) rather than trusting that nothing
will ask.

## What it becomes

The emitted Verilog carries an `initial` block listing the contents. Vivado
recognizes that pattern and turns it into a memory the **bitstream arrives
pre-loaded with** — no startup code, no loader, no first-cycle special case.
The values are part of the configuration that programs the chip.

The simulator models the same thing: contents loaded at construction, and
**`Reset()` reloads them**, so pressing Reset in the debugger models
reconfiguring the FPGA rather than just clearing registers.

Nothing stops a design *writing* to a `rom` — it is the same declaration with
initial contents, so a preloaded RAM is spelled exactly this way.

## Try this

- Set `index` to 5, 6, 7 and watch `prime` read zero while `square` keeps
  working.
- Press **Reset** with any index selected. Nothing changes, which is the point.
- Open the **memory** tab and page through `squares` — the contents are visible
  as a memory, because that is what it is.
- In the source, replace the literal array with `[| for i in 0UL..7UL -> i * i |]`
  and confirm the outputs are identical.

## See also

- [**RAM**](ram.md) — the writable kind, and the sync-versus-async question that matters
  much more once a memory is BRAM-sized.
- [**Bit shapes**](bitShapes.md) — F# computing structure at elaboration time, which is how
  real tables are written.
- [**Assertions**](assertions.md) — how to say "this address is never asked for" and be told
  when it is.
