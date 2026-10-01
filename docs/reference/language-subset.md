# Current language subset

**Status:** Current

This reference summarizes the semantic surface currently implemented and exercised through Nyrwvy's validation, persistence, lowering, viewer, and C# execution pipeline.

It is a current capability reference, not a promise that every listed construct is permanently frozen.

## Primitive domains

The current primitive value domains are:

- `String`;
- `Bool`;
- `Unit`;
- `Int64`;
- `Float64`;
- `Bits32`.

There is no implicit numeric promotion between `Int64` and `Float64`. `Int64ToFloat64` converts explicitly; there is no conversion from `Float64` to `Int64`. `Bits32` is not numeric and converts to and from `Int64` only explicitly (see [`Bits32`](#bits32)).

## Structured values and control

The implemented subset includes:

- product values and field access/construction;
- unions and variant construction;
- `Option<T>`;
- `Result<T,E>`;
- explicit conditionals;
- Bool negation and short-circuit Bool and/or;
- exhaustive union, `Option`, and `Result` matching with pattern bindings;
- internal direct function calls;
- pure capture-free function types, named references, and positional invocation;
- program-defined stateless Capability values backed by pure named functions;
- external capability invocation;
- explicit effects and capability requirements.

Wildcard/default match arms are not part of the current accepted subset where exhaustiveness is semantically important.

`BoolNot(operand)` negates a Bool. `BoolAnd(left, right)` is true when both operands are true and `BoolOr(left, right)` when either is. Their `left` operand is an ordinary Bool value of the enclosing graph; their `right` operand is a nested value graph with the scope, typing and effect rules of a `Conditional` branch. It is evaluated at most once and only when `left` does not decide the result: for `BoolAnd` only when `left` is true, for `BoolOr` only when `left` is false. A right operand that aborts, invokes a capability or calls a failing function therefore has that effect only when it is evaluated. All operands and results are Bool; a right operand may instead abort. Each construct is the dedicated form of a checkable conditional expansion (`Conditional(operand, false, true)`, `Conditional(left, right, false)`, `Conditional(left, true, right)`); the graph keeps the dedicated node so review reads the intent directly (see the [decision](../decisions/bool-operators-and-map.md)). There is no Map or Set.

`ValueEqual(left, right)` is structural equality of two values of one *equatable* type; its result is Bool and it never fails. String, Int64, Bool, Unit and Bits32 are equatable, as are `List`, `Option` and `Result` of equatable types and declared Products and Unions whose fields are all equatable, through any nesting and including recursive types. Float64 (NaN; compare with `Float64Equal`), function types, `Task`, `TaskScope`, capabilities and type parameters are not, nor is any type that reaches one. Two values are equal when they have the same variant and pairwise equal fields; Strings compare by ordinal code units, Options and Results by case and payload, and lists by length and then item by item in order. `ValueEqual` also covers Bool equality. A node whose operands differ in type reports `ValueEqualOperandTypeMismatch`, and one over a type that is not equatable reports `ValueEqualTypeNotEquatable`, naming the path to the offending part by display names (for example `Holder.measures -> List item -> Measure.value`). `StringEqual`, `Int64Equal` and `Float64Equal` remain the per-type forms ([decision](../decisions/value-equality-and-map.md)).

## `Int64`

`Int64` is an explicit signed 64-bit integer domain. Current arithmetic is deliberately named by behavior rather than inheriting a backend overflow mode.

Implemented operations include:

```text
Int64WrappingAdd
Int64WrappingSubtract
Int64WrappingMultiply
Int64FloorDivide
Int64FloorModulo
Int64Equal
Int64LessThan
Int64LessThanOrEqual
Int64GreaterThan
Int64GreaterThanOrEqual
```

Wrapping arithmetic uses two's-complement modulo-`2^64` behavior.

`Int64FloorDivide(left, right)` and `Int64FloorModulo(left, right)` follow Python's `//` and `%`: the quotient `q` is `floor(left / right)`, rounding toward negative infinity (`7 / 2 = 3`, `-7 / 2 = -4`, `7 / -2 = -4`, `-7 / -2 = 3`), and the remainder is `left - right * q`, so a nonzero remainder takes the divisor's sign (`7 % 2 = 1`, `-7 % 2 = 1`, `7 % -2 = -1`, `-7 % -2 = -1`). Both operands and the result are Int64 and neither operation has effects. Like every runtime operation of this subset (compare `StringSlice` clamping), they never fail: a zero divisor yields quotient `0` and remainder `left`, which keeps `left = right * q + r`, and `Int64.MinValue / -1` wraps to `Int64.MinValue` with remainder `0`, consistent with wrapping arithmetic. Callers who must distinguish a zero divisor test it with `Int64Equal` before dividing.

Checked arithmetic, implicit conversion, and a broader numeric tower are not part of this contract.

## Immutable ordered `List<T>`

`StringEqual(left, right)` compares two `String` values exactly by ordinal code-unit equality, without culture or case folding. For non-null strings, testing ordinal `StringContains` in both directions is also equivalent to equality; `StringEqual` expresses that intent directly rather than requiring two containment checks. Both operands must be String; its result is Bool. The C# backend uses `StringComparison.Ordinal`. `StringConcat(left, right)` accepts two Strings and yields their literal left-to-right concatenation, with no separator, normalization, or culture-dependent conversion; empty strings are identities. It does not implicitly format non-string values. `Int64ToString(value)` formats an Int64 as canonical invariant decimal text: ASCII digits, a leading `-` only for negative values, no `+`, grouping or padding (`Int64.MinValue` is `"-9223372036854775808"`). `StringParseInt64(text)` yields `Option<Int64>`: `Some` only when the whole String is an optional `-` followed by one or more ASCII digits and the value fits Int64. Leading zeros and `-0` are accepted; `+`, whitespace, separators, non-ASCII digits, exponents, hex, empty text and overflow yield `None`. Parsing never fails at runtime, so callers branch with `MatchOption`. `StringLength(text)` yields the number of code points as `Int64`: a surrogate pair counts once, and an unpaired surrogate code unit also counts once, so it is not the UTF-16 length. `StringSlice(text, start, end)` yields the code points at positions `[start, end)`. Both bounds are clamped to `[0, StringLength(text)]`, and `end <= start` yields `""`, so slicing never fails at runtime and never splits a surrogate pair. Negative positions are clamped, not counted from the end. Both operations scan the text, so their cost is linear in its length. Together they express bounded display text such as keeping the head and tail of a long String.

`StringLessThan(left, right)` is the String ordering: true when `left` sorts strictly before `right`. It compares Unicode code points lexicographically, as `StringLength` and `StringSlice` count them, and a proper prefix sorts first. A surrogate pair is compared as its scalar value, and an unpaired surrogate code unit as itself. The order is ordinal and case-sensitive (`"B"` before `"a"`), with no culture or normalization, and matches Python's `str` comparison. Characters above U+FFFF therefore sort after every BMP character, including U+E000 to U+FFFF, which UTF-16 code-unit order would put after them. Both operands must be String, the result is Bool, and it never fails. Together with `StringEqual` it gives the other comparisons (`right < left`, `not (right < left)`).

`StringCodePoints(text)` returns a `List<String>` holding each code point of `text` as a one-code-point `String`, in order. It counts code points as `StringLength` and `StringSlice` do: a valid surrogate pair is one item, and an unpaired surrogate code unit is its own one-unit item, so the list length always equals `StringLength(text)` and concatenating the items gives back `text`. It runs in linear time and never fails; the empty String gives the empty list. Iterate text with `ListFold` (or `ListMap`, `ListFilter`, `ListAny`) over `StringCodePoints` rather than with self-recursion that slices one character per call, which overflows the stack at a few thousand characters.

`StringIndexOf(text, substring, from)` yields `Option<Int64>`: the code-point position of the first ordinal occurrence of `substring` that starts at or after `from`, or `None`. `from` is clamped to `[0, StringLength(text)]` like a `StringSlice` bound (negative is 0, not counted from the end), and an empty substring is found at the clamped `from`. `StringLastIndexOf(text, substring)` yields the position of the last occurrence, with an empty substring found at `StringLength(text)`. `StringStartsWith(text, prefix)` and `StringEndsWith(text, suffix)` yield Bool; an empty prefix or suffix always matches. `StringSplit(text, separator)` yields the `List<String>` of pieces between non-overlapping occurrences of `separator`, left to right, keeping empty pieces (`"a,,b"` gives `["a", "", "b"]` and `""` gives `[""]`); an empty separator yields `[text]`. All five compare ordinally without culture, case folding or normalization, count positions as `StringLength` does, accept a match only when it starts and ends on a code-point boundary (so no match splits a surrogate pair), and never fail. Apart from the clamped `from` and the empty separator they agree with Python's `str.find`, `rfind`, `startswith`, `endswith` and `split(sep)` ([decision](../decisions/string-search-and-affixes.md)).

`StringSpan(text, set, from)` yields the `Int64` code-point position of the first code point at or after `from` that is not a member of `set`, or `StringLength(text)` when there is none; `StringSpanBack(text, set, end)` yields the least position `p <= end` such that every code point in `[p, end)` is a member. A member is any code point of the String `set`, counted as `StringLength` counts (a surrogate pair is one code point, an unpaired surrogate its own). `from` and `end` are clamped to `[0, StringLength(text)]` like `StringSlice` bounds, and an empty set yields the clamped bound. They are Python's `lstrip(set)` and `rstrip(set)` as positions, so `text.strip(set)` is `StringSlice(text, StringSpan(text, set, 0), StringSpanBack(text, set, StringLength(text)))`. Both run in linear time and never fail ([decision](../decisions/string-spans.md)).

`StringSplitLines(text, breaks)` yields the `List<String>` of the lines of `text`: scanning left to right, each code point that is a member of `breaks` (membership as for `StringSpan`'s set) ends the current line and is dropped, and when both `"\r"` and `"\n"` are members, `"\r\n"` is one break. As in Python `splitlines()`, an empty `text` yields `[]` and a break at the very end adds no trailing empty line (`"a\n"` gives `["a"]`, `"a\n\nb"` gives `["a", "", "b"]`, `"\n"` gives `[""]`); an empty `breaks` yields `[text]` for non-empty `text`. With `breaks` = `"\n\r\x0b\x0c\x1c\x1d\x1e\x85\u2028\u2029"` it equals Python `text.splitlines()`, and with `"\n\r"` it equals `bytes.splitlines` on the UTF-8 encoding. It runs in linear time and never fails ([decision](../decisions/string-split-lines.md)).

`StringToLower(text)` replaces each code point of `text` with its Unicode simple (one-to-one) lowercase mapping, culture invariant: `I` becomes `i`, KELVIN SIGN `K` becomes `k`, and code points without a mapping, including an unpaired surrogate, are kept, so the code-point length never changes. It equals Python `text.lower()` except for `İ` (U+0130), which becomes `i` rather than Python's `i̇`, and a word-final `Σ`, which becomes `σ` rather than Python's `ς`. The mappings follow the Unicode data of the .NET runtime. `StringUtf8ByteCount(text)` is the `Int64` length of the UTF-8 encoding of `text`, `len(text.encode("utf-8"))`; an unpaired surrogate counts 3 bytes. Both never fail; see the [decision](../decisions/string-lowercase-and-utf8-length.md).

`StringFromCodePoint(code)` yields `Option<String>`: for an `Int64` `code` with `0 <= code <= 0x10FFFF`, `Some` String of that one code point, Python `chr(code)`, and `None` for a negative code or one above 0x10FFFF. A surrogate code 0xD800..0xDFFF gives a String of one unpaired surrogate code unit, as Python allows, and `StringConcat` of a high and a low one forms the pair, as when a JSON `\ud83d\ude00` escape is decoded. `StringToCodePoint(text)` yields `Option<Int64>`, Python `ord(text)`: `Some` code point when `text` is exactly one code point as `StringLength` counts it (a surrogate pair gives its scalar value, an unpaired surrogate its code unit), and `None` for the empty String or a longer one. `StringToCodePoint(StringFromCodePoint(n)) = Some n` for every `n` in `0..0x10FFFF`. `runPure` passes Strings with unpaired surrogates as arguments and results unchanged, using JSON `\uD800`-style escapes ([String run values](../specs/agent-authoring-session.md)). Both never fail; see the [decision](../decisions/string-code-point-conversion.md).

`StringJoin(list, separator)` takes a `List<String>` and a String and yields the items in order with `separator` between adjacent items, Python `separator.join(list)`: an empty list gives `""`, a one-item list gives that item, and `StringJoin(StringSplit(t, s), s) = t` for every non-empty `s`. The items and separator are concatenated as `StringConcat` does. `ListSlice(list, start, end)` takes a `List<T>` for any item type and two Int64 bounds and yields the `List<T>` of the items at positions `[start, end)`, clamped exactly like `StringSlice`: both bounds are clamped to `[0, ListLength(list)]`, a negative bound is 0 and is not counted from the end (unlike Python `list[start:end]`), and `end <= start` gives the empty list. Both never fail ([decision](../decisions/string-join-and-list-slice.md)).

Function type parameters (`TypeRef.TypeParameter`) make a function generic over the types its parameter types mention; see [expression authoring](../specs/expression-authoring.md#higher-order-list-callbacks) for derivation of type arguments and scope rules. `List<T>` is immutable and ordered; element order is semantic state. `ListFold(source, seed, folder)` traverses left to right with a pure existing two-parameter callback `(accumulator: S, item: T) -> S` and returns the seed unchanged for empty input. The seed can carry runtime context such as a search key; this does not imply effectful traversal or closures. An authored Product accumulator carrying a runtime key, a found flag, and an `Option<T>` result can implement first-match lookup over `List<TItem>` without introducing an application-specific search node. The folder must preserve the accumulated result after the first match; an absent key or empty source yields `None`. A pure user-defined `List<String> * Int64 -> Option<String>` function can use `ListFold` with a Product accumulator holding the remaining index and optional value: negative, empty, and out-of-range inputs return `None`. This composition does not stop the fold early and can be generalized with function type parameters. `ListItemAt(list, index)` reads one item directly: for `list : List<T>` and `index : Int64` it returns `Some(item)` when `0 <= index < length` and `None` otherwise, never failing; negative indexes do not count from the end, as with `StringSlice` ([decision](../decisions/task-fields-list-indexing-and-map.md)).

The current collection surface includes:

```text
ListEmpty
ListConstruct
ListLength
ListItemAt
ListSlice
ListAppendItem
ListConcat
ListFold
ListEffectFold
UseResource
ParallelAll
CancellationRequested
ListMap
ListFilter
ListSortBy
ListAny
ListAll
```

`ListAppendItem` onto the list the previous append produced is amortised constant time, so a `ListFold` that appends one item per step builds its list in linear time, and `StringJoin` of such a list is a linear text builder. `StringConcat` onto the String the previous concatenation produced is likewise amortised in the length of its right operand, so a `ListFold` that concatenates one piece per step is also linear. Values are unaffected: appending or concatenating onto an older value, or twice onto the same one, gives independent values.

Higher-order operations reference callbacks by stable `FunctionId`. The accepted slice is pure: effectful callbacks are rejected rather than acquiring an implicit traversal-order/effect contract. Generic lambdas/closures are not part of the current subset.

`ListEffectFold(source, seed, folder, context)` is the exception with an explicit contract: its folder may use effects the enclosing function declares, and runs once per item, left to right. It is also the fold that stops early. When the folder returns `Result<S, E>` for seed `S` (pure or effectful), the node yields `Result<S, E>` and calls the folder for no item after the first `Err`. With a pure folder no effect is needed, so Python code that raises inside a loop is one folder returning `Result`, with no `Result` accumulator and no pass-through `Err` arm ([decision](../decisions/result-fold-short-circuit.md)).

`ListSortBy(source, lessThan, context)` returns the items of a `List<T>` in sorted order. `lessThan` is a pure callback whose two parameters not bound by the optional context receive two items `(left, right)` and return Bool: true when `left` must come before `right`. The sort is stable: items the callback does not order keep their input order. Descending order swaps the comparison, and a composite key compares the first key and falls through to the next on equality. For example, Python's `rows.sort(key=lambda r: (r.role, -r.count))` is `ListSortBy(rows, rowBefore)` with `rowBefore(a, b) = Conditional(StringEqual(a.role, b.role), Int64GreaterThan(a.count, b.count), StringLessThan(a.role, b.role))`. The result is defined by one algorithm, a top-down merge sort: a list of 0 or 1 items is returned as is; otherwise the first `length / 2` (rounded down) items and the rest are sorted, and then merged by repeatedly taking the head of the right half when `lessThan(rightHead, leftHead)` holds and the head of the left half otherwise. A callback that is a strict weak order yields the stable sorted order. Any other callback still yields a deterministic permutation of the source; for example, one that is always true reverses the list. It calls the callback at most `n log2 n` times and never fails.

## Program-defined capability values

`CapabilityConstruct` maps every operation of an existing Capability to a monomorphic pure function with the same positional parameter types and result type. The resulting value can be passed to existing consumers; invocation still requires the operation's declared Effect. See [program capability implementations](../specs/program-capability-implementations.md) for validation, authoring, review and versioned persistence. State, capture and effectful implementations are outside this slice.

## Pure function values

`TypeRef.Function(parameters, resultType)` describes a pure capture-free callable.
`FunctionReference` produces a value referring to a monomorphic named function;
`FunctionInvoke` checks positional arity and exact argument types. References
require transitive purity, including absence of ambient cancellation reads.
Generic functions can consume, forward, return and invoke function values using
their type parameters. `find<T>` can therefore receive a predicate as a value and
pass it to a `ListFold` folder through callback context. Capturing closures,
effectful function types and direct references to generic functions are excluded;
use a concrete wrapper for the latter. See [the contract](../specs/pure-function-values.md).

## `Float64`

`Float64` means IEEE 754 binary64 numeric semantics.

The authoritative literal representation is the exact 64-bit encoding, canonically rendered in persistence/IR as sixteen lowercase hexadecimal digits. This preserves signed zero, infinities, subnormal values, and exact finite binary64 encodings.

Semantic NaN literals canonicalize to:

```text
7ff8000000000000
```

NaN payload and sign are therefore not distinct ordinary `Float64` semantic facts.

Implemented nodes include:

```text
Float64Literal
Float64Add
Float64Subtract
Float64Multiply
Float64Divide
Float64Equal
Float64LessThan
Float64IsNaN
Float64IsFinite
Float64IsInfinite
Int64ToFloat64
Float64ToString
StringParseFloat64
```

`Int64ToFloat64(value)` returns the Float64 nearest to an Int64. Values with `|value| <= 2^53` convert exactly; larger ones round to nearest, ties to even, as C# `(double)long` and Python `float(int)`: `2^53 + 1` gives `2^53`, and `Int64.MaxValue` gives `2^63`. It never fails. There is no Float64 to Int64 conversion and no decimal rounding; see the [decision](../decisions/float64-conversion-and-run-values.md).

`Float64ToString(value)` yields Python `repr(value)`: the shortest decimal digits that read back to the same binary64, in exponent form `d.ddde±XX` (at least two exponent digits) when the decimal point position `decpt` of `0.ddd × 10^decpt` is `<= -4` or `> 16`, and otherwise in fixed form with at least one digit after the point: `0.1`, `100.0`, `1e+16`, `1.5e-05`, `5e-324`. NaN gives `nan`, the infinities `inf` and `-inf`, the zeros `0.0` and `-0.0`. `StringParseFloat64(text)` yields `Option<Float64>`: `Some` when the whole text is `-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?` in ASCII digits (the JSON number grammar, leading zeros accepted), rounded to the nearest binary64 with ties to even for any digit count and exponent length, with a signed infinity on overflow (`1e400`) and a signed zero on underflow (`-1e-400` gives -0.0); `+`, whitespace, `.5`, `5.`, `inf`, `nan`, underscores and empty text yield `None`. Every finite value round-trips through the two. Both never fail; see the [decision](../decisions/float64-decimal-text.md).

Binary64 infinity, NaN, and signed-zero outcomes are normal values under this contract rather than transaction diagnostics. See the [Float64 verification record](../verification/m1-float64-implementation-notes-20260828.md).

## `Bits32`

`Bits32` is exactly 32 bits with no numeric meaning: a separate domain, not a masked `Int64`. It has no literal, no ordering and no arithmetic besides wrapping addition; a constant is `Bits32FromInt64` of an `Int64Literal`. It is equatable (there is no NaN), so `ValueEqual` compares two Bits32 values bit for bit. Every operation is total and never fails:

```text
Bits32FromInt64(value: Int64): Bits32
Bits32ToInt64(value: Bits32): Int64
Bits32Xor(left, right) / Bits32And(left, right) / Bits32Or(left, right): Bits32
Bits32Not(value): Bits32
Bits32Add(left, right): Bits32
Bits32RotateRight(value: Bits32, amount: Int64): Bits32
Bits32ShiftRight(value: Bits32, amount: Int64): Bits32
Bits32ShiftLeft(value: Bits32, amount: Int64): Bits32
```

- `Bits32FromInt64` keeps the low 32 bits of the Int64's two's complement form, that is `value mod 2^32`: `-1` gives all ones, `2^32` gives zero and `2^32 + 5` gives 5.
- `Bits32ToInt64` reads the bits as an unsigned integer, `0..4294967295`. `Bits32ToInt64(Bits32FromInt64(x))` is `x` floor-modulo `2^32`.
- `Bits32Xor`, `Bits32And`, `Bits32Or` and `Bits32Not` are bitwise.
- `Bits32Add` is addition modulo `2^32`: `4294967295 + 2` gives 1.
- `Bits32RotateRight` rotates right by the amount taken floor-modulo 32, so a negative amount rotates left: `-1` is a rotation right by 31 (left by 1), and 32, 64 and `Int64.MinValue` leave the value unchanged.
- `Bits32ShiftRight` and `Bits32ShiftLeft` are logical shifts (zeros enter). An amount `<= 0` gives the value unchanged, and an amount `>= 32` gives zero; there is no masking of the amount.

There are no other widths, no signed interpretation, no reinterpretation of Float64 bits and no generic `Bits<N>`. These operations serve SHA-256 and similar fixed-width algorithms; see the [decision](../decisions/bits32-domain.md) and the [verification record](../verification/bits32-20261001.md).

## Agent editing surfaces

The implemented semantic language and agent editing authority are deliberately distinct. Typed agent operations expose useful semantic construction without making raw snapshot or graph payloads ordinary editing authority.

### Expression body replacement

`AgentExpressionStructuralWorkflow` is the reusable cross-domain construction surface. It replaces the complete body of one existing function from a tree-shaped `ExpressionRecipe` without exposing production graph-node or pattern-binding identities.

The current recipe covers primitive scalar expressions, the implemented pure-callback List family, structured values, complete direct calls, capability invocation, and scoped control flow:

```text
Parameter(ParameterId)
PatternReference(RecipePatternReference)
StringLiteral
BoolLiteral
UnitLiteral
Int64Literal
Float64Literal
StringIsEmpty
StringContains
StringEqual
StringLessThan
StringConcat
ValueEqual
Int64ToString
StringParseInt64
StringLength
StringSlice
StringCodePoints
StringIndexOf / LastIndexOf
StringSpan / SpanBack
StringStartsWith / EndsWith
StringSplit
StringSplitLines
StringJoin
StringToLower
StringUtf8ByteCount
StringFromCodePoint / ToCodePoint
Int64WrappingAdd / Subtract / Multiply
Int64FloorDivide / FloorModulo
Int64Equal / LessThan / LessThanOrEqual / GreaterThan / GreaterThanOrEqual
Float64Add / Subtract / Multiply / Divide
Float64Equal / LessThan
Float64IsNaN / IsFinite / IsInfinite
Int64ToFloat64
Bits32FromInt64 / ToInt64
Bits32Xor / And / Or / Not / Add
Bits32RotateRight / ShiftRight / ShiftLeft
ListEmpty
ListConstruct
ListLength
ListItemAt
ListSlice
ListAppendItem
ListConcat
ListFold
ListMap
ListFilter
ListSortBy
ListAny
ListAll
ProductConstruct
FieldAccess
ProductUpdate
UnionConstruct
OptionNone
OptionSome
ResultOk
ResultErr
CapabilityConstruct
FunctionReference
FunctionInvoke
DirectCall
SelfCall
CapabilityInvoke
Bind
BoolNot
BoolAnd / BoolOr
Conditional
MatchUnion
MatchOption
MatchResult
```

Before materialization the workflow checks target/base guards, the 32-depth / 256-node bounds across all children and nested branches, target-owned parameter scope, lexical pattern-reference scope/slots, scalar/List operand types, explicit `TypeRef` validity, higher-order callback contracts, structured declaration bindings, complete call/invocation contracts, predeclared effect requirements, conditional/match input and branch types, exact union arm coverage, exact replacement-result compatibility, and old-body obligation protection. Recipe nodes receive fresh system-owned `GraphNodeId`s; Bind-body and match-arm slots additionally receive fresh system-owned `PatternBindingId`s.

`ListEmpty`, `ListConstruct`, `OptionNone`, and one side of each `Result` constructor carry explicit type information when it cannot be inferred from a child. Named/capability references and builtin-generic arity are checked before materialization.

Higher-order List cases reference existing callbacks by `FunctionId`. `ListFold` accepts a pure binary `(S, T) -> S` with seed `S`; `ListMap` accepts a pure unary `T -> R`; `ListFilter`, `ListAny`, and `ListAll` accept a pure unary `T -> Bool`. `ListSortBy` accepts a pure binary less-than `(T, T) -> Bool`. An optional context binds further callback parameters by identity to values computed before the traversal, and these arities apply to the remaining unbound parameters (see [expression authoring](../specs/expression-authoring.md#higher-order-list-callbacks)). The workflow does not construct lambdas or permit effectful traversal callbacks. Source expressions may use effects already declared by the target.

Product and union construction bind declaration fields by stable `FieldId`. Preflight requires the exact declared field set with no duplicates and exact field-value types. Caller binding order is not semantic: materialization follows declaration field order and creates ordinary persistent `FieldValueBinding` values only after child nodes receive system-owned IDs. `FieldAccess` requires the exact owning product type. `ProductUpdate` copies a source of exactly the named product type with one or more of its fields replaced, each at most once, and keeps every other field; it never fails. `UnionConstruct` derives its result type from the referenced stable `VariantId`.

`OptionSome` derives its contained type from the child; `OptionNone` names the contained type explicitly. `ResultOk` derives its success type and carries the error type, while `ResultErr` derives its error type and carries the success type.

`DirectCall` references an existing callee by `FunctionId` and arguments by stable callee `ParameterId`. Every callee required effect must already be declared by the target, and every callee parameter must be bound exactly once with an exact-compatible child recipe. Caller binding-list order is ignored; materialization follows callee parameter declaration order and creates persistent `ArgumentBinding` values only after child nodes receive system-owned IDs. Incomplete calls do not create obligations through this surface.

`SelfCall` calls the owning function (the function being created, or the replacement target), with arguments keyed by zero-based parameter slot; it materializes to an ordinary `DirectCall` to the owner, so a recursive function is created in one request. See [expression authoring](../specs/expression-authoring.md#selfcall).

Recursion depth is bounded by the runtime stack, not by the language: each nested `DirectCall` (self or mutual) is one frame on the .NET thread stack, and exceeding it aborts the process. `runPure` reports that as `RunRuntimeFailure` `System.StackOverflowException` with a message saying the recursion was too deep. In practice a small self-recursive function reaches only several thousand nested calls (probe 4 overflowed at about 7,700 calls of a five-parameter scan). Recursion whose depth grows with the input size, such as one call per character or list item, is therefore not suitable for large inputs; use `ListFold` and the other list operations, over `StringCodePoints` for text, and keep recursion for depth that stays small (for example tree depth).

Positions cost time as well as depth. A `String` is stored as UTF-16 while every position counts code points, so `StringLength`, `StringSlice`, `StringIndexOf` with `from`, `StringLastIndexOf`, `StringSpan` and `StringSpanBack` map between code-point positions and UTF-16 offsets. The runtime scans a text of 256 or more UTF-16 units once, on the first such call, and remembers the result for that String value: when the text has no surrogate code units a position is its offset, and otherwise the offset of every 64th code point is kept. Later calls on the same String then reach any position in constant time, or by walking at most 63 code points; a shorter text with surrogates is walked from its start, which is bounded by its 256 units. A loop that advances a position a little per call (one character, or one match) and slices or searches the same String from it again is therefore linear overall, apart from the work of the call itself (the characters a slice copies, or a search scans). Each new String pays the first scan again, so a loop that re-slices its remaining text (`rest = StringSlice(rest, 1, StringLength(rest))`) is still O(n²) in the text length, as is the copying it does; step a position over one String instead. Loops of either kind are also bound by recursion depth. [Probe 5](../verification/rrawrlet-slice-probe5-20260930.md) saw the old quadratic cost only on adversarial input (thousands of colons in one path, or 16,000 spaces before `StringSpan` existed); [the offset map measurements](../verification/string-position-offsets-20260930.md) show the per-code-point scan it made slow. Prefer operations that consume the whole text in one pass (`StringCodePoints` with `ListFold`, `StringSplit`, `StringSplitLines`, `StringSpan`) when they fit.

`CapabilityInvoke` takes an ordinary receiver recipe, an existing `CapabilityOperationId`, and `(CapabilityParameterId, ExpressionRecipe)` bindings. The receiver must produce the operation's exact owning capability type; bindings must exactly cover the operation parameters with no duplicates and exact value types. The operation's effect must already be declared by the target. Materialization creates receiver nodes first, argument nodes in operation declaration order, and the invocation last. Its result type is the existing operation's `ResultType`; failure metadata is not reinterpreted.

All recursive receiver/argument expressions and every branch or Bind body are checked against the same target effect declaration. No effect is added or removed automatically, and simulation/publication do not execute the authored capability operations. Repeated effectful recipe occurrences are separate evaluations; references to a Bind's lexical value reuse its evaluated result.

Deterministic construction order is not a runtime sequencing guarantee. `SemanticallyReady` does not imply execution readiness: the existing execution layer still rejects independent effectful nodes with `AmbiguousEffectOrder` when graph dependencies do not order them. For example, an effectful receiver and an independent effectful argument do not become ordered just because the receiver was materialized first. Dependency-ordered chains and branch-local invocations retain their existing executable semantics; no hidden sequencing or node-list-order semantics are introduced.

`Bind(value, body)` explicitly evaluates its input once before its nested body, gives the body an immutable `Value` slot, and returns the body's result. It works for all supported value types, including `Unit` and capabilities; unused bindings retain initializer effects. Its binding is visible only in its body and nested scopes, not in its own initializer. Bind bodies consume one lexical scope depth just like match frames. The authoritative Bind node and system-owned `PatternBindingId` survive persistence and typed projection; existing Execution IR jumps and block parameters retain the order and provenance. Independent effectful siblings within either graph still reject.

Scoped control flow remains part of the same ephemeral construction model rather than exposing child graphs directly. `Conditional` requires a `Bool` condition and equal branch result types; `BoolAnd` and `BoolOr` require a `Bool` left operand and a right operand that produces `Bool` or aborts. `MatchUnion` is exhaustive and canonicalizes arms and field bindings to declaration order. `MatchOption` and `MatchResult` introduce typed branch bindings. `RecipePatternReference` addresses visible Bind-body and match-arm slots by lexical `ScopeDepth`; structurally empty frames such as `Option.None` still consume one scope depth. Branch recipes and the right operands of `BoolAnd`/`BoolOr` materialize as nested `ValueGraph`s, while production pattern identities and binding types remain system-owned and derived from validated semantic facts. Static effect checks cover every branch without hoisting its runtime effects into the parent graph.

The requested construction tree is ephemeral intent; the resulting `SemanticNodeKind` graph remains authoritative state. Review, receipt binding, stale rejection, readiness, publication, and history use the same structural transaction mechanics established in M2. `ResultingChange.AddedNodes` recursively includes branch-local nodes so the review covers the complete materialized result rather than only the parent graph.

See [Expression structural authoring](../specs/expression-authoring.md). The old `PureExpressionRecipe` and `AgentPureExpression...` names remain thin F# source aliases/forwarders for existing clients, not an independent pure-only interface. Existing diagnostic codes and historical evaluation protocol names are retained.

### M1 operation workflow

`AgentWorkflow` / `AgentOperationIntent` supports:

- display-name rename for types and functions;
- adding a function parameter with an explicit plan for every affected direct call;
- resolving a generated missing-argument obligation with an existing compatible value;
- explicitly adding or removing an existing `EffectId` from a function's `RequiredEffects`;
- adding a Product field with an explicit existing typed value for **every** affected `ProductConstruct`, including nested branches; the new `FieldId` is allocated by the system while existing type, function and node IDs remain stable;
- removing a Product field and its constructor bindings when all field accesses and `ProductUpdate` bindings of it have already been repaired; adding a field needs no plan for a `ProductUpdate`, which keeps it; affected functions must declare no required effects, so removal cannot silently drop effectful evaluation;
- deleting an unused function only after all direct calls, function-value references and List callbacks from other functions are gone, and when no obligation targets its body; the function's references to itself (self-recursion) are removed with it and do not count;
- deleting an unreferenced Product or Union declaration, including its field/variant identities; dependent functions can be removed in the **same** reviewed transaction, but any surviving references (including nested type references) prevent publication;
- deleting an unreferenced Capability and Effect declaration after consumers have been repaired or removed; one reviewed batch may remove the dependent function, Capability, then Effect in that order, and the diff retains removed operation/parameter identities;
- simulation, Change Review, reviewed publication or rejection, and stale-proposal failure.

Field-addition plans use `(constructNodeId, valueNodeId)` pairs for existing values, not arbitrary graph payloads or an implicit default. No partial publication is allowed: missing/duplicate plans, wrong types, or out-of-scope references reject. Function deletion likewise does not silently rewrite callers. The review diff lists added/removed fields, affected constructor identities, and deleted function/node identities. This is a bounded T1 change, not general Product field migration or deletion of referenced declarations.

Required-effect edits do not propagate automatically through callers and do not create obligations. Multiple explicit function edits may be composed in one transaction; full validation of the final proposed snapshot enforces caller/callee and capability/body effect consistency. Expression authoring still never mutates `RequiredEffects` implicitly.

### Fieldless Union variant evolution

`AgentFieldlessUnionVariantEvolutionWorkflow` supports one bounded schema-evolution operation: add a new **fieldless** variant to an existing Union while preserving the Union's `TypeId`. The caller identifies the target Union and one existing fieldless source `VariantId`; the new `VariantId` remains system-owned.

Publication repairs exhaustive `MatchUnion` nodes atomically by adding an arm for the new variant whose behavior is copied from the selected source arm. Copied arm bodies receive fresh system-owned graph-node and pattern-binding IDs. The variant addition and all required match repairs are reviewed and published as one snapshot, so no intermediate non-exhaustive state becomes authoritative.

This surface intentionally does not cover field-bearing variant evolution, arbitrary new match-arm recipes, type deletion, or a generalized schema-migration framework. It was added for the concrete Mini Work Planner `ExecutionState.TimedOut` maintenance case.

### Float64 structural compatibility surface

`AgentStructuralWorkflow` remains available for the narrower Float64-only body-replacement recipe:

```text
Parameter(ParameterId)
Literal(Float64Bits)
Add(left, right)
Subtract(left, right)
Multiply(left, right)
Divide(left, right)
```

It preserves the original bounded Float64 interaction surface and frozen M2 evidence. Adjacent Float64 arithmetic roles were added without creating separate operation/protocol families.

The exact narrower contract is maintained in [Bounded structural editing](../specs/bounded-structural-editing.md).

### Specialized ListMap compatibility surface

`AgentListMapStructuralWorkflow` supports the original shallow `ParameterReference -> ListMap` M2 body-replacement shape. The source is a target-owned `List<T>` parameter and the mapper an existing pure unary function compatible with `T -> R`; replacement node identities remain system-owned and the same simulate/review/publication mechanics apply.

The general `ExpressionRecipe` covers the same semantic capability through `ListMap(Parameter(sourceParameterId), mapperFunctionId)` while also allowing composed source expressions. The specialized workflow remains available for compatibility and historical evidence; removing or wrapping it is a separate API-cleanup decision rather than a requirement of the general construction model.

### Current boundary

The reusable `ExpressionRecipe` covers primitive scalar expressions, the implemented pure-callback List family, product/field/union construction, `Option`/`Result` construction, complete direct calls and capability invocations within predeclared effects, explicit scoped sequencing/value reuse through `Bind`, and scoped conditionals/matches with recipe-local lexical pattern references. It does **not** author incomplete calls, effect/capability/operation declarations, arbitrary references to outer graph nodes, or nested-expression replacement inside an existing body. `ExpressionRecipe` never changes the target function's effect declaration implicitly; `AgentWorkflow` separately supports explicit add/remove edits using already-existing `EffectId`s.

No maintained structural authoring surface accepts arbitrary `Snapshot`, `ValueGraph`, `SemanticNode`, `SemanticNodeKind`, `PatternBinding`, `FieldValueBinding`, `ArgumentBinding`, `CapabilityArgumentBinding`, or caller-owned production node/pattern IDs.

Those limits describe the current incremental implementation, not a standing requirement that agent authority remain one operation per semantic role. Broader typed construction should be judged by semantic clarity, validation, reviewability, and evidence rather than by the shape of the M2 bootstrap experiment.

## Evidence status

Milestone 1 and Milestone 2 both have recorded controlled agent-evaluation evidence.

M2 established, under its frozen conditions, that agents could inspect stable semantic facts, produce reviewed structural proposals, publish exact reviewed results, preserve unrelated semantic state, reject stale work, and recover through fresh inspection/simulation. The current expression workflow reuses that transaction/review interaction model across scalar, List, structured-value, complete-call, capability-invocation, and scoped control-flow domains.

The initial cross-domain scalar/List slice also passed the maintained extended verification suite after #117. Later additions reused that architectural evidence while their interaction assumptions remained materially the same.

Scoped control-flow authoring is a more meaningful interaction extension because agents must understand lexical pattern slots and `ScopeDepth`, while the materialized result includes nested branch graphs and system-owned pattern identities. Issue #132 records its completed architectural/extended pass and four exploratory tasks with accepted and committed proposals and no reported lexical diagnostic. This does not alter or retroactively broaden the frozen M2 claim.

The broader cross-domain surface is maintained implementation, but it does not claim that the entire algebra has independently undergone a new large controlled model evaluation. Capability authoring reuses existing persistent capability semantics and is covered by focused deterministic authoring and execution-IR lowering contracts; it does not claim a new live model evaluation.

See the [Milestone 2 record](../plans/milestone-2.md) and the maintained [verification philosophy](../developer/verification.md).

## Explicitly outside the current subset

The following are not currently accepted language features merely because a backend could express them:

- implicit numeric promotion or conversion;
- generic numeric operator bags;
- generic lambdas/closures;
- effectful List traversal callbacks;
- hidden backend exception/default semantics;
- exact bit manipulation beyond [`Bits32`](#bits32): other widths, byte strings and generic bit vectors.

`Bits32` is the one implemented exact-bit domain, separate from `Int64` and `Float64` numeric semantics; the broader direction remains an [experiment](../experiments/bit-representation.md).


## Failure behavior

Invalid semantic structure, explicit unresolved work, backend readiness failure, and expected runtime-domain failure are separate concepts. See the [failure and obligation policy](../specs/failure-obligation-policy.md).

## Milestone context

Milestone 1 is complete for its accepted bounded-operation claim, including its recorded agent evaluation.

Milestone 2 is complete for its accepted bounded structural-editing claim, including the Float64 and ListMap controlled evaluation records. Subsequent maintained extensions may reuse that evidence while their interaction-model assumptions remain materially unchanged; scoped lexical control-flow authoring was verified separately because it introduced a new interaction assumption rather than changing the completed M2 milestone.

## Typed function abort

`Abort(E)` ends the current `Result<T,E>` function invocation with `Err(E)`.
The exact-ID and symbolic recipe is `{"kind":"abort","error":<recipe>}`. It is
allowed at graph results, including nested Bind/match/conditional bodies, and
has no normal value. Only normally completing arms contribute a branch result
type. There is no user-visible Never type or implicit propagation through callees.

The caller can recover from the returned Err. UseResource releases after its
body function exits; ParallelAll requests cancellation on a worker Err, joins
every worker, and chooses the lowest-index typed failure. Host-fault precedence
is unchanged. See the [implemented contract](../specs/typed-function-abort.md)
and [ordinary-tool example](../../examples/function-abort/README.md).
