using Nayamnyam.Spike;

namespace Nayamnyam.Spike.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: Nayamnyam.Spike.Cli <canonical|summary> <snapshot-path>");
            return 2;
        }

        var snapshot = CanonicalJson.ReadSnapshot(File.ReadAllBytes(args[1]));
        byte[] output;
        if (string.Equals(args[0], "canonical", StringComparison.Ordinal))
        {
            output = CanonicalJson.WriteSnapshot(snapshot);
        }
        else if (string.Equals(args[0], "summary", StringComparison.Ordinal))
        {
            output = CanonicalJson.WriteSummary(snapshot);
        }
        else
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            return 2;
        }

        using var standardOutput = Console.OpenStandardOutput();
        standardOutput.Write(output);
        return 0;
    }
}
