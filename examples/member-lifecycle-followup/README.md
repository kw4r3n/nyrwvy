# Bounded Member lifecycle follow-ups

Two no-core adaptations to the [baseline offline probe](../member-lifecycle/README.md).
This uses Nyrwvy to characterize a bounded rrawrlet workload; it does not integrate
Nyrwvy into rrawrlet or change Nyrwvy's runtime, primitives, dependencies or fault defaults.

The frozen workload is two synthetic Members, one controller-owned settlement
monitor and one separately cancellable observer. Graph code owns all task handles,
status decisions, snapshots/checks, occupancy and workspace retention. The host
provides typed effects, fake provider work, deterministic gates and test cancellation.

## Adaptations

The monitor waits for actual Member task settlement and publishes the ordered
snapshots to a controller-owned `OneShot`. The observer uses cancellable
`OneShotWait`, then checks its own cancellation before returning. Cancelling the
observer does not cancel a Member or the monitor. Controller `TaskWait(any)`
observes the monitor or observer; this also prevents a monitor exception from
leaving the controller parked behind an uncompleted cell. Both use the same
Result type. Scope shutdown still cancels/joins all owned tasks before closing
the global resource. Direct `TaskWait` caller cancellation remains unchanged.

The provider effect explicitly tags expected errors and unexpected ordinary
exceptions thrown **inside provider run** as data (`kind`, `cause`). This is a
changed host contract. Graph code determines failed status and retains the cause;
the healthy peer remains running and subsequently completes. Cancellation using
the requested current provider token is a separate cooperative path. Foreign
cancellation is rethrown. Effect setup outside that boundary and provider-lease
cleanup are not caught or relabelled as provider errors. `UseResource` closes the
provider lease before task settlement on every path; workspaces remain outside
that lease and are retained.

## Frozen verification

| Stage | Result |
| --- | --- |
| Saved/reopened virtual fixtures | 16/16 assertion passes |
| Reopened and moved-directory replay | 16/16 each |
| Unedited emitted C# with real barriers | 16/16 assertion passes |
| Actual pinned Team observations | 13 matches, 3 intentional raw-fault divergences |
| Negative comparison controls | 5/5 rejected |
| Original baseline control recheck | 15 assertions pass; 13 Team matches, original 2 divergences |

The 16-case matrix is frozen: any/all, finite and zero timeout, caller cancellation,
cancel-first, complete-first, publication-before-cancellation, snapshot-gated timeout,
tagged unexpected/expected provider errors, owner-scope exit, injected parent-token
cancellation, and three raw-fault controls. The observer's actual returned payload
is checked separately from fresh task inspection. Every real case measures two
overlapping providers and one global resource close; each provider lease closes once.

The raw controls are cleanup failure, an exception outside provider run and foreign
cancellation. They intentionally retain scope failure while Team isolates the first
two as failed and treats foreign `CancelledError` as stopped. Those assertion
passes are **not** Team parity. Global comparisons cover Member records and cleanup;
graph cancellation returns an explicit Err, whereas Team `close` returns a shutdown
summary. Publication-before-cancellation uses a scheduling barrier after the wait
has observed its data, before the caller returns; the oracle gates the equivalent
caller wrapper around the unchanged Team method. It is not a random race stress test.

## Reproduce exact saved graphs

Use the normal repository setup, Node 22.6+ and installed .NET 10. The probe adds
no dependencies. AuthoringSession must already be built. Use a separate checkout
of rrawrlet pinned to `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`.

```nu
node examples/member-lifecycle/materialize.mjs followup /tmp/member-followup-repro
node examples/member-lifecycle-followup/run.mjs --replay /tmp/member-followup-repro
node examples/member-lifecycle-followup/test-runtime.mjs /tmp/member-followup-repro
python examples/member-lifecycle-followup/oracle.py /path/to/rrawrlet /tmp/member-followup-repro
```

Fresh ordinary-tool authoring is also supported:

```nu
node examples/member-lifecycle-followup/run.mjs /tmp/empty-followup-bundle
```

It authors the original 50 functions plus 24 adaptation functions: **74 functions,
1,585 graph nodes** (955 baseline plus 630 added). Fresh authoring allocates new
stable IDs; materialization/replay retains the committed synthetic fixture IDs and
exact projection hashes. Generated C#, binaries, machine-local transcripts and
raw runtime logs are intentionally not committed. Fixture snapshots contain
program definitions, not live task state; fixture arguments are synthetic.

See the [English verification report](../../docs/verification/member-lifecycle-followup-20261012.md)
and each fixture directory's `verification-summary.json` for bases, hashes and limits.
