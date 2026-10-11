# Bounded two-worker stream verification

Date: 2026-10-11.
Verified base: `89787e3376c8612d301ebc5c3396e5df2a713258`, the TaskStart
capture-release branch in [PR #376](https://github.com/kw4r3n/nyrwvy/pull/376).

## Result

The [example](../../examples/bounded-worker-stream/README.md) demonstrates two
long-lived producers feeding a slower sink with at most two accepted,
unacknowledged Data records. Existing OneShot and TaskScope operations own
admission, routing, acknowledgement, cursors and shutdown. No Channel primitive
or new core change is included in this example.

The ordinary-authored graph has **21 functions and 686 nodes**. The final saved
fixtures, reopened replay and unedited generated-C# runtime all passed. The
same graph is used across these checks, but virtual and real workloads differ
in size because the ordinary fake-operation budget is intentionally unchanged.

## Final verification

| Check | Result | Coverage |
| --- | --- | --- |
| Source, fixture-oracle and compiler-helper tests | 15/15 | Lexical scopes, start-gate ordering, fixture format/budget, delivery oracle and installed-compiler path |
| Saved virtual execution | 9/9 | 4/4 and 64/64 records; asymmetric 1/128; idle input; Stop; source/sink Err; sink exception; parent scope-body exit |
| Reopened snapshot replay | 9/9 | Same saved virtual fixtures after reopening |
| Real-thread functional runtime | 12/12 | 4/4 and 256/256; asymmetric 1/256; idle input; held sink; Stop-first and send-first; source/sink Err; sink exception; parent exit; external parent-token cancellation |
| Live/open-owner GC checks | 3/3 | Complete producer/collector protocol at 4/4, 256/256 and asymmetric 1/256 |
| Projection integrity | Passed | `Generated.cs` hash unchanged through runtime verification |

The same-commit fast Core 851/851 and Node 32/32 results were reused from the
preceding verification; they were **not rerun for the final fixture-only
revision**. No FullExtended, every-architecture, full-repository-suite or
whole-process heap-bound claim is made.

The final local evidence archive has SHA-256:

`0a7fc7f6407971cce15029bfbc997f0262c6ab0e87d7ee4c29c697d7eaaa27bc`

The verifier's report is `V5-REPORT.ja.md`. The saved bundle includes the graph,
fixtures, authoring transcript, symbol manifest, unedited projection and runtime
evidence. Compiler/reference-pack/runtime identity is recorded by the installed
Roslyn harness; it performs no NuGet restore or package fetch. This document
summarizes reported local .NET results. Cloud checks did not execute .NET.

## Delivery, bounded work and shutdown

- Normal real runs delivered all 8, 512 and 257 records exactly once, matching
  the accepted-record multiset and consecutive per-worker sequence order.
- The held-sink fixture admitted exactly two records and permitted no third
  read while the sink remained closed. The same held-capacity setup preceded
  injected sink Err and sink exception, after which all work settled.
- Stop-first forced a waiting producer's later append to reject. Send-first
  forced a racing record to be accepted before Stop and verified its drain.
  Each stream has its own Stop completion cutoff; there is no atomic global
  cutoff across both streams.
- Parent body exit and externally cancelled parent tokens released pending
  waits. Every tested acquired resource closed once, after host I/O settled.
  Abort and error paths did not save a success report.
- The coordinator handles successful early producer completion without closing
  the collector, and observes producer errors while work remains active.

Normal successful append-attempt counts are `N + 2`, including the two Done
markers: **10, 514 and 259** for the real 8-, 512- and 257-record cases. The
virtual 8-, 128- and 129-record cases correspond to **10, 130 and 131**. A
successful Stop append used at most two attempts, including a possible walk
past its stream's one outstanding record. These are graph traversal/attempt
counts, not throughput measurements. Rejected-attempt costs are not included
in the current success-only telemetry.

The host's observed backlog metric is accepted Data minus recorded successful
sink returns. The graph's stronger acknowledgement ordering is also structural:
a producer cannot read another record before its own ack. The held-sink test
makes both counts coincide at the capacity limit. No arbitrary external sink
transaction or rollback guarantee is inferred from these fixture results.

## Retention evidence and scope

The two streams are independent SPSC chains. A producer paused on input or
between instructions cannot retain completed history produced by its peer.
This is why the example does not merely add acknowledgements to the previous
shared MPSC chain with a cached cursor per producer.

Three additional lifetime safeguards matter:

1. The prerequisite TaskStart capture fix consumes the original worker captures
   into loop locals through ref-entry overloads.
2. The head-owning launch helper returns only task handles. A graph-owned start
   gate opens after that frame has returned, before any stream may advance.
3. The collector has one common self-tail-call site. Side-specific processing
   runs in a returning helper so a dormant branch cannot preserve an old cursor
   while the other stream continues.

The GC fixtures call emitted `launchWithHeads` and `awaitTasks` directly, with
harness-managed owner, resource and start-gate setup. They exercise the complete
producer/collector protocol; the 12 functional cases separately execute the full
`run` entry and its scope/resource wrappers.

In all three GC cases, the original heads collected **while the owner remained
open and the collector was blocked in sink I/O**. The owner retained three task
handles; the early-Done case intentionally had one producer already finished.
Live pending-cell counts were **4, 4 and 3**; after settlement and owner closure
each count was zero. Rooted and unrooted GC controls were checked, and the owner
object was kept alive. The runner uses optimized compilation and adds no JIT
tiering overrides.

These checks sample original heads, not every possible intermediate heap root.
The common-loop/returning-helper structure and per-stream outstanding limit
supply the accompanying graph-root analysis. The graph retains no accepted log
and does no history reconstruction at shutdown. Record sizes remain unbounded,
and the test host deliberately retains observation/sink logs for the oracle.
Consequently neither a byte-size bound nor constant whole-process heap usage is
claimed. See the [contract](../../examples/bounded-worker-stream/CONTRACT.md)
for the exact fairness, cutoff and lifetime boundaries.

## Actual authoring friction

Three fixture/recipe issues were corrected before the final pass. They are not
core-runtime failures, and the earlier rejected stages were not counted as
successful virtual execution:

1. **None still creates a lexical frame.** The monitor's MatchOption None branch
   initially referred to outer values at depths 1/0. Ordinary authoring rejected
   this with `PureExpressionPatternSlotNotFound`: None introduces an empty frame,
   so the correct depths are 2/1. The lightweight checker had made the same wrong
   assumption and passed the invalid recipe. It now models the empty frame and
   has a dedicated regression test. Preflight checks do not replace actual
   ordinary-tool semantic validation.
2. **Scalar and nested Result fixture forms differ.** Result<String,String>
   fake responses require flat `okKind`/`errorKind` and a scalar payload. The
   initial typed nested response form was rejected before execution. Unit/String
   results retain their typed nested form. Both forms now have explicit fixture
   regressions. The local verifier also reported insufficient location information
   in fake-value errors. This is recorded as observed diagnostic friction, without
   inferring a specific missing field/path or claiming a separate runtime defect.
3. **The existing fake-operation budget is real.** Two 256-record producers need
   more than 256 cases/responses in shared Host operations. Those large virtual
   fixtures were rejected, while real-runtime runs already passed. Virtual
   workloads were reduced to 64/64 and 1/128, with a budget validator/regression;
   real workloads remain 256/256 and 1/256. No tool limit was expanded.

The function/node count and these concrete failures record the cost of this
example. They do not by themselves justify an editor redesign, generic Channel,
new scheduler or expanded fixture limit. The reusable verified-example stopping
condition is met; publication and the prerequisite fix's merge remain separate.
