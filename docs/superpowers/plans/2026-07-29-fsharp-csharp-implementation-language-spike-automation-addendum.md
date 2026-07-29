# F# vs C# Implementation Language Spike Automation Addendum

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this addendum task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce the F# versus C# spike to three human approval gates by automating setup, calibration, six scored trials, evaluation, reporting, and cleanup while preserving the frozen experimental protocol.

**Architecture:** This document extends [`2026-07-29-fsharp-csharp-implementation-language-spike.md`](2026-07-29-fsharp-csharp-implementation-language-spike.md). Four independent setup Codex sessions build the shared infrastructure, the two isolated baselines, and the integration layer. A deterministic Python state machine then performs preflight, calibration, freeze approval, scored execution, evaluation, report generation, and cleanup. AI may implement and summarize the experiment, but it may not change fairness rules, approve calibration, alter frozen inputs, or override the deterministic scorer.

**Tech Stack:** Python 3.13 standard library, .NET 10 SDK, F#, C#, Git, Codex CLI, Markdown, JSON, TOML.

## Relationship to the main plan

This addendum does not replace the semantic contract, maintenance task, evaluator requirements, or correctness-first selection rules in the main spike plan.

It changes the expected execution workflow:

```text
Original:
  execute Tasks 1-10 manually

Automated:
  use four setup sessions
      -> run one deterministic orchestrator
      -> approve calibration once
      -> let the orchestrator run the scored matrix
      -> approve the final decision once
```

When the documents differ only in execution mechanics, this addendum takes precedence. The main plan remains authoritative for experiment semantics and acceptance criteria.

## Human approval gates

Only the following actions require a human decision.

### Gate 1 — Start the experiment

Approve:

- the shared semantic contract;
- the visible maintenance task;
- the hidden evaluator specification;
- the F# and C# baseline equivalence report;
- the frozen scoring rule.

No trial begins before Gate 1.

### Gate 2 — Freeze calibrated experiment

After one unscored run per language, approve the exact calibration manifest hash.

Approval confirms only that:

- the experiment is fair and operational;
- both agents saw equivalent information;
- hidden evaluator code remained hidden;
- the timeout and toolchain were adequate;
- no calibration defect requires a rerun.

Calibration performance is not scored.

### Gate 3 — Accept final language decision

Review:

- deterministic score result;
- trial summary table;
- repeated failure patterns;
- fixed-checklist patch review;
- proposed `D-015` decision entry.

The deterministic result may be rejected only for a documented experiment-integrity failure, not because the preferred language lost.

## Agent separation

Preparation and trial execution use different Codex sessions.

```text
Setup Session 1  shared infrastructure and language-neutral contract
Setup Session 2  F# baseline only
Setup Session 3  C# baseline only
Setup Session 4  integration, evaluator, runner, and automation

Trial sessions     one fresh Codex invocation per calibration or scored run
Review sessions    optional read-only Codex invocations over prepared packets
```

A setup session must never be reused as a scored trial session. A scored trial session must never see either setup history or any previous trial output.

## Prompt bundle

Canonical setup prompts live under:

```text
docs/superpowers/prompts/implementation-language-spike/
├── README.md
├── 01-shared-infrastructure.md
├── 02-fsharp-baseline.md
├── 03-csharp-baseline.md
└── 04-integration-automation.md
```

Before a setup session starts, record:

- prompt path;
- prompt SHA-256;
- repository base commit;
- Codex CLI version;
- selected model string;
- start and finish commit.

A setup session may use normal interactive Codex review, but its scope is limited by its prompt. The baseline sessions must not inspect one another's implementation directories.

## Added repository layout

The implementation created from this addendum extends the main plan with:

```text
experiments/implementation-language-spike/
├── prompts/
│   ├── calibration-review.md
│   └── final-review.md
├── scripts/
│   ├── run_experiment.py
│   ├── render_setup_prompt.py
│   ├── create_review_packet.py
│   └── verify_frozen_inputs.py
├── tests/
│   ├── test_orchestrator.py
│   ├── test_approval.py
│   ├── test_resume.py
│   └── test_review_packet.py
└── results/
    ├── state.json
    ├── approvals/
    ├── calibration/
    ├── scored/
    └── final/
```

`results/state.json` is runtime state and should normally remain ignored. Compact manifests, evaluations, patches, and final reports remain commit-worthy as defined by the main plan.

## Orchestrator command

The primary workflow is one command:

```bash
python experiments/implementation-language-spike/scripts/run_experiment.py \
  --config experiments/implementation-language-spike/experiment.toml
```

The command must stop safely at human gates. It must never interpret a missing approval as approval.

Required modes:

```text
--dry-run
--through preflight
--through calibration
--approve-calibration <sha256:...>
--run-scored
--score-only
--resume
--keep-failed-workspaces
--cleanup
```

