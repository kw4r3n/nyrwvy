# Verified M1-D agent workflow facade — 2026-08-26

## Status

Verified locally and ready for squash integration into `rebuild/fsharp-m0-20260825`.

The exact code head verified was:

```text
d50c39c94a47b5ce90b176ddbe35be7ed5b6a8cb
```

Subsequent commits on the work branch are documentation-only evidence updates.

## Scope

M1-D adds the first agent-facing workflow facade over the verified semantic query, transaction, fingerprint, preview-receipt, and Change Review layers.

The facade establishes these boundaries:

- `AgentWorkspace` is opaque to ordinary callers;
- agents query semantic facts through focused typed query methods rather than raw graph traversal;
- agents submit `AgentOperationIntent`, not low-level `TransactionRequest` values;
- production IDs for newly created semantic objects, obligations, transactions, and proposed snapshots are allocated by trusted system tooling;
- `AgentChangeProposal` is opaque and retains the exact low-level request, preview, receipt, and resulting Change Review internally;
- review exposes both requested operation intent and resulting semantic change;
- publication goes through reviewed receipt verification;
- stale proposals are rejected rather than silently rebased;
- incomplete publication requires the explicit existing `AllowBlockingObligations` policy;
- rejecting a proposal leaves workspace state unchanged.

The existing lower-level semantic APIs remain available for implementation, tests, migration, and controlled tooling, but the facade is the intended ordinary agent workflow surface.

## Verification environment

The uploaded clean local verification was run at:

```text
time: 2026-08-26T15:53:43+09:00
branch: work/m1-agent-workflow-20260826-0610
head: d50c39c94a47b5ce90b176ddbe35be7ed5b6a8cb
dotnet-sdk: 10.0.111
clean: 1
```

The script removed all repository `bin/` and `obj/` directories before restore.

## Build result

Release build:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

## Full test suite

```text
Passed: 119
Failed: 0
Skipped: 0
Total: 119
```

## Focused verification

| Suite | Passed / Total |
|---|---:|
| Phase I Execution IR lowering | 6 / 6 |
| Phase I Execution IR codec | 3 / 3 |
| Phase J C# backend | 4 / 4 |
| Phase K semantic query | 7 / 7 |
| Phase K Change Review | 3 / 3 |
| Phase L viewer | 6 / 6 |
| Phase M viewer artifact/export | 6 / 6 |
| Phase N workspace publication | 7 / 7 |
| Phase O M0 lifecycle | 1 / 1 |
| M1-A production stable IDs | 6 / 6 |
| M1-B fingerprints | 6 / 6 |
| M1-C preview receipts | 6 / 6 |
| M1-D agent workflow facade | 8 / 8 |

Every focused TRX reports `total = executed = passed` and zero `failed`, `error`, `aborted`, and `notExecuted` results.

## Encoding comparison regression check

The existing comparison remains unchanged:

```text
json-bytes=8440
nyason-bytes=16205
nyason-to-json-byte-ratio=1.920
```

This is only a regression/evidence datum. Later M1 documentation explicitly strengthens Nyason as a Nayamnyam-native encoding candidate and does not treat JSON similarity or byte size alone as the deciding criteria.

## Conclusion

M1-D establishes the safe ordinary agent editing boundary required before broader language growth:

```text
query
  -> semantic operation intent
  -> simulation
  -> requested-intent + resulting-change review
  -> reviewed proposal receipt
  -> commit or reject
```

The implementation preserves the existing semantic graph as the sole authority and does not grant the agent direct mutable graph access.
