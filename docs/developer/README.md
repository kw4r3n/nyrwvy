# Developer and reviewer documentation

**Status:** Current

Use this entry point when changing, reviewing, or validating NyamNyam itself.

## Before changing semantics

1. Read the [architecture](architecture.md) for current system boundaries and invariants.
2. Check the [decision register](../decisions/README.md) for accepted rationale.
3. Check [open questions](../decisions/open-questions.md) before filling an intentional gap by assumption.
4. Read the relevant [specification](../specs/) and [reference](../reference/) for the area being changed.
5. Classify the verification need using the [verification philosophy](verification.md) before turning a routine change into a milestone or experimental validation exercise.
6. If the work follows an active milestone or experiment, use the relevant [plan](../plans/) or [experiment](../experiments/) rather than treating it as an already accepted contract.

## Repository writes

Before a repository mutation, check the actual tool action, explicit non-empty target branch/ref, and intended repository path where applicable. Never use a content write as a tool probe, create scratch files on `main`, or rely on an empty/default branch fallback. Stage related changes on a work branch and inspect the resulting diff before publication; batch file changes into one commit/ref update where possible instead of triggering CI per file.

## Implementation boundaries

The current production dependency direction is organized around:

```text
NyamNyam.Semantics
      ^        ^
      |        |
Execution    Viewer
      ^        ^
      |        |
Backend.CSharp  Tools
```

The semantic graph and semantic operations remain upstream authority. Execution, backend, viewer, tooling, persistence encodings, generated artifacts, and evaluation infrastructure must not redefine program meaning implicitly.

Evaluation is a downstream evidence layer. Frozen protocol or harness behavior may be preserved for historical replay without becoming a production semantic constraint.

## Verification

Read [Verification philosophy](verification.md) before deciding how broadly to validate a change.

The default split is:

- **routine / fast** — ordinary development and CI;
- **milestone / extended** — important architectural or completion boundaries;
- **experimental** — expensive evidence-generating work for a materially new design hypothesis.

Normal repository build/test commands are documented in the root [README](../../README.md). Completed exact-head evidence belongs under [verification](../verification/) rather than being copied into landing pages.

When a change creates a new durable contract, update the corresponding spec/reference and record schema/version implications where applicable. When the result is only an observed experiment, keep it under `experiments/` until a separate decision or spec accepts it.

## Documentation maintenance

Read [Documentation conventions](documentation.md) before adding or relocating design material. In particular:

- Decision != Spec != Plan != Verification.
- Current and historical documentation are separate authority domains.
- Do not create empty categories for symmetry.
- If the intended authority is clear, update the stale side; if it is genuinely ambiguous, raise an issue rather than silently choosing an answer.
