# Serialized offline Member controller

The graph owns registry/handles, settlement-based eligibility, the integration
ledger, rollback, request cancellation and finish/drain/release ordering. Mock
hosts supply effects and gates. An explicit generic ExceptionCatch boundary now
preserves a raw commit exception through shielded rollback, then lets the request
worker handle the fault without poisoning the owner or its peer.

All **42 scenarios / 609 per-request state/effect observations** match unchanged
pinned Python in virtual execution and unedited emitted C#. The original
23rd-case mismatch is retained as baseline RED evidence in the
[verification record](../../docs/verification/member-controller-20261012.md) and
[summary](fixtures/verification-summary.json). Four further raw save cases were
RED against pending local commit `812bfe248e6753dcd86876519b168a8424f5eb4f`;
their [verification record](../../docs/verification/raw-save-ledger-20261012.md)
and [baseline traces](fixtures/raw-save-baseline-red.json) preserve that evidence.
Four raw HEAD-read cases already match Python on the unchanged saved graph and
unedited C# from local `0844516`; their [verified closure](fixtures/raw-head-verified-closure.json)
and [verification record](../../docs/verification/raw-head-read-20261012.md)
require no implementation repair. The prior 38 scenarios / 537 expectations remain unchanged.

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

Fresh authoring requires an empty directory. Ordinary proposals create, review
and commit the graph; the harness saves/reopens it and checks identical emission
and symbols. Replay uses saved bytes. The runtime compiles unedited Generated.cs
with installed Roslyn/reference assemblies, without restore or network.

The oracle checks HEAD and working source bytes against rrawrlet
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` before importing actual Team methods,
their lock and await_cleanup. Provider, Git, checks, workspace and save effects
are synthetic. The close-member wrapper invokes the original method. Gated
finish calls unchanged Team.close under actual await_cleanup. Python save is
synchronous; its stub requests cancellation before returning/raising.

The independent expected fixture records every request's Result/rejection, nine
normalized Member fields and cumulative ordered Git-effect/payload trace.
Regression tests compare fresh authoring, copied replay and real C# against it.
Comparison sensitivity controls mutate normalized observations, not compiled code.

Raw commit failure, typed/raw rollback failures and cancellation during commit or
either reverse phase all preserve source precedence. A held peer remains running;
subsequent serialized requests and owner finish remain available. An exception
outside the explicit request boundary still propagates normally. The generic
[exception fixture](../exception-boundary/README.md) additionally checks OCE
identity/stack, typed abort, context faults, nested restoration, held drainage,
outer resource release and direct TaskCancel. A separate save-only ExceptionCatch
receives the HEAD-bearing candidate in both callbacks. A raw save fault retains
that candidate and its original error, even with pending cancellation. Retry
performs no Git/save effects, and continuation of the integrated Member is rejected.
For a raw HEAD-read fault, the ledger remains empty, no save runs and no rollback
is eligible: Python's rollback boundary ends at commit. The original fault wins
pending cancellation. Retry repeats the full integration, including commit for a
nonempty patch; empty patches never commit. After successful retry, the next
request performs no effects and continuation is rejected. These are synthetic
effect traces, with no physical index or persistence recovery claim.

Virtual and real execution share the same C# emitter/runtime. Independent language
evidence comes from unchanged Python. Fixed virtual delays and held real/Python
events avoid comparing arbitrary wall-time races. The proof covers bounded
serialized requests, not concurrent admission, cancellation of the entire external
controller parent, provider shutdown, physical Git/index rollback, or restart
recovery. Save-error ledger retention is in memory. The next proposed bounded
probe is a raw apply exception after successful check, before commit, with
clear/pending cancellation and only the first apply throwing. This is not an
established mismatch.

No provider, account, credentials, physical integration Git mutation or Library
upload is used. Branch publication is separate from the mocked integration effects.
