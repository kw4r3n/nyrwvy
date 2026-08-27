# F# Milestone 0 rebuild — Phase M viewer artifact export — 2026-08-25

## Status

**Verified through a clean local compiler/test boundary.**

Exact verified code head:

```text
4424026ec89103651cbd598988f888f32f481dfa
```

Verification:

```text
.NET SDK: 10.0.111
clean: bin/ and obj/ removed before restore
restore: success
build: success, 0 warnings, 0 errors
full suite: 85 passed, 0 failed, 0 skipped
ViewerArtifactTests: 6 passed, 0 failed, 0 skipped
```

The focused TRX bundle also recorded `executed = total` and zero failed/error/aborted/notExecuted tests for all Phase I–M focused suites.

## What Phase M establishes

Phase M makes the verified Phase L viewer usable as a deterministic exported artifact without turning files or HTML into semantic state.

The core path is:

```text
Snapshot JSON or Nyason
  -> strict snapshot decoder
  -> stable FunctionId
  -> SemanticQuery.focusFunction
  -> ViewerProjection.focus
  -> ViewerHtml.renderFocusDocument
  -> ViewerArtifact
```

Change Review export starts from an existing `TransactionSimulation`; it does not reconstruct a transaction diff from before/after snapshots.

## Stable artifact identity

Suggested filenames are derived from the raw stable ID bytes encoded as UTF-8 hex. Display names never participate in artifact identity.

This keeps rename behavior clean: a display rename changes visible content but not the artifact's stable semantic selector or suggested filename.

## Source encoding independence

Equivalent JSON and Nyason snapshots produce byte-identical Focus viewer artifacts for the same FunctionId. The encoding is therefore an input transport concern, not a viewer semantic authority.

## CLI boundary

A thin package-free .NET CLI is provided at:

```text
tools/NyamNyam.ViewerExport
```

The first command is intentionally small:

```text
focus --input <snapshot-path> --format <json|nyason> --function-id <id> [--output <path>]
```

The CLI:

- resolves the target only through `FunctionId`;
- does not search display names;
- rejects unknown options;
- rejects duplicate options;
- rejects positional extras;
- rejects options with missing values;
- writes UTF-8 without BOM;
- does not own or mutate semantic state.

Change Review remains available through the Core artifact API because its authoritative input is a `TransactionSimulation`, not two arbitrary snapshot files.

## Integration evidence

The Phase M integration test starts the compiled CLI as a child process, loads a real representative snapshot JSON file, selects `main` by stable FunctionId, writes an HTML artifact, and verifies the resulting document and reported output path.

The same test module also proves:

- JSON/Nyason source equivalence;
- display-name-independent artifact filenames;
- explicit invalid stable-ID failure;
- deterministic Change Review artifacts;
- display-name lookup rejection.

## Boundary retained

Phase M does not make HTML authoritative. The hierarchy remains:

```text
Semantic graph
  -> query projection
  -> viewer model
  -> HTML artifact
  -> filesystem output
```

No upstream layer reads generated HTML or filesystem naming to recover semantics.

## Next gate

The next Milestone 0 gap is transaction publication. `Transaction.simulate` exists, but the milestone still requires explicit `commit` and `reject` actions, conservative incomplete-commit policy, stale-current-state protection, and preview/commit equivalence.
