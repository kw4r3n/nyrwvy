# Core library

Small functions whose calls store fewer nodes than the shapes they replace. They use existing nodes only, so they run on every backend and need no host code.

| Function | Replaces | Nodes saved per call |
| --- | --- | --- |
| `concat3` ... `concat10` | a chain of `stringConcat` | `n - 2` for `n` strings |
| `listIsEmpty<T>(items)` | `valueEqual(listLength(items), 0)` | 2 |
| `itemOr<T>(items, index, fallback)` | `optionGetOrDefault(listItemAt(items, index), fallback)` | 1 |

A call costs one extra stack frame, and there is no optimizing compiler to inline it.

## Using it

Import the workspace: `create --dependency libraries/core/core.json`. The sidecar keeps a copy of the imported snapshot, so a program takes later changes by upgrading the dependency.

## Changing it

`node libraries/core/author.mjs` writes `core.json` again from its recipes, with new IDs, so a program must upgrade its dependency afterwards.

## Check

`node libraries/core/check/run.mjs` (needs the Zig session host built by `npm run build:host`, `zig` and `dotnet`) imports the library and runs each function on the Zig and C# backends. `NYRWVY_ZIG_RUNNER_OPTIMIZE=Debug` makes it faster.
