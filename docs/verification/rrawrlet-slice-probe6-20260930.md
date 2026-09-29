# rrawrlet slice probe 6 — 2026-09-30

**Status:** Complete. A new, stateful part of rrawrlet, the snapshot read path of `LeaderGuard` (`read_member` with `begin_read`, `reserve_lines`, `deny` and `read_result`), was authored by name only. It matched the real Python on 16/16 request sequences: every reply, the final budget state, and the exact order and arguments of every audit and Git call. There were no recipe retries. The guard's state (per-file and total line budgets, the call count and the denial count) was threaded explicitly through an effectful fold, which cost little and needed no stateful capability implementation. Cross-caller abort propagation cost two `matchResult` nodes. The largest cost was one 133-node function with up to seven nested frames and pattern references up to depth 6.

**Date:** 2026-09-30

Baseline: nyrwvy `84d1dab` (main after #257, clean); rrawrlet `72847cd` (`leader_guard.py` and `team.py` clean). The session server was built once with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release -o <scratchpad>/probe7/bin`. The oracle ran rrawrlet's real `LeaderGuard.read_member`, `Team.get` and `Team.latest` with rrawrlet's own `.venv` (Python 3.14.3), against a fake team whose `host_git` serves `ls-tree`/`cat-file` from an in-memory object store and records its calls. The audit file was read back after each sequence.

The method was probe 5's: the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol with `simulateCreateTypeByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `commit` and `runPureByName`, and no `declarations` call. Probe 5's client (`nw.py`) was reused with helpers for effects, capabilities, `matchResult`, `abort` and Unions. Scripts, cases and transcripts are kept outside the repository in the session scratchpad `probe7/`.

## Why this part

The [semantic-independence](../concepts/semantic-independence.md) gaps that were still open wait for a consumer: stateful/effectful program capability implementations, abort propagation through callers, and runtime state identity. Every earlier probe ported stateless code or a loop whose state lived in a host fake. `LeaderGuard` is the first rrawrlet part whose behaviour is its mutable state: an object with four counters that every tool call reads and changes, where a denied call still changes the state (the call count before the check, the denial count on the way out) and the third denial turns into a policy violation that ends the Leader's turn.

## What was ported

| Python | Port |
| --- | --- |
| `LeaderGuard` fields `read_counts`, `total_lines`, `read_calls`, `denials` | `Guard` Product; `read_counts` (a dict in insertion order) is `List<Count>` |
| `native_codex` dispatch: `ToolPermissionDenied` and `ValueError` become results, `ToolPolicyViolation` ends the turn | `runSession`: `listEffectFold` over the requests with `sessionStep : Result<Outcome, Outcome>`; a `Violation` reply is `Err`, which stops the fold |
| `deny` (audit, count, raise one of two exceptions) | `deny : Step`, returning the new `Guard` and a `Denied` or `Violation` reply |
| `begin_read`, `reserve_lines` (raise through `read_member`) | `beginRead`, `reserveLines : Result<Guard, Step>` that `abort` with the denial's `Step` |
| `member` (`Team.get`, completion and `Team.latest` checks) | `memberCommit : Result<String, String>` over `Members.lookup(runId) : Option<Attempt>` |
| `host_git` `ls-tree` and `cat-file` | `Git.lsTree(commit, path)`, `Git.catBlob(blob)` |
| `audit` (JSON lines appended to `leader-policy.jsonl`) | `Audit.denied(reason)`, `Audit.read(path, startLine, lineCount, reason, runId, commit)` |
| `Path(path)` checks and `as_posix()` key | `listFilter(stringSplit(path, "/"), keepPart)`, `"."` for no parts, `stringJoin` |
| `metadata.split()` mode/kind check | `stringSplit` on the part before the tab, `valueEqual` against the two allowed prefixes |
| `splitlines()`, `enumerate`, `line.rstrip()` | `stringSplitLines` with Python's break set, `listSlice`, a numbering fold, `stringSpanBack` |

Out of scope: `read` (worktree reads with filesystem `resolve()` and symlink escapes), `review` and `verify`. The typed `Request` makes Python's `isinstance` checks unrepresentable, so those three denial messages were not ported. `Attempt.commit` is a String with `""` for Python's `None`, since the guard only tests truthiness.

## What was authored

| Part | Declarations | Functions / nodes |
| --- | --- | --- |
| Types, effect, capabilities | 8 types (`Count`, `Guard`, `Request`, `Attempt`, `Reply` with 4 variants, `Step`, `Numbered`, `Outcome`), effect `host`, capabilities `Members`, `Git`, `Audit` | — |
| Pure helpers | — | 13 / 217 (`beginDenial` 37, `snapshotBlob` 36, `reserveDenial` 30, `addLines` 25) |
| Effectful | — | 7 / 280 (`readMember` 133, `memberCommit` 37, `deny` 29, `sessionStep` 28, `beginRead` 23) |
| **Total** | 8 types, 1 effect, 3 capabilities | **20 / 497**; 64 requests / 46.9 KB / 59.3 KB |

There were **no retries**: every simulation and commit succeeded on the first request, including the `scopeDepth` of all 27 pattern references in `readMember`.

## Results vs Python

The cases are 16 request sequences (76 requests, of which 73 ran before a violation ended their sequence) against one object store and nine attempts. Each run compared the returned `Outcome` (all replies and the final `Guard`) as `expected`, and separately compared the run's `Audit` and `Git` call logs with the Python audit lines and recorded `host_git` calls.

| Group | What it covers | Result |
| --- | --- | --- |
| G1-G4 | A read; the 160-line per-file budget; the 640-line total budget; the 16-call budget, then two denials and a violation that drops the 20th request | **4/4** |
| G5-G7 | Blank reasons (`"　"` is blank, `"​"` is not, `"\x1c\x85"` is), range checks, absolute, `..` and `.git` paths; each ends in a violation on the third denial | **3/3** |
| G8, G15 | `./a.txt`, `a//b.txt`, `a/./b.txt/` share the `a.txt`/`a/b.txt` budget; `""` becomes `.`; the key ignores the run, so two Members share one file budget | **2/2** |
| G9-G11 | Leader, unknown, running, commitless, superseded, discarded and empty-commit Members; executable, symlink, tree and gitlink entries; a missing file | **3/3** |
| G12 | Text over 16,000 characters is denied *after* the lines were reserved, so the next read shows 70 remaining file lines | **1/1** |
| G13-G14 | `\r\n`, `\x0c`, ` `, `\x85`, `\x1c` breaks, trailing `　`/`\xa0` stripped, a start beyond the end (empty text, lines still reserved) | **2/2** |
| G16 | Denied, failed, denied, violation; the fifth request is never processed | **1/1** |
| Call logs | `Audit.denied`/`Audit.read` and `Git.lsTree`/`Git.catBlob` order and arguments | **16/16** |

A negative control (one expected `remaining_read_calls` changed) reported a mismatch at `Outcome.replies[0]`. 16 runs took 30.1 s, about 1.9 s each, which is mostly the per-run build. The run requests total 302 KB, mainly the `catBlob` responses carrying file contents.

## Findings on the deferred semantics

**Stateful capability implementations: not needed here.** The guard is the kind of object a stateful capability would model, but explicit threading was cheap. The state lives in one `Guard` value that `deny`, `beginRead` and `addLines` rebuild (9 copied fields, 18 nodes), each result carries the version it produced (`Step`, and the `Err` payloads), and the fold's accumulator owns it between calls. Because the final state is an ordinary return value, the run compared it directly; the Python oracle had to read the object's attributes. The one real hazard is that several versions of the guard are in scope at once (`g`, `g1` after the call count, `g2` after the reservation), and choosing an older one type-checks. G12 is the case that exposes the wrong choice in the text-limit denial; that the authoring surface would not have caught it is an inference, not an observed failure.

**Abort propagation through callers: small cost.** Python raises from `begin_read`/`reserve_lines` through `read_member` to the dispatcher. The port's helpers abort with `Err(Step)`, and `readMember` re-raises each with `matchResult(…, abort(error))`: 2 propagations, 6 nodes. `sessionStep` then collapses `Result<Step, Step>` with one more `matchResult`. The abort payload has to be the updated state, not an error message, because a denial changes the counters; any non-local abort feature would need to carry that too.

**Runtime state identity: not needed.** The guard's identity is its place in the fold; nothing compares or stores guard instances.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Deep positional scopes in one long function** | `readMember` is 133 nodes with up to seven nested frames and 27 pattern references up to depth 6. It was right the first time only because the script annotated every frame (`# text0 g2 1 blob2 …`), and the same guard version `g1` is depth 1, 2, 4 or 5 depending on the frame. Probe 3's `scopeDepth` retry and probe 5's are the same cost. | None queued. A named-slot reference would remove the counting, but no retry happened here, and splitting the function is available today. Revisit if a probe records another `scopeDepth` retry. |
| 2 | **Product update restates every field** | 3 guard updates copy 9 unchanged fields (18 nodes, 4% of the port). | None queued; small. |
| 3 | **Several state versions in scope** | See above. | None queued; the tests caught the class of error. |
| 4 | **A sink operation needs a pre-sized fake queue** | `Audit` returns `Unit`, but ordered fakes need one response per call, so the harness supplied 40 per operation. | None queued; harness-only. |

Nothing blocks this slice, so nothing is queued.
