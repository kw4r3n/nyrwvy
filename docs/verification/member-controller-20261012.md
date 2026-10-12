# Serialized Member lifecycle and integration — 2026-10-12

Existing graph operations express a serialized offline controller with live
settlement-based eligibility, save-error ledger retention, independently
cancellable observation and shielded finish/rollback. Twenty-two mixed scenarios
and **289 per-request state/effect observations** match unchanged pinned Python
in virtual execution and unedited emitted C#. No core/runtime/dependency change.
A raw host exception remains a demonstrated fidelity gap.

Work started on isolated `feat/offline-controller-composition` at verified main
`c7bf1a57345e19be72f284409143463381897586`, tree
`e1f984651242acec70bc5a06e9c64fbe0412edb8`, after PR383/384 were merged.
The original checkout stayed clean at
`90e495c75dd6915440ea22c96222aefcf04f723e`; the SIWC checkout was untouched.

The oracle is rrawrlet `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`.
Before import, the harness checks working bytes against its pinned `team.py`,
`codex.py` and `runner.py` blobs. `team.py` SHA-256 remains
`25e6fc65c0947eb74e12425c6c74664bd0c0adc3b80114223c4eabb651a8ccf4`.
Actual Team methods/lock and `await_cleanup` run; external effects are synthetic.
An observational close-member wrapper invokes the unchanged method; a gated
finish callback invokes unchanged `Team.close` inside actual `await_cleanup`.
Pinned runner uses that same cleanup ownership pattern. Python save is synchronous:
its stub requests cancellation before returning/raising, not during real disk I/O.

## Demonstrated gap and adjustment

Composition exposed an omitted eligibility condition: Python permits continuation
only from a terminal **unintegrated** Member. The lifecycle recipe previously had
no integrated ledger field. A saved pre-guard controller reproduces `spawn:3` in
both virtual execution and its unedited C#, versus Python `reject:integrated`
after integration/save error. The compact summary records that baseline and its
artifact hashes. This is a pre-adjustment composed recipe, not an unchanged-main
compiler negative proof.

The composed Entry carries HEAD, and the continuation recipe now rejects a
nonempty ledger while preserving closing/capacity/latest/discarded precedence.
Eligibility reads one task snapshot, avoiding repeated unordered observations.
Integration outcomes carry both candidate and Result; the serialized owner updates
its ledger on success, cancellation or save Err. Retried integrated requests
perform no further Git/save effects. Policy is graph-owned; hosts provide no
eligibility facts, registry or task handles.

The reusable lifecycle recipe gains optional authoring composition hooks; the
integration definitions are factored for reuse. Default lifecycle/integration
behavior still passes its existing 15 / 36 scenarios in virtual and real C#.
Historical frozen graphs are unchanged.

## Mixed-sequence evidence

| Boundary | Observed behavior |
| --- | --- |
| Snapshot/checks held after determined completion | Integration rejects running; capacity remains occupied |
| Observer cancelled while two Members remain owned | Caller returns cancelled; Member token remains clear; later wait succeeds |
| Nonempty/empty patch save Err, then repeated requests | First save error retained; captured HEAD survives; no repeated effects |
| Request cancellation in apply/commit/HEAD/save | Protected tail completes; ledger retained; cancellation then returned |
| Commit failure and cancellation during either reverse phase | Rollback completes; original commit or reverse error wins |
| HEAD/save errors with pending cancellation | Typed fault wins; ledger stays empty for HEAD error, survives save error |
| Continuation/supersession/discard | New attempt reuses workspace; old/latest/discarded eligibility and one discard preserved |
| Finish cancelled with snapshot held and peer running | Pending until drainage; checks not started are skipped; cancellation recorded; close once |
| Requests after finish/resource release | Spawn rejects closing; settled handles/ledger remain readable; eligible integration stays idempotent |
| Expected provider failure | Failed Member stays isolated; integration rejects status |

Every completed script records each request's Result/rejection and nine normalized
observable Member fields in a state audit: id, status, workspace, base, previous,
discarded, commit, checks and integrated, with the ordered cumulative
Git-effect/payload trace.
Per-attempt provider/close/snapshot/check effects occur at most once; explicit
discard and service close occur once. Finish acknowledgement follows release.
Request cancellation is applied twice while a selected gate is held; zero-limit
poll proves the request remains pending before release. Finish also exposes its
cancelled outcome separately from its post-release acknowledgement.

