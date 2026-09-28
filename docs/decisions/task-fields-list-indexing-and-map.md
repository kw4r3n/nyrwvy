# Task fields, list indexing, and Map revisited

- **Status:** Accepted
- **Date:** 2026-09-28
- **Roadmap:** [post-M2 queue from the 2026-09-28 re-probe](../plans/post-m2-roadmap.md#queue-from-the-2026-09-28-re-probe), order 8

This decision settles order 8 of the post-M2 queue: how a leader selects a member's task handle by id. The [re-probe](../verification/rrawrlet-slice-reprobe-20260928.md) (friction 2) needed parallel `names`/`tasks` lists, an `IndexSearch` type and 4 functions, including an "Err means found" `ListEffectFold`, and folded a one-element wait result only to extract it. That is the second keyed-lookup consumer named in the [Map revisit condition](bool-operators-and-map.md#revisit-conditions). The implemented surface is described by the [language subset](../reference/language-subset.md) and the [expression authoring specification](../specs/expression-authoring.md), which remain the authority once implemented.

## Decision

**Task handles in declared types.** A Product or Union field may hold `Task<T>`, so a `Member { name: String, task: Task<Result<String, String>> }` Product keeps a handle with its name. `TaskScope` stays out of declared types (`TaskTypeInDeclaredType`): a scope is a lexical construct, opened and closed by one `TaskScope` node, and storing it would invite a `TaskStart` on a completed scope from far away.

**Transitive boundary rule.** A type is *task-bearing* if it mentions `Task` or `TaskScope`, or reaches a declared type with a task-bearing field, through `List`, `Option`, `Result`, function types and named types at any depth, including recursive types. Task-bearing types may not appear in capability operation parameters, results or failure types (`TaskTypeAtCapabilityBoundary`), and a `runPure` entry may not take or return one (`RunTaskTypeUnsupported`). This keeps handles out of the host boundary and out of run values exactly as before; only where a handle may live inside one run widens. External implementations implement capability interfaces, so the capability rule covers them.

**`ListItemAt(list, index): Option<T>`.** `list : List<T>`, `index : Int64`. `Some(item)` when `0 <= index < length`, otherwise `None`; it never fails, and a negative index does not count from the end, as with `StringSlice`. Recipe and wire name `listItemAt`, Nyason `list-item-at`.

**Map stays deferred.**

## Reason

**Task fields.** The phase-1 rule kept handles out of declared types to keep them out of `runPure` values and the host boundary; the [concurrency decision](concurrency-control.md#phase-1-as-implemented) deferred a `Member { name, task }` Product "until a probe needs it", and the re-probe now does. The boundaries can be kept without the field ban: checking capability operations and `runPure` entries transitively gives the same guarantee. In the C# backend a `Task<T>` field is an `NyTask<T>` record member; records hold it by reference, which matches a handle's identity.

**TaskScope stays out.** No probe stores a scope; the scope body receives it as a parameter and passes it to helpers. Allowing it would add a way for a scope to outlive its body with no evidence of need.

**ListItemAt.** It is a basic list operation, not a workload feature. It removes the "Err means found" fold that picked the index-th handle and the one-element fold over a single wait result. Returning `Option` keeps it total, like `StringParseInt64`, so there is no out-of-range failure to specify.

**Map revisited and still deferred.** With `Member` Products the handle lookup is the existing first-match `ListFold` over `List<Member>` (one step function and one fold), not 4 functions; the probe's usage upsert remains a fold. A Map still needs a key-equality contract and a new generic type through every layer (semantic graph, validation, codecs, typed and execution IR, the C# backend, the viewer, run values and both recipe wire forms). The remaining cost of the fold does not justify that.

## Consequences

- `TaskTypeInDeclaredType` now reports only `TaskScope` in a field; Task fields validate. Authoring workflows (type creation, field addition, field type migration) use the same snapshot validation, so they follow automatically.
- `TaskTypes.bearing` in the semantics foundation computes the transitive rule; the capability check and `runPure` use it.
- The C# backend emits the task runtime when any declared field holds a `Task`, even if no function mentions one.
- `ListItemAt` follows the usual path through the semantic graph, validation (`ListItemAtListTypeMismatch`, `ListItemAtIndexTypeMismatch`; recipes report `PureExpressionOperandTypeMismatch`), snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend (`NyList<T>.ItemAt`), the viewer (`List item at`, dependency roles source and index) and both recipe wire forms.

## Revisit conditions

- **Map:** measured fold cost matters for real sizes, or a consumer needs replace-or-append in more than one place where the fold helpers dominate its size. The smallest candidate is still a Map with String or Int64 keys.
- **TaskScope in declared types:** a probe needs to pass a scope together with other values in one Product.
