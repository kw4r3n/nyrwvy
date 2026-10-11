# Dynamic workers and a running collector

A bounded executor/collector probe using the existing `OneShot` and `TaskScope`
surface. Two to four independent workers register **after the collector has
started**, publish two progress messages and a final result, and join. Worker
failure, individual cancellation, collector Stop/send races and parent-scope
termination are separate cases. The graph owns registration, routing,
aggregation and shutdown; the host supplies work-phase I/O and test scheduling.
There is no host-side mailbox, aggregator, channel or scheduler implementation.

## Verified result — 2026-10-11

Passed on base `181eb040f6103b2c37900c397efd0a309b2b6d21`:

- Source/oracle checks: 5/5; offline compiler-helper checks: 3/3
- Ordinary-tool authoring: 18 functions, 573 graph nodes
- Saved virtual-time cases: 8/8; reopened snapshot replay: 8/8
- Unedited generated-C# real-runtime cases: 27/27, including 20 Stop races
- Both send-first and Stop-first race outcomes were observed; actual overlap
  was checked for two, three and four producers
- `Generated.cs` SHA-256 matched before and after runtime verification

The local verifier used .NET SDK 10.0.112, Roslyn 5.0.0-dev, and .NET reference
pack/runtime 10.0.12. The real checks use installed compiler/reference files;
no network, installation or mount changes were needed. The earlier restore
attempt timed out before compilation; it is not counted as runtime execution.

See [verification record](../../docs/verification/dynamic-worker-collector-probe-20261011.md)
for evidence identity, findings and limits. These focused passes are not a
claim that the repository's full test suite was rerun.

## Run and replay

Base: `181eb040f6103b2c37900c397efd0a309b2b6d21` (main after #374).
Use Node 22.6+ and .NET 10 from the normal repository setup:

```sh
npm ci
dotnet build tools/Nyrwvy.AuthoringSession --configuration Release
node examples/dynamic-worker-collector/run.mjs /path/to/empty-bundle
node examples/dynamic-worker-collector/run.mjs --replay /path/to/empty-bundle
node examples/dynamic-worker-collector/test-runtime.mjs /path/to/empty-bundle
```

The ordinary authoring tools create and commit the graph, emit `Generated.cs`
and its symbol manifest, then close/reopen the snapshot before running saved
argument-keyed fake I/O fixtures. Move the whole bundle and use `--replay` to
verify that the snapshot and fixtures stand alone. The C# harness compiles the
**unedited** emitted projection with external I/O implementations. It invokes
the installed .NET 10 SDK compiler against the installed reference pack, with
no PackageReference, NuGet restore, network dependency or SDK installation.
Compiler/reference-pack/runtime versions are recorded in the runtime evidence. Its
barrier-controlled workers really overlap on different runtime tasks. Test
watchdogs apply only to these newly started test processes.

The bundle contains `collector.json`, `cases.json`, `evidence.json`,
`Generated.cs`, `symbols.json`, `authoring.json` and `transcript.jsonl`; the
runtime runner adds `runtime-evidence.json`. Authoring failures and runtime
failures must be reported as such, not counted as a passing probe.

## Protocol and acceptance

- Each logical append allocates one next cell, then attempts
  `OneShotComplete(tail, Link)`. Success is its **accepted-send linearization
  point**. Losing writers read the winning link and retry its next cell. A
  failed completion followed by `None` returns rejected; it never spins.
- A Stop link competes in that same append loop. The collector drains the
  accepted prefix preceding Stop; sends that encounter Stop reject. Racing
  send/Stop can choose either winner. No cross-worker order is promised.
- Each worker issues its next event only after the preceding append returns.
  The collector's immutable map routes by ID, checks registration and expected
  sequence, rejects duplicate/out-of-order events, and marks final/error states
  terminal. The tests compare the exact accepted-event multiset with the graph
  report and separately check per-worker order.
- Expected work failure (`Result.Err`) ends just that worker with an error
  event. Individual task cancellation may leave an accepted partial prefix;
  other workers continue. A host exception propagates through the scope and
  never saves a success report. It is deliberately a scope fault, not silently
  converted into isolated expected failure.
- Parent scope exit cancels and joins all tasks. Cancellation alone is **not a
  drain guarantee**: a collector may observe an empty tail before an in-flight
  send completes. Therefore the scope returns the mailbox head, and graph code
  reconstructs the accepted prefix **after scope settlement** from retained
  completed cells. The first closed unresolved tail is then a stable end.
  Completed cells remain readable after owner closure. This final fold does
  not discard `Some` merely because cancellation is requested.
- Normal completion compares the live collector's aggregate with that
  post-settlement fold and saves once. Parent-exit returns
  `Err("cancelled|" + acceptedPrefix)` and does not save. Both paths release the
  resource exactly once, after all tasks and I/O waits settle.
- A successful completion remains accepted even if cancellation arrives before
  its acknowledgement is observed. The real parent-exit case holds one such
  acknowledgement until cancellation and verifies it is retained.

This probe's cancellation case is an explicit **parent scope body exit**, not
an externally injected cancellation token. Host-exception propagation is also
covered. Neither case promises a report when a host exception prevents the
head from being returned.

## Coverage and cost boundaries

Virtual fixtures cover 2/3/4 workers, an isolated error, individual cancellation,
Stop/send cutoff, parent exit and a host exception. The real-runtime harness
covers the same policies, with producer overlap barriers and 20 Stop races.
The earlier scoped-result-handoff example already has 200 real-thread
OneShot completion/completion and completion/close races; this probe extends
that evidence to a dynamically registered multi-producer graph.

Append walks from the head on each send, so work is quadratic in total event
count. Each logical append allocates only once, including across retries;
rejected appends can leave an unlinked cell until its owning scope closes.
Retaining the accepted log and folding it twice is a measured shutdown cost,
not a general unbounded channel design. This fixture intentionally stays at
2–4 workers and four events per healthy worker. A larger workload should
measure this cost before choosing new language semantics.
