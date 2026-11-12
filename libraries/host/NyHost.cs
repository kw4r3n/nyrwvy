// Host implementations of the host library's capabilities (host.json: Fs, Env, Process, Clock, Locks,
// Ptys and the Pty and Lock handles; effect System). Distributed as source: the *Base classes come from the program's own emitted
// facade, so include this file in a project whose Nyrwvy graph imports host.json (docs/plans/host-capability-library.md).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nyrwvy.Generated;

namespace NyrHost;

public sealed class FsHost : FsBase
{
    static NyResult<T, NyStr> Attempt<T>(Func<T> action)
    {
        try { return NyResult<T, NyStr>.Ok(action()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return NyResult<T, NyStr>.Err(e.Message); }
    }

    /// The file's text decoded as UTF-8 (a UTF-8 byte order mark is dropped, invalid bytes read as U+FFFD).
    public override NyResult<NyStr, NyStr> readText(NyStr path) => Attempt<NyStr>(() =>
    {
        // File.ReadAllText would also honour UTF-16 and UTF-32 byte order marks.
        var bytes = File.ReadAllBytes(path);
        int start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return new UTF8Encoding(false).GetString(bytes, start, bytes.Length - start);
    });

    public override NyResult<NyBytes, NyStr> readBytes(NyStr path) => Attempt(() => NyBytes.FromOwnedArray(File.ReadAllBytes(path)));

    /// Creates or replaces the file.
    public override NyResult<NyUnit, NyStr> writeBytes(NyStr path, NyBytes data) =>
        Attempt(() => { File.WriteAllBytes(path, data.Span); return NyUnit.Value; });

    /// Moves the file, replacing the destination if there is one.
    public override NyResult<NyUnit, NyStr> rename(NyStr from, NyStr to) =>
        Attempt(() => { File.Move(from, to, true); return NyUnit.Value; });

    /// Whether the file exists.
    public override bool exists(NyStr path) => File.Exists(path);
}

public sealed class ClockHost : ClockBase
{
    /// The current UTC time in unix seconds.
    public override long now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public override NyUnit sleep(long milliseconds)
    {
        // Thread.Sleep takes at most Int32.MaxValue milliseconds at once, so sleep in day-long slices.
        while (milliseconds > 0)
        {
            var slice = (int)Math.Min(milliseconds, 86_400_000);
            System.Threading.Thread.Sleep(slice);
            milliseconds -= slice;
        }
        return NyUnit.Value;
    }
}

/// Exclusive file locks, held by an open FileStream with FileShare.None, as rrawrlet's Paths.Hold does.
/// The lock file itself is left in place on release.
public sealed class LocksHost : LocksBase
{
    // The bare Lock is the facade's global using alias for the generated handle type; the alias is
    // what binds the name, ahead of System.Threading.Lock in a consumer with implicit usings.
    public override NyResult<Lock, NyStr> acquire(NyStr path)
    {
        try { return NyResult<Lock, NyStr>.Ok(new LockHost(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return NyResult<Lock, NyStr>.Err(e.Message); }
    }

    public override NyUnit release(Lock handle)
    {
        ((LockHost)handle).Release();
        return NyUnit.Value;
    }
}

public sealed class LockHost : LockBase
{
    readonly FileStream _stream;
    readonly string _path;
    bool _released;

    internal LockHost(string path)
    {
        _path = path;
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    void Open() { if (_released) throw new InvalidOperationException("Handle is not open"); }

    /// The path the lock was acquired on.
    public override NyStr path() { Open(); return _path; }

    internal void Release()
    {
        Open();
        _released = true;
        _stream.Dispose();
    }
}

public sealed class EnvHost : EnvBase
{
    /// The variable's value, or None when it is unset.
    public override NyOption<NyStr> get(NyStr name) =>
        Environment.GetEnvironmentVariable(name) is { } value ? NyOption<NyStr>.Some(value) : NyOption<NyStr>.None();
}

public sealed class ProcessHost : ProcessBase
{
    static System.Diagnostics.ProcessStartInfo StartInfo(NyStr program, NyList<NyStr> args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(program);
        for (long i = 0; i < args.Length; i++) info.ArgumentList.Add(args.ItemAt(i).Value);
        return info;
    }

    /// Starts the program without waiting for it.
    public override NyResult<NyUnit, NyStr> spawn(NyStr program, NyList<NyStr> args)
    {
        try
        {
            System.Diagnostics.Process.Start(StartInfo(program, args))?.Dispose();
            return NyResult<NyUnit, NyStr>.Ok(NyUnit.Value);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return NyResult<NyUnit, NyStr>.Err(e.Message); }
    }

    /// Runs the program to completion with empty standard input and returns its exit code and output.
    /// Like Python's subprocess.run, it waits until stdout and stderr close, so a background process the
    /// program leaves holding them keeps it waiting.
    public override NyResult<ProcessOutput, NyStr> run(NyStr program, NyList<NyStr> args)
    {
        var info = StartInfo(program, args);
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        try
        {
            using var child = System.Diagnostics.Process.Start(info)!;
            child.StandardInput.Close();
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            // Both pipes drain at once, so a child filling one while the other is read never blocks.
            var copies = System.Threading.Tasks.Task.WhenAll(
                child.StandardOutput.BaseStream.CopyToAsync(stdout), child.StandardError.BaseStream.CopyToAsync(stderr));
            child.WaitForExit();
            copies.Wait();
            return NyResult<ProcessOutput, NyStr>.Ok(NyFacade.NewProcessOutput(
                exitCode: child.ExitCode, stdout: NyBytes.FromOwnedArray(stdout.ToArray()), stderr: NyBytes.FromOwnedArray(stderr.ToArray())));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return NyResult<ProcessOutput, NyStr>.Err(e.Message); }
    }
}

/// Opens programs on pseudo terminals (Linux with glibc; posix_spawn, no fork from managed code).
public sealed class PtysHost : PtysBase
{
    /// The child inherits this process's environment with `environment`'s variables replaced or added.
    public override NyResult<Pty, NyStr> open(NyList<NyStr> argv, NyMap<NyStr, NyStr> environment, long cols, long rows) =>
        Open(argv, environment, cols, rows, null);

    /// `open` starting in `cwd` instead of this process's working directory. A bad `cwd` fails the open.
    public override NyResult<Pty, NyStr> openAt(NyList<NyStr> argv, NyMap<NyStr, NyStr> environment, long cols, long rows, NyOption<NyStr> cwd) =>
        Open(argv, environment, cols, rows, cwd.HasValue ? cwd.Value.Text : null);

    static NyResult<Pty, NyStr> Open(NyList<NyStr> argv, NyMap<NyStr, NyStr> environment, long cols, long rows, string? cwd)
    {
        var args = new string[argv.Length];
        for (long i = 0; i < argv.Length; i++) args[i] = argv.ItemAt(i).Value;
        if (args.Length == 0) return NyResult<Pty, NyStr>.Err("empty argv");
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(e => !environment.ContainsKey((string)e.Key))
            .Select(e => $"{e.Key}={e.Value}")
            .Concat(environment.Fold(new List<string>(), (list, name, value) => { list.Add($"{(string)name}={(string)value}"); return list; }))
            .ToArray();
        try { return NyResult<Pty, NyStr>.Ok(new PtyHost(args, env, cols, rows, cwd)); }
        catch (IOException e) { return NyResult<Pty, NyStr>.Err(e.Message); }
    }

    /// Hangs up the child's process group, as closing a terminal does, and reaps the child: its exit code,
    /// or None when a signal ended it. A child still running after the grace period is killed.
    public override NyOption<long> close(Pty pty) => ((PtyHost)pty).Close();
}

public sealed unsafe class PtyHost : PtyBase
{
    readonly FileStream _stream;
    readonly int _master;
    readonly int _pid;
    readonly byte[] _buffer = new byte[65536];
    bool _closed;

    internal PtyHost(string[] argv, string[] env, long cols, long rows, string? cwd = null)
    {
        // Close-on-exec from the start, and no slave fd in this process: neither can leak into a child
        // that another thread starts, which would keep this pty's reads from ever seeing EIO.
        _master = Libc.OpenPt(Libc.O_RDWR | Libc.O_NOCTTY | Libc.O_CLOEXEC);
        if (_master < 0) throw new IOException($"posix_openpt failed: {Marshal.GetLastPInvokeError()}");
        byte* name = stackalloc byte[128];
        if (Libc.GrantPt(_master) != 0 || Libc.UnlockPt(_master) != 0 || Libc.PtsName(_master, name, 128) != 0)
        {
            Libc.Close(_master);
            throw new IOException($"pty setup failed: {Marshal.GetLastPInvokeError()}");
        }
        SetSize(cols, rows);
        // A session leader that opens the slave by path gets it as its controlling terminal.
        byte* fa = stackalloc byte[512];
        byte* attr = stackalloc byte[512];
        int r = Libc.ActionsInit(fa);
        if (r != 0) { Libc.Close(_master); throw new IOException($"posix_spawn_file_actions_init failed: {r}"); }
        int pid = 0;
        try
        {
            r = Libc.ActionsOpen(fa, 0, name, Libc.O_RDWR, 0);
            if (r == 0) r = Libc.ActionsDup2(fa, 0, 1);
            if (r == 0) r = Libc.ActionsDup2(fa, 0, 2);
            if (r == 0) r = Libc.AttrInit(attr);
            if (r == 0) r = Libc.AttrSetFlags(attr, Libc.POSIX_SPAWN_SETSID);
            if (r == 0 && cwd != null) r = WithCStrings([cwd], a => Libc.ActionsChdir(fa, a[0]));
            if (r == 0) r = WithCStrings(argv, a => WithCStrings(env, e => Libc.SpawnP(out pid, a[0], fa, attr, a, e)));
        }
        finally { Libc.ActionsDestroy(fa); }
        if (r != 0)
        {
            Libc.Close(_master);
            throw new IOException($"posix_spawnp {argv[0]} failed: {r}");
        }
        _pid = pid;
        _stream = new FileStream(new SafeFileHandle(_master, ownsHandle: true), FileAccess.ReadWrite, 0);
    }

    delegate int CStringsBody(byte** ptrs);

    static int WithCStrings(string[] items, CStringsBody body)
    {
        var handles = items.Select(s => Marshal.StringToCoTaskMemUTF8(s)).ToArray();
        try
        {
            var ptrs = new byte*[items.Length + 1];
            for (int i = 0; i < items.Length; i++) ptrs[i] = (byte*)handles[i];
            fixed (byte** p = ptrs) return body(p);
        }
        finally { foreach (var h in handles) Marshal.FreeCoTaskMem(h); }
    }

    void Open() { if (_closed) throw new InvalidOperationException("Handle is not open"); }

    void SetSize(long cols, long rows)
    {
        var ws = new Libc.WinSize { Cols = (ushort)Math.Clamp(cols, 1, ushort.MaxValue), Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue) };
        Libc.IoctlWinSize(_master, Libc.TIOCSWINSZ, ref ws);
    }

    /// Blocks for the next chunk of output; None once the child has exited and its output is drained.
    public override NyOption<NyBytes> read()
    {
        Open();
        int n;
        try { n = _stream.Read(_buffer); }
        catch (IOException) { n = 0; } // EIO on the master once the child exits
        return n > 0 ? NyOption<NyBytes>.Some(NyBytes.FromOwnedArray(_buffer[..n])) : NyOption<NyBytes>.None();
    }

    public override NyUnit write(NyBytes data)
    {
        Open();
        lock (_stream) { _stream.Write(data.Span); _stream.Flush(); }
        return NyUnit.Value;
    }

    public override NyUnit resize(long cols, long rows)
    {
        Open();
        SetSize(cols, rows);
        return NyUnit.Value;
    }

    /// The name of the foreground process group's leader (the running command, or the shell itself).
    public override NyOption<NyStr> foregroundCommand()
    {
        Open();
        try { return NyOption<NyStr>.Some(File.ReadAllText($"/proc/{Libc.TcGetPgrp(_master)}/comm").Trim()); }
        catch (IOException) { return NyOption<NyStr>.None(); }
    }

    internal NyOption<long> Close()
    {
        Open();
        _closed = true;
        Libc.Kill(-_pid, Libc.SIGHUP);
        _stream.Dispose();
        int status = 0;
        var deadline = Environment.TickCount64 + 2000;
        int reaped;
        while ((reaped = Libc.WaitPid(_pid, out status, Libc.WNOHANG)) == 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                Libc.Kill(-_pid, Libc.SIGKILL);
                reaped = Libc.WaitPid(_pid, out status, 0);
                break;
            }
            System.Threading.Thread.Sleep(10);
        }
        // A failed wait (for example ECHILD when SIGCHLD is ignored) leaves no status to report.
        return reaped == _pid && (status & 0x7f) == 0 ? NyOption<long>.Some((status >> 8) & 0xff) : NyOption<long>.None();
    }
}

static unsafe partial class Libc
{
    const string Lib = "libc.so.6";
    public const int O_RDWR = 2, O_NOCTTY = 0x100, O_CLOEXEC = 0x80000, POSIX_SPAWN_SETSID = 0x80, WNOHANG = 1, SIGHUP = 1, SIGKILL = 9;
    public const nuint TIOCSWINSZ = 0x5414;

