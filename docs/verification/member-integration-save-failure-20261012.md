# Member integration ledger after save failure

The Member integration example now retains captured HEAD when save returns an
error. Its next request returns that HEAD without repeating Git or save effects.
Existing `Outcome`, `Result`, `productUpdate`, bindings and `listEffectFold`
express the change. Core/runtime/tool sources and dependencies are unchanged.

## Source evidence and baseline

Remote `main` was fetched and confirmed at PR382 merge
`37e512c682b32bbacdf40e9b47f26910f2b59c68`, tree
`dee2939eab31d3e3b6138ba63e8b9f2a2f62d335`. Its `author.mjs` blob is
`7afafa92d960eab2adff470d266118a5d6b4ebe8`; its pre-fix frozen graph blob is
`16f63be6f6ada2036af461a6e8f25c3ac3a7272c`.

The unchanged rrawrlet oracle is
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f`, tree
`b26815ab2cc17c1e045f3cde1dc896b265c23851`, `rrawrlet/team.py` blob
`3dc217e290dc60db3ca20ff0c2fe32cbfc8cb991`, SHA-256
`25e6fc65c0947eb74e12425c6c74664bd0c0adc3b80114223c4eabb651a8ccf4`.
The harness checks working source bytes against that blob before importing it.
Around lines 1090–1096, `Team.integrate` assigns `attempt.integrated` after HEAD
succeeds, then saves. A save exception retains that assignment. An eligible later
call returns it before any Git/save effects. The pre-fix Nyrwvy graph updated
Candidate only on `Ok`, losing this ledger on save `Err`.

Before changing the recipe, the new strict check failed with actual ledger `""`
versus expected `"integrated-head"`. Observation mode then proved exactly six
save-failure scenario disagreements for the unchanged recipe and its unedited
emitted C#: nonempty/empty patches with one, two or three requests. All other
30 scenarios agreed. On request one, both return `Err("save")`, but only Python
retains HEAD. On retry, the old graph repeats effects; Python returns `Ok(HEAD)`
with the same effect trace. The committed
[baseline observations](../../examples/member-integration/fixtures/baseline-comparison.json)
retain both sides, including the first error before the later successful result.
The historical frozen pre-fix graph also reproduces those same six disagreements
through the [baseline materializer](../../examples/member-integration/README.md).
Fresh authoring and the historical fixture allocate different stable IDs.

## Implementation and request observations

Policy functions return the existing `Outcome { candidate, result }`. `record`
binds the updated candidate immediately after HEAD succeeds, then returns it on
both save `Ok` and `Err`. Earlier failures carry the original candidate.
`attempt` passes the complete outcome through; the existing effectful fold uses
its candidate on the next request regardless of its Result.

| Boundary | First request | Later requests | Effects |
| --- | --- | --- | --- |
| Nonempty patch, save error | `Err(save)`, integrated HEAD retained | `Ok(integrated-head)` | Check/apply/commit/HEAD/save once |
| Empty patch, save error | `Err(save)`, source HEAD retained | `Ok(source-head)` | HEAD/save once; no apply/commit |
| HEAD error | `Err(head)`, ledger remains empty | Fresh integration can succeed | Full policy retried; no save on failed HEAD |
| Commit error | Original candidate; commit error after successful rollback | Fresh retry can succeed | Reverse-check then reverse-apply |
| Rollback error | Original candidate; rollback phase error | Physical restoration not established | Stops at failing reverse effect |

Every graph/C# request checks every candidate field, Result, cumulative ordered
effects, and exact patch/save payloads. To observe the existing fold without
adding a runtime observer, the harness reruns deterministic request prefixes
of lengths one through the scenario's count, from fresh candidate and fake-host
state. Python records actual requests sequentially under its unchanged lock,
with real held/settled task handles. Prefix observations therefore compare each
request, not only the final successful result.

## Local verification

| Verification | Result |
| --- | --- |
| Fresh authored graph, virtual execution | 36 scenarios / 46 request observations |
| Reopened same snapshot | 36 / 46 |
| Copied-directory replay | 36 / 46 |
| Unedited emitted C# | 36 / 46 |
| Unchanged Python normalized comparison | 36 / 46 |
| Fixed frozen materializer reproduction | 36 / 46, C# and Python agree |
| Pre-fix frozen materializer reproduction | Exactly six save scenario mismatches |
| Negative comparison controls | 10 rejected |
| Release solution build | Zero warnings/errors |
| .NET fast / extended | 854 / 154 (123 Core + 31 Extended) |
| npm adapter / Node / MCP | 8 / 37 / 12 |
| Bun | 23 |
| Adapter and Bun typechecks | Both pass |
| Suite partition and fish checks | Pass |

The eight added scenarios cover save failure for both patch shapes with one,
two and three requests, plus HEAD failure followed by retry for both shapes.
The prior 28 scenarios, including eligibility precedence and every commit/
rollback failure case, still pass. Four added comparison controls detect lost
ledger for either patch, hiding request one's save error while leaving the final
success unchanged, and repeating save on retry while leaving the final result
unchanged. These are comparison-sensitivity controls, not compiled mutants.

The copied replay changed the directory from `fixed-ledger` to `copied-ledger`.
The filename remained **`integration.json`**. This does not claim filename-rename
coverage. Graph bytes, snapshot/semantic fingerprints, symbols and re-emitted
C# remained equal. All graph changes were authored/reviewed/committed through
ordinary tools; saved graph JSON and emitted C# were not edited by hand.
The [fixture summary](../../examples/member-integration/fixtures/verification-summary.json)
records sanitized per-request observations and artifact hashes.

| Artifact | SHA-256 |
| --- | --- |
| Saved graph | `f1e6ee4cc72b9658c4e9869ca247aac12af74de09dbaf097c9555eefbbb78f25` |
| Emitted C# | `5d1c2992cc4e793ae2e02a25de85b5485ac5ed65b088dbf69aa8ab493def0f00` |
| Symbol manifest | `2fcfa4aa326f04a4d6b1ed5047c02f73e0d9baae05bc586400162fd32e850942` |
| Committed compact cases | `496e7520811aa876666793f8cfa9fa403fd311192b57fdcb85537bed733e110c` |

The graph has 12 functions / 272 nodes. Validation used local SDK 10.0.112,
runtime/reference pack 10.0.12, installed Roslyn, Node 26.11.1, Python 3.14.7,
Bun 1.4.3 and Nushell 0.116.1. Generated-runtime compilation uses installed
assemblies without restore or network. Initial sandboxed MSBuild failed without
diagnostics; the same local build succeeded with execution permission. The first
dependency copy omitted workspace-scoped MCP packages; copying those packages
resolved the typecheck failure. Neither attempt is counted as a pass. No saved
cloud environment or workflow dispatch was used.

## Limits

This is sequential policy over supplied eligibility facts and typed synthetic
effect errors. It preserves the in-memory ledger after a save error; it does not
prove disk persistence or recovery after a process restart. External effects are
stubbed, including clean, Git, validation, provider close and save. No actual
provider or Git integration mutation occurred. Real binary patch transport,
path security, index/worktree restoration, concurrent lock equivalence, lifecycle
composition and Leader publication remain outside the proof.

Parent cancellation shielding for integration/rollback remains a separate task.
PR382's TaskWait observer cancellation does not establish it. Cancellation and
raw host exceptions during commit/rollback, provider ownership and scope-drain
behavior are unverified here. This change includes no task-3 late-source-error
artifacts. The [original integration report](member-integration-20261012.md)
remains the historical 28-case record whose save-failure exclusion this follow-up
addresses.
