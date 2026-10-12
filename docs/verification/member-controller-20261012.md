# Serialized Member controller — raw commit fault recovery, 2026-10-12

This record describes the pending ExceptionCatch feature commit. The subsequent
local [raw save ledger probe](raw-save-ledger-20261012.md) extends its evidence to
38 scenarios / 537 request observations without changing core behavior.

All **34 scenarios / 469 per-request state/effect observations** now match
unchanged pinned Python in virtual execution and unedited emitted C#. The former
23rd-case raw commit mismatch is repaired with explicit generic exception
handling composed with existing shields, scopes, Results and resource release.

Work starts from PR385's verified main
`62ea3fcbcc3052138ae10e75c4a4ebf1215f2054`, tree
`094fb17ff911d4f6b1024f9cddd3168a283ab26b`, on isolated
`feat/raw-fault-cleanup`. The source checkout and separate SIWC work were not
modified. Installed dependencies were copied locally; none were installed.

## Baseline RED and source precedence

The unchanged baseline's 23-case harness passed by classifying the raw-fault
gap as expected. A required-parity assertion rejects both saved baseline sides:
Git effects stop at commit, reverse-check/reverse-apply are absent, and the owner
faults and drains its running peer before later requests can execute. Python
runs rollback, returns the raw fault to the serialized caller, and continues.
The maintained [summary](../../examples/member-controller/fixtures/verification-summary.json)
retains both baseline traces, Python's observations and artifact hashes.

The independent oracle remains rrawrlet
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f`. Before import, HEAD and working
`team.py`, `codex.py` and `runner.py` bytes are checked against pinned blobs.
`team.py` SHA-256 is
`25e6fc65c0947eb74e12425c6c74664bd0c0adc3b80114223c4eabb651a8ccf4`.
Actual Team methods, lock and await_cleanup run with synthetic external effects.

`Team.integrate` catches BaseException around commit only, awaits cleanup of
reverse-check then reverse-apply, and rethrows. `await_cleanup` retrieves the
cleanup task's result before raising pending caller cancellation. Thus:

- Successful rollback preserves the original raw commit fault; pending request
  cancellation loses to it.
- A reverse-check failure skips reverse-apply. A reverse-apply failure replaces
  the commit fault. Typed or raw rollback failure wins pending cancellation.
- A successful protected integration checks restored caller cancellation only
  after completion. HEAD/save typed errors retain their established precedence.

Python save is synchronous; the stub cancels before returning/raising. Finish
calls unchanged Team.close under actual await_cleanup. The observational
close-member wrapper invokes the original method.

## Minimal implementation

[ExceptionCatch](../specs/exception-boundary.md) takes explicit Unit body and
opaque Exception handler callbacks returning the same Result. Message access
and dispatch-preserving rethrow are its only exception-value operations. Typed
Err/Abort bypass the handler; context failures and handler rethrows propagate
normally. It adds no exception class query, host adapter or automatic fault policy.

The integration recipe catches exactly the throwing commit invocation. Its
handler runs reverse effects inside the existing CancellationShield and then
rethrows the captured original; a cleanup Err becomes a terminal Outcome, so
rollback is not attempted twice. The request worker handles the raw fault after
the protected tail unwinds. The owner scope and peer remain live for subsequent
requests. The original real host commit throw is unchanged. Virtual raw faults
remain throws; a scripted delay permits deterministic cancellation gate placement.

Existing TaskScope, UseResource, TaskCancel and shield implementations are
unchanged. Snapshot JSON v16 / Nyason v12 gate the new boundary/type; old graphs
retain their versions. Informational typed/execution IR version 11 identifies
exception-bearing programs. Ordinary tools, strict codecs, type/effect validation,
type-parameter rebinding, dependency analysis and the viewer cover the new nodes.

## Evidence and validation

The 22 earlier matches remain exact. Twelve raw-fault scripts cover original
failure, cancellation at commit/either reverse phase, typed/raw failures in both
reverse phases with/without pending cancellation, explicit peer inspection,
subsequent integration/wait requests and finish/release. Every request records
its Result/rejection, nine normalized Member fields and cumulative ordered
Git-effect/payload trace. Reverse effects occur once per failed commit; no HEAD
or save occurs after failed commit. Each raw script keeps the peer running until
explicit stop; owner finish releases once after drainage.

Fresh ordinary authoring, reopen, same-snapshot replay, copied-directory replay
and real compilation pass. The saved graph has **87 functions / 1,925 nodes**;
no graph JSON or emitted C# was edited. Virtual and real paths share one C#
emitter/runtime; independent language evidence is unchanged Python, not their
mutual agreement. Fixed virtual delays and held real/Python gates avoid arbitrary
wall-time race comparisons. Eight sensitivity controls are normalized-record
mutations, not compiled mutants.

The [generic fixture](../../examples/exception-boundary/README.md) adds four
virtual cases and thirteen unedited C# controls: exception identity/throwing
stack including OCE, typed abort bypass, context-fault bypass, nested token
restoration, outer release (including release failure), held nested drainage,
rollback fault precedence and repeated graph TaskCancel without parent cancellation.
Five focused contract tests cover canonical persistence, schema gating, validation,
external-boundary/equality restrictions, generic rebinding and both callback effects.

| Check | Result |
| --- | --- |
| Release solution build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| npm adapter / Node / MCP | 8 / 40 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Suite partition / whitespace | Disjoint/exhaustive / pass |
| Default lifecycle / integration | 15 / 36 scenarios, virtual and real C# |
| Controller independent oracle | 34 / 469 request audits, both scheduler paths |

Aggregate checking caught exact-surface test lists needing the new nodes, a
missing generic rethrow type rebinding, a dependency symlink path issue, and
transport request IDs incorrectly compared across replay. These were fixed;
no semantic IDs, snapshot bytes, source or symbols were normalized away.
Sandbox Node child waits/VSTest sockets required approved local execution.

| Final artifact | SHA-256 |
| --- | --- |
| Saved graph | `0a6e9238f58f9c5e514cfcbf32f5fd9b6c919acaa021c61f433f6574f815d7cf` |
| Unedited emitted C# | `d85b0efbc80a7820b19e7669cc75a12552241fed923845ecfba57e7eca5c9be4` |
| Symbols | `be93db744fb56b2d5d08c34f3ca43555398e06e01da2ed9d449517e167e23cee` |
| Independent expected fixture | `a120fbb9b72a1d8547722bf69b474e1bd2019a03355a9833b11e56b6de9ecc53` |

[Reproduction commands](../../examples/member-controller/README.md) and the
maintained summary preserve source proofs, baseline/final traces and hashes.
Fresh authoring allocates new IDs; saved-bundle replay preserves them.

## Limits and next bounded probe

The next bounded probe is **raw save exception ledger retention**, outside this
matrix. This repair does not claim all Python BaseException classes or all
exceptions at every integration stage are equivalent. Generic OCE recovery is
explicit graph policy; there is no automatic cancellation classification.

The proof covers bounded serialized requests, not concurrent admission/lock
parity, cancellation of the entire controller's external parent, unbounded
scaling, real provider shutdown, physical Git/index rollback or restart recovery.
Shields cannot force non-cooperative effects to finish. Save-error retention is
in memory. No provider, account, credentials, physical integration Git mutation
or Library upload ran. Authorized branch/draft-PR publication is separate from
these mocked effects; review and merge remain with the parent.
