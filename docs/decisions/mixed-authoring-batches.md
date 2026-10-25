# Mixed authoring batches

- **Status:** Accepted; implemented
- **Date:** 2026-10-25
- **Issue:** [#477](https://github.com/kw4r3n/nyrwvy/issues/477)

This decision lets one proposal hold several different authoring steps (types, capability operations, functions, body and target replacements, by-name operations), so that a group of unrelated edits takes one simulate and one commit. It generalizes [function creation batches](function-creation-batches.md) to every by-name step kind and does not rebase proposals.

## Decision

A new by-name operation, `simulateBatchByName`, takes `steps`: a non-empty list. Each step is `{kind, ...}` where `kind` names an existing by-name request and the other fields are that request's fields, without `id`, `op` or `expectedSnapshotId`. The MCP tool `nyrwvy_batch` sends it.

1. **Step kinds (v1):** `createType` (`simulateCreateTypeByName`), `createTypes` (`simulateCreateTypesByName`), `createFunction` / `createFunctions`, `addCapabilityOperation` and other `simulateOperationsByName` operations (`operations`), `replaceBody` (`simulateReplaceBodyByName`) and `replaceTarget` (`simulateReplaceTargetByName`). The file forms (`bodyFile`, `functionsFile`, `replacementFile`) are accepted, and `stepsFile` may replace `steps`. Out of v1: dependency upgrades, signature and field-type migrations with caller rewrites, `verifyChange`, and anything exact-ID only.
2. **Resolution:** steps run in list order. Each step is simulated by its existing workflow against an in-memory workspace (`AgentWorkspace.create`) holding the base snapshot plus every earlier step's result, so a step resolves names against the base and all earlier steps, exactly as the function batch does. No new resolution rules are added. Target node IDs in `replaceTarget` refer to the snapshot the step sees. Earlier steps keep the base's node IDs, so an ID read before the batch stays valid unless an earlier step replaced that node.
3. **Failure:** any rejected step rejects the batch, and nothing is proposed. The rejection names the failing step's index and kind and carries that step's own error. v1 stops at the first failing step; the per-item aggregation of `simulateCreateFunctionsByName` (#475) stays inside a `createFunctions` step.
4. **Review:** the reply is the review of the first step with a `steps` list holding each later step's compact review in order, as `batch` does for functions. The 16 KiB compact bound applies to the whole reply. When it is exceeded, `projection.requiresDetailedReview` points at `steps[i]` paths that `readProposal` pages, as for other omitted paths.
5. **Receipt and commit:** the batch is one proposal over the base snapshot S0. Its candidate is the last step's snapshot with a fresh ID and `ParentId = S0`, validated once against S0 with `SnapshotValidator.validateProposal`, the same check `publishValidatedSnapshot` applies at commit. The receipt binds S0 and that candidate, and commit publishes it. Stale-base semantics are unchanged: if anything was committed after S0, the batch is rejected with `StaleBaseSnapshot` and must be simulated again.
6. **Readiness** is evaluated on the final candidate only. An intermediate step may leave obligations that a later step resolves.

## Reason

The trial behind #477 needed a type, a capability operation, several functions and a few replacements for one feature. Each kind still had to go propose, commit, propose, commit, because two proposals on the same base cannot both commit. The function batch removed this for functions only.

Rebasing a stale proposal at commit was the other option in the issue. It is not taken:

- The receipt binds the review to the exact base and result fingerprints (`Workspace.fs`). A rebased proposal would publish a result nobody reviewed, which the function-batch decision already rejected.
- There is no record of what a proposal read and wrote (`SemanticDiff` lists removals, renames and field changes, but not added or modified functions), so "touches nothing in common" cannot be checked without a new read and write set tracker across every workflow.
- Rebase helps only proposals made without knowing about each other. Multi-agent proposal composition is a stated non-goal. One agent making several edits saves no calls with rebase, because it still proposes and commits each edit; a batch makes N edits one simulate and one commit.

Chaining the existing workflows over in-memory workspaces reuses every step's validation and review unchanged; the batch adds only the loop, the re-parenting of the final candidate and the review composition.

## Consequences

- A feature that touches several declaration kinds takes one simulate and one commit, and the review covers all of it.
- Steps must be ordered so that later ones refer to earlier ones; mutual references between new declarations still need the existing workarounds (#476).
- A batch of many large steps can exceed the compact review bound more often; paging covers it.
- The batch is atomic: one bad step rejects all, which makes one correction cost a resend of the whole batch. `stepsFile` keeps that resend out of the agent's context.
- This supersedes the non-goal "combining structural and M1 operations atomically" in [bounded structural editing](../specs/bounded-structural-editing.md); that line now points here.
- The function-batch decision's revisit condition "proposals for unrelated edits need to commit on the same base" is met; that document links here.

## Tests

- A type, a function using it and a replacement of an existing function's body commit in one batch, and the commit publishes all three.
- A later step that names a declaration created by an earlier step resolves it.
- One bad step (an unknown name in the third step) rejects the batch with that step's index and kind, and nothing is proposed.
- A batch simulated on S0, after another commit moved the workspace to S1, is rejected with `StaleBaseSnapshot`.
- A step that leaves an open obligation followed by a step that resolves it is ready; the reverse is not.

## Revisit conditions

Revisit when a workflow needs dependency upgrades or migrations inside a batch, when per-step aggregation of errors across kinds is needed, or when independent agents need to commit unrelated proposals concurrently, which only a rebase with read and write sets would serve.
