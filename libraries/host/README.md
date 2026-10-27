# Host capability library

Operating-system access for Nyrwvy programs without host code of their own: a workspace that declares the capabilities (`host.json`) and their C# implementations (`NyHost.cs`). The [design note](../../docs/plans/host-capability-library.md) sets what belongs here: primitives a program calls per event or per chunk, never per-frame library calls and never the application's decisions.

All operations use the effect `System`. A failure the program can act on is a `Result` whose error is the OS's message.

Operations that can wait for an external event for an unbounded time are marked `mayWait` ([wait and task-bound attributes](../../docs/decisions/wait-and-task-bound-attributes.md)): `Fs.readText`, `Fs.readBytes`, `Fs.writeBytes`, `Process.run`, `Pty.read` and `Ptys.close` (it waits for the child, up to the 2 s grace period and then until the killed child is reaped). A function declared `mustNotWait` cannot reach them. `Process.spawn`, `Env.get`, `Ptys.open`, `Pty.write`, `Pty.resize` and `Pty.foregroundCommand` are not marked, and no capability is task-bound.

| Capability | Operation | Result |
| --- | --- | --- |
| `Fs` | `readText(path)` | `Result<String, String>`; decoded as UTF-8 (a UTF-8 byte order mark is dropped, invalid bytes read as U+FFFD) |
| `Fs` | `readBytes(path)` | `Result<Bytes, String>` |
| `Fs` | `writeBytes(path, data)` | `Result<Unit, String>`; creates or replaces the file |
| `Env` | `get(name)` | `Option<String>`; `None` when unset |
| `Process` | `spawn(program, args)` | `Result<Unit, String>`; starts without waiting |
| `Process` | `run(program, args)` | `Result<ProcessOutput, String>`; runs to completion with empty standard input and waits until stdout and stderr close, as Python's `subprocess.run` does (a background process left holding them keeps it waiting); `ProcessOutput` has `exitCode`, `stdout` and `stderr` (Bytes) |
| `Ptys` | `open(argv, environment, cols, rows)` | `Result<Pty, String>`; a program on a new pseudo terminal, inheriting this process's environment with `environment` (`Map<String, String>`, for example `TERM`) replaced or added |
| `Ptys` | `close(pty)` | `Option<Int64>`; hangs up the child's process group and reaps it: its exit code, or `None` when a signal ended it. A child still running 2 s after the hangup is killed. |
| `Pty` | `read()` | `Option<Bytes>`; blocks for the next chunk, `None` once the child has exited |
| `Pty` | `write(data)` | `Unit` |
| `Pty` | `resize(cols, rows)` | `Unit` |
| `Pty` | `foregroundCommand()` | `Option<String>`; the foreground process group leader's name (from `/proc`) |

`Pty` is a [capability handle](../../docs/decisions/capability-handles.md): an operation on a closed `Pty` throws "Handle is not open". `close` may run while another thread is blocked in `read`; that `read` then returns `None`. `Ptys` needs Linux with glibc.

Not provided yet: directory operations, file deletion, a working directory for `open` and `run`, standard input for `run`, sockets and clocks. They are added when a program needs them.

## Using it

1. Import the workspace: `create --dependency libraries/host/host.json`, or add it to an existing workspace with the `dependency` request. The sidecar keeps a copy of the imported snapshot, so a program takes later changes by upgrading the dependency.
2. Emit C# with `profile: { kind: "externalImplementations", operationIds: [...] }` listing the library operations (with the program's own host operations). The IDs are in `host.json`. The facade then has a base class (`FsBase`, `PtysBase`, ...) for every capability with a listed operation.
3. Copy `NyHost.cs` into the program's project (it needs `AllowUnsafeBlocks`) and pass `new NyrHost.FsHost()`, `new NyrHost.PtysHost()` and so on where the generated functions take the capabilities. List every library operation in step 2, or remove the classes of unlisted capabilities from the copy.

## Check

`node libraries/host/check/run.mjs` (Linux, needs `dotnet`, a built `tools/Nyrwvy.AuthoringSession` in Release, `sh` and `cat`) builds a program that imports the library, emits its C#, and runs [`check/Program.cs`](check/Program.cs) against the real OS: files in a temporary directory, environment variables, process start, and `Process.run` output and exit codes, and a pty driven both from C# and from a Nyrwvy function (`ptyEcho`: open `cat` with `TERM` set by the program, write, read the echo, close), including closing while another thread reads and a child that ignores the hangup. It is not part of the Node suite.
