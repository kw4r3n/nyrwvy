# Compact simulate replies

- **Status:** Accepted
- **Date:** 2026-09-28
- **Roadmap:** [post-M2 queue from the 2026-09-28 probe 3](../plans/post-m2-roadmap.md#queue-from-the-2026-09-28-probe-3), order 14

This decision settles order 14 of the post-M2 queue: how much of a proposal an AuthoringSession `simulate*` reply returns. [Probe 3](../verification/rrawrlet-slice-probe3-20260928.md) (friction 4) measured about 1.7 KB of reply per recipe node. A 16 KB `teamLoop` creation request (206 nodes) got a 352 KB reply, and the simulate replies of the three probe parts came to 165, 460 and 216 KB against 12, 32 and 32 KB of requests. For a live agent that is the dominant context cost. The [session specification](../specs/agent-authoring-session.md#simulate-review-detail) is the authority for the wire shape.

## What a reply contained

A simulate reply is `{ proposalId, review }`. The review carries the receipt (transaction, base and proposed snapshot IDs, snapshot and semantic fingerprints), readiness, and per kind a diff and the affected function or functions. Every such function was rendered with its whole body: `createdFunction` and `proposedFunction` for creation and whole-body replacement, `baseFocus`/`proposedFocus` for targeted replacement (two complete focuses, each with per-node `nodeTypes`), and `functionFocus` for exact-ID operations. The default projection writes each node's `kind` as an F# debug string, and a branch, match or Bind node's string includes its nested regions in full. Those nested nodes are also listed on their own, so a region is repeated once for every enclosing node.

Measured over the probe 3 transcripts, function body nodes were 91%, 93% and 79% of the simulate reply bytes of the data, leader and usage parts, and nodes that reprinted nested regions were 87%, 86% and 69% of those body bytes. In the `teamLoop` reply, 347.6 of 352.5 KB was the node list, its largest single node 10.7 KB, while the receipt was 0.5 KB, the signature 1.8 KB and the effect evidence 2.1 KB. `structuredReview: true` (used by the ordinary tools) avoids the repetition but still returns every node.

The body was echoed because the reviews were built as "show the proposed function", and the earlier compaction for creation and whole-body replacement only removed a *second* copy (`materializedBody`, duplicate focuses). No consumer needed the node list to commit: `commit` takes only the `proposalId` and binds the exact simulated proposal, and the caller authored the recipe it would be reading back.

## Decision

**Compact by default.** Every `simulate*` request accepts `detail: "compact" | "full"`, default `compact`; any other value is a `ProtocolError` before simulation. The reply gains a `detail` field. In compact replies every function in the review (`createdFunction`, `proposedFunction`, and the `function` of each focus) is its ID, display name, parameters with IDs, display names and types, result type and `requiredEffects`, with `body: { resultNodeId, nodeCount }`. A focus omits the per-node `nodeTypes`. Nothing else changes.

**Full on request.** `detail: "full"` returns exactly the previous review, and `structuredReview` still selects the structured graph for it. `structuredReview` alone does not select `full`, so the ordinary tools, which always send it, get compact reviews too. After commit, `readFunction` (structured) and `focusFunction` return the published graph with impact facts, as before.

**Kept in both forms**, because they are what a reviewer checks before publication and what the [M6 boundary](../plans/post-m2-roadmap.md) protects ("lower context cost must not hide affected consumers, stable IDs, or obligations"):

- the proposal handle and the receipt with base and proposed snapshot IDs and fingerprints;
- readiness, including the obligation IDs that block a complete commit;
- created and changed stable IDs with display names (function, parameters, types, effects, capabilities, added variants);
- every diff: added and removed node IDs, targeted node summaries and affected uses, migration call sites and body rewrites, semantic diffs with obligations created and resolved, repaired functions;
- `bodyEffectRequirements` with evidence and `aborts`;
- a focus's `callers`, `callees` and `openObligations`.

Diagnostics of a rejected simulation are unchanged; they were never part of the echo.

## Reason

**Fix the measured friction narrowly.** This follows the precedent of #168, which compacted `declarations` after a live run showed discovery returning more function detail than ID and signature discovery needed. Only function bodies inside reviews are elided. The targeted diff, which is proportional to the edit rather than to the function, stays; so do the migration diffs, which are the automatic rewrites of other functions and thus impact facts.

**A flag on the simulate request, not a new query.** A focus query (`readFunction`/`focusFunction`) reads the published snapshot, so it cannot show a pending proposal. A `reviewProposal` lookup would add a second handle-based read to the protocol. `detail: "full"` reuses the one review projection that already exists, and a re-simulation to see the graph costs one pending proposal that is dropped with the session.

**Default rather than opt-in.** The consumers of simulate replies are all in this repository: the JSON-lines protocol tests, the OpenCode and MCP graph tools (`tools/nyrwvy-agent`), the evaluation bridges and their fixed prompts, and the exploratory scripts under `scripts/`. They read the proposal handle, receipt, readiness, IDs, names and parameters, which compact replies keep. Only tests that asserted a node kind inside the review needed `detail: "full"`. No external consumer depends on the node list; an opt-in flag would leave the cost where it was measured, with live agents that do not know to ask.

**Measured effect.** The three probe 3 parts were authored again, unchanged, against the session before and after this change (`probe3/*14.py`, outside the repository; only the client binary and the moved rrawrlet source path differ). Simulate replies fell from 168 to 14 KB (data), 473 to 31 KB (leader) and 166 to 38 KB (usage, at the order 13 state), and the `teamLoop` reply from 364 KB to 4.5 KB, of which the effect evidence is about half. Simulate replies are now smaller than the requests that produced them. All results are unchanged: data 26/26, leader S1-S6 as expected, usage 8/8.

## Consequences

- `WireEncodeCommon.functionSummary` and `compactFocus` render the compact forms, and `WireEncode.compactProposal` is the default simulate encoder; `proposalWith` takes the `detail` label.
- `nyrwvy_create_function` and `nyrwvy_replace` take an optional `detail` argument, and the tool descriptions, the open guide and the M4 evaluation bridge prompt describe the compact default.
- `nodeCount` counts every node of the body, including nested regions, so it can be checked against the recipe the caller sent.
- Amended after review: compact diff node summaries carry `literal` values and `referenceDisplayName`, a targeted diff adds `proposedTarget`, and `detail: "full"` adds `rewrittenFunctions` to migration and union-variant evolution reviews, which have no function focus to expand ([session contract](../specs/agent-authoring-session.md#simulate-review-detail)). Without them, retargeting the wrong one of two sibling `false` literals looked identical in a compact review, and `full` added nothing for those review kinds.
- The debug-string `kind` of the full default projection still repeats nested regions. It is only returned on request now; `structuredReview` is the compact way to read a full graph.

## Revisit conditions

- **Proposal graph lookup:** agents re-simulate with `detail: "full"` often enough that the duplicate proposals or request bytes matter. A `reviewProposal { proposalId, detail }` read would then return the stored review.
- **Large targeted or migration diffs:** a workload shows node summaries in `replaceTarget` or migration `bodyRewrites` dominating replies. Node IDs with roles, like the whole-body diff, are the next step.

## 2026-10-07: bounded impact and pending pages

A real Claude authoring task reached the large-diff revisit condition: a valid three-function Product-field migration returned 55,488 bytes even at explicit compact detail. The client diverted the response to an inaccessible overflow file; the author added three helpers to shrink the rewrites before it could review and commit. Omitting whole bodies alone did not bound impact summaries.

Compact results now have a 16 KiB UTF-8 limit, with explicit projection counts and requiresDetailedReview. Small review shapes and normal receipt/readiness/identity facts are preserved. Larger impact lists/text are marked as partial, never silently treated as complete. The existing PendingProposalView supplies readonly readProposal pages (8 KiB), full structured graphs by default, exact original indices and baseIsCurrent. This avoids resimulation and uses the same immutable proposal authority and commit checks. Full simulate detail remains explicit and unbounded. See the session contract for the exceptional oversized-receipt/identity behavior and page limits.

This amends the earlier "everything else is kept" and "flag, not query" decisions after their stated revisit condition was observed. It adds no new semantic edit, publication authority or execution framework.
