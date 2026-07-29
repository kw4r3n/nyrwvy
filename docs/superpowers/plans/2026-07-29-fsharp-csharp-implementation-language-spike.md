# F# vs C# Implementation Language Spike Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build two behaviorally equivalent Nayamnyam semantic-core baselines, run a controlled Codex CLI maintenance experiment against each baseline, and select F# or C# using predefined correctness-first criteria.

**Architecture:** The spike is isolated under `experiments/implementation-language-spike/`. Shared specifications, fixtures, prompts, and evaluation rules define one semantic contract; F# and C# implement that contract independently on .NET 10 without third-party runtime dependencies. A Python runner creates disposable trial workspaces, invokes one non-interactive Codex session per trial, runs public and hidden evaluators after the agent exits, and writes reproducible metrics without exposing the hidden evaluator to the agent.

**Tech Stack:** .NET 10 SDK, F#, C#, `System.Text.Json`, Python 3.13 standard library, Git, Codex CLI, Markdown, JSON Schema Draft 2020-12.

## Global Constraints

- Canonical repository documentation and experiment prompts are written in English.
- The F# and C# baselines must expose equivalent semantics, JSON, fixtures, tests, and extension tasks.
- The spike evaluates maintainability only; neither baseline becomes the production core automatically.
- Target framework is exactly `net10.0`.
- Baseline projects use no third-party NuGet packages.
- Compiler warnings are treated as errors in both languages.
- F# code must not use custom operators, active patterns, computation expressions, `Unchecked.defaultof`, `unbox`, or wildcard branches that hide domain cases.
- C# code must enable nullable reference types and must not use `dynamic`, unchecked casts, reflection, `null!`, warning suppression, or default branches that hide domain cases.
- Stable IDs, snapshot IDs, transaction IDs, and obligation IDs are deterministic strings supplied or derived by the experiment contract; random IDs and wall-clock IDs are forbidden.
- One Codex CLI invocation handles the complete four-phase maintenance task for a trial.
- Recommended model configuration is `gpt-5.6-sol`; the exact accepted model string, Codex CLI version, and active CLI configuration must be frozen before calibration.
- Each Codex invocation has a 60-minute wall-clock limit.
- Calibration consists of one unscored F# run and one unscored C# run.
- Scored evaluation consists of three independent F# runs and three independent C# runs.
- No human clarification, repair, steering, or manual code edit is allowed during a scored run.
- Infrastructure failures before the first repository modification invalidate a run and permit one replacement run; failures after code modification count as trial outcomes unless an external service outage is independently confirmed.
- Hidden evaluator sources are never copied into an agent workspace before the Codex process exits.
- Raw trial workspaces are temporary. Commit only specifications, baseline code, evaluator code, summary metrics, final patches, and the final report. Raw JSONL logs remain ignored or are archived outside the repository.

---

## Planned Repository Layout

```text
experiments/implementation-language-spike/
├── README.md
├── experiment.toml
├── .gitignore
├── shared/
│   ├── public/
│   │   ├── AGENTS.md
│   │   ├── semantic-contract.md
│   │   ├── maintenance-task.md
│   │   ├── acceptance.md
│   │   ├── schema/
│   │   │   └── snapshot-v1.schema.json
│   │   └── fixtures/
│   │       └── initial-snapshot-v1.json
│   └── expected/
│       ├── initial-canonical.json
│       └── initial-summary.json
├── fsharp/
│   ├── Nayamnyam.Spike.FSharp.sln
│   ├── src/Nayamnyam.Spike.Core/
│   │   ├── Nayamnyam.Spike.Core.fsproj
│   │   ├── Ids.fs
│   │   ├── Domain.fs
│   │   ├── Validation.fs
│   │   ├── Operations.fs
│   │   ├── CanonicalJson.fs
│   │   └── Scenarios.fs
│   ├── src/Nayamnyam.Spike.Cli/
│   │   ├── Nayamnyam.Spike.Cli.fsproj
│   │   └── Program.fs
│   └── tests/Nayamnyam.Spike.Tests/
│       ├── Nayamnyam.Spike.Tests.fsproj
│       ├── Assertions.fs
│       └── Program.fs
├── csharp/
│   ├── Nayamnyam.Spike.CSharp.sln
│   ├── src/Nayamnyam.Spike.Core/
│   │   ├── Nayamnyam.Spike.Core.csproj
│   │   ├── Ids.cs
│   │   ├── Domain.cs
│   │   ├── Validation.cs
│   │   ├── Operations.cs
│   │   ├── CanonicalJson.cs
│   │   └── Scenarios.cs
│   ├── src/Nayamnyam.Spike.Cli/
│   │   ├── Nayamnyam.Spike.Cli.csproj
│   │   └── Program.cs
│   └── tests/Nayamnyam.Spike.Tests/
│       ├── Nayamnyam.Spike.Tests.csproj
│       ├── Assertions.cs
│       └── Program.cs
├── evaluator/
│   ├── evaluate.py
│   ├── score.py
│   ├── common.py
│   ├── fsharp-hidden/
│   │   ├── Nayamnyam.Spike.Hidden.fsproj
│   │   └── Program.fs
│   └── csharp-hidden/
│       ├── Nayamnyam.Spike.Hidden.csproj
│       └── Program.cs
├── scripts/
│   ├── preflight.py
│   ├── prepare_workspace.py
│   ├── run_trial.py
│   ├── run_public_checks.py
│   ├── summarize_events.py
│   └── run_scored_matrix.py
├── tests/
│   ├── test_config.py
│   ├── test_workspace.py
│   ├── test_evaluator.py
│   └── test_scoring.py
└── results/
    └── .gitkeep
```

