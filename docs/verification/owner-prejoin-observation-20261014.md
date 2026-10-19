# Owner pre-join observation repair — 2026-10-14

The integration harness now joins peer Member 2 before requiring its terminal
retained state. It adds `stop("settled-b", 2)` using the existing Member task
join. The previous script joined integration request tickets but could inspect
the cancelled Member while its synthetic provider was still returning. Both
`running` before completion and `stopped` after completion were legitimate;
the old exact comparison assumed the latter without establishing its order.

This bounded repair starts from merged main
`e10881973240cae1f340511af1414fbe27107ae3` on
`fix/owner-prejoin-observation`. It changes request scripts, synthetic host
scheduling, assertions and documentation. Authoring recipes, generated runtime,
core semantics, primitives and cancellation policy are unchanged. The retained
view is inside the owner scope after the peer join; it is not an authoritative
registry API after scope exit.

## Deterministic evidence

Independent review of PR392 reproduced the old failure in task and external-root
cancellation modes by holding Member 2's provider after it saw cancellation
until the `retained` reply. Drainage, provider closure, Member cleanup and
once-only resource release passed. The unchanged verifier failed its exact
`running`/`stopped` comparison. The review also established byte-identical base
emission for its graph (SHA-256
`bf3195b69e92cf365f2a06693f5236d010effe97f0781142402e29d993abf8e5`).
Those original logs, script, template, graph and runtime records are preserved
under `artifacts/owner-prejoin/original-review`; their hashes are in the new
closure. That graph is distinct from the freshly authored default bundle.

The maintained [proof runner](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/member-owner-cancellation/prove-prejoin.mjs)
executes the same unedited Generated.cs in three real scheduling variants:

| Schedule | Script and barrier | Required observation |
| --- | --- | --- |
| `retained` | Remove the new peer join; hold its provider until retained reply | Old terminal comparison RED; only the peer view differs |
| `observed` | Inspect peer while held, release at `@prejoin`, then join | Exact pre-completion `running`; exact retained `stopped` |
| `early` | Release peer alongside the held mutation, then join | Exact retained `stopped` |

Each clear/fault integration script runs in both cancellation modes. The six-case
matrix has **4 deterministic RED and 8 GREEN controls**. The recovered
eleven-case matrix exercises all seven integration controls in both modes,
giving **14 RED and 28 GREEN controls**. The default host additionally holds
the peer until `stop-join:2`, so removing the completion barrier cannot pass by
lucky scheduling. Barrier timeouts bound failures; they are not scheduling sleeps.

All cases retain exact owner outcomes, mutation tails/rollback, worker effects,
once-only cleanup/release and drainage. The proof rejects a record mutation that
substitutes `running` for the joined retained `stopped`. A raw `@prejoin` reply
is asserted explicitly and remains separate from the final projection. Existing
historical fixtures and their hashes remain intact; the verifier adds only the
new literal `settled-b` reply to their expectations and keeps every original
golden field exact.

The normal six-script bundle has twelve real runs, and the recovered eleven-script
bundle has twenty-two. Actual Python is rerun against
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f`; the oracle checks HEAD and exact
Git source bytes before import. All existing Python contracts and divergences
remain. No whole-shutdown parity claim follows from this harness repair.

## Reproduction and verification

Use a fresh evidence directory and the pinned Python checkout:

```nu
node examples/member-owner-cancellation/run.mjs /tmp/owner-joined
node examples/member-owner-cancellation/test-runtime.mjs /tmp/owner-joined
python examples/member-owner-cancellation/oracle.py /path/to/pinned-rrawrlet /tmp/owner-joined/cases.json /tmp/owner-joined/oracle-evidence.json
node examples/member-owner-cancellation/verify.mjs /tmp/owner-joined
node examples/member-owner-cancellation/prove-prejoin.mjs /tmp/owner-joined /tmp/owner-schedules
node examples/member-owner-cancellation/run.mjs --replay /tmp/owner-joined
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-member-owner-cancellation.test.mjs
```

The [closure](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/member-owner-cancellation/fixtures/prejoin-verified-closure.json)
records serial regression results, Python source proofs, unchanged emission
identities, original failure evidence, same/moved replay and artifact hashes.
The independent review checkpoint records the exact local commit and tree.

| Check | Result |
| --- | --- |
| Full offline Release rebuild | Zero warnings/errors |
| Focused cancellation/exception/snapshot/task checks | 38 pass |
| .NET fast | 864 pass |
| .NET extended | 123 Core + 31 Extended pass |
| Serial Node aggregate | 46 pass, including owner, snapshot and state recovery |
| Adapters / serial MCP | 8 / 12 pass |
| Explicit Bun TypeScript tests / both typechecks | 23 pass / both pass |
| Suite partition / fish contract | Disjoint and exhaustive / pass |
| Actual pinned Python | Six owner + eleven recovery contracts pass; divergences retained |
| Controlled pre-join schedules | 18 RED + 36 GREEN across both matrices and both modes |

One overly broad Bun directory selector started redundant `.mjs` regressions.
That owned process tree was stopped; its interrupted log and nonzero result are
retained rather than counted as a pass. The corrected run selects the four
maintained TypeScript test files explicitly. Bun uses the installed 1.4.3;
the repository CI pin is 1.4.2. No dependency was installed or changed.

## Limits and handoff

Virtual and real paths share the emitter/runtime. Real schedule controls use
explicit provider barriers and graph task joins; they are bounded scheduling
evidence, not a proof of arbitrary interleavings. Python policy gaps, provider
shutdown policy, closed handoffs, fairness, physical Git recovery and restart
persistence remain outside scope. No new primitive or runtime change is needed.

Nushell orchestrates installed local tools. The SDK was rebuilt from the isolated
checkout with copied cached dependencies and no restore. Expanded local execution
allows Node child waits and VSTest loopback sockets; the sandbox returned EPERM
for a successful child exit. No automatic approval review rejected an action.
Source/user checkouts are preserved. This checkpoint is a local commit for
independent parent review; push is authorized only after that review, and the
parent handles the PR. No push, branch deletion or merge occurred here.
