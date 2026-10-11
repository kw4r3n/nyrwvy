# Fake-value diagnostic paths

Base: `90e495c75dd6915440ea22c96222aefcf04f723e`. Isolated from draft PR #376.

Original stream evidence recovered by the local verifier: `RunContainerTypeMismatch`, with the flat String/Bool/Int64 representation explanation but no operation/case/response location. Recovered evidence archive SHA-256: `8c0baabb30512f2e06280e832ea2eaffb5c9f0f95e475012cb32f9bb74afb56f`.

## Two separate failures

- A flat Result with `okKind: "string"` and `errorKind: "string"` takes a raw JSON string in `value`. Supplying a typed `{kind: "string", value: ...}` there is a wire error. Before this patch it reports only `A String run value must be a JSON string.`
- A syntactically well-formed nested Result with scalar types on both sides parses, but the runner intentionally rejects it with `RunContainerTypeMismatch`. The semantic diagnostic walker previously did not descend into fake bindings, so it omitted the response location. Nested Unit/String remains supported because Unit is not a flat scalar kind.

The patch preserves acceptance rules and error categories. An independent source review found and resolved lost static constraint explanations and missing metadata-entry indices; depth/size/delay reasons are preserved using an exact allowlist of parser-owned messages. Wire errors in fake operations use zero-based indexed paths such as `arguments[1].operations[1].cases[1].responses[1].value.value` and shape-only explanations. They never echo values, user-supplied property names, or response kinds. Existing operation/parameter selector diagnostics remain intact; selector names are not treated as response payloads. Semantic errors retain the existing structured `details` format and name-based argument prefix, then add operation/case/response indices and a value path. A nested scalar Result example becomes `arguments.source.operations[1].cases[1].responses[1].value`, with the existing flat `okKind/errorKind` repair guidance.

## Lexical None frame: concrete authoring cost

The worker-stream monitor wraps two Result matches, then a MatchOption on the collector snapshot. Inside its None branch, frame 0 is empty, frame 1 contains the right producer's result, and frame 2 the left producer's result. Therefore resuming monitor needs `leftAlive = patternReference(value, 2)` and `rightAlive = patternReference(value, 1)`. Using depths 1/0 fails with `PureExpressionPatternSlotNotFound`. The minimal analogue is Bind(Unit, MatchOption(None, Unit, patternReference(value, depth))): depth 0 refers to the empty None frame; depth 1 refers to Bind's value. The existing example regression models this explicitly. No DSL or lexical-scope semantics are changed here.

## Verification

Verified locally against main `90e495c75dd6915440ea22c96222aefcf04f723e`, with source and tests byte-identical to candidate v7. No local source repairs were required for v7.

- Baseline red: both new facts fail against the unchanged base implementation. The same test bytes are used for patched verification.
- Patched new facts: 2 passed.
- Focused PureRunBindingTests / PureRunByNameTests / ActionableRunDiagnosticsTests: 29 passed.
- Repository fast Core suite: 852 passed.
- Node ordinary-authoring / script checks: 32 passed.
- Zero failures and zero skips in these final runs.
- Full Extended suite: not run.

The tests cover malformed flat/nested payloads, multiple operations/cases/responses, nested lists, retained valid flat String/String and nested Unit/String forms, ID/name entry points, unchanged operation/parameter selector diagnostics, static delay/depth constraint explanations, payload/key non-disclosure, and unchanged persisted workspace contents.

Initial candidates exposed test-only overload, indentation, and JSON-fixture quoting errors. After those were repaired, focused tests exposed a real compatibility regression in unknown operation/parameter messages; v7 preserves these selector diagnostics and passes the existing regressions. These earlier blocked/failing stages are not counted as successful verification.

Final local evidence archive SHA-256: `bc70903e3953f76329cb4d214fc1892fd21ba2681b9af8cc205cecc19f489598`; local report: `V7-REPORT.ja.md`. The final deliverable updates this verification record only after v7; production and test source bytes remain unchanged.

No push, merge, paid evaluation, or change to PR #376.
