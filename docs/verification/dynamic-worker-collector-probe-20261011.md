# Dynamic worker/collector probe — 2026-10-11

**Status:** Focused verification passed. The existing `OneShot` and `TaskScope`
surface expressed the dynamically registered multi-producer graph without a
new channel, time value, yield or runtime change.

Base: `181eb040f6103b2c37900c397efd0a309b2b6d21` (main after #374).
Source and reproducible commands:
[`examples/dynamic-worker-collector/`](../../examples/dynamic-worker-collector/README.md).

## What this measures

The earlier [probe 26](task-communication-probe26-20261007.md) had one sequential
producer. The [scoped handoff example](../../examples/scoped-result-handoff/README.md)
registered two cells before its receiver started. This probe instead starts
its collector, completes a readiness handshake, and then admits two to four
workers that register and send concurrently. The host supplies work-phase I/O,
synchronization gates and observations; registration, routing, aggregation,
accepted-send decisions and shutdown remain in the graph.

Basic real-thread `OneShot` races were already tested: the existing handoff
harness has 200 completion/completion and completion/close races. The new
boundary is the multi-producer application's complete lifecycle.

## Results

| Check | Result |
| --- | --- |
| Local Release prerequisite build | Passed |
| Cloud/local source and acceptance-oracle tests | 5/5 |
| Installed-compiler helper tests | 3/3 |
| Ordinary-tool authoring | Passed; 18 functions, 573 graph nodes |
| Saved virtual-time scenarios | 8/8 |
| Reopened snapshot replay | 8/8 |
| Unedited generated C#, real runtime | 27/27 |
| Projection integrity | SHA-256 unchanged before/after |

The 27 real cases comprise normal execution with 2, 3 and 4 producers,
individual expected failure, individual cancellation, 20 Stop/send races,
parent scope exit, and a host exception. Barrier-controlled producer overlap
was checked. Both send-first and Stop-first outcomes occurred across the races.
Tests checked the accepted-event multiset exactly, per-worker order, expected
failure/cancellation isolation, settled I/O waits and exactly-once release.

Environment reported by the local verifier: .NET SDK **10.0.112**, Roslyn
**5.0.0-dev**, .NET reference pack/runtime **10.0.12**. The runtime harness
invokes the installed compiler against the installed reference pack and writes
a matching runtime configuration. Its framework-only fixture has no package
references. Two earlier `dotnet restore` attempts timed out before compilation;
they are not runtime passes. The successful path required no network access,
installation, or filesystem mount change. The timeout's underlying cause was
not established; unnecessary restore was removed from the fixture.

## Findings

1. **Multi-producer append works on the existing surface.** A successful
   `OneShotComplete` is the accepted-send linearization point. Losing writers
   inspect the winning link and retry its next cell. Each logical append
   allocates one next cell, reused across retries. A failed completion followed
   by `None` rejects rather than looping forever.
2. **Stop must share the append protocol.** A Stop entry races for the same
   cell as data. Accepted entries before it drain; attempts that encounter it
   reject. Per-worker order is preserved without specifying a cross-worker
   total order.
3. **Cancellation is not graceful drain.** A cancelled collector can see an
   empty tail before an in-flight successful append is acknowledged. The graph
   therefore retains the mailbox head and reconstructs the accepted prefix
   after `TaskScope` has cancelled/joined all children. Completed cells remain
   readable after owner closure, and the first unresolved closed tail is then
   stable. The parent-exit test deliberately delays one successful append's
   acknowledgement until cancellation and verifies its recovery.
4. **Normal and cancelled completion have different outputs.** Normal
   completion compares the live collector's aggregate with the post-settlement
   fold, then saves once. Parent exit returns a cancellation result carrying
   the accepted prefix and does not save. Expected worker errors are isolated;
   unexpected host exceptions remain scope faults and still close once.
5. **The cost is visible, not eliminated.** Append begins at the head on every
   logical event, making total traversal quadratic in event count. Retaining
   the accepted log and folding it again after settlement adds graph work.
   This bounded 2–4-worker/four-event probe establishes expressibility, not
   throughput or unbounded-stream suitability.

## Evidence

The local verifier retained its complete evidence bundle as:

- `nyrwvy-dynamic-probe-181eb040-runtime-evidence.tar.gz`
- SHA-256:
  `5e3e4ab1ba2ec852dadb28f39184beeac73f0b6de45aa4cace130d8917e29048`

The generated bundle records `collector.json`, saved `cases.json`,
`evidence.json`, `Generated.cs`, `symbols.json`, `authoring.json`, the tool
transcript, and `runtime-evidence.json`. The latter records compiler/reference
pack/runtime versions, all real-runtime success checks and projection hash
integrity. The local verifier's Japanese summary is `FINAL-REPORT.ja.md`.
This record summarizes that verifier's reported results; the evidence archive
is retained separately from repository source.

## Limits and consequence

- Parent termination is deliberate scope-body exit with child cancellation,
  not exhaustive externally injected parent-token cancellation testing.
- Host exceptions that prevent the mailbox head returning do not promise an
  accepted-prefix report; their contract is propagation and safe cleanup.
- No atomic rollback is promised if cancellation races an external save.
- Scope settlement plus watchdogs and active-I/O checks establish these tested
  lifecycles; this is not a proof for arbitrary graphs or unbounded workloads.
- Focused verification passed; the full repository test suite was not claimed
  as rerun for this additive example.
- No channel or combined wait is proposed on this result alone. If scaling
  matters, measure the head-walk and reconciliation cost on the target workload
  before choosing a language change.
