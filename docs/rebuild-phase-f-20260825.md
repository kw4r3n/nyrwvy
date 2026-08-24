# F# Milestone 0 rebuild — Phase F — 2026-08-25

## Status

**Representative Milestone 0 semantic program established. Compile-backed verification was corrected and completed during the Nyason validation phase.**

The original Phase F document recorded a `33 passed` test result. That statement was inaccurate: `MilestoneZeroGraph.fs` existed on the Phase F branch, but `MilestoneZeroTests.fs` was not present and the F# test project did not compile the Milestone 0 fixture as a test source. The graph fixture itself was merged, but the claimed Phase F test count was not evidence for the full scenario.

This was detected while bringing the Nyason experiment under the real compiler boundary. The test project was repaired so that both `MilestoneZeroGraph.fs` and a real `MilestoneZeroTests.fs` are explicit compile inputs. The later validation run on 2026-08-25 used .NET SDK `10.0.400` and produced:

```text
restore:  success
build:    success, 0 warnings, 0 errors
tests:    42 passed, 0 failed, 0 skipped
```

That 42-test run includes the four compile-backed Milestone 0 scenario tests plus the Nyason tests and supersedes the earlier `33 passed` claim. This correction is intentionally recorded rather than silently rewriting history.

Phase F itself adds no new semantic-core construct. Its purpose is to prove that the structures verified through Phase E are sufficient to represent the complete Milestone 0 semantic demonstration program before IR or backend work begins.

## Representative graph

The fixture constructs the program directly as typed semantic graph data without parsing source text.

Declared data types:

```text
RawUserInput
  name: String
  email: String
  display_name: Option<String>

ValidUser
  name: String
  email: String
  display_name: Option<String>

ValidationError
  EmptyName
  InvalidEmail

ValidationPolicy
  {}
```

The `ValidationPolicy` value exists in the fixture so the required signature-change/obligation scenario can later resolve to a real caller-owned value without introducing a guessed default.

## `validate_user`

The fixture models:

```text
validate_user(input: RawUserInput)
    -> Result<ValidUser, ValidationError>
```

Semantically:

1. access `input.name` by stable `FieldId`;
2. evaluate `StringIsEmpty(name)`;
3. if empty, construct `ValidationError.EmptyName` and `Result.Err`;
4. otherwise access `input.email` and evaluate `StringContains(email, "@")`;
5. if the email contains `@`, construct `ValidUser` by stable field associations and return `Result.Ok`;
6. otherwise construct `ValidationError.InvalidEmail` and return `Result.Err`.

The nested conditionals use nested `ValueGraph` scopes. Branch result types converge explicitly to the same `Result<ValidUser, ValidationError>` type.

## `report_validation`

The fixture models:

```text
report_validation(
    result: Result<ValidUser, ValidationError>,
    console: Capability<Console>
) -> Unit
requires ConsoleOutput
```

The function performs an explicit `MatchResult`.

- `Ok` writes a success message through `Console.write_line`.
- `Err` converts the error binding to a graph value with `PatternReference`, then performs an exhaustive `MatchUnion` over `ValidationError`.
- `EmptyName` and `InvalidEmail` each use an explicit Console capability invocation.

There is no wildcard error branch. Adding a new `ValidationError` variant therefore makes this graph fail exhaustiveness validation until the semantic change is addressed.

## `main`

The fixture models:

```text
main(
    input: RawUserInput,
    policy: ValidationPolicy,
    console: Capability<Console>
) -> Unit
requires ConsoleOutput
```

`main`:

1. calls `validate_user(input)` through one authoritative `DirectCall` node;
2. passes the returned `Result` value and Console capability to `report_validation` through another `DirectCall`;
3. explicitly carries the `ConsoleOutput` effect required by `report_validation`.

The policy parameter is initially unused by `validate_user`; it exists as a real caller-owned semantic value for the required signature-evolution demonstration.

## Compile-backed scenario tests

The corrected test suite now proves the following against this exact representative graph.

### Semantic completeness

The initial graph passes `SnapshotValidator.validate` and evaluates to `SemanticReadiness.Complete`.

### Reference JSON round-trip

The complete graph round-trips through strict JSON schema version 4 and reserializes byte-for-byte identically.

### Rename identity continuity

A transaction renames:

```text
validate_user -> validate_profile
```

The function retains its original `FunctionId`, and `main` continues to target that stable ID through the same `DirectCall` node. The semantic diff reports a rename rather than delete/create churn.

### Signature evolution and obligation resolution

A second transaction adds:

```text
policy: ValidationPolicy
```

The existing `main -> validate_*` call is intentionally submitted with `LeaveMissing`, even though `main` already owns a possible policy value. Nayamnyam therefore does not guess that the available value is the intended repair.

The proposed snapshot remains structurally valid with one blocking `MissingArgument` obligation and evaluates as incomplete. A follow-up transaction explicitly selects `main`'s policy value node, updates the same authoritative direct-call node, resolves the obligation with transaction provenance, and restores semantic completeness.

## Architectural result

The important Phase F result is negative in a useful sense: **no additional semantic node kind was required to express the Milestone 0 program.**

The current combination of:

- product and union declarations;
- stable fields and variants;
- `Option` / `Result` construction and matching;
- nested `ValueGraph` branches;
- explicit string predicates;
- direct calls;
- typed capabilities;
- explicit effects;
- semantic transactions and obligations;

is sufficient for the representative semantic program.

That is evidence to stop expanding the semantic model and move to derived representations and encoding experiments.

## Next gate

The representative fixture is now the shared input for:

1. **Nyason v0 prototype and comparison** — compare a dedicated AI-oriented semantic encoding with the strict JSON baseline using the real graph;
2. **Typed Semantic IR** — define the first backend-neutral typed projection from a fully validated snapshot without adding editing or storage concerns to the IR.

The Nyason mechanical experiment is recorded separately. The persistence-format choice remains open until AI comprehension/editing reliability and focused-slice costs are measured, not merely byte size.