---

### Task 1: Create the experiment scaffold and frozen configuration contract

**Files:**
- Create: `experiments/implementation-language-spike/README.md`
- Create: `experiments/implementation-language-spike/experiment.toml`
- Create: `experiments/implementation-language-spike/.gitignore`
- Create: `experiments/implementation-language-spike/evaluator/common.py`
- Create: `experiments/implementation-language-spike/tests/test_config.py`
- Create: `experiments/implementation-language-spike/results/.gitkeep`

**Interfaces:**
- Consumes: Python 3.13 standard library.
- Produces: `ExperimentConfig`, `load_config(path: Path) -> ExperimentConfig`, and one canonical configuration file used by all later runner and scoring tasks.

- [ ] **Step 1: Write the failing configuration tests**

Create `tests/test_config.py` with tests that require exact values and reject extra or missing keys:

```python
from pathlib import Path
import tempfile
import unittest

from evaluator.common import load_config


class ConfigTests(unittest.TestCase):
    def test_loads_canonical_configuration(self) -> None:
        config = load_config(Path(__file__).parents[1] / "experiment.toml")
        self.assertEqual(config.schema_version, 1)
        self.assertEqual(config.model, "gpt-5.6-sol")
        self.assertEqual(config.sandbox, "workspace-write")
        self.assertEqual(config.timeout_seconds, 3600)
        self.assertEqual(config.calibration_runs, 1)
        self.assertEqual(config.scored_runs, 3)
        self.assertEqual(config.languages, ("fsharp", "csharp"))

    def test_rejects_unknown_keys(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "experiment.toml"
            path.write_text(
                'schema_version = 1\nmodel = "gpt-5.6-sol"\n'
                'sandbox = "workspace-write"\ntimeout_seconds = 3600\n'
                'calibration_runs = 1\nscored_runs = 3\n'
                'languages = ["fsharp", "csharp"]\nextra = true\n',
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                load_config(path)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the tests and confirm failure**

Run:

```bash
python -m unittest experiments/implementation-language-spike/tests/test_config.py -v
```

Expected: import failure because `evaluator/common.py` does not exist.

- [ ] **Step 3: Implement the strict configuration loader**

Create `evaluator/common.py` using `tomllib`, a frozen dataclass, exact-key validation, language validation, and positive integer checks. Create `experiment.toml` with:

```toml
schema_version = 1
model = "gpt-5.6-sol"
sandbox = "workspace-write"
timeout_seconds = 3600
calibration_runs = 1
scored_runs = 3
languages = ["fsharp", "csharp"]
```

The loader must reject unknown keys, missing keys, a schema version other than `1`, a sandbox other than `workspace-write`, duplicate languages, and non-positive run counts or timeout.

- [ ] **Step 4: Add experiment hygiene rules**

Create `.gitignore` with:

```gitignore
results/raw/
results/workspaces/
results/**/*.jsonl
results/**/*.jsonl.gz
results/**/*.stderr.log
**/bin/
**/obj/
__pycache__/
*.pyc
```

Create `README.md` stating that the directory is an isolated selection experiment, not the Nayamnyam implementation, and that raw workspaces must be deleted after result extraction.

- [ ] **Step 5: Run tests**

Run:

```bash
python -m unittest experiments/implementation-language-spike/tests/test_config.py -v
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add experiments/implementation-language-spike
git commit -m "chore(spike): scaffold implementation language experiment"
```

---

### Task 2: Define the shared semantic contract, fixture, and maintenance task

**Files:**
- Create: `experiments/implementation-language-spike/shared/public/AGENTS.md`
- Create: `experiments/implementation-language-spike/shared/public/semantic-contract.md`
- Create: `experiments/implementation-language-spike/shared/public/maintenance-task.md`
- Create: `experiments/implementation-language-spike/shared/public/acceptance.md`
- Create: `experiments/implementation-language-spike/shared/public/schema/snapshot-v1.schema.json`
- Create: `experiments/implementation-language-spike/shared/public/fixtures/initial-snapshot-v1.json`
- Create: `experiments/implementation-language-spike/shared/expected/initial-canonical.json`
- Create: `experiments/implementation-language-spike/shared/expected/initial-summary.json`

**Interfaces:**
- Consumes: no implementation code.
- Produces: one language-neutral contract that both baselines and all trial prompts must obey.

- [ ] **Step 1: Write the semantic contract**

Define these baseline entities and invariants in `semantic-contract.md`:

```text
StableId domains:
  TypeId, FunctionId, ParameterId, CallId,
  ObligationId, SnapshotId, TransactionId

