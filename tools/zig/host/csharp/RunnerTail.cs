static class RunnerProcess { [System.Runtime.InteropServices.DllImport("libc")] public static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5); }
sealed class RunnerException(string message) : Exception(message);
sealed class FakeCapabilityException(string message) : Exception(message);
sealed class FakeArgumentsUnmatchedException() : Exception("No fake case matches the complete call arguments.");
sealed class FakeResponsesExhaustedException() : Exception("The fake operation has no scripted response left for this call.");
static class FakeLog {
    public static readonly System.Collections.Generic.List<(int Site, object[] Arguments)> Calls = new();
    public static void Record(int site, object[] arguments) {
        lock (Calls) {
            if (Calls.Count >= 1024) throw new RunnerException("The program made more than 1024 capability calls.");
            Calls.Add((site, arguments));
        }
    }
}
