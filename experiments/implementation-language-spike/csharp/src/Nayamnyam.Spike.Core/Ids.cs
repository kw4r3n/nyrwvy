using System.Diagnostics;

namespace Nayamnyam.Spike;

public abstract class StableIdKind
{
    internal StableIdKind()
    {
    }
}

public readonly record struct StableId<TKind>
    where TKind : StableIdKind, new()
{
    public string Value { get; }

    public StableId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Stable ID must not be blank.", nameof(value));
        }

        var domain = Domain();
        var requiredPrefix = $"{domain.Prefix}:";
        if (!value.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Stable ID must use the required '{requiredPrefix}' domain.",
                nameof(value));
        }

        var tail = value[requiredPrefix.Length..];
        var components = tail.Split(':');
        if (components.Length != domain.TailComponentCount
            || components.Any(component =>
                component.Length == 0
                || component.Contains('\r', StringComparison.Ordinal)
                || component.Contains('\n', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Stable ID must contain exactly {domain.TailComponentCount} "
                    + "nonempty atomic tail component(s).",
                nameof(value));
        }

        Value = value;
    }

    internal string AtomicTail
    {
        get
        {
            var domain = Domain();
            if (domain.TailComponentCount != 1)
            {
                throw new InvalidOperationException(
                    "Only single-tail stable IDs expose an atomic tail.");
            }

            return Value[(domain.Prefix.Length + 1)..];
        }
    }

    public override string ToString() => Value;

    private static (string Prefix, int TailComponentCount) Domain()
    {
        var kind = new TKind();
        if (kind is TypeTag)
        {
            return ("type", 1);
        }

        if (kind is FunctionTag)
        {
            return ("function", 1);
        }

        if (kind is ParameterTag)
        {
            return ("parameter", 1);
        }

        if (kind is CallTag)
        {
            return ("call", 1);
        }

        if (kind is ObligationTag)
        {
            return ("obligation", 3);
        }

        if (kind is SnapshotTag)
        {
            return ("snapshot", 1);
        }

        if (kind is TransactionTag)
        {
            return ("transaction", 1);
        }

        throw new UnreachableException(
            "The closed stable-ID domain hierarchy was not exhaustively handled.");
    }
}

public sealed class TypeTag : StableIdKind;

public sealed class FunctionTag : StableIdKind;

public sealed class ParameterTag : StableIdKind;

public sealed class CallTag : StableIdKind;

public sealed class ObligationTag : StableIdKind;

public sealed class SnapshotTag : StableIdKind;

public sealed class TransactionTag : StableIdKind;
