# Scoped pure control-flow exploratory evaluation v1

**Status:** Exploratory, not frozen

This small tranche asks one question: can an agent discover and use the maintained `PureExpressionRecipe` lexical-scope model well enough to author and review scoped pure control flow?

It is intentionally not a replacement for the frozen M2 corpus and does not repeat M2 stale/publication experiments. The transaction mechanics are inherited; the new interaction under observation is recipe-local pattern scope plus nested branch graphs.

## Evaluation surface

Use protocol `pure-expression-control-flow-v1` through `tools/NyamNyam.AgentEval.PureExpression`.

Each task starts from a fresh built-in deterministic fixture. The agent should begin with `describeV1`, use the returned fixture/schema facts to construct a request, simulate a proposal, inspect the resulting change, and commit only the intended result.

The fixture exposes one target:

- `target(option: Option<Int64>, union: choice) -> Int64`
- `choice.first(value: Int64)`
- `choice.second`

Stable seed IDs are returned by `describeV1`; prompts do not need to teach the recipe schema or lexical-depth rule.

## Initial tasks

### PCF-E1 — Conditional

Replace `target` with a conditional that returns `1` when `true` and `2` otherwise.

Observation: basic discovery and reviewed control-flow construction without lexical bindings.

### PCF-E2 — Option binding

Match `option`. Return the contained value for `Some`; return `0` for `None`.

Observation: discover the `value` pattern slot at `ScopeDepth = 0`.

### PCF-E3 — Union field binding

Match `union`. For `first(value)`, return the bound field. For `second`, return `0`.

Observation: discover variant IDs, field-slot representation, exhaustive arm requirements, and declaration-derived field binding.

### PCF-E4 — Outer reference across an empty frame

Match `option`. In the `Some` arm, perform a nested match over `OptionNone<Int64>`. Both nested arms must return the outer `Some` value. The outer `None` arm returns `0`.

Observation: whether the agent understands that every enclosing match contributes one lexical frame and that the nested `Option.None` arm's structurally empty current frame still consumes depth, so the outer value is at `ScopeDepth = 1`.

### PCF-E5 — Recover from an invalid lexical reference

Implement the same behavior as PCF-E4, but the task explicitly asks the agent to first try the natural current-frame reference in the nested `None` arm, inspect the rejection, and then correct the proposal from the diagnostic and protocol description.

Observation: discoverability/recovery only. This task is optional if PCF-E4 already demonstrates the boundary clearly; do not manufacture a failure solely to increase sample count.

## What to record

For one fresh attempt per selected task, record only evidence needed for the decision:

- whether `describeV1` was used;
- whether the first semantically intended proposal was accepted;
- lexical-reference diagnostics encountered;
- whether the resulting change was inspected before commit;
- whether recursive `addedNodes` made branch-local `PatternReference` nodes visible;
- terminal committed/rejected state;
- a short qualitative note on any confusion around `ScopeDepth`, slots, or union arm construction.

Do not turn this into a 30-attempt corpus unless these few runs expose a question whose answer actually benefits from controlled sampling.

## Deterministic validation before model use

Run once at this architectural boundary:

```shell
./scripts/validate-scoped-pure-control-flow.fish
```

That command builds/smokes the eval-only runner and then runs the maintained extended suite. The eval runner is intentionally not added to `NyamNyam.slnx`, so routine CI does not pay its build cost.

## Exploratory model run

After deterministic validation has passed, run the first four tasks once each with:

```shell
./scripts/run-pure-control-flow-exploratory.fish
```

The script uses `openai/gpt-5.6-terra` with the `high` variant by default. Override those only when needed with `NYAMNYAM_PURE_CONTROL_EVAL_MODEL` and `NYAMNYAM_PURE_CONTROL_EVAL_VARIANT`.

Results and raw OpenCode JSON logs are written under `~/.local/share/nyamnyam/pure-control-flow-exploratory/`. The script prints a compact per-task summary with process exit status, whether a proposal/commit appeared in the tool trace, whether a `PatternReference` appeared, and any lexical-scope diagnostic code.

Do not run PCF-E5 by default. Add that recovery observation only if PCF-E4 exposes uncertainty that is worth probing.
