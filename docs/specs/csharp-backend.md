# C# backend specification

**Status:** Current

The C# backend is a downstream execution backend. It must preserve Nyrwvy semantic and Execution IR contracts without becoming an alternative source of program meaning.

## Input boundary

The backend consumes:

- backend-neutral `ExecutionProgram` data; and
- an explicit backend profile/binding configuration.

It must not consume authoritative semantic `Snapshot` state, JSON/Nyason persistence, raw semantic value graphs, or display names as semantic lookup keys.

The backend does not repeat semantic name resolution or type inference already completed upstream.

## Capability binding

External behavior is bound through stable capability-operation identity. A required operation without an active backend binding is a backend-readiness failure.

Bindings must not be inferred from display names. The profile supports `SystemConsoleWriteLine` for its existing narrow signature and `ExternalImplementation` for a host-supplied implementation of the **generated typed capability interface**. A host must explicitly bind every operation of an externally implemented capability (by `CapabilityOperationId`) and pass its implementation as the function's capability argument. The emitter generates the interface and symbol manifest, but **no adapter or implementation** for that capability. Mixed Console/external bindings on one capability are rejected; unbound operations remain backend-readiness failures. `emitCSharp` accepts `{ "kind": "externalImplementations", "operationIds": ["<stable ID>", ...] }` as an explicit profile. This profile does not load or execute arbitrary source text.

Shared capability use by `ParallelAll` additionally requires the profile's explicit
`ConcurrentCapabilityIds` assertion (wire: `concurrentCapabilityIds` on an
`externalImplementations` profile). Missing support is diagnosed before emission,
including transitive worker calls and resource releases. No implicit serialization
adapter is generated. See the [sharing contract](parallel-capability-sharing.md).

## Generated identifiers

Generated C# identifiers are deterministic backend artifacts, not semantic identity. Their construction must be injective for the identities they encode and must not depend on mutable display names.

## Runtime representation

Backend representation may vary while preserving the upstream semantic contract. The current backend uses .NET-native or generated support types approximately as follows:

- `String` -> `NyStr`, a sealed class emitted with the runtime that converts implicitly to and from `string` (see below);
- `Bool` -> `bool`;
- `Unit` -> generated unit support;
- `Int64` -> 64-bit signed integer representation with operation behavior fixed by semantic node kind;
- `Float64` -> `double`, with literal construction from exact authoritative bits;
- `Bits32` -> `uint`;
- `Option<T>` / `Result<T,E>` -> explicit generated value representations rather than implicit null/exception semantics;
- products/unions -> generated deterministic .NET types;
- `List<T>` -> backend-local representation that preserves semantic ordering and the accepted immutable-value contract;
- capabilities -> generated interfaces, with adapters only for supported backend-provided bindings.

The concrete .NET storage class is not a language-semantic fact. The generated interface uses these same types for parameters and results, including products and `Result<T,E>`. A host implementation must honor the typed/non-null value contract. Expected domain failure can be returned as `Result.Err`; it is not silently inferred from a thrown host exception. `FailureType` metadata remains unsupported by this backend even for external implementations: it is not an exception-conversion policy, and the backend reports `UnsupportedCapabilityFailure` rather than dropping it. Runtime injection failures and unexpected host exceptions remain host/infrastructure failures, not program `Result` values.

## String equality

