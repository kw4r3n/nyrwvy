# F# Milestone 0 rebuild — Phase G / Nyason v0 — 2026-08-25

## Status

**Nyason v0 passes the current mechanical encoding gate, but is not yet accepted as the canonical persistence format.**

The exact code/test/CI branch tree at `99edb9020552bd449d37c298689b77379eacb517` was exercised through pull-request GitHub Actions on 2026-08-25 using .NET SDK `10.0.400`.

Observed result:

```text
restore:  success
build:    success, 0 warnings, 0 errors
tests:    42 passed, 0 failed, 0 skipped
```

The 42 tests include the corrected compile-backed Milestone 0 semantic fixture tests and the Nyason-specific tests. Documentation-only corrections were added afterward.

## Purpose

Nyason is a NyamNyam-specific semantic encoding experiment. It tests whether the storage/transport layer can make semantic roles and invalid edits more mechanically obvious to AI agents than generic JSON without becoming a second semantic model.

The experiment deliberately does **not** ask whether Nyason is pleasant for humans to author manually. Source-level elegance and minimum character count are lower-priority concerns for NyamNyam.

## Layering

Nyason remains below the semantic model:

```text
Nyason text
    -> Nyason syntax tree
    -> explicit semantic decoder
    -> Snapshot
    -> SnapshotValidator
```

The reverse direction is:

```text
validated Snapshot
    -> explicit Nyason semantic encoder
    -> Nyason syntax tree
    -> deterministic text renderer
```

No validator, operation, query, or lowering pass reads Nyason spelling directly. JSON and Nyason therefore decode to the same semantic graph model.

## Minimal syntax layer

`NyasonSyntax` intentionally knows nothing about NyamNyam semantics. Its value model contains only:

- atoms;
- quoted strings;
- booleans;
- lists;
- tagged records with named fields.

There are no macros, anchors, aliases, includes, expressions, implicit conversions, arbitrary schema extensions, or recovery heuristics.

The parser rejects duplicate fields immediately and rejects malformed input rather than attempting best-effort recovery.

## Semantic codec

`NyasonCodec` explicitly maps the currently verified Milestone 0 semantic subset.

Examples of structural forms include:

```text
result-type {
  ok = named-type { id = type:type-valid-user }
  error = named-type { id = type:type-validation-error }
}
```

and:

```text
direct-call {
  id = node:node-main-validate-call
  target-function = function:function-validate-user
  arguments = [
    argument {
      target-parameter = parameter:parameter-validate-input
      value = node:node-main-input
    }
  ]
}
```

The explicit record tag and field roles are part of the encoding contract. They are not looked up through display names or positional conventions.

## Typed stable-ID domains

Nyason stable IDs use a visible domain prefix plus an escaped opaque payload:

```text
type:...
field:...
variant:...
function:...
parameter:...
pattern-binding:...
node:...
effect:...
capability:...
capability-operation:...
capability-parameter:...
obligation:...
snapshot:...
transaction:...
```

The decoder verifies the required domain before constructing the typed `StableId<'Kind>`. A `node:...` atom used where a `FunctionId` is required therefore fails in the decoder instead of surviving until graph validation.

Opaque ID payloads are percent-escaped for transport and round-trip exactly.

## Shape-constrained Option and Result

Nyason does not expose the internal generic-arity representation directly.

Instead of a generic list that could contain an invalid number of arguments, Nyason uses:

```text
option-type {
  item = ...
}
```

and:

```text
result-type {
  ok = ...
  error = ...
}
```

This removes malformed `Option`/`Result` arity from the representable Nyason state space while still decoding to the existing semantic `TypeRef` model.

## Closed records

All semantic record shapes are closed:

- required fields must be present;
- unknown fields are rejected;
- duplicate fields are rejected;
- no ignored extension fields exist;
- optional semantic facts are represented only where the semantic model actually permits absence.

Forward evolution therefore requires an explicit schema identity change rather than silent tolerance of unknown structure.

## Schema identity

