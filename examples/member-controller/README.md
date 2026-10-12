# Serialized offline Member controller

This composes the existing Member lifecycle and integration recipes with the
maintained generic TaskWait observer and CancellationShield. The graph owns the
registry, real task handles, settlement/capacity, continuation/discard eligibility,
HEAD ledger, finish/rollback ordering and request cancellation outcomes. Fake
hosts supply typed effects and deterministic gates. No language/runtime changes.

Twenty-two mixed scenarios / 289 per-request state and cumulative effect
observations agree with unchanged pinned Python. A twenty-third case deliberately
retains a **raw commit exception fidelity gap**. See the
[verification record](../../docs/verification/member-controller-20261012.md).

Run from the repository root with installed dependencies and .NET 10:

```nu
dotnet build Nyrwvy.slnx --configuration Release --ignore-failed-sources -p:NuGetAudit=false -m:1
node examples/member-controller/run.mjs /tmp/controller-fresh
node examples/member-controller/run.mjs --replay /tmp/controller-fresh
node examples/member-controller/test-runtime.mjs /tmp/controller-fresh
python examples/member-controller/oracle.py /path/to/pinned-rrawrlet /tmp/controller-fresh
cp -r /tmp/controller-fresh /tmp/controller-moved
node examples/member-controller/run.mjs --replay /tmp/controller-moved
node --experimental-strip-types --test .opencode/tests/nyrwvy-authoring-member-controller.test.mjs
```

Use an empty directory for fresh authoring. `run.mjs` creates/reviews/commits
ordinary proposals, saves and reopens `controller.json`, re-emits identical C#
and symbols, and executes the saved virtual fixtures. Replay uses those saved
bytes; it does not reauthor. The runtime compiles unedited `Generated.cs` with
installed Roslyn/reference assemblies, without restore/network.

The oracle checks HEAD and working bytes against Git blobs at rrawrlet
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` before import. It invokes actual
`Team.spawn/wait/inspect/stop/integrate/discard/close`, their lock and
`await_cleanup`. Provider, Git, checks, workspace and save effects are synthetic.
The close-member observation wrapper invokes the original method. Finish uses
a gated callback around unchanged `Team.close` under actual `await_cleanup`,
matching the ownership pattern in pinned `runner.py`.

`oracle-expected.json` contains normalized independently observed Python outcomes,
state and ordered cumulative Git-effect traces for the 22 matches, and both sides
of the raw exception disagreement. The maintained Node regression uses that
fixture without requiring a rrawrlet checkout. It checks fresh authoring, copied
replay and real C#. Eight oracle comparison-sensitivity controls are synthetic
record mutations, **not compiled mutants**.

Virtual execution uses scripted delays and scheduler order; real C# and Python
use held gates. Both Nyrwvy paths share the C# emitter/runtime, so their agreement
is not an independent language implementation. No random timing/race equivalence
is claimed. Post-release requests explicitly join the relevant handle before
state audit. Every successful script audits every request, including rejected
requests, and exposes its cumulative effect trace. Observer cancellation leaves
Members alive; owner finish subsequently drains the monitor and Members before
provider-service release and finish acknowledgement.

The fixture covers request-task cancellation, not cancellation of the entire
controller's external parent token. Scope links remain authoritative. Save-error
ledger preservation is in memory; there is no restart/disk persistence claim.
Unexpected host exceptions still fault the owner and bypass typed rollback;
no façade converts them to a domain Result. Real providers, accounts, Git/index
mutation, physical rollback, credentials and Library uploads are outside scope.
