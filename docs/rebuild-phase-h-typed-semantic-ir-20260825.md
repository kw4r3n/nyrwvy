# F# Milestone 0 rebuild — Phase H — Typed Semantic IR — 2026-08-25

## Status

**Verified for the first Milestone 0 Typed Semantic IR boundary.**

The latest source/test tree on the Phase H validation branch was exercised through pull-request GitHub Actions on 2026-08-25 using .NET SDK `10.0.400`.

Observed result:

```text
restore:  success
build:    success, 0 warnings, 0 errors
tests:    50 passed, 0 failed, 0 skipped
```

This phase defines a derived typed semantic projection and canonical serialization. It does not yet define Execution IR or C# generation.

## Purpose

Typed Semantic IR is the boundary between editable semantic state and execution-oriented lowering.

It consumes only a structurally valid, semantically complete `Snapshot` and preserves validated language meaning while removing concerns that belong only to editing, persistence, transactions, or presentation.

```text
Semantic Snapshot
    -> validation / obligations
    -> semantic completeness
    -> Typed Semantic IR
    -> future Execution IR
    -> future backend
```

## Shared semantic facts

Phase H first extracts reproducible node-type derivation into `SemanticFacts`.

Both:

- `SnapshotValidator`; and
- `TypedSemanticIr`

now use the same `SemanticFacts.createNodeTypeResolver` implementation.

This is a deliberate single-derived-fact rule. Type semantics must not be independently reimplemented by validators, lowering passes, viewers, or backends.

If a future node kind changes type derivation, the semantic rule is updated in one place and all consumers observe the same fact.

No type fallback exists. A node whose type cannot be derived produces `None` at the fact layer and causes Typed Semantic IR projection to fail with `MissingNodeType` after the ordinary snapshot validation boundary.

## Typed Semantic IR contents

The projection retains:

- stable `TypeId`, `FieldId`, `VariantId`, `FunctionId`, `ParameterId`, and pattern-binding identity where semantically relevant;
- complete `TypeRef` values;
- structured product and union definitions;
- structured value graphs and branch structure;
- direct function-call semantics;
- capability invocation semantics;
- explicit effect requirements;
- capability operation signatures;
- `OriginNodeId` provenance for every typed semantic node;
- the exact source `SnapshotId`.

The projection removes:

- display names;
- obligations;
- obligation resolution history;
- transaction IDs and operation history;
- semantic diff records;
- JSON/Nyason storage shape;
- viewer/presentation state;
- human-oriented formatting.

## Node normalization

The semantic graph intentionally contains some locally explicit information that is useful while an AI edits an incomplete or proposed graph.

For example:

```text
ResultOk(errorType, valueNodeId)
ResultErr(okType, errorNodeId)
OptionNone(itemType)
ProductConstruct(typeId, fields)
```

In Typed Semantic IR, every node already has one complete validated `TypeRef`:

```text
TypedSemanticNode
  OriginNodeId
  Type
  Kind
```

Therefore the normalized IR kinds become:

```text
ResultOk(valueNodeId)
ResultErr(errorNodeId)
OptionNone
ProductConstruct(fields)
```

The removed constructor hints are not lost semantics. Their validated meaning is represented once in `TypedSemanticNode.Type`.

This is an intentional difference between the AI-editing graph and the derived IR:

- semantic graph explicitness favors safe local editing;
- Typed Semantic IR favors one normalized validated fact per meaning.

## Identity and provenance

Typed Semantic IR keeps semantic stable identities that remain relevant to language meaning and backend-neutral diagnostics.

Each value node also carries:

```text
OriginNodeId: GraphNodeId
```

The IR does not introduce a second persistent semantic node identity.

Execution IR is expected to use separate IR-local block/value IDs because one typed semantic node may eventually lower into multiple execution operations. Those IDs belong to the execution projection and must retain `OriginNodeId` provenance rather than replacing semantic identity.

## Completeness gate

`TypedSemanticIr.project` calls the semantic readiness boundary before projection.

