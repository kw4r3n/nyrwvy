# Project review — 2026-09-22

**Status:** Local review and verification evidence, not a new milestone or live-agent claim.

## Scope and baseline

Reviewed the maintained semantic/transaction/codec boundaries, execution IR and C# backend, Viewer, snapshot-backed AuthoringSession, ordinary authoring/MCP and evaluation transports, test ownership, CI, verification scripts, and maintained documentation entry points.

Baseline: `main` at `414e430` (`docs: record recipe reference live validation`). Existing changes in `verification-planner/transcript.jsonl` were left untouched. No Git commit or branch was created. Frozen evaluation artifacts were not rewritten or rerun.

The review prioritized reproducible correctness failures and important boundaries rather than style-only refactoring or a claim of exhaustive auditing.

## Corrections

| Boundary | Reproduced problem and correction |
| --- | --- |
| Strict snapshot JSON | Non-object tagged records and malformed surrogate escapes in names/values could leak `InvalidOperationException`. Validate object shape and translate the specific JSON decoding failures into `InvalidShape`; retain valid surrogate-pair round trips. |
| Transaction diff | Required-effect add/remove operations could cancel out but remain in the diff, permitting no-change publication. Compute net effect changes from the base and proposed requirements; keep unrelated changes intact. |
| Graph validation | Reusing each preceding node as both operands caused exponential cycle-check traversal in a valid shared DAG. Memoize nodes proven acyclic while retaining current-path cycle detection and scope diagnostics. |
| Nyason input | Unicode escape parsing accepted whitespace and trailing NUL among its four digits. Require four ASCII hexadecimal digits without broadening the grammar. |
| C# emission | An unused literal in valid Execution IR generated CS0219 under warnings-as-errors. Suppress only that warning within generated source and restore it afterward, retaining IR assignments and evaluation order. |
| Viewer HTML | Function required effects and their add/remove diff were present in models but absent from HTML. Render them in focused/related signatures and Change Review. |
| Authoring JSONL | Duplicate request/nested keys were accepted using last-value precedence. Reject duplicate object properties as `ProtocolError`, without publishing or consuming the reviewed proposal. |
| Ordinary authoring transport | Already-cancelled create could still create a snapshot; idle stdout EOF left the client usable. Check cancellation before spawning and invalidate on unexpected response-stream closure. |
| Evaluation transports | Broken stdin could raise an unhandled `EPIPE`; exit zero without a response could wait for the full timeout; M1 could discard buffered final stdout at `exit`. Handle stdin errors, settle missing-response waits, and use drained stdio `close` for termination decisions. |
| Clean verification | Recursive deletion of every `bin`/`obj` could delete installed Node package executables. Restrict cleanup to directories adjacent to .NET projects and prune dependency/metadata/output trees. |
| Suite ownership and CI | The extended filter omitted M4 provider E2E. Include it, register the new isolated script regression in fast/CI, and run evaluation transport regressions in CI with an explicit pinned plugin bootstrap. Document local-vs-CI commands. |

Regression tests were added to the corresponding existing test modules, plus `.opencode/tests/nyrwvy-evaluation-transport.test.mjs` and `scripts/tests/verify-local.test.mjs`.

## Verification performed

Environment: Linux, .NET SDK **10.0.112** / runtime **10.0.12**, Node **26.8.2**.

| Command/check | Observed result |
| --- | --- |
| `./scripts/test-fast.fish --no-restore --maxcpucount:1` | 323 F# tests and 10 Node/script tests passed. |
| `./scripts/test-extended.fish --no-restore --maxcpucount:1` | 36 tests passed, including generated C# execution and the restored M4 provider selection. |
| `node --experimental-strip-types --test .opencode/tests/nyrwvy-evaluation-transport.test.mjs` | 12 tests passed using local Node child fixtures, not live evaluation runners. |
| `npm --prefix tools/nyrwvy-mcp test` | 1 integration test passed, including lifecycle serialization and actual snapshot-lock release on client exit. |
| Focused `SnapshotCodecTests`, `AuthoringSessionTests`, and `NyasonTests` after the final malformed-property-name correction | 46 tests passed with a dependency-graph rebuild. |
| `./scripts/test-fast.fish --no-restore --no-build` after that rebuild | 323 F# tests and 10 Node/script tests passed again. |
| Actual AuthoringSession JSONL process | Duplicate root/nested keys rejected; the next valid request succeeded. |
| Bash/Fish syntax checks and `git diff --check` | Passed. |

These counts overlap; they are not a combined count of distinct tests. The cleanup regression copies the script into an isolated temporary repository with stubbed .NET commands; it does not clean the real working tree.

### Remaining validation limits

- Node 22 (the configured CI version), a clean-checkout npm installation, Windows, and machine-crash/power-loss behavior were not executed locally.
- The full clean-solution verification wrapper was not run against the real repository; relevant fast and extended entry points were used instead.
- No live model/OpenCode/UI evaluation or exhaustive input fuzzing was performed.
- Node reported the existing module-type warning associated with the ignored OpenCode-managed package manifest.
- Editor diagnostics reported **147 TypeScript errors across 13 files**, largely involving missing Node/Bun type environment configuration, plus additional diagnostics requiring a configured type-checking pass. Native type-stripping execution is not static type validation; this remains tracked in #208 rather than being described as a clean diagnostic result.

## Follow-up issues created

All items include evidence, scope, proposed direction, and acceptance criteria. They are bounded follow-ups rather than prerequisites for the verified normal workflows.

| Issue | Follow-up |
| --- | --- |
| [#201](https://github.com/kw4r3n/nyrwvy/issues/201) | Define the crash-durability guarantee of acknowledged snapshot publication. |
| [#202](https://github.com/kw4r3n/nyrwvy/issues/202) | Render existing role-aware value dependencies in HTML; downstream follow-up to #123. |
| [#203](https://github.com/kw4r3n/nyrwvy/issues/203) | Reconcile the uncompiled duplicate M0 test module without duplicating suite coverage. |
| [#204](https://github.com/kw4r3n/nyrwvy/issues/204) | Bound shutdown waits for abnormal/unresponsive authoring children. |
| [#205](https://github.com/kw4r3n/nyrwvy/issues/205) | Bind low-level reviewed receipts to ordered operation intent; the ordinary opaque-proposal path already retains the original request. |
| [#206](https://github.com/kw4r3n/nyrwvy/issues/206) | Make generated C# line endings platform-independent. |
| [#207](https://github.com/kw4r3n/nyrwvy/issues/207) | Serialize concurrent starts of evaluation-only M4 sessions. |
| [#208](https://github.com/kw4r3n/nyrwvy/issues/208) | Define reproducible TypeScript checking and dependency ownership for Node/Bun adapters. |

Existing build-time follow-up [#109](https://github.com/kw4r3n/nyrwvy/issues/109) was reviewed and not duplicated.
