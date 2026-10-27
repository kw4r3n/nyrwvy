# C# mapping tables and the C binding generator

- **Status:** Accepted
- **Date:** 2026-10-27
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#8-mapping-tables-and-the-binding-generator), §8, Order step 3 (slice 3a)

Every native library a program used needed a hand-written C# host class ([host capability library](../plans/host-capability-library.md), snap's `Native.cs`). The plan accepted per-backend mapping tables and a binding generator. This slice does it for **C functions on the C# backend**.

## Decision

**A table binds whole capabilities.** `<graph>.csharp.json` beside the program (one file per library graph, generated), plus an optional hand-edited `<graph>.csharp.override.json`. The override's `library`, `handle` flags and operation entries replace the generated ones, so regenerating keeps them. Version 1:

```json
{ "format": "nyrwvy.csharp-mapping", "version": 1, "library": "nyrdemo",
  "capabilities": {
    "Counter": { "handle": true, "operations": {
      "add": { "symbol": "nyd_counter_add", "arguments": [{ "self": true }, { "from": "amount", "c": "int64" }], "result": { "c": "int64" } } } },
    "Demo": { "operations": {
      "greet": { "symbol": "nyd_greet", "arguments": [{ "from": "name", "c": "utf8" }], "result": { "c": "utf8", "free": "nyd_free" } },
      "counterCreate": { "symbol": "nyd_counter_create", "arguments": [{ "from": "start", "c": "int64" }], "result": { "c": "handle" }, "failure": "null" },
      "counterDestroy": { "symbol": "nyd_counter_destroy", "arguments": [{ "from": "counter", "c": "handle", "release": true }] },
      "parsePort": { "symbol": "nyd_parse_port", "arguments": [{ "from": "text", "c": "utf8" }], "result": { "c": "int32" }, "failure": "negative" },
      "platformOnly": { "unavailable": true } } } } }
```

- **Keys are display names** of the capability, operation and parameters. The tool resolves them to stable IDs once, at `emitCSharp`; the backend sees only IDs, as its [spec](../specs/csharp-backend.md) requires. An unknown or ambiguous name, an operation of a listed capability with no entry, and an unknown format or version are request errors. The graph never names a C symbol.
- **Arguments** are listed in C order: `{ "from": <parameter>, "c": <type> }` or `{ "self": true }`, the handle of a handle capability's value. Every parameter is passed exactly once; a handle capability's operations pass `self` exactly once.
- **C types and conversions:**

  | `c` | Nyr type | Rule |
  | --- | --- | --- |
  | `int8` … `int64`, `uint8` … `uint64` | Int64 | an argument outside the C range is a host failure (`checked`); a `uint64` result above Int64's range likewise |
  | `bool`, `intBool` | Bool | C `bool` (one byte), or a C `int` where nonzero is true |
  | `utf8` | String | argument: a NUL-terminated UTF-8 copy borrowed for the call (a String containing U+0000 is a host failure); result: copied, then passed to `free` when named (owned), else borrowed |
  | `bytes` | Bytes | argument only: a pointer and a `size_t` length (two C arguments), borrowed for the call |
  | `handle` | a handle capability | an opaque pointer; `release: true` on an argument closes the handle |
  | `void` | Unit | result only; the default result |

