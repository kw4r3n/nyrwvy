# Opt-in snapshot-fault containment

Pass `containSnapshotFaults:true` to the controller, admission or owner author.
The default remains false. Two ordinary graph functions compose
`ExceptionCatch` around `Host.snapshot`. Its handler uses the generic pure
`ExceptionIsCancellation`: cancellation rethrows original dispatch information;
ordinary faults return a typed Err to the existing failed-Final branch. Commit publication, checks, memberClose and outer release
stay outside that boundary. The classifier requires snapshot JSON v17 / Nyason v13; generated C# is unedited. `ExceptionMessage` becomes the intermediate Err; Final retains status,
commit and checks only, so this does not preserve Python's diagnostic text.

Eleven bounded scripts cover ordinary raw snapshot failure, held owner/root
cancellation, admitted close, competing release fault, typed failure, success
with and without close, successful snapshot plus release fault, raw checks fault,
undetermined provider cancellation, and an explicitly thrown OCE. Real effects
use entry/release barriers. All retained views follow an explicit Member join;
scope exit drains graph-owned requests, relays and Members before one release
attempt. The synthetic external driver is explicitly joined after the graph
invocation finishes, before publishing evidence. Virtual scheduling is supplementary.

```nu
node examples/member-snapshot-faults/run.mjs /tmp/snapshot-before --baseline
node examples/member-snapshot-faults/test-runtime.mjs /tmp/snapshot-before
node examples/member-snapshot-faults/run.mjs /tmp/snapshot-after
node examples/member-snapshot-faults/test-runtime.mjs /tmp/snapshot-after
python3 examples/member-snapshot-faults/oracle.py /path/to/pinned-rrawrlet /tmp/snapshot-after/cases.json /tmp/snapshot-after/oracle-evidence.json
node examples/member-snapshot-faults/prove.mjs /tmp/snapshot-before /tmp/snapshot-after
node examples/member-snapshot-faults/run.mjs --replay /tmp/snapshot-after
node examples/member-snapshot-faults/prove.mjs /tmp/snapshot-before /tmp/snapshot-after --replay
cp -r /tmp/snapshot-after /tmp/snapshot-moved
node examples/member-snapshot-faults/run.mjs --replay /tmp/snapshot-moved
node examples/member-snapshot-faults/prove.mjs /tmp/snapshot-before /tmp/snapshot-moved --replay
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-member-snapshot-faults.test.mjs
```

The independent oracle verifies exact HEAD and source bytes against Python
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` before importing actual Team methods.
It calls actual execute, wait, inspect, stop, close, lock and await_cleanup.
Only provider/Git/check/save effects are synthetic. No policy method is copied.
The routine Node regression uses the retained independent Python fixture.
Nineteen record-mutation controls test comparison sensitivity; these are not
compiled graph mutants. Installed Roslyn compiles Generated.cs unedited, offline.
Virtual and real use the same C# emitter/runtime.

`ExceptionCatch` still captures OCE; the opt-in handler rethrows it. Real C#
throws a clear-token OCE: baseline and opt-in both propagate it. A separate
compiled invocation of the same emitted Member settlement callback joins its
actual task before reading its completed phase and absent commit. It returns no
Final, faults the task with OCE (IsCanceled is false), and drains before release;
scope propagation preserves original identity. This harness observes existing
runtime objects; it does not construct a substitute graph policy.

Actual pinned Python excludes CancelledError from `except Exception`: its task
is cancelled, retains completed status with no commit/checks/final save, and
drains before release. Task state and retained phase/outcome are distinct
observations. Virtual fake `throw` is ordinary and is recovered by the opt-in
handler; it does not measure exception class. Virtual and real share the emitter;
Python is the independent oracle. No whole-shutdown parity claim is made.

Raw checks faults still propagate in the graph. Python's raw check failure
faults the Member task after capturing its commit; the full records stay separate.
The baseline also keeps a raw snapshot fault over a competing release fault.
After snapshot containment, the release fault is the remaining raw failure and
wins owner cancellation. A separate successful-snapshot/release control preserves
the original release-fault behavior in both variants.

Queued-handler protection, bare-owner shutdown, an atomic concurrent check
cutoff, arbitrary fault classes/interleavings, live provider shutdown, physical
Git recovery and restart persistence are excluded. Defaults and earlier fixtures
remain unchanged. See the [verification report](../../docs/verification/exception-is-cancellation-20261014.md)
and [current closure](fixtures/cancellation-closure.json).
