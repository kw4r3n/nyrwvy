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

There is no implicit numeric promotion between `Int64` and `Float64`. `Int64ToFloat64` converts explicitly; `Float64RoundToInt64` and `Float64FloorToInt64` convert back as `Option<Int64>`. `Bits32` is not numeric and converts to and from `Int64` only explicitly (see [`Bits32`](#bits32)).

## Structured values and control

The implemented subset includes:

- product values and field access/construction;
- unions and variant construction;
- `Option<T>`;
- `Result<T,E>`;
- explicit conditionals;
- Bool negation and short-circuit Bool and/or;
- exhaustive union, `Option`, and `Result` matching with pattern bindings;
- `Option` and `Result` combinators (`OptionGetOrDefault`, `OptionMap`, `OptionAndThen`, `ResultMap`, `ResultMapErr`, `ResultAndThen`; [decision](../decisions/option-result-combinators.md)) and accessors (`ResultGetOrDefault`, `OptionOkOr`, `ResultOkValue`, `ResultErrValue`, `OptionIsSome`, `ResultIsOk`; [decision](../decisions/option-result-accessors.md));
- internal direct function calls;
- pure capture-free function types, named references, and positional invocation;
- program-defined stateless Capability values backed by pure named functions;
- external capability invocation;
- explicit effects and capability requirements.

Wildcard/default match arms are not part of the current accepted subset where exhaustiveness is semantically important.

`BoolNot(operand)` negates a Bool. `BoolAnd(left, right)` is true when both operands are true and `BoolOr(left, right)` when either is. Their `left` operand is an ordinary Bool value of the enclosing graph; their `right` operand is a nested value graph with the scope, typing and effect rules of a `Conditional` branch. It is evaluated at most once and only when `left` does not decide the result: for `BoolAnd` only when `left` is true, for `BoolOr` only when `left` is false. A right operand that aborts, invokes a capability or calls a failing function therefore has that effect only when it is evaluated. All operands and results are Bool; a right operand may instead abort. Each construct is the dedicated form of a checkable conditional expansion (`Conditional(operand, false, true)`, `Conditional(left, right, false)`, `Conditional(left, true, right)`); the graph keeps the dedicated node so review reads the intent directly (see the [decision](../decisions/bool-operators-and-map.md)). Map and Set are also available; their operations and equatable key/item restrictions are described below.

`ValueEqual(left, right)` is structural equality of two values of one *equatable* type; its result is Bool and it never fails. String, Int64, Bool, Unit and Bits32 are equatable, as are `List`, `Option` and `Result` of equatable types and declared Products and Unions whose fields are all equatable, through any nesting and including recursive types. `Set` of equatable items and `Map` with equatable values are equatable too, compared by content regardless of insertion order ([decision](../decisions/map-set-equality.md)). Float64 (NaN; compare with `Float64Equal`), function types, `Task`, `TaskScope`, `OneShot`, capabilities and type parameters are not, nor is any type that reaches one. Two values are equal when they have the same variant and pairwise equal fields; Strings compare by ordinal code units, Options and Results by case and payload, and lists by length and then item by item in order. `ValueEqual` also covers Bool equality. A node whose operands differ in type reports `ValueEqualOperandTypeMismatch`, and one over a type that is not equatable reports `ValueEqualTypeNotEquatable`, naming the path to the offending part by display names (for example `Holder.measures -> List item -> Measure.value`). `StringEqual`, `Int64Equal` and `Float64Equal` remain the per-type forms ([decision](../decisions/value-equality-and-map.md)).

## Generic Products and Unions

A Product or Union may declare type parameters (`Box<T>`, `Maybe<T>`, `Tree<T>`), and every reference to it passes one type argument per parameter. Field types are read at the reference's arguments. Construction derives the arguments from the field values or takes them explicitly; a variant whose fields do not mention every parameter, such as a fieldless `Nothing`, needs them explicitly. A generic type is equatable at arguments that make its fields equatable. A field that refers back into the type's own recursive group passes plain type parameters only, so `Tree<T>` may hold `List<Tree<T>>` but not `Tree<List<T>>`. There are no constraints on type parameters. See the [decision](../decisions/generic-types.md) and the [authoring contract](../specs/expression-authoring.md#generic-product-and-union-types).

## `Int64`

`Int64` is an explicit signed 64-bit integer domain. Current arithmetic is deliberately named by behavior rather than inheriting a backend overflow mode.

Implemented operations include:

```text
Int64WrappingAdd
Int64WrappingSubtract
Int64WrappingMultiply
Int64FloorDivide
Int64FloorModulo
Int64Min
Int64Max
Int64WrappingAbs
Int64BitAnd
Int64BitOr
Int64BitXor
Int64BitNot
Int64ShiftLeft
Int64ShiftRight
Int64CheckedAdd
Int64CheckedSubtract
Int64CheckedMultiply
Int64Equal
Int64LessThan
Int64LessThanOrEqual
Int64GreaterThan
Int64GreaterThanOrEqual
```

Wrapping arithmetic uses two's-complement modulo-`2^64` behavior.

`Int64Min(left, right)` and `Int64Max(left, right)` yield the smaller and the larger operand, Python `min` and `max` of two values. `Int64WrappingAbs(value)` yields the absolute value with two's-complement wrapping, so `Int64.MinValue` gives `Int64.MinValue`, where Python `abs` gives `2^63`; this is the same wrapping as `Int64.MinValue / -1` below. All three never fail ([decision](../decisions/standard-operation-choices.md)).

`Int64BitAnd(left, right)`, `Int64BitOr(left, right)`, `Int64BitXor(left, right)` and `Int64BitNot(value)` are two's-complement bitwise operations, Python `&`, `|`, `^` and `~` (`Int64BitNot(x)` is `-x - 1`). `Int64ShiftLeft(value, count)` and `Int64ShiftRight(value, count)` clamp the count to `0..64`: a negative count leaves the value unchanged (Python raises), and the count is never masked to 6 bits. `Int64ShiftLeft` keeps the low 64 bits (`Int64ShiftLeft(1, 63)` is `Int64.MinValue`) and gives 0 for a count of 64 or more. `Int64ShiftRight` is arithmetic, Python `>>`: `Int64ShiftRight(-5, 1)` is -3, and a count of 64 or more gives 0 for a non-negative value and -1 for a negative one. None of them fails ([decision](../decisions/int64-bitwise-and-float64-min-max.md)).

`Int64FloorDivide(left, right)` and `Int64FloorModulo(left, right)` follow Python's `//` and `%`: the quotient `q` is `floor(left / right)`, rounding toward negative infinity (`7 / 2 = 3`, `-7 / 2 = -4`, `7 / -2 = -4`, `-7 / -2 = 3`), and the remainder is `left - right * q`, so a nonzero remainder takes the divisor's sign (`7 % 2 = 1`, `-7 % 2 = 1`, `7 % -2 = -1`, `-7 % -2 = -1`). Both operands and the result are Int64 and neither operation has effects. Like the other runtime operations of this subset (compare `StringSlice` clamping), they never fail with an error; only host failures such as an allocation too large for `ListRange` or `StringRepeat`, or recursion too deep, end a run: a zero divisor yields quotient `0` and remainder `left`, which keeps `left = right * q + r`, and `Int64.MinValue / -1` wraps to `Int64.MinValue` with remainder `0`, consistent with wrapping arithmetic. Callers who must distinguish a zero divisor test it with `ValueEqual` before dividing.

`Int64CheckedAdd(left, right)`, `Int64CheckedSubtract(left, right)` and `Int64CheckedMultiply(left, right)` yield `Option<Int64>`: `Some` of the exact result of Python `left + right`, `left - right` and `left * right` when it lies in `[-2^63, 2^63)`, and `None` otherwise (`Int64.MaxValue + 1`, `Int64.MinValue * -1`). They never fail ([decision](../decisions/checked-int64-arithmetic.md)).

Implicit conversion and a broader numeric tower are not part of this contract.

## Immutable ordered `List<T>`

`StringEqual`, `Int64Equal`, `MapGetOrDefault`, `Int64GreaterThan`, `Int64LessThanOrEqual` and `Int64GreaterThanOrEqual` are [retired from the agent reference](../decisions/retired-authoring-kinds.md): new graphs use `ValueEqual`, `OptionGetOrDefault` of `MapGet`, and `Int64LessThan` with swapped operands or under `BoolNot` (`a > b` is `Int64LessThan(b, a)`, `a <= b` is `BoolNot(Int64LessThan(b, a))`, `a >= b` is `BoolNot(Int64LessThan(a, b))`), and existing nodes keep their meaning. `StringEqual(left, right)` compares two `String` values exactly by ordinal code-unit equality, without culture or case folding. For non-null strings, testing ordinal `StringContains` in both directions is also equivalent to equality; `StringEqual` expresses that intent directly rather than requiring two containment checks. Both operands must be String; its result is Bool. The C# backend uses `StringComparison.Ordinal`. `StringConcat(left, right)` accepts two Strings and yields their literal left-to-right concatenation, with no separator, normalization, or culture-dependent conversion; empty strings are identities. It does not implicitly format non-string values. `Int64ToString(value)` formats an Int64 as canonical invariant decimal text: ASCII digits, a leading `-` only for negative values, no `+`, grouping or padding (`Int64.MinValue` is `"-9223372036854775808"`). `StringParseInt64(text)` yields `Option<Int64>`: `Some` only when the whole String is an optional `-` followed by one or more ASCII digits and the value fits Int64. Leading zeros and `-0` are accepted; `+`, whitespace, separators, non-ASCII digits, exponents, hex, empty text and overflow yield `None`. Parsing never fails at runtime, so callers branch with `MatchOption`. `StringLength(text)` yields the number of code points as `Int64`: a surrogate pair counts once, and an unpaired surrogate code unit also counts once, so it is not the UTF-16 length. `StringSlice(text, start, end)` yields the code points at positions `[start, end)`. Both bounds are clamped to `[0, StringLength(text)]`, and `end <= start` yields `""`, so slicing never fails at runtime and never splits a surrogate pair. Negative positions are clamped, not counted from the end. Both operations scan the text, so their cost is linear in its length. Together they express bounded display text such as keeping the head and tail of a long String.

`StringLessThan(left, right)` is the String ordering: true when `left` sorts strictly before `right`. It compares Unicode code points lexicographically, as `StringLength` and `StringSlice` count them, and a proper prefix sorts first. A surrogate pair is compared as its scalar value, and an unpaired surrogate code unit as itself. The order is ordinal and case-sensitive (`"B"` before `"a"`), with no culture or normalization, and matches Python's `str` comparison. Characters above U+FFFF therefore sort after every BMP character, including U+E000 to U+FFFF, which UTF-16 code-unit order would put after them. Both operands must be String, the result is Bool, and it never fails. Together with `StringEqual` it gives the other comparisons (`right < left`, `not (right < left)`).

`StringCodePoints(text)` returns a `List<String>` holding each code point of `text` as a one-code-point `String`, in order. It counts code points as `StringLength` and `StringSlice` do: a valid surrogate pair is one item, and an unpaired surrogate code unit is its own one-unit item, so the list length always equals `StringLength(text)` and concatenating the items gives back `text`. It runs in linear time and never fails; the empty String gives the empty list. Iterate text with `ListFold` (or `ListMap`, `ListFilter`, `ListAny`) over `StringCodePoints` rather than with self-recursion that slices one character per call, which copies the remaining text each turn and, unless the call is in tail position, overflows the stack at a few thousand characters.

`StringIndexOf(text, substring, from)` yields `Option<Int64>`: the code-point position of the first ordinal occurrence of `substring` that starts at or after `from`, or `None`. `from` is clamped to `[0, StringLength(text)]` like a `StringSlice` bound (negative is 0, not counted from the end), and an empty substring is found at the clamped `from`. `StringLastIndexOf(text, substring)` yields the position of the last occurrence, with an empty substring found at `StringLength(text)`. `StringLastIndexOfBefore(text, substring, end)` is Python `text.rfind(substring, 0, end)`: the position of the last occurrence that ends at or before `end`, which is clamped to `[0, StringLength(text)]` like `from`; an empty substring is found at the clamped `end`, so a backward scan over every occurrence passes each match's position as the next `end` ([decision](../decisions/string-last-index-of-before.md)). `StringStartsWith(text, prefix)` and `StringEndsWith(text, suffix)` yield Bool; an empty prefix or suffix always matches. `StringSplit(text, separator)` yields the `List<String>` of pieces between non-overlapping occurrences of `separator`, left to right, keeping empty pieces (`"a,,b"` gives `["a", "", "b"]` and `""` gives `[""]`); an empty separator yields `[text]`. `StringContains(text, substring)` is true exactly when `StringIndexOf(text, substring, 0)` is `Some`. All six compare ordinally without culture, case folding or normalization, count positions as `StringLength` does, accept a match only when it starts and ends on a code-point boundary (so no match splits a surrogate pair), and never fail. Apart from the clamped `from` and the empty separator they agree with Python's `str.find`, `rfind`, `startswith`, `endswith` and `split(sep)` ([decision](../decisions/string-search-and-affixes.md)).

`StringSpan(text, set, from)` yields the `Int64` code-point position of the first code point at or after `from` that is not a member of `set`, or `StringLength(text)` when there is none; `StringSpanBack(text, set, end)` yields the least position `p <= end` such that every code point in `[p, end)` is a member. A member is any code point of the String `set`, counted as `StringLength` counts (a surrogate pair is one code point, an unpaired surrogate its own). `from` and `end` are clamped to `[0, StringLength(text)]` like `StringSlice` bounds, and an empty set yields the clamped bound. They are Python's `lstrip(set)` and `rstrip(set)` as positions, so `text.strip(set)` is `StringSlice(text, StringSpan(text, set, 0), StringSpanBack(text, set, StringLength(text)))`. Both run in linear time and never fail ([decision](../decisions/string-spans.md)).

`StringSplitLines(text, breaks)` yields the `List<String>` of the lines of `text`: scanning left to right, each code point that is a member of `breaks` (membership as for `StringSpan`'s set) ends the current line and is dropped, and when both `"\r"` and `"\n"` are members, `"\r\n"` is one break. As in Python `splitlines()`, an empty `text` yields `[]` and a break at the very end adds no trailing empty line (`"a\n"` gives `["a"]`, `"a\n\nb"` gives `["a", "", "b"]`, `"\n"` gives `[""]`); an empty `breaks` yields `[text]` for non-empty `text`. With `breaks` = `"\n\r\x0b\x0c\x1c\x1d\x1e\x85\u2028\u2029"` it equals Python `text.splitlines()`, and with `"\n\r"` it equals `bytes.splitlines` on the UTF-8 encoding. It runs in linear time and never fails ([decision](../decisions/string-split-lines.md)).

`StringReplace(text, pattern, replacement)` yields `text` with every non-overlapping ordinal occurrence of `pattern`, scanning left to right, replaced by `replacement`, Python `text.replace(pattern, replacement)`: `"aaa"` with `"aa"` replaced by `"b"` is `"ba"`. A match starts and ends on code-point boundaries, as for `StringSplit`, so it never splits a surrogate pair. An empty `pattern` yields `text` unchanged, where Python inserts the replacement between every code point. `StringTrim(text)`, `StringTrimStart(text)` and `StringTrimEnd(text)` remove the code points with the Unicode White_Space property (U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and U+3000, exactly .NET `char.IsWhiteSpace`) from both ends, the start or the end. Python's `strip()` also removes U+001C–U+001F, which these keep; strip an explicit set with `StringSpan`, `StringSpanBack` and `StringSlice`. All four never fail ([decision](../decisions/string-replace-and-trim.md)).

`StringToLower(text)` replaces each code point of `text` with its Unicode simple (one-to-one) lowercase mapping, culture invariant: `I` becomes `i`, KELVIN SIGN `K` becomes `k`, and code points without a mapping, including an unpaired surrogate, are kept, so the code-point length never changes. It equals Python `text.lower()` except for `İ` (U+0130), which becomes `i` rather than Python's `i̇`, and a word-final `Σ`, which becomes `σ` rather than Python's `ς`. The mappings follow the Unicode data of the .NET runtime. `StringUtf8ByteCount(text)` is the `Int64` length of the UTF-8 encoding of `text`, `len(text.encode("utf-8"))`; an unpaired surrogate counts 3 bytes. Both never fail; see the [decision](../decisions/string-lowercase-and-utf8-length.md).

`StringToUpper(text)` replaces each code point with its Unicode simple (one-to-one) uppercase mapping, culture invariant: `i` and DOTLESS I `ı` become `I`, LONG S `ſ` becomes `S`, and `ß`, which has no simple uppercase mapping, becomes CAPITAL SHARP S `ẞ`, so `StringToLower` undoes it. Code points without a mapping are kept, so the code-point length never changes. It differs from Python `text.upper()`, which applies the full mapping (`ß` to `SS`, `ŉ` to `ʼN`) ([decision](../decisions/string-uppercase.md)).

`StringDecodeUtf8Lossy(bytes)` decodes UTF-8 bytes held one per code point (U+0000..U+00FF, the latin-1 form in which probes carry bytes, so code-point positions are byte offsets) and equals Python `bytes.decode("utf-8", "replace")`: each maximal ill-formed subpart of the input (a byte that cannot start a sequence, an overlong form, an encoded surrogate, a value above U+10FFFF, or a sequence cut short) becomes one U+FFFD. A code point above U+00FF is not a byte and reads as the invalid byte 0xFF, so it becomes one U+FFFD too. It never fails and runs in linear time; see the [decision](../decisions/utf8-decode.md). `StringEncodeUtf8(text)` is the inverse: the UTF-8 encoding of `text` as a String of one byte per code point, `text.encode("utf-8")` in that form, so decoding it gives `text` back for every well-formed String. An unpaired surrogate, on which Python raises, encodes as U+FFFD (`EF BF BD`), the 3 bytes `StringUtf8ByteCount` counts for it, so `StringLength(StringEncodeUtf8(text))` always equals `StringUtf8ByteCount(text)`. It never fails and runs in linear time; see the [decision](../decisions/string-encode-utf8.md).

`StringFromCodePoint(code)` yields `Option<String>`: for an `Int64` `code` with `0 <= code <= 0x10FFFF`, `Some` String of that one code point, Python `chr(code)`, and `None` for a negative code or one above 0x10FFFF. A surrogate code 0xD800..0xDFFF gives a String of one unpaired surrogate code unit, as Python allows, and `StringConcat` of a high and a low one forms the pair, as when a JSON `\ud83d\ude00` escape is decoded. `StringToCodePoint(text)` yields `Option<Int64>`, Python `ord(text)`: `Some` code point when `text` is exactly one code point as `StringLength` counts it (a surrogate pair gives its scalar value, an unpaired surrogate its code unit), and `None` for the empty String or a longer one. `StringToCodePoint(StringFromCodePoint(n)) = Some n` for every `n` in `0..0x10FFFF`. `runPure` passes Strings with unpaired surrogates as arguments and results unchanged, using JSON `\uD800`-style escapes ([String run values](../specs/agent-authoring-session.md)). Both never fail; see the [decision](../decisions/string-code-point-conversion.md).

`StringJoin(list, separator)` takes a `List<String>` and a String and yields the items in order with `separator` between adjacent items, Python `separator.join(list)`: an empty list gives `""`, a one-item list gives that item, and `StringJoin(StringSplit(t, s), s) = t` for every non-empty `s`. The items and separator are concatenated as `StringConcat` does. `ListSlice(list, start, end)` takes a `List<T>` for any item type and two Int64 bounds and yields the `List<T>` of the items at positions `[start, end)`, clamped exactly like `StringSlice`: both bounds are clamped to `[0, ListLength(list)]`, a negative bound is 0 and is not counted from the end (unlike Python `list[start:end]`), and `end <= start` gives the empty list. Both never fail ([decision](../decisions/string-join-and-list-slice.md)). `ListReverse(list)` yields the items of a `List<T>` in reverse order as a `List<T>` of the same type, Python `list(reversed(list))`; it runs in linear time and never fails ([decision](../decisions/standard-operations.md)).

`ListRange(start, end)` yields the `List<Int64>` `start, start + 1, …, end - 1`, Python `list(range(start, end))`; `end <= start` gives the empty list. `StringRepeat(text, count)` yields `text` repeated `count` times, Python `text * count`; `count <= 0` or an empty `text` gives `""`. The result is the UTF-16 units of `text` repeated, so a text ending in a high surrogate and starting with a low surrogate forms a pair at each seam, as `StringConcat` of the same pieces does (`StringLength` of `"\uDE00x\uD83D"` repeated twice is 5, where Python's code-point strings give 6). Both run in time linear in the result. A result too large to allocate (more items than an array can hold, or memory exhausted) is a host failure that stops the run, as it already is for a chain of `ListConcat` or `StringConcat` that doubles its value; neither returns `Option`, because the real limit is memory, not a fixed size ([decision](../decisions/standard-operation-choices.md)). A `ListFold` or `ListEffectFold` over `ListRange(0, n)` is a counted loop, and `ListEffectFold`'s early stop makes it a bounded while loop.

`Int64ToHexString(value)` formats an Int64 as lowercase hexadecimal with the fewest digits, Python `format(value, "x")`: a leading `-` only for negative values (`-255` is `"-ff"`, `Int64.MinValue` is `"-8000000000000000"`), no `0x` prefix and no padding (pad with `StringRepeat`). `StringParseInt64Hex(text)` yields `Option<Int64>`: `Some` only when the whole String is an optional `-` followed by one or more ASCII hexadecimal digits of either case and the value fits Int64. Leading zeros and `-0` are accepted; `0x`, `+`, whitespace, `_`, non-ASCII digits, empty text and overflow yield `None`, where Python `int(text, 16)` accepts the first four. It is as strict as `StringParseInt64` and never fails, and `StringParseInt64Hex(Int64ToHexString(v))` is `Some(v)` for every Int64.

Function type parameters (`TypeRef.TypeParameter`) make a function generic over the types its parameter types mention; see [expression authoring](../specs/expression-authoring.md#higher-order-list-callbacks) for derivation of type arguments and scope rules. `List<T>` is immutable and ordered; element order is semantic state. `ListFold(source, seed, folder)` traverses left to right with a pure existing two-parameter callback `(accumulator: S, item: T) -> S` and returns the seed unchanged for empty input. The seed can carry runtime context such as a search key; this does not imply effectful traversal or closures. An authored Product accumulator carrying a runtime key, a found flag, and an `Option<T>` result can implement first-match lookup over `List<TItem>` without introducing an application-specific search node. The folder must preserve the accumulated result after the first match; an absent key or empty source yields `None`. A pure user-defined `List<String> * Int64 -> Option<String>` function can use `ListFold` with a Product accumulator holding the remaining index and optional value: negative, empty, and out-of-range inputs return `None`. This composition does not stop the fold early and can be generalized with function type parameters. `ListItemAt(list, index)` reads one item directly: for `list : List<T>` and `index : Int64` it returns `Some(item)` when `0 <= index < length` and `None` otherwise, never failing; negative indexes do not count from the end, as with `StringSlice` ([decision](../decisions/task-fields-list-indexing-and-map.md)).

The current collection surface includes:

```text
ListEmpty
ListConstruct
ListLength
ListItemAt
ListSlice
ListSetAt
ListInsertAt
ListRemoveAt
ListRange
ListReverse
ListLast
ListDropLast
ListAppendItem
ListConcat
ListFold
ListEffectFold
UseResource
ParallelAll
CancellationRequested
ListMap
ListZipWith
ListFlatMap
ListFilter
ListSortBy
ListMinBy
ListMaxBy
ListIndexOf
ListLastIndexOf
ListContains
ListFindIndex
ListAny
ListAll
```

`ListAppendItem` onto the list the previous append produced is amortised constant time, so a `ListFold` that appends one item per step builds its list in linear time, and `StringJoin` of such a list is a linear text builder. `StringConcat` onto the String the previous concatenation produced is likewise amortised in the length of its right operand, so a `ListFold` that concatenates one piece per step is also linear. Values are unaffected: appending or concatenating onto an older value, or twice onto the same one, gives independent values.

`ListLast(list)` gives the last item as `Option<T>` (`None` for the empty list), and `ListDropLast(list)` gives the list without its last item (the empty list stays empty); neither fails. With `ListAppendItem` they make a list a stack: peek, pop and push. Both take constant time, because `ListDropLast` shares the items with the original list. A push onto a popped list copies the list once, since the original still holds that slot, so a loop that alternates pops and pushes on a deep stack still copies; one that only pushes and pops, or pushes again only after a further push, does not ([decision](../decisions/list-last-and-drop-last.md)).

Higher-order operations reference callbacks by stable `FunctionId`. Apart from `ListEffectFold`'s folder below, callbacks are pure: effectful callbacks are rejected rather than acquiring an implicit traversal-order/effect contract. Purity here is the same transitive rule as for [function values](#pure-function-values): a callback has no required effects and neither it nor any function it reaches invokes a capability, uses a resource, starts, waits for or cancels a task, runs `ParallelAll`, or reads `CancellationRequested`. Generic lambdas/closures are not part of the current subset.

`ListEffectFold(source, seed, folder, context)` is the exception with an explicit contract: its folder may use effects the enclosing function declares, and runs once per item, left to right. It is also the fold that stops early. When the folder returns `Result<S, E>` for seed `S` (pure or effectful), the node yields `Result<S, E>` and calls the folder for no item after the first `Err`. With a pure folder no effect is needed, so Python code that raises inside a loop is one folder returning `Result`, with no `Result` accumulator and no pass-through `Err` arm ([decision](../decisions/result-fold-short-circuit.md)).

`ListSortBy(source, lessThan, context)` returns the items of a `List<T>` in sorted order. `lessThan` is a pure callback whose two parameters not bound by the optional context receive two items `(left, right)` and return Bool: true when `left` must come before `right`. The sort is stable: items the callback does not order keep their input order. Descending order swaps the comparison, and a composite key compares the first key and falls through to the next on equality. For example, Python's `rows.sort(key=lambda r: (r.role, -r.count))` is `ListSortBy(rows, rowBefore)` with `rowBefore(a, b) = Conditional(ValueEqual(a.role, b.role), Int64LessThan(b.count, a.count), StringLessThan(a.role, b.role))`. The result is defined by one algorithm, a top-down merge sort: a list of 0 or 1 items is returned as is; otherwise the first `length / 2` (rounded down) items and the rest are sorted, and then merged by repeatedly taking the head of the right half when `lessThan(rightHead, leftHead)` holds and the head of the left half otherwise. A callback that is a strict weak order yields the stable sorted order. Any other callback still yields a deterministic permutation of the source; for example, one that is always true reverses the list. It calls the callback at most `n log2 n` times and never fails.

`ListMinBy(source, lessThan, context)` returns the smallest item as `Option<T>`, under the same kind of callback. The empty list gives `None`. Otherwise it scans left to right: the first item is the best so far, and a later item replaces it only when `lessThan(item, best)` holds. Among equal items the first wins, and any callback gives a deterministic result. It calls the callback at most `n - 1` times and never fails. `ListMaxBy(source, lessThan, context)` is its counterpart under the same callback: the same scan replacing the best only when `lessThan(best, item)` holds, so the first of equal largest items wins, as Python `max` ([decision](../decisions/list-max-by.md)).

`ListSetAt(list, index, item)` returns the list with the item at zero-based `index` replaced, `ListInsertAt(list, index, item)` the list with `item` inserted before `index`, and `ListRemoveAt(list, index)` the list without the item at `index`. `item` must have the list's item type (any type). Set and remove return the list unchanged for an index outside `[0, length)`, where Python raises `IndexError`; insert clamps the index to `[0, length]` like Python's `list.insert`, except that a negative index inserts first instead of counting from the end. None of them fails or changes its input. `ListSetAt` is O(log n); `ListInsertAt` and `ListRemoveAt` are O(log n + length − index), so appending and removing the first or last item are cheap and updating near the front copies the rest ([decision](../decisions/list-positional-update.md)).

`ListZipWith(left, right, combiner, context)` gives the `List<R>` whose item i is `combiner(left[i], right[i])` for every index below the shorter length; the extra items of the longer list are ignored, as Python `zip`. There is no tuple type, so a pairing combiner builds a product. `ListFlatMap(source, mapper, context)` concatenates, in source order, the `List<R>` a pure `T -> List<R>` mapper returns for each item, as Python `[y for x in xs for y in f(x)]`; a mapper returning `[]` drops its item, and the time is linear in the output length, so it replaces a `ListFold` over `ListConcat`. Neither fails ([decision](../decisions/list-zip-with-and-flat-map.md)).

`ListIndexOf(list, item)` gives the zero-based index of the first item equal to `item` (as `ValueEqual`) as `Option<Int64>`, `None` when there is none; the item type must be equatable. `ListFindIndex(source, predicate, context)` gives the index of the first item for which a pure `T -> Bool` callback is true, scanning left to right and stopping there. `ListLastIndexOf(list, item)` gives the index of the last equal item, scanning from the end, so an item near the end of a stack is found in time proportional to its distance from the end ([decision](../decisions/list-last-index-and-set-difference.md)). `ListContains(list, item)` is Python `item in list`: true when some item equals `item` (as `ValueEqual`), under the same equatable restriction, stopping at the first equal item; repeated tests against one list are better served by `SetFromList` once and `SetContains` ([decision](../decisions/list-contains.md)). None of them fails ([decision](../decisions/list-index.md)).

## Immutable insertion-ordered `Map<K, V>`

`Map<K, V>` maps keys to values. `K` must be equatable, the set [`ValueEqual`](#structured-values-and-control) accepts: String, Int64, Bool, Unit, `List`, `Option` and `Result` of equatable types, and declared Products and Unions of equatable fields. Float64, functions, tasks, capabilities and maps with non-equatable values are not keys; a `Map` is itself equatable when its values are ([decision](../decisions/map-set-equality.md)). Keys compare with `ValueEqual`. A type parameter is not equatable either (no `T : equality` constraint exists), so a key type is always concrete and a generic function cannot take or return `Map<T, _>`; `MapKeyTypeNotEquatable` reports it.

A map is an immutable value. `MapInsert` and `MapRemove` return a new map and leave the old one unchanged. Entries are visited in the order their keys were first inserted, as with Python's `dict` and JavaScript's `Map`. Replacing a key's value keeps its position, and removing a key and inserting it again puts it last. The order never depends on hash values, so it is the same on every run and backend.

```text
MapEmpty(keyType, valueType)       Map<K, V>
MapInsert(map, key, value)         Map<K, V>
MapGet(map, key)                   Option<V>
MapGetOrDefault(map, key, default) V, the value for key, or default when absent
MapContainsKey(map, key)           Bool
MapRemove(map, key)                Map<K, V>, unchanged when the key is absent
MapUnion(left, right)              Map<K, V>, both maps' entries; right's value on a shared key
MapSize(map)                       Int64
MapKeys(map)                       List<K>, in iteration order
MapValues(map)                     List<V>, in iteration order
MapFold(map, seed, folder)         S, folder(state, key, value) over the entries in order
MapFromList(items, keyOf)          Map<K, T>, items indexed by keyOf(item)
MapFilter(map, keep)               Map<K, V>, the entries where keep(key, value), in order
MapMapValues(map, mapper)          Map<K, U>, the same keys in order, values mapper(key, value)
```

Insert, get, contains and remove are O(log n), and size is O(1). None of them fails. `MapGetOrDefault` evaluates its default whether or not the key is present, as every operand is evaluated. `MapFold`, `MapFromList`, `MapFilter` and `MapMapValues` take pure named callbacks with context, like `ListFold`, `ListMap` and `ListFilter`. `MapFromList` keeps the first position and the last item for a repeated key, as repeated `MapInsert` does. `MapUnion` is Python `left | right`: its order is that of inserting right's entries into left one by one, so left's keys keep their positions and right's other keys follow in right's order ([decision](../decisions/set-is-subset-and-map-union.md)). See the [decision](../decisions/map.md).

## Immutable insertion-ordered `Set<T>`

`Set<T>` holds each item at most once. `T` must be equatable, as for `Map` keys, and items compare with `ValueEqual`. Like `Map`, a set is an immutable value visited in insertion order: inserting a present item changes nothing, and removing an item and inserting it again puts it last. A `Set` is itself equatable, compared by content regardless of insertion order ([decision](../decisions/map-set-equality.md)). As for `Map` keys, a type parameter is not equatable, so its item type is always concrete: a generic function cannot take or return `Set<T>` (`SetItemTypeNotEquatable`).

```text
SetEmpty(itemType)           Set<T>
SetFromList(items)           Set<T>, the items without repeats in first-occurrence order
SetInsert(set, item)         Set<T>
SetContains(set, item)       Bool
SetRemove(set, item)         Set<T>, unchanged when the item is absent
SetSize(set)                 Int64
SetToList(set)               List<T>, in iteration order
SetUnion(left, right)        Set<T>, left's items, then right's new items in right's order
SetIntersection(left, right) Set<T>, left's items that right contains, in left's order
SetDifference(left, right)   Set<T>, left's items that right does not contain, in left's order
SetIsSubset(left, right)     Bool, every item of left is in right (Python left <= right)
SetFold(set, seed, folder)   S, folder(state, item) over the items in insertion order
SetFilter(set, predicate)    Set<T>, the items where predicate(item), in insertion order
SetMap(set, mapper)          Set<U>, mapper(item) for each item in order, inserted once each; U equatable
```

Insert, contains and remove are O(log n). None of them fails. An empty set is a subset of any set; `SetIsSubset` is false at once when left is larger, and otherwise O(|left| log |right|) ([decision](../decisions/set-is-subset-and-map-union.md)). See the [decision](../decisions/set.md).

`SetFold`, `SetFilter` and `SetMap` are the `ListFold`, `ListFilter` and `ListMap` callbacks over a set's items in insertion order, with the same pure callbacks (`(S, T) -> S` with two parameters not bound by context; `T -> Bool`; `T -> U`). `SetFilter` keeps the kept items' order and type. `SetMap` is Python's `{f(x) for x in s}`: each result is inserted once in first-occurrence order, so the result can be smaller than the source, and `U` must be equatable like every Set item type. None of them fails. There is no `SetAny` or `SetAll`: `SetContains` answers membership, and `ListAny` or `ListAll` over `SetToList` covers a predicate ([decision](../decisions/set-callbacks.md)).

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
Float64FloorModulo
Float64Equal
Float64LessThan
Float64IsNaN
Float64IsFinite
Float64IsInfinite
Int64ToFloat64
Float64RoundToInt64
Float64FloorToInt64
Float64Sqrt
Float64Abs
Float64Negate
Float64Min
Float64Max
Float64Floor
Float64Ceiling
Float64Truncate
Float64ToString
StringParseFloat64
```

`Int64ToFloat64(value)` returns the Float64 nearest to an Int64. Values with `|value| <= 2^53` convert exactly; larger ones round to nearest, ties to even, as C# `(double)long` and Python `float(int)`: `2^53 + 1` gives `2^53`, and `Int64.MaxValue` gives `2^63`. It never fails; see the [decision](../decisions/float64-conversion-and-run-values.md).

`Float64RoundToInt64(value)` is the nearest integer, rounding half away from zero (`2.5` gives 3, `-0.5` gives -1; Python's `round` rounds ties to even), and `Float64FloorToInt64(value)` is Python `math.floor(value)` (`-0.5` gives -1). Both yield `Option<Int64>`: `None` for NaN, either infinity, or a result outside Int64 (Python raises or returns a larger `int` there), and otherwise `Some` of the exact integer. Both never fail. See the [decision](../decisions/float64-to-int64.md) and its [amendment](../decisions/rounding-half-away-from-zero.md).

`Float64Round(value, digits)` rounds to `digits` decimal places (tens, hundreds, ... for negative `digits`), half away from zero, on the shortest decimal text that reads back to the value: `Float64Round(2.675, 2)` is 2.68 and `Float64Round(1250, -2)` is 1300, where Python's `round` gives 2.67 and 1200. NaN, the infinities and zeros are returned unchanged, a result rounded to zero keeps the input's sign, and a result beyond the Float64 range is infinity. It never fails ([decision](../decisions/rounding-half-away-from-zero.md)).

`Float64Sqrt`, `Float64Abs`, `Float64Negate`, `Float64Floor`, `Float64Ceiling` and `Float64Truncate` take a Float64 and yield a Float64. Each is the IEEE 754 operation, which has exactly one result, so every backend agrees bit for bit. `Float64Sqrt` is the correctly rounded square root; a negative value gives NaN, where Python's `math.sqrt` raises, and `-0.0` gives `-0.0`. `Float64Abs` clears the sign (Python `abs(x)`, so `-0.0` gives `0.0`) and `Float64Negate` flips it (Python `-x`, so `0.0` gives `-0.0`, which subtracting from `0.0` does not). `Float64Floor`, `Float64Ceiling` and `Float64Truncate` round to an integral value toward negative infinity, positive infinity and zero, as Python's `math.floor`, `math.ceil` and `math.trunc` but without leaving Float64: a zero result keeps the sign of the input (`-0.5` gives `-0.0` for ceiling and truncation), and NaN, the infinities and the zeros are returned unchanged. All six never fail ([decision](../decisions/standard-operations.md)).

`Float64FloorModulo(left, right)` is Python's float `%`, named and signed like `Int64FloorModulo`: the exact remainder `fmod(left, right)` moved by one divisor when it is nonzero and signed unlike the divisor, so a nonzero result takes the divisor's sign (`7.5 % 2 = 1.5`, `-7.5 % 2 = 0.5`, `7.5 % -2 = -0.5`), and a zero result takes the divisor's sign (`-6.0 % 3.0` is `0.0`, `6.0 % -3.0` is `-0.0`). Where Python raises `ZeroDivisionError` the result is NaN, as `Float64Divide` gives IEEE results: a zero divisor, an infinite dividend or a NaN operand gives NaN. A finite dividend with an infinite divisor is the dividend, or the infinity when their signs differ (`-5.0 % inf` is `inf`), as in Python. It never fails ([decision](../decisions/float64-floor-modulo.md)).

`Float64Min(left, right)` and `Float64Max(left, right)` are the IEEE 754-2019 `minimum` and `maximum` (.NET `Math.Min` and `Math.Max`): NaN when either operand is NaN, and -0.0 is less than 0.0, whatever the operand order. Python's `min` and `max` differ there, since they return the first operand when the comparison is false. Neither fails ([decision](../decisions/int64-bitwise-and-float64-min-max.md)).

`Float64ToString(value)` yields Python `repr(value)`: the shortest decimal digits that read back to the same binary64, in exponent form `d.ddde±XX` (at least two exponent digits) when the decimal point position `decpt` of `0.ddd × 10^decpt` is `<= -4` or `> 16`, and otherwise in fixed form with at least one digit after the point: `0.1`, `100.0`, `1e+16`, `1.5e-05`, `5e-324`. NaN gives `nan`, the infinities `inf` and `-inf`, the zeros `0.0` and `-0.0`. `StringParseFloat64(text)` yields `Option<Float64>`: `Some` when the whole text is `-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?` in ASCII digits (the JSON number grammar, leading zeros accepted), rounded to the nearest binary64 with ties to even for any digit count and exponent length, with a signed infinity on overflow (`1e400`) and a signed zero on underflow (`-1e-400` gives -0.0); `+`, whitespace, `.5`, `5.`, `inf`, `nan`, underscores and empty text yield `None`. Every finite value round-trips through the two. Both never fail; see the [decision](../decisions/float64-decimal-text.md).

Binary64 infinity, NaN, and signed-zero outcomes are normal values under this contract rather than transaction diagnostics. See the [Float64 verification record](../../docs-archive/2026-08-pre-reorg/m1-float64-implementation-notes-20260828.md).

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
ParameterSlot(index)
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
Int64ToHexString
StringParseInt64Hex
StringRepeat
StringLength
StringSlice
StringCodePoints
StringIndexOf / LastIndexOf
StringSpan / SpanBack
StringStartsWith / EndsWith
StringSplit
StringSplitLines
StringReplace
StringTrim / TrimStart / TrimEnd
StringJoin
StringToLower
StringToUpper
StringUtf8ByteCount
StringDecodeUtf8Lossy / EncodeUtf8
StringFromCodePoint / ToCodePoint
Int64WrappingAdd / Subtract / Multiply
Int64FloorDivide / FloorModulo
Int64Min / Max / WrappingAbs
Int64BitAnd / BitOr / BitXor / BitNot
Int64ShiftLeft / ShiftRight
Int64Equal / LessThan / LessThanOrEqual / GreaterThan / GreaterThanOrEqual
Float64Add / Subtract / Multiply / Divide
Float64Equal / LessThan
Float64IsNaN / IsFinite / IsInfinite
Int64ToFloat64
Float64ToString
StringParseFloat64
Float64RoundToInt64 / FloorToInt64
Float64Sqrt / Abs / Negate
Float64Min / Max
Float64Floor / Ceiling / Truncate
Bits32FromInt64 / ToInt64
Bits32Xor / And / Or / Not / Add
Bits32RotateRight / ShiftRight / ShiftLeft
ListEmpty
ListConstruct
ListLength
ListItemAt
ListSlice
ListSetAt
ListInsertAt
ListRemoveAt
ListRange
ListReverse
ListLast
ListDropLast
ListAppendItem
ListConcat
ListFold
ListEffectFold
ParallelAll
CancellationRequested
TaskScope
TaskStart
TaskWait
TaskCancel
OneShotCreate
OneShotComplete
OneShotWait
UseResource
CancellationShield
ExceptionCatch
ExceptionMessage
ExceptionIsCancellation
ExceptionRethrow
ListMap
ListZipWith
ListFlatMap
ListFilter
ListSortBy
ListMinBy
ListMaxBy
ListIndexOf
ListLastIndexOf
ListContains
ListFindIndex
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
OptionGetOrDefault / ResultGetOrDefault
OptionOkOr
ResultOkValue / ResultErrValue
OptionIsSome / ResultIsOk
OptionMap / OptionAndThen
ResultMap / ResultMapErr / ResultAndThen
Abort
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

Higher-order List cases reference existing callbacks by `FunctionId`. `ListFold` accepts a pure binary `(S, T) -> S` with seed `S`; `ListMap` accepts a pure unary `T -> R`; `ListZipWith` accepts a pure binary `(A, B) -> R` over the left and right item types; `ListFlatMap` accepts a pure unary `T -> List<R>`; `ListFilter`, `ListFindIndex`, `ListAny`, and `ListAll` accept a pure unary `T -> Bool`. `ListSortBy` and `ListMinBy` accept a pure binary less-than `(T, T) -> Bool`. An optional context binds further callback parameters by identity to values computed before the traversal, and these arities apply to the remaining unbound parameters (see [expression authoring](../specs/expression-authoring.md#higher-order-list-callbacks)). `ListEffectFold` accepts a binary `(S, T) -> S` folder (or one returning `Result<S, E>`) that may use effects the target declares. The workflow does not construct lambdas or permit any other effectful callback. Source expressions may use effects already declared by the target.

Product and union construction bind declaration fields by stable `FieldId`. Preflight requires the exact declared field set with no duplicates and exact field-value types. Caller binding order is not semantic: materialization follows declaration field order and creates ordinary persistent `FieldValueBinding` values only after child nodes receive system-owned IDs. `FieldAccess` requires the exact owning product type. `ProductUpdate` copies a source of exactly the named product type with one or more of its fields replaced, each at most once, and keeps every other field; it never fails. `UnionConstruct` derives its result type from the referenced stable `VariantId`.

`OptionSome` derives its contained type from the child; `OptionNone` names the contained type explicitly. `ResultOk` derives its success type and carries the error type, while `ResultErr` derives its error type and carries the success type.

`Option` and `Result` combinators follow Rust's methods of the same names ([decision](../decisions/option-result-combinators.md)):

```text
OptionGetOrDefault(option, default)       T, the Some value, or default for None
OptionMap(option, mapper, context)        Option<U>, None, or Some(mapper(x))
OptionAndThen(option, binder, context)    Option<U>, None, or binder(x): Option<U>
ResultMap(result, mapper, context)        Result<U, E>, Ok(mapper(x)), or the Err unchanged
ResultMapErr(result, mapper, context)     Result<T, F>, Err(mapper(e)), or the Ok unchanged
ResultAndThen(result, binder, context)    Result<U, E>, binder(x): Result<U, E>, or the Err unchanged
ResultGetOrDefault(result, default)       T, the Ok value, or default for Err
OptionOkOr(option, error)                 Result<T, E>, Ok(x) for Some(x), Err(error) for None
ResultOkValue(result)                     Option<T>, Some of the Ok value, None for Err
ResultErrValue(result)                    Option<E>, Some of the Err value, None for Ok
OptionIsSome(option)                      Bool, true for Some
ResultIsOk(result)                        Bool, true for Ok
```

`OptionGetOrDefault` and `ResultGetOrDefault` evaluate their default whether or not the value is present, as `MapGetOrDefault` does, and the default has the item or ok type (for `Option<Option<T>>`, `Some(None)` gives `None`, not the default). `OptionOkOr` likewise evaluates its error either way, and its error type is the error operand's type. `ResultOkValue` and `ResultErrValue` are Rust's `Result::ok` and `Result::err`, named for the value they keep because `ResultOk` and `ResultErr` are the constructors. `OptionIsSome` and `ResultIsOk` are `is_some` and `is_ok`; there is no `IsNone` or `IsErr`, which are `BoolNot` of them ([decision](../decisions/option-result-accessors.md)). The callbacks are pure named functions with exactly one parameter not bound by the optional context, as for `ListMap`, and run only for the case they name: `Some`, `Ok`, or `Err` for `ResultMapErr`. `ResultAndThen`'s binder must return a `Result` with the source's error type. None of them fails.

`DirectCall` references an existing callee by `FunctionId` and arguments by stable callee `ParameterId`. Every callee required effect must already be declared by the target, and every callee parameter must be bound exactly once with an exact-compatible child recipe. Caller binding-list order is ignored; materialization follows callee parameter declaration order and creates persistent `ArgumentBinding` values only after child nodes receive system-owned IDs. Incomplete calls do not create obligations through this surface.

`SelfCall` calls the owning function (the function being created, or the replacement target), with arguments keyed by zero-based parameter slot; it materializes to an ordinary `DirectCall` to the owner, so a recursive function is created in one request. A self call, whether written as `SelfCall` or as a `DirectCall` to the owner's own `FunctionId`, takes the owner's exact parameter types: a generic function calls itself at its own type parameters, never at `List<T>` for `T` or with two type parameters swapped (there is no polymorphic self-recursion; a persisted graph that does so reports `PolymorphicSelfCallUnsupported`). Polymorphic recursion through another generic function is an ordinary call. See [expression authoring](../specs/expression-authoring.md#selfcall).

**A self `DirectCall` in tail position never grows the stack.** This is part of the language, not an optimisation: such a call runs as a jump back to the start of the function with its arguments as the new parameter values, so a self-recursive function with its loop variables as parameters is a loop and can run for millions of turns ([decision](../decisions/tail-self-calls.md)). Tail position is defined on the graph, starting from the function body's result:

- the result of the function body is in tail position;
- if a `Bind` is in tail position, its `body` is;
- if a `Conditional` is, both branches are;
- if a `MatchOption`, `MatchResult` or `MatchUnion` is, every arm and `otherwise` is;
- if a `BoolAnd` or `BoolOr` is, its `right` operand is.

Nothing else is. A self call whose value is wrapped (`ResultOk`, `OptionSome`, a Product field) or used as an operand or match input is an ordinary call. A call inside a callback (`ListFold`, `UseResource`, `TaskScope`, `ParallelAll`, `TaskStart`, …) is in that callback's own function: it is a tail call there when it calls that function, and never a tail call of the outer function. The rule applies to effectful functions as well; effects and aborts happen in the same order as in an ordinary call. An endless tail loop is not an error of the language: it ends at the `runPure` time limit or runs until cancelled, so a loop inside a `ParallelAll` worker or task should still check `CancellationRequested`. Mutual recursion is not covered.

Other recursion depth is bounded by the runtime stack, not by the language: each nested `DirectCall` that is not a tail self call (self or mutual) is one frame on the .NET thread stack, and exceeding it aborts the process. `runPure` reports that as `RunRuntimeFailure` `System.StackOverflowException` with a message saying the recursion was too deep. In practice a small self-recursive function reaches only several thousand nested calls this way (probe 4 overflowed at about 7,700 calls of a five-parameter scan). Non-tail recursion whose depth grows with the input size, such as one call per character or list item, is therefore not suitable for large inputs; write it as a tail self call, or use `ListFold` and the other list operations (over `StringCodePoints` for text), and keep non-tail and mutual recursion for depth that stays small (for example tree depth).

Positions cost time as well as depth. A `String` is stored as UTF-16 while every position counts code points, so `StringLength`, `StringSlice`, `StringIndexOf` with `from`, `StringLastIndexOf`, `StringSpan` and `StringSpanBack` map between code-point positions and UTF-16 offsets. The runtime scans a text of 256 or more UTF-16 units once, on the first such call, and remembers the result for that String value: when the text has no surrogate code units a position is its offset, and otherwise the offset of every 64th code point is kept. Later calls on the same String then reach any position in constant time, or by walking at most 63 code points; a shorter text with surrogates is walked from its start, which is bounded by its 256 units. A loop that advances a position a little per call (one character, or one match) and slices or searches the same String from it again is therefore linear overall, apart from the work of the call itself (the characters a slice copies, or a search scans). Each new String pays the first scan again, so a loop that re-slices its remaining text (`rest = StringSlice(rest, 1, StringLength(rest))`) is still O(n²) in the text length, as is the copying it does; step a position over one String instead. Loops of either kind written as non-tail recursion are also bound by recursion depth; a tail self call is not. [Probe 5](../verification/rrawrlet-slice-probes-summary.md#probe-5) saw the old quadratic cost only on adversarial input (thousands of colons in one path, or 16,000 spaces before `StringSpan` existed); [the offset map measurements](../verification/string-position-offsets-20260930.md) show the per-code-point scan it made slow. Prefer operations that consume the whole text in one pass (`StringCodePoints` with `ListFold`, `StringSplit`, `StringSplitLines`, `StringSpan`) when they fit.

`CapabilityInvoke` takes an ordinary receiver recipe, an existing `CapabilityOperationId`, and `(CapabilityParameterId, ExpressionRecipe)` bindings. The receiver must produce the operation's exact owning capability type; bindings must exactly cover the operation parameters with no duplicates and exact value types. The operation's effect must already be declared by the target. Materialization creates receiver nodes first, argument nodes in operation declaration order, and the invocation last. Its result type is the existing operation's `ResultType`; failure metadata is not reinterpreted.

All recursive receiver/argument expressions and every branch or Bind body are checked against the same target effect declaration. No effect is added or removed automatically, and simulation/publication do not execute the authored capability operations. Repeated effectful recipe occurrences are separate evaluations; references to a Bind's lexical value reuse its evaluated result.

Deterministic construction order is not a runtime sequencing guarantee. Independent runtime-observable nodes that graph dependencies do not order are `AmbiguousEffectOrder`: simulation refuses a proposal that adds, edits or newly makes such a function, by the same rule execution lowering applies (see [derived runtime ordering](../specs/runtime-ordering.md)), and lowering still rejects any such function inherited from an older snapshot. For example, an effectful receiver and an independent effectful argument do not become ordered just because the receiver was materialized first. Dependency-ordered chains and branch-local invocations retain their existing executable semantics; no hidden sequencing or node-list-order semantics are introduced.

`Bind(value, body)` explicitly evaluates its input once before its nested body, gives the body an immutable `Value` slot, and returns the body's result. It works for all supported value types, including `Unit` and capabilities; unused bindings retain initializer effects. Its binding is visible only in its body and nested scopes, not in its own initializer. Bind bodies consume one lexical scope depth just like match frames. The authoritative Bind node and system-owned `PatternBindingId` survive persistence and typed projection; existing Execution IR jumps and block parameters retain the order and provenance. Independent effectful siblings within either graph still reject.

Scoped control flow remains part of the same ephemeral construction model rather than exposing child graphs directly. `Conditional` requires a `Bool` condition and equal branch result types; `BoolAnd` and `BoolOr` require a `Bool` left operand and a right operand that produces `Bool` or aborts. `MatchUnion` is exhaustive: each arm names one variant or several sharing one body, and every declared variant is named by exactly one arm; it canonicalizes arms and field bindings to declaration order. `MatchOption` and `MatchResult` introduce typed branch bindings. `RecipePatternReference` addresses visible Bind-body and match-arm slots by lexical `ScopeDepth`; structurally empty frames such as `Option.None` still consume one scope depth. Branch recipes and the right operands of `BoolAnd`/`BoolOr` materialize as nested `ValueGraph`s, while production pattern identities and binding types remain system-owned and derived from validated semantic facts. Static effect checks cover every branch without hoisting its runtime effects into the parent graph.

The requested construction tree is ephemeral intent; the resulting `SemanticNodeKind` graph remains authoritative state. Review, receipt binding, stale rejection, readiness, publication, and history use the same structural transaction mechanics established in M2. `ResultingChange.AddedNodes` recursively includes branch-local nodes so the review covers the complete materialized result rather than only the parent graph.

See [Expression structural authoring](../specs/expression-authoring.md). The old `PureExpressionRecipe` and `AgentPureExpression...` F# aliases were removed on 2026-10-18; they were never an independent pure-only interface. Existing diagnostic codes and historical evaluation protocol names are retained.

### M1 operation workflow

`AgentWorkflow` / `AgentOperationIntent` supports:

- display-name rename for types, functions, fields, Union variants, function parameters, Effects, Capabilities, Capability operations and their parameters, unique within the renamed member's scope; IDs and references are unchanged;
- adding a function parameter with an explicit plan for every affected direct call;
- resolving a generated missing-argument obligation with an existing compatible value;
- explicitly adding or removing an existing `EffectId` from a function's `RequiredEffects`;
- adding a Product field with an explicit existing typed value for **every** affected `ProductConstruct`, including nested branches; the new `FieldId` is allocated by the system while existing type, function and node IDs remain stable;
- removing a Product field and its constructor bindings when all field accesses and `ProductUpdate` bindings of it have already been repaired; adding a field needs no plan for a `ProductUpdate`, which keeps it; affected functions must declare no required effects, so removal cannot silently drop effectful evaluation;
- removing a function parameter when its body no longer references it and every use of the function is a direct call: each call loses its argument binding and every obligation for the parameter is retired; callers that would lose a binding must declare no required effects;
- removing a Union variant that nothing constructs and that is not the last one: match arms drop it, an arm left with no variant is removed with its body, and functions whose arm would be removed must declare no required effects;
- deleting an unused function only after all direct calls, function-value references and List callbacks from other functions are gone, and when no obligation targets its body; the function's references to itself (self-recursion) are removed with it and do not count;
- deleting an unreferenced Product or Union declaration, including its field/variant identities; dependent functions can be removed in the **same** reviewed transaction, but any surviving references (including nested type references) prevent publication;
- deleting an unreferenced Capability and Effect declaration after consumers have been repaired or removed; one reviewed batch may remove the dependent function, Capability, then Effect in that order, and the diff retains removed operation/parameter identities;
- simulation, Change Review, reviewed publication or rejection, and stale-proposal failure.

Field-addition plans use `(constructNodeId, valueNodeId)` pairs for existing values, not arbitrary graph payloads or an implicit default. No partial publication is allowed: missing/duplicate plans, wrong types, or out-of-scope references reject. Function deletion likewise does not silently rewrite callers. The review diff lists added/removed fields, affected constructor identities, and deleted function/node identities. This is a bounded T1 change, not general Product field migration or deletion of referenced declarations.

Required-effect edits do not propagate automatically through callers and do not create obligations. Multiple explicit function edits may be composed in one transaction; full validation of the final proposed snapshot enforces caller/callee and capability/body effect consistency. Expression authoring still never mutates `RequiredEffects` implicitly.

### Union variant evolution

Two reviewed edits add one new variant to an existing Union while preserving the Union's `TypeId`; in both the new `VariantId` and `FieldId`s are system-owned, and the variant addition and every match repair are reviewed and published as one snapshot, so no intermediate non-exhaustive state becomes authoritative.

The session operation `simulateAddUnionVariantLike` (and `…ByName`) copies an existing source variant: the caller names the target Union, the source variant (fieldless or field-bearing) and the new display name, and may add `additionalFields`; the new variant copies the source variant's fields, followed by the additional ones. Publication repairs exhaustive `MatchUnion` nodes by cloning: each arm for the source variant is cloned for the new variant with fresh system-owned graph-node and pattern-binding IDs and its field bindings remapped (additional fields are bound but unused); an arm that names the source variant together with others gains the new variant and keeps its body. Arm bodies can then be refined with targeted replacement. The workflow is `AgentUnionVariantEvolutionWorkflow` (named `AgentFieldlessUnionVariantEvolutionWorkflow` until 2026-10-18).

The `addUnionVariant` operation of `simulateOperations` (and `…ByName`) adds a variant with arbitrary fields and takes the new arm bodies as recipes: one `{ matchNodeId, body }` arm plan per `MatchUnion` over the Union, rejected as incomplete or stray like `addProductField`'s construction plans (`UnionMatchArmPlanMismatch`). Each body is an ordinary `ExpressionRecipe` evaluated in that arm's scope, with the new variant's fields as the arm's own pattern slots (depth 0) and the match's enclosing bindings beyond, bound against the snapshot with the new variant already present; it must produce the match's type. A generic Union's new field types may name the Union's own type parameters, and each arm binds them at the match input's type arguments. The operation composes with the other operations of a batch. `AgentUnionVariantArmPlans` prepares the plans (allocating the IDs and materializing the recipes) and `Transaction.addUnionVariant` applies them, so the operation carries only materialized graphs and re-simulates identically at commit. The [session contract](../specs/agent-authoring-session.md) has both request and review shapes; the [decision](../decisions/authoring-surface-queries-and-parity.md) records why arm recipes were chosen over missing-arm obligations or a default arm.

Neither edit is a generalized schema-migration framework. Variant removal is the separate reviewed edit `removeUnionVariant`.

### Removed Float64 and ListMap compatibility surfaces

The narrower Float64-only body-replacement workflow (`AgentStructuralWorkflow`, the [bounded structural editing](../specs/bounded-structural-editing.md) contract) and the specialized shallow `ParameterReference -> ListMap` workflow (`AgentListMapStructuralWorkflow`) lived in the evaluation runners `Nyrwvy.AgentEval.Structural` and `Nyrwvy.AgentEval.ListMapStructural`, not in `Nyrwvy.Semantics`. Those runners were removed from `main` on 2026-10-18; the surfaces and their frozen M2 evidence remain reproducible from the [evaluation freeze tags](../operations/evaluation-freeze-tags.md) and from commit `b3a958a`. The general `ExpressionRecipe` covers the same semantic capability, for example Float64 arithmetic and `ListMap(Parameter(sourceParameterId), mapperFunctionId)`, through the maintained authoring session.

### Current boundary

The reusable `ExpressionRecipe` covers primitive scalar expressions, the implemented List/Map/Set families, product/field/union construction, `Option`/`Result` construction, complete direct calls and capability invocations within predeclared effects, explicit scoped sequencing/value reuse through `Bind`, and scoped conditionals/matches with recipe-local lexical pattern references. It does **not** author incomplete calls, effect/capability/operation declarations, or arbitrary references to outer graph nodes. Targeted replacement is a separate maintained workflow: it accepts an inspected existing node ID and constructs its replacement from a recipe, including inside nested branches and matches, while validating owner and lexical scope. `ExpressionRecipe` never changes the target function's effect declaration implicitly; `AgentWorkflow` separately supports explicit add/remove edits using already-existing `EffectId`s. Effect and Capability declarations are authored through their dedicated reviewed session operations.

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
- effectful callbacks other than `ListEffectFold`'s folder;
- hidden backend exception/default semantics;
- exact bit domains beyond [`Bits32`](#bits32) and the two's-complement `Int64` bitwise operations: other widths, byte strings and generic bit vectors.

`Bits32` is the one implemented exact-bit domain, separate from `Int64` and `Float64` numeric semantics (the `Int64` bitwise operations act on the numeric value's two's-complement form); the broader direction remains an [experiment](../experiments/bit-representation.md).


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

## Scoped one-shot values

`OneShot<T>` delivers one value between tasks owned by an existing TaskScope. Create allocates an empty cell, Complete accepts the first publication while the owner is open, and Wait observes its value as Option<T>. A timeout or waiter cancellation leaves other users unaffected; owner closure closes unresolved cells before cancelling and joining tasks. Completed values remain readable after closure. These operations are effectful, and cells cannot cross capability or runPure value boundaries. See the [contract](../specs/scoped-one-shot.md) and [runnable example](../../examples/scoped-result-handoff/README.md).

## Explicit host exception boundary

`ExceptionCatch` runs a body callback and, on a host `System.Exception`, a handler callback that receives the opaque builtin `Exception`; both return the same `Result<T, E>`. `ExceptionMessage` reads the message, `ExceptionIsCancellation` is a pure `Exception -> Bool` classifier for `OperationCanceledException`, and `ExceptionRethrow(value, resultType)` rethrows the original exception. `Exception` values do not cross Capability or `runPure` value boundaries. See the [contract](../specs/exception-boundary.md) and [runnable example](../../examples/exception-boundary/README.md).

## Lexical cancellation shield

`CancellationShield(body, context)` runs a `Unit -> Result<T, E>` body in the current task with a non-cancelled ambient token, so cleanup inside it does not observe parent cancellation; the original token is restored on every exit and cancellation stays requested afterwards. The node is effectful and requires `Bind` ordering. See the [contract](../specs/cancellation-shield.md) and [runnable example](../../examples/cleanup-shield/README.md).
