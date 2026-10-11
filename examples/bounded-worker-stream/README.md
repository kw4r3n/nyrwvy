# Bounded two-worker stream

Verified on `89787e3376c8612d301ebc5c3396e5df2a713258`, the capture-lifetime
fix branch from PR #376. This example is separate from that fix and from the
minimal retention probe in `examples/bounded-stream`.

Two long-lived producers own independent OneShot chains. Each waits for a sink
acknowledgement before reading another input, limiting accepted, unacknowledged
Data to two records total. The collector routes round-robin, with deliberate
head-of-line blocking behind a slow input. An idle producer never holds a cursor
into the other producer's history.

A graph-owned start gate opens only after the launch helper returns, so even a
descheduled launcher cannot retain initial heads while streams advance.

The graph owns admission, cursors, routing, acknowledgements and shutdown.
Host capabilities supply record/sink/resource I/O; observation and abort-signal
operations are fixture instrumentation, not a host mailbox or scheduler.

StopAfter K triggers Stop insertion into both streams after K successful sink
calls. Each Stop completion is its stream's cutoff; racing accepted records
before that cutoff are drained. The final count may exceed K by up to two.
Abort and faults settle waits through scope closure and cooperative I/O; they
promise no complete accepted-history reconstruction or external rollback.

## Verified result — 2026-10-11

- Ordinary authoring: 21 functions, 686 nodes
- Source/oracle/compiler-helper tests: 15/15
- Saved virtual cases and reopened replay: 9/9 each
- Unedited C# real-thread cases: 12/12; live/open-owner GC checks: 3/3
- Normal real delivery: 512/512; asymmetric delivery: 257/257
- Held-sink capacity: two; blocked work settled on Stop, cancellation and faults
- Original heads collected with owner open; three task handles, live pending
  cells 4/4/3, then zero after closure

These are focused checks. Same-commit fast Core 851/851 and Node 32/32 results
were reused, not rerun for the final fixture-only revision. See the
[verification record](../../docs/verification/bounded-worker-stream-20261011.md)
for evidence identity, authoring friction and memory/coverage limits.

Run on an isolated checkout of the exact base, with the normal .NET 10 setup:

```sh
node --test examples/bounded-worker-stream/*.test.mjs
node examples/bounded-worker-stream/run.mjs /path/to/empty-bundle
node examples/bounded-worker-stream/run.mjs --replay /path/to/empty-bundle
node examples/bounded-worker-stream/test-runtime.mjs /path/to/empty-bundle
```

The authoring runner uses ordinary graph tools, saves/reopens the snapshot, and
emits C# with saved argument-keyed virtual fixtures. Cloud source checks are not
semantic/runtime evidence. The real-runtime harness covers 12 schedules plus
three live/open-owner GC checks using the unedited emitted projection.

Virtual fixtures use 4/4 and 64/64 records, plus an asymmetric 1/128 run.
The real-runtime checks use 4/4 and 256/256, plus 1/256. The virtual sizes stay
within the existing limit of 256 cases and total scripted responses per Host
operation; this example does not change that limit.

See [contract](CONTRACT.md) for cutoff, abort, fairness and memory limits.
