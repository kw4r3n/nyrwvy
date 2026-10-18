# Option and Result accessors

- **Status:** Accepted
- **Date:** 2026-10-18
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 65

The [Option and Result combinators](option-result-combinators.md) adopted the semantics of Rust's `Option` and `Result` methods for `unwrap_or`, `map`, `and_then` and `map_err`, but only for `Option::unwrap_or`. Reading a `Result` with a fallback, turning an `Option` into a `Result`, keeping one case of a `Result` as an `Option`, or asking whether a value is present still took a `MatchOption` or `MatchResult` whose arms rebuilt the obvious value; [probe 26](../verification/task-communication-probe26-20261007.md) recorded `answerText` (9 nodes) and `succeeded` (8 nodes) written only to ask whether an outcome was present or successful. The owner chose on 2026-10-18 to add the remaining accessors one ordinarily expects, under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here), with Rust's semantics.

## Decision

Each is a fixed-signature operation checked from the `OperationSignatures` table, every operand is evaluated, and none of them fails:

- **`ResultGetOrDefault(result, default): T`**: the `Ok` value, or `default` for `Err`, as Rust's `Result::unwrap_or`. The default has the ok type and is evaluated either way, like `OptionGetOrDefault`'s; the error is dropped.
- **`OptionOkOr(option, error): Result<T, E>`**: `Some(x)` becomes `Ok(x)` and `None` becomes `Err(error)`, as Rust's `Option::ok_or`. `E` is the error operand's type, so no explicit error type is given; the error is evaluated either way (there is no lazy `ok_or_else`).
- **`ResultOkValue(result): Option<T>`** and **`ResultErrValue(result): Option<E>`**: `Some` of the `Ok` value (respectively the `Err` value) and `None` for the other case, as Rust's `Result::ok` and `Result::err`. They are named for the value they keep, since `ResultOk` and `ResultErr` are the constructors.
- **`OptionIsSome(option): Bool`** and **`ResultIsOk(result): Bool`**: true for `Some` and `Ok`, as Rust's `Option::is_some` and `Result::is_ok`.

**Not added: `OptionIsNone` and `ResultIsErr`.** Each is exactly `BoolNot` of the test above, one node away, and the warranted-operation rule excludes an operation that duplicates what an existing operation already expresses directly. Rust has both only because a method call reads better than a negation; in a graph the negation is as direct.

**Rust's semantics fit.** Every candidate takes eager operands, as `OptionGetOrDefault` and `MapGetOrDefault` already do, so no short-circuit or nested-graph rule is involved; none of the names clashes with an existing node kind (`ResultOk` and `ResultErr` are constructors, hence `ResultOkValue` and `ResultErrValue`); and the result types follow from the operands alone, which lets the `OperationSignatures` table check them, with a `Result<T, E>` signature type added for the purpose.

## Consequences

- The six go through the semantic graph, validation (`ResultInputTypeMismatch`, `ResultDefaultTypeMismatch`, `OptionInputTypeMismatch`), the snapshot and Nyason codecs (`resultGetOrDefault` / `result-get-or-default`, `optionOkOr` / `option-ok-or`, `resultOkValue` / `result-ok-value`, `resultErrValue` / `result-err-value`, `optionIsSome` / `option-is-some`, `resultIsOk` / `result-is-ok`), typed semantic IR, Execution IR, the C# backend (one conditional or member read over `HasValue` or `IsOk`, as the combinators), the viewer, wire recipes and the recipe reference.
- A snapshot that uses any of them is written at snapshot JSON 19 / Nyason `nayamnyam.snapshot.v15`, the version this batch of additions shares; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `OptionResultAccessorTests` covers them through every layer and under `runPure`, including a nested `Option<Option<T>>` and the persisted-graph diagnostics.