TypeEntity:
  id, name

Parameter:
  id, name, type_id

FunctionEntity:
  id, name, ordered parameters

ArgumentBinding:
  parameter_id, value_ref

CallEntity:
  id, callee_id, ordered arguments

ObligationKind v1:
  MissingArgument(call_id, parameter_id)

Obligation:
  id, kind, status=open

Snapshot:
  schema_version=1, id, types, functions, calls, obligations

TransactionResult:
  Rejected(base_snapshot_id, diagnostics)
  Incomplete(snapshot, semantic_diff, blocking_obligation_ids)
  Executable(snapshot, semantic_diff)

SemanticChange v1:
  FunctionRenamed(function_id, old_name, new_name)
  ParameterAdded(function_id, parameter_id)
  ObligationCreated(obligation_id)
```

Specify these deterministic rules:

1. Collections serialize sorted by stable-ID text except function parameter order and call argument order, which preserve declared order.
2. `renameFunction` preserves the function ID and every call edge.
3. `addParameter` appends one parameter and creates one `MissingArgument` obligation per existing call lacking that parameter.
4. New snapshot ID is `snapshot:<transaction-id-without-prefix>`.
5. New obligation ID is `obligation:<transaction-id-without-prefix>:<call-id-without-prefix>:<parameter-id-without-prefix>`.
6. A stale base snapshot rejects without producing a candidate snapshot.
7. Existing snapshot objects are never mutated.

- [ ] **Step 2: Define the initial canonical fixture**

Create a v1 snapshot containing:

- types `type:raw-user-input`, `type:valid-user`, and `type:string`;
- function `function:validate-user` named `validate_user`;
- parameter `parameter:input` of `type:raw-user-input`;
- function `function:main` with one call `call:main-validate` to `function:validate-user`;
- one argument binding from `parameter:input` to `local:raw-input`;
- no obligations;
- snapshot ID `snapshot:initial`.

Write the same bytes to `initial-snapshot-v1.json` and `initial-canonical.json`, ending with one newline.

- [ ] **Step 3: Define the four-phase maintenance task**

Create `maintenance-task.md` with this exact order:

1. **Parameter-name conflict obligation**
   - Add `ParameterNameConflict(function_id, existing_parameter_id, added_parameter_id)`.
   - `addParameter` still adds the parameter and also creates a blocking conflict obligation when another parameter has the same display name.
   - Missing-argument obligations remain required for affected calls.
   - Canonical JSON encodes the kind as `parameter_name_conflict`.

2. **No-change transaction result**
   - Add `NoChange(base_snapshot_id, semantic_diff)`.
   - Renaming a function to its current name returns `NoChange`, creates no snapshot, and returns an empty semantic diff.

3. **Type rename**
   - Replace the function-only rename entry point with `renameSymbol` supporting functions and types.
   - Add `TypeRenamed(type_id, old_name, new_name)`.
   - Type rename preserves ID and all references.

4. **Schema v2 migration**
   - Add `origin_transaction_id` to every obligation.
   - Writers emit schema version `2` only.
   - Readers accept v1 and v2.
   - Migrating a v1 obligation assigns `transaction:migrated`.
   - Re-serializing a loaded v1 snapshot emits canonical v2 JSON.

Require the agent to run all public checks, preserve existing tests, avoid changing task or fixture files, and leave the workspace in a cleanly buildable state.

- [ ] **Step 4: Create the agent constraints file**

`AGENTS.md` must state:

```text
You are participating in a controlled implementation-language experiment.
Implement every requirement in maintenance-task.md in order.
Do not modify shared/public, delete tests, weaken compiler settings, suppress warnings, add third-party packages, use network access, or inspect paths outside this repository.
Run the existing public checks before finishing.
A hidden evaluator will test semantic edge cases after you exit.
```

- [ ] **Step 5: Create the acceptance document and JSON schema**

`acceptance.md` must define public behavior, canonical ordering, required commands, and forbidden escape hatches. The JSON schema must require every v1 field and use `additionalProperties: false` at every object layer.

- [ ] **Step 6: Review cross-document consistency and commit**

Confirm IDs, property names, operation names, and task ordering match exactly across all shared documents.

```bash
git add experiments/implementation-language-spike/shared
git commit -m "docs(spike): define shared semantic contract"
```

---

### Task 3: Build the reviewed F# baseline

**Files:**
- Create all files under `experiments/implementation-language-spike/fsharp/` listed in the repository layout.

**Interfaces:**
- Consumes: the shared v1 contract and fixture.
- Produces: namespace `Nayamnyam.Spike`, pure immutable domain functions, `Scenarios.initialSnapshot`, `Operations.renameFunction`, `Operations.addParameter`, `CanonicalJson.readSnapshot`, `CanonicalJson.writeSnapshot`, and a CLI with `canonical` and `summary` commands.

- [ ] **Step 1: Create the F# solution and warning-strict projects**

Create one library, one CLI executable, and one console-test executable targeting `net10.0`. Set these properties in every project:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
</PropertyGroup>
```

