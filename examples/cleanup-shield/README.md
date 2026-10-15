# Parent cancellation during finish and rollback

This offline probe implements the mutation tail of pinned rrawrlet
`Team.integrate` using a generic [lexical cancellation shield](../../docs/specs/cancellation-shield.md).
The graph decides apply/commit/rollback order, HEAD capture, ledger assignment,
failure precedence and post-cleanup cancellation. The host supplies typed mock
effects and gates. Existing Member integration fixtures are unchanged; this
probe has no dependency on unmerged PR383.

Fourteen cases compare exact phase order, per-phase cancellation observations,
parent pending state before gate release, and final ledger/result. Both patch
shapes, repeated parent cancellation, commit rollback, rollback error, HEAD error
and save error are covered. The Python oracle calls unchanged `Team.integrate`
and `await_cleanup` at `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`, replacing only
effects. Save is synchronous in Python: its stub requests cancellation before
return/throw; no asynchronous interruption of disk save is claimed.

Use Node 22.6+ and the repository's installed .NET SDK, with AuthoringSession
built. The runtime helper compiles unedited emitted C# against installed .NET
assemblies without restore or downloads. From the repository root in Nushell:

```nu
node --experimental-strip-types examples/cleanup-shield/run.mjs /tmp/shield-probe
node examples/cleanup-shield/test-runtime.mjs /tmp/shield-probe
python examples/cleanup-shield/oracle.py /path/to/pinned-rrawrlet /tmp/shield-probe
cp -r /tmp/shield-probe /tmp/shield-replay
node --experimental-strip-types examples/cleanup-shield/run.mjs /tmp/shield-replay --replay
python examples/cleanup-shield/oracle.py /path/to/pinned-rrawrlet /tmp/shield-replay
```

The ordinary Node aggregate includes the authoring/replay/runtime regression.
It compares the protected results with committed, normalized Python expectations.
Run only that regression with:

```nu
node --experimental-strip-types --test .opencode/tests-extended/nyrwvy-authoring-cleanup-shield.test.mjs
```

For the before result, build AuthoringSession in a separate checkout of pinned
main `37e512c682b32bbacdf40e9b47f26910f2b59c68`. The baseline recipe uses direct
calls instead of shields and is accepted by that unchanged compiler:

```nu
node --experimental-strip-types examples/cleanup-shield/run.mjs /tmp/shield-before --baseline --authoring-root /path/to/pinned-nyrwvy
node examples/cleanup-shield/test-runtime.mjs /tmp/shield-before --baseline
python examples/cleanup-shield/oracle.py /path/to/pinned-rrawrlet /tmp/shield-before --baseline
```

Both protected=false branches in the current compiler and the separate unchanged
compiler disagree with Python in twelve cancellation cases. Baseline mode records
those disagreements as expected negative evidence; it does not claim parity.

Six real-runtime controls additionally cover token restoration after a raw fault
and already-requested cancellation, context evaluation before masking, a generic
Err result, protected new scopes draining before resource release (including a
worker fault), and existing scopes retaining their parent cancellation link.

This is a bounded finish tail, not a full rrawrlet port. Eligibility, concurrency
locks, provider lifetimes, real Git/index security, filesystem restoration, process
restart, and raw-fault translation are outside the comparison. Synthetic rollback
effects do not prove physical restoration. See the [verification record](../../docs/verification/cleanup-shield-20261012.md).
