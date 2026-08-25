# F# Milestone 0 rebuild — Phase I Execution IR — 2026-08-25

## Status

**Verified through a clean local compiler/test boundary because GitHub Actions capacity was unavailable.**

The exact verified branch head was:

```text
e05f0be3ec62247c1997965a398026cf71c683b6
```

Verification environment and result:

```text
.NET SDK: 10.0.111
clean:     bin/ and obj/ removed before restore
restore:   success
build:     success, 0 warnings, 0 errors
full test: 59 passed, 0 failed, 0 skipped
ExecutionIrTests:      6 passed, 0 failed, 0 skipped
ExecutionIrCodecTests: 3 passed, 0 failed, 0 skipped
```

The run used:

```text
scripts/verify-local-20260825-1427.sh --clean
```

The script mirrors CI and then explicitly re-runs `ExecutionIrTests` and `ExecutionIrCodecTests` by fully-qualified-name filter. This prevents a missing F# project `<Compile Include=...>` entry from appearing as a false-green full suite.

The same run also reproduced the current persistence comparison baseline:

```text
json-bytes=8440
nyason-bytes=16205
nyason-to-json-byte-ratio=1.920
```

GitHub Actions runs immediately before this local verification were failing before useful job execution because repository Actions capacity was exhausted. Those infrastructure failures are not treated as code failures; the clean local run above is the accepted Phase I evidence.

## Purpose

Phase I is the point where validated language meaning becomes explicit execution structure.

The boundary is:

```text
Persistent Semantic Graph
    -> Typed Semantic IR
    -> Execution IR
         - explicit values
         - explicit evaluation order
         - explicit blocks
         - explicit control transfer
         - explicit pattern extraction
    -> backend-specific generation
```

Execution IR remains backend-neutral. It contains no C# class-layout decision, C# identifier, `System.Console` reference, exception policy, or generated-source concern.

## Execution values

Every executable value has its own deterministic `ExecutionValueId`, complete `TypeRef`, and provenance:

```text
ExecutionOrigin
  FunctionParameter(ParameterId)
  SemanticNode(GraphNodeId)
  PatternBinding(matchNodeId, PatternBindingId)
```

Semantic identities are retained where they matter to language meaning. Execution-local identities are separate so one semantic node can later lower to multiple instructions without forcing graph IDs to become instruction IDs.

## Blocks and terminators

Each function has one explicit entry block and a canonical collection of blocks.

A block contains:

```text
Block
  Parameters[]
  Instructions[]
  Terminator
```

Block parameters carry values across structured-branch merges. Terminators are explicit:

- `Return`
- `Jump`
- `Branch`
- `MatchUnion`
- `MatchOption`
- `MatchResult`

This means target backends do not reconstruct control flow from nested semantic graphs.

## Structured lowering

### Conditional

A semantic conditional lowers to:

```text
current block
  -> Branch condition trueBlock falseBlock

trueBlock  -> Jump merge(result)
falseBlock -> Jump merge(result)

merge(resultParameter)
```

The semantic conditional node's execution value is the merge block parameter.

### Union match

Each declared variant receives an explicit target block. Variant payload fields become `ExtractUnionField` instructions with `PatternBinding` provenance before the arm body executes.

### Option match

The `Some` arm receives an explicit `ExtractOptionSome` instruction. `None` has no invented payload.

### Result match

The two arms begin with `ExtractResultOk` and `ExtractResultErr` respectively. Backends therefore do not infer which payload a match binding represents.

## Dependency ordering

Execution ordering is derived from Typed Semantic IR references.

For structured nodes, dependencies include both:

1. the immediate condition/input value; and
2. free outer-node references used by nested child graphs.

The second rule matters. A child graph may legally reference values owned by its parent graph; lowering must not enter that child before those values are available.

Within the set of ready pure nodes, stable semantic identity provides deterministic ordering. That ordering is only a reproducibility rule, not a language-level evaluation-order claim.

## Effect-order rule