The F# project compile order must be:

```xml
<Compile Include="Ids.fs" />
<Compile Include="Domain.fs" />
<Compile Include="Validation.fs" />
<Compile Include="Operations.fs" />
<Compile Include="CanonicalJson.fs" />
<Compile Include="Scenarios.fs" />
```

- [ ] **Step 2: Write failing baseline tests**

The console test program must include named tests for:

- stable IDs reject blank strings;
- canonical fixture loads and re-serializes byte-for-byte;
- function rename preserves `FunctionId` and call target;
- stale-base rename returns `Rejected` and preserves the original object graph;
- add-parameter appends one parameter;
- add-parameter creates exactly one missing-argument obligation for the initial call;
- add-parameter returns `Incomplete`;
- canonical output order is deterministic across two construction orders.

The test executable must print one `PASS <name>` line per success and return nonzero on the first failure.

- [ ] **Step 3: Implement typed stable IDs**

Use a private generic single-case struct union and empty marker types:

```fsharp
[<Struct; StructuralEquality; StructuralComparison>]
type StableId<'Kind> = private StableId of string

module StableId =
    let create value =
        if System.String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Stable ID must not be blank."
        StableId value

    let value (StableId value) = value

type TypeTag = class end
type FunctionTag = class end
type ParameterTag = class end
type CallTag = class end
type ObligationTag = class end
type SnapshotTag = class end
type TransactionTag = class end
```

Expose aliases such as `type FunctionId = StableId<FunctionTag>`.

- [ ] **Step 4: Implement immutable domain unions and records**

Define the baseline closed sets exactly:

```fsharp
type ObligationKind =
    | MissingArgument of callId: CallId * parameterId: ParameterId

type SemanticChange =
    | FunctionRenamed of functionId: FunctionId * oldName: string * newName: string
    | ParameterAdded of functionId: FunctionId * parameterId: ParameterId
    | ObligationCreated of obligationId: ObligationId

type TransactionResult =
    | Rejected of baseSnapshotId: SnapshotId * diagnostics: Diagnostic list
    | Incomplete of snapshot: Snapshot * diff: SemanticChange list * blocking: ObligationId list
    | Executable of snapshot: Snapshot * diff: SemanticChange list
```

Records must contain immutable lists or maps only. Do not expose mutable collections.

- [ ] **Step 5: Implement validation and operations**

`renameFunction` and `addParameter` must take an explicit transaction ID and base snapshot ID. They must validate stale base, missing target, duplicate IDs, and invalid argument-to-parameter ownership. `addParameter` must derive deterministic snapshot and obligation IDs using the contract.

Every pattern match over `TransactionResult`, `ObligationKind`, and `SemanticChange` must enumerate cases explicitly; wildcard branches are forbidden.

- [ ] **Step 6: Implement manual canonical JSON**

Use `Utf8JsonWriter` and `JsonDocument`. Do not rely on automatic F# union serialization. Write fields in schema order, sort ID-keyed collections by `StableId.value`, preserve parameter and argument order, and end CLI output with one newline.

- [ ] **Step 7: Implement CLI and run checks**

CLI commands:

```bash
dotnet run --project src/Nayamnyam.Spike.Cli -- canonical ../../shared/public/fixtures/initial-snapshot-v1.json
dotnet run --project src/Nayamnyam.Spike.Cli -- summary ../../shared/public/fixtures/initial-snapshot-v1.json
```

Run:

```bash
dotnet build fsharp/Nayamnyam.Spike.FSharp.sln
dotnet run --project fsharp/tests/Nayamnyam.Spike.Tests
```

Expected: build succeeds with zero warnings and all named tests pass.

- [ ] **Step 8: Commit**

```bash
git add experiments/implementation-language-spike/fsharp
git commit -m "test(spike): add reviewed F# baseline"
```

---

### Task 4: Build the reviewed C# baseline

**Files:**
- Create all files under `experiments/implementation-language-spike/csharp/` listed in the repository layout.

**Interfaces:**
- Consumes: the same shared v1 contract and fixture.
- Produces: namespace `Nayamnyam.Spike`, APIs semantically equivalent to the F# baseline, and byte-for-byte equivalent CLI output.

- [ ] **Step 1: Create the C# solution and warning-strict projects**

Use these properties in every project:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
</PropertyGroup>
```

- [ ] **Step 2: Write the same failing baseline tests**

The C# test executable must use the same test names, input fixture, expected semantics, stdout format, and exit behavior as the F# test executable.

- [ ] **Step 3: Implement typed stable IDs**

Use a generic readonly record struct with marker types:

```csharp
public readonly record struct StableId<TKind>
{
    public string Value { get; }

    public StableId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Stable ID must not be blank.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}

