# Raw HEAD-read exception — verified closure, 2026-10-12

The existing composition already matches unchanged pinned Python for a raw
HEAD-read exception before ledger capture. **No RED was established and no
implementation repair was made.** Four cases add nonempty/empty patch ×
clear/pending cancellation, with only the first mocked HEAD read throwing.
All **42 scenarios / 609 request observations** match virtual execution and
unedited emitted C#. The prior **38 scenarios / 537 independent expectations**
remain unchanged.

## Local baseline and preserved work

The separate worktree is on `verify/raw-head-read`, starting at commit
`0844516f0e94b1fcad7b57d2e1aba3b21a329cf8`, tree
`558556058476fe83c85c4e927f6864a66881fea7`. It retains both inherited repairs:
explicit raw commit exception rollback (`812bfe2`) and the save-only boundary
that preserves the captured ledger (`0844516`). Neither authoring composition,
Nyrwvy core/runtime, dependencies nor existing fault-host behavior changed.
The mock hosts add only a first-HEAD raw throw for the new scenarios.

The `feat/raw-fault-cleanup` and `fix/raw-save-ledger` branches and their user
checkouts are preserved. The exact pending `812bfe248e6753dcd86876519b168a8424f5eb4f`
payload remains unchanged:

| Local payload | SHA-256 |
| --- | --- |
| `raw-fault-cleanup.bundle` | `087481853e5f0336f2927e58e78964292d3ceef98e7aef073b592080675988c0` |
| `raw-fault-cleanup.patch` | `36a2a6b019dcd209cc833eb350441619e635a5004a9b5e678625e9420893db37` |

No push, PR, publication, merge or alternate export route ran. Repository and
ancestor instructions were checked; no repository `AGENTS.md`, `.agents` rules
or skill files were present. All shell work after shell discovery used Nushell.
Installed dependency files were copied locally; .NET restored from its installed
cache with an empty local package source, without a network source.

## Source derivation and observations

The oracle verifies HEAD and working `team.py`, `codex.py`, `runner.py` bytes
against `4bab9e030744a0887c1ee13bdd8fc8db3b98334f` before importing them. Actual
`Team.integrate`, lock, lifecycle methods and `await_cleanup` execute; only
external effects and deterministic gates are synthetic.

In pinned `team.py` lines 1058–1096, successful nonempty apply precedes commit.
Only the commit call is inside `except BaseException`, which invokes shielded
reverse-check/reverse-apply. The later HEAD read is outside that boundary.
`attempt.integrated` is assigned only after the read returns, followed by save.
In `codex.py` lines 21–33, `await_cleanup` retrieves `task.result()` before it
raises pending caller cancellation. The ordinary raw HEAD error therefore wins.
The empty-patch path reaches the same HEAD read without apply or commit.

Every new case proves:

- First integration returns `err:raw head fault`, even after repeated caller
  cancellation at a held HEAD boundary. Its immediate normalized Member record
  remains completed/snapshotted/checked with an empty integrated field.
- The first effect trace ends at HEAD: a patch has one successful check/apply/commit;
  an empty patch has none. Neither saves nor runs reverse effects.
- The peer remains running and later serialized requests remain available.
  First retry repeats exactly the full validation and integration trace, then
  captures HEAD and saves once. Thus a patch commits twice in the total synthetic
  trace; an empty patch never commits. Two HEAD reads and one save occur.
- A third integration returns the captured HEAD without new effects. Continuation
  is then rejected as integrated. Worker cleanup occurs once; finish drains before
  provider release, releases once and acknowledges afterward.

The [closure fixture](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-controller/fixtures/raw-head-verified-closure.json)
contains all four baseline virtual, real and independent Python per-request
traces, plus real open/close counts and source/artifact hashes. It uses exactly
the previous saved graph, symbols and Generated.cs bytes, reexecuted with the
four added host cases. That unchanged-graph proof establishes correct existing
propagation before fresh authoring.

## Verification

Fresh ordinary proposals still author **89 functions / 1,942 nodes**. Reopen,
same-bundle and copied-directory replay preserve graph, emission and symbol
bytes. Real runtime compiles Generated.cs unchanged using installed Roslyn and
reference assemblies, with no restore/network. All baseline/fresh/replay/copied
normalized records match the independent fixture. Virtual and real execution
share the generated C# emitter/runtime; Python is independent.

Twenty comparison sensitivity controls invoke the actual equality assertions.
Seven new controls reject invented HEAD, cancellation hiding the fault,
spurious rollback, premature save, an effect-free first retry, extra effects on
the third request and a commit for an empty patch. These mutate normalized
records; they are not compiled graph mutants.

| Check | Result |
| --- | --- |
| Release solution build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| npm adapter / Node / MCP | 8 / 40 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Partition / whitespace | Disjoint/exhaustive / pass |
| Default lifecycle / integration | 15 / 36 scenarios, virtual and real |
| Generic exception fixture | Four virtual cases / thirteen real controls, through Node aggregate |
| Controller / pinned Python | 42 / 609 request audits, exact |

The fresh checkout initially lacked .NET assets. Sandboxed offline restore then
exited without diagnostics; expanded local execution completed offline restore,
build and tests. Expanded execution also supports child-process waits and VSTest
local sockets. No configuration or dependency versions changed.

Exact artifact hashes, source proofs and preserved composition hashes are in the
maintained [summary](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-controller/fixtures/verification-summary.json).

## Limits and next bounded probe

This proves bounded serialized requests and ordinary raw exceptions with
cooperative synthetic effects. It does not prove physical Git/index rollback,
save durability/restart recovery, concurrent admission/lock equivalence,
external parent cancellation, all Python BaseException classes or forced
completion of non-cooperative hosts. No provider, account, credentials,
physical integration Git/index effects or Library upload ran.

The next meaningful bounded probe is a **raw apply exception after successful
check, before commit**, nonempty patch × clear/pending cancellation with only
the first apply throwing. Compare the original error, empty ledger, subsequent
retry effects, commit rollback eligibility, peer survival and finish ordering.
This is a proposed probe, not an established RED gap. Real persistence/security
work remains outside the authorized scope.
