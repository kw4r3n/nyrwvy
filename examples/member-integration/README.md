# Offline Member integration policy

A bounded next composition after the [Member lifecycle probes](../member-lifecycle/README.md).
Existing products, Results, Sets and effectful folds express the eligibility,
overlap, application, commit, rollback and idempotence policy of rrawrlet's
`Team.integrate`. No Nyrwvy core, runtime or dependency changes are required.

The graph consumes **already-observed eligibility facts** for one candidate and
runs one to three sequential integration requests. It owns the decisions, effect
ordering and immutable ledger. The host supplies synthetic Git effects through
`Git.step(operation, patch): Result<List<String>, String>`; the second argument
carries patch data for apply/rollback and the captured HEAD for save. It contains
no eligibility, overlap or rollback controller. Effect errors are explicit data.

The unchanged pinned Python `Team.integrate`, `get` and `latest` are the oracle,
with genuine held/settled task handles and its real lock. Only external effects
are stubbed. The comparison normalizes errors to phase names and commits to
synthetic labels, and checks exact ordered effects, each request result and ledger.
This does not implement Nyrwvy inside rrawrlet.

## Verified matrix

On verified remote Nyrwvy base `37e512c682b32bbacdf40e9b47f26910f2b59c68` (PR382 merged):

- 12 functions / 272 nodes, authored, reviewed and committed through ordinary tools
- Virtual / reopened / copied-directory replay: 36 scenarios / 46 request observations each
- Unedited emitted C# with installed .NET 10: 36 scenarios / 46 requests
- Unchanged Python `Team.integrate`: 36 scenarios / 46 normalized request matches
- Pre-fix graph and emitted C#: all six save-failure scenarios disagree with Python
- Negative comparison controls: 10/10 rejected
- Release solution build: no warnings/errors; .NET fast 854 and extended 154 pass
- Both adapter typechecks pass; aggregate npm tests: 8 adapter + 37 Node + 12 MCP; Bun 23

Cases cover disjoint/duplicate/empty paths, overlapping edits, empty patches,
settled versus determined status, superseded/discarded candidates, completed
snapshots, idempotence, failures at eleven effect stages, commit rollback,
reverse-check/reverse-apply failures, and a retry after successful rollback.
A rollback failure is returned explicitly; it is not proof of a restored index.
For nonempty and empty patches, save failure returns `Err("save")` with the
captured HEAD retained. Requests two and three return that HEAD as `Ok` without
repeating any effects, including save. HEAD failure retains the old ledger;
a later request can retry the full policy. `Outcome` carries the candidate and
Result independently through the existing effectful fold. No primitive changed.

Per-request graph/C# observations come from fresh deterministic prefix fold runs
of lengths one, two and three; Python records each request in one sequential
loop. Every prefix checks complete candidate fields, cumulative effect order and
payloads. Copy replay changes only the directory, keeping `integration.json` as
the filename and retaining all graph and symbol identities.

## Reproduce

Use the normal repository setup, Node 22.6+ and .NET 10. AuthoringSession must be
built. The Python oracle requires a separate unchanged rrawrlet checkout at
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f`.

```nu
node examples/member-integration/materialize.mjs /tmp/integration-repro
node examples/member-integration/run.mjs --replay /tmp/integration-repro
node examples/member-integration/test-runtime.mjs /tmp/integration-repro
python examples/member-integration/oracle.py /path/to/rrawrlet /tmp/integration-repro
```

Fresh ordinary authoring allocates new stable identities:

```nu
node examples/member-integration/run.mjs /tmp/empty-integration-bundle
```

Reproduce the gap using the pre-fix frozen graph from the verified base (requires
that commit in local Git history). These commands observe the old behavior and
assert exactly six disagreements with the unchanged oracle:

```nu
node examples/member-integration/materialize.mjs --baseline /tmp/integration-before
node examples/member-integration/run.mjs --baseline /tmp/integration-before
node examples/member-integration/test-runtime.mjs /tmp/integration-before --baseline
python examples/member-integration/oracle.py /path/to/rrawrlet /tmp/integration-before --baseline
```

The materializer restores exact frozen identities. The fixture summary records
hashes and sanitized observations; generated C#, private transcripts and binaries
are regenerated locally. C# compilation uses the installed compiler/reference
assemblies without NuGet restore or network access.

## Limits

Eligibility facts are supplied inputs, not live Nyrwvy task polling. This is a
sequential policy slice, not a composed lifecycle/integration controller or a
concurrent lock implementation. Git commands, path decoding, hooks, security,
index/worktree restoration and disk persistence are synthetic. Save failures are
typed synthetic effects; durable recovery after a process restart remains outside
this proof. Raw host exceptions, cancellation during commit/rollback, Leader publication,
provider calls and scope-drain behavior are outside this matrix. Existing
TaskWait caller cancellation and raw provider-fault defaults are unchanged.
PR382's observer cancellation does not prove parent cancellation shielding for
integration/rollback; that remains separate.

See the [save-failure verification report](../../docs/verification/member-integration-save-failure-20261012.md)
for selection evidence, artifact identities and exact boundaries.
