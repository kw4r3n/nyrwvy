# Prioritized project-review follow-ups — 2026-09-22 UTC

**Status:** Implementation and verification evidence for issues #201–#208.

Baseline: `f858559` (`fix: address project review findings`). This follows the [initial project review](project-review-20260922.md). The existing user modification to `verification-planner/transcript.jsonl` remains outside this work.

## Priority and outcome

The first priorities were publication/review integrity (#205), bounded recovery of production authoring/MCP sessions (#204), and an honest persistence guarantee (#201). The remaining projection, evaluation lifecycle, tooling, and cleanup items were addressed after those boundaries.

| Issue | Implemented outcome |
| --- | --- |
| [#205](https://github.com/kw4r3n/nyrwvy/issues/205) | An opaque in-process receipt retains the exact immutable ordered request. Requests with the same final snapshot but inserted, removed, substituted, duplicated, or reordered operations are rejected. Existing base/result/stale diagnostics, identity/fingerprint domains, and wire receipt projections are unchanged. |
| [#204](https://github.com/kw4r3n/nyrwvy/issues/204) | Authoring close uses bounded queue drain, EOF, SIGTERM, and SIGKILL/reaping waits. Failure settles pending callers without replaying mutations; ownership is not discarded before child `close`. Explicit MCP close starts its predecessor-drain deadline on receipt, so it cannot sit indefinitely behind a hung open/commit. Later reopen/inspect requests retain FIFO order and are not cancelled with predecessors. |
| [#201](https://github.com/kw4r3n/nyrwvy/issues/201) | The specification distinguishes atomic visibility and healthy-OS process-restart persistence from unsupported machine-crash/power-loss durability. The existing process-reopen test now also kills the authoring process after an acknowledged publication and verifies identities/fingerprints after reopen. No fsync/power-loss guarantee was added or inferred from that test. |
| [#202](https://github.com/kw4r3n/nyrwvy/issues/202) | Focus and Change Review HTML render the existing role-aware dependencies, preserving operand roles, List indices/multiplicity, and argument/field keys with HTML escaping. |
| [#207](https://github.com/kw4r3n/nyrwvy/issues/207) | M4 startup reserves the session before its first await. Failure cleanup retains ownership until reaping, permits retry after cleanup, and keeps other sessions independent. Restart and finalization share a stable owner/queue. |
| [#208](https://github.com/kw4r3n/nyrwvy/issues/208) | Root-owned pinned npm dependencies/lockfile and Node/Bun-scoped TypeScript configurations support clean-checkout strict checks of TS and shared/MCP JS implementations. Native Zod versions remain unchanged. Existing OpenCode-generated manifests are neither edited nor required. CI runs the checks and deterministic adapter/Bun contracts. |
| [#206](https://github.com/kw4r3n/nyrwvy/issues/206) | Generated C# uses canonical LF rather than platform newlines. Literal escaping and generated-program behavior remain covered. |
| [#203](https://github.com/kw4r3n/nyrwvy/issues/203) | Unique useful assertions from the uncompiled M0 duplicate were merged into the existing four maintained tests; the duplicate file was removed without adding another suite owner. |

## Compatibility and operational notes

- **Receipt API migration:** direct public receipt record construction/copy/pattern matching is intentionally unavailable. Retain the receipt returned by simulation and use its six existing read-only facts. This is a source-level API restriction, not a snapshot schema change or binary-compatibility claim. Ordinary graph tools still commit opaque proposal handles.
- **Persistence:** after a lost response, inspect the reopened state rather than replaying a mutation. Stronger crash durability remains a future feature requiring OS/filesystem-specific synchronization and failure evidence. See the [normative guarantee](../specs/agent-authoring-session.md#persistence-guarantee-and-failure-boundaries).
- **Timeouts:** authoring's drain/EOF/termination stages use five-second windows. Explicit MCP close also has a five-second predecessor-drain deadline. A signal being sent is not proof of reaping; an unresponsive/unreapable child retains ownership and produces an actionable error.
- **Tooling setup:** run `npm ci` from the repository root. The OpenCode package scope remains independently managed by OpenCode. See [test setup and typechecking](../../tests/README.md#adapter-toolchain-and-typechecking).
- **Node versions:** CI uses current Node 22. The runtime minimum is still 22.6.0; dependency installation recommends 22.22.2+ because a pinned plugin transitive dependency declares a newer engine requirement. The supported development setup does not depend on ignoring that requirement.
- **Generated source:** Windows output changes from CRLF to LF; semantic identities and snapshots are unaffected.

## Validation

Local integrated verification used Linux, .NET SDK 10.0.112 / runtime 10.0.12, Node 26.8.2, and Bun 1.4.2.

| Check | Observed result |
| --- | --- |
| `./scripts/test-fast.fish --no-restore --maxcpucount:1` | 337 F# tests and 28 Node/script tests passed. |
| `./scripts/test-extended.fish --no-restore --maxcpucount:1` | 36 tests passed, including generated C# and provider-backed execution. |
| `npm run typecheck` | Both adapter and Bun configurations passed with no diagnostics. |
| `npm run test:adapters` | 8 tests passed, including schema/handler mismatch detection in both native Zod hosts. |
| `npm run test:bun` | 31 tests passed. |
| `npm run test:node` | 40 transport/script tests and 10 MCP tests passed. |
| Refreshed editor diagnostics | No errors or warnings in the project. |
| `git diff --check` | Passed. |

Counts overlap across commands; they are not a combined count of distinct tests. The real MCP server was exercised with abnormal local child fixtures, including explicit close behind a hung open/commit, EOF/SIGTERM shutdown, no replay, child reaping, and later reopen on the same connection. Correctness does not rely solely on shortened unit-test timers.

Clean-install tooling verification also passed in an isolated copy without existing dependencies, generated OpenCode manifests, or .NET outputs: Node 22.22.2/npm 10.8.2 and Node 22.6.0 restored the lockfile and passed the typechecking/adapter checks; the older Node install emitted the documented transitive engine warning. Bun contracts passed in the isolated setup too. This is not a Windows or live-model validation claim.

## Deferred issue

[#109](https://github.com/kw4r3n/nyrwvy/issues/109) remains deferred rather than being closed or addressed by an unrelated F# project split. The preceding successful [CI run for f858559](https://github.com/kw4r3n/nyrwvy/actions/runs/35728832969) spent approximately **45 seconds** in the fresh fast-graph build and **13 seconds** in fast tests, matching the issue's existing 44–54 second build baseline. There was no evidence of a new compiler/build regression warranting the structural changes described there.

## Limits

No live model evaluation, Windows execution, full real-tree clean, or machine-crash/power-loss test was run. Existing OpenCode-managed package metadata can still produce Node's non-fatal typeless-package warning locally; static typechecking and editor diagnostics are clean. Frozen evaluation evidence was not rewritten.
