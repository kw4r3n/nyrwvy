# F# Milestone 0 rebuild — Phase C candidate — 2026-08-25

## Status

Prepared for compiler and test verification. This phase defines the first executable-shape semantic graph facts but still stops before IR or backend work.

## Authoritative call representation

A direct call exists exactly once: as a `SemanticNodeKind.DirectCall` node owned by one `SemanticFunction`.

```text
SemanticFunction
  Parameters[]
  Nodes[]
    SemanticNode
      Id
      Kind = DirectCall(targetFunctionId, arguments[])
  ResultNodeId
```

There is deliberately no `SemanticFunction.Calls` collection and no second body object containing an independent copy of call arguments. Caller/callee queries, semantic operations, persistence, validation, and future lowering must all derive from the same function-owned node graph.

Arguments associate a callee `ParameterId` with a caller-owned value-producing `GraphNodeId`. Parameter-reference nodes and direct-call nodes are both value-producing in this slice. This allows nested calls later without introducing a second call representation.

## Implemented candidate scope

- parameter, graph-node, and obligation stable-ID kinds;
- typed function parameters and declared result types;
- function-owned value graph rooted by `ResultNodeId`;
- parameter-reference and direct-call node kinds;
- graph validation for ownership, target parameters, value types, duplicate bindings, missing arguments, result types, and value-dependency cycles;
- explicit `MissingArgument` obligations with open/resolved provenance;
- `function.add_parameter` with one explicit plan for every affected direct call;
- direct binding to an existing caller value or creation of one blocking obligation per missing call site;
- obligation resolution that mutates the same authoritative direct-call node;
- semantic completeness state (`Complete` or `Incomplete`) without claiming backend execution readiness;
- persistence schema candidate version 2 covering the Phase C graph and obligations;
- semantic diff records for added parameters, attached arguments, created obligations, and resolved obligations.

## Deliberate boundaries

- `SemanticReadiness.Complete` means no blocking semantic obligations remain and the snapshot validates. It does **not** assert backend availability, capability bindings, or lowering support.
- No C# emitter, Typed Semantic IR, Execution IR, viewer, file store, or operation log is introduced here.
- No production stable-ID allocation policy is chosen; Q-002 remains open.
- No Open Question is marked resolved solely because this slice compiles.
- Parameter list order is preserved by persistence; function/type/node collection order is canonicalized where it is not semantic.
- Missing arguments are never filled with a default or guessed value.

## Required verification

The same .NET 10 boundary used for Phase B must pass with warnings as errors:

```shell
dotnet restore Nayamnyam.slnx
dotnet build Nayamnyam.slnx --configuration Release --no-restore
dotnet test Nayamnyam.slnx --configuration Release --no-build --no-restore
```

Tests must demonstrate at minimum:

1. the baseline graph validates;
2. direct calls are stored only as graph nodes;
3. missing arguments are invalid without an explicit open obligation;
4. `function.add_parameter` requires a plan for every affected call;
5. an unresolved call-site impact produces an explicit blocking obligation;
6. resolving that obligation updates the same call node and restores semantic completeness;
7. an existing caller value can satisfy the new parameter without creating an obligation;
8. canonical persistence round-trips the function/node/obligation graph;
9. cross-function value-node references and node-dependency cycles are rejected.

## Gate after Phase C

Do not begin lowering merely because Phase C passes.

The next design review should decide the minimum value-node forms needed by the Milestone 0 user-validation program (literals, products, unions, field access, branching, Result/Option construction and propagation) and then define the Typed Semantic IR boundary against those validated graph facts.
