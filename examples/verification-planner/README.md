# Verification Planner

Migration checkpoint: `planner.snapshot.json` was migrated on a preserved copy
from schema 9 to 21; `planner.snapshot.json.migration.json` records unchanged
fingerprints (display path normalized relative to the repository). Every existing
stable ID is retained. `runner/Generated.cs` was regenerated through `emitCSharp`
and the documented `--test --maintained` check passed all 1,024 cases. The original
snapshot is preserved at tag `archive/legacy-examples-pre-cutover-20261019`.

A small pure program created with a maintained AuthoringSession. The semantic authority is the session that opened `planner.snapshot.json`; the JSON was neither edited nor read directly. `runner/Generated.cs` is the unedited output of the public `emitCSharp` for the current snapshot content, regenerated with the emitter of the repository revision that last changed the program.

## Running

From the repository root (requires the .NET 10 SDK):

```sh
dotnet run --project examples/verification-planner/runner -- Documentation
dotnet run --project examples/verification-planner/runner -- Semantics,Documentation --regression
dotnet run --project examples/verification-planner/runner -- AuthoringProtocol --agent
dotnet run --project examples/verification-planner/runner -- EvaluationHarness --environment
```

The outputs are, in order, `fast tests only`, `fast + extended`, `fast + live`, and `fast + extended + live`. (The initial experiment authored the first label in Japanese; it was changed to English through `nyrwvy_replace` on the one `StringLiteral` node, as the maintenance section below describes.)

Area names: `Documentation`, `Semantics`, `AuthoringProtocol`, `Backend`, `Persistence`, `EvaluationHarness`, `CI`. Names are case-insensitive, and several can be given separated by commas. `--agent`, `--environment`, and `--regression` are Bool inputs. The CLI is only an input/output adapter; the decision is made by the generated Nyrwvy functions.

```sh
dotnet run --project examples/verification-planner/runner -- --test --maintained
```

## Types and functions

- `ChangeArea`: a Union of the seven cases above.
- `ChangeRequest`: a Product. `areas: List<ChangeArea>`, `agentInteraction: Bool`, `environmentDependent: Bool`, `knownRegression: Bool`.
- `VerificationLevel`: `FastOnly | FastExtended | FastLive | FastExtendedLive`.
- `area_requires_extended: ChangeArea -> Bool`: initially Semantics / Backend / Persistence. The maintenance change added EvaluationHarness.
- `requires_live: ChangeRequest -> Bool`: agentInteraction or environmentDependent.
- `plan_verification: ChangeRequest -> VerificationLevel`: aggregates extended with ListAny and combines it with live.
- `verification_label: VerificationLevel -> String`: the four display strings.

Fast is always required. knownRegression is kept as information and does not raise the verification level. Documentation, AuthoringProtocol, and CI alone do not add extended. However, an explicit live flag applies regardless of area. "Documentation only" means an ordinary documentation change with the live flags also false. An empty list means no area-derived extended; duplicates and order do not affect the ListAny result.

## Authoring and maintenance by AI

The current entry point is the [standard graph editing tools](../../docs/specs/agent-graph-tools.md). Open this directory's `planner.snapshot.json` with `nyrwvy_open`, check the types and the target function with `nyrwvy_inspect`, and change only the target node with `nyrwvy_replace`. Review the returned before/after structure and receipt, then call `nyrwvy_commit` or `nyrwvy_reject`.

For new authoring, use a separate empty snapshot with `nyrwvy_create_type` / `nyrwvy_create_function`. The app-specific Python authoring helper that produced the initial program is no longer needed and was deleted. Deterministic contract tests for the standard tools (`.opencode/tests/nyrwvy-authoring-workflow.test.mjs`) cover creating the same three types and four functions, maintaining one branch, and saving and resuming; the requests and responses of the initial experiment remain in `transcript.jsonl`. These validate the authoring procedure and are not a new LLM evaluation.

A second maintenance change was made with those tools on 2026-10-05: `nyrwvy_open` on `planner.snapshot.json`, `nyrwvy_inspect` of `verification_label`, one `nyrwvy_replace` of the `FastOnly` arm's `StringLiteral` (from the Japanese label of the initial experiment to `fast tests only`, the label this README documents), `nyrwvy_commit`, then `emitCSharp` to regenerate `runner/Generated.cs`. The review showed the one literal as `previousTarget`/`proposedTarget` with no node added or removed; the function, parameter and result-node IDs were unchanged, and the 1,024-case `--test --maintained` run passed again.

