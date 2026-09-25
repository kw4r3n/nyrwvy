# Milestone 1 agent evaluation plan — 2026-08-26

## Status

Completed plan. This was the accepted evaluation methodology for Milestone 1; the results are in the [initial tranche verification record](../verification/m1-agent-evaluation-initial-tranche-20260829.md).

The purpose is to test the AI-first design claim without choosing success criteria after observing the results. This document defines the comparison structure, required evidence, and failure categories before the larger M1 semantic subset is implemented.

It does not define a required percentage improvement over conventional source editing.

## Evaluation question

For supported editing tasks, does a semantic-query / semantic-operation workflow make an AI agent less likely to produce incorrect, incomplete, or unrelated program changes than a conventional source-editing workflow, and what cost does that reliability impose?

Secondary questions:

- Are focused semantic slices sufficient for ordinary edits?
- Do specialized node kinds reduce wrong-operation selection?
- Does explicit obligation handling help signature evolution?
- Does review binding prevent stale or substituted changes?
- Which parts of the semantic representation create unnecessary query or token overhead?

## Comparison modes

### Mode A — Nyrwvy semantic workflow

The agent receives supported agent-facing query and operation tools.

Ordinary writes occur only through:

```text
query
 -> operation intent
 -> simulate
 -> review
 -> reviewed commit/reject
```

The agent does not receive raw mutable snapshot editing as part of the normal task interface.

### Mode B — Conventional source-edit baseline

Use one conventional source implementation of the same representative program, with ordinary file/source editing and compiler/test feedback.

The baseline should preserve equivalent observable behavior and task intent as far as practical. It does not need to mimic Nyrwvy's internal architecture.

If F#, C#, or another baseline language materially changes task difficulty, record that fact rather than silently treating all source languages as equivalent.

### Optional Mode C — Raw graph/serialization editing

A diagnostic mode may expose canonical JSON/Nyason directly to an agent.

This is not an endorsed workflow. It exists only to test whether the semantic facade itself is providing value beyond the graph representation.

## Experimental controls

For every recorded run, store:

- task corpus version;
- repository revision;
- model identifier;
- model/reasoning configuration when available;
- tool set made available;
- system/task prompt revision;
- run date;
- whether the run is first-attempt or a retry;
- any human intervention.

Do not compare runs as if model/tool versions were identical when they were not.

When practical, compare modes with the same model family and comparable reasoning budget.

## Task corpus

The corpus should be small enough to inspect manually but broad enough to exercise distinct error classes.

### Class 1 — Identity-preserving changes

Examples:

- rename a function;
- rename a type;
- change presentation metadata without changing semantic references.

Tests:

- stable identity preservation;
- lack of unrelated churn;
- no display-name reference reconstruction.

### Class 2 — Signature evolution

Examples:

- add a parameter with all call sites immediately resolvable;
- add a parameter when one call site needs an explicit obligation;
- resolve the resulting obligation later.

Tests:

- call-site discovery;
- parameter identity;
- explicit incompleteness;
- propagation accuracy.

### Class 3 — Cross-function structural edits

Examples:

- add a new validation rule used from more than one function;
- redirect an existing call to a new helper while preserving types/effects;
- change a helper signature and repair affected callers.

Tests:

- focused query sufficiency;
- caller/callee reasoning;
- transaction impact review.

### Class 4 — Collection edits

After `List<T>` exists:

- map validation across inputs;
- filter inputs by a pure predicate;
- add valid/invalid counts;
- change a predicate function referenced by a specialized list node.

Tests:

- specialized-node selection;
- ordered collection semantics;
- higher-order stable references;
- absence of accidental effect ordering.

### Class 5 — Numeric edits

After `Int64` / `Float64` exist:

- add a count using explicit integer semantics;
- add a Float64-derived metric;
- intentionally request a conversion requiring an explicit policy.

Tests:

- operation-semantic selection;
- avoidance of implicit conversion;
- handling of precision/failure boundaries.

### Class 6 — Stale review / conflicting-state scenarios

Examples:

- simulate proposal A;
- commit proposal B that advances the workspace;
- attempt to publish A.

Tests:

- stale proposal rejection;
- reviewed-receipt correctness;
- whether agent retries via a fresh query/proposal rather than bypassing review.

## Primary success levels

Each run receives one outcome classification.

### S0 — No valid proposal/result

The agent cannot produce a structurally valid candidate within the run budget.

### S1 — Valid but task-incorrect

The result validates/compiles but does not satisfy the requested semantic behavior.

### S2 — Correct with unrelated semantic churn

The requested behavior is correct, but unrelated semantic structure changes unnecessarily.

