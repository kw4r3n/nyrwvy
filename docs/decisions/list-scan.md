# ListScan

- **Status:** Accepted
- **Date:** 2026-10-26
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 72

The owner added one List callback operation, as an ordinary builtin whose semantics follow normal expectation (F# `List.scan`, Haskell `scanl`, Python `itertools.accumulate(xs, f, initial=seed)`). It is pure, never fails at runtime, and references an existing pure function by stable `FunctionId` with an optional context, as `ListFold` does.

## Decision

- **`ListScan(source, seed, folder, context): List<S>`** returns the seed followed by every intermediate fold state: item 0 is `seed` and item i + 1 is `folder(item i, source[i])`. The length is the source length plus one, and an empty source gives `[seed]`. The last item equals `ListFold(source, seed, folder, context)`.
- The folder has the shape `ListFold` gives it: exactly two parameters not bound by context, `(state S, item T)`, and it returns `S`. Context is optional and binds further folder parameters, exactly as for `ListFold`.
- Diagnostics reuse the shared callback codes with the operation and callback role in the message, as `ListFold` and `ListZipWith` do: authoring reports `PureExpressionCallbackNotFound`, `PureExpressionCallbackArityMismatch`, `PureExpressionCallbackInputTypeMismatch` (parameters that do not match the seed and item types, or a folder that does not return the seed type) and `PureExpressionCallbackEffectsNotAllowed`; persisted graphs report `UnknownCallbackFunction`, `CallbackArityMismatch`, `CallbackParameterTypeMismatch`, `CallbackResultTypeMismatch` and `CallbackEffectsNotAllowed`. No operation-specific codes were added.
- The runtime, `NyList<T>.Scan<S>(seed, folder)`, allocates one array of length `Count + 1`, fills it in a single pass over the items, and builds the list from that owned array with no further copy (the path `ListMap` and `ListFlatMap` use). It is not built from `AppendItem`.

## Reason

A list of running states, for example the x position of each cell in a row as prefix sums of the cell widths, otherwise needs `ListFold` with `ListAppendItem`, which allocates a list object per item. Measured for a 213x60 terminal layout, that was about 13x slower than hand-written C# and about 1 MiB per call. `ListScan` allocates the one result array, so the time and allocation are linear in the source with a small constant.

## Consequences

- It goes through the semantic graph, validation, snapshot and record codecs (`listScan` with `sourceListNodeId`, `seedNodeId`, `folderFunctionId`; Nyason `list-scan`), typed semantic IR, Execution IR, the C# backend (`NyList<T>.Scan`), the viewer ("List scan"), wire and name-resolved recipes and the recipe reference.
- A snapshot that uses it is written at snapshot JSON 22 / Nyason `nayamnyam.snapshot.v18`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions). Ordinary persistence writes the newest version, so every ordinary write is now JSON 22; the canonical form of a snapshot that uses no newer construct is unchanged.
- `ListScanTests` covers diagnostics in both validators, every layer, and under `runPure` an empty source, prefix sums, context, the last item equal to `ListFold`, and a scan of 100,000 items within the run limit.

## Revisit conditions

- A probe that needs the scan without its seed item (Haskell `scanl1`, Python `accumulate` without `initial`) would need a separate operation or a `ListDropFirst`-style slice.
- A probe that needs a right-to-left scan (Haskell `scanr`) would need a separate operation.
