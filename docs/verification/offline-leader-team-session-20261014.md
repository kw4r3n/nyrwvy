# Offline Leader → Team → Member session

One bounded offline executable composes the maintained typed command relay,
admission, Member lifecycle, integration and cleanup through ordinary graph
authoring on base `7f4dc33a33db3c9079359b04a851dc7010f0d784`. No generic expressive
gap was found and no core/runtime/schema changes were required.

[`examples/offline-leader-team`](../../examples/offline-leader-team/README.md)
contains the authoring recipes and reproduction commands. `sessionRun` accepts
typed Leader turns. `sessionTurn` reports the current graph registry and folds
requests through the maintained admission functions. The graph owns all Member
and request handles, dispatch, admission, integration ledger, joins, close intent
and reporting. The C# host supplies synthetic provider/Git effects, effect
attribution and deterministic gates. It holds no Team registry or controller.

## Capability checklist

| Component | Status in this milestone | Evidence / next boundary |
| --- | --- | --- |
| Scripted Leader turns and progress/report feedback | Proven | Four turns, two spawns in separate turns, graph-built reports |
| Typed Request dispatch and admission supervisor relay | Reused and proven in composition | Held integration with independent wait/stop; cancelled queued spawn never admitted |
| Member default settlement and parent cancellation linkage | Reused; bounded cases proven | Completed Member 1 and joined stopped Member 2; original raw provider fault retained |
| Integration and retained HEAD | Reused and proven | First request owns every Git effect; duplicate returns the same HEAD with zero effects |
| Explicit command cancellation Result | Proven application composition | Worker-local adapter handles token-bearing OCE and pending-cell cancellation; later integration works |
| Worker/cleanup/release fault boundaries | Proven | Object/throwing stack identity; worker or cleanup fault precedes release; cleanup OCE remains raw |
| Close cutoff, request/Member drainage and publication | Proven | Late spawn rejected during held drain; all parents and supervisors joined before inspection; success after release |
| Save/reopen, moved replay and unedited projection | Proven | Exact snapshot/source/symbol/case/assembly bytes; identical contract observations |
| Wrong-boundary compiled control | Proven rejection | Wait-only catch executes; original child OCE still escapes scope; identical acceptance rejects it |
| Full historical JSON-RPC command relay | Unverified composition; reusable artifact missing | Probe 9 documented private scripts/snapshots; this session uses the maintained typed Request relay |
| Re-authored bounded JSON-RPC wire/parser/envelope/keyword relay | Proven in [separate continuation](offline-leader-jsonrpc-session-20261014.md), dependent on PR395 | Raw wire feeds the same typed session; exact outputs, validation before dispatch, correlated IDs, notification spawn, compiled negative controls; no historical/full-conformance claim |
| Whole-owner cancellation recovery and protected close intent | Reused recipe; whole-owner behavior unverified here | Existing owner/close-intent evidence remains authoritative; no new equivalence claim |
| Python retained-mutator shutdown | Separate application policy | Not identified as a missing generic primitive; not implemented by this session |
| Live providers, physical Git, budgets, restart recovery, unbounded fairness | Unverified and outside milestone | Advance with separate bounded probes only when requested |
| Generic language/runtime building block | None missing in this session | Do not infer completeness beyond this finite processing |

## Verification

The local bundle is authored, validated and committed through the ordinary tools,
saved and reopened, and emitted again with the same symbols and source. One
virtual session supplies supplementary execution evidence. Nine real compiled
controls supply the decisive gate and exception evidence. The host/test program
is generated separately from the symbol manifest; emitted C# is never edited.
Installed Roslyn/reference assemblies compile without NuGet restore or network.

The real session requires per-request effects and happens-before relations, not
a forced order between independent callers. Both cancellation forms have no
registration, workspace creation or integration effects. The chosen outcome is
observed while integration is still held. Wait and stop replies precede mutation
release. Member 2 is explicitly joined before requiring its stopped view. Save
precedes duplicate admission; only the first request mutates. Close publishes
the cutoff before drainage, so a later spawn is rejected while close remains
held. Every request worker and supervisor is explicitly joined before terminal
inspection. Exactly one resource release precedes final success publication.

The command and Member fault controls preserve original exception identity and
throwing frames against competing release faults. Cleanup ordinary/OCE faults
escape the command adapter and win competing release faults. A release OCE
escapes after successful inspection and suppresses final success publication.
The wait-only compiled placement control returns its chosen local Err, but its
child remains faulted and scope closure rethrows the original OCE.

Two useful harness/graph corrections are retained in the local exploratory
evidence: the initial provider returned before its peer entered, so an explicit
overlap gate was added; and an early terminal reply preceded resource release,
so final success publication was moved after `UseResource` succeeds. Failed
runs were not retried unchanged, expectations were not relaxed, and no sleep or
retry-until-pass loop was introduced. Worker-entry test gates respond to original
cancellation so error drainage does not depend on a watchdog timeout.

`proof.json`, `runtime-evidence.json`, `runtime-raw.jsonl` and the tool transcript
in the generated bundle support independent review. The checked-in compact
fixture records the milestone observations; fresh authoring assigns fresh
semantic IDs, while moved replay preserves the existing IDs exactly.

The Release fast-test graph built with one worker and zero errors/warnings. The
fast suite passed 864 tests. The authoring/script aggregate ran with
`--test-concurrency=1` and passed 45 tests, including a fresh session plus moved
replay, the reused admission/owner/default-settlement cases and #394's task
adapter controls. Adapter tests passed 8, Bun tests 23, and serial evaluation/MCP
transport tests 15. Typechecking, fish syntax and the maintained fish script
test passed. The compact fixture records these observed counts, not new count
gates. The source changes touch only examples, documentation and the new Node
regression; an extended backend suite was unnecessary because no runtime or
backend contract changed.

This environment required sandbox exceptions for Node's `spawnSync` (EPERM
despite successful child exit) and VSTest's local communication socket. Approved
offline execution resolved those restrictions; no network/service access or
credential configuration was changed. Node dependencies were copied from the
existing locked checkout, and NuGet restoration used the installed package
cache. No Python oracle rerun, live provider, physical integration Git operation,
credential change, push or PR is part of the executable proof.
