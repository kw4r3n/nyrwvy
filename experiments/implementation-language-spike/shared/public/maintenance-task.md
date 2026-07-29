# Maintenance task

Implement every phase below in the stated order. Complete and verify one phase
before proceeding to the next. The phases are cumulative: later phases retain
all behavior introduced by earlier phases unless a phase explicitly replaces
it.

## Phase 1: Parameter-name conflict obligation

Add `ParameterNameConflict(function_id, existing_parameter_id,
added_parameter_id)`.

`addParameter` still adds the parameter. When another parameter already has the
same display name, it also creates a blocking parameter-name-conflict
obligation. Name comparison is ordinal and case-sensitive. Create one conflict
obligation for every existing matching parameter, in declared parameter order.
Missing-argument obligations remain required for every affected call.

Canonical JSON encodes the new closed tagged kind as:

```json
{
  "kind": "parameter_name_conflict",
  "function_id": "function:example",
  "existing_parameter_id": "parameter:existing",
  "added_parameter_id": "parameter:added"
}
```

For `transaction:<transaction-tail>`, `function:<function-tail>`,
`parameter:<existing-parameter-tail>`, and
`parameter:<added-parameter-tail>`, the conflict obligation ID is:

```text
obligation:<transaction-tail>:<function-tail>:<existing-parameter-tail>:<added-parameter-tail>
```

ID tail derivation removes exactly one required domain prefix. Missing or
wrong-domain prefixes, empty tails, and tails containing a colon are invalid.
The derived conflict obligation ID contains exactly four nonempty atomic tail
components separated by the colons shown in the formula. The semantic diff
begins with `ParameterAdded`, followed by every `ObligationCreated` change
sorted by obligation-ID text.

## Phase 2: No-change transaction result

Add `NoChange(base_snapshot_id, semantic_diff)`.

Renaming a function to its current name returns `NoChange`, creates no
snapshot, and returns an empty semantic diff. Stale-base validation still
happens before target lookup or no-change handling.

## Phase 3: Type rename

Replace the function-only rename entry point with `renameSymbol`, supporting
both functions and types. Add:

```text
TypeRenamed(type_id, old_name, new_name)
```

Use the closed target representation:

```text
RenameTarget:
  Function(function_id: FunctionId)
  Type(type_id: TypeId)
```

The exact language-neutral operation input order is:

```text
renameSymbol(snapshot, base_snapshot_id, transaction_id, target, new_name)
```

Implement the minimal F# or C# public spelling frozen under **Frozen
post-maintenance rename surface** in `semantic-contract.md`; a hidden evaluator
will call that surface directly. Do not replace the target with a string
discriminator, general stable ID, enum plus separate ID, or open hierarchy.

Stale-base validation precedes target dispatch, target lookup, and same-name
handling. A missing function or type target is rejected without a candidate.
Same-name comparison is ordinal and case-sensitive; it returns
`NoChange(base_snapshot_id, semantic_diff)` with an empty semantic diff and no
candidate snapshot.

A successful function rename preserves its function ID and every call edge and
emits exactly one `FunctionRenamed` change. A successful type rename preserves
its type ID and every parameter `type_id` reference and emits exactly one
`TypeRenamed` change. A rename candidate uses the transaction-derived snapshot
ID and is `Incomplete` with ordinally sorted blocking obligation IDs when open
blocking obligations exist; otherwise it is `Executable`. Existing snapshots
are never mutated.

## Phase 4: Schema v2 migration

Add `origin_transaction_id` to every obligation.

Writers emit schema version `2` only. Readers accept schema versions `1` and
`2`. A native v2 obligation records the transaction that created it in
`origin_transaction_id`. Migrating a v1 obligation assigns
`transaction:migrated`. Re-serializing a loaded v1 snapshot emits canonical v2
JSON. A canonical v2 obligation uses this property order: `id`, `kind`,
`status`, `origin_transaction_id`.

## Completion requirements

- Run all existing public checks before finishing.
- Preserve all existing tests and add coverage for the new behavior.
- Do not modify this task, any fixture, or any other file under
  `shared/public`.
- Do not delete tests, weaken compiler settings, suppress warnings, add
  third-party packages, or use network access.
- Leave the workspace in a cleanly buildable state.

A hidden evaluator will test semantic edge cases after completion.
