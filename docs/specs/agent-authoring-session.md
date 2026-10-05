# Snapshot-backed agent authoring session

**Status:** Current

This specification defines the Milestone 3 maintained authoring boundary tracked by issue #153. It composes the existing semantic query, reviewed mutation, structural expression, persistence, execution-projection, and backend APIs. Later declaration-authoring slices join this same session boundary without creating another workspace authority.

## Purpose

An ordinary agent must be able to open one explicitly selected Nyrwvy snapshot, discover stable declarations, propose and review semantic changes, persist accepted changes across authoring-process restarts, close the process, and later resume from the same committed semantic identities.

The maintained path is a single-workspace, single-writer session. Frozen M1/M2 evaluation protocols remain historical verification surfaces and are not imported as the production authoring protocol.

## Pinned snapshot dependencies

`create --snapshot <local> --dependency <existing-source-snapshot>` selects one independent, strict, complete source snapshot and retains its canonical JSON in `<local>.dependency.json`. `open --snapshot <local>` validates and uses that retained content, never the latest version at the original source path. Upstream commits or removal no longer invalidate the selected version. Clone/move the local snapshot **and its sidecar together**; no original-machine absolute path is needed to reopen. Sources with their own dependencies are flattened (see below).

The embedded manifest has exactly `path`, `snapshotId`, `fingerprint`, and `snapshotJson` (a canonical strict snapshot JSON **string**, avoiding an extra graph nesting level). `path` is original-source provenance only. The local snapshot's dependency ID and canonical-source digest still bind the exact retained content: removing/replacing/corrupting the manifest cannot silently change the pin or turn the graph into a standalone one. Publication rechecks the exact manifest bytes against the opened selection. Creation writes the sidecar and local snapshot separately atomically; a failed create may leave an orphan sidecar that must be inspected/removed before retrying. No cross-file crash atomicity or power-loss durability is promised. The graph codec and fingerprint domains are unchanged; old clients that only accept three-field manifests cannot read the embedded format.

**Several dependencies.** `create` accepts `--dependency` more than once (the MCP `open` tool takes `dependencies: [...]`). Each source is pinned exactly as above, and the workspace resolves against their combination under the import collision policy (Q-002). Imported declarations keep their typed identities. A declaration reached through more than one dependency, such as a type both libraries copied from a shared library, is imported once only when its content is identical. Any other identity that appears in two dependencies with different content is rejected with its kind and ID, and a collision with a local declaration fails snapshot validation, so distinct objects are never merged. Selecting the same source twice is rejected. The sidecar is then `{ "dependencies": [ <embedded member>, ... ] }`, with at least two members, each in the embedded form above; a single dependency keeps the one-object form. The combined imported graph has a deterministic SnapshotId derived from its members' fingerprints, independent of order, and the local snapshot binds to its canonical digest. Tampering with any member is detected on open. `pin` with a source applies only to a single legacy dependency. The `dependency` request reports `{ "dependencies": [ ... ] }` for a set.

**Multi-level dependencies.** A selected source may itself be a workspace with pinned dependencies. It is flattened into its complete graph: its local declarations are resolved against its own retained sidecar content, checked for completeness, and pinned as one exact snapshot. The app therefore needs neither the intermediate's sidecar nor the upstream sources afterwards. Because flattening keeps imported identities, a library reached through several paths (a diamond) is deduplicated by the same identical-content rule, and differing content under one identity is rejected.

**Explicit upgrade.** Reproducing the pinned version (`open`, `pin`) and moving to a new version are separate operations. Inside a session, `{ "op": "previewDependencyUpgrade", "source": "<new version>", "replaces": "<old SnapshotId>" }` pins the new source as a candidate, flattening it if needed; `replaces` is required only when several dependencies are selected. It reports `replacedSnapshotId`/`replacedFingerprint`, `newSnapshotId`/`newFingerprint`, and the `added`, `removed` and `changed` imported declarations as `{ kind, id }`. It also reports `issues` from validating the unchanged local graph against the upgraded imports, and `compatible` when there are none. Nothing is written. Applying is a startup mode: `upgrade --snapshot <local> --dependency <new version> --expect <newFingerprint> [--replaces <old SnapshotId>]` (MCP `open` mode `upgrade` with `dependency`, `expectedFingerprint`, `replaces`). It recomputes the plan under the workspace lock, refuses a source whose fingerprint differs from the reviewed one or a candidate with issues (`WorkspaceValidationFailure`, leaving both files byte-identical), and refuses the already-selected version. Otherwise it rewrites the sidecar and re-binds the local snapshot to the new dependency digest, keeping local declarations and identities, and opens the upgraded session. The sidecar is replaced first; if replacing the local snapshot then fails (for example disk full or permission denied), the previous sidecar content is restored and the error is reported, so the unchanged workspace still opens and the upgrade can be retried. As with creation, this is not cross-file crash atomicity: a process crash between the two replacements can still leave them mismatched. An upgrade that breaks local references is refused rather than applied partially; local references must first be made compatible with the new version.

**Legacy migration and same-content recovery:** old manifests containing only `path`, `snapshotId`, and `fingerprint` retain their original fail-closed behavior on ordinary open/commit. `pin --snapshot <local>` explicitly retains the exact selected source in the sidecar and starts a session. If the old path is unavailable or now points at another version, use `pin --snapshot <local> --dependency <exact-original-copy>`. Pin validates source completeness, selected ID/fingerprint, the local digest and the combined graph before replacing only the manifest. Local graph bytes, identities and fingerprints do not change. It honors the single-writer lock and rechecks both files before replacement; replacement failure retains the old selection. Pin is idempotent on an already embedded selection, and cannot attach a dependency to a standalone workspace.

