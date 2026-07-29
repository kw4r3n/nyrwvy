# Setup Session 1 — Shared Experiment Infrastructure

You are implementing the language-neutral foundation for a controlled F# versus C# maintainability experiment.

Read these plans completely before editing:

- `docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike.md`
- `docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md`

## Scope

Implement only:

- Task 1 from the main plan;
- Task 2 from the main plan;
- the language-neutral scaffolding needed by Tasks 5 and 6;
- Task A1 infrastructure from the automation addendum where it does not depend on completed baselines.

Expected areas include:

```text
experiments/implementation-language-spike/
├── README.md
├── experiment.toml
├── .gitignore
├── shared/public/
├── shared/expected/
├── evaluator/common.py
├── scripts/render_setup_prompt.py
├── scripts/prepare_workspace.py
├── scripts/preflight.py
└── tests for the above
```

## Do not

- implement the F# baseline;
- implement the C# baseline;
- inspect or invent baseline-specific APIs beyond the shared semantic contract;
- finish hidden evaluator implementations that require concrete baseline APIs;
- run calibration or scored trials;
- alter the accepted maintenance task to make implementation easier;
- add third-party runtime dependencies;
- weaken strict configuration validation;
- commit raw workspaces, build output, cache files, or JSONL logs.

## Requirements

- Use Python 3.13 standard library only for experiment automation.
- Keep canonical documentation and prompts in English.
- Make configuration and manifests reject unknown fields.
- Make every serialized experiment artifact deterministic where specified.
- Keep hidden evaluator material outside future trial workspaces.
- Ensure workspace preparation copies only one selected baseline plus `shared/public`.
- Initialize each prepared trial directory as a fresh Git repository.
- Write tests before implementation where the plans require TDD.

## Completion checks

Run all tests implemented in this session. At minimum:

```bash
python -m unittest experiments/implementation-language-spike/tests/test_config.py -v
python -m unittest experiments/implementation-language-spike/tests/test_setup_prompts.py -v
python -m unittest experiments/implementation-language-spike/tests/test_workspace.py -v
```

Run preflight only to the extent possible without completed baselines. Missing baselines must be reported precisely rather than hidden.

Review the shared documents for exact consistency of:

- stable-ID names;
- JSON field names;
- operation names;
- transaction-result names;
- maintenance-task ordering;
- fixture bytes and canonical ordering.

Commit each independently reviewable task with Conventional Commits. Finish with:

- a concise summary;
- tests run and their results;
- remaining baseline-dependent work;
- commit SHAs created by this session.