# Head to head: Warp 11 vs gplearn

Rescued from the Kotlin stack during the 2026-08-13 restructure. It was
gitignored there, so this directory is the only copy — the numbers below cannot
be regenerated from anything in this repo.

The six Feynman starter problems, same train/test split for both engines,
scored by test R². `manifest.csv` names the operator set and feature count
per problem; `datasets/` holds the splits.

| problem | Warp 11 | gplearn | |
|---|---:|---:|---|
| I.14.3 `m*g*z` | 1.000000 | 1.000000 | tie |
| I.12.1 `mu*Nn` | 1.000000 | 1.000000 | tie |
| I.13.4 `0.5*m*(u²+v²+w²)` | 0.956190 | 0.938969 | **Warp 11** |
| I.39.1 `1.5*pr*V` | 0.996776 | 0.985658 | **Warp 11** |
| I.25.13 `q/C` | 1.000000 | 1.000000 | tie |
| I.12.2 `q1*q2/r²` | 0.970792 | 1.000000 | *gplearn* |

**Why this is kept rather than regenerated.** The datasets are reproducible —
`Problems.fs` and `Srbench.fs` generate the same splits — but the gplearn
column is not: it came from running gplearn, which is not part of this repo
and not pinned anywhere. Rerunning Warp 11 alone would leave nothing to
compare against.

Treat the Warp 11 column as a record of the **Kotlin** engine at that date, not
a current claim. The F# engine is measured separately in `notes/BACKLOG.md`
(2026-08-11): same 3/6 solved at the head-to-head config, and 5/6 under the
README's own criterion once the solving config and a seed distribution are
used instead of a single fixed-budget run. I.13.4 remains unsolved by both.
