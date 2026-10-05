# Self calls that keep unlisted arguments

- **Status:** Accepted
- **Date:** 2026-10-05
- **Roadmap:** [post-M2 queue from the 2026-10-05 probe 23 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-23-owner-choice), order 54

A loop written as a tail self-call (order 49) carries its state in its parameters, and each self-call repeats every one of them. [Probe 23](../verification/map-set-probe23-20261005.md)'s `tarjanLoop` passed 47 of its 70 self-call arguments through unchanged. Keeping the state in a Product updated by `ProductUpdate`, which exists today, was [tried](../verification/map-set-probe23-20261005.md#follow-up-loop-state-in-a-product) and came out larger (236 nodes instead of 208), because every read of the state becomes a `FieldAccess`. The owner judged the evidence sufficient on 2026-10-05.

## Decision

In the name-resolved recipe form, a `directCall` that names the function being created or replaced may set `"keepUnlisted": true`. Every owner parameter that its `arguments` do not name is then bound to that same parameter. It is a reading shorthand, like `MatchUnion`'s `otherwise`: the recipe reader expands it into the full `SelfCall`, so the preflight, the stored `DirectCall`, the review and the node count are those of the call written in full. No semantic, IR or schema change.

- `keepUnlisted` on a call to any other function is rejected, since only a self call has parameters of the same names in scope.
- Without it, an omitted argument is still `PureExpressionCallArgumentMissing`, so a forgotten argument is never silently filled.
- The ID-keyed `selfCall` recipe has no such field; it is not the AI-facing form.

## Measurements

Probe 23's loops rewritten with `keepUnlisted`, dropping each argument that only names its own parameter, in the same session:

| Function | Self-call arguments | Recipe JSON | Nodes |
| --- | --- | --- | --- |
| `tarjanLoop` | 70 → 23 | 10,896 → 8,007 characters (−27%) | 208 (unchanged) |
| `dfsLoop` | 30 → 10 | 5,569 → 4,376 characters (−21%) | 105 (unchanged) |

Both were accepted first time, and the rewritten components matched probe 23's reference on all 200 graphs.

## Consequences

- `WireSymbolic` reads the field; the [expression-authoring](../specs/expression-authoring.md#selfcall) and [session](../specs/agent-authoring-session.md#self-calls) specifications and the recipe reference describe it. `SelfCallTests` covers creation and body replacement by name, the full call that results, the missing-argument error without the field, and the rejection on another function.
- The saving is in what an author writes and reads, not in the graph: a structural read of the function shows every argument.
