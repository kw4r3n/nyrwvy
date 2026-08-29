# M1 agent evaluation condition v2

**Status:** A condition-v2.1 pre-integration sample is recorded. The post-main-integration condition remains pending. This document does not change `m1-agent-corpus-v1`.

Condition v2 replaces the shell-managed JSON Lines client used in the initial trials with the project-local OpenCode `nyamnyam-eval` agent and its persistent custom tools. The condition exists to remove client lifecycle errors from Mode A semantic reasoning measurements.

The initial v2 E1/E2 attempts exposed that the custom-tool description did not include the runner's exact JSON schema. The v2.1 agent instructions add task-independent command/operation schemas. This is a versioned tool-instruction change, not a corpus or oracle change; do not compare v2 and v2.1 as the same condition.

## Tool boundary

The project-local `.opencode/agent/nyamnyam-eval.md` denies the wildcard tool surface, including inherited built-ins, MCP tools, and custom plugin tools, then explicitly allows only the three evaluation tools. The project-local OpenCode plugin exposes only:

- `nyamnyam_eval_start` to prepare the harness-configured fresh fixture and start one runner process;
- `nyamnyam_eval_request` to send one runner JSON Lines request and receive its response; and
- `nyamnyam_eval_finalize` to close the session and write terminal evidence.

The tools reject callers other than the `nyamnyam-eval` agent. The adapter, not the agent, exports the fixture, owns the runner child process, stores the raw fixture and verification artifacts outside the worktree under a harness directory, and passes artifact/verification configuration. The agent cannot select the fixture, request raw snapshot access, artifact paths, or E5 perturbation commands. One OpenCode agent session may start and finalize exactly one evaluation run.

One OpenCode agent session maps to one in-memory `NyamNyam.AgentEval.Session`. Opaque proposals remain in that session through query, simulate, review, commit/reject, and finalization. There is intentionally no cross-process resume.

## Evidence and verification

Every v2 run receives a unique harness run directory with:

- start snapshot and semantic fingerprints;
- model/provider/runtime/reasoning/prompt/tool metadata, or explicit `unavailable` values;
- request/response JSONL events and query/focus/context metrics;
- structured stale-perturbation receipts when enabled;
- terminal artifact-persistence state; and
- for `m0`/`m1` semantically-ready commits, exact committed-workspace Typed Semantic IR, Execution IR, generated C#, compiler/execution outputs, and atomic verification evidence.

Detailed artifact paths and derived whole-program files are harness-only. The agent receives only the runner's bounded response plus a verification status/identity summary.

On Unix, the adapter sets the absolute artifact root and run directory to `0700` and the exported fixture to `0600`; configured roots must be outside the worktree. The runner applies the same private modes to its per-run artifact tree; a mode-enforcement failure fails or invalidates the run rather than exposing evidence. Harness events and structured perturbation receipts remain in artifact/operator data and are omitted from agent-visible protocol metrics.

## Run setup

1. Record `opencode --version` and launch OpenCode with that exact value in `NYAMNYAM_EVAL_OPENCODE_VERSION`; the harness persists it as `openCodeVersion` in `run-metadata.json`. Restart OpenCode after checking out the revision containing `.opencode/plugins/nyamnyam-eval.ts` and `.opencode/agent/nyamnyam-eval.md`; plugin and agent configuration are loaded only at startup.
2. The harness launches OpenCode with `NYAMNYAM_EVAL_FIXTURE=m0` or `NYAMNYAM_EVAL_FIXTURE=m1`, then starts a fresh `nyamnyam-eval` agent with exactly one frozen corpus task prompt and only its permitted seed stable IDs.
3. Do not provide the corpus oracle, fixture source, runner implementation, or prior run results to the agent.
4. Use the same model, reasoning configuration, OpenCode system instructions, prompt revision, tool revision, and repository revision for E1-E5 within a condition. The OpenCode adapter records observed model/provider/runtime/reasoning data; the harness may set only prompt/tool revision metadata when they are not otherwise available.
5. The agent calls `nyamnyam_eval_start` once, uses bounded requests until the task reaches its terminal state, then calls `nyamnyam_eval_finalize` once.
6. The evaluator, not the agent, reads the durable artifacts and applies the frozen corpus oracle.

## E5 harness configuration

For E5 only, the harness launches OpenCode with the stale-perturbation mode. The adapter expands that mode into startup-only runner configuration for the first valid `function-validate-user` -> `validate_profile` proposal and the unrelated injected type rename:

```text
NYAMNYAM_EVAL_STALE_PERTURBATION=m0-raw-user-input
```

The runner receives `--stale-perturbation-trigger-function-id function-validate-user`, `--stale-perturbation-trigger-name validate_profile`, `--stale-perturbation-type-id type-raw-user-input`, and `--stale-perturbation-name RawAccountInput`. It inspects the effective review diff and injects the advance only when that diff is exactly one net target function rename, never after an arbitrary successful simulation or a multi-operation request. If the advance cannot be applied, the runner returns an explicit harness failure, invalidates the run, discards the triggering proposal, and exits unsuccessfully after finalization. The evaluated agent has no tool argument or protocol command that can trigger, change, or suppress the perturbation.

## Classification rules

- A missing terminal artifact state invalidates the run for comparison, even if its semantic commit succeeded.
- A verification failure is recorded separately from a semantic commit result; do not represent it as a successful behavioral oracle.
- A tool protocol error remains a `tool-protocol-error`; it must not be folded into a semantic reasoning category.
- The v2 tool adapter removes the initial shell-session restart confounder but does not grant List/numeric structural editing authority. E4 remains partial collection coverage and numeric structural-edit coverage remains blocked.
