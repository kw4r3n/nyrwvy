# Snapshot-backed agent authoring session

**Status:** Current

This specification defines the Milestone 3 maintained authoring boundary tracked by issue #153. It composes the existing semantic query, reviewed mutation, structural expression, persistence, execution-projection, and backend APIs. Later declaration-authoring slices join this same session boundary without creating another workspace authority.

## Purpose

An ordinary agent must be able to open one explicitly selected Nyrwvy snapshot, discover stable declarations, propose and review semantic changes, persist accepted changes across authoring-process restarts, close the process, and later resume from the same committed semantic identities.

The maintained path is a single-workspace, single-writer session. Frozen M1/M2 evaluation protocols remain historical verification surfaces and are not imported as the production authoring protocol.

## Pinned snapshot dependency (one level)

`create --snapshot <local> --dependency <existing-source-snapshot>` explicitly selects one independent, strict, complete source snapshot. `open --snapshot <local>` re-resolves that selection from `<local>.dependency.json`. A new session cannot attach another dependency to an existing local snapshot; nested dependencies are rejected. The manifest pins the absolute source path, source snapshot ID, and structural fingerprint. The local snapshot contains a dependency ID and canonical-source digest marker, so removal or substitution of the manifest does not silently turn a dependent workspace into a standalone one. Missing, changed, corrupt, or incompatible sources fail closed on reopen; publication also checks the source and manifest again. The manifest and local snapshot are separately atomically written; a failed create can leave an orphan manifest, which must be inspected/removed explicitly before retrying. No cross-file crash atomicity or concurrent dependency-writer transaction is promised.

The read/review/workspace and C# emission projection use the validated union of imported and locally authored declarations. All imported declarations (including their body and referenced type closure) remain in the source file, keep their exact stable identities and are inspectable via `declarations`/`focusFunction`. Local snapshots persist **only** local declarations and references to imported IDs, never a copy of imported declarations. Validation rejects duplicate identities, missing references and invalid combined graphs. A candidate that changes or removes an imported declaration cannot be published. The selected provenance is inspectable via the read-only `dependency` operation. The dependency is selected at workspace creation, not added by a mutation proposal; all planner declarations and edits still use ordinary simulation, review and explicit commit. This is deliberately a single, full-snapshot dependency, not a package resolver, general namespace mechanism or merge system. Standalone strict snapshot decoding continues to reject dependency-bearing local files without resolution.

## Session lifecycle

A session starts in exactly one of two explicit modes:

1. **Open** an existing snapshot file. The file is decoded with the existing strict `SnapshotCodec`, validated, and wrapped in `AgentWorkspace`.
2. **Create** a new snapshot file. The session creates `Snapshot.empty` with a system-owned snapshot ID, validates it through `AgentWorkspace`, serializes it with `SnapshotCodec`, and creates the selected persistent file before reporting success.

Opening a missing file is an error. Creating over an existing file is an error. There is no implicit fallback between the two modes.

A live session owns one workspace path. Proposal handles are process-local and may be discarded when the process ends. A new process reopens the last published snapshot and must simulate/review any later change again.

## Persistence guarantee and failure boundaries

The maintained storage contract is **atomic snapshot visibility and persistence across authoring-process restarts**, not machine-crash or power-loss durability:

| Boundary | Maintained guarantee |
| --- | --- |
| Successful create/commit response | The complete snapshot has been written, the temporary file has been closed, and the selected path has been created/replaced before success is reported. A later authoring process can reopen the published state while the OS/filesystem remains healthy. |
| Authoring-process termination after success | The published file does not depend on the terminated process's memory or proposal handles. Normal close and abrupt process termination preserve the acknowledged snapshot under the same healthy-OS assumption. |
| Snapshot visibility during publication | Same-directory replacement is the publication boundary; supported storage must provide the local filesystem's atomic rename/replace and exclusive-lock semantics. Network, distributed, or unusual filesystems are not independently certified by this contract. |
| OS crash, reboot after kernel failure, or power loss | **No guarantee.** `File.WriteAllText` plus `File.Move` does not explicitly flush file contents or parent-directory metadata to durable media. An acknowledged commit may be lost or damaged under these failures. |
| Response lost during/after publication | The mutation outcome is unknown. Reopen, decode/validate, and inspect the persisted state before deciding the next action; do not replay the mutation or reuse the old proposal handle automatically. |