`StringEqual` emits exact ordinal, case-sensitive .NET string comparison (`StringComparison.Ordinal`). It must not use culture-sensitive comparison, case folding, or a two-way substring check. `StringContains` is a substring test, not equality; it calls `NyText.Contains` and shares the boundary rule of the search operations below. `StringConcat` emits `NyStr.Concat(left, right)` without inserting separators, formatting values, or normalizing line endings/Unicode. Both input types and the output type are `String`. `Int64ToString` emits `ToString(CultureInfo.InvariantCulture)`. A generic function becomes a generic method named after its stable type parameter identities (`NyF_…<T_…>`). Calls rely on C# inference from the typed arguments, and generic callbacks are emitted as lambdas rather than method groups so that their type arguments can be inferred. `ParallelAll` emits one block: it starts a `Task` per item with a shared `CancellationTokenSource` stored in the ambient `NyRuntime.Cancellation` (`AsyncLocal`), cancels on a worker `Err` or exception, waits for every task, and then rethrows the first faulted task by index, returns the first `Err` by index, or assembles the `Ok` list in order. The `NyRuntime` class (`Cancellation`, `CancellationRequested`, `WaitForCancellation()`, and the `Run`/`WaitAll`/`Delay` helpers every concurrent start and wait goes through) and `NyVirtualClock` are emitted only for programs that use `ParallelAll`, `CancellationRequested` or task scopes, so other generated sources are unchanged; external implementations may read `NyRuntime.Cancellation` to honor cancellation. `NyRuntime.Clock` is null by default, and the helpers then use real threads and time. A deterministic runner such as `runPure` sets it to a `NyVirtualClock` before calling the entry function. `TaskScope` emits a `NyTaskScope` linked to the ambient token, calls the body in a `try` whose `catch` closes the scope and rethrows, and otherwise closes it after the body returns. Closing cancels the scope's token, waits for every task, and rethrows the first faulted task in start order. `TaskStart` emits `scope.Start(() => worker(...))`, which runs the worker on `Task.Run` with its own `CancellationTokenSource` linked to the scope's and stored in `NyRuntime.Cancellation`. `TaskWait` emits `NyTasks.Wait(tasks, any, limit)`, where an absent limit is `NyOption<long>.None()`. With real time it calls `Task.WaitAny`/`Task.WaitAll` with the limit as a millisecond timeout (negative as 0, above `int.MaxValue` clamped), whose timer is monotonic; under a virtual clock it blocks on the clock. A virtual deadline is the limit or delay added to the current virtual time, saturating at `long.MaxValue`, so a limit too large to represent is one that never elapses rather than one already past, and virtual time never wraps negative. It then snapshots each handle. `TaskCancel` cancels the handle's token source. `INyTask`, `NyTask<T>`, `NyTaskScope` and `NyTasks` are emitted, with `NyRuntime`, only for programs whose functions, instructions or declared fields mention the task types. A declared `Task<T>` field is a `NyTask<T>` record member, held by reference. `UseResource` emits one block: it returns an acquisition `Err` directly; otherwise it calls the body in a `try` whose `catch` attempts release, suppressing only a release exception, and rethrows. Then it releases and combines results, with the body `Err` first and a release `Err` second. `ListEffectFold` emits `Fold` with the folder called by parameter name. In short-circuit mode (result type `Result<S, E>` for seed `S`) it folds over `NyResult<S, E>.Ok(seed)` and, once the state is `Err`, returns it without calling the folder again. `StringParseInt64` first checks that the text is an optional `-` followed by ASCII digits, then uses `long.TryParse` with `NumberStyles.AllowLeadingSign` and the invariant culture only to enforce the range, producing `NyOption<long>` without exceptions. `StringLength` and `StringSlice` walk the UTF-16 text once, treating a high surrogate followed by a low surrogate as one code point and any other code unit as one; `StringSlice` clamps both bounds with `Math.Max(0, …)` and the text end, then emits `Substring` over the mapped offsets, or `""` when the end offset is not after the start.

## String ordering

`StringLessThan` emits `NyText.LessThan(left, right)`, which walks both strings decoding code points (a valid surrogate pair to its scalar value with `char.ConvertToUtf32`, any other code unit as itself) and compares the first differing code point, a proper prefix sorting first. It must not use `string.CompareOrdinal` or `StringComparison.Ordinal` ordering, which compare UTF-16 code units and put U+10000 and above before U+E000..U+FFFF, and it must not use culture-sensitive comparison.

## String code points

