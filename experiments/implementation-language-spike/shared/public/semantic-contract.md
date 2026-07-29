# Shared semantic contract

This document is the language-neutral semantic and serialization contract for
version 1 of the implementation-language spike. Both baselines must implement
the same behavior. Names in code may follow local language conventions, but the
operation names, JSON field names, discriminator values, stable IDs, and
observable results defined here are normative.

## Stable IDs

The v1 stable-ID domains are:

- `TypeId`, serialized with the required prefix `type:`
- `FunctionId`, serialized with the required prefix `function:`
- `ParameterId`, serialized with the required prefix `parameter:`
- `CallId`, serialized with the required prefix `call:`
- `ObligationId`, serialized with the required prefix `obligation:`
- `SnapshotId`, serialized with the required prefix `snapshot:`
- `TransactionId`, serialized with the required prefix `transaction:`

Stable IDs are compared and sorted as ordinal, case-sensitive text. Whenever a
rule below derives an ID tail, it removes exactly one required domain prefix.
A value with a missing prefix or a prefix from the wrong domain is invalid; it
must not be silently truncated or accepted.

## Semantic model

The v1 entities and their fields are:

- `TypeEntity`: `id`, `name`
- `Parameter`: `id`, `name`, `type_id`
- `FunctionEntity`: `id`, `name`, ordered `parameters`
- `ArgumentBinding`: `parameter_id`, `value_ref`
- `CallEntity`: `id`, `callee_id`, ordered `arguments`
- `ObligationKind`: `MissingArgument(call_id, parameter_id)`
- `Obligation`: `id`, `kind`, `status`, where v1 `status` is `open`
- `Snapshot`: `schema_version`, `id`, `types`, `functions`, `calls`,
  `obligations`, where v1 `schema_version` is `1`

Calls are a snapshot-level collection. Caller ownership is intentionally not
part of this contract.

All v1 obligations are open blocking obligations. The JSON representation of
`MissingArgument(call_id, parameter_id)` is the following closed tagged object,
in the shown property order:

```json
{
  "kind": "missing_argument",
  "call_id": "call:example",
  "parameter_id": "parameter:example"
}
```

## Operations and transaction results

The v1 operations are:

- `renameFunction(function_id, new_name)`
- `addParameter(function_id, parameter)`

Both operations are evaluated against an explicitly supplied base snapshot ID
and transaction ID. Same-name comparisons are ordinal and case-sensitive.
Validation of a stale base snapshot happens before target lookup or any other
operation-specific handling.

The closed transaction-result alternatives are:

- `Rejected(base_snapshot_id, diagnostics)`
- `Incomplete(snapshot, semantic_diff, blocking_obligation_ids)`
- `Executable(snapshot, semantic_diff)`

A stale base snapshot returns `Rejected` and does not produce a candidate
snapshot. A candidate containing any open blocking obligation returns
`Incomplete`; otherwise it returns `Executable`. Existing snapshot objects are
never mutated.

`renameFunction` preserves the function ID and every call edge. Its semantic
diff contains exactly:

1. `FunctionRenamed(function_id, old_name, new_name)`

`addParameter` appends the parameter to the function's declared parameter
order. It creates one `MissingArgument` obligation for each existing call to
that function that does not bind the added parameter. Its semantic diff starts
with:

1. `ParameterAdded(function_id, parameter_id)`

It is followed by one `ObligationCreated(obligation_id)` for each created
obligation, sorted by obligation-ID text. Semantic diffs otherwise preserve
operation order.

The v1 semantic-change alternatives are therefore:

- `FunctionRenamed(function_id, old_name, new_name)`
- `ParameterAdded(function_id, parameter_id)`
- `ObligationCreated(obligation_id)`

For a transaction with ID `transaction:<transaction-tail>`, the candidate
snapshot ID is:

```text
snapshot:<transaction-tail>
```

For a missing argument on `call:<call-tail>` for
`parameter:<parameter-tail>`, the new obligation ID is:

```text
obligation:<transaction-tail>:<call-tail>:<parameter-tail>
```

## Canonical JSON

Canonical JSON is UTF-8 without a byte-order mark, uses LF line endings,
two-space indentation, and ends with exactly one LF. Objects use the property
orders specified below. No object permits unknown properties.

Collections are arrays. ID-keyed collections are sorted by stable-ID text:
`types`, `functions`, `calls`, and `obligations`. Function `parameters` and call
`arguments` preserve declared order.

The required property order for each object is:

1. Snapshot: `schema_version`, `id`, `types`, `functions`, `calls`,
   `obligations`
2. Type entity: `id`, `name`
3. Function entity: `id`, `name`, `parameters`
4. Parameter: `id`, `name`, `type_id`
5. Call entity: `id`, `callee_id`, `arguments`
6. Argument binding: `parameter_id`, `value_ref`
7. Obligation: `id`, `kind`, `status`
8. Missing-argument kind: `kind`, `call_id`, `parameter_id`

The deterministic summary is a closed JSON object with this property order:
`schema_version`, `snapshot_id`, `type_count`, `function_count`, `call_count`,
`obligation_count`. Counts are the lengths of the corresponding snapshot
collections. It uses the same UTF-8, indentation, LF, and trailing-LF rules as
canonical snapshot JSON.

The v1 schema is `schema/snapshot-v1.schema.json`. The canonical initial
fixture is `fixtures/initial-snapshot-v1.json`.
