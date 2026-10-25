# Function creation batches

- **Status:** Accepted; implemented
- **Date:** 2026-10-24
- **Issue:** none yet

This decision adds a by-name operation that creates several functions in one proposal. Each function is created as `simulateCreateFunctionByName` creates one; the [expression authoring specification](../specs/expression-authoring.md) stays authoritative for the recipes.

## Decision

`simulateCreateFunctionsByName` takes `functions`: a non-empty list of function requests, each with the fields of `simulateCreateFunctionByName` (`displayName`, `exported`, `typeParameters`, `parameters`, `resultType`, `requiredEffects`, `body`). The MCP tool `nyrwvy_create_functions` sends it.

- The functions are created in list order, all in one candidate snapshot built from the session's current snapshot, so the batch has one receipt, one readiness and one commit.
- A function's body and signature are resolved against the current declarations plus the functions listed before it. It may call itself and any earlier function of the batch by name; a call to a later one fails as an unknown function.
- A display name that already names a function, including an earlier one in the batch, rejects the whole batch with the same error a single creation reports.
- Any rejection of one function rejects the batch; nothing is created.
- The review is the ordinary `createFunction` review of the first function, with a `batch` list holding the reviews of the others in order. The commit publishes them all.

## Reason

An MCP trial built nine functions (about 28 KB of recipes) for one feature. It had to create them one call at a time, and since two proposals on the same base cannot both commit (`StaleBaseSnapshot`), it also had to alternate proposal and commit for each function. One request for the whole group removes both the repeated round trips and the stale-base failures in this case.

Auto-rebasing independent proposals onto each other's commits would need each proposal to be validated again against a snapshot it was not reviewed on, which defeats the review. A batch keeps one reviewed candidate.

Creating the functions in order inside one candidate reuses the single-function path unchanged: each body is materialized against the snapshot that already holds the earlier functions, so no new resolution rules are needed.

## Consequences

- A group of new functions takes one simulate and one commit.
- Functions must be listed after the functions they call. Mutual recursion still needs an existing placeholder callee, as before; declaring every signature first would need another resolution path and is not part of this decision.
- The commit receipt still names one created function (the first); the review's `batch` lists the others.
- Reading recipes from a file was proposed by the same trial and first left out because it would let the server read arbitrary paths. #479 later added `bodyFile`, `functionsFile` and `replacementFile`, confined to the snapshot's directory.

## Revisit conditions

Revisit when a workflow needs mutually recursive new functions in one request, when the commit receipt should list every created function, or when proposals for unrelated edits need to commit on the same base (met by #477; see [mixed authoring batches](mixed-authoring-batches.md)).
