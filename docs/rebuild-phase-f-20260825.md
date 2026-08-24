# F# Milestone 0 rebuild — Phase F — 2026-08-25

## Status

**Verified representative Milestone 0 semantic program.**

The exact test branch tree at `cc69c531bd0e0c438ff30781e50bf048b000fdd7` was exercised through pull-request GitHub Actions on 2026-08-25. The PR merge test commit was `c4e75ae32175532949f3f22f1da87f6d2bd6bc0e` against the Phase E rebuild base.

Verification result:

```text
.NET SDK: 10.0.400
restore:  success
build:    success, 0 warnings, 0 errors
tests:    33 passed, 0 failed, 0 skipped
```

Phase F adds no new semantic-core construct. Its purpose is to prove that the structures verified through Phase E are sufficient to represent the complete Milestone 0 semantic demonstration program before IR or backend work begins.

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

There is no wildcard error branch. Adding a new `ValidationError` variant will therefore make this graph fail exhaustiveness validation until the semantic change is addressed.

## `main`

The fixture models a small orchestration function:

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
2. passes the returned Result value and Console capability to `report_validation` through another `DirectCall`;
3. explicitly carries the `ConsoleOutput` effect required by `report_validation`.

The policy parameter is initially unused by `validate_user`; it exists as a real caller-owned semantic value for the required signature-evolution demonstration.

## Signature-evolution proof on the real graph

The fixture re-runs the required Milestone 0 semantic-operation sequence against the representative graph.

### Rename

A transaction renames:

```text
validate_user -> validate_profile
```

The function keeps the same `FunctionId`, and `main`'s direct-call target remains that same stable ID. The semantic diff contains one rename rather than delete/create churn.

### Add parameter

A second transaction adds:

```text
policy: ValidationPolicy
```

to the validation function.

The existing `main -> validate_*` call is intentionally submitted with `LeaveMissing`, even though `main` owns a possible policy value. This proves that Nayamnyam does not guess that the available value is the intended repair.

The resulting snapshot remains structurally valid with one explicit blocking `MissingArgument` obligation and becomes semantically incomplete.

### Resolve obligation

A follow-up transaction explicitly selects `main`'s policy value node as the repair.

The transaction:

- updates the same authoritative direct-call node;
- attaches the new parameter edge by stable `ParameterId`;
- marks the obligation resolved with transaction provenance;
- restores semantic completeness.

## Reference persistence proof

The complete representative graph round-trips through strict JSON schema version 4 and reserializes byte-for-byte identically.

This provides a realistic reference fixture for comparing Nyason with JSON. The JSON result remains a baseline, not a persistence-format decision.

## Architectural result

The main Phase F result is negative in a useful sense: **no additional semantic node kind was required to express the Milestone 0 program.**

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

The representative fixture should now serve as the shared input for two derived-layer tasks:

1. **Nyason v0 prototype and comparison** — test a dedicated AI-oriented semantic encoding against the strict JSON baseline using a realistic graph rather than toy objects.
2. **Typed Semantic IR** — define the first backend-neutral typed projection from a fully validated snapshot without adding editing/storage concerns to the IR.

Nyason can be explored first because it does not alter graph semantics and the user has explicitly chosen to investigate a dedicated Nayamnyam encoding. Typed Semantic IR should consume the same verified semantic fixture after the encoding experiment or in the immediately following phase.