The normal sequence is:

```bash
# Inspect commands and hashes without changing state.
python .../run_experiment.py --dry-run

# Run preflight and both unscored calibrations, then stop.
python .../run_experiment.py --through calibration

# After human review, bind approval to the exact packet hash.
python .../run_experiment.py --approve-calibration sha256:<packet-hash>

# Execute all six frozen scored runs, score them, and create the final packet.
python .../run_experiment.py --run-scored

# Resume after a host interruption without repeating completed runs.
python .../run_experiment.py --resume
```

## State machine

The orchestrator uses these explicit stages:

```text
uninitialized
  -> preflight_complete
  -> calibration_running
  -> calibration_review_required
  -> frozen
  -> scored_running
  -> scored_complete
  -> final_review_required
  -> accepted
  -> cleaned
```

Rejected or invalid states are not silently repaired. The public CLI must explain the required recovery action.

### State invariants

- Stage advancement is monotonic except for an explicit full calibration reset.
- Each stage records input hashes and output hashes.
- `--resume` refuses to continue when frozen input hashes differ.
- A scored run is never repeated merely because it failed.
- An invalid infrastructure run retains its evidence and may have at most one replacement.
- Calibration changes invalidate both language calibration runs.
- Any frozen prompt, baseline, evaluator, runner, model, or toolchain change invalidates the scored matrix.
- Approval hashes bind to exact review packet bytes.

## Approval records

Approval is external workflow metadata, not program semantics.

A calibration approval file contains:

```json
{
  "schema_version": 1,
  "kind": "calibration_freeze",
  "packet_sha256": "sha256:...",
  "experiment_manifest_sha256": "sha256:...",
  "approved_at": "2026-07-29T00:00:00Z"
}
```

The timestamp is audit metadata and is excluded from experiment determinism. Approval is rejected if the current packet hash differs.

The runner must not generate this file by itself. The user-supplied `--approve-calibration` argument creates it only after exact-hash verification.

## Review packets

Humans should not need to inspect raw JSONL logs.

### Calibration packet

The orchestrator creates a directory containing:

```text
calibration-review/
├── packet.json
├── summary.md
├── fsharp/
│   ├── manifest.json
│   ├── metrics.json
│   ├── evaluation.json
│   └── final.patch
└── csharp/
    ├── manifest.json
    ├── metrics.json
    ├── evaluation.json
    └── final.patch
```

`summary.md` reports experiment validity only. It must not recommend a winning language.

An optional read-only Codex review may produce:

```json
{
  "schema_version": 1,
  "fairness_valid": true,
  "blocking_findings": [],
  "non_blocking_findings": [],
  "baseline_asymmetries": [],
  "prompt_asymmetries": [],
  "recommended_action": "freeze"
}
```

This recommendation is advisory. It cannot create approval.

### Final packet

The orchestrator creates:

```text
final-review/
├── packet.json
├── deterministic-result.json
├── report.md
├── decision-draft.md
└── trial-patches/
```

The selected language comes only from `score.py`. A review model may explain the result but cannot modify `deterministic-result.json`.

## Setup-session safety rules

All four setup sessions must obey:

- Do not start calibration or scored trials.
- Do not change the accepted experiment goal.
- Do not weaken warnings, tests, evaluator checks, or forbidden-pattern scans.
- Do not add network dependencies to baseline projects.
- Do not hide implementation failures with broad catch-all branches.
- Do not commit raw workspaces, build outputs, or full JSONL logs.
- Run the checks named in the session prompt before finishing.
- Commit only the session's assigned scope.

## Task A1: Add the canonical setup prompt bundle

**Files:**

- Create: `docs/superpowers/prompts/implementation-language-spike/README.md`
- Create: `docs/superpowers/prompts/implementation-language-spike/01-shared-infrastructure.md`
- Create: `docs/superpowers/prompts/implementation-language-spike/02-fsharp-baseline.md`
- Create: `docs/superpowers/prompts/implementation-language-spike/03-csharp-baseline.md`
- Create: `docs/superpowers/prompts/implementation-language-spike/04-integration-automation.md`
- Create: `experiments/implementation-language-spike/scripts/render_setup_prompt.py`
- Test: `experiments/implementation-language-spike/tests/test_setup_prompts.py`

**Interfaces:**

- Consumes: repository root and one canonical prompt path.
- Produces: rendered prompt text, prompt SHA-256, base commit SHA, and a session manifest.

- [ ] **Step 1: Write prompt-manifest tests**

Test that prompt rendering:

- fails for unknown prompt names;
- normalizes line endings to LF;
- preserves one trailing newline;
- includes the referenced main plan and automation addendum paths;
- writes deterministic SHA-256 values;
- records the current Git commit without modifying the repository.

