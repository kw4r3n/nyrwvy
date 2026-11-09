# Verification philosophy

**Status:** Current

Nyrwvy is both an implementation project and an experimental investigation into AI-native semantic programming. Verification therefore has two jobs that should not be confused:

1. detect regressions in code and maintained contracts; and
2. generate evidence about whether an architectural or interaction hypothesis actually works.

The second job can justify expensive controlled experiments. It does not make those experiments a routine cost for every later feature.

## Reproducible tooling and shell checks

The .NET SDK is selected by [`global.json`](../../global.json): the pinned
version or any newer patch of the same feature band (`rollForward:
latestPatch`), with prerelease SDKs excluded. CI uses that same file. A
different feature band or major version is a deliberate repository change;
verify the build/tests with that version when updating the pin.
[`Directory.Build.props`](../../Directory.Build.props) owns the shared F# target
framework and warnings-as-errors policy; project-specific executable/test flags
remain in their projects. Generated C# projects do not inherit this F# policy.

GitHub Actions minutes are billed, so the only workflow,
[`ci.yml`](../../.github/workflows/ci.yml), does not run on push or pull request: it
runs only on demand (`workflow_dispatch`), at major milestones (owner decision,
2026-10-05). The extended suite runs locally only; its GitHub workflow was removed
on 2026-10-17 (owner decision). Before
opening a pull request, run the fast suite and the tests related to the change
in a local or cloud session; a PR does not wait on GitHub Actions.

The verification entry points require Node 22.6+ (22.22.2 recommended, see
`.node-version`); fish 3.4+ is needed only for the `.fish` wrapper scripts,
and the README gives the direct `dotnet test` and
`node --test` equivalents. The CI job runs, and a local pre-PR check mirrors,
these steps after building the fast test graph and installing the locked Node
dependencies (`npm ci`):

```sh
npm run typecheck
npm run test:adapters
npm run test:bun
git ls-files -z '*.fish' | xargs -0 -r -n 1 fish --no-execute
fish scripts/test-fast.fish
npm --prefix tools/nyrwvy-mcp test
```

The `test-fast.fish` wrapper includes ordinary authoring, authoring transport, and script contracts through the shared Node discovery runner. Root `npm test` runs only fast groups, adding the adapter and MCP contracts.

The `test-extended.fish` wrapper is separate. It runs xUnit extended and then the heavy Node pipelines in `.opencode/tests-extended/`, passing extra arguments only to dotnet and stopping on either stage's failure. `npm run test:node:extended` runs those Node pipelines alone, with Node's default file concurrency and their per-test timeouts. Before the Node stage the wrapper runs the suite partition audit (`node scripts/check-test-suite-partition.mjs`), which lists the built test assemblies. To spread the Node stage over several terminals or machines, run `node scripts/run-node-tests.mjs extended --shard <index>/<total>` once per index: shards assign the sorted extended files round-robin and together cover every file once. Keep one test file at a time per shard rather than raising `--test-concurrency`, since the timing-sensitive Member and concurrency pipelines would otherwise compete for the same cores. Tests that compile and run
generated C# many times for a finished feature (the `runPure` behaviour tests
of Map, Set, shared `MatchUnion` arms, `ListMinBy`, the List random sequences)
are Extended; the fast suite keeps their checking and persistence tests and one
compiled smoke test per runtime path. Run the extended suite locally when
a change touches the C# backend runtime. The CI job has a 20-minute timeout, so a hung transport
test cannot hold a runner for the default six hours. The freeze-ref materialization
helper is syntax-checked only; discovering a `.fish` file does not authorize executing a
model, changing Git refs, or running frozen workloads in routine CI.

Node runner and audit share directory-based ownership in `scripts/node-test-suites.mjs`. The audit discovers all Git-visible `*.test.mjs`, including new untracked tests and excluding ignored generated outputs, and rejects unassigned files or overlapping rules. Example-local tests retain explicit manual ownership and their documented commands; the latest offline recovery matrices remain opt-in manual reproduction. Observed suite sizes belong in verification evidence, not a maintained count gate. `--node-only` audits Node ownership without building .NET.

## Evidence is reusable

A completed experiment establishes evidence under a set of assumptions. Reuse that evidence until a later change materially invalidates those assumptions.

Do not repeat a large controlled evaluation simply because a new semantic case was added. First ask whether the new case changes the interaction model that the earlier evaluation tested.

For example, the completed M1/M2 evidence supports the tested model of stable-ID targeting, semantic inspection, simulation, review, system-owned identity allocation, stale rejection/recovery, and exact committed-state verification. Adding another operation that uses the same model normally needs regression and integration verification, not another 30-attempt model tranche.

A substantially more general structural authoring surface, merge/rebase workflow, different authority model, or another new interaction hypothesis may justify a new experiment.

## Three verification levels

### 1. Routine contract verification

Use during ordinary development and routine CI.

Purpose: detect regressions in code and maintained contracts quickly and cheaply.

Typical scope:

- type checking and build;
- focused semantic/workflow tests for the changed boundary;
- normal fast test suite;
- codec/fingerprint/query/backend contract tests where relevant.

Rules:

- keep the fast suite small enough to run frequently;
- do not add tests merely because a branch or line was added;
- prioritize important contracts, boundaries, state transitions, and real regressions;
- do not run the full solution after every edit when focused verification covers the changed boundary.