`StringCodePoints` emits one pass over the UTF-16 string: a high surrogate followed by a low surrogate is taken as one two-unit item, and any other code unit, including an unpaired surrogate, as a one-unit item, exactly as `StringLength` counts. The items are collected into a `List<NyStr>` presized to the string length and wrapped with `NyList<NyStr>.FromOwnedArray`, so the whole operation is linear and cannot fail.

## String search

`StringContains`, `StringIndexOf`, `StringLastIndexOf`, `StringStartsWith`, `StringEndsWith` and `StringSplit` call `NyText` helpers emitted with the runtime. They search with ordinal `string.IndexOf`, `LastIndexOf`, `StartsWith` and `EndsWith` and accept a match only when both of its UTF-16 ends are code-point boundaries (not between a high surrogate and the low surrogate after it); a rejected match resumes the search one code unit further. `StringIndexOf` first walks the text to map the clamped `from` to a UTF-16 offset, and both index operations convert the matched offset back to code points in one pass, producing `NyOption<long>`. `StringSplit` collects the pieces into a doubling array wrapped with `NyList<NyStr>.FromOwnedArray`, and returns a one-item list for an empty separator. None of them throws.

## String spans

`StringSpan` and `StringSpanBack` call the emitted `NyText.Span(text, set, from)` and `NyText.SpanBack(text, set, end)`. Each first walks the text once to map the clamped bound to a UTF-16 offset, returns that position for an empty set, and otherwise collects the set's code points (a surrogate pair as its scalar value, an unpaired surrogate as itself) into a `HashSet<int>`. `Span` then advances one code point at a time while it is a member; `SpanBack` steps back one code point at a time, treating a low surrogate directly after a high surrogate as one pair, so the boundaries are the ones a forward walk finds. Both are linear and never throw.

## String split lines

`StringSplitLines` calls the emitted `NyText.SplitLines(text, breaks)`, which returns the same `NyList<NyStr>` as `NyText.Split`. It returns the empty list for empty text and a one-item list for an empty `breaks`; otherwise it collects the break code points into a `HashSet<int>` as `Span` does, walks the text one code point at a time, and at each member cuts the line before it, skipping a directly following `\n` after `\r` when both are members. Lines go into a doubling array wrapped with `NyList<NyStr>.FromOwnedArray`, and the text after the last break is added only when non-empty. It is linear and never throws.

## String replace and trim

`StringReplace` calls the emitted `NyText.Replace(text, pattern, replacement)`. It returns `text` itself for an empty pattern or when there is no match; otherwise it appends the text between the matches that `NyText.Split` would cut at, and the replacement for each, to one `StringBuilder`, so a match never splits a surrogate pair and the replacement is never scanned again. `StringTrim`, `StringTrimStart` and `StringTrimEnd` call `NyText.Trim`, `NyText.TrimStart` and `NyText.TrimEnd`, which skip UTF-16 code units with `char.IsWhiteSpace` from the chosen ends and return `text` itself when nothing is removed. Every White_Space code point is in the BMP and no surrogate unit is white space, so the walk by code unit never splits a pair.

## String lowercase and UTF-8 length