    [StructLayout(LayoutKind.Sequential)] public struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

    [LibraryImport(Lib, EntryPoint = "posix_openpt", SetLastError = true)] public static partial int OpenPt(int flags);
    [LibraryImport(Lib, EntryPoint = "grantpt", SetLastError = true)] public static partial int GrantPt(int fd);
    [LibraryImport(Lib, EntryPoint = "unlockpt", SetLastError = true)] public static partial int UnlockPt(int fd);
    [LibraryImport(Lib, EntryPoint = "ptsname_r", SetLastError = true)] public static partial int PtsName(int fd, byte* buffer, nuint length);
    [LibraryImport(Lib, EntryPoint = "ioctl", SetLastError = true)] public static partial int IoctlWinSize(int fd, nuint request, ref WinSize ws);
    [LibraryImport(Lib, EntryPoint = "kill")] public static partial int Kill(int pid, int sig);
    [LibraryImport(Lib, EntryPoint = "tcgetpgrp")] public static partial int TcGetPgrp(int fd);
    [LibraryImport(Lib, EntryPoint = "close")] public static partial int Close(int fd);
    [LibraryImport(Lib, EntryPoint = "waitpid")] public static partial int WaitPid(int pid, out int status, int options);
    [LibraryImport(Lib, EntryPoint = "posix_spawnp")] public static partial int SpawnP(out int pid, byte* file, void* actions, void* attr, byte** argv, byte** envp);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_init")] public static partial int ActionsInit(void* fa);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_destroy")] public static partial int ActionsDestroy(void* fa);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_addopen")] public static partial int ActionsOpen(void* fa, int fd, byte* path, int flags, uint mode);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_adddup2")] public static partial int ActionsDup2(void* fa, int fd, int newFd);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_addchdir_np")] public static partial int ActionsChdir(void* fa, byte* path);
    [LibraryImport(Lib, EntryPoint = "posix_spawnattr_init")] public static partial int AttrInit(void* attr);
    [LibraryImport(Lib, EntryPoint = "posix_spawnattr_setflags")] public static partial int AttrSetFlags(void* attr, short flags);
}
