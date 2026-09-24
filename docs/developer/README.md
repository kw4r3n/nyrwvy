# Developer and reviewer documentation

**Status:** Current

Use this entry point when changing, reviewing, or validating Nyrwvy itself.

## Before selecting follow-up work

Inspect the default branch, open issues, and open pull requests, including drafts and stacked PRs, before recommending new work or creating an issue. Check related PR bases/heads, implementation status, and recorded verification evidence: an unchanged `main` or an open design issue does not mean the work has not already been prepared. Continue or review existing work rather than creating a duplicate, and reuse sufficient verification unless the relevant implementation has changed.

## Scope of workload-driven features

Nyrwvy is a programming-language replacement and semantic authoring layer, not an application framework. Use real applications such as rrawrlet to identify missing general-purpose language and tool capabilities; do not turn their product features into Nyrwvy builtins. For external integration, distinguish the typed declaration/binding/value-passing mechanism from the concrete provider or library implementation. Reuse existing constructs or libraries when they satisfy the requirement, but do not conceal missing language capability by moving the application's logic wholesale into host code.

Keep application runtime concurrency separate from concurrent editing of Nyrwvy snapshots. Neither implies a requirement or a conclusion about the other. Record confirmed implementation limits, open design choices, and work already prepared in PRs separately. The [general-purpose programming roadmap](../plans/general-purpose-programming-roadmap.md) is the current workload-driven implementation queue.

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
Nyrwvy.Semantics
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
