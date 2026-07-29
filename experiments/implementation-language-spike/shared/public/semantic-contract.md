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

Except for a derived obligation ID as described below, a stable ID is exactly
`<domain>:<tail>`. The tail is nonempty atomic text: it contains no colon, CR,
or LF. Stable IDs are compared and sorted as ordinal, case-sensitive text.
Whenever a rule below derives an ID tail, it removes exactly one required
domain prefix and obtains that one atomic tail. A value with an empty or
non-atomic tail, a missing prefix, or a prefix from the wrong domain is invalid;
it must not be silently truncated or accepted.

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

## Frozen post-maintenance rename surface

Phase 3 of `maintenance-task.md` replaces `renameFunction` with one public
`renameSymbol` entry point. Its target is this closed representation:

```text
RenameTarget:
  Function(function_id: FunctionId)
  Type(type_id: TypeId)
```

No string discriminator, untyped or general stable ID, enum plus separate ID,
or open-ended target representation is part of the public contract. The exact
language-neutral call shape, including input order, is:

```text
renameSymbol(snapshot, base_snapshot_id, transaction_id, target, new_name)
  -> TransactionResult
```

The minimal evaluator-callable public spelling is frozen as follows. All types
are in namespace `Nayamnyam.Spike`.

F#:

```fsharp
type RenameTarget =
    | Function of functionId: FunctionId
    | Type of typeId: TypeId

module Operations =
    val renameSymbol:
        snapshot: Snapshot ->
        baseSnapshotId: SnapshotId ->
        transactionId: TransactionId ->
        target: RenameTarget ->
        newName: string ->
        TransactionResult
```

C# (the stable-ID aliases are expanded because C# source aliases are not public
metadata):

```csharp
public abstract record RenameTarget
{
    private RenameTarget() { }

    public sealed record Function(StableId<FunctionTag> FunctionId)
        : RenameTarget;
    public sealed record Type(StableId<TypeTag> TypeId)
        : RenameTarget;
}

public static class Operations
{
    public static TransactionResult RenameSymbol(
        Snapshot snapshot,
        StableId<SnapshotTag> baseSnapshotId,
        StableId<TransactionTag> transactionId,
        RenameTarget target,
        string newName);
}
```

Observable processing order and results are:

1. Validate `base_snapshot_id` against `snapshot.id` before inspecting
   `target`. A stale base returns `Rejected(base_snapshot_id, diagnostics)`,
   creates no candidate snapshot, and does not perform target lookup or
   same-name handling.
2. Dispatch the closed target. `Function(function_id)` looks up exactly that
   function; `Type(type_id)` looks up exactly that type. A missing target uses
   the existing rejected-transaction behavior and creates no candidate
   snapshot.
3. Compare `new_name` with the target's current display name ordinally and
   case-sensitively. Equality returns
   `NoChange(base_snapshot_id, semantic_diff)` with an empty semantic diff,
   creates no candidate snapshot, and does not derive a snapshot ID from
   `transaction_id`.
4. Otherwise create an immutable candidate whose ID is derived from
   `transaction_id` by the existing snapshot-ID rule. A function target changes
   only that function's display name, preserves its `FunctionId` and every call
   edge, and emits exactly
   `FunctionRenamed(function_id, old_name, new_name)`. A type target changes
   only that type's display name, preserves its `TypeId` and every parameter
   `type_id` reference, and emits exactly
   `TypeRenamed(type_id, old_name, new_name)`.
5. Return `Incomplete(candidate, semantic_diff, blocking_obligation_ids)` when
   the candidate contains open blocking obligations, with blocking IDs sorted
   by ordinal stable-ID text; otherwise return
   `Executable(candidate, semantic_diff)`. The semantic diff contains only the
   one rename change described above.

The input snapshot and every object reachable from it remain unchanged for
every result alternative.

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

A derived v1 obligation ID is the `obligation:` domain prefix followed by
exactly those three nonempty atomic tail components. The colons shown in the
formula are component separators; no component itself may contain a colon.
This makes the derivation reversible and prevents distinct source-ID tuples
from producing the same obligation ID.

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
