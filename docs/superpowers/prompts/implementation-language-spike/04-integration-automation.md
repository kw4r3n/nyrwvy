# Setup Session 4 — Integration, Evaluation, and Automation

You are integrating two independently implemented baselines into one controlled F# versus C# experiment. You may inspect both baselines in this session, but you must not redesign them merely to make them stylistically similar.

Read completely:

- `docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike.md`
- `docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md`
- every setup prompt in this directory;
- every shared contract and acceptance file;
- both baseline public APIs and tests.

## Scope

Complete and integrate:

- Task 5 from the main plan;
- Task 6 from the main plan;
- Tasks A2, A3, and A4 from the automation addendum;
- baseline-equivalence auditing needed before Gate 1.

Expected areas include:

```text
experiments/implementation-language-spike/
├── evaluator/
├── prompts/
├── scripts/
│   ├── preflight.py
│   ├── prepare_workspace.py
│   ├── run_trial.py
│   ├── run_public_checks.py
│   ├── summarize_events.py
│   ├── run_experiment.py
│   ├── verify_frozen_inputs.py
│   └── create_review_packet.py
└── tests/
```

## Baseline handling

First audit both baselines against the language-neutral contract.

Allowed baseline changes:

- correction of an objective shared-contract violation;
- correction of a baseline test that fails to test the documented contract;
- mechanically necessary public API alignment when the shared contract already requires it.

Not allowed:

- rewriting one baseline to resemble the other;
- introducing abstractions solely to equalize line count;
- weakening one side because the other side lacks a feature;
- changing the maintenance task after observing which baseline is easier to extend;
- changing hidden checks to match an implementation quirk.

For every baseline correction, check whether the equivalent issue exists in the other baseline and record the result.

## Evaluator requirements

- Public checks run build, baseline tests, canonicalization, protected-file checks, project-setting checks, and forbidden-pattern scans.
- Hidden evaluator source is absent from a trial workspace until the Codex trial process has exited and `codex-exited.marker` exists.
- Hidden checks verify all four maintenance phases from the main plan.
- Evaluator results are deterministic JSON and do not trust the agent's final message.
- The evaluator must not require undocumented internal class, module, or file layouts.

## Trial-runner requirements

- Each trial workspace contains one baseline and `shared/public` only.
- The other baseline, parent Git metadata, prior trials, evaluator source, and result summaries are absent.
- Each workspace starts as a fresh Git repository with one baseline commit.
- One non-interactive Codex invocation performs the complete maintenance task.
- No resume, MCP, subagent, or prior-session state is used by a scored trial.
- Raw stdout JSONL and stderr are stored outside the agent workspace.
- Public and hidden evaluation run only after Codex exits.
- A file modification before failure changes infrastructure-failure handling into a scored outcome unless an independently confirmed external outage applies.

## Orchestrator requirements

Implement the explicit state machine and CLI described by the automation addendum.

Required behavior includes:

- safe `--dry-run`;
- preflight and two unscored calibrations;
- mandatory stop at calibration review;
- exact hash-bound calibration approval;
- frozen-input verification before every scored run;
- fixed interleaved six-run order;
- resumability without repeating completed runs;
- deterministic scoring;
- mandatory stop at final review;
- explicit cleanup;
- no automatic human approval.

Use dependency injection or command-runner abstractions so unit tests do not invoke live Codex.

## Review-packet requirements

- Calibration packet discusses fairness and infrastructure validity only.
- Calibration packet must not recommend a language winner.
- Final packet includes the immutable deterministic scorer result.
- Optional AI review is advisory and cannot write approval or alter the score.
- Packet hashes bind to exact bytes and change when included evidence changes.
- Raw JSONL logs and temporary workspaces are excluded.

## Do not

- run live calibration;
- run scored trials;
- create the freeze tag;
- select F# or C#;
- append `D-015`;
- change the correctness-first scoring order;
- add manual exceptions favoring a language;
- weaken compiler warnings, public tests, hidden checks, or forbidden scans;
- expose hidden evaluator files to trial workspaces;
- commit raw workspaces, build artifacts, Python caches, or JSONL logs.

## Completion checks

Run:

```bash
python -m unittest discover -s experiments/implementation-language-spike/tests -v

dotnet build experiments/implementation-language-spike/fsharp/Nayamnyam.Spike.FSharp.sln
dotnet run --project experiments/implementation-language-spike/fsharp/tests/Nayamnyam.Spike.Tests

dotnet build experiments/implementation-language-spike/csharp/Nayamnyam.Spike.CSharp.sln
dotnet run --project experiments/implementation-language-spike/csharp/tests/Nayamnyam.Spike.Tests

python experiments/implementation-language-spike/scripts/run_public_checks.py \
  --language fsharp \
  --workspace experiments/implementation-language-spike/fsharp

python experiments/implementation-language-spike/scripts/run_public_checks.py \
  --language csharp \
  --workspace experiments/implementation-language-spike/csharp

python experiments/implementation-language-spike/scripts/run_experiment.py --dry-run
```

Run preflight. A missing external prerequisite must produce one precise failure and no repository mutation.

Prepare a Gate 1 report covering:

- baseline behavioral equivalence;
- any baseline corrections and symmetric checks;
- public evaluator status;
- hidden evaluator isolation;
- runner isolation;
- orchestrator test status;
- unresolved risks before calibration.

Commit independently reviewable integration tasks with Conventional Commits. Finish with:

- commits created;
- checks run and exact results;
- Gate 1 report path;
- exact commands the user should run next;
- confirmation that no live calibration or scored trial was started.