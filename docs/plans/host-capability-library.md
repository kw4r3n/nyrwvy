# A shared host capability library (design note)

**Status:** Accepted by the owner on 2026-10-27 with the three answers as proposed. Implemented in [`libraries/host`](../../libraries/host/README.md); snap's move is still to do. The "no FFI" and "no binding generator" parts are being revised by [backend-neutral semantics and generated library bindings](multi-backend-and-bindings.md#8-mapping-tables-and-the-binding-generator) (directions accepted 2026-10-27; C functions on the C# backend are implemented by [mapping tables](../decisions/csharp-mapping-tables.md)).

**Date:** 2026-10-27

## Context

A Nyrwvy program reaches the operating system and native libraries only through capabilities, whose host classes the program's author writes in C#. There is no FFI, and this note does not add one: generated code stays a rendering of the graph's meaning ([semantic independence](../concepts/semantic-independence.md)), and the [roadmap](general-purpose-programming-roadmap.md) allows external libraries but forbids moving the application's decisions into host functions.

The cost today is that every program writes the same host classes again. snap (the terminal built on Nyrwvy) proved the alternative on 2026-10-26: a separate workspace, `nyr-host`, declares `Fs.readText`, `Env.get` and `Process.spawn` under one effect `System`, snap imports it as a dependency (added to an existing workspace by kw4r3n/nyrwvy#499), and one vendored file `NyHost.cs` implements them. snap's config loading and desktop notifications moved into Nyr with no snap-specific host code. The package lives in a scratch directory outside version control, so nothing else can depend on it.

snap still keeps its pseudo terminal in C# (`app/Pty.cs`: openpty, posix_spawn, resize, a reader thread, waitpid), because Nyr had no byte data. `Bytes` (kw4r3n/nyrwvy#501) removes that reason.

## Decision 1: what gets a capability, by call frequency

An OS call and a library call are the same mechanism from the program's side: a capability operation whose host class calls .NET, libc or a native library. The line is drawn by how often the program crosses it, not by what is behind it.

- **Wrapped:** operations called per event or per chunk, each a primitive the OS or library already exposes: read a file, spawn a process, read a chunk from a terminal, resize it, feed a chunk to a terminal emulator. Their cost (one interface call and a value conversion) is invisible beside the work.
- **Not wrapped:** operations called thousands of times per frame, such as drawing a glyph run with Skia or shaping with HarfBuzz. The program produces a description (a draw-command list, already written in Nyr as `layoutFrame`) and the host consumes it in one call.
- **Never wrapped:** an operation that is the application's decision ("handle the key", "update the screen"). That is the roadmap's prohibition, and each operation in the library must be checkable against it.

No binding generator is added. The library is a handful of hand-written operations chosen by a consumer's need; a generator would wrap whole APIs, including the per-frame ones excluded above.

## Decision 2: the library lives in this repository, distributed as source

- `libraries/host/host.json`: the Nyrwvy workspace (types, effect, capabilities; no functions).
- `libraries/host/NyHost.cs`: the host classes, written against the facade a consuming program emits (`FsBase`, `PtyBase`, ...). A program includes the file in its own project; there is no NuGet package, because the base classes are generated per program.
- `libraries/host/check/`: a runnable check (the pattern of `examples/cleanup-shield`), see Verification.

A program imports the library with the dependency operations that exist today; the dependency sidecar keeps a copy of the imported snapshot, so a consumer is not broken when the library moves or changes, and takes changes by upgrading the dependency.

## Decision 3: the first operations

All under the existing effect `System`. The [library README](../../libraries/host/README.md) has the full contract.

| Capability | Operation | Notes |
| --- | --- | --- |
| `Fs` | `readText(path): Result<String, String>`, `readBytes(path): Result<Bytes, String>`, `writeBytes(path, data): Result<Unit, String>` | the error is the OS's message |
| `Env` | `get(name): Option<String>` | unchanged |
| `Process` | `spawn(program, args): Result<Unit, String>`, `run(program, args): Result<ProcessOutput, String>` | `spawn` does not wait; `run` returns the exit code, stdout and stderr |
| `Ptys` | `open(argv, environment, cols, rows): Result<Pty, String>`, `close(pty: Pty): Option<Int64>` | issuing capability; `environment` replaces inherited variables; `close` hangs up, kills after a grace period, and returns the exit code |
| `Pty` (handle) | `read(): Option<Bytes>`, `write(data: Bytes): Unit`, `resize(cols, rows): Unit`, `foregroundCommand(): Option<String>` | a [capability handle](../decisions/capability-handles.md); `read` blocks for the next chunk, `None` at end of output |

**Revised at implementation (2026-10-27)**, so that no shape presumes snap's single use: failures are `Result<_, String>` instead of `None`/`false` (`Fs.readText` and `Process.spawn` change signature; snap repairs its one caller when it upgrades), `Process.run` is added beside the fire-and-forget `spawn`, `TERM` moves from the host constructor into `open`'s `environment`, and `close` reports the exit status and no longer waits forever for a child that ignores the hangup. The facade now gives a base class to every capability whose operations the host implements ([C# facade](../decisions/csharp-facade.md)), so a program that does not reach a library capability through its own signatures still compiles with the vendored `NyHost.cs`.

`Pty.foregroundCommand` replaces snap's read of `/proc/<pgrp>/comm`, which snap uses to label panes. A clock is left out: snap's agent states already take elapsed milliseconds as arguments from its event loop's host, and no consumer needs another source of time yet. (A clock was added later, on 2026-11-12: `Clock.now` and `Clock.sleep`, with `Fs.rename`, `Fs.exists` and the `Locks`/`Lock` file locks, when the model-cooldown port needed the current time, a retry wait and locked file updates inside the graph; see the library README.)

Use after `close` is a host failure, as the handle decision sets.

## Verification

A capability library has no pure functions, so run fixtures cannot test it. The check is a small program that imports the library, emits C# with the operations as external implementations, includes `NyHost.cs`, and exercises every host class for real in a temporary directory: write bytes and read them back, read a missing file, read a set and an unset environment variable, spawn `true` and a missing program, open a pty running `cat`, write bytes, read them echoed back, resize, read the foreground command, and close. It is run by hand, like `examples/cleanup-shield/test-runtime.mjs`, since it needs `dotnet` and (for the pty part) Linux.

The acceptance consumer is snap: its `Pty.cs` and `Libc` spawn bindings go away, the read loop becomes Nyr (`Pty.read` → feed the terminal → loop by tail self-call, as the agent-status loop does), and snap's own host code shrinks to the libghostty, Skia and Avalonia layer.

## Order

1. kw4r3n/nyrwvy#501 (`Bytes`) merges; the library's snapshot is schema 23 from the start.
2. The library, its check and docs, in one PR here.
3. snap moves `Fs`/`Env`/`Process` to the library (a dependency upgrade), then its pty.

## Owner questions (proposed answers)

1. **Home:** `libraries/host/` in this repository, rather than its own repository. It changes with the schema and the facade, which live here.
2. **Distribution:** vendored source (`NyHost.cs`), not a NuGet package, because the base classes are generated per program.
3. **Scope rule:** Decision 1's frequency rule, with no binding generator and no wrapping of Skia, HarfBuzz or other per-frame APIs.
