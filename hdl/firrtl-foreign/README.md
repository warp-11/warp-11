# FIRRTL nobody here wrote

These files exist to answer the one thing the round-trip check cannot. That
check proves our reader and our writer agree — a construct misunderstood the
same way twice round-trips happily, and stays wrong.

So the judge here is not us:

```
this .fir ──> our reader ──> our Sim ──> trace ──┐
          └─> firtool ─────> Verilog ──> Verilator ┴─> must agree
```

If we read a primitive wrong, our trace and firtool's Verilog part company.

**They are deliberately in shapes our own emitter never produces** — a bare
widening `add` with no `tail` around it, `pad`, `shl`, `neg`, `cvt`, the
comparisons we spell as `not(lt(…))`, a memory with two readers on one array,
an instance whose ports are driven straight from expressions rather than
through staging wires, connects written in reverse dependency order, and
`bits(dshl(a, n), 7, 0)` — a slice of a computed value, which FIRRTL allows and
warp11's named-operand rule does not — a fold over a computed value
(`orr(xor(a, b))`), and **division by a signal**, which warp11's authoring
surface refuses outright and its reader must still take.

It keeps earning itself. `shifts.fir` is what showed that the reader owed the
file a hoisting pass — a slice of a computed value is ordinary FIRRTL, and our
rule that it needs a declared signal is *ours*, so the reader has to satisfy it
rather than refuse the file.

And it earned itself on the very first one. **FIRRTL's `sub` on two `UInt`
operands is `UInt<w+1>`, not signed** — the borrow does not make it signed, which is the
obvious guess and the one the reader had made. Round-tripping against our own
emitter could never have found it, because the export and the import were wrong
together.

Run them with the third leg:

```sh
FIRTOOL_LEG=1 ./run_differential.sh
```

Adding one: write low FIRRTL, check `firtool file.fir` accepts it, and give it
ports worth poking — the testbench drives every input with seeded random
stimulus and asserts every output, so a design with no inputs proves little.
