namespace Nayamnyam.Spike.Tests;

internal static class Assertions
{
    public static void Equal<T>(T expected, T actual, string? message = null)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? $"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string? message = null)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                message ?? "Expected sequences to be equal.");
        }
    }

    public static void Same(object expected, object actual, string? message = null)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? "Expected references to be identical.");
        }
    }

    public static void Is<T>(object actual)
    {
        if (actual is not T)
        {
            throw new InvalidOperationException(
                $"Expected {typeof(T).Name} but found a different result type.");
        }
    }

    public static T IsAndReturn<T>(object actual)
    {
        Is<T>(actual);
        return (T)actual;
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} to be thrown.");
    }
}
