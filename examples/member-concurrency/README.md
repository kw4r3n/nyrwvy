# Bounded concurrent Member admission

Six runs from five scripts exercise the unchanged Python Team's lock policy:
spawn/integrate/discard own mutations; close owns its cutoff transition;
wait/stop run independently. This example extends the existing serialized
controller through ordinary authoring, without changing Nyrwvy core or the
inherited 51 scenarios / 771 request audits.

`AdmissionOwner` holds immutable state and scoped one-shot handoffs. Each
exclusive request waits for its predecessor's state. Its independent supervisor
publishes the resulting state; if the queued caller cancels, the caller settles
immediately and the supervisor later forwards the unchanged predecessor state.
Independent readers select the latest published snapshot. Integration retains
the existing cancellation shield and exception boundary. Close publishes
`closing=true` to its successor before stopping/joining Members outside that
ownership interval. The owner joins every request supervisor before its
TaskScope exits, and releases one resource after all owned tasks drain.

The real and Python runs use explicit held workspace, commit and close-drain
events. Python uses actual pinned Team methods, asyncio.Lock and await_cleanup;
ContextVar attributes effects to individual requests, including shielded child
tasks. C# uses AsyncLocal solely for host-effect attribution. Virtual execution
uses a scripted timing schedule and supplies supplementary evidence; it does
not reproduce real barriers. Comparison checks outcomes, nine-field final state,
request-local mutation order and required happens-before constraints. It does
not require a total order between independent callers.

```nu
node examples/member-concurrency/run.mjs /tmp/admission-fresh
node examples/member-concurrency/test-runtime.mjs /tmp/admission-fresh
python examples/member-concurrency/oracle.py /path/to/pinned-rrawrlet /tmp/admission-fresh/cases.json /tmp/admission-fresh/oracle-evidence.json
node examples/member-concurrency/verify.mjs /tmp/admission-fresh
node examples/member-concurrency/run.mjs --replay /tmp/admission-fresh
cp -r /tmp/admission-fresh /tmp/admission-moved
node examples/member-concurrency/run.mjs --replay /tmp/admission-moved
node examples/member-concurrency/verify.mjs /tmp/admission-moved --replay
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-member-concurrency.test.mjs
```

`verify.mjs` uses the independently recorded Python fixture, so routine regression
tests need no checkout of the Python repository. To reproduce the baseline,
author the unchanged `member-controller` into a fresh directory, copy it locally,
run `baseline.mjs` against the copy, then run `test-runtime.mjs <copy> --baseline`.
The held commit times out because the unchanged whole-request fold cannot
dispatch the stop/release request. Its catch returns the timeout error; the
baseline is not an unhandled generated-runtime exception.

Coverage is finite: at most two registered Members and three concurrent callers.
This is a recipe, not a public Team service or an unbounded queue implementation.
It retains request tickets until scope exit and scans their immutable snapshots,
so long scripts have increasing storage and quadratic total scanning cost.
It does not establish exhaustive fairness, whole-parent cancellation, arbitrary
host faults, provider shutdown, physical Git recovery or restart persistence.
No provider, credentials, account, physical integration Git effects, Library
uploads, repository export, push or PR are performed.

See the [milestone report](../../docs/verification/bounded-request-admission-20261013.md)
and [verified closure](fixtures/verified-closure.json).
