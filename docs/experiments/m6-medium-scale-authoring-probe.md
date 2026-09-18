# M6 medium-scale authoring pressure probe

- **Status:** Exploratory
- **Prepared:** 2026-09-17
- **Baseline:** post-M5 `main`

## Purpose

Run one bounded medium-scale authoring and evolution workload through the maintained `Nyrwvy.AuthoringSession` to determine whether a concrete M6 pressure is now observable.

This is not a new milestone gate or reliability benchmark. A successful run with no decision-relevant friction is a valid result and should not cause speculative M6 infrastructure work.

## Workload

From a fresh maintained snapshot, author:

1. `ValidationOutcome` with `Accepted(message: String)` and `Rejected(reason: String)`;
2. `ConsoleOutput`;
3. `Console.write_line(text: String) -> Unit` associated with `ConsoleOutput`;
4. pure `classify(input: String) -> ValidationOutcome`;
5. five pure `String -> ValidationOutcome` direct callers of `classify`: `validate_signup`, `validate_profile`, `validate_recovery`, `validate_invite`, and `validate_contact`;
6. `process(input: String, console: Console) -> Unit`, which directly calls `classify`, matches the outcome, and writes the bound message/reason.

Then create `ValidationRequest { email: String }` and perform the maintained bounded parameter-type migration:

- preserve `classify`'s existing `FunctionId` and input `ParameterId`;
- change that input from `String` to `ValidationRequest`;
- update `classify` to read `ValidationRequest.email`;
- rewrite all six direct callers in the same reviewed proposal while preserving their existing function identities and signatures;
- publish no invalid intermediate semantic state.

Finally restart the maintained session once, rediscover persisted identities, check execution readiness, verify the expected empty-profile `MissingCapabilityBinding`, and verify explicit `System.Console.WriteLine` emission.

## Observations

Use the already-recorded protocol transcript rather than adding production instrumentation. Record:

- operation counts and rejected requests/retries;
- total recorded request/response JSON bytes;
- `declarations` call count, cumulative response bytes, and largest response;
- `focusFunction` call count and cumulative response bytes;
- migration attempt/accept/commit state;
- affected direct-call count and body-rewrite count;
- stable identity and migrated type after restart;
- execution-readiness and backend-profile results;
- total wall-clock duration as descriptive evidence only;
- any concrete discovery, review, migration, or payload friction reported by the agent.

Do not interpret one run as population-level model reliability or as a benchmark comparison with M4/M5. Payload sizes are interaction artifacts, not token counts.

## Decision rule

After one run, open implementation work only for a decision-relevant observed pressure whose smallest coherent repair is identifiable. In particular:

- large or repetitive discovery payload may justify another focused projection/query repair;
- missing impact facts may justify a narrower review/discovery projection change;
- migration friction may justify a bounded evolution-surface repair only if the current task actually fails or requires avoidable retries;
- latency should trigger deeper validation/fingerprint/persistence profiling only if wall-clock behavior is materially troublesome;
- no meaningful pressure means no M6 implementation issue yet.

## Boundaries

- Reuse the existing isolated M4/M5 OpenCode agent and bridge.
- Do not add semantic authority, a new runner, paging/index infrastructure, or a benchmark corpus.
- Do not add this live/environment-dependent workload to routine CI.
- Do not broaden capability families or the language surface for this probe.
- Keep #109 deferred; this probe is not a build-time investigation.