public sealed class FunctionTag;
public sealed class TypeTag;
public sealed class ParameterTag;
public sealed class CallTag;
public sealed class ObligationTag;
public sealed class SnapshotTag;
public sealed class TransactionTag;
```

Use explicit type aliases in source files for the ID domains.

- [ ] **Step 4: Implement closed record hierarchies**

Use nested sealed records beneath abstract records:

```csharp
public abstract record ObligationKind
{
    public sealed record MissingArgument(CallId CallId, ParameterId ParameterId)
        : ObligationKind;
}

public abstract record TransactionResult
{
    public sealed record Rejected(SnapshotId BaseSnapshotId, IReadOnlyList<Diagnostic> Diagnostics)
        : TransactionResult;
    public sealed record Incomplete(Snapshot Snapshot, IReadOnlyList<SemanticChange> Diff,
        IReadOnlyList<ObligationId> Blocking)
        : TransactionResult;
    public sealed record Executable(Snapshot Snapshot, IReadOnlyList<SemanticChange> Diff)
        : TransactionResult;
}
```

All public collections must be copied into immutable or read-only representations owned by the record. No caller-provided mutable list may be retained.

- [ ] **Step 5: Implement validation and operations**

Match the F# behavior exactly. Every switch expression over a domain hierarchy must list all known nested cases and throw `UnreachableException` only after explicit cases; do not use a `default` arm to absorb domain cases.

- [ ] **Step 6: Implement manual canonical JSON**

Use `Utf8JsonWriter` and `JsonDocument`, the same schema order and sorting rules, and the same newline behavior as F#.

- [ ] **Step 7: Run checks and cross-check output manually**

```bash
dotnet build csharp/Nayamnyam.Spike.CSharp.sln
dotnet run --project csharp/tests/Nayamnyam.Spike.Tests
```

Then capture both canonical CLI outputs and confirm byte equality with `cmp`.

- [ ] **Step 8: Commit**

```bash
git add experiments/implementation-language-spike/csharp
git commit -m "test(spike): add reviewed C# baseline"
```

---

### Task 5: Add public conformance checks and hidden evaluators

**Files:**
- Create: `experiments/implementation-language-spike/scripts/run_public_checks.py`
- Create: `experiments/implementation-language-spike/evaluator/evaluate.py`
- Create: `experiments/implementation-language-spike/evaluator/fsharp-hidden/Nayamnyam.Spike.Hidden.fsproj`
- Create: `experiments/implementation-language-spike/evaluator/fsharp-hidden/Program.fs`
- Create: `experiments/implementation-language-spike/evaluator/csharp-hidden/Nayamnyam.Spike.Hidden.csproj`
- Create: `experiments/implementation-language-spike/evaluator/csharp-hidden/Program.cs`
- Create: `experiments/implementation-language-spike/tests/test_evaluator.py`

**Interfaces:**
- Consumes: a trial workspace containing one baseline implementation after the maintenance task.
- Produces: `public-checks.json`, `hidden-checks.json`, and an exit status independent of the agent's own success claim.

- [ ] **Step 1: Write failing evaluator tests**

Test that:

- unsupported language names fail;
- a missing workspace fails;
- public checks run build, baseline tests, canonical fixture checks, and forbidden-pattern scans;
- hidden evaluator sources are absent before evaluation and copied only after a marker file confirms the Codex process exited;
- evaluator JSON sorts check names and includes command, exit code, duration, and captured output paths.

- [ ] **Step 2: Implement public checks**

For each language, run:

1. `dotnet build` on the solution;
2. the public console-test executable;
3. CLI canonicalization of the fixture;
4. byte comparison against `shared/expected/initial-canonical.json` for an untouched baseline, or schema-aware checks after the maintenance task;
5. forbidden-pattern scans;
6. detection of deleted or modified `shared/public` files;
7. detection of weakened project warning settings or added package references.

- [ ] **Step 3: Implement hidden semantic checks**

After the agent exits, copy the language-specific hidden project into a temporary directory inside the workspace and reference the candidate core project.

Hidden checks must verify all four phases:

1. adding a duplicate-name parameter creates both `ParameterNameConflict` and required `MissingArgument` obligations;
2. same-name rename returns `NoChange`, preserves the base snapshot ID, and has an empty diff;
3. type rename preserves `TypeId`, changes only display name, preserves parameter type references, and emits exactly one `TypeRenamed` change;
4. loading v1 then writing produces schema v2, every migrated obligation has `transaction:migrated`, native v2 obligations preserve their real origin transaction, and canonical output is stable across two loads.

The hidden program must print one `PASS <name>` line per check and return nonzero on failure.

- [ ] **Step 4: Implement evaluator output**

`evaluate.py` writes deterministic JSON with:

```json
{
  "schema_version": 1,
  "language": "fsharp",
  "public_passed": true,
  "hidden_passed": true,
  "public_failures": [],
  "hidden_failures": [],
  "forbidden_findings": [],
  "modified_protected_files": []
}
```

- [ ] **Step 5: Run evaluator tests and baseline conformance**

```bash
python -m unittest experiments/implementation-language-spike/tests/test_evaluator.py -v
python experiments/implementation-language-spike/scripts/run_public_checks.py --language fsharp --workspace experiments/implementation-language-spike/fsharp
python experiments/implementation-language-spike/scripts/run_public_checks.py --language csharp --workspace experiments/implementation-language-spike/csharp
```

Expected: untouched baselines pass public checks; hidden maintenance checks fail because the four maintenance phases have not been implemented yet.

- [ ] **Step 6: Commit**

```bash
git add experiments/implementation-language-spike/evaluator experiments/implementation-language-spike/scripts experiments/implementation-language-spike/tests
git commit -m "test(spike): add public and hidden evaluators"
```

---

### Task 6: Build Codex preflight, workspace isolation, and trial runner

**Files:**
- Create: `experiments/implementation-language-spike/scripts/preflight.py`
- Create: `experiments/implementation-language-spike/scripts/prepare_workspace.py`
- Create: `experiments/implementation-language-spike/scripts/run_trial.py`
- Create: `experiments/implementation-language-spike/scripts/summarize_events.py`
- Create: `experiments/implementation-language-spike/tests/test_workspace.py`

**Interfaces:**
- Consumes: frozen experiment config, a language baseline, shared public bundle, and an authenticated Codex CLI.
- Produces: isolated trial directory, process logs, final patch, public/hidden evaluation, and normalized metrics.

- [ ] **Step 1: Write failing workspace and command-construction tests**

Tests must verify:

- trial workspace contains only the chosen language baseline plus `shared/public`;
- evaluator, other language baseline, previous results, and repository `.git` metadata are absent;
- workspace is initialized as a new local Git repository with one baseline commit;
- the command contains `codex exec`, `--json`, `--model`, the frozen model, `--sandbox`, and `workspace-write`;
- prompt text is read from `shared/public/maintenance-task.md` plus `AGENTS.md`;
- timeout is exactly 3600 seconds;
- all stdout JSONL and stderr are written outside the workspace.

- [ ] **Step 2: Implement preflight**

`preflight.py` must fail unless:

- `python --version` reports 3.13 or newer within major version 3;
- `dotnet --version` begins with `10.`;
- `git`, `node`, `npm`, and `codex` are executable;
- `codex exec --help` advertises `--json`, `--model`, and `--sandbox`;
- `codex --version`, `dotnet --info`, OS details, and a SHA-256 digest of `experiment.toml`, prompt files, and baseline trees are captured to `results/preflight/manifest.json`;
- the selected model can start a harmless read-only smoke task in a temporary Git repository.

Installation and authentication are performed before preflight using the official Codex CLI flow:

```bash
npm install -g @openai/codex
codex login
```

- [ ] **Step 3: Implement isolated workspace preparation**

Copy files rather than using a Git worktree. Place scored workspaces under a timestamped temporary root such as `/tmp/nayamnyam-language-spike-20260729/`. Initialize a fresh Git repository and commit the baseline so final patches are self-contained and cannot traverse into the parent repository.

- [ ] **Step 4: Implement one-invocation trial execution**

The runner must execute conceptually:

```bash
codex exec \
  --json \
  --model gpt-5.6-sol \
  --sandbox workspace-write \
  "$(cat AGENTS.md maintenance-task.md)"