### S3 — Correct after recoverable retries

The final result is correct and scoped, but required one or more failed/rejected attempts.

### S4 — Correct first accepted proposal

The first proposal accepted by the editing system is task-correct, scoped, and passes required validation/execution checks.

These levels are descriptive, not a claim that S4 is always achievable for difficult tasks.

## Failure categories

Record all applicable categories, even if the run later recovers.

- `wrong-target` — edited the wrong stable object/file/symbol;
- `identity-loss` — recreated or redirected identity when continuity was required;
- `missing-propagation` — failed to update a required dependent location;
- `unrelated-churn` — changed semantics outside the task scope;
- `invalid-reference` — created a dangling/wrong reference;
- `type-error` — created incompatible type structure;
- `effect-error` — violated effect/capability contract;
- `order-error` — relied on non-semantic serialization/evaluation order;
- `implicit-default` — invented a missing value/policy instead of surfacing uncertainty;
- `obligation-bypass` — attempted to bypass an explicit unresolved obligation;
- `stale-review` — attempted to publish a stale or mismatched proposal;
- `wrong-specialized-node` — selected a semantic operation/node with the wrong contract;
- `backend-leak` — encoded backend behavior as if it were language semantics;
- `tool-protocol-error` — malformed/incorrect tool/API invocation rather than program reasoning failure.

Keep `tool-protocol-error` separate so API ergonomics problems are not misreported as semantic-model failures.

## Quantitative metrics

Record when available:

- final success level;
- number of failed/rejected proposals;
- number of agent retries;
- semantic query count;
- operation/simulation count;
- commit attempts;
- raw tool-call count;
- input/output tokens;
- focused context bytes or entities retrieved;
- whole-program retrieval count;
- unrelated semantic entities changed;
- compiler/test failures;
- wall-clock duration as secondary operational data.

Token and latency results are environment/model dependent and must not be treated as semantic correctness metrics.

## Semantic churn measure

For Nyrwvy, prefer semantic-diff entities over serialized line counts.

Candidate simple measure:

```text
unrelated churn = changed semantic entities not required by the task oracle
```

The task oracle should list entities/relations that are expected or permitted to change, rather than expecting an exact serialized output.

For source-edit baseline, line diff may be recorded, but semantic correctness should still be judged against behavior/tests and task scope rather than line count alone.

## Query-sufficiency measure

For each successful Nyrwvy run, record whether the agent needed:

1. one Focus slice;
2. multiple bounded semantic queries;
3. a broader snapshot query;
4. raw whole-snapshot/serialization access.

Repeated need for level 3/4 indicates the query surface or Focus closure is insufficient even if the final edit succeeds.

## Repetition policy

Because model behavior is stochastic, do not draw conclusions from one run.

For exploratory M1 results:

- run each important task multiple times when budget permits;
- report the number of runs rather than hiding unsuccessful attempts;
- do not discard a run because its failure is inconvenient;
- separate tool/runtime infrastructure failures from valid agent attempts.

No fixed repetition count is required yet because model/API cost may change. Reports must state the actual count.

## Prompt policy

Do not tune one comparison mode with extensive hidden task-specific instructions while leaving the other generic.

Mode-specific instructions are allowed when they explain the available editing interface, but the requested semantic task should remain equivalent.

If a prompt is changed after observing failures, version it and treat later runs as a new experimental condition.

## Human intervention policy

Classify intervention explicitly:

- `none` — autonomous run;
- `clarification-only` — human supplies missing task intent but no implementation answer;
- `repair-guidance` — human points to an error or relevant area;
- `manual-edit` — human directly performs part of the implementation.

Only `none` runs should be used for claims about autonomous edit success. Other modes remain useful for ergonomics analysis.

## Artifacts per task

Store enough evidence to reproduce/inspect the result:

- task specification and oracle;
- starting revision/fingerprint;
- tool/API surface version;
- agent run transcript or structured event summary when available;
- proposal receipts / semantic diffs for Nyrwvy;
- final snapshot/source revision;
- validation/test/compile results;
- metrics summary;
- failure classifications;
- short reviewer note on ambiguous cases.

## M1 reporting rule

Milestone 1 does not pass because Nyrwvy "wins" every benchmark.

It passes the evaluation requirement when:

1. the methodology is reproducible enough to inspect;
2. failures are not hidden;
3. at least the representative identity, signature, collection, stale-review, and numeric task classes are exercised where implemented;
4. the results identify concrete design decisions to keep, change, or investigate next.

A result showing that a Nyrwvy abstraction harms agent reliability is useful evidence and should drive redesign rather than be explained away.