- [ ] **Step 2: Implement the renderer**

The script accepts:

```bash
python scripts/render_setup_prompt.py \
  --session 01-shared-infrastructure \
  --output /tmp/nayamnyam-session-01
```

It writes:

```text
prompt.md
manifest.json
```

The script does not invoke Codex automatically. This keeps setup execution inspectable.

- [ ] **Step 3: Verify all four prompts**

```bash
python scripts/render_setup_prompt.py --session 01-shared-infrastructure --output /tmp/s1
python scripts/render_setup_prompt.py --session 02-fsharp-baseline --output /tmp/s2
python scripts/render_setup_prompt.py --session 03-csharp-baseline --output /tmp/s3
python scripts/render_setup_prompt.py --session 04-integration-automation --output /tmp/s4
```

Expected: four deterministic manifests with distinct prompt hashes.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/prompts experiments/implementation-language-spike/scripts experiments/implementation-language-spike/tests
git commit -m "docs(spike): add automated setup prompts"
```

## Task A2: Implement the resumable experiment orchestrator

**Files:**

- Create: `experiments/implementation-language-spike/scripts/run_experiment.py`
- Create: `experiments/implementation-language-spike/scripts/verify_frozen_inputs.py`
- Test: `experiments/implementation-language-spike/tests/test_orchestrator.py`
- Test: `experiments/implementation-language-spike/tests/test_resume.py`

**Interfaces:**

- Consumes: `experiment.toml`, completed baseline/evaluator implementation, Codex CLI, and optional approval hash.
- Produces: state transitions, trial invocations, compact results, review packets, and precise stop reasons.

- [ ] **Step 1: Write state-machine tests**

Cover:

- new state initialization;
- dry-run without mutation;
- preflight failure;
- successful transition through calibration review;
- refusal to start scored runs without approval;
- exact approval-hash matching;
- resuming after interruption;
- refusing resume after frozen-input drift;
- preserving a scored failure without rerun;
- one permitted replacement for a pre-edit infrastructure failure;
- cleanup only after final review packet creation.

Use fake command runners; unit tests must not invoke Codex.

- [ ] **Step 2: Implement canonical state persistence**

State writes must be atomic:

1. serialize to a temporary sibling file;
2. flush and fsync;
3. replace `state.json`;
4. fsync the directory where supported.

The loader rejects unknown fields and unsupported schema versions.

- [ ] **Step 3: Implement stage dispatch**

Each stage calls existing scripts rather than duplicating their behavior:

```text
preflight.py
run_trial.py
score.py
create_review_packet.py
```

The orchestrator owns ordering and gate enforcement only.

- [ ] **Step 4: Implement frozen-input verification**

Hash at least:

- experiment config;
- visible task and acceptance documents;
- both baseline trees;
- evaluator tree;
- runner tree;
- model string;
- Codex CLI version;
- .NET SDK version.

A mismatch after freeze blocks all scored execution.

- [ ] **Step 5: Add CLI integration tests**

Use temporary fake experiment trees and stub scripts to verify exit codes and user-facing messages.

- [ ] **Step 6: Commit**

```bash
git add experiments/implementation-language-spike/scripts experiments/implementation-language-spike/tests
git commit -m "feat(spike): automate experiment orchestration"
```

## Task A3: Add review-packet generation and hash-bound approval

**Files:**

- Create: `experiments/implementation-language-spike/scripts/create_review_packet.py`
- Create: `experiments/implementation-language-spike/prompts/calibration-review.md`
- Create: `experiments/implementation-language-spike/prompts/final-review.md`
- Test: `experiments/implementation-language-spike/tests/test_review_packet.py`
- Test: `experiments/implementation-language-spike/tests/test_approval.py`

**Interfaces:**

- Consumes: compact trial outputs only.
- Produces: deterministic packet JSON, human Markdown summary, optional AI-review input, and packet SHA-256.

- [ ] **Step 1: Write packet determinism tests**

Verify that:

- file discovery order does not change packet bytes;
- raw JSONL and workspaces are excluded;
- patch bytes are hashed and copied without normalization;
- calibration packets omit winner language and scoring recommendation;
- final packets contain the immutable deterministic scorer result;
- packet hash changes when any included artifact changes.

- [ ] **Step 2: Implement calibration summary**

Report:

- environment parity;
- baseline start checks;
- prompt and baseline hashes;
- evaluator visibility checks;
- timeout and infrastructure status;
- any asymmetry requiring both calibrations to be invalidated.

Do not compare performance for language selection.

- [ ] **Step 3: Implement approval verification**

`--approve-calibration` accepts only the exact `sha256:<hex>` printed by the packet generator. It must reject malformed, stale, or already superseded approvals.

- [ ] **Step 4: Implement final packet**

Include the deterministic result and a decision draft, but mark the draft as unaccepted until Gate 3.

- [ ] **Step 5: Commit**

```bash
git add experiments/implementation-language-spike/scripts experiments/implementation-language-spike/prompts experiments/implementation-language-spike/tests
git commit -m "feat(spike): add review and approval packets"
```

## Task A4: Integrate the one-command workflow

**Files:**

- Modify: `experiments/implementation-language-spike/README.md`
- Modify: `experiments/implementation-language-spike/scripts/run_experiment.py`
- Create: `experiments/implementation-language-spike/tests/test_end_to_end_orchestrator.py`

**Interfaces:**

- Consumes: all main-plan and addendum components.
- Produces: a dry-run transcript and a complete fake end-to-end orchestration test.

- [ ] **Step 1: Build a fake experiment harness**

The test fixture provides fake preflight, trial, scorer, and packet commands. It simulates:

- two successful calibrations;
- calibration approval;
- one scored implementation failure;
- five scored successes;
- deterministic winner selection;
- final packet creation;
- cleanup.

- [ ] **Step 2: Verify the complete state path**

Assert every expected transition and ensure no stage runs twice after `--resume`.

- [ ] **Step 3: Document the three-gate workflow**

The experiment README must contain copyable commands for:

1. rendering and running the four setup sessions;
2. running through calibration;
3. approving the calibration hash;
4. running the scored matrix;
5. reviewing and accepting the final decision;
6. cleaning temporary artifacts.

- [ ] **Step 4: Run all automation tests**

```bash
python -m unittest discover -s experiments/implementation-language-spike/tests -v
```

Expected: all tests pass without invoking live Codex.

- [ ] **Step 5: Commit**

```bash
git add experiments/implementation-language-spike
git commit -m "docs(spike): integrate automated experiment workflow"
```

## Task A5: Execute setup through four isolated Codex sessions

This task occurs only after Tasks A1-A4 and the original Tasks 1-6 have an executable plan.

- [ ] **Session 1: Shared infrastructure**

Render and provide `01-shared-infrastructure.md`. Review its commits before continuing.

- [ ] **Session 2: F# baseline**

Provide `02-fsharp-baseline.md` in a context where the C# implementation is absent or inaccessible.

- [ ] **Session 3: C# baseline**

Provide `03-csharp-baseline.md` in a context where the F# implementation is absent or inaccessible.

- [ ] **Session 4: Integration and automation**

Provide `04-integration-automation.md` after both baseline branches or commits are available. This session integrates but does not run live calibration or scored trials.

- [ ] **Run Gate 1 review**

Verify both baseline implementations and approve experiment start.

## Task A6: Run the automated experiment

- [ ] **Run dry-run**

```bash
python experiments/implementation-language-spike/scripts/run_experiment.py --dry-run
```

- [ ] **Run calibration**

```bash
python experiments/implementation-language-spike/scripts/run_experiment.py --through calibration
```

- [ ] **Review calibration packet**

Inspect `results/calibration-review/summary.md` and compact evidence.

- [ ] **Approve exact packet hash**

```bash
python experiments/implementation-language-spike/scripts/run_experiment.py \
  --approve-calibration sha256:<exact-packet-hash>