This scope describes the existing implementation rather than claiming stronger storage behavior from process-reopen tests. A future machine-crash guarantee needs an explicit supported OS/filesystem scope, durable file and directory synchronization, and targeted failure-injection evidence. Its error protocol must distinguish a pre-replacement failure from a post-replacement synchronization failure: the latter cannot safely be described as an unchanged authoritative file.

See `AuthoringSessionTests` for acknowledged publication/reopen, abrupt process termination, single-writer exclusion, and reported I/O/external-modification failure checks. These tests establish process-level behavior, not power-loss durability.

## Single-writer and external modification rule

The maintained session is intentionally not collaborative storage.

- One authoring session owns a sidecar exclusive lock for the selected workspace path.
- The session remembers the exact persisted source it opened or last published.
- Immediately before publication it verifies that the authoritative snapshot file still contains that exact source.
- A mismatch, deletion, or replacement is reported as an external-modification error. The session does not merge, rebase, or revive an earlier approval against the new file.

The lock coordinates maintained authoring sessions. The persisted-source comparison detects writers that do not honor that lock.

## Proposal and publication lifecycle

Queries do not mutate state. Simulation and review do not execute capabilities.

A mutation follows this lifecycle:

1. Build an intent using one of the existing agent authoring APIs.
2. Simulate against the current `AgentWorkspace` and return the existing review facts plus an ephemeral proposal handle.
3. The caller either rejects the proposal or asks to commit that exact handle.
4. Core commit/review validation produces an immutable candidate `AgentWorkspace` or rejects it as invalid/stale.
5. On a candidate commit, serialize the candidate workspace through the existing strict snapshot codec.
6. Re-check the persisted-source token, write the complete candidate to a temporary file in the same directory, and replace the authoritative file as one publication boundary.
7. Only after persistence succeeds does the session replace its in-memory workspace and report commit success.

A reported temporary-write or replacement failure leaves the session's current workspace at the previously published state and does not intentionally publish the candidate file. The existing I/O-failure tests retain the previous authoritative file. This is not a promise about OS/storage failure or a lost response after a successful replacement; those boundaries are distinguished above. A rejected semantic commit never reaches the persistence step.

The session does not provide a durable proposal database. Existing proposal handles that become stale after another publication are allowed to fail through the existing stale-base checks.

## Read/query surface

The maintained session exposes read-only discovery over the current workspace:

- current snapshot ID plus structural and semantic fingerprints;
- declaration discovery for functions, product/union types, effects, and capabilities;
- focused function inspection, including stable graph-node identities and impact facts already supplied by semantic queries;
- callers, callees, open obligations, dependencies, and uses through the existing query projections.

Read projection may expose semantic facts and stable IDs, but it grants no raw snapshot publication authority.

## Mutation surface

The session is an adapter over existing authoring contracts:

- ordinary `AgentWorkflow` operations: display-name rename, add function parameter, add Product field with exhaustive constructor plans, remove an unused Product field, delete an unreferenced function/type/effect/capability, resolve missing argument, and add/remove required effects;
- whole-body `ExpressionRecipe` replacement;
- targeted `ExpressionRecipe` replacement from issue #148;
- atomic function creation with ordered `ParameterSlot` references from issue #149;
- atomic product/union declaration creation defined by [type declaration authoring](type-declaration-authoring.md);
- atomic effect declaration creation defined by [effect declaration authoring](effect-declaration-authoring.md);
- atomic capability/operation declaration creation defined by [capability declaration authoring](capability-declaration-authoring.md);
- exact reviewed commit/reject and readiness/diagnostics from those workflows.

Production graph-node, parameter, function, type, variant, field, effect, capability, capability-operation, capability-parameter, pattern-binding, transaction, and snapshot IDs created by these operations remain system-owned. The transport may only reference identities where the corresponding authoring contract accepts existing IDs.

Milestone 3 initially excluded declaration creation. The staged function, Product/Union, Effect, and Capability/Operation authoring contracts now all compose through the same maintained session without changing its persistence or single-writer authority.

