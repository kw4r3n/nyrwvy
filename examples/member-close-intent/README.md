# Close intent after cancellation

The default admission recipe owns its close OneShot in a separate shielded
scope. It starts admission work through the original cancellable scope and
waits for that task's complete drainage before closing the latch scope.
Requests, providers, Member scopes and immutable state handoffs retain their
original cancellation linkage. An admitted close can publish its latch after
cancellation closes those state handoffs, so a determined snapshot skips checks.
A close cancelled while queued never publishes intent. Checks already entered
can finish.

Eleven scripts compare baseline and fixed graphs: cancellation before close
publication, held cleanup, typed/raw snapshot faults, release fault, close
published before cancellation, checks entered before close, independently
cancelled admitted/queued close callers, and undetermined providers including
completed return after cancellation. Explicit C# barriers control admission,
publication and effect release. The queued control joins both Members before
inspection; comparisons retain per-Member effect order without requiring a
total order between independent Members. Virtual timing is supplementary.

```nu
node examples/member-close-intent/run.mjs /tmp/close-before --baseline
node examples/member-close-intent/test-runtime.mjs /tmp/close-before
node examples/member-close-intent/run.mjs /tmp/close-after
node examples/member-close-intent/test-runtime.mjs /tmp/close-after
python examples/member-close-intent/oracle.py /path/to/pinned-rrawrlet /tmp/close-after/cases.json /tmp/close-after/oracle-evidence.json
node examples/member-close-intent/prove.mjs /tmp/close-before /tmp/close-after
node examples/member-close-intent/run.mjs --replay /tmp/close-after
node examples/member-close-intent/prove.mjs /tmp/close-before /tmp/close-after --replay
cp -r /tmp/close-after /tmp/close-moved
node examples/member-close-intent/run.mjs --replay /tmp/close-moved
node examples/member-close-intent/prove.mjs /tmp/close-before /tmp/close-moved --replay
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-member-close-intent.test.mjs
```

The unchanged Python oracle checks HEAD and exact bytes of team.py, codex.py,
runner.py and team_tools.py before import. It invokes actual Team.spawn,
execute, wait, close, lock, stop and await_cleanup with synthetic external
effects. A requested close behind the actual lock has not set Team.closing;
cancel it directly to prove that boundary. Cleanup through actual await_cleanup
retains close despite caller cancellation, then the actual close transition
suppresses subsequent checks. No policy method is copied and no provider starts.

Committed fixtures retain exact graph records, independent Python records and
the before/after proof. Routine regression uses the independent fixture; the
commands above regenerate it from actual pinned Python. Twelve sensitivity
controls mutate records, not compiled graphs. Installed Roslyn compiles the
emitted C# unedited and offline. Virtual and real share the C# emitter/runtime;
Python supplies independent language evidence.

This is a bounded admitted-close guarantee, not whole shutdown parity. Bare
owner cancellation without an admitted close does not itself publish intent.
There is no atomic cross-task cutoff against concurrent check entry. Raw C#
snapshot faults still propagate while Python records failed snapshots. Python
queued mutating-handler protection remains separate. The historical owner and
determined examples explicitly disable this new recipe option to preserve their
prior fixtures; the default admission recipe enables it. Completed mutation
recovery, ledger policy and determined settlement are unchanged.

See the [verification report](../../docs/verification/close-intent-handoff-20261013.md).
