# Amortised List append — 2026-09-30

**Status:** Implemented and verified. Baseline: `94a8447`.

[Probe 9](rrawrlet-slice-probe9-20260930.md) found that text built one item at a time is quadratic: a 64K-character line ran, a 128K one exceeded the 5 s `runPure` limit. One cause was the emitted `NyList<T>`, a bare `T[]` that `AppendItem` and `Concat` copied in full on every call, so a `ListFold` that appends each step was O(n²), and "append, then `StringJoin`" was not a linear builder.

`NyList<T>` is now a shared store (an array and a used count) plus the list's own count, as a Go slice is:

- `AppendItem` writes into the store's spare capacity when the list ends exactly at the used count. It takes the slot with `Interlocked.CompareExchange` of the used count from the list's count; otherwise it copies into a new store with doubled capacity (at least 4).
- `Concat` does the same for the whole right-hand list. It returns the other list unchanged when one side is empty.
- Every reader (`Length`, `ItemAt`, `Slice`, `Fold`, `Map`, `Filter`, `SortBy`, `Any`, `All`, `Equals`, `GetHashCode`, `NyText.Join`) looks only at the first `count` items. `Filter` now wraps its buffer instead of copying it again. `Empty()` returns one shared empty list.

Values stay immutable. The used count only grows, so a slot is written once, by the one caller whose list ended at that count, and before any list containing the slot exists. A list never reads past its own count. Appending to an older list, or twice to the same list, loses the compare-exchange and copies. Lists reach other threads through task starts and results (`ParallelAll`, `TaskStart`, `TaskWait`), which order the slot writes before the reads. Two threads appending to one list race only on the compare-exchange; the loser copies.

## Measurements

These were measured on one Linux machine through `runPure`, on the program `StringEqual(rebuild(t), t)`, where `t` is 16 copies of an argument chunk joined in the program (arguments are limited to 64K UTF-16 units) and `rebuild` is `StringJoin(ListFold(StringCodePoints(t), ListEmpty, push), "")` with `push = ListAppendItem(acc, item)`. Each time is one `runPure` call, including its Roslyn build of about 180 ms (a 16-character run took 170–220 ms).

| Code points appended | Before | After |
| --- | --- | --- |
| 16,384 | 348 ms | 267 ms |
| 32,768 | 956 ms | 237 ms |
| 65,536 | 4,471 ms | 238 ms |
| 131,072 | exceeded the 5 s limit | 249 ms |
| 1,048,576 | exceeded the 5 s limit | 397 ms |

Before, the time grew fourfold per doubling; after, 1M appends take about 0.2 s beyond the build. Repeated `StringConcat` still copies its left operand each time and stays quadratic; append to a List and join once instead.

## Verification

- `ListAppendTests` (new) compiles the emitted runtime with a C# driver and checks that two appends to one base give independent lists, that appending to an older list after a newer append does not change the newer one, equality and hash against `Create`, slicing then appending, `Concat` of a list with itself, `Length`/`ItemAt`/`Fold`/`Map`/`Filter` bounded by the count, and 200 rounds of 32 `Parallel.For` workers extending one shared list with `AppendItem` and `Concat`. An Extended test authors the same through the session: branches of one base via `runPure`, and 262,144 appends of astral and BMP code points joined back to the original text.
- `ListTests` checks the new fields in the emitted source.
- `./scripts/test-fast.fish`: 600 Core tests and 29 adapter tests passed.
- `./scripts/test-extended.fish`: 31 Extended and 73 extended Core tests passed.

## Not done

- `Slice` still copies (except for the whole list), so a prefix does not keep a large store alive.
- A store kept only by a short list may hold up to twice its items' memory, as any doubling buffer does.