## Execution boundary

Semantic readiness, execution/lowering readiness, and actual backend execution are distinct results.

The session may project the committed workspace with `AgentExecution.projectTypedSemanticIr`, lower it with `ExecutionIr.lower`, and pass the result to an explicitly selected existing backend/profile. Simulation and review never invoke this path. `runPure` is a separate explicit execution request: it emits the exact committed snapshot through the empty C# profile, builds a temporary executable, invokes a chosen pure function by stable `FunctionId` with typed scalar inputs, and returns the actual value and optional expected-value comparison. It never edits the snapshot or generated C# source and refuses a changed snapshot or dependency pin before running. Capability execution therefore remains controlled by the explicit backend/profile/provider setup rather than by editable C# or an implicit live service.

## JSON-lines process protocol

The maintained executable uses one JSON object per input line and emits exactly one JSON object per input line. Blank lines are ignored. The process is started with one selected snapshot path and explicit open/create mode; the path is not repeated in request messages.

Every request has:

```json
{"id":"caller-token","op":"status"}
```

Every response echoes `id` and has either:

```json
{"id":"caller-token","ok":true,"result":{}}
```

or:

```json
{"id":"caller-token","ok":false,"error":{"code":"...","message":"..."}}
```

Unknown properties that would change request meaning are rejected rather than ignored. Duplicate property names in request objects, including nested recipes and declarations, are rejected as `ProtocolError` rather than resolved by first- or last-value precedence.

### Operations

The maintained operation set is:

| `op` | Purpose |
| --- | --- |
| `status` | Current snapshot ID and fingerprints. |
| `dependency` | Read-only pinned dependency path, snapshot ID and fingerprint, or null. |
| `declarations` | Stable-ID declaration discovery. |
| `focusFunction` | Focused function/query projection by `functionId`. |
| `simulateOperations` | Simulate a list of existing `AgentOperationIntent` operations. |
| `simulateCreateFunction` | Simulate atomic function creation with an `ExpressionRecipe`. |
| `simulateCreateFunctionByName` | Resolve display names to current semantic identities, then simulate function creation. |
| `simulateCreateTypeByName` | Resolve display-name type references, then simulate product/union creation. |
| `simulateCreateType` | Simulate atomic product/union declaration creation. |
| `simulateCreateEffect` | Simulate atomic effect declaration creation. |
| `simulateCreateCapability` | Simulate atomic capability/operation declaration creation. |
| `simulateReplaceBody` | Simulate whole-body `ExpressionRecipe` replacement. |
| `simulateReplaceBodyByName` | Resolve the target function and recipe references by display name, including the current result node, then simulate whole-body replacement. |
| `simulateReplaceTarget` | Simulate targeted `ExpressionRecipe` replacement. |
| `commit` | Commit one ephemeral `proposalId` and persist it before success. |
| `reject` | Reject and forget one ephemeral `proposalId`. |
| `executionReadiness` | Project and lower the current committed snapshot without running it. |
| `emitCSharp` | Emit through the existing C# backend using an explicit existing profile. |
| `runPure` | Build and run one pure function with String/Bool/Int64 or flat List/Option arguments/results and optional expected value; report identity, actual result and match. |

`simulateOperations` also accepts `{ "kind": "addProductField", "typeId": "<existing>", "displayName": "<new>", "type": <TypeRef>, "constructions": [{ "constructNodeId": "<existing>", "valueNodeId": "<existing>" }] }`. Every affected Product construction (including nested branches) needs exactly one plan. The field ID is system-owned; plans must select existing values with the exact new field type and valid lexical scope. The review diff reports `productFieldsAdded` and `productFieldsBound`. `{ "kind": "removeProductField", "fieldId": "<existing>" }` removes a Product field and its constructor bindings only after field accesses have been repaired. Affected functions must have no declared effects; otherwise the operation refuses to risk discarding effectful evaluation. Its review diff reports `productFieldsRemoved` with affected constructor IDs. `{ "kind": "deleteFunction", "functionId": "<existing>" }` deletes only an unreferenced function with no obligations targeting its body; its removed node IDs appear in `functionsRemoved`. `{ "kind": "deleteType", "typeId": "<existing>" }` removes an unreferenced Product or Union; the diff lists its removed type, variant and field IDs in `typesRemoved`. References from functions, other types, capabilities, graph nodes, or obligations must be repaired or removed first. Dependent functions may be deleted earlier in the **same** operation batch; field edits and deletion of that type must be reviewed in separate transactions to avoid misleading net diffs. `{ "kind": "deleteCapability", "capabilityId": "<existing>" }` and `{ "kind": "deleteEffect", "effectId": "<existing>" }` remove unused declarations only after all references have been repaired or removed. `capabilitiesRemoved` lists operation and parameter identities; `effectsRemoved` lists the effect identity. A dependent function, its Capability, and the required Effect may be removed in one reviewed batch in that order. These operations use the same reviewed transaction and stale-base protection as the existing operations.

