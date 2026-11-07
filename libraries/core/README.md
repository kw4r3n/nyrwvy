# Core library

Small functions whose calls store fewer nodes than the shapes they replace. They use existing nodes only, so they run on every backend and need no host code.

| Function | Replaces | Nodes saved per call |
| --- | --- | --- |
| `concat3` ... `concat10` | a chain of `stringConcat` | `n - 2` for `n` strings |
| `listIsEmpty<T>(items)` | `valueEqual(listLength(items), 0)` | 2 |
| `itemOr<T>(items, index, fallback)` | `optionGetOrDefault(listItemAt(items, index), fallback)` | 1 |
| `isNotEmpty(text)` | `boolNot(stringIsEmpty(text))` | 1 |
| `isBlank(text)` | `stringIsEmpty(stringTrim(text))` | 1 |
| `orIfEmpty(text, fallback)` | a `conditional` on `stringIsEmpty` | 2 |
| `int64InRange(value, low, high)` | two comparisons, both ends included | 5 |
| `float64InRange(value, low, high)` | the same; false for NaN | 11 |
| `int64Clamp`, `float64Clamp(value, low, high)` | `min(max(value, low), high)`; `high` wins when `low > high` | 1 |
| `splitTrimmed(text, separator)` | split, trim each, drop the empty ones | 2 |
| `stripPrefix(text, prefix)` | a `conditional` on `stringStartsWith` and a slice | 8 |
| `beforeFirst(text, separator)` | the text before the first separator, or all of it when there is none | 7 |
| `afterFirst(text, separator)` | the text after the first separator, or `""` when there is none | 11 |
| `parseInt64Or(text, fallback)` | `optionGetOrDefault(stringParseInt64(text), fallback)` | 1 |
| `stringPadStart`, `stringPadEnd(text, width, fill)` | `fill` repeated `width - length` times, whole, on one side | 6 |
| `textsLack(items, item)` | `boolNot(listContains(items, item))` on `List<String>` | 1 |
| `find<T>(items, predicate)` | `listFindIndex` and `listItemAt`, with a function value instead of a named callback | 3 |
| `optionIsNone<T>(option)` | `boolNot(optionIsSome(option))` | 1 |
| `optionOrElse<T>(option, other)` | a `conditional` on `optionIsSome` | 2 |
| `resultIsErr<T, E>(result)` | `boolNot(resultIsOk(result))` | 1 |

`trimmed` and `holds` exist for callbacks, which must name a function; `isNotEmpty` and `textsLack` serve as callbacks too. `textsLack` is not generic because `listContains` needs an equatable item and a type parameter never is. All operands are evaluated first, as for any call, so a `fallback` or `other` is computed whether or not it is used.

## Stack

These functions do their work and return, so a call adds at most one frame at the deepest point of a run: recursion does not stack them up. The exception is `find`, whose predicate runs inside it and may recurse. In the Zig backend's default ReleaseSafe build the compiler removes most of them: with each called from 6 exports, `splitTrimmed`, `stripPrefix`, `beforeFirst`, `afterFirst`, `parseInt64Or` and the two pad functions kept a frame and the other new ones none, and `concat10` kept one when called from 30 exports. Debug builds keep every frame, and the C# backend leaves inlining to the JIT. Inlining is not guaranteed, so do not rely on it for depth.

## Using it

Import the workspace: `create --dependency libraries/core/core.json`. The sidecar keeps a copy of the imported snapshot, so a program takes later changes by upgrading the dependency.

## Changing it

`node libraries/core/author.mjs` writes `core.json` again from its recipes, with new IDs, so a program must upgrade its dependency afterwards.

## Check

`node libraries/core/check/run.mjs` (needs the Zig session host built by `npm run build:host`, `zig` and `dotnet`) imports the library and runs each function on the Zig and C# backends. `NYRWVY_ZIG_RUNNER_OPTIMIZE=Debug` makes it faster.
