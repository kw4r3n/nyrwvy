using System;
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
    method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(NyStr) }));
var close = typeof(Files).GetMethods().Single(method =>
    method.ReturnType == typeof(NyUnit) && method.GetParameters().Length == 1);
Exception? primary = null;
try { open.Invoke(files, new object[] { (NyStr)root }); }
catch (TargetInvocationException failure) { primary = failure.InnerException; }
Check(primary is IOException or UnauthorizedAccessException, "Acquire must retain the trace I/O failure");
Check(primary!.Message.Contains(trace, StringComparison.Ordinal), "Primary failure must name the trace path");
// The original FileShare.None stream must have released the same lease file.
using (var lease = new FileStream(Path.Combine(root, "lease.tmp"), FileMode.Open, FileAccess.Write, FileShare.None))
    Check(lease.CanWrite, "Failed acquire retained its exclusive stream");

Directory.Delete(trace);
dynamic reopened = open.Invoke(files, new object[] { (NyStr)root })!;
Check(reopened.IsOk, "Same host and root must reopen after failed acquisition");
var handle = (OpenFile)reopened.OkValue;
var stream = handle.Stream!;
close.Invoke(files, new object[] { handle });
Check(handle.Stream is null && !stream.CanWrite, "Reopened handle was not released");
Exception? reused = null;
try { close.Invoke(files, new object[] { handle }); }
catch (TargetInvocationException failure) { reused = failure.InnerException; }
Check(reused?.Message == "Handle is not open", "Closing a released handle must be a host failure");
Check(File.ReadAllText(trace) == "open\nclose disposed\n", "Expected one successful acquire/release after recovery");
Console.WriteLine("PASS: trace failure retained; exclusive lease released; same host reopened and closed; released handle rejected.");