Projection outcomes distinguish:

- invalid snapshot -> `InvalidSnapshot`;
- valid snapshot with open blocking obligations -> `IncompleteSnapshot`;
- complete snapshot with an unexpectedly missing derived node type -> `MissingNodeType`;
- complete valid snapshot -> `TypedSemanticProgram`.

An incomplete signature-change snapshot therefore cannot silently proceed toward backend lowering.

The representative Milestone 0 test creates an actual `MissingArgument` obligation and verifies that Typed Semantic IR projection refuses it.

## Canonical projection

Nonsemantic ordering is normalized during projection:

- types by `TypeId`;
- capabilities by `CapabilityId`;
- capability operations by operation ID;
- functions by `FunctionId`;
- graph nodes by `OriginNodeId`;
- field/argument associations by stable target identity;
- union arms by `VariantId`;
- effect sets by `EffectId`.

Function parameter order is preserved because the current semantic model still treats declared parameter order as meaningful metadata for the signature.

A test reverses nonsemantic source collections and confirms that the resulting Typed Semantic IR values are structurally equal.

## Canonical serialization

`TypedSemanticIrCodec` defines derived IR schema version `1` and emits deterministic compact JSON.

The codec is intentionally one-way at this phase:

```text
TypedSemanticProgram -> canonical bytes
```

Typed Semantic IR is derived from the authoritative graph, so no upstream workflow requires treating serialized IR as an editable source.

The serialization includes:

- IR schema version;
- source snapshot identity;
- typed declarations;
- effects and capabilities;
- typed functions and structured value graphs;
- graph-node provenance.

It excludes display names, obligations, and transaction provenance.

Tests verify byte-for-byte identical serialization after nonsemantic graph reordering.

## Verified invariants

The Phase H tests demonstrate that:

1. `SemanticFacts` derives a type for every node in the representative Milestone 0 graph;
2. Typed Semantic IR preserves stable function identities;
3. every projected node preserves its source `GraphNodeId` as provenance;
4. normalized Result/Option constructors retain complete node types without duplicate type hints;
5. open obligations block Typed Semantic IR projection;
6. nonsemantic graph ordering does not change the projected IR;
7. effect and capability contracts survive projection;
8. canonical Typed Semantic IR serialization is byte-for-byte deterministic;
9. the serialized IR contains execution-relevant semantics but omits editing/presentation metadata;
10. validator and IR projection share the same node-type fact implementation.

## Q-009 boundary result

For Milestone 0, the conceptual boundary between the two IR stages is now concrete enough to treat as resolved direction.

### Typed Semantic IR

Represents validated Nayamnyam language meaning:

- complete types;
- stable semantic references;
- structured products/unions/Option/Result;
- structured branching/matching;
- direct calls;
- effects and capabilities;
- source semantic provenance.

It does **not** choose an execution schedule or target runtime representation.

### Execution IR

The next phase will make execution behavior explicit:

- evaluation order;
- execution blocks;
- branch targets and control transfer;
- IR-local produced values;
- explicit function return;
- capability invocation order;
- deterministic lowering of structured matches and conditionals;
- runtime-representation requirements only where backend-neutral execution needs them.

Execution IR should use a distinct schema rather than overloading Typed Semantic IR with staged optional fields. This keeps the AI-visible contract of each stage smaller and avoids partially lowered states that require contextual interpretation.

## Deliberately deferred

Phase H does not define:

- Execution IR block/value ID encoding;
- C# representations of products, unions, Option, or Result;
- evaluation order;
- backend capability bindings;
- C# source generation;
- exception lowering;
- optimization;
- Nyason as the canonical graph persistence format.

## Next gate

Implement the smallest backend-neutral Execution IR capable of lowering the representative Milestone 0 Typed Semantic IR.

The key rule is:

> Execution IR may make execution order explicit, but it must not re-resolve names, re-infer semantic types, or introduce backend-specific C# object-model decisions.

Only after that boundary is verified should the C# emitter be introduced.
