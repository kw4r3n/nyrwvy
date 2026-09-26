# Typed function abort — 2026-09-26

**Status:** Implementation and deterministic verification complete.

Baseline: `a51e838` (`docs: specify typed function abort and cleanup boundaries`).
This change implements the [bounded contract](../specs/typed-function-abort.md),
including every implementation gate. The generated backend uses an explicit
function-local Err return; resource and worker boundaries receive ordinary typed
Results. There is no internal transfer exception or fabricated normal value.

## Gate evidence

The maintained tests are in
[`FunctionAbortTests.fs`](../../tests/Nyrwvy.Core.Tests/FunctionAbortTests.fs).
Its eight cases run as three fast contracts and five Extended execution cases.

| Gate | Evidence |
| --- | --- |
| 1. Exact encodings and explicit versions | JSON 11 / Nyason v7 round trips and downgrade rejection; Typed IR 8 / Execution IR 7 encode abort and abrupt completion. Old snapshots and IR retain their prior canonical encodings without Abort. Reordered storage lowers deterministically. |
| 2. Ordinary authoring, review, identity and reopen | Symbolic function creation and exact-ID targeted replacements switch between normal and abrupt graph results. Function, parameter and target node identities survive publication/reopen. Simulation leaves disk unchanged. Replacing a still-used scalar with Abort is rejected without publication. Focus, creation review and HTML expose owner, payload/type and continuation identities. Both tool hosts discover the new recipe through their shared reference. |
| 3. Early exit versus Result data | Nested Bind/Option-match abort skips later capability effects; payload effects run once. A caller explicitly matches a callee's Err and recovers, while ResultErr can be matched inside the same invocation. Generic functions and pure function-value invocation preserve their own function boundaries. Conditional, Option, Result and Union arms admit abrupt completion; all-aborting graphs have no normal merge/return, including stored graphs with an unused abrupt control node. |
| 4. Invalid graphs | Non-Result owner, wrong payload type, abrupt ordinary value uses, non-result Abort nodes, incompatible normal branch types and malformed saved payloads are rejected. Undeclared effects in a branch remain errors. Abort/effect ordering uses the same dependency-pair rule for semantic admission and lowering. |
| 5. Resource cleanup and failures | Successful acquisition/body abort/release; failed acquisition without release; body error over typed release error; successful body with typed release error; release host fault over typed body error; body host fault over release fault. Nested releases are inner-first. A program-defined release helper aborts locally without losing the body error or the surrounding release. Observable call traces prove skipped continuations and exactly-once cleanup. |
| 6. Workers, cancellation and joins | Fixed source items `first`, `second` run with either item delayed until cancellation. Release traces verify opposite completion orders, both workers' cleanup and surrounding release last. Both runs return the same lowest-index `first error`, mixing ordinary Err and abort-derived Err. A worker host fault retains precedence over typed failure. |
| 7. Saved execution and diagnostics | Common-runner execution after close/reopen, deterministic lowering, expected typed results, and runtime host-failure diagnostics with recorded capability calls. Existing receipt, stale-publication and backend contracts remain covered by the regression suites. |

## Ordinary-tool example

[`examples/function-abort/run.mjs`](../../examples/function-abort/run.mjs)
uses the same `nyrwvy_*` handlers as OpenCode and MCP. It authors symbolic
recipes, checks returned reviews, commits, inspects, closes/reopens, then calls
`runPure`. It allocates no graph identities and edits no snapshot or generated
source. The run produced:

```text
invalid input: Err(empty path); calls=none
read failure: Err(read failed); calls=open -> read -> close
success: Ok(contents); calls=open -> read -> audit -> close
```

The output directory contains the reopenable snapshot and a JSONL transcript of
the tool requests, reviews and results. See the [example instructions](../../examples/function-abort/README.md).

## Commands and results

| Command | Observed result |
| --- | --- |
| `./scripts/test-fast.fish --no-restore` | 478 F# tests and 29 Node/script tests passed. |
| `./scripts/test-extended.fish --no-restore` | 46 Extended cases in Core.Tests and 31 in Extended.Tests passed. |
| `npm run typecheck` | Adapter and Bun configurations passed. |
| `npm run test:adapters` | 8 passed, including recipe discovery in both native tool hosts. |
| `npm run test:node` | 41 Node contracts and 11 MCP tests passed. |
| `node --experimental-strip-types examples/function-abort/run.mjs` | All three expected results and operation sequences above matched; identities survived reopen. |
| Changed-document relative links and `git diff --check` | Passed. |

Counts overlap across commands and are not a combined count of distinct tests.
This is local Linux contract/integration evidence, not a new live-model trial or
Windows validation. Broader abort scopes, resumable handlers, implicit Result
propagation, preemption and timeouts remain outside the implemented contract.
