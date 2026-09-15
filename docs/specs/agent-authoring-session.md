# Snapshot-backed agent authoring session

**Status:** Proposed

This specification defines the Milestone 3 maintained authoring boundary tracked by issue #153. It composes the existing semantic query, reviewed mutation, structural expression, persistence, execution-projection, and backend APIs. It does not introduce another semantic graph authority or expression engine.

## Purpose

An ordinary agent must be able to open one explicitly selected NyamNyam snapshot, discover stable declarations, propose and review semantic changes, publish accepted changes durably, close the process, and later resume from the same committed semantic identities.

The maintained path is a single-workspace, single-writer session. Frozen M1/M2 evaluation protocols remain historical verification surfaces and are not imported as the production authoring protocol.

## Session lifecycle

A session starts in exactly one of two explicit modes:

1. **Open** an existing snapshot file. The file is decoded with the existing strict `SnapshotCodec`, validated, and wrapped in `AgentWorkspace`.
2. **Create** a new snapshot file. The session creates `Snapshot.empty` with a system-owned snapshot ID, validates it through `AgentWorkspace`, serializes it with `SnapshotCodec`, and durably creates the selected file before reporting success.

Opening a missing file is an error. Creating over an existing file is an error. There is no implicit fallback between the two modes.

A live session owns one workspace path. Proposal handles are process-local and may be discarded when the process ends. A new process reopens the last published snapshot and must simulate/review any later change again.

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

1. Build an intent using the existing agent mutation or `ExpressionRecipe` authoring API.
2. Simulate against the current `AgentWorkspace` and return the existing review facts plus an ephemeral proposal handle.
3. The caller either rejects the proposal or asks to commit that exact handle.
4. Core commit/review validation produces an immutable candidate `AgentWorkspace` or rejects it as invalid/stale.
5. On a candidate commit, serialize the candidate workspace through the existing strict snapshot codec.
6. Re-check the persisted-source token, write the complete candidate to a temporary file in the same directory, and replace the authoritative file as one publication boundary.
7. Only after persistence succeeds does the session replace its in-memory workspace and report commit success.

A failed write therefore leaves both the authoritative snapshot file and the session's current workspace at the previously published state. A rejected semantic commit never reaches the persistence step.

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

- ordinary `AgentWorkflow` operations: display-name rename, add function parameter, resolve missing argument, and add/remove required effects;
- whole-body `ExpressionRecipe` replacement;
- targeted `ExpressionRecipe` replacement from issue #148;
- atomic function creation with ordered `ParameterSlot` references from issue #149;
- exact reviewed commit/reject and readiness/diagnostics from those workflows.

Production graph-node, parameter, function, pattern-binding, transaction, and snapshot IDs created by these operations remain system-owned. The transport may only reference identities that already exist.

Type/effect/capability declaration creation is deliberately absent from M3 and remains issue #154.

## Execution boundary

Semantic readiness, execution/lowering readiness, and actual backend execution are distinct results.

The session may project the committed workspace with `AgentExecution.projectTypedSemanticIr`, lower it with `ExecutionIr.lower`, and pass the result to an explicitly selected existing backend/profile. Simulation and review never invoke this path. Capability execution therefore remains controlled by the explicit backend/profile/provider setup rather than by editable C# or an implicit live service.

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

Unknown properties that would change request meaning are rejected rather than ignored.

### Operations

The initial maintained operation set is:

| `op` | Purpose |
| --- | --- |
| `status` | Current snapshot ID and fingerprints. |
| `declarations` | Stable-ID declaration discovery. |
| `focusFunction` | Focused function/query projection by `functionId`. |
| `simulateOperations` | Simulate a list of existing `AgentOperationIntent` operations. |
| `simulateCreateFunction` | Simulate atomic function creation with an `ExpressionRecipe`. |
| `simulateReplaceBody` | Simulate whole-body `ExpressionRecipe` replacement. |
| `simulateReplaceTarget` | Simulate targeted `ExpressionRecipe` replacement. |
| `commit` | Commit one ephemeral `proposalId` and persist it before success. |
| `reject` | Reject and forget one ephemeral `proposalId`. |
| `executionReadiness` | Project and lower the current committed snapshot without running it. |
| `emitCSharp` | Emit through the existing C# backend using an explicit existing profile. |

`proposalId` is the proposal transaction ID rendered as a stable-ID string. It is only a handle inside the live process and is not resumable across restart.

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

The transport parser is only a DTO-to-`ExpressionRecipe` adapter. Type inference, recipe bounds, materialization, ID allocation, effect checks, control-flow checks, and graph construction remain exclusively in `AgentExpressionStructuralWorkflow`.

## Persistence format

M3 does not select a new long-term storage format. The maintained session stores the existing strict JSON snapshot codec because it is already the exact validated persistence boundary available to this milestone. Nyason evaluation remains a separate encoding concern and does not fork the session semantics.

## Non-goals

M3 does not add MCP/HTTP/cloud transport, multiple simultaneous transports, orchestration, a database, semantic merge/rebase, caller-owned production IDs, raw graph mutation, declaration authoring from issue #154, a new evaluator/runtime, or compatibility changes to the frozen evaluation protocols.
