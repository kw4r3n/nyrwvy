# Minimum AI graph-editing tools

**Status:** Maintained tool contract.

This surface makes the existing [AuthoringSession](agent-authoring-session.md) directly callable by an ordinary AI agent. It does not introduce another runtime, authoritative format, source language, planner, or human editor. The [research context](../concepts/structured-agent-actions.md) explains the intended boundary.

OpenCode and [stdio MCP clients](mcp-graph-tools.md) share the tool definitions and transport under `tools/nyrwvy-agent/`. The MCP guide includes setup and configuration for Codex CLI, Claude Code and Zed.

## Completion boundary

An agent can create/open a snapshot, discover declarations, inspect typed graph structure, create Product/Union types and functions, replace a chosen expression or an intentional whole body, review the proposal, commit/reject it, and reopen committed identities. Existing exact-ID operations remain available for rename, effect/capability declarations, supported migrations, readiness and backend emission.

The minimum does not mean every imaginable graph transformation. Unsupported semantic constructs and arbitrary node/edge mutation are not added merely to fill an operation matrix. Add the next operation when a concrete task cannot be expressed through the maintained tools.

## Setup and direct tools

Build the maintained executable once from the repository root, and rebuild after changing its implementation:

```sh
dotnet build tools/Nyrwvy.AuthoringSession --configuration Release
```

OpenCode discovers `.opencode/tools/nyrwvy.ts`. These ordinary tools are not restricted to an evaluation agent or a temporary evaluation workspace. They use the existing JSONL process transport and one explicitly selected snapshot per OpenCode session.

