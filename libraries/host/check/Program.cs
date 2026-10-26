// Calls every host class of NyHost.cs against the real OS; run by run.mjs, which emits the program's C#.
using System;
using System.IO;
using System.Text;
using Nyrwvy.Generated;
using NyrHost;

int failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok) failures++;
}
NyList<NyStr> Strings(params string[] items)
{
    var list = NyList<NyStr>.Empty();
    foreach (var item in items) list = list.AppendItem(item);
    return list;
}

var dir = Directory.CreateTempSubdirectory("nyr-host-");
var fs = new FsHost();
var file = Path.Combine(dir.FullName, "data.bin");
var data = NyBytes.FromOwnedArray([0, 1, 127, 128, 255]);
Check("Fs.writeBytes", fs.writeBytes(file, data).IsOk);
Check("Fs.readBytes round trip", fs.readBytes(file) is { IsOk: true } read && read.OkValue.Equals(data));
Check("Fs.readText", fs.readText(file).IsOk);
Check("Fs.readBytes missing gives the reason", fs.readBytes(Path.Combine(dir.FullName, "missing")) is { IsOk: false } missing && missing.ErrorValue.Text.Contains("missing"));
Check("Fs.writeBytes into a missing directory", !fs.writeBytes(Path.Combine(dir.FullName, "no/such/file"), data).IsOk);

fs.writeBytes(file, NyBytes.FromOwnedArray([0xFF, 0xFE, 0x41, 0x00]));
Check("Fs.readText ignores UTF-16 byte order marks", fs.readText(file) is { IsOk: true } utf16 && utf16.OkValue.Text != "A");
fs.writeBytes(file, NyBytes.FromOwnedArray([0xEF, 0xBB, 0xBF, 0x41]));
Check("Fs.readText drops a UTF-8 byte order mark", fs.readText(file) is { IsOk: true } utf8 && utf8.OkValue.Text == "A");

var env = new EnvHost();
Environment.SetEnvironmentVariable("NYR_HOST_CHECK", "set");
Check("Env.get set", env.get("NYR_HOST_CHECK") is { HasValue: true } value && value.Value.Text == "set");
Check("Env.get unset", !env.get("NYR_HOST_CHECK_UNSET").HasValue);

var process = new ProcessHost();
Check("Process.spawn", process.spawn("true", Strings()).IsOk);
Check("Process.spawn missing program", !process.spawn("nyr-host-no-such-program", Strings()).IsOk);
var ran = process.run("sh", Strings("-c", "cat; printf out; printf err >&2; exit 3"));
Check("Process.run exit code and output", ran.IsOk && ran.OkValue.exitCode == 3
    && Encoding.Latin1.GetString(ran.OkValue.stdout.Span) == "out" && Encoding.Latin1.GetString(ran.OkValue.stderr.Span) == "err");
Check("Process.run missing program", !process.run("nyr-host-no-such-program", Strings()).IsOk);
Check("Process.run and spawn with an empty program name", !process.run("", Strings()).IsOk && !process.spawn("", Strings()).IsOk);

// The Nyrwvy function opens cat on a pty, writes, reads the first chunk back and closes.
var ptys = new PtysHost();
var xterm = NyMap<NyStr, NyStr>.Empty().Insert("TERM", "xterm-256color");
var echoed = NyFacade.ptyEcho(ptys, "hello\n").Text;
Check($"ptyEcho through Nyrwvy ({echoed.Replace("\r", "\\r").Replace("\n", "\\n")})", echoed.StartsWith("hello"));

var opened = ptys.open(Strings("sh", "-c", "stty size; echo $TERM; read x; stty size; exit 4"), xterm, 100, 30);
Check("Ptys.open", opened.IsOk);
var pty = (PtyHost)opened.OkValue;
string ReadUntil(string marker)
{
    var output = new StringBuilder();
    while (!output.ToString().Contains(marker) && pty.read() is { HasValue: true } chunk)
        output.Append(Encoding.Latin1.GetString(chunk.Value.Span));
    return output.ToString();
}
Check("Pty size and environment", ReadUntil("xterm-256color").Contains("30 100"));
Check("Pty.foregroundCommand", pty.foregroundCommand() is { HasValue: true } command && command.Value.Text == "sh");
var fds = process.run("sh", Strings("-c", "for f in /proc/self/fd/*; do readlink $f; done"));
var fdList = Encoding.Latin1.GetString(fds.OkValue.stdout.Span);
Check("an open pty does not leak into other children", fds.IsOk && !fdList.Contains("ptmx") && !fdList.Contains("/dev/pts"));
pty.resize(120, 40);
pty.write(NyBytes.FromOwnedArray("\n"u8.ToArray()));
Check("Pty.resize", ReadUntil("40 120").Contains("40 120"));
Check("Pty.read at end of output", !pty.read().HasValue);
Check("Ptys.close exit code", ptys.close(pty) is { HasValue: true } code && code.Value == 4);
Check("use after close fails", Throws(() => pty.read()));
// snap's pattern: the loop thread blocks in read while another thread closes the pty.
var blocked = (PtyHost)ptys.open(Strings("sh", "-c", "exec sleep 30"), xterm, 80, 24).OkValue;
string? readerEnd = null;
var reader = new System.Threading.Thread(() =>
{
    try { readerEnd = blocked.read().HasValue ? "Some" : "None"; }
    catch (Exception e) { readerEnd = e.GetType().Name; }
});
reader.Start();
System.Threading.Thread.Sleep(200);
Check("Ptys.close of a hung-up child has no exit code", !ptys.close(blocked).HasValue);
var readerEnded = reader.Join(5000);
Check($"close while another thread reads (reader got {readerEnd ?? "nothing"})", readerEnded && readerEnd == "None");
Check("Ptys.open missing program", !ptys.open(Strings("nyr-host-no-such-program"), xterm, 80, 24).IsOk);
var stubborn = ptys.open(Strings("sh", "-c", "trap '' HUP; while :; do sleep 1; done"), xterm, 80, 24).OkValue;
System.Threading.Thread.Sleep(200);
var watch = System.Diagnostics.Stopwatch.StartNew();
Check("Ptys.close kills a child that ignores the hangup", !ptys.close(stubborn).HasValue && watch.ElapsedMilliseconds < 5000);

dir.Delete(true);
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} checks failed");
return failures == 0 ? 0 : 1;

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (InvalidOperationException) { return true; }
}
