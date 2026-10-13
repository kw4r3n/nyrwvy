# Determined Member settlement

The default admission recipe captures Member status immediately after its
cancellable provider scope returns. A `CancellationShield` then publishes that
status, closes the provider lease and finishes the snapshot. It restores the
original cancellation token before returning. The owner still reports its
cancellation only after scope drainage and outer release. Raw snapshot and
release faults retain their existing precedence.

The standalone serialized recipe retains its historical default. The older
owner-cancellation runner explicitly selects `protectDetermined:false` to
reproduce its six and eleven historical fixtures. This switch is a recipe
option, with no generic runtime, schema or capability change.

Ten bounded scripts exercise held completed snapshot, raw and typed snapshot
faults, held completed/failed cleanup, an undetermined provider, a provider that
returns completed after cancellation, cancellation before worker entry, prior
settlement, and a raw outer release fault. Both variants contain the same
probe-only cancellation audits around cleanup/snapshot effects and repeat
TaskCancel calls. Real C# uses explicit effect barriers and an owned/joined
external-root driver; the virtual schedule is supplementary. Virtual and real
share the C# emitter/runtime. Actual pinned Python supplies independent evidence.

```nu
node examples/member-determined-settlement/run.mjs /tmp/determined-before --baseline
node examples/member-determined-settlement/test-runtime.mjs /tmp/determined-before
node examples/member-determined-settlement/run.mjs /tmp/determined-after
node examples/member-determined-settlement/test-runtime.mjs /tmp/determined-after
python examples/member-determined-settlement/oracle.py /path/to/pinned-rrawrlet /tmp/determined-after/cases.json /tmp/determined-after/oracle-evidence.json
node examples/member-determined-settlement/prove.mjs /tmp/determined-before /tmp/determined-after
node examples/member-determined-settlement/run.mjs --replay /tmp/determined-after
node examples/member-determined-settlement/prove.mjs /tmp/determined-before /tmp/determined-after --replay
cp -r /tmp/determined-after /tmp/determined-moved
node examples/member-determined-settlement/run.mjs --replay /tmp/determined-moved
node examples/member-determined-settlement/prove.mjs /tmp/determined-before /tmp/determined-moved --replay
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-member-determined-settlement.test.mjs
```

The same assertions reject five baseline cases in each of the three graph modes
and accept all ten protected cases. Eight sensitivity controls mutate recorded
observations; they are not compiled graph mutants. Fixtures preserve before,
after, independent Python and the proof. Full local bundles retain transcripts,
saved graphs, source/symbol identity and compiler evidence under
`artifacts/determined-settlement/`.

The Python harness verifies HEAD and exact source bytes before calling unchanged
Team.execute/stop/close and await_cleanup. Provider/Git/save effects are fake.
Python cancels only a still-running, not-already-cancelling Member. Status is
already determined before cleanup and snapshot; the actual stop joins those
stages without cancellation. A completed provider return while its task has
pending cancellation becomes stopped.

This closes the bounded token/settlement gap, without claiming whole Python
policy equivalence. Python records snapshot errors as failed attempts, while
raw graph snapshot faults propagate. Python close suppresses subsequent checks;
this historical probe disables the later close-intent composition so its
cancelled closing handoff can still leave checks runnable. The default admission
recipe now [retains admitted close intent](../member-close-intent/README.md).
Python may acquire an agent before provider entry; the graph pre-entry path
creates none. Queue protection, arbitrary faults/interleavings, persistent
recovery, provider-specific shutdown and physical Git effects remain separate.
No live provider, account action, push, PR, upload or export is performed.

See the [verification report](../../docs/verification/determined-member-settlement-20261013.md).
