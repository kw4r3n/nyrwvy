# Amortised String concatenation — 2026-10-01

**Status:** Implemented and verified. Baseline: `7277ecd`. Roadmap: order 31, from [probe 14](rrawrlet-slice-probe14-20261001.md)'s cost #1.

[Probe 14](rrawrlet-slice-probe14-20261001.md) wrote `bytes.decode("utf-8", "replace")` by hand. Its first draft built the result with one `StringConcat` per character, which the language subset documented as quadratic, and it exceeded the 5 s `runPure` limit at 195 KB. The list-and-join form was linear but took 12 more requests. [Amortised List append](list-amortised-append-20260930.md) had already made the list form linear; this change does the same for `StringConcat`, so the natural form is linear too.

The emitted C# now maps `String` to `NyStr`, a sealed class emitted with the runtime:

- A `NyStr` is either a .NET `string`, or the first `length` UTF-16 units of a shared char store (an array and a used count), in the same scheme as `NyList<T>`.
- `NyStr.Concat(left, right)` returns the other operand when one is empty. When `left` is store-backed and ends exactly at the used count, it takes the spare capacity with `Interlocked.CompareExchange` of the used count and copies only `right`. Otherwise a result shorter than 256 units is a plain `string`, and a longer one is copied into a new store with twice its length.
- A store-backed `NyStr` makes its `string` on first use and keeps it. Every operation other than `Concat`, `Join`, `Length` and equality works on that `string`, so their code is unchanged apart from types.
- `NyStr` converts implicitly from and to `string`. `Equals`, `GetHashCode`, `==` and `!=` are ordinal over the UTF-16 units, so `NyEquality`, `NyList<NyStr>` equality and Products with String fields compare as before.
- `NyText.Join` sums the item lengths and copies each item once, without making a `string` for a store-backed item.

Values stay immutable, for the same reasons as lists: the used count only grows, a char is written once, by the one caller whose String ended at that count, and before any String containing it exists, and a String never reads past its own length. Concatenating onto an older String, or twice onto the same String, loses the compare-exchange and copies.

**Interface change.** The spec already said that the concrete .NET storage class is not a language fact, but the generated capability interfaces are public: a `String` parameter or result is now `NyStr` rather than `string`. External implementations and `hostSources` must declare `NyStr` in those signatures. A `string` value or literal still converts implicitly where a `NyStr` is expected. The repository's own hand-written harnesses (the M0/M1 verification fixtures and the Bind test's recording console) were updated.

## Measurements

These were measured on one Linux machine through `runPure`, with the AuthoringSession built from the baseline (before) and from this change (after), on the same snapshot. Each time is one `runPure` call, including its Roslyn build.

The program is the list-append measurement's, with `StringConcat` as the step: `StringEqual(rebuild(t), t)`, where `t` is 16 copies of an argument chunk joined in the program and `rebuild` is `ListFold(StringCodePoints(t), "", push)` with `push = StringConcat(acc, item)`.

| Code points concatenated | Before | After |
| --- | --- | --- |
| 16,384 | 455 ms | 353 ms |
| 32,768 | 472 ms | 410 ms |
| 65,536 | 1,493 ms | 283 ms |
| 131,072 | exceeded the 5 s limit | 284 ms |
| 1,048,576 | not run | 585 ms |

On probe 14's own inputs (the 39-byte mixed UTF-8 sample repeated `n` times, with valid and invalid sequences):

| Program | Input | Before | After |
| --- | --- | --- | --- |
| `copyConcat`: fold `StringConcat` over the code points | 97.5 KB | 3.93 s | 0.30 s |
| | 195 KB | exceeded the 5 s limit | 0.35 s |
| | 3.12 MB | not run | 1.24 s |
| First-draft `decode` (`StringConcat` per character) | 97.5 KB | 2.58 s | 0.31 s |
| | 195 KB | exceeded the 5 s limit | 0.36 s |
| | 3.12 MB | not run | 1.91 s |

The first-draft decode now runs 3.12 MB in 1.91 s, against 1.56 MB in 0.85 s for the list-and-join form that probe 14 settled on. So the list-and-join rewrite is no longer needed for speed. Probe 14's final port, re-run on the new build, still matches the real Python on 310/310 cases.

Scripts and raw results are in private files outside the repository (`rrawrlet-probe14/order31/`) (`scale3.py`, `scale4.py` and their `-before`/`-after` JSON).

## Verification

- `StringConcatAmortisedTests` (new) compiles the emitted runtime with a C# driver. It checks that branches of one store-backed base see only their own text, lengths and code-point counts, ordinal equality and hash against a plain `string` in both directions, `==`/`!=`, `NyEquality`, concatenating a String with itself and onto an older String, the empty-operand identities, `NyText.Join` over mixed store-backed and plain items, and 200 rounds of 32 `Parallel.For` workers extending one shared String. An Extended test authors the same through the session: branches of one base via `runPure`, and 262,144 concatenations of astral and BMP code points compared back to the original text.
- Tests that spell emitted types in expected source or in hand-written C# now use `NyStr` (`NyOption<NyStr>`, `NyList<NyStr>`, `NyResult<…, NyStr>`).
- `dotnet test tests/Nyrwvy.Core.Tests -c Release --settings tests/fast.runsettings` and the adapter tests: 608 Core tests and 29 adapter tests passed.
- `dotnet test Nyrwvy.slnx -c Release --settings tests/extended.runsettings`: 31 Extended and 81 extended Core tests passed.

## Not done

- `StringSlice` and the other operations still make a `string` for a store-backed operand, so slicing a long built String copies it once.
- A store kept only by a short String may hold up to twice its units' memory, as any doubling buffer does.
