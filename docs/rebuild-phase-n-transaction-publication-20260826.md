# Verified Phase N — Transaction Publication

## Scope

Phase N completes the Milestone 0 transaction action boundary on top of the existing immutable semantic transaction simulator.

The new workspace layer provides three explicit actions:

- `simulate` — derive a proposed snapshot, semantic diff, obligations, and semantic readiness without publishing;
- `commit` — conservatively publish a semantically ready proposal only;
- `reject` — explicitly retain the exact current workspace state.

Publishing an incomplete snapshot requires the deliberately louder `commitWithPolicy ... AllowBlockingObligations` path.

## Authority boundary

`WorkspaceState` has a private record representation. Callers cannot construct or mutate the current snapshot/history directly through record syntax.

The authoritative state is one `CurrentSnapshot`; prior snapshots are retained only as immutable history. Snapshot IDs are not stored in parallel to the corresponding `Snapshot` records.

`commit` does not trust a cached preview. It re-runs the same `TransactionRequest` against the current authoritative snapshot and publishes the resulting proposed snapshot only if the selected commit policy permits its readiness state.

This preserves one semantic implementation of the requested change: `Transaction.simulate` remains the operation engine used by both preview and publication.

## Readiness terminology

The workspace layer reports:

- `SemanticallyReady`
- `Incomplete of ObligationId list`

It intentionally does not call this state `Executable`: backend binding readiness is a later concern and is not evaluated by the workspace publication layer.

## Conservative incomplete-commit policy

The normal API:

```fsharp
SemanticWorkspace.commit workspace request
```

rejects blocking obligations.

Publishing a structurally valid but incomplete snapshot requires:

```fsharp
SemanticWorkspace.commitWithPolicy
    workspace
    request
    CommitPolicy.AllowBlockingObligations
```

This makes incomplete publication explicit rather than a caller-default accident.

## Verification evidence

The exact verified code head was:

```text
c134005ef1c95e03e1e138b69e34d69ecd4822a6
```

Local verification was run on 2026-08-26 at `00:47:49 +09:00` from a clean `bin/obj` state with .NET SDK `10.0.111`.

Release build result:

```text
0 Warning(s)
0 Error(s)
```

Full suite:

```text
Passed: 92
Failed: 0
Skipped: 0
Total: 92
```

Focused Phase N suite:

```text
WorkspaceTests
Passed: 7
Failed: 0
Total: 7
```

The uploaded TRX evidence records `executed = total`, with `failed = 0`, `error = 0`, `aborted = 0`, and `notExecuted = 0` for every focused suite.

The seven workspace tests cover:

1. preview does not publish and reports semantic readiness;
2. commit publishes the same proposed snapshot/diff/readiness derived by preview;
3. explicit reject preserves the exact workspace;
4. stale-base publication is rejected;
5. blocking obligations are rejected by the conservative default and require explicit opt-in;
6. resolving a published obligation restores semantic readiness;
7. a previously published snapshot identity cannot be reused.

## Milestone consequence

Milestone 0 now has an implemented `simulate / commit / reject` publication boundary. The next high-value gate is the required reproducible end-to-end scenario that runs the complete lifecycle — persistence, focus projection, execution, rename publication, incomplete signature evolution, obligation review, explicit incomplete publication, lowering block, resolution, and final execution — as one integrated proof.