`StringToLower` calls the emitted `NyText.ToLower`, one pass over the UTF-16 string: a surrogate pair is mapped with `System.Text.Rune.ToLowerInvariant`, an unpaired surrogate is copied unchanged, U+0130 becomes `i` (the UnicodeData simple mapping, which .NET's invariant casing does not apply), and every other code unit is mapped with `char.ToLowerInvariant`. `string.ToLowerInvariant` must not be used: it replaces an unpaired surrogate with U+FFFD. `StringToUpper` calls `NyText.ToUpper`, the same pass with `Rune.ToUpperInvariant` and `char.ToUpperInvariant`, where U+0131 becomes `I` and U+00DF becomes U+1E9E explicitly. `StringUtf8ByteCount` calls `NyText.Utf8ByteCount`, which is `Encoding.UTF8.GetByteCount`; its replacement fallback counts an unpaired surrogate as the 3 bytes of U+FFFD. `StringDecodeUtf8Lossy` calls `NyText.DecodeUtf8Lossy`, which writes each code point of the input as one byte (a surrogate pair is one code point; a code point above U+00FF becomes 0xFF) and decodes them with `Encoding.UTF8.GetString`, whose replacement fallback writes one U+FFFD per maximal ill-formed subpart, as Python does.

## String code point conversion

`StringFromCodePoint` calls the emitted `NyText.FromCodePoint`, which returns `NyOption<NyStr>.None()` outside 0..0x10FFFF, casts a code below 0x10000 to one `char` (so a surrogate code gives one unpaired surrogate code unit) and uses `char.ConvertFromUtf32` only above U+FFFF; `char.ConvertFromUtf32` must not be used for the whole range, since it throws for a surrogate code. `StringToCodePoint` calls `NyText.ToCodePoint`, which returns the code unit of a one-unit string, `char.ConvertToUtf32` of a two-unit string that is a valid surrogate pair, and `NyOption<long>.None()` otherwise. Both are constant time.

## Map

`Map<K, V>` is the emitted `NyMap<K, V>`, a persistent map, so an old map stays valid after an insert. It holds an `ImmutableDictionary<K, (long Order, V Value)>` under `EqualityComparer<K>.Default`, which is structural for every equatable type (see [value equality](#value-equality)). It also holds an `ImmutableSortedDictionary<long, K>` from insertion number to key, and the next insertion number. `Insert` keeps an existing key's number and gives a new key the next one. `Remove` drops both entries. `Keys`, `Values` and `Fold` walk the sorted numbers, so the visible order is insertion order and never depends on hash codes. Lookup, insert and remove are O(log n).

`Set<T>` is the emitted `NySet<T>`: an `ImmutableDictionary<T, long>` from item to insertion number and an `ImmutableSortedDictionary<long, T>` for the order, with the same structural equality and the same guarantee that hash codes never affect the visible order. `Union` inserts the right items into the left set; `Intersection` and `Difference` remove items from the left set, so the kept items keep their order. When the right set is smaller, `Difference` removes the right set's items one by one instead of testing every left item, so its cost follows the smaller set; removal keeps the other items' insertion numbers, so the result is the same. `IsSubsetOf` answers false at once when the left set is larger and otherwise looks up each left item in the right set until the first miss. `NyMap<K, V>.Union` inserts the right map's entries into the left map in the right map's order, so a shared key keeps its left position and takes the right value; it returns the other map unchanged when one side is empty, and never inserts the left map into a larger right map, which would change the order. `NyMap<K, V>.Filter` likewise removes the rejected entries. `NyMap<K, V>.MapValues` builds a map with the same keys and insertion numbers and the mapped values, so the order is unchanged, and `NyMap<K, V>.GetOrDefault` returns the stored value or the already evaluated default.

## List sorting

`ListSortBy` emits `source.SortBy(lessThan)`, with the callback rendered like other List callbacks (a method group, or a lambda passing context and the two items by parameter name). `NyList<T>.SortBy` copies the items and runs the specified top-down merge sort: the left half is the first `length / 2` items, and the merge takes the right head only when `lessThan(rightHead, leftHead)` is true, so equal items keep their order. It does not use `Array.Sort` or `List<T>.Sort`, which are unstable introsorts whose result for an inconsistent comparer is unspecified and which may throw for one.

`ListMinBy` emits `source.MinBy(lessThan)`. `NyList<T>.MinBy` returns `None` for the empty list; otherwise it keeps the first item and scans the rest left to right, replacing the kept item only when `lessThan(item, kept)` is true, so the first of equal items wins.

`ListFindIndex` emits `source.FindIndex(predicate)`, `ListIndexOf` emits `list.IndexOf(item)` and `ListLastIndexOf` emits `list.LastIndexOf(item)`, which scans from the last item backwards. Both scan left to right and return `NyOption<long>.Some` of the first matching position; `IndexOf` compares with `NyEquality.Equal`, as `ValueEqual` does.

## Value equality

`ProductUpdate` emits a C# `with` expression on the source record, `source with { F_a = …, F_b = … }`, assigning the updated fields in stable field-ID order; every operand is already a computed value, so the order is not observable. Kept fields are copied by the record's generated copy, so a reference-held field such as a task handle or a list is shared, not copied.

`ValueEqual` emits `NyEquality.Equal(left, right)`, which calls `EqualityComparer<T>.Default.Equals`. Every value type an equatable Nyrwvy type maps to compares structurally under it: `NyStr` is ordinal (it implements `IEquatable<NyStr>` over its UTF-16 code units, with a matching `GetHashCode`), `long`, `bool` and `NyUnit` compare by value, `NyOption<T>` and `NyResult<TOk, TError>` are record structs whose generated equality compares every field, declared Products and Union variants are sealed records whose generated equality compares the runtime type (so different variants differ) and every field, and `NyList<T>` implements `IEquatable<NyList<T>>` by length and then item by item with the item type's default comparer (with a matching `GetHashCode`). Float64, delegates, task handles and capabilities never reach it, because validation rejects those types.

## Int64 floor division

`Int64FloorDivide` and `Int64FloorModulo` emit an explicit block rather than C# `/` and `%`, which truncate toward zero and throw on a zero divisor and on `long.MinValue / -1`. The block returns quotient `0` and remainder `left` for divisor `0`, the `unchecked` negation of `left` and remainder `0` for divisor `-1`, and otherwise adjusts the truncated result: the quotient decreases by one and the remainder adds the divisor when the remainder is nonzero and its sign differs from the divisor's. The generated code therefore never throws and matches the floor semantics in the [language subset](../reference/language-subset.md#int64).

`Int64BitAnd`, `Int64BitOr`, `Int64BitXor` and `Int64BitNot` emit `&`, `|`, `^` and `~` on `long`. C# masks a `long` shift count to its low 6 bits, so the shifts clamp the count explicitly and shift only by 1 to 63: `Int64ShiftLeft` emits `count <= 0L ? value : count >= 64L ? 0L : value << (int)count`, and `Int64ShiftRight` emits `count <= 0L ? value : count >= 64L ? (value < 0L ? -1L : 0L) : value >> (int)count`, whose `>>` on `long` is arithmetic.

`ListItemAt` emits `list.ItemAt(index)`, which returns `NyOption<T>.Some(item)` when `0 <= index < Length` and `NyOption<T>.None()` otherwise, without exceptions.

`ListSlice` emits `list.Slice(start, end)`. `NyList<T>.Slice` clamps both bounds with `Math.Clamp(bound, 0, Length)`, returns the empty list when `end <= start`, the list itself when the slice covers it (lists are immutable), and otherwise a list that shares the persistent vector's nodes (below). `StringJoin` emits `NyText.Join(list, separator)`, which sums the lengths, copies each item and separator once into one buffer of that size, and makes the result string from it; neither throws.

`ListLast` emits `list.Last()`, which reads the item before the list's count. `ListDropLast` emits `list.DropLast()`, which returns a list with a count one smaller over the same nodes (the empty list for one item or none). Appending to that list then loses the compare-exchange below, because the tail's used count is still the longer list's, and copies the tail, at most 32 items; no slot is written twice.

A `NyList<T>` is a persistent vector, as Clojure's vectors are: a 32-way trie of full 32-item leaves holds the first items, a multiple of 32, and a tail holds the last 1 to 32. A new list shares every node of the old one and copies only the nodes on one path, so `ListAppendItem`, `ListDropLast`, `ListLast`, `ListItemAt` and `ListSlice` take O(log₃₂ n), at most 4 levels for a million items, and never copy the list (except a slice that would keep too little of it, below); a push after a pop copies at most the tail. A list is the trie positions from a start to an end: a slice moves both, so dequeuing with `ListSlice(queue, 1, length)` copies nothing. A slice from a start other than the list's own that would keep fewer than half of the positions up to its end copies its items instead, which bounds what a small slice keeps alive and keeps repeated dequeues amortised O(log₃₂ n) each ([decision](../decisions/list-slice-sharing.md)). `ListSortBy` is unchanged (n log n); `ListConcat` (which appends the right list's items one by one), `ListReverse`, `ListMap` and `ListFilter` are linear in the items they read or produce. `ListAppendItem` writes into the tail's spare slot when the list ends exactly at the tail's used count, taking the slot with an atomic compare-exchange of that count; otherwise it copies the tail. Pushing a full tail into the trie copies the nodes on its path, so a trie node is never written after a list containing it exists; `ListDropLast` and `ListSlice` may leave nodes before the start or past the end, which no reader looks at. Appending to a list repeatedly is therefore O(1) amortised per item, and appending to an older list, or to the same list twice, copies at most a tail and never changes a list already made. Lists stay immutable when tasks and `ParallelAll` workers share them.

A `String` is a `NyStr`: either a .NET `string`, or the first `length` UTF-16 units of a shared char store with a used count, the scheme `NyList<T>` used before it became a persistent vector ([decision](../decisions/persistent-vector-list.md)). `NyStr.Concat` returns the other operand when one is empty. When the left operand is store-backed and ends exactly at the store's used count, it takes the spare capacity with an atomic compare-exchange of that count and copies only the right operand; otherwise a result shorter than 256 units is a plain `string`, and a longer one gets a new store of twice its length. Concatenating onto the previous result, as a `ListFold` that builds a String does, is therefore amortised O(length of the right operand), and concatenating onto an older String, or twice onto the same one, copies and never changes a String already made. A store-backed `NyStr` makes its `string` once, on first use, and keeps it. `NyStr` converts implicitly from `string` and to `string`, and its `Equals`, `GetHashCode`, `==` and `!=` are ordinal over its UTF-16 units, so every other String operation works on the `string`. External capability implementations and `hostSources` see `NyStr` in the generated interfaces; a `string` literal or value can be passed wherever a `NyStr` is expected.

## Float64 literal emission

Float64 source construction uses exact bits rather than decimal source text, conceptually:

```text
BitConverter.UInt64BitsToDouble(0x<16 hex digits>UL)
```

This preserves signed zero and exact binary64 encodings and respects the semantic NaN canonicalization performed upstream.

`Bits32FromInt64` emits `unchecked((uint)value)` and `Bits32ToInt64` emits `(long)value`, so the low 32 bits are kept and read back unsigned. `Bits32Xor`, `Bits32And`, `Bits32Or` and `Bits32Not` emit `^`, `&`, `|` and `~` on `uint`, and `Bits32Add` emits `unchecked(left + right)`, which wraps modulo 2^32 in a checked context too. `Bits32RotateRight` emits `System.Numerics.BitOperations.RotateRight(value, (int)(amount & 31L))`: `amount & 31` is the amount floor-modulo 32 for every Int64, negative ones included. The shifts emit `amount <= 0L ? value : amount >= 32L ? 0u : value >> (int)amount` (and `<<`), because C# masks a `uint` shift count to its low five bits, which would make a shift by 32 a no-op; the explicit bounds keep the language subset's total meaning.

`Int64ToFloat64` emits `(double)value`.
 .NET converts `long` to `double` with IEEE 754 round to nearest, ties to even, which is the conversion the language subset defines; the backend must not route it through decimal text.

`Float64RoundToInt64` calls `NyNumber.RoundToInt64`, `System.Math.Round` with `MidpointRounding.AwayFromZero` and a range check. `Float64Round` calls `NyNumber.Round`, which formats the absolute value with `"R"` (the shortest round-trip text), rounds its digit string half away from zero at the requested place, and reads the result back with `double.Parse`, so it never depends on culture or on the binary64 lying just below a written half. `Float64Sqrt`, `Float64Abs`, `Float64Floor`, `Float64Ceiling` and `Float64Truncate` call `System.Math.Sqrt`, `Abs`, `Floor`, `Ceiling` and `Truncate`, and `Float64Negate` is unary minus; each is the IEEE 754 operation with one result. `ListReverse` calls the emitted `NyList<T>.Reverse()`, which copies the items into a new store in reverse order and returns a list of 0 or 1 items itself. `ListRange` fills a new `long[]` with `start + i` and wraps it with `NyList<long>.FromOwnedArray`; it counts `end - start` as `ulong`, so the full Int64 span cannot overflow, and throws `OutOfMemoryException` when the count exceeds `Array.MaxLength`. `StringRepeat` uses `StringBuilder.Insert(0, text, count)` and throws `OutOfMemoryException` when `text.Length * count` exceeds `int.MaxValue`; both failures are host failures, like an allocation failure in `ListConcat`. `Int64ToHexString` formats the magnitude with `ToString("x", CultureInfo.InvariantCulture)`, taking the magnitude of a negative value as the unsigned negation so that `Int64.MinValue` gives `8000000000000000`, and prefixes `-`. `StringParseInt64Hex` checks an optional `-` and ASCII hex digits while accumulating the magnitude as `ulong`, stopping before it could overflow, then accepts at most `2^63 - 1`, or `2^63` when negative, without exceptions. `Int64Min` and `Int64Max` call `System.Math.Min` and `Max`; `Int64WrappingAbs` negates a negative value in an `unchecked` context. `Int64CheckedAdd`, `Int64CheckedSubtract` and `Int64CheckedMultiply` call the emitted `NyNumber.CheckedAdd`, `CheckedSubtract` and `CheckedMultiply`, which compute the exact result in `System.Int128` and return `None` when it is outside Int64, without `checked` contexts or exceptions. `Float64FloorToInt64` calls the emitted `NyNumber.FloorToInt64`, `Math.Floor(value)` with the same range check as `RoundToInt64`: both return `Some((long)result)` only when the rounded or floored result is in [-2^63, 2^63). NaN fails that comparison, so NaN, the infinities and out-of-range values give `None`; the `(long)` cast is never reached for them, because C# leaves it unspecified outside the range. `Float64ToString` calls the emitted `NyText.FromFloat64`, which returns `nan`, `inf`, `-inf`, `0.0` or `-0.0` for those values, and otherwise takes only the sign, digits and decimal exponent from `value.ToString("R", CultureInfo.InvariantCulture)` (the shortest round-trip digits in .NET Core 3.0+) and lays them out as Python `repr` does; .NET's own layout (`1E+16`, `100`, `1E-05`) must not leak. `StringParseFloat64` calls `NyText.ParseFloat64`, which checks the grammar by hand, because `double.Parse` with these styles also accepts `.5` and `5.`, and then calls `double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)`: correctly rounded for any digit count, a signed infinity on overflow and a signed zero on underflow, and saturating for an exponent of any length, all without throwing.

`Float64Min` and `Float64Max` emit `System.Math.Min` and `System.Math.Max` on `double`, which since .NET Core 3.0 are the IEEE 754-2019 `minimum` and `maximum`: NaN when either operand is NaN, and -0.0 below 0.0.

## Control flow

The emitter preserves Execution IR behavior rather than reconstructing high-level source structure or relying on C# surface inference.

Execution functions lower deterministically to generated methods/blocks, with explicit transfer of execution values and explicit branch/return behavior corresponding to the IR.

`BoolNot` emits `!operand`. `BoolAnd` and `BoolOr` reach the emitter as Execution IR branches, not as C# `&&`/`||`: the right operand's block runs only when the left operand does not decide the result, so short-circuit evaluation follows from the branch structure.

A self `DirectCall` in tail position reaches the emitter as the Execution IR `tailCall` terminator, not as a call ([decision](../decisions/tail-self-calls.md)). Each generated method copies its C# parameters into value locals once, in its prologue, and then jumps to the entry block; every parameter reference reads those locals. `tailCall` assigns the locals from its arguments in two phases, every argument into a temporary first and then every temporary into its local, so arguments that read other parameters (`f(a: b, b: a)`) see the values of the current turn, and then jumps back to the entry block's label. The prologue is not rerun, the method keeps its C# signature, and no stack frame is added, so a self-recursive loop runs in constant stack. Lowering places the tail call last in its block, after every node of its graph that does not feed it; effect-order validation already orders every observable node against an effectful call, so effect and abort order are unchanged. A structured node whose arms all abort or end in a tail call has no merge block. Because a loop reuses its value locals, a callback that runs after its instruction must not capture them: `TaskStart` copies its context values and item into locals of a C# block, which are fresh variables each time the block is entered, before its worker lambda captures them. The other callbacks run before their instruction completes. Execution IR that contains a `tailCall` serializes as schema 9; other programs keep schema 6, 7 or 8 by the same rule as before.

## Determinism

For identical supported Execution IR and backend profile, generated C# must be deterministic. Declaration order, symbol allocation, control-flow emission, and runtime support generation must not depend on hash-map traversal or unrelated environment state.

Generated source uses canonical LF (`\n`) line endings, including its final newline, on every host operating system. String-literal contents are not normalized: CR, LF, CRLF, and other characters retain their exact values through C# literal escaping.

## Authority boundary

Generated C# is a build artifact.

- it is not an editable upstream source;
- it does not define semantic identity;
- it does not define Nyrwvy numeric or collection semantics;
- it does not become a persistence format;
- backend limitations do not justify silently changing the semantic graph.

If a semantically valid program cannot be represented or bound by the selected backend, return a backend-readiness/emission diagnostic. See the [failure and obligation policy](failure-obligation-policy.md).

## Historical design evidence

The original Milestone 0 backend design gate and implementation chronology remain in the [pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/csharp-backend-design-20260825.md). This maintained specification intentionally excludes historical CI state and milestone-specific verification instructions.

## Pure function values

Function types lower to invariant `NyFunctionN<..., TResult>` delegates, emitted
only for the arities used by the program. Named monomorphic references lower to
method groups; invocation calls the typed delegate. References remain in entry
reachability, so selecting one entry retains the referenced implementation and
its transitive dependencies. These representations implement the
[pure function-value contract](pure-function-values.md); delegate identity and
captures are not new semantic capabilities.

## Program-defined capabilities

[CapabilityConstruct](program-capability-implementations.md) emits a stateless
class implementing the existing capability interface. Its methods call the
mapped generated Nyrwvy functions with positional semantic correspondence
expressed as named C# arguments. The profile's existing interface binding and
parallel-sharing checks remain in force; internally constructed values require
no host implementation source in the common runner. Typed Semantic IR schema 7
and Execution IR schema 6 carry the operation/function mapping.

## Typed function abort

The backend consumes the explicit Execution IR `abort` terminator (schema 7)
and emits a local typed Err return. Structured branches with no normal completion
do not create merge values. No host exception or hidden default value represents
an abort. Existing UseResource and ParallelAll cleanup/failure policies apply at
the callee/worker Result boundary. See [typed function abort](typed-function-abort.md).
Programs without an abort terminator retain Execution IR serialization schema 6.

`ExceptionIsCancellation` emits `edi.SourceException is System.OperationCanceledException`
with a Bool result. It includes subclasses without inspecting message text,
ambient or exception token state, type names, reflection or InnerException.
`ExceptionCatch` still captures every System.Exception using ExceptionDispatchInfo;
selective recovery is ordinary graph composition with Conditional and ExceptionRethrow.
