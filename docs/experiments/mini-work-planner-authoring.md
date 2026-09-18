# Mini Work Planner authoring probe

- **Status:** Prepared
- **Prepared:** 2026-09-18
- **Baseline:** `main` after the C# symbol-manifest repair (#177/#178)
- **Issue:** #179

## Purpose

Author a second small practical NyamNyam program with a different semantic shape from Verification Planner.

The workload is deliberately adjacent to FurCrawr's task-decision problem, but it models only the next action for one work item. It does not introduce WorkGraph orchestration, concurrency, timers, model routing, durable orchestration state, or a generic state-machine abstraction.

A clean result is evidence that the current language/authoring surface handles this workload. It is not a reason to create a new semantic milestone.

## Program

Author these domain types through the maintained AuthoringSession:

- `PrerequisiteState = Ready | Waiting | Failed`
- `ExecutionState = NotStarted | Running | Failed | Succeeded`
- `ValidationState = NotRequired | Pending | Passed | Failed`
- `WorkItem { prerequisites; execution; validation; attempts: Int64; maxAttempts: Int64 }`
- `NextAction = Start | Wait | Retry | Validate | Complete | NeedsHuman`

Author these pure functions:

- `can_retry(attempts, maxAttempts) -> Bool`
- `next_action(item) -> NextAction`
- `action_label(action) -> String`

Initial `next_action` policy:

1. failed prerequisite -> `NeedsHuman`
2. waiting prerequisite -> `Wait`
3. ready + `NotStarted` -> `Start`
4. ready + `Running` -> `Wait`
5. ready + execution `Failed` -> `Retry` while `attempts < maxAttempts`, otherwise `NeedsHuman`
6. ready + `Succeeded`:
   - validation `NotRequired` / `Passed` -> `Complete`
   - validation `Pending` -> `Validate`
   - validation `Failed` -> `NeedsHuman`

Then perform one ordinary maintenance edit without changing the schema:

> for succeeded execution with failed validation, return `Retry` while retry budget remains; otherwise `NeedsHuman`.

Preserve the existing `next_action` FunctionId and parameter identity by replacing its body rather than recreating the function.

## Verification boundary

The bounded authoring run must:

- review and commit every proposal;
- rediscover published IDs rather than predict them;
- perform exactly one maintenance body replacement;
- restart the maintained session exactly once after all requested changes are committed;
- rediscover type/function/parameter identities after restart;
- reach execution readiness;
- successfully emit C# with the empty profile;
- observe that the `emitCSharp` result contains the public symbol manifest added in #177/#178.

The runner records operation counts, payload sizes, rejected requests, identity continuity, maintenance commit state, readiness, and manifest coverage from the existing protocol transcript.

This first step does not duplicate the semantic rules in a host-side runtime oracle. If authoring is clean, package the resulting persisted program and add a small execution-facing adapter/check as a separate step.

## Decision rule

If the workload succeeds without decision-relevant friction, do not open semantic implementation work.

If it fails, identify the smallest missing construct, authoring operation, or review/discovery fact that directly blocks this program. Do not expand into generic workflow infrastructure.

## Non-goals

- WorkGraph/DAG semantics
- parallel workers
- timers/backoff
- model/provider routing
- automatic task execution
- new capability families
- generic state-machine framework
- routine live CI
- #109 build-time work
