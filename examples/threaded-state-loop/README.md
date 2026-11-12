# Threaded state loop

A proof that a Nyrwvy program can hold long-lived state without any mutable place: the state is an
ordinary immutable value carried through the arguments and returns of the program's own
tail-recursive loop. `run(events, seed)` builds the initial `State`, then `loop` asks the `Events`
capability for the next event code, folds it into the state with the pure `step`, and tail-calls
itself; a negative code ends the loop and `run` returns the state's checksum.

The state is chosen to cover the shapes a real program threads: a turn counter and a scalar counter,
a 64-element `List<Int64>`, a 64-key `Map<Int64, Int64>`, a nested record, a depth-6 binary tree
(64 leaves) and a two-variant union. Each event code updates exactly one place:

| code | update |
| --- | --- |
| 0 | none (baseline turn) |
| 1 | scalar counter + 1 |
| 2 | list element at `turn mod 64` + 1 |
| 3 | map value at key `turn mod 64` + 1 |
| 4 | nested record's number + 1 |
| 5 | tree leaf at `turn mod 64` + 1 |
| 6 | the union's number + 1, rewritten as the other variant |

Every code but 0 raises the checksum by exactly one, so `run`'s result checks the run.

`run.mjs` first checks `step` against saved cases with `runPure` on both backends (the checksum,
the turn count, and the one place each code changes, read back through probe functions), then emits
the Zig library and measures each code at 100,000 and 1,000,000 turns with the value allocator
wrapped in a counting allocator: allocation count, peak live bytes, the runtime's recording-arena
capacity (`ny_arena_capacity()` — the arena the runtime keeps for its own records, not a work
arena; the arena a host declaration works in is created and freed per call, which is why the
process RSS is measured too), RSS, and leaks after `run` returns. Measured on 2026-11-12:

| code | allocations / turn (before the emitter fix) | allocations / turn (after the emitter fix) | recording arena |
| --- | --- | --- | --- |
| 0 | 0 | 0 | 0 |
| 1 | 0 | 0 | 0 |
| 2 | 1 | 0 | 0 |
| 3 | 1 | 0 | 0 |
| 4 | 1 | 1 | 0 |
| 5 | 7 | 7 | 0 |
| 6 | 1 | 1 | 0 |

Peak live bytes and RSS are not constants of the program, so the table does not pin them: within
every run the peak is identical at 100,000 and 1,000,000 turns — that identity is what the harness
asserts — but the values move between runs (peaks from 6,312 to 6,984 bytes across the runs
measured: 6,984 for every code in some runs, lower and differing by code in others; RSS between
2,736 and 2,872 kB). The first RSS reading of a run can sit about 128 kB below the rest, before
the process has touched its pages, so the harness allows 1 MiB of slack on RSS instead of
equality: far above that noise, and far below what retaining anything per turn would add over
900,000 extra turns.

The million-turn loop finishes for every code, nothing leaks, and neither the peak, the recording
arena nor RSS grows with the turn count: the loop is flat. A scalar field updates in place, at the
no-op turn's cost.

A list or map directly under the record updates in place in this shape, at the no-op turn's cost —
and the harness asserts it, for codes 2 and 3 as for the scalar. That was not always so: before
the emitter fix (`c0206086`, PR #660), each reference to the argument became a separate alias, so
the move-out treatment could not pair a field's read with its update, and the collection was
copied whole on each update — the "before" column: one allocation per turn at 64 elements, with
the copied bytes growing with the element count (at 64 elements that growth is not visible, one
allocation whatever its size; the proportionality is a property of what a copy is, not a slope
measured here). The follow-up investigation found the copy, before the fix, under every natural writing it tried —
six forms, including reading the field once and reading and updating inside one branch (those
variants were tried outside the repository; the tree keeps no evidence for them, and the writing
kept here is the one in `author.mjs`). One writing avoided it (bind every other field out, then
rebuild the record whole), but it depends on the emitter's internal ordering, so it was not
adopted. The fix unifies repeated argument reads within a block and joins blocks entered by a
simple jump, which lets the pairing succeed. The copy is still what happens when a branch or a
merge separates the read from the update. A nested record and a union variant cost one allocation
per turn, and a tree leaf costs the path length plus one (seven boxes for depth 6); the fix does
not change those. See `docs/decisions/threaded-state.md` for the decision this evidence supports.

Run it with:

```sh
node examples/threaded-state-loop/run.mjs
```

One run takes about a minute (one Zig test build and fourteen measured loops). The same check runs in
the extended Node suite as `.opencode/tests-extended/nyrwvy-threaded-state-loop.test.mjs`.