This implements the **retained immutable content** strategy from [issue #209](https://github.com/kw4r3n/nyrwvy/issues/209); version changes go only through the explicit upgrade above. Pin cannot accept changed content, even under the same SnapshotId. No semantic upgrade is implied or authorized by a provenance-path change. A legacy pin whose original contents have been lost cannot be reconstructed from a digest; an exact old copy is necessary. A future semantic upgrade still needs a separately reviewed old/new dependency and local-reference migration contract.

The shared MCP/OpenCode `nyrwvy_open` supports `mode: "create"`, `"open"`, or `"pin"`. Its optional `dependency` argument is accepted only for create or same-content pin; relative paths resolve against the selected workspace. Open never silently migrates storage or accepts another selection. A structurally valid dependency with any open obligation is rejected at create, pin and reopen before a commit-blocked workspace is admitted. Standalone incomplete authoring and ordinary commit policy are unchanged. Malformed manifests and access failures return controlled errors and release the lock.

The read/review/workspace and C# emission projection use the validated union of imported and locally authored declarations. All imported declarations (including their body and referenced type closure) keep their exact stable identities and are inspectable via `declarations`/`focusFunction`. Local graph snapshots persist **only** local declarations and references to imported IDs; the sidecar separately retains the selected imported snapshot as immutable dependency content. Validation rejects duplicate identities, missing references and invalid combined graphs. Simulation rejects edits to imported declarations as `ImportedDeclarationModified` before returning a proposal handle or review. This checks the workflows' resulting affected owners, including caller and match repairs, not just the primary requested target. Publication retains the independent check that no imported declaration changed or disappeared. The selected provenance is inspectable via the read-only `dependency` operation. The dependency version is selected at workspace creation, not added by a mutation proposal; same-content pin only changes storage/provenance, and all planner declarations and edits still use ordinary simulation, review and explicit commit. This is deliberately a single, full-snapshot dependency, not a package resolver, general namespace mechanism or merge system. Standalone strict snapshot decoding continues to reject dependency-bearing local files without resolution.

## Session lifecycle

A session starts in exactly one of three explicit modes:

1. **Open** an existing snapshot file. The file is decoded with the existing strict `SnapshotCodec`, validated, and wrapped in `AgentWorkspace`.
2. **Create** a new snapshot file. The session creates `Snapshot.empty` with a system-owned snapshot ID, validates it through `AgentWorkspace`, serializes it with `SnapshotCodec`, and creates the selected persistent file before reporting success.

3. **Pin** an existing dependency selection using the same-content migration/recovery rules above, then open that existing graph. This is storage maintenance, not a semantic mutation or version upgrade.

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

The session may project the committed workspace with `AgentExecution.projectTypedSemanticIr`, lower it with `ExecutionIr.lower`, and pass the result to an explicitly selected existing backend/profile. Simulation and review never invoke this path. `runPure` is a separate explicit execution request: it emits the exact committed snapshot with every capability operation as an external implementation, builds a temporary executable, invokes a chosen function by stable `FunctionId` with typed inputs, and returns the actual value and optional expected-value comparison. Capability parameters are bound only to scripted fakes supplied in the request; the runner never connects a host service. It never edits the snapshot or generated C# source and refuses a changed snapshot or dependency pin before running. Live capability execution therefore remains controlled by the explicit backend/profile/provider setup rather than by editable C# or an implicit live service.

## JSON-lines process protocol

The maintained executable uses one JSON object per input line and emits exactly one JSON object per input line. A blank or whitespace-only line is malformed input and gets a `ProtocolError` reply with a null `id`, like any other line that is not a request object. The process is started with one selected snapshot path and explicit open/create mode; the path is not repeated in request messages.

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

Besides the operation-specific codes below and the session error codes (`SnapshotFileNotFound`, `IoFailure`, `SnapshotCodecFailure`, `WorkspaceValidationFailure`, `ProposalNotFound`, `CommitRejected`, …), the transport uses: `ProtocolError` for malformed or unresolvable input, `UnsupportedOperation` for an unknown `op`, `SessionError` for an operation the session state does not allow, and `InternalError` for any other unexpected failure. `InternalError` answers only that request: its `message` is a fixed text that names no exception (the exception type and message go to the process's stderr only), the session keeps running, and its state is unchanged (the in-memory workspace and pending proposals are updated only after a successful write). A graph that would nest deeper than the snapshot codec's 1000 JSON levels cannot be persisted or fingerprinted; the simulation or commit reports the snapshot codec error `MaximumDepthExceeded 1000` (for example as `SnapshotCodecFailure`) instead of failing the process.

Unknown properties that would change request meaning are rejected rather than ignored. Duplicate property names in request objects, including nested recipes and declarations, are rejected as `ProtocolError` rather than resolved by first- or last-value precedence.

A request line may nest up to **835** JSON levels. The bound is derived from the recipe node limit ([decision](../decisions/expression-recipe-bounds.md#consequences)): 3 envelope levels above a recipe root (`functionRewrites[i].replacement` is the deepest), 3 levels per recipe node (the node object, a child list such as `arguments` or `arms`, and the entry holding the child) for all 256 nodes the limit allows, and 64 levels at the leaf for an explicit TypeRef. Any recipe within the node limit therefore parses and is judged by the recipe bounds, so a recipe deeper than 32 nodes gets `StructuralRecipeDepthLimitExceeded` (which states its actual depth) rather than a JSON error. Only a line nested deeper than 835 levels is a `ProtocolError` (`Invalid JSON: The maximum configured depth of 835 has been exceeded…`). Responses are encoded with a 2000-level allowance, twice the snapshot codec's 1000, so any graph the session can persist can also be read back (a nested scope costs a response at most 4/3 of its snapshot levels). Responses for deep graphs can nest well beyond 64 JSON levels; clients should parse without a small depth limit (Node's `JSON.parse` has none).

### Operations

The maintained operation set is:

| `op` | Purpose |
| --- | --- |
| `status` | Current snapshot ID and fingerprints. |
| `dependency` | Read-only provenance path, snapshot ID, fingerprint and `storage` (`embedded` or `legacySource`), or null. Does not return the retained graph payload. |
| `declarations` | Stable-ID declaration discovery. |
| `focusFunction` | Focused function/query projection by `functionId`. |
| `viewFunction` | Write the committed function's human-readable Function Focus HTML and return its path; exactly one of `function` and `functionId`, optional `.html` `output` ([contract](agent-graph-tools.md#human-readable-view)). |
| `simulateOperations` | Simulate a list of existing `AgentOperationIntent` operations. |
| `simulateOperationsByName` | Resolve display names in a list of deletion operations, then simulate them as `simulateOperations` does. |
| `simulateCreateFunction` | Simulate atomic function creation with an `ExpressionRecipe`. |
| `simulateCreateFunctionByName` | Resolve display names to current semantic identities, then simulate function creation. |
| `simulateCreateTypeByName` | Resolve display-name type references, then simulate product/union creation. A type that names itself is created as a group of one. |
| `simulateCreateTypesByName` | Simulate creating a group of product/union types that may name themselves and each other. |
| `simulateCreateType` | Simulate atomic product/union declaration creation. |
| `simulateCreateEffect` | Simulate atomic effect declaration creation. |
| `simulateCreateCapability` | Simulate atomic capability/operation declaration creation. |
| `simulateReplaceBody` | Simulate whole-body `ExpressionRecipe` replacement. |
| `simulateReplaceBodyByName` | Resolve the target function and recipe references by display name, including the current result node, then simulate whole-body replacement. |
| `simulateReplaceTarget` | Simulate targeted `ExpressionRecipe` replacement. |
| `simulateAddUnionVariantLike` | Add a Union variant that copies a source variant's fields (fresh IDs) plus optional `additionalFields`, cloning every match arm for the source. |
| `simulateProductFieldTypeMigration` | Change one Product field's type with a replacement body for every function that constructs the Product or reads the field, reviewed and published together. |
| `simulateResultTypeMigration` | Change one function's result type with a complete new body and one replacement body per direct caller, reviewed and published together. |
| `commit` | Commit one ephemeral `proposalId` and persist it before success. |
| `reject` | Reject and forget one ephemeral `proposalId`. |
| `executionReadiness` | Project and lower the current committed snapshot without running it. |
| `emitCSharp` | Emit through the existing C# backend using an explicit existing profile. |
| `runPure` / `runPureByName` | Build and run one function with Unit/String/Bool/Int64/Float64/Bits32, List/Option, Product/Union or Result arguments/results (bounded nesting through container items, fields and Result payloads, including recursive named types), scripted fakes for capability parameters, and optional expected value; report identity, actual result, match, mismatch path and fake calls. |

`simulateOperations` also accepts `{ "kind": "addProductField", "typeId": "<existing>", "displayName": "<new>", "type": <TypeRef>, "constructions": [{ "constructNodeId": "<existing>", "valueNodeId": "<existing>" }] }`. Every affected Product construction (including nested branches) needs exactly one plan. The field ID is system-owned; plans must select existing values with the exact new field type and valid lexical scope. The review diff reports `productFieldsAdded` and `productFieldsBound`. `{ "kind": "removeProductField", "fieldId": "<existing>" }` removes a Product field and its constructor bindings only after field accesses have been repaired. Affected functions must have no declared effects; otherwise the operation refuses to risk discarding effectful evaluation. Its review diff reports `productFieldsRemoved` with affected constructor IDs. `{ "kind": "deleteFunction", "functionId": "<existing>" }` deletes only a function that no other function references and that has no obligations targeting its body; its removed node IDs appear in `functionsRemoved`. A function's references to itself (a `SelfCall`, or itself as a callback inside its own body) disappear with its body and never block the deletion, so a self-recursive function is deleted in one operation. A rejection (`FunctionStillReferenced`) names the function and every referring function by display name and ID, for example `Function 'leaf' (fn-1) is still referenced by 'caller' (fn-2), 'other' (fn-3).` Two functions that reference each other still need one reference repaired first, since whichever is deleted first is referenced by the other. `{ "kind": "deleteType", "typeId": "<existing>" }` removes an unreferenced Product or Union; the diff lists its removed type, variant and field IDs in `typesRemoved`. References from functions, other types, capabilities, graph nodes, or obligations must be repaired or removed first. Dependent functions may be deleted earlier in the **same** operation batch; field edits and deletion of that type must be reviewed in separate transactions to avoid misleading net diffs. `{ "kind": "deleteCapability", "capabilityId": "<existing>" }` and `{ "kind": "deleteEffect", "effectId": "<existing>" }` remove unused declarations only after all references have been repaired or removed. `capabilitiesRemoved` lists operation and parameter identities; `effectsRemoved` lists the effect identity. A dependent function, its Capability, and the required Effect may be removed in one reviewed batch in that order. These operations use the same reviewed transaction and stale-base protection as the existing operations.

`{ "kind": "deleteCapabilityOperation", "operationId": "<existing>" }` removes one unused operation without deleting its owning Capability or siblings. Invocation references (including nested graphs) must be repaired or removed first. The review's `capabilityOperationsRemoved` entries contain `ownerCapabilityId`, `operationId`, `displayName`, and `removedParameterIds`; HTML review also shows the removed identities. An empty Capability is retained, and its values and type references remain valid. A caller may be deleted earlier in the same batch, and an Effect that is no longer referenced may be deleted afterward. Unknown targets fail with `TargetNotFound`, surviving invocations with `CapabilityOperationStillReferenced`; failed batches publish no changes. Imported operations are read-only, just like their owning Capability.

`simulateAddUnionVariantLike` takes `{ "typeId", "sourceVariantId", "displayName", "additionalFields": [{ "displayName", "type": <TypeRef> }] }` (`additionalFields` optional). The new variant receives copies of the source variant's fields with fresh FieldIds and the same names and types, followed by the additional fields. Every `MatchUnion` arm for the source variant, in every function and nested body, is cloned for the new variant; an arm that names the source variant together with others instead gains the new variant and keeps its one body. The clone gets fresh node and pattern-binding IDs, copied field bindings are remapped to the new fields, and additional fields are bound but unused. The program therefore stays exhaustive and valid in one publication. Arm bodies can then be refined with targeted replacement, now that the new variant and field IDs exist; requests never guess future IDs. Blank or duplicate field names (including copied ones) reject with `UnionVariantEvolutionFieldDisplayNameInvalid`, and an existing variant name with `UnionVariantEvolutionDuplicateVariantDisplayName`. The earlier `simulateAddFieldlessUnionVariantLike` keeps its fieldless-only contract. The review is the same `fieldlessUnionVariantEvolution` shape, including the added variant with its fields.

`simulateProductFieldTypeMigration` takes `{ "fieldId", "newFieldType": <TypeRef>, "functionRewrites": [{ "functionId", "replacement": <recipe> }] }`. The affected set is every function that constructs the owning Product or reads the field (`FieldAccess`), at any nesting depth, and it must be covered exactly (`FieldTypeMigrationRewriteMismatch`, `FieldTypeMigrationDuplicateRewrite`). Each replacement is bound and type-checked against the retyped declaration, and the whole candidate is validated once, so neither a stale construction or read nor a type-invalid intermediate is ever published. The TypeId, FieldId, field order, and function and parameter identities are kept; functions that neither construct nor read the field are untouched. Union variant fields are not targets of this operation (`FieldTypeMigrationFieldNotFound`). The review kind is `productFieldTypeMigration`, with `fieldChange` (`typeId`, `fieldId`, `oldType`, `newType`) and `bodyRewrites`.

`simulateResultTypeMigration` takes `{ "targetFunctionId", "newResultType": <TypeRef>, "targetReplacement": <recipe>, "callerRewrites": [{ "callerFunctionId", "replacement": <recipe> }] }`. It is the result-type counterpart of `simulateParameterTypeMigration` and shares its reviewed, all-or-nothing contract. The target's FunctionId, parameters and ParameterIds are kept; the target body is replaced by a recipe of the new type; and the complete set of direct callers must each receive exactly one replacement body (`ResultTypeMigrationCallerRewriteMismatch` names missing and unrelated callers). Recipes reference parameters by stable ID and may call the target by its existing FunctionId. The target replacement may call the target itself, by its FunctionId or by `selfCall`; the call is typed against the new signature (a self call binding a parameter the target does not own is a `TargetRewriteInvalid` rejection naming `TargetSelfCallArgumentNotFound`) and persists as an ordinary DirectCall to the target. A target that already calls itself is therefore migrated like any other: because its body is replaced as a whole, its own call sites are neither callers to rewrite nor listed in `affectedCallSites`. Generic targets and callers keep their own type parameter identities in the migrated signatures and bodies. Every recipe kind is accepted in both migrations, including Option/Result construction and matching, Bind and List operations. Only one final candidate snapshot is validated and published. An unchanged type, stale base, invalid target or caller body, callback references to the target (`ResultTypeMigrationUnsupportedCallerUse`), and obligation-referenced bodies are rejected before publication. The review kind is `resultTypeMigration`, with `resultChange` (`functionId`, `oldType`, `newType`), `affectedCallSites` and `bodyRewrites`.

`runPure` also accepts Products as `{ "kind": "product", "typeId": "<stable TypeId>", "fields": [{ "fieldId": "<stable FieldId>", "value": { "kind": "int64", "value": "42" } }] }` in arguments and expectations, and emits the same shape in results, with display names added beside the IDs (see *Display names in run output*). Type and field identities, not display names, determine the shape: each declared field must occur exactly once with a value of its exact declared type. A field value uses the same tagged run-value format as a top-level value of that type (for example a flat `list` with `itemKind` for `List<String>`, a nested `list` with `itemType` for `List<Item>`, or another `product`/`union`/`result`), and is validated recursively. Missing, duplicate, foreign or mistyped fields reject with `RunProductShapeMismatch` (also for an inner Product); a representation error inside a field, such as a nested-form container with scalar items, rejects with `RunContainerTypeMismatch`; a different type identity rejects as an argument/expectation type mismatch. The limit is 256 fields and, for each input/expectation Product, 65536 aggregate String UTF-16 code units. Empty Products are valid. Field input order is irrelevant; responses (including `expected`) sort fields by stable ID, and comparisons use this canonical order. Int64 field values use decimal strings in responses. The runner uses generated constructor/property symbols derived from validated IDs, never caller-supplied C# expressions.

Unions use `{ "kind": "union", "typeId": "<stable TypeId>", "variantId": "<stable VariantId>", "fields": [{ "fieldId": "<stable FieldId>", "value": { "kind": "string", "value": "payload" } }] }`. A fieldless variant uses `fields: []`; `typeId`, `variantId`, and `fields` are always required. The variant must belong to that Union, and fields must exactly cover the selected variant with values of their declared types, in the same recursive format as Product fields; foreign/unknown variants and field-shape errors reject with `RunUnionShapeMismatch`. As with Products, field order is irrelevant and responses sort fields by stable ID. Equality compares Union identity, variant identity, and fields, so distinct fieldless variants never match. Input/expectation String payloads share the 65536 aggregate UTF-16 limit. This slice accepts Unions with 1–256 variants, each with at most 256 fields of supported types. **All** variants must satisfy that restriction, even if the supplied or expected variant has no fields. Runtime null/unknown variants are process diagnostics, never silently converted into a valid variant.

List/Option may contain supported non-scalar values, including other Lists/Options, Results, Products and Unions. Use `itemType: <TypeRef>` instead of `itemKind` for these containers, and **fully tagged** child values: `{ "kind": "list", "itemType": { "kind": "builtinGeneric", "generic": "option", "arguments": [{ "kind": "primitive", "primitive": "int64" }] }, "items": [{ "kind": "option", "itemKind": "int64", "hasValue": false }] }`. A nested Option uses `{ "kind": "option", "itemType": <TypeRef>, "hasValue": true, "value": <tagged value> }`, or omits `value` when false. `itemType` is mandatory even for empty Lists/None. Scalar elements still use the original `itemKind`/raw-scalar format; specifying both metadata fields is invalid. Every child must match the declared item type, and named field identities are checked recursively. An invalid nested item type yields `RunContainerTypeMismatch`. Responses preserve this distinction between flat and nested formats.

Results with at least one non-scalar side use a nested form with full type references instead of `okKind`/`errorKind`: `{ "kind": "result", "okType": <TypeRef>, "errorType": <TypeRef>, "isOk": true, "value": <tagged value of the active side> }`. Results whose sides are both String/Bool/Int64 must keep the flat scalar form; the nested form for them, or a payload that does not match the active side, rejects with `RunContainerTypeMismatch`. Mixing `okType` with `errorKind` (or the reverse) is a protocol error. Responses use the same flat/nested distinction.

Float64 uses `{ "kind": "float64", "value": <number or string> }`. A finite value other than -0.0 is a JSON number; responses write the shortest decimal that reads back to the same binary64 (for example `0.1`, `3.5000000000000004`, `1E+308`, `5E-324`), and requests may write any JSON number, read as the nearest binary64 (`1e-400` reads as 0). A number beyond the finite range, such as `1e400`, is a protocol error. NaN, +Infinity, -Infinity and -0.0 are the strings `"NaN"`, `"Infinity"`, `"-Infinity"` and `"-0"`; any other string is a protocol error, and a request may also write -0.0 as the number `-0`. Float64 has no flat container form: `itemKind`, `okKind` and `errorKind` do not accept it, so Lists and Options of Float64 and Results with a Float64 side use the nested form (`itemType`/`okType`/`errorType`), as Unit does. Expectations, fake `cases` keys and mismatch reports compare Float64 bitwise after canonicalizing every NaN to one value: `"NaN"` matches any NaN, `"-0"` does not match `0`, and there is no tolerance. The generated runner reports Float64 results and call arguments by their exact bits, so nothing is rounded in transit. See the [decision](../decisions/float64-conversion-and-run-values.md).

Bits32 uses `{ "kind": "bits32", "value": <integer> }`, carried like Int64 as its unsigned value `0..4294967295`: requests may write a JSON integer or an exact decimal string of ASCII digits, and responses always write a decimal string (`"4294967295"`). A negative value, a value above `4294967295`, a sign, a fraction, hexadecimal text or any other string is a protocol error; values are never wrapped on input (use `Bits32FromInt64` in the program for that). Bits32 has no flat container form either, so Lists, Options and Results of Bits32 use the nested form. Expectations, fake `cases` keys and mismatch reports compare the 32 bits exactly. See the [decision](../decisions/bits32-domain.md).


Each argument, expectation, and result is limited to 32 composite layers along any path (List, Option, Result, Product and Union each count; scalar leaves do not), 8192 total value nodes (including scalar fields and container payloads), 4096 elements per List, 256 fields per Product or Union value, and 65536 aggregate String UTF-16 code units. A fake script has at most 256 operations, each with at most 256 responses and 256 argument-keyed cases of at most 256 arguments. The build is limited to 30 s and the run to 5 s (see below). The aggregate String and layer **output** limits are enforced by the generated runner as well. Output budget violations are process diagnostics; input budget violations reject before building (a wire value deeper than 32 layers is a protocol error). These are value-conversion bounds, not a sandbox or a bound on the function's internal allocations. A type is supported only if every **acyclic** path through containers, Result arguments and declared fields stays within eight composite layers and its expansion, with each recursive reference counted once, has at most 131072 type nodes; otherwise the request rejects with `RunResultTypeUnsupported` (result) or `RunContainerTypeMismatch` (item/payload) before building. Only values of recursive named types, such as `Node { next: Option<Node> }` or a `Tree` Union whose variant holds `List<Tree>`, can exceed eight layers, so the 32-layer value bound applies to them. The runner encodes each named result type with one generated function, so recursion does not expand the generated code. Responses for deep recursive values can nest more than 64 JSON levels; clients should parse with a matching depth allowance.

When an expectation does not match, the response's `mismatch` locates the first difference by semantic identity: `{ "path": [...], "expected": <tagged value>, "actual": <tagged value> }`. Path steps are `{ "kind": "field", "typeId", "fieldId" }` (plus `type` and `field` display names), `{ "kind": "item", "index" }` (zero-based List position), `{ "kind": "some" }`, `{ "kind": "ok" }` and `{ "kind": "err" }`; fields are visited in canonical stable-ID order. The path stops where the values stop sharing a shape: different Union variants, Result tags, Option presence, List lengths after an equal common prefix, or scalar values. `mismatch` is null when there is no expectation or it matches.

A failure raised by the program while it runs, rather than by the runner's value conversion, is `RunRuntimeFailure` with `exceptionType`, `message`, the entry `functionId`, `sourceSnapshotId`, and `frames`, innermost first: `{ "kind": "function", "functionId" }` or `{ "kind": "capabilityOperation", "operationId" }`. Frames are mapped from generated symbols through the emitted symbol manifest; frames without a semantic declaration (runtime helpers, the runner itself) are omitted, and at most 256 are kept. Unbounded `DirectCall` recursion is valid in the language. A self call in tail position runs as a loop ([expression authoring](expression-authoring.md#selfcall)), so an endless one ends as `RunTimeout`; other recursion is reported as `System.StackOverflowException`, with frames recovered from the runtime's crash report and a message saying the recursion was too deep, that a self call in tail position runs as a loop, and pointing to `ListFold` (over `StringCodePoints` for text); a small function reaches only several thousand nested calls. On Linux the runner process opts out of core dumps (`PR_SET_DUMPABLE` 0) before running the function, so a crash handler such as systemd-coredump does not hold the aborted child past the run's time limit. Output conversion limits (layers, value nodes, String budget, null Strings, unknown variants) remain `RunProcessFailure`, and timeouts remain `RunTimeout`, whose message names the timed-out step (the build or the pure function) and its limit.

A capability parameter takes a fake instead of a value: `{ "kind": "fakeCapability", "capabilityId": "<stable CapabilityId>", "operations": [{ "operationId": "<stable CapabilityOperationId>", "responses": [{ "kind": "return", "value": <tagged value> }, { "kind": "throw", "message": "..." }] }] }`. In this ordered form, each call to an operation consumes its next response in order (or use [argument-keyed cases](#argument-keyed-fake-responses)): `return` yields a value of the operation's declared result type, and `throw` raises a host exception from the operation, as a failing external implementation would. Operations that are not listed have no responses. `{ "kind": "awaitCancellation", "value": <tagged value> }` blocks like a long-running external process until the calling `ParallelAll` worker's or task's cancellation is requested, then returns the value. `return` and `throw` responses may add `"delayMillis": "<non-negative Int64>"`: the call then answers after that much virtual time. A delay is not interrupted by cancellation; use `awaitCancellation` for work that stops when asked. Both are accepted only for programs that use `ParallelAll`, a task construct or `CancellationRequested` (`RunBindingShapeMismatch` otherwise).

Such programs run on a **virtual clock**, so a run is deterministic. One thread runs at a time: a started task or `ParallelAll` worker waits for its turn, and the running thread gives way only when it blocks (on a delay, `awaitCancellation`, `TaskWait`, a `ParallelAll` join or a scope's exit) or finishes. The next thread to resume is, in order: an elapsed delay, by (due time, start order), so everything due at a time has happened before a wait observes it; then a thread that can continue (a new task, or a wait whose condition holds or whose limit has elapsed), lowest start order first, with the entry function first and tasks and workers numbered as they start. Only when nothing can continue does virtual time jump to the earliest pending due time or `TaskWait` limit. Due times saturate at `Int64.MaxValue`: a delay or limit that would overflow virtual time never elapses on its own, and virtual time never wraps negative. Real elapsed time never affects the result, and fake responses are consumed in that deterministic order. If every thread is blocked and nothing is due, the run fails with `RunRuntimeFailure` (`InvalidOperationException`, "Every task is blocked and no delay is pending…") instead of hanging. Fakes are thread-safe: responses are consumed atomically in call order within the selected queue, and `calls` are recorded in the order they happen across workers and tasks. A call with no response left fails with `RunBindingExhausted`; a `throw` fails with `RunRuntimeFailure` and `exceptionType` `FakeCapabilityException`. Both carry frames (starting with the `capabilityOperation` frame) and the calls made so far. Fakes are accepted only as top-level arguments whose parameter has exactly that capability type. The capability must exist, operations must be distinct members of it (at most 256, each with at most 256 responses), and return values must match the declared result type (`RunBindingShapeMismatch`). A fake inside another value or response is `RunBindingPlacement`. Because the program may call any operation, **every** operation of a faked capability needs parameter and result types that run values support; otherwise the request fails with `RunBindingTypeUnsupported`. Unit values use `{ "kind": "unit" }`; containers of Unit use the nested form. Operations with a declared `failureType` remain unsupported by the C# backend and fail as a session backend error; express expected failures as `Result` return values. Response values and throw messages count toward the argument's value, String and UTF-16 limits.

A capability parameter may instead be bound to an explicitly registered host implementation: `{ "kind": "hostCapability", "capabilityId": "<stable CapabilityId>", "implementation": "Example.Hosts.Directory" }`, with the request's `hostSources: ["Hosts.cs"]` listing the C# files that define it. Host sources are **disabled by default**: unless the session process was started with `--allow-host-sources` (a startup flag of `open`, `create`, `pin` and `upgrade`, never a request field), any request with `hostSources` or a `hostCapability` argument fails with `RunHostSourcesDisabled` before a source is read or anything is built. Relative paths resolve against the directory that contains the session's snapshot file (`--snapshot`), not the process working directory or an adapter's workspace; at most 16 existing `.cs` files of at most 1 MiB each are accepted, and `hostSources` must be present exactly when some argument is a `hostCapability` (otherwise `RunHostSourceInvalid`). `implementation` must be a dotted C# type name (`RunBindingShapeMismatch` otherwise) with a public parameterless constructor implementing the generated capability interface. Host authors write against the generated names reported by `emitCSharp`'s symbol manifest; a `String` parameter or result is the generated `NyStr`, which converts implicitly to and from `string`. The sources are compiled beside the generated program with warnings as errors, so compile errors are `RunProcessFailure`. The runner wraps the implementation in a generated decorator that records every call exactly as for fakes, so `calls` and runtime frames (host method and decorator as `capabilityOperation` frames, then callers) are reported for host bindings too. Host exceptions are `RunRuntimeFailure`; expected failures belong in the operation's `Result` type. Host code runs with the user's permissions in the bounded child process; enabling host sources and registering them are explicit trust decisions, not a sandbox. Scripted fakes need no opt-in.

For `ParallelAll`, host bindings must add `"supportsConcurrentCalls": true` (omitted/false means sequential only). This asserts support across all operations and aliases of the capability; every supplied host instance of that capability must agree. Scripted fakes already satisfy the shared-state contract. Unasserted worker bindings fail with `ConcurrentCapabilityBindingRequired`, including semantic IDs in `details`, before building or invoking host code. The [sharing contract](parallel-capability-sharing.md) specifies ordering and visibility. Fake response consumption and logging are atomic together; waits/returns/throws run outside that critical section, so cancellation remains possible.

Successful responses include `calls`, in recorded invocation order (not worker completion or source-index order): `{ "parameterId", "operationId", "arguments": [{ "capabilityParameterId", "value" }] }`, with arguments sorted by stable ID (display names are added beside these IDs; see below). A run may make at most 1024 calls, and calls are encoded with their own 8192-node and String budgets; exceeding them is a `RunProcessFailure`. Functions that declare effects but receive no capability are run like any other function. Declared effects need no binding, because only a capability value can perform one.

**String run values.** A String run value is any sequence of UTF-16 code units, like a String in the language: a lone high or low surrogate, or a low surrogate before a high one, is a valid value, not an error. On the wire it uses JSON's own escapes in both directions. A request spells a lone surrogate as `\uD800` (any `\uD800`–`\uDFFF` escape that does not form a pair), which standard JSON parsers such as JavaScript's `JSON.parse` read as that code unit. A response writes every lone surrogate the same way, with uppercase hex digits. JavaScript's `JSON.stringify` also escapes lone surrogates (in lowercase), and a request may use either case. This holds for top-level and nested Strings (List, Option and Result payloads, Product and Union fields), expectations, fake case arguments, fake return values, throw messages, recorded `calls`, `mismatch` values and the `message` of a runtime failure. Parsers that insist on well-formed UTF-16, such as .NET's `JsonElement.GetString`, refuse such a string, so a client that needs lone surrogates must read it with a parser that keeps them. Well-formed Strings are encoded exactly as before. Equality is ordinal over code units, so `\uD800` and `\uDC00` are different values and a lone half never matches a pair. The UTF-16 length limits count code units.

`runPure` accepts `{ "functionId": "<stable ID>", "arguments": [{ "kind": "string", "value": "..." }], "expected": { "kind": "string", "value": "..." } }` in addition to `id`/`op`. Values support `string`, `bool`, and `int64` (Int64 uses an exact decimal string in responses), `float64` as described above, plus flat Lists encoded as `{ "kind": "list", "itemKind": "int64", "items": ["1", "2"] }`. `itemKind` is required even for empty Lists and accepts `string`, `bool`, or `int64`. Items are raw scalar values; Int64 output items are decimal strings. Scalar Options use `{ "kind": "option", "itemKind": "int64", "hasValue": true, "value": "0" }` or `{ "kind": "option", "itemKind": "int64", "hasValue": false }`. `itemKind` is mandatory for None as well as Some. None forbids `value`; Some requires a non-null scalar value. Some(false), Some(0), and Some("") remain distinct from None. String validation and limits also apply to Some payloads. Scalar Results use `{ "kind": "result", "okKind": "int64", "errorKind": "string", "isOk": false, "value": "missing" }`. Both kinds and the boolean `isOk` tag are mandatory even for the inactive side; each kind is `string`, `bool`, or `int64`. `value` is a required non-null raw scalar of the active side's type. Int64 payloads are decimal strings in responses. Input/expectation String payloads have the same 65536 UTF-16 code-unit limit as scalar Strings. Result equality compares both type arguments, the Ok/Err tag, and the payload; different type arguments reject before execution, and equal payloads with different tags do not match. Program `Err` is a successful execution (`ok: true`), not a process failure. Products with scalar fields are also supported (see below). Unions, List/Option nesting, and nesting inside Product/Union fields and Result payloads are supported as described above. List inputs, expectations, and results are limited to 4096 elements; String List inputs/expectations have a 65536-code-unit aggregate limit. List equality is ordered, exact element equality; scalar/List and differing element types reject before execution. The generated runner converts output Lists via the existing public Fold API, without changing the backend List representation. Parameter count/types, fake bindings, result type, and expectation type are checked before building. An expectation mismatch is a successful execution with `matches: false`, not a protocol/semantic failure. The response includes `sourceSnapshotId`, `functionId`, `actual`, `expected` (or null), `matches` (or null), and `mismatch` (or null). The runner emits the selected entry and its transitive function/callback targets, conservatively including all branches. Unrelated parallel functions do not impose concurrency requirements on a sequential entry. All type/capability declarations are retained and all capability operations remain externally bound, so an operation with a declared `failureType` anywhere in the program still blocks the run. Strings in arguments, expectations, fake responses, results and calls are bounded by the runner; any sequence of UTF-16 code units is a valid String (see *String run values*). The session compiles the program in process with Roslyn, into a temporary directory, with the options of a Release `dotnet build` of the generated project: optimized, nullable enabled, warnings as errors, against the SDK's reference pack of the running .NET version. There is no MSBuild and no package restore. The SDK's code analyzers do not run. The build is limited to 30 s; the program then runs in a bounded child process (5 s). Argument values and fake scripts are not compiled into the program: the child reads them from an input file, so every call of one entry over the same emitted code builds the same source. The session process keeps the built programs in a least-recently-used cache of 32 assemblies or 64 MiB, keyed by a SHA-256 fingerprint of the sources and the compiler, language, runtime and reference versions, so repeated calls skip the build. Failed builds are not cached. Results and diagnostics do not depend on whether a build was reused. No app-specific C# harness or identity lookup by generated name is required. This is not an OS sandbox, general execution engine, or live capability runner. It requires the local .NET 10 SDK; runtime and host/process failures remain diagnostics rather than program `Result` values.

`proposalId` is the proposal transaction ID rendered as a stable-ID string. It is only a handle inside the live process and is not resumable across restart.

### Simulate review detail

Every `simulate*` request accepts optional `"detail": "compact" | "full"`; anything else is a `ProtocolError` before simulation. The default is `compact`. The reply is `{ "proposalId", "detail", "review" }`, and the two levels differ only in how a function body is rendered inside the review; the proposal, its validation and what `commit` publishes are identical, so a compact proposal commits exactly like a full one ([decision](../decisions/compact-simulate-replies.md)).

- **compact** renders every function in the review (`createdFunction`, `proposedFunction`, the `function` of `baseFocus`/`proposedFocus`/`functionFocus` entries) as its identity, display name, parameters (IDs, display names, types), result type and `requiredEffects`, with `body: { "resultNodeId", "nodeCount" }` in place of the node list, and omits the per-node `nodeTypes` of a focus. Everything else is kept: the receipt (transaction, base and proposed snapshot IDs and fingerprints), readiness with blocking obligation IDs, the diff (added/removed node IDs or summaries, affected uses, call sites and body rewrites), `bodyEffectRequirements` with evidence, `aborts`, and a focus's `callers`, `callees` and `openObligations`. Node summaries in diffs (`previousTarget`, `proposedTarget`, `removedNodes`, `addedNodes`, `retainedSharedDependencies`, body rewrites) are the same at both levels: `nodeId`, `role`, `type`, dependencies, `parameterId`/`functionId`, `float64Bits`, plus `literal` for Bool, Int64 and String literals (`{ "kind": "bool", "value": false }`, `{ "kind": "int64", "value": "-42" }` as an exact decimal string, `{ "kind": "string", "value": "..." }`; a String longer than 256 UTF-16 code units is cut to that prefix with `"truncated": true` and its full `length`) and `referenceDisplayName`, the display name of the parameter, function, field, variant, Product type, capability operation or capability the node names directly (both null otherwise). A compact review can therefore tell sibling literals apart and show which value a targeted edit replaces. Type, effect and capability reviews contain no bodies and are the same at both levels.
- **full** adds the per-node graph and `nodeTypes`. Parameter-type, result-type and Product-field-type migrations and union-variant evolutions have no function focus, so at `full` their review adds `rewrittenFunctions`: the complete proposed function (signature and body) of every function in `bodyRewrites` or `repairedFunctionIds`, in that order. Nodes are rendered as before: `kind` as a debug string, or with `structuredReview: true` as the structured scoped graph of `readFunction`. `structuredReview` alone does not select `full`.

After commit, `readFunction`/`focusFunction` return the published graph. `nodeCount` counts every node of the body, including those in nested branch, arm and Bind regions.

### Create-function review projection

The Core function-creation proposal retains its full review model. The JSON-lines review of `simulateCreateFunction` returns the receipt/readiness, `createdFunction`, `bodyEffectRequirements` and `aborts`. It does not repeat the body through `materializedBody` or `proposedFocus`; at `detail: "full"`, `createdFunction` carries the complete body.

### Replace-body review projection

`simulateReplaceBody` already requires `expectedCurrentResultNodeId`, which callers obtain from the current function focus. Its review returns the receipt/readiness, a compact structural diff (owner function, old/new result node IDs, and added/removed node IDs), `proposedFunction`, `bodyEffectRequirements` and `aborts`, without complete `baseFocus` and `proposedFocus` projections. `ReplaceTarget` reviews keep both focuses (compact by default) and the targeted diff with node summaries: `previousTarget` is the replaced node and `proposedTarget` the node now at the target ID (the replacement's root keeps it), so a literal-for-literal edit, which adds no node, still shows the old and new value.

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
- `simulateCreateTypesByName`
- `simulateCreateFunctionByName`
- `simulateReplaceBodyByName`
- `simulateCreateCapabilityByName`
- `simulateOperationsByName`
- `runPureByName`

This layer is not a textual source language and does not become program authority. Resolution happens inside the current single-writer session; ambiguous or missing names are rejected. `simulateCreateFunctionByName` also rejects an exact display name already held by any local or imported function before registering a proposal (`ProtocolError`). The exact-ID creation workflows enforce the same rule per declaration kind, so a retried create cannot make a name ambiguous: a function, type, effect or capability whose display name another declaration of that kind already has is rejected with `FunctionCreationDuplicateDisplayName`, `TypeDeclarationDuplicateDisplayName`, `EffectDeclarationDuplicateDisplayName` or `CapabilityDeclarationDuplicateDisplayName`, and a type or function rename whose resulting name collides is rejected with `DuplicateDisplayName` (only the transaction's final names count, so two declarations may exchange names). This is a guard on creation and rename, not a snapshot validation rule: existing snapshots and dependency sets that already contain duplicates still open, and by-name requests against such a name still fail as ambiguous. Simulation, review, validation, system-owned identity allocation, commit policy, and publication are otherwise unchanged.

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

### Self calls

A `directCall` whose `function` is the display name of the function being created (`simulateCreateFunctionByName`) or replaced (`simulateReplaceBodyByName`) resolves to a [`SelfCall`](expression-authoring.md#selfcall). Its `arguments` name the owner's own parameters, so a recursive function is created in one request:

```json
{"kind": "directCall", "function": "sumTo", "arguments": [
  {"parameter": "n", "value": {"kind": "int64WrappingSubtract", "left": {"kind": "parameter", "name": "n"}, "right": {"kind": "int64Literal", "value": 1}}}
]}
```

The exact-ID form is `{"kind":"selfCall","arguments":[{"slot":0,"value":<recipe>}]}`, with arguments keyed by zero-based parameter slot, because the new function's IDs are system-assigned. Both persist as an ordinary `DirectCall` to the owner. In `simulateReplaceBody`, an exact-ID `directCall` whose `functionId` is the function being replaced stays a `directCall` in the request and is typed as a self call: its arguments must have the owner's exact parameter types, so a generic function cannot call itself at other type arguments on any of the three forms ([expression authoring](expression-authoring.md#selfcall)).

A self call may add `"keepUnlisted": true` to pass every parameter it does not name through unchanged. A loop over `(deps, roots, root, stack, seen)` that only advances `root` is then written `{"kind": "directCall", "function": "visit", "keepUnlisted": true, "arguments": [{"parameter": "root", "value": …}]}`; the review and the stored graph show the full call. `keepUnlisted` on a call to another function is rejected.

Creation by name already rejects a display name held by any existing function, so a self call in a creation body cannot be ambiguous; if one were, it would fail as `Ambiguous function`. During replacement the target is already resolved, so a call naming the target's own display name is a self call by the target's ID, even if another function shares that name. Mutual recursion and a `functionReference` to the function being created are out of scope; they still need the placeholder-then-replace route. A callback (`listMap`, `listFilter`, `listAny`, `listAll`, `listSortBy`, `listFold`, `listEffectFold`), worker, task body, capability implementation or `functionReference` that names the function being created is a `ProtocolError` that says so and names that route: create the function with a placeholder body, create the functions it calls back, then replace its body with `simulateReplaceBodyByName`, which resolves the function's own name ([probe 8](../verification/rrawrlet-slice-probe8-20260929.md#recursion-through-callbacks-measured)).

### Recursive types

`simulateCreateTypesByName` takes `types: [{ "displayName", "typeKind" }]`, each in the `simulateCreateTypeByName` shape. A named type reference inside any member resolves first to a member of the group, then to a published type, so the members may name themselves and each other:

```json
{"id": "json-1", "op": "simulateCreateTypesByName", "types": [
  {"displayName": "Json", "typeKind": {"kind": "union", "variants": [
    {"displayName": "Str", "fields": [{"displayName": "value", "type": {"kind": "primitive", "primitive": "string"}}]},
    {"displayName": "Arr", "fields": [{"displayName": "items", "type": {"kind": "builtinGeneric", "generic": "list", "arguments": [{"kind": "named", "name": "Json"}]}}]},
    {"displayName": "Obj", "fields": [{"displayName": "members", "type": {"kind": "builtinGeneric", "generic": "list", "arguments": [{"kind": "named", "name": "Member"}]}}]}]}},
  {"displayName": "Member", "typeKind": {"kind": "product", "fields": [
    {"displayName": "key", "type": {"kind": "primitive", "primitive": "string"}},
    {"displayName": "value", "type": {"kind": "named", "name": "Json"}}]}}]}
```

`simulateCreateTypeByName` accepts its own display name the same way (for example `Tree` with `children: List<Tree>`). Both review as `{ "kind": "createTypes", "receipt", "readiness", "createdTypes": [...] }` in request order, and one `commit` publishes every member; a single declaration that names no member keeps the `createType` review. The transport gives each member a request-local reference that simulation replaces with an allocated TypeId, so no reference is persisted. A member that could never be constructed, because each of its Product fields or of its Union variants needs a group member without a value, is rejected as `TypeDeclarationUninhabited`; see the [type-declaration contract](type-declaration-authoring.md#type-groups).

`simulateCreateCapabilityByName` takes the `simulateCreateCapability` shape with name-resolved `TypeRef`s and `"effect": "<effect display name>"` in place of `effectId`. Like function creation, it rejects a display name already held by any local or imported capability. `simulateCreateEffect` already takes no references and has no separate name form.

`simulateOperationsByName` takes the `simulateOperations` request with name-resolved deletion operations: `{ "kind": "deleteFunction", "function": "<name>" }`, `{ "kind": "deleteType", "type": "<name>" }`, `{ "kind": "deleteEffect", "effect": "<name>" }`, `{ "kind": "deleteCapability", "capability": "<name>" }` and `{ "kind": "deleteCapabilityOperation", "capability": "<name>", "operation": "<name>" }`. Every name is resolved against the current snapshot before the batch is simulated (an unknown or ambiguous name is a `ProtocolError`), and the batch then runs exactly as the exact-ID operations, in order and with the same review. Other operation kinds carry node IDs or create names, so they stay on `simulateOperations`; sending one here is a `ProtocolError`. Deleting dead helpers, including self-recursive ones, therefore needs no IDs:

```json
{
  "id": "cleanup-1",
  "op": "simulateOperationsByName",
  "operations": [
    {"kind": "deleteFunction", "function": "splitFrom"},
    {"kind": "deleteFunction", "function": "utf8From"}
  ]
}
```

`runPureByName` takes the `runPure` request with `"function": "<display name>"` in place of `functionId`. Its run values use the same selectors as expression recipes, each resolved within its owner:

| Run value | Exact-ID key | Name key (resolved within) |
| --- | --- | --- |
| Product | `typeId`, fields `fieldId` | `type`, fields `field` (the Product) |
| Union | `typeId`, `variantId`, fields `fieldId` | `type`, `variant` (the Union), fields `field` (the variant) |
| Nested List/Option/Result | `itemType`/`okType`/`errorType` exact `TypeRef` | the same keys with name-resolved `TypeRef` |
| `hostCapability` | `capabilityId` | `capability` |
| `fakeCapability` | `capabilityId`, operations `operationId`, case arguments `capabilityParameterId` | `capability`, `operation` (the capability), `parameter` (the operation) |

```json
{"kind": "fakeCapability", "capability": "Provider", "operations": [
  {"operation": "lookup", "cases": [{"arguments": [{"parameter": "key", "value": {"kind": "string", "value": "a"}}],
                                     "responses": [{"kind": "return", "value": {"kind": "union", "type": "Answer", "variant": "Missing", "fields": []}}]}]}]}
```

Each operation accepts only its own vocabulary: exact-ID keys in a `…ByName` request, or name keys in an exact-ID request, fail as malformed input. The one exception is the display names that run output reports (below): an exact-ID Product/Union value may carry `type`, `variant` and field `field` beside its IDs, so a reported `actual` can be passed back as `expected` or a fake response; they are informational and ignored, and the IDs decide. An unknown or ambiguous name, or a name of the wrong kind (such as a Union for a Product value), is a `ProtocolError` before anything is built or run. Once resolved, the values pass through the ordinary `runPure` validation, so shape and binding failures keep their `Run*` codes.

**Display names in run output.** `runPure` and `runPureByName` report the same output: every exact ID stays as before, and the declaration's current display name is added as a sibling property under the name key of the name-resolved input form, so a by-name client needs no ID→name map built from `declarations`:

| Output object | Exact-ID property | Added display name |
| --- | --- | --- |
| Result / runtime failure / unsupported-run error | `functionId` | `function` |
| Product / Union value (in `actual`, `expected`, `mismatch`, call arguments) | `typeId`, `variantId`, fields `fieldId` | `type`, `variant`, fields `field` |
| Mismatch path `field` step | `typeId`, `fieldId` | `type`, `field` |
| Call (`calls`, including runtime failures) | `parameterId` (the function's capability parameter), `operationId`, arguments `capabilityParameterId` | `parameter`, `capability` and `operation`, arguments `parameter` |
| Runtime frame | `functionId` / `operationId` | `function` / `capability` and `operation` |

```json
{"parameterId": "…", "parameter": "provider", "operationId": "…", "capability": "Provider", "operation": "lookup",
 "arguments": [{"capabilityParameterId": "…", "parameter": "key", "value": {"kind": "string", "value": "a"}}]}
```

A name is omitted only when its ID is not declared in the current snapshot. Nested `itemType`/`okType`/`errorType` `TypeRef`s stay in exact-ID form. The change is additive: clients reading IDs are unaffected.

Validation and structural-edit diagnostics that name a field, capability operation or parameter, function or effect print the display name next to the ID, for example `Field 'task' (<id>) uses TaskScope…` (`TaskTypeInDeclaredType`) or `CapabilityInvoke operation 'lookup' (<id>) requires effect 'io' (<id>)…` (`ExpressionCapabilityMissingEffectRequirement`). Diagnostic codes are unchanged.

This direction is consistent with evidence from structure-aware coding-agent interfaces such as [CODESTRUCT (ACL 2026)](https://aclanthology.org/2026.acl-long.607/), which found that operating on named AST entities instead of text spans can improve reliability and reduce token use. Nyrwvy does not treat that work as design authority or as evidence that every graph interface is superior. The relevant hypothesis is narrower: an AI-facing structured action space can remove text-position and identity-reconstruction work. Nyrwvy applies that idea to its authoritative typed semantic graph rather than translating the edit back into authoritative source text.

## Persistence format

M3 does not select a new long-term storage format. The maintained session stores the existing strict JSON snapshot codec because it is already the exact validated persistence boundary available to this milestone. Nyason evaluation remains a separate encoding concern and does not fork the session semantics.

## Non-goals

The maintained session does not add MCP/HTTP/cloud transport, multiple simultaneous transports, orchestration, a database, semantic merge/rebase, caller-owned production IDs, raw graph mutation, runtime provider registration/implementation, a new evaluator/runtime, or compatibility changes to the frozen evaluation protocols.

## Program-defined capabilities

The exact-ID and name-resolved create/replace recipes accept
[`capabilityConstruct`](program-capability-implementations.md). Its mappings
persist in snapshot schema 10 (old graphs still write schema 9), survive pinned
dependencies and reopen, and appear in structured reads. `runPure` executes
program-constructed capabilities internally through the generated interface; no
new input/output value encoding or host source registration is required.

### Typed function abort

Both exact-ID and symbolic expression recipes accept
`{"kind":"abort","error":<recipe>}` at graph-result positions in a Result-returning
function. Focus and structural review expose `aborts` with owner, error and
continuation identities. Abort snapshots use JSON schema 11; their prior schema
encodings remain unchanged when no Abort is present. See the
[contract and compatibility table](typed-function-abort.md#representation-and-compatibility)
and [ordinary-tool example](../../examples/function-abort/README.md).

### Argument-keyed fake responses

Each `fakeCapability` operation accepts exactly one script mode:

- `responses`: the existing single call-order queue;
- `cases`: independent response queues selected by **all** operation argument values.

```json
{
  "operationId": "<published operation ID>",
  "cases": [
    {
      "arguments": [
        { "capabilityParameterId": "<published parameter ID>", "value": { "kind": "string", "value": "member-2" } }
      ],
      "responses": [
        { "kind": "return", "value": { "kind": "result", "okKind": "string", "errorKind": "string", "isOk": false, "value": "member 2 failed" } }
      ]
    }
  ]
}
```

Every case must supply each operation parameter exactly once with its declared
type. Argument and case array order does not select a winner: equality is exact,
including ordinal Strings, list order, active Option/Result payloads, named type
and variant identities, and Product/Union fields. Field and parameter order in
the request is irrelevant. Keys support the same bounded typed run values as
ordinary arguments; capabilities cannot be nested in a key. Float64 keys match
bitwise, with every NaN equal and -0.0 distinct from 0.0. Bits32 keys match by value.


Duplicate complete keys, missing/unknown/duplicate parameters, wrong types or
mixing modes fail before host code runs. Malformed wire shapes are `ProtocolError`;
semantic binding errors are `RunBindingShapeMismatch`. Invalid nested values keep
their existing diagnostics. A zero-argument operation has one possible key, `[]`.
There is no partial match, wildcard, implicit fallback, repeat-last response or
case precedence.

On a call, logging, exact selection and queue consumption happen under the same
lock. Unrelated keys never consume each other's responses. Concurrent calls with
the **same** arguments still consume their shared key's queue in call order.
Return, throw, `delayMillis` and `awaitCancellation` retain their existing behavior;
waiting and throwing occur after the consumption lock has been released.

An unmatched call, including an empty `cases` array, produces `RunBindingNoMatch`
with exception type `FakeArgumentsUnmatchedException`. A matched but exhausted
queue produces `RunBindingExhausted`. Both carry semantic frames and the call log,
including the failed call. Empty queues are allowed and never fall through to
another case. Omitted operations retain the existing exhausted-binding behavior.

Limits are 256 cases per operation, 256 arguments per case, and 256 total responses
per operation across all cases. Key values participate in the existing String,
node-count, nesting, type and placement checks. Running a script never changes
the committed snapshot or persists its queues.

Existing wire `responses` scripts need no migration; snapshot and IR schemas do
not change. Direct F# `PureRunFakeOperation` construction adds `Cases = None` for
ordered scripts, or `Cases = Some cases` with `Responses = []` for keyed scripts.
