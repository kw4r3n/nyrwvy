# Expression structural authoring

**Status:** Current

This specification defines the reusable cross-domain construction surface for agent-authored expressions, including calls and capability invocations within a target function's already-declared effects.

The purpose is to separate two representations that serve different jobs:

```text
ephemeral typed construction intent
  -> preflight type/scope/effect validation
  -> system-side materialization
  -> persistent typed semantic graph
```

`ExpressionRecipe` is not authoritative program state. It is a bounded tree-shaped request used to construct authoritative semantic nodes without giving the caller production `GraphNodeId` or `PatternBindingId` authority.

## Scope

`AgentExpressionStructuralWorkflow` replaces the complete body of one existing function with a body materialized from `ExpressionRecipe`.

The maintained surface covers primitive scalar expressions, the current pure-callback List family, structured values, complete direct calls, capability invocation, scoped value binding, and control flow:

```text
RecipePatternSlot =
  | Field(FieldId)
  | Value
  | Error

RecipePatternReference =
  ScopeDepth: non-negative integer
  Slot: RecipePatternSlot

ExpressionRecipe =
  | Parameter(ParameterId)
  | ParameterSlot(index)
  | PatternReference(RecipePatternReference)
  | StringLiteral(String)
  | BoolLiteral(Bool)
  | UnitLiteral
  | Int64Literal(Int64)
  | Float64Literal(Float64Bits)
  | StringIsEmpty(expr)
  | StringContains(value, substring)
  | StringEqual(left, right)
  | StringLessThan(left, right)
  | ValueEqual(left, right)
  | StringConcat(left, right)
  | Int64ToString(expr)
  | StringParseInt64(expr)
  | Int64ToHexString(expr) | StringParseInt64Hex(expr)
  | StringRepeat(text, count)
  | StringLength(expr)
  | StringSlice(expr, expr, expr)
  | StringCodePoints(expr)
  | StringIndexOf(value, substring, from)
  | StringSpan(value, set, from)
  | StringSpanBack(value, set, end)
  | StringLastIndexOf(value, substring)
  | StringStartsWith(value, prefix)
  | StringEndsWith(value, suffix)
  | StringSplit(value, separator)
  | StringSplitLines(value, breaks)
  | StringReplace(value, pattern, replacement)
  | StringTrim(expr)
  | StringTrimStart(expr)
  | StringTrimEnd(expr)
  | StringJoin(list, separator)
  | StringToLower(expr)
  | StringToUpper(expr)
  | StringUtf8ByteCount(expr)
  | StringDecodeUtf8Lossy(expr)
  | StringFromCodePoint(expr)
  | StringToCodePoint(expr)
  | Int64WrappingAdd(left, right)
  | Int64WrappingSubtract(left, right)
  | Int64WrappingMultiply(left, right)
  | Int64FloorDivide(left, right)
  | Int64FloorModulo(left, right)
  | Int64Min(left, right) | Int64Max(left, right) | Int64WrappingAbs(expr)
  | Int64BitAnd(left, right) | Int64BitOr(left, right) | Int64BitXor(left, right) | Int64BitNot(expr)
  | Int64ShiftLeft(value, count) | Int64ShiftRight(value, count)
  | Int64CheckedAdd(left, right) | Int64CheckedSubtract(left, right) | Int64CheckedMultiply(left, right)
  | Int64Equal(left, right)
  | Int64LessThan(left, right)
  | Int64LessThanOrEqual(left, right)
  | Int64GreaterThan(left, right)
  | Int64GreaterThanOrEqual(left, right)
  | Float64Add(left, right)
  | Float64Subtract(left, right)
  | Float64Multiply(left, right)
  | Float64Divide(left, right)
  | Float64Equal(left, right)
  | Float64LessThan(left, right)
  | Float64IsNaN(expr)
  | Float64IsFinite(expr)
  | Float64IsInfinite(expr)
  | Int64ToFloat64(expr)
  | Bits32FromInt64(expr) | Bits32ToInt64(expr) | Bits32Not(expr)
  | Bits32Xor(left, right) | Bits32And(left, right) | Bits32Or(left, right) | Bits32Add(left, right)
  | Bits32RotateRight(value, amount) | Bits32ShiftRight(value, amount) | Bits32ShiftLeft(value, amount)
  | Float64RoundToInt64(expr) | Float64FloorToInt64(expr)
  | Float64Sqrt(expr) | Float64Abs(expr) | Float64Negate(expr)
  | Float64Min(left, right) | Float64Max(left, right)
  | Float64Floor(expr) | Float64Ceiling(expr) | Float64Truncate(expr)
  | Float64Round(value, digits)
  | Float64ToString(expr)
  | StringParseFloat64(expr)
  | ListEmpty(itemType)
  | ListConstruct(itemType, items)
  | ListLength(list)
  | ListReverse(list)
  | ListLast(list)
  | ListDropLast(list)
  | ListRange(start, end)
  | ListItemAt(list, index)
  | ListSlice(list, start, end)
  | ListAppendItem(list, item)
  | ListConcat(left, right)
  | MapEmpty(keyType, valueType)
  | MapInsert(map, key, value) | MapGet(map, key) | MapContainsKey(map, key) | MapRemove(map, key)
  | MapSize(map) | MapKeys(map) | MapValues(map) | MapUnion(left, right)
  | MapFold(map, seed, folderFunctionId, context) | MapFromList(source, keyOfFunctionId, context)
  | MapFilter(map, keepFunctionId, context) | MapGetOrDefault(map, key, default)
  | MapMapValues(map, mapperFunctionId, context)
  | SetEmpty(itemType) | SetFromList(list) | SetInsert(set, item) | SetContains(set, item)
  | SetRemove(set, item) | SetSize(set) | SetToList(set)
  | SetUnion(left, right) | SetIntersection(left, right) | SetDifference(left, right)
  | SetIsSubset(left, right)
  | ListFold(source, seed, folderFunctionId, context)
  | ListEffectFold(source, seed, folderFunctionId, context)
  | ParallelAll(source, workerFunctionId, context)
  | CancellationRequested
  | CancellationShield(bodyFunctionId, context)
  | TaskScope(bodyFunctionId, context)
  | TaskStart(scope, workerFunctionId, context, item)
  | TaskWait(tasks, mode: Any | All, limit option)
  | OneShotCreate(scope, valueType)
  | OneShotComplete(cell, value)
  | OneShotWait(cell, limit?)
  | TaskCancel(task)
  | UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context)
  | ListMap(source, mapperFunctionId, context)
  | ListFilter(source, predicateFunctionId, context)
  | ListSortBy(source, lessThanFunctionId, context)
  | ListMinBy(source, lessThanFunctionId, context)
  | ListIndexOf(list, item)
  | ListLastIndexOf(list, item)
  | ListFindIndex(source, predicateFunctionId, context)
  | ListAny(source, predicateFunctionId, context)
  | ListAll(source, predicateFunctionId, context)
  | ProductConstruct(typeId, fieldBindings)
  | FieldAccess(source, fieldId)
  | ProductUpdate(typeId, source, fieldBindings)
  | UnionConstruct(variantId, fieldBindings)
  | OptionNone(itemType)
  | OptionSome(value)
  | ResultOk(errorType, value)
  | ResultErr(okType, error)
  | Abort(error)
  | CapabilityConstruct(CapabilityId, implementations: (CapabilityOperationId * FunctionId) list)
  | FunctionReference(FunctionId)
  | FunctionInvoke(functionValue, arguments)
  | DirectCall(targetFunctionId, argumentBindings)
  | SelfCall(slotBindings)
  | CapabilityInvoke(capabilityValue, operationId, argumentBindings)
  | Bind(value, body)
  | BoolNot(expr)
  | BoolAnd(left, right)
  | BoolOr(left, right)
  | Conditional(condition, whenTrue, whenFalse)
  | MatchUnion(input, arms)
  | MatchOption(input, someBody, noneBody)
  | MatchResult(input, okBody, errBody)
```

This remains a selected construction algebra rather than a generic wrapper around every `SemanticNodeKind`.

## Request

```text
AgentReplaceFunctionBodyWithExpressionRecipe
  TargetFunctionId: FunctionId
  ExpectedBaseSnapshotId: SnapshotId
  ExpectedCurrentResultNodeId: GraphNodeId
  Replacement: ExpressionRecipe
```

The request references existing semantic declarations by typed stable ID where needed. It does not accept caller-supplied production IDs for newly materialized graph nodes or pattern bindings. It cannot change the target function's signature or `RequiredEffects`.

## Bounds and materialization order

The complete construction tree has these bounds ([decision](../decisions/expression-recipe-bounds.md)); the Milestone 2 bounded Float64 workflow keeps its own 64-node limit:

- maximum recipe nodes: **256**;
- maximum recipe depth: **32**;
- receivers, arguments, condition/input expressions, and every nested branch or Bind body count toward the same bounds;
- every recipe occurrence materializes one semantic node;
- repeated equal subexpressions remain distinct occurrences;
- depth is checked before node count. A `StructuralRecipeDepthLimitExceeded` message states the recipe's actual depth. A `StructuralRecipeNodeLimitExceeded` message states the full node count and up to three of the largest subtrees within the limit, by child-index path from the root and recipe kind, as candidates to extract.

Unary operands materialize before their parent. Binary operands materialize left then right, followed by the parent. `ListConstruct` materializes its item recipes in list order and then the construction node. Higher-order List recipes materialize the source expression first (and for `ListFold`, the seed second) and then the traversal node referencing the existing callback `FunctionId`.

Product and union field bindings are caller-supplied by stable `FieldId`, but their list order is not semantic. After preflight verifies the exact binding set, materialization constructs field recipes in declaration order and stores persistent bindings in that same order.

`DirectCall` arguments are caller-supplied by the callee's stable `ParameterId`, but argument-list order is likewise not semantic. After exact binding validation, argument recipes materialize in callee parameter declaration order and the call node is created last.

`CapabilityInvoke` materializes its capability receiver first, arguments in operation parameter declaration order, and the invocation last. Caller binding-list order does not override this construction order. This is not a runtime sequencing guarantee for independent effectful children; see execution readiness below.

Control-flow condition/input expressions materialize in the current graph. Branch recipes materialize as child `ValueGraph`s, not as eagerly evaluated operands in the parent graph. Union match arms and their pattern-field bindings are canonicalized to declaration order.

