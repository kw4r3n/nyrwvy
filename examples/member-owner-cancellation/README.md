# Whole-owner cancellation and shutdown

The integration scripts now explicitly stop/join peer Member 2 before requiring
its terminal retained view. Joining the integration request tickets alone did
not join that Member; a delayed cancelled provider could legitimately leave it
`running` at inspection. The [pre-join repair report](../../docs/verification/owner-prejoin-observation-20261014.md)
retains the old failure and distinguishes that observation from the joined
`stopped` result. Historical fixtures remain intact; the verifier adds the new
`settled-b` reply while checking every original golden field exactly.

After authoring a bundle, reproduce deterministic old RED and alternate joined
GREEN schedules in both task and root cancellation modes:

```nu
node examples/member-owner-cancellation/prove-prejoin.mjs /tmp/owner-final /tmp/owner-schedules
```

The output directory must be fresh. The synthetic host releases the cancelled
peer at the retained reply (old script), at an explicit pre-join observation,
or alongside mutation release. The maintained default holds it until the
existing stop/join gate. Only the graph join establishes completion; provider
return or cleanup entry is not treated as a completed task. No sleeps, retries,
runtime changes or relaxed terminal expectations are used.

The default authoring API now protects determined Member settlement. This
historical runner explicitly selects `protectDetermined:false` to reproduce
the earlier six and eleven fixtures. The new bounded before/after matrix is in
[member-determined-settlement](../member-determined-settlement/README.md).

The completed-mutation milestone adds a composition-only state recovery path.
The default admission authoring recipe polls successful completed exclusive
task results after reading its handoffs. Parent cancellation still closes those
cells, cancels queued callers and propagates to Members. The new retained view
contains the completed HEAD; an explicit current-request cancellation still
returns `err:cancelled`. The historical six-script return-only adaptation below
is preserved by the runner's explicit legacy authoring switch.

```nu
node examples/member-owner-cancellation/run.mjs /tmp/recovery-before --recovery-baseline
node examples/member-owner-cancellation/test-runtime.mjs /tmp/recovery-before
node examples/member-owner-cancellation/verify.mjs /tmp/recovery-before
node examples/member-owner-cancellation/run.mjs /tmp/recovery-after --recovered
node examples/member-owner-cancellation/test-runtime.mjs /tmp/recovery-after
python examples/member-owner-cancellation/oracle.py /path/to/pinned-rrawrlet /tmp/recovery-after/cases.json /tmp/recovery-after/oracle-evidence.json
node examples/member-owner-cancellation/verify.mjs /tmp/recovery-after
node examples/member-owner-cancellation/prove-recovery.mjs /tmp/recovery-before /tmp/recovery-after
node examples/member-owner-cancellation/run.mjs --replay /tmp/recovery-after
cp -r /tmp/recovery-after /tmp/recovery-moved
node examples/member-owner-cancellation/run.mjs --replay /tmp/recovery-moved
node examples/member-owner-cancellation/verify.mjs /tmp/recovery-moved --replay
```

The eleven scripts retain the original six and add empty integration, raw save,
raw HEAD, typed commit, and a completed stale reader. Virtual/task/root records
agree exactly. The independent Python oracle remains distinct: the first four
original scripts expose owner-policy gaps; the two shutdown scripts chiefly
differ in caller context and harness records, while drainage and release-fault
precedence agree. No comparison erases those differences. See the
[report](../../docs/verification/completed-mutation-recovery-20261013.md) and
[verified closure](fixtures/recovery-verified-closure.json).

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
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-member-owner-cancellation.test.mjs
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