## Work and verification record (initial experiment)

1. Simulated the three types and four functions, confirmed the review, and committed. The initial implementation was complete.
2. Compiled the output of the public emitCSharp and ran all 128 subsets of the 7 areas × all 8 combinations of the 3 flags = **1,024 cases, all passing**.
3. Ended and restarted the session once and rediscovered the declarations. Confirmed that all TypeId / VariantId / FieldId / FunctionId / ParameterId values and signatures were preserved. executionReadiness was `ready` and the function count was 4.
4. Used focusFunction to check the EvaluationHarness branch and its caller. Used simulateReplaceTarget to change only that BoolLiteral from false to true. After checking the review's base/proposed focus and impact, made **exactly one maintenance commit**.
5. After the maintenance change, all 1,024 cases still passed and executionReadiness was ready. The discovery responses for all declarations and signatures matched those right after the restart. The focus after the commit matched the review's proposedFocus exactly.

The only affected site was the single ListAny in plan_verification. No signature change or caller rewrite was needed, so parameter-type migration was not used. No IDs were added or removed, including the target graph node.

Main evidence (from the initial experiment):

- `transcript.jsonl`: all public JSONL requests and responses (including review and commit).
- `initial-declarations.json`, `maintained-declarations.json`: public discovery responses before and after the maintenance change. The response after the restart was byte-identical to the maintained one and is not kept separately.
- `restart-check.json`, `maintenance-check.json`: comparison results and operation counts.
- `maintenance-focus.json`, `maintenance-review.json`: the target, callers, and the before/after review.
- The execution test output (`PASS 1024 exhaustive cases`, before and after the maintenance change) is reproduced by the `--test` and `--test --maintained` runs above. The initial implementation's generated C# differed from the maintained one only in the one `BoolLiteral` and is not kept separately.

## Friction, failures, and constraints (initial experiment)

- **0** semantic requests were rejected and **0** proposals were retried. There were 3 createType calls, 4 createFunction calls, 1 replaceTarget call, and 8 commits in total.
- On restart, the enumeration order of fields / variants changed to ID order, so a simple exact-match check of the discovery JSON failed once. After normalizing the order by ID and checking again, the only difference was the enumeration order. No workaround that reorders function or parameter arrays was used.
- `--help` returned usage and exit 2. One attempt to obtain a subprocess through a dynamic Node import in the browser failed. Neither was a rejection of a semantic request.
- Declaration discovery had to be repeated, 13 times in this work. The review duplicated the string representation of the nested graph and the flattened node representation, which made the payload large for a change to a single Bool.
- The targeted diff's previousTarget does not show the Bool value itself, so the baseFocus / proposedFocus values were also checked. The correspondence between nested match branches and the target ID was also confirmed from the string representation.
- The JSONL keys were not clear enough from the specification alone, so only the transport parser was consulted. The Core implementation and the raw snapshot were not used as the semantic authority of the program.
- A conditional was used in place of a dedicated Bool OR recipe. Multiple areas are expressed naturally with List + ListAny, and there is no blocker that undermines the requirement.
- The execution boundary was the public emitCSharp followed by .NET compilation, not direct execution of JSONL operations. In the first implementation, the CLI adapter had to reimplement the ID encoding of generated symbols; #177 added a public symbol manifest to emitCSharp, and the regeneration script used that manifest. It also checked that the returned symbols actually exist in the output.
- The repository's own implementation was not changed, so the repository-wide fast / extended tests were not run.

## Runner adapter

The runner adapter (`runner/Generated.cs`, `runner/Program.cs`) is kept as committed evidence. `Program.cs` is a hand-written input/output adapter that calls the generated symbols; `Generated.cs` is written only by `emitCSharp`. The Python relay and regeneration script that produced the first version were removed because they depended on the deleted app-specific authoring helper. To change the program, use the standard graph tools described in the "Authoring and maintenance by AI" section, then regenerate `runner/Generated.cs` from the `emitCSharp` reply (`nyrwvy_request` with `op: "emitCSharp"` and `arguments: {"profile": {"kind": "empty"}}`) and rerun `--test --maintained`.
