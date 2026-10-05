# M2 Float64 structural editing verification — 2026-08-30

**Status:** Observed result

This record captures local verification for the first Milestone 2 bounded structural-editing implementation.

It supports the current [bounded structural editing specification](../specs/bounded-structural-editing.md) and [Milestone 2 decisions](../decisions/milestone-2.md). It is implementation evidence, not a controlled model-evaluation result.

## Verified implementation

- Repository: `kw4r3n/nyrwvy-lang` (the repository's name at the time; now `kw4r3n/nyrwvy`)
- Implementation issue: #44
- Implementation PR: #46
- Exact verified implementation head: `1e1025074a87ed669366169ecc39318abf6167e7`
- Merge commit preserving that verified head as a parent: `a8d2c08bd345fa44824a94384e0e815e01149785`
- Branch used for verification: `feat/issue-44-bounded-float64-body-replacement`
- .NET SDK: `10.0.111`
- Verification time: `2026-08-30T05:50:04+09:00`

PR #45 contained the same implementation head but remained a draft because the connected GitHub ready-for-review mutation failed against the current GraphQL schema. It was closed without changing the branch. PR #46 was created from the same exact head and merged with a normal merge so the locally verified commit remains directly present in history.

GitHub-hosted Actions was unavailable because the repository/account usage limit had been reached. This record therefore uses the repository's clean local verification path as execution evidence.

## Final clean verification command

```shell
bash scripts/verify-local-20260825-1427.sh --clean
```

The script cleaned all `bin/` and `obj/` directories before restore/build/test, so the final result did not depend on stale compiled test assemblies.

## Final result

```text
branch: feat/issue-44-bounded-float64-body-replacement
head: 1e1025074a87ed669366169ecc39318abf6167e7
dotnet-sdk: 10.0.111

Release build:
  0 warnings
  0 errors

Full test suite:
  passed: 192
  failed: 0
  skipped: 0

VERIFICATION PASSED
```

The verification log path on the validating machine was:

```text
/tmp/nyrwvy-verification/local-verify-20260830-055004.log
```

The path itself is host-local and is recorded only as execution context; the durable facts are summarized here.

## Focused regression phases

All pre-existing focused verification phases passed at the exact implementation head.

| Phase | Result |
| --- | ---: |
| Phase I execution lowering | 6 / 6 |
| Phase I canonical Execution IR codec | 3 / 3 |
| Phase J C# backend | 5 / 5 |
| Phase K semantic query | 7 / 7 |
| Phase K Change Review query | 3 / 3 |
| Phase L read-only viewer | 6 / 6 |
| Phase M viewer artifact export | 6 / 6 |
| Phase N workspace publication | **8 / 8** |
| Phase O Milestone 0 lifecycle | 1 / 1 |
| M1-A stable ID allocator | 6 / 6 |
| M1-B fingerprints | 6 / 6 |
| M1-C preview receipts | 6 / 6 |
| M1-D agent workflow | 8 / 8 |
| M1-E assembly boundaries | 5 / 5 |
| M1-F Int64 | 7 / 7 |
| M1-G basic List | 7 / 7 |
| M1-H higher-order List | 7 / 7 |
| M1-I expanded program | 1 / 1 |
| M1-I post-commit execution | 3 / 3 |
| M1-J Float64 | 8 / 8 |

Phase N increased from seven to eight tests after structural publication was changed to preserve workspace history through `SemanticWorkspace.publishValidatedSnapshot`.

## Structural workflow evidence

The structural test module verifies the first accepted workflow across the following behaviors:

- materialization of `Add(Parameter(p), Literal(bits))` into a fresh reviewed body;
- fresh UUIDv7 graph-node and proposal identities;
- distinct graph nodes for repeated references to the same parameter;
- preservation of function, parameter, and unrelated semantic identities;
- removal of the old body-node set from the committed target body;
- stale structural proposal rejection after an ordinary workspace publication;
- inspected-base/result and parameter-scope/type rejection;
- separate depth and node-count recipe limits;
- rejection when either an open or resolved obligation targets the old body;
- committed-state projection through Typed Semantic IR and Execution IR;
- C# emission, generated-project build, and behavioral execution of the committed structural body.

The representative generated execution evaluates the committed replacement body corresponding to `1.5 + 2.25` and observes `3.75`.

Existing Float64 regression coverage continues to validate exact binary64 literal semantics, including signed zero and canonical NaN behavior, while the structural workflow reuses the same `Float64Bits` and semantic node kinds.

## Publication-history correction

An earlier locally verified implementation head (`54509cb4e795299557640a1056116c8cdc92bf1`) passed the Release build, 191 tests, and clean verification, but review identified one design issue: structural commit reconstructed an opaque `AgentWorkspace` from the proposed snapshot and therefore discarded `PreviousSnapshots` history.

That was corrected before final acceptance:

- `SemanticWorkspace.publishValidatedSnapshot` was introduced as an assembly-internal trusted primitive;
- it checks candidate parent/current alignment;
- rejects reuse of a previously published snapshot ID;
- re-runs full validation;
- publishes the exact candidate while retaining workspace history;
- structural commit was switched to that path;
- a focused workspace-history regression test was added.

The final clean verification at `1e102507...` is the accepted implementation evidence.

## Initial compile correction

The first attempted local build of the new test module failed because F# inferred several structurally similar fixture records as `PatternBinding` instead of `SemanticParameter`, plus one ambiguous xUnit `Assert.Equal` overload.

Those failures were test-code type-inference issues rather than semantic implementation failures. Explicit semantic record annotations and an unambiguous list comparison fixed them. The final clean verification above rebuilt from deleted `bin/obj`, confirming the correction.

## Scope of this evidence

This record establishes that the current first structural-editing implementation compiles, passes the repository regression suite, preserves existing M0/M1 verification, keeps workspace history, and executes its representative committed Float64 body end to end.

It does **not** establish agent reliability for structural editing. The following remain future M2 work:

- a versioned persistent agent-evaluation command for the structural request;
- a frozen `m2-agent-corpus-v1`;
- controlled model runs and durable run artifacts;
- a reviewed structural agent-evaluation record;
- any optional List structural extension.

No M1 evaluation result is reinterpreted by this implementation.