```

Run it with Python `subprocess.Popen`, stream stdout to `events.jsonl`, stderr to `stderr.log`, enforce the configured timeout, and record the time and Git tree hash at first modification. Do not use session resume, MCP, subagents, or network-dependent repository commands.

After Codex exits:

1. write `codex-exited.marker`;
2. capture `git diff --binary` and `git status --short`;
3. run public checks;
4. run hidden evaluation;
5. run event summarization;
6. write `trial-result.json`;
7. remove build directories from the workspace;
8. retain the workspace only when `--keep-workspace` was explicitly supplied.

- [ ] **Step 5: Implement normalized event metrics**

Parse JSONL without depending on undocumented fields. Preserve unknown events and derive metrics only when fields are present:

- wall-clock duration;
- Codex process exit code;
- command count;
- `dotnet build` attempt count and failed count;
- public-test attempt count and failed count;
- files changed;
- lines added and removed;
- whether warnings or tests were weakened;
- first modification time;
- timeout or infrastructure classification.

- [ ] **Step 6: Run runner tests**

```bash
python -m unittest experiments/implementation-language-spike/tests/test_workspace.py -v
python experiments/implementation-language-spike/scripts/preflight.py --config experiments/implementation-language-spike/experiment.toml
```

Expected: unit tests pass and preflight either succeeds or reports one precise missing prerequisite without modifying the repository.

- [ ] **Step 7: Commit**

```bash
git add experiments/implementation-language-spike/scripts experiments/implementation-language-spike/tests
git commit -m "feat(spike): add isolated Codex trial runner"
```

---

### Task 7: Run calibration and freeze experiment version 1

**Files:**
- Create: `experiments/implementation-language-spike/results/calibration/fsharp/report.json`
- Create: `experiments/implementation-language-spike/results/calibration/csharp/report.json`
- Create: `experiments/implementation-language-spike/results/calibration/report.md`
- Modify only if calibration reveals an experiment defect: shared contract, baseline, evaluator, or runner files.

**Interfaces:**
- Consumes: complete unscored experiment implementation.
- Produces: reviewed calibration evidence and a frozen Git tag for scored trials.

- [ ] **Step 1: Run one unscored F# calibration trial**

```bash
python experiments/implementation-language-spike/scripts/run_trial.py \
  --language fsharp \
  --run-id calibration-fsharp \
  --mode calibration
