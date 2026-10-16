# Option and Result combinators

- **Status:** Accepted
- **Date:** 2026-10-17
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 64

Transforming an `Option` or a `Result` took a `MatchOption` or `MatchResult` whose other arm rebuilt the value unchanged, and reading an `Option` with a fallback took a match too. No probe counted these. The owner chose on 2026-10-17 to add the combinators one ordinarily expects ahead of probe evidence, with the semantics of Rust's `Option` and `Result` methods of the same names.

## Decision

- **`OptionGetOrDefault(option, default): T`**: the `Some` value, or `default` for `None`, as Rust's `Option::unwrap_or`. The default has the item type and is evaluated either way, like `MapGetOrDefault`'s; there is no lazy `unwrap_or_else` form. For an `Option<Option<T>>`, `Some(None)` gives `None`, not the default.
- **`OptionMap(option, mapper, context): Option<U>`**: `None` stays `None` and `Some(x)` becomes `Some(mapper(x))`, as `Option::map`.
- **`OptionAndThen(option, binder, context): Option<U>`**: `None` stays `None` and `Some(x)` becomes `binder(x)`, which must return an `Option`, as `Option::and_then`.
- **`ResultMap(result, mapper, context): Result<U, E>`**: `Ok(x)` becomes `Ok(mapper(x))` and an `Err` stays unchanged, as `Result::map`.
- **`ResultMapErr(result, mapper, context): Result<T, F>`**: `Err(e)` becomes `Err(mapper(e))` and an `Ok` stays unchanged, as `Result::map_err`.
- **`ResultAndThen(result, binder, context): Result<U, E>`**: `Ok(x)` becomes `binder(x)` and an `Err` stays unchanged, as `Result::and_then`. The binder must return a `Result` with the source's error type `E`; there is no implicit error conversion.

The callbacks are existing pure named functions referenced by `FunctionId`, with exactly one parameter not bound by the optional context, as for `ListMap`. A callback runs only for the case it names. None of the six fails.

## Consequences

- The six go through the semantic graph, validation, the snapshot and Nyason codecs (`optionGetOrDefault`, `option-get-or-default`, ...), typed semantic IR, Execution IR, the C# backend (a conditional expression on `HasValue` or `IsOk`, as `MatchOption` and `MatchResult` branch), the viewer, wire recipes and the recipe reference. `OptionGetOrDefault` is checked from the `OperationSignatures` table; the five callback forms are hand-coded like `ListMap` and report the shared callback diagnostics. A snapshot that uses any of them is written at snapshot JSON 17 / Nyason `nayamnyam.snapshot.v13`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `OptionResultCombinatorTests` covers them through every layer and under `runPure`, including a callback that would change the result if it ran for the other case, context use, and `OptionGetOrDefault` over a nested `Option`.