Virtual fixtures use fixed scripted delays; real C# and Python use held events.
Both Nyrwvy scheduler paths share the **same emitter/runtime**. Independent evidence
comes from unchanged Python, not from treating virtual/real agreement as two
language implementations. Transient release states are stabilized by explicit
handle joins. No arbitrary cross-worker interleaving or wall-time race is compared.

## Verification and artifacts

Fresh authoring, reopen, same-snapshot replay, copied-directory replay and real
unedited C# each pass all 23 fixture assertions: 22 oracle matches plus the
explicit raw-fault classification. Copying changed directory, not filename.
Snapshot bytes, source and symbols remained equal. Ordinary proposals authored
all 82 functions / 1,881 nodes; neither graph JSON nor emitted C# was edited.
Eight comparison mutations reject lost ledger, hidden save Err, duplicate save,
invalid continuation, observer peer cancellation, early settlement, wrong rollback
precedence and lost finish cancellation. They are not compiled mutants.

| Artifact | SHA-256 |
| --- | --- |
| Saved graph | `5d58fee3a448da532ecb5057cb1c1d0605a09f18c989fa172631566f4e72955e` |
| Unedited emitted C# | `a6a06ac033a2274abead68aa6c133556275da21d214b259c654be0591b820e7c` |
| Symbols | `d9bfa7cbc294a7c402fd566a04f3da299a70eeeddd63da21cd35a18a3bb91436` |
| Independent expected fixture | `51f4e517ffed0e5f593a72a557916249ace0976d7da9d063a7e501d41e1b03de` |

The [example](../../examples/member-controller/README.md) contains reproduction
commands. Its [summary](../../examples/member-controller/fixtures/verification-summary.json)
contains source proofs, per-side baseline/gap traces and tooling evidence.
Fresh authoring allocates new IDs; replay of a saved bundle preserves its hashes.
Local tools: Nushell 0.116.1, Node 26.11.1, Python 3.14.7, Bun 1.4.3,
SDK 10.0.112 and runtime/reference pack 10.0.12 (167 installed references).

| Local check | Result |
| --- | --- |
| Release solution build | Zero warnings/errors |
| .NET fast / extended | 855 / 154 (123 Core + 31 Extended) |
| npm adapter / Node-script / MCP | 8 / 39 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Suite partition / fish / whitespace | Pass |
| Controller ordinary-authoring regression | Fresh/replay/C#/independent fixture pass |
| Default lifecycle / integration regressions | 15 / 36 scenarios, virtual and C# |


Initial authoring rejected an incorrectly scoped patch binding and unordered
eligibility operands; both recipes were corrected. Empty-patch cancellation
gates and oracle eligibility normalization were corrected. A pre-fix lineage
script blocked after Python rejected the invalid continuation; it is not counted
as a pass. Sandbox Node child waits/VSTest sockets required approved local
execution. A sandbox suite-partition attempt failed; approved discovery passed.
No cloud environment, dependency installation or workflow dispatch was used.

## Remaining gap and limits

A raw commit host exception triggers Python's `except BaseException` rollback;
Nyrwvy's typed Result recipe cannot catch it. Virtual and real C# both omit the
reverse effects, fault the owner and cancel its peer; Python rolls back and
continues the serialized script. The twenty-third fixture keeps both sides and
requires this mismatch to remain visible. The next real gap is an explicit generic
fault boundary that can preserve raw-fault/rollback/drainage precedence. No silent
host exception translation or rrawrlet builtin was added.

This proves bounded serialized request-task composition, not concurrent admission
or lock equivalence, external-parent cancellation of the entire controller,
unbounded scaling, provider shutdown, process cleanup, physical Git/index rollback,
or filesystem recovery after restart. Existing scope parent links remain
unchanged; a shield cannot force non-cooperative effects to finish. Save-error
retention is in memory. Full exception parity remains unimplemented. No actual
provider, account, integration Git mutation, credentials or Library upload was used.