```

- [ ] **Step 2: Run one unscored C# calibration trial**

```bash
python experiments/implementation-language-spike/scripts/run_trial.py \
  --language csharp \
  --run-id calibration-csharp \
  --mode calibration
```

- [ ] **Step 3: Review only experiment validity**

Check:

- both agents received equivalent visible information;
- both baselines passed before the task;
- hidden checks were genuinely hidden;
- task language did not encode one implementation style more naturally;
- both SDK toolchains worked offline after setup;
- Codex logs and patches were captured;
- no hidden test relies on an undocumented implementation detail;
- timeout was sufficient;
- the runner did not expose parent repository data.

Do not tune the experiment to improve one language's outcome. Calibration scores are not included in the final decision.

- [ ] **Step 4: Fix experiment defects and rerun both calibration trials**

Any contract, baseline, prompt, evaluator, or runner change invalidates both calibration outputs. Delete both calibration workspaces and rerun both languages after the fix.

- [ ] **Step 5: Freeze version 1**

Record exact baseline tree hashes, prompt hashes, model, Codex CLI version, SDK version, and evaluator hash in `results/calibration/report.md`.

```bash
git add experiments/implementation-language-spike
git commit -m "test(spike): freeze calibrated language experiment"
git tag implementation-language-spike-v1
```

No experiment code or prompt changes are allowed after this tag until all six scored runs finish.

---

### Task 8: Execute the six scored trials

**Files:**
- Create: `experiments/implementation-language-spike/results/scored/<run-id>/manifest.json`
- Create: `experiments/implementation-language-spike/results/scored/<run-id>/metrics.json`
- Create: `experiments/implementation-language-spike/results/scored/<run-id>/evaluation.json`
- Create: `experiments/implementation-language-spike/results/scored/<run-id>/final.patch`
- Create: `experiments/implementation-language-spike/results/scored/run-order.json`

**Interfaces:**
- Consumes: immutable tag `implementation-language-spike-v1`.
- Produces: six independent scored outcomes.

- [ ] **Step 1: Record the fixed interleaved run order**

Use this order to reduce simple time-order bias:

```json
[
  "fsharp-1",
  "csharp-1",
  "csharp-2",
  "fsharp-2",
  "fsharp-3",
  "csharp-3"
]
```

- [ ] **Step 2: Run each trial from the frozen tag**

For every run ID, checkout or export `implementation-language-spike-v1`, verify hashes against the calibration manifest, and execute `run_trial.py` with no human interaction.

Example:

```bash
python experiments/implementation-language-spike/scripts/run_trial.py \
  --language fsharp \
  --run-id fsharp-1 \
  --mode scored
