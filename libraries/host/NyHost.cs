// Host implementations of the host library's capabilities (host.json: Fs, Env, Process, Ptys and the Pty
// handle; effect System). Distributed as source: the *Base classes come from the program's own emitted
// facade, so include this file in a project whose Nyrwvy graph imports host.json (docs/plans/host-capability-library.md).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nyrwvy.Generated;

namespace NyrHost;

public sealed class FsHost : FsBase
{
    /// The file's text, or None when it is missing or unreadable.
    public override NyOption<NyStr> readText(NyStr path)
    {
        try { return NyOption<NyStr>.Some(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return NyOption<NyStr>.None(); }
    }

    /// The file's bytes, or None when it is missing or unreadable.
    public override NyOption<NyBytes> readBytes(NyStr path)
    {
        try { return NyOption<NyBytes>.Some(NyBytes.FromOwnedArray(File.ReadAllBytes(path))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return NyOption<NyBytes>.None(); }
    }

    /// Replaces the file's contents; false when it cannot be written.
    public override bool writeBytes(NyStr path, NyBytes data)
    {
        try { File.WriteAllBytes(path, data.Span); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
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
    /// Starts the program without waiting; false when it cannot be started (for example, not installed).
    public override bool spawn(NyStr program, NyList<NyStr> args)
    {
        var list = new List<string>();
        for (long i = 0; i < args.Length; i++) list.Add(args.ItemAt(i).Value);
        try { System.Diagnostics.Process.Start(program, list)?.Dispose(); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

/// Opens shells and other programs on pseudo terminals (Linux; posix_spawn, no fork from managed code).
/// The child inherits this process's environment with the given variables replaced, for example TERM.
public sealed class PtysHost(params (string Name, string Value)[] environment) : PtysBase
{
    public override NyResult<Pty, NyStr> open(NyList<NyStr> argv, long cols, long rows)
    {
        var args = new string[argv.Length];
        for (long i = 0; i < argv.Length; i++) args[i] = argv.ItemAt(i).Value;
        if (args.Length == 0) return NyResult<Pty, NyStr>.Err("empty argv");
        var replaced = environment.Select(v => v.Name).ToHashSet();
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(e => !replaced.Contains((string)e.Key))
            .Select(e => $"{e.Key}={e.Value}")
            .Concat(environment.Select(v => $"{v.Name}={v.Value}")).ToArray();
        try { return NyResult<Pty, NyStr>.Ok(new PtyHost(args, env, cols, rows)); }
        catch (IOException e) { return NyResult<Pty, NyStr>.Err(e.Message); }
    }

    /// Hangs up the child's process group, as closing a terminal does, and waits for the child.
    public override NyUnit close(Pty pty)
    {
        ((PtyHost)pty).Close();
        return NyUnit.Value;
    }
}

public sealed unsafe class PtyHost : PtyBase
{
    readonly FileStream _stream;
    readonly int _master;
    readonly int _pid;
    readonly byte[] _buffer = new byte[65536];
    bool _closed;

    internal PtyHost(string[] argv, string[] env, long cols, long rows)
    {
        byte* name = stackalloc byte[128];
        if (Libc.OpenPty(out _master, out int slave, name, 0, 0) != 0)
            throw new IOException($"openpty failed: {Marshal.GetLastPInvokeError()}");
        SetSize(cols, rows);
        // A session leader that opens the slave by path gets it as its controlling terminal.
        byte* fa = stackalloc byte[512];
        byte* attr = stackalloc byte[512];
        Libc.ActionsInit(fa);
        Libc.ActionsOpen(fa, 0, name, Libc.O_RDWR, 0);
        Libc.ActionsDup2(fa, 0, 1);
        Libc.ActionsDup2(fa, 0, 2);
        Libc.ActionsClose(fa, _master);
        Libc.ActionsClose(fa, slave);
        Libc.AttrInit(attr);
        Libc.AttrSetFlags(attr, Libc.POSIX_SPAWN_SETSID);
        int pid = 0;
        int r = WithCStrings(argv, a => WithCStrings(env, e => Libc.SpawnP(out pid, a[0], fa, attr, a, e)));
        Libc.Close(slave);
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

    internal void Close()
    {
        Open();
        _closed = true;
        Libc.Kill(-_pid, 1); // SIGHUP
        _stream.Dispose();
        Libc.WaitPid(_pid, out _, 0);
    }
}

static unsafe partial class Libc
{
    const string Lib = "libc.so.6";
    public const int O_RDWR = 2, POSIX_SPAWN_SETSID = 0x80;
    public const nuint TIOCSWINSZ = 0x5414;

    [StructLayout(LayoutKind.Sequential)] public struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

    [LibraryImport(Lib, EntryPoint = "openpty", SetLastError = true)] public static partial int OpenPty(out int master, out int slave, byte* name, nint termios, nint winsize);
    [LibraryImport(Lib, EntryPoint = "ioctl", SetLastError = true)] public static partial int IoctlWinSize(int fd, nuint request, ref WinSize ws);
    [LibraryImport(Lib, EntryPoint = "kill")] public static partial int Kill(int pid, int sig);
    [LibraryImport(Lib, EntryPoint = "tcgetpgrp")] public static partial int TcGetPgrp(int fd);
    [LibraryImport(Lib, EntryPoint = "close")] public static partial int Close(int fd);
    [LibraryImport(Lib, EntryPoint = "waitpid")] public static partial int WaitPid(int pid, out int status, int options);
    [LibraryImport(Lib, EntryPoint = "posix_spawnp")] public static partial int SpawnP(out int pid, byte* file, void* actions, void* attr, byte** argv, byte** envp);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_init")] public static partial int ActionsInit(void* fa);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_addopen")] public static partial int ActionsOpen(void* fa, int fd, byte* path, int flags, uint mode);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_adddup2")] public static partial int ActionsDup2(void* fa, int fd, int newFd);
    [LibraryImport(Lib, EntryPoint = "posix_spawn_file_actions_addclose")] public static partial int ActionsClose(void* fa, int fd);
    [LibraryImport(Lib, EntryPoint = "posix_spawnattr_init")] public static partial int AttrInit(void* attr);
    [LibraryImport(Lib, EntryPoint = "posix_spawnattr_setflags")] public static partial int AttrSetFlags(void* attr, short flags);
}
