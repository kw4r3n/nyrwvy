using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using BatchExample;
using Nyrwvy.Generated;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = args[0];
Directory.CreateDirectory(root);
var trace = Path.Combine(root, "trace.txt");
Directory.CreateDirectory(trace); // Force a real append failure after handle registration.
var files = new Files();
var open = typeof(Files).GetMethods().Single(method =>
    method.ReturnType == typeof(NyResult<NyStr, NyStr>) && method.GetParameters().Length == 1);
var close = typeof(Files).GetMethods().Single(method =>
    method.ReturnType == typeof(NyUnit) && method.GetParameters().Length == 1);
// Inspect the owned handles directly; no production visibility or helper is added.
var handles = (ConcurrentDictionary<string, FileStream>)typeof(Files)
    .GetField("handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(files)!;
Exception? primary = null;
try { open.Invoke(files, new object[] { (NyStr)root }); }
catch (TargetInvocationException failure) { primary = failure.InnerException; }
Check(primary is IOException or UnauthorizedAccessException, "Acquire must retain the trace I/O failure");
Check(primary!.Message.Contains(trace, StringComparison.Ordinal), "Primary failure must name the trace path");
Check(handles.IsEmpty, "Failed acquire left an owned handle registered");
// The original FileShare.None stream must have released the same lease file.
using (var lease = new FileStream(Path.Combine(root, "lease.tmp"), FileMode.Open, FileAccess.Write, FileShare.None))
    Check(lease.CanWrite, "Failed acquire retained its exclusive stream");

Directory.Delete(trace);
var reopened = (NyResult<NyStr, NyStr>)open.Invoke(files, new object[] { (NyStr)root })!;
Check(reopened.IsOk && handles.Count == 1, "Same host and root must reopen after failed acquisition");
var stream = handles[root];
close.Invoke(files, new object[] { (NyStr)root });
Check(handles.IsEmpty && !stream.CanWrite, "Reopened handle was not released");
Check(File.ReadAllText(trace) == "open\nclose disposed\n", "Expected one successful acquire/release after recovery");
Console.WriteLine("PASS: trace failure retained; owned handle removed; exclusive lease released; same host reopened and closed.");