```

- [ ] **Step 3: Apply the invalid-run policy exactly**

A run is invalid and may be replaced once only when all are true:

- no tracked file changed;
- failure occurred before the first model-authored edit;
- the runner classifies the cause as authentication, service availability, Codex process startup, or host resource failure;
- the failure evidence is retained.

A timeout, build failure, test failure, incomplete implementation, or agent crash after modification is a scored failure.

- [ ] **Step 4: Verify independence after every run**

Confirm the next workspace is created from the frozen baseline, contains no prior patch or result, and starts with a new Git repository and empty Codex session.

- [ ] **Step 5: Commit compact scored evidence**

Do not commit raw JSONL or workspaces. Commit manifests, normalized metrics, evaluator output, final patches, and infrastructure-failure evidence.

```bash
git add experiments/implementation-language-spike/results/scored
git commit -m "test(spike): record scored Codex language trials"
```

---

### Task 9: Score results and select the implementation language

**Files:**
- Create: `experiments/implementation-language-spike/evaluator/score.py`
- Create: `experiments/implementation-language-spike/tests/test_scoring.py`
- Create: `experiments/implementation-language-spike/results/final/summary.json`
- Create: `experiments/implementation-language-spike/results/final/report.md`
- Modify: `docs/decisions.md`
- Modify: `docs/open-questions.md` only if the implementation-language question is added there during execution.

**Interfaces:**
- Consumes: six scored trial results.
- Produces: deterministic aggregate metrics, one selected language, and decision D-015.

- [ ] **Step 1: Write scoring tests before the scorer**

Test the lexicographic rule with synthetic result sets:

1. more full-trial successes wins;
2. on equal successes, fewer hidden assertion failures wins;
3. on equal hidden failures, fewer forbidden escape findings wins;
4. on equality, fewer public regressions and protected-file modifications wins;
5. on equality, fewer failed build/test attempts wins;
6. on equality, lower median wall-clock time wins;
7. if every preceding measure is tied or differs by less than one failure and five percent in duration, select C# as the lower ecosystem-risk default because F# has not demonstrated a measurable safety advantage.

- [ ] **Step 2: Implement aggregate scoring**

A full-trial success requires:

- Codex process completed before timeout;
- public checks pass;
- all hidden checks pass;
- no forbidden pattern;
- no protected file changed;
- warning strictness remains enabled;
- no test was deleted or weakened.

Report counts and medians separately. Do not collapse primary correctness into an opaque weighted score.

- [ ] **Step 3: Perform fixed manual review**

Review all six final patches using one checklist:

- domain cases are represented explicitly;
- existing invariants remain visible;
- no implementation uses language tricks absent from the baseline style;
- changes are local to relevant modules;
- JSON migration logic is understandable;
- failure behavior is explicit;
- test additions target requirements rather than implementation details.

Manual review may explain a result but may not override a clear correctness difference without documenting the exact reason.

- [ ] **Step 4: Write the final report**

`report.md` must include:

- frozen environment and hashes;
- trial table;
- correctness and hidden-test outcomes;
- compile/test iteration metrics;
- forbidden escape findings;
- patch-size and duration summaries;
- repeated failure patterns;
- language-specific advantages and liabilities;
- selected language;
- confidence level;
- conditions that would justify revisiting the decision.

- [ ] **Step 5: Record D-015**

Append a decision entry to `docs/decisions.md`:

```text
D-015 — The Nayamnyam semantic core is implemented in <selected language>
Status: Accepted
Evidence: experiments/implementation-language-spike/results/final/report.md
```

The actual entry must name the selected language, summarize the decisive evidence, state consequences for the viewer and C# backend, and define revisit conditions. Do not leave angle-bracket text in the committed document.

- [ ] **Step 6: Run scorer tests and commit**

```bash
python -m unittest experiments/implementation-language-spike/tests/test_scoring.py -v
python experiments/implementation-language-spike/evaluator/score.py \
  --results experiments/implementation-language-spike/results/scored \
  --output experiments/implementation-language-spike/results/final

git add experiments/implementation-language-spike docs/decisions.md docs/open-questions.md
git commit -m "docs: select semantic core implementation language"
```

---

### Task 10: Clean temporary artifacts and close the spike

**Files:**
- Modify: `experiments/implementation-language-spike/README.md`
- Verify: repository contains no raw workspace, `bin`, `obj`, `__pycache__`, or uncompressed JSONL log.

**Interfaces:**
- Consumes: completed experiment and decision.
- Produces: a compact, auditable, closed experiment directory.

- [ ] **Step 1: Remove disposable data**

Delete all temporary workspaces, build outputs, cached Python files, calibration workspaces, and raw logs after retaining compact metrics and final patches. Keep a timestamped raw-log archive outside the repository only when deeper later analysis is desired.

- [ ] **Step 2: Mark the spike complete**

Update the experiment README with the selected language, decision link, frozen tag, result report, and statement that neither baseline should be copied wholesale into production without a separate implementation plan.

- [ ] **Step 3: Run final repository checks**

```bash
python -m unittest discover -s experiments/implementation-language-spike/tests -v
git status --short
find experiments/implementation-language-spike \
  \( -name bin -o -name obj -o -name __pycache__ -o -name '*.jsonl' \) -print
```

Expected: all tests pass, Git status is clean, and `find` prints nothing.

- [ ] **Step 4: Commit**

```bash
git add experiments/implementation-language-spike
git commit -m "chore(spike): close implementation language experiment"
```

---

## Experiment Execution Summary

After implementation, the actual comparison procedure is:

```text
1. Build and review equivalent F# and C# baselines.
2. Verify byte-equivalent canonical v1 output.
3. Implement public and hidden evaluators.
4. Freeze Codex model, CLI version, prompt, SDK, and baseline hashes.
5. Run one unscored calibration per language.
6. Fix experiment defects only; rerun both calibrations after any change.
7. Tag implementation-language-spike-v1.
8. Run six independent scored trials in fixed interleaved order.
9. Evaluate every trial outside the agent session.
10. Apply correctness-first lexicographic selection.
11. Record D-015 and clean disposable artifacts.
```

## Official Codex References

- Codex CLI installation and terminal workflow: https://help.openai.com/en/articles/11096431
- Codex access through ChatGPT plans and supported clients: https://help.openai.com/ja-jp/articles/11369540-using-codex-with-your-chatgpt-plan
- Non-interactive shell workflows use `codex exec`: https://openai.com/ja-JP/index/codex-now-generally-available/
- Current GPT-5.6 model guidance: https://developers.openai.com/api/docs/guides/latest-model
