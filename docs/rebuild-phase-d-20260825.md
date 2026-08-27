# F# Milestone 0 rebuild — Phase D — 2026-08-25

## Status

**Verified for the current Phase D value-graph scope.**

The exact semantic-core and test tree at branch commit `3cf05bcd9895af63296487f645845ea5b5539f5a` was exercised through pull-request GitHub Actions on 2026-08-25 using .NET SDK `10.0.400`.

Verification commands:

```shell
dotnet restore NyamNyam.slnx
dotnet build NyamNyam.slnx --configuration Release --no-restore
dotnet test NyamNyam.slnx --configuration Release --no-build --no-restore
```

Result:

```text
restore: success
build:   success, 0 warnings, 0 errors
tests:   21 passed, 0 failed, 0 skipped
```

Documentation-only records were added after that successful source/test tree. No lowering, C# backend, viewer, or Nyason parser is included in this verification claim.

## Purpose

Phase D establishes the minimum typed value-graph structures needed to represent the Milestone 0 validation program without reintroducing a text-first body model or duplicating semantic facts.

The design is explicitly AI-first. The authoritative representation favors local semantic clarity, typed identity, closed node alternatives, and mechanically detectable invalid states over concise human-authored syntax.

See [AI-first semantic design principles](ai-first-design-principles-20260825.md).

## Type references

Phase D introduces explicit `TypeRef` values:

```text
Primitive(String | Bool | Unit)
Named(TypeId)
BuiltinGeneric(Option, [item])
BuiltinGeneric(Result, [ok, error])
```

`Option` and `Result` are the only generic forms required by Milestone 0. They do not introduce general user-defined generics.

Builtin generic arity is validated explicitly:

- `Option` requires exactly one type argument;
- `Result` requires exactly two type arguments.

A `Named` type reference must resolve to an existing declared type. No unknown-type fallback exists.

## Declared product and union structure

Declared semantic types now have one of two Milestone 0 forms:

```text
Product(fields[])
Union(variants[])
```

Fields and variants receive typed stable IDs. Variant payloads use named fields rather than positional tuple payloads.

This permits construction, access, matching, diagnostics, and future operations to target semantic identity rather than a name or ordinal position.

## Value nodes

The Phase D node taxonomy includes:

- `ParameterReference`;
- `PatternReference`;
- `StringLiteral`;
- `BoolLiteral`;
- `UnitLiteral`;
- `DirectCall`;
- `ProductConstruct`;
- `FieldAccess`;
- `UnionConstruct`;
- `OptionNone`;
- `OptionSome`;
- `ResultOk`;
- `ResultErr`;
- `Conditional`;
- `MatchUnion`;
- `MatchOption`;
- `MatchResult`.

A larger closed taxonomy is intentional. Semantically different constructs are not hidden behind a generic operator string merely to reduce the number of node cases.

`DirectCall` remains the only authoritative representation of an internal function call.

## Explicit type information in constructors

`ResultOk` carries the error-side type and `ResultErr` carries the ok-side type because the missing side is not derivable from the child value alone.

This is deliberate explicit redundancy for local comprehension, not duplicate authority: the information is part of the one node representation and is validated as a type reference.

`OptionNone` similarly carries its item type because it has no child value from which the type could be derived.

## Nested ValueGraph

A function now owns one root `ValueGraph`:

```text
SemanticFunction
  Parameters[]
  ResultType
  Body: ValueGraph
    Nodes[]
    ResultNodeId
```

Structured branch nodes own nested `ValueGraph` values.

The lexical scope rule is:

- nodes in a child graph may reference nodes visible in parent graphs;
- child-local nodes do not escape to parents;
- sibling branch-local nodes are not mutually visible;
- a graph's `ResultNodeId` must identify a node owned directly by that graph;
- serialized node order does not define dependency order.

This makes branch execution structure explicit without encoding source-text order as semantic identity.

## Match bindings and exhaustiveness

Pattern payloads use stable `PatternBindingId` values. A `PatternReference` is legal only inside the lexical branch scope that owns its binding.

Union matching is exhaustive in the Milestone 0 subset:

- every declared stable `VariantId` must have exactly one arm;
- unknown or duplicate variant arms are rejected through exact set comparison and duplicate checks;
- every variant payload field must be bound exactly once;
- pattern binding types must match their source fields.

