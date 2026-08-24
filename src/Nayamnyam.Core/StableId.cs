namespace Nayamnyam.Core;

public enum IdDomain
{
    Snapshot,
    Type,
    Field,
    Variant,
    Function,
    Parameter,
    Effect,
    Capability,
    Obligation,
    Transaction,
    GraphNode,
}

public readonly record struct StableId(IdDomain Domain, Guid Value)
{
    public override string ToString() => $"{Domain.ToString().ToLowerInvariant()}:{Value:D}";

    public static StableId Create(IdDomain domain) => new(domain, Guid.CreateVersion7());

    public static StableId Parse(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || !Guid.TryParse(value[(separator + 1)..], out var guid) ||
            !TryParseDomain(value[..separator], out var domain))
        {
            throw new FormatException($"Invalid stable ID: '{value}'.");
        }

        return new StableId(domain, guid);
    }

    private static bool TryParseDomain(string value, out IdDomain domain) => value switch
    {
        "snapshot" => Assign(IdDomain.Snapshot, out domain),
        "type" => Assign(IdDomain.Type, out domain),
        "field" => Assign(IdDomain.Field, out domain),
        "variant" => Assign(IdDomain.Variant, out domain),
        "function" => Assign(IdDomain.Function, out domain),
        "parameter" => Assign(IdDomain.Parameter, out domain),
        "effect" => Assign(IdDomain.Effect, out domain),
        "capability" => Assign(IdDomain.Capability, out domain),
        "obligation" => Assign(IdDomain.Obligation, out domain),
        "transaction" => Assign(IdDomain.Transaction, out domain),
        "graphnode" => Assign(IdDomain.GraphNode, out domain),
        _ => Assign(default, out domain, false),
    };

    private static bool Assign(IdDomain value, out IdDomain domain, bool result = true)
    {
        domain = value;
        return result;
    }
}