`runPure` accepts `{ "functionId": "<stable ID>", "arguments": [{ "kind": "string", "value": "..." }], "expected": { "kind": "string", "value": "..." } }` in addition to `id`/`op`. Values support `string`, `bool`, and `int64` (Int64 uses an exact decimal string in responses), plus flat Lists encoded as `{ "kind": "list", "itemKind": "int64", "items": ["1", "2"] }`. `itemKind` is required even for empty Lists and accepts `string`, `bool`, or `int64`. Items are raw scalar values; Int64 output items are decimal strings. Scalar Options use `{ "kind": "option", "itemKind": "int64", "hasValue": true, "value": "0" }` or `{ "kind": "option", "itemKind": "int64", "hasValue": false }`. `itemKind` is mandatory for None as well as Some. None forbids `value`; Some requires a non-null scalar value. Some(false), Some(0), and Some("") remain distinct from None. String validation and limits also apply to Some payloads. Nested containers, Result, Product and Union values are not yet supported. List inputs, expectations, and results are limited to 4096 elements; String List inputs/expectations have a 65536-code-unit aggregate limit. List equality is ordered, exact element equality; scalar/List and differing element types reject before execution. The generated runner converts output Lists via the existing public Fold API, without changing the backend List representation. Parameter count/types, the target's required effects, result type, and expectation type are checked before building. An expectation mismatch is a successful execution with `matches: false`, not a protocol/semantic failure. The response includes `sourceSnapshotId`, `functionId`, `actual`, `expected` (or null), and `matches` (or null). The selected function must be effect-free; the **entire** committed program must also emit under the empty C# profile, so unrelated unbound capabilities can currently block this first slice. Arguments and results must be well-formed UTF-16 Strings and bounded by the runner. Builds run in a temporary directory; builds and executions use bounded child processes (30 s/5 s), no app-specific C# harness or identity lookup by generated name is required. This is not an OS sandbox, general execution engine, external capability runner, or full T3 completion. It requires the local .NET 10 SDK; unexpected host/process failures remain diagnostics rather than program `Result` values.

`proposalId` is the proposal transaction ID rendered as a stable-ID string. It is only a handle inside the live process and is not resumable across restart.

### Create-function review projection

The Core function-creation proposal retains its full review model. The maintained JSON-lines projection is deliberately compact: a successful `simulateCreateFunction` review returns the proposal receipt/readiness and the complete proposed `createdFunction` (identity, signature, effects, and body). It does not repeat the same function body again through `materializedBody` or `proposedFocus`.

This keeps nested ordinary authoring within practical tool-response bounds without changing semantic validation, proposal identity, or commit authority. After publication, `focusFunction` remains the detailed impact/query surface.

### Replace-body review projection

The Core whole-body replacement proposal retains its full review model. The maintained JSON-lines projection is deliberately compact because `simulateReplaceBody` already requires `expectedCurrentResultNodeId`, which callers obtain from the current function focus.

A successful replacement review returns the proposal receipt/readiness, a compact structural diff (owner function, old/new result node IDs, and added/removed node IDs), and the complete `proposedFunction`. It does not repeat complete `baseFocus` and `proposedFocus` projections.

This preserves exact pre-commit review of the proposed semantic function while avoiding duplicate nested bodies. `ReplaceTarget` keeps its existing review projection until a concrete workload demonstrates similar pressure.