Phase I deliberately refuses to invent observable order.

`ExecutionIrAnalysis.validateEffectOrder` classifies as effectful:

- `CapabilityInvoke`; and
- `DirectCall` to a function with non-empty `RequiredEffects`.

If two effectful nodes in one value graph are not ordered by a dependency path in either direction, lowering returns:

```text
AmbiguousEffectOrder(functionId, firstNodeId, secondNodeId)
```

It does **not** sort those effects by stable ID and silently choose an execution order.

This is intentionally conservative. A future semantic ordering construct may make such a program valid, but the backend layer must never manufacture that semantic decision.

## AI-first implementation structure

The first lowerer grew into a large single file and produced many F# inference collisions because Semantic IR and Execution IR intentionally contain structurally similar records.

Rather than solving that with scattered casts, Phase I split the implementation into role-specific modules:

```text
ExecutionIrModel.fs
    IR schema only

ExecutionIrAnalysis.fs
    deterministic IDs
    dependencies
    free-reference analysis
    effect-order validation

ExecutionIrDeclarations.fs
    typed declaration projection

ExecutionIrLoweringCore.fs
    graph/control-flow lowering

ExecutionIrCodec.fs
    canonical artifact serialization
```

Boundary functions use explicit F# types where record field names overlap. This is not only a compiler workaround: it makes layer ownership locally visible to AI agents editing the implementation.

The obsolete monolithic lowerer was removed after the modular lowerer passed build and tests.

## Canonical serialization

`ExecutionIrCodec` is a derived IR serializer with its own schema version.

Canonicalization rules distinguish semantic from non-semantic order:

### Canonicalized

- type collection by `TypeId`;
- type fields and variants by stable ID;
- capability collection and operations by stable ID;
- function collection by `FunctionId`;
- block collection by `ExecutionBlockId`;
- call/capability/field associations by their stable target identity;
- required-effect sets by `EffectId`.

### Preserved exactly

- function parameter order;
- block parameter order;
- **instruction order**;
- ordered generic arguments such as `Result<ok,error>`.

Instruction order is executable meaning. The serializer must never canonicalize it away.

The serializer tests require and now verify:

1. serializing one `ExecutionProgram` repeatedly produces identical bytes;
2. reordering non-semantic entity/block collections produces identical bytes;
3. reversing instructions in a real M0 block produces different bytes.

## Provenance

Execution IR does not copy edit-time metadata, but it retains enough provenance for downstream diagnostics:

- semantic function identity on every execution function;
- `GraphNodeId` on semantic-node-derived instruction results;
- pattern binding identity and owning match node on extracted payload values;
- semantic parameter identity on function execution parameters.

A C# compiler/generator diagnostic can therefore be mapped back toward the semantic graph without using generated names as identity.

## Representative M0 evidence

The compile-backed tests prove that the representative M0 program lowers as follows:

- `main` contains the validation call before the reporting call in explicit instruction order;
- validation conditionals become branch/merge blocks;
- Result and union matches become explicit match terminators and extraction instructions;
- every block is terminated;
- execution values retain graph or pattern provenance;
- an added independent Console invocation makes lowering fail with `AmbiguousEffectOrder` instead of receiving an arbitrary order.

## What Phase I does not decide

Phase I deliberately does not decide:

- C# product/union representation;
- generated C# identifier spelling;
- whether C# uses labels, a state machine, or reconstructed structured syntax;
- Console adapter class/interface shape;
- exception mapping;
- Native AOT behavior;
- optimization;
- general async/cancellation ordering.

Those are backend or later-language questions.

## Next gate

Phase I is ready to be squashed into `rebuild/fsharp-m0-20260825`.

The next phase is the first C#/.NET backend slice. Its hard boundary is:

> The C# emitter consumes `ExecutionProgram` only.

It must not read `Snapshot`, JSON, Nyason, Semantic Graph nodes, or Typed Semantic IR directly, and it must not repeat name resolution or type inference already completed upstream.
