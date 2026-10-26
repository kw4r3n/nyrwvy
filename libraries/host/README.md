# Host capability library

Operating-system access for Nyrwvy programs without host code of their own: a workspace that declares the capabilities (`host.json`) and their C# implementations (`NyHost.cs`). The [design note](../../docs/plans/host-capability-library.md) sets what belongs here: primitives a program calls per event or per chunk, never per-frame library calls and never the application's decisions.

All operations use the effect `System`.

| Capability | Operation | Result |
| --- | --- | --- |
| `Fs` | `readText(path)` | `Option<String>`; `None` when missing or unreadable |
| `Fs` | `readBytes(path)` | `Option<Bytes>`; as `readText` |
| `Fs` | `writeBytes(path, data)` | `Bool`; replaces the file, `false` when it cannot be written |
| `Env` | `get(name)` | `Option<String>`; `None` when unset |
| `Process` | `spawn(program, args)` | `Bool`; starts without waiting, `false` when it cannot start |
| `Ptys` | `open(argv, cols, rows)` | `Result<Pty, String>`; a program on a new pseudo terminal (Linux) |
| `Ptys` | `close(pty)` | `Unit`; hangs up the child's process group and waits for the child |
| `Pty` | `read()` | `Option<Bytes>`; blocks for the next chunk, `None` once the child has exited |
| `Pty` | `write(data)` | `Unit` |
| `Pty` | `resize(cols, rows)` | `Unit` |
| `Pty` | `foregroundCommand()` | `Option<String>`; the foreground process group leader's name |

`Pty` is a [capability handle](../../docs/decisions/capability-handles.md): an operation on a closed `Pty` throws "Handle is not open".

## Using it

1. Import the workspace: `create --dependency libraries/host/host.json`, or add it to an existing workspace with the `dependency` request. The sidecar keeps a copy of the imported snapshot, so a program takes later changes by upgrading the dependency.
2. Emit C# with `profile: { kind: "externalImplementations", operationIds: [...] }` listing the library operations the program reaches (with the program's own host operations). The IDs are in `host.json`.
3. Copy `NyHost.cs` into the program's project (it needs `AllowUnsafeBlocks` for the pty) and pass `new NyrHost.FsHost()`, `new NyrHost.PtysHost(("TERM", "xterm-256color"))` and so on where the generated functions take the capabilities. `PtysHost` takes environment variables that replace the inherited ones in the child.

The facade has a base class (`FsBase`, `PtyBase`, ...) only for capabilities the program reaches; a program that does not reach one removes its class from its copy of `NyHost.cs`.

## Check

`node libraries/host/check/run.mjs` (Linux, needs `dotnet`, a built `tools/Nyrwvy.AuthoringSession` in Release, `sh` and `cat`) builds a program that imports the library, emits its C#, and runs [`check/Program.cs`](check/Program.cs) against the real OS: files in a temporary directory, environment variables, process start, and a pty driven both from C# and from a Nyrwvy function (`ptyEcho`: open `cat`, write, read the echo, close). It is not part of the Node suite.
