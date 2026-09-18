# Minimum AI graph-editing tools

**Status:** Maintained tool contract.

This surface makes the existing [AuthoringSession](agent-authoring-session.md) directly callable by an ordinary AI agent. It does not introduce another runtime, authoritative format, source language, planner, or human editor. The [research context](../concepts/structured-agent-actions.md) explains the intended boundary.

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
| `nyrwvy_open` | Explicit `create` or `open`, with a snapshot path relative to the worktree or absolute. |
| `nyrwvy_inspect` | Status, compact declarations, or a structured function graph with impact facts. |
| `nyrwvy_create_type` | Propose a Product/Union with name-resolved type references. |
| `nyrwvy_create_function` | Propose a function with named parameter/declaration references. |
| `nyrwvy_replace` | Propose a targeted replacement using an inspected node ID; omit the target only for whole-body replacement. |
| `nyrwvy_commit` / `nyrwvy_reject` | Publish or discard exactly the reviewed proposal handle. |
| `nyrwvy_request` | Direct structured access to every maintained protocol operation, including exact-ID operations. |
| `nyrwvy_close` | Release the session; committed state remains on disk. |

Tool arguments are objects, not JSON-generating Python/F#/C# programs. The bridge handles request IDs, JSONL transport, request serialization and session ownership. It does not resolve semantic ambiguity, retry mutations, auto-commit or inspect snapshot JSON. A cancelled/lost response requires reopening and inspecting committed state, not assuming that publication failed.

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

After reviewing the returned `createdFunction` and receipt, pass its exact `proposalId` to `nyrwvy_commit`. No declaration IDs are supplied for this creation. Existing IDs remain visible in the result and can be used through `nyrwvy_request` when exact identity matters.

## Structured graph reading

The additive JSONL operation `readFunction` takes exactly one of `function` (exact display name) and `functionId` (published stable ID). Unknown/ambiguous names fail; IDs are not inferred from spelling. Other properties are rejected.

```json
{"id":"read-1","op":"readFunction","function":"identity"}
```

The result keeps the focused query's snapshot ID, declaration, inferred node types, callers, callees and open obligations. Its `function.body` is a scoped graph containing `resultNodeId` and `nodes`. Each node has an `id` and a structured `kind` object. Literals expose their values; operands reference node IDs; conditional/match/Bind nodes retain their child graphs and lexical binding identities. Branch identity, value dependencies and lexical scopes are not flattened into F# debug strings.

This is a read projection, not a new accepted snapshot encoding or a raw graph-editing payload. Non-semantic node/arm/binding order is rendered by stable ID for consistent reads across reopen; parameter and List item order are retained. `focusFunction` retains its existing response format.

`Int64` literal values in this projection are decimal strings so JavaScript tool hosts do not round values above their safe-integer range. Recipe input accepts exact decimal strings as well as existing JSON integer tokens; `Float64` retains its explicit hexadecimal bits representation. These are transport encodings, not numeric conversions.

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

All simulation operations accept optional `structuredReview: true`. This selects the same structured function/focus projection inside the existing review; receipts, diffs, proposal handles, validation and commit semantics are unchanged. The default remains the existing wire format. Ordinary tools request structured review automatically. Targeted review includes both base and proposed focus so the agent can inspect actual old/new literal values and their branch context before publication.

Name resolution happens at simulation. Commit still binds the exact simulated proposal, never a re-resolved name or regenerated body. Stale proposals remain rejected. Closing a session drops pending proposals; reopening preserves only committed semantic identities.

## Named recipe reference guide

Use the existing expression kinds and explicit types. Only references differ from the stable-ID form:

| Semantic reference | Named form |
| --- | --- |
| Named/capability type | `{"kind":"named","name":"T"}` / `{"kind":"capability","name":"C"}` |
| Function parameter | `{"kind":"parameter","name":"value"}` |
| Product field access | `{"kind":"fieldAccess","source":<recipe>,"type":"T","field":"fieldName"}` |
| Product construction | `type`, plus `fields: [{field, value}]` |
| Union construction | `type`, `variant`, plus `fields: [{field, value}]` |
| Union match | `type`, `input`, plus `arms: [{variant, body}]` |
| Direct call | `function`, plus `arguments: [{parameter, value}]` |
| Higher-order List operation | `source` and `function` |
| Capability call | `capabilityValue`, `capability`, `operation`, `arguments: [{parameter, value}]` |
| Pattern field | `scopeDepth`, `slot: "field"`, `type`, `variant`, `field` |

`scopeDepth` and the `value`/`error` pattern slots retain the existing scoped recipe semantics. There is no hidden inference of missing branches, numeric promotion, effects or sequencing.

## Verification boundary

The ordinary client is exercised against the real maintained executable with a deterministic Verification Planner authoring/maintenance flow, exact-literal/name-ambiguity checks and a nested-scope response regression. Separate small transport tests exercise framing/serialization and lost responses. These run in the fast suite with Node 22 and do not install an agent harness or invoke a model.

OpenCode UI discovery and a real model choosing the tools are a distinct live integration boundary, not a claim made by these deterministic tests. The tool definitions follow OpenCode's [custom-tool contract](https://opencode.ai/docs/custom-tools/).

## Maintainer rule

Exercise new authoring operations through the ordinary tools before introducing workload-specific authoring scripts or evaluation adapters. Keep semantic construction and validation in Core; keep the bridge transport-only. Preserve usable exact-ID access rather than treating name-only authoring as the final architecture. Verification should cover the real contract or observed regression, not one test per serializer branch.