```

- [ ] **Run scored matrix**

```bash
python experiments/implementation-language-spike/scripts/run_experiment.py --run-scored
```

The command stops at `final_review_required` after scoring and packet generation.

- [ ] **Perform Gate 3 review**

Review `deterministic-result.json`, `report.md`, and `decision-draft.md`.

- [ ] **Record accepted D-015**

After human acceptance, commit the completed decision entry and final compact evidence.

- [ ] **Clean disposable artifacts**

```bash
python experiments/implementation-language-spike/scripts/run_experiment.py --cleanup
```

## Self-review checklist

Before this addendum is considered implemented:

- every automatic transition has a unit test;
- human gates cannot be bypassed by default arguments;
- the runner cannot change scorer rules after freeze;
- setup prompts have recorded hashes;
- baseline sessions cannot inspect one another's implementation;
- calibration review does not select a language;
- hidden evaluator remains absent during trial execution;
- `--resume` checks frozen input hashes;
- failures after edits remain scored failures;
- raw logs and workspaces are not committed;
- final selection comes from deterministic scoring, not AI prose.

## Expected manual workload

After implementation, normal human work is reduced to:

```text
1. Approve the comparison specification and equivalent baselines.
2. Review the calibration packet and approve its exact hash.
3. Review the final packet and accept or reject D-015 for integrity reasons.
```

All command sequencing, trial isolation, retry policy, result extraction, scoring, reporting, and cleanup are automated.