`Option` and `Result` matching explicitly provide both alternatives. There is no wildcard/default arm in Phase D.

`Option.Some` and `Result.Ok` intentionally share the `ValueMatchArm` semantic type because their arm-local semantics are identical: bind one produced value and evaluate one body graph. `Result.Err` uses the structurally distinct `ErrorMatchArm`. This avoids maintaining multiple record schemas with identical meaning and shape.

## Product construction and access

`ProductConstruct` binds values to stable `FieldId` targets. Validation requires the exact declared field set and checks every value type.

`FieldAccess` stores only the source value node and stable field identity. The owning product type is derived from the field declaration rather than stored as a second independently editable fact.

`UnionConstruct` follows the same rule: stable `VariantId` determines its owner type, and payload bindings use stable field identities.

## Transactions remain graph-aware

Phase C semantic operations were migrated to the nested value-graph model without adding a parallel call representation.

- affected-call discovery recursively traverses `ValueGraph`;
- `function.add_parameter` updates direct-call nodes even when they are nested in branches;
- obligation resolution updates that same authoritative direct-call node;
- every affected call still requires exactly one explicit call-site plan;
- no missing argument is guessed or defaulted.

Operation request records and semantic-diff records now use deliberately different field names where their roles differ. This reduces both F# inference ambiguity and AI schema confusion.

## Validation coverage

The current validator covers, for the implemented Phase D subset:

- duplicate typed identities for types, fields, variants, functions, parameters, pattern bindings, graph nodes, and obligations;
- named type resolution and builtin generic arity;
- graph-node lexical scope;
- pattern-binding lexical scope;
- graph result ownership;
- value dependency cycles;
- direct-call ownership, parameter association, argument completeness, and argument type compatibility;
- product/union construction target and field completeness;
- field-access source type;
- conditional condition and branch result types;
- exhaustive union matching;
- Option/Result input, binding, and arm result types;
- obligation target and resolution consistency.

Semantic readiness still means only structural/semantic completeness with respect to current blocking obligations. It does not yet assert backend capability or lowering readiness.

## Reference JSON codec

The strict JSON codec is updated to schema version 3 so the Phase D graph can be persisted and round-tripped during development.

This is **not** a decision that JSON is the long-term canonical persistence format.

The codec remains useful as:

- a verified reference implementation;
- a deterministic round-trip oracle;
- a debugging format;
- a comparison baseline for Nyason and TSON experiments.

Unknown fields and duplicate properties remain rejected. Collections whose order is not semantic are canonically sorted; parameter order and builtin generic argument order are preserved where they currently carry meaning.

## Nyason gate

[Nyason v0](nyason-v0-20260825.md) is now the dedicated-format candidate for semantic persistence and AI-facing structured transport.

Nyason is intentionally separate from the semantic model. A future Nyason decoder must produce the same typed graph that the reference JSON decoder produces.

No full Nyason parser is implemented in Phase D. The next storage experiment should use the verified Phase D graph as a realistic fixture and compare Nyason against strict JSON and, where practical, Typed Schema Object Notation (TSON).

## Compiler feedback as design evidence

The first Phase D CI compile exposed F# record inference ambiguity around multiple records with overlapping fields. This was not bypassed with dynamic typing or fallback behavior.

Two responses were adopted:

1. validator helper boundaries now carry explicit domain types where inference could select the wrong stable-ID domain;
2. semantically identical `Option.Some` and `Result.Ok` arm record shapes were consolidated into one `ValueMatchArm` concept rather than maintaining duplicate schemas.

The successful second compile therefore validates both the semantic changes and the AI-first structural-distinctness rule used to resolve the failure.

## Deliberately deferred

Phase D does not yet define:

- production stable-ID allocation or cross-repository copy semantics;
- obligation invalidation/supersession states;
- core string intrinsics required by the concrete validation algorithm;
- effects or capability invocation;
- Console capability contracts;
- Typed Semantic IR;
- Execution IR;
- C# lowering;
- viewer query payloads;
- Nyason parser/encoder implementation.

The next semantic gate should add the minimal intrinsic/effect/capability boundary required by Milestone 0, then define Typed Semantic IR over fully validated graph facts. Nyason can be prototyped against the now-realistic Phase D value graph without making persistence syntax part of language semantics.
