# Expression recipe bounds

- **Status:** Accepted
- **Date:** 2026-09-28
- **Roadmap:** [post-M2 queue from the 2026-09-28 re-probe](../plans/post-m2-roadmap.md#queue-from-the-2026-09-28-re-probe), order 6

This decision settles order 6: whether ordinary expression authoring keeps the 64-node recipe limit, and what a node-limit rejection reports. The normative bounds are in the [expression authoring specification](../specs/expression-authoring.md#bounds-and-materialization-order).

## Decision

| Workflow | Max depth | Max nodes |
| --- | --- | --- |
| Ordinary expression authoring: function creation, complete-body replacement, targeted replacement, migration replacement bodies | 32 | **256** |
| Frozen Milestone 2 bounded Float64 replacement ([bounded structural editing](../specs/bounded-structural-editing.md)) | 32 | 64 (unchanged) |

A `StructuralRecipeNodeLimitExceeded` rejection now states the recipe's full node count and up to three of the largest subtrees that fit within the limit, each with its child-index path from the root and its recipe kind. A subtree nested inside one already listed is not repeated. The depth limit and its diagnostic are unchanged, and depth is still checked first.

## Reason

The 64-node bound was set for the first bounded Float64 replacement, where recipes were small arithmetic trees. Expression authoring later reused it as "the same practical bounds", without evidence about ordinary functions. The [2026-09-28 re-probe](../verification/rrawrlet-slice-reprobe-20260928.md) supplies that evidence: rrawrlet's leader loop is ~204 nodes as one body. Fitting it under 64 needed 2 bundling Product types and 5 helper functions that do not correspond to anything in the source program, and two pieces still sat near the limit. That is splitting driven by the bound, not by review.

256 covers the observed body with margin while keeping a request bounded. The limit still protects against runaway recipes and keeps one proposal's review readable; authors can still split a function where it helps review. Depth was never the constraint (the deepest probe recipe was far below 32), so it stays.

The rejection used to say only "more than 64 nodes", which gave no count and no hint where to split. Listing the largest subtrees that fit gives the author concrete candidates to extract into a helper.

The Milestone 2 bounded Float64 workflow and its frozen evaluation corpus keep 64, because their recorded evidence (including the node-overflow case at 65 nodes) is defined against that bound.

## Consequences

- `StructuralTransactionInternals.validateRecipeBoundsWith` takes explicit bounds; ordinary authoring uses `expressionAuthoringBounds` and the M2 evaluation tool uses `boundedFloat64Bounds`.
- Tests at the new boundary: 256 nodes pass and 257 reject for pure expressions; `Bind` with a wide List passes at 256 nodes.

## Revisit conditions

- A real program needs a single body over 256 nodes, or reviewers find bodies near the limit hard to review.
