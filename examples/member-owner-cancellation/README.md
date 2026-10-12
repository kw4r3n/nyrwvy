# Whole-owner cancellation and shutdown

Six bounded scripts distinguish parent cancellation from cancelling a request
or observer. They test queued admission, held protected integration with and
without a raw commit fault, held determined Member settlement, and held shutdown
with and without a raw resource-release fault. Each uses at most two registered
Members and three concurrent callers. All effects are synthetic.

The subject is the existing admission composition. The baseline harness adds
four ordinary functions that own `concurrentRun` as a task. `TaskCancel` injects
linked parent cancellation in the virtual clock and real runtime. Real C# also
injects a root token from a separately owned and joined driver, before the graph
repeats cancellation. No jobs escape their scopes.

The adaptation adds one ordinary function, `ownerCancellationRun`: after the
existing scope drains and the resource releases, it checks the restored owner
token and returns `Err("owner cancelled")`. Uncancelled execution retains its
Result. Raw settlement or resource-release faults still propagate. This is an
explicit return-contract adaptation; it does not change admission ownership,
handoff-cell closure or worker cancellation policy.

Pinned Python `4bab9e030744a0887c1ee13bdd8fc8db3b98334f` has a different
shutdown contract. The oracle calls actual `Codex.close`/`_close`, Team methods,
lock and `await_cleanup`, with synthetic provider/Git effects. It never starts
a Codex process. The synthetic owner follows the runner's nested cleanup shape;
`run_team` itself is not invoked. `Codex._close` drains named mutating handlers
before Team shutdown. Python tasks do not inherit parent cancellation. The
runner's shielded Team cleanup preserves determined Member settlement and
rejects spawns after its closing transition. A raw cleanup fault wins pending
parent cancellation. The graph's finish request is not a Python Team tool:
shutdown cases cancel the actual runner-shaped final cleanup instead of
inventing a `team_finish` handler.

There are **zero complete Python matches**. Separate fixtures retain Python
and baseline/adapted C# observations. Exact comparison covers request outcomes,
registry views, mutation order within each request, Member effect order and
required happens-before. Independent replies have no forced total order.
The settlement graph view is gated before its snapshot fault; the final Python
view is after shutdown. These are the actual available views, not a reconstructed
common terminal registry. Closed C# handoffs can hide an admitted Member or
late retained integration ledger even though owned tasks drain.

```nu
node examples/member-owner-cancellation/run.mjs /tmp/owner-baseline --baseline
node examples/member-owner-cancellation/test-runtime.mjs /tmp/owner-baseline
node examples/member-owner-cancellation/verify.mjs /tmp/owner-baseline
node examples/member-owner-cancellation/run.mjs /tmp/owner-final
node examples/member-owner-cancellation/test-runtime.mjs /tmp/owner-final
python examples/member-owner-cancellation/oracle.py /path/to/pinned-rrawrlet /tmp/owner-final/cases.json /tmp/owner-final/oracle-evidence.json
node examples/member-owner-cancellation/verify.mjs /tmp/owner-final
node examples/member-owner-cancellation/run.mjs --replay /tmp/owner-final
cp -r /tmp/owner-final /tmp/owner-moved
node examples/member-owner-cancellation/run.mjs --replay /tmp/owner-moved
node examples/member-owner-cancellation/verify.mjs /tmp/owner-moved --replay
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-member-owner-cancellation.test.mjs
```

Virtual and real paths share the unchanged C# emitter. Virtual timing is
supplementary scheduling evidence; real/Python cases use explicit gates. The
fixtures require the divergences and reject twelve normalized-record mutations;
they are not compiled mutants. Python 3.14 may report the expected release fault
through its shielded-future diagnostic even when the cleanup exception is
retrieved and wins cancellation; the oracle records this rather than hiding it.

The recipe still retains tickets until scope exit and scans snapshots, with
linear retained storage and quadratic total scanning across long scripts.
Whole-owner Python policy equivalence, arbitrary host faults, general provider
shutdown, fairness, physical Git recovery and restart persistence remain gaps.
No live provider, credentials, accounts, physical integration Git effects,
publication, push, PR, upload or repository export is performed.