- **Failure** (`null`, `negative`, `nonzero`) applies to a pointer, signed integer or integer result. For an operation whose result is `Result<T, String>` it gives `Err("<symbol> returned null")` or `Err("<symbol> failed with <value>")`, and `Ok` carries the converted result (`Unit` for `nonzero`, or for an integer status with `negative`); for any other result it is a host failure with that message. A `Result<_, String>` operation without failure detection is rejected.
- **`"unavailable": true`** binds an operation explicitly to nothing on this backend; calling it is a host failure (`NotSupportedException`).
- **Handles are capability values** ([capability handles](capability-handles.md)): a capability marked `handle` has a generated class holding the pointer. Its release is the operation the program declares (used through `UseResource` as usual); the argument marked `release` clears the pointer before the call, and any later use is a host failure "Handle is not open". A handle passed to a table operation must be that generated class.
- **Profile.** The `externalImplementations` profile takes `"tables": ["demo.csharp.json"]` (the table files are confined to the snapshot's directory like `bodyFile`; the `library` a table names is not: tables are trusted configuration, and `DllImport` loads whatever library they name); listed `operationIds` stay host-implemented, so one program mixes tables and hand-written hosts, and a capability still cannot mix kinds.
- **Validation** (`IncompatibleNativeBinding`, before emission): each conversion must fit its parameter or result type, and handle types must name a table-bound handle capability; the existing checks still require every operation of a bound capability to be bound. Generated handle classes are not synchronized, so `concurrentCapabilityIds` may not name a table-bound capability whose operations hold, take or return a handle (`ConcurrentNativeHandle`); a racing double release is still safe (the pointer is taken with `Interlocked.Exchange`). Arguments are all converted before a released handle is cleared, and an owned string result is freed even if reading it fails.
- **Emission.** Each table-bound capability gets an adapter `NyA_<capability>` (listed in the manifest's `capabilityAdapters`) with a public parameterless constructor (handle classes are created only by operations), holding one `[DllImport]` per C function. `library` is loaded by .NET's native library probing (`DllImport` by name: beside the application, or on the loader path such as `LD_LIBRARY_PATH`). Signatures use blittable types only (`IntPtr`, sized integers, `nuint`), so there is no source generator, no `unsafe` and no `AllowUnsafeBlocks`; marshalling is explicit in the method body.
- **Generator.** `Nyrwvy.AuthoringSession bind-c --header demo.h --library nyrdemo --capability Demo --effect DemoCalls --prefix nyd_ --functions a,b,... --requests demo.requests.jsonl --table demo.csharp.json [--release f] [--free f=freeFn] [--failure f=null|negative|nonzero] [--clang-arg x]` runs `clang -x c -Xclang -ast-dump=json -fsyntax-only` and maps only the listed functions. It writes by-name session requests (`simulateCreateEffect`, then one `simulateCreateCapabilityByName` per handle capability, then the library capability; commit each before the next) and the table, and prints a report listing what it could not map. Rules: exact-width and LP64 integer types map with their width; `bool` to Bool; `const char *` to String (a `char *` result needs `--free`); a `const unsigned char *` followed by an integer to Bytes; a pointer to a struct with no definition in the header to a handle capability named after the struct (prefix stripped); a function whose first parameter is a handle becomes an operation of that handle's capability with `self`, except a `--release` function, which stays on the library capability. Failure conventions are not visible in a header, so `--failure` declares them. Everything else is reported, including variadic functions, declarations without a parameter list, plain `char` values (target-dependent signedness) and a byte pointer whose length is not `size_t`; a target that is not LP64 (checked with clang's `__LP64__`) is refused.
- **`runPure` is unchanged**: it still binds fakes; native libraries never run inside it.

## Reason

A table is mechanical data, so it can be generated, checked against the graph and corrected by hand without moving application decisions into host code. Name keys let the generator write the table before the capabilities exist; resolving them once at emit keeps the backend on IDs. `DllImport` with blittable signatures compiles under any host project setting; `LibraryImport` would need `partial` classes, the source generator and `AllowUnsafeBlocks` in every consumer.

**Rejected: ID-keyed tables.** The generator runs before the proposals are committed, so it cannot know IDs; a second pass to rewrite the table would add a step without adding safety, since a stale name is a diagnostic either way.

## Consequences

- `CSharpMappingTableTests` (extended suite) compiles a C library with `cc`, runs `bind-c`, applies the requests through a session, emits a program with the table and an override, builds it with warnings as errors and runs it against the library: int, bool, owned string, non-ASCII and U+0000 strings, bytes, a negative-return `Err`, a handle through `UseResource` with its release observed by the library, a null handle as `Err`, use after release, an unavailable operation, regeneration keeping the override, and table mismatches as diagnostics. It skips with a message when `cc` or `clang` is missing.
- A host still constructs the library capability's adapter by its generated name (from the manifest); the facade has no alias for it yet.

## Out of scope (3a)

Callbacks, structs by value and out-parameters, enums, floats, `Option` for null, Bytes results, allocators, cancellation passed to C, async, and `.NET`, `.d.ts` or Zig sources. The generator supports LP64 targets only. Tables are found by explicit path only; reading a table beside a dependency's snapshot is not done.

## Revisit conditions

Revisit when snap's libghostty binding (3b) needs one of the out-of-scope conversions, when tables should be discovered beside dependencies, or when a second backend gets tables.
