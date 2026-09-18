# M2 structural contract coverage extension — 2026-08-30

**Status:** Observed result

This record extends the first M2 Float64 structural-editing verification with the focused contract-edge coverage tracked in issue #48 and merged in PR #55.

It is deterministic implementation/regression evidence, not a controlled model-evaluation result.

## Exact verification

- Repository: `kw4r3n/nyrwvy-lang`
- Issue: #48
- PR: #55
- Exact verified head: `6cb432bef4f0764c6bb18d648a5dd547e3051b2d`
- Merge commit preserving that verified head as a parent: `858677424ba58f01ef0be3d206897c1eea94924b`
- Branch: `test/issue-48-structural-contract-coverage`
- .NET SDK: `10.0.111`
- Verification time: `2026-08-30T06:25:45+09:00`

Command:

```shell
bash scripts/verify-local-20260825-1427.sh --clean
```

Result:

```text
Release build:
  0 warnings
  0 errors

Full test suite:
  passed: 197
  failed: 0
  skipped: 0

VERIFICATION PASSED
```

All existing M0/M1 focused verification phases also passed unchanged.

## Added contract evidence

The focused coverage verifies:

- unknown structural target rejection;
- non-`Float64` target rejection;
- accepted recipe depth 32 and rejection at depth 33;
- the largest representable node count under the current binary recipe grammar within the 64-node limit (63) and first overflow (65);
- exact finite, negative-zero, infinity, and canonical-NaN `Float64Bits` through structural materialization and publication;
- strict JSON and Nyason snapshot round-trips for committed structural results;
- snapshot and semantic fingerprint stability across those round-trips;
- caller, target signature, declaration collections, obligations, and unrelated functions remain unchanged;
- removed structural-node summaries are stable-ID ordered;
- added structural-node summaries preserve materialization order;
- reflection-level structural request/recipe authority remains limited to guarded existing IDs and the closed `Parameter` / `Literal` / `Add` recipe, with no raw graph/snapshot payload.

## Relationship to the original implementation record

The original implementation record remains authoritative for the first end-to-end implementation head and its 192-test clean verification:

- [`m2-float64-structural-editing-20260830.md`](m2-float64-structural-editing-20260830.md)

This record does not replace it. It closes the additional focused coverage gaps identified after that implementation was accepted.

## Remaining M2 evidence gate

With #44 and #48 complete, the deterministic structural implementation contract is covered. The next distinct evidence gate is the versioned structural agent-evaluation protocol (#49), followed by the frozen M2 corpus (#50) and controlled model evaluation (#51).
