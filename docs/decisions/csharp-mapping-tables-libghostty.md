# Mapping tables for libghostty: out-parameters, constants, structs and buffers

- **Status:** Accepted
- **Date:** 2026-10-27
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#8-mapping-tables-and-the-binding-generator), §8, Order step 3 (slice 3b, the Nyrwvy half)
- **Builds on:** [C# mapping tables and the C binding generator](csharp-mapping-tables.md) (3a)

snap's hand-written `app/Native.cs` imports 46 libghostty-vt functions. 3a could bind none of them: handles are typedefs of pointers, results are status codes with the value in an out-parameter, callers pass a null allocator, getters and setters take a data kind and a `void *`, encoders write into a caller buffer, and some arguments are structs by value. This slice extends the table and `bind-c` so that every one of those functions can be bound. Moving snap onto the binding is a separate step (3c).

## Decision

**The table format stays `nyrwvy.csharp-mapping` version 1.** Every addition is a new optional key or a new `c` value, so a 3a table reads the same. A 3a build refuses a table that uses the additions (unknown keys are errors), which is the existing behaviour for anything it does not understand; the generator and the reader ship together, so no version bump is needed.

**New argument forms** (in C order, beside 3a's `{ "from", "c" }` and `{ "self": true }`):

| Form | Example | Meaning |
| --- | --- | --- |
| constant | `{ "value": 1, "c": "int32", "name": "GHOSTTY_CELL_DATA_CODEPOINT" }`, `{ "value": null, "c": "pointer" }` | a fixed integer, enum value or null pointer the operation does not take; `name` is informational. Range-checked against `c` before emission |
| by address | `{ "from": "rows", "c": "handle", "by": "pointer" }` | the converted parameter in a local, passed as `ref` (`const T *` or a pre-allocated handle the library populates) |
| out | `{ "out": "uint32" }`, `{ "out": "handle" }`, `{ "out": "bits64" }` | a local the C function writes; it is the operation's value (the `Ok` value under `failure`). The C result is then a status: `void`, or an integer that `failure` must check (a `bool` status is refused, since no failure rule reads it) |
| out bytes | `{ "out": "bytes", "capacity": 16, "retry": -3 }` | three C parameters `char *buf, size_t size, size_t *written`; the written bytes are a Bytes value. When the C result equals `retry`, the call runs once more with a buffer of the size written back (libghostty's `GHOSTTY_OUT_OF_SPACE` convention). `retry` needs a signed status checked by `failure` and no released handle (the retry would pass it again). A requested size above 16 MiB, or a written length beyond the buffer, is the operation's `Err` (a host failure for a non-Result operation), never an overflow |
| struct | `{ "struct": [{ "value": 2, "c": "int32" }, { "from": "delta", "c": "int64" }], "size": 24 }`, `"by": "pointer"` | a C struct of scalar fields in declaration order: parameters, constants and `{ "sizeof": true, "c": "uint64" }` (versioned structs that carry their own size). It is a `[StructLayout(Sequential)]` struct, so fields get C's natural alignment; `size` pads a union tail and may not be below the fields' natural size; a `sizeof` field that does not fit its type is a host failure. Passed by value or, with `by: pointer`, by address |
| retained | `{ "from": "utf8", "c": "bytes", "retain": true }` | a Bytes or String the library keeps pointing to after the call (`ghostty_key_event_set_utf8`: the event does not copy it). Only on a handle capability's operation: the copy is unmanaged memory owned by the handle object, replaced (and the previous copy freed) by the next call of that operation, and freed when the handle is released. The header cannot tell a retained pointer from a borrowed one, so the spec must say |

At most one out or out-bytes argument per operation. Every operation parameter is still passed exactly once, counting struct fields.

**New `c` values:** `float32` and `float64` convert to and from Float64 (an argument rounds to `float`; a finite value outside `float`'s range is a host failure); `bits64` carries a 64-bit C value in an Int64 bit for bit, for opaque references such as `GhosttyCell` and `GhosttyRow` (`uint64` would make a high bit a host failure); `pointer` is only a null constant.

**Row and cell references are Int64 (`bits64`), not handles.** They are plain values valid until the next render-state update, owned by nothing; a handle would need a release the library does not have.

**`bind-c` additions:**

- Typedefs are resolved before classification: a typedef of a pointer to an incomplete struct (`GhosttyTerminal`) is a handle named after the typedef, and a non-const pointer to it (`GhosttyTerminal *`) is an out handle; a `const` one is an input left to the spec. `--type-prefix Ghostty` strips the type prefix as `--prefix` strips the function prefix; a method name drops the snake-case handle name (`ghostty_render_state_row_iterator_next` → `RenderStateRowIterator.next`).
- Enums map to `int32`. This holds for every enum whose values fit `int` and for `enum : int`, which is libghostty's; a library with wider fixed enum types needs the AST's `fixedUnderlyingType` read (a `shortcut:` in the code).
- A byte pointer (`char *`, `const char *`, `uint8_t *`) followed by `size_t` is one Bytes argument; Bytes are copied and pinned for the call only, so a library that writes into its input (`ghostty_paste_encode`) cannot reach the caller's value, and a library that keeps the pointer needs a spec entry with `retain`. `char *, size_t, size_t *` is an out buffer with `--buffer <capacity>[:<retry status>]` (an enumerator name is resolved).
- `--constant allocator=null` passes a constant for every parameter of that name; `--failure GhosttyResult=nonzero` keys a convention by result type as well as by function.
- `--spec <file>` describes what the header cannot say: one operation per data kind for getters and setters, structs, `bits64`, handles passed as `void *` (an argument names its `"capability"`; the hint is not written to the table). Its shape is the table's, with constants allowed as enumerator names and `c`, `result` and `failure` left to the header and the flags. The generator resolves names (enumerators without an initializer count on from the previous one; one that does not fit Int64 is reported), aligns the entry with the C parameters, derives the operation's declaration from it, and writes the completed entry to the table. A function with spec entries is not mapped automatically. The spec is generator input: the emit-time override stays as in 3a, with numbers only, so emission never needs clang.

**Readable adapter names.** The facade gains `New<Capability>()` for each table-bound library capability (a handle capability is created only by operations), so a host writes `NewGhostty()` instead of the adapter's generated name.

## Reason

A struct argument is flattened into scalar operation parameters rather than mapped to a graph Product: a Product would need type declarations in the binding graph, the generator would have to emit type requests, and the table would have to name fields, all for values that libghostty only passes in (positions, sizes, scroll requests, colours). The spec is generator input rather than names in the override because only the generator has clang; emission must stay a pure read of numbers. Version 1 is kept because every addition is optional and a 3a table means the same thing to the new reader.

## The libghostty binding

`CSharpMappingTableGhosttyTests` runs:

```
bind-c --header vt.h --clang-arg -I<include> --library ghostty-vt --capability Ghostty --effect GhosttyCalls
       --prefix ghostty_ --type-prefix Ghostty --functions <the 46 functions> --spec ghostty.spec.json
       --release ghostty_terminal_free ... (the eight *_free functions)
       --failure GhosttyResult=nonzero --constant allocator=null --buffer 16:GHOSTTY_OUT_OF_SPACE
       --requests ghostty.requests.jsonl --table ghostty.csharp.json
```

The spec (in the test) covers the getters, setters and structs of 11 functions (including `set_utf8`, whose text is retained); the other 35 map from the header alone. All 46 are bound; the test checks that the table's symbols are exactly those functions. It applies the requests, creates a forwarding function per operation, emits with the table, builds with warnings as errors and runs against the real `libghostty-vt.so`: a terminal, text plus an SGR sequence, render-state update, row and cell iteration with codepoints read back, a key event and an SGR mouse event encoded, paste safety, a bracketed paste that overflows the 16-byte buffer and retries, resize, a foreground colour set through a struct by address, scroll by delta and to the bottom, and every handle freed (a released handle is then a host failure). The library directory comes from `NYRWVY_LIBGHOSTTY_VT` or a `snap/vendor/libghostty-vt` beside an ancestor of the test; the test skips with a message without it or clang.

## Not covered

- **Callbacks** (`ghostty_terminal_set` with userdata, write-pty, bell, title and desktop-notification callbacks): plan §5. snap keeps these in host code.
- **Out-structs**: RGB colours (`GhosttyColorRgb` from `*_get`), the cell `Style`, the 256-colour palette array, the grapheme buffer, the title `GhosttyString` and the in-out `GhosttyModeConfig`. The functions are bound (for scalar data kinds), but those data kinds are not. A struct result needs either a graph Product per struct (type declarations in the binding graph) or a packing rule; that choice is left to the slice that needs it.
- **Regenerating against existing capabilities**: `bind-c` writes create requests only. To change a binding, regenerate into a fresh library graph and diff its requests against the committed one; added operations and changed signatures are then applied by hand with the session's operation and signature-migration requests. The table and override are regenerated or kept as in 3a.
- **Table discovery beside a dependency's snapshot**: tables are still listed by path in the profile (confined to the program's snapshot directory).

## How snap should hold the binding

A **separate library graph**, like `libraries/host`: `snap/libraries/ghostty/` with its snapshot, `ghostty.spec.json`, the generated `ghostty.csharp.json` and a hand-kept `ghostty.csharp.override.json`, imported by snap's graph as a dependency. The binding is then regenerated and reviewed on its own, snap's application graph names no C symbol, and the table path lies under snap's snapshot directory, so the profile can list it without discovery. The snap half (3c):

1. Create the library graph with `bind-c` (the command above, buffer 128 as snap uses, plus spec entries for every data kind snap reads), and import it into snap's graph; check that the dependency's capabilities resolve by display name in `emitCSharp` (not exercised here: the test binds in the program's own graph). If they do not, add table discovery beside the dependency's snapshot first.
2. Replace `Vt.*` calls in `Screen.cs` with the generated operations through Nyr functions or the facade, constructing the library adapter with `NewGhostty()`.
3. Keep a hand-written host for callbacks and the out-structs above until those land, and delete everything else in `Native.cs`.

## Revisit conditions

Revisit when snap needs an out-struct or a callback, when regeneration should update a committed graph in place, or when a library uses enums wider than `int`.