The prototype uses the exact schema identity:

```text
nayamnyam.snapshot.v0
```

A content hash is intentionally not included yet. The project does not currently have one separately stored authoritative schema artifact whose hash could be referenced without creating another source of truth. Adding a decorative or manually synchronized hash would weaken rather than strengthen the guarantee.

## Canonicalization

For the current profile:

- top-level entity collections are sorted by stable ID when order is not semantic;
- graph nodes are sorted by stable ID;
- argument and field associations are sorted by target stable ID;
- union match arms are sorted by variant ID;
- required effects are sorted by effect ID;
- function parameter order remains preserved;
- ordered type arguments retain semantic order;
- display/layout position never becomes semantic identity.

A test confirms that reordering nonsemantic top-level collections does not change canonical Nyason output.

## Mechanical test evidence

The current tests verify:

1. canonical Nyason round-trip of the complete representative Milestone 0 graph;
2. canonical output independence from nonsemantic top-level collection ordering;
3. unsupported schema identity rejection;
4. unknown-field rejection;
5. duplicate-field rejection during syntax parsing;
6. typed stable-ID domain mismatch rejection before graph validation;
7. malformed syntax rejection without best-effort recovery;
8. exact round-trip of escaped opaque stable-ID payloads;
9. the corrected Milestone 0 semantic scenario tests remain green alongside Nyason.

This is a **mechanical** reliability gate. It does not yet measure LLM comprehension or mutation error rates.

## JSON size comparison

The same representative Milestone 0 snapshot was serialized through strict JSON schema 4 and Nyason v0 in CI.

Observed UTF-8 sizes:

```text
JSON:    8,440 bytes
Nyason: 16,205 bytes
ratio:   1.920 Nyason / JSON
```

The current Nyason output is therefore about **92% larger** than the compact JSON baseline for a full snapshot.

This is a real cost and should not be hidden. It is also not a rejection criterion by itself because:

- byte count is lower priority than semantic edit reliability;
- the current Nyason renderer intentionally uses self-describing tags, field names, newlines, and indentation;
- normal AI work is expected to use focused semantic queries/slices rather than repeatedly ingesting a complete persisted snapshot;
- no AI comprehension/editing comparison has been run yet.

Nevertheless, a near-2x full-snapshot cost is large enough that Nyason should not be promoted to canonical persistence solely on its mechanical benefits.

## Current assessment

Nyason v0 has demonstrated useful properties that strict JSON only obtains through additional schema/decoder conventions:

- semantic alternatives are grammar-visible tagged shapes;
- stable-ID domains are visible in the data and checked early;
- `Option`/`Result` malformed arity is excluded by shape;
- unknown and duplicate fields fail strictly;
- local constructs remain self-describing without distant table headers;
- canonical ordering is explicit rather than accidental.

The current tradeoff is substantial representation overhead.

Therefore the correct current status is:

> **Nyason remains a serious candidate and passes the mechanical gate, but Q-001 remains open.**

## Next evaluation gate

Before selecting Nyason as canonical persistence, compare JSON and Nyason on AI-oriented tasks using the same semantic fixture and focused slices.

The most useful tests are:

1. identify node kind, type, target, ownership, and effect from a bounded slice;
2. detect an intentionally wrong stable-ID domain;
3. repair a missing/incorrect field without changing unrelated structure;
4. update a direct-call argument association by stable parameter identity;
5. reason about `Result`/union branch structure;
6. compare full snapshot versus focused-slice context cost;
7. measure whether a less whitespace-heavy Nyason transport projection preserves the same AI reliability.

Token/byte cost should be reported, but the deciding metric remains semantic comprehension and edit reliability.

## Next architecture work

Nyason does not block Typed Semantic IR. The semantic model and the representative Milestone 0 fixture are now stable enough that Typed Semantic IR can proceed in parallel or immediately after the AI-format comparison.

The IR must consume validated graph facts, not Nyason text, and must not inherit persistence-only choices such as field spelling, record tags, or indentation.
