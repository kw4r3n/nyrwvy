# ListZipWith and ListFlatMap

- **Status:** Accepted
- **Date:** 2026-10-17
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 63

The owner added two List callback operations ahead of probe evidence, as ordinary builtins whose semantics follow normal expectation (Python where it applies). Both are pure, never fail at runtime, and reference an existing pure function by stable `FunctionId` with an optional context, as `ListMap` and `ListFold` do.

## Decision

- **`ListZipWith(left, right, combiner, context): List<R>`**: item i is `combiner(left[i], right[i])` for every index below the shorter length; the extra items of the longer list are ignored, as Python `zip`. The combiner has exactly two parameters not bound by context, (left item, right item), of the two item types, and returns `R`. The language has no tuple type, so a `zip` that returns pairs cannot be expressed; zip-with is the form that needs none, and a program that wants pairs combines the items into a product.
- **`ListFlatMap(source, mapper, context): List<R>`**: the lists `mapper(item)` returns, concatenated in source order, as Python `[y for x in xs for y in f(x)]`. The mapper has one parameter not bound by context and returns `List<R>`; a mapper returning `[]` drops its item. The runtime collects the parts, then copies each into one buffer of the total length that becomes the new list (the path `ListMap` uses), so the time is linear in the output length rather than quadratic as a `ListFold` over `ListConcat` is.
- Context is optional and binds further callback parameters, exactly as for `ListMap` and `ListFold`.
- Diagnostics reuse the shared callback codes with the operation and callback role in the message, as `ListMap` and `ListFold` do: authoring reports `PureExpressionCallbackNotFound`, `PureExpressionCallbackArityMismatch`, `PureExpressionCallbackInputTypeMismatch`, `PureExpressionCallbackResultTypeMismatch` (a flat-map mapper that does not return a List) and `PureExpressionCallbackEffectsNotAllowed`; persisted graphs report `UnknownCallbackFunction`, `CallbackArityMismatch`, `CallbackParameterTypeMismatch`, `CallbackResultTypeMismatch` and `CallbackEffectsNotAllowed`. No operation-specific codes such as `ListZipWithCombinerTypeMismatch` were added, because `ListMap` has none.

## Consequences

- Both go through the semantic graph, validation, snapshot and Nyason codecs (`listZipWith` with `leftListNodeId`, `rightListNodeId`, `combinerFunctionId`; `listFlatMap` with `sourceListNodeId`, `mapperFunctionId`; Nyason `list-zip-with`, `list-flat-map`), typed semantic IR, Execution IR, the C# backend (`NyList<T>.ZipWith`, `NyList<T>.FlatMap`), the viewer ("List zip with", "List flat map"), wire and name-resolved recipes and the recipe reference.
- A snapshot that uses either is written at snapshot JSON 17 / Nyason `nayamnyam.snapshot.v13`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `ListZipWithAndFlatMapTests` covers diagnostics in both validators, every layer, and under `runPure` unequal lengths both ways, empty lists, context, mappers returning empty lists and a flat map of 100,000 items within the run limit.

## Revisit conditions

- A probe that needs pairs themselves, for example to sort or group zipped items, would make a tuple type or a `ListZip` returning a product worth weighing.
- A probe that needs a zip that keeps the longer list's extra items (Python `zip_longest`) would need a separate operation with a fill value.