The current routine entry point is:

```shell
./scripts/test-fast.fish
```

### 2. Milestone or architectural verification

Use at meaningful completion boundaries or after changes that alter important architecture/semantics.

Purpose: establish that the integrated system still supports the claim being made at that milestone.

Typical triggers:

- a milestone or major slice reaches its completion criteria;
- transaction/publication semantics change;
- an important semantic representation or lowering boundary changes;
- a new backend-relevant semantic class is completed;
- a large refactor crosses several established boundaries;
- a previous regression demonstrates that the fast suite does not cover an important integration contract.

Typical scope:

- fast suite plus the relevant extended suite;
- clean build/verification when stale artifacts could hide a problem;
- selected end-to-end execution or artifact checks that exercise the changed boundary;
- live/environment-dependent checks only when that environment is actually part of the claim.

The current extended entry point is:

```shell
./scripts/test-extended.fish
```

While iterating, `NYRWVY_ZIG_RUNNER_OPTIMIZE=Debug` makes the host build Zig `runPure` runners in Debug instead of ReleaseSafe: about 0.4 s per runner instead of 5.5 s (ReleaseSafe runners are built without debug info, `-fstrip`, which took them from 16 s; a crash reply reads only the runner's `panic:` line). Any other value is ignored, and the mode is part of the runner cache key. Run without it before merging a change to the emitter or its runtime, because the runners a program actually gets are ReleaseSafe.

For an opt-in full-solution build/test with a shareable log, use `bash scripts/verify-local-20260825-1427.sh --clean`. This includes the extended project once and is not the routine CI fallback.

Verification wrappers should select an existing suite once, not run the whole suite and then repeat its modules with fixed historical test-count gates. When tests move between projects, update their suite ownership rather than preserving stale per-module selectors. Put observed counts in verification evidence, not in a second test inventory maintained by shell scripts.

Do not permanently move every milestone check into routine CI after it has served its evidence purpose.

### 3. Experimental validation

Use when Nyrwvy introduces or challenges a design hypothesis whose practical value cannot be established by deterministic tests alone.

Purpose: answer a research question, not merely prove that code executes as implemented.

Examples:

- whether agents can reliably discover and use a new class of semantic-authoring interface;
- whether substantially broader structural authority remains understandable and reviewable;
- whether a new conflict/merge interaction can be recovered from autonomously;
- whether a representation change materially improves or harms agent reliability.

Controlled model evaluations, frozen corpora/conditions, repeated fresh sessions, durable evidence packages, and strict pre-result sampling decisions belong here when they improve the credibility of the experiment.

They are intentionally expensive and should be used when the expected information gain is high.

## Classify the change before choosing tests

Use this decision rule:

```text
change
  |
  +-- Does it implement/refactor an already evidenced contract or interaction model?
  |      -> focused + fast verification
  |
  +-- Does it materially change an important semantic/architectural boundary?
  |      -> milestone/extended verification
  |
  +-- Does it introduce a new practical hypothesis about how agents can use Nyrwvy?
         -> experimental validation when deterministic evidence is insufficient
```

These levels can overlap at a major milestone, but they should not collapse into one always-on suite.

## Frozen evaluation evidence

A frozen evaluation revision is an immutable record of what was tested. Preserve enough tooling and metadata to interpret or replay that record when useful.

It is not automatically the maintained product protocol.

The evaluation-only runners, their OpenCode bridges, preflight/runner scripts and frozen corpora were removed from `main` on 2026-10-18. Commands in historical records that name them run from the [evaluation freeze tags](../operations/evaluation-freeze-tags.md) or from commit `b3a958a`, the last `main` commit that contained them.

Historical quirks discovered after a run may remain exactly as recorded without forcing current production behavior to preserve them. If a later maintained interface improves proposal cleanup, transport, ergonomics, or another orchestration detail without changing the underlying evidenced interaction model, focused regression plus milestone verification is normally sufficient.

For example, retiring a proposal handle that can never succeed unchanged after `StaleBaseSnapshot` is orchestration lifecycle cleanup. By itself it does not create a new semantic-authority hypothesis requiring a fresh large controlled model tranche.

## Live and environment-dependent validation

Treat real model runtimes, operating-system integration, external services, and other environment-dependent checks separately from deterministic fast/extended tests.

Run them when the feature or claim actually crosses that boundary. Once a boundary has been sufficiently verified, do not ask for the same manual/live validation again unless related implementation has changed.

Prefer a single scripted validation command over a sequence of manual commands when repeated real-environment checking is necessary.

## Completion rule

Do not measure confidence by the total number of tests executed.

A change is sufficiently verified when the completion criteria for its actual claim are met with the smallest credible evidence set. At an important research or milestone boundary that evidence may be deliberately rigorous. During ordinary development it should remain focused and inexpensive.

## Opt-in change verification checks

The maintained `verifyChange` contract is in
[agent-authoring-session](../specs/agent-authoring-session.md#opt-in-change-verification).
Classifier/guard/UI tests run in the existing fast partition; real candidate
execution, canonical expectations, effect traces, host refusal and proposal
lifecycle tests run in the extended partition. The existing MCP contract test
also compares a pending candidate without publication. No new CI workflow or
commit gate is required. The standalone acceptance script and practical trial
remain reproducible supplementary evidence, not the only product regression tests.
