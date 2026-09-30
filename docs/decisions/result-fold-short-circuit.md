# Result folds that stop at the first Err

- **Status:** Accepted
- **Date:** 2026-09-30
- **Roadmap:** [findings from the 2026-09-30 probe 11](../plans/post-m2-roadmap.md#findings-from-the-2026-09-30-probe-11), cost #1

[Probe 11](../verification/rrawrlet-slice-probe11-20260930.md) ranked Python exceptions written as data as its largest new cost: 138 `Result` nodes, 8% of its new nodes. Every fold whose Python loop can raise carried a `Result` accumulator, and every folder repeated a pass-through arm (`MatchResult(acc, …, ResultErr(err))`), because `ListFold` cannot stop. [String join and list slice](string-join-and-list-slice.md) deferred early-stop folds until a probe measured them; this is that measurement.

## Decision

No new construct and no new mode. The fold that stops at the first `Err` is `ListEffectFold` with a pure folder:

| Form | Seed | Folder | Result |
| --- | --- | --- | --- |
| `ListEffectFold(items, seed, folder, context)` | `S` | `(S, T) -> Result<S, E>`, pure or effectful | `Result<S, E>`; the folder is not called after the first `Err` |

`ListEffectFold` already selected this Result mode by type, and neither authoring nor persisted-graph validation requires its folder to declare effects: a pure folder was accepted and the node is effectful only when the folder is. This change makes that a documented contract, and:

- The C# backend's Result mode now ends the loop at the first `Err` (`NyList<T>.FoldWhile`) instead of passing the `Err` through the remaining items. The folder was already not called after the first `Err`, so the value and every effect are unchanged; only the remaining iterations are skipped.
- `ListFold` with a folder returning `Result<S, E>` for seed `S` is still rejected, and its message now names `ListEffectFold`.
- The spec, the language subset, the semantic-independence table and the agent recipe reference say so.

## Reason

**The construct already existed.** `ListEffectFold`'s Result mode has exactly the wanted semantics, and its checks (seed and item types, the `S` or `Result<S, E>` result, effects declared by the caller) hold unchanged for a pure folder. A pure `ListEffectFold` node is not effectful, so the enclosing function stays pure and runs with `runPure`.

**Adding the same mode to `ListFold` would duplicate it.** It would be backward compatible, since that shape is a type error today, but it would thread a second copy of the Result mode through validation, both IRs, the codecs, the backend, the viewer and the wire recipe, and leave two nodes with the same meaning for pure folders. Reviewers would then have to recognize both. The only cost of reuse is the name: `ListEffectFold` reads as "effectful". The documentation and the `ListFold` diagnostic point authors to it.

**Saving.** For one fold with its folder, the pass-through form costs 5 more nodes: the `ResultOk` seed, the folder's `MatchResult`, its `ResultErr`, the `Err` reference and the accumulator reference. `tests/Nyrwvy.Core.Tests/ResultFoldTests.fs` authors the same count both ways and checks that difference. Probe 11's folds that can raise would each save the same 5 nodes.

## Consequences

- No schema, codec, IR or viewer change. Existing graphs, including probe 11's, keep their meaning; rewriting a pass-through fold as a pure `ListEffectFold` is an ordinary body replacement.
- `tests/Nyrwvy.Core.Tests/ResultFoldTests.fs` covers a pure folder with no effects (by recipe and over the wire), the all-Ok, empty and first-`Err` results, the node counts, the `ListFold` hint and mismatched seed and result types. One case puts an `Err` before 4,000 items whose folder calls would each scan 960,000 code points; it returns in about 2 s (the run's own start-up), while 200 such calls already exceed the 5 s run limit.
- The pass-through form at up to 4,096 argument items was not measurably slower in `runPure` (both about 1.5 to 2 s including start-up): the saving is nodes and the guarantee, not time, unless the list is built in the program and is very long.

## Revisit conditions

- **Authors keep reaching for `ListFold`:** probes show pass-through arms despite the documentation and diagnostic. Then give the pure short-circuiting fold its own name (an alias node or a `ListFold` mode) rather than more prose.
- **Early stop without an error:** a consumer needs to stop on success, as Python `break` or `next(...)` does (probe 11's `leaderStep`). An `Err` carrying the found value works but reads poorly; a dedicated stop value would then be justified.
