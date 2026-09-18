# M2 ListMap structural agent protocol v1 verification — 2026-08-31

## Result

The dedicated M2.3 ListMap agent protocol is implemented and clean-verified.

```text
protocol: m2-listmap-structural-v1
candidate: 3727907a54931221243754d859b1b9e24a1b1c57
implementation PR: #84
merge commit: 25cfd06ef00da15ec4aa941ca1607e4f4aec86b4
.NET SDK: 10.0.111
clean local verification: PASS
focused protocol/structural tests: 20/20 PASS
```

The completed Float64 protocol `m2-structural-v1`, corpus, condition, and 30-attempt evidence remain unchanged historical evidence. This verification adds a separate ListMap protocol rather than widening or reinterpreting the frozen Float64 condition.

## Agent-visible surface

The versioned runner is:

```text
tools/Nyrwvy.AgentEval.ListMapStructural
protocol: m2-listmap-structural-v1
```

Agent-visible commands are limited to:

- `describeV1`
- `stateV1`
- `focusFunctionV1`
- `simulateListMapV1`
- `commitListMapV1`
- `rejectListMapV1`
- `metricsV1`

`simulateListMapV1` accepts only existing semantic IDs and exact inspected-state guards:

```text
targetFunctionId
expectedBaseSnapshotId
expectedCurrentResultNodeId
sourceParameterId
mapperFunctionId
```

The protocol does not expose:

- raw `Snapshot`, `ValueGraph`, or `SemanticNode` payloads;
- caller-selected production `GraphNodeId`s;
- a recursive List or generic expression recipe;
- generic call/node constructors;
- lambda or closure construction;
- effectful callback authority;
- arbitrary graph editing.

Successful review preserves requested `SourceParameterId` / `MapperFunctionId` separately from the system-materialized `ParameterReference` / `ListMap` node identities and structural diff.

## Verified behavior

The focused verification covers:

- exact versioned describe/authority surface;
- state and bounded function-focus discovery;
- successful shallow ListMap simulation, review, and commit;
- requested semantic IDs versus materialized graph IDs;
- structured expected-base/result/source/mapper diagnostics;
- stale reviewed-proposal rejection and explicit reject cleanup;
- opaque process-local proposal handles;
- usage metrics and pending-proposal accounting;
- strict malformed/unknown-field/widened-payload rejection;
- exact CLI protocol pinning;
- the underlying bounded ListMap structural workflow, including persistence and semantic-to-C# behavior.

## Findings discovered before the verified candidate

Local verification intentionally retained several pre-candidate failures as engineering evidence.

### F# authoring/inference findings

Earlier candidates exposed compile/test-authoring problems only:

1. an interpolated diagnostic embedded `String.concat ", " ...` directly and triggered FS3373;
2. an unconstrained `JsonElement.TryGetProperty` name triggered FS0041;
3. a chained `GetProperty(...)[0].GetProperty(...)` test expression triggered FS0597.

These were repaired with explicit bindings/types and by aligning the ListMap protocol tests with the already-proven typed helper pattern used by the Float64 protocol tests. They did not alter protocol authority or intended semantics.

### Non-object JSON had to fail closed

A later real test reached `ExecuteLine("[]")` and showed that `TryGetProperty("command")` could run before the root JSON kind was checked, causing `InvalidOperationException` rather than a protocol response.

The verified candidate makes `Input.property` reject non-object roots through the normal `ProtocolInputException -> ProtocolError` path. This is a real fail-closed protocol hardening, not merely a test fix.

### Snapshot roundtrip node storage order is not semantic authority

The ListMap persistence test initially assumed `Body.Nodes` would retain materialization order after JSON/Nyason roundtrip. Fingerprints remained exact, while canonical serialization/deserialization could restore the two nodes in a different storage order.

The corrected test no longer treats collection storage order as semantic authority. It still requires:

- exactly two target body nodes;
- the exact target-owned source `ParameterReference`;
- the exact mapper `FunctionId` on `ListMap`;
- the `ListMap` source dependency to reference that materialized parameter node;
- the `ListMap` node to remain the function result;
- exact snapshot fingerprint equality;
- exact semantic fingerprint equality.

Materialization/review order remains tested at the structural workflow boundary where that ordering is part of the reviewed diff. Codec persistence is tested by semantic identity/dependency/fingerprint instead.

## Evaluation gate

This verification unblocks ListMap corpus construction under #77, but it is **not** ListMap model evidence.

Before any ListMap model observation:

1. define deterministic ListMap fixtures and task oracles;
2. freeze the machine-readable corpus on an exact branch/SHA;
3. pin protocol/tool/repository/OpenCode/model/reasoning/prompt revisions;
4. real-runtime preflight the controlled transport;
5. only then begin fresh model attempts.

No ListMap corpus/model run occurred before this protocol verification.

Refs: #52, #76, #77, #82, #84