### Stable IDs and type references

Stable IDs are JSON strings. Existing IDs may be referenced where the semantic API already accepts them. New declaration/node identities are never supplied by the caller.

`TypeRef` uses the same shape and vocabulary as the strict snapshot codec: `primitive`, `named`, `capability`, and `builtinGeneric` with explicit generic arguments.

### Expression recipes

Expression requests carry recursively nested recipe objects with a `kind` discriminator. The discriminator follows the existing `ExpressionRecipe` case vocabulary in lower camel case, for example:

```json
{"kind":"int64WrappingAdd","left":{"kind":"parameterSlot","index":0},"right":{"kind":"int64Literal","value":1}}
```

References use stable IDs (`parameterId`, `fieldId`, `variantId`, `functionId`, `operationId`) already accepted by the corresponding `ExpressionRecipe` case. Function-creation bodies may use `parameterSlot`; replacement requests may not gain any additional slot semantics from the transport.

Calls and structured constructors use arrays of explicit target-ID/value pairs. Pattern references use `scopeDepth` plus `slot` (`field`, `value`, or `error`); a field slot additionally carries `fieldId`.

The transport parser is only a DTO-to-existing-authoring-intent adapter. Type inference, recipe bounds, materialization, ID allocation, effect checks, control-flow checks, graph construction, and declaration validation remain exclusively in the Core workflows.

## Name-resolved AI authoring

The maintained protocol also exposes a convenience layer for AI tool use when persistent stable IDs are not themselves the subject of the task. These operations resolve exact display names against the current session snapshot and immediately lower them to the existing stable-ID authoring requests:

- `simulateCreateTypeByName`
- `simulateCreateFunctionByName`
- `simulateReplaceBodyByName`

This layer is not a textual source language and does not become program authority. Resolution happens inside the current single-writer session; ambiguous or missing names are rejected. Simulation, review, validation, system-owned identity allocation, commit policy, and publication are unchanged.

The stable-ID operations remain the exact-identity surface for callers that already hold semantic identities. The name-resolved layer exists to remove incidental plumbing such as repeatedly discovering `TypeId`, `FieldId`, `VariantId`, `FunctionId`, `ParameterId`, or the current whole-body result node merely to express an otherwise unambiguous graph edit.

Name-resolved `TypeRef` uses `{"kind":"named","name":"ChangeRequest"}` and `{"kind":"capability","name":"Console"}`. Expression recipes retain the existing structural vocabulary, but semantic references use explicit owner/name selectors. For example:

```json
{
  "kind": "fieldAccess",
  "source": {"kind": "parameter", "name": "request"},
  "type": "ChangeRequest",
  "field": "environmentDependent"
}
```

A whole-body change therefore needs only the semantic target name and replacement structure:

```json
{
  "id": "replace-1",
  "op": "simulateReplaceBodyByName",
  "function": "requires_live",
  "replacement": {"kind": "boolLiteral", "value": false}
}
```

The session resolves the target `FunctionId` and current result node before constructing the ordinary reviewed replacement request.

This direction is consistent with evidence from structure-aware coding-agent interfaces such as [CODESTRUCT (ACL 2026)](https://aclanthology.org/2026.acl-long.607/), which found that operating on named AST entities instead of text spans can improve reliability and reduce token use. Nyrwvy does not treat that work as design authority or as evidence that every graph interface is superior. The relevant hypothesis is narrower: an AI-facing structured action space can remove text-position and identity-reconstruction work. Nyrwvy applies that idea to its authoritative typed semantic graph rather than translating the edit back into authoritative source text.

## Persistence format

M3 does not select a new long-term storage format. The maintained session stores the existing strict JSON snapshot codec because it is already the exact validated persistence boundary available to this milestone. Nyason evaluation remains a separate encoding concern and does not fork the session semantics.

## Non-goals

The maintained session does not add MCP/HTTP/cloud transport, multiple simultaneous transports, orchestration, a database, semantic merge/rebase, caller-owned production IDs, raw graph mutation, runtime provider registration/implementation, a new evaluator/runtime, or compatibility changes to the frozen evaluation protocols.
