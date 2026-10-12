# Offline Member integration policy

A bounded next composition after the [Member lifecycle probes](../member-lifecycle/README.md).
Existing products, Results, Sets and effectful folds express the eligibility,
overlap, application, commit, rollback and idempotence policy of rrawrlet's
`Team.integrate`. No Nyrwvy core, runtime or dependency changes are required.

The graph consumes **already-observed eligibility facts** for one candidate and
runs one or two sequential integration requests. It owns the decisions, effect
ordering and immutable ledger. The host supplies synthetic Git effects through
`Git.step(operation, patch): Result<List<String>, String>`; the second argument
carries patch data for apply/rollback and the captured HEAD for save. It contains
no eligibility, overlap or rollback controller. Effect errors are explicit data.

The unchanged pinned Python `Team.integrate`, `get` and `latest` are the oracle,
with genuine held/settled task handles and its real lock. Only external effects
are stubbed. The comparison normalizes errors to phase names and commits to
synthetic labels, and checks exact ordered effects, final result and ledger.
This does not implement Nyrwvy inside rrawrlet.

## Verified matrix

On Nyrwvy base `44835914c6f10d845facbe872d8be660c5fd5e53` (PR380 merged):

- 12 functions / 231 nodes, authored, reviewed and committed through ordinary tools
- Virtual / reopened / moved-directory replay: 28/28 each
- Unedited emitted C# with installed .NET 10: 28/28
- Unchanged Python `Team.integrate`: 28/28 normalized matches
- Negative comparison controls: 6/6 rejected
- Release solution build: no warnings/errors; .NET fast 854 and extended 154 pass
- Both adapter typechecks pass; aggregate npm tests: 8 adapter + 36 Node + 12 MCP

Cases cover disjoint/duplicate/empty paths, overlapping edits, empty patches,
settled versus determined status, superseded/discarded candidates, completed
snapshots, idempotence, failures at eleven effect stages, commit rollback,
reverse-check/reverse-apply failures, and a retry after successful rollback.
A rollback failure is returned explicitly; it is not proof of a restored index.

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

The materializer restores exact frozen identities. The fixture summary records
hashes and sanitized observations; generated C#, private transcripts and binaries
are regenerated locally. C# compilation uses the installed compiler/reference
assemblies without NuGet restore or network access.

## Limits

Eligibility facts are supplied inputs, not live Nyrwvy task polling. This is a
sequential policy slice, not a composed lifecycle/integration controller or a
concurrent lock implementation. Git commands, path decoding, hooks, security,
index/worktree restoration and disk persistence are synthetic. Save failure,
raw host exceptions, cancellation during commit/rollback, Leader publication,
provider calls and scope-drain behavior are outside this matrix. Existing
TaskWait caller cancellation and raw provider-fault defaults are unchanged.

See the [verification report](../../docs/verification/member-integration-20261012.md)
for selection evidence, artifact identities and exact boundaries.