`Bind` materializes its input in the current graph and its body in a child graph with one system-owned lexical binding. This explicit scope boundary supplies execution order independently of allocation order.

There is no caller-controlled CSE, arbitrary DAG sharing, local-node reuse, or operand reordering. Repeating an effectful expression represents separate evaluations; use the lexical value slot in a Bind body to reuse one result.

## Preflight

Before allocating replacement graph-node or pattern-binding IDs, simulation checks:

1. the expected base snapshot is still current;
2. the target function exists;
3. the expected current result node still matches the target body;
4. recipe depth and node count are within bounds across all children and nested branches;
5. every `Parameter` belongs to the target function;
6. every `PatternReference` resolves to a visible lexical Bind-body or match-arm frame and slot;
7. every scalar operation receives operands of its required primitive type;
8. every explicit `TypeRef` is valid in the current snapshot and has valid builtin-generic arity;
9. List construction/query operands have exact compatible List/item types;
10. higher-order List callbacks satisfy the applicable pure callback contract (unary for Map/Filter/Any/All, binary for Fold);
11. `ProductConstruct` names an existing product type and binds every declared field exactly once with an exact-compatible value type;
12. `FieldAccess` names a product field and its source produces the exact owning named type; `ProductUpdate` names an existing product type, its source produces exactly that type, and it binds at least one declared field of that type, each at most once, with an exact-compatible value type;
13. `UnionConstruct` names an existing variant and binds every variant field exactly once with an exact-compatible value type;
14. `OptionNone`, `ResultOk`, and `ResultErr` explicit type arguments are valid;
15. `DirectCall` names an existing function whose required effects are all already declared by the target function;
16. every `DirectCall` callee parameter is bound exactly once by its stable `ParameterId`, with no unknown or omitted parameter;
17. every `DirectCall` argument recipe exactly matches the declared callee parameter type (a generic callee's type arguments by matching), every `SelfCall` binds each owner parameter slot exactly once with the owner's exact parameter type, and a `DirectCall` to the owner itself takes the owner's exact parameter types as a `SelfCall` does;
18. `CapabilityInvoke` names an existing operation and its receiver produces that operation's exact owning capability type;
19. every capability-operation parameter is bound exactly once by its stable `CapabilityParameterId`, with no unknown or omitted parameter and with exact child types;
20. the target function already declares the capability operation's effect;
21. every `Conditional` condition produces `Bool` and both branches produce exactly the same type; every `BoolNot` operand and `BoolAnd`/`BoolOr` left operand produces `Bool`, and every `BoolAnd`/`BoolOr` right operand produces `Bool` or aborts;
22. every match input has the required union/`Option<T>`/`Result<T,E>` shape;
23. `MatchUnion` names every declared variant in exactly one arm (an `otherwise` arm takes the rest), with no unknown or duplicate variants;
24. all branches of one match produce exactly the same result type;
25. the inferred recipe result type exactly equals the target function result type;
26. no open or resolved obligation targets a node in the body being replaced.

This replacement guard is unchanged by function deletion's bounded retirement
of resolved obligations: that operation removes the owner itself and exposes the
exact retired records in its review. It does not authorize dropping obligations
from a body that remains alive.

For `Bind`, input inference uses only the enclosing frames. Its body receives a new `Value` slot with exactly the input's inferred type; the Bind expression has the body's type. There is no inferred conversion or caller-supplied binding type.

There is no implicit numeric conversion, collection item conversion, or fallback type coercion.

### Diagnostic text

A preflight rejection keeps its code; its message is written for the author of the recipe:

- Types are rendered by display name in the wire `TypeRef` vocabulary: `String`, `Option<String>`, `List<Tail>`, `Result<Int64, Finding>`, `(String, Int64) -> Bool`, a type parameter by its name. A named type or capability that is not in the snapshot keeps its ID (`type '01a0…'`). Where two named types are compared, as in a `FieldAccess` on the wrong owner, both carry their IDs: `Field access to 'line' (id) requires 'Finding' (id), but the source recipe produces 'Tail' (id).` Where the two types of a mismatch would otherwise read the same, because they differ only in type-parameter identity (the owner's `T` against a `T` of another function or of the request, as the ID-keyed `typeRef` form can carry), every type parameter in both carries its ID: `requires T (type parameter 01a0…), but the recipe produces T (type parameter 01b2…)`. A branch that always aborts is described as having no value.
- Callbacks, callees and bodies are named as `'name' (id)`, and a callback that does not fit its role is shown with its declared signature and, where the role fixes it, the concrete shape it needs: `ListFold folder 'countStep' (id) must have type (accumulator, item) -> accumulator, here (Int64, String) -> Int64; the folder has type (line: String, acc: Int64) -> Int64.` or `ListSortBy less-than callback 'cmpLen' (id) must return Bool; it has type (a: String, b: String) -> Int64.` Call and capability arguments are named by parameter display name and ID.
- The message ends with the location of the node whose own check failed, as a path of request property names from the recipe root followed by that node's kind: `At body.whenFalse (stringLength).`, `At body.items[1] (stringLength).`, `At replacement.arms[0].body.noneBody (patternReference).` The root is `body` for function creation and `replacement` for body and targeted replacement; list elements are indexed in request order, and the recipe of a keyed entry (`arguments`, `fields`, `context`) is its `.value`, of a `MatchUnion` arm its `.body`. The path is the same for exact-ID and name-resolved (`…ByName`) requests, because both use the same property names. A parameter-migration rewrite, which is checked through an internal creation request, is not located.

## Effect requirements

Every call and invocation is checked against the same enclosing target function's existing `RequiredEffects`. Recursive preflight visits receivers, arguments, scalar/structured-value children, match inputs, and every branch.

Equivalently, the union of all effect requirements in the recipe must be a subset of the target's declared effects:

```text
DirectCall requirements = child requirements + callee.RequiredEffects
CapabilityInvoke requirements = receiver/argument requirements + operation.EffectId
Conditional / Match requirements = condition/input requirements + all branch requirements
BoolAnd / BoolOr requirements = left requirements + right requirements
```

Here `+` means set union, not execution order. Per-occurrence checks during recursive type/scope validation enforce this condition without a separate public effect-inference result.

A branch is checked even when a literal condition would not select it at runtime. This static requirement does not make the branch execute: existing control-flow semantics still execute only the selected branch. Simulation, review, and publication construct/validate program state; they do not execute the authored capability operations.

A missing direct-call requirement produces `ExpressionCallMissingEffectRequirement`; a missing capability-operation requirement produces `ExpressionCapabilityMissingEffectRequirement`.

The workflow never adds or removes target effects automatically. A function may retain declared effects unused by its replacement body. Declaration evolution is a separate authority boundary.

## Execution readiness and effect order

`TransactionReadiness.SemanticallyReady` is not a guarantee of execution readiness. The existing execution layer determines order from graph dependencies, not the physical order of `ValueGraph.Nodes`, caller binding-list order, or fresh node IDs. A control node (`Conditional`, `BoolAnd`/`BoolOr`, `Bind`, the matches) depends on every node its branch graphs reference from outside themselves, not only on their result nodes. Validation follows the same edges: a branch node that names its own enclosing control node, even off the branch result, is `GraphNodeDependencyCycle` at publication rather than an unresolved dependency at lowering.

The deterministic materialization order above therefore does not sequence independent effects. `ExecutionIr.lower` retains its existing `AmbiguousEffectOrder` rejection when two effectful nodes in one graph are not ordered by a dependency path. Authoring and semantic publication may succeed while that later execution-readiness check rejects the result.

The [effectful expression ordering decision](../decisions/effectful-expression-ordering.md) selects the explicit scoped `Bind` form described below. This orders its input before its body without ordering independent siblings inside either graph.

For example, an effectful capability-producing call and an independent effectful message-producing argument both precede their consuming invocation, but neither precedes the other. Declaring both effects does not resolve that ambiguity. In contrast, one effectful receiver or one effectful argument followed by its consuming invocation forms a dependency-ordered chain. Branch-local invocations also keep the existing control-flow boundary.

The authoring surface does not insert hidden sequencing, manufacture data dependencies, or reinterpret stored node order to make an otherwise ambiguous graph executable. The caller must request the intended dependency or Bind scope order explicitly.

## Explicit types

`ListEmpty`, `ListConstruct`, and `OptionNone` carry an explicit contained `TypeRef`; `ResultOk` carries its error type and `ResultErr` carries its success type. These facts cannot always be inferred from a child expression.

The supplied types are construction intent, not raw graph payloads. Before materialization they are checked using current semantic type facts:

- primitive types are valid directly;
- named types must name a current `TypeId`;
- capability types must name a current `CapabilityId`;
- `Option<T>` and `List<T>` require one type argument;
- `Result<T,E>` requires two type arguments;
- nested type arguments are validated recursively.

## Program-defined capability values

`CapabilityConstruct` constructs an existing Capability type by assigning a named, monomorphic pure function to each operation. It is a leaf recipe: no capture or state values are evaluated. Full snapshot validation checks the exact operation set, positional signature and transitive purity both at creation and after later edits. Calls retain their nominal Effect requirements. See [the implementation contract](program-capability-implementations.md) for wire shapes and version boundaries.

## Pure function values

`FunctionReference(functionId)` accepts only a monomorphic named function with a
pure transitive body: no declared effects, capability calls, resource/parallel
execution, or ambient cancellation reads. It neither executes nor captures the
body. `FunctionInvoke(functionValue, arguments)` evaluates the value and ordered
argument dependencies, checks the exact positional signature, and returns the
function result. Materialization visits the value, then arguments, then the
invocation; evaluation of independent effectful inputs still requires `Bind`.
Function type parameters are matched recursively inside function types.

Name-resolved wire recipes use `functionReference` with `function`; exact-ID
recipes use `functionId`. `functionInvoke` takes `functionValue` and `arguments`.
The TypeRef shape is `{ "kind": "function", "parameters": [<TypeRef>],
"resultType": <TypeRef> }`. See [pure function values](pure-function-values.md)
for purity, persistence, dependency and host-boundary contracts.

## Higher-order List callbacks

`ListFold`, `ListMap`, `ListFilter`, `ListSortBy`, `ListMinBy`, `ListFindIndex`, `ListAny`, and `ListAll` reference an existing callback by stable `FunctionId`. They do not embed a lambda or construct a new function.

For a source `List<T>`:

- `ListFold(source, seed, f)` requires a pure binary `f : (S, T) -> S` where `seed : S`; it returns `S`, traverses left to right, and returns the seed for an empty source;
- `ListMap(source, f)` requires a pure unary `f : T -> R` and produces `List<R>`;
- `ListFilter(source, p)` requires a pure unary `p : T -> Bool` and produces `List<T>`;
- `ListSortBy(source, lessThan)` requires a pure binary `lessThan : (T, T) -> Bool` receiving `(left, right)` and produces `List<T>` in stable merge-sort order (see the [language subset](../reference/language-subset.md#immutable-ordered-listt)); wire `listSortBy`, Nyason `list-sort-by`, persisted callback field `lessThanFunctionId`. A callback with another number of unbound parameters reports `PureExpressionCallbackArityMismatch`, one whose parameters do not both match the item type `PureExpressionCallbackInputTypeMismatch`, a non-Bool result `PureExpressionCallbackResultTypeMismatch`, and required effects `PureExpressionCallbackEffectsNotAllowed`; persisted graphs report `CallbackArityMismatch`, `CallbackParameterTypeMismatch`, `PredicateResultTypeMismatch` and `CallbackEffectsNotAllowed`;
- `ListMinBy(source, lessThan)` takes the same callback as `ListSortBy` and produces `Option<T>`: `None` for the empty list, otherwise the first smallest item of a left-to-right scan that replaces the best so far only when `lessThan(item, best)` holds; wire `listMinBy`, Nyason `list-min-by`, persisted callback field `lessThanFunctionId`, with the same diagnostics as `ListSortBy`;
- `ListFindIndex(source, predicate)` (wire `listFindIndex`, Nyason `list-find-index`) takes the same callback as `ListAny` and produces `Option<Int64>`: the zero-based index of the first item for which the predicate is true, or `None`.
- `ListIndexOf(list, item)` (wire `listIndexOf` with the fields `list` and `item`, Nyason `list-index-of`) takes no callback: it produces the index of the first item equal to `item` as `ValueEqual` compares, or `None`. The item must have the list's item type (`ListIndexOfItemTypeMismatch`), and that type must be equatable (`ListIndexOfItemTypeNotEquatable`; a Float64 list uses `ListFindIndex`).
- `ListLastIndexOf(list, item)` (wire `listLastIndexOf` with the fields `list` and `item`, Nyason `list-last-index-of`) is `ListIndexOf` scanning from the end: the index of the last equal item, or `None`, with the same rules and the diagnostics `ListLastIndexOfItemTypeMismatch` and `ListLastIndexOfItemTypeNotEquatable`.
- `ListAny(source, p)` requires a pure unary `p : T -> Bool` and produces `Bool`;
- `ListAll(source, p)` requires a pure unary `p : T -> Bool` and produces `Bool`.

**Context arguments.** Each of these cases takes an optional `context: [(ParameterId, recipe)]` (`[]` when absent). The listed parameters of the callback are bound by stable identity to values evaluated once, in the callback's parameter order, before the traversal; the arities above then apply to the callback's **unbound** parameters, taken in declaration order. For example, `ListFilter(items, above, [threshold, limit])` calls `above(threshold = limit, item = x)` for each item. Context parameters must exist on the callback, occur at most once, and match the value's type exactly. This is how one callback serves several runtime contexts without duplicating its body, and without capturing closures. Context can also carry pure function values. The persisted node stores the context as `ArgumentBinding`s whose value nodes are ordinary dependencies. JSON and Nyason codecs and the IR serializers write `context` only when it is non-empty, so context-free graphs keep their exact bytes and fingerprints, and an empty `context` array is rejected on read. Wire recipes accept `"context": [{ "parameterId", "value" }]`, and name-resolved recipes accept `"context": [{ "parameter": "<callback parameter name>", "value" }]`. The C# backend emits a lambda that passes context and iteration values by parameter name.

Effectful callbacks remain rejected by these pure traversals, even when the target declares their effects.

**Generic functions.** `TypeRef.TypeParameter(id, displayName)` names a function type parameter. A function's type parameters are exactly those occurring in its parameter types (first-occurrence order). Its result type, pattern bindings and body type references may use only those (`TypeParameterNotDeclared`). Each belongs to one function (`TypeParameterSharedAcrossFunctions`), keeps one display name, and never appears in a Product/Union field or capability signature (`TypeParameterOutsideFunction`); declared types stay monomorphic. Identities are system-owned: function creation replaces every type parameter in the request (wire: `{ "kind": "typeParameter", "name": "T" }`; the same name is the same parameter) with a fresh `TypeParameterId`, keeping the name. Existing parameters are referenced as `{ "kind": "typeParameter", "typeParameterId", "displayName" }`. At a `DirectCall` to another function, the callee's type arguments come from **matching** each parameter type against its argument's type; a call to the owner itself is a self call and takes the owner's exact parameter types ([SelfCall](#selfcall)). For List callbacks, `ListEffectFold` and `UseResource` bodies, they come from matching context, item, accumulator and resource types. This is deterministic structural matching, not general inference, and a conflict is a type mismatch. Results are the callee's result type under that substitution. Inside a generic body, a type parameter is opaque: only operations generic in it (List construction and traversal, Option/Result wrapping and matching, calls, passing values through) typecheck. There are no constraints or type classes. `runPure` rejects a generic entry point (`RunGenericEntryUnsupported`); run a concrete function that calls it.

**Host exception recovery.** `ExceptionCatch` explicitly invokes a handler with a captured run-local exception; `ExceptionMessage` reads it, pure `ExceptionIsCancellation` classifies the original cancellation exception, and `ExceptionRethrow` preserves its dispatch information. Typed Err/Abort bypass the handler. Body/handler contexts, Result signatures, ownership and persistence rules are specified in [the exception boundary contract](exception-boundary.md).

**Structured concurrency.** `ParallelAll(source, workerFunctionId, context)` runs the worker once per item of `source : List<T>`, concurrently. The worker has exactly one parameter not bound by context (the item) and returns `Result<U, E>`; its effects must be declared by the enclosing function, and capabilities arrive as context. The node always waits for **every** worker before it completes, so no task outlives it. Workers are scheduled without a source-index start order; simultaneous execution and a particular degree of parallelism are not guaranteed. When a worker returns `Err` or raises a host exception, cancellation is requested for the others. Cancellation is cooperative: `CancellationRequested : Bool` reads whether the current worker's cancellation was requested (`false` outside a worker), and external implementations observe the same ambient token. Result: a host exception is rethrown (the lowest-index one) after all workers finish. Otherwise the lowest-index `Err` is returned, independent of timing; otherwise `Ok` of all values in source order. An empty source yields `Ok []`. `CancellationRequested` is effectful for ordering. Combine with `UseResource` so that cleanup runs after every worker has stopped. Shared capabilities retain one provider/state; worker calls require an explicitly concurrent host binding. The [capability-sharing contract](parallel-capability-sharing.md) defines ordering, visibility, conservative admission checks, and the common runner protocol.

**Cancellation shielding.** `CancellationShield(bodyFunctionId, context)` runs a `Unit -> Result<T, E>` callback with ambient cancellation masked and restores the caller token on every exit. Context evaluates before masking. The caller explicitly handles pending cancellation afterward; existing scopes retain captured tokens. See the [contract](cancellation-shield.md) and [offline finish/rollback probe](../../examples/cleanup-shield/README.md).

**Task scopes.** For control that `ParallelAll` cannot express (start members one at a time, wait for any of them, stop one), a task scope owns individually started tasks. The [concurrency-control decision](../decisions/concurrency-control.md) records why; this is its phases 1 and 2 (time limits).

- `TaskScope(bodyFunctionId, context) : Result<T, E>` calls the body with a new value of builtin type `TaskScope` through its one parameter not bound by context (`TaskScopeBodyMismatch`); the body returns `Result<T, E>` (`TaskScopeResultMismatch`), which is the node's result. When the body exits, by `Ok`, `Err` or a host exception, cancellation is requested for every unfinished task of the scope and the node waits for all of them. A host exception is then rethrown: the body's own, otherwise that of the earliest-started task that raised one.
- `TaskStart(scope, workerFunctionId, context, item) : Task<Result<U, E>>` starts the worker with `item` as a new task of `scope : TaskScope` (`TaskStartScopeTypeMismatch`) and returns its handle at once. The worker has the `ParallelAll` worker shape and diagnostics: one parameter not bound by context, of the item type, returning `Result<U, E>`, with its effects declared by the enclosing function. A task's `Err` does not cancel other tasks.
- `TaskWait(tasks, mode, limit) : List<Option<T>>` takes `tasks : List<Task<T>>` (`TaskWaitInputTypeMismatch`), mode `Any` or `All`, and an optional `limit : Option<Int64>` of milliseconds (`TaskWaitLimitTypeMismatch`). An absent limit operand means the same as `None`: no limit. A negative limit counts as 0. It returns once at least one (`Any`) or every (`All`) listed task has finished, or once the limit has elapsed on a monotonic clock, whichever comes first, with `Some result` for each finished task and `None` for each running one, in input order. Tasks keep running after it returns, including when the limit elapsed; a per-task time limit is a limited wait followed by `TaskCancel` for the tasks still running. `Any` or `All` on `[]` returns `[]` at once. If a task it would report as finished raised a host exception, `TaskWait` rethrows it.
- `TaskCancel(task) : Unit` requests cooperative cancellation of `task : Task<T>` (`TaskCancelInputTypeMismatch`) and returns without waiting. It is idempotent and a no-op for a finished task. The worker sees `CancellationRequested` and returns its own `Result`, which `TaskWait` reports; there is no separate cancelled state.

`TaskWait` does not end early when its waiting caller is cancelled. Its blocking condition remains task completion or the supplied limit, and it never cancels the listed handles. An unbounded `All` wait still requires every handle to settle, including when one has faulted. For an independently cancellable caller, use the [generic task observer composition](../../examples/task-wait-observer/README.md): an owner-scope monitor publishes actual ordered task snapshots to a OneShot, the caller waits on that cell and checks CancellationRequested, and the owner supervises and joins the monitor. This preserves the direct wait and scope drainage contracts.

Cancellation flows only from parent to child: a task's token is linked to its scope, and the scope's to the token of whoever evaluates the `TaskScope` node, so a scope inside a `ParallelAll` worker or another task inherits that cancellation. `CancellationRequested` in a scope body reads the body's inherited token. A handle is an ordinary value and may leave its scope; once the scope has completed, the handle is finished, `TaskWait` reports it at once and `TaskCancel` does nothing. `TaskStart` on a completed scope runs the worker at once, already cancelled, so no task outlives its scope. All four constructs are effectful for execution ordering; order them by data dependencies or `Bind`. A helper function that uses them but declares no effect is, like one that reads `CancellationRequested`, treated as pure by the ordering check, so its calls are not checked for order.

`Task<T>` (one type argument) and `TaskScope` (none) are builtin generics, written `{"kind":"builtinGeneric","generic":"task"|"taskScope"}` on the wire. They may appear in function parameter and result types and inside `List`, `Option` and `Result`. A declared Product or Union field may hold `Task<T>`, as in a `Member { name: String, task: Task<Result<String, String>> }` Product, but not `TaskScope`, which is lexical (`TaskTypeInDeclaredType`). A type is task-bearing if it mentions either, or reaches a declared type with a task-bearing field through any nesting, including recursive types. Task-bearing types may not appear in capability operation parameters, results or failure types (`TaskTypeAtCapabilityBoundary`), and they are not `runPure` values, so an entry function cannot take or return one (`RunTaskTypeUnsupported`) ([decision](../decisions/task-fields-list-indexing-and-map.md)). Operations of one capability value may be called concurrently by different tasks; within a task, calls follow its program order, and a task's calls happen before any `TaskWait` that reports it finished and before its scope completes.

`OneShot<T>` is an opaque cell owned by an existing TaskScope. `OneShotCreate(scope, valueType)` returns an empty cell; `OneShotComplete(cell, value)` returns Bool, accepting the first exact-T value while the owner is open; `OneShotWait(cell, limit?)` returns Option<T> without consuming the value. Wire kinds are `oneShotCreate` (scope, valueType), `oneShotComplete` (cell, value), and `oneShotWait` (cell, optional limit of Option<Int64>). The type uses builtin generic `oneShot` with one type argument. All three nodes are intrinsically effectful. Closed unresolved cells, timeouts and individual waiter cancellation yield None; completed values remain readable after closure and take priority at final observation. Check CancellationRequested explicitly when cancellation must override a value or prevent saving. Runtime-bearing boundary restrictions also include OneShot; live cells cannot be serialized or forged through runPure. See the [full contract](scoped-one-shot.md) for closure, races and helper-call effect rules.


**Scoped resources.** `UseResource(acquire, releaseCapability, releaseOperationId, bodyFunctionId, context)` has these parts:
- `acquire : Result<R, E>` is an ordinary expression, usually a capability invocation.
- The body function has exactly one parameter not bound by `context`, of type `R`, and returns `Result<T, E>` with the same `E`.
- The release operation belongs to `releaseCapability`'s capability, takes exactly `R`, and returns `Unit` or `Result<Unit, E>`.
- The body's and release operation's effects must be declared by the enclosing function.

The node yields `Result<T, E>`. If acquisition is `Err`, it is returned; the body and release do not run. Otherwise the body runs with the resource, and release **always** runs afterwards, on the body's Ok and Err exits alike and also before a host exception from the body propagates. A release failure during host-exception unwinding never replaces the original exception. Failure combination: a body `Err` wins; a release `Err` is the result only when the body succeeded. Values stay managed (Q-012 is unchanged): the resource is an ordinary value such as a handle string or Product, and nothing prevents it from being copied into the result. The construct guarantees release, not uniqueness. The guarantee is tied to the `acquire` node, and two structural rules keep it: the `acquire` node must be owned by the same graph as the `UseResource` node (`UseResourceAcquireOutOfGraph`), so an acquisition never happens in an enclosing graph while the branch that would release it is not taken; and the `UseResource` node must be the acquisition's only consumer (`UseResourceAcquireShared`), so one acquisition is released exactly once and no other node can order an effect after it. Together with effect ordering this leaves no observable node between acquisition and release scope: an effectful or aborting node ordered after the `acquire` node would have to depend on it, which the second rule forbids, and an unordered one is `AmbiguousEffectOrder`. The acquisition effect must therefore be the `acquire` node's own, such as a capability invocation returning `Result<R, E>`; an effect the `acquire` node merely depends on (an invocation wrapped in `ResultOk`, say) happens before the guarantee begins. Diagnostics are `UseResourceAcquireTypeMismatch`, `UseResourceBodyMismatch`, `UseResourceResultMismatch`, `UseResourceReleaseReceiverMismatch`, `UseResourceReleaseSignatureMismatch`, `UseResourceAcquireOutOfGraph` and `UseResourceAcquireShared`, plus the ordinary effect and reference errors. Wire recipes use `{ "kind": "useResource", "acquire", "releaseCapability", "releaseOperationId", "bodyFunctionId", "context"? }`; name-resolved recipes use `capability`, `releaseOperation` and `body` names.

**Effectful iteration.** `ListEffectFold(source, seed, folderFunctionId, context)` is the separate construct for effectful traversal. The folder takes `(accumulator, item)` over its parameters not bound by context, may use effects, and every effect it requires must already be declared by the enclosing function (`ExpressionCallMissingEffectRequirement` / `MissingEffectRequirement`). Capabilities reach it as context arguments. It runs the folder exactly once per item, left to right, in the enclosing effect order. It has two modes, chosen by types: a folder returning the seed type `S` yields `S`; a folder returning `Result<S, E>` for seed `S` yields `Result<S, E>`, stops at the first `Err`, returns it, and calls the folder (and so performs its effects) for no later item. An empty source yields the seed (or `Ok seed`). The node is effectful for execution effect-ordering exactly when its folder declares effects.

**Folds that stop at the first Err.** The folder of `ListEffectFold` may also be pure. With a pure folder returning `Result<S, E>` for seed `S`, the node is the short-circuiting pure fold: it needs no declared effect, the enclosing function stays pure (it can be run with `runPure` and used as a pure callback), and it yields `Result<S, E>` without a `Result` accumulator in the seed or a pass-through `Err` arm in the folder. It calls the folder for no item after the first `Err`, and the generated loop ends there without visiting the remaining items. `ListFold` keeps requiring a folder that returns the seed type; a folder returning `Result<S, E>` for seed `S` is rejected with a message that names `ListEffectFold` ([decision](../decisions/result-fold-short-circuit.md)). This does not prohibit an effectful source expression; its effects are checked before applying the existing pure-callback traversal contract.

## Map

`MapEmpty(keyType, valueType)` (wire `mapEmpty` with the fields `keyType` and `valueType`) produces `Map<K, V>`; a key type that is not equatable reports `MapKeyTypeNotEquatable`. `MapInsert` (wire `mapInsert`: `map`, `key`, `value`), `MapGet`, `MapContainsKey` and `MapRemove` (wire `mapGet`, `mapContainsKey`, `mapRemove`: `map`, `key`) take a map and a key of its key type (and, for insert, a value of its value type). `MapSize`, `MapKeys` and `MapValues` (wire `mapSize`, `mapKeys`, `mapValues`) take the map in the field `value`. `MapUnion` (wire `mapUnion`: `left`, `right`; Nyason `map-union`) takes two maps of the same key and value types; a persisted graph whose right map differs reports `MapInputTypeMismatch`. Nyason uses the kebab-case tags (`map-empty`, `map-insert`, ...). A map operand of another type, or a key or value of the wrong type, reports `PureExpressionOperandTypeMismatch`. A persisted graph reports `MapInputTypeMismatch`, `MapKeyTypeMismatch` or `MapValueTypeMismatch`. A `Map` type anywhere reports `MapKeyTypeNotEquatable` when its key type is not equatable. `MapFold(map, seed, folderFunctionId, context)` (wire `mapFold`: `map`, `seed`, `function` or `functionId`, `context`) takes a pure folder with three parameters not bound by context, `(S, K, V) -> S`, and yields `S`. `MapFromList(source, keyOfFunctionId, context)` (wire `mapFromList`: `source`, `function` or `functionId`, `context`) takes a pure `T -> K` callback over a `List<T>` and yields `Map<K, T>`; `K` must be equatable (`MapKeyTypeNotEquatable`). `MapFilter(map, keepFunctionId, context)` (wire `mapFilter`: `map`, `function` or `functionId`, `context`) takes a pure `(K, V) -> Bool` callback with two parameters not bound by context and yields the same `Map<K, V>`. `MapMapValues(map, mapperFunctionId, context)` (wire `mapMapValues`: `map`, `function` or `functionId`, `context`; Nyason `map-map-values`) takes a pure `(K, V) -> U` callback with two parameters not bound by context and yields `Map<K, U>`. `MapGetOrDefault(map, key, default)` (wire `mapGetOrDefault`: `map`, `key`, `default`; Nyason `map-get-or-default`) takes a key of the key type and a default of the value type and yields `V`; its operands carry the `Source`, `Key` and `Value` dependency roles. Callback problems report the same `PureExpressionCallback*` codes as the List callbacks. Semantics are in the [language subset](../reference/language-subset.md#immutable-insertion-ordered-mapk-v).

`runPure` takes and returns a map as `{"kind": "map", "keyType", "valueType", "entries": [{"key", "value"}]}` in iteration order; an input that names a key twice is a `ProtocolError`, and a map holds at most 4,096 entries.

## Set

`SetEmpty(itemType)` (wire `setEmpty` with the field `itemType`) produces `Set<T>`. `SetInsert`, `SetContains` and `SetRemove` (wire `setInsert`, `setContains`, `setRemove`: `set`, `item`) take a set and an item of its item type. `SetFromList`, `SetSize` and `SetToList` (wire `setFromList`, `setSize`, `setToList`) take their operand in the field `value`. `SetUnion`, `SetIntersection`, `SetDifference` and `SetIsSubset` (wire `setUnion`, `setIntersection`, `setDifference`, `setIsSubset`: `left`, `right`) take two sets of the same type; `SetIsSubset` gives `Bool`. Nyason uses the kebab-case tags. An item type that is not equatable reports `SetItemTypeNotEquatable`; a mistyped operand reports `PureExpressionOperandTypeMismatch`, and a persisted graph reports `SetInputTypeMismatch` or `SetItemTypeMismatch`. `runPure` takes and returns `{"kind": "set", "itemType", "items": [...]}` in iteration order, with each item once. Semantics are in the [language subset](../reference/language-subset.md#immutable-insertion-ordered-sett).

## Structured declaration bindings

`ProductConstruct` and `UnionConstruct` accept field values as `(FieldId, ExpressionRecipe)` pairs. They deliberately do not expose persistent `FieldValueBinding`, because that type contains production `GraphNodeId` references.

Preflight requires no duplicate fields, the exact declaration field set, and exact child value types. Caller binding order is ignored after validation. Materialization follows declaration field order, allocates all child node IDs system-side, and constructs the ordinary persistent field bindings.

`FieldAccess` references an existing stable `FieldId`. Only fields owned by product types are accepted, and the source recipe must produce the exact owning `TypeRef.Named` type.

`ProductUpdate(typeId, source, fieldBindings)` is a copy of the source product with the bound fields replaced and every other field kept, like Python's `dataclasses.replace`. Preflight rejects an empty binding list (`PureExpressionEmptyProductUpdate`), a field the type does not declare (`PureExpressionFieldBindingMismatch`), a duplicate field, a wrong value type, and a source that does not produce exactly `TypeRef.Named typeId` (`PureExpressionProductUpdateSourceMismatch`). The source materializes first, then the bound values in declaration field order. Field values may read the source; the source value itself is unchanged. The [decision](../decisions/product-update.md) records why the node exists.

## Option and Result construction

`OptionSome(value)` derives `Option<T>` from the value recipe. `OptionNone(T)` carries explicit `T`.

`ResultOk(E, value)` derives the success type from `value` and produces `Result<T,E>`. `ResultErr(T, error)` derives the error type from `error` and produces `Result<T,E>`.

These cases add no implicit conversion and no special failure semantics; they materialize the existing semantic constructors.

## StringLessThan

`StringLessThan(left, right)` (wire `stringLessThan`, Nyason `string-less-than`) takes two Strings and produces Bool: whether `left` sorts strictly before `right` in ordinal code-point order, as defined in the [language subset](../reference/language-subset.md). An operand of another type reports `PureExpressionOperandTypeMismatch`, and `StringIntrinsicTypeMismatch` in a persisted graph. There is no three-way `StringCompare`: sort callbacks and conditions need a Bool, and `StringEqual` plus `StringLessThan` express every other comparison.

## StringCodePoints

`StringCodePoints(text)` (wire `stringCodePoints`, Nyason `string-code-points`) takes a String and produces `List<String>`: each code point as a one-code-point String, in order, counted as `StringLength` counts them (an unpaired surrogate is its own item), as defined in the [language subset](../reference/language-subset.md). It runs in linear time and never fails. An operand of another type reports `PureExpressionOperandTypeMismatch`, and `StringIntrinsicTypeMismatch` in a persisted graph. It is the way to iterate text: `ListFold(StringCodePoints(text), seed, folder)` replaces a self-recursive scan that slices one character per call.

## String search and split

`StringIndexOf(value, substring, from)` (wire `stringIndexOf` with `value`, `substring`, `from`; Nyason `string-index-of`) and `StringLastIndexOf(value, substring)` (`stringLastIndexOf`, `string-last-index-of`) produce `Option<Int64>` code-point positions. `StringStartsWith(value, prefix)` (`stringStartsWith`, `string-starts-with`) and `StringEndsWith(value, suffix)` (`stringEndsWith`, `string-ends-with`) produce Bool. `StringSplit(value, separator)` (`stringSplit`, `string-split`) produces `List<String>`. Every text operand is a String and `from` is an Int64; another type reports `PureExpressionOperandTypeMismatch`, and `StringIntrinsicTypeMismatch` in a persisted graph. The semantics (ordinal, code-point positions, `from` clamped like `StringSlice`, an empty separator giving `[value]`, never failing) are defined in the [language subset](../reference/language-subset.md) and the [decision](../decisions/string-search-and-affixes.md).

## StringSpan and StringSpanBack

`StringSpan(value, set, from)` (wire `stringSpan` with `value`, `set`, `from`; Nyason `string-span`) and `StringSpanBack(value, set, end)` (wire `stringSpanBack` with `value`, `set`, `end`; Nyason `string-span-back`) take two Strings and an Int64 and produce an Int64 code-point position: where a run of `set` members that starts at the clamped `from` ends, and where a run that ends at the clamped `end` starts. They never fail; the semantics are in the [language subset](../reference/language-subset.md) and the [decision](../decisions/string-spans.md). A mistyped operand reports `PureExpressionOperandTypeMismatch` with its path (`body.value`, `body.set`, `body.from`, `body.end`); a persisted graph reports `StringIntrinsicTypeMismatch`.

## StringSplitLines

`StringSplitLines(value, breaks)` (wire `stringSplitLines` with `value`, `breaks`; Nyason `string-split-lines`) takes two Strings and produces `List<String>`: the lines of `value` split at any code point of `breaks`, with `"\r\n"` as one break when both are members, `""` giving `[]` and no trailing empty line after a final break. It never fails; the semantics are in the [language subset](../reference/language-subset.md) and the [decision](../decisions/string-split-lines.md). A mistyped operand reports `PureExpressionOperandTypeMismatch` with its path (`body.value`, `body.breaks`); a persisted graph reports `StringIntrinsicTypeMismatch`.

## StringReplace and the trims

`StringReplace(value, pattern, replacement)` (wire `stringReplace` with `value`, `pattern`, `replacement`; Nyason `string-replace`) takes three Strings and produces a String: every non-overlapping ordinal occurrence of `pattern`, scanning left to right, replaced by `replacement`, as Python `str.replace(old, new)`, with matches on code-point boundaries only and an empty `pattern` giving `value` unchanged. `StringTrim(text)`, `StringTrimStart(text)` and `StringTrimEnd(text)` (wire `stringTrim`, `stringTrimStart`, `stringTrimEnd` with `value`; Nyason `string-trim`, `string-trim-start`, `string-trim-end`) take a String and produce a String without the Unicode White_Space code points at both ends, the start or the end; unlike Python `strip()`, U+001C–U+001F are kept. None of them fails ([decision](../decisions/string-replace-and-trim.md)). A mistyped operand reports `PureExpressionOperandTypeMismatch`; a persisted node over a non-String operand is a `StringIntrinsicTypeMismatch` validation error.

## StringToLower, StringToUpper and StringUtf8ByteCount

`StringToLower(text)` (wire `stringToLower`, Nyason `string-to-lower`) takes a String and produces a String: each code point replaced by its culture-invariant Unicode simple lowercase mapping, unpaired surrogates kept. `StringToUpper(text)` (wire `stringToUpper`, Nyason `string-to-upper`) is the uppercase counterpart, with `ß` mapped to `ẞ` ([decision](../decisions/string-uppercase.md)). `StringUtf8ByteCount(text)` (wire `stringUtf8ByteCount`, Nyason `string-utf8-byte-count`) takes a String and produces the Int64 UTF-8 byte length, an unpaired surrogate counting 3 bytes. All three use the wire field `value`, run in linear time and never fail; the semantics and the Python differences are in the [language subset](../reference/language-subset.md) and the [decision](../decisions/string-lowercase-and-utf8-length.md). An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `StringIntrinsicTypeMismatch`.

## StringDecodeUtf8Lossy

`StringDecodeUtf8Lossy(bytes)` (wire `stringDecodeUtf8Lossy`, Nyason `string-decode-utf8-lossy`) takes a String of one byte per code point and produces the String that Python's `bytes.decode("utf-8", "replace")` gives for those bytes. It uses the wire field `value`, runs in linear time and never fails; the semantics are in the [language subset](../reference/language-subset.md) and the [decision](../decisions/utf8-decode.md). An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `StringIntrinsicTypeMismatch`.

## StringFromCodePoint and StringToCodePoint

`StringFromCodePoint(code)` (wire `stringFromCodePoint`, Nyason `string-from-code-point`) takes an Int64 and produces `Option<String>`: the one-code-point String for a code in 0..0x10FFFF, surrogate codes included as an unpaired surrogate code unit, and `None` otherwise. `StringToCodePoint(text)` (wire `stringToCodePoint`, Nyason `string-to-code-point`) takes a String and produces `Option<Int64>`: its code point when it is exactly one code point, and `None` otherwise. Both use the wire field `value` and never fail; the semantics are in the [language subset](../reference/language-subset.md) and the [decision](../decisions/string-code-point-conversion.md). An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `StringIntrinsicTypeMismatch`.

## StringJoin and ListSlice

`StringJoin(list, separator)` (wire `stringJoin` with `list`, `separator`; Nyason `string-join`) takes exactly a `List<String>` and a String and produces a String, Python `separator.join(list)`. `ListSlice(list, start, end)` (wire `listSlice` with `list`, `start`, `end`; Nyason `list-slice`) is generic like `ListItemAt`: the item type is inferred from `list`, which must be some `List<T>`, and the result is that same `List<T>`; `start` and `end` are Int64 and clamp like `StringSlice` bounds. Both never fail; the semantics are in the [language subset](../reference/language-subset.md) and the [decision](../decisions/string-join-and-list-slice.md). A mistyped operand reports `PureExpressionOperandTypeMismatch` with its path (`body.separator`, `body.end`). A persisted graph reports `StringIntrinsicTypeMismatch` for `StringJoin`, and `ListSliceListTypeMismatch` or `ListSliceBoundTypeMismatch` for `ListSlice`.

## Int64ToFloat64

`Int64ToFloat64(value)` (wire `int64ToFloat64`, Nyason `int64-to-float64`) takes an Int64 and produces the nearest Float64: exact for `|value| <= 2^53`, otherwise rounded to nearest with ties to even, as defined in the [language subset](../reference/language-subset.md#float64). It never fails. An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `Int64IntrinsicTypeMismatch`.

## Float64RoundToInt64 and Float64FloorToInt64

`Float64RoundToInt64(value)` (wire `float64RoundToInt64`, Nyason `float64-round-to-int64`) and `Float64FloorToInt64(value)` (wire `float64FloorToInt64`, Nyason `float64-floor-to-int64`) take a Float64 and produce `Option<Int64>`: the nearest integer, half away from zero, and `math.floor(value)`, `None` for NaN, the infinities and results outside Int64. Both use the wire field `value` and never fail; the semantics are in the [language subset](../reference/language-subset.md#float64) and the [decision](../decisions/float64-to-int64.md). An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `Float64OperandTypeMismatch`.

`Float64Round(value, digits)` (wire `float64Round` with the fields `value` and `digits`, Nyason `float64-round`) takes a Float64 and an Int64 and produces a Float64, rounded to `digits` decimal places half away from zero on the shortest decimal text ([decision](../decisions/rounding-half-away-from-zero.md)). An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `Float64OperandTypeMismatch` for either operand.

## Float64 unary operations

`Float64Sqrt`, `Float64Abs`, `Float64Negate`, `Float64Floor`, `Float64Ceiling` and `Float64Truncate` (wire `float64Sqrt`, `float64Abs`, `float64Negate`, `float64Floor`, `float64Ceiling`, `float64Truncate`; Nyason `float64-sqrt`, `float64-abs`, `float64-negate`, `float64-floor`, `float64-ceiling`, `float64-truncate`) take a Float64 in the wire field `value` and produce a Float64. They are the IEEE 754 operations described in the [language subset](../reference/language-subset.md#float64) and never fail. An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `Float64OperandTypeMismatch`.

`Float64Min(left, right)` and `Float64Max(left, right)` (wire `float64Min`, `float64Max` with the fields `left` and `right`; Nyason `float64-min`, `float64-max`) take two Float64 values and produce a Float64, the IEEE 754-2019 `minimum` and `maximum` ([decision](../decisions/int64-bitwise-and-float64-min-max.md)). An operand of another type reports `PureExpressionOperandTypeMismatch` from the recipe preflight; a persisted graph reports `Float64OperandTypeMismatch`.

## ListReverse

`ListReverse(list)` (wire `listReverse` with the field `value`, Nyason `list-reverse`) takes any `List<T>` and produces the same `List<T>` with its items in reverse order. It never fails. A source that is not a List reports `PureExpressionOperandTypeMismatch` from the recipe preflight; a persisted graph reports `ListReverseInputTypeMismatch`.

## ListLast and ListDropLast

`ListLast(list)` (wire `listLast` with the field `value`, Nyason `list-last`) takes any `List<T>` and produces `Option<T>`: `Some` last item, or `None` for the empty list. `ListDropLast(list)` (wire `listDropLast`, Nyason `list-drop-last`) produces the same `List<T>` without its last item; the empty list stays empty. Neither fails. A source that is not a List reports `PureExpressionOperandTypeMismatch` from the recipe preflight; a persisted graph reports `ListLastInputTypeMismatch` or `ListDropLastInputTypeMismatch`.

## Standard operations with an owner choice

These operations follow the project owner's choices recorded in the [decision](../decisions/standard-operation-choices.md); their meaning is in the [language subset](../reference/language-subset.md).

- `ListRange(start, end)` (wire `listRange` with the fields `start` and `end`, Nyason `list-range`) takes two Int64 values and produces `List<Int64>`. `StringRepeat(text, count)` (wire `stringRepeat` with the fields `text` and `count`, Nyason `string-repeat`) takes a String and an Int64 and produces a String. A result too large to allocate is a host failure (`RunRuntimeFailure` with `System.OutOfMemoryException` under `runPure`); neither returns `Option`.
- `Int64ToHexString` (wire `int64ToHexString`, Nyason `int64-to-hex-string`) takes an Int64 in the field `value` and produces a String; `StringParseInt64Hex` (wire `stringParseInt64Hex`, Nyason `string-parse-int64-hex`) takes a String in the field `value` and produces `Option<Int64>`.
- `Int64Min` and `Int64Max` (wire `int64Min`, `int64Max` with the fields `left` and `right`; Nyason `int64-min`, `int64-max`) take two Int64 values, and `Int64WrappingAbs` (wire `int64WrappingAbs` with the field `value`, Nyason `int64-wrapping-abs`) takes one; all produce an Int64.
- `Int64CheckedAdd`, `Int64CheckedSubtract` and `Int64CheckedMultiply` (wire `int64CheckedAdd`, `int64CheckedSubtract`, `int64CheckedMultiply` with the fields `left` and `right`; Nyason `int64-checked-add`, `int64-checked-subtract`, `int64-checked-multiply`) take two Int64 values and produce `Option<Int64>`.
- `Int64BitAnd`, `Int64BitOr` and `Int64BitXor` (wire `int64BitAnd`, `int64BitOr`, `int64BitXor` with the fields `left` and `right`; Nyason `int64-bit-and`, `int64-bit-or`, `int64-bit-xor`) take two Int64 values, `Int64BitNot` (wire `int64BitNot` with the field `value`, Nyason `int64-bit-not`) takes one, and `Int64ShiftLeft` and `Int64ShiftRight` (wire `int64ShiftLeft`, `int64ShiftRight` with the fields `value` and `count`; Nyason `int64-shift-left`, `int64-shift-right` with the fields `value` and `count`; snapshot JSON `valueNodeId` and `countNodeId`) take an Int64 value and an Int64 count; all produce an Int64 ([decision](../decisions/int64-bitwise-and-float64-min-max.md)).

An operand of the wrong type reports `PureExpressionOperandTypeMismatch` from the recipe preflight. A persisted graph reports `Int64OperandTypeMismatch` for `ListRange`, `Int64Min`, `Int64Max`, the bitwise operations, the shifts and the checked operations, `Int64IntrinsicTypeMismatch` for `Int64ToHexString`, `Int64WrappingAbs` and `Int64BitNot`, and `StringIntrinsicTypeMismatch` for `StringRepeat` and `StringParseInt64Hex`.

## Bits32

The [`Bits32`](../reference/language-subset.md#bits32) operations use these wire kinds (Nyason tags in parentheses): `bits32FromInt64` (`bits32-from-int64`), `bits32ToInt64` (`bits32-to-int64`) and `bits32Not` (`bits32-not`) take `value`; `bits32Xor`, `bits32And`, `bits32Or` and `bits32Add` (`bits32-xor`, `bits32-and`, `bits32-or`, `bits32-add`) take `left` and `right`; `bits32RotateRight`, `bits32ShiftRight` and `bits32ShiftLeft` (`bits32-rotate-right`, `bits32-shift-right`, `bits32-shift-left`) take a Bits32 `value` and an Int64 `amount`. Snapshot JSON writes the shift operands as `valueNodeId` and `amountNodeId`, and review lists the amount with the `Amount` dependency role. The type reference is `{"kind":"primitive","primitive":"bits32"}`. There is no Bits32 literal: a constant is `bits32FromInt64` of an `int64Literal`. All ten are total. An operand of the wrong type, including a Bits32 amount or an Int64 where a Bits32 is expected, reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `Bits32OperandTypeMismatch`.

## Float64ToString and StringParseFloat64


`Float64ToString(value)` (wire `float64ToString`, Nyason `float64-to-string`) takes a Float64 and produces a String, Python `repr(value)`. `StringParseFloat64(text)` (wire `stringParseFloat64`, Nyason `string-parse-float64`) takes a String and produces `Option<Float64>`, Python `float(text)` on the JSON number grammar with leading zeros, correctly rounded, with a signed infinity on overflow. Both use the wire field `value` and never fail; the semantics are in the [language subset](../reference/language-subset.md#float64) and the [decision](../decisions/float64-decimal-text.md). An operand of another type reports `PureExpressionOperandTypeMismatch`; a persisted graph reports `Float64OperandTypeMismatch` for `Float64ToString` and `StringIntrinsicTypeMismatch` for `StringParseFloat64`.

## ValueEqual

`ValueEqual(left, right)` (wire `valueEqual`, Nyason `value-equal`) produces Bool. Preflight infers both operands; a right operand whose type differs from the left one reports `PureExpressionOperandTypeMismatch`, as for other operands, and a shared type that is not equatable reports `ValueEqualTypeNotEquatable` with the path to the first offending part by display names. Equatable types and the comparison are defined in the [language subset](../reference/language-subset.md). Persisted graphs report `ValueEqualOperandTypeMismatch` and `ValueEqualTypeNotEquatable`. A generic function cannot compare values of its type parameters, because nothing constrains their type arguments to be equatable; it takes an equality callback instead.

## Complete DirectCall

`DirectCall(targetFunctionId, arguments)` references an existing callee by stable `FunctionId`. Arguments are `(ParameterId, ExpressionRecipe)` pairs and deliberately do not expose persistent `ArgumentBinding` values.

The maintained construction surface accepts complete calls:

- the callee must exist;
- every callee required effect must already be declared by the target;
- each callee parameter must be bound exactly once;
- unknown, duplicate, or omitted parameter IDs are rejected;
- every child recipe result type must equal the corresponding declared parameter type; for a generic callee, the type arguments follow from matching ([generic functions](#higher-order-list-callbacks));
- a `DirectCall` whose target is the function that owns the recipe (the replacement target, by its `FunctionId`) is a self call: it takes the owner's exact parameter types, with the [`SelfCall`](#selfcall) rule and wording, so the ID-keyed wire `directCall` and the name-resolved form, which reads such a call as `selfCall`, reject the same calls.

Caller argument-list order is ignored after validation. Materialization follows callee parameter declaration order and creates the `DirectCall` node last.

This surface does **not** create missing-argument obligations. Incomplete calls remain outside this boundary.

## SelfCall

`SelfCall(arguments)` is a complete direct call to the function that owns the recipe. Arguments are `(slot, ExpressionRecipe)` pairs keyed by the owner's zero-based parameter slot, because a function being created has no `FunctionId` or `ParameterId`s yet: both are system-assigned. This lets a recursive function be created in one request instead of creating a placeholder body and replacing it.

- It is valid in function creation, where the owner is the function being created, and in complete-body and targeted replacement, where the owner is the target function.
- Every owner parameter slot must be bound exactly once. Out-of-range, duplicate, and omitted slots are rejected with the `DirectCall` codes `PureExpressionCallArgumentTargetNotFound`, `PureExpressionDuplicateCallArgument`, and `PureExpressionCallArgumentMissing`.
- Each argument type must equal the owner's parameter type at that slot (`PureExpressionCallArgumentTypeMismatch`). A generic owner calls itself at its own type parameters; there is no polymorphic recursion: `nest<T>(value: T, n)` cannot call `nest([value], n - 1)` (`T := List<T>`), and `swap<T, U>(a: T, b: U)` cannot call `swap(b, a)`. The rule holds for every self call, not only the `SelfCall` recipe: a `DirectCall` to the owner's own `FunctionId` is typed the same way, and a persisted graph whose self call binds other types is invalid (`PolymorphicSelfCallUnsupported`). Execution IR lowering also admits only an exact-type self call as a tail call (below), because the jump reuses the generic method's parameter locals, where `List<T>` cannot be assigned to `T`. Polymorphic recursion *through another function* (`ping<T>` calls `pong<T>` at `List<T>`, which calls `ping` back) is an ordinary generic call, typed by matching, compiled as a call and bounded by the stack like any mutual recursion.
- The result type is the owner's declared result type. A self call needs the owner's own `RequiredEffects`, which the owner already declares, so it adds no effect check.

`SelfCall` adds no semantic node kind. It materializes to an ordinary `DirectCall(ownerFunctionId, bindings)`: creation allocates the new `FunctionId` and `ParameterId`s before materializing the body. Bindings follow the owner's parameter order, so persistence and typed IR are unchanged; execution IR lowers a self call in tail position to a jump (below). Parameter- and result-type migration accept `SelfCall` in the target replacement, which stays a call to the migrated target, and in a caller replacement, which becomes a call to that unchanged caller; a target replacement's `DirectCall` to the target's own FunctionId is treated as a `SelfCall`, so it is typed against the new signature rather than the pre-migration function. Because it is an ordinary self-reference, it never blocks deleting its owner ([`deleteFunction`](agent-authoring-session.md#operations)).

In the name-resolved form, a self-call `directCall` may set `"keepUnlisted": true` ([decision](../decisions/self-call-keep-unlisted.md)). Every owner parameter that its `arguments` do not name is then bound to that same parameter, as if the call listed `{"parameter": p, "value": {"kind": "parameter", "name": p}}`, so a loop that threads its state through its parameters lists only what changes. The expansion happens while the recipe is read, so the `SelfCall`, its preflight, the materialized `DirectCall` and the node count are those of the full call. `keepUnlisted` on a call to any other function is rejected; the ID-keyed `selfCall` recipe has no such field and lists every slot.

Out of scope: a `FunctionReference` to the function being created, for higher-order use, and mutual recursion. Both still need an existing function, so the placeholder-then-replace route remains for them; a name-resolved callback or function reference naming the function being created is rejected with a message naming that route ([session spec](agent-authoring-session.md#self-calls)).

A self call in **tail position** never grows the stack: it runs as a jump back to the start of the function with its arguments as the new parameter values, so a self-recursive loop can run for millions of turns. This is a language guarantee, not an optimisation ([decision](../decisions/tail-self-calls.md)). Tail position is defined on the graph, starting from the function body's result: the body's result is in tail position; so is the body of a `Bind` in tail position, both branches of a `Conditional` in tail position, every arm and `otherwise` of a `MatchOption`, `MatchResult` or `MatchUnion` in tail position, and the `right` operand of a `BoolAnd` or `BoolOr` in tail position. Nothing else is: a self call whose value is wrapped (`ResultOk`, `OptionSome`, a Product field) or used as an operand or match input is an ordinary call. A call inside a callback (`ListFold`, `UseResource`, `TaskScope`, `ParallelAll`, `TaskStart`, …) is in the callback's own function, so it is a tail call there only when it calls that function. The rule applies to effectful functions too. Write a loop with its loop variables as parameters and the self call as the last step of each continuing branch, for example `sumTo(n, acc) = if n <= 0 then acc else sumTo(n - 1, acc + n)`.

Any other self call, and mutual recursion, is a real call at run time, so its depth is limited by the runtime stack: several thousand nested calls for a small function, fewer for one with many parameters or locals, after which `runPure` reports `RunRuntimeFailure` `System.StackOverflowException` with a "recursion was too deep" message. Keep such recursion for depth that stays small, such as tree depth. Per-item iteration can be either a tail self call or `ListFold` over a list (`StringCodePoints(text)` for text). Positional String operations reach a position in constant or near-constant time after the first call on a String, so a loop that steps a position over one String and slices or searches again is linear; a loop that re-slices its remaining text makes a new String each step and is still O(n²). See the [language subset](../reference/language-subset.md).

## Complete CapabilityInvoke

```text
CapabilityInvoke(
  capabilityValue: ExpressionRecipe,
  operationId: CapabilityOperationId,
  arguments: (CapabilityParameterId * ExpressionRecipe) list
)
```

The operation must already exist. The receiver must produce `TypeRef.Capability` for the exact capability owning that operation. An unrelated capability value does not grant access merely because its operation signatures look similar.

Arguments must bind exactly the declared operation parameters, once each, using their stable `CapabilityParameterId`s and exact types. Empty bindings are accepted only for a zero-parameter operation. There are no omitted/default arguments or generated obligations.

The receiver is an ordinary child recipe: it may be a target-owned parameter, a visible lexical pattern slot, or another compatible expression. All receiver and argument effects must already be declared by the target, as must the operation's `EffectId`.

The result is the existing operation's `ResultType`. This authoring surface does not reinterpret `FailureType`, wrap results automatically, or introduce new exception/failure semantics.

Materialization allocates ordinary `CapabilityArgumentBinding`s only after their child graph-node IDs are allocated. It creates the existing `SemanticNodeKind.CapabilityInvoke`; no effect, capability, operation, parameter, or runtime implementation is created or modified.

Preflight diagnostics distinguish unknown operation, receiver-type mismatch, duplicate argument, argument-set mismatch, argument-type mismatch, and missing effect requirement.

## Scoped control flow

### Lexical references

Bind bodies and match arms introduce ephemeral recipe-local lexical frames. `RecipePatternReference.ScopeDepth = 0` addresses the current enclosing frame, `1` addresses the next outer frame, and so on. Conditionals do not introduce a frame.

Frames exist structurally even when they bind no values. In particular, an `Option.None` body receives an empty frame, so an outer slot seen from inside that body moves to the next scope depth rather than collapsing inward.

Slots are derived from the persistent semantic shape:

- union arm: `Field(fieldId)` for every declared variant field;
- Bind body: `Value`;
- `Option.Some`: `Value`;
- `Option.None`: no slots;
- `Result.Ok`: `Value`;
- `Result.Err`: `Error`.

The caller never supplies a production `PatternBindingId`, binding type, or pattern display name. Slot types come from the Bind input type, union declaration, or matched `Option`/`Result` input type.

A reference that does not resolve keeps its code (`PureExpressionPatternScopeDepthInvalid`, `PureExpressionPatternScopeNotFound` or `PureExpressionPatternSlotNotFound`), and its message names the slot and lists the visible frames, innermost first. A field slot is named as `field 'member' (id) of variant 'Stop' of 'Action'` (or `of product 'T'`), and `Value`/`Error` by kind. Each frame is described by what opened it and what it binds, for example `depth 0 = None arm of MatchOption (no bindings); depth 1 = arm 'Stop' of MatchUnion on 'Action' (binds 'member')`; the other openers are `Bind body`, `Some arm of MatchOption`, `Ok arm of MatchResult` and `Err arm of MatchResult`. When the slot is bound at other visible depths, the message says so first (`The slot is found at depth 1.`). The same wording applies to exact-ID and name-resolved (`…ByName`) recipes and to targeted replacement inside an existing nested body. Parameter diagnostics likewise name the parameter as `'name' (id)` with its owning function when it belongs to another function, and list the owner's parameters by slot (`0 = 'action' (id), 1 = 'maybe' (id)`) for an out-of-scope `Parameter`, an out-of-range `ParameterSlot` or `SelfCall` slot.

### Bind

`Bind(value, body)` evaluates `value` once before entering `body`, binds its exact result type to the body's `Value` slot, and returns the body's result. It supports every currently valid value type, including `Unit` and capabilities. An unused binding still evaluates its initializer and preserves its effects. Binding an `Option` or `Result` does not unwrap it or introduce failure handling.

The binding is immutable and non-recursive: it is unavailable in its own initializer and outside its body. Nested bodies may refer to it at the appropriate lexical depth. An initializer may refer to an enclosing binding. The new frame has no `Field` or `Error` slot.

The authoritative node is `SemanticNodeKind.Bind(inputNodeId, ValueMatchArm)`. The system allocates a fresh `PatternBindingId`, derives `PatternBinding.Type` from the input, and uses `DisplayName = None`. Its child `ValueGraph` owns the body nodes. Persistent validation enforces scope, exact binding type, unique identity, and graph acyclicity.

For example, using names only as explanatory pseudocode:

```text
Bind(acquireConsole(),
  Bind(send(Value@0, loadMessage()),
    Bind(send(Value@1, "second"), Value@2)))
```

`Value@depth` denotes `PatternReference { ScopeDepth = depth; Slot = Value }`. This acquires once, loads and sends the first message, sends the second, then returns the acquired console. Discard-only sequencing is `Bind(audit(), next())`; it needs no separate recipe case.

Typed projection retains the input, typed binding, nested graph, and node origins. Execution lowering jumps to a body block with a parameter carrying `ExecutionOrigin.PatternBinding`, then returns the body result through a continuation parameter carrying the Bind node origin. Existing Execution IR block/argument constructs express this order without a synthetic match or new instruction. Lowering still rejects unordered effectful siblings within the body or input's containing graph. A Bind inside an unselected branch does not execute.

### Conditional

`Conditional(condition, whenTrue, whenFalse)` requires a `Bool` condition. Both branches are validated in the same lexical pattern environment and must produce exactly one common type.

The condition materializes in the current graph. Each branch materializes into its own child `ValueGraph`, followed by the parent `Conditional` node.

### BoolNot, BoolAnd and BoolOr

`BoolNot(expr)` requires a `Bool` operand and materializes as one node after it. `BoolAnd(left, right)` and `BoolOr(left, right)` require a `Bool` left operand and a right operand that produces `Bool` or aborts; all three produce `Bool`. The right operand is validated like a `Conditional` branch: in the same lexical pattern environment, without introducing a frame, and against the same target effect declaration.

The left operand materializes in the current graph. The right operand materializes into its own child `ValueGraph`, followed by the parent `BoolAnd`/`BoolOr` node, so its effects stay local to it and it runs only when the left operand does not decide the result (true for `BoolAnd`, false for `BoolOr`). Execution lowering uses the branch shape of the conditional expansion: the block where the left operand decides forwards that value to the merge, and only the other block evaluates the right graph.

### MatchUnion

`MatchUnion(input, arms)` requires an input whose inferred type names an existing union. The caller supplies `(VariantId list, ExpressionRecipe)` arms. An arm names one variant or several that share its body; at most one arm names none, and it is the `otherwise` arm. The named variants must be distinct declared variants. Without `otherwise` they must be exactly the declared set (`PureExpressionMatchArmMismatch`); with it, at least one variant must be left for it (`PureExpressionMatchOtherwiseUnused`). A variant named twice is `PureExpressionMatchArmDuplicate`, and a second `otherwise` is `PureExpressionMatchOtherwiseDuplicate`.

Materialization expands `otherwise` into an explicit list of the variants no other arm names, so the persisted graph names every variant by ID and a variant added to the union later is not absorbed: every match over it reports `NonExhaustiveUnionMatch` until an arm names the new variant ([decision](../decisions/match-union-shared-arms.md)). The expanded list is visible in a `detail: "full"` structured review and in `readFunction`.

Only an explicit arm naming exactly one variant opens a frame with that variant's fields. An arm naming several variants, and the `otherwise` arm even when it covers one variant, opens a frame with no slots. A persisted arm with one variant still binds each of its fields exactly once (system-side, unused under `otherwise`); a persisted arm with several variants binds none (`PatternFieldBindingMismatch` otherwise).

Wire forms: the ID-based recipe takes `arms: [{"variantId", "body"} | {"variantIds": [...], "body"}]` and an optional `otherwise` body; the name-resolved recipe takes `variant` or `variants` (display names) in the same way. Persisted snapshots, Nyason and the graph read keep `variantId` (Nyason `variant`) for a one-variant arm and use `variantIds` (Nyason `variants`, at least two) for a shared arm, so existing graphs and their fingerprints are unchanged.

Caller arm order is not semantic. Persistent arms follow union declaration order of their first variant, and each arm's pattern-field bindings follow field declaration order. Fresh production `PatternBindingId`s are allocated system-side with `DisplayName = None` and declaration-derived types.

All arm bodies must produce the same result type.

### MatchOption

`MatchOption(input, someBody, noneBody)` requires `Option<T>`.

- Some body receives `Value : T`;
- None body receives an empty lexical frame.

Both bodies must produce the same result type. The Some pattern binding is allocated system-side.

### MatchResult

`MatchResult(input, okBody, errBody)` requires `Result<T,E>`.

- Ok body receives `Value : T`;
- Err body receives `Error : E`.

Both bodies must produce the same result type. Both pattern bindings are allocated system-side.

### Outer local values remain out of scope

Branch recipes may reference target-function parameters, existing semantic declarations, and visible lexical pattern slots. They do not gain caller-controlled references to arbitrary outer graph nodes.

If the same computation is needed inside a nested body, bind it in an enclosing scope and refer to the visible lexical slot. Repeating an effectful computation is not equivalent to sharing its result. General local symbols, recursive/mutable bindings, arbitrary DAG sharing, and CSE remain separate design questions.

## Materialization

After complete preflight succeeds, recipe nodes receive fresh `GraphNodeId`s from `StableIdAllocator` and become the corresponding existing `SemanticNodeKind` values.

Bind-body and match-arm slots additionally receive fresh production `PatternBindingId`s. Their persistent `PatternBinding.Type` is derived from validated semantic facts and `DisplayName` is initially `None`.

Branch recipes become nested child `ValueGraph`s. No caller-supplied raw graph, semantic-node, or pattern-binding representation crosses the construction boundary.

Existing declaration references such as callback/callee `FunctionId`, callee `ParameterId`, capability `CapabilityOperationId` / `CapabilityParameterId`, product `TypeId`, union `VariantId`, and `FieldId` remain references; the workflow does not allocate or mutate those declarations.

Bind adds a persisted semantic node role. The Bind-era snapshot JSON schema is **9** and Nyason schema is **`nayamnyam.snapshot.v5`**; these remain the canonical formats for graphs without `CapabilityConstruct` or `Abort`. That new construct uses JSON **10** and Nyason **v6** as defined in [program capability implementations](program-capability-implementations.md). Graphs without Abort retain Typed Semantic IR **7** and Execution IR **6**. Abort uses JSON **11**, Nyason **v7**, Typed Semantic IR **8**, and Execution IR **7**; see [typed function abort](typed-function-abort.md). A `TaskWait` with a `limit` operand uses JSON **12**, Nyason **v8**, Typed Semantic IR **9**, and Execution IR **8**, and a timed snapshot written with an older version is rejected; graphs without a limit keep the versions above. Checked Int64 arithmetic, shared `MatchUnion` arms, `StringToUpper`, `Float64Round`, Map, Set, `ListMinBy`, `ListLast`/`ListDropLast` and the list index searches use JSON **13** and Nyason **v9**; [snapshot schema versions](schema-evolution-policy.md#snapshot-schema-versions) is the complete table. OneShot uses JSON **14**, Nyason **v10** and Typed/Execution IR debug version **10**; graphs without OneShot retain their previous selected versions. Snapshot and semantic fingerprint domains advance to **v6**. Bind itself needs no new execution instruction: existing jumps and block parameters preserve both order and provenance. The legacy `nayamnyam` spelling is retained in schema/domain identifiers.

Snapshot JSON 8 and Nyason v4 are now rejected explicitly, including for graphs without Bind. There is no implicit migration or inferred ordering of older graphs. Older artifacts require the matching older reader or a separately reviewed meaning-preserving migration; no migration is added in this slice. Typed Semantic IR currently has a serializer, not a round-trip reader. Frozen evaluation protocols/corpora are not extended with Bind.

## Review and publication

The workflow reuses the existing structural transaction mechanics:

- full proposed-snapshot validation;
- readiness classification;
- exact base/proposed snapshot and semantic fingerprints;
- base/proposed Function Focus views;
- `FunctionBodyReplacementDiff` with stable node summaries;
- stale-base and receipt-mismatch rejection;
- reviewed publication through `publishValidatedSnapshot`;
- explicit rejection without mutation.

`ResultingChange.AddedNodes` recursively includes branch-local nodes through `ValueGraph.collectNodes`, while `RequestedChange` remains the original construction intent. This preserves the distinction between what the caller asked for and the full materialized result.

Bind summaries label the input dependency `BindInput`; the base/proposed Focus graphs retain the body scope, binding identity/type, and pattern references. Viewer projection shows the input as "evaluate before body" and the nested scope as "body after input". Allocation-order summaries alone are not the execution-order contract.

## Naming and compatibility

The canonical API is `ExpressionRecipe`, `AgentReplaceFunctionBodyWithExpressionRecipe`, `AgentExpressionStructuralChangeReview`, `AgentExpressionStructuralChangeProposal`, and `AgentExpressionStructuralWorkflow`.

The earlier `PureExpressionRecipe` and `AgentPureExpression...` names remain thin F# source aliases and function forwarders for existing clients and evaluation tooling. There is one recipe algebra and one implementation, not separate pure and effectful engines. These historical names do **not** enforce purity; new code should use the canonical names. This is source compatibility, not a promise of binary compatibility for previously compiled clients.

Existing `PureExpression...` diagnostic codes are retained as stable diagnostic identifiers. New capability diagnostics use the `Expression...` prefix. Changing API names does not require renaming historical protocol identifiers or rewriting frozen evaluation evidence. The one exception is the callback codes, which Map, Set and Task nodes share with the List callbacks: they are named for the callback, not the collection (`PureExpressionCallbackNotFound`, `PureExpressionCallbackArityMismatch`, `PureExpressionCallbackInputTypeMismatch`, `PureExpressionCallbackResultTypeMismatch`, `PureExpressionCallbackContextTypeMismatch`, `PureExpressionCallbackEffectsNotAllowed`; in persisted graphs `UnknownCallbackFunction`, `CallbackArityMismatch`, `CallbackParameterTypeMismatch`, `CallbackResultTypeMismatch`, `CallbackContextTypeMismatch`, `CallbackEffectsNotAllowed`, `PredicateResultTypeMismatch` and `HigherOrderSourceTypeMismatch`), and no code for a Map, Set or Task node starts with `List`.

The earlier `Float64ExpressionRecipe` / `AgentStructuralWorkflow` surface remains available as a narrower compatibility surface backed by the same structural interaction model.

The specialized `AgentListMapStructuralWorkflow` also remains available for compatibility with the M2 ListMap surface and its frozen evidence. Removing or wrapping that specialized API is a separate compatibility decision.

## Deliberately outside the current surface

The maintained recipe does not author:

- incomplete direct calls or missing-argument obligation creation;
- calls/invocations requiring effects not already declared by the target;
- automatic addition or removal of target effect requirements;
- effect, capability, operation, or signature declarations;
- effectful higher-order List callbacks;
- nested-expression replacement inside an existing body;
- raw `SemanticNode`, `SemanticNodeKind`, `PatternBinding`, `PatternBindingId`, `FieldValueBinding`, `ArgumentBinding`, `CapabilityArgumentBinding`, `ValueGraph`, or `Snapshot` payloads;
- caller-supplied production graph-node IDs;
- caller-controlled local node references, recursive/mutable bindings, arbitrary DAG sharing, or CSE;
- wildcard/default match arms;
- pattern display-name authoring;
- declaration mutation as part of expression construction;
- semantic merge/rebase or multi-proposal composition.

These exclusions are deliberate authority or semantic boundaries rather than missing encodings of the current feature.

## Typed local early exit

The `abort` recipe has exactly `kind` and `error` fields. Both name-resolved and
exact-ID authoring support it in a `Result<T,E>` function when the error recipe
has type `E`. Abort has no normal value and is valid only at a graph result,
including a branch or Bind body. It cannot be a call argument, container element
or Bind initializer. A mixed branch can abort on one side and produce a normal
value on the other. All-aborting branches propagate abrupt completion.

Targeted replacement can switch a graph-result expression between normal and
abrupt completion while preserving the target identity. The entire proposed
snapshot must still validate; an existing ordinary value use cannot consume an
abort. Simulation reports a misplaced abrupt replacement before building the
proposed snapshot: `TargetedExpressionAbruptValueUse` when another node uses the
target as a value, and `TargetedExpressionAbortNotGraphResult` when an `abort`
replaces a node that is not its graph's result. Review reports the owning function, payload and dependent continuations.
See the [complete contract](typed-function-abort.md), including cleanup, worker
cancellation, ordering and schema compatibility.
