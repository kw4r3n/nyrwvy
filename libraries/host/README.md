# Host capability library

Operating-system access for Nyrwvy programs without host code of their own: a workspace that declares the capabilities (`host.json`) and their C# implementations (`NyHost.cs`). The [design note](../../docs/plans/host-capability-library.md) sets what belongs here: primitives a program calls per event or per chunk, never per-frame library calls and never the application's decisions.

All operations use the effect `System`. A failure the program can act on is a `Result` whose error is the OS's message.

Operations that can wait for an external event for an unbounded time are marked `mayWait` ([wait and task-bound attributes](../../docs/decisions/wait-and-task-bound-attributes.md)): `Fs.readText`, `Fs.readBytes`, `Fs.writeBytes`, `Fs.rename`, `Fs.exists`, `Clock.sleep`, `Process.run`, `Pty.read`, `Pty.write` (it blocks while the pty's buffer is full, until the child reads) and `Ptys.close` (it waits for the child, up to the 2 s grace period and then until the killed child is reaped). A function declared `mustNotWait` cannot reach them. `Process.spawn`, `Env.get`, `Clock.now`, `Locks.acquire`, `Locks.release`, `Lock.path`, `Ptys.open`, `Ptys.openAt`, `Pty.resize` and `Pty.foregroundCommand` are not marked, and no capability is task-bound.

| Capability | Operation | Result |
| --- | --- | --- |
| `Fs` | `readText(path)` | `Result<String, String>`; decoded as UTF-8 (a UTF-8 byte order mark is dropped, invalid bytes read as U+FFFD) |
| `Fs` | `readBytes(path)` | `Result<Bytes, String>` |
| `Fs` | `writeBytes(path, data)` | `Result<Unit, String>`; creates or replaces the file |
| `Fs` | `rename(from, to)` | `Result<Unit, String>`; moves the file, replacing the destination if there is one |
| `Fs` | `exists(path)` | `Bool`; whether the file exists (a directory does not count) |
| `Env` | `get(name)` | `Option<String>`; `None` when unset |
| `Clock` | `now()` | `Int64`; the current UTC time in unix seconds |
| `Clock` | `sleep(milliseconds)` | `Unit`; waits for the given time |
| `Process` | `spawn(program, args)` | `Result<Unit, String>`; starts without waiting |
| `Process` | `run(program, args)` | `Result<ProcessOutput, String>`; runs to completion with empty standard input and waits until stdout and stderr close, as Python's `subprocess.run` does (a background process left holding them keeps it waiting); `ProcessOutput` has `exitCode`, `stdout` and `stderr` (Bytes) |
| `Ptys` | `open(argv, environment, cols, rows)` | `Result<Pty, String>`; a program on a new pseudo terminal, inheriting this process's environment with `environment` (`Map<String, String>`, for example `TERM`) replaced or added |
| `Ptys` | `openAt(argv, environment, cols, rows, cwd)` | `Result<Pty, String>`; `open` starting in `cwd` (`Option<String>`, `None` for the inherited directory); a bad `cwd` fails the open |
| `Ptys` | `close(pty)` | `Option<Int64>`; hangs up the child's process group and reaps it: its exit code, or `None` when a signal ended it. A child still running 2 s after the hangup is killed. |
| `Pty` | `read()` | `Option<Bytes>`; blocks for the next chunk, `None` once the child has exited |
| `Pty` | `write(data)` | `Unit` |
| `Pty` | `resize(cols, rows)` | `Unit` |
| `Pty` | `foregroundCommand()` | `Option<String>`; the foreground process group leader's name (from `/proc`) |
| `Locks` | `acquire(path)` | `Result<Lock, String>`; takes the lock file's exclusive lock without waiting, so a held lock is an `Err` and retrying is the program's job; the file is created if needed |
| `Locks` | `release(lock)` | `Unit`; releases the lock; the file itself is left in place |
| `Lock` | `path()` | `String`; the path the lock was acquired on |

`Pty` is a [capability handle](../../docs/decisions/capability-handles.md): an operation on a closed `Pty` throws "Handle is not open". `close` may run while another thread is blocked in `read`; that `read` then returns `None`. `Ptys` needs Linux with glibc.

`Lock` is a capability handle too: an operation on a released `Lock` throws "Handle is not open". `Lock` carries a `path()` operation because neither backend treats an operation-less capability as a handle. C# holds the lock as an open `FileStream` with `FileShare.None` and Zig as an `flock` on the open file; the two exclude each other on the same file, so a program and its port can share a lock file across backends (checked by `check/run-cross.mjs`).

Not provided yet: directory operations, file deletion, a working directory for `run`, standard input for `run` and sockets. They are added when a program needs them.

## Using it

1. Import the workspace: `create --dependency libraries/host/host.json`, or add it to an existing workspace with the `dependency` request. The sidecar keeps a copy of the imported snapshot, so a program takes later changes by upgrading the dependency.
2. Emit C# with `profile: { kind: "externalImplementations", operationIds: [...] }` listing the library operations (with the program's own host operations). The IDs are in `host.json`. The facade then has a base class (`FsBase`, `PtysBase`, ...) for every capability with a listed operation.
3. Copy `NyHost.cs` into the program's project (it needs `AllowUnsafeBlocks`) and pass `new NyrHost.FsHost()`, `new NyrHost.PtysHost()` and so on where the generated functions take the capabilities. List every library operation in step 2, or remove the classes of unlisted capabilities from the copy.

## Using it on Zig

Zig's [library mode](../../docs/decisions/zig-backend-memory-ownership.md#library-mode-as-built) binds every capability here ([exports that keep state](../../docs/decisions/zig-stateful-exports.md)).

1. Import the workspace as above.
2. Copy `host.zig` and `host.zig.json` beside the snapshot and add `"zigTables": ["host.zig.json"]` to `<snapshot>.backends.json`, next to `"exports"`.
3. Emit with `emitZigLibrary` into the same directory and build with `zig build-lib -dynamic -lc`. An export's `Fs`, `Env`, `Process` or `Ptys` parameter is not part of its C signature. A `Pty` crosses as an opaque `ny_handle *`: an export returning `Result<Pty, String>` gives it through `ny_handle **out` (status 4 is `Err`, its text through `ny_last_failure`), and an export taking one takes the pointer. `Ptys.close` is the release; `ny_open_handles()` counts handles not yet closed. `Ptys.open` and `Ptys.openAt` take a `List` and a `Map`, which have no C form, so a Nyrwvy function builds them and is exported instead.

The behaviour matches the table above, except that an `Err` holds the Zig error's name (`FileNotFound`) instead of the OS's message. `Pty.read` returns at most 64 KiB, so a C host passes a buffer that large: status 3 re-runs the read and loses the chunk. An export that may wait runs its waiting operations with the library's lock released, so other threads' calls go on meanwhile.

## Check

`node libraries/host/check/run-zig.mjs` (Linux, needs `zig` and `sh`) does the same for `host.zig` with a checking allocator, which must find nothing live at the end: files, environment, processes and a pty (size and environment, foreground command, resize, exit code, a call that returns while another thread is blocked in `read`, closing while that read waits, use after close and a second close, a child that ignores the hangup), file rename and existence, the clock, and locks (a lock used through `UseResource` and released when the body fails, a held lock refusing a second acquire, use after release). `node libraries/host/check/run.mjs` (Linux, needs `dotnet`, the Zig session host built by `npm run build:host`, `sh` and `cat`) builds a program that imports the library, emits its C#, and runs [`check/Program.cs`](check/Program.cs) against the real OS: files in a temporary directory (including rename and existence), environment variables, process start, and `Process.run` output and exit codes, the clock, locks (a held lock refusing a second acquire, use after release), and a pty driven both from C# and from a Nyrwvy function (`ptyEcho`: open `cat` with `TERM` set by the program, write, read the echo, close), including closing while another thread reads and a child that ignores the hangup. It runs in the extended Node suite (`.opencode/tests-extended/nyrwvy-host-library-csharp.test.mjs`). `node libraries/host/check/run-cross.mjs` holds a lock through one backend's emitted program and requires the other backend's acquire of the same lock file to fail, in both directions. It is a manual check, not part of the Node suite: each side holds its lock for 6 s while the other must fail to acquire within 1.5 s or 2.5 s (including dotnet start-up), and Node runs test files concurrently, so a loaded machine could fail it spuriously.
