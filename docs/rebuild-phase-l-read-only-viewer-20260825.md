# F# Milestone 0 rebuild — Phase L read-only viewer — 2026-08-25

## Status

**Verified through a clean local compiler/test boundary because GitHub Actions capacity remained unavailable.**

The exact verified code head was:

```text
21fc62b9b088c6a17abeb8fee930be8b09229273
```

Verification environment and result:

```text
.NET SDK: 10.0.111
clean:     bin/ and obj/ removed before restore
restore:   success
build:     success, 0 warnings, 0 errors
full test: 79 passed, 0 failed, 0 skipped
ViewerTests: 6 passed, 0 failed, 0 skipped
```

The focused TRX also reports `executed = total = passed = 6` with zero failed, error, timeout, aborted, notRunnable, or notExecuted results.

The same clean run re-verified all earlier focused suites:

```text
ExecutionIrTests:        6 / 6
ExecutionIrCodecTests:   3 / 3
CSharpBackendTests:      4 / 4
SemanticQueryTests:      7 / 7
ChangeReviewQueryTests:  3 / 3
ViewerTests:              6 / 6
```

## Purpose

Phase L establishes the first human-facing read-only semantic viewer without allowing presentation code to become another semantic interpreter.

The boundary is:

```text
Snapshot
  -> Phase K semantic query projections
  -> typed ViewerModel
  -> deterministic static HTML
```

The HTML renderer does not consume `Snapshot`, `SemanticNode`, Typed Semantic IR, Execution IR, generated C#, or persistence text directly.

## Viewer model

`ViewerModel` turns the already-derived Focus / Change Review projections into presentation-specific typed records.

The model preserves:

- stable graph node identity;
- derived node type when available;
- explicit semantic node role;
- typed relations such as dependency, call, field, variant, and capability-operation relationships;
- nested structured branches;
- pattern bindings and their types;
- caller/callee information;
- referenced declaration closure;
- explicit missing declaration IDs;
- open obligations;
- Change Review readiness and the existing `SemanticDiff`.

The viewer does not create a second semantic graph. Its relationships are projections over Phase K results and the focused function graph already contained in those results.

## Structured control flow

Conditional and match nodes remain nested structured graphs in the viewer model.

Branch roles are explicit rather than positional:

- conditional true / false;
- union variant identity;
- Option.Some / Option.None;
- Result.Ok / Result.Err.

Pattern bindings are likewise explicit and retain stable binding identity, optional display name, type, and source field when applicable.

## Node relations

Relations are tagged rather than rendered from arbitrary strings:

- `DependsOn`;
- `CallsFunction`;
- `ReadsParameter`;
- `ReadsPatternBinding`;
- `InvokesCapabilityOperation`;
- `ConstructsType`;
- `AccessesField`;
- `ConstructsVariant`;
- `BranchCondition`;
- `MatchInput`.

This keeps UI relation meaning mechanically inspectable and prevents the renderer from reverse-engineering node semantics.

## Stable identity

Display names are shown for humans but never replace stable identity.

The HTML exposes stable IDs for functions, nodes, parameters, fields, variants, capabilities, capability operations, effects, pattern bindings, obligations, and transactions wherever the corresponding object is shown.

This makes the human view useful for review and debugging without turning generated labels into semantic lookup keys.

## Incomplete and invalid states

The read-only viewer intentionally remains usable for incomplete snapshots.

Open obligations are shown explicitly. Missing declarations from a Focus projection are surfaced in an unresolved-declarations section rather than silently omitted or guessed.

Change Review shows one of:

- complete;
- incomplete with blocking obligation IDs;
- invalid with validation diagnostics.

The viewer therefore supports review of work that cannot yet lower to executable IR.

## HTML safety boundary

The first renderer is intentionally static:

- no JavaScript;
- no `innerHTML`-style dynamic rendering;
- no interpretation of embedded markup in display names or literal values;
- all dynamic textual content is HTML encoded before insertion.

A viewer regression test supplies hostile display-name / literal content and proves that raw script markup is not emitted as executable HTML.

This directly addresses the XSS failure mode identified in the discarded C# foundation.

## Determinism

The viewer model canonicalizes non-semantic root node collection order by stable node identity while preserving semantic structures such as branch roles and graph result identity.

The tests require equivalent root-node reorderings to produce byte-identical HTML.

Deterministic static output makes viewer snapshots useful for review, regression testing, and later artifact export.

## What Phase L does not decide

Phase L deliberately does not add:

- an editable graph UI;
- browser-side state ownership;
- semantic operations triggered from HTML controls;
- layout coordinates stored in the semantic graph;
- interactive dependency expansion;
- approval workflow persistence;
- a web server;
- visual graph layout algorithms;
- generated-source editing.

Those should remain separate from the semantic core and can be introduced only as projection/tooling layers.

## Next gate

The next practical step is artifact/export tooling around the verified viewer projection:

1. deterministic export of representative Focus and Change Review HTML files;
2. a small command/tool surface that receives semantic IDs rather than display names;
3. generated artifacts placed outside the authoritative semantic model;
4. optional later interactive UI built on the same typed viewer model.

The viewer must continue to consume query projections rather than becoming another source of semantic facts.