| Tool | Role |
| --- | --- |
| `nyrwvy_open` | `create`, `open`, same-content `pin`, or reviewed `upgrade`; optional `dependency` selects a complete source for creation, supplies the exact original version for pin recovery, or names the new version for an upgrade previewed as compatible. Relative paths use the selected workspace/worktree. |
| `nyrwvy_inspect` | Status, compact declarations, or a structured function graph with impact facts. |
| `nyrwvy_view` | Write a human-readable HTML page and return its path, for a person to open in a browser: Function Focus of one committed function, or Change Review of a pending proposal (`proposalId`). See [human-readable view](#human-readable-view). |
| `nyrwvy_recipe_reference` | Read the maintained name-resolved `ExpressionRecipe` wire shapes without opening a snapshot: no argument for the categorized index of kind names, `category` for its one-line signatures, `kind` or `kinds` for exact shapes. |
| `nyrwvy_create_type` | Propose a Product/Union with name-resolved type references. |
| `nyrwvy_create_function` | Propose a function with named references; reject an existing local/imported function's exact display name. |
| `nyrwvy_replace` | Propose a targeted replacement using an inspected node ID; omit the target only for whole-body replacement. |
| `nyrwvy_upgrade_dependencies` | Propose a dependency selection update together with explicit local field, function and Capability operation repairs (`simulateDependencyUpgradeByName`); review and commit it like any other proposal. |
| `nyrwvy_commit` / `nyrwvy_reject` | Publish or discard exactly the reviewed proposal handle. |
| `nyrwvy_request` | Direct structured access to every maintained protocol operation, including exact-ID operations. |
| `nyrwvy_close` | Release the session; committed state remains on disk. |

Tool arguments are objects, not JSON-generating Python/F#/C# programs. The bridge handles request IDs, JSONL transport, request serialization and session ownership. It does not resolve semantic ambiguity, retry mutations, auto-commit or inspect snapshot JSON. A cancelled/lost response requires reopening and inspecting committed state, not assuming that publication failed.

For repeated execution checks, the [saved-fixture MCP client](../../examples/run-fixtures/README.md) sends stored `{op, arguments}` objects directly to `nyrwvy_request`, avoiding a print-and-copy round trip. It retains full responses on disk and displays a short comparison summary. Execution inputs are [RunValue data](../../examples/run-fixtures/run-values.md), not the `ExpressionRecipe` shapes returned by `nyrwvy_recipe_reference`; the existing session validation and execution limits still apply.

`nyrwvy_request` reaches `runPure`, whose `hostSources` compile and run arbitrary C# with the user's permissions. The tools therefore start AuthoringSession without host sources, and such requests fail with `RunHostSourcesDisabled`. An operator enables them as configuration, never per request: `NYRWVY_ALLOW_HOST_SOURCES=1` in the environment of the OpenCode or MCP host process (or the MCP server's `--allow-host-sources`, or `allowHostSources: true` in the adapter context) makes `nyrwvy_open` start the session with `--allow-host-sources` and report `hostSourcesAllowed: true`. Do not enable it where tool calls are auto-approved ([MCP guidance](mcp-graph-tools.md#host-sources-run-arbitrary-code)).


For example, a function proposal takes these arguments directly:

```json
{
  "displayName": "identity",
  "parameters": [{"displayName": "value", "type": {"kind": "primitive", "primitive": "bool"}}],
  "resultType": {"kind": "primitive", "primitive": "bool"},
  "requiredEffects": [],
  "body": {"kind": "parameter", "name": "value"}
}
```

To create against a pinned source, use `nyrwvy_open` with `{"mode":"create","snapshot":"planner.json","dependency":"policy.json"}`. The source must be complete and standalone; its exact content is retained in a sidecar, so later source changes do not change or invalidate the pin. Imported declarations can be inspected/referenced but not edited. Reopen with only `mode: "open"` and the local path; clone/move both the local graph and its sidecar together. For an old source-only selection, use `mode: "pin"` and optionally `dependency` pointing to an exact original copy. Pin only migrates/repairs storage, never upgrades the selected version. A dependency argument on ordinary open is rejected. See the [session contract](agent-authoring-session.md#pinned-snapshot-dependencies).

After reviewing the returned `createdFunction` summary (IDs, display names, signature, `nodeCount`) and receipt, pass its exact `proposalId` to `nyrwvy_commit`; pass `detail: "full"` to review the proposed body graph itself. No declaration IDs are supplied for this creation. Existing IDs remain visible in the result and can be used through `nyrwvy_request` when exact identity matters.

## Transport lifetime

`close()` is idempotent, rejects new requests, and gives the already accepted request queue **5 seconds** to drain successful responses. After draining it sends stdin EOF and allows **5 seconds** for the child's `close` event. A queue deadline or transport failure settles pending/queued callers with an actionable error; unsent requests are discarded. Shutdown then allows **5 seconds after SIGTERM**, escalating to **SIGKILL with a final 5-second reaping window**. These are fixed per-phase limits, not request timeouts during ordinary authoring; the longest shutdown path is 20 seconds of deadline waits. Cancellation after sending invalidates the transport and starts termination even without an explicit close.

Sending a signal is not proof of exit. Open-failure cleanup and close retain the session slot until the child is reaped and its pipes close. If even the SIGKILL window expires (for example, termination was denied by the OS), close reports that ownership is retained rather than claiming success or allowing a replacement child. After resolving the external process problem, close again to release the reaped slot. A sent mutation with a missing response has **unknown outcome**: inspect committed state after reopening; never automatically replay it. Normal shutdown still drains responses and releases the actual snapshot lock, removing the `<snapshot>.lock` file; a killed process leaves the file behind, and the next open simply reuses it. A child that exits before answering its first request (a missing snapshot, a snapshot open elsewhere, a file that is not a snapshot) is reported with the session's own startup diagnostic and no replay advice, since nothing was sent.

The evaluation-only OpenCode bridges that duplicated this transport (the M4 authoring bridge, the scoped pure-control bridge and the M0/M1 `nyrwvy-eval` plugin) were removed from main; replay them from commit `800f364`. The frozen structural and ListMap bridges and their shared `.opencode/lib/evaluation-jsonl-runtime.ts` were removed from main on 2026-10-18; replay them from their [evaluation freeze tags](../operations/evaluation-freeze-tags.md) or from commit `b3a958a`.

## Structured graph reading

The additive JSONL operation `readFunction` takes exactly one of `function` (exact display name) and `functionId` (published stable ID). Unknown/ambiguous names fail; IDs are not inferred from spelling. Other properties are rejected.

```json
{"id":"read-1","op":"readFunction","function":"identity"}
```

The result keeps the focused query's snapshot ID, declaration, inferred node types, callers, callees and open obligations. Its `function.body` is a scoped graph containing `resultNodeId` and `nodes`. Each node has an `id` and a structured `kind` object. Literals expose their values; operands reference node IDs; conditional/match/Bind nodes retain their child graphs and lexical binding identities. Branch identity, value dependencies and lexical scopes are not flattened into F# debug strings.

This is a read projection, not a new accepted snapshot encoding or a raw graph-editing payload. Non-semantic node/arm/binding order is rendered by stable ID for consistent reads across reopen; parameter and List item order are retained. The earlier `focusFunction` operation, whose response kept an older format, was retired on 2026-10-18; use `readFunction`.

`Int64` literal values in this projection are decimal strings so JavaScript tool hosts do not round values above their safe-integer range. Recipe input accepts exact decimal strings as well as existing JSON integer tokens; `Float64` retains its explicit hexadecimal bits representation. These are transport encodings, not numeric conversions.

## Human-readable view

`nyrwvy_view` (JSONL `viewFunction`) takes exactly one of `function` (exact display name) and `functionId`, as `readFunction` does, plus an optional `output` path ending in `.html`. A relative `output` resolves against the snapshot's directory, and the result must stay inside that directory (its subdirectories are created as needed); an `output` that would land elsewhere, by `..` or as an absolute path, is refused with `ViewWriteFailed` and nothing is written. So is an `output` (or the default directory) that passes through a symbolic link or reparse point below the snapshot's directory, including a link at the target file itself, since the string check alone would let a link carry the write elsewhere. The page is written to a temporary file beside the target and renamed over it, so a link created at the target name after the check is replaced, not written through. On Linux (x86-64 and arm64) every directory below the snapshot's directory is opened by handle without following links, missing ones are created through those handles, and the file is created and renamed relative to its directory's handle, so a parent directory swapped for a link after the check cannot redirect the write either. Other platforms keep the path-based check, which leaves that window open. Without `output`, the page is written to `nyrwvy-views/` beside the snapshot under the viewer's stable-ID-derived file name. The reply is `{ functionId, displayName, kind: "functionFocus", mediaType, path }`; the HTML itself is not returned, so it costs the agent no context. The page renders the committed state with the same [viewer](../developer/architecture.md#human-review-and-projections) as `tools/Nyrwvy.ViewerExport`. It is a projection: it is never read back, and editing it changes nothing.

With `proposalId` instead (JSONL `viewProposal`), `nyrwvy_view` writes the Change Review of a pending proposal, so a person can review it before `nyrwvy_commit` or `nyrwvy_reject`; `function` and `functionId` must then be absent. `output` and the default directory work as above, and the default file name is derived from the proposal's transaction ID. The reply is `{ proposalId, kind: "changeReview", mediaType, path }`. Writing the page publishes nothing and leaves the proposal pending; a committed or rejected proposal is no longer viewable. An operations proposal (`simulateOperations`, `simulateOperationsByName`) renders the Change Review projection its review already carries. Every other proposal kind renders, for each function it creates or rewrites, the committed focus (when the proposal's base is still the committed state) and the proposed focus; the types, effects and capabilities it creates or rewrites; and the engine review exactly as the session returned it to the agent (the full review), so requested intent and resulting diff come from the engine, not from the page.

## Targeted edits and pre-commit review

`simulateReplaceTargetByName` takes `function`, `targetNodeId` and a name-resolved `replacement` recipe. The node ID is obtained from `readFunction`, never invented. It delegates to the existing targeted workflow, including owner/scope/type validation and identity preservation outside the edited region.

```json
{
  "id": "edit-1",
  "op": "simulateReplaceTargetByName",
  "function": "identity",
  "targetNodeId": "<node ID returned by readFunction>",
  "replacement": {"kind": "boolLiteral", "value": true},
  "structuredReview": true
}
```

All simulation operations accept optional `structuredReview: true`, `detail` and `expectedSnapshotId`; the last, passed through `nyrwvy_request`, rejects the simulation with the workflow's stale-base code when another publication happened after the caller read that snapshot ([session contract](agent-authoring-session.md#inspected-base-snapshot)). Reviews are compact by default: each function is summarized by its signature, `resultNodeId` and `nodeCount`, while receipts and fingerprints, readiness, diffs, effect evidence, aborts, callers, callees and open obligations stay in the reply. `detail: "full"` adds the per-node graph (and a focus's `nodeTypes`), and `structuredReview` selects the structured function/focus projection for it; proposal handles, validation and commit semantics are the same at both levels ([session contract](agent-authoring-session.md#simulate-review-detail)). Ordinary tools request structured review automatically, and `nyrwvy_create_function` and `nyrwvy_replace` take an optional `detail` argument. Targeted review includes both base and proposed focus and the targeted diff, whose node summaries carry literal values and referenced display names even when compact (`previousTarget`/`proposedTarget` show the old and new value); request `detail: "full"` to read the surrounding branch context before publication. For migrations and union-variant evolution, `detail: "full"` adds `rewrittenFunctions`, the proposed bodies of every rewritten function.

Inspect and review distinguish declared `requiredEffects` from derived
`bodyEffectRequirements`, with effect, node, and referenced declaration IDs as
evidence. See [body effect requirements](body-effect-requirements.md) for report
scope and incomplete-reference handling; inspection never widens a declaration.

Name resolution happens at simulation. Commit still binds the exact simulated proposal, never a re-resolved name or regenerated body. Stale proposals remain rejected. Closing a session drops pending proposals; reopening preserves only committed semantic identities.

## Named recipe reference guide

`nyrwvy_recipe_reference` is the ordinary AI-facing wire-shape discovery surface. It is read-only, does not open an AuthoringSession, and may be called before `nyrwvy_open`. With no argument it returns the kind names by category (`bool`, `string`, `int64`, `float64`, `bits32`, `list`, `tasks`, `structured`, `optionResult`, `calls`, `control`, `references`, `literals`). `category` returns that category's one-line signatures in wire field names, such as `"float64Sqrt": "(value: Float64) -> Float64"`, which is often enough to write a scalar recipe without reading its shape. `kind` returns one exact name-resolved shape with concise construction notes, and `kinds` returns several in one call (duplicates once each, an unknown kind rejected). Pass at most one of the three. `kinds` takes plain strings checked when read, so the tool schema lists the kind names only once. It describes the symbolic ordinary-tool format, not the stable-ID protocol form and not the semantic rules in place of validation.

`nyrwvy_create_function.body` and `nyrwvy_replace.replacement` remain generic structured objects rather than a large recursive tool schema. Their descriptions point to this reference so models can fetch only the recipe shape they need.

Use the existing expression kinds and explicit types. Only references differ from the stable-ID form:

| Semantic reference | Named form |
| --- | --- |
| Named/capability type | `{"kind":"named","name":"T"}` / `{"kind":"capability","name":"C"}` |
| Function parameter | `{"kind":"parameter","name":"value"}` |
| Product field access | `{"kind":"fieldAccess","source":<recipe>,"type":"T","field":"fieldName"}` |
| Product construction | `type`, plus `fields: [{field, value}]` |
| Product update | `source`, `type`, plus `fields: [{field, value}]` for the replaced fields only |
| Union construction | `type`, `variant`, plus `fields: [{field, value}]` |
| Union match | `type`, `input`, plus `arms: [{variant, body}]` or `[{variants: [...], body}]`, and an optional `otherwise` body |
| Direct call | `function`, plus `arguments: [{parameter, value}]` |
| Higher-order List operation | `source` and `function` |
| Capability call | `capabilityValue`, `capability`, `operation`, `arguments: [{parameter, value}]` |
| Pattern field | `scopeDepth`, `slot: "field"`, `type`, `variant`, `field` |

`scopeDepth` and the `value`/`error` pattern slots retain the existing scoped recipe semantics. There is no hidden inference of missing branches, numeric promotion, effects or sequencing.

## Verification boundary

The ordinary client is exercised against the real maintained executable with a deterministic Verification Planner authoring/maintenance flow, exact-literal/name-ambiguity checks and a nested-scope response regression. Separate small transport tests exercise framing/serialization and lost responses. The MCP contract also checks sessionless recipe discovery for `conditional`, `fieldAccess`, and `matchUnion`, the concrete wire-shape mistakes observed in the first independent consumer project. These run in the fast suite with Node 22 and do not install an agent harness or invoke a model.

OpenCode UI discovery and a real model choosing the tools are a distinct live integration boundary, not a claim made by these deterministic tests. The tool definitions follow OpenCode's [custom-tool contract](https://opencode.ai/docs/custom-tools/).

## Maintainer rule

Exercise new authoring operations through the ordinary tools before introducing workload-specific authoring scripts or evaluation adapters. Keep semantic construction and validation in Core; keep the bridge transport-only. Preserve usable exact-ID access rather than treating name-only authoring as the final architecture. Verification should cover the real contract or observed regression, not one test per serializer branch. Host adapters must reuse the shared tool definitions rather than fork their behavior. Keep the on-demand recipe reference aligned with the maintained name-resolved `WireSymbolic` surface; add focused regression checks for observed discovery failures rather than one test per recipe variant.

Pure function types and the `functionReference` / `functionInvoke` recipes are
available through the shared recipe reference and ordinary name/ID authoring.
See [pure function values](pure-function-values.md) for the exact wire shapes and
restrictions. Focused inspection retains reference dependencies and shows
function-valued node types; review validates referenced signatures and purity
before publication.

## Program-defined capability implementations

Use `nyrwvy_recipe_reference` with `kind: "capabilityConstruct"` to discover the
operation/function mapping recipe. `nyrwvy_create_function` can define a factory
returning a Capability; `nyrwvy_replace` edits that mapping with the existing
review/commit path. Inspect exposes exact capability, operation and function IDs.
See [program capability implementations](program-capability-implementations.md).
